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
    public void Example_patch_passes_its_own_procedure_through_the_dry_run_path()
    {
        // Through the workflow's dry-run patch path, so MappingModule.Validate and the risk model run on the playbook's own example.
        // Step 7: no blockers and no attention remain; type-risk lines are their own class and are expected to remain.
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
        Assert.Equal(
        [
            "app.Customers.BirthDate: type risk: time part dropped",
            "app.Customers.CreatedAt: type risk: fractional seconds rounded to 0 digits",
            $"app.Customers.FirstName: type risk: {TypeCompat.UnevaluatedRisk}",
            $"app.Customers.LastName: type risk: {TypeCompat.UnevaluatedRisk}",
            $"app.Customers.PrimaryAddressId: type risk: {TypeCompat.UnevaluatedRisk}",
            "app.Orders.Comment: type risk: may truncate (source max 300)",
            "app.Orders.OrderDate: type risk: fractional seconds rounded to 0 digits",
            $"app.Orders.StatusCode: type risk: {TypeCompat.UnevaluatedRisk}",
            $"app.Products.IsActive: type risk: {TypeCompat.UnevaluatedRisk}",
        ], dry.Warnings);
        Assert.DoesNotContain(patch.Ops, o => o.Value?.ToJsonString().Contains("\"typeRisk\"") == true || o.Path.EndsWith("/typeRisk"));
        foreach (var column in new[] { "CreatedAt", "OrderDate", "BirthDate", "Comment" })
            Assert.Contains(column, patch.Summary);   // step 7: each genuine risk is explained in summary
    }

    [Fact]
    public void Playbook_teaches_the_engine_owned_risk_and_names_the_warning_classes()
    {
        var text = AgentText();
        Assert.Contains("`typeRisk` is computed by dbm. Never write it**", text);
        Assert.DoesNotContain("never remove it", text);   // contradicted "omit typeRisk on a whole-object replace"
        Assert.Contains("or say in `summary` why it is acceptable", text);
        Assert.Contains("are **expected to remain**", text);
        Assert.DoesNotContain("riskAck", text);
        Assert.DoesNotContain("copy its `typeRisk`", text);
    }
}
