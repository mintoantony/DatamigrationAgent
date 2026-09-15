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

        // The harm the shape check prevents: nothing else stops the checkpoint statements from overwriting and adding to that data.
        await ControlTable.UpsertAsync(conn, null, 1, "T01", Checkpoint.Start, default);
        await ControlTable.UpsertAsync(conn, null, 1, "T02", Checkpoint.Start, default);
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT chunk_no FROM dbo.__dbm_checkpoint WHERE task_id = N'T01'"));
        Assert.Equal(2L, await db.CountAsync("dbo.__dbm_checkpoint"));
    }

    [Fact]
    public async Task A_chunk_size_below_one_would_return_no_rows_which_reads_as_a_finished_task()
    {
        await using var db = await TempDatabase.CreateAsync("dbm_chunk0");
        await db.ExecAsync("CREATE TABLE dbo.S (id int NOT NULL PRIMARY KEY, v int NOT NULL); INSERT dbo.S VALUES (1,1),(2,2),(3,3);");
        var task = new TaskPlan { Target = "dbo.T", SourceQuery = "SELECT s.v AS [V], s.id AS [__k0] FROM dbo.S AS s", KeyColumns = ["__k0"] };
        await using var conn = new SqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        // The harm, shown with the planner's own SQL and no guard: TOP (0) returns an empty chunk from a 3-row source, and an empty
        // chunk is exactly how the engine recognises a completed task — nothing copied, reported as done.
        foreach (long n in new long[] { 0, -1 })
        {
            await using var raw = new SqlCommand(ChunkPlanner.Sql(task, afterKey: false), conn);
            raw.Parameters.Add(new SqlParameter("@__n", System.Data.SqlDbType.BigInt) { Value = n });
            try
            {
                await using var r = await raw.ExecuteReaderAsync();
                Assert.False(await r.ReadAsync());
            }
            catch (SqlException) when (n < 0) { /* a negative TOP errors instead; still not a copy */ }
        }
        Assert.Equal(3L, await db.CountAsync("dbo.S"));

        // The guard: Command refuses it before any query runs, and a real chunk size reads the rows.
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkPlanner.Command(conn, task, null, 0));
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

        string prefix = new('x', 64);
        string orders = prefix + "_orders", customers = prefix + "_customers";

        // The harm, shown with the same nvarchar(64) parameter the control table binds and no length guard:
        // SqlClient truncates silently, so two distinct tasks write one row and each reads the other's resume position.
        async Task RawUpsert(string taskId, int chunkNo, string lastKey)
        {
            await using var cmd = new SqlCommand($"""
                UPDATE {ControlTable.Name} SET chunk_no = @c, last_key = @k WHERE run_id = 1 AND task_id = @t;
                IF @@ROWCOUNT = 0 INSERT {ControlTable.Name} VALUES (1, @t, @c, @k, 0, 0, 0, SYSUTCDATETIME());
                """, conn);
            cmd.Parameters.Add(new SqlParameter("@t", System.Data.SqlDbType.NVarChar, 64) { Value = taskId });
            cmd.Parameters.Add(new SqlParameter("@c", System.Data.SqlDbType.Int) { Value = chunkNo });
            cmd.Parameters.Add(new SqlParameter("@k", System.Data.SqlDbType.NVarChar, -1) { Value = lastKey });
            await cmd.ExecuteNonQueryAsync();
        }
        await RawUpsert(orders, 7, "orders-key");
        await RawUpsert(customers, 2, "customers-key");
        Assert.Equal(1L, await db.CountAsync(ControlTable.Name));
        Assert.Equal("customers-key", await db.ScalarAsync<string>($"SELECT last_key FROM {ControlTable.Name}"));   // orders' position is gone
        await db.ExecAsync($"DELETE FROM {ControlTable.Name}");

        // The guard: both ids are refused at the boundary, before any statement runs.
        await Assert.ThrowsAsync<ArgumentException>(() => ControlTable.UpsertAsync(conn, null, 1, orders, Checkpoint.Start, default));
        await Assert.ThrowsAsync<ArgumentException>(() => ControlTable.ReadAsync(conn, 1, customers, default));
        Assert.Equal(0L, await db.CountAsync(ControlTable.Name));
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
