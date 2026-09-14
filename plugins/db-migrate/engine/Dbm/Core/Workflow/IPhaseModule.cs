using System.Text.Json.Nodes;
using Dbm.Core.State;

namespace Dbm.Core.Workflow;

public sealed record PayloadCheck(List<string> Errors, List<string> Warnings)
{
    public bool Ok => Errors.Count == 0;
    public static PayloadCheck Pass() => new([], []);
}

public sealed class ModuleContext
{
    public required DbmServices Services { get; init; }
    public required ArtifactRow Current { get; init; }
    public required IReadOnlyList<FeedbackRow> OpenFeedback { get; init; }
}

/// <summary>One per reviewable phase (Analysis, Mapping, Sql); registered in ModuleRegistry.</summary>
public interface IPhaseModule
{
    PhaseName Phase { get; }

    /// <summary>"schema-analyst" | "mapping-architect" | "sql-engineer".</summary>
    string Agent { get; }

    /// <summary>"analyze" | "automap" | "sqlgen".</summary>
    string JobKind { get; }

    /// <summary>false → the script draft goes straight to awaiting_review.</summary>
    bool NeedsAgent(JsonNode draft);

    /// <summary>The module-specific "data" section of the work packet.</summary>
    JsonNode BuildPacket(ModuleContext ctx, PacketMode mode);

    /// <summary>Runs on every agent/human patch result; errors reject the patch.</summary>
    PayloadCheck Validate(ModuleContext ctx, JsonNode payload);

    IReadOnlyList<string> ApprovalBlockers(ModuleContext ctx, JsonNode payload);

    /// <summary>One line.</summary>
    string Summarize(JsonNode payload);
}
