using System.Text;

namespace Dbm.Core.Matching;

/// <summary>Turns identifiers ("CUST_NM", "EmailAddress", "tbl_Customer") into normalised word tokens.</summary>
public static class NameNormalizer
{
    private static readonly HashSet<string> NoisePrefixes = new(StringComparer.Ordinal) { "tbl", "tb", "vw", "fld", "col" };

    /// <summary>Splits on non-alphanumerics, lower→upper, acronym→word ("HTTPServer") and letter↔digit boundaries; lower-case output.</summary>
    public static IReadOnlyList<string> Split(string identifier)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < identifier.Length; i++)
        {
            var c = identifier[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }
            if (current.Length > 0)
            {
                var p = identifier[i - 1];
                var boundary =
                    (char.IsLower(p) && char.IsUpper(c)) ||
                    (char.IsLetter(p) && char.IsDigit(c)) ||
                    (char.IsDigit(p) && char.IsLetter(c)) ||
                    (char.IsUpper(p) && char.IsUpper(c) && i + 1 < identifier.Length && char.IsLower(identifier[i + 1]));
                if (boundary) Flush();
            }
            current.Append(char.ToLowerInvariant(c));
        }
        Flush();
        return tokens;

        void Flush()
        {
            if (current.Length > 0) tokens.Add(current.ToString());
            current.Clear();
        }
    }

    /// <summary>
    /// Split → drop a noise prefix (tbl, tb, vw, fld, col) when other tokens remain → Expand (a plural that is unknown as written is
    /// looked up in its singular form) → Singular → de-duplicate → drop tokens contained in <paramref name="contextTokens"/> when
    /// other tokens remain.
    /// </summary>
    public static IReadOnlyList<string> Tokens(string identifier, Synonyms synonyms, IReadOnlyCollection<string>? contextTokens = null)
    {
        var parts = Split(identifier).ToList();
        if (parts.Count > 1 && NoisePrefixes.Contains(parts[0])) parts.RemoveAt(0);
        var tokens = new List<string>();
        foreach (var part in parts)
        {
            var lookup = synonyms.Knows(part) ? part : Singular(part);
            foreach (var expanded in synonyms.Expand(lookup))
            {
                var token = Singular(expanded);
                if (!tokens.Contains(token)) tokens.Add(token);
            }
        }
        if (contextTokens is { Count: > 0 })
        {
            var kept = tokens.Where(t => !contextTokens.Contains(t)).ToList();
            if (kept.Count > 0) return kept;
        }
        return tokens;
    }

    /// <summary>addresses→address, categories→category, statuses→status, orders→order; "ss"/"us"/"is" endings and tokens shorter than 3 chars unchanged.</summary>
    public static string Singular(string token)
    {
        if (token.Length < 3 || !char.IsLetter(token[^1])) return token;
        if (token.EndsWith("ss", StringComparison.Ordinal) || token.EndsWith("us", StringComparison.Ordinal) ||
            token.EndsWith("is", StringComparison.Ordinal)) return token;
        if (token.EndsWith("ies", StringComparison.Ordinal) && token.Length > 4) return token[..^3] + "y";
        if (token.EndsWith("es", StringComparison.Ordinal))
        {
            var stem = token[..^2];
            if (stem.EndsWith("ss", StringComparison.Ordinal) || stem.EndsWith("us", StringComparison.Ordinal) ||
                stem.EndsWith("x", StringComparison.Ordinal) || stem.EndsWith("ch", StringComparison.Ordinal) ||
                stem.EndsWith("sh", StringComparison.Ordinal)) return stem;
        }
        return token.EndsWith('s') ? token[..^1] : token;
    }
}
