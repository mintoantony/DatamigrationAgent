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
        Assert.Equal(1L, db.Scalar<long>("PRAGMA user_version"));
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
