using System.Text;
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Catalog;
using Dbm.Core.Mapping;
using Dbm.Core.Matching;
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Dbm.Tests.Support;
using Xunit.Abstractions;

namespace Dbm.Tests.Unit.Workflow;

/// <summary>
/// Ruling 187 (final review I-3). Work packets were written as one compact line: a mapping packet for a 60-100 table schema is
/// hundreds of KB on that line - over the Read tool's per-read ceiling and not pageable with offset/limit. They are now indented, one
/// value per line, and must still parse to the same object.
/// </summary>
public sealed class PacketLayoutTests(ITestOutputHelper output)
{
    private const int MaxLine = 2000;

    /// <summary>The C15 sample catalogs and mapping cloned into <paramref name="copies"/> schema pairs (6 target tables each), with every
    /// column made uncertain so that every table gets a full <c>detail</c> entry - the packet's worst case.</summary>
    private static (MappingPayload Mapping, CatalogSnapshot Src, CatalogSnapshot Tgt) Synthetic(int copies)
    {
        string srcJson = Json.Serialize(SampleCatalogs.Source()), tgtJson = Json.Serialize(SampleCatalogs.Target());
        string mapJson = Json.Serialize(SampleMappings.Approved());
        var srcTables = new List<TableInfo>();
        var tgtTables = new List<TableInfo>();
        var mapping = new MappingPayload();
        for (int i = 1; i <= copies; i++)
        {
            string s = $"s{i:000}", t = $"t{i:000}";
            srcTables.AddRange(Json.Deserialize<CatalogSnapshot>(srcJson.Replace("\"dbo\"", $"\"{s}\"")).Tables);
            tgtTables.AddRange(Json.Deserialize<CatalogSnapshot>(tgtJson.Replace("\"app\"", $"\"{t}\"")).Tables);
            var m = Json.Deserialize<MappingPayload>(mapJson.Replace("dbo.", s + ".").Replace("app.", t + "."));
            foreach (var (key, table) in m.Tables)
            {
                foreach (var column in table.Columns.Values)
                {
                    column.Method = MapMethod.Fuzzy;
                    column.Confidence = 0.6;
                }
                mapping.Tables[key] = table;
            }
            foreach (var (key, drop) in m.Drops) mapping.Drops[key] = drop;
        }
        var src = SampleCatalogs.Source() with { Tables = srcTables };
        var tgt = SampleCatalogs.Target() with { Tables = tgtTables };
        return (mapping, src, tgt);
    }

    [Fact]
    public void A_120_table_mapping_packet_is_pageable_and_parses_to_the_same_object()
    {
        var (mapping, src, tgt) = Synthetic(20);
        Assert.Equal(120, tgt.Tables.Count);
        var data = MappingPacket.Draft(mapping, src, tgt, new MatchOptions());
        var envelope = new JsonObject
        {
            ["phase"] = "mapping", ["mode"] = "draft", ["baseVersion"] = 0, ["patchPath"] = "C:/w/mapping-v0-draft.patch.json",
            ["agent"] = "mapping-architect", ["feedback"] = new JsonArray(), ["rules"] = WorkflowEngine.PatchRules, ["data"] = data,
        };

        string text = WorkflowEngine.PacketText(envelope);

        var lines = text.Split('\n');
        int longest = lines.Max(l => l.Length);
        int bytes = Encoding.UTF8.GetByteCount(text);
        output.WriteLine($"120 tables: {data["detail"]!.AsArray().Count} detailed, {bytes:N0} bytes, {lines.Length:N0} lines, longest line {longest} chars; "
                         + $"compact would be {Encoding.UTF8.GetByteCount(envelope.ToJsonString(Json.Options)):N0} bytes on one line");
        Assert.Equal(120, data["detail"]!.AsArray().Count);
        Assert.True(lines.Length > 1000, $"the packet is {lines.Length} line(s): the Read tool cannot page it");
        Assert.True(longest <= MaxLine, $"a {longest}-character line: one Read page cannot hold it");
        Assert.True(JsonNode.DeepEquals(envelope, JsonNode.Parse(text)), "the indented packet does not parse to the same object");
        Assert.DoesNotContain('\r', text);
    }

    [Fact]
    public void The_packet_dbm_next_writes_is_indented_and_says_the_same_thing()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);
        FakeServices.DriveToAnalysisDraft(s);

        var next = s.Workflow.Next();

        string text = File.ReadAllText(next.Packet!);
        Assert.True(text.Split('\n').Length > 10, "dbm next wrote the packet on one line: " + text[..Math.Min(120, text.Length)]);
        Assert.Equal("analysis", (string?)JsonNode.Parse(text)!["phase"]);
    }
}
