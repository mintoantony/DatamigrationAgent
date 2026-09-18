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
            GO
            WITH n AS (SELECT TOP (400) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT dbo.Wide (Id, V) SELECT i, CONCAT('v', i) FROM n;
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
        Assert.True(error.Contains("The most common recorded error, on 100 of 100 recorded rows, was: ", StringComparison.Ordinal)
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
