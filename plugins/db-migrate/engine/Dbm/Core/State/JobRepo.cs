using Microsoft.Data.Sqlite;

namespace Dbm.Core.State;

public sealed class JobRepo(StateDb db)
{
    private const string Columns = "id, kind, phase, status, error, created_at, started_at, ended_at";

    public long Enqueue(string kind, PhaseName? phase) =>
        db.Scalar<long>("INSERT INTO job (kind, phase, status, created_at) VALUES ($Kind, $Phase, 'queued', $Now) RETURNING id",
            new { Kind = kind, Phase = phase, Now = Clock.NowText() });

    public JobRow? Get(long id) =>
        db.Query($"SELECT {Columns} FROM job WHERE id = $Id", Map, new { Id = id }).FirstOrDefault();

    public JobRow? NextQueued() =>
        db.Query($"SELECT {Columns} FROM job WHERE status = 'queued' ORDER BY id LIMIT 1", Map).FirstOrDefault();

    public JobRow? LatestFor(PhaseName phase) =>
        db.Query($"SELECT {Columns} FROM job WHERE phase = $Phase ORDER BY id DESC LIMIT 1", Map, new { Phase = phase })
            .FirstOrDefault();

    public void MarkRunning(long id) =>
        db.Execute("UPDATE job SET status = 'running', started_at = $Now, error = NULL WHERE id = $Id",
            new { Id = id, Now = Clock.NowText() });

    /// <summary>Atomically moves a queued job to running; false when another runner (process) took it first.</summary>
    public bool TryClaim(long id) =>
        db.Execute("UPDATE job SET status = 'running', started_at = $Now, error = NULL WHERE id = $Id AND status = 'queued'",
            new { Id = id, Now = Clock.NowText() }) == 1;

    public void MarkDone(long id) =>
        db.Execute("UPDATE job SET status = 'done', ended_at = $Now WHERE id = $Id", new { Id = id, Now = Clock.NowText() });

    public void MarkFailed(long id, string error) =>
        db.Execute("UPDATE job SET status = 'failed', error = $Error, ended_at = $Now WHERE id = $Id",
            new { Id = id, Error = error, Now = Clock.NowText() });

    /// <summary>Queued or running, oldest first.</summary>
    public IReadOnlyList<JobRow> Active() =>
        db.Query($"SELECT {Columns} FROM job WHERE status IN ('queued', 'running') ORDER BY id", Map);

    /// <summary>Newest first.</summary>
    public IReadOnlyList<JobRow> Recent(int limit) =>
        db.Query($"SELECT {Columns} FROM job ORDER BY id DESC LIMIT $Limit", Map, new { Limit = limit });

    /// <summary>running → queued; called when a server starts (the previous process died mid-job).</summary>
    public int RequeueStaleRunning() =>
        db.Execute("UPDATE job SET status = 'queued', started_at = NULL WHERE status = 'running'");

    private static JobRow Map(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.EnumN<PhaseName>(2), r.Enum<JobStatus>(3), r.Str(4), r.Ts(5), r.TsN(6), r.TsN(7));
}
