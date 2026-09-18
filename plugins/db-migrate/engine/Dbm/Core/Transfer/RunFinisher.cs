using System.Text.Json;
using System.Text.Json.Nodes;
using Dbm.Core.State;

namespace Dbm.Core.Transfer;

/// <summary>
/// Ruling 183 (final review I-1). The one place a finished run is recorded in the workflow: the Complete artifact for a completed run,
/// and <c>WorkflowEngine.OnTransferFinished</c>.
/// <para><b>Idempotent, and one transaction.</b> <c>TransferEngine.Finish</c> writes the run's terminal status before this runs, so a
/// process that dies in between - or a finisher that throws - leaves the Transfer phase <c>running</c> over a <c>completed</c> run.
/// That state refuses Start ("the Ready step is approved"), Resume (<c>not_resumable</c>), Cancel (<c>not_cancellable</c>) and Reopen
/// alike, and <c>dbm next</c> answers <c>await/transfer</c> for ever. <see cref="Reconcile"/> re-runs this step for it on server start
/// and on every <c>dbm next</c> / <c>dbm status</c>, so the same code finishes a run whoever gets there first; the BEGIN IMMEDIATE
/// transaction makes the second caller - another process included - find the phase already approved and do nothing.</para>
/// </summary>
public static class RunFinisher
{
    /// <summary>
    /// Records <paramref name="runId"/>'s outcome in the workflow when the Transfer phase is still <c>running</c>. Returns true when it
    /// changed the workflow (a completed run approved Transfer and Complete); false when there was nothing to do.
    /// <para>A failed or cancelled run changes nothing in the workflow by design - Transfer stays <c>running</c> so the run can be
    /// resumed, restarted or the plan reopened - so for those this only tells the workflow, which publishes the change.</para>
    /// </summary>
    public static bool Finish(DbmServices s, long runId)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Db.InTransaction(() =>
        {
            var run = s.Transfers.GetRun(runId);
            if (run is null || s.Phases.Get(PhaseName.Transfer).Status != PhaseStatus.Running) return false;
            switch (run.Status)
            {
                case RunStatus.Completed:
                    int version = StoredReportVersion(s, runId) ?? StoreReport(s, run);
                    s.Workflow.OnTransferFinished("completed", version);
                    return true;
                case RunStatus.Failed:
                    s.Workflow.OnTransferFinished("failed", null);
                    return false;
                case RunStatus.Cancelled:
                    s.Workflow.OnTransferFinished("cancelled", null);
                    return false;
                default:
                    return false;
            }
        });
    }

    /// <summary>
    /// The latest run is <c>completed</c> and the Transfer phase still says <c>running</c>: finish it. Returns the run id it finished,
    /// or null when the workflow already agrees with the run. Reads nothing else and writes nothing when there is nothing to do, so it
    /// is cheap enough for every <c>dbm next</c>.
    /// </summary>
    public static long? Reconcile(DbmServices s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.Phases.Get(PhaseName.Transfer).Status != PhaseStatus.Running) return null;
        TransferRunRow? latest;
        try
        {
            latest = s.Transfers.Latest();
        }
        catch (JsonException)
        {
            return null;   // ruling 142: an unreadable run is reported by the transfer routes themselves, in their own words
        }
        if (latest is not { Status: RunStatus.Completed }) return null;
        return Finish(s, latest.Id) ? latest.Id : null;
    }

    /// <summary>The Complete artifact already stored for this run (its payload's <c>runId</c>), so a retry never stores a second one.</summary>
    private static int? StoredReportVersion(DbmServices s, long runId)
    {
        foreach (var meta in s.Artifacts.List(PhaseName.Complete).Reverse())
        {
            var row = s.Artifacts.Get(PhaseName.Complete, meta.Version);
            if (row is null) continue;
            try
            {
                if (JsonNode.Parse(row.PayloadJson)?["runId"]?.GetValue<long>() == runId) return row.Version;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                // a stored report that will not parse belongs to no run we can name; keep looking
            }
        }
        return null;
    }

    private static int StoreReport(DbmServices s, TransferRunRow run)
    {
        FinalReport? report = null;
        try
        {
            report = run.SummaryJson is null ? null : Json.Deserialize<FinalReport>(run.SummaryJson);
        }
        catch (JsonException)
        {
            // The run's own summary is kept verbatim as the artifact below; only the one-line headline falls back.
        }
        string summary = report is null ? $"Transfer run {run.Id} completed." : FinalReportBuilder.Summary(report);
        int version = Math.Max(1, s.Artifacts.NextVersion(PhaseName.Complete));
        s.Artifacts.Add(PhaseName.Complete, version, run.SummaryJson ?? "{}", "script", summary);
        s.Sink.Publish("artifact_created", new { phase = "complete", version, author = "script" });
        return version;
    }
}
