using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.State;

public class StateDbTests
{
    [Fact]
    public void Open_applies_pragmas_and_the_schema_up_to_version_3()
    {
        using var tw = new TestWorkspace();
        using var db = StateDb.Open(tw.Ws.StateDbPath);

        Assert.True(File.Exists(tw.Ws.StateDbPath));
        Assert.Equal("wal", db.Scalar<string>("PRAGMA journal_mode"));
        Assert.Equal(1L, db.Scalar<long>("PRAGMA foreign_keys"));
        Assert.Equal(3L, db.Scalar<long>("PRAGMA user_version"));
        var tables = db.Query("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name", r => r.GetString(0));
        Assert.Equal(new[] { "artifact", "catalog", "connection", "error_row", "event", "feedback", "job", "phase", "project",
            "transfer_run", "transfer_task", "vector" }, tables);
    }

    /// <summary>
    /// Open item 22's migration step 3. A workspace written by an earlier engine is at user_version 1; opening it must add the run's
    /// target columns and keep every row it had. <b>Harm:</b> a migration that stops at "version &gt;= 1" leaves the columns out, and the
    /// first run on the upgraded workspace dies on "no such column" - or one that re-applies the schema drops the run history.
    /// </summary>
    [Fact]
    public void A_version_1_database_upgrades_to_3_with_its_rows_intact()
    {
        using var tw = new TestWorkspace();
        Directory.CreateDirectory(Path.GetDirectoryName(tw.Ws.StateDbPath)!);
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={tw.Ws.StateDbPath};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = StateDb.LoadSchema() + """

                PRAGMA user_version = 1;
                INSERT INTO transfer_run (sql_version, status, options_json, started_at) VALUES (4, 'failed', '{}', '2026-09-01T00:00:00.0000000+00:00');
                INSERT INTO transfer_task (run_id, task_id, target, ordinal, status) VALUES (1, 'T01', 'app.X', 0, 'failed');
                """;
            cmd.ExecuteNonQuery();
        }

        using var db = StateDb.Open(tw.Ws.StateDbPath);

        var columns = db.Query("SELECT name FROM pragma_table_info('transfer_run')", r => r.GetString(0));
        Assert.True(columns.Contains("target_server") && columns.Contains("target_database"),
            "a version-1 workspace was not upgraded: transfer_run has no target columns (" + string.Join(", ", columns) + ")");
        Assert.Equal(3L, db.Scalar<long>("PRAGMA user_version"));
        Assert.Equal(1L, db.Scalar<long>("SELECT COUNT(*) FROM transfer_run WHERE sql_version = 4 AND status = 'failed' AND target_database IS NULL"));
        Assert.Equal(1L, db.Scalar<long>("SELECT COUNT(*) FROM transfer_task WHERE task_id = 'T01'"));
        db.Dispose();

        using var again = StateDb.Open(tw.Ws.StateDbPath);                            // the step is idempotent: reopening changes nothing
        Assert.Equal(1L, again.Scalar<long>("SELECT COUNT(*) FROM transfer_run"));
    }

    [Fact]
    public void Reopening_keeps_data_and_does_not_reapply_the_schema()
    {
        using var tw = new TestWorkspace();
        using (var db = StateDb.Open(tw.Ws.StateDbPath)) db.Execute("INSERT INTO event (ts, type) VALUES ('t', 'x')");

        using var again = StateDb.Open(tw.Ws.StateDbPath);

        Assert.Equal(1L, again.Scalar<long>("SELECT COUNT(*) FROM event"));
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
