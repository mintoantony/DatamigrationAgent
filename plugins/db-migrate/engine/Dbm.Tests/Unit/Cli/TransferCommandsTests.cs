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

    /// <summary>
    /// <c>--truncate --yes-target ShopV2</c> is the shape an operator actually types, and neither new flag may take a value.
    /// <para><b>The second half is a contract assertion whose trigger another guard already covers:</b> <c>Args.LooksLikeOption</c>
    /// stops <c>--yes-target</c> being read as the value of <c>--truncate</c> whatever <c>BooleanFlags</c> says, so taking the two
    /// names out again fails only the first two lines below. They are declared because that other guard is in a different class and
    /// only covers a following token that looks like an option: <c>--truncate 1</c> would otherwise bind "1" in silence.</para>
    /// </summary>
    [Fact]
    public void The_new_boolean_flags_never_take_a_value()
    {
        Assert.Contains("skip-errors", Args.BooleanFlags);
        Assert.Contains("truncate", Args.BooleanFlags);

        var (o, confirm) = TransferStartCommand.Parse(Args.Parse(new[] { "--truncate", "--yes-target", "ShopV2" }));
        Assert.True(o.TruncateTarget);
        Assert.Equal("ShopV2", confirm);

        var (o2, confirm2) = TransferStartCommand.Parse(Args.Parse(new[] { "--skip-errors", "ShopV2" }));
        Assert.Equal("skip", o2.ErrorMode);
        Assert.Null(confirm2);
    }

    /// <summary>A status line an agent can act on: the failed task's own error is the one thing it must carry, and it is the field
    /// <c>Json.Options</c> would drop when it is null - so its presence has to depend on the value, not on the task.</summary>
    [Fact]
    public void Status_keeps_a_run_error_and_omits_the_key_when_there_is_none()
    {
        var failed = TransferStatusCommand.Compact(JsonNode.Parse("""
            {"run":{"id":9,"status":"failed","error":"T02: CHECK constraint"},"tasks":[],
             "totals":{"rowsSource":1,"rowsDone":0,"rowsError":0,"tasksDone":0,"tasksTotal":1}}
            """)!);
        Assert.Equal("T02: CHECK constraint", (string?)failed["error"]);

        var running = TransferStatusCommand.Compact(JsonNode.Parse("""
            {"run":{"id":9,"status":"running"},"tasks":[],"totals":{"rowsSource":1,"rowsDone":0,"rowsError":0,"tasksDone":0,"tasksTotal":1}}
            """)!);
        Assert.False(running.ContainsKey("error"));
    }
}
