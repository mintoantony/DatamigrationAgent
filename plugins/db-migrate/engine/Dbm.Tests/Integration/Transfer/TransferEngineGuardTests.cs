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
        Assert.Contains("Id", rig.Repo.Task(runId, "T01")!.Error);
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
        Assert.Contains("V", rig.Repo.Task(runId, "T01")!.Error);
    }

    /// <summary>
    /// Harm: a chunk in which every row fails the same way is a broken load, not N bad rows. Treated as rows to skip it would empty
    /// a customer's table into error_row and report the run as "completed with 8 rejected", which reads like a data-quality finding
    /// rather than the plan defect it is.
    /// </summary>
    [Fact]
    public async Task A_chunk_whose_every_row_fails_alike_fails_the_task_even_in_skip_mode()
    {
        await using var rig = await RigAsync(One(TaskOf("app.Uni",
            "SELECT s.[Id] AS [Id], s.[Qty] AS [Qty], s.[Id] AS [__k0] FROM [dbo].[Uni] AS s", ["__k0"],
            [("Id", "Id"), ("Qty", "Qty")])));
        long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 100, ErrorMode = "skip" });

        var outcome = await rig.Engine.RunAsync(runId, new TransferControl(), default);

        Assert.Equal(RunStatus.Failed, outcome.Status);
        Assert.Equal(TransferTaskStatus.Failed, rig.Repo.Task(runId, "T01")!.Status);
        Assert.Contains("CK_Uni_Qty", rig.Repo.Task(runId, "T01")!.Error);
        Assert.Equal(0, await rig.Tgt.CountAsync("app.Uni"));
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
        Assert.Contains("checkpoint", rig.Repo.Task(runId, "T01")!.Error);
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
        Assert.Contains($"run {runId}", refused.Message);                                           // and it says which run
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
        Assert.DoesNotContain("10:00:04", bad.KeyJson);                              // not the datetime2(0) the target rounds it to
    }
}
