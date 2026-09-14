using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dbm.Core.Patching;

/// <summary>op: "add" | "replace" | "remove"; Path is a JSON pointer into the artifact payload.</summary>
public sealed record PatchOp(string Op, string Path, JsonNode? Value = null);

/// <summary>status: "addressed" | "declined".</summary>
public sealed record FeedbackResponse(long FeedbackId, string Status, string Note);

public sealed record Patch(string Phase, int BaseVersion, List<PatchOp> Ops, List<FeedbackResponse> Responses, string? Summary = null)
{
    /// <summary>Parses a patch file; missing lists become empty; structural problems throw PatchException.</summary>
    public static Patch Parse(string json)
    {
        Patch? raw;
        try
        {
            raw = JsonSerializer.Deserialize<Patch>(json, Json.Options);
        }
        catch (JsonException ex)
        {
            throw new PatchException($"patch is not valid JSON: {ex.Message}");
        }
        if (raw is null) throw new PatchException("patch is empty");
        if (string.IsNullOrWhiteSpace(raw.Phase)) throw new PatchException("patch.phase is required");

        var ops = raw.Ops ?? [];
        for (var i = 0; i < ops.Count; i++)
        {
            if (ops[i] is null || string.IsNullOrWhiteSpace(ops[i].Op) || ops[i].Path is null)
                throw new PatchException($"op {i}: 'op' and 'path' are required");
        }
        var responses = raw.Responses ?? [];
        for (var i = 0; i < responses.Count; i++)
        {
            if (responses[i] is null) throw new PatchException($"response {i} is null");
        }
        return raw with { Ops = ops, Responses = responses.Select(r => r with { Note = r.Note ?? "" }).ToList() };
    }
}

public sealed class PatchException(string message) : Exception(message);
