using Dbm.Core.Catalog;
using Dbm.Core.Matching;

namespace Dbm.Core.State;

public sealed record VectorRow(Side Side, string Kind, string Key, string Text, SparseVector Vec);   // Kind "table"|"column"

/// <summary>One JSON catalog snapshot (profiles embedded) + fingerprint per side, and the search vectors.</summary>
public sealed class CatalogRepo(StateDb db)
{
    public void Save(Side side, CatalogSnapshot snapshot, string fingerprint) =>
        db.Execute("""
            INSERT INTO catalog (side, snapshot_json, fingerprint, extracted_at) VALUES ($side, $json, $fingerprint, $extractedAt)
            ON CONFLICT(side) DO UPDATE SET snapshot_json = excluded.snapshot_json, fingerprint = excluded.fingerprint,
              extracted_at = excluded.extracted_at
            """,
            new { side = EnumText.ToText(side), json = Json.Serialize(snapshot), fingerprint, extractedAt = snapshot.ExtractedAt.ToString("O") });

    public CatalogSnapshot? Get(Side side)
    {
        var json = db.Scalar<string>("SELECT snapshot_json FROM catalog WHERE side = $side", new { side = EnumText.ToText(side) });
        return json is null ? null : Json.Deserialize<CatalogSnapshot>(json);
    }

    public string? Fingerprint(Side side) =>
        db.Scalar<string>("SELECT fingerprint FROM catalog WHERE side = $side", new { side = EnumText.ToText(side) });

    /// <summary>Replaces all vector rows of <paramref name="side"/>; every row must belong to that side.</summary>
    public void SaveVectors(Side side, IEnumerable<VectorRow> rows)
    {
        var sideText = EnumText.ToText(side);
        db.InTransaction(() =>
        {
            db.Execute("DELETE FROM vector WHERE side = $side", new { side = sideText });
            foreach (var row in rows)
            {
                if (row.Side != side)
                    throw new ArgumentException($"Vector row '{row.Key}' belongs to side {EnumText.ToText(row.Side)}, not {sideText}.");
                db.Execute("INSERT INTO vector (side, kind, key, text, vec_json) VALUES ($side, $kind, $key, $text, $vec)",
                    new { side = sideText, kind = row.Kind, key = row.Key, text = row.Text, vec = Json.Serialize(row.Vec) });
            }
        });
    }

    public IReadOnlyList<VectorRow> Vectors(Side? side = null, string? kind = null) =>
        db.Query(
            "SELECT side, kind, key, text, vec_json FROM vector WHERE ($side IS NULL OR side = $side) AND ($kind IS NULL OR kind = $kind) ORDER BY side, kind, key",
            r => new VectorRow(EnumText.Parse<Side>(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetString(3),
                Json.Deserialize<SparseVector>(r.GetString(4))),
            new { side = side is null ? null : EnumText.ToText(side.Value), kind });
}
