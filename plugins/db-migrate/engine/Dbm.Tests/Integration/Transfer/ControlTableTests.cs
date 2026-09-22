using Dbm.Core.SqlGen;
using Dbm.Core.Transfer;
using Dbm.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Dbm.Tests.Integration.Transfer;

[Trait("Category", "Integration")]
public sealed class ControlTableTests
{
    /// <summary>Ruling 215 (N-6): checkpoints are read and written for a project, and an unset owner throws; every test here writes as
    /// this one. Set in the constructor, which xUnit calls synchronously in the flow that then runs the test.</summary>
    private static readonly CheckpointOwner TestOwner = new("00000000-0000-0000-0000-00000000c7b1", @"D:\control-table-tests");

    public ControlTableTests() => ControlTable.CurrentOwner = TestOwner;

    /// <summary>N-6: a caller that forgot the owner is told so, instead of writing rows that look like an earlier engine's.</summary>
    [Fact]
    public async Task Reading_or_writing_a_checkpoint_with_no_owner_set_throws_instead_of_writing_ownerless_rows()
    {
        await using var db = await TempDatabase.CreateAsync("dbm_ctlnoown");
        await using var conn = new SqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await ControlTable.EnsureAsync(conn, default);
        ControlTable.CurrentOwner = null;

        var write = await Record.ExceptionAsync(() => ControlTable.UpsertAsync(conn, null, 1, "T01", Checkpoint.Start, default));
        var read = await Record.ExceptionAsync(() => ControlTable.ReadAsync(conn, 1, "T01", default));

        long rows = await db.CountAsync("dbo.__dbm_checkpoint");
        Assert.True(write is InvalidOperationException && rows == 0,
            $"a checkpoint was written with no owner set ({rows} rows, {write?.GetType().Name ?? "no exception"}): it would read as an earlier engine's");
        Assert.IsType<InvalidOperationException>(read);
    }

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
        Assert.Equal("[{\"t\":\"varchar\",\"s\":20,\"p\":0,\"c\":0,\"v\":\"Z\"},{\"t\":\"smallint\",\"s\":2,\"p\":5,\"c\":0,\"v\":\"1\"}]",
            KeyCodec.Encode(last!));   // SqlClient reports 255 for "not applicable"; the codec stores 0 instead
    }

    // ---- additions beyond the brief ----

    [Fact]
    public async Task Keyset_paging_resumes_exactly_on_awkward_key_types_one_row_per_chunk()
    {
        await using var db = await TempDatabase.CreateAsync("dbm_page2");
        // Neighbouring keys differ only in the 4th decimal place, the last datetime2(7) tick, and the offset of equal local times,
        // so any widening or rounding of a resumed parameter skips or repeats a row.
        await db.ExecAsync("""
            CREATE TABLE dbo.A (d decimal(18,4) NOT NULL, t datetime2(7) NOT NULL, o datetimeoffset(3) NOT NULL, n nvarchar(10) NOT NULL, v int NOT NULL,
              CONSTRAINT PK_A PRIMARY KEY (d, t, o, n));
            INSERT dbo.A VALUES
              (1.0001, '2020-01-01T00:00:00.0000001', '2020-01-01T10:00:00.000+02:00', N'x', 1),
              (1.0001, '2020-01-01T00:00:00.0000002', '2020-01-01T10:00:00.000+02:00', N'x', 2),
              (1.0001, '2020-01-01T00:00:00.0000002', '2020-01-01T10:00:00.000+01:00', N'x', 3),
              (1.0001, '2020-01-01T00:00:00.0000002', '2020-01-01T10:00:00.001+01:00', N'x', 4),
              (1.0001, '2020-01-01T00:00:00.0000002', '2020-01-01T10:00:00.001+01:00', N'é', 5),
              (1.0002, '2020-01-01T00:00:00.0000000', '2020-01-01T10:00:00.000+00:00', N'a', 6),
              (1.0010, '2019-01-01T00:00:00.0000000', '2020-01-01T10:00:00.000+00:00', N'a', 7),
              (-1.0000, '2021-01-01T00:00:00.0000000', '2020-01-01T10:00:00.000+00:00', N'a', 8);
            """);
        var task = new TaskPlan
        {
            Target = "dbo.T",
            SourceQuery = "SELECT a.v AS [V], a.d AS [__k0], a.t AS [__k1], a.o AS [__k2], a.n AS [__k3] FROM dbo.A AS a;",
            KeyColumns = ["__k0", "__k1", "__k2", "__k3"],
        };
        await using var conn = new SqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var seen = new List<int>();
        KeyValue? last = null;
        IReadOnlyList<KeyType>? types = null;
        for (int guard = 0; guard < 50; guard++)
        {
            await using var cmd = ChunkPlanner.Command(conn, task, last, 1);
            await using var r = await cmd.ExecuteReaderAsync();
            types ??= KeyCodec.TypesOf(r, task.KeyColumns);
            if (!await r.ReadAsync()) break;
            seen.Add(r.GetInt32(0));
            last = KeyCodec.Decode(KeyCodec.Encode(new KeyValue(types, [r.GetValue(1), r.GetValue(2), r.GetValue(3), r.GetValue(4)])));
        }

        string expected = await db.ScalarAsync<string>("SELECT STRING_AGG(CAST(v AS varchar(10)), ',') WITHIN GROUP (ORDER BY d, t, o, n) FROM dbo.A");
        Assert.Equal(expected, string.Join(",", seen));
        Assert.Equal(8, seen.Distinct().Count());

        Assert.Equal(("decimal", (byte)18, (byte)4), (types![0].Name, types[0].Precision, types[0].Scale));
        Assert.Equal(("datetime2", (byte)7), (types[1].Name, types[1].Scale));
        Assert.Equal(("datetimeoffset", (byte)3), (types[2].Name, types[2].Scale));
        Assert.Equal(("nvarchar", 10), (types[3].Name, types[3].Size));
    }

    [Fact]
    public async Task EnsureAsync_is_safe_under_concurrent_callers()
    {
        await using var db = await TempDatabase.CreateAsync("dbm_ctlrace");
        var conns = new List<SqlConnection>();
        try
        {
            for (int i = 0; i < 8; i++)
            {
                var c = new SqlConnection(db.ConnectionString);
                await c.OpenAsync();
                conns.Add(c);
            }
            using var gate = new SemaphoreSlim(0);
            var runs = conns.Select(c => System.Threading.Tasks.Task.Run(async () =>
            {
                await gate.WaitAsync();
                await ControlTable.EnsureAsync(c, default);
            })).ToList();
            gate.Release(conns.Count);
            await System.Threading.Tasks.Task.WhenAll(runs);   // no "There is already an object named ..." from a losing racer
            Assert.True(await ControlTable.ExistsAsync(conns[0], default));
            Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name = N'__dbm_checkpoint'"));
        }
        finally
        {
            foreach (var c in conns) await c.DisposeAsync();
        }
    }

    [Fact]
    public async Task Ensure_and_drop_touch_nothing_else_in_the_target()
    {
        await using var db = await TempDatabase.CreateAsync("dbm_ctlsafe");
        await db.ExecAsync("""
            CREATE TABLE dbo.Keep (id int NOT NULL CONSTRAINT PK_Keep PRIMARY KEY, v nvarchar(10) NULL);
            INSERT dbo.Keep VALUES (1, N'a'), (2, N'b');
            GO
            CREATE SCHEMA app;
            GO
            CREATE TABLE app.__dbm_checkpoint (x int NOT NULL);
            INSERT app.__dbm_checkpoint VALUES (7);
            """);
        const string Inventory = "SELECT STRING_AGG(CONCAT(SCHEMA_NAME(schema_id), '.', name, ':', type) COLLATE Latin1_General_BIN2, ',') WITHIN GROUP (ORDER BY schema_id, name) " +
                                 "FROM sys.objects WHERE is_ms_shipped = 0 AND NOT (schema_id = SCHEMA_ID('dbo') AND (name = '__dbm_checkpoint' OR parent_object_id = ISNULL(OBJECT_ID('dbo.__dbm_checkpoint'), -1)))";
        string before = await db.ScalarAsync<string>(Inventory);

        await using var conn = new SqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await ControlTable.EnsureAsync(conn, default);
        await ControlTable.UpsertAsync(conn, null, 1, "T01", Checkpoint.Start, default);
        Assert.Equal(before, await db.ScalarAsync<string>(Inventory));
        await ControlTable.DropAsync(conn, default);

        Assert.Equal(before, await db.ScalarAsync<string>(Inventory));
        Assert.Equal(2L, await db.CountAsync("dbo.Keep"));
        Assert.Equal(1L, await db.CountAsync("app.__dbm_checkpoint"));
    }

    [Fact]
    public async Task EnsureAsync_refuses_a_look_alike_foreign_table_that_upsert_would_otherwise_write_into()
    {
        // Same columns, so every checkpoint statement would run against it — but it is someone else's table (extra column, no PK).
        await using var db = await TempDatabase.CreateAsync("dbm_ctllook");
        await db.ExecAsync("""
            CREATE TABLE dbo.__dbm_checkpoint (
              run_id bigint NOT NULL, task_id nvarchar(64) NOT NULL, chunk_no int NOT NULL, last_key nvarchar(max) NULL,
              rows_done bigint NOT NULL, rows_error bigint NOT NULL, done bit NOT NULL, updated_at datetime2(3) NOT NULL, owner nvarchar(20) NULL);
            INSERT dbo.__dbm_checkpoint VALUES (1, N'T01', 5, NULL, 50, 0, 0, SYSUTCDATETIME(), N'customer');
            """);
        await using var conn = new SqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var ex = await Assert.ThrowsAsync<TransferException>(() => ControlTable.EnsureAsync(conn, default));
        Assert.Equal("control_table_mismatch", ex.Code);
        Assert.Equal("customer", await db.ScalarAsync<string>("SELECT owner FROM dbo.__dbm_checkpoint"));
        Assert.Equal(5, await db.ScalarAsync<int>("SELECT chunk_no FROM dbo.__dbm_checkpoint"));

        // Ruling 212: the end-of-run release (delete our rows, drop when empty) refuses it the same way and leaves its rows alone. (The
        // upsert-into-it demonstration this test used to end with no longer applies: the checkpoint statements now name the project
        // columns, which this table does not have.)
        var release = await Assert.ThrowsAsync<TransferException>(() => ControlTable.ReleaseAsync(conn, CheckpointOwner.Unowned, default));
        Assert.Equal("control_table_mismatch", release.Code);
        Assert.Equal(1L, await db.CountAsync("dbo.__dbm_checkpoint"));
    }

    /// <summary>
    /// Ruling 212: a checkpoint table an earlier engine created (no project columns) is upgraded in place, keeping its rows, which are
    /// then claimable only by a resume of the same run - and until claimed they count as another project's unfinished rows, never as
    /// rows a fresh run may adopt.
    /// </summary>
    [Fact]
    public async Task An_earlier_engines_checkpoint_table_is_upgraded_in_place_and_its_rows_are_claimed_only_by_their_run()
    {
        await using var db = await TempDatabase.CreateAsync("dbm_ctlold");
        await db.ExecAsync("""
            CREATE TABLE dbo.__dbm_checkpoint (
              run_id bigint NOT NULL, task_id nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL, chunk_no int NOT NULL,
              last_key nvarchar(max) COLLATE Latin1_General_100_BIN2 NULL,
              rows_done bigint NOT NULL, rows_error bigint NOT NULL, done bit NOT NULL, updated_at datetime2(3) NOT NULL,
              PRIMARY KEY (run_id, task_id));
            INSERT dbo.__dbm_checkpoint VALUES (1, N'T01', 2, N'[200]', 200, 0, 0, DATEADD(HOUR, -1, SYSUTCDATETIME()));
            """);
        await using var conn = new SqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var me = new CheckpointOwner(Guid.NewGuid().ToString("D"), @"D:\me");

        await ControlTable.EnsureAsync(conn, default);

        Assert.Equal(1L, await db.CountAsync("dbo.__dbm_checkpoint"));
        var foreign = await ControlTable.ForeignUnfinishedAsync(conn, me, default);
        Assert.True(foreign is { Legacy: true }, "an earlier engine's unfinished checkpoint was not counted as another project's");
        // N-3: a run whose recorded counters differ (another project's run 1, T01) cannot take them.
        await ControlTable.ClaimLegacyAsync(conn, me, 1, [("T01", 150, 0)], default);
        Assert.True(await ControlTable.ForeignUnfinishedAsync(conn, me, default) is not null,
            "a run whose recorded rows_done (150) differ from the legacy row's (200) claimed it");
        await ControlTable.ClaimLegacyAsync(conn, me, 1, [("T01", 200, 0)], default);
        Assert.Null(await ControlTable.ForeignUnfinishedAsync(conn, me, default));
        ControlTable.CurrentOwner = me;
        try
        {
            Assert.Equal(200, (await ControlTable.ReadAsync(conn, 1, "T01", default))!.RowsDone);
        }
        finally
        {
            ControlTable.CurrentOwner = TestOwner;
        }
        Assert.True(await ControlTable.ReleaseAsync(conn, me, default));
        Assert.False(await ControlTable.ExistsAsync(conn, default));
    }

    /// <summary>
    /// Ruling 215 (N-5). A released engine takes a run-scoped lock this one does not contend for, so the two could load one target at
    /// once. Its rows changing within <see cref="ControlTable.LegacyQuietPeriod"/> is the sign that it may be loading now: they are not
    /// claimed, and the claim is refused <c>run_in_progress</c> saying why.
    /// </summary>
    [Fact]
    public async Task Legacy_checkpoints_written_moments_ago_are_never_claimed()
    {
        await using var db = await TempDatabase.CreateAsync("dbm_ctllive");
        await using var conn = new SqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await ControlTable.EnsureAsync(conn, default);
        await db.ExecAsync("""
            INSERT dbo.__dbm_checkpoint (run_id, task_id, chunk_no, last_key, rows_done, rows_error, done, updated_at, project_id, project_folder)
            VALUES (1, N'T01', 2, N'[200]', 200, 0, 0, SYSUTCDATETIME(), N'', NULL);
            """);
        var me = new CheckpointOwner(Guid.NewGuid().ToString("D"), @"D:\me");

        var refusal = await Record.ExceptionAsync(() => ControlTable.ClaimLegacyAsync(conn, me, 1, [("T01", 200, 0)], default));

        Assert.True(await ControlTable.ForeignUnfinishedAsync(conn, me, default) is not null,
            "legacy checkpoints written moments ago were claimed: an earlier engine may be loading them right now");
        var refused = Assert.IsType<TransferException>(refusal);
        Assert.Equal("run_in_progress", refused.Code);
        Assert.Contains("earlier db-migrate version", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_chunk_size_below_one_would_return_no_rows_which_reads_as_a_finished_task()
    {
        await using var db = await TempDatabase.CreateAsync("dbm_chunk0");
        await db.ExecAsync("CREATE TABLE dbo.S (id int NOT NULL PRIMARY KEY, v int NOT NULL); INSERT dbo.S VALUES (1,1),(2,2),(3,3);");
        var task = new TaskPlan { Target = "dbo.T", SourceQuery = "SELECT s.v AS [V], s.id AS [__k0] FROM dbo.S AS s", KeyColumns = ["__k0"] };
        await using var conn = new SqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        // Product path. With the guard, Command refuses 0. Without it, the command it builds is run exactly as the engine
        // would run a chunk, and the harm assertion is what fails: TOP (0) yields an empty chunk from a 3-row source, and an
        // empty chunk is how the engine recognises a finished task (nothing copied, reported done).
        bool guarded = false;
        SqlCommand? cmd = null;
        try { cmd = ChunkPlanner.Command(conn, task, null, 0); }
        catch (ArgumentOutOfRangeException) { guarded = true; }
        if (cmd is not null)
        {
            await using (cmd)
            {
                int rowsRead = 0;
                await using (var r = await cmd.ExecuteReaderAsync())
                    while (await r.ReadAsync()) rowsRead++;
                Assert.True(rowsRead > 0,
                    $"chunk size 0 returned {rowsRead} rows from a 3-row source: the engine would mark the task done having copied nothing");
            }
        }
        Assert.True(guarded);
        Assert.Equal(3L, await db.CountAsync("dbo.S"));

        // A real chunk size reads the rows through the same path.
        await using var ok = ChunkPlanner.Command(conn, task, null, 1);
        await using var rows = await ok.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
    }

    [Fact]
    public async Task Task_ids_longer_than_64_characters_would_collapse_onto_one_checkpoint_row()
    {
        await using var db = await TempDatabase.CreateAsync("dbm_ctltrunc");
        await using var conn = new SqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await ControlTable.EnsureAsync(conn, default);

        // Two distinct 65-character ids sharing a 64-character prefix, written and read through the product path.
        string prefix = new('x', 64);
        string orders = prefix + "o", customers = prefix + "c";
        bool guarded = false;
        try
        {
            await ControlTable.UpsertAsync(conn, null, 1, orders, new Checkpoint(7, "orders-key", 700, 0, false), default);
            await ControlTable.UpsertAsync(conn, null, 1, customers, new Checkpoint(2, "customers-key", 200, 0, false), default);
        }
        catch (ArgumentException) { guarded = true; }

        if (!guarded)
        {
            // Without the guard the nvarchar(64) parameter truncates silently: both tasks land on one row, and each would
            // resume from the other's position.
            Assert.Equal(2L, await db.CountAsync(ControlTable.Name));
            Assert.Equal("orders-key", (await ControlTable.ReadAsync(conn, 1, orders, default))!.LastKeyJson);
        }
        Assert.True(guarded);
        await Assert.ThrowsAsync<ArgumentException>(() => ControlTable.ReadAsync(conn, 1, customers, default));
        Assert.Equal(0L, await db.CountAsync(ControlTable.Name));
    }

    // ---- fix round 1 (H1, M1, L1) ----

    [Fact]
    public async Task DropAsync_refuses_a_foreign_table_of_the_same_name_and_leaves_it_and_its_rows_intact()
    {
        await using var db = await TempDatabase.CreateAsync("dbm_ctldrop");
        await db.ExecAsync("CREATE TABLE dbo.__dbm_checkpoint (run_id bigint NOT NULL, note nvarchar(20) NULL); INSERT dbo.__dbm_checkpoint VALUES (1, N'mine'), (2, N'also mine');");
        await using var conn = new SqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var ex = await Record.ExceptionAsync(() => ControlTable.DropAsync(conn, default));

        // The harm first, so a removed check fails here, on the missing table, not on the missing exception.
        Assert.True(await ControlTable.ExistsAsync(conn, default), "DropAsync dropped a customer's table that is not a db-migrate checkpoint table");
        Assert.Equal(2L, await db.CountAsync("dbo.__dbm_checkpoint"));
        Assert.Equal("mine", await db.ScalarAsync<string>("SELECT note FROM dbo.__dbm_checkpoint WHERE run_id = 1"));

        var te = Assert.IsType<TransferException>(ex);
        Assert.Equal("control_table_mismatch", te.Code);
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM sys.dm_tran_locks WHERE resource_type = 'APPLICATION' AND resource_database_id = DB_ID()"));
    }

    [Fact]
    public async Task Ensure_and_drop_leave_the_callers_xact_abort_setting_as_they_found_it()
    {
        await using var db = await TempDatabase.CreateAsync("dbm_ctlxact");
        await using var conn = new SqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        async Task<bool> XactAbort()
        {
            await using var c = new SqlCommand("SELECT CAST(@@OPTIONS & 16384 AS int)", conn);
            return (int)(await c.ExecuteScalarAsync())! != 0;
        }
        async Task Set(bool on)
        {
            await using var c = new SqlCommand(on ? "SET XACT_ABORT ON;" : "SET XACT_ABORT OFF;", conn);
            await c.ExecuteNonQueryAsync();
        }

        foreach (bool on in new[] { false, true, false })
        {
            await Set(on);
            Assert.Equal(on, await XactAbort());
            await ControlTable.EnsureAsync(conn, default);     // creates
            Assert.Equal(on, await XactAbort());
            await ControlTable.EnsureAsync(conn, default);     // already exists
            Assert.Equal(on, await XactAbort());
            await ControlTable.DropAsync(conn, default);       // drops ours
            Assert.Equal(on, await XactAbort());
            await ControlTable.DropAsync(conn, default);       // absent: no-op
            Assert.Equal(on, await XactAbort());

            await db.ExecAsync("CREATE TABLE dbo.__dbm_checkpoint (x int NOT NULL);");
            await Assert.ThrowsAsync<TransferException>(() => ControlTable.EnsureAsync(conn, default));   // mismatch path
            Assert.Equal(on, await XactAbort());
            await Assert.ThrowsAsync<TransferException>(() => ControlTable.DropAsync(conn, default));
            Assert.Equal(on, await XactAbort());
            await db.ExecAsync("DROP TABLE dbo.__dbm_checkpoint;");
        }
    }

    [Fact]
    public async Task Ensure_and_drop_leave_the_callers_session_isolation_level_as_they_found_it()
    {
        await using var db = await TempDatabase.CreateAsync("dbm_ctliso");
        await using var conn = new SqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        async Task<string> Level()
        {
            await using var c = new SqlCommand("SELECT transaction_isolation_level FROM sys.dm_exec_sessions WHERE session_id = @@SPID", conn);
            return Convert.ToInt16(await c.ExecuteScalarAsync()) switch
            {
                1 => "READ UNCOMMITTED", 2 => "READ COMMITTED", 3 => "REPEATABLE READ", 4 => "SERIALIZABLE", 5 => "SNAPSHOT", var n => n.ToString(),
            };
        }

        // Ends on a non-default level too, so the last step cannot pass merely by landing on READ COMMITTED.
        foreach (var level in new[] { "READ UNCOMMITTED", "SERIALIZABLE", "REPEATABLE READ" })
        {
            await using (var s = new SqlCommand($"SET TRANSACTION ISOLATION LEVEL {level};", conn)) await s.ExecuteNonQueryAsync();
            Assert.Equal(level, await Level());

            await ControlTable.EnsureAsync(conn, default);                                   // ensure-create
            Assert.Equal(level, await Level());
            await ControlTable.EnsureAsync(conn, default);                                   // ensure-existing
            Assert.Equal(level, await Level());
            await ControlTable.UpsertAsync(conn, null, 1, "T01", Checkpoint.Start, default);
            await ControlTable.DropAsync(conn, default);                                     // drop ours
            Assert.Equal(level, await Level());
            await ControlTable.DropAsync(conn, default);                                     // drop absent
            Assert.Equal(level, await Level());

            await db.ExecAsync("CREATE TABLE dbo.__dbm_checkpoint (x int NOT NULL);");
            await Assert.ThrowsAsync<TransferException>(() => ControlTable.EnsureAsync(conn, default));   // ensure-mismatch (failure path)
            Assert.Equal(level, await Level());
            await Assert.ThrowsAsync<TransferException>(() => ControlTable.DropAsync(conn, default));     // drop-mismatch (failure path)
            Assert.Equal(level, await Level());
            await db.ExecAsync("DROP TABLE dbo.__dbm_checkpoint;");
            await db.ExecAsync("CREATE VIEW dbo.__dbm_checkpoint AS SELECT 1 AS x;");
            await Assert.ThrowsAsync<SqlException>(() => ControlTable.EnsureAsync(conn, default));       // server error after the lock
            Assert.Equal(level, await Level());
            await db.ExecAsync("DROP VIEW dbo.__dbm_checkpoint;");
        }
    }

    [Fact]
    public async Task A_create_or_drop_that_fails_after_taking_the_lock_leaves_nothing_behind()
    {
        await using var db = await TempDatabase.CreateAsync("dbm_ctlfail");
        await using var conn = new SqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var other = new SqlConnection(db.ConnectionString);
        await other.OpenAsync();

        async Task<int> TranCount()
        {
            await using var c = new SqlCommand("SELECT @@TRANCOUNT", conn);
            return (int)(await c.ExecuteScalarAsync())!;
        }
        async Task<int> LockFromOtherConnection()
        {
            // 0 = granted immediately; negative = still held by the failed call.
            await using var c = new SqlCommand($"""
                BEGIN TRANSACTION;
                DECLARE @rc int;
                EXEC @rc = sp_getapplock @Resource = N'dbm:{ControlTable.Name}', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 0;
                ROLLBACK TRANSACTION;
                SELECT @rc;
                """, other);
            return (int)(await c.ExecuteScalarAsync())!;
        }

        foreach (bool xactAbort in new[] { false, true })
        {
            await using (var s = new SqlCommand(xactAbort ? "SET XACT_ABORT ON;" : "SET XACT_ABORT OFF;", conn)) await s.ExecuteNonQueryAsync();

            // Create fails after the table exists inside our transaction: a DDL trigger throws once CREATE TABLE has run.
            await db.ExecAsync("""
                CREATE TRIGGER probe_create ON DATABASE FOR CREATE_TABLE AS
                  IF EVENTDATA().value('(/EVENT_INSTANCE/ObjectName)[1]', 'sysname') = N'__dbm_checkpoint' THROW 50001, N'probe: create refused', 1;
                """);
            await Assert.ThrowsAnyAsync<Exception>(() => ControlTable.EnsureAsync(conn, default));
            Assert.Equal(0, await TranCount());
            Assert.False(await ControlTable.ExistsAsync(conn, default));
            Assert.Equal(0, await LockFromOtherConnection());
            await db.ExecAsync("DROP TRIGGER probe_create ON DATABASE;");

            // Drop fails after the shape check passed: our table and its rows must survive, with no transaction left open.
            await ControlTable.EnsureAsync(conn, default);
            await ControlTable.UpsertAsync(conn, null, 9, "T01", new Checkpoint(3, "k", 30, 0, false), default);
            await db.ExecAsync("""
                CREATE TRIGGER probe_drop ON DATABASE FOR DROP_TABLE AS
                  IF EVENTDATA().value('(/EVENT_INSTANCE/ObjectName)[1]', 'sysname') = N'__dbm_checkpoint' THROW 50002, N'probe: drop refused', 1;
                """);
            await Assert.ThrowsAnyAsync<Exception>(() => ControlTable.DropAsync(conn, default));
            Assert.Equal(0, await TranCount());
            Assert.True(await ControlTable.ExistsAsync(conn, default));
            Assert.Equal(new Checkpoint(3, "k", 30, 0, false), await ControlTable.ReadAsync(conn, 9, "T01", default));
            Assert.Equal(0, await LockFromOtherConnection());
            await db.ExecAsync("DROP TRIGGER probe_drop ON DATABASE;");

            // Statement-level failures. A trigger error ends the transaction server-side; these do not. With XACT_ABORT OFF the
            // transaction (and the application lock it owns) stays open after the error unless ControlTable rolls it back.
            // Drop: a schema-bound view makes DROP TABLE fail (error 3729) after the shape check passed.
            await db.ExecAsync("CREATE VIEW dbo.probe_bound WITH SCHEMABINDING AS SELECT run_id FROM dbo.__dbm_checkpoint;");
            await Assert.ThrowsAsync<SqlException>(() => ControlTable.DropAsync(conn, default));
            Assert.Equal(0, await TranCount());
            Assert.Equal(0, await LockFromOtherConnection());
            Assert.Equal(new Checkpoint(3, "k", 30, 0, false), await ControlTable.ReadAsync(conn, 9, "T01", default));
            await db.ExecAsync("DROP VIEW dbo.probe_bound;");
            await ControlTable.DropAsync(conn, default);

            // Create: a non-table object of the name makes CREATE TABLE fail (error 2714) after the lock was taken.
            await db.ExecAsync("CREATE VIEW dbo.__dbm_checkpoint AS SELECT 1 AS x;");
            await Assert.ThrowsAsync<SqlException>(() => ControlTable.EnsureAsync(conn, default));
            Assert.Equal(0, await TranCount());
            Assert.Equal(0, await LockFromOtherConnection());
            Assert.False(await ControlTable.ExistsAsync(conn, default));
            Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM sys.views WHERE name = N'__dbm_checkpoint'"));
            await db.ExecAsync("DROP VIEW dbo.__dbm_checkpoint;");

            // Client-side failure after the lock: the shape check throws control_table_mismatch while the transaction and its
            // application lock are still open on the server. Only ControlTable's rollback ends them.
            await db.ExecAsync("CREATE TABLE dbo.__dbm_checkpoint (x int NOT NULL); INSERT dbo.__dbm_checkpoint VALUES (5);");
            await Assert.ThrowsAsync<TransferException>(() => ControlTable.EnsureAsync(conn, default));
            Assert.Equal(0, await LockFromOtherConnection());
            Assert.Equal(0, await TranCount());
            await Assert.ThrowsAsync<TransferException>(() => ControlTable.DropAsync(conn, default));
            Assert.Equal(0, await LockFromOtherConnection());
            Assert.Equal(0, await TranCount());
            Assert.Equal(1L, await db.CountAsync("dbo.__dbm_checkpoint"));
            await db.ExecAsync("DROP TABLE dbo.__dbm_checkpoint;");
        }
    }

    [Fact]
    public async Task An_open_checkpoint_upsert_does_not_block_a_different_tasks_first_upsert()
    {
        await using var db = await TempDatabase.CreateAsync("dbm_ctlblock");
        await using var a = new SqlConnection(db.ConnectionString);
        await a.OpenAsync();
        await using var b = new SqlConnection(db.ConnectionString);
        await b.OpenAsync();
        await ControlTable.EnsureAsync(a, default);

        await using var txA = (SqlTransaction)await a.BeginTransactionAsync();
        await ControlTable.UpsertAsync(a, txA, 1, "T01", new Checkpoint(1, "a", 10, 0, false), default);   // left open, as mid-chunk

        await using var txB = (SqlTransaction)await b.BeginTransactionAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var blocked = await Record.ExceptionAsync(() => ControlTable.UpsertAsync(b, txB, 1, "T02", new Checkpoint(1, "b", 10, 0, false), timeout.Token));
        sw.Stop();
        Assert.True(blocked is null, $"T02's first upsert was blocked by T01's open upsert for {sw.ElapsedMilliseconds} ms ({blocked?.GetType().Name}); parallel tasks would serialise on the checkpoint table");
        await txB.CommitAsync();
        await txA.CommitAsync();
        Assert.Equal(2L, await db.CountAsync(ControlTable.Name));
    }

    [Fact]
    public async Task Task_ids_differing_only_by_case_keep_separate_checkpoints_on_a_case_insensitive_target()
    {
        await using var db = await TempDatabase.CreateAsync("dbm_ctlcase");
        // Precondition that makes this a harm test: the target's default collation treats the two ids as equal.
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT CASE WHEN N'T01' = N't01' THEN 1 ELSE 0 END"));
        await using var conn = new SqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await ControlTable.EnsureAsync(conn, default);

        await ControlTable.UpsertAsync(conn, null, 1, "T01", new Checkpoint(7, "upper", 700, 0, false), default);
        await ControlTable.UpsertAsync(conn, null, 1, "t01", new Checkpoint(2, "lower", 200, 0, false), default);

        Assert.Equal(2L, await db.CountAsync(ControlTable.Name));
        Assert.Equal(new Checkpoint(7, "upper", 700, 0, false), await ControlTable.ReadAsync(conn, 1, "T01", default));
        Assert.Equal(new Checkpoint(2, "lower", 200, 0, false), await ControlTable.ReadAsync(conn, 1, "t01", default));

        // Trailing and leading whitespace compare equal even under BIN2, so they are refused before any statement runs.
        foreach (var bad in new[] { "T01 ", " T01", "T01\t" })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => ControlTable.ReadAsync(conn, 1, bad, default));
            await Assert.ThrowsAsync<ArgumentException>(() => ControlTable.UpsertAsync(conn, null, 1, bad, Checkpoint.Start, default));
        }
        Assert.Equal(700, (await ControlTable.ReadAsync(conn, 1, "T01", default))!.RowsDone);
    }

    [Fact]
    public async Task EnsureAsync_refuses_an_otherwise_identical_table_whose_task_id_uses_the_database_collation()
    {
        await using var db = await TempDatabase.CreateAsync("dbm_ctlcoll");
        await db.ExecAsync("""
            CREATE TABLE dbo.__dbm_checkpoint (
              run_id bigint NOT NULL, task_id nvarchar(64) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, chunk_no int NOT NULL,
              last_key nvarchar(max) COLLATE Latin1_General_100_BIN2 NULL,
              rows_done bigint NOT NULL, rows_error bigint NOT NULL, done bit NOT NULL, updated_at datetime2(3) NOT NULL,
              PRIMARY KEY (run_id, task_id));
            """);
        await using var conn = new SqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var ex = await Assert.ThrowsAsync<TransferException>(() => ControlTable.EnsureAsync(conn, default));
        Assert.Equal("control_table_mismatch", ex.Code);
        Assert.Contains(ex.Details, d => d.Contains("task_id|nvarchar|128|0|0|SQL_Latin1_General_CP1_CI_AS", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnsureAsync_refuses_a_foreign_table_of_the_same_name_and_leaves_it_intact()
    {
        await using var db = await TempDatabase.CreateAsync("dbm_ctlclash");
        await db.ExecAsync("CREATE TABLE dbo.__dbm_checkpoint (run_id bigint NOT NULL, note nvarchar(20) NULL); INSERT dbo.__dbm_checkpoint VALUES (1, N'mine');");
        await using var conn = new SqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var ex = await Assert.ThrowsAsync<TransferException>(() => ControlTable.EnsureAsync(conn, default));
        Assert.Equal("control_table_mismatch", ex.Code);
        Assert.Equal(1L, await db.CountAsync("dbo.__dbm_checkpoint"));
        Assert.Equal("mine", await db.ScalarAsync<string>("SELECT note FROM dbo.__dbm_checkpoint"));
    }

    [Fact]
    public async Task Missing_table_is_an_error_not_an_absent_checkpoint_and_long_task_ids_are_refused()
    {
        await using var db = await TempDatabase.CreateAsync("dbm_ctlmiss");
        await using var conn = new SqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        Assert.False(await ControlTable.ExistsAsync(conn, default));
        await Assert.ThrowsAsync<SqlException>(() => ControlTable.ReadAsync(conn, 1, "T01", default));   // "never looked" != "nothing there"

        await ControlTable.EnsureAsync(conn, default);
        string id64 = new('a', 64);
        await ControlTable.UpsertAsync(conn, null, 1, id64, Checkpoint.Start, default);
        Assert.Equal(Checkpoint.Start, await ControlTable.ReadAsync(conn, 1, id64, default));
        await Assert.ThrowsAsync<ArgumentException>(() => ControlTable.UpsertAsync(conn, null, 1, id64 + "b", Checkpoint.Start, default));
        await Assert.ThrowsAsync<ArgumentException>(() => ControlTable.ReadAsync(conn, 1, id64 + "b", default));
        await Assert.ThrowsAsync<ArgumentException>(() => ControlTable.ReadAsync(conn, 1, "", default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ControlTable.UpsertAsync(conn, null, 1, "T01", null!, default));
    }
}
