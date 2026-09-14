using Microsoft.Data.Sqlite;

namespace Dbm.Core.State;

public sealed class ArtifactRepo(StateDb db)
{
    private const string Columns = "id, phase, version, payload_json, author, summary, created_at";

    /// <summary>0 when the phase has no artifact yet, else max(version) + 1.</summary>
    public int NextVersion(PhaseName phase)
    {
        var max = db.Scalar<long?>("SELECT MAX(version) FROM artifact WHERE phase = $Phase", new { Phase = phase });
        return max is null ? 0 : (int)max.Value + 1;
    }

    /// <summary>author: "script" | "agent" | "human".</summary>
    public ArtifactRow Add(PhaseName phase, int version, string payloadJson, string author, string? summary)
    {
        var now = Clock.Now();
        var id = db.Scalar<long>("""
            INSERT INTO artifact (phase, version, payload_json, author, summary, created_at)
            VALUES ($Phase, $Version, $Payload, $Author, $Summary, $Now) RETURNING id
            """, new { Phase = phase, Version = version, Payload = payloadJson, Author = author, Summary = summary, Now = now });
        return new ArtifactRow(id, phase, version, payloadJson, author, summary, now);
    }

    public ArtifactRow? Get(PhaseName phase, int version) =>
        db.Query($"SELECT {Columns} FROM artifact WHERE phase = $Phase AND version = $Version", Map,
            new { Phase = phase, Version = version }).FirstOrDefault();

    public ArtifactRow? Latest(PhaseName phase) =>
        db.Query($"SELECT {Columns} FROM artifact WHERE phase = $Phase ORDER BY version DESC LIMIT 1", Map,
            new { Phase = phase }).FirstOrDefault();

    /// <summary>Ascending version, without payloads.</summary>
    public IReadOnlyList<ArtifactMeta> List(PhaseName phase) =>
        db.Query("SELECT version, author, summary, created_at FROM artifact WHERE phase = $Phase ORDER BY version",
            r => new ArtifactMeta(r.GetInt32(0), r.GetString(1), r.Str(2), r.Ts(3)), new { Phase = phase });

    private static ArtifactRow Map(SqliteDataReader r) =>
        new(r.GetInt64(0), r.Enum<PhaseName>(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.Str(5), r.Ts(6));
}
