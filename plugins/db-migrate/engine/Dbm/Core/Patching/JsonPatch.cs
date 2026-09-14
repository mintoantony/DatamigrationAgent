using System.Text.Json.Nodes;

namespace Dbm.Core.Patching;

/// <summary>RFC 6902 subset: add, replace, remove. Pure: the input document is never modified.</summary>
public static class JsonPatch
{
    public static JsonNode Apply(JsonNode document, IEnumerable<PatchOp> ops)
    {
        JsonNode root = document.DeepClone();
        var index = 0;
        foreach (var op in ops)
        {
            try
            {
                root = ApplyOne(root, op);
            }
            catch (PatchException ex)
            {
                throw new PatchException($"op {index} ({op.Op} {op.Path}): {ex.Message}");
            }
            index++;
        }
        return root;
    }

    /// <summary>The node at <paramref name="pointer"/> ("" = root), or null when it does not exist.</summary>
    public static JsonNode? Resolve(JsonNode document, string pointer)
    {
        JsonNode? current = document;
        foreach (var segment in Parse(pointer))
        {
            current = current switch
            {
                JsonObject o => o.TryGetPropertyValue(segment, out var child) ? child : null,
                JsonArray a => TryIndex(segment, out var i) && i < a.Count ? a[i] : null,
                _ => null,
            };
            if (current is null) return null;
        }
        return current;
    }

    private static JsonNode ApplyOne(JsonNode root, PatchOp op)
    {
        var segments = Parse(op.Path);
        var kind = op.Op.ToLowerInvariant();
        if (kind is not ("add" or "replace" or "remove")) throw new PatchException($"unknown op '{op.Op}' (use add, replace or remove)");

        if (segments.Count == 0)
        {
            if (kind == "remove") throw new PatchException("cannot remove the document root");
            return op.Value?.DeepClone() ?? throw new PatchException("the document root cannot be null");
        }

        var parent = Walk(root, segments, createMissing: kind == "add");
        var last = segments[^1];
        switch (parent)
        {
            case JsonObject obj:
                switch (kind)
                {
                    case "add":
                        obj[last] = op.Value?.DeepClone();
                        break;
                    case "replace":
                        if (!obj.ContainsKey(last)) throw new PatchException($"member '{last}' does not exist");
                        obj[last] = op.Value?.DeepClone();
                        break;
                    default:
                        if (!obj.Remove(last)) throw new PatchException($"member '{last}' does not exist");
                        break;
                }
                break;
            case JsonArray arr:
                if (kind == "add" && last == "-")
                {
                    arr.Add(op.Value?.DeepClone());
                    break;
                }
                if (!TryIndex(last, out var i)) throw new PatchException($"'{last}' is not a valid array index");
                switch (kind)
                {
                    case "add":
                        if (i > arr.Count) throw new PatchException($"index {i} is out of range (count {arr.Count})");
                        arr.Insert(i, op.Value?.DeepClone());
                        break;
                    case "replace":
                        if (i >= arr.Count) throw new PatchException($"index {i} is out of range (count {arr.Count})");
                        arr[i] = op.Value?.DeepClone();
                        break;
                    default:
                        if (i >= arr.Count) throw new PatchException($"index {i} is out of range (count {arr.Count})");
                        arr.RemoveAt(i);
                        break;
                }
                break;
            default:
                throw new PatchException("parent is not an object or array");
        }
        return root;
    }

    /// <summary>Returns the container that holds the last segment; "add" creates missing intermediate objects.</summary>
    private static JsonNode Walk(JsonNode root, IReadOnlyList<string> segments, bool createMissing)
    {
        var current = root;
        for (var s = 0; s < segments.Count - 1; s++)
        {
            var segment = segments[s];
            JsonNode? next;
            switch (current)
            {
                case JsonObject obj:
                    if (!obj.TryGetPropertyValue(segment, out next) || next is null)
                    {
                        if (!createMissing) throw new PatchException($"path segment '{segment}' does not exist");
                        next = new JsonObject();
                        obj[segment] = next;
                    }
                    break;
                case JsonArray arr:
                    if (!TryIndex(segment, out var i) || i >= arr.Count)
                        throw new PatchException($"array index '{segment}' is out of range");
                    next = arr[i] ?? throw new PatchException($"array element {i} is null");
                    break;
                default:
                    throw new PatchException($"cannot descend into a value at '{segment}'");
            }
            current = next;
        }
        return current;
    }

    private static List<string> Parse(string pointer)
    {
        if (pointer.Length == 0) return [];
        if (pointer[0] != '/') throw new PatchException($"path '{pointer}' must start with '/'");
        return pointer[1..].Split('/').Select(s => s.Replace("~1", "/").Replace("~0", "~")).ToList();
    }

    /// <summary>Digits only, no leading zeros (RFC 6901); range checks are done by the caller.</summary>
    private static bool TryIndex(string segment, out int index)
    {
        index = -1;
        if (segment.Length == 0 || segment.Length > 9 || !segment.All(char.IsAsciiDigit)) return false;
        if (segment.Length > 1 && segment[0] == '0') return false;
        index = int.Parse(segment);
        return true;
    }
}
