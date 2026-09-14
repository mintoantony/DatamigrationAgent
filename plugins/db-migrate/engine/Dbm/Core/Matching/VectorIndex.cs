using Dbm.Core.Catalog;
using Dbm.Core.State;

namespace Dbm.Core.Matching;

public sealed record SearchHit(Side Side, string Kind, string Key, double Score, string Text);

/// <summary>
/// In-memory cosine search over table and column documents of both catalogs.
/// Table document = table-name tokens. Column document = column-name tokens (owning table's tokens as context, so "CUST_NM" in CUST
/// becomes [name]) + value-class tokens (email, phone, url, guid, postal code, country code) + up to 8 description tokens.
/// A column's stored vector is blended with its table's vector (weight 0.5) so "customer email" prefers Customers.Email.
/// Row text = "&lt;key&gt; — &lt;tokens&gt;"; FromRows re-fits the vectorizer from those tokens and reuses the stored vectors.
/// </summary>
public sealed class VectorIndex
{
    public const string Separator = " — ";
    public const double ContextWeight = 0.5;
    private const int MaxDescriptionTokens = 8;

    private static readonly Dictionary<string, string[]> ClassTokens = new(StringComparer.Ordinal)
    {
        ["email"] = new[] { "email" },
        ["phone"] = new[] { "phone" },
        ["url"] = new[] { "url" },
        ["guid"] = new[] { "guid" },
        ["postal_code"] = new[] { "postal", "code" },
        ["country_code"] = new[] { "country", "code" },
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "from", "this", "that", "which", "are", "was", "not", "used", "value", "column", "table",
    };

    private readonly IReadOnlyList<VectorRow> _rows;
    private readonly NgramTfidfVectorizer _vectorizer;
    private readonly Synonyms _synonyms;

    private VectorIndex(IReadOnlyList<VectorRow> rows, NgramTfidfVectorizer vectorizer, Synonyms synonyms)
    {
        _rows = rows;
        _vectorizer = vectorizer;
        _synonyms = synonyms;
    }

    public static (List<VectorRow> Rows, VectorIndex Index) Build(CatalogSnapshot src, CatalogSnapshot tgt, Synonyms synonyms)
    {
        var documents = new List<(Side Side, string Kind, string Key, IReadOnlyList<string> Tokens)>();
        foreach (var (side, snapshot) in new[] { (Side.Src, src), (Side.Tgt, tgt) })
        {
            foreach (var table in snapshot.Tables)
            {
                var tableTokens = NameNormalizer.Tokens(table.Name, synonyms);
                documents.Add((side, "table", table.Key, tableTokens));
                foreach (var column in table.Columns)
                    documents.Add((side, "column", $"{table.Key}.{column.Name}", ColumnTokens(column, tableTokens, synonyms)));
            }
        }
        var vectorizer = new NgramTfidfVectorizer();
        vectorizer.Fit(documents.Select(d => d.Tokens));
        var rows = new List<VectorRow>(documents.Count);
        SparseVector tableVector = new();
        foreach (var d in documents)
        {
            var vector = vectorizer.Transform(d.Tokens);
            if (d.Kind == "table") tableVector = vector;   // documents are ordered table, its columns, next table, …
            else vector = Blend(vector, tableVector, ContextWeight);
            rows.Add(new VectorRow(d.Side, d.Kind, d.Key, TextOf(d.Key, d.Tokens), vector));
        }
        return (rows, new VectorIndex(rows, vectorizer, synonyms));
    }

    /// <summary>normalize(a + weight·b): a column vector carries its owning table's vector as context.</summary>
    public static SparseVector Blend(SparseVector a, SparseVector b, double weight)
    {
        var sum = new Dictionary<string, double>(a.W, StringComparer.Ordinal);
        foreach (var (key, value) in b.W) sum[key] = sum.GetValueOrDefault(key) + weight * value;
        return SparseVector.Normalized(sum);
    }

    public static VectorIndex FromRows(IReadOnlyList<VectorRow> rows, Synonyms synonyms)
    {
        var vectorizer = new NgramTfidfVectorizer();
        vectorizer.Fit(rows.Select(r => TokensOf(r.Text)));
        return new VectorIndex(rows.ToList(), vectorizer, synonyms);
    }

    public IReadOnlyList<SearchHit> Search(string query, Side? side = null, string? kind = null, int k = 8)
    {
        var q = _vectorizer.Transform(NameNormalizer.Tokens(query, _synonyms));
        if (q.IsEmpty) return Array.Empty<SearchHit>();
        return _rows
            .Where(r => (side is null || r.Side == side) && (kind is null || string.Equals(r.Kind, kind, StringComparison.OrdinalIgnoreCase)))
            .Select(r => new SearchHit(r.Side, r.Kind, r.Key, q.Dot(r.Vec), r.Text))
            .Where(h => h.Score > 0)
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Side)
            .ThenBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, k))
            .ToList();
    }

    public static string TextOf(string key, IReadOnlyList<string> tokens) => key + Separator + string.Join(' ', tokens);

    public static IReadOnlyList<string> TokensOf(string text)
    {
        var i = text.LastIndexOf(Separator, StringComparison.Ordinal);
        return i < 0 ? Array.Empty<string>() : text[(i + Separator.Length)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static IReadOnlyList<string> ColumnTokens(ColumnInfo column, IReadOnlyList<string> tableTokens, Synonyms synonyms)
    {
        var tokens = NameNormalizer.Tokens(column.Name, synonyms, tableTokens.ToList()).ToList();
        if (column.Profile?.SemanticClass is { } semanticClass && ClassTokens.TryGetValue(semanticClass, out var extra))
            foreach (var t in extra) if (!tokens.Contains(t)) tokens.Add(t);
        if (!string.IsNullOrWhiteSpace(column.Description))
        {
            var added = 0;
            foreach (var t in NameNormalizer.Tokens(column.Description, synonyms))
            {
                if (added == MaxDescriptionTokens) break;
                if (t.Length < 3 || StopWords.Contains(t) || tokens.Contains(t)) continue;
                tokens.Add(t);
                added++;
            }
        }
        return tokens;
    }
}
