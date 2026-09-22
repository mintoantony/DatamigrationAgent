using System.Collections.Concurrent;
using Dbm.Core;
using Dbm.Core.Samples;
using Dbm.Core.Sql;
using Dbm.Core.State;
using Microsoft.Data.SqlClient;

namespace Dbm.Cli.Commands;

/// <summary>
/// `dbm demo --server "&lt;conn&gt;" [--scale n] [--prefix name] [--force] [--attach]` — build the LegacyShop → ShopV2
/// sample pair for a trial run. With --attach both connections are saved into this project, so nothing is pasted anywhere.
/// `dbm demo --attach` without --server takes the server from a connection already saved in the project on the Setup
/// screen (the source, else the target; open item 38), so a password never has to leave the browser.
/// </summary>
public sealed class DemoCommand : ICommand
{
    public string Name => "demo";
    public string Help => "Create the LegacyShop/ShopV2 sample databases (--attach saves them as this project's connections; " +
                          "without --server it uses the server of a connection saved on the Setup screen)";

    /// <summary>
    /// Test seam, keyed by exact database name like DemoDatabases.CreateDatabaseSuffixOverrides: the connection string
    /// --attach probes in place of that database's own, so a test can make the probe fail on the server after both
    /// databases were created and seeded (e.g. one naming a database that does not exist).
    /// </summary>
    internal static readonly ConcurrentDictionary<string, string> ProbeConnectionStringOverrides = new(StringComparer.Ordinal);

    private static string ProbeTarget(string database, string connectionString) =>
        ProbeConnectionStringOverrides.TryGetValue(database, out var o) ? o : connectionString;

    /// <summary>What the CLI prints per side: the connection string with every secret replaced by ***.</summary>
    public static object ConnectionView(string database, string connectionString) => new
    {
        database,
        connectionString = Redactor.Scrub(connectionString, Redactor.SecretsOf(connectionString)),
        describe = Redactor.Describe(connectionString),
    };

    private const string Usage =
        "dbm demo --server \"<connection string to any database on the server>\" [--scale n] [--prefix name] [--force] [--attach], " +
        "or, in a project folder, dbm demo --attach [--scale n] [--prefix name] [--force] after saving a connection to that server " +
        "on the Setup screen";

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        var server = args.Opt("server");
        var attach = args.Flag("attach");
        if (string.IsNullOrWhiteSpace(server) && !attach) throw new CliFailure("usage", Usage);

        var scale = args.Int("scale", 1);
        if (scale is < 1 or > 1000) throw new CliFailure("usage", "--scale must be between 1 and 1000.");

        var prefix = args.Opt("prefix") ?? DemoDatabases.DefaultPrefix;
        if (!DemoDatabases.IsValidPrefix(prefix))
            throw new CliFailure("usage", "--prefix may contain only letters, digits and '_' (at most 50 characters).");

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
            // Open item 38: without --server the server comes from a connection saved on the Setup screen (the source if
            // saved, else the target), so a SQL-auth password is typed only into the browser and never passes through
            // the chat or a terminal command line.
            if (string.IsNullOrWhiteSpace(server))
            {
                server = project.Connections.GetConnectionString(Side.Src) ?? project.Connections.GetConnectionString(Side.Tgt);
                if (server is null)
                {
                    throw new CliFailure("usage",
                        "No --server given and no connection is saved in this project: save a connection to the server on the " +
                        "Setup screen (either side, any database), then run dbm demo --attach again. Usage: " + Usage);
                }
            }
        }
        var serverText = server!;   // given, or read from the project just above

        try
        {
            var result = await DemoDatabases.CreateAsync(serverText, scale, prefix, args.Flag("force"), CancellationToken.None);
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
                        Redactor.SecretsOf(serverText)));
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
            throw new CliFailure("sql_error", Redactor.Scrub(ex.Message, Redactor.SecretsOf(serverText)));
        }
    }

    /// <summary>Saves both connections exactly like POST /api/connections/{side} does, then lets the workflow continue.</summary>
    private static async Task AttachAsync(CliContext ctx, Workspace ws, DemoResult result, CancellationToken ct)
    {
        var sourceMeta = await SqlConnect.ProbeAsync(ProbeTarget(result.SourceDatabase, result.SourceConnectionString), ct);
        var targetMeta = await SqlConnect.ProbeAsync(ProbeTarget(result.TargetDatabase, result.TargetConnectionString), ct);
        using var services = ctx.OpenServices(ws);
        services.Connections.Save(Side.Src, result.SourceConnectionString, sourceMeta);
        services.Connections.Save(Side.Tgt, result.TargetConnectionString, targetMeta);
        services.Sink.Publish("state_changed", new { phase = PhaseName.Setup, status = services.Phases.Get(PhaseName.Setup).Status });
        services.Workflow.OnConnectionsSaved();
    }
}
