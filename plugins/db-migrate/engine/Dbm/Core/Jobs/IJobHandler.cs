using System.Text.Json.Nodes;
using Dbm.Core.State;

namespace Dbm.Core.Jobs;

/// <summary>DraftPayload: the phase's script draft (null for jobs that produce no artifact, e.g. "discover").</summary>
public sealed record JobResult(JsonNode? DraftPayload, string? Summary);

public sealed class JobContext
{
    public required DbmServices Services { get; init; }
    public required JobRow Job { get; init; }

    /// <summary>Publishes a "log" event (shown live in the UI).</summary>
    public required Action<string> Log { get; init; }
}

/// <summary>Deterministic server-side work; registered in JobRegistry.</summary>
public interface IJobHandler
{
    string Kind { get; }
    Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct);
}
