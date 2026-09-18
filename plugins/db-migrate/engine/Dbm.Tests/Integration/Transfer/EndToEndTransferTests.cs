using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Samples;
using Dbm.Core.Sql;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Dbm.Core.Transfer;
using Dbm.Tests.Support;
using Xunit;

namespace Dbm.Tests.Integration.Transfer;

/// <summary>The C15 sample pair (scale 1) and the plan SqlGenerator makes for it; shared by the end-to-end tests.</summary>
public sealed class SamplePlanFixture : IAsyncLifetime
{
    private SqlPlanPayload _plan = null!;

    public SamplePair Pair { get; private set; } = null!;
    public TempDatabase Src => Pair.Source;

    public async Task InitializeAsync()
    {
        Pair = await SampleDatabases.CreateAsync(scale: 1);
        var src = await SampleExtract.CatalogAsync(Pair.SourceCs, profile: false);
        var tgt = await SampleExtract.CatalogAsync(Pair.TargetCs, profile: false);   // Pair.Target stays empty: schema template only
        _plan = SqlGenerator.Generate(SampleMappings.Approved(), src, tgt);
    }

    public Task DisposeAsync() => Pair.DisposeAsync().AsTask();

    /// <summary>A private deep copy per test.</summary>
    public SqlPlanPayload Plan() => Json.Deserialize<SqlPlanPayload>(Json.Serialize(_plan));

    /// <summary>A fresh, empty ShopV2 for one test. A schema script that throws drops the database it was run in (5.7 review F10):
    /// no fixture database outlives its test, and one that never reached its caller has no test to dispose it.</summary>
    public static async Task<TempDatabase> NewTargetAsync(string? schema = null, string prefix = "shopv2")
    {
        var db = await TempDatabase.CreateAsync(prefix);
        try
        {
            await SampleDatabases.RunAsync(db.ConnectionString, schema ?? SampleSql.ShopV2Schema);
            return db;
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }
}

[Trait("Category", "Integration")]
public sealed class EndToEndTransferTests(SamplePlanFixture fx) : IClassFixture<SamplePlanFixture>
{
    private static readonly Dictionary<string, long> KnownRejects = new(StringComparer.OrdinalIgnoreCase)
    {
        ["app.Orders"] = 5,
        ["app.OrderLines"] = 3,
    };

    /// <summary>C15 source rows per target at scale 1: ORD_HDR = 3000·s + 5, ORD_LINE = 9000·s + 2, ….</summary>
    private static readonly Dictionary<string, long> SourceRows = new(StringComparer.OrdinalIgnoreCase)
    {
        ["app.Customers"] = 1000,
        ["app.Addresses"] = 1500,
        ["app.Products"] = 200,
        ["app.Orders"] = 3005,
        ["app.OrderLines"] = 9002,
        ["app.AuditEvents"] = 5000,
    };

    /// <summary>Total source rows the run moves (the pre-flight estimate), and what is left after the eight rejects.</summary>
    private const long TotalSourceRows = 19_707;
    private const long TotalLoadedRows = TotalSourceRows - 8;

    /// <summary>
    /// The chunk size the pause, resume and crash cases run at - the brief's own 500, restored by ruling 147. It is the number that
    /// found the defect: 9 002 = 18 x 500 + <b>2</b>, so the last chunk of app.OrderLines is exactly the two orphan lines, every row of
    /// it is rejected with a byte-identical FK message, and until 147 <see cref="Bisector"/>'s H2 rule read that as a broken load and
    /// failed the migration in skip mode. A constraint violation is now the server's verdict on one row, so the chunk is two rejected
    /// rows and these cases test pausing, resuming and recovering. The boundary itself is held by
    /// <see cref="A_whole_chunk_of_alike_constraint_rejects_completes_with_those_rows_rejected"/>.
    /// </summary>
    private const int ChunkedSize = 500;

    /// <summary>
    /// The eight rows the C15 fixture plants, by key and by reason. Eight rejects with the wrong eight rows would satisfy a count, so
    /// the test names each one: seed.sql gives ORD_HDR 3001/3002 a CUST_ID that does not exist, 3003-3005 a 300-character CMNT against
    /// <c>Comment nvarchar(200)</c>, ORD_LINE (1, 1) QTY = 0 against CK_OrderLines_Quantity, and the two lines of the orphan orders
    /// have no parent to point at.
    /// </summary>
    private static readonly (string Target, long K0, long? K1, string Reason)[] EightRejects =
    [
        ("app.Orders", 3001, null, "FK_Orders_Customers"),
        ("app.Orders", 3002, null, "FK_Orders_Customers"),
        ("app.Orders", 3003, null, TruncatedComment),
        ("app.Orders", 3004, null, TruncatedComment),
        ("app.Orders", 3005, null, TruncatedComment),
        ("app.OrderLines", 1, 1, "CK_OrderLines_Quantity"),
        ("app.OrderLines", 3001, 1, "FK_OrderLines_Orders"),
        ("app.OrderLines", 3002, 1, "FK_OrderLines_Orders"),
    ];

    /// <summary>What the server says about the three 300-character comments; the column it names is the one the plan binds.</summary>
    private const string TruncatedComment = "column 'Comment'";

    /// <summary>
    /// The eight planted rows, as a predicate over the task's own SourceQuery aliases, so that a row-by-row comparison of source
    /// against target can leave out exactly the rows that are supposed to be missing - and nothing else.
    /// </summary>
    private static readonly Dictionary<string, string> RejectedSourceRows = new(StringComparer.OrdinalIgnoreCase)
    {
        ["app.Orders"] = "q.[OrderId] NOT IN (3001, 3002, 3003, 3004, 3005)",
        ["app.OrderLines"] = "NOT (q.[OrderId] = 1 AND q.[LineNumber] = 1) AND NOT (q.[OrderId] IN (3001, 3002) AND q.[LineNumber] = 1)",
    };

    private sealed class Rig(XferServices svc, TempDatabase tgt, SqlPlanPayload plan, string srcCs) : IAsyncDisposable
    {
        public XferServices Svc { get; } = svc;
        public TempDatabase Tgt { get; } = tgt;
        public SqlPlanPayload Plan { get; } = plan;
        public TransferRepo Repo => Svc.Services.Transfers;
        public TransferEngine NewEngine() => new(Svc.Services, Plan, srcCs, Tgt.ConnectionString);
        public string TaskOf(string target) => Plan.Tasks.Single(kv => string.Equals(kv.Value.Target, target, StringComparison.OrdinalIgnoreCase)).Key;
        public async ValueTask DisposeAsync()
        {
            Svc.Dispose();
            await Tgt.DisposeAsync();
        }
    }

    private async Task<Rig> RigAsync()
    {
        var plan = fx.Plan();
        Assert.Equal(6, plan.Tasks.Count);
        Assert.All(plan.Tasks.Values, t => Assert.Empty(t.Errors));
        // The target first and the workspace second, each released if the other cannot be made (5.7 review F10).
        var tgt = await SamplePlanFixture.NewTargetAsync();
        try
        {
            return new Rig(new XferServices(), tgt, plan, fx.Src.ConnectionString);
        }
        catch
        {
            await tgt.DisposeAsync();
            throw;
        }
    }

    /// <summary>5.7 review F10. <b>Harm:</b> a schema script that throws left its database on the server for good - the constraint is that
    /// no fixture database outlives its test.</summary>
    [Fact]
    public async Task A_target_whose_schema_script_fails_is_dropped_rather_than_left_on_the_server()
    {
        string prefix = "shopv2_leak_" + Guid.NewGuid().ToString("N")[..8];
        await Assert.ThrowsAnyAsync<Exception>(() => SamplePlanFixture.NewTargetAsync("THIS IS NOT SQL;", prefix));
        int left = await fx.Src.ScalarAsync<int>($"SELECT COUNT(*) FROM sys.databases WHERE name LIKE N'{prefix}%'");
        Assert.True(left == 0, $"{left} database(s) named {prefix}* were left on the server after their schema script failed");
    }

    /// <summary>
    /// Rows of the source query that are not in the target, plus rows of the target that are not in the source, compared on every bound
    /// column. Carry-forward 1 (ruling 118): a checksum is a SUM of per-row BINARY_CHECKSUM values, so equal sums do not prove equal
    /// rows, and a count proves less still. Zero here does. Run from the source connection, with the target addressed by its database
    /// name, because the plan's SourceQuery only resolves there.
    /// </summary>
    /// <para>5.7 review F4: string columns are compared as bytes, not under the database's case-insensitive collation, where 'Shipped'
    /// and 'SHIPPED ' are equal. The source side is cast to the target column's own type first, so both sides are the bytes the target
    /// would hold.</para>
    private async Task<long> RowDiffAsync(Rig rig, string taskId)
    {
        var task = rig.Plan.Tasks[taskId];
        var strings = await StringColumnTypesAsync(rig, task.Target);
        string SourceCol(string c, int i) => (strings.TryGetValue(c, out var type)
            ? $"CAST(CAST(q.{SqlQuote.Ident(c)} AS {type}) AS varbinary(max))" : $"q.{SqlQuote.Ident(c)}") + $" AS [c{i}]";
        string TargetCol(string c, int i) => (strings.ContainsKey(c)
            ? $"CAST({SqlQuote.Ident(c)} AS varbinary(max))" : SqlQuote.Ident(c)) + $" AS [c{i}]";
        string sourceCols = string.Join(", ", task.Columns.Select((c, i) => SourceCol(c.Target, i)));
        string targetCols = string.Join(", ", task.Columns.Select((c, i) => TargetCol(c.Target, i)));
        string where = RejectedSourceRows.TryGetValue(task.Target, out var predicate) ? $" WHERE {predicate}" : "";
        string source = $"SELECT {sourceCols} FROM (\n{task.SourceQuery}\n) AS q{where}";
        string target = $"SELECT {targetCols} FROM [{rig.Tgt.Name}].{SqlQuote.TableKey(task.Target)}";
        return await fx.Src.ScalarAsync<long>(
            $"SELECT (SELECT COUNT_BIG(*) FROM (\n{source}\nEXCEPT\n{target}\n) AS missing)"
            + $" + (SELECT COUNT_BIG(*) FROM (\n{target}\nEXCEPT\n{source}\n) AS extra)");
    }

    /// <summary>The target table's character columns and their full types, e.g. <c>nvarchar(200)</c> or <c>nvarchar(max)</c>.</summary>
    private static async Task<Dictionary<string, string>> StringColumnTypesAsync(Rig rig, string target)
    {
        var (schema, table) = (target[..target.IndexOf('.', StringComparison.Ordinal)], target[(target.IndexOf('.', StringComparison.Ordinal) + 1)..]);
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var conn = await SqlConnect.OpenAsync(rig.Tgt.ConnectionString, default);
        await using var cmd = new Microsoft.Data.SqlClient.SqlCommand(
            "SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = @s AND TABLE_NAME = @t "
            + "AND DATA_TYPE IN ('char', 'varchar', 'nchar', 'nvarchar')", conn);
        cmd.Parameters.AddWithValue("@s", schema);
        cmd.Parameters.AddWithValue("@t", table);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            int length = r.GetInt32(2);
            types[r.GetString(0)] = $"{r.GetString(1)}({(length < 0 ? "max" : length.ToString(System.Globalization.CultureInfo.InvariantCulture))})";
        }
        return types;
    }

    private async Task AssertExactOutcomeAsync(Rig rig, long runId)
    {
        var tasks = rig.Repo.Tasks(runId);
        foreach (var (id, task) in rig.Plan.Tasks)
        {
            long source = await fx.Src.ScalarAsync<long>(TargetOps.CountSqlOf(task));
            long rejects = KnownRejects.GetValueOrDefault(task.Target);
            Assert.Equal(SourceRows[task.Target], source);                                     // e.g. Orders 3005 -> 3000 loaded, OrderLines 9002 -> 8999
            long inTarget = await rig.Tgt.CountAsync(SqlQuote.TableKey(task.Target));
            Assert.True(inTarget == source - rejects, $"{task.Target}: the target holds {inTarget} rows, not {source - rejects}");
            var row = tasks.Single(t => t.TaskId == id);
            Assert.True(row.Status == TransferTaskStatus.Done, $"{task.Target}: the task ended {row.Status}: {row.Error}");
            Assert.True(row.RowsError == rejects, $"{task.Target}: {row.RowsError} rows counted as rejected, not {rejects}");
            Assert.True(row.RowsDone == source - rejects, $"{task.Target}: {row.RowsDone} rows counted as loaded, not {source - rejects}");
            // The rows themselves, not only how many of them there are: a pause, a crash or a resume that loaded the right number of
            // the wrong rows would pass every count above.
            long diff = await RowDiffAsync(rig, id);
            Assert.True(diff == 0, $"{task.Target}: {diff} rows differ between source and target (missing plus extra), compared on every "
                                   + "bound column with only the planted rejects left out");
        }
        Assert.Equal(8, rig.Repo.ErrorRows(runId, null, 1000).Count);                  // each rejected row recorded exactly once
        AssertTheEightRejects(rig, runId);
        Assert.Equal(0, await rig.Tgt.ScalarAsync<int>("SELECT COUNT(*) FROM sys.foreign_keys WHERE is_not_trusted = 1 OR is_disabled = 1"));
        Assert.Equal(0, await rig.Tgt.ScalarAsync<int>("SELECT CAST(is_not_trusted AS int) FROM sys.foreign_keys WHERE name = 'FK_Customers_PrimaryAddress'"));
        Assert.Equal(0, await rig.Tgt.ScalarAsync<int>("SELECT COUNT(*) FROM sys.check_constraints WHERE is_not_trusted = 1 OR is_disabled = 1"));
        Assert.Equal(0, await rig.Tgt.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name = '__dbm_checkpoint'"));
    }

    /// <summary>Every rejected row is one of the eight the fixture plants, named by its key and its reason, and each appears once.</summary>
    private static void AssertTheEightRejects(Rig rig, long runId)
    {
        var actual = rig.Repo.ErrorRows(runId, null, 1000)
            .Select(e => (Target: rig.Plan.Tasks[e.TaskId].Target, Key: KeyOf(e), e.Error)).ToList();
        foreach (var (target, k0, k1, reason) in EightRejects)
        {
            string what = k1 is null ? $"{target} __k0={k0}" : $"{target} __k0={k0} __k1={k1}";
            var hits = actual.Where(a => string.Equals(a.Target, target, StringComparison.OrdinalIgnoreCase)
                                         && a.Key.K0 == k0 && a.Key.K1 == k1).ToList();
            Assert.True(hits.Count == 1, $"{what}: expected exactly one rejected row, found {hits.Count}.");
            Assert.True(hits[0].Error.Contains(reason, StringComparison.Ordinal),
                $"{what}: expected the reason to name \"{reason}\"; the recorded error was: {hits[0].Error}");
        }
        Assert.Equal(EightRejects.Length, actual.Count);
    }

    private static (long K0, long? K1) KeyOf(ErrorRowEntry entry)
    {
        var key = JsonNode.Parse(Assert.IsType<string>(entry.KeyJson))!.AsObject();
        return (key["__k0"]!.GetValue<long>(), key.TryGetPropertyValue("__k1", out var k1) ? k1!.GetValue<long>() : null);
    }

    [Fact]
    public async Task Skip_mode_rejects_exactly_the_eight_known_bad_rows_and_validates_everything_else()
    {
        await using var rig = await RigAsync();
        var options = new TransferOptions { ErrorMode = "skip" };

        // Pre-flight over the real pair, before a row moves. Ruling 139: a check that did not run carries the NotRun flag, so the test
        // reads the flag rather than the wording - and if one ever comes back not-run, the assertion names it.
        await using (var src = await SqlConnect.OpenAsync(fx.Src.ConnectionString, default))
        await using (var tgt = await SqlConnect.OpenAsync(rig.Tgt.ConnectionString, default))
        {
            var checks = new List<PreflightCheck> { Preflight.PlanCheck(rig.Plan) };
            checks.AddRange(await Preflight.TargetChecksAsync(tgt, rig.Plan, options, default));
            checks.Add(await Preflight.SourceEstimateAsync(src, rig.Plan, default));
            var notRun = checks.Where(c => c.NotRun).ToList();
            Assert.True(notRun.Count == 0, "pre-flight checks that did not run: " + string.Join("; ", notRun.Select(c => $"{c.Name}: {c.Detail}")));
            Assert.All(checks, c => Assert.True(c.Ok, $"{c.Name}: {c.Detail}"));
            Assert.Contains("19,707 rows across 6 tasks", checks.Single(c => c.Name == "estimated_rows").Detail, StringComparison.Ordinal);
            Assert.Contains("All target tables that could be counted are empty", checks.Single(c => c.Name == "target_rows").Detail,
                StringComparison.Ordinal);
        }

        var engine = rig.NewEngine();
        long runId = engine.CreateRun(1, options);
        var outcome = await engine.RunAsync(runId, new TransferControl(), default);

        Assert.Equal(RunStatus.Completed, outcome.Status);
        await AssertExactOutcomeAsync(rig, runId);

        var orders = rig.Repo.ErrorRows(runId, rig.TaskOf("app.Orders"), 100);
        Assert.Equal(2, orders.Count(e => e.Error.Contains("FK_Orders_Customers", StringComparison.Ordinal)));
        Assert.Equal(3, orders.Count(e => e.Error.Contains(TruncatedComment, StringComparison.Ordinal)));
        Assert.All(orders, e => Assert.StartsWith("{\"__k0\":", e.KeyJson, StringComparison.Ordinal));
        var lines = rig.Repo.ErrorRows(runId, rig.TaskOf("app.OrderLines"), 100);
        Assert.Equal(1, lines.Count(e => e.Error.Contains("CK_OrderLines_Quantity", StringComparison.Ordinal)));
        Assert.Equal(2, lines.Count(e => e.Error.Contains("FK_OrderLines_Orders", StringComparison.Ordinal)));

        foreach (var row in rig.Repo.Tasks(runId))
        {
            var v = Json.Deserialize<TaskValidation>(row.ValidationJson!);
            Assert.True(v.CountMatch, $"{row.Target}: count mismatch");
            if (row.RowsError == 0)
            {
                Assert.NotEmpty(v.Checksums);
                Assert.All(v.Checksums, c => Assert.True(c.Match, $"{row.Target}.{c.Column}: checksum {c.Source} != {c.Target}"));
            }
            else Assert.Equal("rows were rejected", v.ChecksumsSkipped);
        }

        var report = Json.Deserialize<FinalReport>(rig.Repo.GetRun(runId)!.SummaryJson!);
        Assert.Equal(8, report.RowsError);
        Assert.Equal(report.RowsSource - 8, report.RowsLoaded);
        Assert.Contains(report.Notes, n => n.Contains("Row counts validated for all 6 tasks", StringComparison.Ordinal));
        Assert.StartsWith("Transferred ", FinalReportBuilder.Summary(report), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_mode_fails_the_run_on_the_first_bad_chunk()
    {
        await using var rig = await RigAsync();
        var engine = rig.NewEngine();
        long runId = engine.CreateRun(1, new TransferOptions { ErrorMode = "stop" });
        var outcome = await engine.RunAsync(runId, new TransferControl(), default);

        Assert.Equal(RunStatus.Failed, outcome.Status);
        var orders = rig.Repo.Task(runId, rig.TaskOf("app.Orders"))!;
        Assert.Equal(TransferTaskStatus.Failed, orders.Status);
        Assert.Contains("rejected", orders.Error, StringComparison.Ordinal);
        // Which row, not only that one was recorded (5.7 review F5): the first bad Orders row in key order is 3001, an FK orphan.
        var single = Assert.Single(rig.Repo.ErrorRows(runId, orders.TaskId, 100));
        Assert.True(KeyOf(single) == (3001, null) && single.Error.Contains("FK_Orders_Customers", StringComparison.Ordinal),
            $"stop mode recorded {single.KeyJson} ({single.Error}), not the first bad row, order 3001 on FK_Orders_Customers");
        Assert.Equal(0, await rig.Tgt.CountAsync("[app].[Orders]"));                          // one chunk, rolled back
        Assert.Equal(TransferTaskStatus.Pending, rig.Repo.Task(runId, rig.TaskOf("app.OrderLines"))!.Status);   // depends on Orders
        Assert.Equal(RunStatus.Failed, rig.Repo.GetRun(runId)!.Status);
    }

    [Fact]
    public async Task Pause_mid_table_and_resume_gives_exact_counts_without_duplicates()
    {
        await using var rig = await RigAsync();
        string ordersTask = rig.TaskOf("app.Orders");
        var engine = rig.NewEngine();
        long runId = engine.CreateRun(1, new TransferOptions { ErrorMode = "skip", ChunkSize = ChunkedSize });
        var control = new TransferControl();
        control.ChunkCommitted += c => { if (c.TaskId == ordersTask && c.ChunkNo == 2) control.RequestPause(); };

        Assert.Equal(RunStatus.Paused, (await engine.RunAsync(runId, control, default)).Status);
        var orders = rig.Repo.Task(runId, ordersTask)!;
        Assert.Equal(TransferTaskStatus.Paused, orders.Status);
        long ordersSource = await fx.Src.ScalarAsync<long>(TargetOps.CountSqlOf(rig.Plan.Tasks[ordersTask]));
        long loadedSoFar = await rig.Tgt.CountAsync("[app].[Orders]");
        Assert.InRange(loadedSoFar, 1, ordersSource - 1);                                      // really mid-table
        Assert.Equal(orders.RowsDone, loadedSoFar);

        var resumed = await rig.NewEngine().RunAsync(runId, new TransferControl(), default);
        Assert.True(resumed.Status == RunStatus.Completed, $"the resumed segment ended {resumed.Status}: {resumed.Error}");
        await AssertExactOutcomeAsync(rig, runId);
    }

    [Fact]
    public async Task Crash_and_recovery_give_exact_counts_without_duplicates()
    {
        await using var rig = await RigAsync();
        string linesTask = rig.TaskOf("app.OrderLines");
        var engine = rig.NewEngine();
        long runId = engine.CreateRun(1, new TransferOptions { ErrorMode = "skip", ChunkSize = ChunkedSize });
        using var cts = new CancellationTokenSource();
        var control = new TransferControl();
        control.ChunkCommitted += c => { if (c.TaskId == linesTask && c.ChunkNo == 3) cts.Cancel(); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.RunAsync(runId, control, cts.Token));
        Assert.Equal(RunStatus.Running, rig.Repo.GetRun(runId)!.Status);                     // what a dead process leaves behind
        long partial = await rig.Tgt.CountAsync("[app].[OrderLines]");
        Assert.True(partial > 0);

        Assert.Equal(1, rig.Repo.RecoverInterrupted());                                         // server start
        var recovered = new TransferEngine(rig.Svc.Services, fx.Plan(), fx.Src.ConnectionString, rig.Tgt.ConnectionString);
        var resumed = await recovered.RunAsync(runId, new TransferControl(), default);
        Assert.True(resumed.Status == RunStatus.Completed, $"the recovered segment ended {resumed.Status}: {resumed.Error}");
        await AssertExactOutcomeAsync(rig, runId);
    }

    /// <summary>
    /// Added for dispatch item 2. The test above kills the run between chunks; this one kills it at 5.3's seam - the single instant
    /// at which a chunk's rows and its checkpoint have both become durable and nothing else has been written down, which is exactly
    /// where a checkpoint outside the chunk transaction would show up as a chunk loaded twice (rulings 114/115). The mirror row, the
    /// chunk's error rows and the ChunkCommitted event are all still unwritten when the process dies here.
    /// </summary>
    [Fact]
    public async Task Crash_at_the_chunk_commit_seam_recovers_with_the_same_numbers()
    {
        await using var rig = await RigAsync();
        string ordersTask = rig.TaskOf("app.Orders");
        var engine = rig.NewEngine();
        long runId = engine.CreateRun(1, new TransferOptions { ErrorMode = "skip", ChunkSize = ChunkedSize, Parallelism = 1 });
        using var cts = new CancellationTokenSource();
        try
        {
            TaskRunner.AfterChunkTransaction = (taskId, chunkNo) =>
            {
                if (taskId != ordersTask || chunkNo != 3) return;
                cts.Cancel();                                   // chunk 3 of Orders holds none of the five planted rows
                throw new OperationCanceledException(cts.Token);
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.RunAsync(runId, new TransferControl(), cts.Token));
        }
        finally
        {
            TaskRunner.AfterChunkTransaction = null;
        }

        Assert.Equal(RunStatus.Running, rig.Repo.GetRun(runId)!.Status);
        var killed = rig.Repo.Task(runId, ordersTask)!;
        Assert.Equal(2 * ChunkedSize, killed.RowsDone);           // the mirror stopped one chunk short of the target: chunk 3 never reached it
        Assert.Equal(3 * ChunkedSize, await rig.Tgt.CountAsync("[app].[Orders]"));

        Assert.Equal(1, rig.Repo.RecoverInterrupted());
        var recovered = new TransferEngine(rig.Svc.Services, fx.Plan(), fx.Src.ConnectionString, rig.Tgt.ConnectionString);
        var resumed = await recovered.RunAsync(runId, new TransferControl(), default);
        Assert.True(resumed.Status == RunStatus.Completed, $"the recovered segment ended {resumed.Status}: {resumed.Error}");
        await AssertExactOutcomeAsync(rig, runId);
    }

    /// <summary>
    /// Ruling 147, end to end and at the chunk size that found the defect. The two rows that must be rejected on
    /// FK_OrderLines_Orders are the last two rows of app.OrderLines in key order and 9 002 = 18 x 500 + 2, so the final chunk holds
    /// nothing but them, and the FK message names no key value so the two texts are identical. That used to satisfy H2 ("data is not
    /// uniformly bad; a plan is") and fail the whole migration in skip mode: run failed, app.OrderLines left at 8 999 with the chunk
    /// rolled back, and only one of the three rejected rows ever recorded. It now completes, and this holds the boundary:
    /// <b>8 999 rows in the target, 3 000 orders, 8 rejects, and the two orphan lines among them by key.</b>
    /// </summary>
    [Fact]
    public async Task A_whole_chunk_of_alike_constraint_rejects_completes_with_those_rows_rejected()
    {
        await using var rig = await RigAsync();
        string linesTask = rig.TaskOf("app.OrderLines");
        var engine = rig.NewEngine();
        long runId = engine.CreateRun(1, new TransferOptions { ErrorMode = "skip", ChunkSize = 500 });
        var outcome = await engine.RunAsync(runId, new TransferControl(), default);

        Assert.True(outcome.Status == RunStatus.Completed, $"the run ended {outcome.Status}: {outcome.Error}");
        Assert.Equal(8999, await rig.Tgt.CountAsync("[app].[OrderLines]"));
        Assert.Equal(3000, await rig.Tgt.CountAsync("[app].[Orders]"));
        Assert.Equal(8, rig.Repo.ErrorRows(runId, null, 1000).Count);
        // Counted rows are not the same claim as the right rows: this compares every bound column of every loaded line against the
        // source by key, with only the three planted OrderLines keys left out, so 8 999 is proven rather than counted.
        Assert.Equal(0, await RowDiffAsync(rig, linesTask));
        AssertTheEightRejects(rig, runId);
        var lines = rig.Repo.Task(runId, linesTask)!;
        Assert.Equal(TransferTaskStatus.Done, lines.Status);
        Assert.Equal(3, lines.RowsError);
        Assert.Equal(8999, lines.RowsDone);
    }

    /// <summary>
    /// Added for dispatch item 3 (report F2), restated by rulings 147, 186, 192 and 201. A second run into the target the first one
    /// filled, with the options the Execute screen offers by default (skip, no truncate). Every row of a keyed table is a duplicate key,
    /// which since ruling 147 is a per-row reject (2627 is a row fault, so H2 would not fire even if the texts were identical - they are
    /// not, each names its own value). Ruling 192's zero-load guard does not stop it: ruling 201 exempts a table that held rows before
    /// the run when every reject is a duplicate key. So the run completes in one segment: app.Products ends Done with everything
    /// rejected and nothing added, and app.AuditEvents, which ShopV2 gives no key at all, has nothing to refuse it and loads its 5 000
    /// rows a second time. Both tasks' count comparisons then balance
    /// as rows added, the checksums are skipped ("target table was not empty before the run"), and the headline says what was compared
    /// and names the table that received nothing, never "validated" (rulings 186, 192).
    /// <para>The second run here is given two of the six tasks - one keyed, one keyless - because that is what the finding is about. The
    /// six-task form was measured by task 5.7 at 4 m 03 s (every one of 14 707 doomed rows bisected down to a single-row attempt); in the
    /// sweep it measured 2 m 56 s before ruling 193's error-number short-cut and 2 m 43 s - 2 m 46 s after, on a shared machine.</para>
    /// <para>Pre-flight is the only thing that says so in advance, and it says it as a warning. With "truncate target first" set, the
    /// run repeats the first one exactly - that is the supported path, over the whole plan, and it is asserted here too.</para>
    /// </summary>
    [Fact]
    public async Task Second_run_into_a_non_empty_target_completes_while_rejecting_everything_and_doubling_the_keyless_table()
    {
        await using var rig = await RigAsync();
        var first = rig.NewEngine();
        long firstRun = first.CreateRun(1, new TransferOptions { ErrorMode = "skip" });
        Assert.Equal(RunStatus.Completed, (await first.RunAsync(firstRun, new TransferControl(), default)).Status);
        await AssertExactOutcomeAsync(rig, firstRun);

        // Ruling 148: this is the gate, and it is the only one. The engine below is driven directly and bypasses it, so it is asserted
        // here instead - the check does not pass, it is a check that ran rather than one that did not (ruling 139), and it names the
        // tables that already hold rows. The run is not blocked: target_rows is a warning, and PreflightResult.Passed only falls to
        // false on an error, which is the whole of open item 27.
        await using (var tgt = await SqlConnect.OpenAsync(rig.Tgt.ConnectionString, default))
        {
            var checks = await Preflight.TargetChecksAsync(tgt, rig.Plan, new TransferOptions { ErrorMode = "skip" }, default);
            var rows = checks.Single(c => c.Name == "target_rows");
            Assert.False(rows.Ok);
            Assert.False(rows.NotRun);
            Assert.Equal("warning", rows.Severity);
            Assert.Contains("app.Orders", rows.Detail, StringComparison.Ordinal);
            Assert.Contains("app.AuditEvents (5,000)", rows.Detail, StringComparison.Ordinal);
            Assert.Contains("Truncate target first", rows.Detail, StringComparison.Ordinal);
            // Ruling 186: the keyless table is named as the one that doubles, not lumped in with "duplicate keys".
            Assert.Contains("No primary key or unique index on app.AuditEvents: rows will be loaded again — duplicates", rows.Detail, StringComparison.Ordinal);
            Assert.True(new PreflightResult(1, Clock.Now(), checks.ToList()).Passed);   // a warning does not stop the run
        }

        string products = rig.TaskOf("app.Products"), audit = rig.TaskOf("app.AuditEvents");
        var second = new TransferEngine(rig.Svc.Services, Only(fx.Plan(), products, audit), fx.Src.ConnectionString, rig.Tgt.ConnectionString);
        long secondRun = second.CreateRun(1, new TransferOptions { ErrorMode = "skip" });
        var outcome = await second.RunAsync(secondRun, new TransferControl(), default);
        // Ruling 201: the confirmed re-run is not stopped by ruling 192's guard - app.Products held rows before the run and every one of
        // its rejects is a duplicate key (2627), which is what the confirmation said would happen.
        Assert.True(outcome.Status == RunStatus.Completed,
            $"the confirmed re-run into a non-empty target was stopped over its duplicate keys ({outcome.Status}): {outcome.Error}");
        foreach (var row in rig.Repo.Tasks(secondRun))
        {
            bool keyless = row.TaskId == audit;
            Assert.Equal(TransferTaskStatus.Done, row.Status);
            Assert.Equal(keyless ? 0 : SourceRows[row.Target], row.RowsError);
            Assert.Equal(keyless ? SourceRows[row.Target] : 0, row.RowsDone);
            var v = Json.Deserialize<TaskValidation>(row.ValidationJson!);
            Assert.True(v.CountMatch, $"{row.Target}: {v.CountNote}");                      // every count balances; nothing says the table doubled
            Assert.Equal("target table was not empty before the run", v.ChecksumsSkipped);
        }
        Assert.Equal(10_000, await rig.Tgt.CountAsync("[app].[AuditEvents]"));              // the keyless table has nothing to refuse it
        Assert.Equal(SourceRows["app.Products"], await rig.Tgt.CountAsync("[app].[Products]"));   // the keyed one added nothing
        var refused = Json.Deserialize<FinalReport>(rig.Repo.GetRun(secondRun)!.SummaryJson!);
        Assert.Equal(SourceRows["app.Products"], refused.RowsError);
        Assert.Equal(SourceRows["app.AuditEvents"], refused.RowsLoaded);
        // Ruling 186: the counts balance as rows ADDED, and the report now says exactly that instead of an unqualified "validated"
        // over a doubled table.
        string headline = FinalReportBuilder.Summary(refused);
        Assert.True(headline.Contains(FinalReportBuilder.NotEmptyBefore, StringComparison.Ordinal) && !headline.Contains("validated", StringComparison.Ordinal)
                    && headline.Contains("app.Products loaded 0 of 200 rows", StringComparison.Ordinal),
            "the headline over a target that was not empty: " + headline);
        Assert.True(refused.Notes.Any(n => n.StartsWith("Target tables were not empty before this run:", StringComparison.Ordinal)
                                           && n.Contains("app.AuditEvents (5,000)", StringComparison.Ordinal)),
            "the report does not say the target was not empty: " + string.Join(" | ", refused.Notes));
        Assert.DoesNotContain(refused.Notes, n => n.Contains("Row counts validated", StringComparison.Ordinal));

        var third = rig.NewEngine();
        long thirdRun = third.CreateRun(1, new TransferOptions { ErrorMode = "skip", TruncateTarget = true });
        Assert.Equal(RunStatus.Completed, (await third.RunAsync(thirdRun, new TransferControl(), default)).Status);
        await AssertExactOutcomeAsync(rig, thirdRun);
        var report = Json.Deserialize<FinalReport>(rig.Repo.GetRun(thirdRun)!.SummaryJson!);
        Assert.Contains(report.Notes, n => n.Contains("Target tables were emptied before loading", StringComparison.Ordinal));
    }

    /// <summary>
    /// Ruling 192 (open item 30), the 5.7 reviewer's reproduction: the real plan with app.Orders.CustomerId bound to an expression no
    /// customer matches, skip mode, default options. Every order is an FK reject. <b>Harm</b> (5.7 review F1, measured): the run
    /// <b>completed</b> in 2 m 37 s (3 m 11 s in this sweep's baseline) with Orders 0 / 3 005 and, by cascade, OrderLines 0 / 9 002, under
    /// "row counts validated, checksums 23/23 matched". Now the Orders task fails as soon as its whole source (one chunk) has loaded
    /// nothing, naming FK_Orders_Customers and error 547; its rejects are recorded; OrderLines depends on Orders and never starts.
    /// </summary>
    [Fact]
    public async Task A_wrong_FK_mapping_fails_its_task_instead_of_completing_with_the_table_rejected()
    {
        await using var rig = await RigAsync();
        string ordersTask = rig.TaskOf("app.Orders");
        var orders = rig.Plan.Tasks[ordersTask];
        Assert.Contains("s.[CUST_ID]", orders.SourceQuery!, StringComparison.Ordinal);
        orders.SourceQuery = orders.SourceQuery!.Replace("s.[CUST_ID]", "(-ABS(s.[CUST_ID]) - 1000)", StringComparison.Ordinal);
        var engine = rig.NewEngine();
        long runId = engine.CreateRun(1, new TransferOptions { ErrorMode = "skip" });

        var outcome = await engine.RunAsync(runId, new TransferControl(), default);

        Assert.True(outcome.Status == RunStatus.Failed,
            $"a wrong FK mapping ended {outcome.Status}: the whole table was rejected row by row and the run reported as finished");
        var row = rig.Repo.Task(runId, ordersTask)!;
        Assert.Equal(TransferTaskStatus.Failed, row.Status);
        Assert.True(row.Error!.StartsWith("Every row of the first chunk of app.Orders was rejected (3,005 rows) and none loaded", StringComparison.Ordinal)
                    // 3 002, not 3 005: the three planted 300-character comments fail client-side first, with no server number
                    && row.Error.Contains("on 3,002 of 3,005 rows, was error 547", StringComparison.Ordinal)
                    && row.Error.Contains("FK_Orders_Customers", StringComparison.Ordinal),
            "the failure does not say every row was rejected and name the FK with its error number: " + row.Error);
        Assert.Contains($"{ordersTask}: Every row of the first chunk", outcome.Error!, StringComparison.Ordinal);
        Assert.Equal(0, await rig.Tgt.CountAsync("[app].[Orders]"));
        Assert.Equal(3005, rig.Repo.ErrorRowCount(runId, ordersTask));                 // skip-and-log still recorded every reject
        var lines = rig.Repo.Task(runId, rig.TaskOf("app.OrderLines"))!;
        Assert.True(lines.Status == TransferTaskStatus.Pending && lines.RowsError == 0,
            $"OrderLines, which depends on Orders, ran anyway: {lines.Status}, {lines.RowsError} rejected");
    }

    /// <summary>The same plan with only the named tasks left in it - order, tasks and dependencies alike, and nothing else touched.</summary>
    private static SqlPlanPayload Only(SqlPlanPayload plan, params string[] taskIds)
    {
        foreach (var id in plan.Tasks.Keys.Where(k => !taskIds.Contains(k, StringComparer.Ordinal)).ToList()) plan.Tasks.Remove(id);
        plan.Order = plan.Order.Where(id => plan.Tasks.ContainsKey(id)).ToList();
        foreach (var task in plan.Tasks.Values) task.DependsOn = task.DependsOn.Where(plan.Tasks.ContainsKey).ToList();
        return plan;
    }

    /// <summary>
    /// Added for dispatch item 4. The report is read instead of the database, so it has to say what the database holds: every number
    /// in it is compared against a query, and <c>CountMatch</c> must not be null for a task that was in fact compared. Then the other
    /// half (carry-forwards 1 and 9): one row of a task whose checksums matched is changed in the target, and the same validator and
    /// the same builder are made to say so - the counts still balance, one column's sum does not, and the headline's "matched" is a
    /// count of matching columns rather than a total minus the differences.
    /// </summary>
    [Fact]
    public async Task Final_report_states_what_the_database_holds_and_a_changed_row_is_caught()
    {
        await using var rig = await RigAsync();
        var engine = rig.NewEngine();
        long runId = engine.CreateRun(1, new TransferOptions { ErrorMode = "skip" });
        Assert.Equal(RunStatus.Completed, (await engine.RunAsync(runId, new TransferControl(), default)).Status);

        var report = Json.Deserialize<FinalReport>(rig.Repo.GetRun(runId)!.SummaryJson!);
        Assert.Equal(6, report.Tasks.Count);
        Assert.Equal(0, report.TasksWithoutSource);
        Assert.Equal(TotalSourceRows, report.RowsSource);
        Assert.Equal(TotalLoadedRows, report.RowsLoaded);
        Assert.Equal(8, report.RowsError);
        foreach (var t in report.Tasks)
        {
            Assert.Equal(SourceRows[t.Target], t.RowsSource);
            Assert.Equal(KnownRejects.GetValueOrDefault(t.Target), t.RowsError);
            Assert.Equal(await rig.Tgt.CountAsync(SqlQuote.TableKey(t.Target)), t.RowsLoaded);
            Assert.True(t.CountCompared, $"{t.Target}: {t.CountNote}");
            Assert.True(t.CountMatch, $"{t.Target}: {t.CountNote}");                 // never null: every task was in fact compared
            Assert.Null(t.CountNote);
            Assert.Null(t.RowsSourceNote);
            Assert.Null(t.ErrorSamplesNote);
            Assert.Null(t.StatusNote);
            // Carry-forward 2 (ruling 118): datetime is never checksummed and would be named here. ShopV2 has no datetime column, so
            // no bound column is dropped - which the next two assertions turn from an assumption into a fact.
            Assert.Null(t.ChecksumColumnsNote);
            Assert.Equal(0, t.ChecksumColumnsNotCompared);
        }
        Assert.DoesNotContain("datetime", RunValidator.ChecksumTypes);
        Assert.Equal(0, await rig.Tgt.ScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id "
            + "JOIN sys.tables tb ON tb.object_id = c.object_id WHERE t.name = 'datetime'"));

        Assert.Contains(report.Notes, n => n.Contains("Row counts validated for all 6 tasks", StringComparison.Ordinal));
        Assert.Contains(report.Notes, n => n.Contains("8 rows were rejected and logged", StringComparison.Ordinal));
        Assert.Contains(report.Notes, n => n.Contains("equal sums do not prove identical rows", StringComparison.Ordinal));
        Assert.Contains(report.Notes, n => n.Contains("Checksums skipped for app.Orders: rows were rejected", StringComparison.Ordinal));
        Assert.Contains(report.Notes, n => n.Contains("Checksums skipped for app.OrderLines: rows were rejected", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Notes, n => n.Contains("could not be", StringComparison.Ordinal));
        int sums = report.Tasks.Sum(t => t.Checksums.Count);
        Assert.True(sums >= 20, $"only {sums} columns were checksummed");
        Assert.Contains($"checksums {sums}/{sums} matched", FinalReportBuilder.Summary(report), StringComparison.Ordinal);

        // One row of a clean task is changed behind the report's back.
        string productsTask = rig.TaskOf("app.Products");
        await rig.Tgt.ExecAsync("UPDATE app.Products SET [Name] = [Name] + N'!' WHERE ProductId = 7;");
        TaskValidation changed;
        await using (var src = await SqlConnect.OpenAsync(fx.Src.ConnectionString, default))
        await using (var tgt = await SqlConnect.OpenAsync(rig.Tgt.ConnectionString, default))
        {
            changed = await RunValidator.ValidateTaskAsync(src, tgt, rig.Plan.Tasks[productsTask],
                rig.Repo.Task(runId, productsTask)!, checksums: true, default);
        }
        Assert.True(changed.CountMatch);                                                  // a changed row is not a missing row
        Assert.Null(changed.ChecksumsSkipped);
        Assert.Equal(1, changed.Checksums.Count(c => !c.Match));
        Assert.False(changed.Checksums.Single(c => c.Column == "Name").Match);
        Assert.Equal(1, await RowDiffAsync(rig, productsTask) / 2);                       // one row differs, so it is missing and extra

        var rows = rig.Repo.Tasks(runId)
            .Select(t => t.TaskId == productsTask ? t with { ValidationJson = Json.Serialize(changed) } : t).ToList();
        var after = FinalReportBuilder.Build(rig.Repo.GetRun(runId)!, RunStatus.Completed, rows,
            id => rig.Repo.ErrorRows(runId, id, FinalReportBuilder.MaxErrorSamples), Clock.Now(), [],
            id => rig.Repo.ErrorRowCount(runId, id));
        Assert.Contains(after.Notes, n => n.Contains("Column checksums differ for: app.Products.Name", StringComparison.Ordinal));
        Assert.Contains($"checksums {sums - 1}/{sums} matched", FinalReportBuilder.Summary(after), StringComparison.Ordinal);

        // 5.7 review F4: the row diff itself must see a change of case and a trailing space, which the database's case-insensitive
        // collation calls equal. Two more rows changed, so three rows differ, each counted once missing and once extra.
        Assert.True(await rig.Tgt.ScalarAsync<int>("SELECT COUNT(*) FROM app.Products WHERE ProductId = 8 AND [Name] <> UPPER([Name]) COLLATE Latin1_General_BIN2") == 1,
            "product 8's name has no lower-case letter, so upper-casing it proves nothing");
        await rig.Tgt.ExecAsync("UPDATE app.Products SET [Name] = UPPER([Name]) WHERE ProductId = 8; "
                                + "UPDATE app.Products SET [Name] = [Name] + N' ' WHERE ProductId = 9;");
        long diff = await RowDiffAsync(rig, productsTask);
        Assert.True(diff == 6, $"the row diff counts {diff} differing rows, not 6: a change of case or a trailing space is invisible to it");
    }
}
