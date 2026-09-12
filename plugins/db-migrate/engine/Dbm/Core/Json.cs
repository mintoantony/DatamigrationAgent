using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Dbm.Core;

/// <summary>The one JSON configuration used everywhere (CLI output, SQLite payloads, HTTP API, packets).</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = Create(indented: false);
    public static readonly JsonSerializerOptions Pretty = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,   // no DictionaryKeyPolicy: keys like "dbo.CUST_NM" round-trip exactly
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            WriteIndented = indented,
            // Keep quotes, <, >, & and non-ASCII readable for agents. Anything that embeds JSON inside HTML
            // must additionally replace "</" with "<\/".
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }

    public static string Serialize<T>(T value, bool pretty = false) =>
        JsonSerializer.Serialize(value, pretty ? Pretty : Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"JSON did not contain a {typeof(T).Name}");

    public static JsonNode ToNode<T>(T value) =>
        JsonSerializer.SerializeToNode(value, Options) ?? throw new JsonException("value serialised to null");

    public static T FromNode<T>(JsonNode node) =>
        node.Deserialize<T>(Options) ?? throw new JsonException($"JSON did not contain a {typeof(T).Name}");
}
