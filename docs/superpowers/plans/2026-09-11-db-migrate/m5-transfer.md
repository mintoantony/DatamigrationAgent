# Milestone 5 — Transfer & Final Report

> Part of the db-migrate plan. Read 00-overview.md (Global Constraints + Shared contracts) before any task; every task implicitly includes the Global Constraints.

**Goal:** Execute the approved SQL plan: stream every task from source to target with keyset chunks, `SqlBulkCopy` and one target transaction per chunk that also writes the checkpoint (exactly-once), isolate bad rows by bisection (skip-and-log or stop), honour dependencies/parallelism, pause/resume/cancel, recover from crashes, validate counts and column checksums, store the final report as the Complete artifact, and give the human an Execute screen (pre-flight, options, typed confirmation, live progress) and a Final report screen with standalone export.

**Verified behaviour this milestone relies on** (experiments run 2026-09-11 against LocalDB 17.0.4025 with Microsoft.Data.SqlClient 7.0.3, net8.0; the code below is designed around these facts):

| # | Behaviour | Consequence in the code |
|---|---|---|
| V1 | `SqlBulkCopy(conn, CheckConstraints \| KeepNulls \| KeepIdentity, externalTx)` loads inside the caller's transaction; FK/CHECK violations (547), PK duplicates (2627), `RAISERROR` in a trigger (with `FireTriggers`), divide-by-zero in a persisted computed column (8134) throw `SqlException` and leave `XACT_STATE() = 1`; `tx.Rollback("sp")` to a savepoint taken before the copy works and later copies/commit in the same transaction succeed. Holds for 100 000-row batches failing at row 70 000. | `Bisector` uses savepoints when `XACT_STATE() = 1`. |
| V2 | Too-long strings, `NULL` into NOT NULL and uncastable values fail **client-side** with `InvalidOperationException` (e.g. `The given value 'x' of type String from the data source cannot be converted to type nvarchar for Column 3 [s] Row 2.`); the server transaction survives (`XACT_STATE() = 1`) and none of the batch's rows are visible. | Bisection catches `SqlException` **and** `InvalidOperationException`. |
| V3 | A T-SQL conversion error inside `MERGE` (245) ends the whole transaction (`XACT_STATE() = 0`); a trigger that runs `ROLLBACK` raises 3609 after which even `SELECT XACT_STATE()` on the transaction fails ("The server failed to resume the transaction") and `tx.Rollback()` throws — but a new `BeginTransaction` on the same connection works. `SET XACT_ABORT ON` + FK violation → `XACT_STATE() = 0`. | Any state other than 1 (including a failed probe) = "doomed": roll back (swallowing errors), begin a new transaction, reload the rows already confirmed good, continue bisecting. Keyless (single-transaction) tasks cannot do this and fail the task. |
| V4 | Bulk copy **without** `CheckConstraints` marks the FK untrusted (`is_not_trusted = 1`). | `CheckConstraints` is always set. |
| V5 | `CREATE TABLE #stg` inside the transaction works; `SqlBulkCopy` with `DestinationTableName = "#stg"` works on that connection/transaction; rolling back (to a savepoint or fully) removes `#stg`. | Staging mode: drop-if-exists + create `#stg` per attempt, bulk copy, `MergeSql`, drop. |
| V6 | `SELECT TOP (@__n) q.* FROM (<query with correlated subquery>) AS q WHERE … ORDER BY q.[__k0], q.[__k1]` pages a composite key correctly; reader values come back as `Int32`/`Int16`. | `ChunkPlanner` shape. |
| V7 | A `varchar` key (SQL_Latin1_General_CP1_CI_AS) compared with an **nvarchar** parameter walks a different order and **skipped a row**; typed as `varchar` it walks exactly the `ORDER BY` order. | Last-key JSON stores the SQL type (`t`, size, precision, scale) and parameters are created with that exact type. |
| V8 | `DateTime` → `datetime2(0)` via bulk copy **truncates** (10:00:00.997 → 10:00:00) while `CAST` **rounds** (→ 10:00:01); decimal scale reduction rounds like `CAST` (12.345 → 12.35). SqlClient reads `datetime` rounded to whole ms (….0033333 → ….003). | `ChunkReader` reads `datetime` exactly via `GetSqlDateTime` (1/300 s ticks); `TargetShape.Normalize` rounds temporal values half-up to the target scale (clamped at the type maximum, e.g. `time` 23:59:59.9999999 → 23:59:59.9). Verified: bulk-loaded values then equal `CAST` values and the checksums match for datetime2(0/1/4/7), datetimeoffset(0), time(1). |
| V9 | `SUM(CAST(BINARY_CHECKSUM(CAST(q.[c] AS <target type>)) AS bigint))` equals the target-side sum for nvarchar(max) (9 000-char values), decimal(19,4) from money, bit from `CASE`, char(2); `BINARY_CHECKSUM(NULL)` = 2147483647 for every type; `BINARY_CHECKSUM` on `text` fails ("no comparable columns"). | `RunValidator` checksum SQL + excluded type list. |
| V10 | `TRUNCATE` fails (4712) on a table referenced by another table's FK even when that FK is `NOCHECK`; a self-referencing FK does not block it. Unmapped target columns keep their defaults under `KeepNulls`. | Truncate uses `DELETE` only when another table references it; global `PreSql` (cycle `NOCHECK`) runs **before** truncation. |
| V11 | Cancelling the token passed to `WriteToServerAsync` throws `TaskCanceledException`; disposing the transaction rolls everything back. `HAS_PERMS_BY_NAME` works for `OBJECT INSERT/ALTER`, `DATABASE CREATE TABLE`, `SCHEMA ALTER` (unknown object → 0). | Crash simulation = hard token cancel; preflight permission queries. |

**Test helpers:** M5 tests reuse the upstream helpers:
- `TempDatabase.CreateAsync(prefix)` with `ExecAsync`, `ScalarAsync<T>` and `Name`, plus a one-line `CountAsync` extension added in T5.1.
- `TestWorkspace.OpenServices()`, wrapped by `XferServices` with a recording event sink (T5.3).
- `SampleDatabases.CreateAsync(scale)`, `SampleDatabases.RunAsync`, `SampleSql.ShopV2Schema` and `SampleExtract.CatalogAsync` (T2.1/T2.x).
- `SampleMappings.Approved()` (T3.2).

Every database a test creates has a unique name and is dropped by that exact name.

**Task order:** 5.1 → 5.2 → 5.3 → 5.4 → 5.5 → 5.6 → 5.7 (each builds on the previous ones; M1–M4 must be complete).

---

### Task 5.1: Transfer repo, options, control table, chunk planner

**Files:**
- Create: `plugins/db-migrate/engine/Dbm/Core/Transfer/TransferOptions.cs`
- Create: `plugins/db-migrate/engine/Dbm/Core/Transfer/TransferException.cs`
- Create: `plugins/db-migrate/engine/Dbm/Core/State/TransferRepo.cs`
- Create: `plugins/db-migrate/engine/Dbm/Core/Transfer/KeyCodec.cs`
- Create: `plugins/db-migrate/engine/Dbm/Core/Transfer/ChunkPlanner.cs`
- Create: `plugins/db-migrate/engine/Dbm/Core/Transfer/ControlTable.cs`
- Modify: `plugins/db-migrate/engine/Dbm/Core/DbmServices.cs` (add `Transfers`)
- Create: `plugins/db-migrate/engine/Dbm.Tests/Support/TempDatabaseExtensions.cs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/Transfer/TransferRepoTests.cs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/Transfer/KeyCodecTests.cs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/Transfer/ChunkPlannerTests.cs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Integration/Transfer/ControlTableTests.cs`

**Interfaces:**
- Consumes: `StateDb` (`Open`, `InTransaction`, `Execute`, `Scalar`, `Query`), `Json`, `EnumText`, `Clock` (C1/C2), `SqlQuote.Ident` (C3), `TaskPlan` (C13), C14 enums/options, `SqlConnect.OpenAsync` (C3).
- Produces:
```csharp
namespace Dbm.Core.Transfer;
public sealed record TransferOptions { /* C14 members */ public bool SkipErrors { get; }   /* [JsonIgnore] */ public TransferOptions Normalized(); }
public enum RunStatus { Pending, Running, Paused, Completed, Failed, Cancelled }
public enum TransferTaskStatus { Pending, Running, Paused, Done, Failed }
public sealed class TransferException(string code, string message, IReadOnlyList<string>? details = null) : Exception(message)
{ public string Code { get; } public IReadOnlyList<string> Details { get; } }
public sealed record KeyType(string Name, int Size, byte Precision, byte Scale);
public sealed record KeyValue(IReadOnlyList<KeyType> Types, IReadOnlyList<object> Values);
public static class KeyCodec
{
    public static IReadOnlyList<KeyType> TypesOf(SqlDataReader reader, IReadOnlyList<string> keyColumns);
    public static KeyValue FromRow(DataRow row, IReadOnlyList<string> keyColumns, IReadOnlyList<KeyType> types);
    public static string Encode(KeyValue key);            // [{"t":"int","s":4,"p":10,"c":0,"v":"42"}, …]
    public static KeyValue Decode(string json);
    public static SqlParameter Parameter(string name, KeyType type, object value);
    public static string Display(KeyValue key, IReadOnlyList<string> keyColumns);   // {"__k0":42,…} for error rows / UI
}
public static class ChunkPlanner
{
    public static string Predicate(IReadOnlyList<string> keyColumns);
    public static string Sql(TaskPlan task, bool afterKey);
    public static SqlCommand Command(SqlConnection conn, TaskPlan task, KeyValue? lastKey, int chunkSize);
}
public sealed record Checkpoint(int ChunkNo, string? LastKeyJson, long RowsDone, long RowsError, bool Done) { public static readonly Checkpoint Start; }
public static class ControlTable
{
    public const string Name = "[dbo].[__dbm_checkpoint]";
    public static Task EnsureAsync(SqlConnection conn, CancellationToken ct);
    public static Task<bool> ExistsAsync(SqlConnection conn, CancellationToken ct);
    public static Task<Checkpoint?> ReadAsync(SqlConnection conn, long runId, string taskId, CancellationToken ct);
    public static Task UpsertAsync(SqlConnection conn, SqlTransaction? tx, long runId, string taskId, Checkpoint cp, CancellationToken ct);
    public static Task DropAsync(SqlConnection conn, CancellationToken ct);
}

namespace Dbm.Core.State;
public sealed record TransferRunRow(long Id, int SqlVersion, RunStatus Status, TransferOptions Options, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, string? SummaryJson);
public sealed record TransferTaskRow(long RunId, string TaskId, string Target, int Ordinal, TransferTaskStatus Status, long? RowsSource, long? RowsBefore,
    long RowsDone, long RowsError, string? LastKeyJson, DateTimeOffset? HeartbeatAt, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, string? Error, string? ValidationJson);
public sealed record ErrorRowEntry(long Id, long RunId, string TaskId, string? KeyJson, string? RowJson, string Error, DateTimeOffset Ts);
public sealed class TransferRepo(StateDb db)
{
    public long CreateRun(int sqlVersion, TransferOptions options, IReadOnlyList<(string TaskId, string Target)> tasks);   // run "running", tasks "pending", ordinal = index
    public TransferRunRow? Latest();
    public TransferRunRow? GetRun(long runId);
    public void SetRunStatus(long runId, RunStatus status, string? summaryJson = null);   // ended_at set for completed|failed|cancelled, cleared otherwise; status running clears summary_json
    public IReadOnlyList<TransferTaskRow> Tasks(long runId);                               // by ordinal
    public TransferTaskRow? Task(long runId, string taskId);
    public void SetTaskCounts(long runId, string taskId, long? rowsSource, long? rowsBefore);
    public void UpdateTaskProgress(long runId, string taskId, long rowsDone, long rowsError, string? lastKeyJson);   // heartbeat_at = now
    public void UpdateTaskStatus(long runId, string taskId, TransferTaskStatus status, string? error = null);
    public void SetTaskValidation(long runId, string taskId, string validationJson);
    public void AddErrorRow(long runId, string taskId, string? keyJson, string? rowJson, string error);
    public IReadOnlyList<ErrorRowEntry> ErrorRows(long runId, string? taskId = null, int limit = 100);
    public long ErrorRowCount(long runId, string taskId);
    public int RecoverInterrupted();   // runs running -> paused and their running tasks -> paused; returns runs changed
}
// DbmServices: public TransferRepo Transfers { get; }
```

- [ ] **Step 1: Add the row-count helper for temporary databases**

`plugins/db-migrate/engine/Dbm.Tests/Support/TempDatabaseExtensions.cs`:

```csharp
namespace Dbm.Tests.Support;

public static class TempDatabaseExtensions
{
    /// <summary>COUNT_BIG(*) of a table given as it appears in SQL ("app.Parent", "[app].[Orders]").</summary>
    public static Task<long> CountAsync(this TempDatabase db, string table) => db.ScalarAsync<long>($"SELECT COUNT_BIG(*) FROM {table}");
}
```

- [ ] **Step 2: Write the failing unit tests (repo, key codec, chunk planner)**

`plugins/db-migrate/engine/Dbm.Tests/Unit/Transfer/TransferRepoTests.cs`:

```csharp
using Dbm.Core;
using Dbm.Core.State;
using Dbm.Core.Transfer;
using Xunit;

namespace Dbm.Tests.Unit.Transfer;

public sealed class TransferRepoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbm-xfer-repo-" + Guid.NewGuid().ToString("N"));
    private readonly StateDb _db;
    private readonly TransferRepo _repo;

    public TransferRepoTests()
    {
        Directory.CreateDirectory(_dir);
        _db = StateDb.Open(Path.Combine(_dir, "state.db"));
        _repo = new TransferRepo(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private long NewRun() => _repo.CreateRun(3, new TransferOptions { ChunkSize = 500, ErrorMode = "skip" },
        [("T01", "app.Customers"), ("T02", "app.Orders")]);

    [Fact]
    public void CreateRun_stores_running_run_and_pending_tasks_in_order()
    {
        long id = NewRun();
        var run = _repo.GetRun(id)!;
        Assert.Equal(3, run.SqlVersion);
        Assert.Equal(RunStatus.Running, run.Status);
        Assert.Equal(500, run.Options.ChunkSize);
        Assert.Equal("skip", run.Options.ErrorMode);
        Assert.NotNull(run.StartedAt);
        Assert.Equal("running", _db.Scalar<string>("SELECT status FROM transfer_run WHERE id = $Id", new { Id = id }));

        var tasks = _repo.Tasks(id);
        Assert.Equal(new[] { "T01", "T02" }, tasks.Select(t => t.TaskId));
        Assert.Equal(new[] { 0, 1 }, tasks.Select(t => t.Ordinal));
        Assert.All(tasks, t => Assert.Equal(TransferTaskStatus.Pending, t.Status));
        Assert.Equal("pending", _db.Scalar<string>("SELECT status FROM transfer_task WHERE run_id = $Id AND task_id = 'T01'", new { Id = id }));
    }

    [Fact]
    public void Task_status_transitions_set_timestamps_and_error()
    {
        long id = NewRun();
        _repo.UpdateTaskStatus(id, "T01", TransferTaskStatus.Running);
        var running = _repo.Task(id, "T01")!;
        Assert.NotNull(running.StartedAt);
        Assert.Null(running.EndedAt);

        _repo.UpdateTaskStatus(id, "T01", TransferTaskStatus.Failed, "boom");
        var failed = _repo.Task(id, "T01")!;
        Assert.Equal(TransferTaskStatus.Failed, failed.Status);
        Assert.Equal("boom", failed.Error);
        Assert.NotNull(failed.EndedAt);

        _repo.UpdateTaskStatus(id, "T01", TransferTaskStatus.Running);
        var again = _repo.Task(id, "T01")!;
        Assert.Null(again.Error);
        Assert.Null(again.EndedAt);
        Assert.Equal(running.StartedAt, again.StartedAt);
    }

    [Fact]
    public void Progress_counts_validation_and_run_status_round_trip()
    {
        long id = NewRun();
        _repo.SetTaskCounts(id, "T02", 3005, 0);
        _repo.UpdateTaskProgress(id, "T02", 1000, 2, "[{\"t\":\"int\",\"s\":4,\"p\":10,\"c\":0,\"v\":\"1000\"}]");
        _repo.SetTaskValidation(id, "T02", "{\"countMatch\":true}");
        var t = _repo.Task(id, "T02")!;
        Assert.Equal(3005, t.RowsSource);
        Assert.Equal(0, t.RowsBefore);
        Assert.Equal(1000, t.RowsDone);
        Assert.Equal(2, t.RowsError);
        Assert.NotNull(t.HeartbeatAt);
        Assert.Contains("\"v\":\"1000\"", t.LastKeyJson);
        Assert.Equal("{\"countMatch\":true}", t.ValidationJson);

        _repo.SetRunStatus(id, RunStatus.Paused);
        Assert.Null(_repo.GetRun(id)!.EndedAt);
        _repo.SetRunStatus(id, RunStatus.Completed, "{\"runId\":1}");
        var run = _repo.GetRun(id)!;
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.NotNull(run.EndedAt);
        Assert.Equal("{\"runId\":1}", run.SummaryJson);
        Assert.Equal(id, _repo.Latest()!.Id);
    }

    [Fact]
    public void Error_rows_filter_by_task_and_limit()
    {
        long id = NewRun();
        for (int i = 0; i < 5; i++) _repo.AddErrorRow(id, "T02", $"{{\"__k0\":{i}}}", "{\"Comment\":\"x\"}", $"err {i}");
        _repo.AddErrorRow(id, "T01", null, "{}", "other");
        Assert.Equal(3, _repo.ErrorRows(id, "T02", 3).Count);
        Assert.Equal("err 0", _repo.ErrorRows(id, "T02", 3)[0].Error);
        Assert.Equal(6, _repo.ErrorRows(id).Count);
        Assert.Equal(5, _repo.ErrorRowCount(id, "T02"));
    }

    [Fact]
    public void RecoverInterrupted_pauses_running_runs_and_tasks()
    {
        long id = NewRun();
        _repo.UpdateTaskStatus(id, "T01", TransferTaskStatus.Done);
        _repo.UpdateTaskStatus(id, "T02", TransferTaskStatus.Running);
        Assert.Equal(1, _repo.RecoverInterrupted());
        Assert.Equal(RunStatus.Paused, _repo.GetRun(id)!.Status);
        Assert.Equal(TransferTaskStatus.Done, _repo.Task(id, "T01")!.Status);
        Assert.Equal(TransferTaskStatus.Paused, _repo.Task(id, "T02")!.Status);
        Assert.Equal(0, _repo.RecoverInterrupted());
    }

    [Fact]
    public void Options_normalize_clamps_and_rejects_unknown_error_mode()
    {
        var o = new TransferOptions { ChunkSize = 0, Parallelism = 99, ErrorMode = "SKIP" }.Normalized();
        Assert.Equal(1, o.ChunkSize);
        Assert.Equal(32, o.Parallelism);
        Assert.Equal("skip", o.ErrorMode);
        Assert.True(o.SkipErrors);
        Assert.DoesNotContain("skipErrors", Json.Serialize(o));
        var ex = Assert.Throws<TransferException>(() => new TransferOptions { ErrorMode = "ignore" }.Normalized());
        Assert.Equal("bad_options", ex.Code);
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/Transfer/KeyCodecTests.cs`:

```csharp
using System.Data;
using Dbm.Core.Transfer;
using Xunit;

namespace Dbm.Tests.Unit.Transfer;

public sealed class KeyCodecTests
{
    [Fact]
    public void Encode_decode_preserves_types_and_values()
    {
        var types = new List<KeyType>
        {
            new("int", 4, 10, 0), new("smallint", 2, 5, 0), new("varchar", 20, 0, 0), new("datetime2", 8, 27, 7),
            new("decimal", 17, 19, 4), new("uniqueidentifier", 16, 0, 0), new("varbinary", 16, 0, 0),
            new("datetimeoffset", 10, 34, 7), new("bigint", 8, 19, 0), new("time", 5, 16, 7), new("bit", 1, 1, 0),
        };
        var dt = new DateTime(2020, 1, 1, 10, 0, 0, DateTimeKind.Unspecified).AddTicks(33333);
        var values = new List<object>
        {
            42, (short)7, "a-b", dt, 12.3456m, Guid.Parse("7d9f7a4c-3b8e-4a53-9a57-1b2d4c6e8f00"), new byte[] { 1, 2, 255 },
            new DateTimeOffset(2020, 1, 1, 10, 0, 0, TimeSpan.FromHours(2)), 9_000_000_000L, new TimeSpan(0, 23, 59, 59, 999), true,
        };
        string json = KeyCodec.Encode(new KeyValue(types, values));
        Assert.StartsWith("[{\"t\":\"int\"", json);

        var back = KeyCodec.Decode(json);
        Assert.Equal(types, back.Types);
        Assert.Equal(42, back.Values[0]);
        Assert.Equal((short)7, back.Values[1]);
        Assert.Equal("a-b", back.Values[2]);
        Assert.Equal(dt, back.Values[3]);
        Assert.Equal(12.3456m, back.Values[4]);
        Assert.Equal(values[5], back.Values[5]);
        Assert.Equal(new byte[] { 1, 2, 255 }, (byte[])back.Values[6]);
        Assert.Equal(values[7], back.Values[7]);
        Assert.Equal(9_000_000_000L, back.Values[8]);
        Assert.Equal(values[9], back.Values[9]);
        Assert.Equal(true, back.Values[10]);
    }

    [Fact]
    public void Parameter_uses_the_exact_sql_type()
    {
        var p = KeyCodec.Parameter("@k0", new KeyType("varchar", 20, 0, 0), "abc");
        Assert.Equal(SqlDbType.VarChar, p.SqlDbType);
        Assert.Equal(20, p.Size);
        var d = KeyCodec.Parameter("@k1", new KeyType("decimal", 17, 19, 4), 1.5m);
        Assert.Equal(SqlDbType.Decimal, d.SqlDbType);
        Assert.Equal(19, d.Precision);
        Assert.Equal(4, d.Scale);
        var m = KeyCodec.Parameter("@k2", new KeyType("nvarchar", int.MaxValue, 0, 0), "x");
        Assert.Equal(-1, m.Size);
        Assert.Throws<TransferException>(() => KeyCodec.Parameter("@k3", new KeyType("xml", 0, 0, 0), "<a/>"));
    }

    [Fact]
    public void FromRow_and_Display_use_key_aliases()
    {
        var table = new DataTable();
        table.Columns.Add("Name", typeof(string));
        table.Columns.Add("__k0", typeof(int));
        table.Columns.Add("__k1", typeof(short));
        table.Rows.Add("x", 5, (short)2);
        var types = new List<KeyType> { new("int", 4, 10, 0), new("smallint", 2, 5, 0) };
        var key = KeyCodec.FromRow(table.Rows[0], ["__k0", "__k1"], types);
        Assert.Equal(5, key.Values[0]);
        Assert.Equal((short)2, key.Values[1]);
        Assert.Equal("{\"__k0\":5,\"__k1\":2}", KeyCodec.Display(key, ["__k0", "__k1"]));
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/Transfer/ChunkPlannerTests.cs`:

```csharp
using Dbm.Core.SqlGen;
using Dbm.Core.Transfer;
using Xunit;

namespace Dbm.Tests.Unit.Transfer;

public sealed class ChunkPlannerTests
{
    private static TaskPlan Task(params string[] keys) => new()
    {
        Target = "app.OrderLines",
        SourceQuery = "SELECT s.[QTY] AS [Quantity], s.[ORD_ID] AS [__k0], s.[LINE_NO] AS [__k1] FROM [dbo].[ORD_LINE] AS s;  ",
        KeyColumns = keys.ToList(),
    };

    [Fact]
    public void Single_key_predicate()
        => Assert.Equal("(q.[__k0] > @k0)", ChunkPlanner.Predicate(["__k0"]));

    [Fact]
    public void Composite_key_predicate_has_seekable_prefix_and_or_chain()
        => Assert.Equal(
            "q.[__k0] >= @k0 AND ((q.[__k0] > @k0) OR (q.[__k0] = @k0 AND q.[__k1] > @k1) OR (q.[__k0] = @k0 AND q.[__k1] = @k1 AND q.[__k2] > @k2))",
            ChunkPlanner.Predicate(["__k0", "__k1", "__k2"]));

    [Fact]
    public void First_chunk_has_no_where_and_orders_by_all_keys()
    {
        string sql = ChunkPlanner.Sql(Task("__k0", "__k1"), afterKey: false);
        Assert.Equal(
            "SELECT TOP (@__n) q.* FROM (\nSELECT s.[QTY] AS [Quantity], s.[ORD_ID] AS [__k0], s.[LINE_NO] AS [__k1] FROM [dbo].[ORD_LINE] AS s\n) AS q ORDER BY q.[__k0], q.[__k1]",
            sql);
    }

    [Fact]
    public void Later_chunks_add_the_keyset_predicate()
    {
        string sql = ChunkPlanner.Sql(Task("__k0", "__k1"), afterKey: true);
        Assert.EndsWith(") AS q WHERE q.[__k0] >= @k0 AND ((q.[__k0] > @k0) OR (q.[__k0] = @k0 AND q.[__k1] > @k1)) ORDER BY q.[__k0], q.[__k1]", sql);
    }

    [Fact]
    public void Keyless_task_is_rejected()
        => Assert.Throws<TransferException>(() => ChunkPlanner.Sql(Task(), afterKey: false));
}
```

`plugins/db-migrate/engine/Dbm.Tests/Integration/Transfer/ControlTableTests.cs` (control table + keyset paging against a real server — regression tests for V5–V7):

```csharp
using Dbm.Core.SqlGen;
using Dbm.Core.Transfer;
using Dbm.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Dbm.Tests.Integration.Transfer;

[Trait("Category", "Integration")]
public sealed class ControlTableTests
{
    [Fact]
    public async Task Checkpoint_is_written_only_when_the_transaction_commits()
    {
        await using var db = await TempDatabase.CreateAsync("dbm_ctl");
        await using var conn = new SqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        await ControlTable.EnsureAsync(conn, default);
        await ControlTable.EnsureAsync(conn, default);            // idempotent
        Assert.True(await ControlTable.ExistsAsync(conn, default));
        Assert.Null(await ControlTable.ReadAsync(conn, 1, "T01", default));

        await using (var tx = (SqlTransaction)await conn.BeginTransactionAsync())
        {
            await ControlTable.UpsertAsync(conn, tx, 1, "T01", new Checkpoint(1, "[{\"t\":\"int\",\"s\":4,\"p\":10,\"c\":0,\"v\":\"500\"}]", 500, 0, false), default);
            await tx.RollbackAsync();
        }
        Assert.Null(await ControlTable.ReadAsync(conn, 1, "T01", default));

        await using (var tx = (SqlTransaction)await conn.BeginTransactionAsync())
        {
            await ControlTable.UpsertAsync(conn, tx, 1, "T01", new Checkpoint(1, "[]", 500, 2, false), default);
            await tx.CommitAsync();
        }
        await ControlTable.UpsertAsync(conn, null, 1, "T01", new Checkpoint(2, null, 900, 3, true), default);
        var cp = await ControlTable.ReadAsync(conn, 1, "T01", default);
        Assert.Equal(new Checkpoint(2, null, 900, 3, true), cp);
        Assert.Null(await ControlTable.ReadAsync(conn, 2, "T01", default));

        await ControlTable.DropAsync(conn, default);
        await ControlTable.DropAsync(conn, default);              // idempotent
        Assert.False(await ControlTable.ExistsAsync(conn, default));
    }

    [Fact]
    public async Task Keyset_paging_visits_every_row_once_in_key_order_for_varchar_and_composite_keys()
    {
        await using var db = await TempDatabase.CreateAsync("dbm_page");
        await db.ExecAsync("""
            CREATE TABLE dbo.K (k1 varchar(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, k2 smallint NOT NULL, v int NOT NULL,
              CONSTRAINT PK_K PRIMARY KEY (k1, k2));
            INSERT dbo.K VALUES ('a',1,1),('a',2,2),('B',1,3),('a-b',1,4),('ab',1,5),('ab',2,6),('Z',1,7),('_x',1,8),('c',1,9);
            """);
        var task = new TaskPlan
        {
            Target = "dbo.T",
            SourceQuery = "SELECT k.v AS [V], (SELECT COUNT(*) FROM dbo.K AS x WHERE x.k1 = k.k1) AS [Siblings], k.k1 AS [__k0], k.k2 AS [__k1] FROM dbo.K AS k",
            KeyColumns = ["__k0", "__k1"],
        };
        await using var conn = new SqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var seen = new List<int>();
        KeyValue? last = null;
        IReadOnlyList<KeyType>? types = null;
        for (int guard = 0; guard < 20; guard++)
        {
            await using var cmd = ChunkPlanner.Command(conn, task, last, 2);
            await using var r = await cmd.ExecuteReaderAsync();
            types ??= KeyCodec.TypesOf(r, task.KeyColumns);
            int n = 0;
            object[]? lastKey = null;
            while (await r.ReadAsync())
            {
                seen.Add(r.GetInt32(0));
                lastKey = [r.GetValue(2), r.GetValue(3)];
                n++;
            }
            if (n == 0) break;
            last = KeyCodec.Decode(KeyCodec.Encode(new KeyValue(types, lastKey!)));   // simulates resume from the stored JSON
            if (n < 2) break;
        }

        string expected = await db.ScalarAsync<string>("SELECT STRING_AGG(CAST(v AS varchar(10)), ',') WITHIN GROUP (ORDER BY k1, k2) FROM dbo.K");
        Assert.Equal(expected, string.Join(",", seen));
        Assert.Equal("varchar", types![0].Name);
        Assert.Equal(20, types[0].Size);
        Assert.Equal("smallint", types[1].Name);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~Dbm.Tests.Unit.Transfer|FullyQualifiedName~Dbm.Tests.Integration.Transfer"`
Expected: build FAILS with `CS0246: The type or namespace name 'TransferRepo' could not be found` (and `KeyCodec`, `ChunkPlanner`, `ControlTable`, `TransferException`, …).

- [ ] **Step 4: Implement options, exception and repo**

`plugins/db-migrate/engine/Dbm/Core/Transfer/TransferOptions.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Dbm.Core.Transfer;

public sealed record TransferOptions
{
    public int ChunkSize { get; init; } = 100_000;
    public int Parallelism { get; init; } = 4;
    public string ErrorMode { get; init; } = "stop";     // "stop" | "skip"
    public bool TruncateTarget { get; init; }
    public bool TableLock { get; init; }
    public bool ValidateChecksums { get; init; } = true;
    public bool FireTriggers { get; init; }
    public bool KeepControlTable { get; init; }

    [JsonIgnore] public bool SkipErrors => ErrorMode == "skip";

    /// <summary>Clamps sizes and canonicalises ErrorMode; unknown ErrorMode -> TransferException("bad_options").</summary>
    public TransferOptions Normalized()
    {
        string mode = (ErrorMode ?? "").Trim().ToLowerInvariant();
        if (mode is not ("stop" or "skip"))
            throw new TransferException("bad_options", $"errorMode must be \"stop\" or \"skip\" (got \"{ErrorMode}\").");
        return this with
        {
            ChunkSize = Math.Clamp(ChunkSize, 1, 10_000_000),
            Parallelism = Math.Clamp(Parallelism, 1, 32),
            ErrorMode = mode,
        };
    }
}

public enum RunStatus { Pending, Running, Paused, Completed, Failed, Cancelled }

public enum TransferTaskStatus { Pending, Running, Paused, Done, Failed }
```

`plugins/db-migrate/engine/Dbm/Core/Transfer/TransferException.cs`:

```csharp
namespace Dbm.Core.Transfer;

/// <summary>Expected transfer failure with a stable machine code (API: 409 {error: Code, message, details}).</summary>
public sealed class TransferException(string code, string message, IReadOnlyList<string>? details = null) : Exception(message)
{
    public string Code { get; } = code;
    public IReadOnlyList<string> Details { get; } = details ?? [];
}
```

`plugins/db-migrate/engine/Dbm/Core/State/TransferRepo.cs`:

```csharp
using System.Globalization;
using Dbm.Core.Transfer;
using Microsoft.Data.Sqlite;

namespace Dbm.Core.State;

public sealed record TransferRunRow(long Id, int SqlVersion, RunStatus Status, TransferOptions Options,
    DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, string? SummaryJson);

public sealed record TransferTaskRow(long RunId, string TaskId, string Target, int Ordinal, TransferTaskStatus Status,
    long? RowsSource, long? RowsBefore, long RowsDone, long RowsError, string? LastKeyJson, DateTimeOffset? HeartbeatAt,
    DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, string? Error, string? ValidationJson);

public sealed record ErrorRowEntry(long Id, long RunId, string TaskId, string? KeyJson, string? RowJson, string Error, DateTimeOffset Ts);

/// <summary>SQLite mirror of transfer runs (the target control table is the source of truth for checkpoints).</summary>
public sealed class TransferRepo(StateDb db)
{
    private const string RunCols = "id, sql_version, status, options_json, started_at, ended_at, summary_json";
    private const string TaskCols = "run_id, task_id, target, ordinal, status, rows_source, rows_before, rows_done, rows_error, " +
                                    "last_key_json, heartbeat_at, started_at, ended_at, error, validation_json";

    public long CreateRun(int sqlVersion, TransferOptions options, IReadOnlyList<(string TaskId, string Target)> tasks)
        => db.InTransaction(() =>
        {
            db.Execute("INSERT INTO transfer_run (sql_version, status, options_json, started_at) VALUES ($SqlVersion, $Status, $Options, $Now)",
                new { SqlVersion = sqlVersion, Status = EnumText.ToText(RunStatus.Running), Options = Json.Serialize(options), Now = Clock.NowText() });
            long id = db.Scalar<long>("SELECT last_insert_rowid()");
            for (int i = 0; i < tasks.Count; i++)
                db.Execute("INSERT INTO transfer_task (run_id, task_id, target, ordinal, status) VALUES ($RunId, $TaskId, $Target, $Ordinal, $Status)",
                    new { RunId = id, TaskId = tasks[i].TaskId, Target = tasks[i].Target, Ordinal = i, Status = EnumText.ToText(TransferTaskStatus.Pending) });
            return id;
        });

    public TransferRunRow? Latest()
        => db.Query($"SELECT {RunCols} FROM transfer_run ORDER BY id DESC LIMIT 1", MapRun).FirstOrDefault();

    public TransferRunRow? GetRun(long runId)
        => db.Query($"SELECT {RunCols} FROM transfer_run WHERE id = $Id", MapRun, new { Id = runId }).FirstOrDefault();

    public void SetRunStatus(long runId, RunStatus status, string? summaryJson = null)
    {
        bool terminal = status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled;
        db.Execute("UPDATE transfer_run SET status = $Status, ended_at = $EndedAt, " +
                   "summary_json = CASE WHEN $Status = 'running' THEN NULL ELSE COALESCE($Summary, summary_json) END WHERE id = $Id",
            new { Status = EnumText.ToText(status), EndedAt = terminal ? Clock.NowText() : null, Summary = summaryJson, Id = runId });
    }

    public IReadOnlyList<TransferTaskRow> Tasks(long runId)
        => db.Query($"SELECT {TaskCols} FROM transfer_task WHERE run_id = $RunId ORDER BY ordinal", MapTask, new { RunId = runId });

    public TransferTaskRow? Task(long runId, string taskId)
        => db.Query($"SELECT {TaskCols} FROM transfer_task WHERE run_id = $RunId AND task_id = $TaskId", MapTask,
            new { RunId = runId, TaskId = taskId }).FirstOrDefault();

    public void SetTaskCounts(long runId, string taskId, long? rowsSource, long? rowsBefore)
        => db.Execute("UPDATE transfer_task SET rows_source = $RowsSource, rows_before = $RowsBefore WHERE run_id = $RunId AND task_id = $TaskId",
            new { RowsSource = rowsSource, RowsBefore = rowsBefore, RunId = runId, TaskId = taskId });

    public void UpdateTaskProgress(long runId, string taskId, long rowsDone, long rowsError, string? lastKeyJson)
        => db.Execute("UPDATE transfer_task SET rows_done = $RowsDone, rows_error = $RowsError, last_key_json = $LastKey, heartbeat_at = $Now " +
                      "WHERE run_id = $RunId AND task_id = $TaskId",
            new { RowsDone = rowsDone, RowsError = rowsError, LastKey = lastKeyJson, Now = Clock.NowText(), RunId = runId, TaskId = taskId });

    public void UpdateTaskStatus(long runId, string taskId, TransferTaskStatus status, string? error = null)
        => db.Execute(
            "UPDATE transfer_task SET status = $Status, heartbeat_at = $Now, " +
            "started_at = CASE WHEN $Status = 'running' AND started_at IS NULL THEN $Now ELSE started_at END, " +
            "ended_at = CASE WHEN $Status IN ('done', 'failed') THEN $Now WHEN $Status = 'running' THEN NULL ELSE ended_at END, " +
            "error = CASE WHEN $Status = 'failed' THEN $Error WHEN $Status = 'running' THEN NULL ELSE error END " +
            "WHERE run_id = $RunId AND task_id = $TaskId",
            new { Status = EnumText.ToText(status), Now = Clock.NowText(), Error = error, RunId = runId, TaskId = taskId });

    public void SetTaskValidation(long runId, string taskId, string validationJson)
        => db.Execute("UPDATE transfer_task SET validation_json = $Json WHERE run_id = $RunId AND task_id = $TaskId",
            new { Json = validationJson, RunId = runId, TaskId = taskId });

    public void AddErrorRow(long runId, string taskId, string? keyJson, string? rowJson, string error)
        => db.Execute("INSERT INTO error_row (run_id, task_id, key_json, row_json, error, ts) VALUES ($RunId, $TaskId, $KeyJson, $RowJson, $Error, $Ts)",
            new { RunId = runId, TaskId = taskId, KeyJson = keyJson, RowJson = rowJson, Error = error, Ts = Clock.NowText() });

    public IReadOnlyList<ErrorRowEntry> ErrorRows(long runId, string? taskId = null, int limit = 100)
        => db.Query("SELECT id, run_id, task_id, key_json, row_json, error, ts FROM error_row " +
                    "WHERE run_id = $RunId AND ($TaskId IS NULL OR task_id = $TaskId) ORDER BY id LIMIT $Limit",
            r => new ErrorRowEntry(r.GetInt64(0), r.GetInt64(1), r.GetString(2), Str(r, 3), Str(r, 4), r.GetString(5), Ts(r, 6)!.Value),
            new { RunId = runId, TaskId = taskId, Limit = Math.Clamp(limit, 1, 10_000) });

    public long ErrorRowCount(long runId, string taskId)
        => db.Scalar<long>("SELECT COUNT(*) FROM error_row WHERE run_id = $RunId AND task_id = $TaskId", new { RunId = runId, TaskId = taskId });

    /// <summary>Server start: a run left 'running' by a dead process becomes 'paused' (its running tasks too).</summary>
    public int RecoverInterrupted() => db.InTransaction(() =>
    {
        db.Execute("UPDATE transfer_task SET status = 'paused' WHERE status = 'running' AND run_id IN (SELECT id FROM transfer_run WHERE status = 'running')");
        return db.Execute("UPDATE transfer_run SET status = 'paused' WHERE status = 'running'");
    });

    private static TransferRunRow MapRun(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt32(1), EnumText.Parse<RunStatus>(r.GetString(2)), Json.Deserialize<TransferOptions>(r.GetString(3)),
        Ts(r, 4), Ts(r, 5), Str(r, 6));

    private static TransferTaskRow MapTask(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), EnumText.Parse<TransferTaskStatus>(r.GetString(4)),
        Long(r, 5), Long(r, 6), r.GetInt64(7), r.GetInt64(8), Str(r, 9), Ts(r, 10), Ts(r, 11), Ts(r, 12), Str(r, 13), Str(r, 14));

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static long? Long(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    private static DateTimeOffset? Ts(SqliteDataReader r, int i)
        => r.IsDBNull(i) ? null : DateTimeOffset.Parse(r.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
```

Modify `plugins/db-migrate/engine/Dbm/Core/DbmServices.cs` — directly below the existing line `    public CatalogRepo Catalog => _catalog ??= new CatalogRepo(Db);` add (same lazy pattern as the catalog repo; `TransferRepo` is stateless):

```csharp
    // T5.1: transfer runs, tasks and rejected rows (a stateless wrapper over Db, created on first use)
    private TransferRepo? _transfers;
    public TransferRepo Transfers => _transfers ??= new TransferRepo(Db);
```

- [ ] **Step 5: Implement the key codec, chunk planner and control table**

`plugins/db-migrate/engine/Dbm/Core/Transfer/KeyCodec.cs`:

```csharp
using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

/// <summary>SQL type of one key column as reported by the source reader (V7: parameters must use this exact type).</summary>
public sealed record KeyType(string Name, int Size, byte Precision, byte Scale);

public sealed record KeyValue(IReadOnlyList<KeyType> Types, IReadOnlyList<object> Values);

/// <summary>Type-preserving last-key serialisation: [{"t":"varchar","s":20,"p":0,"c":0,"v":"abc"}, …] (values as invariant text).</summary>
public static class KeyCodec
{
    public static IReadOnlyList<KeyType> TypesOf(SqlDataReader reader, IReadOnlyList<string> keyColumns)
    {
        var schema = reader.GetColumnSchema();
        var result = new List<KeyType>(keyColumns.Count);
        foreach (var key in keyColumns)
        {
            var col = schema.FirstOrDefault(c => string.Equals(c.ColumnName, key, StringComparison.OrdinalIgnoreCase))
                ?? throw new TransferException("key_missing", $"Key column {key} is not in the source query result.");
            string name = (col.DataTypeName ?? "").ToLowerInvariant();
            DbTypeOf(name);   // validates support early
            result.Add(new KeyType(name, col.ColumnSize ?? 0,
                (byte)Math.Clamp(col.NumericPrecision ?? 0, 0, 255), (byte)Math.Clamp(col.NumericScale ?? 0, 0, 255)));
        }
        return result;
    }

    public static KeyValue FromRow(DataRow row, IReadOnlyList<string> keyColumns, IReadOnlyList<KeyType> types)
    {
        var values = new List<object>(keyColumns.Count);
        foreach (var key in keyColumns)
        {
            object v = row[key];
            if (v is DBNull) throw new TransferException("key_null", $"Key column {key} is NULL; keyset chunking needs NOT NULL keys.");
            values.Add(v);
        }
        return new KeyValue(types, values);
    }

    public static string Encode(KeyValue key)
    {
        var arr = new JsonArray();
        for (int i = 0; i < key.Types.Count; i++)
        {
            var t = key.Types[i];
            arr.Add(new JsonObject { ["t"] = t.Name, ["s"] = t.Size, ["p"] = t.Precision, ["c"] = t.Scale, ["v"] = ToText(key.Values[i]) });
        }
        return arr.ToJsonString();
    }

    public static KeyValue Decode(string json)
    {
        var arr = JsonNode.Parse(json) as JsonArray ?? throw new TransferException("bad_key", "Checkpoint key is not a JSON array.");
        var types = new List<KeyType>();
        var values = new List<object>();
        foreach (var node in arr)
        {
            var o = node!.AsObject();
            var t = new KeyType(o["t"]!.GetValue<string>(), o["s"]!.GetValue<int>(), o["p"]!.GetValue<byte>(), o["c"]!.GetValue<byte>());
            types.Add(t);
            values.Add(FromText(t.Name, o["v"]!.GetValue<string>()));
        }
        return new KeyValue(types, values);
    }

    public static SqlParameter Parameter(string name, KeyType type, object value)
    {
        var p = new SqlParameter(name, DbTypeOf(type.Name)) { Value = value };
        switch (type.Name)
        {
            case "char" or "varchar" or "nchar" or "nvarchar" or "binary" or "varbinary":
                p.Size = type.Size is <= 0 or > 8000 ? -1 : type.Size;
                break;
            case "decimal" or "numeric":
                p.Precision = type.Precision;
                p.Scale = type.Scale;
                break;
            case "datetime2" or "datetimeoffset" or "time":
                p.Scale = type.Scale > 7 ? (byte)7 : type.Scale;
                break;
        }
        return p;
    }

    /// <summary>Human-readable key for error rows and the UI: {"__k0":42,"__k1":"abc"}.</summary>
    public static string Display(KeyValue key, IReadOnlyList<string> keyColumns)
    {
        var o = new JsonObject();
        for (int i = 0; i < keyColumns.Count; i++)
        {
            object v = key.Values[i];
            JsonNode? node = v switch
            {
                int n => n, short n => n, long n => n, byte n => n, decimal n => n, bool n => n, double n => n, float n => n,
                _ => ToText(v),
            };
            o[keyColumns[i]] = node;
        }
        return o.ToJsonString();
    }

    private static SqlDbType DbTypeOf(string type) => type switch
    {
        "bigint" => SqlDbType.BigInt, "int" => SqlDbType.Int, "smallint" => SqlDbType.SmallInt, "tinyint" => SqlDbType.TinyInt,
        "bit" => SqlDbType.Bit, "decimal" or "numeric" => SqlDbType.Decimal, "money" => SqlDbType.Money, "smallmoney" => SqlDbType.SmallMoney,
        "float" => SqlDbType.Float, "real" => SqlDbType.Real, "date" => SqlDbType.Date, "datetime" => SqlDbType.DateTime,
        "datetime2" => SqlDbType.DateTime2, "smalldatetime" => SqlDbType.SmallDateTime, "datetimeoffset" => SqlDbType.DateTimeOffset,
        "time" => SqlDbType.Time, "char" => SqlDbType.Char, "varchar" => SqlDbType.VarChar, "nchar" => SqlDbType.NChar,
        "nvarchar" => SqlDbType.NVarChar, "uniqueidentifier" => SqlDbType.UniqueIdentifier, "binary" => SqlDbType.Binary,
        "varbinary" => SqlDbType.VarBinary,
        _ => throw Unsupported(type),
    };

    private static string ToText(object v) => v switch
    {
        string s => s,
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset o => o.ToString("O", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToBase64String(bytes),
        Guid g => g.ToString("D"),
        bool flag => flag ? "true" : "false",
        double dbl => dbl.ToString("R", CultureInfo.InvariantCulture),
        float flt => flt.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    private static object FromText(string type, string s) => type switch
    {
        "bigint" => long.Parse(s, CultureInfo.InvariantCulture),
        "int" => int.Parse(s, CultureInfo.InvariantCulture),
        "smallint" => short.Parse(s, CultureInfo.InvariantCulture),
        "tinyint" => byte.Parse(s, CultureInfo.InvariantCulture),
        "bit" => s == "true",
        "decimal" or "numeric" or "money" or "smallmoney" => decimal.Parse(s, NumberStyles.Number, CultureInfo.InvariantCulture),
        "float" => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture),
        "real" => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture),
        "date" or "datetime" or "datetime2" or "smalldatetime" => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        "datetimeoffset" => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        "time" => TimeSpan.ParseExact(s, "c", CultureInfo.InvariantCulture),
        "char" or "varchar" or "nchar" or "nvarchar" => s,
        "uniqueidentifier" => Guid.Parse(s),
        "binary" or "varbinary" => Convert.FromBase64String(s),
        _ => throw Unsupported(type),
    };

    private static TransferException Unsupported(string type)
        => new("key_type", $"Key column type '{type}' is not supported for keyset chunking.");
}
```

`plugins/db-migrate/engine/Dbm/Core/Transfer/ChunkPlanner.cs`:

```csharp
using System.Data;
using Dbm.Core.Sql;
using Dbm.Core.SqlGen;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

/// <summary>Keyset chunk queries over the task's SourceQuery used as a derived table (V6).</summary>
public static class ChunkPlanner
{
    /// <summary>(k0 > @k0) for one key; for n keys a seekable prefix plus the lexicographic OR chain.</summary>
    public static string Predicate(IReadOnlyList<string> keyColumns)
    {
        if (keyColumns.Count == 0) throw new TransferException("no_key", "Keyset chunking needs at least one key column.");
        string K(int i) => "q." + SqlQuote.Ident(keyColumns[i]);
        var terms = new List<string>();
        for (int i = 0; i < keyColumns.Count; i++)
        {
            var parts = new List<string>();
            for (int j = 0; j < i; j++) parts.Add($"{K(j)} = @k{j}");
            parts.Add($"{K(i)} > @k{i}");
            terms.Add("(" + string.Join(" AND ", parts) + ")");
        }
        return keyColumns.Count == 1 ? terms[0] : $"{K(0)} >= @k0 AND ({string.Join(" OR ", terms)})";
    }

    public static string Sql(TaskPlan task, bool afterKey)
    {
        if (task.KeyColumns.Count == 0) throw new TransferException("no_key", $"Task for {task.Target} has no key columns.");
        string source = task.SourceQuery.Trim().TrimEnd(';').TrimEnd();
        string order = string.Join(", ", task.KeyColumns.Select(k => "q." + SqlQuote.Ident(k)));
        string where = afterKey ? " WHERE " + Predicate(task.KeyColumns) : "";
        return $"SELECT TOP (@__n) q.* FROM (\n{source}\n) AS q{where} ORDER BY {order}";
    }

    public static SqlCommand Command(SqlConnection conn, TaskPlan task, KeyValue? lastKey, int chunkSize)
    {
        var cmd = new SqlCommand(Sql(task, lastKey is not null), conn) { CommandTimeout = 0 };
        cmd.Parameters.Add(new SqlParameter("@__n", SqlDbType.BigInt) { Value = (long)chunkSize });
        if (lastKey is not null)
            for (int i = 0; i < task.KeyColumns.Count; i++)
                cmd.Parameters.Add(KeyCodec.Parameter($"@k{i}", lastKey.Types[i], lastKey.Values[i]));
        return cmd;
    }
}
```

`plugins/db-migrate/engine/Dbm/Core/Transfer/ControlTable.cs`:

```csharp
using System.Data;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

public sealed record Checkpoint(int ChunkNo, string? LastKeyJson, long RowsDone, long RowsError, bool Done)
{
    public static readonly Checkpoint Start = new(0, null, 0, 0, false);
}

/// <summary>dbo.__dbm_checkpoint in the TARGET: written in the same transaction as each chunk (exactly-once).</summary>
public static class ControlTable
{
    public const string Name = "[dbo].[__dbm_checkpoint]";

    public static Task EnsureAsync(SqlConnection conn, CancellationToken ct) => ExecAsync(conn, null, """
        IF OBJECT_ID(N'dbo.__dbm_checkpoint', N'U') IS NULL
        CREATE TABLE dbo.__dbm_checkpoint (
          run_id bigint NOT NULL, task_id nvarchar(64) NOT NULL, chunk_no int NOT NULL, last_key nvarchar(max) NULL,
          rows_done bigint NOT NULL, rows_error bigint NOT NULL, done bit NOT NULL, updated_at datetime2(3) NOT NULL,
          CONSTRAINT PK___dbm_checkpoint PRIMARY KEY (run_id, task_id));
        """, ct);

    public static async Task<bool> ExistsAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("SELECT CASE WHEN OBJECT_ID(N'dbo.__dbm_checkpoint', N'U') IS NULL THEN 0 ELSE 1 END", conn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 1;
    }

    public static async Task<Checkpoint?> ReadAsync(SqlConnection conn, long runId, string taskId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT chunk_no, last_key, rows_done, rows_error, done FROM dbo.__dbm_checkpoint WHERE run_id = @r AND task_id = @t", conn);
        AddKey(cmd, runId, taskId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new Checkpoint(r.GetInt32(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetBoolean(4));
    }

    public static async Task UpsertAsync(SqlConnection conn, SqlTransaction? tx, long runId, string taskId, Checkpoint cp, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("""
            UPDATE dbo.__dbm_checkpoint SET chunk_no = @c, last_key = @k, rows_done = @d, rows_error = @e, done = @f, updated_at = SYSUTCDATETIME()
            WHERE run_id = @r AND task_id = @t;
            IF @@ROWCOUNT = 0
              INSERT dbo.__dbm_checkpoint (run_id, task_id, chunk_no, last_key, rows_done, rows_error, done, updated_at)
              VALUES (@r, @t, @c, @k, @d, @e, @f, SYSUTCDATETIME());
            """, conn, tx);
        AddKey(cmd, runId, taskId);
        cmd.Parameters.Add(new SqlParameter("@c", SqlDbType.Int) { Value = cp.ChunkNo });
        cmd.Parameters.Add(new SqlParameter("@k", SqlDbType.NVarChar, -1) { Value = (object?)cp.LastKeyJson ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@d", SqlDbType.BigInt) { Value = cp.RowsDone });
        cmd.Parameters.Add(new SqlParameter("@e", SqlDbType.BigInt) { Value = cp.RowsError });
        cmd.Parameters.Add(new SqlParameter("@f", SqlDbType.Bit) { Value = cp.Done });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static Task DropAsync(SqlConnection conn, CancellationToken ct)
        => ExecAsync(conn, null, "IF OBJECT_ID(N'dbo.__dbm_checkpoint', N'U') IS NOT NULL DROP TABLE dbo.__dbm_checkpoint;", ct);

    private static void AddKey(SqlCommand cmd, long runId, string taskId)
    {
        cmd.Parameters.Add(new SqlParameter("@r", SqlDbType.BigInt) { Value = runId });
        cmd.Parameters.Add(new SqlParameter("@t", SqlDbType.NVarChar, 64) { Value = taskId });
    }

    private static async Task ExecAsync(SqlConnection conn, SqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~Dbm.Tests.Unit.Transfer|FullyQualifiedName~Dbm.Tests.Integration.Transfer"`
Expected: PASS — 16 tests (6 TransferRepoTests, 3 KeyCodecTests, 5 ChunkPlannerTests, 2 ControlTableTests). Then run the whole unit suite to prove nothing else broke: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"` → PASS.

- [ ] **Step 7: Commit**

```bash
git add plugins/db-migrate/engine/Dbm/Core/Transfer plugins/db-migrate/engine/Dbm/Core/State/TransferRepo.cs plugins/db-migrate/engine/Dbm/Core/DbmServices.cs plugins/db-migrate/engine/Dbm.Tests/Support/TempDatabaseExtensions.cs plugins/db-migrate/engine/Dbm.Tests/Unit/Transfer plugins/db-migrate/engine/Dbm.Tests/Integration/Transfer
git commit -F - <<'EOF'
feat(transfer): transfer repo, options, control table and keyset chunk planner

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49
EOF
```

---

### Task 5.2: Bulk loader + bisection

**Files:**
- Create: `plugins/db-migrate/engine/Dbm/Core/Transfer/Bisector.cs`
- Create: `plugins/db-migrate/engine/Dbm/Core/Transfer/TxScope.cs`
- Create: `plugins/db-migrate/engine/Dbm/Core/Transfer/TargetShape.cs`
- Create: `plugins/db-migrate/engine/Dbm/Core/Transfer/ChunkReader.cs`
- Create: `plugins/db-migrate/engine/Dbm/Core/Transfer/RowSnapshot.cs`
- Create: `plugins/db-migrate/engine/Dbm/Core/Transfer/BulkLoader.cs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/Transfer/BisectorTests.cs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/Transfer/TargetShapeTests.cs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Integration/Transfer/BulkLoaderTests.cs`

**Interfaces:**
- Consumes: `TaskPlan`, `ColumnBinding` (C13), `TransferOptions` (C14), `SqlQuote.TableKey` (C3), `TransferException` (T5.1).
- Produces:
```csharp
namespace Dbm.Core.Transfer;
public sealed record LoadAttempt(bool Ok, string? Error = null, bool Doomed = false) { public static readonly LoadAttempt Success; }
public sealed record RowFailure(int Row, string Error);
public sealed record BisectResult(IReadOnlyList<int> Loaded, IReadOnlyList<RowFailure> Failed);
public interface IBisectTarget
{
    Task<LoadAttempt> TryLoadAsync(IReadOnlyList<int> rows, CancellationToken ct);   // failure: attempt undone (savepoint) or Doomed = true
    Task RestartAsync(CancellationToken ct);                                         // discard the doomed transaction, begin a new one
}
public static class Bisector { public static Task<BisectResult> RunAsync(int rowCount, IBisectTarget target, bool stopAtFirstFailure, CancellationToken ct); }
public sealed class TxScope : IAsyncDisposable
{
    public SqlConnection Connection { get; }  public SqlTransaction Tx { get; }  public int Restarts { get; }
    public static Task<TxScope> BeginAsync(SqlConnection conn, CancellationToken ct);
    public Task<int> XactStateAsync(CancellationToken ct);   // probe failure -> 0
    public Task RestartAsync(CancellationToken ct);
    public Task CommitAsync(CancellationToken ct);
    public Task RollbackAsync();                              // never throws
}
public sealed record TargetColumn(string Name, string DataType, int MaxLength, byte Precision, byte Scale, bool IsIdentity, bool IsComputed) { public string TypeText { get; } }
public sealed class TargetShape(string target, IReadOnlyList<TargetColumn> columns)
{
    public string Target { get; }  public IReadOnlyList<TargetColumn> Columns { get; }
    public TargetColumn? Find(string name);
    public static Task<TargetShape> LoadAsync(SqlConnection conn, string targetKey, CancellationToken ct);
    public void Normalize(DataTable table, IReadOnlyList<ColumnBinding> bindings);     // V8 temporal rounding
    public static string TypeText(string dataType, int maxLength, byte precision, byte scale);
    public static long RoundTicks(long ticks, int scale, long maxTicks);
    public static object RoundValue(object value, string targetType, int scale);
}
public static class ChunkReader
{
    public static DataTable NewTable(SqlDataReader reader);
    public static Task<int> FillAsync(SqlDataReader reader, DataTable table, int max, CancellationToken ct);
    public static DateTime ExactDateTime(SqlDateTime value);
}
public static class RowSnapshot
{
    public const int MaxValueLength = 200;
    public static string Json(DataRow row, IReadOnlyList<ColumnBinding> bindings);
    public static string Text(object value);
}
public sealed record ChunkOutcome(long Loaded, IReadOnlyList<RowFailure> Failed, int Restarts);
public sealed class BulkLoader(TaskPlan task, TransferOptions options)
{
    public const string SavepointName = "dbm_b";
    public Task<ChunkOutcome> LoadAsync(TxScope scope, DataTable rows, bool allowRestart, Action<long>? progress, CancellationToken ct);
}
```

**Algorithm (normative):** `Bisector` keeps a LIFO stack of row segments, starting with all rows. Each segment is tried with `TryLoadAsync`. Success → the rows join `Loaded`. Failure → if `Doomed` (V3) the target restarts the transaction and all rows in `Loaded` are reloaded as one batch (a failure there throws `TransferException("bisect_reload")`); then a single-row segment is recorded as failed (stopping immediately when `stopAtFirstFailure`), a larger one is split into halves (first half processed first). The bulk-copy target takes savepoint `dbm_b` before every attempt; on `SqlException`/`InvalidOperationException` it probes `XACT_STATE()`: `1` → `Rollback("dbm_b")`, not doomed; anything else → doomed. Keyless tasks pass `allowRestart: false`, so a doomed failure throws `TransferException("tx_ended")`. Direct mode copies into `Target` with explicit `ColumnMappings` from `Columns` (key aliases are never mapped) and options `CheckConstraints | KeepNulls | (IdentityInsert ? KeepIdentity) | (TableLock ? TableLock) | (FireTriggers ? FireTriggers)`, `BulkCopyTimeout = 0`; staging mode drops/creates `#stg` from `StagingDdl`, copies with `KeepNulls` (bindings `Source→Target` when `#stg` has the target name, otherwise same-name columns), runs `MergeSql`, drops `#stg` — all inside the chunk transaction.

- [ ] **Step 1: Write the failing unit tests**

`plugins/db-migrate/engine/Dbm.Tests/Unit/Transfer/BisectorTests.cs`:

```csharp
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

    [Fact]
    public async Task Failed_reload_after_restart_throws()
    {
        var t = new FakeTarget(new HashSet<int>(), dooming: new HashSet<int> { 6 }) { FailReload = true };
        var ex = await Assert.ThrowsAsync<TransferException>(() => Bisector.RunAsync(8, t, false, default));
        Assert.Equal("bisect_reload", ex.Code);
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/Transfer/TargetShapeTests.cs`:

```csharp
using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using Dbm.Core.SqlGen;
using Dbm.Core.Transfer;
using Xunit;

namespace Dbm.Tests.Unit.Transfer;

public sealed class TargetShapeTests
{
    private static DateTime At(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    [Theory]
    [InlineData("2020-01-01T10:00:00.9970000", 0, "2020-01-01T10:00:01.0000000")]   // V8: CAST rounds up
    [InlineData("2020-01-01T10:00:00.4970000", 0, "2020-01-01T10:00:00.0000000")]
    [InlineData("2020-01-01T10:00:00.5000000", 0, "2020-01-01T10:00:01.0000000")]   // half rounds up
    [InlineData("2020-01-01T00:00:00.0500000", 1, "2020-01-01T00:00:00.1000000")]
    [InlineData("2020-01-01T00:00:00.4999999", 1, "2020-01-01T00:00:00.5000000")]
    [InlineData("9999-12-31T23:59:59.9999999", 1, "9999-12-31T23:59:59.9000000")]   // clamped at the maximum
    [InlineData("2020-01-01T10:00:00.0033333", 7, "2020-01-01T10:00:00.0033333")]
    public void Datetime2_values_round_like_CAST(string input, int scale, string expected)
        => Assert.Equal(At(expected), TargetShape.RoundValue(At(input), "datetime2", scale));

    [Fact]
    public void Time_and_datetimeoffset_round_like_CAST()
    {
        Assert.Equal(TimeSpan.Parse("23:59:59.9", CultureInfo.InvariantCulture), TargetShape.RoundValue(TimeSpan.Parse("23:59:59.9999999", CultureInfo.InvariantCulture), "time", 1));
        Assert.Equal(TimeSpan.Parse("00:00:00.1", CultureInfo.InvariantCulture), TargetShape.RoundValue(TimeSpan.Parse("00:00:00.05", CultureInfo.InvariantCulture), "time", 1));
        var dto = DateTimeOffset.Parse("2020-01-01T10:00:00.9999996+02:00", CultureInfo.InvariantCulture);
        Assert.Equal(DateTimeOffset.Parse("2020-01-01T10:00:01+02:00", CultureInfo.InvariantCulture), TargetShape.RoundValue(dto, "datetimeoffset", 0));
        Assert.Equal("abc", TargetShape.RoundValue("abc", "datetime2", 0));
        Assert.Equal(At("2020-01-01T10:00:00.9970000"), TargetShape.RoundValue(At("2020-01-01T10:00:00.9970000"), "datetime", 0));
    }

    [Theory]
    [InlineData("nvarchar", 100, 0, 0, "nvarchar(50)")]
    [InlineData("nvarchar", -1, 0, 0, "nvarchar(max)")]
    [InlineData("varchar", 12, 0, 0, "varchar(12)")]
    [InlineData("char", 2, 0, 0, "char(2)")]
    [InlineData("varbinary", -1, 0, 0, "varbinary(max)")]
    [InlineData("decimal", 9, 19, 4, "decimal(19,4)")]
    [InlineData("datetime2", 6, 19, 0, "datetime2(0)")]
    [InlineData("time", 5, 16, 7, "time(7)")]
    [InlineData("int", 4, 10, 0, "int")]
    [InlineData("bit", 1, 1, 0, "bit")]
    public void TypeText_matches_TSQL_declarations(string type, int maxLength, byte precision, byte scale, string expected)
        => Assert.Equal(expected, TargetShape.TypeText(type, maxLength, precision, scale));

    [Fact]
    public void Normalize_rounds_only_bound_temporal_columns_with_lower_scale()
    {
        var shape = new TargetShape("app.T", [
            new TargetColumn("CreatedAt", "datetime2", 6, 19, 0, false, false),
            new TargetColumn("Raw", "datetime2", 8, 27, 7, false, false),
        ]);
        var table = new DataTable();
        table.Columns.Add("CreatedAtSrc", typeof(DateTime));
        table.Columns.Add("Raw", typeof(DateTime));
        table.Rows.Add(At("2020-01-01T10:00:00.997"), At("2020-01-01T10:00:00.997"));
        table.Rows.Add(DBNull.Value, DBNull.Value);
        shape.Normalize(table, [new ColumnBinding("CreatedAtSrc", "CreatedAt"), new ColumnBinding("Raw", "Raw")]);
        Assert.Equal(At("2020-01-01T10:00:01"), table.Rows[0]["CreatedAtSrc"]);
        Assert.Equal(At("2020-01-01T10:00:00.997"), table.Rows[0]["Raw"]);
        Assert.Equal(DBNull.Value, table.Rows[1]["CreatedAtSrc"]);
    }

    [Fact]
    public void RowSnapshot_truncates_long_values_and_keeps_nulls()
    {
        var table = new DataTable();
        table.Columns.Add("Cmnt", typeof(string));
        table.Columns.Add("Qty", typeof(int));
        table.Columns.Add("Blob", typeof(byte[]));
        table.Rows.Add(new string('x', 300), DBNull.Value, new byte[] { 0xAB, 0x01 });
        string json = RowSnapshot.Json(table.Rows[0],
            [new ColumnBinding("Cmnt", "Comment"), new ColumnBinding("Qty", "Quantity"), new ColumnBinding("Blob", "Data")]);
        var o = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(RowSnapshot.MaxValueLength + 1, o["Comment"]!.GetValue<string>().Length);   // 200 chars + "…"
        Assert.True(o.ContainsKey("Quantity"));
        Assert.Null(o["Quantity"]);
        Assert.Equal("0xAB01", o["Data"]!.GetValue<string>());
    }
}
```

- [ ] **Step 2: Write the failing integration tests**

`plugins/db-migrate/engine/Dbm.Tests/Integration/Transfer/BulkLoaderTests.cs`:

```csharp
using System.Data;
using Dbm.Core.SqlGen;
using Dbm.Core.Transfer;
using Dbm.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Dbm.Tests.Integration.Transfer;

[Trait("Category", "Integration")]
public sealed class BulkLoaderTests
{
    private const string Schema = """
        CREATE TABLE dbo.P (id int NOT NULL PRIMARY KEY);
        INSERT dbo.P VALUES (1),(2),(3);
        CREATE TABLE dbo.C (id int IDENTITY(1,1) NOT NULL PRIMARY KEY, pid int NOT NULL CONSTRAINT FK_C_P REFERENCES dbo.P(id),
          qty int NOT NULL CONSTRAINT CK_C_qty CHECK (qty > 0), note nvarchar(10) NULL, at datetime2(0) NULL);
        CREATE TABLE dbo.T (id int NOT NULL PRIMARY KEY, v int NOT NULL);
        GO
        CREATE TRIGGER dbo.trg_T_rollback ON dbo.T AFTER INSERT AS BEGIN IF EXISTS (SELECT 1 FROM inserted WHERE v < 0) ROLLBACK TRANSACTION; END
        """;

    private static TaskPlan TaskFor(string mode = "direct") => new()
    {
        Target = "dbo.C",
        Mode = mode,
        SourceQuery = "unused",
        KeyColumns = ["__k0"],
        IdentityInsert = true,
        Columns = [new("Id", "id"), new("Pid", "pid"), new("Qty", "qty"), new("Note", "note"), new("At", "at")],
        StagingDdl = mode == "staging_merge"
            ? "CREATE TABLE #stg (Id int NOT NULL, Pid int NOT NULL, Qty int NULL, Note nvarchar(10) NULL, At datetime2(0) NULL)" : null,
        MergeSql = mode == "staging_merge"
            ? "SET IDENTITY_INSERT dbo.C ON; INSERT dbo.C (id, pid, qty, note, at) SELECT Id, Pid, Qty, Note, At FROM #stg; SET IDENTITY_INSERT dbo.C OFF;"
            : null,
    };

    private static DataTable Rows(int count, Action<int, object[]>? spoil = null)
    {
        var t = new DataTable();
        t.Columns.Add("Id", typeof(int));
        t.Columns.Add("Pid", typeof(int));
        t.Columns.Add("Qty", typeof(int));
        t.Columns.Add("Note", typeof(string));
        t.Columns.Add("At", typeof(DateTime));
        t.Columns.Add("__k0", typeof(int));
        for (int i = 0; i < count; i++)
        {
            object[] v = [100 + i, 1 + i % 3, 5, "ok", new DateTime(2020, 1, 1, 10, 0, 0).AddMilliseconds(997), 100 + i];
            spoil?.Invoke(i, v);
            t.Rows.Add(v);
        }
        return t;
    }

    private static async Task<(TempDatabase Db, SqlConnection Conn)> OpenAsync()
    {
        var db = await TempDatabase.CreateAsync("dbm_bulk");
        await db.ExecAsync(Schema);
        var conn = new SqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        return (db, conn);
    }

    private static void Spoil(int i, object[] v)
    {
        if (i == 3) v[1] = 99;                    // FK violation (server side, V1)
        if (i == 7) v[2] = 0;                     // CHECK violation (server side, V1)
        if (i == 11) v[3] = "far too long!!";     // truncation (client side, V2)
        if (i == 15) v[2] = DBNull.Value;         // NULL into NOT NULL (client side, V2)
    }

    [Fact]
    public async Task Skip_mode_isolates_each_bad_row_and_commits_the_rest_with_identity_kept()
    {
        var (db, conn) = await OpenAsync();
        await using var dbScope = db;
        await using var connScope = conn;
        var loader = new BulkLoader(TaskFor(), new TransferOptions { ErrorMode = "skip" });
        await using var scope = await TxScope.BeginAsync(conn, default);
        var outcome = await loader.LoadAsync(scope, Rows(20, Spoil), allowRestart: true, progress: null, default);
        await scope.CommitAsync(default);

        Assert.Equal(16, outcome.Loaded);
        Assert.Equal(new[] { 3, 7, 11, 15 }, outcome.Failed.Select(f => f.Row));
        Assert.Contains("FK_C_P", outcome.Failed[0].Error);
        Assert.Contains("CK_C_qty", outcome.Failed[1].Error);
        Assert.Equal(16, await db.CountAsync("dbo.C"));
        Assert.Equal(100, await db.ScalarAsync<int>("SELECT MIN(id) FROM dbo.C"));
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM sys.foreign_keys WHERE is_not_trusted = 1"));   // V4
    }

    [Fact]
    public async Task Stop_mode_reports_the_first_bad_row_only()
    {
        var (db, conn) = await OpenAsync();
        await using var dbScope = db;
        await using var connScope = conn;
        var loader = new BulkLoader(TaskFor(), new TransferOptions { ErrorMode = "stop" });
        await using var scope = await TxScope.BeginAsync(conn, default);
        var outcome = await loader.LoadAsync(scope, Rows(20, Spoil), true, null, default);
        await scope.RollbackAsync();
        Assert.Single(outcome.Failed);
        Assert.Equal(3, outcome.Failed[0].Row);
        Assert.Equal(0, await db.CountAsync("dbo.C"));
    }

    [Fact]
    public async Task Transaction_ending_errors_restart_and_reload_confirmed_rows()
    {
        var (db, conn) = await OpenAsync();
        await using var dbScope = db;
        await using var connScope = conn;
        var task = new TaskPlan { Target = "dbo.T", SourceQuery = "unused", KeyColumns = ["__k0"], Columns = [new("Id", "id"), new("V", "v")] };
        var table = new DataTable();
        table.Columns.Add("Id", typeof(int));
        table.Columns.Add("V", typeof(int));
        table.Columns.Add("__k0", typeof(int));
        for (int i = 0; i < 10; i++) table.Rows.Add(i, i == 6 ? -1 : i, i);

        var loader = new BulkLoader(task, new TransferOptions { ErrorMode = "skip", FireTriggers = true });
        await using var scope = await TxScope.BeginAsync(conn, default);
        var outcome = await loader.LoadAsync(scope, table, allowRestart: true, progress: null, default);
        await scope.CommitAsync(default);

        Assert.Equal(9, outcome.Loaded);
        Assert.Equal(new[] { 6 }, outcome.Failed.Select(f => f.Row));
        Assert.True(outcome.Restarts >= 1);
        Assert.Equal(9, await db.CountAsync("dbo.T"));
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.T WHERE v < 0"));
    }

    [Fact]
    public async Task Keyless_loads_cannot_survive_a_transaction_ending_error()
    {
        var (db, conn) = await OpenAsync();
        await using var dbScope = db;
        await using var connScope = conn;
        var task = new TaskPlan { Target = "dbo.T", SourceQuery = "unused", Columns = [new("Id", "id"), new("V", "v")] };
        var table = new DataTable();
        table.Columns.Add("Id", typeof(int));
        table.Columns.Add("V", typeof(int));
        table.Rows.Add(1, 1);
        table.Rows.Add(2, -1);
        var loader = new BulkLoader(task, new TransferOptions { ErrorMode = "skip", FireTriggers = true });
        await using var scope = await TxScope.BeginAsync(conn, default);
        var ex = await Assert.ThrowsAsync<TransferException>(() => loader.LoadAsync(scope, table, allowRestart: false, progress: null, default));
        Assert.Equal("tx_ended", ex.Code);
    }

    [Fact]
    public async Task Staging_merge_bisects_inside_the_same_transaction()
    {
        var (db, conn) = await OpenAsync();
        await using var dbScope = db;
        await using var connScope = conn;
        var loader = new BulkLoader(TaskFor("staging_merge"), new TransferOptions { ErrorMode = "skip" });
        await using var scope = await TxScope.BeginAsync(conn, default);
        var outcome = await loader.LoadAsync(scope, Rows(10, (i, v) => { if (i == 4) v[1] = 42; }), true, null, default);
        await scope.CommitAsync(default);
        Assert.Equal(9, outcome.Loaded);
        Assert.Equal(new[] { 4 }, outcome.Failed.Select(f => f.Row));
        Assert.Equal(9, await db.CountAsync("dbo.C"));
    }

    [Fact]
    public async Task Normalized_temporal_values_equal_CAST_and_target_shape_is_read()
    {
        var (db, conn) = await OpenAsync();
        await using var dbScope = db;
        await using var connScope = conn;
        var task = TaskFor();
        var shape = await TargetShape.LoadAsync(conn, "dbo.C", default);
        Assert.Equal("nvarchar(10)", shape.Find("NOTE")!.TypeText);
        Assert.Equal("datetime2(0)", shape.Find("at")!.TypeText);
        Assert.True(shape.Find("id")!.IsIdentity);

        var table = Rows(3);
        shape.Normalize(table, task.Columns);
        var loader = new BulkLoader(task, new TransferOptions { ErrorMode = "skip" });
        await using (var scope = await TxScope.BeginAsync(conn, default))
        {
            await loader.LoadAsync(scope, table, true, null, default);
            await scope.CommitAsync(default);
        }
        Assert.Equal(3, await db.ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.C WHERE at = CAST(CAST('2020-01-01T10:00:00.997' AS datetime) AS datetime2(0))"));   // 10:00:01
        var ex = await Assert.ThrowsAsync<TransferException>(() => TargetShape.LoadAsync(conn, "dbo.Nope", default));
        Assert.Equal("target_missing", ex.Code);
    }

    [Fact]
    public async Task ChunkReader_reads_datetime_exactly_and_in_pieces()
    {
        var (db, conn) = await OpenAsync();
        await using var dbScope = db;
        await using var connScope = conn;
        await using var cmd = new SqlCommand(
            "SELECT CAST('2020-01-01T10:00:00.003' AS datetime) AS [D], CAST(NULL AS datetime) AS [N], 7 AS [I] " +
            "UNION ALL SELECT CAST('2020-01-01T10:00:00.007' AS datetime), NULL, 8", conn);
        await using var r = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
        var table = ChunkReader.NewTable(r);
        Assert.Equal(1, await ChunkReader.FillAsync(r, table, 1, default));
        Assert.Equal(1, await ChunkReader.FillAsync(r, table, 5, default));
        Assert.Equal(0, await ChunkReader.FillAsync(r, table, 5, default));
        Assert.Equal(new DateTime(2020, 1, 1, 10, 0, 0).AddTicks(33333), table.Rows[0]["D"]);   // V8: SqlClient alone gives .0030000
        Assert.Equal(new DateTime(2020, 1, 1, 10, 0, 0).AddTicks(66667), table.Rows[1]["D"]);
        Assert.Equal(DBNull.Value, table.Rows[0]["N"]);
        Assert.Equal(8, table.Rows[1]["I"]);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~BisectorTests|FullyQualifiedName~TargetShapeTests|FullyQualifiedName~BulkLoaderTests"`
Expected: build FAILS with `CS0246: The type or namespace name 'IBisectTarget' could not be found` (and `BulkLoader`, `TxScope`, `TargetShape`, `ChunkReader`, `RowSnapshot`, …).

- [ ] **Step 4: Implement bisection and the transaction scope**

`plugins/db-migrate/engine/Dbm/Core/Transfer/Bisector.cs`:

```csharp
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
    public static async Task<BisectResult> RunAsync(int rowCount, IBisectTarget target, bool stopAtFirstFailure, CancellationToken ct)
    {
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
                            "Rows that loaded before a transaction-ending error failed to reload: " + reload.Error);
                }
            }
            if (segment.Length == 1)
            {
                failed.Add(new RowFailure(segment[0], attempt.Error ?? "unknown error"));
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
}
```

`plugins/db-migrate/engine/Dbm/Core/Transfer/TxScope.cs`:

```csharp
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

/// <summary>One target transaction that can be replaced after a transaction-ending error (V3). Dispose = rollback unless committed.</summary>
public sealed class TxScope : IAsyncDisposable
{
    private bool _finished;

    private TxScope(SqlConnection connection, SqlTransaction tx)
    {
        Connection = connection;
        Tx = tx;
    }

    public SqlConnection Connection { get; }
    public SqlTransaction Tx { get; private set; }
    public int Restarts { get; private set; }

    public static async Task<TxScope> BeginAsync(SqlConnection conn, CancellationToken ct)
        => new(conn, (SqlTransaction)await conn.BeginTransactionAsync(ct));

    /// <summary>1 = usable (savepoint rollback possible). A failed probe means the server transaction is gone: 0.</summary>
    public async Task<int> XactStateAsync(CancellationToken ct)
    {
        try
        {
            await using var cmd = new SqlCommand("SELECT XACT_STATE()", Connection, Tx);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            return 0;
        }
    }

    public async Task RestartAsync(CancellationToken ct)
    {
        await SafeRollbackAsync();
        Tx = (SqlTransaction)await Connection.BeginTransactionAsync(ct);
        Restarts++;
    }

    public async Task CommitAsync(CancellationToken ct)
    {
        await Tx.CommitAsync(ct);
        _finished = true;
    }

    public async Task RollbackAsync()
    {
        await SafeRollbackAsync();
        _finished = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_finished) await SafeRollbackAsync();
        else await Tx.DisposeAsync();
    }

    private async Task SafeRollbackAsync()
    {
        try { await Tx.RollbackAsync(); }
        catch (Exception) { /* V3: the server may already have ended it; a broken connection is handled by the caller */ }
        try { await Tx.DisposeAsync(); }
        catch (Exception) { }
    }
}
```

- [ ] **Step 5: Implement target shape, chunk reader, row snapshot and the bulk loader**

`plugins/db-migrate/engine/Dbm/Core/Transfer/TargetShape.cs`:

```csharp
using System.Data;
using System.Globalization;
using Dbm.Core.Sql;
using Dbm.Core.SqlGen;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

/// <summary>MaxLength in bytes as in sys.columns (-1 = max).</summary>
public sealed record TargetColumn(string Name, string DataType, int MaxLength, byte Precision, byte Scale, bool IsIdentity, bool IsComputed)
{
    public string TypeText => TargetShape.TypeText(DataType, MaxLength, Precision, Scale);
}

/// <summary>Target table metadata read live from sys.columns, plus the V8 temporal rounding applied before bulk copy.</summary>
public sealed class TargetShape(string target, IReadOnlyList<TargetColumn> columns)
{
    private static readonly long[] Units = [10_000_000, 1_000_000, 100_000, 10_000, 1_000, 100, 10];

    public string Target { get; } = target;
    public IReadOnlyList<TargetColumn> Columns { get; } = columns;

    public TargetColumn? Find(string name) => Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public static async Task<TargetShape> LoadAsync(SqlConnection conn, string targetKey, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("""
            SELECT c.name, COALESCE(CASE WHEN t.is_user_defined = 1 THEN bt.name END, t.name), c.max_length, c.precision, c.scale, c.is_identity, c.is_computed
            FROM sys.columns AS c
            JOIN sys.types AS t ON t.user_type_id = c.user_type_id
            LEFT JOIN sys.types AS bt ON bt.user_type_id = c.system_type_id
            WHERE c.object_id = OBJECT_ID(@t)
            ORDER BY c.column_id
            """, conn);
        cmd.Parameters.Add(new SqlParameter("@t", SqlDbType.NVarChar, 600) { Value = SqlQuote.TableKey(targetKey) });
        var cols = new List<TargetColumn>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                cols.Add(new TargetColumn(r.GetString(0), r.GetString(1).ToLowerInvariant(), r.GetInt16(2), r.GetByte(3), r.GetByte(4),
                    r.GetBoolean(5), r.GetBoolean(6)));
        if (cols.Count == 0) throw new TransferException("target_missing", $"Target table {targetKey} does not exist.");
        return new TargetShape(targetKey, cols);
    }

    /// <summary>Rounds bound datetime2/datetimeoffset/time values to the target scale exactly like CAST (V8).</summary>
    public void Normalize(DataTable table, IReadOnlyList<ColumnBinding> bindings)
    {
        foreach (var b in bindings)
        {
            var col = Find(b.Target);
            if (col is null || col.Scale >= 7 || col.DataType is not ("datetime2" or "datetimeoffset" or "time")) continue;
            int idx = table.Columns.IndexOf(b.Source);
            if (idx < 0) continue;
            foreach (DataRow row in table.Rows)
            {
                object v = row[idx];
                if (v is not DBNull) row[idx] = RoundValue(v, col.DataType, col.Scale);
            }
        }
    }

    public static string TypeText(string dataType, int maxLength, byte precision, byte scale) => dataType switch
    {
        "char" or "varchar" or "binary" or "varbinary" => $"{dataType}({(maxLength == -1 ? "max" : maxLength.ToString(CultureInfo.InvariantCulture))})",
        "nchar" or "nvarchar" => $"{dataType}({(maxLength == -1 ? "max" : (maxLength / 2).ToString(CultureInfo.InvariantCulture))})",
        "decimal" or "numeric" => $"{dataType}({precision},{scale})",
        "datetime2" or "datetimeoffset" or "time" => $"{dataType}({scale})",
        _ => dataType,
    };

    /// <summary>Half-up rounding to 10^-scale seconds; when rounding up would pass maxTicks the value is truncated instead (as CAST does).</summary>
    public static long RoundTicks(long ticks, int scale, long maxTicks)
    {
        if (scale >= 7) return ticks;
        long unit = Units[Math.Max(scale, 0)];
        long rem = ticks % unit;
        if (rem * 2 < unit) return ticks - rem;
        long up = ticks - rem + unit;
        return up > maxTicks ? ticks - rem : up;
    }

    public static object RoundValue(object value, string targetType, int scale)
    {
        if (scale >= 7 || targetType is not ("datetime2" or "datetimeoffset" or "time")) return value;
        return value switch
        {
            DateTime d => new DateTime(RoundTicks(d.Ticks, scale, DateTime.MaxValue.Ticks), d.Kind),
            DateTimeOffset o => new DateTimeOffset(RoundTicks(o.Ticks, scale, DateTime.MaxValue.Ticks), o.Offset),
            TimeSpan t => new TimeSpan(RoundTicks(t.Ticks, scale, TimeSpan.TicksPerDay - 1)),
            _ => value,
        };
    }
}
```

`plugins/db-migrate/engine/Dbm/Core/Transfer/ChunkReader.cs`:

```csharp
using System.Data;
using System.Data.SqlTypes;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

/// <summary>Reads source rows into a DataTable (needed for bisection). Safe with CommandBehavior.SequentialAccess.</summary>
public static class ChunkReader
{
    private static readonly DateTime SqlBaseDate = new(1900, 1, 1);

    public static DataTable NewTable(SqlDataReader reader)
    {
        var table = new DataTable();
        for (int i = 0; i < reader.FieldCount; i++) table.Columns.Add(reader.GetName(i), reader.GetFieldType(i));
        return table;
    }

    /// <summary>Appends up to max rows; returns the number read (0 = reader exhausted).</summary>
    public static async Task<int> FillAsync(SqlDataReader reader, DataTable table, int max, CancellationToken ct)
    {
        int n = reader.FieldCount;
        var exact = new bool[n];
        for (int i = 0; i < n; i++) exact[i] = string.Equals(reader.GetDataTypeName(i), "datetime", StringComparison.OrdinalIgnoreCase);
        int count = 0;
        table.BeginLoadData();
        try
        {
            while (count < max && await reader.ReadAsync(ct))
            {
                var values = new object[n];
                for (int i = 0; i < n; i++)
                {
                    if (reader.IsDBNull(i)) { values[i] = DBNull.Value; continue; }
                    values[i] = exact[i] ? ExactDateTime(reader.GetSqlDateTime(i)) : reader.GetValue(i);
                }
                table.Rows.Add(values);
                count++;
            }
        }
        finally
        {
            table.EndLoadData();
        }
        return count;
    }

    /// <summary>V8: datetime stores 1/300 s ticks; SqlClient rounds them to ms, CAST(... AS datetime2(7)) does not.</summary>
    public static DateTime ExactDateTime(SqlDateTime value)
        => SqlBaseDate.AddDays(value.DayTicks).AddTicks((value.TimeTicks * 100_000L + 1) / 3);
}
```

`plugins/db-migrate/engine/Dbm/Core/Transfer/RowSnapshot.cs`:

```csharp
using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using Dbm.Core.SqlGen;

namespace Dbm.Core.Transfer;

/// <summary>Rejected-row JSON for error_row: bound columns by target name, each value cut to 200 characters.</summary>
public static class RowSnapshot
{
    public const int MaxValueLength = 200;

    public static string Json(DataRow row, IReadOnlyList<ColumnBinding> bindings)
    {
        var o = new JsonObject();
        foreach (var b in bindings)
        {
            if (!row.Table.Columns.Contains(b.Source)) continue;
            object v = row[b.Source];
            o[b.Target] = v is DBNull ? null : Text(v);
        }
        return o.ToJsonString();
    }

    public static string Text(object value)
    {
        string s = value switch
        {
            byte[] bytes => "0x" + Convert.ToHexString(bytes, 0, Math.Min(bytes.Length, MaxValueLength / 2)),
            DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset o => o.ToString("O", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
        return s.Length <= MaxValueLength ? s : s[..MaxValueLength] + "…";
    }
}
```

`plugins/db-migrate/engine/Dbm/Core/Transfer/BulkLoader.cs`:

```csharp
using System.Data;
using Dbm.Core.Sql;
using Dbm.Core.SqlGen;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

public sealed record ChunkOutcome(long Loaded, IReadOnlyList<RowFailure> Failed, int Restarts);

/// <summary>Loads one DataTable into the target inside the caller's transaction, bisecting failures (V1–V5).</summary>
public sealed class BulkLoader(TaskPlan task, TransferOptions options)
{
    public const string SavepointName = "dbm_b";
    private const int NotifyEvery = 5_000;

    public async Task<ChunkOutcome> LoadAsync(TxScope scope, DataTable rows, bool allowRestart, Action<long>? progress, CancellationToken ct)
    {
        var target = new Target(this, scope, rows, allowRestart, progress);
        var result = await Bisector.RunAsync(rows.Rows.Count, target, stopAtFirstFailure: !options.SkipErrors, ct);
        return new ChunkOutcome(result.Loaded.Count, result.Failed, target.Restarts);
    }

    private bool Staging => string.Equals(task.Mode, "staging_merge", StringComparison.OrdinalIgnoreCase);

    private async Task WriteAsync(TxScope scope, DataRow[] rows, Action<long>? progress, CancellationToken ct)
    {
        if (!Staging)
        {
            var o = SqlBulkCopyOptions.CheckConstraints | SqlBulkCopyOptions.KeepNulls;
            if (task.IdentityInsert) o |= SqlBulkCopyOptions.KeepIdentity;
            if (options.TableLock) o |= SqlBulkCopyOptions.TableLock;
            if (options.FireTriggers) o |= SqlBulkCopyOptions.FireTriggers;
            using var bc = NewCopy(scope, SqlQuote.TableKey(task.Target), o, progress);
            foreach (var b in task.Columns) bc.ColumnMappings.Add(b.Source, b.Target);
            await bc.WriteToServerAsync(rows, ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(task.StagingDdl) || string.IsNullOrWhiteSpace(task.MergeSql))
            throw new TransferException("bad_task", $"Task for {task.Target} is staging_merge but has no StagingDdl/MergeSql.");
        await ExecAsync(scope, "IF OBJECT_ID(N'tempdb..#stg') IS NOT NULL DROP TABLE #stg;", ct);
        await ExecAsync(scope, task.StagingDdl, ct);
        var stg = await StagingColumnsAsync(scope, ct);
        using (var bc = NewCopy(scope, "#stg", SqlBulkCopyOptions.KeepNulls, progress))
        {
            var mapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var table = rows[0].Table;
            foreach (var b in task.Columns)
                if (table.Columns.Contains(b.Source) && stg.TryGetValue(b.Target, out var dest) && mapped.Add(b.Source))
                    bc.ColumnMappings.Add(b.Source, dest);
            foreach (DataColumn c in table.Columns)
                if (!mapped.Contains(c.ColumnName) && stg.TryGetValue(c.ColumnName, out var dest) && mapped.Add(c.ColumnName))
                    bc.ColumnMappings.Add(c.ColumnName, dest);
            await bc.WriteToServerAsync(rows, ct);
        }
        await ExecAsync(scope, task.MergeSql, ct);
        await ExecAsync(scope, "DROP TABLE #stg;", ct);
    }

    private static SqlBulkCopy NewCopy(TxScope scope, string destination, SqlBulkCopyOptions o, Action<long>? progress)
    {
        var bc = new SqlBulkCopy(scope.Connection, o, scope.Tx) { DestinationTableName = destination, BulkCopyTimeout = 0, EnableStreaming = true };
        if (progress is not null)
        {
            bc.NotifyAfter = NotifyEvery;
            bc.SqlRowsCopied += (_, e) => progress(e.RowsCopied);
        }
        return bc;
    }

    private static async Task<Dictionary<string, string>> StagingColumnsAsync(TxScope scope, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = new SqlCommand("SELECT name FROM tempdb.sys.columns WHERE object_id = OBJECT_ID(N'tempdb..#stg')", scope.Connection, scope.Tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result[r.GetString(0)] = r.GetString(0);
        return result;
    }

    private static async Task ExecAsync(TxScope scope, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, scope.Connection, scope.Tx) { CommandTimeout = 0 };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private sealed class Target(BulkLoader loader, TxScope scope, DataTable table, bool allowRestart, Action<long>? progress) : IBisectTarget
    {
        private bool _first = true;
        public int Restarts { get; private set; }

        public async Task<LoadAttempt> TryLoadAsync(IReadOnlyList<int> rows, CancellationToken ct)
        {
            var batch = new DataRow[rows.Count];
            for (int i = 0; i < rows.Count; i++) batch[i] = table.Rows[rows[i]];
            var report = _first ? progress : null;   // live progress only for the first (whole-chunk) attempt
            _first = false;
            scope.Tx.Save(SavepointName);
            try
            {
                await loader.WriteAsync(scope, batch, report, ct);
                return LoadAttempt.Success;
            }
            catch (Exception ex) when ((ex is SqlException or InvalidOperationException) && !ct.IsCancellationRequested)
            {
                if (await scope.XactStateAsync(ct) == 1)
                {
                    scope.Tx.Rollback(SavepointName);   // V1/V2
                    return new LoadAttempt(false, ex.Message);
                }
                return new LoadAttempt(false, ex.Message, Doomed: true);   // V3
            }
        }

        public async Task RestartAsync(CancellationToken ct)
        {
            if (!allowRestart)
                throw new TransferException("tx_ended",
                    "A row error ended the transaction of a single-transaction (keyless) task, so it cannot be bisected. Fix the data or give the task a key and retry.");
            await scope.RestartAsync(ct);
            Restarts++;
        }
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~BisectorTests|FullyQualifiedName~TargetShapeTests|FullyQualifiedName~BulkLoaderTests"`
Expected: PASS — 6 BisectorTests, 20 TargetShapeTests (theory rows included), 7 BulkLoaderTests.

- [ ] **Step 7: Commit**

```bash
git add plugins/db-migrate/engine/Dbm/Core/Transfer plugins/db-migrate/engine/Dbm.Tests/Unit/Transfer plugins/db-migrate/engine/Dbm.Tests/Integration/Transfer
git commit -F - <<'EOF'
feat(transfer): bulk loader with savepoint/restart bisection and CAST-exact temporal values

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49
EOF
```

---

### Task 5.3: Transfer engine (dependencies, parallelism, pause/resume/cancel, crash recovery)

**Files:**
- Create: `plugins/db-migrate/engine/Dbm/Core/Transfer/TransferControl.cs`
- Create: `plugins/db-migrate/engine/Dbm/Core/Transfer/TransferProgress.cs`
- Create: `plugins/db-migrate/engine/Dbm/Core/Transfer/TargetOps.cs`
- Create: `plugins/db-migrate/engine/Dbm/Core/Transfer/TaskRunner.cs`
- Create: `plugins/db-migrate/engine/Dbm/Core/Transfer/TransferEngine.cs`
- Create: `plugins/db-migrate/engine/Dbm.Tests/Support/XferServices.cs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/Transfer/TransferProgressTests.cs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Integration/Transfer/TransferEngineTests.cs`

**Interfaces:**
- Consumes: `DbmServices` (`Transfers`, `Sink`), `IEventSink` (C1/C6), `SqlConnect.OpenAsync`, `Redactor.Scrub/SecretsOf`, `SqlQuote.TableKey` (C3), `SqlPlanPayload`/`TaskPlan` (C13), `EnumText`, `Json`, `Clock`; everything from T5.1/T5.2.
- Produces:
```csharp
namespace Dbm.Core.Transfer;
public enum StopKind { None, Pause, Cancel, Fail }
public sealed record ChunkCommit(string TaskId, string Target, int ChunkNo, long RowsDone, long RowsError);
public sealed class TransferControl
{
    public StopKind Kind { get; }  public bool StopRequested { get; }
    public void RequestPause();    // only from None
    public void RequestCancel();   // overrides Pause, never Fail
    public void RequestFail();     // always wins
    public event Action<ChunkCommit>? ChunkCommitted;   // raised synchronously after each committed chunk
}
public sealed class RateWindow(TimeSpan window) { public void Add(DateTimeOffset t, long value); public double PerSecond { get; } }
public sealed record ProgressTask(string TaskId, string Target, string Status, long RowsDone, long? RowsSource, long RowsError, double RowsPerSec);
public sealed record ProgressOverall(long Done, long Total, long RowsError, double RowsPerSec, double? EtaSec);
public sealed record ProgressSnapshot(long RunId, string Status, List<ProgressTask> Tasks, ProgressOverall Overall);
public sealed class TransferProgress
{
    public static readonly TimeSpan Throttle;   // 250 ms => at most 4 "transfer_progress" events per second
    public TransferProgress(long runId, IEventSink sink, IEnumerable<TransferTaskRow> tasks, Func<DateTimeOffset>? now = null);
    public string RunStatus { get; set; }
    public void SetStatus(string taskId, TransferTaskStatus status);
    public void SetSource(string taskId, long? rowsSource);
    public void Committed(string taskId, long rowsDone, long rowsError);
    public void InFlight(string taskId, long rowsInFlight);
    public ProgressSnapshot Snapshot();
    public bool Publish(bool force);            // sink.Publish("transfer_progress", Snapshot(), persist: false)
}
public static class TargetOps
{
    public static Task ExecAllAsync(SqlConnection conn, IEnumerable<string> statements, CancellationToken ct);
    public static Task<long> ScalarLongAsync(SqlConnection conn, string sql, CancellationToken ct, int timeoutSec = 0);
    public static Task<long> CountTargetAsync(SqlConnection conn, string targetKey, CancellationToken ct);
    public static string CountSqlOf(TaskPlan task);                       // CountSql, or COUNT_BIG over SourceQuery
    public static Task<bool> IsReferencedAsync(SqlConnection conn, string targetKey, CancellationToken ct);
    public static Task<List<string>> TruncateAsync(SqlConnection conn, IReadOnlyList<string> targetsInLoadOrder, CancellationToken ct);
}
public sealed record TaskResult(string TaskId, TransferTaskStatus Status, string? Error);
public sealed record TransferOutcome(RunStatus Status, string? Error);
public sealed class TransferEngine(DbmServices services, SqlPlanPayload plan, string sourceCs, string targetCs)
{
    public static IReadOnlyList<string> PlanOrder(SqlPlanPayload plan);
    public long CreateRun(int sqlVersion, TransferOptions options);
    public Task<TransferOutcome> RunAsync(long runId, TransferControl control, CancellationToken ct);
    // fresh or resume; hard cancellation of ct throws OperationCanceledException and leaves the run "running" (crash semantics)
}
```

**Run algorithm (normative):**
1. Run → `running` (+ `transfer_run_changed`). Open target: `ControlTable.EnsureAsync`; global `PreSql` (every run segment — generated statements are idempotent; V10 needs the cycle `NOCHECK` before truncation).
2. Setup only when some task has `rows_before = null` (fresh run or a crash during setup): `TruncateTarget` → `TargetOps.TruncateAsync` (reverse plan order, `DELETE` for tables referenced by another table's FK, else `TRUNCATE`); then per task `rows_before = COUNT_BIG(*)` on the target and `rows_source` = `CountSql` on the source.
3. Scheduler: tasks not `done` in ordinal order; a task starts when all its `DependsOn` tasks are `done`; at most `Parallelism` at once; no new starts once a stop is requested. A failed task records `firstError` and calls `RequestFail()`.
4. Task (`TaskRunner`): status `running`; read the checkpoint (done → mirror + `done`); task `PreSql`; keyed → chunk loop (stop requested → task `paused`; read ≤ ChunkSize rows via `ChunkPlanner`; empty → checkpoint done; `TargetShape.Normalize`; one `TxScope`: `BulkLoader` (skip → good rows + checkpoint commit; stop + failure → rollback, record the first bad row, task `failed`); after commit write `error_row`s, mirror progress/heartbeat, raise `ChunkCommitted`); keyless → one `TxScope` for the whole task reading sequential DataTables from one reader (pause waits for the task to finish; cancel/fail rolls it back); task `PostSql`; `done`.
5. End: `firstError` → run `failed` (summary `{"error":…}`, control table kept for resume); cancel → drop control table (unless `KeepControlTable`), run `cancelled`; pause → run `paused`; otherwise global `PostSql`, drop control table (unless kept), completion hook (T5.4: validation + report) → run `completed`.

- [ ] **Step 1: Write the test support helpers**

`plugins/db-migrate/engine/Dbm.Tests/Support/XferServices.cs`:

```csharp
using Dbm.Core;

namespace Dbm.Tests.Support;

public sealed class RecordingSink : IEventSink
{
    private readonly List<(string Type, object? Payload, bool Persist)> _events = new();

    public void Publish(string type, object? payload = null, bool persist = true)
    {
        lock (_events) _events.Add((type, payload, persist));
    }

    public IReadOnlyList<(string Type, object? Payload, bool Persist)> Events
    {
        get { lock (_events) return _events.ToList(); }
    }

    public int Count(string type) => Events.Count(e => e.Type == type);
}

/// <summary>TestWorkspace.OpenServices (real registries, project "test") whose event sink records every event.</summary>
public sealed class XferServices : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public XferServices()
    {
        Services = _workspace.OpenServices();
        Services.Sink = Sink;
    }

    public string Root => _workspace.Root;
    public RecordingSink Sink { get; } = new();
    public DbmServices Services { get; }

    public void Dispose() => _workspace.Dispose();
}
```

- [ ] **Step 2: Write the failing unit tests (progress, rate, throttle, control)**

`plugins/db-migrate/engine/Dbm.Tests/Unit/Transfer/TransferProgressTests.cs`:

```csharp
using Dbm.Core.State;
using Dbm.Core.Transfer;
using Dbm.Tests.Support;
using Xunit;

namespace Dbm.Tests.Unit.Transfer;

public sealed class TransferProgressTests
{
    private DateTimeOffset _now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    private static TransferTaskRow Row(string id, string target, long? source) =>
        new(1, id, target, id == "T01" ? 0 : 1, TransferTaskStatus.Pending, source, 0, 0, 0, null, null, null, null, null, null);

    [Fact]
    public void RateWindow_measures_rows_per_second_over_the_window()
    {
        var w = new RateWindow(TimeSpan.FromSeconds(10));
        Assert.Equal(0, w.PerSecond);
        w.Add(_now, 0);
        w.Add(_now.AddSeconds(2), 1000);
        Assert.Equal(500, w.PerSecond);
        w.Add(_now.AddSeconds(30), 1000);          // old samples fall out; no progress -> 0
        w.Add(_now.AddSeconds(31), 1000);
        Assert.Equal(0, w.PerSecond);
    }

    [Fact]
    public void Snapshot_aggregates_committed_and_in_flight_rows_with_eta()
    {
        var sink = new RecordingSink();
        var p = new TransferProgress(7, sink, [Row("T01", "app.A", 1000), Row("T02", "app.B", 2000)], () => _now);
        p.SetStatus("T01", TransferTaskStatus.Running);
        p.Committed("T01", 0, 0);
        _now = _now.AddSeconds(2);
        p.Committed("T01", 800, 3);
        p.InFlight("T01", 200);
        var s = p.Snapshot();
        Assert.Equal(7, s.RunId);
        Assert.Equal("running", s.Status);
        var t1 = s.Tasks.Single(t => t.TaskId == "T01");
        Assert.Equal(1000, t1.RowsDone);
        Assert.Equal(3, t1.RowsError);
        Assert.Equal("running", t1.Status);
        Assert.Equal(500, t1.RowsPerSec);
        Assert.Equal(3000, s.Overall.Total);
        Assert.Equal(1000, s.Overall.Done);
        Assert.Equal(500, s.Overall.RowsPerSec);
        Assert.Equal(4, s.Overall.EtaSec);                         // (3000 - 1000) / 500
        Assert.Equal("pending", s.Tasks.Single(t => t.TaskId == "T02").Status);
    }

    [Fact]
    public void Publish_is_throttled_to_four_per_second_unless_forced()
    {
        var sink = new RecordingSink();
        var p = new TransferProgress(1, sink, [Row("T01", "app.A", 10)], () => _now);
        Assert.True(p.Publish(false));
        _now = _now.AddMilliseconds(100);
        Assert.False(p.Publish(false));
        Assert.True(p.Publish(true));
        _now = _now.AddMilliseconds(300);
        Assert.True(p.Publish(false));
        Assert.Equal(3, sink.Count("transfer_progress"));
        Assert.All(sink.Events, e => Assert.False(e.Persist));
    }

    [Fact]
    public void Control_precedence_is_fail_over_cancel_over_pause()
    {
        var c = new TransferControl();
        Assert.False(c.StopRequested);
        c.RequestPause();
        Assert.Equal(StopKind.Pause, c.Kind);
        c.RequestCancel();
        Assert.Equal(StopKind.Cancel, c.Kind);
        c.RequestPause();
        Assert.Equal(StopKind.Cancel, c.Kind);
        c.RequestFail();
        c.RequestCancel();
        Assert.Equal(StopKind.Fail, c.Kind);
    }
}
```

- [ ] **Step 3: Write the failing engine integration tests**

`plugins/db-migrate/engine/Dbm.Tests/Integration/Transfer/TransferEngineTests.cs`:

```csharp
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
        Assert.Equal(1, rig.Repo.RecoverInterrupted());
        Assert.Equal(RunStatus.Paused, rig.Repo.GetRun(runId)!.Status);
        Assert.DoesNotContain(rig.Repo.Tasks(runId), t => t.Status == TransferTaskStatus.Running);

        var fresh = new TransferEngine(rig.Svc.Services, Plan(), fx.Src.ConnectionString, rig.Tgt.ConnectionString);
        var outcome = await fresh.RunAsync(runId, new TransferControl(), default);
        Assert.Equal(RunStatus.Completed, outcome.Status);
        await AssertExactFinalStateAsync(rig, runId);
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
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~TransferProgressTests|FullyQualifiedName~TransferEngineTests"`
Expected: build FAILS with `CS0246: The type or namespace name 'TransferEngine' could not be found` (and `TransferControl`, `TransferProgress`, `RateWindow`, `StopKind`, …).

- [ ] **Step 5: Implement control, progress and target helpers**

`plugins/db-migrate/engine/Dbm/Core/Transfer/TransferControl.cs`:

```csharp
namespace Dbm.Core.Transfer;

public enum StopKind { None, Pause, Cancel, Fail }

public sealed record ChunkCommit(string TaskId, string Target, int ChunkNo, long RowsDone, long RowsError);

/// <summary>Cooperative stop signal for one run segment; workers check it between chunks ("finish the current chunk").</summary>
public sealed class TransferControl
{
    private int _kind;

    public StopKind Kind => (StopKind)Volatile.Read(ref _kind);
    public bool StopRequested => Kind != StopKind.None;

    /// <summary>Raised synchronously on the worker right after a chunk (with its checkpoint) committed.</summary>
    public event Action<ChunkCommit>? ChunkCommitted;

    public void RequestPause() => Interlocked.CompareExchange(ref _kind, (int)StopKind.Pause, (int)StopKind.None);

    public void RequestCancel()
    {
        while (true)
        {
            int current = Volatile.Read(ref _kind);
            if (current == (int)StopKind.Fail || current == (int)StopKind.Cancel) return;
            if (Interlocked.CompareExchange(ref _kind, (int)StopKind.Cancel, current) == current) return;
        }
    }

    public void RequestFail() => Interlocked.Exchange(ref _kind, (int)StopKind.Fail);

    internal void OnChunkCommitted(ChunkCommit commit) => ChunkCommitted?.Invoke(commit);
}
```

`plugins/db-migrate/engine/Dbm/Core/Transfer/TransferProgress.cs`:

```csharp
using Dbm.Core.State;

namespace Dbm.Core.Transfer;

/// <summary>Rows/second over a sliding time window (always keeps the last two samples).</summary>
public sealed class RateWindow(TimeSpan window)
{
    private readonly Queue<(DateTimeOffset T, long V)> _samples = new();
    private (DateTimeOffset T, long V) _last;

    public void Add(DateTimeOffset t, long value)
    {
        _samples.Enqueue((t, value));
        _last = (t, value);
        while (_samples.Count > 2 && t - _samples.Peek().T > window) _samples.Dequeue();
    }

    public double PerSecond
    {
        get
        {
            if (_samples.Count < 2) return 0;
            var first = _samples.Peek();
            double seconds = (_last.T - first.T).TotalSeconds;
            return seconds <= 0 ? 0 : Math.Max(0, (_last.V - first.V) / seconds);
        }
    }
}

public sealed record ProgressTask(string TaskId, string Target, string Status, long RowsDone, long? RowsSource, long RowsError, double RowsPerSec);

public sealed record ProgressOverall(long Done, long Total, long RowsError, double RowsPerSec, double? EtaSec);

public sealed record ProgressSnapshot(long RunId, string Status, List<ProgressTask> Tasks, ProgressOverall Overall);

/// <summary>Live counters for the UI; publishes non-persisted "transfer_progress" events at most every 250 ms.</summary>
public sealed class TransferProgress
{
    public static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    private sealed class TaskState
    {
        public required string TaskId { get; init; }
        public required string Target { get; init; }
        public TransferTaskStatus Status { get; set; }
        public long Committed { get; set; }
        public long InFlight { get; set; }
        public long Errors { get; set; }
        public long? Source { get; set; }
        public RateWindow Rate { get; } = new(Window);
        public long Done => Committed + InFlight;
    }

    private readonly object _lock = new();
    private readonly List<TaskState> _tasks;
    private readonly RateWindow _overall = new(Window);
    private readonly long _runId;
    private readonly IEventSink _sink;
    private readonly Func<DateTimeOffset> _now;
    private DateTimeOffset _lastPublish = DateTimeOffset.MinValue;

    public TransferProgress(long runId, IEventSink sink, IEnumerable<TransferTaskRow> tasks, Func<DateTimeOffset>? now = null)
    {
        _runId = runId;
        _sink = sink;
        _now = now ?? (() => Clock.Now());
        _tasks = tasks.OrderBy(t => t.Ordinal).Select(t => new TaskState
        {
            TaskId = t.TaskId, Target = t.Target, Status = t.Status, Committed = t.RowsDone, Errors = t.RowsError, Source = t.RowsSource,
        }).ToList();
    }

    public string RunStatus { get; set; } = "running";

    public void SetStatus(string taskId, TransferTaskStatus status)
    {
        lock (_lock)
        {
            var t = Find(taskId);
            if (t is null) return;
            t.Status = status;
            if (status != TransferTaskStatus.Running) t.InFlight = 0;
        }
        Publish(force: true);
    }

    public void SetSource(string taskId, long? rowsSource)
    {
        lock (_lock)
        {
            var t = Find(taskId);
            if (t is not null) t.Source = rowsSource;
        }
    }

    public void Committed(string taskId, long rowsDone, long rowsError)
    {
        lock (_lock)
        {
            var t = Find(taskId);
            if (t is null) return;
            t.Committed = rowsDone;
            t.InFlight = 0;
            t.Errors = rowsError;
            Sample(t);
        }
        Publish(force: false);
    }

    public void InFlight(string taskId, long rowsInFlight)
    {
        lock (_lock)
        {
            var t = Find(taskId);
            if (t is null) return;
            t.InFlight = rowsInFlight;
            Sample(t);
        }
        Publish(force: false);
    }

    public ProgressSnapshot Snapshot()
    {
        lock (_lock)
        {
            var tasks = _tasks.Select(t => new ProgressTask(t.TaskId, t.Target, EnumText.ToText(t.Status), t.Done, t.Source, t.Errors,
                Math.Round(t.Status == TransferTaskStatus.Running ? t.Rate.PerSecond : 0, 1))).ToList();
            long total = _tasks.Sum(t => t.Source ?? 0);
            long done = _tasks.Sum(t => t.Done);
            long errors = _tasks.Sum(t => t.Errors);
            double rate = Math.Round(_overall.PerSecond, 1);
            long remaining = Math.Max(0, total - done - errors);
            double? eta = rate > 0 ? Math.Round(remaining / rate, 0) : null;
            return new ProgressSnapshot(_runId, RunStatus, tasks, new ProgressOverall(done, total, errors, rate, eta));
        }
    }

    public bool Publish(bool force)
    {
        ProgressSnapshot snapshot;
        lock (_lock)
        {
            var now = _now();
            if (!force && now - _lastPublish < Throttle) return false;
            _lastPublish = now;
            snapshot = Snapshot();
        }
        _sink.Publish("transfer_progress", snapshot, persist: false);
        return true;
    }

    private TaskState? Find(string taskId) => _tasks.FirstOrDefault(t => t.TaskId == taskId);

    private void Sample(TaskState t)
    {
        var now = _now();
        t.Rate.Add(now, t.Done);
        _overall.Add(now, _tasks.Sum(x => x.Done));
    }
}
```

`plugins/db-migrate/engine/Dbm/Core/Transfer/TargetOps.cs`:

```csharp
using System.Data;
using System.Globalization;
using Dbm.Core.Sql;
using Dbm.Core.SqlGen;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

public static class TargetOps
{
    /// <summary>Runs each non-empty statement as its own autocommit batch (PreSql/PostSql are session-independent).</summary>
    public static async Task ExecAllAsync(SqlConnection conn, IEnumerable<string> statements, CancellationToken ct)
    {
        foreach (var sql in statements)
        {
            if (string.IsNullOrWhiteSpace(sql)) continue;
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public static async Task<long> ScalarLongAsync(SqlConnection conn, string sql, CancellationToken ct, int timeoutSec = 0)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = timeoutSec };
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public static Task<long> CountTargetAsync(SqlConnection conn, string targetKey, CancellationToken ct)
        => ScalarLongAsync(conn, $"SELECT COUNT_BIG(*) FROM {SqlQuote.TableKey(targetKey)}", ct);

    public static string CountSqlOf(TaskPlan task)
        => string.IsNullOrWhiteSpace(task.CountSql)
            ? $"SELECT COUNT_BIG(*) FROM (\n{task.SourceQuery.Trim().TrimEnd(';')}\n) AS q"
            : task.CountSql;

    /// <summary>True when ANOTHER table has an FK to this one (V10: TRUNCATE would fail; self-references do not block it).</summary>
    public static async Task<bool> IsReferencedAsync(SqlConnection conn, string targetKey, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT COUNT(*) FROM sys.foreign_keys WHERE referenced_object_id = OBJECT_ID(@t) AND parent_object_id <> referenced_object_id", conn);
        cmd.Parameters.Add(new SqlParameter("@t", SqlDbType.NVarChar, 600) { Value = SqlQuote.TableKey(targetKey) });
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>Empties targets children-first (reverse load order). Returns log lines like "DELETE app.Parent".</summary>
    public static async Task<List<string>> TruncateAsync(SqlConnection conn, IReadOnlyList<string> targetsInLoadOrder, CancellationToken ct)
    {
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = targetsInLoadOrder.Count - 1; i >= 0; i--)
        {
            string target = targetsInLoadOrder[i];
            if (!seen.Add(target)) continue;
            bool referenced = await IsReferencedAsync(conn, target, ct);
            string table = SqlQuote.TableKey(target);
            await ExecAllAsync(conn, [referenced ? $"DELETE FROM {table};" : $"TRUNCATE TABLE {table};"], ct);
            lines.Add((referenced ? "DELETE " : "TRUNCATE ") + target);
        }
        return lines;
    }
}
```

- [ ] **Step 6: Implement the task runner and the engine**

`plugins/db-migrate/engine/Dbm/Core/Transfer/TaskRunner.cs`:

```csharp
using System.Data;
using Dbm.Core.Sql;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

public sealed record TaskResult(string TaskId, TransferTaskStatus Status, string? Error);

internal sealed class RunContext
{
    public required DbmServices Services { get; init; }
    public required long RunId { get; init; }
    public required SqlPlanPayload Plan { get; init; }
    public required string SourceCs { get; init; }
    public required string TargetCs { get; init; }
    public required TransferOptions Options { get; init; }
    public required TransferControl Control { get; init; }
    public required TransferProgress Progress { get; init; }
    public required IReadOnlyList<string> Secrets { get; init; }

    public TransferRepo Repo => Services.Transfers;

    public string Scrub(string text) => Redactor.Scrub(text, Secrets);

    public void Log(string level, string message, bool persist = true)
        => Services.Sink.Publish("log", new { level, message = Scrub(message) }, persist);

    public void SetTaskStatus(string taskId, TransferTaskStatus status, string? error = null)
    {
        Repo.UpdateTaskStatus(RunId, taskId, status, error);
        Services.Sink.Publish("transfer_task_changed", new { runId = RunId, taskId, status = EnumText.ToText(status) });
        Progress.SetStatus(taskId, status);
    }
}

/// <summary>Runs one task: checkpointed keyset chunks (one target transaction per chunk) or a single-transaction keyless load.</summary>
internal sealed class TaskRunner(RunContext rc)
{
    private sealed record ErrorRecord(string? Key, string Row, string Error);

    public async Task<TaskResult> RunAsync(TransferTaskRow row, CancellationToken ct)
    {
        string id = row.TaskId;
        var task = rc.Plan.Tasks[id];
        rc.SetTaskStatus(id, TransferTaskStatus.Running);
        try
        {
            await using var tgt = await SqlConnect.OpenAsync(rc.TargetCs, ct);
            var cp = await ControlTable.ReadAsync(tgt, rc.RunId, id, ct) ?? Checkpoint.Start;
            if (cp.Done)
            {
                Mirror(id, cp);   // committed before a crash; only the mirror and PostSql may be missing
            }
            else
            {
                await TargetOps.ExecAllAsync(tgt, task.PreSql, ct);
                var shape = await TargetShape.LoadAsync(tgt, task.Target, ct);
                var loader = new BulkLoader(task, rc.Options);
                var status = task.KeyColumns.Count > 0
                    ? await RunKeyedAsync(id, task, tgt, shape, loader, cp, ct)
                    : await RunKeylessAsync(id, task, tgt, shape, loader, ct);
                if (status == TransferTaskStatus.Paused)
                {
                    rc.SetTaskStatus(id, TransferTaskStatus.Paused);
                    return new TaskResult(id, TransferTaskStatus.Paused, null);
                }
            }
            await TargetOps.ExecAllAsync(tgt, task.PostSql, ct);
            rc.SetTaskStatus(id, TransferTaskStatus.Done);
            rc.Log("info", $"{id} {task.Target}: done");
            return new TaskResult(id, TransferTaskStatus.Done, null);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);   // crash semantics: the task stays 'running' until recovery
        }
        catch (Exception ex)
        {
            string msg = rc.Scrub(ex.Message);
            rc.SetTaskStatus(id, TransferTaskStatus.Failed, msg);
            rc.Log("error", $"{id} {task.Target}: {msg}");
            return new TaskResult(id, TransferTaskStatus.Failed, msg);
        }
    }

    private async Task<TransferTaskStatus> RunKeyedAsync(string id, TaskPlan task, SqlConnection tgt, TargetShape shape, BulkLoader loader,
        Checkpoint cp, CancellationToken ct)
    {
        int chunkSize = Math.Max(1, task.ChunkSize ?? rc.Options.ChunkSize);
        KeyValue? last = cp.LastKeyJson is null ? null : KeyCodec.Decode(cp.LastKeyJson);
        IReadOnlyList<KeyType>? types = last?.Types;
        await using var src = await SqlConnect.OpenAsync(rc.SourceCs, ct);
        while (true)
        {
            if (rc.Control.StopRequested) return TransferTaskStatus.Paused;

            DataTable table;
            await using (var cmd = ChunkPlanner.Command(src, task, last, chunkSize))
            await using (var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct))
            {
                types ??= KeyCodec.TypesOf(reader, task.KeyColumns);
                table = ChunkReader.NewTable(reader);
                await ChunkReader.FillAsync(reader, table, chunkSize, ct);
            }

            if (table.Rows.Count == 0)
            {
                cp = cp with { Done = true };
                await ControlTable.UpsertAsync(tgt, null, rc.RunId, id, cp, ct);
                Mirror(id, cp);
                return TransferTaskStatus.Done;
            }

            var newLast = KeyCodec.FromRow(table.Rows[table.Rows.Count - 1], task.KeyColumns, types);
            shape.Normalize(table, task.Columns);
            bool lastChunk = table.Rows.Count < chunkSize;
            ChunkOutcome outcome;
            Checkpoint next;
            await using (var scope = await TxScope.BeginAsync(tgt, ct))
            {
                outcome = await loader.LoadAsync(scope, table, allowRestart: true, n => rc.Progress.InFlight(id, n), ct);
                if (outcome.Failed.Count > 0 && !rc.Options.SkipErrors)
                {
                    await scope.RollbackAsync();
                    var first = Capture(task, table, [outcome.Failed[0]], types);
                    Write(id, first);
                    throw new TransferException("row_rejected", $"Row {first[0].Key} rejected: {outcome.Failed[0].Error}");
                }
                next = new Checkpoint(cp.ChunkNo + 1, KeyCodec.Encode(newLast), cp.RowsDone + outcome.Loaded,
                    cp.RowsError + outcome.Failed.Count, lastChunk);
                await ControlTable.UpsertAsync(scope.Connection, scope.Tx, rc.RunId, id, next, ct);
                await scope.CommitAsync(ct);
            }

            cp = next;
            last = newLast;
            Write(id, Capture(task, table, outcome.Failed, types));   // after commit: a rolled-back chunk never leaves error rows
            Mirror(id, cp);
            rc.Control.OnChunkCommitted(new ChunkCommit(id, task.Target, cp.ChunkNo, cp.RowsDone, cp.RowsError));
            rc.Log("info", $"{id} {task.Target}: chunk {cp.ChunkNo} committed ({cp.RowsDone:N0} rows, {cp.RowsError:N0} rejected)", persist: false);
            if (lastChunk) return TransferTaskStatus.Done;
        }
    }

    private async Task<TransferTaskStatus> RunKeylessAsync(string id, TaskPlan task, SqlConnection tgt, TargetShape shape, BulkLoader loader,
        CancellationToken ct)
    {
        int chunkSize = Math.Max(1, task.ChunkSize ?? rc.Options.ChunkSize);
        await using var src = await SqlConnect.OpenAsync(rc.SourceCs, ct);
        await using var cmd = new SqlCommand(task.SourceQuery, src) { CommandTimeout = 0 };
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        await using var scope = await TxScope.BeginAsync(tgt, ct);
        long loaded = 0, rejected = 0;
        int chunks = 0;
        var errors = new List<ErrorRecord>();
        while (true)
        {
            if (rc.Control.Kind is StopKind.Cancel or StopKind.Fail)
            {
                await scope.RollbackAsync();   // nothing of a keyless task is committed before it completes
                return TransferTaskStatus.Paused;
            }
            var table = ChunkReader.NewTable(reader);
            int n = await ChunkReader.FillAsync(reader, table, chunkSize, ct);
            if (n == 0) break;
            shape.Normalize(table, task.Columns);
            long before = loaded;
            var outcome = await loader.LoadAsync(scope, table, allowRestart: false, r => rc.Progress.InFlight(id, before + r), ct);
            if (outcome.Failed.Count > 0 && !rc.Options.SkipErrors)
            {
                await scope.RollbackAsync();
                var first = Capture(task, table, [outcome.Failed[0]], null);
                Write(id, first);
                throw new TransferException("row_rejected", $"A row was rejected: {outcome.Failed[0].Error}");
            }
            loaded += outcome.Loaded;
            rejected += outcome.Failed.Count;
            chunks++;
            errors.AddRange(Capture(task, table, outcome.Failed, null));
            rc.Progress.InFlight(id, loaded);
            if (n < chunkSize) break;
        }
        var done = new Checkpoint(chunks, null, loaded, rejected, true);
        await ControlTable.UpsertAsync(scope.Connection, scope.Tx, rc.RunId, id, done, ct);
        await scope.CommitAsync(ct);
        Write(id, errors);
        Mirror(id, done);
        rc.Control.OnChunkCommitted(new ChunkCommit(id, task.Target, done.ChunkNo, done.RowsDone, done.RowsError));
        return TransferTaskStatus.Done;
    }

    private void Mirror(string id, Checkpoint cp)
    {
        rc.Repo.UpdateTaskProgress(rc.RunId, id, cp.RowsDone, cp.RowsError, cp.LastKeyJson);
        rc.Progress.Committed(id, cp.RowsDone, cp.RowsError);
    }

    private static List<ErrorRecord> Capture(TaskPlan task, DataTable table, IEnumerable<RowFailure> failures, IReadOnlyList<KeyType>? types)
    {
        var list = new List<ErrorRecord>();
        foreach (var f in failures)
        {
            var row = table.Rows[f.Row];
            string? key = null;
            if (types is not null && task.KeyColumns.Count > 0)
            {
                try { key = KeyCodec.Display(KeyCodec.FromRow(row, task.KeyColumns, types), task.KeyColumns); }
                catch (TransferException) { key = null; }
            }
            list.Add(new ErrorRecord(key, RowSnapshot.Json(row, task.Columns), f.Error));
        }
        return list;
    }

    private void Write(string id, IEnumerable<ErrorRecord> records)
    {
        foreach (var e in records) rc.Repo.AddErrorRow(rc.RunId, id, e.Key, e.Row, rc.Scrub(e.Error));
    }
}
```

`plugins/db-migrate/engine/Dbm/Core/Transfer/TransferEngine.cs`:

```csharp
using Dbm.Core.Sql;
using Dbm.Core.SqlGen;
using Dbm.Core.State;

namespace Dbm.Core.Transfer;

public sealed record TransferOutcome(RunStatus Status, string? Error);

/// <summary>Runs (or resumes) one transfer run inside the server process (spec §9).</summary>
public sealed class TransferEngine(DbmServices services, SqlPlanPayload plan, string sourceCs, string targetCs)
{
    public static IReadOnlyList<string> PlanOrder(SqlPlanPayload plan)
    {
        var order = plan.Order.Where(plan.Tasks.ContainsKey).Distinct().ToList();
        foreach (var id in plan.Tasks.Keys.OrderBy(k => k, StringComparer.Ordinal))
            if (!order.Contains(id)) order.Add(id);
        return order;
    }

    public long CreateRun(int sqlVersion, TransferOptions options)
        => services.Transfers.CreateRun(sqlVersion, options.Normalized(),
            PlanOrder(plan).Select(id => (id, plan.Tasks[id].Target)).ToList());

    public async Task<TransferOutcome> RunAsync(long runId, TransferControl control, CancellationToken ct)
    {
        var repo = services.Transfers;
        var run = repo.GetRun(runId) ?? throw new TransferException("no_run", $"Transfer run {runId} does not exist.");
        if (run.Status is RunStatus.Completed or RunStatus.Cancelled)
            throw new TransferException("run_finished", $"Transfer run {runId} is already {EnumText.ToText(run.Status)}.");
        var tasks = repo.Tasks(runId);
        var missing = tasks.Where(t => !plan.Tasks.ContainsKey(t.TaskId)).Select(t => t.TaskId).ToList();
        if (missing.Count > 0) throw new TransferException("plan_mismatch", "The SQL plan has no task " + string.Join(", ", missing) + ".");

        var rc = new RunContext
        {
            Services = services, RunId = runId, Plan = plan, SourceCs = sourceCs, TargetCs = targetCs,
            Options = run.Options.Normalized(), Control = control,
            Progress = new TransferProgress(runId, services.Sink, tasks),
            Secrets = Redactor.SecretsOf(sourceCs).Concat(Redactor.SecretsOf(targetCs)).ToList(),
        };
        repo.SetRunStatus(runId, RunStatus.Running);
        services.Sink.Publish("transfer_run_changed", new { runId, status = EnumText.ToText(RunStatus.Running) });
        rc.Log("info", $"Transfer run {runId}: {tasks.Count} tasks, parallelism {rc.Options.Parallelism}, errors: {rc.Options.ErrorMode}.");
        try
        {
            await PrepareAsync(rc, tasks, ct);
            tasks = repo.Tasks(runId);
            foreach (var t in tasks) rc.Progress.SetSource(t.TaskId, t.RowsSource);

            string? error = await ScheduleAsync(rc, tasks, ct);
            if (error is not null) return Finish(rc, RunStatus.Failed, error);
            bool allDone = repo.Tasks(runId).All(t => t.Status == TransferTaskStatus.Done);
            if (!allDone && control.Kind == StopKind.Cancel)
            {
                await DropControlTableAsync(rc, ct);
                return Finish(rc, RunStatus.Cancelled, null);
            }
            if (!allDone) return Finish(rc, RunStatus.Paused, null);

            await using (var tgt = await SqlConnect.OpenAsync(targetCs, ct))
            {
                await TargetOps.ExecAllAsync(tgt, plan.PostSql, ct);
            }
            await DropControlTableAsync(rc, ct);
            string? summary = await FinishCompletedAsync(rc, ct);
            return Finish(rc, RunStatus.Completed, null, summary);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);   // crash semantics: run stays 'running'; RecoverInterrupted pauses it
        }
        catch (Exception ex)
        {
            return Finish(rc, RunStatus.Failed, rc.Scrub(ex.Message));
        }
    }

    private async Task PrepareAsync(RunContext rc, IReadOnlyList<TransferTaskRow> tasks, CancellationToken ct)
    {
        await using var tgt = await SqlConnect.OpenAsync(targetCs, ct);
        await ControlTable.EnsureAsync(tgt, ct);
        await TargetOps.ExecAllAsync(tgt, plan.PreSql, ct);   // every segment (idempotent); before truncation (V10)
        if (tasks.All(t => t.RowsBefore is not null)) return;   // setup finished in an earlier segment

        if (rc.Options.TruncateTarget)
            foreach (var line in await TargetOps.TruncateAsync(tgt, tasks.Select(t => t.Target).ToList(), ct))
                rc.Log("warn", line);
        await using var src = await SqlConnect.OpenAsync(sourceCs, ct);
        foreach (var t in tasks)
        {
            long before = await TargetOps.CountTargetAsync(tgt, t.Target, ct);
            long source = await TargetOps.ScalarLongAsync(src, TargetOps.CountSqlOf(plan.Tasks[t.TaskId]), ct);
            rc.Repo.SetTaskCounts(rc.RunId, t.TaskId, source, before);
        }
    }

    private static async Task<string?> ScheduleAsync(RunContext rc, IReadOnlyList<TransferTaskRow> tasks, CancellationToken ct)
    {
        var runner = new TaskRunner(rc);
        var done = new HashSet<string>(tasks.Where(t => t.Status == TransferTaskStatus.Done).Select(t => t.TaskId));
        var remaining = tasks.Where(t => t.Status != TransferTaskStatus.Done).OrderBy(t => t.Ordinal).ToList();
        var running = new Dictionary<Task<TaskResult>, string>();
        int parallel = Math.Max(1, rc.Options.Parallelism);
        string? firstError = null;
        try
        {
            while (true)
            {
                if (!rc.Control.StopRequested)
                {
                    foreach (var t in remaining.ToList())
                    {
                        if (running.Count >= parallel) break;
                        bool ready = rc.Plan.Tasks[t.TaskId].DependsOn
                            .Where(d => d != t.TaskId && rc.Plan.Tasks.ContainsKey(d))
                            .All(done.Contains);
                        if (!ready) continue;
                        remaining.Remove(t);
                        running.Add(Task.Run(() => runner.RunAsync(t, ct), CancellationToken.None), t.TaskId);
                    }
                }
                if (running.Count == 0) break;
                var finished = await Task.WhenAny(running.Keys);
                running.Remove(finished);
                var result = await finished;
                if (result.Status == TransferTaskStatus.Done) done.Add(result.TaskId);
                else if (result.Status == TransferTaskStatus.Failed)
                {
                    firstError ??= $"{result.TaskId}: {result.Error}";
                    rc.Control.RequestFail();
                }
            }
        }
        catch
        {
            try { await Task.WhenAll(running.Keys); }
            catch (Exception) { /* the other workers observe the same token */ }
            throw;
        }
        if (firstError is null && !rc.Control.StopRequested && remaining.Count > 0)
            firstError = "Unsatisfiable task dependencies: " + string.Join(", ", remaining.Select(t => t.TaskId));
        return firstError;
    }

    private async Task DropControlTableAsync(RunContext rc, CancellationToken ct)
    {
        if (rc.Options.KeepControlTable) return;
        await using var tgt = await SqlConnect.OpenAsync(targetCs, ct);
        await ControlTable.DropAsync(tgt, ct);
    }

    /// <summary>Completion hook: Task 5.4 replaces this body with run validation + the final report. Returns summary_json.</summary>
    private Task<string?> FinishCompletedAsync(RunContext rc, CancellationToken ct) => Task.FromResult<string?>(null);

    private TransferOutcome Finish(RunContext rc, RunStatus status, string? error, string? summaryJson = null)
    {
        string text = EnumText.ToText(status);
        rc.Repo.SetRunStatus(rc.RunId, status, summaryJson ?? (error is null ? null : Json.Serialize(new { error })));
        services.Sink.Publish("transfer_run_changed", new { runId = rc.RunId, status = text });
        rc.Progress.RunStatus = text;
        rc.Progress.Publish(force: true);
        rc.Log(status == RunStatus.Failed ? "error" : "info",
            error is null ? $"Transfer run {rc.RunId} {text}." : $"Transfer run {rc.RunId} failed: {error}");
        return new TransferOutcome(status, error);
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~TransferProgressTests|FullyQualifiedName~TransferEngineTests"`
Expected: PASS — 4 TransferProgressTests, 6 TransferEngineTests. Then the full transfer suite: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~Transfer"` → PASS.

- [ ] **Step 8: Commit**

```bash
git add plugins/db-migrate/engine/Dbm/Core/Transfer plugins/db-migrate/engine/Dbm.Tests/Support/XferServices.cs plugins/db-migrate/engine/Dbm.Tests/Unit/Transfer plugins/db-migrate/engine/Dbm.Tests/Integration/Transfer
git commit -F - <<'EOF'
feat(transfer): engine with dependency scheduling, pause/resume/cancel and crash recovery

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49
EOF
```

---

### Task 5.4: Preflight, run validation, final report

**Files:**
- Create: `plugins/db-migrate/engine/Dbm/Core/Transfer/Preflight.cs`
- Create: `plugins/db-migrate/engine/Dbm/Core/Transfer/RunValidator.cs`
- Create: `plugins/db-migrate/engine/Dbm/Core/Transfer/FinalReport.cs`
- Modify: `plugins/db-migrate/engine/Dbm/Core/Transfer/TransferEngine.cs` (completion hook)
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/Transfer/ReportingTests.cs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Integration/Transfer/RunValidatorTests.cs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Integration/Transfer/PreflightTests.cs`

**Interfaces:**
- Consumes: `DbmServices` (`Phases`, `Artifacts`, `Connections`), `PhaseName`/`PhaseStatus`/`Side` (C2), `DriftChecker.CheckAsync` (C10), `SqlConnect`, `Redactor`, `SqlQuote` (C3), T5.1–T5.3 types.
- Produces:
```csharp
namespace Dbm.Core.Transfer;
public sealed record PreflightCheck(string Name, bool Ok, string Severity, string Detail);        // Severity: "error" | "warning" | "info"
public sealed record PreflightResult(int SqlVersion, DateTimeOffset At, List<PreflightCheck> Checks) { public bool Passed { get; } }   // no check with Ok=false && Severity="error"
public sealed record ApprovedPlan(int Version, SqlPlanPayload Plan);
public static class Preflight
{
    public static ApprovedPlan? LoadApprovedPlan(DbmServices s);     // Sql phase approved + its approved artifact
    public static Task<PreflightResult> RunAsync(DbmServices s, TransferOptions options, CancellationToken ct);
    public static PreflightCheck PlanCheck(SqlPlanPayload plan);
    public static Task<List<PreflightCheck>> TargetChecksAsync(SqlConnection tgt, SqlPlanPayload plan, TransferOptions options, CancellationToken ct);
    public static Task<PreflightCheck> SourceEstimateAsync(SqlConnection src, SqlPlanPayload plan, CancellationToken ct);
}
public sealed record ChecksumResult(string Column, bool Match, long? Source, long? Target);
public sealed record TaskValidation(bool CountMatch, long RowsSource, long RowsError, long RowsBefore, long RowsAfter, List<ChecksumResult> Checksums, string? ChecksumsSkipped);
public sealed record ChecksumColumn(string Source, string Target, string TypeText);
public static class RunValidator
{
    public static readonly IReadOnlySet<string> ChecksumTypes;       // system scalar types only (text/ntext/image/xml/spatial/hierarchyid/sql_variant/rowversion/UDTs excluded)
    public static Task<TaskValidation> ValidateTaskAsync(SqlConnection src, SqlConnection tgt, TaskPlan task, TransferTaskRow row, bool checksums, CancellationToken ct);
    public static List<ChecksumColumn> ChecksumColumns(TaskPlan task, TargetShape shape);
    public static string SourceChecksumSql(TaskPlan task, IReadOnlyList<ChecksumColumn> cols);
    public static string TargetChecksumSql(string targetKey, IReadOnlyList<ChecksumColumn> cols);
}
public sealed record ErrorSample(string? Key, string Error);
public sealed record TaskReport(string TaskId, string Target, TransferTaskStatus Status, long RowsSource, long RowsLoaded, long RowsError,
    double DurationSec, bool? CountMatch, List<ChecksumResult> Checksums, string? ChecksumsSkipped, List<ErrorSample> ErrorSamples, string? Error);
public sealed record FinalReport(long RunId, RunStatus Status, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, double DurationSec,
    long RowsSource, long RowsLoaded, long RowsError, double RowsPerSec, List<TaskReport> Tasks, List<string> Notes, TransferOptions Options);
public static class FinalReportBuilder
{
    public const int MaxErrorSamples = 5;
    public static FinalReport Build(TransferRunRow run, RunStatus status, IReadOnlyList<TransferTaskRow> tasks, Func<string, IReadOnlyList<ErrorRowEntry>> errorRows, DateTimeOffset endedAt);
    public static string Summary(FinalReport report);   // one line, used as the Complete artifact summary
    public static string Dur(double seconds);           // "42s" | "3m 05s" | "1h 02m"
}
```
Validation rules: count check `rowsSource − rowsError == rowsAfter − rowsBefore`; checksums only when `ValidateChecksums && rowsBefore == 0 && rowsError == 0`, per bound column of a checksum-able type: source `SUM(CAST(BINARY_CHECKSUM(CAST(q.[src] AS <target type>)) AS bigint))` over the SourceQuery vs target `SUM(CAST(BINARY_CHECKSUM([col]) AS bigint))` (V9; exact because of V8 normalisation). Validation never fails the run; mismatches are reported.

- [ ] **Step 1: Write the failing unit tests**

`plugins/db-migrate/engine/Dbm.Tests/Unit/Transfer/ReportingTests.cs`:

```csharp
using Dbm.Core;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Dbm.Core.Transfer;
using Xunit;

namespace Dbm.Tests.Unit.Transfer;

public sealed class ReportingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    private static TransferTaskRow Row(string id, string target, long source, long done, long errors, TaskValidation? v, int seconds) =>
        new(1, id, target, int.Parse(id[1..]), TransferTaskStatus.Done, source, 0, done, errors, null, null, T0, T0.AddSeconds(seconds),
            null, v is null ? null : Json.Serialize(v));

    [Fact]
    public void Build_aggregates_tasks_validation_samples_and_notes()
    {
        var run = new TransferRunRow(1, 3, RunStatus.Completed, new TransferOptions { ErrorMode = "skip" }, T0, null, null);
        var tasks = new List<TransferTaskRow>
        {
            Row("T01", "app.Customers", 1000, 1000, 0, new TaskValidation(true, 1000, 0, 0, 1000, [new ChecksumResult("Email", true, 5, 5)], null), 4),
            Row("T02", "app.Orders", 3005, 3000, 5, new TaskValidation(true, 3005, 5, 0, 3000, [], "rows were rejected"), 6),
        };
        var samples = Enumerable.Range(0, 7).Select(i => new ErrorRowEntry(i, 1, "T02", $"{{\"__k0\":{i}}}", "{}", $"bad {i}", T0)).ToList();
        var report = FinalReportBuilder.Build(run, RunStatus.Completed, tasks, id => id == "T02" ? samples : [], T0.AddSeconds(10));

        Assert.Equal(RunStatus.Completed, report.Status);
        Assert.Equal(10, report.DurationSec);
        Assert.Equal(4005, report.RowsSource);
        Assert.Equal(4000, report.RowsLoaded);
        Assert.Equal(5, report.RowsError);
        Assert.Equal(400, report.RowsPerSec);
        Assert.Equal(4, report.Tasks[0].DurationSec);
        Assert.Equal(FinalReportBuilder.MaxErrorSamples, report.Tasks[1].ErrorSamples.Count);
        Assert.Equal("{\"__k0\":0}", report.Tasks[1].ErrorSamples[0].Key);
        Assert.True(report.Tasks[0].CountMatch);
        Assert.True(report.Tasks[0].Checksums.Single().Match);
        Assert.Equal("rows were rejected", report.Tasks[1].ChecksumsSkipped);
        Assert.Contains(report.Notes, n => n.Contains("5 rows were rejected"));
        Assert.Contains(report.Notes, n => n.Contains("Row counts validated for all 2 tasks"));
        Assert.Contains(report.Notes, n => n.Contains("Checksums skipped for app.Orders: rows were rejected"));

        string json = Json.Serialize(report);
        Assert.Contains("\"status\":\"completed\"", json);
        Assert.Contains("\"errorSamples\":[", json);
        Assert.Equal("Transferred 4,000 of 4,005 rows into 2 tables in 10s (5 rejected); row counts validated, checksums 1/1 matched.",
            FinalReportBuilder.Summary(report));
    }

    [Fact]
    public void Mismatches_are_called_out_in_notes_and_summary()
    {
        var run = new TransferRunRow(2, 3, RunStatus.Completed, new TransferOptions(), T0, null, null);
        var tasks = new List<TransferTaskRow>
        {
            Row("T01", "app.A", 10, 9, 0, new TaskValidation(false, 10, 0, 0, 9, [new ChecksumResult("X", false, 1, 2)], null), 1),
        };
        var report = FinalReportBuilder.Build(run, RunStatus.Completed, tasks, _ => [], T0.AddSeconds(1));
        Assert.Contains(report.Notes, n => n.Contains("Row counts do not match for: app.A"));
        Assert.Contains(report.Notes, n => n.Contains("app.A.X"));
        Assert.Contains("into 1 table in 1s; row counts MISMATCH, checksums 0/1 matched.", FinalReportBuilder.Summary(report));
    }

    [Theory]
    [InlineData(42, "42s")]
    [InlineData(185, "3m 05s")]
    [InlineData(3720, "1h 02m")]
    public void Durations_are_compact(double seconds, string expected) => Assert.Equal(expected, FinalReportBuilder.Dur(seconds));

    [Fact]
    public void Preflight_passes_only_without_failed_error_checks()
    {
        var ok = new PreflightResult(3, T0, [new("a", true, "info", ""), new("b", false, "warning", "rows present")]);
        Assert.True(ok.Passed);
        var bad = ok with { Checks = [.. ok.Checks, new("c", false, "error", "no insert")] };
        Assert.False(bad.Passed);
    }

    [Fact]
    public void PlanCheck_flags_tasks_with_validation_errors()
    {
        var plan = new SqlPlanPayload
        {
            Order = ["T01"],
            Tasks = new() { ["T01"] = new TaskPlan { Target = "app.A", SourceQuery = "SELECT 1 AS [X]", Errors = ["bad column"] } },
        };
        var check = Preflight.PlanCheck(plan);
        Assert.False(check.Ok);
        Assert.Equal("error", check.Severity);
        Assert.Contains("T01", check.Detail);
        plan.Tasks["T01"].Errors.Clear();
        Assert.True(Preflight.PlanCheck(plan).Ok);
    }

    [Fact]
    public void Checksum_sql_casts_source_values_to_the_target_types_and_skips_unsupported_columns()
    {
        var task = new TaskPlan
        {
            Target = "app.Customers",
            SourceQuery = "SELECT s.[E] AS [Email] FROM [dbo].[CUST] AS s;",
            Columns = [new("Email", "Email"), new("Notes", "Notes"), new("DisplayName", "DisplayName")],
        };
        var shape = new TargetShape("app.Customers", [
            new TargetColumn("Email", "nvarchar", 240, 0, 0, false, false),
            new TargetColumn("Notes", "ntext", 16, 0, 0, false, false),
            new TargetColumn("DisplayName", "nvarchar", 202, 0, 0, false, true),
        ]);
        var cols = RunValidator.ChecksumColumns(task, shape);
        Assert.Equal("Email", cols.Single().Target);
        Assert.Equal("SELECT SUM(CAST(BINARY_CHECKSUM(CAST(q.[Email] AS nvarchar(120))) AS bigint)) AS [c0] FROM (\nSELECT s.[E] AS [Email] FROM [dbo].[CUST] AS s\n) AS q",
            RunValidator.SourceChecksumSql(task, cols));
        Assert.Equal("SELECT SUM(CAST(BINARY_CHECKSUM([Email]) AS bigint)) AS [c0] FROM [app].[Customers]",
            RunValidator.TargetChecksumSql("app.Customers", cols));
    }
}
```

- [ ] **Step 2: Write the failing integration tests**

`plugins/db-migrate/engine/Dbm.Tests/Integration/Transfer/RunValidatorTests.cs`:

```csharp
using Dbm.Core;
using Dbm.Core.Transfer;
using Dbm.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Dbm.Tests.Integration.Transfer;

[Trait("Category", "Integration")]
public sealed class RunValidatorTests(EngineSourceFixture fx) : IClassFixture<EngineSourceFixture>
{
    [Fact]
    public async Task Completed_run_stores_validation_and_a_final_report()
    {
        await using var tgt = await TempDatabase.CreateAsync("dbm_val_tgt");
        await tgt.ExecAsync(TransferEngineTests.TargetSchema);
        using var svc = new XferServices();
        var engine = new TransferEngine(svc.Services, TransferEngineTests.Plan(), fx.Src.ConnectionString, tgt.ConnectionString);
        long runId = engine.CreateRun(1, new TransferOptions { ChunkSize = 500, ErrorMode = "skip" });
        Assert.Equal(RunStatus.Completed, (await engine.RunAsync(runId, new TransferControl(), default)).Status);

        var v = svc.Services.Transfers.Tasks(runId).ToDictionary(t => t.TaskId, t => Json.Deserialize<TaskValidation>(t.ValidationJson!));
        Assert.All(v.Values, x => Assert.True(x.CountMatch));
        Assert.Equal(new[] { "Id", "Name" }, v["T01"].Checksums.Select(c => c.Column));
        Assert.All(v["T01"].Checksums, c => Assert.True(c.Match));
        Assert.Single(v["T03"].Checksums);
        Assert.True(v["T03"].Checksums[0].Match);
        Assert.Empty(v["T02"].Checksums);
        Assert.Equal("rows were rejected", v["T02"].ChecksumsSkipped);
        Assert.Equal(1998, v["T02"].RowsAfter);

        var report = Json.Deserialize<FinalReport>(svc.Services.Transfers.GetRun(runId)!.SummaryJson!);
        Assert.Equal(RunStatus.Completed, report.Status);
        Assert.Equal(2, report.RowsError);
        Assert.Equal(2998, report.RowsLoaded);
        Assert.Equal(2, report.Tasks.Single(t => t.TaskId == "T02").ErrorSamples.Count);
    }

    [Fact]
    public async Task Checksums_catch_a_changed_value_and_counts_catch_an_extra_row()
    {
        await using var tgt = await TempDatabase.CreateAsync("dbm_val_tgt");
        await tgt.ExecAsync(TransferEngineTests.TargetSchema);
        using var svc = new XferServices();
        var plan = TransferEngineTests.Plan();
        plan.Tasks.Remove("T02");
        plan.Order.Remove("T02");
        var engine = new TransferEngine(svc.Services, plan, fx.Src.ConnectionString, tgt.ConnectionString);
        long runId = engine.CreateRun(1, new TransferOptions { ErrorMode = "skip" });
        Assert.Equal(RunStatus.Completed, (await engine.RunAsync(runId, new TransferControl(), default)).Status);

        await tgt.ExecAsync("UPDATE app.Parent SET Name = N'changed' WHERE Id = 7; INSERT app.Log (Msg) VALUES (N'extra');");
        await using var src = new SqlConnection(fx.Src.ConnectionString);
        await using var dst = new SqlConnection(tgt.ConnectionString);
        await src.OpenAsync();
        await dst.OpenAsync();
        var rows = svc.Services.Transfers.Tasks(runId).ToDictionary(t => t.TaskId);

        var parent = await RunValidator.ValidateTaskAsync(src, dst, plan.Tasks["T01"], rows["T01"], true, default);
        Assert.True(parent.CountMatch);
        Assert.True(parent.Checksums.Single(c => c.Column == "Id").Match);
        Assert.False(parent.Checksums.Single(c => c.Column == "Name").Match);

        var log = await RunValidator.ValidateTaskAsync(src, dst, plan.Tasks["T03"], rows["T03"], true, default);
        Assert.False(log.CountMatch);
        Assert.Equal(701, log.RowsAfter);

        var nonEmpty = await RunValidator.ValidateTaskAsync(src, dst, plan.Tasks["T01"], rows["T01"] with { RowsBefore = 5 }, true, default);
        Assert.Equal("target table was not empty before the run", nonEmpty.ChecksumsSkipped);
        Assert.Empty(nonEmpty.Checksums);
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Integration/Transfer/PreflightTests.cs`:

```csharp
using Dbm.Core.SqlGen;
using Dbm.Core.Transfer;
using Dbm.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Dbm.Tests.Integration.Transfer;

[Trait("Category", "Integration")]
public sealed class PreflightTests(EngineSourceFixture fx) : IClassFixture<EngineSourceFixture>
{
    private static PreflightCheck Check(IEnumerable<PreflightCheck> checks, string name) => checks.Single(c => c.Name == name);

    private static SqlPlanPayload PlanWithMissingTable()
    {
        var plan = TransferEngineTests.Plan();
        plan.Tasks["T01"].IdentityInsert = true;
        plan.Tasks["T04"] = new TaskPlan { Target = "app.Missing", SourceQuery = "SELECT 1 AS [X]", Columns = [new("X", "X")] };
        plan.Order.Add("T04");
        return plan;
    }

    [Fact]
    public async Task Target_checks_report_missing_tables_existing_rows_and_permissions()
    {
        await using var tgt = await TempDatabase.CreateAsync("dbm_pre");
        await tgt.ExecAsync(TransferEngineTests.TargetSchema);
        await tgt.ExecAsync("INSERT app.Parent (Id, Name) VALUES (1, N'x');");
        await using var conn = new SqlConnection(tgt.ConnectionString);
        await conn.OpenAsync();
        var plan = PlanWithMissingTable();

        var checks = await Preflight.TargetChecksAsync(conn, plan, new TransferOptions(), default);
        Assert.False(Check(checks, "target_tables").Ok);
        Assert.Equal("error", Check(checks, "target_tables").Severity);
        Assert.Contains("app.Missing", Check(checks, "target_tables").Detail);
        Assert.True(Check(checks, "insert_permission").Ok);
        Assert.True(Check(checks, "identity_insert_permission").Ok);
        Assert.True(Check(checks, "control_table").Ok);
        var rows = Check(checks, "target_rows");
        Assert.False(rows.Ok);
        Assert.Equal("warning", rows.Severity);
        Assert.Contains("app.Parent (1)", rows.Detail);
        Assert.DoesNotContain(checks, c => c.Name == "truncate_permission");

        var truncating = await Preflight.TargetChecksAsync(conn, plan, new TransferOptions { TruncateTarget = true }, default);
        Assert.True(Check(truncating, "target_rows").Ok);
        Assert.Contains("emptied", Check(truncating, "target_rows").Detail);
        Assert.True(Check(truncating, "truncate_permission").Ok);
    }

    [Fact]
    public async Task Missing_permissions_are_errors()
    {
        await using var tgt = await TempDatabase.CreateAsync("dbm_pre");
        await tgt.ExecAsync(TransferEngineTests.TargetSchema);
        await tgt.ExecAsync("CREATE USER limited WITHOUT LOGIN; GRANT SELECT ON SCHEMA::app TO limited;");
        await using var conn = new SqlConnection(tgt.ConnectionString);
        await conn.OpenAsync();
        await using (var cmd = new SqlCommand("EXECUTE AS USER = 'limited';", conn)) await cmd.ExecuteNonQueryAsync();

        var checks = await Preflight.TargetChecksAsync(conn, TransferEngineTests.Plan(), new TransferOptions(), default);
        Assert.False(Check(checks, "insert_permission").Ok);
        Assert.Contains("app.Parent", Check(checks, "insert_permission").Detail);
        Assert.False(Check(checks, "control_table").Ok);
        await using (var cmd = new SqlCommand("REVERT;", conn)) await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Source_estimate_counts_rows_per_task()
    {
        await using var conn = new SqlConnection(fx.Src.ConnectionString);
        await conn.OpenAsync();
        var check = await Preflight.SourceEstimateAsync(conn, TransferEngineTests.Plan(), default);
        Assert.True(check.Ok);
        Assert.Contains("3,000 rows across 3 tasks", check.Detail);
        Assert.Contains("largest: app.Child 2,000", check.Detail);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~ReportingTests|FullyQualifiedName~RunValidatorTests|FullyQualifiedName~PreflightTests"`
Expected: build FAILS with `CS0246: The type or namespace name 'FinalReportBuilder' could not be found` (and `Preflight`, `RunValidator`, `TaskValidation`, `PreflightResult`, …).

- [ ] **Step 4: Implement the validator and the final report**

`plugins/db-migrate/engine/Dbm/Core/Transfer/RunValidator.cs`:

```csharp
using Dbm.Core.Sql;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

public sealed record ChecksumResult(string Column, bool Match, long? Source, long? Target);

public sealed record TaskValidation(bool CountMatch, long RowsSource, long RowsError, long RowsBefore, long RowsAfter,
    List<ChecksumResult> Checksums, string? ChecksumsSkipped);

public sealed record ChecksumColumn(string Source, string Target, string TypeText);

/// <summary>Post-run checks (spec §9 Validation): counts always, column checksums when the target started empty and nothing was rejected.</summary>
public static class RunValidator
{
    public static readonly IReadOnlySet<string> ChecksumTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "bigint", "int", "smallint", "tinyint", "bit", "decimal", "numeric", "money", "smallmoney", "float", "real",
        "date", "datetime", "datetime2", "smalldatetime", "datetimeoffset", "time",
        "char", "varchar", "nchar", "nvarchar", "binary", "varbinary", "uniqueidentifier",
    };

    public static async Task<TaskValidation> ValidateTaskAsync(SqlConnection src, SqlConnection tgt, TaskPlan task, TransferTaskRow row,
        bool checksums, CancellationToken ct)
    {
        long after = await TargetOps.CountTargetAsync(tgt, task.Target, ct);
        long source = row.RowsSource ?? await TargetOps.ScalarLongAsync(src, TargetOps.CountSqlOf(task), ct);
        long before = row.RowsBefore ?? 0;
        bool countMatch = source - row.RowsError == after - before;
        var results = new List<ChecksumResult>();
        string? skipped = !checksums ? "disabled"
            : before != 0 ? "target table was not empty before the run"
            : row.RowsError != 0 ? "rows were rejected"
            : null;
        if (skipped is null)
        {
            var cols = ChecksumColumns(task, await TargetShape.LoadAsync(tgt, task.Target, ct));
            if (cols.Count == 0) skipped = "no comparable columns";
            else
            {
                try
                {
                    var s = await SumsAsync(src, SourceChecksumSql(task, cols), cols.Count, ct);
                    var t = await SumsAsync(tgt, TargetChecksumSql(task.Target, cols), cols.Count, ct);
                    for (int i = 0; i < cols.Count; i++) results.Add(new ChecksumResult(cols[i].Target, s[i] == t[i], s[i], t[i]));
                }
                catch (SqlException ex)
                {
                    results.Clear();
                    skipped = "checksum query failed: " + ex.Message;
                }
            }
        }
        return new TaskValidation(countMatch, source, row.RowsError, before, after, results, skipped);
    }

    public static List<ChecksumColumn> ChecksumColumns(TaskPlan task, TargetShape shape)
    {
        var list = new List<ChecksumColumn>();
        foreach (var b in task.Columns)
        {
            var c = shape.Find(b.Target);
            if (c is null || c.IsComputed || !ChecksumTypes.Contains(c.DataType)) continue;
            list.Add(new ChecksumColumn(b.Source, c.Name, c.TypeText));
        }
        return list;
    }

    public static string SourceChecksumSql(TaskPlan task, IReadOnlyList<ChecksumColumn> cols)
        => "SELECT " + string.Join(", ", cols.Select((c, i) =>
               $"SUM(CAST(BINARY_CHECKSUM(CAST(q.{SqlQuote.Ident(c.Source)} AS {c.TypeText})) AS bigint)) AS [c{i}]"))
           + $" FROM (\n{task.SourceQuery.Trim().TrimEnd(';').TrimEnd()}\n) AS q";

    public static string TargetChecksumSql(string targetKey, IReadOnlyList<ChecksumColumn> cols)
        => "SELECT " + string.Join(", ", cols.Select((c, i) => $"SUM(CAST(BINARY_CHECKSUM({SqlQuote.Ident(c.Target)}) AS bigint)) AS [c{i}]"))
           + $" FROM {SqlQuote.TableKey(targetKey)}";

    private static async Task<long?[]> SumsAsync(SqlConnection conn, string sql, int count, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var result = new long?[count];
        if (await r.ReadAsync(ct))
            for (int i = 0; i < count; i++) result[i] = r.IsDBNull(i) ? null : r.GetInt64(i);
        return result;
    }
}
```

`plugins/db-migrate/engine/Dbm/Core/Transfer/FinalReport.cs`:

```csharp
using System.Globalization;
using Dbm.Core.State;

namespace Dbm.Core.Transfer;

public sealed record ErrorSample(string? Key, string Error);

public sealed record TaskReport(string TaskId, string Target, TransferTaskStatus Status, long RowsSource, long RowsLoaded, long RowsError,
    double DurationSec, bool? CountMatch, List<ChecksumResult> Checksums, string? ChecksumsSkipped, List<ErrorSample> ErrorSamples, string? Error);

public sealed record FinalReport(long RunId, RunStatus Status, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, double DurationSec,
    long RowsSource, long RowsLoaded, long RowsError, double RowsPerSec, List<TaskReport> Tasks, List<string> Notes, TransferOptions Options);

public static class FinalReportBuilder
{
    public const int MaxErrorSamples = 5;

    public static FinalReport Build(TransferRunRow run, RunStatus status, IReadOnlyList<TransferTaskRow> tasks,
        Func<string, IReadOnlyList<ErrorRowEntry>> errorRows, DateTimeOffset endedAt)
    {
        var reports = tasks.OrderBy(t => t.Ordinal).Select(t =>
        {
            var v = t.ValidationJson is null ? null : Json.Deserialize<TaskValidation>(t.ValidationJson);
            double duration = t.StartedAt is { } s && t.EndedAt is { } e ? Math.Max(0, (e - s).TotalSeconds) : 0;
            var samples = errorRows(t.TaskId).Take(MaxErrorSamples).Select(r => new ErrorSample(r.KeyJson, r.Error)).ToList();
            return new TaskReport(t.TaskId, t.Target, t.Status, t.RowsSource ?? 0, t.RowsDone, t.RowsError, Math.Round(duration, 1),
                v?.CountMatch, v?.Checksums ?? [], v?.ChecksumsSkipped, samples, t.Error);
        }).ToList();
        double total = run.StartedAt is { } started ? Math.Max(0, (endedAt - started).TotalSeconds) : 0;
        long loaded = reports.Sum(r => r.RowsLoaded);
        return new FinalReport(run.Id, status, run.StartedAt, endedAt, Math.Round(total, 1), reports.Sum(r => r.RowsSource), loaded,
            reports.Sum(r => r.RowsError), total > 0 ? Math.Round(loaded / total, 1) : 0, reports, Notes(reports, run.Options), run.Options);
    }

    public static string Summary(FinalReport r)
    {
        int tables = r.Tasks.Select(t => t.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        bool countsOk = r.Tasks.All(t => t.CountMatch != false);
        int sums = r.Tasks.Sum(t => t.Checksums.Count);
        int matched = r.Tasks.Sum(t => t.Checksums.Count(c => c.Match));
        string rejected = r.RowsError > 0 ? $" ({N(r.RowsError)} rejected)" : "";
        string checks = sums > 0 ? $", checksums {matched}/{sums} matched" : "";
        return $"Transferred {N(r.RowsLoaded)} of {N(r.RowsSource)} rows into {tables} {(tables == 1 ? "table" : "tables")} in {Dur(r.DurationSec)}{rejected}; " +
               $"row counts {(countsOk ? "validated" : "MISMATCH")}{checks}.";
    }

    public static string Dur(double seconds)
    {
        long s = (long)Math.Round(Math.Max(0, seconds));
        return s < 60 ? $"{s}s" : s < 3600 ? $"{s / 60}m {s % 60:00}s" : $"{s / 3600}h {s % 3600 / 60:00}m";
    }

    private static List<string> Notes(List<TaskReport> tasks, TransferOptions options)
    {
        var notes = new List<string>();
        long rejected = tasks.Sum(t => t.RowsError);
        if (rejected > 0) notes.Add($"{N(rejected)} rows were rejected and logged; see the error samples per task.");
        var mismatch = tasks.Where(t => t.CountMatch == false).Select(t => t.Target).ToList();
        if (mismatch.Count > 0) notes.Add("Row counts do not match for: " + string.Join(", ", mismatch) + ".");
        else if (tasks.Count > 0 && tasks.All(t => t.CountMatch == true)) notes.Add($"Row counts validated for all {tasks.Count} tasks.");
        var badSums = tasks.SelectMany(t => t.Checksums.Where(c => !c.Match).Select(c => $"{t.Target}.{c.Column}")).ToList();
        if (badSums.Count > 0) notes.Add("Column checksums differ for: " + string.Join(", ", badSums) + ".");
        foreach (var t in tasks.Where(t => t.ChecksumsSkipped is not null && t.ChecksumsSkipped != "disabled"))
            notes.Add($"Checksums skipped for {t.Target}: {t.ChecksumsSkipped}.");
        if (!options.ValidateChecksums) notes.Add("Column checksums were disabled for this run.");
        if (options.TruncateTarget) notes.Add("Target tables were emptied before loading (truncate target first).");
        if (options.KeepControlTable) notes.Add("The checkpoint table dbo.__dbm_checkpoint was kept in the target.");
        return notes;
    }

    private static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
```

- [ ] **Step 5: Implement preflight**

`plugins/db-migrate/engine/Dbm/Core/Transfer/Preflight.cs`:

```csharp
using System.Data;
using System.Globalization;
using Dbm.Core.Catalog;
using Dbm.Core.Sql;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

public sealed record PreflightCheck(string Name, bool Ok, string Severity, string Detail);

public sealed record PreflightResult(int SqlVersion, DateTimeOffset At, List<PreflightCheck> Checks)
{
    public bool Passed => Checks.All(c => c.Ok || c.Severity != "error");
}

public sealed record ApprovedPlan(int Version, SqlPlanPayload Plan);

/// <summary>Execute-screen checklist (spec §8): connectivity, drift, approved SQL, permissions, target rows, volume.</summary>
public static class Preflight
{
    public static ApprovedPlan? LoadApprovedPlan(DbmServices s)
    {
        var phase = s.Phases.Get(PhaseName.Sql);
        if (phase.Status != PhaseStatus.Approved || phase.ApprovedVersion is not int version) return null;
        var artifact = s.Artifacts.Get(PhaseName.Sql, version);
        return artifact is null ? null : new ApprovedPlan(version, Json.Deserialize<SqlPlanPayload>(artifact.PayloadJson));
    }

    public static async Task<PreflightResult> RunAsync(DbmServices s, TransferOptions options, CancellationToken ct)
    {
        var checks = new List<PreflightCheck>();
        var approved = LoadApprovedPlan(s);
        if (approved is null)
        {
            checks.Add(Err("sql_plan", "The SQL phase is not approved yet."));
            return new PreflightResult(0, Clock.Now(), checks);
        }
        var phase = s.Phases.Get(PhaseName.Sql);
        checks.Add(phase.CurrentVersion == approved.Version
            ? Ok("sql_plan", $"Approved SQL plan v{approved.Version} ({approved.Plan.Tasks.Count} tasks).")
            : Err("sql_plan", $"The current SQL version (v{phase.CurrentVersion}) is not the approved one (v{approved.Version}); approve it again."));
        checks.Add(PlanCheck(approved.Plan));

        string? srcCs = s.Connections.GetConnectionString(Side.Src);
        string? tgtCs = s.Connections.GetConnectionString(Side.Tgt);
        var secrets = Redactor.SecretsOf(srcCs ?? "").Concat(Redactor.SecretsOf(tgtCs ?? "")).ToList();
        var src = await TryOpenAsync("source_connection", srcCs, checks, secrets, ct);
        var tgt = await TryOpenAsync("target_connection", tgtCs, checks, secrets, ct);
        try
        {
            if (src is not null && tgt is not null)
            {
                try
                {
                    var drift = await DriftChecker.CheckAsync(s, ct);
                    string sides = string.Join(" and ", new[] { drift.SrcChanged ? "source" : null, drift.TgtChanged ? "target" : null }.OfType<string>());
                    checks.Add(drift.Any
                        ? Err("schema_drift", $"The {sides} schema changed since discovery. Re-run discovery and review before transferring.")
                        : Ok("schema_drift", "Both schemas match the discovered catalogs."));
                }
                catch (Exception ex) when (ex is SqlException or InvalidOperationException)
                {
                    checks.Add(new PreflightCheck("schema_drift", false, "warning", "Drift check failed: " + ex.Message));
                }
            }
            if (tgt is not null) checks.AddRange(await TargetChecksAsync(tgt, approved.Plan, options, ct));
            if (src is not null) checks.Add(await SourceEstimateAsync(src, approved.Plan, ct));
            var keyless = TransferEngine.PlanOrder(approved.Plan).Where(id => approved.Plan.Tasks[id].KeyColumns.Count == 0)
                .Select(id => approved.Plan.Tasks[id].Target).ToList();
            if (keyless.Count > 0)
                checks.Add(Ok("keyless_tasks",
                    $"No usable key, loaded in one transaction (pause waits for them; failures restart them): {string.Join(", ", keyless)}."));
        }
        finally
        {
            if (src is not null) await src.DisposeAsync();
            if (tgt is not null) await tgt.DisposeAsync();
        }
        return new PreflightResult(approved.Version, Clock.Now(),
            checks.Select(c => c with { Detail = Redactor.Scrub(c.Detail, secrets) }).ToList());
    }

    public static PreflightCheck PlanCheck(SqlPlanPayload plan)
    {
        var bad = TransferEngine.PlanOrder(plan)
            .Where(id => plan.Tasks[id].Errors.Count > 0 || string.IsNullOrWhiteSpace(plan.Tasks[id].SourceQuery)).ToList();
        return bad.Count == 0
            ? Ok("plan_valid", $"{plan.Tasks.Count} tasks, none with validation errors.")
            : Err("plan_valid", "Tasks with validation errors: " + string.Join(", ", bad) + ". Fix them in the SQL phase.");
    }

    public static async Task<List<PreflightCheck>> TargetChecksAsync(SqlConnection tgt, SqlPlanPayload plan, TransferOptions options, CancellationToken ct)
    {
        var targets = TransferEngine.PlanOrder(plan).Select(id => plan.Tasks[id])
            .GroupBy(t => t.Target, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Target: g.Key, Identity: g.Any(t => t.IdentityInsert))).ToList();
        var missing = new List<string>();
        var noInsert = new List<string>();
        var noAlter = new List<string>();
        var noTruncate = new List<string>();
        var nonEmpty = new List<string>();
        foreach (var (target, identity) in targets)
        {
            int exists, insert, alter, delete;
            await using (var cmd = new SqlCommand(
                "SELECT CASE WHEN OBJECT_ID(@t, N'U') IS NULL THEN 0 ELSE 1 END, ISNULL(HAS_PERMS_BY_NAME(@t, N'OBJECT', N'INSERT'), 0), " +
                "ISNULL(HAS_PERMS_BY_NAME(@t, N'OBJECT', N'ALTER'), 0), ISNULL(HAS_PERMS_BY_NAME(@t, N'OBJECT', N'DELETE'), 0)", tgt))
            {
                cmd.Parameters.Add(new SqlParameter("@t", SqlDbType.NVarChar, 600) { Value = SqlQuote.TableKey(target) });
                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                (exists, insert, alter, delete) = (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3));
            }
            if (exists == 0) { missing.Add(target); continue; }
            if (insert == 0) noInsert.Add(target);
            if (identity && alter == 0) noAlter.Add(target);
            if (options.TruncateTarget && (alter == 0 || delete == 0)) noTruncate.Add(target);
            long rows = await TargetOps.CountTargetAsync(tgt, target, ct);
            if (rows > 0) nonEmpty.Add($"{target} ({rows.ToString("N0", CultureInfo.InvariantCulture)})");
        }

        var checks = new List<PreflightCheck>
        {
            missing.Count == 0 ? Ok("target_tables", $"All {targets.Count} target tables exist.")
                : Err("target_tables", "Missing target tables: " + string.Join(", ", missing) + "."),
            noInsert.Count == 0 ? Ok("insert_permission", "INSERT permission on every target table.")
                : Err("insert_permission", "No INSERT permission on: " + string.Join(", ", noInsert) + "."),
        };
        if (targets.Any(t => t.Identity))
            checks.Add(noAlter.Count == 0 ? Ok("identity_insert_permission", "ALTER permission for identity-preserving loads.")
                : Err("identity_insert_permission", "Keeping identity values needs ALTER on: " + string.Join(", ", noAlter) + "."));
        if (options.TruncateTarget)
            checks.Add(noTruncate.Count == 0 ? Ok("truncate_permission", "ALTER and DELETE permission to empty the target tables.")
                : Err("truncate_permission", "Emptying targets needs ALTER and DELETE on: " + string.Join(", ", noTruncate) + "."));

        int ctlExists, canCreate, canAlterDbo;
        await using (var cmd = new SqlCommand(
            "SELECT CASE WHEN OBJECT_ID(N'dbo.__dbm_checkpoint', N'U') IS NULL THEN 0 ELSE 1 END, " +
            "ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CREATE TABLE'), 0), ISNULL(HAS_PERMS_BY_NAME(N'dbo', N'SCHEMA', N'ALTER'), 0)", tgt))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            (ctlExists, canCreate, canAlterDbo) = (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2));
        }
        checks.Add(ctlExists == 1 ? Ok("control_table", "The checkpoint table dbo.__dbm_checkpoint already exists and will be reused.")
            : canCreate == 1 && canAlterDbo == 1 ? Ok("control_table", "Can create the checkpoint table dbo.__dbm_checkpoint.")
            : Err("control_table", "Creating dbo.__dbm_checkpoint needs CREATE TABLE and ALTER on schema dbo."));

        checks.Add(nonEmpty.Count == 0 ? Ok("target_rows", "All target tables are empty.")
            : options.TruncateTarget ? Ok("target_rows", "Will be emptied first: " + string.Join(", ", nonEmpty) + ".")
            : new PreflightCheck("target_rows", false, "warning",
                "Target tables already contain rows: " + string.Join(", ", nonEmpty) + ". Loading may hit duplicate keys; consider 'Truncate target first'."));
        return checks;
    }

    public static async Task<PreflightCheck> SourceEstimateAsync(SqlConnection src, SqlPlanPayload plan, CancellationToken ct)
    {
        long total = 0;
        int counted = 0;
        (string Target, long Rows) largest = ("", -1);
        var failed = new List<string>();
        foreach (var id in TransferEngine.PlanOrder(plan))
        {
            var task = plan.Tasks[id];
            try
            {
                long n = await TargetOps.ScalarLongAsync(src, TargetOps.CountSqlOf(task), ct, timeoutSec: 120);
                total += n;
                counted++;
                if (n > largest.Rows) largest = (task.Target, n);
            }
            catch (SqlException ex)
            {
                failed.Add($"{id} ({ex.Message})");
            }
        }
        string detail = $"{total.ToString("N0", CultureInfo.InvariantCulture)} rows across {counted} tasks" +
                        (largest.Rows >= 0 ? $" (largest: {largest.Target} {largest.Rows.ToString("N0", CultureInfo.InvariantCulture)})" : "") + ".";
        return failed.Count == 0
            ? Ok("estimated_rows", detail)
            : new PreflightCheck("estimated_rows", false, "warning", detail + " Could not count: " + string.Join("; ", failed));
    }

    private static async Task<SqlConnection?> TryOpenAsync(string name, string? cs, List<PreflightCheck> checks, IReadOnlyList<string> secrets, CancellationToken ct)
    {
        if (cs is null)
        {
            checks.Add(Err(name, "No connection saved."));
            return null;
        }
        try
        {
            var conn = await SqlConnect.OpenAsync(cs, ct);
            checks.Add(Ok(name, "Connected: " + Redactor.Describe(cs)));
            return conn;
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException)
        {
            checks.Add(Err(name, "Cannot connect: " + Redactor.Scrub(ex.Message, secrets)));
            return null;
        }
    }

    private static PreflightCheck Ok(string name, string detail) => new(name, true, "info", detail);
    private static PreflightCheck Err(string name, string detail) => new(name, false, "error", detail);
}
```

- [ ] **Step 6: Wire validation + report into the engine's completion hook**

Modify `plugins/db-migrate/engine/Dbm/Core/Transfer/TransferEngine.cs` — replace these two lines:

```csharp
    /// <summary>Completion hook: Task 5.4 replaces this body with run validation + the final report. Returns summary_json.</summary>
    private Task<string?> FinishCompletedAsync(RunContext rc, CancellationToken ct) => Task.FromResult<string?>(null);
```

with:

```csharp
    /// <summary>Completion: validate every task (counts + checksums), then build the final report (stored as summary_json).</summary>
    private async Task<string?> FinishCompletedAsync(RunContext rc, CancellationToken ct)
    {
        await using (var src = await SqlConnect.OpenAsync(sourceCs, ct))
        await using (var tgt = await SqlConnect.OpenAsync(targetCs, ct))
        {
            foreach (var row in rc.Repo.Tasks(rc.RunId))
            {
                var validation = await RunValidator.ValidateTaskAsync(src, tgt, plan.Tasks[row.TaskId], row, rc.Options.ValidateChecksums, ct);
                rc.Repo.SetTaskValidation(rc.RunId, row.TaskId, Json.Serialize(validation with { ChecksumsSkipped = validation.ChecksumsSkipped is null ? null : rc.Scrub(validation.ChecksumsSkipped) }));
                if (!validation.CountMatch)
                    rc.Log("warn", $"{row.TaskId} {row.Target}: expected {validation.RowsSource - validation.RowsError:N0} new rows, found {validation.RowsAfter - validation.RowsBefore:N0}.");
            }
        }
        var run = rc.Repo.GetRun(rc.RunId)!;
        var report = FinalReportBuilder.Build(run, RunStatus.Completed, rc.Repo.Tasks(rc.RunId),
            taskId => rc.Repo.ErrorRows(rc.RunId, taskId, FinalReportBuilder.MaxErrorSamples), Clock.Now());
        return Json.Serialize(report);
    }
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~ReportingTests|FullyQualifiedName~RunValidatorTests|FullyQualifiedName~PreflightTests|FullyQualifiedName~TransferEngineTests"`
Expected: PASS — 8 ReportingTests (theory rows included), 2 RunValidatorTests, 3 PreflightTests, and the 6 TransferEngineTests still green.

- [ ] **Step 8: Commit**

```bash
git add plugins/db-migrate/engine/Dbm/Core/Transfer plugins/db-migrate/engine/Dbm.Tests/Unit/Transfer plugins/db-migrate/engine/Dbm.Tests/Integration/Transfer
git commit -F - <<'EOF'
feat(transfer): preflight checklist, count/checksum validation and final report

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49
EOF
```

---

### Task 5.5: Transfer service, API, CLI and transfer playbook

**Files:**
- Create: `plugins/db-migrate/engine/Dbm/Core/Transfer/TransferService.cs`
- Create: `plugins/db-migrate/engine/Dbm/Web/Endpoints/TransferEndpoints.cs`
- Create: `plugins/db-migrate/engine/Dbm/Cli/Commands/TransferCommands.cs`
- Create: `plugins/db-migrate/skills/db-migrate/reference/transfer.md`
- Modify: `plugins/db-migrate/engine/Dbm/Web/WebHost.cs` (create the service), `plugins/db-migrate/engine/Dbm/Web/Endpoints/EndpointRegistry.cs`, `plugins/db-migrate/engine/Dbm/Cli/CommandRegistry.cs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Integration/Transfer/TransferServiceTests.cs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/Cli/TransferCommandsTests.cs`

**Interfaces:**
- Consumes: `WorkflowEngine.OnTransferStarted/OnTransferFinished/Next` (C5), `PhaseRepo`, `ArtifactRepo`, `ConnectionRepo` (C2), `WebState`, `EndpointRegistry`, `ServerControl.EnsureRunningAsync`, `ServerInfo` (C7), `ICommand`, `Args`, `CliContext`, `Output`, `CommandRegistry` (C8), `CatalogExtractor`, `Fingerprint`, `CatalogRepo.Save` (C10, tests only), T5.1–T5.4.
- Produces:
```csharp
namespace Dbm.Core.Transfer;
public sealed record TransferRunView(long Id, int SqlVersion, RunStatus Status, TransferOptions Options, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, string? Error, bool HasReport);
public sealed record TransferTaskView(string TaskId, string Target, int Ordinal, TransferTaskStatus Status, long? RowsSource, long? RowsBefore, long RowsDone,
    long RowsError, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, DateTimeOffset? HeartbeatAt, string? Error, TaskValidation? Validation, List<string> DependsOn, bool Keyless);
public sealed record TransferTotals(long RowsSource, long RowsDone, long RowsError, int TasksDone, int TasksTotal);
public sealed record TransferView(TransferRunView? Run, List<TransferTaskView> Tasks, TransferTotals Totals, bool Active, bool CanStart,
    string? TargetDatabase, int? SqlVersion, PreflightResult? Preflight, TransferOptions Defaults);
public sealed class TransferService(DbmServices services)
{
    public static readonly TimeSpan PreflightMaxAge;             // 15 min
    public PreflightResult? LastPreflight { get; }
    public event Action<ChunkCommit>? ChunkCommitted;
    public bool IsActive { get; }
    public Task Current { get; }                                 // the background run (Task.CompletedTask when idle)
    public int RecoverInterrupted();                             // server start
    public Task<PreflightResult> PreflightAsync(TransferOptions options, CancellationToken ct);
    public Task<long> StartAsync(TransferOptions options, string confirmTarget, CancellationToken ct);
    public void Pause();
    public long Resume();                                        // latest run paused|failed -> continues from checkpoints
    public Task CancelAsync(CancellationToken ct);               // running -> after current chunk; paused|failed -> cancelled now
    public Task StopAsync();                                     // server shutdown: hard stop (recovered as paused on the next start)
    public TransferView View();
}
// Dbm.Web.Endpoints.TransferEndpoints.Map(IEndpointRouteBuilder app, WebState state)
// Dbm.Cli.Commands: TransferStartCommand ("transfer start"), TransferPauseCommand, TransferResumeCommand, TransferCancelCommand, TransferStatusCommand
//   public static (TransferOptions Options, string? Confirm) TransferStartCommand.Parse(Args args);
//   public static JsonObject TransferStatusCommand.Compact(JsonNode view);
```

**HTTP API (C7 rows for T5.5):**

| Method & path | Body → Response |
|---|---|
| `POST /api/transfer/preflight` | `{options?}` → `PreflightResult` `{sqlVersion, at, checks:[{name, ok, severity, detail}], passed}` |
| `POST /api/transfer/start` | `{options, confirmTarget}` → `{ok:true, runId}`; 409 `{error, message, details}` (`busy`, `paused_run`, `not_ready`, `confirm_mismatch`, `preflight_failed`, `bad_options`, `no_connection`) |
| `POST /api/transfer/pause` / `resume` / `cancel` | → `{ok:true}` (resume: `{ok:true, runId}`); 409 `{error:"not_running"|"not_resumable"|"not_cancellable"|"busy", message}` |
| `GET /api/transfer` | → `TransferView` (latest run + tasks + totals; before the first run: the approved plan's tasks as `pending`) |
| `GET /api/transfer/errors?task=T02&limit=100` | → `[ErrorRowEntry]` of the latest run |

**Start rules (normative):** exclusive (`busy`); a paused latest run must be resumed or cancelled first; allowed when Ready is `awaiting_review` (first run → `Workflow.OnTransferStarted()`) or when Transfer is `running` and the latest run is `failed`/`cancelled` (a restart starts a new run id; the abandoned run's checkpoint rows are ignored, and `__dbm_*` tables never count as schema drift); `confirmTarget` must equal the saved target `ServerMeta.Database` exactly; the last pre-flight is reused only if it passed, is < 15 min old, used the same options and the approved SQL version — otherwise pre-flight runs again and must pass. On completion the service stores the report as the Complete artifact (`Math.Max(1, NextVersion)`, author `script`, summary `FinalReportBuilder.Summary`) and calls `OnTransferFinished("completed", version)`; failed → `OnTransferFinished("failed", null)`; cancelled → `OnTransferFinished("cancelled", null)`; paused → nothing (`Next()` reads the run status).

- [ ] **Step 1: Write the failing tests**

`plugins/db-migrate/engine/Dbm.Tests/Integration/Transfer/TransferServiceTests.cs`:

```csharp
using Dbm.Core;
using Dbm.Core.Catalog;
using Dbm.Core.Sql;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Dbm.Core.Transfer;
using Dbm.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Dbm.Tests.Integration.Transfer;

[Trait("Category", "Integration")]
public sealed class TransferServiceTests(EngineSourceFixture fx) : IClassFixture<EngineSourceFixture>
{
    private sealed class Rig(XferServices svc, TempDatabase tgt) : IAsyncDisposable
    {
        public XferServices Svc { get; } = svc;
        public TempDatabase Tgt { get; } = tgt;
        public DbmServices S => Svc.Services;
        public TransferService Service { get; } = new(svc.Services);
        public async ValueTask DisposeAsync()
        {
            try { await Service.Current; } catch (Exception) { }
            Svc.Dispose();
            await Tgt.DisposeAsync();
        }
    }

    private static async Task SaveSideAsync(DbmServices s, Side side, string cs)
    {
        var meta = await SqlConnect.ProbeAsync(cs, default);
        s.Connections.Save(side, cs, meta);
        await using var conn = await SqlConnect.OpenAsync(cs, default);
        var snapshot = await CatalogExtractor.ExtractAsync(conn, meta, default);
        s.Catalog.Save(side, snapshot, Fingerprint.Compute(snapshot));
    }

    private async Task<Rig> RigAsync()
    {
        var tgt = await TempDatabase.CreateAsync("dbm_svc_tgt");
        await tgt.ExecAsync(TransferEngineTests.TargetSchema);
        var rig = new Rig(new XferServices(), tgt);
        await SaveSideAsync(rig.S, Side.Src, fx.Src.ConnectionString);
        await SaveSideAsync(rig.S, Side.Tgt, tgt.ConnectionString);
        foreach (var p in new[] { PhaseName.Setup, PhaseName.Discovery, PhaseName.Analysis, PhaseName.Mapping }) rig.S.Phases.SetApproved(p, 1, null);
        rig.S.Artifacts.Add(PhaseName.Sql, 1, Json.Serialize(TransferEngineTests.Plan()), "script", "test plan");
        rig.S.Phases.SetCurrentVersion(PhaseName.Sql, 1);
        rig.S.Phases.SetApproved(PhaseName.Sql, 1, null);
        rig.S.Phases.SetStatus(PhaseName.Ready, PhaseStatus.AwaitingReview);
        return rig;
    }

    private static readonly TransferOptions Skip = new() { ChunkSize = 100, Parallelism = 1, ErrorMode = "skip" };

    [Fact]
    public async Task Start_requires_the_exact_target_database_name()
    {
        await using var rig = await RigAsync();
        var ex = await Assert.ThrowsAsync<TransferException>(() => rig.Service.StartAsync(Skip, rig.Tgt.Name.ToUpperInvariant() + "X", default));
        Assert.Equal("confirm_mismatch", ex.Code);
        Assert.Null(rig.S.Transfers.Latest());
    }

    [Fact]
    public async Task Preflight_then_start_runs_to_completion_and_completes_the_workflow()
    {
        await using var rig = await RigAsync();
        var pre = await rig.Service.PreflightAsync(Skip, default);
        Assert.True(pre.Passed, string.Join("; ", pre.Checks.Where(c => !c.Ok).Select(c => $"{c.Name}: {c.Detail}")));
        Assert.Equal(1, pre.SqlVersion);
        Assert.Contains(pre.Checks, c => c.Name == "schema_drift" && c.Ok);
        Assert.Equal(rig.Tgt.Name, rig.Service.View().TargetDatabase);
        Assert.True(rig.Service.View().CanStart);
        Assert.Equal(3, rig.Service.View().Tasks.Count);                              // plan tasks shown before the first run

        long runId = await rig.Service.StartAsync(Skip, rig.Tgt.Name, default);
        Assert.Equal(PhaseStatus.Approved, rig.S.Phases.Get(PhaseName.Ready).Status);
        await rig.Service.Current;

        var view = rig.Service.View();
        Assert.Equal(runId, view.Run!.Id);
        Assert.Equal(RunStatus.Completed, view.Run.Status);
        Assert.True(view.Run.HasReport);
        Assert.Equal(2998, view.Totals.RowsDone);
        Assert.Equal(2, view.Totals.RowsError);
        Assert.All(view.Tasks, t => Assert.True(t.Validation!.CountMatch));
        var report = rig.S.Artifacts.Latest(PhaseName.Complete)!;
        Assert.Equal("script", report.Author);
        Assert.StartsWith("Transferred 2,998 of 3,000 rows into 3 tables", report.Summary);
        Assert.Equal(PhaseStatus.Approved, rig.S.Phases.Get(PhaseName.Transfer).Status);
        Assert.Equal(PhaseStatus.Approved, rig.S.Phases.Get(PhaseName.Complete).Status);
        var next = rig.S.Workflow.Next();
        Assert.Equal("stop", next.Action);
        Assert.Equal("complete", next.Reason);
        Assert.Equal(2, rig.S.Transfers.ErrorRows(runId, "T02").Count);
    }

    [Fact]
    public async Task Pause_and_resume_through_the_service()
    {
        await using var rig = await RigAsync();
        rig.Service.ChunkCommitted += c => { if (c.TaskId == "T02" && c.ChunkNo == 2) rig.Service.Pause(); };
        await rig.Service.StartAsync(Skip, rig.Tgt.Name, default);
        await rig.Service.Current;
        Assert.Equal(RunStatus.Paused, rig.Service.View().Run!.Status);
        Assert.False(rig.Service.View().CanStart);
        var next = rig.S.Workflow.Next();
        Assert.Equal("await", next.Action);
        Assert.Equal("transfer_paused", next.Reason);
        Assert.Equal("paused_run", (await Assert.ThrowsAsync<TransferException>(() => rig.Service.StartAsync(Skip, rig.Tgt.Name, default))).Code);

        rig.Service.Resume();
        await rig.Service.Current;
        Assert.Equal(RunStatus.Completed, rig.Service.View().Run!.Status);
        Assert.Equal(1998, await rig.Tgt.CountAsync("app.Child"));
        Assert.Equal("not_resumable", Assert.Throws<TransferException>(() => rig.Service.Resume()).Code);
    }

    [Fact]
    public async Task Cancelled_run_can_be_restarted_with_truncation()
    {
        await using var rig = await RigAsync();
        int pauses = 0;   // pause only the first run; the handler stays subscribed for the second one
        rig.Service.ChunkCommitted += c => { if (c.TaskId == "T01" && c.ChunkNo == 1 && Interlocked.Increment(ref pauses) == 1) rig.Service.Pause(); };
        long first = await rig.Service.StartAsync(Skip, rig.Tgt.Name, default);
        await rig.Service.Current;
        await rig.Service.CancelAsync(default);
        Assert.Equal(RunStatus.Cancelled, rig.S.Transfers.GetRun(first)!.Status);
        Assert.Equal("transfer_cancelled", rig.S.Workflow.Next().Reason);
        Assert.True(rig.Service.View().CanStart);

        long second = await rig.Service.StartAsync(Skip with { TruncateTarget = true, ChunkSize = 1000 }, rig.Tgt.Name, default);
        Assert.NotEqual(first, second);
        await rig.Service.Current;
        Assert.Equal(RunStatus.Completed, rig.S.Transfers.GetRun(second)!.Status);
        Assert.Equal(300, await rig.Tgt.CountAsync("app.Parent"));
        await using var conn = new SqlConnection(rig.Tgt.ConnectionString);
        await conn.OpenAsync();
        Assert.False(await ControlTable.ExistsAsync(conn, default));
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/Cli/TransferCommandsTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Dbm.Cli;
using Dbm.Cli.Commands;
using Xunit;

namespace Dbm.Tests.Unit.Cli;

public sealed class TransferCommandsTests
{
    [Fact]
    public void Start_options_map_from_flags()
    {
        var (o, confirm) = TransferStartCommand.Parse(Args.Parse(new[]
            { "--chunk", "500", "--parallel", "2", "--skip-errors", "--truncate", "--yes-target", "ShopV2" }));
        Assert.Equal(500, o.ChunkSize);
        Assert.Equal(2, o.Parallelism);
        Assert.Equal("skip", o.ErrorMode);
        Assert.True(o.TruncateTarget);
        Assert.Equal("ShopV2", confirm);
    }

    [Fact]
    public void Start_defaults_are_the_contract_defaults()
    {
        var (o, confirm) = TransferStartCommand.Parse(Args.Parse(Array.Empty<string>()));
        Assert.Equal(100_000, o.ChunkSize);
        Assert.Equal(4, o.Parallelism);
        Assert.Equal("stop", o.ErrorMode);
        Assert.False(o.TruncateTarget);
        Assert.Null(confirm);
    }

    [Fact]
    public async Task Start_without_confirmation_fails_before_contacting_the_server()
    {
        var output = new StringWriter();
        var ctx = new CliContext { Out = output, Err = new StringWriter(), WorkspaceOverride = Path.GetTempPath() };
        int code = await new TransferStartCommand().RunAsync(Args.Parse(Array.Empty<string>()), ctx);
        Assert.Equal(1, code);
        Assert.Contains("\"error\":\"confirm_required\"", output.ToString());
    }

    [Fact]
    public void Status_is_compact_and_lists_only_active_tasks()
    {
        var view = JsonNode.Parse("""
            {"run":{"id":3,"status":"running","error":null},
             "tasks":[{"taskId":"T01","target":"app.A","status":"done","rowsDone":10,"rowsSource":10,"rowsError":0,"error":null},
                      {"taskId":"T02","target":"app.B","status":"running","rowsDone":5,"rowsSource":20,"rowsError":1,"error":null},
                      {"taskId":"T03","target":"app.C","status":"failed","rowsDone":0,"rowsSource":7,"rowsError":1,"error":"FK violation"}],
             "totals":{"rowsSource":37,"rowsDone":15,"rowsError":2,"tasksDone":1,"tasksTotal":3}}
            """)!;
        Assert.Equal(
            "{\"runId\":3,\"status\":\"running\",\"done\":15,\"total\":37,\"errors\":2,\"tasksDone\":1,\"tasksTotal\":3,\"tasks\":[" +
            "{\"id\":\"T02\",\"target\":\"app.B\",\"status\":\"running\",\"done\":5,\"source\":20,\"errors\":1}," +
            "{\"id\":\"T03\",\"target\":\"app.C\",\"status\":\"failed\",\"done\":0,\"source\":7,\"errors\":1,\"error\":\"FK violation\"}]}",
            TransferStatusCommand.Compact(view).ToJsonString());
        Assert.Equal("{\"status\":\"none\"}", TransferStatusCommand.Compact(JsonNode.Parse("""{"run":null,"tasks":[],"totals":{}}""")!).ToJsonString());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~TransferServiceTests|FullyQualifiedName~TransferCommandsTests"`
Expected: build FAILS with `CS0246: The type or namespace name 'TransferService' could not be found` (and `TransferStartCommand`, `TransferStatusCommand`).

- [ ] **Step 3: Implement the service**

`plugins/db-migrate/engine/Dbm/Core/Transfer/TransferService.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Dbm.Core.Sql;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

public sealed record TransferRunView(long Id, int SqlVersion, RunStatus Status, TransferOptions Options, DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt, string? Error, bool HasReport);

public sealed record TransferTaskView(string TaskId, string Target, int Ordinal, TransferTaskStatus Status, long? RowsSource, long? RowsBefore,
    long RowsDone, long RowsError, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, DateTimeOffset? HeartbeatAt, string? Error,
    TaskValidation? Validation, List<string> DependsOn, bool Keyless);

public sealed record TransferTotals(long RowsSource, long RowsDone, long RowsError, int TasksDone, int TasksTotal);

public sealed record TransferView(TransferRunView? Run, List<TransferTaskView> Tasks, TransferTotals Totals, bool Active, bool CanStart,
    string? TargetDatabase, int? SqlVersion, PreflightResult? Preflight, TransferOptions Defaults);

/// <summary>Server singleton (WebState.Transfer): owns the background run and connects it to the workflow.</summary>
public sealed class TransferService(DbmServices services)
{
    public static readonly TimeSpan PreflightMaxAge = TimeSpan.FromMinutes(15);

    private readonly object _lock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _active, _starting;
    private Task? _current;
    private TransferControl? _control;
    private TransferOptions? _preflightOptions;
    private PreflightResult? _lastPreflight;

    public event Action<ChunkCommit>? ChunkCommitted;

    public PreflightResult? LastPreflight { get { lock (_lock) return _lastPreflight; } }
    public bool IsActive { get { lock (_lock) return _active; } }
    public Task Current { get { lock (_lock) return _current ?? Task.CompletedTask; } }

    public int RecoverInterrupted()
    {
        int n = services.Transfers.RecoverInterrupted();
        if (n > 0)
            services.Sink.Publish("log", new { level = "warn", message = $"{n} interrupted transfer run(s) marked paused; resume from the Execute screen." });
        return n;
    }

    public async Task<PreflightResult> PreflightAsync(TransferOptions options, CancellationToken ct)
    {
        var normalized = options.Normalized();
        var result = await Preflight.RunAsync(services, normalized, ct);
        lock (_lock)
        {
            _lastPreflight = result;
            _preflightOptions = normalized;
        }
        return result;
    }

    public async Task<long> StartAsync(TransferOptions options, string confirmTarget, CancellationToken ct)
    {
        options = options.Normalized();
        lock (_lock)
        {
            if (_active || _starting) throw new TransferException("busy", "A transfer is already running.");
            _starting = true;
        }
        try
        {
            var latest = services.Transfers.Latest();
            if (latest?.Status == RunStatus.Paused)
                throw new TransferException("paused_run", $"Run {latest.Id} is paused; resume or cancel it first.");
            bool restart = services.Phases.Get(PhaseName.Transfer).Status == PhaseStatus.Running
                           && (latest?.Status is RunStatus.Failed or RunStatus.Cancelled);
            if (!restart && services.Phases.Get(PhaseName.Ready).Status != PhaseStatus.AwaitingReview)
                throw new TransferException("not_ready", "The transfer starts from the Ready step: approve the SQL phase first.");
            string? database = services.Connections.GetMeta(Side.Tgt)?.Database;
            if (database is null || !string.Equals((confirmTarget ?? "").Trim(), database, StringComparison.Ordinal))
                throw new TransferException("confirm_mismatch", "Type the target database name exactly to confirm the transfer.");
            var approved = Preflight.LoadApprovedPlan(services) ?? throw new TransferException("not_ready", "The SQL phase is not approved.");
            var (srcCs, tgtCs) = ConnectionStrings();

            PreflightResult? pre;
            TransferOptions? preOptions;
            lock (_lock)
            {
                pre = _lastPreflight;
                preOptions = _preflightOptions;
            }
            if (restart || pre is null || !pre.Passed || pre.SqlVersion != approved.Version || Clock.Now() - pre.At > PreflightMaxAge || preOptions != options)
                pre = await PreflightAsync(options, ct);
            if (!pre.Passed)
                throw new TransferException("preflight_failed", "Pre-flight checks failed.",
                    pre.Checks.Where(c => !c.Ok && c.Severity == "error").Select(c => $"{c.Name}: {c.Detail}").ToList());

            var engine = new TransferEngine(services, approved.Plan, srcCs, tgtCs);
            long runId = engine.CreateRun(approved.Version, options);
            if (!restart) services.Workflow.OnTransferStarted();
            Launch(engine, runId, srcCs, tgtCs);
            return runId;
        }
        finally
        {
            lock (_lock) _starting = false;
        }
    }

    public void Pause()
    {
        TransferControl? control;
        lock (_lock) control = _active ? _control : null;
        if (control is null) throw new TransferException("not_running", "No transfer is running.");
        control.RequestPause();
        services.Sink.Publish("log", new { level = "info", message = "Pause requested: running tasks stop after their current chunk." });
    }

    public long Resume()
    {
        lock (_lock)
            if (_active || _starting) throw new TransferException("busy", "The transfer is already running.");
        var run = services.Transfers.Latest();
        if (run is null || run.Status is not (RunStatus.Paused or RunStatus.Failed))
            throw new TransferException("not_resumable", "There is no paused or failed transfer run to resume.");
        var artifact = services.Artifacts.Get(PhaseName.Sql, run.SqlVersion)
            ?? throw new TransferException("no_plan", $"SQL plan v{run.SqlVersion} of run {run.Id} is missing.");
        var (srcCs, tgtCs) = ConnectionStrings();
        var engine = new TransferEngine(services, Json.Deserialize<SqlPlanPayload>(artifact.PayloadJson), srcCs, tgtCs);
        Launch(engine, run.Id, srcCs, tgtCs);
        services.Sink.Publish("log", new { level = "info", message = $"Resuming transfer run {run.Id} from its checkpoints." });
        return run.Id;
    }

    public async Task CancelAsync(CancellationToken ct)
    {
        TransferControl? control;
        lock (_lock) control = _active ? _control : null;
        if (control is not null)
        {
            control.RequestCancel();
            services.Sink.Publish("log", new { level = "warn", message = "Cancel requested: running tasks stop after their current chunk." });
            return;
        }
        var run = services.Transfers.Latest();
        if (run is null || run.Status is not (RunStatus.Paused or RunStatus.Failed))
            throw new TransferException("not_cancellable", "There is no running, paused or failed transfer to cancel.");
        if (!run.Options.KeepControlTable) await DropControlTableAsync(ConnectionStrings().Target, ct);
        services.Transfers.SetRunStatus(run.Id, RunStatus.Cancelled);
        services.Sink.Publish("transfer_run_changed", new { runId = run.Id, status = EnumText.ToText(RunStatus.Cancelled) });
        services.Workflow.OnTransferFinished("cancelled", null);
    }

    /// <summary>Server shutdown: hard-stops the background run (crash semantics — after the restart it is recovered as 'paused').</summary>
    public async Task StopAsync()
    {
        _lifetime.Cancel();
        try { await Current.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (Exception) { /* cancelled or timed out: the process is ending */ }
    }

    public TransferView View()
    {
        var run = services.Transfers.Latest();
        var approved = Preflight.LoadApprovedPlan(services);
        SqlPlanPayload? plan = approved?.Plan;
        if (run is not null && run.SqlVersion != approved?.Version && services.Artifacts.Get(PhaseName.Sql, run.SqlVersion) is { } artifact)
            plan = Json.Deserialize<SqlPlanPayload>(artifact.PayloadJson);

        List<TransferTaskView> tasks;
        if (run is null)
        {
            tasks = plan is null ? [] : TransferEngine.PlanOrder(plan).Select((id, i) => new TransferTaskView(id, plan.Tasks[id].Target, i,
                TransferTaskStatus.Pending, null, null, 0, 0, null, null, null, null, null, plan.Tasks[id].DependsOn.ToList(),
                plan.Tasks[id].KeyColumns.Count == 0)).ToList();
        }
        else
        {
            tasks = services.Transfers.Tasks(run.Id).Select(t =>
            {
                var tp = plan?.Tasks.GetValueOrDefault(t.TaskId);
                return new TransferTaskView(t.TaskId, t.Target, t.Ordinal, t.Status, t.RowsSource, t.RowsBefore, t.RowsDone, t.RowsError,
                    t.StartedAt, t.EndedAt, t.HeartbeatAt, t.Error,
                    t.ValidationJson is null ? null : Json.Deserialize<TaskValidation>(t.ValidationJson),
                    tp?.DependsOn.ToList() ?? [], tp is not null && tp.KeyColumns.Count == 0);
            }).ToList();
        }

        var totals = new TransferTotals(tasks.Sum(t => t.RowsSource ?? 0), tasks.Sum(t => t.RowsDone), tasks.Sum(t => t.RowsError),
            tasks.Count(t => t.Status == TransferTaskStatus.Done), tasks.Count);
        bool active = IsActive;
        bool restartable = services.Phases.Get(PhaseName.Transfer).Status == PhaseStatus.Running
                           && (run?.Status is RunStatus.Failed or RunStatus.Cancelled);
        bool canStart = !active && run?.Status != RunStatus.Paused
                        && (services.Phases.Get(PhaseName.Ready).Status == PhaseStatus.AwaitingReview || restartable);
        var runView = run is null ? null : new TransferRunView(run.Id, run.SqlVersion, run.Status, run.Options, run.StartedAt, run.EndedAt,
            RunError(run), run.Status == RunStatus.Completed);
        return new TransferView(runView, tasks, totals, active, canStart, services.Connections.GetMeta(Side.Tgt)?.Database,
            approved?.Version, LastPreflight, new TransferOptions());
    }

    private void Launch(TransferEngine engine, long runId, string srcCs, string tgtCs)
    {
        var control = new TransferControl();
        control.ChunkCommitted += c => ChunkCommitted?.Invoke(c);
        var secrets = Redactor.SecretsOf(srcCs).Concat(Redactor.SecretsOf(tgtCs)).ToList();
        lock (_lock)
        {
            _control = control;
            _active = true;
            _current = Task.Run(() => RunBackgroundAsync(engine, runId, control, secrets));
        }
    }

    private async Task RunBackgroundAsync(TransferEngine engine, long runId, TransferControl control, IReadOnlyList<string> secrets)
    {
        try
        {
            TransferOutcome outcome;
            try
            {
                outcome = await engine.RunAsync(runId, control, _lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                return;   // server shutting down: the run stays 'running' and RecoverInterrupted pauses it on the next start
            }
            catch (Exception ex)
            {
                string msg = Redactor.Scrub(ex.Message, secrets);
                services.Transfers.SetRunStatus(runId, RunStatus.Failed, Json.Serialize(new { error = msg }));
                services.Sink.Publish("transfer_run_changed", new { runId, status = EnumText.ToText(RunStatus.Failed) });
                outcome = new TransferOutcome(RunStatus.Failed, msg);
            }
            OnFinished(runId, outcome);
        }
        catch (Exception ex)
        {
            services.Sink.Publish("log", new { level = "error", message = "Transfer bookkeeping failed: " + Redactor.Scrub(ex.Message, secrets) });
        }
        finally
        {
            lock (_lock)
            {
                _active = false;
                _control = null;
            }
        }
    }

    private void OnFinished(long runId, TransferOutcome outcome)
    {
        switch (outcome.Status)
        {
            case RunStatus.Completed:
                var run = services.Transfers.GetRun(runId)!;
                var report = run.SummaryJson is null ? null : Json.Deserialize<FinalReport>(run.SummaryJson);
                string summary = report is null ? $"Transfer run {runId} completed." : FinalReportBuilder.Summary(report);
                int version = Math.Max(1, services.Artifacts.NextVersion(PhaseName.Complete));
                services.Artifacts.Add(PhaseName.Complete, version, run.SummaryJson ?? "{}", "script", summary);
                services.Sink.Publish("artifact_created", new { phase = "complete", version, author = "script" });
                services.Workflow.OnTransferFinished("completed", version);
                break;
            case RunStatus.Failed:
                services.Workflow.OnTransferFinished("failed", null);
                break;
            case RunStatus.Cancelled:
                services.Workflow.OnTransferFinished("cancelled", null);
                break;
        }
    }

    private (string Source, string Target) ConnectionStrings()
        => (services.Connections.GetConnectionString(Side.Src) ?? throw new TransferException("no_connection", "The source connection is not saved."),
            services.Connections.GetConnectionString(Side.Tgt) ?? throw new TransferException("no_connection", "The target connection is not saved."));

    private async Task DropControlTableAsync(string targetCs, CancellationToken ct)
    {
        try
        {
            await using var conn = await SqlConnect.OpenAsync(targetCs, ct);
            await ControlTable.DropAsync(conn, ct);
        }
        catch (SqlException ex)
        {
            services.Sink.Publish("log", new { level = "warn",
                message = "Could not drop dbo.__dbm_checkpoint: " + Redactor.Scrub(ex.Message, Redactor.SecretsOf(targetCs)) });
        }
    }

    private static string? RunError(TransferRunRow run)
    {
        if (run.Status != RunStatus.Failed || run.SummaryJson is null) return null;
        try { return JsonNode.Parse(run.SummaryJson)?["error"]?.GetValue<string>(); }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }
}
```

- [ ] **Step 4: Implement the endpoints and wire the service into the server**

`plugins/db-migrate/engine/Dbm/Web/Endpoints/TransferEndpoints.cs`:

```csharp
using System.Text.Json;
using Dbm.Core;
using Dbm.Core.Transfer;
using Dbm.Core.Workflow;

namespace Dbm.Web.Endpoints;

public static class TransferEndpoints
{
    public sealed record PreflightBody(TransferOptions? Options);
    public sealed record StartBody(TransferOptions? Options, string? ConfirmTarget);

    public static void Map(IEndpointRouteBuilder app, WebState state)
    {
        app.MapGet("/api/transfer", () => Results.Json(Service(state).View(), Json.Options));

        app.MapPost("/api/transfer/preflight", (HttpRequest req, CancellationToken ct) => Guard(async () =>
        {
            var body = await ReadAsync<PreflightBody>(req, ct);
            return Results.Json(await Service(state).PreflightAsync(body?.Options ?? new TransferOptions(), ct), Json.Options);
        }));

        app.MapPost("/api/transfer/start", (HttpRequest req, CancellationToken ct) => Guard(async () =>
        {
            var body = await ReadAsync<StartBody>(req, ct);
            long runId = await Service(state).StartAsync(body?.Options ?? new TransferOptions(), body?.ConfirmTarget ?? "", ct);
            return Results.Json(new { ok = true, runId }, Json.Options);
        }));

        app.MapPost("/api/transfer/pause", () => Guard(() =>
        {
            Service(state).Pause();
            return Task.FromResult(Results.Json(new { ok = true }, Json.Options));
        }));

        app.MapPost("/api/transfer/resume", () => Guard(() =>
            Task.FromResult(Results.Json(new { ok = true, runId = Service(state).Resume() }, Json.Options))));

        app.MapPost("/api/transfer/cancel", (CancellationToken ct) => Guard(async () =>
        {
            await Service(state).CancelAsync(ct);
            return Results.Json(new { ok = true }, Json.Options);
        }));

        app.MapGet("/api/transfer/errors", (string? task, int? limit) =>
        {
            var run = state.Services.Transfers.Latest();
            IReadOnlyList<Dbm.Core.State.ErrorRowEntry> rows = run is null
                ? []
                : state.Services.Transfers.ErrorRows(run.Id, string.IsNullOrWhiteSpace(task) ? null : task, limit ?? 100);
            return Results.Json(rows, Json.Options);
        });
    }

    private static TransferService Service(WebState state)
        => state.Transfer ?? throw new InvalidOperationException("The transfer service is not initialised (see WebHost).");

    private static async Task<T?> ReadAsync<T>(HttpRequest req, CancellationToken ct) where T : class
    {
        if ((req.ContentLength is null or 0) && !req.Headers.ContainsKey("Transfer-Encoding")) return null;
        return await req.ReadFromJsonAsync<T>(Json.Options, ct);
    }

    private static async Task<IResult> Guard(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (TransferException ex)
        {
            return Results.Json(new { error = ex.Code, message = ex.Message, details = ex.Details }, Json.Options, statusCode: 409);
        }
        catch (WorkflowException ex)
        {
            return Results.Json(new { error = "workflow", message = ex.Message, details = ex.Details }, Json.Options, statusCode: 409);
        }
        catch (JsonException ex)
        {
            return Results.Json(new { error = "bad_request", message = "Invalid JSON body: " + ex.Message }, Json.Options, statusCode: 400);
        }
    }
}
```

Modify `plugins/db-migrate/engine/Dbm/Web/Endpoints/EndpointRegistry.cs` — replace the comment line

```csharp
        // T5.5: TransferEndpoints.Map(app, state);
```

with

```csharp
        TransferEndpoints.Map(app, state);
```

Modify `plugins/db-migrate/engine/Dbm/Web/WebState.cs` — replace the comment line

```csharp
    // T5.5 adds: public Dbm.Core.Transfer.TransferService? Transfer { get; set; }
```

with

```csharp
    public Dbm.Core.Transfer.TransferService? Transfer { get; set; }   // T5.5
```

Modify `plugins/db-migrate/engine/Dbm/Web/WebHost.cs` in `RunAsync`. Immediately **before** the line `            EndpointRegistry.MapAll(app, state);` insert:

```csharp
            var transfer = new Dbm.Core.Transfer.TransferService(services);
            transfer.RecoverInterrupted();   // spec §9 crash recovery: runs/tasks left 'running' by a dead process become 'paused'
            state.Transfer = transfer;
```

In the inner `finally` block, directly **after** the line `                background.Cancel();` insert:

```csharp
                await Quietly(transfer.StopAsync());   // hard-stop a running transfer before services are disposed; it resumes as 'paused'
```

Modify `plugins/db-migrate/engine/Dbm/Cli/Args.cs`. `--skip-errors` and `--truncate` must never take a value, so in `BooleanFlags` replace the line

```csharp
        "quiet", "rebuild", "dry-run", "no-browser", "json", "human", "inline", "help", "force", "detached",
```

with

```csharp
        "quiet", "rebuild", "dry-run", "no-browser", "json", "human", "inline", "help", "force", "detached",
        "skip-errors", "truncate",   // T5.5
```

- [ ] **Step 5: Implement the CLI commands and register them**

`plugins/db-migrate/engine/Dbm/Cli/Commands/TransferCommands.cs`:

```csharp
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Transfer;
using Dbm.Web;

namespace Dbm.Cli.Commands;

/// <summary>All transfer commands go through the running server (the transfer lives in the server process).</summary>
internal static class TransferApi
{
    public sealed record Reply(bool Ok, JsonNode? Body, string Code, string Message);

    public static async Task<Reply> SendAsync(CliContext ctx, HttpMethod method, string path, object? body)
    {
        var ws = ctx.RequireProject();   // CliFailure("no_project") outside a project
        var info = await ServerControl.EnsureRunningAsync(ws);
        using var http = new HttpClient { BaseAddress = new Uri(info.BaseUrl), Timeout = TimeSpan.FromMinutes(10) };
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Add("X-Dbm-Token", info.Token);
        if (body is not null) req.Content = new StringContent(Json.Serialize(body), Encoding.UTF8, "application/json");
        using var res = await http.SendAsync(req);
        string text = await res.Content.ReadAsStringAsync();
        JsonNode? node = null;
        try { node = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text); }
        catch (JsonException) { node = null; }
        if (res.IsSuccessStatusCode) return new Reply(true, node, "", "");
        string code = node?["error"]?.GetValue<string>() ?? $"http_{(int)res.StatusCode}";
        string message = node?["message"]?.GetValue<string>() ?? (res.ReasonPhrase ?? "Request failed.");
        var details = node?["details"]?.AsArray().Select(d => d?.GetValue<string>()).OfType<string>().ToList() ?? [];
        return new Reply(false, node, code, details.Count == 0 ? message : message + " " + string.Join(" | ", details));
    }

    public static int Print(CliContext ctx, Reply reply)
        => reply.Ok ? Output.Ok(ctx, reply.Body ?? new JsonObject { ["ok"] = true }) : Output.Fail(ctx, reply.Code, reply.Message);
}

public sealed class TransferStartCommand : ICommand
{
    public string Name => "transfer start";
    public string Help => "Start the transfer (normally from the UI): --yes-target <db> [--chunk n] [--parallel n] [--skip-errors] [--truncate]";

    public static (TransferOptions Options, string? Confirm) Parse(Args args) => (new TransferOptions
    {
        ChunkSize = args.Int("chunk", 100_000),
        Parallelism = args.Int("parallel", 4),
        ErrorMode = args.Flag("skip-errors") ? "skip" : "stop",
        TruncateTarget = args.Flag("truncate"),
    }.Normalized(), args.Opt("yes-target"));

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        var (options, confirm) = Parse(args);
        if (string.IsNullOrWhiteSpace(confirm))
            return Output.Fail(ctx, "confirm_required", "Pass --yes-target <target database name> to confirm the transfer.");
        return TransferApi.Print(ctx, await TransferApi.SendAsync(ctx, HttpMethod.Post, "/api/transfer/start", new { options, confirmTarget = confirm }));
    }
}

public sealed class TransferPauseCommand : ICommand
{
    public string Name => "transfer pause";
    public string Help => "Pause the running transfer after the current chunks commit";
    public async Task<int> RunAsync(Args args, CliContext ctx)
        => TransferApi.Print(ctx, await TransferApi.SendAsync(ctx, HttpMethod.Post, "/api/transfer/pause", null));
}

public sealed class TransferResumeCommand : ICommand
{
    public string Name => "transfer resume";
    public string Help => "Resume a paused or failed transfer run from its checkpoints";
    public async Task<int> RunAsync(Args args, CliContext ctx)
        => TransferApi.Print(ctx, await TransferApi.SendAsync(ctx, HttpMethod.Post, "/api/transfer/resume", null));
}

public sealed class TransferCancelCommand : ICommand
{
    public string Name => "transfer cancel";
    public string Help => "Cancel the transfer (committed rows stay in the target)";
    public async Task<int> RunAsync(Args args, CliContext ctx)
        => TransferApi.Print(ctx, await TransferApi.SendAsync(ctx, HttpMethod.Post, "/api/transfer/cancel", null));
}

public sealed class TransferStatusCommand : ICommand
{
    public string Name => "transfer status";
    public string Help => "One-line transfer status: run, rows done/total, errors, active tasks";

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        var reply = await TransferApi.SendAsync(ctx, HttpMethod.Get, "/api/transfer", null);
        return reply.Ok && reply.Body is not null ? Output.Ok(ctx, Compact(reply.Body)) : TransferApi.Print(ctx, reply);
    }

    /// <summary>Compact projection of GET /api/transfer for Claude (no per-row data, no connection details).</summary>
    public static JsonObject Compact(JsonNode view)
    {
        var run = view["run"];
        if (run is null) return new JsonObject { ["status"] = "none" };
        var totals = view["totals"];
        var o = new JsonObject
        {
            ["runId"] = run["id"]?.DeepClone(),
            ["status"] = run["status"]?.DeepClone(),
            ["done"] = totals?["rowsDone"]?.DeepClone(),
            ["total"] = totals?["rowsSource"]?.DeepClone(),
            ["errors"] = totals?["rowsError"]?.DeepClone(),
            ["tasksDone"] = totals?["tasksDone"]?.DeepClone(),
            ["tasksTotal"] = totals?["tasksTotal"]?.DeepClone(),
        };
        if (run["error"] is JsonValue error) o["error"] = error.DeepClone();
        var active = new JsonArray();
        foreach (var t in view["tasks"]?.AsArray() ?? new JsonArray())
        {
            string status = t?["status"]?.GetValue<string>() ?? "";
            if (status is not ("running" or "paused" or "failed")) continue;
            var item = new JsonObject
            {
                ["id"] = t!["taskId"]?.DeepClone(),
                ["target"] = t["target"]?.DeepClone(),
                ["status"] = status,
                ["done"] = t["rowsDone"]?.DeepClone(),
                ["source"] = t["rowsSource"]?.DeepClone(),
                ["errors"] = t["rowsError"]?.DeepClone(),
            };
            if (t["error"] is JsonValue taskError) item["error"] = taskError.DeepClone();
            active.Add(item);
        }
        o["tasks"] = active;
        return o;
    }
}
```

Modify `plugins/db-migrate/engine/Dbm/Cli/CommandRegistry.cs` — directly after the line `        new SqlValidateCommand(), // T4.3` add:

```csharp
        new TransferStartCommand(),   // T5.5
        new TransferPauseCommand(),   // T5.5
        new TransferResumeCommand(),  // T5.5
        new TransferCancelCommand(),  // T5.5
        new TransferStatusCommand(),  // T5.5
```

- [ ] **Step 6: Write the orchestrator's transfer playbook**

`plugins/db-migrate/skills/db-migrate/reference/transfer.md`:

````markdown
# Transfer playbook

Read this when `dbm next` returns a transfer-related action. The transfer runs inside the dbm server; you never move data, never
see rows or connection strings, and never start a transfer on your own initiative.

## What `dbm next` means

| Action / reason | What is happening | What you do |
|---|---|---|
| `await` / `execute` | SQL approved. The human runs pre-flight, picks options and clicks **Execute** (typed confirmation of the target database). | One line: "SQL approved — open the Execute screen (<Url>) to run pre-flight and start the transfer." Keep `dbm await` running in the background. |
| `await` / `transfer` | A run is in progress. | Nothing. If the human asks for progress, run `dbm transfer status` once and report one line. Keep `dbm await` in the background; do not poll. |
| `await` / `transfer_paused` | Paused by the human, or by a server restart (crash recovery turns interrupted runs into paused ones). All committed chunks are safe. | Say it is paused. Resume only when the human asks: `dbm transfer resume`. |
| `stop` / `transfer_failed` | Stop-on-error rejected a row, or a task/script failed. | Run `dbm transfer status`; report the failed task, its target and its error (already redacted). Offer the options below. |
| `stop` / `transfer_cancelled` | The human cancelled. Rows already committed stay in the target. | Report it. A new run can be started from the Execute screen (usually with "Truncate target first"). |
| `stop` / `complete` | Finished and validated. | Report the one-line summary. The final report is under **Complete** in the UI; `dbm export report` writes it as HTML. |

## Commands (all JSON, all go through the local server)

| Command | Effect |
|---|---|
| `dbm transfer status` | `{"runId","status","done","total","errors","tasksDone","tasksTotal","tasks":[running/paused/failed tasks]}` |
| `dbm transfer pause` | Running tasks finish their current chunk, commit its checkpoint, then stop. |
| `dbm transfer resume` | Continues a paused **or failed** run from the last committed checkpoint of every task — no duplicates, no gaps. |
| `dbm transfer cancel` | Running: stop after the current chunk. Paused/failed: cancel now. Drops the checkpoint table. |
| `dbm transfer start --yes-target <db> [--chunk n] [--parallel n] [--skip-errors] [--truncate]` | Only when the human explicitly asks you to start from the CLI **and** gave you the target database name. |

## After a failure

- **A row was rejected (stop-on-error):** the failing row is in the task's error list (Execute screen → task → errors). The human can
  fix the source data or the mapping/SQL and then `dbm transfer resume` (the failed chunk is retried), or cancel and start a new run
  with "Skip and log bad rows".
- **Pre-flight failed:** report the failing checks; schema drift means discovery must be re-run (the human does that in the UI).
- **Connection/permission errors:** report them; the human fixes access and resumes.
- Never suggest editing the target tables by hand, disabling constraints, or re-running with truncation without the human deciding.

## Facts worth knowing

- Chunks are keyset pages of the task's source key; each chunk and its checkpoint commit in one target transaction.
- Tasks without a usable key load in a single transaction: a pause waits for them to finish, a failure restarts them from scratch.
- Rejected rows (skip-and-log) are counted per task; the final report shows up to 5 samples per task and validates row counts and
  column checksums.
````

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~TransferServiceTests|FullyQualifiedName~TransferCommandsTests"`
Expected: PASS — 4 TransferServiceTests, 4 TransferCommandsTests. Then `dotnet build plugins/db-migrate/engine/Dbm.sln` → 0 errors (proves the WebHost/EndpointRegistry/CommandRegistry edits compile).

Manual smoke (with a project whose SQL phase is approved): `dbm ui`, then in another shell `dbm transfer status` → `{"status":"none"}` before the first run; `curl -s -X POST -H "X-Dbm-Token: <token>" http://127.0.0.1:<port>/api/transfer/preflight` → checklist JSON with `"passed":true`.

- [ ] **Step 8: Commit**

```bash
git add plugins/db-migrate/engine/Dbm/Core/Transfer/TransferService.cs plugins/db-migrate/engine/Dbm/Web/Endpoints/TransferEndpoints.cs plugins/db-migrate/engine/Dbm/Web/Endpoints/EndpointRegistry.cs plugins/db-migrate/engine/Dbm/Web/WebHost.cs plugins/db-migrate/engine/Dbm/Web/WebState.cs plugins/db-migrate/engine/Dbm/Cli/Args.cs plugins/db-migrate/engine/Dbm/Cli/Commands/TransferCommands.cs plugins/db-migrate/engine/Dbm/Cli/CommandRegistry.cs plugins/db-migrate/skills/db-migrate/reference/transfer.md plugins/db-migrate/engine/Dbm.Tests/Integration/Transfer/TransferServiceTests.cs plugins/db-migrate/engine/Dbm.Tests/Unit/Cli/TransferCommandsTests.cs
git commit -F - <<'EOF'
feat(transfer): transfer service, HTTP API, dbm transfer commands and playbook

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49
EOF
```

---

### Task 5.6: Execute & Final report UI

**Files:**
- Create: `plugins/db-migrate/engine/Dbm/wwwroot/js/lib/xfer.js` (pure helpers, Node-testable)
- Create: `plugins/db-migrate/engine/Dbm/wwwroot/js/views/execute.js`
- Create: `plugins/db-migrate/engine/Dbm/wwwroot/js/views/report.js`
- Create: `plugins/db-migrate/engine/Dbm/Web/Endpoints/ReportExport.cs`
- Modify: `plugins/db-migrate/engine/Dbm/wwwroot/index.html` (script tags), `plugins/db-migrate/engine/Dbm/wwwroot/css/app.css` (append `exe-`/`rep-` block), `plugins/db-migrate/engine/Dbm/Web/Endpoints/TransferEndpoints.cs` (register the `report` export; `ExportEndpoints.cs` itself is not touched)
- Test: `plugins/db-migrate/engine/Dbm.Tests/js/xfer.test.cjs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/Transfer/ReportExportTests.cs`

**Interfaces:**
- Consumes: C9 (`DBM.h`, `DBM.api.get/post`, `DBM.components.badge/modal/sparkline/emptyState`, `DBM.fmt.ts`, CSS vocabulary incl. `.bar` + `--v`, `.drawer`/`.drawer-open`, `.kpi*`, `.tbl*`), `GET/POST /api/transfer*` (T5.5), SSE events `transfer_progress` (`ProgressSnapshot`), `transfer_task_changed`, `transfer_run_changed`, `log`; T2.8 export registration (`Dbm.Web.Endpoints`): `record ExportFile(string FileName, string ContentType, byte[] Content)`, `ExportEndpoints.Register(string what, ExportEndpoints.Builder builder)` with `delegate Task<ExportFile> Builder(WebState state, IWebAssets assets, CancellationToken ct)`, and `WebExport.BuildHtml(IWebAssets assets, string view, string title, JsonNode payload)` → standalone HTML whose view reads `window.DBM_EXPORT.payload`.
- Produces:
```js
DBM.xfer = { num, ratio, pct, rate(samples, windowMs), pushSample(samples, t, v, max), eta(done, total, rate), fmtEta(sec), fmtRate(rowsPerSec),
             fmtDuration(startIso, endIso, nowMs), aggregate(tasks), mergeProgress(tasks, snapshot), controls(view), checkTone(check),
             preflightCounts(result), countText(task), keyText(keyJson), prettyJson(text), optionsKey(options), checksumText(checksums, skipped) };
DBM.views.ready = DBM.views.transfer = DBM.views.execute = { title: 'Execute', render(root, ctx), onEvent(evt, ctx) };
DBM.views.complete = DBM.views.report = { title: 'Final report', render(root, ctx) };   // 'report' is the export view name (views/report.js)
```
```csharp
namespace Dbm.Web.Endpoints;
public static class ReportExport
{
    public const string FileName = "final-report.html";
    public static JsonNode? LatestPayload(DbmServices s);                                          // latest Complete artifact payload or null
    public static Task<ExportFile> BuildAsync(WebState state, IWebAssets assets, CancellationToken ct);   // ExportEndpoints.Builder
}
// TransferEndpoints.Map calls ExportEndpoints.Register("report", ReportExport.BuildAsync) -> GET/POST /api/export/report serve the
// standalone HTML (view "report", payload = the {project, exportedAt, artifact} envelope); before the first completed run the builder
// throws ExportException, which ExportEndpoints answers with 404 export_unavailable.
```

**UI behaviour (normative):** the Execute screen (phases Ready and Transfer) shows, before the first run (or after a failed/cancelled one when `canStart`): a pre-flight card (Run pre-flight → checklist with ✓/!/✕ per check and a summary) and an options card (chunk size, parallelism, stop vs skip-and-log, truncate first with a danger note, checksums, table lock, fire triggers, keep checkpoint table) with **Execute…** (enabled once pre-flight passed) opening `DBM.components.modal({requireText: targetDatabase})`. With a run: a status banner (completed / failed with the error / paused / cancelled), KPIs (rows loaded, throughput, ETA, rejected), overall bar, throughput sparkline, Pause / Resume / Cancel (per `DBM.xfer.controls`), per-task table in execution order (status badge, progress bar, done/source, rows/s, rejected → error drawer from `/api/transfer/errors`, time or error) and a live log tail. `transfer_progress` updates the DOM in place (one `requestAnimationFrame` per burst, sparkline at most once a second); task/run change events re-fetch `GET /api/transfer` (debounced 150 ms). The report screen shows KPIs (rows loaded, rejected, duration + throughput, row counts n/m, checksums n/m), a per-task table (source / loaded / rejected / count / checksums / duration / rows/s, click or Enter expands checksum details and error samples), rejected-row samples, notes and run options, plus **Export HTML** (hidden when `ctx.readOnly`).

- [ ] **Step 1: Write the failing JS unit tests**

`plugins/db-migrate/engine/Dbm.Tests/js/xfer.test.cjs`:

```js
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

vm.runInThisContext(fs.readFileSync(path.join(__dirname, '..', '..', 'Dbm', 'wwwroot', 'js', 'lib', 'xfer.js'), 'utf8'));
const X = globalThis.DBM.xfer;

test('num and pct format tabular figures', () => {
  assert.equal(X.num(1234567), '1,234,567');
  assert.equal(X.num(null), '—');
  assert.equal(X.pct(1, 3), 33);
  assert.equal(X.pct(2999, 3000), 99);          // never shows 100 before it is finished
  assert.equal(X.pct(3, 3), 100);
  assert.equal(X.pct(0, 0), 0);
  assert.equal(X.ratio(5, 0), 1);
  assert.equal(X.ratio(15, 10), 1);
});

test('rate uses the samples inside the window', () => {
  assert.equal(X.rate([]), 0);
  assert.equal(X.rate([{ t: 0, v: 0 }, { t: 2000, v: 1000 }]), 500);
  assert.equal(X.rate([{ t: 0, v: 0 }, { t: 20000, v: 100 }, { t: 22000, v: 1100 }]), 500);
  assert.equal(X.rate([{ t: 0, v: 0 }, { t: 30000, v: 900 }]), 30);     // falls back to the previous sample
  assert.equal(X.rate([{ t: 0, v: 100 }, { t: 1000, v: 50 }]), 0);      // never negative
});

test('pushSample caps the history', () => {
  const s = [];
  for (let i = 0; i < 10; i++) X.pushSample(s, i * 1000, i, 4);
  assert.deepEqual(s.map((x) => x.v), [6, 7, 8, 9]);
});

test('eta and duration formatting', () => {
  assert.equal(X.eta(1000, 3000, 500), 4);
  assert.equal(X.eta(1000, 3000, 0), null);
  assert.equal(X.eta(4000, 3000, 10), 0);
  assert.equal(X.fmtEta(42), '42s');
  assert.equal(X.fmtEta(185), '3m 05s');
  assert.equal(X.fmtEta(3720), '1h 02m');
  assert.equal(X.fmtEta(null), '—');
  assert.equal(X.fmtRate(0), '0 rows/s');
  assert.equal(X.fmtRate(850.4), '850 rows/s');
  assert.equal(X.fmtRate(12345), '12.3k rows/s');
  assert.equal(X.fmtRate(2500000), '2.50M rows/s');
  assert.equal(X.fmtDuration('2026-09-11T12:00:00Z', '2026-09-11T12:03:05Z'), '3m 05s');
  assert.equal(X.fmtDuration('2026-09-11T12:00:00Z', null, Date.parse('2026-09-11T12:00:42Z')), '42s');
  assert.equal(X.fmtDuration(null, null), '—');
});

test('aggregate and mergeProgress combine task rows with live snapshots', () => {
  const tasks = [
    { taskId: 'T01', target: 'app.A', status: 'done', rowsDone: 100, rowsSource: 100, rowsError: 0, validation: { countMatch: true } },
    { taskId: 'T02', target: 'app.B', status: 'running', rowsDone: 10, rowsSource: 50, rowsError: 1 },
    { taskId: 'T03', target: 'app.C', status: 'pending', rowsDone: 0, rowsSource: null, rowsError: 0 },
  ];
  assert.deepEqual(X.aggregate(tasks), { done: 110, total: 150, errors: 1, tasksDone: 1, tasksTotal: 3, running: 1, failed: 0, paused: 0 });
  const merged = X.mergeProgress(tasks, { runId: 1, status: 'running', tasks: [{ taskId: 'T02', status: 'running', rowsDone: 40, rowsSource: 50, rowsError: 2, rowsPerSec: 12.5 }], overall: {} });
  assert.equal(merged[1].rowsDone, 40);
  assert.equal(merged[1].rowsError, 2);
  assert.equal(merged[1].rowsPerSec, 12.5);
  assert.equal(merged[0], tasks[0]);                        // untouched rows are reused
  assert.deepEqual(merged[0].validation, { countMatch: true });
  assert.equal(tasks[1].rowsDone, 10);                      // input not mutated
  assert.equal(X.countText(merged[1]), '40 / 50');
});

test('controls follow run status and activity', () => {
  assert.deepEqual(X.controls({ active: true, canStart: false, run: { status: 'running' } }), { pause: true, resume: false, cancel: true, start: false });
  assert.deepEqual(X.controls({ active: false, canStart: false, run: { status: 'paused' } }), { pause: false, resume: true, cancel: true, start: false });
  assert.deepEqual(X.controls({ active: false, canStart: true, run: { status: 'failed' } }), { pause: false, resume: true, cancel: true, start: true });
  assert.deepEqual(X.controls({ active: false, canStart: true, run: { status: 'cancelled' } }), { pause: false, resume: false, cancel: false, start: true });
  assert.deepEqual(X.controls({ active: false, canStart: true, run: null }), { pause: false, resume: false, cancel: false, start: true });
});

test('pre-flight tones and counts', () => {
  const checks = [
    { name: 'a', ok: true, severity: 'info', detail: '' },
    { name: 'b', ok: false, severity: 'warning', detail: '' },
    { name: 'c', ok: false, severity: 'error', detail: '' },
  ];
  assert.deepEqual(checks.map(X.checkTone), ['ok', 'warn', 'err']);
  assert.deepEqual(X.preflightCounts({ checks }), { errors: 1, warnings: 1 });
});

test('key, json, options and checksum text', () => {
  assert.equal(X.keyText('{"__k0":777,"__k1":"a"}'), '__k0=777, __k1=a');
  assert.equal(X.keyText(null), '(no key)');
  assert.equal(X.prettyJson('{"a":1}'), '{\n  "a": 1\n}');
  assert.equal(X.prettyJson('not json'), 'not json');
  assert.equal(X.optionsKey({ chunkSize: 5, parallelism: 2, errorMode: 'skip' }),
    'chunkSize=5;parallelism=2;errorMode=skip;truncateTarget=undefined;tableLock=undefined;validateChecksums=undefined;fireTriggers=undefined;keepControlTable=undefined');
  assert.equal(X.checksumText([{ match: true }, { match: false }], null), '1/2 match');
  assert.equal(X.checksumText([], 'rows were rejected'), 'skipped');
  assert.equal(X.checksumText(undefined, undefined), '—');
});
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/Transfer/ReportExportTests.cs`:

```csharp
using System.Text;
using Dbm.Core;
using Dbm.Core.State;
using Dbm.Tests.Support;
using Dbm.Web;
using Dbm.Web.Endpoints;
using Xunit;

namespace Dbm.Tests.Unit.Transfer;

public sealed class ReportExportTests
{
    [Fact]
    public async Task Report_export_is_unavailable_before_completion_and_a_standalone_page_afterwards()
    {
        using var svc = new XferServices();
        var s = svc.Services;
        var broadcaster = new Broadcaster(s.Events);
        var state = new WebState
        {
            Services = s, Broadcaster = broadcaster, Presence = new AgentPresence(s, broadcaster), Info = new ServerInfo(1, 1, "token", Clock.Now()),
        };
        var assets = WebExport.LocateAssets();
        Assert.Null(ReportExport.LatestPayload(s));
        await Assert.ThrowsAsync<ExportException>(() => ReportExport.BuildAsync(state, assets, default));

        s.Artifacts.Add(PhaseName.Complete, 1, "{\"runId\":4,\"status\":\"completed\"}", "script", "done");
        Assert.Equal(4, ReportExport.LatestPayload(s)!["runId"]!.GetValue<int>());
        var file = await ReportExport.BuildAsync(state, assets, default);
        Assert.Equal(ReportExport.FileName, file.FileName);
        string html = Encoding.UTF8.GetString(file.Content);
        Assert.Contains("window.DBM_EXPORT", html);
        Assert.Contains("\"view\":\"report\"", html);
        Assert.Contains("\"artifact\":{\"version\":1", html);
        Assert.Contains("DBM.views.complete = view", html);   // views/report.js inlined
        Assert.Contains("DBM.xfer = X", html);               // lib/xfer.js inlined (all lib/*)
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `node --test plugins/db-migrate/engine/Dbm.Tests/js/xfer.test.cjs`
Expected: FAIL with `ENOENT: no such file or directory, open '…/Dbm/wwwroot/js/lib/xfer.js'`.
Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~ReportExportTests"`
Expected: build FAILS with `CS0103: The name 'ReportExport' does not exist in the current context`.

- [ ] **Step 3: Implement the helpers and the export payload**

`plugins/db-migrate/engine/Dbm/wwwroot/js/lib/xfer.js`:

```js
/* Transfer math + formatting shared by the Execute and Final report views. Pure functions; also loaded by node --test. */
(function (DBM) {
  'use strict';
  const X = {};
  const OPTION_KEYS = ['chunkSize', 'parallelism', 'errorMode', 'truncateTarget', 'tableLock', 'validateChecksums', 'fireTriggers', 'keepControlTable'];

  function pad2(n) { return n < 10 ? '0' + n : String(n); }

  X.num = function (n) {
    if (n === null || n === undefined || n === '' || !isFinite(Number(n))) return '—';
    return Math.round(Number(n)).toLocaleString('en-US');
  };

  X.ratio = function (done, total) {
    if (!total || total <= 0) return done > 0 ? 1 : 0;
    return Math.max(0, Math.min(1, done / total));
  };

  X.pct = function (done, total) { return Math.floor(X.ratio(done, total) * 100); };

  /** Rows/second between the oldest sample inside the window (default 10 s) and the newest one. */
  X.rate = function (samples, windowMs) {
    const w = windowMs || 10000;
    if (!samples || samples.length < 2) return 0;
    const last = samples[samples.length - 1];
    let first = samples[samples.length - 2];
    for (let i = 0; i < samples.length - 1; i++) {
      if (last.t - samples[i].t <= w) { first = samples[i]; break; }
    }
    const dt = (last.t - first.t) / 1000;
    return dt > 0 ? Math.max(0, (last.v - first.v) / dt) : 0;
  };

  X.pushSample = function (samples, t, v, max) {
    samples.push({ t: t, v: v });
    const cap = max || 120;
    while (samples.length > cap) samples.shift();
    return samples;
  };

  X.eta = function (done, total, rate) {
    if (!rate || rate <= 0 || !total) return null;
    return Math.round(Math.max(0, total - done) / rate);
  };

  X.fmtEta = function (sec) {
    if (sec === null || sec === undefined || !isFinite(sec)) return '—';
    const s = Math.max(0, Math.round(sec));
    if (s < 60) return s + 's';
    if (s < 3600) return Math.floor(s / 60) + 'm ' + pad2(s % 60) + 's';
    return Math.floor(s / 3600) + 'h ' + pad2(Math.floor((s % 3600) / 60)) + 'm';
  };

  X.fmtRate = function (r) {
    const v = Math.max(0, Number(r) || 0);
    if (v < 1000) return Math.round(v) + ' rows/s';
    if (v < 1e6) return (v / 1000).toFixed(1) + 'k rows/s';
    return (v / 1e6).toFixed(2) + 'M rows/s';
  };

  X.fmtDuration = function (startIso, endIso, nowMs) {
    if (!startIso) return '—';
    const start = Date.parse(startIso);
    const end = endIso ? Date.parse(endIso) : (nowMs || Date.now());
    return X.fmtEta((end - start) / 1000);
  };

  X.aggregate = function (tasks) {
    const a = { done: 0, total: 0, errors: 0, tasksDone: 0, tasksTotal: 0, running: 0, failed: 0, paused: 0 };
    (tasks || []).forEach(function (t) {
      a.done += t.rowsDone || 0;
      a.total += t.rowsSource || 0;
      a.errors += t.rowsError || 0;
      a.tasksTotal += 1;
      if (t.status === 'done') a.tasksDone += 1;
      if (t.status === 'running') a.running += 1;
      if (t.status === 'failed') a.failed += 1;
      if (t.status === 'paused') a.paused += 1;
    });
    return a;
  };

  /** Returns a new task array with counters from a transfer_progress snapshot; rows without news are reused as-is. */
  X.mergeProgress = function (tasks, snapshot) {
    if (!snapshot || !snapshot.tasks) return tasks;
    const byId = {};
    snapshot.tasks.forEach(function (t) { byId[t.taskId] = t; });
    return tasks.map(function (t) {
      const u = byId[t.taskId];
      if (!u) return t;
      return Object.assign({}, t, {
        rowsDone: u.rowsDone,
        rowsError: u.rowsError,
        rowsSource: u.rowsSource === null || u.rowsSource === undefined ? t.rowsSource : u.rowsSource,
        status: u.status || t.status,
        rowsPerSec: u.rowsPerSec,
      });
    });
  };

  X.controls = function (view) {
    const run = view && view.run;
    const s = run ? run.status : null;
    const active = !!(view && view.active);
    return {
      pause: active && s === 'running',
      resume: !active && (s === 'paused' || s === 'failed'),
      cancel: !!run && (active ? s === 'running' : (s === 'paused' || s === 'failed')),
      start: !!(view && view.canStart),
    };
  };

  X.checkTone = function (c) { return c.ok ? 'ok' : (c.severity === 'error' ? 'err' : 'warn'); };

  X.preflightCounts = function (result) {
    const r = { errors: 0, warnings: 0 };
    ((result && result.checks) || []).forEach(function (c) {
      if (c.ok) return;
      if (c.severity === 'error') r.errors += 1; else r.warnings += 1;
    });
    return r;
  };

  X.countText = function (t) { return X.num(t.rowsDone) + ' / ' + X.num(t.rowsSource); };

  X.keyText = function (keyJson) {
    if (!keyJson) return '(no key)';
    try {
      const o = JSON.parse(keyJson);
      return Object.keys(o).map(function (k) { return k + '=' + o[k]; }).join(', ');
    } catch (e) {
      return String(keyJson);
    }
  };

  X.prettyJson = function (text) {
    try { return JSON.stringify(JSON.parse(text), null, 2); } catch (e) { return String(text); }
  };

  X.optionsKey = function (o) {
    return OPTION_KEYS.map(function (k) { return k + '=' + (o ? o[k] : undefined); }).join(';');
  };

  X.checksumText = function (checksums, skipped) {
    if (checksums && checksums.length) {
      const ok = checksums.filter(function (c) { return c.match; }).length;
      return ok + '/' + checksums.length + ' match';
    }
    return skipped ? 'skipped' : '—';
  };

  DBM.xfer = X;
})(globalThis.DBM = globalThis.DBM || {});
```

`plugins/db-migrate/engine/Dbm/Web/Endpoints/ReportExport.cs`:

```csharp
using System.Text;
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.State;

namespace Dbm.Web.Endpoints;

/// <summary>The "report" export (T2.8 registry; registered by TransferEndpoints.Map): the Final report screen as one offline HTML file.</summary>
public static class ReportExport
{
    public const string FileName = "final-report.html";

    /// <summary>Payload of the latest Complete artifact (the FinalReport JSON), or null before the first completed run.</summary>
    public static JsonNode? LatestPayload(DbmServices s)
        => s.Artifacts.Latest(PhaseName.Complete) is { } artifact ? JsonNode.Parse(artifact.PayloadJson) : null;

    /// <summary>
    /// ExportEndpoints.Builder. Payload = the envelope app.js export mode reads:
    /// {project, exportedAt, artifact:{version, author, summary, createdAt, payload}}. ExportException (→ 404) before completion.
    /// </summary>
    public static Task<ExportFile> BuildAsync(WebState state, IWebAssets assets, CancellationToken ct)
    {
        var s = state.Services;
        var row = s.Artifacts.Latest(PhaseName.Complete) ?? throw new ExportException("No transfer run has completed yet.");
        var project = s.Project.Get().Name;
        var data = new JsonObject
        {
            ["project"] = project,
            ["exportedAt"] = Clock.NowText(),
            ["artifact"] = new JsonObject
            {
                ["version"] = row.Version,
                ["author"] = row.Author,
                ["summary"] = row.Summary,
                ["createdAt"] = row.CreatedAt.ToString("O"),
                ["payload"] = JsonNode.Parse(row.PayloadJson),
            },
        };
        var html = WebExport.BuildHtml(assets, "report", $"{project} — Final report", data);
        return Task.FromResult(new ExportFile(FileName, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html)));
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `node --test plugins/db-migrate/engine/Dbm.Tests/js/xfer.test.cjs`
Expected: PASS — `# pass 8`, `# fail 0`.
Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~ReportExportTests"`
Expected: PASS — 1 test.

- [ ] **Step 5: Implement the Execute view**

`plugins/db-migrate/engine/Dbm/wwwroot/js/views/execute.js`:

```js
/* Execute screen (phases Ready + Transfer): pre-flight, options, typed confirmation, live progress, pause/resume/cancel. */
(function (DBM) {
  'use strict';
  DBM.views = DBM.views || {};
  const h = DBM.h;
  const X = DBM.xfer;

  const CHECK_LABELS = {
    sql_plan: 'Approved SQL plan',
    plan_valid: 'Plan validation',
    source_connection: 'Source connection',
    target_connection: 'Target connection',
    schema_drift: 'Schemas unchanged since discovery',
    target_tables: 'Target tables exist',
    insert_permission: 'INSERT permission',
    identity_insert_permission: 'Identity insert permission',
    truncate_permission: 'Truncate permission',
    control_table: 'Checkpoint table',
    target_rows: 'Target row counts',
    estimated_rows: 'Estimated volume',
    keyless_tasks: 'Tasks without a key',
  };

  let S = null;

  function clear(el) { while (el.firstChild) el.removeChild(el.firstChild); }
  function errText(e) { return e && e.message ? e.message : String(e); }
  function apiMessage(e) { return errText(e) + (e && e.details && e.details.length ? ' — ' + e.details.join('; ') : ''); }
  function payloadOf(evt) {
    if (!evt) return {};
    const p = evt.data !== undefined ? evt.data : evt.payload;
    if (typeof p === 'string') { try { return JSON.parse(p); } catch (e) { return {}; } }
    return p || {};
  }
  function setBar(el, ratio) {
    const v = Math.max(0, Math.min(1, ratio || 0));
    el.style.setProperty('--v', v.toFixed(4));
    el.setAttribute('aria-valuenow', String(Math.round(v * 100)));
  }
  function bar(ratio, extra) {
    const el = h('div', { class: 'bar' + (extra ? ' ' + extra : ''), role: 'progressbar', 'aria-valuemin': '0', 'aria-valuemax': '100' });
    setBar(el, ratio);
    return el;
  }
  function kpi(label, value, sub) {
    const v = h('div', { class: 'kpi-v num' }, value);
    const s = h('div', { class: 'kpi-s' }, sub || '');
    return { el: h('div', { class: 'kpi' }, h('div', { class: 'kpi-l' }, label), v, s), v: v, s: s };
  }

  function render(root, ctx) {
    S = { root: root, ctx: ctx, data: null, options: null, preflight: null, preflightKey: null, samples: [], rateHistory: [],
      log: [], rowEls: {}, live: null, logEl: null, busy: false, reloadTimer: 0, frame: 0, sparkAt: 0, lastOverall: null };
    clear(root);
    root.appendChild(h('div', { class: 'page exe-page' }, h('div', { class: 'empty' }, 'Loading transfer…')));
    reload();
  }

  function reload() {
    const mine = S;
    return DBM.api.get('/api/transfer').then(function (v) {
      if (S !== mine) return;
      S.data = v;
      if (!S.options) S.options = Object.assign({}, v.defaults || {});
      if (!S.preflight && v.preflight) { S.preflight = v.preflight; S.preflightKey = null; }
      paint();
    }).catch(function (e) { if (S === mine) S.ctx.toast('Could not load the transfer: ' + errText(e), 'err'); });
  }

  function scheduleReload() {
    clearTimeout(S.reloadTimer);
    S.reloadTimer = setTimeout(reload, 150);
  }

  function paint() {
    const d = S.data;
    const run = d.run;
    const page = h('div', { class: 'page exe-page' },
      h('div', { class: 'page-h row' },
        h('div', { class: 'stack' },
          h('div', { class: 'h1' }, 'Execute transfer'),
          h('div', { class: 'muted small' }, 'Target ', h('span', { class: 'mono' }, d.targetDatabase || '—'),
            d.sqlVersion ? ' · SQL plan v' + d.sqlVersion : '', ' · ' + d.tasks.length + ' tasks')),
        h('div', { class: 'spacer' }),
        DBM.components.badge(run ? run.status : 'pending')));
    S.rowEls = {};
    S.live = null;
    S.logEl = null;
    if (run) page.appendChild(liveSection());
    if (!S.ctx.export && d.canStart) {
      if (run) page.appendChild(h('div', { class: 'h2 exe-section-title' }, 'Start a new run'));
      page.appendChild(h('div', { class: 'exe-grid' }, preflightCard(), optionsCard()));
    }
    if (!run) page.appendChild(tasksCard());
    clear(S.root);
    S.root.appendChild(page);
  }

  /* ---------- pre-flight + options ---------- */

  function preflightCard() {
    const pf = S.preflight;
    const list = h('ul', { class: 'exe-checks' });
    if (pf) pf.checks.forEach(function (c) { list.appendChild(checkItem(c)); });
    const btn = h('button', { class: 'btn', type: 'button', on: { click: runPreflight } }, pf ? 'Re-run pre-flight' : 'Run pre-flight');
    if (S.busy === 'preflight') { btn.disabled = true; btn.classList.add('is-loading'); btn.textContent = 'Checking…'; }
    return h('section', { class: 'card' },
      h('div', { class: 'card-h row' }, h('div', { class: 'h3' }, 'Pre-flight checklist'), h('div', { class: 'spacer' }), btn),
      h('div', { class: 'card-b stack' }, preflightSummary(pf), list));
  }

  function preflightSummary(pf) {
    if (!pf) return h('div', { class: 'muted small' },
      'Checks connectivity, schema drift, the approved SQL version, permissions, existing target rows and the volume to move.');
    const n = X.preflightCounts(pf);
    const text = pf.passed
      ? 'Ready to execute' + (n.warnings ? ' · ' + n.warnings + (n.warnings === 1 ? ' warning' : ' warnings') : '')
      : n.errors + (n.errors === 1 ? ' blocking problem' : ' blocking problems');
    return h('div', { class: 'row' },
      h('span', { class: 'badge ' + (pf.passed ? (n.warnings ? 'st-paused' : 'st-done') : 'st-failed') }, text),
      h('span', { class: 'muted small' }, 'checked ' + (DBM.fmt && DBM.fmt.rel ? DBM.fmt.rel(pf.at) : pf.at)));
  }

  function checkItem(c) {
    const tone = X.checkTone(c);
    return h('li', { class: 'exe-check' },
      h('span', { class: 'exe-check-icon is-' + tone, role: 'img', 'aria-label': tone === 'ok' ? 'passed' : tone === 'warn' ? 'warning' : 'failed' },
        tone === 'ok' ? '✓' : tone === 'warn' ? '!' : '✕'),
      h('div', {}, h('div', { class: 'exe-check-name' }, CHECK_LABELS[c.name] || c.name), h('div', { class: 'exe-check-detail' }, c.detail)));
  }

  function runPreflight() {
    S.busy = 'preflight';
    paint();
    const key = X.optionsKey(S.options);
    DBM.api.post('/api/transfer/preflight', { options: S.options })
      .then(function (r) { S.preflight = r; S.preflightKey = key; })
      .catch(function (e) { S.ctx.toast('Pre-flight could not run: ' + apiMessage(e), 'err'); })
      .then(function () { S.busy = false; paint(); });
  }

  function optionsCard() {
    const o = S.options;
    const set = function (k, v) { o[k] = v; updateExecuteState(); };
    const numberField = function (id, label, key, min, max, step, hint) {
      const input = h('input', { class: 'input', type: 'number', id: id, min: String(min), max: String(max), step: String(step), value: String(o[key]),
        on: { change: function (e) { const v = Math.min(max, Math.max(min, parseInt(e.target.value, 10) || min)); e.target.value = String(v); set(key, v); } } });
      return h('div', { class: 'exe-field' }, h('label', { for: id }, label), input, h('div', { class: 'muted small' }, hint));
    };
    const radio = function (value, label, hint) {
      const input = h('input', { type: 'radio', name: 'exe-errmode', value: value, on: { change: function () { set('errorMode', value); } } });
      input.checked = o.errorMode === value;
      return h('label', { class: 'exe-choice' }, input, h('span', { class: 'stack' }, h('span', {}, label), h('span', { class: 'muted small' }, hint)));
    };
    const check = function (key, label, hint, danger) {
      const input = h('input', { type: 'checkbox', class: 'check', on: { change: function (e) { set(key, e.target.checked); } } });
      input.checked = !!o[key];
      return h('label', { class: 'exe-choice' }, input,
        h('span', { class: 'stack' }, h('span', {}, label), h('span', { class: 'small ' + (danger ? 'exe-danger-note' : 'muted') }, hint)));
    };
    S.execBtn = h('button', { class: 'btn btn-primary', type: 'button', on: { click: confirmAndStart } }, 'Execute…');
    S.execHint = h('div', { class: 'muted small' });
    const card = h('section', { class: 'card' },
      h('div', { class: 'card-h' }, h('div', { class: 'h3' }, 'Options')),
      h('div', { class: 'card-b exe-form' },
        h('div', { class: 'grid-2' },
          numberField('exe-chunk', 'Chunk size (rows)', 'chunkSize', 1, 10000000, 1000, 'Rows per target transaction and checkpoint.'),
          numberField('exe-par', 'Parallel tasks', 'parallelism', 1, 32, 1, 'Independent tables load at the same time.')),
        h('fieldset', { class: 'exe-fieldset' }, h('legend', { class: 'exe-legend' }, 'When a row is rejected'),
          radio('stop', 'Stop at the first bad row', 'The chunk is rolled back; fix the data and resume.'),
          radio('skip', 'Skip and log bad rows', 'Bad rows are isolated by bisection, logged and counted; the rest loads.')),
        h('fieldset', { class: 'exe-fieldset' }, h('legend', { class: 'exe-legend' }, 'Advanced'),
          check('truncateTarget', 'Truncate target first', 'Deletes every existing row in the ' + S.data.tasks.length + ' target tables before loading.', true),
          check('validateChecksums', 'Validate column checksums', 'Compares per-column checksums when a target table started empty.'),
          check('tableLock', 'Table lock', 'Faster bulk loads; blocks other readers of the target tables.'),
          check('fireTriggers', 'Fire target triggers', 'Off by default: the plan’s pre/post scripts handle trigger logic.'),
          check('keepControlTable', 'Keep the checkpoint table', 'Leaves dbo.__dbm_checkpoint in the target after completion.')),
        h('div', { class: 'toolbar' }, S.execBtn, S.execHint)));
    updateExecuteState();
    return card;
  }

  function updateExecuteState() {
    if (!S.execBtn) return;
    const pf = S.preflight;
    let hint = '';
    let enabled = !!(S.data && S.data.canStart) && S.busy === false;
    if (!pf) { enabled = false; hint = 'Run pre-flight first.'; }
    else if (!pf.passed) { enabled = false; hint = 'Resolve the blocking pre-flight problems first.'; }
    else if (S.preflightKey && S.preflightKey !== X.optionsKey(S.options)) hint = 'Options changed since pre-flight; it re-runs automatically when you execute.';
    S.execBtn.disabled = !enabled;
    S.execBtn.classList.toggle('is-disabled', !enabled);
    S.execHint.textContent = hint;
  }

  function confirmAndStart() {
    const d = S.data;
    const o = S.options;
    const body = h('div', { class: 'stack' },
      h('p', {}, 'Load ', h('strong', {}, d.tasks.length + ' tables'), ' into ', h('span', { class: 'mono' }, d.targetDatabase), '.'),
      h('ul', { class: 'small' },
        h('li', {}, 'Chunk size ' + X.num(o.chunkSize) + ', ' + o.parallelism + ' parallel tasks'),
        h('li', {}, o.errorMode === 'skip' ? 'Bad rows are skipped and logged' : 'Stops at the first bad row'),
        o.truncateTarget ? h('li', { class: 'exe-danger-note' }, 'All existing rows in the target tables are deleted first') : ''),
      h('p', { class: 'small muted' }, 'Type the target database name to confirm.'));
    DBM.components.modal({ title: 'Start the transfer?', body: body, confirmText: 'Start transfer', requireText: d.targetDatabase })
      .then(function (ok) {
        if (!ok) return null;
        S.busy = 'start';
        updateExecuteState();
        return DBM.api.post('/api/transfer/start', { options: o, confirmTarget: d.targetDatabase })
          .then(function (r) {
            S.samples = [];
            S.rateHistory = [];
            S.log = [];
            S.ctx.toast('Transfer run #' + r.runId + ' started', 'ok');
          })
          .catch(function (e) { S.ctx.toast(apiMessage(e), 'err'); })
          .then(function () { S.busy = false; return reload(); });
      });
  }

  /* ---------- live run ---------- */

  function liveSection() {
    const d = S.data;
    const agg = X.aggregate(d.tasks);
    const rate = X.rate(S.samples);
    const wrap = h('div', { class: 'stack' });
    const banner = runBanner(d.run, d);
    if (banner) wrap.appendChild(banner);

    const kDone = kpi('Rows loaded', X.num(agg.done), 'of ' + X.num(agg.total) + ' · ' + X.pct(agg.done + agg.errors, agg.total) + '%');
    const kRate = kpi('Throughput', d.active ? X.fmtRate(rate) : '—', 'last 10 seconds');
    const kEta = kpi('Time left', d.active ? X.fmtEta(X.eta(agg.done + agg.errors, agg.total, rate)) : '—',
      'started ' + (d.run.startedAt ? (DBM.fmt && DBM.fmt.ts ? DBM.fmt.ts(d.run.startedAt) : d.run.startedAt) : '—'));
    const kErr = kpi('Rejected rows', X.num(agg.errors), d.run.options.errorMode === 'skip' ? 'skipped and logged' : 'stop on first error');
    const overall = bar(X.ratio(agg.done + agg.errors, agg.total), 'exe-bar-lg');
    const spark = h('div', { class: 'exe-spark', 'aria-label': 'Throughput over time' });
    if (S.rateHistory.length > 1) spark.appendChild(DBM.components.sparkline(S.rateHistory.map(function (s) { return s.v; })));
    S.live = { done: kDone, rate: kRate, eta: kEta, err: kErr, bar: overall, spark: spark };

    wrap.appendChild(h('section', { class: 'card' },
      h('div', { class: 'card-b stack' },
        h('div', { class: 'grid-kpi' }, kDone.el, kRate.el, kEta.el, kErr.el),
        h('div', { class: 'exe-live-head' }, h('div', { class: 'exe-overall' }, overall), spark),
        controlsBar())));
    wrap.appendChild(tasksCard());
    wrap.appendChild(logCard());
    return wrap;
  }

  function runBanner(run, d) {
    if (run.status === 'completed') {
      return h('div', { class: 'exe-banner is-ok', role: 'status' }, h('strong', {}, 'Transfer completed. '),
        'Validation finished — open ', h('strong', {}, 'Complete'), ' in the stepper for the final report.');
    }
    if (run.status === 'failed') {
      const failed = d.tasks.filter(function (t) { return t.status === 'failed'; });
      const msg = run.error || (failed.length ? failed[0].taskId + ' ' + failed[0].target + ': ' + failed[0].error : 'See the log.');
      return h('div', { class: 'exe-banner is-err', role: 'alert' }, h('strong', {}, 'Transfer failed. '), msg,
        h('div', { class: 'small muted' }, 'Resume retries from the last committed checkpoint; Cancel abandons the run.'));
    }
    if (run.status === 'paused') {
      return h('div', { class: 'exe-banner is-warn', role: 'status' }, h('strong', {}, 'Paused. '),
        'Committed chunks are safe; Resume continues exactly where each task stopped.');
    }
    if (run.status === 'cancelled') {
      return h('div', { class: 'exe-banner', role: 'status' }, h('strong', {}, 'Cancelled. '),
        'Rows already committed remain in the target. You can start a new run below.');
    }
    return null;
  }

  function controlsBar() {
    const c = X.controls(S.data);
    const tb = h('div', { class: 'toolbar' });
    if (S.ctx.export) return tb;
    if (c.pause) tb.appendChild(h('button', { class: 'btn', type: 'button', on: { click: function () { act('pause'); } } }, 'Pause'));
    if (c.resume) tb.appendChild(h('button', { class: 'btn btn-primary', type: 'button', on: { click: function () { act('resume'); } } }, 'Resume'));
    if (c.cancel) tb.appendChild(h('button', { class: 'btn btn-danger', type: 'button', on: { click: confirmCancel } }, 'Cancel run'));
    if (S.data.active) tb.appendChild(h('span', { class: 'muted small' }, 'Pause and cancel take effect after the current chunks commit.'));
    return tb;
  }

  function act(what) {
    DBM.api.post('/api/transfer/' + what, {})
      .then(function () {
        S.ctx.toast(what === 'pause' ? 'Pausing after the current chunks…' : what === 'resume' ? 'Resuming from the last checkpoints' : 'Cancelling…', 'info');
        scheduleReload();
      })
      .catch(function (e) { S.ctx.toast(apiMessage(e), 'err'); });
  }

  function confirmCancel() {
    DBM.components.modal({
      title: 'Cancel this run?',
      body: h('p', {}, 'Running tasks stop after their current chunk. Rows already committed stay in the target and the run cannot be resumed.'),
      confirmText: 'Cancel run',
    }).then(function (ok) { if (ok) act('cancel'); });
  }

  function tasksCard() {
    const d = S.data;
    const tbody = h('tbody', {});
    d.tasks.forEach(function (t, i) { tbody.appendChild(taskRow(t, i)); });
    const agg = X.aggregate(d.tasks);
    const heads = ['#', 'Task', 'Target', 'Status', 'Progress', 'Rows/s', 'Rejected', 'Time'];
    return h('section', { class: 'card' },
      h('div', { class: 'card-h row' }, h('div', { class: 'h3' }, 'Tasks in execution order'), h('div', { class: 'spacer' }),
        h('span', { class: 'muted small' }, agg.tasksDone + ' of ' + agg.tasksTotal + ' done')),
      h('div', { class: 'card-b exe-table-wrap' },
        h('table', { class: 'tbl tbl-compact exe-tasks' },
          h('thead', {}, h('tr', {}, ...heads.map(function (c) { return h('th', { scope: 'col' }, c); }))),
          tbody)));
  }

  function taskRow(t, i) {
    const prog = bar(X.ratio((t.rowsDone || 0) + (t.rowsError || 0), t.rowsSource));
    const count = h('span', { class: 'num small' }, X.countText(t));
    const rate = h('td', { class: 'num' }, t.status === 'running' ? X.fmtRate(t.rowsPerSec || 0) : '—');
    const errCell = h('td', { class: 'num' }, errorsControl(t));
    S.rowEls[t.taskId] = { prog: prog, count: count, rate: rate, errCell: errCell, errors: t.rowsError || 0 };
    return h('tr', {},
      h('td', { class: 'num muted' }, String(i + 1)),
      h('td', { class: 'mono' }, t.taskId),
      h('td', { class: 'mono ellipsis', title: t.target }, t.target),
      h('td', {}, DBM.components.badge(t.status), t.keyless ? h('span', { class: 'tag exe-tag', title: 'Loads in one transaction' }, 'no key') : ''),
      h('td', {}, h('div', { class: 'exe-task-progress' }, prog, count)),
      rate,
      errCell,
      h('td', { class: 'small' }, t.error
        ? h('span', { class: 'sev-high exe-task-error', title: t.error }, t.error)
        : X.fmtDuration(t.startedAt, t.endedAt)));
  }

  function errorsControl(t) {
    if (!t.rowsError) return h('span', { class: 'muted' }, '0');
    return h('button', { class: 'btn btn-ghost btn-sm exe-err-btn', type: 'button', title: 'Show rejected rows',
      on: { click: function () { openErrors(t); } } }, X.num(t.rowsError));
  }

  function logCard() {
    S.logEl = h('pre', { class: 'code exe-log', 'aria-live': 'polite' });
    paintLog();
    return h('section', { class: 'card' }, h('div', { class: 'card-h' }, h('div', { class: 'h3' }, 'Log')), h('div', { class: 'card-b' }, S.logEl));
  }

  function pushLog(level, message) {
    S.log.push({ t: new Date(), level: level, message: message });
    if (S.log.length > 200) S.log.shift();
    if (S.logEl) paintLog();
  }

  function paintLog() {
    clear(S.logEl);
    if (!S.log.length) { S.logEl.appendChild(h('span', { class: 'muted' }, 'Live messages appear here while the transfer runs.')); return; }
    S.log.slice(-80).forEach(function (l) {
      const time = l.t.toTimeString().slice(0, 8);
      S.logEl.appendChild(h('div', { class: 'exe-log-line lvl-' + l.level }, time + '  ' + (l.level + '     ').slice(0, 5) + '  ' + l.message));
    });
    S.logEl.scrollTop = S.logEl.scrollHeight;
  }

  /* ---------- rejected rows drawer ---------- */

  function openErrors(t) {
    const list = h('div', { class: 'stack' }, h('div', { class: 'muted' }, 'Loading…'));
    DBM.components.drawer.open('Rejected rows · ' + t.taskId + ' ' + t.target, list);
    DBM.api.get('/api/transfer/errors?task=' + encodeURIComponent(t.taskId) + '&limit=200')
      .then(function (rows) {
        clear(list);
        if (!rows.length) { list.appendChild(h('div', { class: 'muted' }, 'No rejected rows recorded for this task.')); return; }
        if (rows.length === 200 && t.rowsError > 200) list.appendChild(h('div', { class: 'muted small' }, 'Showing the first 200 of ' + X.num(t.rowsError) + '.'));
        rows.forEach(function (r) {
          list.appendChild(h('div', { class: 'exe-err-item stack' },
            h('div', { class: 'row' }, h('span', { class: 'mono small' }, X.keyText(r.keyJson)), h('div', { class: 'spacer' }),
              h('span', { class: 'muted small' }, DBM.fmt && DBM.fmt.ts ? DBM.fmt.ts(r.ts) : r.ts)),
            h('div', { class: 'small' }, r.error),
            r.rowJson ? h('pre', { class: 'code exe-row-json' }, X.prettyJson(r.rowJson)) : ''));
        });
      })
      .catch(function (e) { clear(list); list.appendChild(h('div', { class: 'sev-high' }, errText(e))); });
  }

  /* ---------- live updates ---------- */

  function onProgress(p) {
    if (!S.data || !S.data.run || p.runId !== S.data.run.id) return;
    S.data.tasks = X.mergeProgress(S.data.tasks, p);
    const now = Date.now();
    if (p.overall) {
      X.pushSample(S.samples, now, p.overall.done);
      X.pushSample(S.rateHistory, now, p.overall.rowsPerSec || 0, 60);
      S.lastOverall = p.overall;
    }
    if (!S.frame) S.frame = requestAnimationFrame(function () { S.frame = 0; updateLive(); });
  }

  function updateLive() {
    const d = S.data;
    if (!d || !S.live) return;
    const agg = X.aggregate(d.tasks);
    const o = S.lastOverall || {};
    const total = o.total || agg.total;
    const errors = o.rowsError !== undefined ? o.rowsError : agg.errors;
    const done = o.done !== undefined ? o.done : agg.done;
    const rate = o.rowsPerSec !== undefined ? o.rowsPerSec : X.rate(S.samples);
    const eta = o.etaSec !== undefined ? o.etaSec : X.eta(done + errors, total, rate);
    S.live.done.v.textContent = X.num(done);
    S.live.done.s.textContent = 'of ' + X.num(total) + ' · ' + X.pct(done + errors, total) + '%';
    S.live.rate.v.textContent = X.fmtRate(rate);
    S.live.eta.v.textContent = X.fmtEta(eta);
    S.live.err.v.textContent = X.num(errors);
    setBar(S.live.bar, X.ratio(done + errors, total));
    if (Date.now() - S.sparkAt > 1000 && S.rateHistory.length > 1) {
      S.sparkAt = Date.now();
      clear(S.live.spark);
      S.live.spark.appendChild(DBM.components.sparkline(S.rateHistory.map(function (s) { return s.v; })));
    }
    d.tasks.forEach(function (t) {
      const r = S.rowEls[t.taskId];
      if (!r) return;
      setBar(r.prog, X.ratio((t.rowsDone || 0) + (t.rowsError || 0), t.rowsSource));
      r.count.textContent = X.countText(t);
      r.rate.textContent = t.status === 'running' ? X.fmtRate(t.rowsPerSec || 0) : '—';
      if ((t.rowsError || 0) !== r.errors) {
        r.errors = t.rowsError || 0;
        clear(r.errCell);
        r.errCell.appendChild(errorsControl(t));
      }
    });
  }

  function onEvent(evt) {
    if (!S || !S.data) return;
    const type = evt && (evt.type || evt.event);
    const p = payloadOf(evt);
    if (type === 'transfer_progress') onProgress(p);
    else if (type === 'transfer_task_changed' || type === 'transfer_run_changed') scheduleReload();
    else if (type === 'log') pushLog(p.level || 'info', p.message || '');
  }

  const view = { title: 'Execute', render: render, onEvent: onEvent };
  DBM.views.ready = view;
  DBM.views.transfer = view;
  DBM.views.execute = view;
})(window.DBM = window.DBM || {});
```

- [ ] **Step 6: Implement the Final report view**

`plugins/db-migrate/engine/Dbm/wwwroot/js/views/report.js`:

```js
/* Final report (phase Complete; also the standalone export view "report"). Read-only rendering of the FinalReport payload. */
(function (DBM) {
  'use strict';
  DBM.views = DBM.views || {};
  const h = DBM.h;
  const X = DBM.xfer;

  function clear(el) { while (el.firstChild) el.removeChild(el.firstChild); }
  function fmtTs(s) { return !s ? '—' : (DBM.fmt && DBM.fmt.ts ? DBM.fmt.ts(s) : new Date(s).toLocaleString()); }

  /** Live view: ctx.artifact = {version, …, payload: report}. Standalone export: window.DBM_EXPORT.payload is the report. */
  function reportOf(ctx) {
    const a = ctx && ctx.artifact;
    if (a && typeof a === 'object') {
      if (a.payload && typeof a.payload === 'object' && 'runId' in a.payload) return a.payload;
      if ('runId' in a) return a;
    }
    const e = typeof window !== 'undefined' && window.DBM_EXPORT;
    if (e && e.payload && typeof e.payload === 'object' && 'runId' in e.payload) return e.payload;
    return null;
  }

  function kpi(label, value, sub, tone) {
    return h('div', { class: 'kpi' + (tone ? ' rep-kpi-' + tone : '') },
      h('div', { class: 'kpi-l' }, label), h('div', { class: 'kpi-v num' }, value), h('div', { class: 'kpi-s' }, sub || ''));
  }

  function matchMark(v) {
    if (v === true) return h('span', { class: 'rep-match' }, '✓ match');
    if (v === false) return h('span', { class: 'rep-mismatch' }, '✕ mismatch');
    return h('span', { class: 'muted' }, '—');
  }

  function checksumCell(t) {
    const list = t.checksums || [];
    const text = X.checksumText(list, t.checksumsSkipped);
    const bad = list.some(function (c) { return !c.match; });
    return h('span', { class: list.length ? (bad ? 'rep-mismatch' : 'rep-match') : 'muted', title: t.checksumsSkipped || '' }, text);
  }

  function render(root, ctx) {
    clear(root);
    const r = reportOf(ctx);
    if (!r) {
      root.appendChild(h('div', { class: 'page rep-page' },
        DBM.components.emptyState('No final report yet', 'The report is created when a transfer run completes. Follow the run on the Execute screen.')));
      return;
    }
    const tasks = r.tasks || [];
    const counted = tasks.filter(function (t) { return t.countMatch === true || t.countMatch === false; });
    const countOk = counted.filter(function (t) { return t.countMatch; }).length;
    const sums = tasks.reduce(function (a, t) { return a + (t.checksums || []).length; }, 0);
    const sumOk = tasks.reduce(function (a, t) { return a + (t.checksums || []).filter(function (c) { return c.match; }).length; }, 0);

    const exportBtn = ctx.export ? '' : h('button', { class: 'btn', type: 'button', on: { click: function (e) { exportReport(e.currentTarget, ctx); } } }, 'Export HTML');
    const page = h('div', { class: 'page rep-page' },
      h('div', { class: 'page-h row' },
        h('div', { class: 'stack' }, h('div', { class: 'h1' }, 'Final report'),
          h('div', { class: 'muted small' }, 'Run #' + r.runId + ' · ' + fmtTs(r.startedAt) + ' → ' + fmtTs(r.endedAt))),
        h('div', { class: 'spacer' }), DBM.components.badge(r.status), exportBtn),
      h('div', { class: 'grid-kpi' },
        kpi('Rows loaded', X.num(r.rowsLoaded), 'of ' + X.num(r.rowsSource) + ' source rows'),
        kpi('Rejected', X.num(r.rowsError), r.rowsError ? X.pct(r.rowsError, r.rowsSource) + '% of source · logged' : 'none', r.rowsError ? 'warn' : 'ok'),
        kpi('Duration', X.fmtEta(r.durationSec), X.fmtRate(r.rowsPerSec)),
        kpi('Row counts', countOk + ' / ' + counted.length, countOk === counted.length ? 'every task matches' : (counted.length - countOk) + ' mismatched',
          countOk === counted.length ? 'ok' : 'err'),
        kpi('Checksums', sums ? sumOk + ' / ' + sums : '—', sums ? (sumOk === sums ? 'every column matches' : (sums - sumOk) + ' differ') : 'not computed',
          sums ? (sumOk === sums ? 'ok' : 'err') : '')),
      tasksCard(tasks));
    const withSamples = tasks.filter(function (t) { return (t.errorSamples || []).length; });
    if (withSamples.length) page.appendChild(samplesCard(withSamples));
    page.appendChild(h('div', { class: 'grid-2' }, notesCard(r.notes || []), optionsCard(r.options || {})));
    root.appendChild(page);
  }

  function tasksCard(tasks) {
    const tbody = h('tbody', {});
    tasks.forEach(function (t) {
      const detail = h('tr', { class: 'rep-detail' }, h('td', { colspan: '10' }, taskDetail(t)));
      detail.hidden = true;
      const row = h('tr', { class: 'tr-click', tabindex: '0', 'aria-expanded': 'false' },
        h('td', { class: 'mono' }, t.taskId),
        h('td', { class: 'mono ellipsis', title: t.target }, t.target),
        h('td', {}, DBM.components.badge(t.status)),
        h('td', { class: 'num' }, X.num(t.rowsSource)),
        h('td', { class: 'num' }, X.num(t.rowsLoaded)),
        h('td', { class: 'num' + (t.rowsError ? ' rep-mismatch' : '') }, X.num(t.rowsError)),
        h('td', {}, matchMark(t.countMatch)),
        h('td', {}, checksumCell(t)),
        h('td', { class: 'num' }, X.fmtEta(t.durationSec)),
        h('td', { class: 'num' }, t.durationSec > 0 ? X.fmtRate(t.rowsLoaded / t.durationSec) : '—'));
      const toggle = function () {
        detail.hidden = !detail.hidden;
        row.setAttribute('aria-expanded', String(!detail.hidden));
      };
      row.addEventListener('click', toggle);
      row.addEventListener('keydown', function (e) { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(); } });
      tbody.appendChild(row);
      tbody.appendChild(detail);
    });
    const heads = ['Task', 'Target', 'Status', 'Source', 'Loaded', 'Rejected', 'Count', 'Checksums', 'Duration', 'Rows/s'];
    return h('section', { class: 'card' },
      h('div', { class: 'card-h row' }, h('div', { class: 'h3' }, 'Tasks'), h('div', { class: 'spacer' }),
        h('span', { class: 'muted small' }, 'Select a row for checksum details and error samples')),
      h('div', { class: 'card-b rep-table-wrap' },
        h('table', { class: 'tbl tbl-compact rep-tasks' },
          h('thead', {}, h('tr', {}, ...heads.map(function (c) { return h('th', { scope: 'col' }, c); }))), tbody)));
  }

  function taskDetail(t) {
    const box = h('div', { class: 'stack rep-detail-body' });
    if (t.error) box.appendChild(h('div', { class: 'sev-high' }, t.error));
    if ((t.checksums || []).length) {
      box.appendChild(h('table', { class: 'tbl tbl-compact rep-checksums' },
        h('thead', {}, h('tr', {}, h('th', {}, 'Column'), h('th', {}, 'Source checksum'), h('th', {}, 'Target checksum'), h('th', {}, 'Result'))),
        h('tbody', {}, ...t.checksums.map(function (c) {
          return h('tr', {}, h('td', { class: 'mono' }, c.column), h('td', { class: 'num mono' }, String(c.source)),
            h('td', { class: 'num mono' }, String(c.target)), h('td', {}, matchMark(c.match)));
        }))));
    } else {
      box.appendChild(h('div', { class: 'muted small' }, 'Checksums: ' + (t.checksumsSkipped ? 'skipped — ' + t.checksumsSkipped : 'not computed')));
    }
    if ((t.errorSamples || []).length) box.appendChild(samplesList(t.errorSamples));
    return box;
  }

  function samplesList(samples) {
    return h('ul', { class: 'rep-samples' }, ...samples.map(function (s) {
      return h('li', {}, h('span', { class: 'mono small' }, X.keyText(s.key)), h('span', { class: 'small' }, s.error));
    }));
  }

  function samplesCard(tasks) {
    return h('section', { class: 'card' },
      h('div', { class: 'card-h' }, h('div', { class: 'h3' }, 'Rejected row samples')),
      h('div', { class: 'card-b stack' }, ...tasks.map(function (t) {
        return h('div', { class: 'stack' },
          h('div', { class: 'row' }, h('span', { class: 'mono' }, t.target), h('span', { class: 'muted small' },
            X.num(t.rowsError) + ' rejected · first ' + t.errorSamples.length + ' shown')),
          samplesList(t.errorSamples));
      })));
  }

  function notesCard(notes) {
    return h('section', { class: 'card' },
      h('div', { class: 'card-h' }, h('div', { class: 'h3' }, 'Notes')),
      h('div', { class: 'card-b' }, notes.length
        ? h('ul', { class: 'rep-notes' }, ...notes.map(function (n) { return h('li', {}, n); }))
        : h('div', { class: 'muted' }, 'Nothing to note.')));
  }

  function optionsCard(o) {
    const chip = function (text) { return h('span', { class: 'chip' }, text); };
    return h('section', { class: 'card' },
      h('div', { class: 'card-h' }, h('div', { class: 'h3' }, 'Run options')),
      h('div', { class: 'card-b row-wrap' },
        chip('Chunk ' + X.num(o.chunkSize)),
        chip(o.parallelism + ' parallel'),
        chip(o.errorMode === 'skip' ? 'Skip and log bad rows' : 'Stop on first bad row'),
        chip(o.truncateTarget ? 'Target truncated first' : 'Appended to target'),
        chip(o.validateChecksums ? 'Checksums on' : 'Checksums off'),
        o.tableLock ? chip('Table lock') : '',
        o.fireTriggers ? chip('Triggers fired') : '',
        o.keepControlTable ? chip('Checkpoint table kept') : ''));
  }

  function exportReport(btn, ctx) {
    btn.disabled = true;
    fetch(DBM.api.url('/api/export/report'), { cache: 'no-store' })
      .then(function (res) {
        if (res.ok) return res.blob();
        return res.json().then(
          function (e) { throw new Error(e.message || 'Export failed (HTTP ' + res.status + ')'); },
          function () { throw new Error('Export failed (HTTP ' + res.status + ')'); });
      })
      .then(function (blob) {
        const url = URL.createObjectURL(blob);
        const a = h('a', { href: url, download: 'final-report.html' });
        document.body.appendChild(a);
        a.click();
        a.remove();
        setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
        ctx.toast('Final report exported', 'ok');
      })
      .catch(function (e) { ctx.toast(e.message || String(e), 'err'); })
      .then(function () { btn.disabled = false; });
  }

  const view = { title: 'Final report', render: render };
  DBM.views.complete = view;
  DBM.views.report = view;
})(window.DBM = window.DBM || {});
```

- [ ] **Step 7: Styles, script tags and the export route**

Append to the end of `plugins/db-migrate/engine/Dbm/wwwroot/css/app.css`:

```css
/* ---------- Execute (T5.6) ---------- */
.exe-page .exe-grid { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: 16px; align-items: start; }
.exe-page .exe-section-title { margin-top: 8px; }
.exe-checks { list-style: none; margin: 0; padding: 0; }
.exe-check { display: grid; grid-template-columns: 24px minmax(0, 1fr); gap: 8px; padding: 8px 0; border-bottom: 1px solid var(--border); }
.exe-check:last-child { border-bottom: 0; }
.exe-check-icon { width: 20px; height: 20px; border-radius: 50%; display: inline-flex; align-items: center; justify-content: center;
  font-size: 12px; font-weight: 700; color: var(--accent-contrast); }
.exe-check-icon.is-ok { background: var(--ok); }
.exe-check-icon.is-warn { background: var(--warn); }
.exe-check-icon.is-err { background: var(--err); }
.exe-check-name { font-weight: 600; }
.exe-check-detail { color: var(--text-muted); font-size: 12px; overflow-wrap: anywhere; }
.exe-form { display: grid; gap: 16px; }
.exe-field { display: grid; gap: 4px; }
.exe-field label, .exe-legend { font-size: 12px; color: var(--text-muted); }
.exe-fieldset { border: 1px solid var(--border); border-radius: var(--radius); padding: 8px 12px 12px; margin: 0; display: grid; gap: 8px; }
.exe-choice { display: flex; gap: 8px; align-items: flex-start; cursor: pointer; }
.exe-choice input { margin-top: 3px; }
.exe-danger-note { color: var(--err); }
.exe-banner { padding: 12px 16px; border-radius: var(--radius); border: 1px solid var(--border); border-left-width: 4px; background: var(--surface-2); }
.exe-banner.is-ok { border-left-color: var(--ok); }
.exe-banner.is-warn { border-left-color: var(--warn); }
.exe-banner.is-err { border-left-color: var(--err); }
.exe-live-head { display: grid; grid-template-columns: minmax(0, 1fr) 160px; gap: 16px; align-items: center; }
.exe-overall .bar { height: 12px; }
.exe-spark { min-height: 32px; }
.exe-table-wrap, .rep-table-wrap { overflow-x: auto; }
.exe-tasks td, .rep-tasks td { vertical-align: middle; }
.exe-task-progress { display: grid; grid-template-columns: minmax(80px, 1fr) auto; gap: 8px; align-items: center; min-width: 180px; }
.exe-task-error { display: inline-block; max-width: 280px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; vertical-align: bottom; }
.exe-tag { margin-left: 4px; }
.exe-err-btn { color: var(--warn); font-variant-numeric: tabular-nums; }
.exe-log { max-height: 240px; overflow: auto; margin: 0; font-size: 12px; }
.exe-log .lvl-warn { color: var(--warn); }
.exe-log .lvl-error { color: var(--err); }
.exe-err-item { border-bottom: 1px solid var(--border); padding: 8px 0; }
.exe-row-json { font-size: 12px; max-height: 200px; overflow: auto; margin: 0; }
@media (max-width: 900px) {
  .exe-page .exe-grid { grid-template-columns: 1fr; }
  .exe-live-head { grid-template-columns: 1fr; }
}

/* ---------- Final report (T5.6) ---------- */
.rep-match { color: var(--ok); }
.rep-mismatch { color: var(--err); font-weight: 600; }
.rep-kpi-ok .kpi-v { color: var(--ok); }
.rep-kpi-warn .kpi-v { color: var(--warn); }
.rep-kpi-err .kpi-v { color: var(--err); }
.rep-detail > td { background: var(--surface-2); }
.rep-detail-body { padding: 8px 4px; }
.rep-checksums { max-width: 720px; }
.rep-samples { margin: 0; padding-left: 16px; display: grid; gap: 4px; }
.rep-samples li { display: grid; grid-template-columns: minmax(120px, auto) minmax(0, 1fr); gap: 8px; }
.rep-notes { margin: 0; padding-left: 18px; display: grid; gap: 4px; }
@media (max-width: 600px) {
  .rep-samples li { grid-template-columns: 1fr; }
}
```

Modify `plugins/db-migrate/engine/Dbm/wwwroot/index.html` (keep the `src` prefix style of the neighbouring tags):
- directly after the line `  <script src="js/lib/highlight.js"></script>` insert the line `  <script src="js/lib/xfer.js"></script>`;
- directly after the line `  <script src="js/views/sql.js"></script>` insert:

```html
  <script src="js/views/execute.js"></script>
  <script src="js/views/report.js"></script>
```

Register the report export (T2.8 registry; no new route — `GET/POST /api/export/report` then work through `ExportEndpoints`). Modify `plugins/db-migrate/engine/Dbm/Web/Endpoints/TransferEndpoints.cs` — in `Map`, directly after the opening line `public static void Map(IEndpointRouteBuilder app, WebState state)` and its `{`, insert as the first statement:

```csharp
        ExportEndpoints.Register("report", ReportExport.BuildAsync);
```

- [ ] **Step 8: Run the automated checks**

Run: `node --test plugins/db-migrate/engine/Dbm.Tests/js/xfer.test.cjs` → PASS (8).
Run: `dotnet build plugins/db-migrate/engine/Dbm.sln` → 0 errors.
Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"` → PASS.

- [ ] **Step 9: Manual verification (light + dark, desktop + ~400 px)**

Set up the demo pair (after M6: `dbm demo --server "Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true"`; before M6: run the T5.7 fixture SQL by hand) and approve Analysis/Mapping/SQL, then open the UI (`dbm ui`):
- [ ] Ready → Execute screen shows the plan's tasks (pending), the pre-flight card with "Run pre-flight" and the options card; **Execute…** is disabled with "Run pre-flight first."
- [ ] Run pre-flight: every check shows ✓/!/✕, labels read naturally, details wrap at 400 px; the summary tag says "Ready to execute".
- [ ] Changing an option shows "Options changed since pre-flight…"; ticking "Truncate target first" shows the red note.
- [ ] Execute… opens the modal; the confirm button stays disabled until the exact target database name is typed; cancel closes it without side effects.
- [ ] While running: KPIs update smoothly (no flicker), the overall bar and per-task bars advance, the sparkline appears after a few seconds, the log tail scrolls, rows/s shows only for running tasks, "no key" tag on AuditEvents.
- [ ] Rejected counts turn into buttons; clicking opens the drawer with key, error and pretty row JSON; Esc and ✕ close it.
- [ ] Pause → banner "Paused", Resume button appears; Resume continues; Cancel asks for confirmation.
- [ ] Completed → green banner; the stepper shows Complete; the Final report shows KPIs, per-task table (Enter/click expands checksum details), samples, notes, options chips.
- [ ] Export HTML downloads `final-report.html`; opened offline it renders the same report read-only without the Export button.
- [ ] Dark theme (OS setting and `data-theme="dark"`): banners, check icons, match/mismatch colours and bars keep contrast; nothing uses hard-coded colours.
- [ ] 400 px wide: options and pre-flight stack, tables scroll horizontally inside their cards, no page-level horizontal scroll.
- [ ] `prefers-reduced-motion: reduce`: no animation is added by these views (bars follow the T1.9 `.bar` rules; only the drawer slide animates).

- [ ] **Step 10: Commit**

```bash
git add plugins/db-migrate/engine/Dbm/wwwroot/js/lib/xfer.js plugins/db-migrate/engine/Dbm/wwwroot/js/views/execute.js plugins/db-migrate/engine/Dbm/wwwroot/js/views/report.js plugins/db-migrate/engine/Dbm/wwwroot/css/app.css plugins/db-migrate/engine/Dbm/wwwroot/index.html plugins/db-migrate/engine/Dbm/Web/Endpoints/ReportExport.cs plugins/db-migrate/engine/Dbm/Web/Endpoints/TransferEndpoints.cs plugins/db-migrate/engine/Dbm.Tests/js/xfer.test.cjs plugins/db-migrate/engine/Dbm.Tests/Unit/Transfer/ReportExportTests.cs
git commit -F - <<'EOF'
feat(ui): execute screen with live progress and the final report view + export

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49
EOF
```

---

### Task 5.7: Full end-to-end migration test

**Files:**
- Test: `plugins/db-migrate/engine/Dbm.Tests/Integration/Transfer/EndToEndTransferTests.cs`

**Interfaces:**
- Consumes (upstream test helpers and product code):
  - `SampleDatabases.CreateAsync(scale)` → `SamplePair {Source, Target, SourceCs, TargetCs}`, `SampleDatabases.RunAsync(cs, script)`, `SampleSql.ShopV2Schema` (T2.1).
  - `SampleExtract.CatalogAsync(cs, profile)` (M2), `SampleMappings.Approved()` (T3.2), `SqlGenerator.Generate` (T4.2).
  - `TempDatabase.CreateAsync(prefix)` (T1.3), and `TransferEngine` with the rest of T5.1–T5.4.
- Produces: the `SamplePlanFixture` class fixture, defined in the test file: the sample pair plus the generated plan, and a fresh empty ShopV2 per test.

This is the milestone acceptance test: the whole pipeline in code on the C15 sample pair at scale 1. Expected outcome (C15): exactly **8** rejected rows — app.Orders 5 (2 orphan customers → `FK_Orders_Customers`, 3 comments of 300 chars → truncation of `[Comment]`), app.OrderLines 3 (1 `QTY = 0` → `CK_OrderLines_Quantity`, 2 lines of the orphan orders → `FK_OrderLines_Orders`); every other row loads; counts validate; checksums match for every task without rejects; `FK_Customers_PrimaryAddress` (the cycle edge) is trusted again after the global PostSql; the checkpoint table is gone. Source rows at scale 1 (C15): Customers 1 000, Addresses 1 500, Products 200, Orders 3 005 (3 000·s + 5), OrderLines 9 002 (9 000·s + 2), AuditEvents 5 000. So the expected loads are 1 000 / 1 500 / 200 / **3 000** / **8 999** / 5 000.

- [ ] **Step 1: Write the failing end-to-end tests**

`plugins/db-migrate/engine/Dbm.Tests/Integration/Transfer/EndToEndTransferTests.cs`:

```csharp
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

    /// <summary>A fresh, empty ShopV2 for one test.</summary>
    public static async Task<TempDatabase> NewTargetAsync()
    {
        var db = await TempDatabase.CreateAsync("shopv2");
        await SampleDatabases.RunAsync(db.ConnectionString, SampleSql.ShopV2Schema);
        return db;
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
        return new Rig(new XferServices(), await SamplePlanFixture.NewTargetAsync(), plan, fx.Src.ConnectionString);
    }

    private async Task AssertExactOutcomeAsync(Rig rig, long runId)
    {
        var tasks = rig.Repo.Tasks(runId);
        foreach (var (id, task) in rig.Plan.Tasks)
        {
            long source = await fx.Src.ScalarAsync<long>(TargetOps.CountSqlOf(task));
            long rejects = KnownRejects.GetValueOrDefault(task.Target);
            Assert.Equal(SourceRows[task.Target], source);                                     // e.g. Orders 3005 -> 3000 loaded, OrderLines 9002 -> 8999
            Assert.Equal(source - rejects, await rig.Tgt.CountAsync(SqlQuote.TableKey(task.Target)));
            var row = tasks.Single(t => t.TaskId == id);
            Assert.Equal(TransferTaskStatus.Done, row.Status);
            Assert.Equal(rejects, row.RowsError);
            Assert.Equal(source - rejects, row.RowsDone);
        }
        Assert.Equal(8, rig.Repo.ErrorRows(runId, null, 1000).Count);                  // each rejected row recorded exactly once
        Assert.Equal(0, await rig.Tgt.ScalarAsync<int>("SELECT COUNT(*) FROM sys.foreign_keys WHERE is_not_trusted = 1 OR is_disabled = 1"));
        Assert.Equal(0, await rig.Tgt.ScalarAsync<int>("SELECT CAST(is_not_trusted AS int) FROM sys.foreign_keys WHERE name = 'FK_Customers_PrimaryAddress'"));
        Assert.Equal(0, await rig.Tgt.ScalarAsync<int>("SELECT COUNT(*) FROM sys.check_constraints WHERE is_not_trusted = 1 OR is_disabled = 1"));
        Assert.Equal(0, await rig.Tgt.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name = '__dbm_checkpoint'"));
    }

    [Fact]
    public async Task Skip_mode_rejects_exactly_the_eight_known_bad_rows_and_validates_everything_else()
    {
        await using var rig = await RigAsync();
        var engine = rig.NewEngine();
        long runId = engine.CreateRun(1, new TransferOptions { ErrorMode = "skip" });
        var outcome = await engine.RunAsync(runId, new TransferControl(), default);

        Assert.Equal(RunStatus.Completed, outcome.Status);
        await AssertExactOutcomeAsync(rig, runId);

        var orders = rig.Repo.ErrorRows(runId, rig.TaskOf("app.Orders"), 100);
        Assert.Equal(2, orders.Count(e => e.Error.Contains("FK_Orders_Customers")));
        Assert.Equal(3, orders.Count(e => e.Error.Contains("[Comment]")));
        Assert.All(orders, e => Assert.StartsWith("{\"__k0\":", e.KeyJson));
        var lines = rig.Repo.ErrorRows(runId, rig.TaskOf("app.OrderLines"), 100);
        Assert.Equal(1, lines.Count(e => e.Error.Contains("CK_OrderLines_Quantity")));
        Assert.Equal(2, lines.Count(e => e.Error.Contains("FK_OrderLines_Orders")));

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
        Assert.Contains(report.Notes, n => n.Contains("Row counts validated for all 6 tasks"));
        Assert.StartsWith("Transferred ", FinalReportBuilder.Summary(report));
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
        Assert.Contains("rejected", orders.Error);
        Assert.Single(rig.Repo.ErrorRows(runId, orders.TaskId, 100));
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
        long runId = engine.CreateRun(1, new TransferOptions { ErrorMode = "skip", ChunkSize = 500 });
        var control = new TransferControl();
        control.ChunkCommitted += c => { if (c.TaskId == ordersTask && c.ChunkNo == 2) control.RequestPause(); };

        Assert.Equal(RunStatus.Paused, (await engine.RunAsync(runId, control, default)).Status);
        var orders = rig.Repo.Task(runId, ordersTask)!;
        Assert.Equal(TransferTaskStatus.Paused, orders.Status);
        long ordersSource = await fx.Src.ScalarAsync<long>(TargetOps.CountSqlOf(rig.Plan.Tasks[ordersTask]));
        long loadedSoFar = await rig.Tgt.CountAsync("[app].[Orders]");
        Assert.InRange(loadedSoFar, 1, ordersSource - 1);                                      // really mid-table
        Assert.Equal(orders.RowsDone, loadedSoFar);

        Assert.Equal(RunStatus.Completed, (await rig.NewEngine().RunAsync(runId, new TransferControl(), default)).Status);
        await AssertExactOutcomeAsync(rig, runId);
    }

    [Fact]
    public async Task Crash_and_recovery_give_exact_counts_without_duplicates()
    {
        await using var rig = await RigAsync();
        string linesTask = rig.TaskOf("app.OrderLines");
        var engine = rig.NewEngine();
        long runId = engine.CreateRun(1, new TransferOptions { ErrorMode = "skip", ChunkSize = 500 });
        using var cts = new CancellationTokenSource();
        var control = new TransferControl();
        control.ChunkCommitted += c => { if (c.TaskId == linesTask && c.ChunkNo == 3) cts.Cancel(); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.RunAsync(runId, control, cts.Token));
        Assert.Equal(RunStatus.Running, rig.Repo.GetRun(runId)!.Status);                     // what a dead process leaves behind
        long partial = await rig.Tgt.CountAsync("[app].[OrderLines]");
        Assert.True(partial > 0);

        Assert.Equal(1, rig.Repo.RecoverInterrupted());                                         // server start
        var recovered = new TransferEngine(rig.Svc.Services, fx.Plan(), fx.Src.ConnectionString, rig.Tgt.ConnectionString);
        Assert.Equal(RunStatus.Completed, (await recovered.RunAsync(runId, new TransferControl(), default)).Status);
        await AssertExactOutcomeAsync(rig, runId);
    }
}
```

- [ ] **Step 2: Run the end-to-end tests**

This is an acceptance test over code that T5.1–T5.6 already built and tested, so there is no red phase: it passes straight away, or it has found a real defect.
Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~EndToEndTransferTests"`
Expected: PASS — 4 tests. On a failure, use superpowers:systematic-debugging. Typical causes and where to fix them:
- a source count that differs from C15 → `seed.sql`, T2.1;
- a checksum mismatch on a temporal column → `TargetShape.Normalize` / `ChunkReader.ExactDateTime`, T5.2;
- `FK_Customers_PrimaryAddress` untrusted → generator PostSql, T4.2.

- [ ] **Step 3: Run the whole suite**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests` → PASS (unit + LocalDB integration).
Run: `node --test "plugins/db-migrate/engine/Dbm.Tests/js/*.test.cjs"` → PASS.

- [ ] **Step 4: Commit**

```bash
git add plugins/db-migrate/engine/Dbm.Tests/Integration/Transfer/EndToEndTransferTests.cs
git commit -F - <<'EOF'
test(transfer): end-to-end LegacyShop to ShopV2 migration (skip, stop, pause/resume, crash recovery)

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49
EOF
```

---

## Contract notes

**Additions (new names; no existing contract renamed or changed):**
- `Dbm.Core.State`: `TransferRepo` + `TransferRunRow`, `TransferTaskRow`, `ErrorRowEntry`; `DbmServices.Transfers` is implemented as a lazy get-only property (`_transfers ??= new TransferRepo(Db)`), which reads the same as the C6 `{ get; }` declaration.
- `Dbm.Core.Transfer`: `TransferException`, `KeyType`/`KeyValue`/`KeyCodec`, `ChunkPlanner`, `Checkpoint`/`ControlTable`, `Bisector` (+ `IBisectTarget`, `LoadAttempt`, `RowFailure`, `BisectResult`), `TxScope`, `TargetShape`/`TargetColumn`, `ChunkReader`, `RowSnapshot`, `BulkLoader`/`ChunkOutcome`, `TransferControl`/`StopKind`/`ChunkCommit`, `TransferProgress`/`RateWindow`/`Progress*`, `TargetOps`, `TaskResult`, `TransferOutcome`, `TransferEngine`, `Preflight` (+ `PreflightCheck`, `PreflightResult`, `ApprovedPlan`), `RunValidator` (+ `TaskValidation`, `ChecksumResult`, `ChecksumColumn`), `FinalReport`/`TaskReport`/`ErrorSample`/`FinalReportBuilder`, `TransferService` (+ `TransferView`, `TransferRunView`, `TransferTaskView`, `TransferTotals`). `TransferOptions` gains `SkipErrors` (`[JsonIgnore]`) and `Normalized()`.
- Web/CLI: `TransferEndpoints`, `ReportExport`, five `Transfer*Command`s. UI: new library file `wwwroot/js/lib/xfer.js` (`DBM.xfer`), loaded after `lib/highlight.js` (standalone exports pick it up because `WebExport` inlines all `lib/*`). Views register under the phase names `ready`, `transfer`, `complete` plus `execute`/`report` (the export view name equals the script file name).

**Deliberate changes to the lead's design (driven by the experiments):**
1. Composite keyset predicate is `q.[__k0] >= @k0 AND ((q.[__k0] > @k0) OR (q.[__k0] = @k0 AND q.[__k1] > @k1) …)` — the leading conjunct makes it seekable; semantics are unchanged.
2. The last key is a JSON array of typed values `{"t","s","p","c","v"}`; parameters use the exact SQL type (V7 showed an nvarchar parameter against a varchar SQL-collation key skips rows).
3. `ChunkReader` reads `datetime` exactly (1/300 s) and `TargetShape.Normalize` rounds temporal values to the target scale like `CAST` (V8). Without it, bulk-loaded `datetime2(0)` values differ from the reviewed T-SQL semantics and checksums fail.
4. Transaction-ending errors (V3) restart the transaction and reload the rows already confirmed good. A keyless task cannot do this and fails with `tx_ended`. Stop mode also bisects, but only until it finds the first bad row, so the error report names that row.
5. Global `PreSql` runs before truncation (V10: TRUNCATE is blocked by an FK even when it is NOCHECK) and again on every run segment. Task `PreSql`/`PostSql` re-run on resume. **All pre/post scripts must be idempotent** (the generated NOCHECK / WITH CHECK statements are).
6. `error_row` rows are written only after the chunk commits (so error rows are exactly-once). A crash between the commit and the SQLite write loses those samples; the counts stay right because the control table holds them.
7. Failed runs can be resumed (`Resume` accepts `failed` as well as `paused`). After a run is `failed` or `cancelled`, `StartAsync` starts a new run while Transfer is still `running`; the new run id makes the abandoned run's checkpoint rows irrelevant. A cancelled run is final. On cancel, tasks that were in flight end as `paused`, since C14 has no `cancelled` task status.
8. `transfer_run.summary_json` holds `{"error": …}` for failed runs and the `FinalReport` for completed runs. Setting status `running` clears it.
9. Checksums use an **inclusion** list of system scalar types. That covers the lead's exclusions (text/ntext/image/xml/spatial/hierarchyid/sql_variant/rowversion) and also user-defined types. Computed target columns are skipped.
10. Keyless tasks: Pause waits until the single-transaction task finishes. Cancel or a failure elsewhere rolls it back.

**How M5 meets the M0–M4 code (all verified in the integration run):**
- `StateDb.ToDb` converts a null argument to `DBNull`, enums to snake text and timestamps to `"O"`, so the repo binds `$Name` arguments as they are written here.
- `WorkflowEngine.OnTransferFinished("failed"|"cancelled")` only republishes `state_changed`; `Next()` reads the latest `transfer_run.status` itself, so the run status alone drives `await/transfer`, `await/transfer_paused`, `stop/transfer_failed` and `stop/transfer_cancelled`. Endpoint errors are answered by the WebHost error boundary plus the 409/400 results in `TransferEndpoints`; every call passes `TokenGuard`.
- `StateView.transfer` stays `null` (T1.7 owns the `Extenders` list); the Execute screen reads `GET /api/transfer` and its own SSE events.
- UI (T1.9): `ctx` carries `artifact` as `{version, author, summary, createdAt, payload}`, plus `versions`, `latestVersion`, `readOnly` and `export`. **The Transfer and Complete phases are always `readOnly: true`**, so the Execute and report views gate their controls on `ctx.export` (null in the live UI, the payload in a standalone export) instead. Events arrive as `{id, type, data}`. `DBM.api.token()` is a function and `DBM.api.url(path)` appends the token to a GET link (used by **Export HTML**); an export never loads `api.js`. `C.drawer.open(title, node)` is the one shared drawer (Esc and ✕ included), `C.modal` accepts a DOM node as `body`, `.bar` reads `--v`, and `st-paused` / `st-completed` / `st-cancelled` badges already exist, so M5 adds no badge CSS.
- Export (T2.8): `TransferEndpoints.Map` calls `ExportEndpoints.Register("report", ReportExport.BuildAsync)`; the builder returns an `ExportFile` built with `WebExport.BuildHtml(assets, "report", …)` from the `{project, exportedAt, artifact:{…}}` envelope that `app.js` export mode expects, and throws `ExportException` (→ 404 `export_unavailable`) before the first completed run. `GET/POST /api/export/report` then work without a new route, and the export inlines every `lib/*` (so `xfer.js`) plus `views/report.js`.
- `CatalogExtractor`/`DriftChecker` skip `__dbm_*` tables, so the checkpoint table never counts as schema drift and M5 needs no workaround.
- Sample counts at scale s (C15): CUST 1000·s, ADDR 1500·s, PROD 200, ORD_STATUS 4, ORD_HDR 3000·s + 5, ORD_LINE 9000·s + 2, AUDIT_LOG 5000·s; the 3 long-comment orders have no lines → 8 rejected rows. T5.7 asserts these numbers at scale 1.
- Test helpers: `TempDatabase`, `TestWorkspace.OpenServices`, `SampleDatabases.CreateAsync`/`RunAsync`, `SamplePair`, `SampleExtract.CatalogAsync`, `SampleMappings.Approved()`. M5 adds only `TempDatabaseExtensions.CountAsync` and `XferServices` (a `TestWorkspace` with a recording sink).

**Upstream files M5 edits (exact lines and anchors are in the tasks):** `Core/DbmServices.cs` (+`Transfers`), `Web/WebState.cs` (+`Transfer`), `Web/WebHost.cs` (create the service, stop it on shutdown), `Web/Endpoints/EndpointRegistry.cs`, `Cli/CommandRegistry.cs`, `Cli/Args.cs` (`--skip-errors`, `--truncate` are boolean flags), `wwwroot/index.html` (three script tags) and `wwwroot/css/app.css` (appended `exe-`/`rep-` block).

## Self-review

**Spec coverage:**

| Spec requirement | Where it is covered |
|---|---|
| §9 ordering, dependency levels, N workers, FK cycles via NOCHECK / WITH CHECK | T5.3 scheduler; generator PreSql/PostSql executed; T5.7 asserts `FK_Customers_PrimaryAddress` trusted |
| §9 copy path: SequentialAccess reader → SqlBulkCopy (timeout 0, CheckConstraints, KeepNulls, KeepIdentity, NotifyAfter) | T5.2 |
| §9 direct / staging_merge | T5.2 (staging test) |
| §9 keyset chunks + checkpoint in the same target transaction; keyless tasks in one transaction; control table dropped at the end | T5.1–T5.3 |
| §9 crash recovery | `RecoverInterrupted` at server start; T5.3 and T5.7 crash tests |
| §9 validation: counts + checksums, LOB excluded | T5.4 |
| §10 bisection, skip vs stop | T5.2, T5.3, T5.7 |
| §10 secrets | `Redactor.Scrub` on every stored or published error, pre-flight details and log |
| §10 destructive actions need typed confirmation | UI modal `requireText` + server `confirmTarget`; CLI `--yes-target` |
| §8 Execute screen: pre-flight, options, typed confirm, live bars/rate/ETA/sparkline/errors/log, Pause/Resume/Cancel | T5.6 |
| §8 Final report: counts, rejected, duration, throughput, validation, samples, export | T5.6 |
| §3.3 pause semantics ("finish the current chunk, commit, checkpoint, then stop") | T5.3 |
| CLI `transfer start/pause/resume/cancel/status` | T5.5 |
| Playbook `reference/transfer.md` | T5.5 |

**Placeholder scan:** no TBD/TODO. The only staged body is the T5.3 completion hook, which T5.4 Step 6 replaces with exact before/after code. Every modification of another milestone's file shows the exact inserted lines and their anchor.

**Type consistency:** names and signatures used across tasks match the Interfaces blocks: `TransferRepo`, `KeyCodec`, `Checkpoint`, `BulkLoader(task, options)`, `TransferEngine(services, plan, srcCs, tgtCs)`, `TransferControl`, `RunValidator`, `FinalReportBuilder`, `TransferService`. The JSON shapes the UI consumes (`TransferView`, `ProgressSnapshot`, `PreflightResult`, `ErrorRowEntry`, `FinalReport`) come from the C# records serialised with `Json.Options` (camelCase, snake_case enums).

**Executed verification (2026-09-12, on the real M0–M4 code):** every code block of this file was written mechanically into the cumulative
integration repository (M0+M1, M2, M3, M4 already executed there), and the file edits above were applied to the real upstream files.

- `dotnet build Dbm.sln`: 0 errors, 0 warnings.
- `dotnet test --filter "Category!=Integration"`: **729 pass** (672 upstream + 57 from M5).
- `dotnet test --filter "Category=Integration"` (LocalDB): **73 pass** (45 upstream + 28 from M5), including all four T5.7 end-to-end tests:
  skip mode (exactly 8 rejected rows, every count and checksum validated, `FK_Customers_PrimaryAddress` trusted again, checkpoint table gone),
  stop mode, pause mid-table + resume, and crash + recovery.
- `node --test "…/js/*.test.cjs"`: **62 pass** (54 upstream + 8 from M5).
- An earlier scratch run (stand-ins for M1–M4) found and fixed one test bug: a pause handler that fired again on the restarted run.

**Not executed:** the manual UI checklist of T5.6 (light/dark, ~400 px, drawer, modal) — it needs a browser.

**Risks:**
- Throughput: DataTable chunks hold ChunkSize rows in memory (100 000 by default; the generator lowers it to 5 000 for LOB tasks).
- CLR/UDT source columns (`hierarchyid`, spatial types) are not readable without `Microsoft.SqlServer.Types`, which is not an allowed package, so such tasks fail at read time and need a CAST in the SourceQuery.
- Long keyless tasks cannot be paused mid-way.

