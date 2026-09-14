using Dbm.Core;
using Dbm.Core.Catalog;
using Dbm.Core.Mapping;
using Dbm.Core.State;
using Dbm.Core.Workflow;

namespace Dbm.Tests.Support;

/// <summary>M3 helpers for services opened by TempProject / TestWorkspace.OpenServices.</summary>
public static class MappingTestData
{
    /// <summary>Saves SampleCatalogs as the discovered source and target catalogs (as DiscoverJob would).</summary>
    public static DbmServices WithSampleCatalogs(this DbmServices services)
    {
        var src = SampleCatalogs.Source();
        var tgt = SampleCatalogs.Target();
        services.Catalog.Save(Side.Src, src, Fingerprint.Compute(src));
        services.Catalog.Save(Side.Tgt, tgt, Fingerprint.Compute(tgt));
        return services;
    }

    /// <summary>Stores <paramref name="m"/> as the next Mapping version, makes it current and sets the phase status.</summary>
    public static ArtifactRow AddMapping(this DbmServices services, MappingPayload m, PhaseStatus status, string author = "script")
    {
        var version = services.Artifacts.NextVersion(PhaseName.Mapping);
        var row = services.Artifacts.Add(PhaseName.Mapping, version, Json.Serialize(m), author, null);
        services.Phases.SetCurrentVersion(PhaseName.Mapping, version);
        services.Phases.SetStatus(PhaseName.Mapping, status);
        return row;
    }

    /// <summary>Marks every phase before <paramref name="phase"/> approved (for Mapping: Setup, Discovery, Analysis).</summary>
    public static void ApproveBefore(this DbmServices services, PhaseName phase)
    {
        foreach (var p in WorkflowEngine.Order.TakeWhile(p => p != phase))
            services.Phases.SetStatus(p, PhaseStatus.Approved);
    }
}
