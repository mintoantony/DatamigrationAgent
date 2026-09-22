using Dbm.Core;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Dbm.Core.Transfer;
using Dbm.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Dbm.Tests.Integration.Transfer;

/// <summary>Small purpose-built tables for the runner's guards: each one exists to make one defect visible as damage.</summary>
public sealed class GuardSourceFixture : IAsyncLifetime
{
    public TempDatabase Src { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Src = await TempDatabase.CreateAsync("dbm_eng_g_src");
        await Src.ExecAsync("""
            CREATE TABLE dbo.Wide (Id int NOT NULL PRIMARY KEY, V nvarchar(20) NOT NULL);
            CREATE TABLE dbo.Stamp (Id int NOT NULL PRIMARY KEY, At datetime2(7) NOT NULL);
            CREATE TABLE dbo.Ident (Id int NOT NULL PRIMARY KEY, V nvarchar(20) NOT NULL);
            CREATE TABLE dbo.Uni (Id int NOT NULL PRIMARY KEY, Qty int NOT NULL);
            CREATE TABLE dbo.Tkey (At datetime2(7) NOT NULL PRIMARY KEY, Qty int NOT NULL);
            CREATE TABLE dbo.Big (Id int NOT NULL PRIMARY KEY, V nvarchar(20) NOT NULL);
            CREATE TABLE dbo.Many (Id int NOT NULL PRIMARY KEY, V nvarchar(20) NOT NULL);
            GO
            WITH n AS (SELECT TOP (12000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT dbo.Many (Id, V) SELECT i, CASE WHEN i = 7000 THEN N'bad' ELSE N'ok' END FROM n;
            WITH n AS (SELECT TOP (400) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT dbo.Wide (Id, V) SELECT i, CONCAT('v', i) FROM n;
            -- 4,500 rows: past chunk 4 at 1,000 rows, so the sizes pin K = 3 exactly and a resume's sizes pin the checkpoint's chunk
            -- number (a per-segment counter would read chunks 2-4 small on resume).
            WITH n AS (SELECT TOP (4500) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT dbo.Big (Id, V) SELECT i, CONCAT('v', i) FROM n;
            -- .6 of a second: CAST to datetime2(0) rounds up, plain truncation rounds down, and the two answers never coincide here.
            INSERT dbo.Stamp (Id, At) VALUES
              (1, '2020-01-01T10:00:01.6000000'), (2, '2020-01-01T10:00:03.6000000'), (3, '2020-01-01T10:00:05.6000000'),
              (4, '2020-01-01T10:00:07.6000000'), (5, '2020-01-01T10:00:09.6000000'), (6, '2020-01-01T10:00:11.6000000');
            INSERT dbo.Ident (Id, V) VALUES (100, N'a'), (101, N'b'), (102, N'c'), (103, N'd');
            WITH n AS (SELECT TOP (8) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
            INSERT dbo.Uni (Id, Qty) SELECT i, i FROM n;
            -- a temporal KEY column that is also a bound column: the one shape in which normalising in place would
            -- rewrite the key an error row is identified by.
            INSERT dbo.Tkey (At, Qty) VALUES ('2020-01-01T10:00:01.6000000', 1), ('2020-01-01T10:00:03.6000000', 0);
            """);
    }

    public Task DisposeAsync() => Src.DisposeAsync().AsTask();
}

[Trait("Category", "Integration")]
public sealed class TransferEngineGuardTests(GuardSourceFixture fx) : IClassFixture<GuardSourceFixture>
{
    private const string TargetSchema = """
        CREATE SCHEMA app;
        GO
        CREATE TABLE app.Wide (Id int NOT NULL, V nvarchar(20) NOT NULL);
        CREATE TABLE app.Stamp (Id int NOT NULL PRIMARY KEY, At datetime2(0) NOT NULL);
        CREATE TABLE app.Flat (At datetime2(0) NOT NULL);
        CREATE TABLE app.Ident (Id int IDENTITY(1,1) NOT NULL PRIMARY KEY, V nvarchar(20) NOT NULL);
        CREATE TABLE app.Uni (Id int NOT NULL PRIMARY KEY, Qty int NOT NULL CONSTRAINT CK_Uni_Qty CHECK (Qty < 0));
        CREATE TABLE app.Missing (Id int NOT NULL PRIMARY KEY);
        CREATE TABLE app.Tkey (At datetime2(0) NOT NULL PRIMARY KEY, Qty int NOT NULL CONSTRAINT CK_Tkey_Qty CHECK (Qty > 0));
        CREATE TABLE app.Okay (Id int NOT NULL PRIMARY KEY, V nvarchar(20) NOT NULL CONSTRAINT CK_Okay_V CHECK (V = N'ok'));
        CREATE TABLE app.FlatOkay (V nvarchar(20) NOT NULL CONSTRAINT CK_FlatOkay_V CHECK (V = N'ok'));
        CREATE TABLE app.Big (Id int NOT NULL PRIMARY KEY, V nvarchar(20) NOT NULL CONSTRAINT CK_Big_V CHECK (V = N'ok'));
        CREATE TABLE app.Many (Id int NOT NULL PRIMARY KEY, V nvarchar(20) NOT NULL);
        GO
        CREATE TRIGGER app.trg_Many_rollback ON app.Many AFTER INSERT AS BEGIN IF EXISTS (SELECT 1 FROM inserted WHERE V = N'bad') ROLLBACK TRANSACTION; END
        """;

    private sealed class Rig(XferServices svc, TempDatabase tgt, TransferEngine engine) : IAsyncDisposable
    {
        public XferServices Svc { get; } = svc;
        public TempDatabase Tgt { get; } = tgt;
        public TransferEngine Engine { get; } = engine;
        public TransferRepo Repo => Svc.Services.Transfers;
        public async ValueTask DisposeAsync()
        {
            Svc.Dispose();
            await Tgt.DisposeAsync();
        }
    }

    private async Task<Rig> RigAsync(SqlPlanPayload plan)
    {
        var tgt = await TempDatabase.CreateAsync("dbm_eng_g_tgt");
        await tgt.ExecAsync(TargetSchema);
        var svc = new XferServices();
        return new Rig(svc, tgt, new TransferEngine(svc.Services, plan, fx.Src.ConnectionString, tgt.ConnectionString));
    }

    private static SqlPlanPayload One(TaskPlan task) => new() { Order = ["T01"], Tasks = new() { ["T01"] = task } };

    private static TaskPlan TaskOf(string target, string query, string[] keys, (string Source, string Target)[] cols, string? countSql = null) => new()
    {
        Target = target,
        SourceQuery = query,
        KeyColumns = keys.ToList(),
        Columns = cols.Select(c => new ColumnBinding(c.Source, c.Target)).ToList(),
        CountSql = countSql ?? $"SELECT COUNT_BIG(*) FROM ({query}) AS q",
    };

    /// <summary>
    /// Harm: BulkLoader does not normalize by itself, so a keyed chunk loaded without TargetShape.Normalize reaches a datetime2(0)
    /// column truncated - 10:00:01.6 arrives as 10:00:01 where the server's own CAST gives 10:00:02. Every row is off by up to a
    /// second and nothing reports an error, which is the worst shape a migration defect can take.
    /// </summary>
    [Fact]
    public async Task The_keyed_path_rounds_temporal_values_the_way_the_server_casts_them()
    {
        await using var rig = await RigAsync(One(TaskOf("app.Stamp",
            "SELECT s.[Id] AS [Id], s.[At] AS [At], s.[Id] AS [__k0] FROM [dbo].[Stamp] AS s", ["__k0"],
            [("Id", "Id"), ("At", "At")])));
        long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 4, ErrorMode = "skip" });

        Assert.Equal(RunStatus.Completed, (await rig.Engine.RunAsync(runId, new TransferControl(), default)).Status);
        Assert.Equal(6, await rig.Tgt.CountAsync("app.Stamp"));
        Assert.Equal(0, await rig.Tgt.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM app.Stamp t JOIN [{fx.Src.Name}].dbo.Stamp s ON s.Id = t.Id WHERE t.At <> CAST(s.At AS datetime2(0))"));
    }

    /// <summary>The same defect on the other path: a keyless task has no chunk loop, so it needs its own Normalize call.</summary>
    [Fact]
    public async Task The_keyless_path_rounds_temporal_values_the_way_the_server_casts_them()
    {
        await using var rig = await RigAsync(One(TaskOf("app.Flat",
            "SELECT s.[At] AS [At] FROM [dbo].[Stamp] AS s", [], [("At", "At")])));
        long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 4, ErrorMode = "skip" });

        Assert.Equal(RunStatus.Completed, (await rig.Engine.RunAsync(runId, new TransferControl(), default)).Status);
        Assert.Equal(6, await rig.Tgt.CountAsync("app.Flat"));
        Assert.Equal(6, await rig.Tgt.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM app.Flat t JOIN [{fx.Src.Name}].dbo.Stamp s ON t.At = CAST(s.At AS datetime2(0))"));
    }

    /// <summary>
    /// Harm: without the target shape the loader cannot see that Id is an identity column, so SqlBulkCopy discards the source ids and
    /// the server renumbers the rows 1..4. No error, no rejected row - just a table whose primary keys no longer match the source, and
    /// every foreign key that referenced them now points at the wrong row.
    /// </summary>
    [Fact]
    public async Task A_binding_to_an_identity_column_fails_the_task_instead_of_silently_renumbering_the_rows()
    {
        await using var rig = await RigAsync(One(TaskOf("app.Ident",
            "SELECT s.[Id] AS [Id], s.[V] AS [V], s.[Id] AS [__k0] FROM [dbo].[Ident] AS s", ["__k0"],
            [("Id", "Id"), ("V", "V")])));
        long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 100, ErrorMode = "skip" });

        var outcome = await rig.Engine.RunAsync(runId, new TransferControl(), default);

        Assert.Equal(RunStatus.Failed, outcome.Status);
        Assert.Equal(0, await rig.Tgt.CountAsync("app.Ident"));                       // nothing was renumbered, because nothing was loaded
        Assert.Equal(0, await rig.Tgt.ScalarAsync<int>("SELECT COUNT(*) FROM app.Ident WHERE Id IN (1, 2, 3, 4)"));
        Assert.Contains("Id", rig.Repo.Task(runId, "T01")!.Error, StringComparison.Ordinal);
    }

    /// <summary>A plan defect is reported as a plan defect naming the column, not as a chunk of individually rejected rows.</summary>
    [Fact]
    public async Task A_binding_to_a_column_the_target_does_not_have_names_the_column_instead_of_rejecting_every_row()
    {
        await using var rig = await RigAsync(One(TaskOf("app.Missing",
            "SELECT s.[Id] AS [Id], s.[V] AS [V], s.[Id] AS [__k0] FROM [dbo].[Wide] AS s", ["__k0"],
            [("Id", "Id"), ("V", "V")])));
        long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 100, ErrorMode = "skip" });

        Assert.Equal(RunStatus.Failed, (await rig.Engine.RunAsync(runId, new TransferControl(), default)).Status);
        Assert.Equal(0, await rig.Tgt.CountAsync("app.Missing"));
        Assert.Equal(0, rig.Repo.ErrorRowCount(runId, "T01"));                        // not 400 error rows blaming the data
        Assert.Contains("V", rig.Repo.Task(runId, "T01")!.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ruling 147 (and 164, which moved this test): a chunk whose every row fails alike on a CHECK is N rejected rows, not a broken
    /// load - every row is recorded with its own reason. Ruling 192 (open item 30) then judges the task: its whole source (one chunk,
    /// fewer than <see cref="TaskRunner.ZeroLoadChunks"/>) loaded nothing, so a CHECK no row satisfies fails the task as bad_task,
    /// naming the error and its number, instead of completing under a headline that reads as success. Resume is the operator's "these
    /// rows really are bad": the chunk is already committed, so the resumed task finishes Done without loading or recording anything
    /// twice. At chunk size 4 the 8 rows are two full chunks and the source ends on the boundary, so the end is seen by an empty read.
    /// </summary>
    [Theory]
    [InlineData(100, "the first chunk")]
    [InlineData(4, "the first 2 chunks")]
    public async Task A_check_no_source_row_satisfies_fails_the_task_and_resume_accepts_the_rejects(int chunkSize, string first)
    {
        await using var rig = await RigAsync(One(TaskOf("app.Uni",
            "SELECT s.[Id] AS [Id], s.[Qty] AS [Qty], s.[Id] AS [__k0] FROM [dbo].[Uni] AS s", ["__k0"],
            [("Id", "Id"), ("Qty", "Qty")])));
        long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = chunkSize, ErrorMode = "skip" });

        var outcome = await rig.Engine.RunAsync(runId, new TransferControl(), default);

        Assert.True(outcome.Status == RunStatus.Failed,
            $"a task whose every source row was rejected ended {outcome.Status} instead of failing as a plan defect: {outcome.Error}");
        var task = rig.Repo.Task(runId, "T01")!;
        Assert.Equal(TransferTaskStatus.Failed, task.Status);
        Assert.True(task.Error!.Contains($"Every row of {first} of app.Uni was rejected (8 rows)", StringComparison.Ordinal)
                    && task.Error.Contains("error 547", StringComparison.Ordinal) && task.Error.Contains("CK_Uni_Qty", StringComparison.Ordinal),
            "the failure does not say every row was rejected and name the error with its number: " + task.Error);
        Assert.Equal(0, await rig.Tgt.CountAsync("app.Uni"));
        var rejected = rig.Repo.ErrorRows(runId, "T01", 100);
        Assert.Equal(8, rejected.Count);                                               // every one of dbo.Uni's 8 source rows, recorded
        Assert.All(rejected, e => Assert.True(e.Error.Contains("CK_Uni_Qty", StringComparison.Ordinal), e.Error));
        // Ruling 208: each recorded row keeps the server's number beside its text.
        Assert.True(rejected.All(e => e.ErrorNumber == 547),
            "a rejected row was recorded without its error number: " + string.Join(", ", rejected.Select(e => e.ErrorNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null")));

        var resumed = await rig.Engine.RunAsync(runId, new TransferControl(), default);
        Assert.True(resumed.Status == RunStatus.Completed,
            $"Resume did not carry on past the judgement (it ended {resumed.Status}: {resumed.Error}), so rows that really are bad can "
            + "never be accepted");
        task = rig.Repo.Task(runId, "T01")!;
        Assert.Equal(TransferTaskStatus.Done, task.Status);
        Assert.Equal(8, task.RowsError);
        Assert.Equal(8, rig.Repo.ErrorRowCount(runId, "T01"));                         // nothing recorded twice
    }

    private static TaskPlan Okay(string vExpression, string where = "") => TaskOf("app.Okay",
        $"SELECT s.[Id] AS [Id], {vExpression} AS [V], s.[Id] AS [__k0] FROM [dbo].[Wide] AS s{where}", ["__k0"], [("Id", "Id"), ("V", "V")]);

    /// <summary>
    /// Ruling 192 at its bound: 400 source rows in chunks of 50, and a CHECK no row satisfies. The task is failed once the first
    /// <see cref="TaskRunner.ZeroLoadChunks"/> chunks have loaded nothing - 150 rows bisected and recorded - rather than after all eight.
    /// <b>Harm</b> (5.7 review F1): the whole table single-row bisected, ~76 rows/s, a day and a half for 10 M rows, then a completed run.
    /// </summary>
    [Fact]
    public async Task A_task_whose_first_three_chunks_load_nothing_stops_there()
    {
        await using var rig = await RigAsync(One(Okay("s.[V]")));
        long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 50, ErrorMode = "skip" });

        var outcome = await rig.Engine.RunAsync(runId, new TransferControl(), default);

        Assert.True(outcome.Status == RunStatus.Failed,
            $"a task whose first 3 chunks loaded nothing ended {outcome.Status}: every one of its 400 rows was bisected and rejected");
        var task = rig.Repo.Task(runId, "T01")!;
        Assert.True(task.RowsError == 150 && rig.Repo.ErrorRowCount(runId, "T01") == 150,
            $"the task went on past its first 3 empty chunks: {task.RowsError} rows rejected, {rig.Repo.ErrorRowCount(runId, "T01")} recorded "
            + "(expected 150 of the 400, then a failed task)");
        Assert.True(task.Error!.StartsWith("Every row of the first 3 chunks of app.Okay was rejected (150 rows) and none loaded",
            StringComparison.Ordinal), task.Error);
        Assert.Contains("on 150 of 150 rows, was error 547", task.Error, StringComparison.Ordinal);
        Assert.Contains("Resume carries on from the next chunk", task.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guard counts the first chunks as a whole, and only them. A task whose first chunk rejects everything but whose second loads a
    /// row is data, not a plan defect, and runs to the end; and after a legitimately all-rejecting start, Resume goes on to load the rest
    /// without judging the task again. <b>Harm:</b> a guard that re-judged on resume, or judged any later run of rejected chunks, would
    /// fail a correct migration the operator had already accepted.
    /// </summary>
    [Fact]
    public async Task Only_the_first_chunks_together_are_judged_and_resume_loads_the_rest()
    {
        // Rows 1-50 and 101-150 bad, 51-100 good: chunk 2 loads, so nothing is judged.
        await using (var rig = await RigAsync(One(Okay("CASE WHEN s.[Id] BETWEEN 51 AND 100 OR s.[Id] > 150 THEN N'ok' ELSE s.[V] END"))))
        {
            long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 50, ErrorMode = "skip" });
            var outcome = await rig.Engine.RunAsync(runId, new TransferControl(), default);
            Assert.True(outcome.Status == RunStatus.Completed,
                $"a task whose second chunk loaded rows was failed as if it had loaded nothing: {outcome.Error}");
            Assert.Equal(300, await rig.Tgt.CountAsync("app.Okay"));
        }

        // Rows 1-200 bad (the first four chunks), 201-400 good: judged once, after chunk 3. Resume rejects chunk 4 as well - still
        // nothing loaded - and must not judge again; then it loads the other 200.
        await using (var rig = await RigAsync(One(Okay("CASE WHEN s.[Id] > 200 THEN N'ok' ELSE s.[V] END"))))
        {
            long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 50, ErrorMode = "skip" });
            var first = await rig.Engine.RunAsync(runId, new TransferControl(), default);
            Assert.True(first.Status == RunStatus.Failed, $"a task whose first 3 chunks loaded nothing ended {first.Status}");
            var resumed = await rig.Engine.RunAsync(runId, new TransferControl(), default);
            Assert.True(resumed.Status == RunStatus.Completed,
                $"Resume after the judgement did not load the rest (it ended {resumed.Status}: {resumed.Error})");
            Assert.Equal(200, await rig.Tgt.CountAsync("app.Okay"));
            var task = rig.Repo.Task(runId, "T01")!;
            Assert.True(task.RowsDone == 200 && task.RowsError == 200 && rig.Repo.ErrorRowCount(runId, "T01") == 200,
                $"after resume: {task.RowsDone} loaded, {task.RowsError} rejected, {rig.Repo.ErrorRowCount(runId, "T01")} recorded");
        }

        // Exactly three full chunks, all bad: judged at chunk 3; the resume's first read is empty, and that end must not judge again.
        await using (var rig = await RigAsync(One(Okay("s.[V]", " WHERE s.[Id] <= 150"))))
        {
            long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 50, ErrorMode = "skip" });
            var first = await rig.Engine.RunAsync(runId, new TransferControl(), default);
            Assert.True(first.Status == RunStatus.Failed, $"a task whose 3 chunks loaded nothing ended {first.Status}");
            var resumed = await rig.Engine.RunAsync(runId, new TransferControl(), default);
            Assert.True(resumed.Status == RunStatus.Completed,
                $"Resume was judged again at the end of the source (it ended {resumed.Status}: {resumed.Error})");
            Assert.Equal(150, rig.Repo.Task(runId, "T01")!.RowsError);
        }
    }

    /// <summary>
    /// Ruling 201's limit: the re-run exemption is for duplicate keys only. A table that held rows before the run and whose rejects
    /// include an FK or CHECK violation (547) is still stopped - whether every reject is a CHECK, or half of them are duplicates of the
    /// rows already there. <b>Harm:</b> an exemption keyed on "the target was not empty" alone would let a wrong mapping in a re-run
    /// shred the table row by row again, which is open item 30 all over.
    /// </summary>
    [Theory]
    [InlineData("s.[V]")]                                                  // every reject a CHECK violation
    [InlineData("CASE WHEN s.[Id] <= 50 THEN N'ok' ELSE s.[V] END")]       // 1-50 duplicates of the rows already there, 51-100 CHECK
    public async Task A_rerun_into_a_non_empty_table_still_stops_when_a_reject_is_not_a_duplicate_key(string v)
    {
        await using var rig = await RigAsync(One(Okay(v, " WHERE s.[Id] <= 100")));
        await rig.Tgt.ExecAsync("INSERT app.Okay (Id, V) SELECT TOP (50) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), N'ok' FROM sys.all_objects;");
        long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 50, ErrorMode = "skip" });

        var outcome = await rig.Engine.RunAsync(runId, new TransferControl(), default);

        Assert.True(outcome.Status == RunStatus.Failed,
            $"a re-run whose rejects include a CHECK violation ended {outcome.Status}: the re-run exemption covered more than duplicate keys");
        Assert.True(rig.Repo.Task(runId, "T01")!.Error!.Contains("CK_Okay_V", StringComparison.Ordinal), rig.Repo.Task(runId, "T01")!.Error);
    }

    /// <summary>
    /// Ruling 201 across a pause: a re-run of 100 rows into a table that already holds all of them, paused after its first chunk. The
    /// resumed segment's tally sees only chunk 2's rejects, so the duplicates have to be shown from the task's recorded error rows.
    /// <b>Harm:</b> judged on the tally alone, a pause would turn the confirmed re-run back into a stopped task.
    /// </summary>
    [Fact]
    public async Task A_paused_rerun_of_duplicates_is_exempt_on_its_recorded_rejects()
    {
        await using var rig = await RigAsync(One(Okay("N'ok'", " WHERE s.[Id] <= 100")));
        await rig.Tgt.ExecAsync("INSERT app.Okay (Id, V) SELECT TOP (100) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), N'ok' FROM sys.all_objects;");
        long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 50, ErrorMode = "skip" });
        var control = new TransferControl();
        control.ChunkCommitted += c => { if (c.ChunkNo == 1) control.RequestPause(); };
        Assert.Equal(RunStatus.Paused, (await rig.Engine.RunAsync(runId, control, default)).Status);

        var resumed = await rig.Engine.RunAsync(runId, new TransferControl(), default);

        Assert.True(resumed.Status == RunStatus.Completed,
            $"the paused re-run of duplicates was stopped on resume ({resumed.Status}): {resumed.Error}");
        var task = rig.Repo.Task(runId, "T01")!;
        Assert.True(task.RowsDone == 0 && task.RowsError == 100, $"{task.RowsDone} loaded, {task.RowsError} rejected");
    }

    /// <summary>
    /// Review F3: the judgement lands in a segment that rejected nothing of its own - 100 bad rows in chunks of 50, paused after chunk
    /// 2, and the resumed segment's first read is the empty end. The reason still names the error, from the recorded rows.
    /// <b>Harm:</b> the one sentence that tells the operator what went wrong vanished exactly when a pause came first.
    /// </summary>
    [Fact]
    public async Task A_judgement_after_a_pause_names_the_error_from_the_recorded_rows()
    {
        await using var rig = await RigAsync(One(Okay("s.[V]", " WHERE s.[Id] <= 100")));
        long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 50, ErrorMode = "skip" });
        var control = new TransferControl();
        control.ChunkCommitted += c => { if (c.ChunkNo == 2) control.RequestPause(); };
        Assert.Equal(RunStatus.Paused, (await rig.Engine.RunAsync(runId, control, default)).Status);

        Assert.Equal(RunStatus.Failed, (await rig.Engine.RunAsync(runId, new TransferControl(), default)).Status);

        string error = rig.Repo.Task(runId, "T01")!.Error!;
        Assert.True(error.Contains("The most common recorded error, on 100 of 100 recorded rows, was error 547: ", StringComparison.Ordinal)
                    && error.Contains("CK_Okay_V", StringComparison.Ordinal),
            "the reason for a judgement after a pause does not name the error: " + error);
    }

    /// <summary>
    /// A keyless task loads in one transaction, so the judgement rolls it back whole: nothing is in the target, nothing is recorded,
    /// and the reason says so. <b>Harm:</b> the keyless path bisects just as slowly, and it would otherwise run to the end of the table.
    /// </summary>
    [Theory]
    [InlineData("", "the first 3 chunks", "150")]
    [InlineData(" WHERE s.[Id] <= 30", "the first chunk", "30")]   // the whole source is one short chunk
    public async Task A_keyless_task_whose_first_three_chunks_load_nothing_is_rolled_back_and_failed(string where, string first, string rows)
    {
        await using var rig = await RigAsync(One(TaskOf("app.FlatOkay", $"SELECT s.[V] AS [V] FROM [dbo].[Wide] AS s{where}", [], [("V", "V")])));
        long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 50, ErrorMode = "skip" });

        var outcome = await rig.Engine.RunAsync(runId, new TransferControl(), default);
        Assert.True(outcome.Status == RunStatus.Failed,
            $"a keyless task whose first chunks loaded nothing ended {outcome.Status}: every row bisected and rejected");
        var task = rig.Repo.Task(runId, "T01")!;
        Assert.True(task.Error!.StartsWith($"Every row of {first} of app.FlatOkay was rejected ({rows} rows)", StringComparison.Ordinal)
                    && task.Error.Contains("has no key", StringComparison.Ordinal),
            "the keyless task was not failed after its first 3 empty chunks: " + task.Error);
        Assert.Equal(0, await rig.Tgt.CountAsync("app.FlatOkay"));
        Assert.Equal(0, rig.Repo.ErrorRowCount(runId, "T01"));
    }

    private static TaskPlan Big(string vExpression, string where = "") => TaskOf("app.Big",
        $"SELECT s.[Id] AS [Id], {vExpression} AS [V], s.[Id] AS [__k0] FROM [dbo].[Big] AS s{where}", ["__k0"], [("Id", "Id"), ("V", "V")]);

    /// <summary>
    /// Ruling 207 (open item 45): the first <see cref="TaskRunner.ZeroLoadChunks"/> chunks of every task hold at most
    /// <see cref="TaskRunner.SmallChunkRows"/> rows, whatever the chunk size, so the zero-load guard judges a task on ~3,000 rows.
    /// <b>Harm</b> (sweep A review F5): at the default 100,000 a wrong FK on a big table stops only after 300,000 single-row rejects,
    /// about an hour. Good tables go on at full size from chunk 4.
    /// </summary>
    [Fact]
    public async Task The_first_three_chunks_of_a_task_hold_at_most_1000_rows_whatever_the_chunk_size()
    {
        await using var rig = await RigAsync(One(Big("N'ok'")));
        long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 100_000, ErrorMode = "skip" });
        var done = new List<long>();
        var control = new TransferControl();
        control.ChunkCommitted += c => done.Add(c.RowsDone);

        Assert.Equal(RunStatus.Completed, (await rig.Engine.RunAsync(runId, control, default)).Status);

        Assert.True(done.SequenceEqual(new long[] { 1_000, 2_000, 3_000, 4_500 }),
            "expected exactly three chunks of 1,000 rows and then the other 1,500 in one (1000, 2000, 3000, 4500 done); rows done "
            + "after each chunk were " + string.Join(", ", done));
        Assert.Equal(4_500, await rig.Tgt.CountAsync("app.Big"));
        Assert.Equal(4_500, await rig.Tgt.ScalarAsync<int>("SELECT COUNT(DISTINCT Id) FROM app.Big"));
    }

    /// <summary>
    /// Keyset paging does not persist the chunk size, so a checkpoint taken after a small first chunk resumes correctly: the resumed
    /// segment reads chunks 2 and 3 small as well (by the checkpoint's chunk number) and loads every row exactly once. <b>Harm:</b> a
    /// resume that skipped or repeated the rows between a small chunk's last key and the next read.
    /// </summary>
    [Fact]
    public async Task A_resume_after_a_small_first_chunk_loads_every_row_exactly_once()
    {
        await using var rig = await RigAsync(One(Big("N'ok'")));
        long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 100_000, ErrorMode = "skip" });
        var control = new TransferControl();
        control.ChunkCommitted += c => { if (c.ChunkNo == 1) control.RequestPause(); };
        var first = await rig.Engine.RunAsync(runId, control, default);
        Assert.True(first.Status == RunStatus.Paused && await rig.Tgt.CountAsync("app.Big") == 1_000,
            $"the run did not pause after a first chunk of 1,000 rows: {first.Status}, {await rig.Tgt.CountAsync("app.Big")} rows in the target");

        var done = new List<long>();
        var again = new TransferControl();
        again.ChunkCommitted += c => done.Add(c.RowsDone);
        Assert.Equal(RunStatus.Completed, (await rig.Engine.RunAsync(runId, again, default)).Status);

        Assert.True(done.SequenceEqual(new long[] { 2_000, 3_000, 4_500 }),
            "expected the resumed segment to size chunks 2 and 3 small by the checkpoint's chunk number and then read the rest at the "
            + "chunk size (2000, 3000, 4500 done); the resumed chunks were " + string.Join(", ", done));
        Assert.Equal(4_500, await rig.Tgt.CountAsync("app.Big"));
        Assert.Equal(4_500, await rig.Tgt.ScalarAsync<int>("SELECT COUNT(DISTINCT Id) FROM app.Big"));
        Assert.Equal(4_500, rig.Repo.Task(runId, "T01")!.RowsDone);
    }

    /// <summary>
    /// The same cap on the keyless path, which judges its first chunks inside its one transaction: 3,001 rows no row of which satisfies
    /// the CHECK, at the default chunk size, are judged on the first 3,000. <b>Harm:</b> judged on the whole of a 100,000-row first chunk.
    /// </summary>
    [Fact]
    public async Task A_keyless_task_is_judged_on_its_first_3000_rows_at_the_default_chunk_size()
    {
        await using var rig = await RigAsync(One(TaskOf("app.FlatOkay", "SELECT s.[V] AS [V] FROM [dbo].[Big] AS s WHERE s.[Id] <= 3001", [], [("V", "V")])));
        long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 100_000, ErrorMode = "skip" });

        Assert.Equal(RunStatus.Failed, (await rig.Engine.RunAsync(runId, new TransferControl(), default)).Status);

        string error = rig.Repo.Task(runId, "T01")!.Error!;
        Assert.True(error.StartsWith("Every row of the first 3 chunks of app.FlatOkay was rejected (3,000 rows)", StringComparison.Ordinal),
            "the keyless task was not judged on its first three 1,000-row chunks: " + error);
        Assert.Equal(0, await rig.Tgt.CountAsync("app.FlatOkay"));
    }

    /// <summary>
    /// Ruling 207 (open item 15): a row whose error ends the transaction (here a target trigger's ROLLBACK) costs a restart and a reload
    /// of every row confirmed so far, once per bisection level - measured 18 restarts and 7.0 s for one such row in a 100,000-row chunk,
    /// against 0.5 s in a 1,000-row one. So a chunk of more than <see cref="TaskRunner.SmallChunkRows"/> rows is not bisected across
    /// restarts: at the first one it is given back, and the same rows are read again in chunks of 1,000, then the chunk size resumes.
    /// <b>Harm:</b> up to ~17 reloads of up to 100,000 rows each to reject one row. 12,000 rows, chunk size 5,000, one bad row at 7,000:
    /// chunks 1-3 are small anyway (open item 45); chunk 4 (rows 3001-8000) meets the row and is read again as five 1,000-row chunks;
    /// then the chunk size applies again (rows 8001-12000 in one chunk).
    /// </summary>
    [Fact]
    public async Task A_transaction_ending_row_error_in_a_big_chunk_is_isolated_in_1000_row_chunks()
    {
        await using var rig = await RigAsync(One(TaskOf("app.Many",
            "SELECT s.[Id] AS [Id], s.[V] AS [V], s.[Id] AS [__k0] FROM [dbo].[Many] AS s", ["__k0"], [("Id", "Id"), ("V", "V")])));
        long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 5_000, ErrorMode = "skip", FireTriggers = true });
        var done = new List<long>();
        var control = new TransferControl();
        control.ChunkCommitted += c => done.Add(c.RowsDone);

        var outcome = await rig.Engine.RunAsync(runId, control, default);

        Assert.True(outcome.Status == RunStatus.Completed, $"the run ended {outcome.Status}: {outcome.Error}");
        Assert.True(done.SequenceEqual(new long[] { 1_000, 2_000, 3_000, 4_000, 5_000, 6_000, 6_999, 7_999, 11_999 }),
            "expected the 5,000-row chunk holding the transaction-ending row to be read again as five 1,000-row chunks and then the "
            + "chunk size to apply again (…, 6999, 7999, 11999 done); rows done after each chunk were " + string.Join(", ", done));
        Assert.Equal(11_999, await rig.Tgt.CountAsync("app.Many"));
        Assert.Equal(11_999, await rig.Tgt.ScalarAsync<int>("SELECT COUNT(DISTINCT Id) FROM app.Many"));
        var bad = Assert.Single(rig.Repo.ErrorRows(runId, "T01"));
        Assert.Equal("{\"__k0\":7000}", bad.KeyJson);
    }

    /// <summary>
    /// Harm: the target control table is the resume point. If it is gone while this run has already loaded rows, resuming from
    /// Checkpoint.Start reloads them - and on a table without a unique key nothing objects, so the migration "succeeds" with every
    /// loaded row duplicated. A missing checkpoint under recorded progress has to stop the task, not restart it.
    /// </summary>
    [Fact]
    public async Task A_resume_whose_checkpoint_has_vanished_stops_instead_of_silently_reloading_from_the_beginning()
    {
        await using var rig = await RigAsync(One(TaskOf("app.Wide",
            "SELECT s.[Id] AS [Id], s.[V] AS [V], s.[Id] AS [__k0] FROM [dbo].[Wide] AS s", ["__k0"],
            [("Id", "Id"), ("V", "V")])));
        long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 100, Parallelism = 1, ErrorMode = "skip" });
        var control = new TransferControl();
        control.ChunkCommitted += c => { if (c.ChunkNo == 2) control.RequestPause(); };

        Assert.Equal(RunStatus.Paused, (await rig.Engine.RunAsync(runId, control, default)).Status);
        Assert.Equal(200, await rig.Tgt.CountAsync("app.Wide"));

        await rig.Tgt.ExecAsync("DELETE FROM dbo.__dbm_checkpoint WHERE task_id = N'T01';");

        var outcome = await rig.Engine.RunAsync(runId, new TransferControl(), default);

        Assert.Equal(RunStatus.Failed, outcome.Status);
        Assert.Equal(200, await rig.Tgt.CountAsync("app.Wide"));                      // 200, not 600
        Assert.Contains("checkpoint", rig.Repo.Task(runId, "T01")!.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Harm: a task that moves no rows at all and ends "done" is the quietest failure a migration has. The source count is the only
    /// thing that knows the table should not be empty, so the run has to say the two disagree rather than leave an empty table and
    /// a green report.
    /// </summary>
    [Fact]
    public async Task A_task_that_loads_nothing_while_its_source_count_is_not_zero_says_so()
    {
        await using var rig = await RigAsync(One(TaskOf("app.Wide",
            "SELECT s.[Id] AS [Id], s.[V] AS [V], s.[Id] AS [__k0] FROM [dbo].[Wide] AS s WHERE 1 = 0", ["__k0"],
            [("Id", "Id"), ("V", "V")], "SELECT COUNT_BIG(*) FROM [dbo].[Wide]")));
        long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 100, ErrorMode = "skip" });

        Assert.Equal(RunStatus.Completed, (await rig.Engine.RunAsync(runId, new TransferControl(), default)).Status);
        Assert.Equal(0, await rig.Tgt.CountAsync("app.Wide"));
        Assert.Equal(400, rig.Repo.Task(runId, "T01")!.RowsSource);
        Assert.Contains(rig.Svc.Sink.Events, e => e.Type == "log"
            && (e.Payload?.ToString() ?? "").Contains("no rows")
            && (e.Payload?.ToString() ?? "").Contains("400"));
    }

    private static TaskPlan WidePlan() => TaskOf("app.Wide",
        "SELECT s.[Id] AS [Id], s.[V] AS [V], s.[Id] AS [__k0] FROM [dbo].[Wide] AS s", ["__k0"], [("Id", "Id"), ("V", "V")]);

    /// <summary>The checkpoint's rows_done as a second connection sees it; -1 when the task has no checkpoint row.</summary>
    private static long CheckpointRowsDone(TempDatabase tgt, long runId, string taskId)
    {
        using var conn = new SqlConnection(tgt.ConnectionString);
        conn.Open();
        using var cmd = new SqlCommand(
            "SELECT ISNULL((SELECT rows_done FROM dbo.__dbm_checkpoint WHERE run_id = @r AND task_id = @t), -1)", conn);
        cmd.Parameters.Add(new SqlParameter("@r", System.Data.SqlDbType.BigInt) { Value = runId });
        cmd.Parameters.Add(new SqlParameter("@t", System.Data.SqlDbType.NVarChar, 64) { Value = taskId });
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Harm, measured by the reviewer twice: two engines on one run id, a target with no unique key, and every row is loaded exactly
    /// twice while BOTH calls return Completed. The checkpoint primary key does not serialise them - only the first chunk can collide
    /// on the INSERT, and from there both runners just overwrite each other's UPDATE. A run left "running" has to stay resumable,
    /// because that is what a crash leaves behind, so the engine needs a live-versus-crashed signal of its own (ruling 103).
    /// </summary>
    [Fact]
    public async Task A_second_runner_on_the_same_run_is_refused_before_it_copies_a_row()
    {
        var plan = One(WidePlan());
        await using var rig = await RigAsync(plan);
        var second = new TransferEngine(rig.Svc.Services, plan, fx.Src.ConnectionString, rig.Tgt.ConnectionString);
        // KeepControlTable so the damage on show is the duplicated rows themselves: without it the second runner finishes the run and
        // drops the checkpoint table under the first one, which then dies on its next upsert and hides the doubling behind a crash.
        long runId = rig.Engine.CreateRun(1,
            new TransferOptions { ChunkSize = 100, Parallelism = 1, ErrorMode = "skip", KeepControlTable = true });

        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var control = new TransferControl();
        control.ChunkCommitted += _ =>
        {
            holding.TrySetResult();
            release.Task.GetAwaiter().GetResult();   // the first runner stays inside the run while the second one tries
        };

        var first = Task.Run(() => rig.Engine.RunAsync(runId, control, default));
        await holding.Task;

        var refusal = await Record.ExceptionAsync(() => second.RunAsync(runId, new TransferControl(), default));

        release.TrySetResult();
        var outcome = await first;

        Assert.Equal(400, await rig.Tgt.CountAsync("app.Wide"));                                    // not 700, not 800
        Assert.Equal(400, await rig.Tgt.ScalarAsync<int>("SELECT COUNT(DISTINCT Id) FROM app.Wide"));
        Assert.Equal(RunStatus.Completed, outcome.Status);
        var refused = Assert.IsType<TransferException>(refusal);
        Assert.Equal("run_in_progress", refused.Code);
        Assert.Contains($"run {runId}", refused.Message, StringComparison.Ordinal);                                           // and it says which run
    }

    /// <summary>
    /// Open item 23 / rulings 209, 212. Two project folders - two terminals, or the CLI beside a desktop session - each create their own
    /// run against one target, and both are run 1: run ids start at 1 in every project (review MED-1). The lock is the target's: the
    /// second is refused before it copies a row, told which target and which project's run holds it - not "run 1 is already being
    /// run", which names neither - and the target is free again once the first is done.
    /// </summary>
    [Fact]
    public async Task A_second_project_loading_the_same_target_is_refused_naming_the_target_and_the_holder()
    {
        var plan = One(WidePlan());
        await using var rig = await RigAsync(plan);
        using var otherProject = new XferServices();
        var second = new TransferEngine(otherProject.Services, plan, fx.Src.ConnectionString, rig.Tgt.ConnectionString);
        var options = new TransferOptions { ChunkSize = 100, Parallelism = 1, ErrorMode = "skip" };
        long runId = rig.Engine.CreateRun(1, options);
        long otherRunId = second.CreateRun(1, options);
        Assert.Equal(runId, otherRunId);                                // the ordinary case: both projects are on run 1

        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var control = new TransferControl();
        control.ChunkCommitted += _ =>
        {
            holding.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };
        var first = Task.Run(() => rig.Engine.RunAsync(runId, control, default));
        await holding.Task;

        var refusal = await Record.ExceptionAsync(() => second.RunAsync(otherRunId, new TransferControl(), default));
        long rowsWhileHeld = await rig.Tgt.CountAsync("app.Wide");
        release.TrySetResult();
        var outcome = await first;

        long rows = await rig.Tgt.CountAsync("app.Wide");
        Assert.True(rows == 400,
            $"a second project's run loaded into the target another project was loading: app.Wide holds {rows} rows, not 400");
        Assert.Equal(RunStatus.Completed, outcome.Status);
        var refused = Assert.IsType<TransferException>(refusal);
        Assert.Equal("run_in_progress", refused.Code);
        Assert.True(refused.Message.Contains(rig.Tgt.Name, StringComparison.Ordinal),
            "the refusal does not name the target database: " + refused.Message);
        Assert.True(refused.Message.Contains($"run {runId} of the project in {rig.Svc.Root}", StringComparison.OrdinalIgnoreCase),
            "the refusal does not name what holds the target (its run and project folder): " + refused.Message);
        Assert.True(refused.Message.Contains("already being loaded by another transfer", StringComparison.Ordinal),
            "another project's run 1 was reported as this project's own run 1 already being run: " + refused.Message);
        Assert.True(rowsWhileHeld <= 100, $"rows were loaded by the refused runner: {rowsWhileHeld}");

        // Not a permanent refusal: once the first run is done the target is free for the other project.
        await using (await RunLock.AcquireAsync(rig.Tgt.ConnectionString, otherRunId, default, TransferEngine.OwnerOf(otherProject.Services))) { }
    }

    /// <summary>
    /// Ruling 212, review HIGH-1 (the reviewer's probe REVF_B). The lock excludes only while a segment executes, and run and task ids
    /// repeat across projects: project A's run 1 pauses at 200 of 400 rows, holding nothing, and project B's fresh run 1 then found A's
    /// checkpoint row (1, T01) and adopted it - it loaded the other 200, recorded 400 as its own, completed, and dropped the table,
    /// so A's resume failed with "no checkpoint row". B must be refused, naming A's folder, and A's resume must complete to 400.
    /// </summary>
    [Fact]
    public async Task A_second_project_is_refused_while_another_projects_run_is_paused_part_way_through_the_target()
    {
        var plan = One(WidePlan());
        await using var rig = await RigAsync(plan);
        using var projectB = new XferServices();
        var engineB = new TransferEngine(projectB.Services, plan, fx.Src.ConnectionString, rig.Tgt.ConnectionString);
        var options = new TransferOptions { ChunkSize = 100, Parallelism = 1, ErrorMode = "skip" };
        long runA = rig.Engine.CreateRun(1, options);
        var pauseA = new TransferControl();
        pauseA.ChunkCommitted += c => { if (c.ChunkNo == 2) pauseA.RequestPause(); };
        Assert.Equal(RunStatus.Paused, (await rig.Engine.RunAsync(runA, pauseA, default)).Status);
        Assert.Equal(200, await rig.Tgt.CountAsync("app.Wide"));

        long runB = engineB.CreateRun(1, options);
        Assert.Equal(runA, runB);                                       // both are run 1, and both plans say T01
        var refusal = await Record.ExceptionAsync(() => engineB.RunAsync(runB, new TransferControl(), default));

        long rowsAfterB = await rig.Tgt.CountAsync("app.Wide");
        Assert.True(rowsAfterB == 200,
            $"project B loaded into the target while project A's run was paused part-way: app.Wide holds {rowsAfterB} rows (A had loaded 200); "
            + $"B {(refusal is null ? "ran" : "was refused: " + refusal.Message)}");
        var refused = Assert.IsType<TransferException>(refusal);
        Assert.Equal("run_in_progress", refused.Code);
        Assert.True(refused.Message.Contains($"run {runA} of the project in {rig.Svc.Root}", StringComparison.OrdinalIgnoreCase),
            "the refusal does not name the paused run's project folder: " + refused.Message);

        var resumed = await rig.Engine.RunAsync(runA, new TransferControl(), default);
        Assert.True(resumed.Status == RunStatus.Completed, $"project A's resume ended {resumed.Status}: {resumed.Error}");
        Assert.Equal(400, await rig.Tgt.CountAsync("app.Wide"));
        Assert.Equal(400, await rig.Tgt.ScalarAsync<int>("SELECT COUNT(DISTINCT Id) FROM app.Wide"));
    }

    /// <summary>
    /// Ruling 212, review LOW-1. A run records the database it loads into, and the lock session says which database the connection
    /// reaches now (<c>DB_NAME()</c>). A segment whose connection now reaches another database is refused before anything is written
    /// there - checkpoints, PreSql or rows - instead of starting the run over in a database nobody reviewed.
    /// </summary>
    [Fact]
    public async Task A_segment_whose_connection_reaches_another_database_than_the_run_recorded_is_refused()
    {
        var plan = One(WidePlan());
        await using var rig = await RigAsync(plan);
        await using var other = await TempDatabase.CreateAsync("dbm_eng_g_oth");
        await other.ExecAsync(TargetSchema);
        long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 100, ErrorMode = "skip" }, ("any server", rig.Tgt.Name));
        var elsewhere = new TransferEngine(rig.Svc.Services, plan, fx.Src.ConnectionString, other.ConnectionString);

        var refusal = await Record.ExceptionAsync(() => elsewhere.RunAsync(runId, new TransferControl(), default));

        long rows = await other.CountAsync("app.Wide");
        int table = await other.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'dbo.__dbm_checkpoint') IS NULL THEN 0 ELSE 1 END");
        Assert.True(rows == 0 && table == 0,
            $"run {runId}, recorded as loading into {rig.Tgt.Name}, ran in {other.Name}: {rows} rows" + (table == 1 ? " and a checkpoint table" : ""));
        var refused = Assert.IsType<TransferException>(refusal);
        Assert.Equal("target_changed", refused.Code);
        Assert.True(refused.Message.Contains(rig.Tgt.Name, StringComparison.Ordinal) && refused.Message.Contains(other.Name, StringComparison.Ordinal),
            refused.Message);
        // The recorded server is not compared (LocalDB renames its instance on every start): "any server" above did not refuse it.
        Assert.Equal(RunStatus.Completed, (await rig.Engine.RunAsync(runId, new TransferControl(), default)).Status);
    }

    /// <summary>
    /// Ruling 215, N-1 (probe REVF_E). A copied project folder carries its state database, and with it the workspace identity, so the
    /// copy's run 1 is indistinguishable from the original's in the target: the copy's fresh run adopted the original's paused
    /// checkpoint, completed, and the original's resume then failed. A fresh run owns no checkpoint yet, so one already there for its
    /// (project, run) is refused, naming the folder that wrote it and saying this folder may be a copy.
    /// </summary>
    [Fact]
    public async Task A_copied_project_folders_fresh_run_is_refused_rather_than_adopting_the_originals_checkpoint()
    {
        var plan = One(WidePlan());
        await using var rig = await RigAsync(plan);
        using var copy = new XferServices();
        copy.Services.Db.Execute("UPDATE transfer_identity SET workspace_id = $W", new { W = rig.Svc.Services.Transfers.WorkspaceId() });
        var engineCopy = new TransferEngine(copy.Services, plan, fx.Src.ConnectionString, rig.Tgt.ConnectionString);
        var options = new TransferOptions { ChunkSize = 100, Parallelism = 1, ErrorMode = "skip" };
        long run = rig.Engine.CreateRun(1, options);
        var pause = new TransferControl();
        pause.ChunkCommitted += c => { if (c.ChunkNo == 2) pause.RequestPause(); };
        Assert.Equal(RunStatus.Paused, (await rig.Engine.RunAsync(run, pause, default)).Status);
        long copyRun = engineCopy.CreateRun(1, options);
        Assert.Equal(run, copyRun);

        var refusal = await Record.ExceptionAsync(() => engineCopy.RunAsync(copyRun, new TransferControl(), default));

        long rows = await rig.Tgt.CountAsync("app.Wide");
        var copied = copy.Services.Transfers.Task(copyRun, "T01")!;
        Assert.True(rows == 200 && copied.RowsDone == 0,
            $"a copy of the project folder took over the original's paused checkpoint: app.Wide holds {rows} rows, the copy recorded "
            + $"{copied.RowsDone} loaded; {(refusal is null ? "it ran" : "refused: " + refusal.Message)}");
        var refused = Assert.IsType<TransferException>(refusal);
        Assert.Equal("run_in_progress", refused.Code);
        Assert.True(refused.Message.Contains(rig.Svc.Root, StringComparison.OrdinalIgnoreCase)
                    && refused.Message.Contains("may be a copy", StringComparison.Ordinal),
            "the refusal does not name the folder that wrote the checkpoint and say this one may be a copy: " + refused.Message);

        var resumed = await rig.Engine.RunAsync(run, new TransferControl(), default);
        Assert.True(resumed.Status == RunStatus.Completed, $"the original's resume ended {resumed.Status}: {resumed.Error}");
        Assert.Equal(400, await rig.Tgt.ScalarAsync<int>("SELECT COUNT(DISTINCT Id) FROM app.Wide"));
    }

    /// <summary>
    /// Ruling 216, N-7 (probe REVF_H). Copies' run ids drift apart - a busy refusal cancels the run it just created and uses up an id -
    /// so the copy's fresh run is run 2 while the original's run 1 is paused at 200 of 400. Keyed on its own run id, the copy was not
    /// refused, loaded the table, and its release deleted the original's checkpoints; the original's resume then failed. Any unfinished
    /// row of this project under any run id refuses a fresh run.
    /// </summary>
    [Fact]
    public async Task A_copied_project_folder_whose_run_ids_have_drifted_is_refused_while_the_original_is_part_way()
    {
        var plan = One(WidePlan());
        await using var rig = await RigAsync(plan);
        using var copy = new XferServices();
        copy.Services.Db.Execute("UPDATE transfer_identity SET workspace_id = $W", new { W = rig.Svc.Services.Transfers.WorkspaceId() });
        var engineCopy = new TransferEngine(copy.Services, plan, fx.Src.ConnectionString, rig.Tgt.ConnectionString);
        var options = new TransferOptions { ChunkSize = 100, Parallelism = 1, ErrorMode = "skip" };
        long run = rig.Engine.CreateRun(1, options);
        var pause = new TransferControl();
        pause.ChunkCommitted += c => { if (c.ChunkNo == 2) pause.RequestPause(); };
        Assert.Equal(RunStatus.Paused, (await rig.Engine.RunAsync(run, pause, default)).Status);
        long refusedEarlier = engineCopy.CreateRun(1, options);                              // the id a busy refusal used up
        copy.Services.Transfers.SetRunStatus(refusedEarlier, RunStatus.Cancelled);
        long copyRun = engineCopy.CreateRun(1, options);
        Assert.NotEqual(run, copyRun);

        var refusal = await Record.ExceptionAsync(() => engineCopy.RunAsync(copyRun, new TransferControl(), default));

        long rows = await rig.Tgt.CountAsync("app.Wide");
        Assert.True(rows == 200,
            $"a copy of the project folder on run {copyRun} loaded into the target while the original's run {run} was paused at 200: "
            + $"app.Wide holds {rows} rows; {(refusal is null ? "it ran" : "refused: " + refusal.Message)}");
        var refused = Assert.IsType<TransferException>(refusal);
        Assert.Equal("run_in_progress", refused.Code);
        Assert.True(refused.Message.Contains($"run {run} of this project", StringComparison.Ordinal)
                    && refused.Message.Contains(rig.Svc.Root, StringComparison.OrdinalIgnoreCase)
                    && refused.Message.Contains("may be a copy", StringComparison.Ordinal),
            "the refusal does not name the original's run and folder and say this one may be a copy: " + refused.Message);

        var resumed = await rig.Engine.RunAsync(run, new TransferControl(), default);
        Assert.True(resumed.Status == RunStatus.Completed, $"the original's resume ended {resumed.Status}: {resumed.Error}");
        Assert.Equal(400, await rig.Tgt.ScalarAsync<int>("SELECT COUNT(DISTINCT Id) FROM app.Wide"));
    }

    /// <summary>
    /// Ruling 217, R3-1 (probe REVF_J). An earlier engine's row (run 1, T01) stands at 300 rows with last key 300 - another project's
    /// run, from before rows carried a project. This project's run 1 was prepared and paused before T01 started, so it recorded 0 for
    /// T01. "At most one chunk ahead" (300 - 0 &lt;= 1,000) let its resume claim the row, continue from key 300 and record 400 rows done
    /// having loaded 100 - the other 300 of its own never loaded, and nothing said so. A pending task never wrote a checkpoint and
    /// claims nothing: the resume is refused as over checkpoints that may be its own, and the row stays unclaimed.
    /// </summary>
    [Fact]
    public async Task A_resume_does_not_claim_an_earlier_engines_checkpoint_for_a_task_it_never_started()
    {
        var plan = One(WidePlan());
        await using var rig = await RigAsync(plan);
        using var earlier = new XferServices();
        var engineEarlier = new TransferEngine(earlier.Services, plan, fx.Src.ConnectionString, rig.Tgt.ConnectionString);
        var options = new TransferOptions { ChunkSize = 100, Parallelism = 1, ErrorMode = "skip" };
        long run = rig.Engine.CreateRun(1, options);
        var stopAtOnce = new TransferControl();
        stopAtOnce.RequestPause();                                     // prepared, then paused before T01 starts
        Assert.Equal(RunStatus.Paused, (await rig.Engine.RunAsync(run, stopAtOnce, default)).Status);
        var pending = rig.Repo.Task(run, "T01")!;
        Assert.True(pending.Status == TransferTaskStatus.Pending && pending.RowsDone == 0 && pending.RowsBefore == 0);   // the premise
        long earlierRun = engineEarlier.CreateRun(1, options);
        Assert.Equal(run, earlierRun);
        var pause = new TransferControl();
        pause.ChunkCommitted += c => { if (c.ChunkNo == 3) pause.RequestPause(); };
        Assert.Equal(RunStatus.Paused, (await engineEarlier.RunAsync(earlierRun, pause, default)).Status);
        // What an upgrade leaves of that run: its row without a project, last written well before the quiet period.
        await rig.Tgt.ExecAsync("UPDATE dbo.__dbm_checkpoint SET project_id = N'', project_folder = NULL, updated_at = DATEADD(HOUR, -1, SYSUTCDATETIME());");
        Assert.Equal(300, await rig.Tgt.CountAsync("app.Wide"));

        var refusal = await Record.ExceptionAsync(() => rig.Engine.RunAsync(run, new TransferControl(), default));

        long rows = await rig.Tgt.CountAsync("app.Wide");
        // -1: the table is gone (a run that completed over the claimed row released it).
        int ownerless = await rig.Tgt.ScalarAsync<int>("""
            IF OBJECT_ID(N'dbo.__dbm_checkpoint', N'U') IS NULL SELECT -1
            ELSE EXEC (N'SELECT COUNT(*) FROM dbo.__dbm_checkpoint WHERE project_id = N''''');
            """);
        var t01 = rig.Repo.Task(run, "T01")!;
        Assert.True(rows == 300 && ownerless == 1,
            $"a resume claimed an earlier engine's checkpoint (300 rows, last key 300) for T01, a task it had never started: app.Wide "
            + $"holds {rows} rows, the run recorded {t01.RowsDone} loaded, "
            + (ownerless < 0 ? "the checkpoint table was dropped" : $"{ownerless} ownerless checkpoints are left") + "; "
            + (refusal is null ? $"it ended {EnumText.ToText(rig.Repo.GetRun(run)!.Status)}" : "refused: " + refusal.Message));
        var refused = Assert.IsType<TransferException>(refusal);
        Assert.True(refused.Code == "run_in_progress" && refused.Message.Contains("may be this run's own checkpoints", StringComparison.Ordinal),
            $"the resume was not refused as over checkpoints that may be its own ({refused.Code}): {refused.Message}");
    }

    /// <summary>Ruling 217, R3-1: the one-chunk tolerance stays for a task the run had started - a crash in its first chunk leaves it
    /// running with 0 recorded while the checkpoint committed with that chunk says 100 - and a pending task claims nothing at all.</summary>
    [Fact]
    public void Only_a_started_task_claims_an_earlier_engines_checkpoint_ahead_of_its_record()
    {
        using var svc = new XferServices();
        var engine = new TransferEngine(svc.Services, One(WidePlan()), fx.Src.ConnectionString, fx.Src.ConnectionString);
        var options = new TransferOptions { ChunkSize = 100 };
        long run = engine.CreateRun(1, options);
        var pending = svc.Services.Transfers.Task(run, "T01")!;
        var crashed = pending with { Status = TransferTaskStatus.Running, StartedAt = DateTimeOffset.UtcNow };

        bool startedClaims = TransferEngine.ClaimOf(run, options, [crashed]).Matches(run, "T01", 100, 0);
        bool pendingClaims = TransferEngine.ClaimOf(run, options, [pending]).Matches(run, "T01", 100, 0);

        Assert.True(startedClaims, "a task that crashed in its first chunk no longer claims its own checkpoint one chunk ahead of its record");
        Assert.False(pendingClaims, "a task still pending claimed an earlier engine's checkpoint: it never wrote one");
    }

    /// <summary>
    /// Ruling 217, R3-2 (probe REVF_K, engine half). A cancel that could not reach the target (F-16) leaves the run's unfinished rows,
    /// written from this very folder. The run has ended and nothing will resume or cancel it again, so a fresh run of the same folder
    /// was refused for good as "may be a copy" of itself. Its own ended run's rows are retired instead, and the fresh run loads.
    /// </summary>
    [Theory]
    [InlineData(RunStatus.Cancelled)]
    [InlineData(RunStatus.Failed)]
    public async Task A_fresh_run_retires_its_own_folders_ended_runs_unfinished_checkpoints(RunStatus ended)
    {
        var plan = One(WidePlan());
        await using var rig = await RigAsync(plan);
        var options = new TransferOptions { ChunkSize = 100, Parallelism = 1, ErrorMode = "skip" };
        long first = rig.Engine.CreateRun(1, options);
        var pause = new TransferControl();
        pause.ChunkCommitted += c => { if (c.ChunkNo == 2) pause.RequestPause(); };
        Assert.Equal(RunStatus.Paused, (await rig.Engine.RunAsync(first, pause, default)).Status);
        rig.Repo.SetRunStatus(first, ended);                              // ended while the target could not be reached: rows left
        Assert.Equal(1, await rig.Tgt.ScalarAsync<int>($"SELECT COUNT(*) FROM dbo.__dbm_checkpoint WHERE run_id = {first} AND done = 0"));
        long second = rig.Engine.CreateRun(1, options with { TruncateTarget = true });

        var outcome = await Record.ExceptionAsync(async () =>
            Assert.Equal(RunStatus.Completed, (await rig.Engine.RunAsync(second, new TransferControl(), default)).Status));

        Assert.True(outcome is null,
            $"a fresh run {second} was refused over its own folder's {EnumText.ToText(ended)} run {first} as if this folder were a copy: "
            + outcome?.Message);
        Assert.Equal(400, await rig.Tgt.ScalarAsync<int>("SELECT COUNT(DISTINCT Id) FROM app.Wide"));
        Assert.Equal(400, await rig.Tgt.CountAsync("app.Wide"));
    }

    /// <summary>
    /// Ruling 218, R4-1 (probe REVF_M). The original project is paused at 200 of 400 on run 1; it is moved, and a copy is left at the
    /// old path whose run 1 is cancelled (a busy-refused start cancels the run it created). The copy's fresh run 2 took the original's
    /// row for its own ended run's and marked it done - and the original's resume then read the task as finished and ended Completed
    /// with 200 of 400 rows, saying nothing. The R3-2 path deletes the ended run's rows instead: nothing of this folder resumes an
    /// ended run, and a mistaken delete makes the victim's resume fail loudly for want of its checkpoint.
    /// </summary>
    [Fact]
    public async Task A_copy_at_the_old_path_never_lets_the_originals_resume_complete_short()
    {
        var plan = One(WidePlan());
        await using var rig = await RigAsync(plan);
        using var copy = new XferServices();
        copy.Services.Db.Execute("UPDATE transfer_identity SET workspace_id = $W", new { W = rig.Svc.Services.Transfers.WorkspaceId() });
        var engineCopy = new TransferEngine(copy.Services, plan, fx.Src.ConnectionString, rig.Tgt.ConnectionString);
        var options = new TransferOptions { ChunkSize = 100, Parallelism = 1, ErrorMode = "skip" };
        long run = rig.Engine.CreateRun(1, options);
        var pause = new TransferControl();
        pause.ChunkCommitted += c => { if (c.ChunkNo == 2) pause.RequestPause(); };
        Assert.Equal(RunStatus.Paused, (await rig.Engine.RunAsync(run, pause, default)).Status);
        // The original wrote its row from the path the copy now occupies (it has since been moved).
        await rig.Tgt.ExecAsync($"UPDATE dbo.__dbm_checkpoint SET project_folder = N'{copy.Root.Replace("'", "''")}';");
        long cancelled = engineCopy.CreateRun(1, options);
        Assert.Equal(run, cancelled);
        copy.Services.Transfers.SetRunStatus(cancelled, RunStatus.Cancelled);
        long copyRun = engineCopy.CreateRun(1, options);
        var stopAtOnce = new TransferControl();
        stopAtOnce.RequestPause();                                      // its start checks run, then it stops before loading
        await Record.ExceptionAsync(() => engineCopy.RunAsync(copyRun, stopAtOnce, default));

        var resumed = await rig.Engine.RunAsync(run, new TransferControl(), default);

        long rows = await rig.Tgt.CountAsync("app.Wide");
        Assert.True(resumed.Status != RunStatus.Completed || rows == 400,
            $"a retired copy's checkpoint let the original's resume end Completed with {rows} of 400 rows and no error");
        Assert.True(resumed.Status == RunStatus.Failed && (resumed.Error ?? "").Contains("has no checkpoint row", StringComparison.Ordinal),
            $"the original's resume did not fail for want of its checkpoint ({EnumText.ToText(resumed.Status)}): {resumed.Error}");
    }

    /// <summary>Ruling 217, R3-2: only this folder's own ENDED run is retired. A paused run of this folder is still a live claim (a
    /// fresh run beside it is refused, and its rows stay unfinished).</summary>
    [Fact]
    public async Task A_fresh_run_still_refuses_its_own_folders_paused_run()
    {
        var plan = One(WidePlan());
        await using var rig = await RigAsync(plan);
        var options = new TransferOptions { ChunkSize = 100, Parallelism = 1, ErrorMode = "skip" };
        long first = rig.Engine.CreateRun(1, options);
        var pause = new TransferControl();
        pause.ChunkCommitted += c => { if (c.ChunkNo == 2) pause.RequestPause(); };
        Assert.Equal(RunStatus.Paused, (await rig.Engine.RunAsync(first, pause, default)).Status);
        long second = rig.Engine.CreateRun(1, options);

        var refusal = await Record.ExceptionAsync(() => rig.Engine.RunAsync(second, new TransferControl(), default));

        int unfinished = await rig.Tgt.ScalarAsync<int>($"""
            IF OBJECT_ID(N'dbo.__dbm_checkpoint', N'U') IS NULL SELECT 0
            ELSE EXEC (N'SELECT COUNT(*) FROM dbo.__dbm_checkpoint WHERE run_id = {first} AND done = 0');
            """);
        Assert.True(refusal is TransferException { Code: "run_in_progress" } && unfinished == 1,
            $"a fresh run retired its own folder's paused run {first} ({unfinished} unfinished checkpoints left) and "
            + (refusal is null ? $"loaded {await rig.Tgt.CountAsync("app.Wide")} rows beside it" : "was refused: " + refusal.Message));
    }

    /// <summary>
    /// Ruling 217, R3-3. A run's end keeps the table when rows of another unfinished run are left - and when that run is this very
    /// project's (a copy of this workspace part-way through the target), the note said "another project's checkpoints". It names the
    /// run of this project instead.
    /// </summary>
    [Fact]
    public async Task A_kept_table_note_names_this_projects_other_unfinished_run_not_another_project()
    {
        var plan = One(WidePlan());
        await using var rig = await RigAsync(plan);
        string id = rig.Svc.Services.Transfers.WorkspaceId();
        long run = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 100, Parallelism = 1, ErrorMode = "skip" });
        var control = new TransferControl();
        int planted = 0;
        // A copy of this workspace starts its run 9 while this one loads (after this run's own start checks).
        control.ChunkCommitted += c =>
        {
            if (c.ChunkNo != 1 || Interlocked.Exchange(ref planted, 1) != 0) return;
            rig.Tgt.ExecAsync($"""
                INSERT dbo.__dbm_checkpoint (run_id, task_id, chunk_no, last_key, rows_done, rows_error, done, updated_at, project_id, project_folder)
                VALUES (9, N'T01', 1, NULL, 100, 0, 0, SYSUTCDATETIME(), N'{id}', N'D:\copy-of-this-project');
                """).GetAwaiter().GetResult();
        };

        Assert.Equal(RunStatus.Completed, (await rig.Engine.RunAsync(run, control, default)).Status);

        string summary = rig.Repo.GetRun(run)!.SummaryJson ?? "";
        Assert.True(summary.Contains("run 9 of this project", StringComparison.Ordinal)
                    && !summary.Contains("another project's checkpoints", StringComparison.Ordinal),
            "the kept-table note calls this project's own other unfinished run another project's checkpoints: " + summary);
    }

    /// <summary>
    /// Ruling 215, N-2 (probe REVF_F). A run cancelled with "Keep the checkpoint table" left its unfinished rows behind, and they
    /// blocked every other project's run on that target for good, with advice (resume or cancel it) that could no longer be followed.
    /// The option keeps the table, never a claim on the target: the cancelled run's rows are marked done.
    /// </summary>
    [Fact]
    public async Task A_run_cancelled_with_the_table_kept_leaves_no_claim_on_the_target()
    {
        var plan = One(WidePlan());
        await using var rig = await RigAsync(plan);
        using var projectB = new XferServices();
        var engineB = new TransferEngine(projectB.Services, plan, fx.Src.ConnectionString, rig.Tgt.ConnectionString);
        long runA = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 100, Parallelism = 1, ErrorMode = "skip", KeepControlTable = true });
        var cancel = new TransferControl();
        cancel.ChunkCommitted += c => { if (c.ChunkNo == 2) cancel.RequestCancel(); };
        Assert.Equal(RunStatus.Cancelled, (await rig.Engine.RunAsync(runA, cancel, default)).Status);
        Assert.Equal(1, await rig.Tgt.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.__dbm_checkpoint"));   // kept, as asked
        long runB = engineB.CreateRun(1, new TransferOptions { ChunkSize = 100, ErrorMode = "skip", TruncateTarget = true });

        var outcome = await Record.ExceptionAsync(async () =>
            Assert.Equal(RunStatus.Completed, (await engineB.RunAsync(runB, new TransferControl(), default)).Status));

        Assert.True(outcome is null,
            "a run cancelled with its checkpoint table kept still blocks another project's run on the target: " + outcome?.Message);
        Assert.Equal(400, await rig.Tgt.CountAsync("app.Wide"));
    }

    /// <summary>
    /// Ruling 212 (c). A run's end removes its own project's checkpoint rows and drops the table only when it is then empty. Project A
    /// completed keeping its checkpoint table for auditing; project B's run then completes and, before, dropped the shared table with
    /// A's rows in it.
    /// </summary>
    [Fact]
    public async Task A_completing_run_removes_only_its_own_checkpoints_and_keeps_the_table_for_another_projects()
    {
        var plan = One(WidePlan());
        await using var rig = await RigAsync(plan);
        using var projectB = new XferServices();
        var engineB = new TransferEngine(projectB.Services, plan, fx.Src.ConnectionString, rig.Tgt.ConnectionString);
        long runA = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 100, ErrorMode = "skip", KeepControlTable = true });
        Assert.Equal(RunStatus.Completed, (await rig.Engine.RunAsync(runA, new TransferControl(), default)).Status);
        long runB = engineB.CreateRun(1, new TransferOptions { ChunkSize = 100, ErrorMode = "skip", TruncateTarget = true });

        Assert.Equal(RunStatus.Completed, (await engineB.RunAsync(runB, new TransferControl(), default)).Status);

        int table = await rig.Tgt.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'dbo.__dbm_checkpoint') IS NULL THEN 0 ELSE 1 END");
        Assert.True(table == 1, "project B's completed run dropped the checkpoint table that still held project A's checkpoints");
        string idA = rig.Svc.Services.Transfers.WorkspaceId();
        Assert.Equal(1, await rig.Tgt.ScalarAsync<int>($"SELECT COUNT(*) FROM dbo.__dbm_checkpoint WHERE project_id = N'{idA}'"));
        Assert.Equal(0, await rig.Tgt.ScalarAsync<int>($"SELECT COUNT(*) FROM dbo.__dbm_checkpoint WHERE project_id <> N'{idA}'"));
        Assert.Contains("still holds another project's checkpoints", projectB.Services.Transfers.GetRun(runB)!.SummaryJson ?? "",
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Open item 20 / ruling 209. The lock connection is idle for the whole run, so anything that drops it - an idle timeout between
    /// here and the server, a failover, a KILL - frees the target without a word, and a second runner could then load beside this
    /// one. The seam kills the lock session after the first chunk: the run must notice at the next chunk commit and stop as
    /// <c>paused</c>, saying why, instead of loading the rest of the table unguarded. A resume takes the lock again and finishes.
    /// </summary>
    [KillSessionFact]
    public async Task A_run_whose_lock_session_is_killed_pauses_saying_the_lock_was_lost()
    {
        await using var rig = await RigAsync(One(WidePlan()));
        long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 100, Parallelism = 1, ErrorMode = "skip" });
        int lockSession = 0;
        TransferEngine.AfterLockTaken = l => lockSession = l.SessionId;
        var control = new TransferControl();
        control.ChunkCommitted += c =>
        {
            if (c.ChunkNo != 1) return;
            using var killer = new SqlConnection(rig.Tgt.ConnectionString);
            killer.Open();
            using var kill = new SqlCommand($"KILL {lockSession}", killer);
            kill.ExecuteNonQuery();
        };
        TransferOutcome outcome;
        try
        {
            outcome = await rig.Engine.RunAsync(runId, control, default);
        }
        finally
        {
            TransferEngine.AfterLockTaken = null;
        }

        long rows = await rig.Tgt.CountAsync("app.Wide");
        Assert.True(outcome.Status == RunStatus.Paused && rows < 400,
            $"the run kept loading after its lock session was killed: it ended {outcome.Status} with {rows} of 400 rows loaded unguarded");
        string summary = rig.Repo.GetRun(runId)!.SummaryJson ?? "";
        Assert.True(summary.Contains("lock on the target database", StringComparison.Ordinal) && summary.Contains("was lost", StringComparison.Ordinal),
            "the paused run does not say its lock was lost: " + summary);

        var resumed = await rig.Engine.RunAsync(runId, new TransferControl(), default);
        Assert.Equal(RunStatus.Completed, resumed.Status);
        Assert.Equal(400, await rig.Tgt.CountAsync("app.Wide"));
        Assert.Equal(400, await rig.Tgt.ScalarAsync<int>("SELECT COUNT(DISTINCT Id) FROM app.Wide"));
    }

    /// <summary>
    /// Harm: the chunk's rows and its checkpoint must commit in one transaction. Written afterwards instead, a crash in between leaves
    /// rows in the target that no checkpoint knows about, and the resume loads them again - on a target with no unique key, silently.
    /// The staged crash is the only place this is visible: between chunks both writes have always happened.
    /// </summary>
    [Fact]
    public async Task A_crash_between_a_chunk_and_its_checkpoint_does_not_load_the_chunk_twice()
    {
        await using var rig = await RigAsync(One(WidePlan()));
        long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 100, Parallelism = 1, ErrorMode = "skip" });
        var tgt = rig.Tgt;
        long checkpointAtCrash = -2, targetAtCrash = -2;
        TaskRunner.AfterChunkTransaction = (taskId, chunkNo) =>
        {
            if (chunkNo != 1) return;
            checkpointAtCrash = CheckpointRowsDone(tgt, runId, taskId);           // read on another connection: is it durable yet?
            targetAtCrash = tgt.CountAsync("app.Wide").GetAwaiter().GetResult();
            throw new InvalidOperationException("staged crash between a chunk and its checkpoint");
        };

        Assert.Equal(RunStatus.Failed, (await rig.Engine.RunAsync(runId, new TransferControl(), default)).Status);
        TaskRunner.AfterChunkTransaction = null;

        Assert.Equal(100, targetAtCrash);
        Assert.Equal(targetAtCrash, checkpointAtCrash);                           // committed together, or not at all

        Assert.Equal(RunStatus.Completed, (await rig.Engine.RunAsync(runId, new TransferControl(), default)).Status);
        Assert.Equal(400, await rig.Tgt.CountAsync("app.Wide"));                  // 400, not 500
        Assert.Equal(400, await rig.Tgt.ScalarAsync<int>("SELECT COUNT(DISTINCT Id) FROM app.Wide"));
    }

    /// <summary>
    /// Harm (ruling 106, which overrides the brief's ordering): task PreSql is operator-authored text carried verbatim. Re-run at the
    /// start of a resume segment, a DELETE there wipes what the previous segment loaded and the run still reports completed.
    /// </summary>
    [Fact]
    public async Task Task_PreSql_runs_when_a_task_starts_and_not_again_on_a_resume()
    {
        var task = WidePlan();
        task.PreSql = ["DELETE FROM app.Wide;"];
        await using var rig = await RigAsync(One(task));
        long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 100, Parallelism = 1, ErrorMode = "skip" });
        var control = new TransferControl();
        control.ChunkCommitted += c => { if (c.ChunkNo == 2) control.RequestPause(); };

        Assert.Equal(RunStatus.Paused, (await rig.Engine.RunAsync(runId, control, default)).Status);
        Assert.Equal(200, await rig.Tgt.CountAsync("app.Wide"));

        Assert.Equal(RunStatus.Completed, (await rig.Engine.RunAsync(runId, new TransferControl(), default)).Status);
        Assert.Equal(400, await rig.Tgt.CountAsync("app.Wide"));                  // the first segment's 200 rows are still there
        Assert.Equal(400, await rig.Tgt.ScalarAsync<int>("SELECT COUNT(DISTINCT Id) FROM app.Wide"));
    }

    /// <summary>
    /// Harm: a CountSql is an arbitrary plan field returned verbatim. One that yields NULL or no row gives rows_source = 0, and a 0
    /// that means "nobody could count" then silences the very warning that exists to say an empty table is not expected. The ninth
    /// instance of this project's recurring shape - a count of 0 meaning both "none" and "never looked".
    /// </summary>
    [Fact]
    public async Task A_source_count_that_comes_back_with_no_number_is_unknown_not_zero()
    {
        await using var rig = await RigAsync(One(TaskOf("app.Wide",
            "SELECT s.[Id] AS [Id], s.[V] AS [V], s.[Id] AS [__k0] FROM [dbo].[Wide] AS s WHERE 1 = 0", ["__k0"],
            [("Id", "Id"), ("V", "V")], "SELECT CAST(NULL AS bigint)")));
        long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 100, ErrorMode = "skip" });

        Assert.Equal(RunStatus.Completed, (await rig.Engine.RunAsync(runId, new TransferControl(), default)).Status);
        Assert.Equal(0, await rig.Tgt.CountAsync("app.Wide"));
        Assert.Null(rig.Repo.Task(runId, "T01")!.RowsSource);
        Assert.Contains(rig.Svc.Sink.Events, e => e.Type == "log"
            && (e.Payload?.ToString() ?? "").Contains("the source count is unknown"));
        Assert.DoesNotContain(rig.Svc.Sink.Events, e => e.Type == "log"
            && (e.Payload?.ToString() ?? "").Contains("the source count said 0"));   // an empty table declared expected
    }

    /// <summary>
    /// Harm: key_json is how an operator finds the row they were told about. If it carried the value rounded for the target, the row
    /// would not be there. Pinned on the one shape where it could go wrong - a temporal key column that is also a bound column.
    /// </summary>
    [Fact]
    public async Task The_error_row_key_is_the_source_value_not_the_value_rounded_for_the_target()
    {
        await using var rig = await RigAsync(One(TaskOf("app.Tkey",
            "SELECT s.[At] AS [At], s.[Qty] AS [Qty] FROM [dbo].[Tkey] AS s", ["At"], [("At", "At"), ("Qty", "Qty")])));
        long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 10, ErrorMode = "skip" });

        Assert.Equal(RunStatus.Completed, (await rig.Engine.RunAsync(runId, new TransferControl(), default)).Status);
        Assert.Equal(1, await rig.Tgt.CountAsync("app.Tkey"));
        var bad = Assert.Single(rig.Repo.ErrorRows(runId, "T01"));
        Assert.Equal("{\"At\":\"2020-01-01T10:00:03.6000000\"}", bad.KeyJson);        // the source value, which the operator can find
        Assert.DoesNotContain("10:00:04", bad.KeyJson, StringComparison.Ordinal);                              // not the datetime2(0) the target rounds it to
    }
}
