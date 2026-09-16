using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Dbm.Core.Transfer;
using Dbm.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Dbm.Tests.Integration.Transfer;

/// <summary>Source with 300 parents, 2000 children (Id 777: Qty = 0 -> CHECK; Id 1500: orphan -> FK) and a 700-row heap.</summary>
public sealed class EngineSourceFixture : IAsyncLifetime
{
    public TempDatabase Src { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Src = await TempDatabase.CreateAsync("dbm_eng_src");
        await Src.ExecAsync("""
            CREATE TABLE dbo.Parent (Id int NOT NULL PRIMARY KEY, Name varchar(50) NOT NULL);
            CREATE TABLE dbo.Child (Id int NOT NULL PRIMARY KEY, ParentId int NOT NULL, Qty int NOT NULL, At datetime NOT NULL);
            CREATE TABLE dbo.Log (Msg varchar(100) NOT NULL);
            GO
            WITH n AS (SELECT TOP (2000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT dbo.Parent (Id, Name) SELECT i, CONCAT('Parent ', i) FROM n WHERE i <= 300;
            WITH n AS (SELECT TOP (2000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT dbo.Child (Id, ParentId, Qty, At)
            SELECT i, CASE WHEN i = 1500 THEN 9999 ELSE 1 + i % 300 END, CASE WHEN i = 777 THEN 0 ELSE 1 + i % 5 END,
                   DATEADD(MILLISECOND, (i % 1000) * 3, CAST('2020-01-01T10:00:00' AS datetime))
            FROM n;
            WITH n AS (SELECT TOP (700) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT dbo.Log (Msg) SELECT CONCAT('event ', i) FROM n;
            """);
    }

    public Task DisposeAsync() => Src.DisposeAsync().AsTask();
}

[Trait("Category", "Integration")]
public sealed class TransferEngineTests(EngineSourceFixture fx) : IClassFixture<EngineSourceFixture>
{
    internal const string TargetSchema = """
        CREATE SCHEMA app;
        GO
        CREATE TABLE app.Parent (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NOT NULL);
        CREATE TABLE app.Child (Id int NOT NULL PRIMARY KEY, ParentId int NOT NULL CONSTRAINT FK_Child_Parent REFERENCES app.Parent (Id),
          Qty int NOT NULL CONSTRAINT CK_Child_Qty CHECK (Qty > 0), At datetime2(0) NOT NULL);
        CREATE TABLE app.Log (Msg nvarchar(100) NOT NULL);
        """;

    private static TaskPlan TaskOf(string target, string query, string[] keys, string[] cols, params string[] dependsOn) => new()
    {
        Target = target,
        SourceQuery = query,
        KeyColumns = keys.ToList(),
        Columns = cols.Select(c => new ColumnBinding(c, c)).ToList(),
        CountSql = $"SELECT COUNT_BIG(*) FROM ({query}) AS q",
        DependsOn = dependsOn.ToList(),
    };

    internal static SqlPlanPayload Plan() => new()
    {
        Order = ["T01", "T02", "T03"],
        Tasks = new()
        {
            ["T01"] = TaskOf("app.Parent", "SELECT s.[Id] AS [Id], s.[Name] AS [Name], s.[Id] AS [__k0] FROM [dbo].[Parent] AS s",
                ["__k0"], ["Id", "Name"]),
            ["T02"] = TaskOf("app.Child", "SELECT s.[Id] AS [Id], s.[ParentId] AS [ParentId], s.[Qty] AS [Qty], s.[At] AS [At], s.[Id] AS [__k0] FROM [dbo].[Child] AS s",
                ["__k0"], ["Id", "ParentId", "Qty", "At"], "T01"),
            ["T03"] = TaskOf("app.Log", "SELECT s.[Msg] AS [Msg] FROM [dbo].[Log] AS s", [], ["Msg"]),
        },
    };

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

    private async Task<Rig> RigAsync(string? seedTarget = null)
    {
        var tgt = await TempDatabase.CreateAsync("dbm_eng_tgt");
        await tgt.ExecAsync(TargetSchema);
        if (seedTarget is not null) await tgt.ExecAsync(seedTarget);
        var svc = new XferServices();
        return new Rig(svc, tgt, new TransferEngine(svc.Services, Plan(), fx.Src.ConnectionString, tgt.ConnectionString));
    }

    private static async Task<bool> ControlTableExistsAsync(TempDatabase tgt)
    {
        await using var conn = new SqlConnection(tgt.ConnectionString);
        await conn.OpenAsync();
        return await ControlTable.ExistsAsync(conn, default);
    }

    private static async Task<Checkpoint?> CheckpointAsync(TempDatabase tgt, long runId, string taskId)
    {
        await using var conn = new SqlConnection(tgt.ConnectionString);
        await conn.OpenAsync();
        return await ControlTable.ReadAsync(conn, runId, taskId, default);
    }

    private async Task AssertExactFinalStateAsync(Rig rig, long runId)
    {
        Assert.Equal(300, await rig.Tgt.CountAsync("app.Parent"));
        Assert.Equal(1998, await rig.Tgt.CountAsync("app.Child"));
        Assert.Equal(700, await rig.Tgt.CountAsync("app.Log"));
        Assert.Equal(0, await rig.Tgt.ScalarAsync<int>($"SELECT COUNT(*) FROM app.Child WHERE Id IN (777, 1500)"));
        var tasks = rig.Repo.Tasks(runId);
        Assert.All(tasks, t => Assert.Equal(TransferTaskStatus.Done, t.Status));
        Assert.Equal(new long[] { 300, 1998, 700 }, tasks.Select(t => t.RowsDone));
        Assert.Equal(new long[] { 0, 2, 0 }, tasks.Select(t => t.RowsError));
        var errors = rig.Repo.ErrorRows(runId, "T02");
        Assert.Equal(2, errors.Count);                                             // recorded exactly once, even across pauses/crashes
        Assert.Equal(new[] { "{\"__k0\":1500}", "{\"__k0\":777}" }, errors.Select(e => e.KeyJson).Order(StringComparer.Ordinal));
        Assert.False(await ControlTableExistsAsync(rig.Tgt));
    }

    [Fact]
    public async Task Completes_every_task_with_exact_counts_rounded_values_and_no_control_table()
    {
        await using var rig = await RigAsync();
        long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 250, Parallelism = 2, ErrorMode = "skip" });
        var outcome = await rig.Engine.RunAsync(runId, new TransferControl(), default);

        Assert.Equal(RunStatus.Completed, outcome.Status);
        Assert.Equal(RunStatus.Completed, rig.Repo.GetRun(runId)!.Status);
        await AssertExactFinalStateAsync(rig, runId);
        var tasks = rig.Repo.Tasks(runId);
        Assert.Equal(new long?[] { 300, 2000, 700 }, tasks.Select(t => t.RowsSource));
        Assert.All(tasks, t => Assert.Equal(0, t.RowsBefore));
        Assert.True(tasks[1].StartedAt >= tasks[0].EndedAt);                      // T02 depends on T01
        Assert.Equal(0, await rig.Tgt.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM app.Child AS t JOIN [{fx.Src.Name}].dbo.Child AS s ON s.Id = t.Id WHERE t.At <> CAST(s.At AS datetime2(0))"));   // V8
        Assert.Contains(rig.Svc.Sink.Events, e => e.Type == "transfer_progress" && !e.Persist);
        Assert.True(rig.Svc.Sink.Count("transfer_task_changed") >= 6);
        Assert.Contains(rig.Svc.Sink.Events, e => e.Type == "transfer_run_changed" && e.Persist);
    }

    [Fact]
    public async Task Pause_mid_table_then_resume_loads_everything_exactly_once()
    {
        await using var rig = await RigAsync();
        long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 200, Parallelism = 1, ErrorMode = "skip" });
        var control = new TransferControl();
        control.ChunkCommitted += c => { if (c.TaskId == "T02" && c.ChunkNo == 3) control.RequestPause(); };

        var paused = await rig.Engine.RunAsync(runId, control, default);
        Assert.Equal(RunStatus.Paused, paused.Status);
        Assert.Equal(RunStatus.Paused, rig.Repo.GetRun(runId)!.Status);
        var child = rig.Repo.Task(runId, "T02")!;
        Assert.Equal(TransferTaskStatus.Paused, child.Status);
        Assert.Equal(600, child.RowsDone);
        Assert.Equal(600, await rig.Tgt.CountAsync("app.Child"));
        Assert.Equal(TransferTaskStatus.Pending, rig.Repo.Task(runId, "T03")!.Status);
        Assert.True(await ControlTableExistsAsync(rig.Tgt));

        var resumed = await rig.Engine.RunAsync(runId, new TransferControl(), default);
        Assert.Equal(RunStatus.Completed, resumed.Status);
        await AssertExactFinalStateAsync(rig, runId);
    }

    [Fact]
    public async Task Hard_cancel_leaves_the_run_running_and_recovery_resumes_without_duplicates()
    {
        await using var rig = await RigAsync();
        long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 200, Parallelism = 2, ErrorMode = "skip" });
        using var cts = new CancellationTokenSource();
        var control = new TransferControl();
        control.ChunkCommitted += c => { if (c.TaskId == "T02" && c.ChunkNo == 4) cts.Cancel(); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Engine.RunAsync(runId, control, cts.Token));
        Assert.Equal(RunStatus.Running, rig.Repo.GetRun(runId)!.Status);               // like a crashed server process

        // The checkpoint is the resume point, so it has to agree with what is actually in the target: a chunk that committed its rows
        // without its checkpoint is loaded twice on resume, and a checkpoint past uncommitted rows skips them for good.
        var cp = await CheckpointAsync(rig.Tgt, runId, "T02");
        Assert.NotNull(cp);
        Assert.Equal(await rig.Tgt.CountAsync("app.Child"), cp!.RowsDone);

        Assert.Equal(1, rig.Repo.RecoverInterrupted());
        Assert.Equal(RunStatus.Paused, rig.Repo.GetRun(runId)!.Status);
        Assert.DoesNotContain(rig.Repo.Tasks(runId), t => t.Status == TransferTaskStatus.Running);

        var fresh = new TransferEngine(rig.Svc.Services, Plan(), fx.Src.ConnectionString, rig.Tgt.ConnectionString);
        var outcome = await fresh.RunAsync(runId, new TransferControl(), default);
        Assert.Equal(RunStatus.Completed, outcome.Status);
        await AssertExactFinalStateAsync(rig, runId);
    }

    /// <summary>The third resume seam: the process dies in the gap between one task finishing and the next one starting.</summary>
    [Fact]
    public async Task Crash_between_two_tasks_resumes_at_the_next_task_and_never_reloads_the_finished_one()
    {
        await using var rig = await RigAsync();
        long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 1000, Parallelism = 1, ErrorMode = "skip" });
        using var cts = new CancellationTokenSource();
        var svc = rig.Svc.Services;
        var control = new TransferControl();
        control.ChunkCommitted += c => { if (c.TaskId == "T01") cts.Cancel(); };   // T01 is one chunk: cancel as it finishes

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Engine.RunAsync(runId, control, cts.Token));
        Assert.Equal(300, await rig.Tgt.CountAsync("app.Parent"));
        Assert.Equal(0, await rig.Tgt.CountAsync("app.Child"));
        rig.Repo.RecoverInterrupted();

        var fresh = new TransferEngine(svc, Plan(), fx.Src.ConnectionString, rig.Tgt.ConnectionString);
        Assert.Equal(RunStatus.Completed, (await fresh.RunAsync(runId, new TransferControl(), default)).Status);
        await AssertExactFinalStateAsync(rig, runId);                              // 300 parents, not 600
    }

    [Fact]
    public async Task Stop_mode_fails_the_run_on_the_first_bad_row_and_keeps_the_checkpoint()
    {
        await using var rig = await RigAsync();
        long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 500, Parallelism = 1, ErrorMode = "stop" });
        var outcome = await rig.Engine.RunAsync(runId, new TransferControl(), default);

        Assert.Equal(RunStatus.Failed, outcome.Status);
        Assert.Contains("T02", outcome.Error);
        var child = rig.Repo.Task(runId, "T02")!;
        Assert.Equal(TransferTaskStatus.Failed, child.Status);
        Assert.Contains("CK_Child_Qty", child.Error);
        Assert.Single(rig.Repo.ErrorRows(runId, "T02"));
        Assert.Equal(500, await rig.Tgt.CountAsync("app.Child"));                   // chunk 2 (501..1000, holds Id 777) rolled back
        Assert.Equal(TransferTaskStatus.Pending, rig.Repo.Task(runId, "T03")!.Status);
        Assert.True(await ControlTableExistsAsync(rig.Tgt));
        Assert.Contains("error", rig.Repo.GetRun(runId)!.SummaryJson);
    }

    [Fact]
    public async Task Cancel_stops_after_the_current_chunk_and_drops_the_control_table()
    {
        await using var rig = await RigAsync();
        long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 100, Parallelism = 1, ErrorMode = "skip" });
        var control = new TransferControl();
        control.ChunkCommitted += c => { if (c.TaskId == "T01" && c.ChunkNo == 1) control.RequestCancel(); };
        var outcome = await rig.Engine.RunAsync(runId, control, default);
        Assert.Equal(RunStatus.Cancelled, outcome.Status);
        Assert.Equal(100, await rig.Tgt.CountAsync("app.Parent"));
        Assert.False(await ControlTableExistsAsync(rig.Tgt));
        Assert.NotNull(rig.Repo.GetRun(runId)!.EndedAt);
    }

    [Fact]
    public async Task Truncate_option_empties_prepopulated_targets_first()
    {
        await using var rig = await RigAsync("""
            INSERT app.Parent (Id, Name) VALUES (5000, N'old');
            INSERT app.Child (Id, ParentId, Qty, At) VALUES (9000, 5000, 1, '2019-01-01');
            INSERT app.Log (Msg) VALUES (N'old');
            """);
        long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 1000, ErrorMode = "skip", TruncateTarget = true });
        var outcome = await rig.Engine.RunAsync(runId, new TransferControl(), default);
        Assert.Equal(RunStatus.Completed, outcome.Status);
        await AssertExactFinalStateAsync(rig, runId);
        Assert.All(rig.Repo.Tasks(runId), t => Assert.Equal(0, t.RowsBefore));
        Assert.Contains(rig.Svc.Sink.Events, e => e.Type == "log" && (e.Payload?.ToString() ?? "").Contains("DELETE app.Parent"));
    }

    /// <summary>
    /// Harm: ControlTable.DropAsync refuses to drop a table of that name that is not ours, and TransferException is caught nowhere else
    /// on the completion path. Left uncaught, a customer's table standing where our checkpoint table used to be turns a migration that
    /// loaded every row into a failed run; swallowed, the table is simply still there afterwards with nothing saying why.
    /// </summary>
    [Fact]
    public async Task A_control_table_that_is_not_ours_is_left_untouched_and_named_rather_than_failing_the_run()
    {
        await using var rig = await RigAsync();
        long runId = rig.Engine.CreateRun(1, new TransferOptions { ChunkSize = 1000, Parallelism = 1, ErrorMode = "skip" });
        var control = new TransferControl();
        var tgt = rig.Tgt;
        control.ChunkCommitted += c =>
        {
            if (c.TaskId != "T03") return;   // the last task: swap our checkpoint table for a customer's, as the engine goes to drop it
            tgt.ExecAsync("""
                DROP TABLE dbo.__dbm_checkpoint;
                GO
                CREATE TABLE dbo.__dbm_checkpoint (note nvarchar(50) NOT NULL);
                GO
                INSERT dbo.__dbm_checkpoint (note) VALUES (N'customer data');
                """).GetAwaiter().GetResult();
        };

        var outcome = await rig.Engine.RunAsync(runId, control, default);

        Assert.Null(outcome.Error);
        Assert.Equal(RunStatus.Completed, outcome.Status);                                  // every row loaded: this is not a failed run
        Assert.Equal(300, await rig.Tgt.CountAsync("app.Parent"));
        Assert.Equal(1998, await rig.Tgt.CountAsync("app.Child"));
        Assert.Equal(700, await rig.Tgt.CountAsync("app.Log"));
        Assert.Equal(1, await rig.Tgt.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.__dbm_checkpoint WHERE note = N'customer data'"));
        Assert.Contains(rig.Svc.Sink.Events, e => e.Type == "log"
            && (e.Payload?.ToString() ?? "").Contains("control_table_mismatch"));           // and it says so, by name
        Assert.Contains("controlTable", rig.Repo.GetRun(runId)!.SummaryJson);               // in the run's own outcome, not only a log line
    }
}
