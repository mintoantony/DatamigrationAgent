using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Mapping;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.SqlGen;

public sealed class SqlModuleTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly DbmServices _services;
    private readonly SqlModule _module = new();

    public SqlModuleTests()
    {
        _services = _workspace.OpenServices();
        _services.Catalog.Save(Side.Src, SampleCatalogs.Source(), "src-fp");
        _services.Catalog.Save(Side.Tgt, SampleCatalogs.Target(), "tgt-fp");
        var mv = _services.Artifacts.NextVersion(PhaseName.Mapping);
        _services.Artifacts.Add(PhaseName.Mapping, mv, Json.Serialize(SampleMappings.Approved()), "human", "approved mapping");
        _services.Phases.SetApproved(PhaseName.Mapping, mv, null);
    }

    public void Dispose() => _workspace.Dispose();

    /// <summary>The generated sample plan, carrying the evidence a live validation would have stored (Ruling 204): the fixture stands
    /// for a validated version unless a test clears <see cref="SqlPlanPayload.Validation"/>.</summary>
    private static SqlPlanPayload Plan(MappingPayload? mapping = null)
    {
        var plan = SqlGenerator.Generate(mapping ?? SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target());
        plan.Validation = Evidence;
        return plan;
    }

    private static readonly SqlValidation Evidence = SqlValidation.From(new DateTimeOffset(2026, 9, 18, 9, 30, 0, TimeSpan.Zero), true);

    /// <summary>Review N3: the evidence time is written like every other stored time - UTC, ISO-8601 "O" - not System.Text.Json's
    /// default, which drops the fraction digits.</summary>
    [Fact]
    public void Validation_evidence_time_is_written_in_the_O_format()
    {
        var json = Json.Serialize(SqlValidation.From(new DateTimeOffset(2026, 9, 18, 9, 30, 0, TimeSpan.Zero), true));
        Assert.True(json.Contains("\"at\":\"2026-09-18T09:30:00.0000000+00:00\"", StringComparison.Ordinal), "evidence time is not written with \"O\": " + json);
    }

    /// <summary>Ruling 204: a version without validation evidence is NOT validated, whatever its warnings say - here it carries no
    /// "live validation skipped" line at all, which is exactly the version the marker rule used to wave through. An old record, stored
    /// before the evidence existed, has no "validation" field and reads the same way.</summary>
    [Fact]
    public void A_version_without_validation_evidence_is_not_validated_whatever_its_warnings_say()
    {
        var plan = Plan();
        plan.Validation = null;
        Assert.DoesNotContain(plan.Warnings, w => w.StartsWith(SqlPlanSource.SkippedPrefix, StringComparison.Ordinal));   // no marker
        var stored = Json.Serialize(plan);
        Assert.DoesNotContain("\"validation\"", stored, StringComparison.Ordinal);   // an old record looks exactly like this
        var ctx = Ctx(plan);

        var blockers = _module.ApprovalBlockers(ctx, JsonNode.Parse(stored)!);

        Assert.True(blockers.Contains(SqlModule.NotValidatedBlocker + SqlModule.NoEvidence),
            "a version with no validation evidence was not blocked as not validated: [" + string.Join("; ", blockers) + "]");
        Assert.Empty(_module.ApprovalBlockers(ctx, Json.ToNode(Plan())));   // the same plan with evidence is approvable
    }

    /// <summary>Ruling 204, every writer that validates: an offline run (SqlModule.Validate - agent patch, dry-run and UI edit) must not
    /// pass its base's evidence on, because it did not validate THIS SQL. Pinned through both stored doors, agent and human.</summary>
    [Fact]
    public void An_offline_patch_or_edit_stores_no_validation_evidence_even_when_its_base_had_some()
    {
        var plan = Plan();
        var payload = Json.ToNode(plan);
        payload["tasks"]!["T04"]!["sourceQuery"] = plan.Tasks["T04"].SourceQuery + "\nWHERE s.[CITY] <> ''";
        Assert.True(_module.Validate(Ctx(plan), payload).Ok);
        Assert.True(payload["validation"] is null, "an offline patch kept its base's validation evidence: " + payload["validation"]?.ToJsonString());

        foreach (var human in new[] { false, true })
        {
            Ctx(plan);
            _services.Phases.SetStatus(PhaseName.Sql, human ? PhaseStatus.AwaitingReview : PhaseStatus.Drafting);
            var current = _services.Phases.Get(PhaseName.Sql).CurrentVersion!.Value;
            var patch = new Dbm.Core.Patching.Patch("sql", current,
                [new Dbm.Core.Patching.PatchOp("replace", "/tasks/T04/sourceQuery", JsonValue.Create(plan.Tasks["T04"].SourceQuery + "\nWHERE 1 = 1"))], []);
            var result = human ? _services.Workflow.HumanEdit(patch) : _services.Workflow.ApplyPatch(patch);
            Assert.True(result.Ok, string.Join("; ", result.Errors));
            var stored = Json.Deserialize<SqlPlanPayload>(_services.Artifacts.Get(PhaseName.Sql, result.Version!.Value)!.PayloadJson);
            Assert.True(stored.Validation is null, (human ? "a UI edit" : "an agent patch") + " stored offline kept its base's validation evidence");
        }
    }

    private ModuleContext Ctx(SqlPlanPayload plan, params FeedbackRow[] feedback)
    {
        var v = _services.Artifacts.NextVersion(PhaseName.Sql);
        var row = _services.Artifacts.Add(PhaseName.Sql, v, Json.Serialize(plan), "script", "draft");
        _services.Phases.SetCurrentVersion(PhaseName.Sql, v);
        return new ModuleContext { Services = _services, Current = row, OpenFeedback = feedback };
    }

    private static FeedbackRow Fb(long id, string? anchor, string text) =>
        new(id, PhaseName.Sql, 0, anchor, text, FeedbackStatus.Open, null, null, DateTimeOffset.UtcNow);

    [Fact]
    public void Identity_matches_the_contract()
    {
        Assert.Equal(PhaseName.Sql, _module.Phase);
        Assert.Equal("sql-engineer", _module.Agent);
        Assert.Equal("sqlgen", _module.JobKind);
        Assert.IsType<SqlModule>(_services.Modules[PhaseName.Sql]);
    }

    [Fact]
    public void NeedsAgent_only_for_errors_or_unreviewed_staging_tasks()
    {
        Assert.False(_module.NeedsAgent(Json.ToNode(Plan())));

        var withError = Plan();
        withError.Tasks["T05"].Errors.Add("sourceQuery: Invalid column name 'X'.");
        Assert.True(_module.NeedsAgent(Json.ToNode(withError)));

        var mapping = SampleMappings.Approved();
        mapping.Tables["app.Addresses"].Kind = "lookup";
        var lookup = Plan(mapping);
        Assert.True(_module.NeedsAgent(Json.ToNode(lookup)));
        lookup.Tasks["T04"].Custom = true;
        Assert.False(_module.NeedsAgent(Json.ToNode(lookup)));
    }

    [Fact]
    public void Draft_packet_details_tasks_needing_work_and_summarises_the_rest()
    {
        var plan = Plan();
        plan.Tasks["T05"].Errors.Add("sourceQuery: Invalid column name 'X'.");
        var data = _module.BuildPacket(Ctx(plan), PacketMode.Draft).AsObject();

        var item = Assert.Single(data["work"]!.AsArray())!.AsObject();
        Assert.Equal("T05", (string?)item["id"]);
        Assert.Equal(plan.Tasks["T05"].SourceQuery, (string?)item["sourceQuery"]);
        Assert.Equal("merge", (string?)item["tableMap"]!["kind"]);
        Assert.Contains("Comment nvarchar(200) NULL", item["targetColumns"]!.AsArray().Select(n => (string?)n));
        Assert.Contains("OrderId int NOT NULL IDENTITY", item["targetColumns"]!.AsArray().Select(n => (string?)n));
        Assert.Equal("/tasks/T05/sourceQuery", (string?)item["paths"]!["sourceQuery"]);
        Assert.Equal("sourceQuery: Invalid column name 'X'.", (string?)item["errors"]![0]);

        var others = data["others"]!.AsArray();
        Assert.Equal(5, others.Count);
        Assert.All(others, o => Assert.Null(o!["sourceQuery"]));
        Assert.Equal(["T01", "T02", "T03", "T04", "T06"], others.Select(o => (string?)o!["id"]));
        Assert.NotNull(data["legend"]!["sourceQuery"]);
        Assert.Equal("ALTER TABLE [app].[Customers] NOCHECK CONSTRAINT [FK_Customers_PrimaryAddress];", (string?)data["pre"]![0]);
        Assert.Null(data["context"]);
    }

    [Fact]
    public void Draft_packet_includes_lookup_tasks_in_full()
    {
        var mapping = SampleMappings.Approved();
        mapping.Tables["app.Addresses"].Kind = "lookup";
        var data = _module.BuildPacket(Ctx(Plan(mapping)), PacketMode.Draft).AsObject();
        var item = Assert.Single(data["work"]!.AsArray())!.AsObject();
        Assert.Equal("T04", (string?)item["id"]);
        Assert.StartsWith("CREATE TABLE #stg (", (string?)item["stagingDdl"]);
        Assert.EndsWith("FROM #stg;", (string?)item["mergeSql"]);
    }

    [Fact]
    public void Packet_servers_block_carries_server_meta_but_never_any_part_of_a_connection_string()
    {
        const string srcCs = "Server=src-secret-host;Database=LegacySecretDb;User ID=legacy_user;Password=Src-P4ss-word;TrustServerCertificate=True";
        const string tgtCs = "Server=tgt-secret-host;Database=ShopSecretDb;User ID=shop_user;Password=Tgt-P4ss-word;TrustServerCertificate=True";
        _services.Connections.Save(Side.Src, srcCs, FakeServices.Meta("src-secret-host", "LegacySecretDb") with { AuthSummary = "sql login legacy_user" });
        _services.Connections.Save(Side.Tgt, tgtCs, FakeServices.Meta("tgt-secret-host", "ShopSecretDb") with { AuthSummary = "sql login shop_user" });

        var data = _module.BuildPacket(Ctx(Plan()), PacketMode.Draft).AsObject();

        var src = data["servers"]!["src"]!.AsObject();
        Assert.Equal("17.0.1000.7", (string?)src["version"]);
        Assert.Equal(17, (int?)src["major"]);
        Assert.Equal("Developer Edition (64-bit)", (string?)src["edition"]);
        Assert.Equal(170, (int?)src["compatLevel"]);
        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", (string?)src["collation"]);
        Assert.Equal(["version", "major", "edition", "compatLevel", "collation"], src.Select(p => p.Key));
        Assert.Equal(["version", "major", "edition", "compatLevel", "collation"], data["servers"]!["tgt"]!.AsObject().Select(p => p.Key));

        var text = data.ToJsonString(Json.Options);
        foreach (var part in new[] { "secret-host", "SecretDb", "legacy_user", "shop_user", "P4ss", "Password", "TrustServerCertificate", "sql login" })
            Assert.DoesNotContain(part, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rework_packet_gives_context_per_feedback_anchor()
    {
        var plan = Plan();   // T04 = app.Addresses; listing line 9 = "FROM [dbo].[ADDR] AS s"
        var ctx = Ctx(plan, Fb(12, "sql:T04:9", "Skip addresses without a city"), Fb(13, "task:T02", "Load customers last"), Fb(14, null, "Looks good overall"));
        var data = _module.BuildPacket(ctx, PacketMode.Rework).AsObject();
        var context = data["context"]!.AsObject();

        Assert.Equal(9, (int?)context["12"]!["line"]);
        Assert.Equal("source", (string?)context["12"]!["section"]);
        Assert.Contains("   9 source  >> FROM [dbo].[ADDR] AS s", (string?)context["12"]!["listing"]);
        Assert.Contains("   8 source  |      s.[ADDR_ID] AS [__k0]", (string?)context["12"]!["listing"]);
        Assert.Equal("T04", (string?)context["12"]!["task"]!["id"]);
        Assert.Equal("T02", (string?)context["13"]!["task"]!["id"]);
        Assert.Equal("general", (string?)context["14"]!["anchor"]);
        Assert.Empty(data["work"]!.AsArray());
        Assert.Equal(["T01", "T03", "T05", "T06"], data["others"]!.AsArray().Select(o => (string?)o!["id"]));
    }

    [Fact]
    public void Validate_marks_edited_tasks_custom_and_recomputes_the_count_query()
    {
        var plan = Plan();
        var ctx = Ctx(plan);
        var payload = Json.ToNode(plan);
        var edited = plan.Tasks["T04"].SourceQuery + "\nWHERE s.[CITY] <> ''";
        payload["tasks"]!["T04"]!["sourceQuery"] = edited;

        var check = _module.Validate(ctx, payload);

        Assert.True(check.Ok, string.Join("; ", check.Errors));
        Assert.Contains(OfflineMarker, check.Warnings);
        Assert.True((bool)payload["tasks"]!["T04"]!["custom"]!);
        Assert.False((bool)payload["tasks"]!["T05"]!["custom"]!);
        Assert.Equal(SqlGenerator.CountSql(edited), (string?)payload["tasks"]!["T04"]!["countSql"]);
    }

    [Fact]
    public void Validate_rejects_shape_errors_when_offline()
    {
        var plan = Plan();
        var ctx = Ctx(plan);
        var payload = Json.ToNode(plan);
        payload["tasks"]!["T04"]!["mode"] = "bulk";
        payload["tasks"]!["T05"]!["preSql"] = new JsonArray("SELECT 1\nGO");
        var check = _module.Validate(ctx, payload);
        Assert.False(check.Ok);
        Assert.Contains("T04: mode 'bulk' must be direct or staging_merge", check.Errors);
        Assert.Contains("T05: preSql[0]: GO batch separators are not allowed", check.Errors);
    }

    [Fact]
    public void Validate_rejects_explicit_nulls_instead_of_crashing()
    {
        var plan = Plan();
        var ctx = Ctx(plan);
        var payload = Json.ToNode(plan);
        payload["tasks"]!["T04"]!["sourceQuery"] = null;
        payload["tasks"]!["T05"] = null;

        var check = _module.Validate(ctx, payload);

        Assert.False(check.Ok);
        Assert.Contains("T04: only stagingDdl, mergeSql, chunkSize and mappingHash may be null", check.Errors);
        Assert.Contains("T05: task must not be null", check.Errors);
        Assert.False(_module.Validate(ctx, new JsonArray()).Ok);

        var nullElement = Json.ToNode(plan);
        nullElement["order"]!.AsArray().Add(null);
        Assert.Equal(["preSql, postSql, order, warnings and errors must not contain null"], _module.Validate(ctx, nullElement).Errors);
    }

    [Fact]
    public void Human_edit_stores_the_custom_flag_set_by_validate()
    {
        var plan = Plan();
        Ctx(plan);
        _services.Phases.SetStatus(PhaseName.Sql, PhaseStatus.AwaitingReview);
        var current = _services.Phases.Get(PhaseName.Sql).CurrentVersion!.Value;
        var edited = plan.Tasks["T04"].SourceQuery + "\nWHERE s.[CITY] <> ''";

        var result = _services.Workflow.HumanEdit(new Dbm.Core.Patching.Patch("sql", current,
            [new Dbm.Core.Patching.PatchOp("replace", "/tasks/T04/sourceQuery", JsonValue.Create(edited))], []));

        Assert.True(result.Ok, string.Join("; ", result.Errors));
        var stored = Json.Deserialize<SqlPlanPayload>(_services.Artifacts.Get(PhaseName.Sql, result.Version!.Value)!.PayloadJson);
        Assert.True(stored.Tasks["T04"].Custom);
        Assert.Equal(SqlGenerator.CountSql(edited), stored.Tasks["T04"].CountSql);
    }

    [Fact]
    public void Approval_is_blocked_by_errors_and_a_broken_order()
    {
        var plan = Plan();
        var ctx = Ctx(plan);
        Assert.Empty(_module.ApprovalBlockers(ctx, Json.ToNode(plan)));

        plan.Tasks["T05"].Errors.Add("sourceQuery: Invalid column name 'X'.");
        plan.Order.RemoveAt(0);
        var blockers = _module.ApprovalBlockers(ctx, Json.ToNode(plan));
        Assert.Contains("T05 (app.Orders): 1 validation error(s)", blockers);
        Assert.Contains("order must list every task id exactly once", blockers);
    }

    /// <summary>The fixture saves both catalogs but no connection.</summary>
    private const string OfflineMarker = "live validation skipped: source connection, target connection missing";

    private static List<string> Stored(JsonNode payload, string? task = null) =>
        (task is null ? payload["warnings"] : payload["tasks"]![task]!["warnings"])!.AsArray().Select(n => (string)n!).ToList();

    /// <summary>H1: offline, nothing compiled the SQL — the stored plan says so, naming what is missing, and cannot be approved.
    /// Also pins that the ownership check runs BEFORE Validate writes the marker: re-validating the stored version passes.</summary>
    [Fact]
    public void Offline_validation_records_that_it_did_not_run_and_blocks_approval()
    {
        var plan = Plan();
        var ctx = Ctx(plan);
        var payload = Json.ToNode(plan);
        payload["tasks"]!["T04"]!["sourceQuery"] = plan.Tasks["T04"].SourceQuery + "\nWHERE s.[CITY] <> ''";

        var check = _module.Validate(ctx, payload);

        Assert.True(check.Ok, string.Join("; ", check.Errors));
        Assert.Contains(OfflineMarker, check.Warnings);
        Assert.Equal(OfflineMarker, Stored(payload).Last());
        Assert.Empty(plan.Tasks["T04"].Errors);   // "0 errors" — which is exactly why the marker must block
        Assert.Contains("plan is not validated: " + OfflineMarker, _module.ApprovalBlockers(ctx, payload));
        Assert.Empty(_module.ApprovalBlockers(ctx, Json.ToNode(plan)));

        // The stored version (marker included) is the base of the next patch: its own marker is not "a patch change",
        // and it is re-derived, never duplicated.
        var storedCtx = Ctx(Json.FromNode<SqlPlanPayload>(payload));
        var next = JsonNode.Parse(payload.ToJsonString())!;
        next["tasks"]!["T05"]!["sourceQuery"] = plan.Tasks["T05"].SourceQuery + "\nWHERE 1 = 1";
        var again = _module.Validate(storedCtx, next);
        Assert.True(again.Ok, string.Join("; ", again.Errors));
        Assert.Single(Stored(next), w => w.StartsWith("live validation skipped: ", StringComparison.Ordinal));
    }

    [Fact]
    public void Offline_marker_names_every_missing_input()
    {
        using var bare = new TestWorkspace();
        var s = bare.OpenServices();
        var plan = Plan();
        var row = s.Artifacts.Add(PhaseName.Sql, s.Artifacts.NextVersion(PhaseName.Sql), Json.Serialize(plan), "script", "draft");
        var payload = Json.ToNode(plan);

        var check = _module.Validate(new ModuleContext { Services = s, Current = row, OpenFeedback = [] }, payload);

        Assert.Contains("live validation skipped: source connection, target connection, target catalog missing", check.Warnings);
        Assert.Contains("plan is not validated: live validation skipped: source connection, target connection, target catalog missing",
            _module.ApprovalBlockers(new ModuleContext { Services = s, Current = row, OpenFeedback = [] }, payload));
    }

    /// <summary>H1/M1 + ruling 44fix-Q1: errors, warnings and custom are engine-authored evidence. Exact comparison — ordinal,
    /// order-sensitive, whole list.</summary>
    [Theory]
    [InlineData("clear task errors", "T05: errors is engine-owned (recorded by validation); a patch may not change it")]
    [InlineData("add task error", "T04: errors is engine-owned (recorded by validation); a patch may not change it")]
    [InlineData("clear plan errors", "plan: errors is engine-owned (recorded by validation); a patch may not change it")]
    [InlineData("delete generator warning", "T05: warnings is engine-owned (recorded by the generator and validation); a patch may not change it")]
    [InlineData("reword warning by a trailing space", "T05: warnings is engine-owned (recorded by the generator and validation); a patch may not change it")]
    [InlineData("reorder plan warnings", "plan: warnings is engine-owned (recorded by the generator and validation); a patch may not change it")]
    [InlineData("author the offline marker", "plan: warnings is engine-owned (recorded by the generator and validation); a patch may not change it")]
    [InlineData("set custom on a skeleton", "T04: custom is engine-owned (set when the task's SQL changes); a patch may not change it")]
    [InlineData("clear custom", "T02: custom is engine-owned (set when the task's SQL changes); a patch may not change it")]
    [InlineData("new task with errors", "T07: errors is engine-owned (recorded by validation); a patch may not change it")]
    [InlineData("new task with warnings", "T07: warnings is engine-owned (recorded by the generator and validation); a patch may not change it")]
    [InlineData("forge validation evidence", "plan: validation is engine-owned (recorded by validation); a patch may not change it")]
    [InlineData("erase validation evidence", "plan: validation is engine-owned (recorded by validation); a patch may not change it")]
    public void A_patch_may_not_author_or_erase_validation_evidence(string change, string expected)
    {
        var mapping = SampleMappings.Approved();
        mapping.Tables["app.Addresses"].Kind = "lookup";   // T04 = generated staging skeleton
        var plan = Plan(mapping);
        plan.Tasks["T05"].Errors.Add("sourceQuery: Invalid column name 'X'.");
        plan.Errors.Add("target connection failed: timeout");
        plan.Tasks["T02"].Custom = true;
        plan.Warnings.Add("a second generator notice");   // reordering needs two lines
        Assert.NotEmpty(plan.Tasks["T05"].Warnings);
        var ctx = Ctx(plan);
        var payload = Json.ToNode(plan);
        var newTask = Json.ToNode(plan.Tasks["T06"]).AsObject();
        newTask["target"] = "app.OrderLinesCopy";
        switch (change)
        {
            case "clear task errors": payload["tasks"]!["T05"]!["errors"] = new JsonArray(); break;
            case "add task error": payload["tasks"]!["T04"]!["errors"] = new JsonArray("agent says so"); break;
            case "clear plan errors": payload["errors"] = new JsonArray(); break;
            case "delete generator warning": payload["tasks"]!["T05"]!["warnings"]!.AsArray().RemoveAt(0); break;
            case "reword warning by a trailing space":
                payload["tasks"]!["T05"]!["warnings"]![0] = plan.Tasks["T05"].Warnings[0] + " "; break;
            case "reorder plan warnings": payload["warnings"] = new JsonArray(Enumerable.Reverse(plan.Warnings).Select(w => (JsonNode?)w).ToArray()); break;
            case "author the offline marker": payload["warnings"]!.AsArray().Add(OfflineMarker); break;
            case "set custom on a skeleton": payload["tasks"]!["T04"]!["custom"] = true; break;
            case "clear custom": payload["tasks"]!["T02"]!["custom"] = false; break;
            case "new task with errors":
                newTask["errors"] = new JsonArray("x"); newTask["warnings"] = new JsonArray();
                payload["tasks"]!["T07"] = newTask; payload["order"]!.AsArray().Add("T07"); break;
            case "new task with warnings":
                newTask["errors"] = new JsonArray(); newTask["warnings"] = new JsonArray("x");
                payload["tasks"]!["T07"] = newTask; payload["order"]!.AsArray().Add("T07"); break;
            case "forge validation evidence": payload["validation"] = new JsonObject { ["at"] = "2030-01-01T00:00:00+00:00", ["ok"] = true }; break;
            case "erase validation evidence": payload.AsObject().Remove("validation"); break;
            default: throw new ArgumentException(change);
        }

        var check = _module.Validate(ctx, payload);

        Assert.False(check.Ok, change + ": a patch that changes engine-owned evidence was accepted");
        Assert.Equal([expected], check.Errors);
    }

    [Fact]
    public void Patch_rejection_happens_before_anything_is_normalised()
    {
        var plan = Plan();
        var ctx = Ctx(plan);
        var payload = Json.ToNode(plan);
        payload["tasks"]!["T05"]!["warnings"] = new JsonArray();
        payload["tasks"]!["T04"]!["sourceQuery"] = plan.Tasks["T04"].SourceQuery + "\nWHERE 1 = 1";
        var before = payload.ToJsonString();

        Assert.False(_module.Validate(ctx, payload).Ok);
        Assert.Equal(before, payload.ToJsonString());
    }

    [Fact]
    public void A_custom_task_stays_custom_when_a_patch_leaves_its_sql_alone()
    {
        var plan = Plan();
        plan.Tasks["T02"].Custom = true;
        var ctx = Ctx(plan);
        var payload = Json.ToNode(plan);
        payload["tasks"]!["T04"]!["sourceQuery"] = plan.Tasks["T04"].SourceQuery + "\nWHERE 1 = 1";

        Assert.True(_module.Validate(ctx, payload).Ok);
        Assert.True((bool)payload["tasks"]!["T02"]!["custom"]!);
    }

    [Fact]
    public void A_new_clean_task_is_accepted_and_marked_custom()
    {
        var plan = Plan();
        var ctx = Ctx(plan);
        var payload = Json.ToNode(plan);
        var newTask = Json.ToNode(plan.Tasks["T06"]).AsObject();
        newTask["errors"] = new JsonArray();
        newTask["warnings"] = new JsonArray();
        payload["tasks"]!["T07"] = newTask;
        payload["order"]!.AsArray().Add("T07");

        var check = _module.Validate(ctx, payload);

        Assert.True(check.Ok, string.Join("; ", check.Errors));
        Assert.True((bool)payload["tasks"]!["T07"]!["custom"]!);
    }

    /// <summary>Ruling 44fix-Q1 condition 1: removing a whole task stays legal; its engine lines leave with it.</summary>
    [Fact]
    public void Deleting_a_whole_task_is_still_allowed()
    {
        var plan = Plan();
        Assert.NotEmpty(plan.Tasks["T05"].Warnings);
        var ctx = Ctx(plan);
        var payload = Json.ToNode(plan);
        payload["tasks"]!.AsObject().Remove("T05");
        payload["order"]!.AsArray().RemoveAt(plan.Order.IndexOf("T05"));

        var check = _module.Validate(ctx, payload);

        Assert.True(check.Ok, string.Join("; ", check.Errors));
        Assert.Null(payload["tasks"]!["T05"]);
    }

    /// <summary>Ruling 63 (N1): deleting a task is allowed, but its evidence does not vanish with it. The re-review's rename route —
    /// delete T05, add an identical T07 with no warnings — leaves every error and non-validate: warning of T05 as a plan warning,
    /// verbatim but flattened, in the stored plan and in the dry-run warnings.</summary>
    [Fact]
    public void Removing_a_task_carries_its_evidence_into_plan_warnings()
    {
        var plan = Plan();
        var t05 = plan.Tasks["T05"];
        Assert.Contains("target has 1 trigger(s); not fired unless FireTriggers", t05.Warnings);
        t05.Errors.Add("sourceQuery: Invalid column name 'X'.");
        t05.Warnings.Add(SqlValidator.WarningPrefix + "Comment: varchar(500) -> nvarchar(200)");   // recomputed by validation: not carried
        t05.Warnings.Add("line one\nline two");
        var ctx = Ctx(plan);
        var payload = Json.ToNode(plan);
        var copy = Json.ToNode(t05).AsObject();
        copy["errors"] = new JsonArray();
        copy["warnings"] = new JsonArray();
        payload["tasks"]!.AsObject().Remove("T05");
        payload["tasks"]!["T07"] = copy;
        payload["order"]![plan.Order.IndexOf("T05")] = "T07";

        var check = _module.Validate(ctx, payload);

        Assert.True(check.Ok, string.Join("; ", check.Errors));
        List<string> expected =
        [
            "removed task T05 carried: sourceQuery: Invalid column name 'X'.",
            .. t05.Warnings.Where(w => !w.StartsWith(SqlValidator.WarningPrefix, StringComparison.Ordinal) && w != "line one\nline two")
                .Select(w => "removed task T05 carried: " + w),
            "removed task T05 carried: line one line two",
        ];
        Assert.Contains("removed task T05 carried: target has 1 trigger(s); not fired unless FireTriggers", expected);
        Assert.Equal(expected, Stored(payload).Where(w => w.StartsWith("removed task ", StringComparison.Ordinal)));
        Assert.Equal(expected, check.Warnings.Where(w => w.StartsWith("removed task ", StringComparison.Ordinal)));
    }

    [Fact]
    public void Removing_a_task_without_evidence_carries_nothing()
    {
        var plan = Plan();
        plan.Tasks["T03"].Warnings.Clear();
        Assert.Empty(plan.Tasks["T03"].Errors);
        var ctx = Ctx(plan);
        var payload = Json.ToNode(plan);
        payload["tasks"]!.AsObject().Remove("T03");
        payload["order"]!.AsArray().RemoveAt(plan.Order.IndexOf("T03"));

        var check = _module.Validate(ctx, payload);

        Assert.True(check.Ok, string.Join("; ", check.Errors));
        Assert.Equal([.. plan.Warnings, OfflineMarker], Stored(payload));
        Assert.Equal([OfflineMarker], check.Warnings);
    }

    /// <summary>M2: a task missing from order would vanish from the script pack and the run; an order id with no task, or listed
    /// twice, is equally wrong. Each is its own error.</summary>
    [Fact]
    public void Validate_checks_order_against_tasks_in_both_directions()
    {
        var plan = Plan();
        var ctx = Ctx(plan);
        var payload = Json.ToNode(plan);
        var order = payload["order"]!.AsArray();
        order.RemoveAt(plan.Order.IndexOf("T03"));   // T01 T02 T04 T05 T06
        order.Insert(2, "T99");                      // T01 T02 T99 T04 T05 T06
        order.Add("T01");                            // ... T01

        var check = _module.Validate(ctx, payload);

        Assert.False(check.Ok);
        Assert.Contains("order[2]: T99 is not a task", check.Errors);
        Assert.Contains("order[6]: T01 is listed more than once", check.Errors);
        Assert.Contains("T03: task is missing from order", check.Errors);
        Assert.Equal(3, check.Errors.Count);
    }

    [Fact]
    public void Summary_counts_tasks_custom_and_warnings()
    {
        var plan = Plan();
        plan.Tasks["T04"].Custom = true;
        Assert.Equal($"6 tasks, 1 custom, {plan.WarningCount()} warnings", _module.Summarize(Json.ToNode(plan)));
        plan.Tasks["T04"].Errors.Add("x");
        Assert.EndsWith(", 1 errors", _module.Summarize(Json.ToNode(plan)));
    }

    [Theory]
    [InlineData("task:T04", "T04", null)]
    [InlineData("sql:T04:12", "T04", 12)]
    [InlineData("sql:T04:x", null, null)]
    [InlineData("tablemap:app.Orders", null, null)]
    [InlineData(null, null, null)]
    public void ParseAnchor_understands_task_and_line_anchors(string? anchor, string? task, int? line)
    {
        var (t, l) = SqlModule.ParseAnchor(anchor);
        Assert.Equal(task, t);
        Assert.Equal(line, l);
    }

    /// <summary>The playbook must carry the T4.3 fix-round contract: globalWarnings (and the single-task "not checked" line) and the
    /// current safety claim — never the voided "nothing is ever executed on either database".</summary>
    [Fact]
    public void Agent_playbook_carries_the_current_validation_contract()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "plugins", "db-migrate", "agents", "sql-engineer.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var text = File.ReadAllText(Path.Combine(dir!.FullName, "plugins", "db-migrate", "agents", "sql-engineer.md")).Replace("\r\n", "\n");

        Assert.StartsWith("---\nname: sql-engineer\n", text);
        Assert.Contains("\ntools: Bash, Read, Write\n", text);
        Assert.Contains("\nmodel: inherit\n", text);
        foreach (var phrase in new[] { "dbm sql validate --patch <patchPath>", "dbm apply <patchPath> --dry-run", "\"globalWarnings\":[…]",
                     $"\"{SqlValidator.SingleTaskGlobalWarning}\"", SqlValidator.NotCheckedMarker.Trim(), "No plan SQL — generated or agent-written — is ever executed",
                     "session-local temp table in tempdb, dropped at connection close", "state.db", "feedbackId", "bare carriage return" })
            Assert.Contains(phrase, text);
        Assert.DoesNotContain("nothing is ever executed", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PARSEONLY", text, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- Ruling 57: a lone CR in any SQL body is an error

    private const string Advice = " (SQL Server treats it as a line break; use CRLF or LF)";
    private const string Hidden = "-- note\rDELETE FROM app.Customers";

    private static string CrError(string scope, string field, int line) =>
        $"{scope}: {field}: bare carriage return at line {line}{Advice}";

    /// <summary>The number the review screen shows: the TaskListing line whose text holds the CR.</summary>
    private static int ListingLineOfCr(TaskPlan task) => TaskListing.Build(task).Single(l => l.Text.Contains('\r')).No;

    /// <summary>The global card numbers the statements joined by a GO line, by TaskListing's rules.</summary>
    private static int GlobalCardLineOfCr(List<string> statements) =>
        TaskListing.Build(new TaskPlan { SourceQuery = string.Join("\nGO\n", statements) }).Single(l => l.Text.Contains('\r')).No;

    /// <summary>Harm-first: a stored version whose SQL hides a DELETE behind a comment that SQL Server ends at the CR — stored with no
    /// evidence at all, as a version from before this rule would be — cannot be approved. The outcome, not a string, is the assertion.</summary>
    [Fact]
    public void A_hidden_statement_behind_a_bare_carriage_return_is_not_approvable()
    {
        var plan = Plan();
        plan.Tasks["T05"].PreSql.Insert(0, Hidden);
        Ctx(plan);
        _services.Phases.SetStatus(PhaseName.Sql, PhaseStatus.AwaitingReview);

        var ex = Assert.Throws<WorkflowException>(() => _services.ApproveCurrent(PhaseName.Sql));

        Assert.NotEqual(PhaseStatus.Approved, _services.Phases.Get(PhaseName.Sql).Status);
        Assert.Contains(CrError("T05", "preSql[0]", 1), ex.Details);

        // Control: the same plan without the CR approves, so nothing else blocked it.
        plan.Tasks["T05"].PreSql[0] = "-- note\r\nDELETE FROM app.Customers";
        Ctx(plan);
        _services.Phases.SetStatus(PhaseName.Sql, PhaseStatus.AwaitingReview);
        _services.ApproveCurrent(PhaseName.Sql);
        Assert.Equal(PhaseStatus.Approved, _services.Phases.Get(PhaseName.Sql).Status);
    }

    [Fact]
    public void The_error_line_is_the_TaskListing_line_that_holds_the_carriage_return()
    {
        var plan = Plan();
        var t05 = plan.Tasks["T05"];
        t05.PreSql.Insert(0, Hidden);
        Assert.Equal(1, ListingLineOfCr(t05));

        var t04 = plan.Tasks["T04"];
        t04.PreSql.Add("UPDATE STATISTICS [app].[Addresses];\r\n");            // lines 1-2 (trailing CRLF numbers an empty line)
        t04.SourceQuery = "SELECT\r\n    s.[X] AS [X]\r\n" + Hidden + "\r\nFROM [dbo].[T] AS s";
        var expected = ListingLineOfCr(t04);
        Assert.Equal(5, expected);
        var ctx = Ctx(Plan());
        var payload = Json.ToNode(plan);

        var check = _module.Validate(ctx, payload);

        Assert.False(check.Ok);
        Assert.Contains(CrError("T05", "preSql[0]", 1), check.Errors);
        Assert.Contains(CrError("T04", "sourceQuery", expected), check.Errors);
        Assert.Equal(2, check.Errors.Count(e => e.Contains(SqlValidator.BareCarriageReturnMarker, StringComparison.Ordinal)));
        // Stored in the task's own errors (the ones that block approval), without the task prefix.
        Assert.Equal([$"sourceQuery: bare carriage return at line {expected}{Advice}"], Json.FromNode<SqlPlanPayload>(payload).Tasks["T04"].Errors);
    }

    [Fact]
    public void Every_occurrence_is_its_own_error()
    {
        var plan = Plan();
        plan.Tasks["T05"].PostSql.Add("-- a\rUPDATE x SET y = 1 -- b\rDELETE FROM app.Customers");
        var line = ListingLineOfCr(plan.Tasks["T05"]);

        var check = _module.Validate(Ctx(Plan()), Json.ToNode(plan));

        Assert.Equal(2, check.Errors.Count(e => e == CrError("T05", "postSql[0]", line)));
    }

    [Fact]
    public void Crlf_and_lf_alone_are_not_errors()
    {
        var plan = Plan();
        plan.Tasks["T05"].SourceQuery = plan.Tasks["T05"].SourceQuery.Replace("\n", "\r\n", StringComparison.Ordinal);
        plan.Tasks["T04"].PreSql.Add("SELECT 1;\nSELECT 2;\r\n");
        plan.PreSql.Add("SELECT 1;\r\n\r\nSELECT 2;\n");

        var check = _module.Validate(Ctx(Plan()), Json.ToNode(plan));

        Assert.True(check.Ok, string.Join("; ", check.Errors));
        Assert.Empty(SqlValidator.FindBareCarriageReturns(plan));
    }

    /// <summary>Offline (the fixture has no connections): the check still runs, on the global statements too. A global statement's N is
    /// its line in the screen's global card; the field names the statement.</summary>
    [Fact]
    public void The_check_runs_offline_and_covers_the_global_statements()
    {
        var plan = Plan();
        plan.PreSql.Add("SELECT 1;\r\nSELECT 2;");
        plan.PreSql.Add("SELECT 3;\r\n" + Hidden);
        plan.PostSql.Insert(0, Hidden);
        var preLine = GlobalCardLineOfCr(plan.PreSql);
        var postLine = GlobalCardLineOfCr(plan.PostSql);
        var ctx = Ctx(Plan());
        var payload = Json.ToNode(plan);

        var check = _module.Validate(ctx, payload);

        Assert.Contains(OfflineMarker, check.Warnings);
        Assert.False(check.Ok);
        var pre = $"preSql[{plan.PreSql.Count - 1}]";
        Assert.Contains(CrError("plan", pre, preLine), check.Errors);
        Assert.Contains(CrError("plan", "postSql[0]", postLine), check.Errors);
        Assert.Equal(1, postLine);
        var stored = Json.FromNode<SqlPlanPayload>(payload);
        Assert.Contains($"{pre}: bare carriage return at line {preLine}{Advice}", stored.Errors);
        Assert.Contains(CrError("plan", pre, preLine), _module.ApprovalBlockers(ctx, payload));
    }

    /// <summary>A stored version with a bare CR and its error (as sqlgen stores it). The plan is the base of the next patch.</summary>
    private (SqlPlanPayload Plan, ModuleContext Ctx) StoredWithCr()
    {
        var plan = Plan();
        plan.Tasks["T05"].PreSql.Insert(0, Hidden);
        SqlValidator.RecordBareCarriageReturns(plan);
        Assert.Equal([$"preSql[0]: bare carriage return at line 1{Advice}"], plan.Tasks["T05"].Errors);
        return (plan, Ctx(plan));
    }

    [Fact]
    public void A_patch_that_fixes_the_carriage_return_clears_the_error()
    {
        var (plan, ctx) = StoredWithCr();
        var payload = Json.ToNode(plan);
        payload["tasks"]!["T05"]!["preSql"]![0] = "-- note\r\nDELETE FROM app.Customers";

        var check = _module.Validate(ctx, payload);

        Assert.True(check.Ok, string.Join("; ", check.Errors));
        Assert.Empty(Json.FromNode<SqlPlanPayload>(payload).Tasks["T05"].Errors);
    }

    [Fact]
    public void A_patch_that_leaves_the_carriage_return_keeps_exactly_one_error()
    {
        var (plan, ctx) = StoredWithCr();
        var payload = Json.ToNode(plan);
        payload["tasks"]!["T04"]!["sourceQuery"] = plan.Tasks["T04"].SourceQuery + "\nWHERE 1 = 1";

        var check = _module.Validate(ctx, payload);

        Assert.Equal([CrError("T05", "preSql[0]", 1)], check.Errors);
        Assert.Equal([$"preSql[0]: bare carriage return at line 1{Advice}"], Json.FromNode<SqlPlanPayload>(payload).Tasks["T05"].Errors);
    }

    [Fact]
    public void A_patch_may_not_delete_the_error_while_the_carriage_return_remains()
    {
        var (plan, ctx) = StoredWithCr();
        var payload = Json.ToNode(plan);
        payload["tasks"]!["T05"]!["errors"] = new JsonArray();

        var check = _module.Validate(ctx, payload);

        Assert.Equal(["T05: errors is engine-owned (recorded by validation); a patch may not change it"], check.Errors);
        Assert.Contains(CrError("T05", "preSql[0]", 1), _module.ApprovalBlockers(ctx, Json.ToNode(plan)));
    }

    [Fact]
    public void Dry_run_shows_the_bare_carriage_return()
    {
        var plan = Plan();
        Ctx(plan);
        _services.Phases.SetStatus(PhaseName.Sql, PhaseStatus.Drafting);
        var current = _services.Phases.Get(PhaseName.Sql).CurrentVersion!.Value;

        var result = _services.Workflow.ApplyPatch(new Dbm.Core.Patching.Patch("sql", current,
            [new Dbm.Core.Patching.PatchOp("add", "/tasks/T05/preSql/0", JsonValue.Create(Hidden))], []), dryRun: true);

        Assert.False(result.Ok);
        Assert.Contains(CrError("T05", "preSql[0]", 1), result.Errors);
        Assert.Equal(current, _services.Phases.Get(PhaseName.Sql).CurrentVersion);
    }

    /// <summary>Pins every numbering rule that views/sql.js (T4.5) mirrors: section order, the empty-section skip, CRLF
    /// normalisation, a trailing newline yielding an empty numbered line, 1-based continuous numbering and the Render layout.</summary>
    [Fact]
    public void TaskListing_numbers_sections_in_execution_order_continuously()
    {
        var task = new TaskPlan
        {
            PreSql = ["ALTER TABLE [a].[B] NOCHECK CONSTRAINT ALL;", "", "UPDATE STATISTICS [a].[B];\r\n"],
            SourceQuery = "SELECT\r\n    s.[X] AS [X]\r\nFROM [dbo].[T] AS s",
            StagingDdl = null,
            MergeSql = "",
            PostSql = ["ALTER TABLE [a].[B]\nWITH CHECK CHECK CONSTRAINT ALL;"],
        };

        var lines = TaskListing.Build(task);

        Assert.Equal(
            [
                new ListingLine(1, "pre", "ALTER TABLE [a].[B] NOCHECK CONSTRAINT ALL;"),
                new ListingLine(2, "pre", "UPDATE STATISTICS [a].[B];"),
                new ListingLine(3, "pre", ""),
                new ListingLine(4, "source", "SELECT"),
                new ListingLine(5, "source", "    s.[X] AS [X]"),
                new ListingLine(6, "source", "FROM [dbo].[T] AS s"),
                new ListingLine(7, "post", "ALTER TABLE [a].[B]"),
                new ListingLine(8, "post", "WITH CHECK CHECK CONSTRAINT ALL;"),
            ],
            lines);
        Assert.Equal(
            "   1 pre     |  ALTER TABLE [a].[B] NOCHECK CONSTRAINT ALL;\n" +
            "   2 pre     |  UPDATE STATISTICS [a].[B];\n" +
            "   3 pre     |  \n" +
            "   4 source  |  SELECT\n" +
            "   5 source  >>     s.[X] AS [X]\n" +
            "   6 source  |  FROM [dbo].[T] AS s\n" +
            "   7 post    |  ALTER TABLE [a].[B]\n" +
            "   8 post    |  WITH CHECK CHECK CONSTRAINT ALL;",
            TaskListing.Render(task, 5));
    }
}
