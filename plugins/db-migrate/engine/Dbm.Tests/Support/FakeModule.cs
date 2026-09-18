using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Jobs;
using Dbm.Core.Patching;
using Dbm.Core.Sql;
using Dbm.Core.State;
using Dbm.Core.Workflow;

namespace Dbm.Tests.Support;

/// <summary>Configurable stand-in for AnalysisModule / MappingModule / SqlModule.</summary>
public sealed class FakeModule(PhaseName phase, string agent, string jobKind) : IPhaseModule
{
    public PhaseName Phase => phase;
    public string Agent => agent;
    public string JobKind => jobKind;

    public bool NeedsAgentResult { get; set; } = true;
    public Func<JsonNode, PayloadCheck> ValidateFn { get; set; } = _ => PayloadCheck.Pass();
    public Func<JsonNode, IReadOnlyList<string>> BlockersFn { get; set; } = _ => [];
    public List<PacketMode> PacketsBuilt { get; } = new();

    public bool NeedsAgent(JsonNode draft) => NeedsAgentResult;

    public JsonNode BuildPacket(ModuleContext ctx, PacketMode mode)
    {
        lock (PacketsBuilt) PacketsBuilt.Add(mode);
        return new JsonObject { ["fake"] = true, ["baseVersion"] = ctx.Current.Version, ["openFeedback"] = ctx.OpenFeedback.Count };
    }

    public PayloadCheck Validate(ModuleContext ctx, JsonNode payload) => ValidateFn(payload);

    public IReadOnlyList<string> ApprovalBlockers(ModuleContext ctx, JsonNode payload) => BlockersFn(payload);

    public string Summarize(JsonNode payload) => payload["summary"]?.GetValue<string>() ?? $"{phase} artifact";
}

public sealed class FakeModules
{
    public FakeModule Analysis { get; } = new(PhaseName.Analysis, "schema-analyst", "analyze");
    public FakeModule Mapping { get; } = new(PhaseName.Mapping, "mapping-architect", "automap");
    public FakeModule Sql { get; } = new(PhaseName.Sql, "sql-engineer", "sqlgen");
    public IEnumerable<IPhaseModule> All => [Analysis, Mapping, Sql];

    public FakeModule this[PhaseName phase] => phase switch
    {
        PhaseName.Analysis => Analysis,
        PhaseName.Mapping => Mapping,
        PhaseName.Sql => Sql,
        _ => throw new ArgumentOutOfRangeException(nameof(phase)),
    };
}

/// <summary>Job handler returning a canned result ("discover" → no draft, others → FakeServices.Draft()).</summary>
public sealed class FakeJobHandler(string kind, Func<JobContext, JobResult>? run = null) : IJobHandler
{
    private int _runs;

    public string Kind => kind;
    public int Runs => Volatile.Read(ref _runs);

    public Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        Interlocked.Increment(ref _runs);
        return Task.FromResult((run ?? Default)(ctx));
    }

    private static JobResult Default(JobContext ctx) =>
        ctx.Job.Kind == "discover" ? new JobResult(null, "catalogs extracted") : new JobResult(FakeServices.Draft($"{ctx.Job.Kind} draft"), null);

    public static List<IJobHandler> Standard() =>
        [new FakeJobHandler("discover"), new FakeJobHandler("analyze"), new FakeJobHandler("automap"), new FakeJobHandler("sqlgen")];
}

/// <summary>Services wired with fakes, plus helpers that drive the workflow without SQL Server.</summary>
public static class FakeServices
{
    /// <summary>Approves <paramref name="phase"/> naming its current version - what a reviewer looking at the latest version sends
    /// (Ruling 194: every approve names the version it saw).</summary>
    public static void ApproveCurrent(this DbmServices s, PhaseName phase) =>
        s.Workflow.Approve(phase, s.Phases.Get(phase).CurrentVersion ?? -1);

    public const string SrcConnection = "Server=src-host;Database=Legacy;Integrated Security=true";
    public const string TgtConnection = "Server=tgt-host;Database=ShopV2;Integrated Security=true";

    /// <param name="initProject">false = leave project creation to the code under test (e.g. `dbm init`).</param>
    public static DbmServices Open(Workspace ws, FakeModules? modules = null, IEnumerable<IJobHandler>? handlers = null,
        IEventSink? sink = null, bool initProject = true)
    {
        ws.EnsureCreated();
        var m = modules ?? new FakeModules();
        var h = handlers?.ToList() ?? FakeJobHandler.Standard();
        var s = DbmServices.Open(ws, sink, _ => m.All, _ => h);
        if (initProject && !s.Project.Exists()) s.Project.Init("test-project");
        return s;
    }

    public static Func<Workspace, DbmServices> Factory(FakeModules? modules = null, IEnumerable<IJobHandler>? handlers = null,
        bool initProject = true) =>
        ws => Open(ws, modules, handlers, initProject: initProject);

    public static JsonObject Draft(string summary = "script draft") =>
        new() { ["summary"] = summary, ["items"] = new JsonObject { ["a"] = 1, ["b"] = 2 } };

    public static ServerMeta Meta(string server, string database) =>
        new(server, database, "Microsoft SQL Server 2025 (RTM) - 17.0.1000.7 (X64)", "17.0.1000.7", 17, "Developer Edition (64-bit)",
            "SQL_Latin1_General_CP1_CI_AS", "SQL_Latin1_General_CP1_CI_AS", 170, "integrated");

    /// <summary>Saves both (fake) connections and fires OnConnectionsSaved → Discovery running, "discover" queued.</summary>
    public static void SaveConnections(DbmServices s)
    {
        s.Connections.Save(Side.Src, SrcConnection, Meta("src-host", "Legacy"));
        s.Connections.Save(Side.Tgt, TgtConnection, Meta("tgt-host", "ShopV2"));
        s.Workflow.OnConnectionsSaved();
    }

    public static void SetCatalogFingerprints(DbmServices s, string src, string tgt) =>
        s.Db.Execute("""
            INSERT OR REPLACE INTO catalog (side, snapshot_json, fingerprint, extracted_at)
            VALUES ('src', '{}', $Src, $Now), ('tgt', '{}', $Tgt, $Now)
            """, new { Src = src, Tgt = tgt, Now = Clock.NowText() });

    /// <summary>Completes the oldest queued job the way JobRunner would (without running a handler).</summary>
    public static JobRow CompleteNextJob(DbmServices s, JsonNode? payload = null, string? summary = null)
    {
        var job = s.Jobs.NextQueued() ?? throw new InvalidOperationException("no queued job");
        s.Jobs.MarkRunning(job.Id);
        s.Db.InTransaction(() =>
        {
            s.Jobs.MarkDone(job.Id);
            s.Workflow.OnJobDone(job, payload, summary);
        });
        return job;
    }

    /// <summary>Agent patch replacing /summary on the current version (with responses for every open item).</summary>
    public static ApplyResult ApplySummary(DbmServices s, PhaseName phase, string summary)
    {
        var row = s.Phases.Get(phase);
        var responses = s.Feedback.List(phase, FeedbackStatus.Open)
            .Select(f => new FeedbackResponse(f.Id, "addressed", $"done: {f.Text}"))
            .ToList();
        var patch = new Patch(phase.Text(), row.CurrentVersion ?? -1,
            [new PatchOp("replace", "/summary", JsonValue.Create(summary))], responses, summary);
        return s.Workflow.ApplyPatch(patch);
    }

    /// <summary>Setup → Discovery → Analysis drafting (v0 script draft).</summary>
    public static void DriveToAnalysisDraft(DbmServices s)
    {
        SaveConnections(s);
        CompleteNextJob(s);            // discover
        CompleteNextJob(s, Draft());   // analyze
    }

    /// <summary>Drives <paramref name="phase"/> (Analysis, Mapping or Sql) to awaiting_review with an agent v1.</summary>
    public static void DriveToReview(DbmServices s, PhaseName phase)
    {
        DriveToAnalysisDraft(s);
        ApplySummary(s, PhaseName.Analysis, "analysis v1");
        foreach (var next in new[] { PhaseName.Mapping, PhaseName.Sql })
        {
            if (phase < next) return;
            s.ApproveCurrent(next == PhaseName.Mapping ? PhaseName.Analysis : PhaseName.Mapping);
            CompleteNextJob(s, Draft());
            ApplySummary(s, next, $"{next.Text()} v1");
        }
    }
}
