using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Dbm.Core.Transfer;
using Dbm.Tests.Support;
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
}
