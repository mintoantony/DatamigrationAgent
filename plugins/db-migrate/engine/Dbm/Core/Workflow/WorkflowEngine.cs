using System.Text.Json.Nodes;
using Dbm.Core.Patching;
using Dbm.Core.State;

namespace Dbm.Core.Workflow;

public sealed record ApplyResult(bool Ok, int? Version, List<string> Errors, List<string> Warnings)
{
    public static ApplyResult Fail(params string[] errors) => new(false, null, errors.ToList(), []);
}

public sealed class WorkflowException(string message, IReadOnlyList<string>? details = null) : Exception(message)
{
    public IReadOnlyList<string> Details { get; } = details ?? [];
}

/// <summary>Owns every phase transition (see the transition table in 00-overview.md C5). All mutations are transactional.</summary>
public sealed class WorkflowEngine(DbmServices services)
{
    public static readonly PhaseName[] Order = Enum.GetValues<PhaseName>();

    public const string PatchRules =
        "Write a Patch JSON to patchPath: ops (add|replace|remove, JSON pointer into the artifact), a response for every feedback id, and a one-line summary.";

    // ---------------------------------------------------------------- next action

    /// <summary>The orchestrator's next step; writes the work packet for "agent" actions.</summary>
    public NextAction Next() => Compute(writePacket: true);

    /// <summary>Same as Next() without writing packet files (used by /api/state and `dbm status`).</summary>
    public NextAction Peek() => Compute(writePacket: false);

    private NextAction Compute(bool writePacket)
    {
        var url = services.UiUrl();
        if (services.Project.Get().Paused) return new NextAction("await", Reason: "paused");

        foreach (var row in services.Phases.All())
        {
            if (row.Status == PhaseStatus.Approved) continue;
            var name = row.Name.Text();
            if (row.Name == PhaseName.Setup) return new NextAction("await", Reason: "setup", Phase: name, Url: url);

            switch (row.Status)
            {
                case PhaseStatus.Running when row.Name == PhaseName.Transfer:
                    return TransferAction(url);
                case PhaseStatus.Running:
                    var job = services.Jobs.LatestFor(row.Name);
                    return job?.Status == JobStatus.Failed
                        ? new NextAction("stop", Reason: "job_failed", Phase: name, Summary: job.Error ?? $"{job.Kind} failed")
                        : new NextAction("await", Reason: "job", Phase: name);
                case PhaseStatus.Drafting:
                    return AgentAction(row, PacketMode.Draft, writePacket);
                case PhaseStatus.Reworking:
                    return AgentAction(row, PacketMode.Rework, writePacket);
                case PhaseStatus.AwaitingReview:
                    return new NextAction("await", Reason: row.Name == PhaseName.Ready ? "execute" : "review", Phase: name, Url: url);
                default:
                    // pending / stale while every earlier phase is approved: an upstream job is about to (re)start it.
                    return new NextAction("await", Reason: "job", Phase: name);
            }
        }

        var summary = services.Artifacts.Latest(PhaseName.Complete)?.Summary;
        return new NextAction("stop", Reason: "complete", Summary: summary ?? "Migration complete.");
    }

    private NextAction TransferAction(string url)
    {
        var status = services.Db.Scalar<string>("SELECT status FROM transfer_run ORDER BY id DESC LIMIT 1");
        const string phase = "transfer";
        return status switch
        {
            "paused" => new NextAction("await", Reason: "transfer_paused", Phase: phase, Url: url),
            "failed" => new NextAction("stop", Reason: "transfer_failed", Phase: phase, Summary: "The transfer failed; see the Execute screen.", Url: url),
            "cancelled" => new NextAction("stop", Reason: "transfer_cancelled", Phase: phase, Summary: "The transfer was cancelled.", Url: url),
            _ => new NextAction("await", Reason: "transfer", Phase: phase, Url: url),
        };
    }

    private NextAction AgentAction(PhaseRow row, PacketMode mode, bool writePacket)
    {
        if (!services.Modules.TryGetValue(row.Name, out var module) || row.CurrentVersion is not int baseVersion)
            return new NextAction("await", Reason: "job", Phase: row.Name.Text());

        var modeText = EnumText.ToText(mode);
        var stem = $"{row.Name.Text()}-v{baseVersion}-{modeText}";
        var packetPath = Path.Combine(services.Ws.WorkDir, stem + ".json");
        var patchPath = Path.Combine(services.Ws.WorkDir, stem + ".patch.json");
        if (writePacket) WritePacket(module, row.Name, mode, baseVersion, packetPath, patchPath);
        return new NextAction("agent", Agent: module.Agent, Phase: row.Name.Text(), Mode: modeText,
            Packet: Slashes(packetPath), PatchPath: Slashes(patchPath));
    }

    private void WritePacket(IPhaseModule module, PhaseName phase, PacketMode mode, int baseVersion, string packetPath, string patchPath)
    {
        var current = services.Artifacts.Get(phase, baseVersion)
                      ?? throw new WorkflowException($"artifact {phase.Text()} v{baseVersion} is missing");
        var open = mode == PacketMode.Rework ? services.Feedback.List(phase, FeedbackStatus.Open) : [];
        var ctx = new ModuleContext { Services = services, Current = current, OpenFeedback = open };
        var data = module.BuildPacket(ctx, mode);
        var envelope = new JsonObject
        {
            ["phase"] = phase.Text(),
            ["mode"] = EnumText.ToText(mode),
            ["baseVersion"] = baseVersion,
            ["patchPath"] = Slashes(patchPath),
            ["agent"] = module.Agent,
            ["feedback"] = new JsonArray(open
                .Select(f => (JsonNode)new JsonObject { ["id"] = f.Id, ["anchor"] = f.Anchor, ["text"] = f.Text })
                .ToArray()),
            ["rules"] = PatchRules,
            ["data"] = data.Parent is null ? data : data.DeepClone(),
        };
        Directory.CreateDirectory(services.Ws.WorkDir);
        if (File.Exists(patchPath)) File.Delete(patchPath);   // never apply a patch written for an older packet
        File.WriteAllText(packetPath, envelope.ToJsonString(Json.Options));
    }

    // ---------------------------------------------------------------- setup & jobs

    /// <summary>Both sides saved → Setup approved, Discovery running + "discover"; started later phases → stale.</summary>
    public void OnConnectionsSaved() => services.Db.InTransaction(() =>
    {
        if (!services.Connections.Has(Side.Src) || !services.Connections.Has(Side.Tgt)) return;
        EnsureTransferPending("Connections cannot change after the transfer has started.");
        SetStatus(PhaseName.Setup, PhaseStatus.Approved);
        foreach (var later in Order.Where(p => p > PhaseName.Discovery)) MarkStale(later);
        StartJob(PhaseName.Discovery);
    });

    /// <summary>Re-extract both catalogs; Analysis..Ready become stale. Only while Transfer is pending.</summary>
    public void Rediscover() => services.Db.InTransaction(() =>
    {
        EnsureTransferPending("Re-discovery is not possible after the transfer has started.");
        if (services.Phases.Get(PhaseName.Setup).Status != PhaseStatus.Approved)
            throw new WorkflowException("Save both connections before running discovery.");
        foreach (var later in Order.Where(p => p > PhaseName.Discovery && p <= PhaseName.Ready)) MarkStale(later);
        StartJob(PhaseName.Discovery);
    });

    public void OnJobDone(JobRow job, JsonNode? draftPayload, string? summary) => services.Db.InTransaction(() =>
    {
        if (job.Phase is not PhaseName phase) return;
        var row = services.Phases.Get(phase);
        var latest = services.Jobs.LatestFor(phase);
        if (row.Status != PhaseStatus.Running || (latest is not null && latest.Id != job.Id))
        {
            Publish("log", new { level = "warn", message = $"Ignored the result of job {job.Id} ({job.Kind}): {phase.Text()} is {EnumText.ToText(row.Status)} or has a newer job." });
            return;
        }

        if (phase == PhaseName.Discovery)
        {
            var version = row.CurrentVersion ?? 0;
            if (draftPayload is not null) version = StoreArtifact(phase, draftPayload, "script", summary);
            services.Phases.SetApproved(phase, version, CatalogFingerprint());
            Publish("state_changed", new { phase, status = PhaseStatus.Approved });
            StartJob(PhaseName.Analysis);
            return;
        }

        if (!services.Modules.TryGetValue(phase, out var module))
            throw new WorkflowException($"No module is registered for phase {phase.Text()}.");
        if (draftPayload is null) throw new WorkflowException($"Job {job.Kind} returned no draft for {phase.Text()}.");
        StoreArtifact(phase, draftPayload, "script", summary ?? module.Summarize(draftPayload));
        SetStatus(phase, module.NeedsAgent(draftPayload) ? PhaseStatus.Drafting : PhaseStatus.AwaitingReview);
    });

    /// <summary>The phase stays running; Next() reports stop/job_failed until the job is retried.</summary>
    public void OnJobFailed(JobRow job, string error) =>
        Publish("job_failed", new { id = job.Id, kind = job.Kind, phase = job.Phase, error });

    /// <summary>Re-enqueues the phase's job: `running` (its job failed) or `drafting` (regenerate the script draft).</summary>
    public void RetryJob(PhaseName phase) => services.Db.InTransaction(() =>
    {
        var row = services.Phases.Get(phase);
        if (row.Status is not (PhaseStatus.Running or PhaseStatus.Drafting))
            throw new WorkflowException($"{phase.Text()} is {EnumText.ToText(row.Status)}; only a running or drafting phase's job can be retried.");
        if (services.Jobs.Active().Any(j => j.Phase == phase))
            throw new WorkflowException($"A job for {phase.Text()} is already queued or running.");
        services.Jobs.Enqueue(JobKind(phase), phase);
        if (row.Status == PhaseStatus.Drafting) SetStatus(phase, PhaseStatus.Running);
        else Publish("state_changed", new { phase, status = row.Status });
    });

    /// <summary>The artifact a re-running job should carry over (the latest version before the re-run).</summary>
    public ArtifactRow? CarryOver(PhaseName phase) => services.Artifacts.Latest(phase);

    // ---------------------------------------------------------------- review loop

    /// <summary>Agent patch (author "agent"); allowed while paused so in-flight work is never lost.</summary>
    public ApplyResult ApplyPatch(Patch patch, bool dryRun = false) => Apply(patch, "agent", dryRun);

    /// <summary>Direct edit from the UI (author "human"); the phase must be awaiting review.</summary>
    public ApplyResult HumanEdit(Patch patch) => Apply(patch, "human", dryRun: false);

    private ApplyResult Apply(Patch patch, string author, bool dryRun)
    {
        if (!Phases.TryParse(patch.Phase, out var phase)) return ApplyResult.Fail($"unknown phase '{patch.Phase}'");
        if (!services.Modules.TryGetValue(phase, out var module)) return ApplyResult.Fail($"phase '{phase.Text()}' does not accept patches");
        var human = author == "human";

        return services.Db.InTransaction(() =>
        {
            var row = services.Phases.Get(phase);
            var status = EnumText.ToText(row.Status);
            if (human && row.Status != PhaseStatus.AwaitingReview)
                return ApplyResult.Fail($"phase {phase.Text()} is {status}; direct edits are allowed only while it awaits review");
            if (!human && row.Status is not (PhaseStatus.Drafting or PhaseStatus.Reworking))
                return ApplyResult.Fail($"phase {phase.Text()} is {status}; agent patches are accepted only while drafting or reworking");
            if (row.CurrentVersion is not int current || patch.BaseVersion != current)
                return ApplyResult.Fail($"baseVersion {patch.BaseVersion} does not match the current version {row.CurrentVersion?.ToString() ?? "(none)"}");

            var open = !human && row.Status == PhaseStatus.Reworking ? services.Feedback.List(phase, FeedbackStatus.Open) : [];
            var responses = human ? [] : patch.Responses ?? [];
            var errors = CheckResponses(open, responses);
            if (errors.Count > 0) return new ApplyResult(false, null, errors, []);

            var baseRow = services.Artifacts.Get(phase, current)!;
            JsonNode updated;
            try
            {
                updated = JsonPatch.Apply(JsonNode.Parse(baseRow.PayloadJson)!, patch.Ops ?? []);
            }
            catch (PatchException ex)
            {
                return ApplyResult.Fail(ex.Message);
            }

            var check = module.Validate(new ModuleContext { Services = services, Current = baseRow, OpenFeedback = open }, updated);
            if (!check.Ok) return new ApplyResult(false, null, check.Errors, check.Warnings);
            if (dryRun) return new ApplyResult(true, null, [], check.Warnings);

            var summary = string.IsNullOrWhiteSpace(patch.Summary) ? module.Summarize(updated) : patch.Summary;
            var version = StoreArtifact(phase, updated, author, summary);
            foreach (var r in responses)
            {
                var fs = r.Status == "declined" ? FeedbackStatus.Declined : FeedbackStatus.Addressed;
                services.Feedback.Respond(r.FeedbackId, fs, r.Note, version);
            }
            if (responses.Count > 0) Publish("feedback_changed", new { phase });
            if (!human) SetStatus(phase, PhaseStatus.AwaitingReview);
            return new ApplyResult(true, version, [], check.Warnings);
        });
    }

    private static List<string> CheckResponses(IReadOnlyList<FeedbackRow> open, IReadOnlyList<FeedbackResponse> responses)
    {
        var errors = new List<string>();
        var openIds = open.Select(f => f.Id).ToHashSet();
        foreach (var r in responses)
        {
            if (r.Status is not ("addressed" or "declined"))
                errors.Add($"response for feedback {r.FeedbackId}: status must be 'addressed' or 'declined'");
            if (!openIds.Contains(r.FeedbackId))
                errors.Add($"feedback {r.FeedbackId} is not an open item of this phase");
            if (string.IsNullOrWhiteSpace(r.Note))
                errors.Add($"response for feedback {r.FeedbackId}: note is required");
        }
        foreach (var id in openIds.Except(responses.Select(r => r.FeedbackId)).Order())
            errors.Add($"missing response for feedback {id}");
        return errors;
    }

    /// <summary>Submits draft feedback; requires at least one open item; phase → reworking.</summary>
    public void RequestChanges(PhaseName phase) => services.Db.InTransaction(() =>
    {
        if (!Phases.Reviewable.Contains(phase)) throw new WorkflowException($"{phase.Text()} cannot be reworked.");
        var row = services.Phases.Get(phase);
        if (row.Status != PhaseStatus.AwaitingReview)
            throw new WorkflowException($"{phase.Text()} is {EnumText.ToText(row.Status)}; changes can be requested only while it awaits review.");
        services.Feedback.SubmitDrafts(phase);
        if (services.Feedback.List(phase, FeedbackStatus.Open).Count == 0)
            throw new WorkflowException("Add at least one feedback item before requesting changes.");
        Publish("feedback_changed", new { phase });
        SetStatus(phase, PhaseStatus.Reworking);
    });

    /// <summary>Throws WorkflowException(details = blockers). Records approved_fingerprint = "&lt;src&gt;:&lt;tgt&gt;".</summary>
    public void Approve(PhaseName phase) => services.Db.InTransaction(() =>
    {
        if (!Phases.Reviewable.Contains(phase)) throw new WorkflowException($"{phase.Text()} is not approved from a review screen.");
        var row = services.Phases.Get(phase);
        if (row.Status != PhaseStatus.AwaitingReview)
            throw new WorkflowException($"{phase.Text()} is {EnumText.ToText(row.Status)}; only a phase awaiting review can be approved.");
        if (!services.Modules.TryGetValue(phase, out var module))
            throw new WorkflowException($"No module is registered for phase {phase.Text()}.");

        var current = services.Artifacts.Get(phase, row.CurrentVersion ?? -1)
                      ?? throw new WorkflowException($"{phase.Text()} has no current version to approve.");
        var ctx = new ModuleContext { Services = services, Current = current, OpenFeedback = services.Feedback.List(phase, FeedbackStatus.Open) };
        var blockers = module.ApprovalBlockers(ctx, JsonNode.Parse(current.PayloadJson)!);
        if (blockers.Count > 0) throw new WorkflowException("Approval is blocked.", blockers);

        services.Phases.SetApproved(phase, current.Version, CatalogFingerprint());
        Publish("state_changed", new { phase, status = PhaseStatus.Approved });
        switch (phase)
        {
            case PhaseName.Analysis: StartJob(PhaseName.Mapping); break;
            case PhaseName.Mapping: StartJob(PhaseName.Sql); break;
            case PhaseName.Sql: SetStatus(PhaseName.Ready, PhaseStatus.AwaitingReview); break;
        }
    });

    /// <summary>approved → awaiting_review at the approved version; later phases up to Ready → stale.</summary>
    public void Reopen(PhaseName phase) => services.Db.InTransaction(() =>
    {
        EnsureTransferPending("Phases cannot be reopened after the transfer has started.");
        if (!Phases.Reviewable.Contains(phase)) throw new WorkflowException($"{phase.Text()} cannot be reopened.");
        var row = services.Phases.Get(phase);
        if (row.Status != PhaseStatus.Approved) throw new WorkflowException($"{phase.Text()} is not approved.");
        services.Phases.ClearApproval(phase);
        if (row.ApprovedVersion is int approved) services.Phases.SetCurrentVersion(phase, approved);
        SetStatus(phase, PhaseStatus.AwaitingReview);
        foreach (var later in Order.Where(p => p > phase && p <= PhaseName.Ready)) MarkStale(later);
    });

    public void SetPaused(bool paused) => services.Db.InTransaction(() =>
    {
        services.Project.SetPaused(paused);
        Publish(paused ? "paused" : "resumed");
    });

    // ---------------------------------------------------------------- transfer

    public void OnTransferStarted() => services.Db.InTransaction(() =>
    {
        SetStatus(PhaseName.Ready, PhaseStatus.Approved);
        SetStatus(PhaseName.Transfer, PhaseStatus.Running);
    });

    /// <summary>"completed" → Transfer and Complete approved (the report artifact is already stored).</summary>
    public void OnTransferFinished(string runStatus, int? reportVersion) => services.Db.InTransaction(() =>
    {
        if (runStatus != "completed")
        {
            Publish("state_changed", new { phase = PhaseName.Transfer, status = PhaseStatus.Running });
            return;
        }
        SetStatus(PhaseName.Transfer, PhaseStatus.Approved);
        if (reportVersion is int v)
        {
            services.Phases.SetCurrentVersion(PhaseName.Complete, v);
            services.Phases.SetApproved(PhaseName.Complete, v, null);
            Publish("state_changed", new { phase = PhaseName.Complete, status = PhaseStatus.Approved });
        }
        else
        {
            SetStatus(PhaseName.Complete, PhaseStatus.Approved);
        }
    });

    // ---------------------------------------------------------------- helpers

    private void StartJob(PhaseName phase)
    {
        SetStatus(phase, PhaseStatus.Running);
        var kind = JobKind(phase);
        var alreadyQueued = services.Jobs.Active().Any(j => j.Phase == phase && j.Kind == kind && j.Status == JobStatus.Queued);
        if (!alreadyQueued) services.Jobs.Enqueue(kind, phase);
    }

    private string JobKind(PhaseName phase) =>
        services.Modules.TryGetValue(phase, out var m) ? m.JobKind
        : Phases.DefaultJobKinds.TryGetValue(phase, out var kind) ? kind
        : throw new WorkflowException($"{phase.Text()} has no job.");

    private void MarkStale(PhaseName phase)
    {
        var row = services.Phases.Get(phase);
        if (row.Status is PhaseStatus.Pending or PhaseStatus.Stale) return;
        services.Phases.ClearApproval(phase);
        // Open/draft feedback targeted the version this phase is leaving behind; it must not survive the cascade.
        if (services.Feedback.CloseOpenAndDrafts(phase) > 0) Publish("feedback_changed", new { phase });
        SetStatus(phase, PhaseStatus.Stale);
    }

    private int StoreArtifact(PhaseName phase, JsonNode payload, string author, string? summary)
    {
        var version = services.Artifacts.NextVersion(phase);
        services.Artifacts.Add(phase, version, payload.ToJsonString(Json.Options), author, summary);
        services.Phases.SetCurrentVersion(phase, version);
        Publish("artifact_created", new { phase, version, author });
        return version;
    }

    private void EnsureTransferPending(string message)
    {
        if (services.Phases.Get(PhaseName.Transfer).Status != PhaseStatus.Pending) throw new WorkflowException(message);
    }

    /// <summary>"&lt;src&gt;:&lt;tgt&gt;" from the catalog table; null until both sides are discovered.</summary>
    private string? CatalogFingerprint()
    {
        var rows = services.Db.Query("SELECT side, fingerprint FROM catalog", r => (Side: r.GetString(0), Fp: r.GetString(1)));
        var src = rows.FirstOrDefault(r => r.Side == "src").Fp;
        var tgt = rows.FirstOrDefault(r => r.Side == "tgt").Fp;
        return src is null || tgt is null ? null : $"{src}:{tgt}";
    }

    private void SetStatus(PhaseName phase, PhaseStatus status)
    {
        services.Phases.SetStatus(phase, status);
        Publish("state_changed", new { phase, status });
    }

    private void Publish(string type, object? payload = null) => services.Sink.Publish(type, payload);

    private static string Slashes(string path) => Path.GetFullPath(path).Replace('\\', '/');
}
