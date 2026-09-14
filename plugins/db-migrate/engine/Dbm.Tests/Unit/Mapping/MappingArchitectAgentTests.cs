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

        Assert.Equal(("mapping", 0, 24), (patch.Phase, patch.BaseVersion, patch.Ops.Count));
        Assert.Empty(MappingValidator.Errors(result, SampleCatalogs.Source(), SampleCatalogs.Target()));
        Assert.Empty(MappingValidator.Blockers(result, SampleCatalogs.Source(), SampleCatalogs.Target()));
    }

    [Fact]
    public void Example_patch_never_silently_deletes_a_type_risk()
    {
        // B8: a replaced column keeps the draft's typeRisk unless its rationale says the new expression handles the hazard.
        var text = AgentText();
        Assert.Contains("copy its `typeRisk` across unchanged", text);
        var marker = text.IndexOf("<!-- example-patch -->", StringComparison.Ordinal);
        var start = text.IndexOf("```json", marker, StringComparison.Ordinal) + "```json".Length;
        var patch = Patch.Parse(text[start..text.IndexOf("```", start, StringComparison.Ordinal)]);
        var draft = AutoMapper.Map(SampleCatalogs.Source(), SampleCatalogs.Target(), Synonyms.Default(), new MatchOptions());

        using var project = TempProject.Create();
        var services = project.Services.WithSampleCatalogs();
        services.ApproveBefore(Dbm.Core.State.PhaseName.Mapping);
        services.AddMapping(draft, Dbm.Core.State.PhaseStatus.Drafting);
        var applied = services.Workflow.ApplyPatch(patch);   // through Validate, so the recompute runs too
        Assert.True(applied.Ok, string.Join("\n", applied.Errors));
        var result = Json.Deserialize<MappingPayload>(services.Artifacts.Get(Dbm.Core.State.PhaseName.Mapping, applied.Version!.Value)!.PayloadJson);

        var checkedColumns = 0;
        foreach (var op in patch.Ops.Where(o => o.Path.Split('/') is { Length: 5 } p && p[3] == "columns"))
        {
            var (table, column) = (op.Path.Split('/')[2], op.Path.Split('/')[4]);
            var before = draft.Tables[table].Columns[column];
            if (!MappingValidator.HasTypeRisk(before)) continue;
            checkedColumns++;
            var after = result.Tables[table].Columns[column];
            if (after.TypeRisk != before.TypeRisk)
                Assert.Contains("risk", after.Rationale ?? "", StringComparison.OrdinalIgnoreCase);
        }
        Assert.True(checkedColumns >= 3, $"only {checkedColumns} risky columns replaced by the example");
        Assert.Equal("fractional seconds rounded to 0 digits", result.Tables["app.Customers"].Columns["CreatedAt"].TypeRisk);
    }
}
