using Dbm.Core;
using Dbm.Core.Jobs;
using Dbm.Core.State;

namespace Dbm.Cli.Commands;

/// <summary><c>dbm map auto [--inline]</c>: re-runs the auto-mapper while Mapping is running or drafting.</summary>
public sealed class MapAutoCommand : ICommand
{
    public string Name => "map auto";
    public string Help => "Re-run the auto-mapper (only while Mapping is running or drafting) [--inline runs it now]";

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        using var services = ctx.OpenProject();
        var status = services.Phases.Get(PhaseName.Mapping).Status;
        if (status is not (PhaseStatus.Running or PhaseStatus.Drafting))
            return Output.Fail(ctx, "wrong_phase",
                $"Mapping is {EnumText.ToText(status)}; `map auto` only runs while Mapping is running or drafting.");

        // A queued or running automap job (e.g. right after Analysis was approved) is reused instead of queueing a second one.
        if (!services.Jobs.Active().Any(j => j.Phase == PhaseName.Mapping)) services.Workflow.RetryJob(PhaseName.Mapping);
        if (!args.Flag("inline")) return Output.Ok(ctx, new { ok = true, queued = "automap" });

        var ran = await new JobRunner(services).RunPendingAsync(CancellationToken.None);
        var job = services.Jobs.LatestFor(PhaseName.Mapping);
        if (job is { Status: JobStatus.Failed })
            return Output.Fail(ctx, "job_failed", job.Error ?? "automap failed");
        var phase = services.Phases.Get(PhaseName.Mapping);
        return Output.Ok(ctx, new
        {
            ok = true,
            ran,
            status = EnumText.ToText(phase.Status),
            version = phase.CurrentVersion,
            summary = services.Artifacts.Latest(PhaseName.Mapping)?.Summary
        });
    }
}
