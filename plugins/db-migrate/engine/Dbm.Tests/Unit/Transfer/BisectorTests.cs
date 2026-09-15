using Dbm.Core.Transfer;
using Xunit;

namespace Dbm.Tests.Unit.Transfer;

public sealed class BisectorTests
{
    /// <summary>Simulates a transaction with savepoints: failed attempts leave nothing behind; dooming rows kill the transaction.</summary>
    private sealed class FakeTarget(ISet<int> bad, ISet<int>? dooming = null) : IBisectTarget
    {
        public readonly List<int> Tx = new();
        public int Attempts, Restarts;
        public bool FailReload;
        private bool _doomed;

        public Task<LoadAttempt> TryLoadAsync(IReadOnlyList<int> rows, CancellationToken ct)
        {
            if (_doomed) throw new InvalidOperationException("used a doomed transaction");
            Attempts++;
            if (FailReload && Restarts >= 2) return Task.FromResult(new LoadAttempt(false, "reload broke"));   // 2nd restart is followed by a reload
            int hit = rows.FirstOrDefault(r => bad.Contains(r) || (dooming?.Contains(r) ?? false), -1);
            if (hit < 0) { Tx.AddRange(rows); return Task.FromResult(LoadAttempt.Success); }
            if (dooming?.Contains(hit) ?? false)
            {
                _doomed = true;
                return Task.FromResult(new LoadAttempt(false, $"row {hit} ended the transaction", Doomed: true));
            }
            return Task.FromResult(new LoadAttempt(false, $"row {hit} is bad"));
        }

        public Task RestartAsync(CancellationToken ct)
        {
            Tx.Clear();
            _doomed = false;
            Restarts++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task All_good_rows_load_in_one_attempt()
    {
        var t = new FakeTarget(new HashSet<int>());
        var r = await Bisector.RunAsync(100, t, false, default);
        Assert.Equal(1, t.Attempts);
        Assert.Equal(100, r.Loaded.Count);
        Assert.Empty(r.Failed);
    }

    [Fact]
    public async Task Bad_rows_are_isolated_and_every_good_row_stays_loaded()
    {
        var t = new FakeTarget(new HashSet<int> { 3, 17, 39 });
        var r = await Bisector.RunAsync(40, t, false, default);
        Assert.Equal(new[] { 3, 17, 39 }, r.Failed.Select(f => f.Row));
        Assert.Equal("row 17 is bad", r.Failed[1].Error);
        Assert.Equal(37, r.Loaded.Count);
        Assert.Equal(Enumerable.Range(0, 40).Except(new[] { 3, 17, 39 }), t.Tx.Order());
        Assert.Equal(r.Loaded, t.Tx.Order());
    }

    [Fact]
    public async Task Doomed_transaction_is_restarted_and_confirmed_rows_reloaded()
    {
        var t = new FakeTarget(new HashSet<int> { 8 }, dooming: new HashSet<int> { 5 });
        var r = await Bisector.RunAsync(10, t, false, default);
        Assert.Equal(new[] { 5, 8 }, r.Failed.Select(f => f.Row));
        Assert.True(t.Restarts >= 1);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 6, 7, 9 }, t.Tx.Order());
        Assert.Equal(t.Tx.Order(), r.Loaded);
    }

    [Fact]
    public async Task Stop_at_first_failure_reports_only_the_first_bad_row()
    {
        var t = new FakeTarget(new HashSet<int> { 2, 7 });
        var r = await Bisector.RunAsync(10, t, true, default);
        Assert.Single(r.Failed);
        Assert.Equal(2, r.Failed[0].Row);
    }

    [Fact]
    public async Task Zero_rows_do_nothing()
    {
        var t = new FakeTarget(new HashSet<int>());
        var r = await Bisector.RunAsync(0, t, false, default);
        Assert.Equal(0, t.Attempts);
        Assert.Empty(r.Loaded);
    }

    private sealed class SilentTarget(string? error) : IBisectTarget
    {
        public Task<LoadAttempt> TryLoadAsync(IReadOnlyList<int> rows, CancellationToken ct) => Task.FromResult(new LoadAttempt(false, error));
        public Task RestartAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_failure_without_error_text_is_still_recorded_with_a_reason(string? error)
    {
        // Harm: the error_row store refuses a blank error (ArgumentException), which would take down the run over one row.
        var r = await Bisector.RunAsync(2, new SilentTarget(error), false, default);
        Assert.Equal(new[] { 0, 1 }, r.Failed.Select(f => f.Row));
        Assert.All(r.Failed, f => Assert.False(string.IsNullOrWhiteSpace(f.Error)));
    }

    [Fact]
    public async Task Failed_reload_after_restart_throws()
    {
        var t = new FakeTarget(new HashSet<int>(), dooming: new HashSet<int> { 6 }) { FailReload = true };
        var ex = await Assert.ThrowsAsync<TransferException>(() => Bisector.RunAsync(8, t, false, default));
        Assert.Equal("bisect_reload", ex.Code);
    }
}
