using Dbm.Core;
using Dbm.Core.Mapping;
using Dbm.Core.Matching;
using Dbm.Core.Patching;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Mapping;

public class MappingArchitectAgentTests
{
    private static string AgentText()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "plugins", "db-migrate", "agents", "mapping-architect.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "plugins", "db-migrate", "agents", "mapping-architect.md")).Replace("\r\n", "\n");
    }

    [Fact]
    public void Agent_file_has_frontmatter_and_the_playbook_rules()
    {
        var text = AgentText();
        Assert.StartsWith("---\nname: mapping-architect\n", text);
        Assert.Contains("\ntools: Bash, Read, Write\n", text);
        Assert.Contains("\nmodel: inherit\n", text);
        foreach (var phrase in new[] { "dbm apply <patchPath> --dry-run", "patchPath", "sourceColumns", "\"method\": \"agent\"", "dbm show", "dbm search", "state.db", "feedbackId" })
            Assert.Contains(phrase, text);
    }

    [Fact]
    public void Complete_example_patch_applies_to_the_sample_draft_and_clears_the_blockers()
    {
        var text = AgentText();
        var marker = text.IndexOf("<!-- example-patch -->", StringComparison.Ordinal);
        var start = text.IndexOf("```json", marker, StringComparison.Ordinal) + "```json".Length;
        var patch = Patch.Parse(text[start..text.IndexOf("```", start, StringComparison.Ordinal)]);
        var draft = AutoMapper.Map(SampleCatalogs.Source(), SampleCatalogs.Target(), Synonyms.Default(), new MatchOptions());

        var result = Json.FromNode<MappingPayload>(JsonPatch.Apply(Json.ToNode(draft), patch.Ops));

        Assert.Equal(("mapping", 0), (patch.Phase, patch.BaseVersion));
        Assert.Empty(MappingValidator.Errors(result, SampleCatalogs.Source(), SampleCatalogs.Target()));
        Assert.Empty(MappingValidator.Blockers(result, SampleCatalogs.Source(), SampleCatalogs.Target()));
    }

    [Fact]
    public void Example_patch_passes_its_own_procedure_through_the_dry_run_path()
    {
        // E5: through the workflow's dry-run patch path, so MappingModule.Validate and the Risk model run on the playbook's own
        // example. Step 7 requires every blocker and attention item cleared; acknowledgement output is informational warnings.
        var text = AgentText();
        var marker = text.IndexOf("<!-- example-patch -->", StringComparison.Ordinal);
        var start = text.IndexOf("```json", marker, StringComparison.Ordinal) + "```json".Length;
        var patch = Patch.Parse(text[start..text.IndexOf("```", start, StringComparison.Ordinal)]);
        var draft = AutoMapper.Map(SampleCatalogs.Source(), SampleCatalogs.Target(), Synonyms.Default(), new MatchOptions());
        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        services.ApproveBefore(Dbm.Core.State.PhaseName.Mapping);
        services.AddMapping(draft, Dbm.Core.State.PhaseStatus.Drafting);

        var dry = services.Workflow.ApplyPatch(patch, dryRun: true);

        Assert.True(dry.Ok, string.Join(Environment.NewLine, dry.Errors));
        var open = dry.Warnings.Where(w => !w.Contains(": risk acknowledged: ", StringComparison.Ordinal)).ToList();
        Assert.True(open.Count == 0, "still open after the example patch: " + string.Join(Environment.NewLine, open));
        Assert.Contains(patch.Ops, o => o.Value?.ToJsonString().Contains("\"riskAck\"") == true);
        Assert.DoesNotContain(patch.Ops, o => o.Value?.ToJsonString().Contains("\"typeRisk\"") == true || o.Path.EndsWith("/typeRisk"));
    }

    [Fact]
    public void Playbook_teaches_one_risk_channel()
    {
        var text = AgentText();
        Assert.Contains("`typeRisk` is computed by dbm. Never write it and never remove it", text);
        Assert.Contains("A type risk is cleared only by `riskAck`", text);
        Assert.DoesNotContain("copy its `typeRisk`", text);
        Assert.DoesNotContain("keeping the risk with a rationale", text);
    }
}
