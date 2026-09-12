using System.Text.Json;

namespace Dbm.Core;

/// <summary>Enum ⇄ snake_case_lower text, identical to the JSON enum converter ("AwaitingReview" ⇄ "awaiting_review").</summary>
public static class EnumText
{
    public static string ToText<T>(T value) where T : struct, Enum =>
        Cache<T>.ToText.TryGetValue(value, out var text) ? text : Convert(value.ToString());

    /// <summary>Non-generic form used by parameter binding.</summary>
    public static string ToText(Enum value) => Convert(value.ToString());

    public static T Parse<T>(string text) where T : struct, Enum =>
        TryParse<T>(text, out var value)
            ? value
            : throw new ArgumentException(
                $"'{text}' is not a valid {typeof(T).Name}; expected one of: {string.Join(", ", Cache<T>.ToText.Values)}");

    public static bool TryParse<T>(string? text, out T value) where T : struct, Enum
    {
        value = default;
        return text is not null && Cache<T>.FromText.TryGetValue(text, out value);
    }

    private static string Convert(string name) => JsonNamingPolicy.SnakeCaseLower.ConvertName(name);

    private static class Cache<T> where T : struct, Enum
    {
        public static readonly Dictionary<T, string> ToText = Enum.GetValues<T>().ToDictionary(v => v, v => Convert(v.ToString()));

        public static readonly Dictionary<string, T> FromText = BuildFromText();

        private static Dictionary<string, T> BuildFromText()
        {
            var map = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in Enum.GetValues<T>())
            {
                map[Convert(v.ToString())] = v;
                map.TryAdd(v.ToString(), v);
            }
            return map;
        }
    }
}
