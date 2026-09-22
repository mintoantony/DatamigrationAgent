using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Jobs;
using Dbm.Core.Patching;
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Workflow;

/// <summary>
/// The whole M1 orchestration loop at engine level, exactly as SKILL.md drives it:
/// init → connections → jobs → agent draft → review → feedback → rework → approve … → await execute.
/// </summary>
public class M1LoopTests
{
    private static async Task RunJobs(DbmServices s) => await new JobRunner(s).RunPendingAsync(CancellationToken.None);

    /// <summary>What a subagent does: read the packet, write a patch answering every feedback item.</summary>
    private static Patch AgentPatch(NextAction action, string summary)
    {
        var packet = JsonNode.Parse(File.ReadAllText(action.Packet!))!;
        var responses = packet["feedback"]!.AsArray()
            .Select(f => new FeedbackResponse(f!["id"]!.GetValue<long>(), "addressed", $"Handled: {f["text"]!.GetValue<string>()}"))
            .ToList();
        var patch = new Patch(packet["phase"]!.GetValue<string>(), packet["baseVersion"]!.GetValue<int>(),
            [new PatchOp("replace", "/summary", JsonValue.Create(summary))], responses, summary);
        File.WriteAllText(action.PatchPath!, Json.Serialize(patch));
        return Patch.Parse(File.ReadAllText(action.PatchPath!));
    }

    [Fact]
    public async Task Full_review_loop_through_all_three_phases_until_execution_awaits()
    {
        using var tw = new TestWorkspace();
        tw.Ws.EnsureCreated();
        using var s = FakeServices.Open(tw.Ws);                       // `dbm init`

        Assert.Equal("setup", s.Workflow.Next().Reason);

        FakeServices.SaveConnections(s);                              // UI: both connections saved
        Assert.Equal(new NextAction("await", Reason: "job", Phase: "discovery"), s.Workflow.Next());
        await RunJobs(s);                                             // server jobs: discover + analyze

        foreach (var (phase, agent) in new[] { (PhaseName.Analysis, "schema-analyst"), (PhaseName.Mapping, "mapping-architect"), (PhaseName.Sql, "sql-engineer") })
        {
            var name = phase.Text();

            var draft = s.Workflow.Next();
            Assert.Equal(("agent", agent, name, "draft"), (draft.Action, draft.Agent, draft.Phase, draft.Mode));
            var v1 = s.Workflow.ApplyPatch(AgentPatch(draft, $"{name} v1"));
            Assert.True(v1.Ok, string.Join("; ", v1.Errors));
            Assert.Equal(("await", "review", name), (s.Workflow.Next().Action, s.Workflow.Next().Reason, s.Workflow.Next().Phase));

            var item = s.Feedback.Add(phase, v1.Version!.Value, $"narrative", $"Tighten the {name} summary");
            s.Workflow.RequestChanges(phase);
            var rework = s.Workflow.Next();
            Assert.Equal(("agent", "rework"), (rework.Action, rework.Mode));

            var v2 = s.Workflow.ApplyPatch(AgentPatch(rework, $"{name} v2"));
            Assert.True(v2.Ok, string.Join("; ", v2.Errors));
            Assert.Equal(FeedbackStatus.Addressed, s.Feedback.Get(item.Id)!.Status);
            Assert.Equal("review", s.Workflow.Next().Reason);

            s.ApproveCurrent(phase);
            Assert.Equal(2, s.Phases.Get(phase).ApprovedVersion);
            await RunJobs(s);                                         // automap / sqlgen after approval
        }

        Assert.Equal(new NextAction("await", Reason: "execute", Phase: "ready", Url: ""), s.Workflow.Next());
        Assert.All(new[] { PhaseName.Setup, PhaseName.Discovery, PhaseName.Analysis, PhaseName.Mapping, PhaseName.Sql },
            p => Assert.Equal(PhaseStatus.Approved, s.Phases.Get(p).Status));
    }
}
