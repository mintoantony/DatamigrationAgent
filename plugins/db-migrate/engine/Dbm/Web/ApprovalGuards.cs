using Dbm.Core.State;

namespace Dbm.Web;

/// <summary>
/// Run by POST /api/phase/{phase}/approve before WorkflowEngine.Approve; the first non-null message aborts with
/// 409 {"error":"guard","message":…}. Empty in M1; T2.5 appends the drift guard.
/// </summary>
public static class ApprovalGuards
{
    public static readonly List<Func<WebState, PhaseName, CancellationToken, Task<string?>>> All = new();
}
