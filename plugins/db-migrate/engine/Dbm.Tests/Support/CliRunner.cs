using System.Text.Json.Nodes;
using Dbm.Cli;
using Dbm.Core;

namespace Dbm.Tests.Support;

public sealed record CliResult(int Exit, string Out, string Err)
{
    public JsonNode Json => JsonNode.Parse(Out) ?? throw new InvalidOperationException("empty output");
}

/// <summary>Runs CliApp in-process against a workspace (always passes --workspace), capturing stdout/stderr.</summary>
public static class CliRunner
{
    public static async Task<CliResult> RunAsync(Workspace ws, Func<Workspace, DbmServices>? factory, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await CliApp.RunAsync([.. args, "--workspace", ws.Root], stdout, stderr, factory);
        return new CliResult(exit, stdout.ToString().Trim(), stderr.ToString());
    }
}
