namespace Dbm.Core.Transfer;

public sealed record LoadAttempt(bool Ok, string? Error = null, bool Doomed = false)
{
    public static readonly LoadAttempt Success = new(true);
}

public sealed record RowFailure(int Row, string Error);

public sealed record BisectResult(IReadOnlyList<int> Loaded, IReadOnlyList<RowFailure> Failed);

public interface IBisectTarget
{
    /// <summary>Loads the rows (indexes) in the current transaction. On failure the attempt must be undone, or Doomed = true.</summary>
    Task<LoadAttempt> TryLoadAsync(IReadOnlyList<int> rows, CancellationToken ct);

    /// <summary>Discards the doomed transaction and begins a new one.</summary>
    Task RestartAsync(CancellationToken ct);
}

/// <summary>Isolates failing rows by recursive halving (spec §10). Good rows stay loaded in the caller's transaction.</summary>
public static class Bisector
{
    /// <summary>Recorded for a failed single row whose attempt carried no error text: the error_row store refuses a blank error.</summary>
    public const string NoErrorText = "the load failed without an error message";

    /// <summary>With <paramref name="stopAtFirstFailure"/> the run ends at the first failed row, so rows after it are in neither list.</summary>
    public static async Task<BisectResult> RunAsync(int rowCount, IBisectTarget target, bool stopAtFirstFailure, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        var loaded = new List<int>(rowCount);
        var failed = new List<RowFailure>();
        if (rowCount == 0) return new BisectResult(loaded, failed);

        var stack = new Stack<int[]>();
        stack.Push(Enumerable.Range(0, rowCount).ToArray());
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            int[] segment = stack.Pop();
            var attempt = await target.TryLoadAsync(segment, ct);
            if (attempt.Ok)
            {
                loaded.AddRange(segment);
                continue;
            }
            if (attempt.Doomed)
            {
                await target.RestartAsync(ct);
                if (loaded.Count > 0)
                {
                    var reload = await target.TryLoadAsync(loaded.ToArray(), ct);
                    if (!reload.Ok)
                        throw new TransferException("bisect_reload",
                            "Rows that loaded before a transaction-ending error failed to reload: " + TextOf(reload.Error));
                }
            }
            if (segment.Length == 1)
            {
                failed.Add(new RowFailure(segment[0], TextOf(attempt.Error)));
                if (stopAtFirstFailure) break;
                continue;
            }
            int half = segment.Length / 2;
            stack.Push(segment[half..]);
            stack.Push(segment[..half]);
        }
        loaded.Sort();
        return new BisectResult(loaded, failed);
    }

    private static string TextOf(string? error) => string.IsNullOrWhiteSpace(error) ? NoErrorText : error;
}
