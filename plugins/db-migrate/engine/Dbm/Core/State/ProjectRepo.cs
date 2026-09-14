namespace Dbm.Core.State;

public sealed class ProjectRepo(StateDb db)
{
    public bool Exists() => db.Scalar<long>("SELECT COUNT(*) FROM project") > 0;

    /// <summary>Creates the project row and one phase row per PhaseName (Setup = awaiting_review, others = pending).</summary>
    public void Init(string name) => db.InTransaction(() =>
    {
        if (Exists()) throw new InvalidOperationException("project already initialised");
        var now = Clock.NowText();
        db.Execute("INSERT INTO project (id, name, created_at, paused, settings_json) VALUES (1, $Name, $Now, 0, '{}')",
            new { Name = name, Now = now });
        foreach (var phase in Enum.GetValues<PhaseName>())
        {
            db.Execute("INSERT INTO phase (name, ordinal, status, updated_at) VALUES ($Name, $Ordinal, $Status, $Now)", new
            {
                Name = phase,
                Ordinal = (int)phase,
                Status = phase == PhaseName.Setup ? PhaseStatus.AwaitingReview : PhaseStatus.Pending,
                Now = now,
            });
        }
    });

    public ProjectRow Get() =>
        db.Query("SELECT name, created_at, paused, agent_seen_at FROM project WHERE id = 1",
                r => new ProjectRow(r.GetString(0), r.Ts(1), r.Bool(2), r.TsN(3)))
            .FirstOrDefault()
        ?? throw new InvalidOperationException("project not initialised");

    public void SetPaused(bool paused) =>
        db.Execute("UPDATE project SET paused = $Paused WHERE id = 1", new { Paused = paused });

    public void TouchAgent() =>
        db.Execute("UPDATE project SET agent_seen_at = $Now WHERE id = 1", new { Now = Clock.NowText() });

    public ProjectSettings GetSettings() =>
        Json.Deserialize<ProjectSettings>(db.Scalar<string>("SELECT settings_json FROM project WHERE id = 1") ?? "{}");

    public void SaveSettings(ProjectSettings settings) =>
        db.Execute("UPDATE project SET settings_json = $SettingsJson WHERE id = 1", new { SettingsJson = Json.Serialize(settings) });
}
