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
}
