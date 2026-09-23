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
/// in memory) against the live databases and prints the ValidationReport; exit 1 when it is not ok. SQL is never stored.
/// Bare carriage returns are added as errors by the offline scan (open item 17); without connections the command still reports them,
/// with the "live validation skipped" line as its globalWarnings, and refuses as <c>not_ready</c> only when there is nothing to say.
/// Without --patch, a whole-plan live pass over a version lacking validation evidence stores it as a new version while Sql awaits
/// review (Rulings 210/211; <c>stored</c>/<c>storedVersion</c>/<c>storeNote</c>) and nothing otherwise.</summary>
public sealed class SqlValidateCommand : ICommand
{
    public string Name => "sql validate";
    public string Help => "Validate the current SQL plan live [--task <id>] [--patch <file> checks a patch first]; exit 1 when not ok";

    /// <summary>Test seam (open item 8 L3, decision I-2). Backed by <see cref="AsyncLocal{T}"/>, not a plain mutable static field
    /// (decision I-3, sweep I review MED): a `static` field is one process-wide slot regardless of how many
    /// <see cref="SqlValidateCommand"/> instances exist, so a plain static field set by one test would be visible to every other
    /// test running concurrently in a different xUnit collection - including <c>SqlCommandsIntegrationTests</c>, which runs the real
    /// `sql validate` against LocalDB. <see cref="AsyncLocal{T}"/> scopes the override to the setting call's own async execution
    /// context (and whatever it awaits), so a value set inside one test's `CliRunner.RunAsync` call is invisible to a
    /// concurrently-running test's, even though both flow through this same static property. Tests may still replace it (restore
    /// it in a finally block) to simulate the 5-minute budget expiring without a live database or a real 5-minute wait.</summary>
    static readonly AsyncLocal<Func<DbmServices, SqlPlanPayload, string?, CancellationToken, Task<ValidationReport>>?> ValidateLiveOverride = new();

    internal static Func<DbmServices, SqlPlanPayload, string?, CancellationToken, Task<ValidationReport>> ValidateLive
    {
        get => ValidateLiveOverride.Value ?? SqlPlanSource.ValidateLiveAsync;
        set => ValidateLiveOverride.Value = value;
    }

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
            try
            {
                live = await ValidateLive(services, plan, args.Opt("task"), cts.Token);
            }
            // Open item 8 L3, decision I-2: this token is internal to the command and is never linked to any external
            // cancellation source, so any OperationCanceledException here is this 5-minute budget expiring - never a
            // cancellation the caller requested. Report it the same clean way an unconfigured connection is reported
            // (`skipped`, consumed by WithBareCarriageReturns below), instead of an unhandled-exception stack trace.
            catch (OperationCanceledException)
            {
                skipped = SqlPlanSource.SkippedPrefix + "timed out after 5 minutes";
            }
        }
        var report = SqlValidator.WithBareCarriageReturns(live, plan, args.Opt("task"), skipped ?? "")
            ?? throw new CliFailure("not_ready", skipped ?? "Both connections and the target catalog are needed to validate.");
        // Rulings 210/211: without --patch, a whole-plan pass records the evidence a version lacks, as Validate live does - only while
        // Sql awaits review, never while the agent drafts or reworks (its baseVersion must not move).
        var store = live is null || patchPath is not null ? new SqlPlanSource.StoreOutcome(false, null, null)
            : SqlPlanSource.StoreEvidence(services, row.Version, report, args.Opt("task"));
        return Output.Write(ctx, new
        {
            ok = report.Ok,
            version = row.Version,
            patched = patchPath is not null,
            taskErrors = report.TaskErrors,
            taskWarnings = report.TaskWarnings,
            globalErrors = report.GlobalErrors,
            globalWarnings = report.GlobalWarnings,
            stored = store.Stored,
            storedVersion = store.Version,
            storeNote = store.Note,
        }, report.Ok ? 0 : 1);
    }
}
