using System.Text.Json.Nodes;
using Dbm.Core.Patching;
using Dbm.Core.State;

namespace Dbm.Core.Workflow;

public sealed record ApplyResult(bool Ok, int? Version, List<string> Errors, List<string> Warnings)
{
    public static ApplyResult Fail(params string[] errors) => new(false, null, errors.ToList(), []);
}

public sealed class WorkflowException(string message, IReadOnlyList<string>? details = null, string? code = null) : Exception(message)
{
    /// <summary>Ruling 194: the version an approve named is no longer the phase's current version.</summary>
    public const string StaleVersion = "stale_version";

    public IReadOnlyList<string> Details { get; } = details ?? [];

    /// <summary>A machine-readable reason the API answers with, or null for the endpoint's own default code.</summary>
    public string? Code { get; } = code;
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

    /// <summary>What a run that stopped leaves the operator to choose from (ruling 185) - the orchestrator relays it word for word.</summary>
    public const string ReopenHint = "To change the plan instead, reopen Analysis, Mapping or SQL on its review screen; after it is approved "
                                     + "again, Execute starts a new run.";

    private NextAction TransferAction(string url)
    {
        var latest = services.Db.Query("SELECT id, status, summary_json FROM transfer_run ORDER BY id DESC LIMIT 1",
            r => (Id: r.GetInt64(0), Status: r.GetString(1), Summary: r.IsDBNull(2) ? null : r.GetString(2))).FirstOrDefault();
        const string phase = "transfer";
        return latest.Status switch
        {
            "paused" => new NextAction("await", Reason: "transfer_paused", Phase: phase, Url: url),
            "failed" => new NextAction("stop", Reason: "transfer_failed", Phase: phase, Url: url, Summary:
                $"Transfer run {latest.Id} failed{ErrorOf(latest.Summary)}. On the Execute screen: Resume continues from the last "
                + "checkpoints; Cancel abandons the run and restores what the plan's pre-load SQL disabled; a new run loads every "
                + "table again. " + ReopenHint + " Reopening cancels the failed run first."),
            "cancelled" => new NextAction("stop", Reason: "transfer_cancelled", Phase: phase, Url: url, Summary:
                $"Transfer run {latest.Id} was cancelled; rows it committed stay in the target. A new run from the Execute screen loads "
                + "every table again: choose Truncate target first, or confirm loading into the tables that already hold rows. "
                + ReopenHint),
            _ => new NextAction("await", Reason: "transfer", Phase: phase, Url: url),
        };
    }

    private static string ErrorOf(string? summaryJson)
    {
        if (summaryJson is null) return "";
        try
        {
            var error = JsonNode.Parse(summaryJson)?["error"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(error)) return "";
            return ": " + (error.Length > 400 ? error[..400] + "…" : error);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return "";
        }
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
        File.WriteAllText(packetPath, PacketText(envelope));
    }

    /// <summary>
    /// Ruling 187 (final review I-3): a work packet is indented JSON, one value per line, so the subagent's Read tool can page a large
    /// one with offset/limit. Written compact it was a single line - on a real schema hundreds of KB that could not be read in pages at
    /// all. Same JSON options otherwise (camelCase, nulls omitted, relaxed escaping), so it parses to the same object.
    /// </summary>
    public static string PacketText(JsonNode envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return envelope.ToJsonString(Json.Pretty).Replace("\r\n", "\n") + "\n";
    }

    // ---------------------------------------------------------------- setup & jobs

    /// <summary>Both sides saved → Setup approved, Discovery running + "discover"; started later phases → stale.</summary>
    public void OnConnectionsSaved() => services.Db.InTransaction(() =>
    {
        if (!services.Connections.Has(Side.Src) || !services.Connections.Has(Side.Tgt)) return;
        EnsureUpstreamOpen("The connections cannot change");
        SetStatus(PhaseName.Setup, PhaseStatus.Approved);
        foreach (var later in Order.Where(p => p > PhaseName.Discovery && p <= PhaseName.Ready)) MarkStale(later);
        ResetTransfer();
        StartJob(PhaseName.Discovery);
    });

    /// <summary>Re-extract both catalogs; Analysis..Ready become stale. Before the first run, or after a completed or cancelled one
    /// (ruling 185).</summary>
    public void Rediscover() => services.Db.InTransaction(() =>
    {
        EnsureUpstreamOpen("Discovery cannot run again");
        if (services.Phases.Get(PhaseName.Setup).Status != PhaseStatus.Approved)
            throw new WorkflowException("Save both connections before running discovery.");
        foreach (var later in Order.Where(p => p > PhaseName.Discovery && p <= PhaseName.Ready)) MarkStale(later);
        ResetTransfer();
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
            // Ruling 196: a discovery that started while sample values were on may finish after they were switched off.
            if (!services.Project.GetSettings().SampleValues) SampleValuesSetting.Scrub(services);
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

    /// <summary>Summary of a version <see cref="Revalidate"/> stores.</summary>
    public const string RevalidatedSummary = "validation, no SQL change";

    /// <summary>
    /// Rulings 210/211 (open item 10 fix round): re-runs the module's own <see cref="IPhaseModule.Validate"/> over the current version
    /// unchanged (an empty patch) and stores what it writes - for SQL, the validation evidence - as a new version (author "script",
    /// <see cref="RevalidatedSummary"/>), in one transaction. <paramref name="seenVersion"/> must be the current version
    /// (<see cref="WorkflowException.StaleVersion"/> otherwise). It stores ONLY while the phase awaits review: while drafting or
    /// reworking the agent holds the current version as its patch's baseVersion, and a new version would reject its next apply.
    /// Returns Ok false with the reason (nothing stored) otherwise, or when validation finds errors.
    /// </summary>
    public ApplyResult Revalidate(PhaseName phase, int seenVersion) => services.Db.InTransaction(() =>
    {
        if (!services.Modules.TryGetValue(phase, out var module)) return ApplyResult.Fail($"phase '{phase.Text()}' has no module to validate");
        EnsureCurrentVersion(phase, seenVersion);
        var row = services.Phases.Get(phase);
        if (row.Status != PhaseStatus.AwaitingReview)
            return ApplyResult.Fail($"{phase.Text()} is {EnumText.ToText(row.Status)}; a validation is stored as a new version only while it awaits review");
        var baseRow = services.Artifacts.Get(phase, seenVersion)!;
        var updated = JsonNode.Parse(baseRow.PayloadJson)!;
        var check = module.Validate(new ModuleContext { Services = services, Current = baseRow, OpenFeedback = [] }, updated);
        if (!check.Ok) return new ApplyResult(false, null, check.Errors, check.Warnings);
        var version = StoreArtifact(phase, updated, "script", RevalidatedSummary);
        return new ApplyResult(true, version, [], check.Warnings);
    });

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

    /// <summary>
    /// Ruling 195 (open item 37): the human takes over a phase Claude is drafting or reworking - typically one whose patch was
    /// rejected twice, which leaves the orchestrator stopped and the UI without direct edits or Request changes. The phase goes back
    /// to awaiting_review on its current version; open feedback stays open (a later Request changes sends it again); Next() then
    /// awaits the human. The pending agent work is discarded by the status alone: <see cref="ApplyPatch"/> accepts a patch only
    /// while drafting or reworking, so a patch delivered afterwards is refused. The caller refuses this while an agent may be
    /// applying (the web endpoint uses <c>AgentPresence.Online</c>).
    /// </summary>
    public void TakeOver(PhaseName phase) => services.Db.InTransaction(() =>
    {
        if (!Phases.Reviewable.Contains(phase)) throw new WorkflowException($"{phase.Text()} cannot be taken over.");
        var row = services.Phases.Get(phase);
        if (row.Status is not (PhaseStatus.Drafting or PhaseStatus.Reworking))
            throw new WorkflowException($"{phase.Text()} is {EnumText.ToText(row.Status)}; only a phase Claude is drafting or reworking can be taken over.");
        if (row.CurrentVersion is not int version) throw new WorkflowException($"{phase.Text()} has no version to review yet.");
        SetStatus(phase, PhaseStatus.AwaitingReview);
        Publish("log", new
        {
            level = "info",
            message = $"You took over {phase.Text()}: Claude's pending {(row.Status == PhaseStatus.Drafting ? "first draft" : "rework")} was "
                      + $"discarded and v{version} awaits your review.",
        });
    });

    /// <summary>Throws WorkflowException(details = blockers). Records approved_fingerprint = "&lt;src&gt;:&lt;tgt&gt;".
    /// <para>Ruling 194 (open item 1): <paramref name="seenVersion"/> is the version the reviewer saw. Unless it is still the current
    /// version the approval is refused with <see cref="WorkflowException.StaleVersion"/> - checked here, inside the transaction that
    /// approves, so a version stored a moment earlier can never be signed off by a screen that never displayed it.</para></summary>
    public void Approve(PhaseName phase, int seenVersion) => services.Db.InTransaction(() =>
    {
        if (!Phases.Reviewable.Contains(phase)) throw new WorkflowException($"{phase.Text()} is not approved from a review screen.");
        var row = services.Phases.Get(phase);
        if (row.Status != PhaseStatus.AwaitingReview)
            throw new WorkflowException($"{phase.Text()} is {EnumText.ToText(row.Status)}; only a phase awaiting review can be approved.");
        EnsureCurrentVersion(phase, seenVersion);
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

    /// <summary>Ruling 194: throws <see cref="WorkflowException.StaleVersion"/> unless <paramref name="seenVersion"/> is the phase's
    /// current version. <see cref="Approve"/> runs it in its own transaction; the approve endpoint also runs it first, so a stale
    /// screen is told to reload before any slower approval guard runs.</summary>
    public void EnsureCurrentVersion(PhaseName phase, int seenVersion)
    {
        var current = services.Phases.Get(phase).CurrentVersion;
        if (current == seenVersion) return;
        if (current is null || seenVersion > current)   // nothing newer arrived: the screen named a version that is not current
            throw new WorkflowException(
                $"v{seenVersion} is not the current version ({(current is int c ? $"v{c}" : "none")}) - reload and review it.",
                null, WorkflowException.StaleVersion);
        throw new WorkflowException(
            $"You approved v{seenVersion} of {phase.Text()}, but its current version is "
            + (current is int v ? $"v{v}" : "(none)") + ". A newer version arrived - review it first.",
            null, WorkflowException.StaleVersion);
    }

    /// <summary>
    /// approved → awaiting_review at the approved version; later phases up to Ready → stale; Transfer and Complete back to pending.
    /// <para>Ruling 185: allowed before the first run and after a completed or cancelled one - never while a run is running or
    /// paused. A failed run is cancelled by the caller first (<c>TransferService.CancelFailedRunAsync</c>, which restores the plan's
    /// PreSql); here it is refused, so no path can leave a failed run's disabled constraints behind in silence. Earlier runs and their
    /// final reports stay where they are; after re-approval, Execute starts a new run.</para>
    /// </summary>
    public void Reopen(PhaseName phase) => services.Db.InTransaction(() =>
    {
        if (WhyNotReopen(phase, failedRunWillBeCancelled: false) is { } why) throw new WorkflowException(why);
        var row = services.Phases.Get(phase);
        services.Phases.ClearApproval(phase);
        if (row.ApprovedVersion is int approved) services.Phases.SetCurrentVersion(phase, approved);
        SetStatus(phase, PhaseStatus.AwaitingReview);
        foreach (var later in Order.Where(p => p > phase && p <= PhaseName.Ready)) MarkStale(later);
        ResetTransfer();
    });

    /// <summary>Why <see cref="Reopen"/> would refuse <paramref name="phase"/> now, or null. With
    /// <paramref name="failedRunWillBeCancelled"/> a failed latest run does not count against it - the endpoint cancels it first.</summary>
    public string? WhyNotReopen(PhaseName phase, bool failedRunWillBeCancelled)
    {
        if (!Phases.Reviewable.Contains(phase)) return $"{phase.Text()} cannot be reopened.";
        if (services.Phases.Get(phase).Status != PhaseStatus.Approved) return $"{phase.Text()} is not approved.";
        return UpstreamLock(failedRunWillBeCancelled) is { } locked ? $"{phase.Text()} cannot be reopened: {locked}" : null;
    }

    /// <summary>
    /// Ruling 185: why the plan and the connections cannot change right now, or null when they can - before the first run, and after
    /// a run that completed or was cancelled. A running or paused run holds the plan it is loading (Pause, then Cancel, first); a failed
    /// one still has the plan's PreSql in force in the target, which only a cancel restores.
    /// </summary>
    public string? UpstreamLock(bool failedRunWillBeCancelled = false)
    {
        if (services.Phases.Get(PhaseName.Transfer).Status == PhaseStatus.Pending) return null;
        (long Id, string Status)? run;
        try
        {
            run = services.Db.Query("SELECT id, status FROM transfer_run ORDER BY id DESC LIMIT 1",
                r => ((long Id, string Status)?)(r.GetInt64(0), r.GetString(1))).FirstOrDefault();
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            return "the latest transfer run could not be read (" + ex.Message + ").";
        }
        if (run is not { } latest) return "a transfer is starting.";
        return latest.Status switch
        {
            "completed" or "cancelled" => null,
            "failed" when failedRunWillBeCancelled => null,
            "failed" => $"transfer run {latest.Id} failed and the plan's pre-load SQL is still in force in the target. Cancel the run "
                        + "first (Cancel restores what that SQL disabled), or reopen from the review screen, which cancels it for you.",
            "paused" => $"transfer run {latest.Id} is paused. Cancel it first (Cancel restores what the plan's pre-load SQL disabled), "
                        + "or resume it to the end.",
            _ => $"transfer run {latest.Id} is {latest.Status}. Pause it and cancel it first.",
        };
    }

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
        if (services.Feedback.CloseOpenAndDrafts(phase, row.CurrentVersion) > 0) Publish("feedback_changed", new { phase });
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

    private void EnsureUpstreamOpen(string what)
    {
        if (UpstreamLock() is { } why) throw new WorkflowException($"{what}: {why}");
    }

    /// <summary>
    /// Ruling 185: after an upstream change following a run, Transfer and Complete go back to pending so the re-approved plan starts a
    /// new run. Nothing is deleted - the earlier runs, their checkpoints' history and their final reports (Complete artifacts, and
    /// Complete's current version) stay in the workspace.
    /// </summary>
    private void ResetTransfer()
    {
        foreach (var phase in new[] { PhaseName.Transfer, PhaseName.Complete })
        {
            var row = services.Phases.Get(phase);
            if (row.Status == PhaseStatus.Pending) continue;
            services.Phases.ClearApproval(phase);
            SetStatus(phase, PhaseStatus.Pending);
        }
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
