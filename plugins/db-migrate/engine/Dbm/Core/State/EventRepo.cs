namespace Dbm.Core.State;

/// <summary>Append-only audit log; also the feed the server''s EventPump turns into SSE.</summary>
public sealed class EventRepo(StateDb db)
{
    public long Append(string type, object? payload = null) =>
        db.Scalar<long>("INSERT INTO event (ts, type, payload_json) VALUES ($Now, $Type, $Payload) RETURNING id",
            new { Now = Clock.NowText(), Type = type, Payload = payload is null ? "{}" : Json.Serialize(payload) });

    public IReadOnlyList<EventRow> Since(long afterId, int limit = 500) =>
        db.Query("SELECT id, ts, type, payload_json FROM event WHERE id > $After ORDER BY id LIMIT $Limit",
            r => new EventRow(r.GetInt64(0), r.Ts(1), r.GetString(2), r.GetString(3)),
            new { After = afterId, Limit = limit });

    public long LastId() => db.Scalar<long?>("SELECT MAX(id) FROM event") ?? 0;
}
