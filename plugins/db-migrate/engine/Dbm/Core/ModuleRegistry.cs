using Dbm.Core.Workflow;

namespace Dbm.Core;

public static class ModuleRegistry
{
    /// <summary>
    /// Every phase module. Later milestones append one line each below the marker comment:
    /// T2.7 <c>yield return new Dbm.Core.Analysis.AnalysisModule();</c>, T3.4 <c>… MappingModule(s)</c>, T4.4 <c>… SqlModule(s)</c>.
    /// </summary>
    public static IEnumerable<IPhaseModule> Create(DbmServices s)
    {
        if (s is null) yield break;   // keeps this an iterator while no module is registered
        // milestone registrations below
        yield return new Dbm.Core.Analysis.AnalysisModule();   // T2.7
        yield return new Dbm.Core.Mapping.MappingModule(s);    // T3.4
    }
}
