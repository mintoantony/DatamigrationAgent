using System.Text.Json;
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Jobs;
using Dbm.Core.Patching;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Dbm.Core.Workflow;

namespace Dbm.Cli.Commands;

/// <summary><c>dbm sql gen [--inline]</c>: re-runs the SQL generator while Sql is running or drafting.</summary>
public sealed class SqlGenCommand : ICommand
{
    public string Name => "sql gen";
    public string Help => "Regenerate the SQL plan from the approved mapping (only while Sql is running or drafting) [--inline runs it now]";

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        using var services = ctx.OpenProject();
        var status = services.Phases.Get(PhaseName.Sql).Status;
        if (status is not (PhaseStatus.Running or PhaseStatus.Drafting))
            return Output.Fail(ctx, "wrong_phase",
                $"Sql is {EnumText.ToText(status)}; `sql gen` only runs while Sql is running or drafting.");

        services.Db.InTransaction(() =>
        {
            // WorkflowEngine.OnJobDone only accepts a job result while the phase is running, so a drafting phase goes back to running.
            if (status == PhaseStatus.Drafting) services.Phases.SetStatus(PhaseName.Sql, PhaseStatus.Running);
            if (!services.Jobs.Active().Any(j => j.Phase == PhaseName.Sql)) services.Workflow.RetryJob(PhaseName.Sql);
        });
        if (!args.Flag("inline")) return Output.Ok(ctx, new { ok = true, queued = "sqlgen" });

        var ran = await new JobRunner(services).RunPendingAsync(CancellationToken.None);
        var job = services.Jobs.LatestFor(PhaseName.Sql);
        if (job is { Status: JobStatus.Failed })
            return Output.Fail(ctx, "job_failed", job.Error ?? "sqlgen failed");
        var phase = services.Phases.Get(PhaseName.Sql);
        return Output.Ok(ctx, new
        {
            ok = true,
            ran,
            status = EnumText.ToText(phase.Status),
            version = phase.CurrentVersion,
            summary = services.Artifacts.Latest(PhaseName.Sql)?.Summary
        });
    }
}

/// <summary><c>dbm sql validate [--task T04] [--patch file]</c>: validates the current sql version (optionally with a patch applied
/// in memory) against the live databases and prints the ValidationReport; exit 1 when it is not ok. Nothing is stored.
/// Bare carriage returns are added as errors by the offline scan (open item 17); without connections the command still reports them,
/// with the "live validation skipped" line as its globalWarnings, and refuses as <c>not_ready</c> only when there is nothing to say.</summary>
public sealed class SqlValidateCommand : ICommand
{
    public string Name => "sql validate";
    public string Help => "Validate the current SQL plan live [--task <id>] [--patch <file> checks a patch first]; exit 1 when not ok";

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        using var services = ctx.OpenProject();
        var row = SqlPlanSource.CurrentRow(services) ?? throw new CliFailure("no_version", "The sql phase has no version yet.");

        var node = JsonNode.Parse(row.PayloadJson)!;
        var patchPath = args.Opt("patch");
        if (patchPath is not null)
        {
            if (!File.Exists(patchPath)) throw new CliFailure("not_found", $"Patch file not found: {patchPath}");
            try
            {
                var patch = Patch.Parse(File.ReadAllText(patchPath));
                // Same rule and wording as the web endpoint (CoreEndpoints human edit): a patch is only checked against its own phase.
                if (!Phases.TryParse(patch.Phase, out var patchPhase) || patchPhase != PhaseName.Sql)
                    throw new CliFailure("invalid_patch", $"patch.phase must be '{PhaseName.Sql.Text()}'.");
                if (patch.BaseVersion != row.Version)
                    throw new CliFailure("stale_patch", $"The patch is based on v{patch.BaseVersion} but the current version is v{row.Version}.");
                node = JsonPatch.Apply(node, patch.Ops);
            }
            catch (PatchException ex)
            {
                throw new CliFailure("invalid_patch", ex.Message);
            }
        }

        SqlPlanPayload plan;
        try
        {
            plan = Json.FromNode<SqlPlanPayload>(node);
        }
        catch (JsonException ex)
        {
            throw new CliFailure("bad_payload", ex.Message);
        }
        // Open item 17: the bare-CR scan is offline and runs on every call, so this command agrees with `apply --dry-run` about the
        // rule sql-engineer.md teaches - and says so even when live validation cannot run.
        var skipped = SqlPlanSource.SkippedWarning(services);
        ValidationReport? live = null;
        if (skipped is null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            live = await SqlPlanSource.ValidateLiveAsync(services, plan, args.Opt("task"), cts.Token);
        }
        var report = SqlValidator.WithBareCarriageReturns(live, plan, args.Opt("task"), skipped ?? "")
            ?? throw new CliFailure("not_ready", "Both connections and the target catalog are needed to validate.");
        return Output.Write(ctx, new
        {
            ok = report.Ok,
            version = row.Version,
            patched = patchPath is not null,
            taskErrors = report.TaskErrors,
            taskWarnings = report.TaskWarnings,
            globalErrors = report.GlobalErrors,
            globalWarnings = report.GlobalWarnings,
        }, report.Ok ? 0 : 1);
    }
}
