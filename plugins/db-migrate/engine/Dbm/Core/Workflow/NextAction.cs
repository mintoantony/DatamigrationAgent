namespace Dbm.Core.Workflow;

/// <summary>
/// What the orchestrator does next. Action: "agent" | "await" | "stop".
/// await reasons: setup | job | review | execute | transfer | transfer_paused | paused.
/// stop reasons: complete | job_failed | transfer_failed | transfer_cancelled.
/// </summary>
public sealed record NextAction(string Action, string? Reason = null, string? Agent = null, string? Phase = null,
    string? Mode = null, string? Packet = null, string? PatchPath = null, string? Summary = null, string? Url = null);

public enum PacketMode { Draft, Rework }
