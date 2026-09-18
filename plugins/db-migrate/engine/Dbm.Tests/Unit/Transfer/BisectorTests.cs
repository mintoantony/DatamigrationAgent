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
        Assert.Null(r.UniformError);   // rows loaded, so the load itself is not what is broken
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

    /// <summary>A plan defect: every attempt fails the same way, whichever rows it carries.</summary>
    private sealed class HopelessTarget : IBisectTarget
    {
        public const string Error = "Invalid column name 'nope'.";
        public int Attempts;

        public Task<LoadAttempt> TryLoadAsync(IReadOnlyList<int> rows, CancellationToken ct)
        {
            Attempts++;
            return Task.FromResult(new LoadAttempt(false, Error));
        }

        public Task RestartAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task A_chunk_where_every_attempt_fails_the_same_way_is_not_re_run_for_every_node()
    {
        var t = new HopelessTarget();
        var r = await Bisector.RunAsync(64, t, false, default);
        Assert.Equal(64, r.Failed.Count);
        // Harm: the hopeless load is re-executed once per node of the bisection tree, 2N-1 = 127 times for 64 rows -- at the
        // default chunk size, 200_000 re-runs of a broken merge. Once the whole chunk and two single rows have failed with one
        // identical error and nothing has loaded, no multi-row attempt can succeed, so none is made.
        Assert.Equal(64 + 6, t.Attempts);   // the rows, plus the whole chunk and the log2(64) halvings above the first row
        Assert.Equal(HopelessTarget.Error, r.UniformError);
    }

    /// <summary>Every attempt fails alike, and the failure is the server's verdict on one row's values (ruling 147).</summary>
    private sealed class RowFaultTarget(bool rowFault) : IBisectTarget
    {
        public const string Error = "The INSERT statement conflicted with the FOREIGN KEY constraint \"FK_C_P\".";

        public Task<LoadAttempt> TryLoadAsync(IReadOnlyList<int> rows, CancellationToken ct)
            => Task.FromResult(new LoadAttempt(false, Error) { RowFault = rowFault });

        public Task RestartAsync(CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// Ruling 147. A constraint violation is the server judging one row's values against a rule, so a chunk of nothing but those is N
    /// bad rows however identical the message - and H2 must not fail the task over them.
    /// <para><b>Harm:</b> without the row-fault exclusion the task was failed for N individually bad rows. On the C15 sample pair that is
    /// not hypothetical: the two lines of the orphan orders are the last two rows of app.OrderLines, so at ChunkSize 500 they are a chunk
    /// of their own, and skip mode failed the migration instead of rejecting them.</para>
    /// </summary>
    [Fact]
    public async Task A_chunk_of_nothing_but_constraint_violations_is_rejected_rows_not_a_broken_load()
    {
        var r = await Bisector.RunAsync(8, new RowFaultTarget(rowFault: true), false, default);
        // A UniformError here is what BulkLoader turns into bad_task, so it is named for what it would do rather than for the property.
        Assert.True(r.UniformError is null,
            $"the task was failed for {r.Failed.Count} individually bad rows (H2 read their constraint violation as a broken load): {r.UniformError}");
        Assert.Equal(Enumerable.Range(0, 8), r.Failed.Select(f => f.Row));
        Assert.All(r.Failed, f => Assert.Equal(RowFaultTarget.Error, f.Error));
        Assert.Empty(r.Loaded);
    }

    /// <summary>
    /// The other side of ruling 147, so the fix cannot over-reach: the same shape of failure that is <b>not</b> a row fault - a plan
    /// defect, a broken MergeSql, a target column that is not there - still fails the task, which is what H2 exists for.
    /// </summary>
    [Fact]
    public async Task A_chunk_that_fails_alike_on_anything_but_a_constraint_still_fails_the_task()
    {
        var r = await Bisector.RunAsync(8, new RowFaultTarget(rowFault: false), false, default);
        Assert.Equal(RowFaultTarget.Error, r.UniformError);
        Assert.Equal(8, r.Failed.Count);
    }

    [Fact]
    public async Task Failed_reload_after_restart_throws()
    {
        var t = new FakeTarget(new HashSet<int>(), dooming: new HashSet<int> { 6 }) { FailReload = true };
        var ex = await Assert.ThrowsAsync<TransferException>(() => Bisector.RunAsync(8, t, false, default));
        Assert.Equal("bisect_reload", ex.Code);
    }
}
