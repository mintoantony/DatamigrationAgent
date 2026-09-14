using Microsoft.Data.Sqlite;

namespace Dbm.Core.State;

public sealed class FeedbackRepo(StateDb db)
{
    private const string Columns = "id, phase, version, anchor, text, status, response, responded_version, created_at";

    /// <summary>New items start as drafts; RequestChanges submits them.</summary>
    public FeedbackRow Add(PhaseName phase, int version, string? anchor, string text)
    {
        var now = Clock.Now();
        var id = db.Scalar<long>("""
            INSERT INTO feedback (phase, version, anchor, text, status, created_at)
            VALUES ($Phase, $Version, $Anchor, $Text, 'draft', $Now) RETURNING id
            """, new { Phase = phase, Version = version, Anchor = anchor, Text = text, Now = now });
        return new FeedbackRow(id, phase, version, anchor, text, FeedbackStatus.Draft, null, null, now);
    }

    public FeedbackRow? Get(long id) =>
        db.Query($"SELECT {Columns} FROM feedback WHERE id = $Id", Map, new { Id = id }).FirstOrDefault();

    /// <summary>draft → open for the phase; returns how many were submitted.</summary>
    public int SubmitDrafts(PhaseName phase) =>
        db.Execute("UPDATE feedback SET status = 'open' WHERE phase = $Phase AND status = 'draft'", new { Phase = phase });

    /// <summary>
    /// Marks every open/draft item of the phase superseded (they targeted a version the phase is leaving behind),
    /// pairing them with <paramref name="respondedVersion"/> (the phase's version at the time of the cascade, if any);
    /// addressed/declined items are untouched. Returns how many were changed.
    /// </summary>
    public int CloseOpenAndDrafts(PhaseName phase, int? respondedVersion) =>
        db.Execute("""
            UPDATE feedback SET status = 'superseded', response = 'The phase changed before this was addressed.',
                responded_version = $Version
            WHERE phase = $Phase AND status IN ('open', 'draft')
            """, new { Phase = phase, Version = respondedVersion });

    public IReadOnlyList<FeedbackRow> List(PhaseName phase, FeedbackStatus? status = null) =>
        status is null
            ? db.Query($"SELECT {Columns} FROM feedback WHERE phase = $Phase ORDER BY id", Map, new { Phase = phase })
            : db.Query($"SELECT {Columns} FROM feedback WHERE phase = $Phase AND status = $Status ORDER BY id", Map,
                new { Phase = phase, Status = status.Value });

    public void Respond(long id, FeedbackStatus status, string response, int respondedVersion) =>
        db.Execute("UPDATE feedback SET status = $Status, response = $Response, responded_version = $Version WHERE id = $Id",
            new { Id = id, Status = status, Response = response, Version = respondedVersion });

    public bool DeleteDraft(long id) =>
        db.Execute("DELETE FROM feedback WHERE id = $Id AND status = 'draft'", new { Id = id }) > 0;

    private static FeedbackRow Map(SqliteDataReader r) =>
        new(r.GetInt64(0), r.Enum<PhaseName>(1), r.GetInt32(2), r.Str(3), r.GetString(4), r.Enum<FeedbackStatus>(5),
            r.Str(6), r.IntN(7), r.Ts(8));
}
