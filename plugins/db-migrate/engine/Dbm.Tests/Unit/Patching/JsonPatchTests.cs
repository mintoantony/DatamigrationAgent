using System.Text.Json.Nodes;
using Dbm.Core.Patching;

namespace Dbm.Tests.Unit.Patching;

public class JsonPatchTests
{
    private static JsonNode Doc() => JsonNode.Parse("""
        {"tables":{"dbo.Customer":{"columns":{"Email":{"expr":"s.EMAIL"}}},"a/b":{"x~y":1}},"list":[1,2,3],"n":null}
        """)!;

    private static JsonNode Apply(JsonNode doc, params PatchOp[] ops) => JsonPatch.Apply(doc, ops);

    [Fact]
    public void Replace_changes_a_member_without_touching_the_input()
    {
        var doc = Doc();

        var result = Apply(doc, new PatchOp("replace", "/tables/dbo.Customer/columns/Email/expr", JsonValue.Create("LOWER(s.EMAIL)")));

        Assert.Equal("LOWER(s.EMAIL)", result["tables"]!["dbo.Customer"]!["columns"]!["Email"]!["expr"]!.GetValue<string>());
        Assert.Equal("s.EMAIL", doc["tables"]!["dbo.Customer"]!["columns"]!["Email"]!["expr"]!.GetValue<string>());
    }

    [Fact]
    public void Add_creates_missing_intermediate_objects_and_overwrites_existing_members()
    {
        var result = Apply(Doc(),
            new PatchOp("add", "/tables/dbo.Orders/columns/Id/expr", JsonValue.Create("s.ID")),
            new PatchOp("add", "/n", JsonValue.Create(5)));

        Assert.Equal("s.ID", result["tables"]!["dbo.Orders"]!["columns"]!["Id"]!["expr"]!.GetValue<string>());
        Assert.Equal(5, result["n"]!.GetValue<int>());
    }

    [Fact]
    public void Add_to_arrays_by_index_and_dash()
    {
        var result = Apply(Doc(),
            new PatchOp("add", "/list/0", JsonValue.Create(0)),
            new PatchOp("add", "/list/-", JsonValue.Create(9)),
            new PatchOp("add", "/list/5", JsonValue.Create(10)));

        Assert.Equal("[0,1,2,3,9,10]", result["list"]!.ToJsonString());
    }

    [Fact]
    public void Remove_members_and_array_elements()
    {
        var result = Apply(Doc(),
            new PatchOp("remove", "/list/1"),
            new PatchOp("remove", "/tables/dbo.Customer"));

        Assert.Equal("[1,3]", result["list"]!.ToJsonString());
        Assert.False(result["tables"]!.AsObject().ContainsKey("dbo.Customer"));
    }

    [Fact]
    public void Pointer_escapes_are_decoded()
    {
        var result = Apply(Doc(), new PatchOp("replace", "/tables/a~1b/x~0y", JsonValue.Create(2)));

        Assert.Equal(2, result["tables"]!["a/b"]!["x~y"]!.GetValue<int>());
    }

    [Fact]
    public void Null_value_sets_json_null_and_root_can_be_replaced()
    {
        var withNull = Apply(Doc(), new PatchOp("replace", "/list", null));
        var root = Apply(Doc(), new PatchOp("replace", "", new JsonObject { ["fresh"] = true }));

        Assert.Null(withNull["list"]);
        Assert.True(withNull.AsObject().ContainsKey("list"));
        Assert.Equal("{\"fresh\":true}", root.ToJsonString());
    }

    [Theory]
    [InlineData("replace", "/tables/missing", "member 'missing' does not exist")]
    [InlineData("remove", "/list/7", "index 7 is out of range")]
    [InlineData("add", "/list/9", "index 9 is out of range")]
    [InlineData("replace", "/list/01", "'01' is not a valid array index")]
    [InlineData("replace", "/nothing/here", "path segment 'nothing' does not exist")]
    [InlineData("move", "/list", "unknown op 'move'")]
    [InlineData("add", "list", "must start with '/'")]
    [InlineData("remove", "", "cannot remove the document root")]
    public void Errors_name_the_op_index_path_and_reason(string op, string path, string reason)
    {
        var ex = Assert.Throws<PatchException>(() => Apply(Doc(),
            new PatchOp("add", "/ok", JsonValue.Create(1)),
            new PatchOp(op, path, JsonValue.Create(1))));

        Assert.StartsWith($"op 1 ({op} {path}): ", ex.Message);
        Assert.Contains(reason, ex.Message);
    }

    [Fact]
    public void Resolve_returns_the_node_or_null()
    {
        var doc = Doc();

        Assert.Equal("s.EMAIL", JsonPatch.Resolve(doc, "/tables/dbo.Customer/columns/Email/expr")!.GetValue<string>());
        Assert.Equal(3, JsonPatch.Resolve(doc, "/list/2")!.GetValue<int>());
        Assert.Same(doc, JsonPatch.Resolve(doc, ""));
        Assert.Null(JsonPatch.Resolve(doc, "/list/9"));
        Assert.Null(JsonPatch.Resolve(doc, "/tables/none/x"));
    }
}
