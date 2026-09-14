using Dbm.Core.Catalog;

namespace Dbm.Core.Matching;

/// <summary>Name tokens and TF-IDF vectors for every table and column of both catalogs, fitted once per auto-map run.
/// Column tokens use the owning table's tokens as context (so CUST.CUST_ID → [id]).</summary>
public sealed class MatchContext
{
    private readonly Dictionary<string, IReadOnlyList<string>> _tokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SparseVector> _vectors = new(StringComparer.OrdinalIgnoreCase);

    public CatalogSnapshot Src { get; }
    public CatalogSnapshot Tgt { get; }

    public MatchContext(CatalogSnapshot src, CatalogSnapshot tgt, Synonyms synonyms)
    {
        Src = src;
        Tgt = tgt;
        var tableDocs = new List<(string Key, IReadOnlyList<string> Tokens)>();
        var columnDocs = new List<(string Key, IReadOnlyList<string> Tokens)>();
        foreach (var (isSource, catalog) in new[] { (true, src), (false, tgt) })
        {
            foreach (var table in catalog.Tables)
            {
                var tableTokens = NameNormalizer.Tokens(table.Name, synonyms);
                tableDocs.Add((TableKey(isSource, table), tableTokens));
                var context = new HashSet<string>(tableTokens, StringComparer.OrdinalIgnoreCase);
                foreach (var column in table.Columns)
                    columnDocs.Add((ColumnKey(isSource, table, column), NameNormalizer.Tokens(column.Name, synonyms, context)));
            }
        }
        Index(tableDocs, new NgramTfidfVectorizer());
        Index(columnDocs, new NgramTfidfVectorizer());
    }

    public IReadOnlyList<string> TableTokens(bool isSource, TableInfo table) => _tokens[TableKey(isSource, table)];

    public IReadOnlyList<string> ColumnTokens(bool isSource, TableInfo table, ColumnInfo column) => _tokens[ColumnKey(isSource, table, column)];

    public double TableNameSimilarity(TableInfo tgtTable, TableInfo srcTable) =>
        Cosine(TableKey(false, tgtTable), TableKey(true, srcTable));

    public double ColumnNameSimilarity(TableInfo tgtTable, ColumnInfo tgtColumn, TableInfo srcTable, ColumnInfo srcColumn) =>
        Cosine(ColumnKey(false, tgtTable, tgtColumn), ColumnKey(true, srcTable, srcColumn));

    private void Index(List<(string Key, IReadOnlyList<string> Tokens)> docs, IVectorizer vectorizer)
    {
        vectorizer.Fit(docs.Select(d => d.Tokens));
        foreach (var (key, tokens) in docs)
        {
            _tokens[key] = tokens;
            _vectors[key] = vectorizer.Transform(tokens);
        }
    }

    private double Cosine(string a, string b) => Math.Clamp(_vectors[a].Dot(_vectors[b]), 0.0, 1.0);

    private static string TableKey(bool isSource, TableInfo t) => $"T|{(isSource ? "src" : "tgt")}|{t.Key}";

    private static string ColumnKey(bool isSource, TableInfo t, ColumnInfo c) => $"C|{(isSource ? "src" : "tgt")}|{t.Key}|{c.Name}";
}
