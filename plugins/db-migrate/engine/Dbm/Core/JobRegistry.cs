using Dbm.Core.Jobs;

namespace Dbm.Core;

public static class JobRegistry
{
    /// <summary>
    /// Every job handler (a queued job without a handler fails with "no handler for &lt;kind&gt;"). Later milestones append
    /// their lines below the marker comment: T2.5 <c>yield return new Dbm.Core.Catalog.DiscoverJob();</c>, T2.6 AnalyzeJob,
    /// T3.3 AutomapJob, T4.3 SqlGenJob.
    /// </summary>
    public static IEnumerable<IJobHandler> Create(DbmServices s)
    {
        if (s is null) yield break;   // keeps this an iterator while no handler is registered
        // milestone registrations below
        yield return new Dbm.Core.Catalog.DiscoverJob();   // T2.5
        yield return new Dbm.Core.Analysis.AnalyzeJob();   // T2.6
    }
}
