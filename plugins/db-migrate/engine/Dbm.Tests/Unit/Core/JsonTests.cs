using System.Text.Json;
using System.Text.Json.Nodes;
using Dbm.Core;

namespace Dbm.Tests.Unit.Core;

public class JsonTests
{
    public enum SampleState { Pending, AwaitingReview }

    public sealed record Sample(string Name, SampleState State, int? Count, Dictionary<string, int> Tables);

    [Fact]
    public void Serialises_camel_case_snake_enums_and_omits_nulls()
    {
        var json = Json.Serialize(new Sample("x", SampleState.AwaitingReview, null, new() { ["dbo.Customer"] = 1 }));

        Assert.Equal("{\"name\":\"x\",\"state\":\"awaiting_review\",\"tables\":{\"dbo.Customer\":1}}", json);
    }

    [Fact]
    public void Deserialises_case_insensitively_and_parses_snake_enums()
    {
        var s = Json.Deserialize<Sample>("{\"NAME\":\"y\",\"state\":\"awaiting_review\",\"count\":3,\"tables\":{}}");

        Assert.Equal("y", s.Name);
        Assert.Equal(SampleState.AwaitingReview, s.State);
        Assert.Equal(3, s.Count);
    }

    [Fact]
    public void Dictionary_keys_are_never_renamed()
    {
        var json = Json.Serialize(new Dictionary<string, int> { ["dbo.CUST_NM"] = 1, ["FirstName"] = 2, ["T01"] = 3 });

        Assert.Equal("{\"dbo.CUST_NM\":1,\"FirstName\":2,\"T01\":3}", json);
        Assert.Equal(2, Json.Deserialize<Dictionary<string, int>>(json)["FirstName"]);
    }

    [Fact]
    public void Deserialising_null_throws()
    {
        Assert.Throws<JsonException>(() => Json.Deserialize<Sample>("null"));
    }

    [Fact]
    public void Keeps_quotes_and_angle_brackets_readable()
    {
        Assert.Equal("\"LOWER(s.[EMAIL]) <> 'x'\"", Json.Serialize("LOWER(s.[EMAIL]) <> 'x'"));
    }

    [Fact]
    public void Pretty_is_indented_and_nodes_round_trip()
    {
        var node = Json.ToNode(new Sample("z", SampleState.Pending, 1, new()));
        var back = Json.FromNode<Sample>(node);

        Assert.Contains("\n", Json.Serialize(new { a = 1 }, pretty: true));
        Assert.Equal("pending", node["state"]!.GetValue<string>());
        Assert.Equal("z", back.Name);
        Assert.IsType<JsonObject>(node);
    }
}
