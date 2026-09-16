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
        Assert.Equal(9L, outcome.MergeRowsAffected);   // summed over the attempts that survived (ruling 74)
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

    // ---- hardening beyond the brief: each test fails on the harm when its guard is removed ----

    [Fact]
    public async Task ChunkReader_refuses_a_zero_max_because_zero_means_exhausted()
    {
        var (db, conn) = await OpenAsync();
        await using var dbScope = db;
        await using var connScope = conn;
        await using var cmd = new SqlCommand("SELECT id FROM dbo.P ORDER BY id", conn);
        await using var r = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
        var table = ChunkReader.NewTable(r);
        int? got = null;
        try { got = await ChunkReader.FillAsync(r, table, 0, default); }
        catch (ArgumentOutOfRangeException) { }
        // Harm: a 0 here is the "reader exhausted" signal, so the caller would finish the task with three rows never read.
        Assert.True(got is null, $"FillAsync(max: 0) returned {got} while rows remain");
        Assert.Equal(3, await ChunkReader.FillAsync(r, table, 5, default));
    }

    [Fact]
    public async Task Row_errors_carry_the_client_side_reason_not_only_the_wrapper_message()
    {
        var (db, conn) = await OpenAsync();
        await using var dbScope = db;
        await using var connScope = conn;
        var loader = new BulkLoader(TaskFor(), new TransferOptions { ErrorMode = "skip" });
        await using var scope = await TxScope.BeginAsync(conn, default);
        var outcome = await loader.LoadAsync(scope, Rows(20, Spoil), true, null, default);
        await scope.RollbackAsync();
        Assert.Equal(new[] { 3, 7, 11, 15 }, outcome.Failed.Select(f => f.Row));
        Assert.Contains("truncat", outcome.Failed[2].Error, StringComparison.OrdinalIgnoreCase);
        Assert.All(outcome.Failed, f => Assert.False(string.IsNullOrWhiteSpace(f.Error)));
    }

    [Fact]
    public async Task Xact_abort_on_the_callers_connection_does_not_turn_row_errors_into_a_failed_load_and_is_restored()
    {
        var (db, conn) = await OpenAsync();
        await using var dbScope = db;
        await using var connScope = conn;
        await using (var set = new SqlCommand("SET XACT_ABORT ON;", conn)) await set.ExecuteNonQueryAsync();
        var task = TaskFor();
        task.KeyColumns = [];
        var loader = new BulkLoader(task, new TransferOptions { ErrorMode = "skip" });
        ChunkOutcome? outcome = null;
        TransferException? failure = null;
        await using (var scope = await TxScope.BeginAsync(conn, default))
        {
            try
            {
                // keyless: a transaction-ending error cannot be recovered, so XACT_ABORT ON would fail the whole task over one FK row
                outcome = await loader.LoadAsync(scope, Rows(8, (i, v) => { if (i == 5) v[1] = 99; }), allowRestart: false, progress: null, default);
                await scope.CommitAsync(default);
            }
            catch (TransferException ex) { failure = ex; }
        }
        Assert.True(failure is null, $"the load failed: {failure?.Code} {failure?.Message}");
        Assert.Equal(7, outcome!.Loaded);
        Assert.Equal(new[] { 5 }, outcome.Failed.Select(f => f.Row));
        Assert.Equal(7, await db.CountAsync("dbo.C"));
        await using var probe = new SqlCommand("SELECT CAST(@@OPTIONS & 16384 AS int)", conn);
        Assert.NotEqual(0, (int)(await probe.ExecuteScalarAsync())!);   // the caller's setting is back
    }

    [Fact]
    public async Task A_task_without_bindings_is_refused_instead_of_mapping_columns_by_position()
    {
        var (db, conn) = await OpenAsync();
        await using var dbScope = db;
        await using var connScope = conn;
        var task = new TaskPlan { Target = "dbo.T", SourceQuery = "unused" };   // no Columns
        var table = new DataTable();
        table.Columns.Add("V", typeof(int));    // source order differs from the target's (id, v)
        table.Columns.Add("Id", typeof(int));
        table.Rows.Add(7, 1);
        table.Rows.Add(8, 2);
        var loader = new BulkLoader(task, new TransferOptions { ErrorMode = "skip" });
        TransferException? refused = null;
        await using (var scope = await TxScope.BeginAsync(conn, default))
        {
            try
            {
                await loader.LoadAsync(scope, table, false, null, default);
                await scope.CommitAsync(default);
            }
            catch (TransferException ex) { refused = ex; }
        }
        // Harm: SqlBulkCopy without mappings pairs columns by ordinal, so V's values would become ids and ids would become V.
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.T WHERE id IN (7, 8)"));
        Assert.Equal("bad_task", refused?.Code);
    }

    [Fact]
    public async Task A_binding_the_query_does_not_return_is_a_plan_defect_not_a_chunk_of_rejected_rows()
    {
        var (db, conn) = await OpenAsync();
        await using var dbScope = db;
        await using var connScope = conn;
        var task = TaskFor();
        task.Columns = [.. task.Columns, new ColumnBinding("Missing", "note")];
        var loader = new BulkLoader(task, new TransferOptions { ErrorMode = "skip" });
        ChunkOutcome? outcome = null;
        TransferException? refused = null;
        await using (var scope = await TxScope.BeginAsync(conn, default))
        {
            try { outcome = await loader.LoadAsync(scope, Rows(4), true, null, default); }
            catch (TransferException ex) { refused = ex; }
        }
        // Harm: every attempt fails on the mapping, so bisection reports every good row as rejected and the chunk commits nothing.
        Assert.True(outcome is null || outcome.Failed.Count == 0, $"a plan defect was reported as {outcome?.Failed.Count} rejected rows");
        Assert.Equal("bad_task", refused?.Code);
        Assert.Contains("Missing", refused!.Details);
    }

    [Fact]
    public async Task A_staging_binding_with_no_stg_column_is_refused_instead_of_loading_NULL()
    {
        var (db, conn) = await OpenAsync();
        await using var dbScope = db;
        await using var connScope = conn;
        var task = TaskFor("staging_merge");
        task.StagingDdl = "CREATE TABLE #stg (Id int NOT NULL, Pid int NOT NULL, Qty int NULL, At datetime2(0) NULL)";   // no note column
        task.MergeSql = "SET IDENTITY_INSERT dbo.C ON; INSERT dbo.C (id, pid, qty, at) SELECT Id, Pid, Qty, At FROM #stg; SET IDENTITY_INSERT dbo.C OFF;";
        var loader = new BulkLoader(task, new TransferOptions { ErrorMode = "skip" });
        TransferException? refused = null;
        await using (var scope = await TxScope.BeginAsync(conn, default))
        {
            try
            {
                await loader.LoadAsync(scope, Rows(4), true, null, default);
                await scope.CommitAsync(default);
            }
            catch (TransferException ex) { refused = ex; }
        }
        // Harm: the bound "ok" notes would silently arrive as NULL.
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.C WHERE note IS NULL"));
        Assert.Equal("bad_task", refused?.Code);
    }

    [Fact]
    public async Task Smalldatetime_targets_get_what_the_server_CAST_gives_for_each_source_type()
    {
        // SQL Server's smalldatetime rounding depends on the source type (a datetime has already rounded .999 up to the next second),
        // so the expected value is the server's own CAST of the source column, never a hand-coded constant.
        string[] values = ["2020-01-01T10:00:29.998", "2020-01-01T10:00:29.999", "2020-01-01T10:00:59.999", "2020-01-01T23:59:59.999",
            "2020-01-01T23:59:29.999", "2020-01-01T10:00:30.000"];
        await using var db = await TempDatabase.CreateAsync("dbm_bulk");
        await db.ExecAsync("""
            CREATE TABLE dbo.S (k int NOT NULL PRIMARY KEY, dt datetime NULL, d3 datetime2(3) NULL, d7 datetime2(7) NULL);
            CREATE TABLE dbo.D (k int NOT NULL PRIMARY KEY, dt smalldatetime NULL, d3 smalldatetime NULL, d7 smalldatetime NULL);
            """);
        await using var conn = new SqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        for (int i = 0; i < values.Length; i++)
        {
            await using var ins = new SqlCommand("INSERT dbo.S VALUES (@k, CAST(@v AS datetime), CAST(@v AS datetime2(3)), CAST(@v AS datetime2(7)))", conn);
            ins.Parameters.Add(new SqlParameter("@k", SqlDbType.Int) { Value = i });
            ins.Parameters.Add(new SqlParameter("@v", SqlDbType.VarChar, 30) { Value = values[i] });
            await ins.ExecuteNonQueryAsync();
        }

        var task = new TaskPlan { Target = "dbo.D", SourceQuery = "SELECT k, dt, d3, d7 FROM dbo.S", KeyColumns = ["k"],
            Columns = [new("k", "k"), new("dt", "dt"), new("d3", "d3"), new("d7", "d7")] };
        var shape = await TargetShape.LoadAsync(conn, task.Target, default);
        DataTable table;
        await using (var cmd = new SqlCommand(task.SourceQuery, conn))
        await using (var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess))
        {
            table = ChunkReader.NewTable(reader);
            Assert.Equal(values.Length, await ChunkReader.FillAsync(reader, table, 100, default));
        }
        shape.Normalize(table, task.Columns);
        await using (var scope = await TxScope.BeginAsync(conn, default))
        {
            var outcome = await new BulkLoader(task, new TransferOptions { ErrorMode = "stop" }).LoadAsync(scope, table, true, null, default);
            Assert.Empty(outcome.Failed);
            await scope.CommitAsync(default);
        }

        var mismatches = new List<string>();
        await using (var cmp = new SqlCommand("""
            SELECT s.k, t.type, CONVERT(varchar(30), t.cast_value, 121), CONVERT(varchar(30), t.loaded, 121)
            FROM dbo.S AS s JOIN dbo.D AS d ON d.k = s.k
            CROSS APPLY (VALUES ('datetime', CAST(s.dt AS smalldatetime), d.dt), ('datetime2(3)', CAST(s.d3 AS smalldatetime), d.d3),
                                ('datetime2(7)', CAST(s.d7 AS smalldatetime), d.d7)) AS t(type, cast_value, loaded)
            ORDER BY s.k, t.type
            """, conn))
        await using (var r = await cmp.ExecuteReaderAsync())
        {
            int rows = 0;
            while (await r.ReadAsync())
            {
                rows++;
                if (r.GetString(2) != r.GetString(3))
                    mismatches.Add($"{values[r.GetInt32(0)]} as {r.GetString(1)}: CAST {r.GetString(2)}, loaded {r.GetString(3)}");
            }
            Assert.Equal(values.Length * 3, rows);
        }
        Assert.True(mismatches.Count == 0, "smalldatetime differs from CAST: " + string.Join("; ", mismatches));
    }

    [Fact]
    public async Task Bulk_copied_values_equal_INSERT_SELECT_for_awkward_types()
    {
        await using var db = await TempDatabase.CreateAsync("dbm_bulk");
        await db.ExecAsync("""
            CREATE TABLE dbo.S (k int NOT NULL PRIMARY KEY, d decimal(19,6) NULL, dt datetime NULL, dt2 datetime2(7) NULL, sdt datetime2(7) NULL,
              dto datetimeoffset(7) NULL, tm time(7) NULL, s nvarchar(20) NULL, vb varbinary(max) NULL, f float NULL, g uniqueidentifier NULL, big nvarchar(max) NULL);
            INSERT dbo.S VALUES
              (1, 1.235, '2020-01-01T10:00:00.003', '2020-01-01T10:00:00.0016666', '2020-01-01T10:00:29.999', '2020-01-01T10:00:00.5+05:30', '10:00:00.005', N'Жx', 0x00FF, 0.1, NEWID(), REPLICATE(CAST(N'ab' AS nvarchar(max)), 6000)),
              (2, -1.235, '2020-01-01T23:59:59.997', '2020-01-01T10:00:00.9983334', '2020-01-01T10:00:29.998', '2020-01-01T10:00:00.4999999-08:00', '23:59:59.9950000', N'plain', CAST(REPLICATE(CAST(0xAB AS varbinary(max)), 10000) AS varbinary(max)), 1e10, NULL, N''),
              (3, 1.234999, '1753-01-01T00:00:00.000', '2020-01-01T10:00:00.0050000', '2020-01-01T10:00:30.000', '2020-01-01T10:00:00.9999999+00:00', '00:00:00.0049999', N'', 0x, -0.0, NULL, NULL),
              (4, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL);
            CREATE TABLE dbo.D1 (k int NOT NULL PRIMARY KEY, d decimal(19,2) NULL, dt datetime2(2) NULL, dt2 datetime NULL, sdt smalldatetime NULL,
              dto datetimeoffset(0) NULL, tm time(2) NULL, s varchar(20) COLLATE Latin1_General_CI_AS NULL, vb varbinary(max) NULL, f real NULL, g uniqueidentifier NULL, big nvarchar(max) NULL);
            SELECT * INTO dbo.D2 FROM dbo.D1 WHERE 1 = 0;
            INSERT dbo.D2 SELECT * FROM dbo.S;
            """);
        await using var src = new SqlConnection(db.ConnectionString);
        await src.OpenAsync();
        await using var tgt = new SqlConnection(db.ConnectionString);
        await tgt.OpenAsync();
        var task = new TaskPlan
        {
            Target = "dbo.D1", SourceQuery = "SELECT * FROM dbo.S", KeyColumns = ["k"],
            Columns = [.. new[] { "k", "d", "dt", "dt2", "sdt", "dto", "tm", "s", "vb", "f", "g", "big" }.Select(c => new ColumnBinding(c, c))],
        };
        var shape = await TargetShape.LoadAsync(tgt, task.Target, default);
        await using (var cmd = new SqlCommand(task.SourceQuery, src))
        await using (var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess))
        {
            var table = ChunkReader.NewTable(reader);
            Assert.Equal(4, await ChunkReader.FillAsync(reader, table, 100, default));
            shape.Normalize(table, task.Columns);
            await using var scope = await TxScope.BeginAsync(tgt, default);
            var outcome = await new BulkLoader(task, new TransferOptions { ErrorMode = "stop" }).LoadAsync(scope, table, true, null, default);
            Assert.Empty(outcome.Failed);
            await scope.CommitAsync(default);
        }

        const string Canon = """
            SELECT k, CONVERT(varchar(60), d) AS d, CONVERT(varchar(40), dt, 121) AS dt, CONVERT(varchar(40), dt2, 121) AS dt2, CONVERT(varchar(40), sdt, 121) AS sdt,
              CONVERT(varchar(60), dto, 127) + '|' + DATENAME(tzoffset, dto) AS dto, CONVERT(varchar(20), tm, 114) + CONVERT(varchar(20), tm) AS tm,
              CAST(s AS varbinary(40)) AS s, HASHBYTES('SHA2_256', vb) AS vb, CAST(f AS binary(4)) AS f, g, HASHBYTES('SHA2_256', CAST(big AS varbinary(max))) AS big
            FROM dbo.
            """;
        var diff = await db.ScalarAsync<string>($"""
            SELECT COALESCE(STRING_AGG(CONCAT(x.side, ' k=', x.k, ' d=', x.d, ' dt=', x.dt, ' dt2=', x.dt2, ' sdt=', x.sdt, ' dto=', x.dto, ' tm=', x.tm,
              ' s=', CONVERT(varchar(90), x.s, 1), ' f=', CONVERT(varchar(20), x.f, 1)), ' ; '), '')
            FROM (
              SELECT 'bulk' AS side, * FROM ({Canon}D1 EXCEPT {Canon}D2) a
              UNION ALL
              SELECT 'cast' AS side, * FROM ({Canon}D2 EXCEPT {Canon}D1) b
            ) x
            """);
        Assert.True(diff.Length == 0, "bulk copy differs from INSERT ... SELECT: " + diff);
        Assert.Equal(4, await db.CountAsync("dbo.D1"));
    }

    // ---- fix round 1 ----

    [Fact]
    public async Task The_callers_XACT_ABORT_setting_survives_a_transaction_ending_load()
    {
        var (db, conn) = await OpenAsync();
        await using var dbScope = db;
        await using var connScope = conn;
        await using (var set = new SqlCommand("SET XACT_ABORT ON;", conn)) await set.ExecuteNonQueryAsync();
        var task = new TaskPlan { Target = "dbo.T", SourceQuery = "unused", Columns = [new("Id", "id"), new("V", "v")] };
        var table = new DataTable();
        table.Columns.Add("Id", typeof(int));
        table.Columns.Add("V", typeof(int));
        table.Rows.Add(1, 1);
        table.Rows.Add(2, -1);   // the trigger rolls the transaction back: the server transaction is gone
        var loader = new BulkLoader(task, new TransferOptions { ErrorMode = "skip", FireTriggers = true });
        TransferException? failure = null;
        await using (var scope = await TxScope.BeginAsync(conn, default))
        {
            try { await loader.LoadAsync(scope, table, allowRestart: false, progress: null, default); }
            catch (TransferException ex) { failure = ex; }
        }
        Assert.Equal("tx_ended", failure?.Code);
        // Harm: the restore is issued on the transaction the server already destroyed, so it fails every time and the caller's
        // own long-lived connection silently loses the XACT_ABORT it chose.
        await using var probe = new SqlCommand("SELECT CAST(@@OPTIONS & 16384 AS int)", conn);
        Assert.NotEqual(0, (int)(await probe.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task A_chunk_whose_every_row_fails_with_the_same_error_fails_the_task_instead_of_rejecting_every_row()
    {
        var (db, conn) = await OpenAsync();
        await using var dbScope = db;
        await using var connScope = conn;
        var loader = new BulkLoader(TaskFor(), new TransferOptions { ErrorMode = "skip" });
        ChunkOutcome? outcome = null;
        TransferException? failure = null;
        await using (var scope = await TxScope.BeginAsync(conn, default))
        {
            try { outcome = await loader.LoadAsync(scope, Rows(8, (i, v) => v[1] = 99), true, null, default); }
            catch (TransferException ex) { failure = ex; }
        }
        // Harm: a customer's whole table shredded into error rows and the run reported as "completed with N rejected".
        Assert.True(outcome is null, $"every row failed for the same reason, and the chunk still reported {outcome?.Failed.Count} rejected rows");
        Assert.Equal("bad_task", failure?.Code);
        Assert.Contains("FK_C_P", failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_broken_MergeSql_fails_the_task_instead_of_rejecting_every_row()
    {
        var (db, conn) = await OpenAsync();
        await using var dbScope = db;
        await using var connScope = conn;
        var task = TaskFor("staging_merge");
        task.MergeSql = "INSERT dbo.C (pid, qty, note, at) SELECT Pid, Qty, Note, nonexistent_col FROM #stg;";
        var loader = new BulkLoader(task, new TransferOptions { ErrorMode = "skip" });
        ChunkOutcome? outcome = null;
        TransferException? failure = null;
        await using (var scope = await TxScope.BeginAsync(conn, default))
        {
            try { outcome = await loader.LoadAsync(scope, Rows(8), true, null, default); }
            catch (TransferException ex) { failure = ex; }
        }
        // Harm: the broken merge is re-run once per bisection node and every good row is reported rejected.
        Assert.True(outcome is null, $"a broken MergeSql was reported as {outcome?.Failed.Count} rejected rows");
        Assert.Equal("bad_task", failure?.Code);
        Assert.Contains("nonexistent_col", failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_chunk_whose_rows_fail_for_different_reasons_still_returns_rejected_rows()
    {
        var (db, conn) = await OpenAsync();
        await using var dbScope = db;
        await using var connScope = conn;
        var loader = new BulkLoader(TaskFor(), new TransferOptions { ErrorMode = "skip" });
        // Every row is bad, but two break the foreign key and two break the check constraint: that is data, not a plan defect.
        var rows = Rows(4, (i, v) => { if (i < 2) v[1] = 99; else v[2] = 0; });
        ChunkOutcome? outcome = null;
        TransferException? failure = null;
        await using (var scope = await TxScope.BeginAsync(conn, default))
        {
            try { outcome = await loader.LoadAsync(scope, rows, true, null, default); }
            catch (TransferException ex) { failure = ex; }
        }
        Assert.True(failure is null, $"rows failing for different reasons were reported as a task defect: {failure?.Message}");
        Assert.Equal(4, outcome!.Failed.Count);
        Assert.Equal(0, outcome.Loaded);
    }

    [Fact]
    public async Task One_source_column_bound_to_two_target_scales_gives_each_target_its_own_CAST()
    {
        await using var db = await TempDatabase.CreateAsync("dbm_bulk");
        await db.ExecAsync("""
            CREATE TABLE dbo.S (k int NOT NULL PRIMARY KEY, v datetime2(7) NOT NULL);
            INSERT dbo.S VALUES (1, '2020-01-01T10:00:00.9974999');
            CREATE TABLE dbo.D (k int NOT NULL PRIMARY KEY, coarse datetime2(0) NULL, fine datetime2(7) NULL);
            """);
        await using var conn = new SqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        foreach (var order in new[] { new[] { "coarse", "fine" }, new[] { "fine", "coarse" } })
        {
            await db.ExecAsync("DELETE dbo.D;");
            var task = new TaskPlan
            {
                Target = "dbo.D", SourceQuery = "SELECT k, v FROM dbo.S", KeyColumns = ["k"],
                Columns = [new("k", "k"), new("v", order[0]), new("v", order[1])],
            };
            var shape = await TargetShape.LoadAsync(conn, task.Target, default);
            DataTable table;
            await using (var cmd = new SqlCommand(task.SourceQuery, conn))
            await using (var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess))
            {
                table = ChunkReader.NewTable(reader);
                Assert.Equal(1, await ChunkReader.FillAsync(reader, table, 10, default));
            }
            shape.Normalize(table, task.Columns);
            await using (var scope = await TxScope.BeginAsync(conn, default))
            {
                var outcome = await new BulkLoader(task, new TransferOptions { ErrorMode = "stop" }).LoadAsync(scope, table, true, null, default);
                Assert.Empty(outcome.Failed);
                await scope.CommitAsync(default);
            }
            // Harm: Normalize rounded the shared source column in place, so whichever binding ran first decided both targets
            // and the finer one silently lost its fraction.
            var diff = await db.ScalarAsync<string>("""
                SELECT COALESCE(STRING_AGG(CONCAT('coarse=', CONVERT(varchar(30), d.coarse, 121), ' (CAST ', CONVERT(varchar(30), CAST(s.v AS datetime2(0)), 121),
                  '), fine=', CONVERT(varchar(30), d.fine, 121), ' (CAST ', CONVERT(varchar(30), CAST(s.v AS datetime2(7)), 121), ')'), ' ; '), '')
                FROM dbo.S AS s JOIN dbo.D AS d ON d.k = s.k
                WHERE d.coarse <> CAST(s.v AS datetime2(0)) OR d.fine <> CAST(s.v AS datetime2(7))
                """);
            Assert.True(diff.Length == 0, $"bindings in order {order[0]}, {order[1]}: {diff}");
            Assert.Equal(1L, await db.CountAsync("dbo.D"));
        }
    }

    [Fact]
    public async Task Progress_reports_only_rows_that_survived_the_attempt()
    {
        var (db, conn) = await OpenAsync();
        await using var dbScope = db;
        await using var connScope = conn;
        const int N = 6000;   // more than NotifyAfter, so the whole-chunk attempt reports before it fails
        var loader = new BulkLoader(TaskFor(), new TransferOptions { ErrorMode = "skip" });
        var reported = new List<long>();
        ChunkOutcome outcome;
        await using (var scope = await TxScope.BeginAsync(conn, default))
        {
            outcome = await loader.LoadAsync(scope, Rows(N, (i, v) => { if (i == N - 1) v[3] = "far too long!!"; }),
                allowRestart: true, progress: v => { lock (reported) reported.Add(v); }, default);
            await scope.CommitAsync(default);
        }
        long[] seen;
        lock (reported) seen = [.. reported];
        Assert.Equal(N - 1, outcome.Loaded);
        Assert.Equal((long)(N - 1), await db.CountAsync("dbo.C"));
        // Harm: the whole-chunk attempt notified 5000 rows copied and was then rolled back to the savepoint, leaving the caller
        // believing rows were loaded that no longer exist -- and it was never told anything again.
        Assert.True(seen.Length > 0, "the caller was told no progress at all");
        Assert.True(seen.Contains(0L), "the rolled-back first attempt was never retracted: " + string.Join(",", seen));
        Assert.True(seen[^1] == outcome.Loaded, $"the caller's last progress was {seen[^1]} while {outcome.Loaded} rows survived");
        Assert.True(seen.All(v => v <= outcome.Loaded), $"progress reported {seen.Max()} rows while only {outcome.Loaded} survived");
    }

    [Fact]
    public async Task Stop_mode_says_how_many_rows_it_was_handed_so_the_unaccounted_ones_are_visible()
    {
        var (db, conn) = await OpenAsync();
        await using var dbScope = db;
        await using var connScope = conn;
        var loader = new BulkLoader(TaskFor(), new TransferOptions { ErrorMode = "stop" });
        ChunkOutcome outcome;
        await using (var scope = await TxScope.BeginAsync(conn, default))
        {
            outcome = await loader.LoadAsync(scope, Rows(20, Spoil), true, null, default);
            await scope.CommitAsync(default);   // the misuse the type has to make visible: 5.3 rolls back here, a later caller may not
        }
        // Harm: 20 rows handed over, Loaded=3 Failed=1, 16 rows in neither list, and the result looks successful.
        Assert.Equal(3, outcome.Loaded);
        Assert.Single(outcome.Failed);
        Assert.Equal(20L, outcome.Attempted);
        Assert.True(outcome.Loaded + outcome.Failed.Count < outcome.Attempted,
            $"the outcome accounts for every one of the {outcome.Attempted} rows, so nothing is being lost here");
        Assert.Equal(3L, await db.CountAsync("dbo.C"));
    }

    [Fact]
    public async Task A_binding_to_a_column_the_target_does_not_have_is_refused_before_a_row_is_attempted()
    {
        var (db, conn) = await OpenAsync();
        await using var dbScope = db;
        await using var connScope = conn;
        var task = TaskFor();
        task.Columns = [.. task.Columns, new ColumnBinding("Note", "nonexistent_col")];
        var loader = new BulkLoader(task, new TransferOptions { ErrorMode = "skip" }, await TargetShape.LoadAsync(conn, task.Target, default));
        var touched = new List<long>();
        ChunkOutcome? outcome = null;
        TransferException? refused = null;
        await using (var scope = await TxScope.BeginAsync(conn, default))
        {
            try
            {
                outcome = await loader.LoadAsync(scope, Rows(8), true, v => { lock (touched) touched.Add(v); }, default);
                await scope.CommitAsync(default);
            }
            catch (TransferException ex) { refused = ex; }
        }
        // Harm: SqlBulkCopy rejects every row on the mapping, so a one-character typo in a target column name turns a customer's
        // table into error rows reported as "completed with 8 rejected".
        Assert.True(outcome is null, $"a plan defect was reported as {outcome?.Failed.Count} rejected rows");
        Assert.True(touched.Count == 0, $"the chunk was attempted {touched.Count} times before the binding was checked");
        Assert.Equal("bad_task", refused?.Code);
        Assert.Contains("nonexistent_col", refused!.Details);
        Assert.Equal(0L, await db.CountAsync("dbo.C"));
    }

    [Fact]
    public async Task A_binding_to_an_identity_column_without_identityInsert_is_refused_instead_of_renumbering_the_rows()
    {
        var (db, conn) = await OpenAsync();
        await using var dbScope = db;
        await using var connScope = conn;
        var task = TaskFor();
        task.IdentityInsert = false;
        var loader = new BulkLoader(task, new TransferOptions { ErrorMode = "skip" }, await TargetShape.LoadAsync(conn, task.Target, default));
        ChunkOutcome? outcome = null;
        TransferException? refused = null;
        await using (var scope = await TxScope.BeginAsync(conn, default))
        {
            try
            {
                outcome = await loader.LoadAsync(scope, Rows(4), true, null, default);
                await scope.CommitAsync(default);
            }
            catch (TransferException ex) { refused = ex; }
        }
        // Harm: source ids 100..103 land as 1, 2, 3, 4 with Loaded=4, Failed=0 and no warning; every foreign key pointing at
        // those ids now points somewhere else.
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.C WHERE id BETWEEN 1 AND 4"));
        Assert.True(outcome is null, $"the rows were renumbered and reported as {outcome?.Loaded} loaded");
        Assert.Equal("bad_task", refused?.Code);
        Assert.Contains("id", refused!.Details);
        Assert.Contains("identityInsert", refused.Message, StringComparison.Ordinal);   // and the way out
        Assert.Contains("drop the binding", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_merge_that_moved_no_rows_can_be_seen_in_the_outcome()
    {
        var (db, conn) = await OpenAsync();
        await using var dbScope = db;
        await using var connScope = conn;
        var task = TaskFor("staging_merge");
        task.MergeSql = "SET IDENTITY_INSERT dbo.C ON; INSERT dbo.C (id, pid, qty, note, at) SELECT Id, Pid, Qty, Note, At FROM #stg WHERE 1 = 0; SET IDENTITY_INSERT dbo.C OFF;";
        ChunkOutcome staged, direct;
        await using (var scope = await TxScope.BeginAsync(conn, default))
        {
            staged = await new BulkLoader(task, new TransferOptions { ErrorMode = "skip" }).LoadAsync(scope, Rows(4), true, null, default);
            await scope.CommitAsync(default);
        }
        // Harm: ExecuteNonQuery's count was discarded, so a merge that moved nothing was indistinguishable from one that worked.
        Assert.Equal(4, staged.Loaded);
        Assert.Equal(0L, await db.CountAsync("dbo.C"));
        Assert.Equal(0L, staged.MergeRowsAffected);

        await using (var scope = await TxScope.BeginAsync(conn, default))
        {
            direct = await new BulkLoader(TaskFor(), new TransferOptions { ErrorMode = "skip" }).LoadAsync(scope, Rows(4), true, null, default);
            await scope.CommitAsync(default);
        }
        Assert.Equal(4, direct.Loaded);
        Assert.Null(direct.MergeRowsAffected);   // no MergeSql: nothing to report, not "moved nothing"
    }
}
