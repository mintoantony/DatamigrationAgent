using Dbm.Core.State;

namespace Dbm.Core.Workflow;

/// <summary>Phase-name helpers shared by the engine, the API and the CLI.</summary>
public static class Phases
{
    /// <summary>Phases with a script draft + agent/human review loop.</summary>
    public static readonly PhaseName[] Reviewable = [PhaseName.Analysis, PhaseName.Mapping, PhaseName.Sql];

    /// <summary>Job kind that produces each phase's draft when no module overrides it.</summary>
    public static readonly IReadOnlyDictionary<PhaseName, string> DefaultJobKinds = new Dictionary<PhaseName, string>
    {
        [PhaseName.Discovery] = "discover",
        [PhaseName.Analysis] = "analyze",
        [PhaseName.Mapping] = "automap",
        [PhaseName.Sql] = "sqlgen",
    };

    public static string Text(this PhaseName phase) => EnumText.ToText(phase);

    public static bool TryParse(string? text, out PhaseName phase) => EnumText.TryParse(text, out phase);

    /// <summary>Throws WorkflowException("unknown phase") for bad input (the API maps it to 400/409).</summary>
    public static PhaseName Parse(string text) =>
        TryParse(text, out var p) ? p : throw new WorkflowException($"unknown phase '{text}'");

    public static PhaseName? After(PhaseName phase) =>
        phase == PhaseName.Complete ? null : (PhaseName)((int)phase + 1);
}
