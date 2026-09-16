using Dbm.Core.SqlGen;
using Dbm.Core.Transfer;
using Microsoft.Data.SqlClient;
using System.Data;
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

    // ---- additions beyond the brief ----

    [Fact]
    public void Key_aliases_are_bracket_quoted()
        => Assert.Equal("(q.[a]]b] > @k0)", ChunkPlanner.Predicate(["a]b"]));

    [Fact]
    public void Trailing_line_comment_in_the_source_query_cannot_swallow_the_wrapper()
    {
        var task = Task("__k0");
        task.SourceQuery = "SELECT 1 AS [__k0] FROM dbo.X -- trailing comment";
        Assert.Contains("-- trailing comment\n) AS q", ChunkPlanner.Sql(task, afterKey: true));
    }

    [Fact]
    public void Command_binds_chunk_size_and_every_key_with_its_exact_type()
    {
        var task = Task("__k0", "__k1");
        var key = new KeyValue([new("varchar", 20, 0, 0), new("decimal", 9, 18, 4)], ["ab", 1.25m]);
        using var cmd = ChunkPlanner.Command(new SqlConnection(), task, key, 250);
        Assert.Equal(ChunkPlanner.Sql(task, afterKey: true), cmd.CommandText);
        Assert.Equal(new[] { "@__n", "@k0", "@k1" }, cmd.Parameters.Cast<SqlParameter>().Select(p => p.ParameterName));
        Assert.Equal(250L, cmd.Parameters["@__n"].Value);
        Assert.Equal(SqlDbType.VarChar, cmd.Parameters["@k0"].SqlDbType);
        Assert.Equal(20, cmd.Parameters["@k0"].Size);
        Assert.Equal((byte)4, cmd.Parameters["@k1"].Scale);

        using var first = ChunkPlanner.Command(new SqlConnection(), task, null, 250);
        Assert.Equal(ChunkPlanner.Sql(task, afterKey: false), first.CommandText);
        Assert.Single(first.Parameters);
    }

    [Fact]
    public void Command_rejects_a_chunk_size_that_would_read_as_done_and_a_key_of_the_wrong_arity()
    {
        var task = Task("__k0", "__k1");
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkPlanner.Command(new SqlConnection(), task, null, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkPlanner.Command(new SqlConnection(), task, null, -1));
        var shortKey = new KeyValue([new("int", 4, 10, 0)], [1]);
        Assert.Equal("bad_key", Assert.Throws<TransferException>(() => ChunkPlanner.Command(new SqlConnection(), task, shortKey, 10)).Code);
        // A longer key is the dangerous direction: unguarded, the loop binds @k0/@k1 from the first two values and silently
        // ignores the rest, producing a valid predicate over the wrong position (a checkpoint from a different key shape).
        var longKey = new KeyValue([new("int", 4, 10, 0), new("int", 4, 10, 0), new("int", 4, 10, 0)], [1, 2, 3]);
        Assert.Equal("bad_key", Assert.Throws<TransferException>(() => ChunkPlanner.Command(new SqlConnection(), task, longKey, 10)).Code);
    }

    [Fact]
    public void Null_and_malformed_tasks_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => ChunkPlanner.Sql(null!, false));
        Assert.Throws<ArgumentNullException>(() => ChunkPlanner.Predicate(null!));
        Assert.Equal("no_key", Assert.Throws<TransferException>(() => ChunkPlanner.Predicate([])).Code);
        Assert.Equal("no_key", Assert.Throws<TransferException>(() => ChunkPlanner.Sql(new TaskPlan { SourceQuery = "SELECT 1", KeyColumns = null! }, false)).Code);
        Assert.Equal("bad_task", Assert.Throws<TransferException>(() => ChunkPlanner.Sql(new TaskPlan { SourceQuery = null!, KeyColumns = ["__k0"] }, false)).Code);
        Assert.Equal("bad_task", Assert.Throws<TransferException>(() => ChunkPlanner.Sql(new TaskPlan { SourceQuery = " ; ", KeyColumns = ["__k0"] }, false)).Code);
        Assert.Equal("bad_task", Assert.Throws<TransferException>(() => ChunkPlanner.Predicate(["__k0", null!])).Code);
    }
}
