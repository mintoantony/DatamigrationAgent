using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.State;

public class StateDbTests
{
    [Fact]
    public void Open_applies_pragmas_and_schema_version_1()
    {
        using var tw = new TestWorkspace();
        using var db = StateDb.Open(tw.Ws.StateDbPath);

        Assert.True(File.Exists(tw.Ws.StateDbPath));
        Assert.Equal("wal", db.Scalar<string>("PRAGMA journal_mode"));
        Assert.Equal(1L, db.Scalar<long>("PRAGMA foreign_keys"));
        Assert.Equal(2L, db.Scalar<long>("PRAGMA user_version"));
        var tables = db.Query("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name", r => r.GetString(0));
        Assert.Equal(new[] { "artifact", "catalog", "connection", "error_row", "event", "feedback", "job", "phase", "project",
            "transfer_run", "transfer_task", "vector" }, tables);
    }

    [Fact]
    public void Reopening_keeps_data_and_does_not_reapply_the_schema()
    {
        using var tw = new TestWorkspace();
        using (var db = StateDb.Open(tw.Ws.StateDbPath)) db.Execute("INSERT INTO event (ts, type) VALUES ('t', 'x')");

        using var again = StateDb.Open(tw.Ws.StateDbPath);

        Assert.Equal(1L, again.Scalar<long>("SELECT COUNT(*) FROM event"));
    }

    /// <summary>A state database exactly as a released engine left it: the version-1 schema, user_version 1, and one rejected row.</summary>
    private static void WriteVersion1(string path, bool withErrorNumberColumn = false, int stampVersion = 1)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var c = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = StateDb.LoadSchema()
            + "INSERT INTO transfer_run (sql_version, status, options_json, started_at) VALUES (1, 'failed', '{}', '2026-09-17T09:00:00.0000000+00:00');"
            + "INSERT INTO transfer_task (run_id, task_id, target, ordinal, status, rows_error) VALUES (1, 'T01', 'app.P', 0, 'failed', 1);"
            + "INSERT INTO error_row (run_id, task_id, key_json, row_json, error, ts) VALUES "
            + "(1, 'T01', '{\"Id\":7}', '{\"Id\":7}', 'Violation of PRIMARY KEY constraint ''PK_P''.', '2026-09-17T09:01:00.0000000+00:00');"
            + (withErrorNumberColumn ? "ALTER TABLE error_row ADD COLUMN error_number INTEGER;" : "")
            + $"PRAGMA user_version = {stampVersion};";
        cmd.ExecuteNonQuery();
    }

    private static List<string> Columns(StateDb db, string table)
        => db.Query($"SELECT name FROM pragma_table_info('{table}') ORDER BY cid", r => r.GetString(0));

    /// <summary>
    /// Migration step 2 (Ruling 208, open item 46): error_row gains a nullable error_number. <b>Harm:</b> a workspace from the released
    /// engine either stays at version 1 - so recording a row with its number fails on a column that does not exist and the run ends over
    /// one rejected row - or is "upgraded" by re-running the schema, which fails on the existing tables or loses the rows recorded so far.
    /// </summary>
    [Fact]
    public void A_version_1_database_upgrades_to_version_2_with_its_rows_intact()
    {
        using var tw = new TestWorkspace();
        WriteVersion1(tw.Ws.StateDbPath);

        using var db = StateDb.Open(tw.Ws.StateDbPath);

        Assert.True(db.Scalar<long>("PRAGMA user_version") == 2, "the version-1 database was not migrated: user_version is still "
            + db.Scalar<long>("PRAGMA user_version").ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(Columns(db, "error_row").Contains("error_number"),
            "error_row has no error_number column after the upgrade: " + string.Join(", ", Columns(db, "error_row")));
        var row = db.Query("SELECT key_json, error, error_number IS NULL FROM error_row", r => (r.GetString(0), r.GetString(1), r.GetInt64(2)));
        Assert.Equal(("{\"Id\":7}", "Violation of PRIMARY KEY constraint 'PK_P'.", 1L), Assert.Single(row));   // kept, and its number unknown
        Assert.Equal(1L, db.Scalar<long>("SELECT COUNT(*) FROM transfer_task WHERE rows_error = 1"));
    }

    /// <summary>Each step is additive and idempotent: a version-1 database that already has the column (a step interrupted after its
    /// ALTER but before its version stamp cannot happen inside one transaction, but a hand-repaired one can) still reaches version 2.</summary>
    [Fact]
    public void Step_2_is_idempotent_over_a_database_that_already_has_the_column()
    {
        using var tw = new TestWorkspace();
        WriteVersion1(tw.Ws.StateDbPath, withErrorNumberColumn: true);

        using var db = StateDb.Open(tw.Ws.StateDbPath);

        Assert.True(db.Scalar<long>("PRAGMA user_version") == 2,
            "a version-1 database that already had error_number was not brought to version 2");
        Assert.Equal(1, Columns(db, "error_row").Count(c => c == "error_number"));
        Assert.Equal(1L, db.Scalar<long>("SELECT COUNT(*) FROM error_row"));
    }

    /// <summary>
    /// Steps are joined from branches that were developed apart (batch F's step 3 beside this step 2), so a database can reach a
    /// later version without step 2's column - one opened by a build that had step 3 and not step 2. Step 2 is keyed on the column,
    /// not only on the version, and never lowers the version. <b>Harm:</b> a version gate alone skips it there, and the first rejected
    /// row recorded with its number fails on a missing column and ends the run.
    /// </summary>
    [Fact]
    public void A_later_version_database_without_the_column_gains_it_and_keeps_its_version()
    {
        using var tw = new TestWorkspace();
        WriteVersion1(tw.Ws.StateDbPath, stampVersion: 3);

        using var db = StateDb.Open(tw.Ws.StateDbPath);

        Assert.True(Columns(db, "error_row").Contains("error_number"),
            "a version-3 database without error_row.error_number was left without it: step 2 is gated on the version alone");
        long v = db.Scalar<long>("PRAGMA user_version");
        Assert.True(v == 3, "step 2 lowered a version-3 database to version "
            + v.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", so step 3 would run again");
        Assert.Equal(1L, db.Scalar<long>("SELECT COUNT(*) FROM error_row"));
    }

    public enum Colour { DeepBlue }

    [Fact]
    public void Binds_anonymous_properties_converting_enums_dates_bools_and_nulls()
    {
        using var tw = new TestWorkspace();
        using var db = StateDb.Open(tw.Ws.StateDbPath);
        db.Execute("CREATE TABLE t (e TEXT, d TEXT, b INTEGER, n TEXT)");
        var when = new DateTimeOffset(2026, 9, 11, 12, 30, 0, TimeSpan.FromHours(2));

        db.Execute("INSERT INTO t VALUES ($E, $D, $B, $N)", new { E = Colour.DeepBlue, D = when, B = true, N = (string?)null });

        var row = db.Query("SELECT e, d, b, n FROM t", r => (E: r.GetString(0), D: r.GetString(1), B: r.GetInt64(2), N: r.IsDBNull(3)));
        Assert.Equal(("deep_blue", "2026-09-11T10:30:00.0000000+00:00", 1L, true), row.Single());
    }

    [Fact]
    public void Binds_dictionary_arguments()
    {
        using var tw = new TestWorkspace();
        using var db = StateDb.Open(tw.Ws.StateDbPath);

        var v = db.Scalar<long>("SELECT $A + $B", new Dictionary<string, object?> { ["A"] = 2L, ["B"] = 3L });

        Assert.Equal(5L, v);
    }

    [Fact]
    public void Scalar_converts_nulls_and_types()
    {
        using var tw = new TestWorkspace();
        using var db = StateDb.Open(tw.Ws.StateDbPath);

        Assert.Null(db.Scalar<long?>("SELECT NULL"));
        Assert.Null(db.Scalar<string>("SELECT NULL"));
        Assert.Equal(42, db.Scalar<int>("SELECT 42"));
        Assert.True(db.Scalar<bool>("SELECT 1"));
    }

    [Fact]
    public void Failed_transaction_rolls_back_including_nested_work()
    {
        using var tw = new TestWorkspace();
        using var db = StateDb.Open(tw.Ws.StateDbPath);

        Assert.Throws<InvalidOperationException>(() => db.InTransaction(() =>
        {
            db.Execute("INSERT INTO event (ts, type) VALUES ('t', 'outer')");
            db.InTransaction(() => db.Execute("INSERT INTO event (ts, type) VALUES ('t', 'inner')"));
            throw new InvalidOperationException("boom");
        }));

        Assert.Equal(0L, db.Scalar<long>("SELECT COUNT(*) FROM event"));
        var committed = db.InTransaction(() => db.Execute("INSERT INTO event (ts, type) VALUES ('t', 'ok')"));
        Assert.Equal(1, committed);
    }

    [Fact]
    public async Task Concurrent_writers_on_one_instance_are_serialised()
    {
        using var tw = new TestWorkspace();
        using var db = StateDb.Open(tw.Ws.StateDbPath);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 25; i++)
            {
                db.InTransaction(() =>
                {
                    var n = db.Scalar<long>("SELECT COUNT(*) FROM event");
                    db.Execute("INSERT INTO event (ts, type) VALUES ($Ts, $Type)", new { Ts = n.ToString(), Type = $"t{t}" });
                });
            }
        })));

        Assert.Equal(200L, db.Scalar<long>("SELECT COUNT(*) FROM event"));
    }

    [Fact]
    public void Two_instances_on_the_same_file_see_each_others_commits()
    {
        using var tw = new TestWorkspace();
        using var a = StateDb.Open(tw.Ws.StateDbPath);
        using var b = StateDb.Open(tw.Ws.StateDbPath);

        a.InTransaction(() => a.Execute("INSERT INTO event (ts, type) VALUES ('t', 'from-a')"));

        Assert.Equal("from-a", b.Scalar<string>("SELECT type FROM event"));
    }
}
