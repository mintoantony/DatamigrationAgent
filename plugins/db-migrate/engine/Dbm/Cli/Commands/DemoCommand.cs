using Dbm.Core;
using Dbm.Core.Samples;
using Dbm.Core.Sql;
using Dbm.Core.State;
using Microsoft.Data.SqlClient;

namespace Dbm.Cli.Commands;

/// <summary>
/// `dbm demo --server "&lt;conn&gt;" [--scale n] [--prefix name] [--force] [--attach]` — build the LegacyShop → ShopV2
/// sample pair for a trial run. With --attach both connections are saved into this project, so nothing is pasted anywhere.
/// </summary>
public sealed class DemoCommand : ICommand
{
    public string Name => "demo";
    public string Help => "Create the LegacyShop/ShopV2 sample databases (--attach saves them as this project's connections)";

    /// <summary>What the CLI prints per side: the connection string with every secret replaced by ***.</summary>
    public static object ConnectionView(string database, string connectionString) => new
    {
        database,
        connectionString = Redactor.Scrub(connectionString, Redactor.SecretsOf(connectionString)),
        describe = Redactor.Describe(connectionString),
    };

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        var server = args.Opt("server");
        if (string.IsNullOrWhiteSpace(server))
        {
            throw new CliFailure("usage",
                "dbm demo --server \"<connection string to any database on the server>\" [--scale n] [--prefix name] [--force] [--attach]");
        }

        var scale = args.Int("scale", 1);
        if (scale is < 1 or > 1000) throw new CliFailure("usage", "--scale must be between 1 and 1000.");

        var prefix = args.Opt("prefix") ?? DemoDatabases.DefaultPrefix;
        if (!DemoDatabases.IsValidPrefix(prefix))
            throw new CliFailure("usage", "--prefix may contain only letters, digits and '_' (at most 50 characters).");

        var attach = args.Flag("attach");
        Workspace? ws = null;
        if (attach)
        {
            ws = ctx.RequireProject();
            using var project = ctx.OpenServices(ws);
            if (project.Phases.Get(PhaseName.Transfer).Status != PhaseStatus.Pending)
            {
                throw new CliFailure("locked",
                    "Connections cannot change after the transfer has started; run the demo in a new project folder.");
            }
        }

        try
        {
            var result = await DemoDatabases.CreateAsync(server, scale, prefix, args.Flag("force"), CancellationToken.None);
            if (attach)
            {
                try
                {
                    await AttachAsync(ctx, ws!, result, CancellationToken.None);
                }
                catch (SqlException ex)
                {
                    throw new CliFailure("sql_error", Redactor.Scrub(
                        $"Created and seeded {result.SourceDatabase} and {result.TargetDatabase}, then failed saving them as this " +
                        $"project's connections: {ex.Message.Trim().TrimEnd('.')}. Re-run with --force --attach to start over.",
                        Redactor.SecretsOf(server)));
                }
            }
            return Output.Ok(ctx, new
            {
                ok = true,
                scale,
                source = ConnectionView(result.SourceDatabase, result.SourceConnectionString),
                target = ConnectionView(result.TargetDatabase, result.TargetConnectionString),
                attached = attach,
                next = attach
                    ? "Both connections are saved and discovery is queued - continue in the browser (dbm ui)."
                    : "Paste each connectionString into the Setup screen, or re-run inside a project folder with --attach.",
            });
        }
        catch (DemoExistsException ex)
        {
            throw new CliFailure("demo_exists", ex.Message);
        }
        catch (DemoFailedException ex)
        {
            // Ruling 157: names both databases, what happened to each, the server's text and the remedy.
            throw new CliFailure("sql_error", Redactor.Scrub(ex.Message, Redactor.SecretsOf(server)));
        }
    }

    /// <summary>Saves both connections exactly like POST /api/connections/{side} does, then lets the workflow continue.</summary>
    private static async Task AttachAsync(CliContext ctx, Workspace ws, DemoResult result, CancellationToken ct)
    {
        var sourceMeta = await SqlConnect.ProbeAsync(result.SourceConnectionString, ct);
        var targetMeta = await SqlConnect.ProbeAsync(result.TargetConnectionString, ct);
        using var services = ctx.OpenServices(ws);
        services.Connections.Save(Side.Src, result.SourceConnectionString, sourceMeta);
        services.Connections.Save(Side.Tgt, result.TargetConnectionString, targetMeta);
        services.Sink.Publish("state_changed", new { phase = PhaseName.Setup, status = services.Phases.Get(PhaseName.Setup).Status });
        services.Workflow.OnConnectionsSaved();
    }
}
