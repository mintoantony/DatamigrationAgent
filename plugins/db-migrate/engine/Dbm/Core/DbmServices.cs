using System.Text.Json.Nodes;
using Dbm.Core.Crypto;
using Dbm.Core.Jobs;
using Dbm.Core.State;
using Dbm.Core.Workflow;

namespace Dbm.Core;

/// <summary>Composition root for one workspace. The server shares one instance across all threads.</summary>
public sealed class DbmServices : IDisposable
{
    private DbmServices(Workspace ws, StateDb db, ISecretProtector protector)
    {
        Ws = ws;
        Db = db;
        Protector = protector;
        Project = new ProjectRepo(db);
        Phases = new PhaseRepo(db);
        Artifacts = new ArtifactRepo(db);
        Feedback = new FeedbackRepo(db);
        Events = new EventRepo(db);
        Jobs = new JobRepo(db);
        Connections = new ConnectionRepo(db, protector);
        Sink = new DbEventSink(Events);
        Workflow = new WorkflowEngine(this);
    }

    public Workspace Ws { get; }
    public StateDb Db { get; }
    public ISecretProtector Protector { get; }
    public ProjectRepo Project { get; }
    public PhaseRepo Phases { get; }
    public ArtifactRepo Artifacts { get; }
    public FeedbackRepo Feedback { get; }
    public EventRepo Events { get; }
    public JobRepo Jobs { get; }
    public ConnectionRepo Connections { get; }

    // T2.5: catalog snapshots + search vectors (a stateless wrapper over Db, created on first use)
    private CatalogRepo? _catalog;
    public CatalogRepo Catalog => _catalog ??= new CatalogRepo(Db);

    public IEventSink Sink { get; set; }
    public WorkflowEngine Workflow { get; }
    public IReadOnlyDictionary<PhaseName, IPhaseModule> Modules { get; private set; } = new Dictionary<PhaseName, IPhaseModule>();
    public IReadOnlyDictionary<string, IJobHandler> JobHandlers { get; private set; } = new Dictionary<string, IJobHandler>();

    /// <param name="sink">Default: DbEventSink (persist only).</param>
    /// <param name="modules">Default: ModuleRegistry.Create (tests pass fakes).</param>
    /// <param name="jobs">Default: JobRegistry.Create (tests pass fakes).</param>
    public static DbmServices Open(Workspace ws, IEventSink? sink = null,
        Func<DbmServices, IEnumerable<IPhaseModule>>? modules = null,
        Func<DbmServices, IEnumerable<IJobHandler>>? jobs = null)
    {
        var db = StateDb.Open(ws.StateDbPath);
        try
        {
            var s = new DbmServices(ws, db, SecretProtector.ForCurrentUser());
            if (sink is not null) s.Sink = sink;
            s.Modules = (modules ?? ModuleRegistry.Create)(s).ToDictionary(m => m.Phase);
            s.JobHandlers = (jobs ?? JobRegistry.Create)(s).ToDictionary(h => h.Kind, StringComparer.Ordinal);
            return s;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    /// <summary>The browser URL (with token) from server.json; "" when no server has written one.</summary>
    public string UiUrl()
    {
        try
        {
            if (!File.Exists(Ws.ServerJsonPath)) return "";
            var node = JsonNode.Parse(File.ReadAllText(Ws.ServerJsonPath));
            var port = node?["port"]?.GetValue<int>();
            var token = node?["token"]?.GetValue<string>();
            return port is > 0 && !string.IsNullOrEmpty(token) ? $"http://127.0.0.1:{port}/?t={token}" : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    public void Dispose() => Db.Dispose();
}
