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

    private static SqlPlanPayload Plan(MappingPayload? mapping = null) =>
        SqlGenerator.Generate(mapping ?? SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target());

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
        Assert.Contains(SqlModule.SkippedWarning, check.Warnings);
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
                     "session-local temp table in tempdb, dropped at connection close", "state.db", "feedbackId" })
            Assert.Contains(phrase, text);
        Assert.DoesNotContain("nothing is ever executed", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PARSEONLY", text, StringComparison.OrdinalIgnoreCase);
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
