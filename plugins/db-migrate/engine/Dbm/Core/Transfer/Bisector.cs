namespace Dbm.Core.Transfer;

public sealed record LoadAttempt(bool Ok, string? Error = null, bool Doomed = false)
{
    public static readonly LoadAttempt Success = new(true);
}

public sealed record RowFailure(int Row, string Error);

public sealed record BisectResult(IReadOnlyList<int> Loaded, IReadOnlyList<RowFailure> Failed)
{
    /// <summary>Set only when every row of the chunk was attempted on its own, every one of them failed, nothing loaded, and every
    /// attempt carried this one error text. Data is not uniformly bad; a plan is — so the caller fails the task with this text rather
    /// than reporting a whole chunk of rejected rows (H2). Null whenever a row loaded, a text differed, or rows went unattempted.</summary>
    public string? UniformError { get; init; }
}

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

        string? uniformText = null;   // the error text every attempt so far has carried
        bool uniform = true;          // ... and nothing at all has loaded
        int failedRows = 0;

        var stack = new Stack<int[]>();
        stack.Push(Enumerable.Range(0, rowCount).ToArray());
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            int[] segment = stack.Pop();

            // A multi-row attempt earns its cost only while a whole segment might still load. Once the whole chunk and two single rows
            // have failed with one identical error and nothing has loaded, the load itself is what is broken, so every further multi-row
            // attempt is a re-run of it: go straight to the rows (H2). 2N-1 attempts become N + log2(N) + 1.
            if (segment.Length > 1 && uniform && failedRows >= 2)
            {
                Halve(stack, segment);
                continue;
            }

            var attempt = await target.TryLoadAsync(segment, ct);
            if (attempt.Ok)
            {
                uniform = false;
                loaded.AddRange(segment);
                continue;
            }
            string text = TextOf(attempt.Error);
            if (uniformText is null) uniformText = text;
            else if (!string.Equals(uniformText, text, StringComparison.Ordinal)) uniform = false;

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
                failedRows++;
                failed.Add(new RowFailure(segment[0], text));
                if (stopAtFirstFailure) break;
                continue;
            }
            Halve(stack, segment);
        }
        loaded.Sort();
        bool everyRowFailedAlike = uniform && uniformText is not null && loaded.Count == 0 && failed.Count == rowCount;
        return new BisectResult(loaded, failed) { UniformError = everyRowFailedAlike ? uniformText : null };
    }

    private static void Halve(Stack<int[]> stack, int[] segment)
    {
        int half = segment.Length / 2;
        stack.Push(segment[half..]);
        stack.Push(segment[..half]);
    }

    private static string TextOf(string? error) => string.IsNullOrWhiteSpace(error) ? NoErrorText : error;
}
