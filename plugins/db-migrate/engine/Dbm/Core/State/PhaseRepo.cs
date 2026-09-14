using Microsoft.Data.Sqlite;

namespace Dbm.Core.State;

public sealed class PhaseRepo(StateDb db)
{
    private const string Columns = "name, ordinal, status, current_version, approved_version, approved_fingerprint, updated_at";

    public PhaseRow Get(PhaseName phase) =>
        db.Query($"SELECT {Columns} FROM phase WHERE name = $Name", Map, new { Name = phase }).FirstOrDefault()
        ?? throw new KeyNotFoundException($"phase {EnumText.ToText(phase)} not found (project not initialised?)");

    public IReadOnlyList<PhaseRow> All() => db.Query($"SELECT {Columns} FROM phase ORDER BY ordinal", Map);

    public void SetStatus(PhaseName phase, PhaseStatus status) =>
        db.Execute("UPDATE phase SET status = $Status, updated_at = $Now WHERE name = $Name",
            new { Name = phase, Status = status, Now = Clock.NowText() });

    public void SetCurrentVersion(PhaseName phase, int? version) =>
        db.Execute("UPDATE phase SET current_version = $Version, updated_at = $Now WHERE name = $Name",
            new { Name = phase, Version = version, Now = Clock.NowText() });

    /// <summary>Also sets status = approved.</summary>
    public void SetApproved(PhaseName phase, int version, string? fingerprint) =>
        db.Execute("""
            UPDATE phase SET status = 'approved', approved_version = $Version, approved_fingerprint = $Fingerprint,
                             updated_at = $Now
            WHERE name = $Name
            """, new { Name = phase, Version = version, Fingerprint = fingerprint, Now = Clock.NowText() });

    public void ClearApproval(PhaseName phase) =>
        db.Execute("UPDATE phase SET approved_version = NULL, approved_fingerprint = NULL, updated_at = $Now WHERE name = $Name",
            new { Name = phase, Now = Clock.NowText() });

    private static PhaseRow Map(SqliteDataReader r) =>
        new(r.Enum<PhaseName>(0), r.GetInt32(1), r.Enum<PhaseStatus>(2), r.IntN(3), r.IntN(4), r.Str(5), r.Ts(6));
}
