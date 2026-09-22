using Dbm.Core;
using Dbm.Core.Catalog;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Dbm.Core.Transfer;
using Dbm.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Dbm.Tests.Integration.Transfer;

[Trait("Category", "Integration")]
public sealed class PreflightTests(EngineSourceFixture fx) : IClassFixture<EngineSourceFixture>
{
    private static PreflightCheck Check(IEnumerable<PreflightCheck> checks, string name) => checks.Single(c => c.Name == name);

    /// <summary>Approves <paramref name="plan"/> as v0 so that Preflight.RunAsync gets past its sql_plan gate.</summary>
    private static void Approve(DbmServices s, SqlPlanPayload plan)
    {
        s.Artifacts.Add(PhaseName.Sql, 0, Json.Serialize(plan), "script", null);
        s.Phases.SetCurrentVersion(PhaseName.Sql, 0);
        s.Phases.SetApproved(PhaseName.Sql, 0, null);
    }

    private static SqlPlanPayload PlanWithMissingTable()
    {
        var plan = TransferEngineTests.Plan();
        plan.Tasks["T01"].IdentityInsert = true;
        plan.Tasks["T04"] = new TaskPlan { Target = "app.Missing", SourceQuery = "SELECT 1 AS [X]", Columns = [new("X", "X")] };
        plan.Order.Add("T04");
        return plan;
    }

    [Fact]
    public async Task Target_checks_report_missing_tables_existing_rows_and_permissions()
    {
        await using var tgt = await TempDatabase.CreateAsync("dbm_pre");
        await tgt.ExecAsync(TransferEngineTests.TargetSchema);
        await tgt.ExecAsync("INSERT app.Parent (Id, Name) VALUES (1, N'x');");
        await using var conn = new SqlConnection(tgt.ConnectionString);
        await conn.OpenAsync();
        var plan = PlanWithMissingTable();

        var checks = await Preflight.TargetChecksAsync(conn, plan, new TransferOptions(), default);
        Assert.False(Check(checks, "target_tables").Ok);
        Assert.Equal("error", Check(checks, "target_tables").Severity);
        Assert.Contains("app.Missing", Check(checks, "target_tables").Detail);
        Assert.True(Check(checks, "insert_permission").Ok);
        Assert.True(Check(checks, "identity_insert_permission").Ok);
        Assert.True(Check(checks, "control_table").Ok);
        var rows = Check(checks, "target_rows");
        Assert.False(rows.Ok);
        Assert.Equal("warning", rows.Severity);
        Assert.Contains("app.Parent (1)", rows.Detail);
        Assert.DoesNotContain(checks, c => c.Name == "truncate_permission");

        var truncating = await Preflight.TargetChecksAsync(conn, plan, new TransferOptions { TruncateTarget = true }, default);
        Assert.True(Check(truncating, "target_rows").Ok);
        Assert.Contains("emptied", Check(truncating, "target_rows").Detail);
        Assert.True(Check(truncating, "truncate_permission").Ok);
    }

    [Fact]
    public async Task Missing_permissions_are_errors()
    {
        await using var tgt = await TempDatabase.CreateAsync("dbm_pre");
        await tgt.ExecAsync(TransferEngineTests.TargetSchema);
        await tgt.ExecAsync("CREATE USER limited WITHOUT LOGIN; GRANT SELECT ON SCHEMA::app TO limited;");
        await using var conn = new SqlConnection(tgt.ConnectionString);
        await conn.OpenAsync();
        await using (var cmd = new SqlCommand("EXECUTE AS USER = 'limited';", conn)) await cmd.ExecuteNonQueryAsync();

        var checks = await Preflight.TargetChecksAsync(conn, TransferEngineTests.Plan(), new TransferOptions(), default);
        Assert.False(Check(checks, "insert_permission").Ok);
        Assert.Contains("app.Parent", Check(checks, "insert_permission").Detail);
        Assert.False(Check(checks, "control_table").Ok);
        await using (var cmd = new SqlCommand("REVERT;", conn)) await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Source_estimate_counts_rows_per_task()
    {
        await using var conn = new SqlConnection(fx.Src.ConnectionString);
        await conn.OpenAsync();
        var check = await Preflight.SourceEstimateAsync(conn, TransferEngineTests.Plan(), default);
        Assert.True(check.Ok);
        Assert.Contains("3,000 rows across 3 tasks", check.Detail);
        Assert.Contains("largest: app.Child 2,000", check.Detail);
    }

    /// <summary>
    /// Ruling 188 (final review M-1). A 1:N join repeats the parent key, and keyset chunking pages with <c>key &gt; @last</c>: the
    /// rows sharing the last key of a chunk would be skipped, and the run would only say MISMATCH at the very end. Pre-flight finds one
    /// repeated key first and blocks the run, naming the task and the key.
    /// </summary>
    [Fact]
    public async Task A_join_that_repeats_the_chunk_key_blocks_the_run_naming_the_task_and_the_key()
    {
        var plan = TransferEngineTests.Plan();
        plan.Tasks["T01"].SourceQuery = "SELECT p.[Id] AS [Id], p.[Name] AS [Name], p.[Id] AS [__k0]\nFROM [dbo].[Parent] AS p\n"
                                        + "JOIN [dbo].[Child] AS c ON c.[ParentId] = p.[Id]";
        await using var conn = new SqlConnection(fx.Src.ConnectionString);
        await conn.OpenAsync();

        Assert.True(Preflight.NeedsKeyProbe(plan.Tasks["T01"]), "a task whose FROM joins a child table is not probed for a repeated key");
        Assert.False(Preflight.NeedsKeyProbe(plan.Tasks["T02"]), "the generator's own single-table shape is probed needlessly");
        var check = await Preflight.ChunkKeyCheckAsync(conn, plan, default);

        Assert.True(check is { Ok: false, Severity: "error" }, "a repeated chunk key did not block the run: " + check?.Detail);
        Assert.Matches(@"T01 \(app\.Parent\): key __k0=\d+ appears \d+ times", check!.Detail);

        using var svc = new XferServices();
        var s = svc.Services;
        s.Connections.Save(Side.Src, fx.Src.ConnectionString, FakeServices.Meta("src", "x"));
        Approve(s, plan);
        var result = await Preflight.RunAsync(s, new TransferOptions(), default);
        Assert.True(result.Checks.Any(c => c.Name == "chunk_keys" && !c.Ok && c.Detail.Contains("T01", StringComparison.Ordinal)),
            "the pre-flight the Execute screen runs has no failing chunk_keys line: " + string.Join(", ", result.Checks.Select(c => c.Name)));
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task A_join_whose_key_stays_unique_passes_the_probe()
    {
        var plan = TransferEngineTests.Plan();
        plan.Tasks["T02"].SourceQuery = "SELECT c.[Id] AS [Id], c.[ParentId] AS [ParentId], c.[Qty] AS [Qty], c.[At] AS [At], c.[Id] AS [__k0]\n"
                                        + "FROM [dbo].[Child] AS c JOIN [dbo].[Parent] AS p ON p.[Id] = c.[ParentId]";
        await using var conn = new SqlConnection(fx.Src.ConnectionString);
        await conn.OpenAsync();

        var check = await Preflight.ChunkKeyCheckAsync(conn, plan, default);

        Assert.True(check is { Ok: true }, "a joined task with a unique key: " + (check?.Detail ?? "not probed at all"));
        Assert.Contains("T02", check!.Detail);
    }

    // ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Harm: a table that does not exist is skipped by every later check in the loop, so "INSERT permission on every target table"
    /// and "All target tables are empty" are pronounced over a set that excludes it. The operator creates the missing table, re-runs,
    /// and discovers the permission problem the checklist said was not there.
    /// </summary>
    [Fact]
    public async Task Checks_that_could_not_cover_every_table_say_which_ones_they_skipped()
    {
        await using var tgt = await TempDatabase.CreateAsync("dbm_pre_skip");
        await tgt.ExecAsync(TransferEngineTests.TargetSchema);
        await using var conn = new SqlConnection(tgt.ConnectionString);
        await conn.OpenAsync();

        var plan = PlanWithMissingTable();
        plan.Tasks["T04"].IdentityInsert = true;          // the missing table is one the identity check would have had to cover
        var checks = await Preflight.TargetChecksAsync(conn, plan, new TransferOptions(), default);

        Assert.True(Check(checks, "insert_permission").Ok);
        Assert.Contains("app.Missing", Check(checks, "insert_permission").Detail);
        Assert.Contains("app.Missing", Check(checks, "target_rows").Detail);
        Assert.Contains("app.Missing", Check(checks, "identity_insert_permission").Detail);
    }

    /// <summary>
    /// Harm: a source count that comes back as NULL or as no row at all - a CountSql the plan supplies verbatim can do either - is not
    /// a count of zero. Added into the total it understates the volume the operator is about to move, and the estimate says nothing
    /// about having failed.
    /// </summary>
    [Fact]
    public async Task A_source_count_that_yields_no_number_is_not_counted_as_zero_rows()
    {
        await using var conn = new SqlConnection(fx.Src.ConnectionString);
        await conn.OpenAsync();
        var plan = TransferEngineTests.Plan();
        plan.Tasks["T03"].CountSql = "SELECT CAST(NULL AS bigint);";

        var check = await Preflight.SourceEstimateAsync(conn, plan, default);

        Assert.False(check.Ok);
        Assert.Equal("warning", check.Severity);
        Assert.Contains("2,300 rows across 2 tasks", check.Detail);      // 300 + 2000; the 700 of T03 were never established
        Assert.Contains("T03", check.Detail);
        Assert.Contains("no number", check.Detail);
    }

    /// <summary>
    /// Harm (F1, ruling 119): an INSERT-without-SELECT loader account - the ordinary case a preflight checklist exists for - passes
    /// HAS_PERMS_BY_NAME('INSERT') and then dies on COUNT_BIG. Uncaught, the operator gets an exception where the checklist belongs and
    /// loses every line that already succeeded, including the one sentence naming what to fix. Preflight never throws for a SQL fault:
    /// it reports the fault as an error check, keeps the lines that ran, and says which tables the remaining verdicts could not cover.
    /// </summary>
    [Fact]
    public async Task A_table_that_cannot_be_probed_is_reported_rather_than_thrown()
    {
        await using var tgt = await TempDatabase.CreateAsync("dbm_pre_deny");
        await tgt.ExecAsync(TransferEngineTests.TargetSchema);
        await tgt.ExecAsync("CREATE USER loader WITHOUT LOGIN; GRANT INSERT ON SCHEMA::app TO loader;");
        await using var conn = new SqlConnection(tgt.ConnectionString);
        await conn.OpenAsync();
        await using (var cmd = new SqlCommand("EXECUTE AS USER = 'loader';", conn)) await cmd.ExecuteNonQueryAsync();

        var checks = await Preflight.TargetChecksAsync(conn, TransferEngineTests.Plan(), new TransferOptions(), default);
        await using (var cmd = new SqlCommand("REVERT;", conn)) await cmd.ExecuteNonQueryAsync();

        var probe = Check(checks, "target_probe");
        Assert.False(probe.Ok);
        Assert.Equal("error", probe.Severity);
        Assert.Contains("app.Parent", probe.Detail);
        Assert.Contains("SELECT permission", probe.Detail);

        Assert.True(Check(checks, "target_tables").Ok);                                   // the lines that ran are still here
        Assert.True(Check(checks, "insert_permission").Ok);
        Assert.NotEmpty(Check(checks, "control_table").Detail);
        // and the verdict that depends on counting says which tables it could not cover, instead of "all empty"
        Assert.Contains("app.Parent", Check(checks, "target_rows").Detail);
        Assert.False(new PreflightResult(1, DateTimeOffset.UtcNow, [.. checks]).Passed);
    }

    /// <summary>Both connections and both discovered catalogs saved exactly as discovery saves them (real fingerprints), and the
    /// plan approved - so every check can run and the schemas really do match.</summary>
    private async Task<(XferServices Svc, TempDatabase Tgt)> DiscoveredRigAsync()
    {
        var tgt = await TempDatabase.CreateAsync("dbm_pre_cat");
        await tgt.ExecAsync(TransferEngineTests.TargetSchema);
        var svc = new XferServices();
        foreach (var (side, cs) in new[] { (Side.Src, fx.Src.ConnectionString), (Side.Tgt, tgt.ConnectionString) })
        {
            var meta = await Dbm.Core.Sql.SqlConnect.ProbeAsync(cs, default);
            svc.Services.Connections.Save(side, cs, meta);
            await using var conn = await Dbm.Core.Sql.SqlConnect.OpenAsync(cs, default);
            var snapshot = await CatalogExtractor.ExtractAsync(conn, meta, default);
            svc.Services.Catalog.Save(side, snapshot, Fingerprint.Compute(snapshot));
        }
        Approve(svc.Services, TransferEngineTests.Plan());
        return (svc, tgt);
    }

    /// <summary>
    /// Open item 26. With a discovered catalog that will not parse, the drift check still compared fingerprints (a column of its own)
    /// and said "Both schemas match the discovered catalogs" - a green tick over catalogs nobody could read, while the start door
    /// refuses 409 <c>not_ready</c> naming that same catalog. The check comes back not run, with the reason, and blocks.
    /// </summary>
    [Theory]
    [InlineData("tgt", "target")]
    [InlineData("src", "source")]
    public async Task An_unreadable_discovered_catalog_makes_the_schema_check_not_run_never_passed(string side, string word)
    {
        var (svc, tgt) = await DiscoveredRigAsync();
        using var _ = svc;
        await using var __ = tgt;
        Assert.True(Check((await Preflight.RunAsync(svc.Services, new TransferOptions(), default)).Checks, "schema_drift").Ok);   // the premise
        svc.Services.Db.Execute("UPDATE catalog SET snapshot_json = '{not json' WHERE side = $Side", new { Side = side });

        var result = await Preflight.RunAsync(svc.Services, new TransferOptions(), default);

        var drift = Check(result.Checks, "schema_drift");
        Assert.True(!drift.Ok && drift.NotRun && drift.Severity == "error",
            $"pre-flight passed the schema check over a discovered {word} catalog it could not read: ok={drift.Ok}, notRun={drift.NotRun}, "
            + $"{drift.Severity}: {drift.Detail}");
        Assert.Contains($"discovered {word} catalog could not be read", drift.Detail, StringComparison.Ordinal);
        Assert.Contains("re-run discovery", drift.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.Passed);
    }

    /// <summary>
    /// Ruling 212, review MED-2 (probe REVF_C). A catalog that parses but names no server, or a blank database, passed pre-flight while
    /// the start door refused it <c>not_ready</c>: the two had two different predicates for one record. Now they share one.
    /// </summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"server":{"server":"x","database":""},"tables":[]}""")]
    public async Task A_discovered_target_catalog_that_names_no_database_makes_the_schema_check_not_run(string snapshot)
    {
        var (svc, tgt) = await DiscoveredRigAsync();
        using var _ = svc;
        await using var __ = tgt;
        svc.Services.Db.Execute("UPDATE catalog SET snapshot_json = $J WHERE side = 'tgt'", new { J = snapshot });

        var result = await Preflight.RunAsync(svc.Services, new TransferOptions(), default);

        var drift = Check(result.Checks, "schema_drift");
        Assert.True(!drift.Ok && drift.NotRun && drift.Severity == "error",
            $"pre-flight passed the schema check over a discovered target catalog that names no database ({snapshot}): ok={drift.Ok}, "
            + $"notRun={drift.NotRun}, {drift.Severity}: {drift.Detail}");
        Assert.Contains("does not record which server and database", drift.Detail, StringComparison.Ordinal);
        Assert.False(result.Passed);
    }

    /// <summary>
    /// Ruling 128 on the checklist (review MED-2): the start door refuses a saved target that is not the database discovery read, and
    /// pre-flight said nothing about it - a green checklist above a Start that then refuses.
    /// </summary>
    [Fact]
    public async Task A_saved_target_that_is_not_the_discovered_database_fails_pre_flight()
    {
        var (svc, tgt) = await DiscoveredRigAsync();
        using var _ = svc;
        await using var __ = tgt;
        Assert.True(Check((await Preflight.RunAsync(svc.Services, new TransferOptions(), default)).Checks, "target_identity").Ok);  // premise
        var meta = svc.Services.Connections.GetMeta(Side.Tgt)!;
        svc.Services.Connections.Save(Side.Tgt, tgt.ConnectionString, meta with { Database = "SomeOtherDb" });

        var result = await Preflight.RunAsync(svc.Services, new TransferOptions(), default);

        var identity = Check(result.Checks, "target_identity");
        Assert.True(!identity.Ok && identity.Severity == "error" && !identity.NotRun,
            $"pre-flight passed a saved target that is not the database discovery read: ok={identity.Ok}, {identity.Severity}: {identity.Detail}");
        Assert.Contains("SomeOtherDb", identity.Detail, StringComparison.Ordinal);
        Assert.Contains(tgt.Name, identity.Detail, StringComparison.Ordinal);
        Assert.False(result.Passed);
    }

    /// <summary>The same shape one step further: no discovered catalog at all for a side. "Nothing to drift from" is not "they match".</summary>
    [Fact]
    public async Task A_missing_discovered_catalog_makes_the_schema_check_not_run_never_passed()
    {
        var (svc, tgt) = await DiscoveredRigAsync();
        using var _ = svc;
        await using var __ = tgt;
        svc.Services.Db.Execute("DELETE FROM catalog WHERE side = 'tgt'");

        var result = await Preflight.RunAsync(svc.Services, new TransferOptions(), default);

        var drift = Check(result.Checks, "schema_drift");
        Assert.True(!drift.Ok && drift.NotRun && drift.Severity == "error",
            $"pre-flight passed the schema check with no discovered target catalog to compare against: ok={drift.Ok}, notRun={drift.NotRun}, "
            + $"{drift.Severity}: {drift.Detail}");
        Assert.Contains("no discovered target catalog", drift.Detail, StringComparison.Ordinal);
        Assert.False(result.Passed);
    }

    /// <summary>
    /// Harm (F2, ruling 120): NotRun was always a warning, so on the route where the drift checker itself fails - both connections
    /// open, nothing else in the list an error - PreflightResult.Passed stayed true. Drift DETECTED blocks the run; drift
    /// UNVERIFIABLE let it through, on the one-bit gate 5.5 hangs the Run button on.
    /// <para>The rig is deterministic and needs no timing: a fingerprint was saved by discovery, so the drift check must run, but the
    /// stored server metadata it needs is corrupt, so it cannot. Both connections still open, which is the shape of the finding -
    /// nothing else in the list is an error.</para>
    /// </summary>
    [Fact]
    public async Task A_drift_check_that_could_not_run_is_an_error_not_a_pass()
    {
        await using var src = await TempDatabase.CreateAsync("dbm_pre_drift_src");
        await src.ExecAsync("""
            CREATE TABLE dbo.Parent (Id int NOT NULL, Name varchar(50) NOT NULL);
            CREATE TABLE dbo.Child (Id int NOT NULL, ParentId int NOT NULL, Qty int NOT NULL, At datetime NOT NULL);
            CREATE TABLE dbo.Log (Msg varchar(100) NOT NULL);
            """);
        await using var tgt = await TempDatabase.CreateAsync("dbm_pre_drift_tgt");
        await tgt.ExecAsync(TransferEngineTests.TargetSchema);
        using var svc = new XferServices();
        var s = svc.Services;
        s.Connections.Save(Side.Src, src.ConnectionString, TestCatalogs.Meta(src.Name));
        s.Connections.Save(Side.Tgt, tgt.ConnectionString, TestCatalogs.Meta(tgt.Name));
        s.Catalog.Save(Side.Src, TestCatalogs.Snapshot(TestCatalogs.Meta(src.Name), Array.Empty<TableInfo>()), "fingerprint-from-discovery");
        Approve(s, TransferEngineTests.Plan());
        s.Db.Execute("UPDATE connection SET server_meta_json = '{not json'");   // discovery ran; what it saved is unreadable now

        var result = await Preflight.RunAsync(s, new TransferOptions(), default);

        var drift = Check(result.Checks, "schema_drift");
        Assert.False(drift.Ok);
        Assert.Equal("error", drift.Severity);                             // ruling 120: nothing else records this fault
        Assert.Contains("could not be verified", drift.Detail);
        // N2: a JSON parser message about a file the operator never saw is a line they cannot act on. Name the thing that will not
        // parse, and the remedy, the way the sql_plan route one screen up already does.
        Assert.Contains("saved catalog metadata", drift.Detail);
        Assert.Contains("re-run discovery", drift.Detail);
        Assert.False(result.Passed);
        Assert.True(Check(result.Checks, "source_connection").Ok);          // nothing else failed: this is the drift check alone
        Assert.True(Check(result.Checks, "target_connection").Ok);
        Assert.DoesNotContain(result.Checks, c => c.Name != "schema_drift" && !c.Ok && c.Severity == "error");
    }

    /// <summary>
    /// Harm (F5): schema_drift and estimated_rows put server messages verbatim in front of an operator, and a server message can quote
    /// the literal that failed. Deleting the redaction pass left the suite green, so the guard had no witness. Here the failing literal
    /// IS the connection's password - integrated auth ignores a Password keyword, but Redactor.SecretsOf still reads it, so this is a
    /// real connection secret arriving in a real server message.
    /// </summary>
    [Fact]
    public async Task Server_messages_in_the_checklist_are_scrubbed_of_the_connection_secret()
    {
        const string secret = "Pa55word-never-used-here";
        await using var tgt = await TempDatabase.CreateAsync("dbm_pre_secret");
        await tgt.ExecAsync(TransferEngineTests.TargetSchema);
        using var svc = new XferServices();
        var s = svc.Services;
        string srcCs = new SqlConnectionStringBuilder(fx.Src.ConnectionString) { Password = secret }.ConnectionString;
        s.Connections.Save(Side.Src, srcCs, TestCatalogs.Meta(fx.Src.Name));
        s.Connections.Save(Side.Tgt, tgt.ConnectionString, TestCatalogs.Meta(tgt.Name));
        var plan = TransferEngineTests.Plan();
        plan.Tasks["T01"].CountSql = $"SELECT CAST(N'{secret}' AS int);";   // the server quotes the failing literal back at us
        Approve(s, plan);

        var result = await Preflight.RunAsync(s, new TransferOptions(), default);

        var estimate = Check(result.Checks, "estimated_rows");
        Assert.Contains("T01", estimate.Detail);
        Assert.Contains("***", estimate.Detail);
        Assert.All(result.Checks, c => Assert.DoesNotContain(secret, c.Detail, StringComparison.Ordinal));
    }

    /// <summary>
    /// Harm (N1, ruling 124): CountSql is a plan field the generator returns verbatim, so a human can put anything in it. One that
    /// yields a non-number needs no server fault at all - Convert.ToInt64 raises FormatException, SourceEstimateAsync caught only
    /// SqlException, and RunAsync called it from a try with a finally and no catch. The operator got an exception where the checklist
    /// belongs and lost every line that had already succeeded: F1's harm, on the other half of the same method.
    /// </summary>
    [Fact]
    public async Task A_source_count_that_is_not_a_number_is_reported_rather_than_thrown()
    {
        await using var tgt = await TempDatabase.CreateAsync("dbm_pre_nan");
        await tgt.ExecAsync(TransferEngineTests.TargetSchema);
        using var svc = new XferServices();
        var s = svc.Services;
        s.Connections.Save(Side.Src, fx.Src.ConnectionString, TestCatalogs.Meta(fx.Src.Name));
        s.Connections.Save(Side.Tgt, tgt.ConnectionString, TestCatalogs.Meta(tgt.Name));
        var plan = TransferEngineTests.Plan();
        plan.Tasks["T01"].CountSql = "SELECT N'about a thousand';";
        Approve(s, plan);

        // First without the group wrap above it, so that what is being pinned is the per-task catch itself and not the wrapper that
        // would otherwise hide its absence: the fault must not leave this method at all.
        await using (var direct = new SqlConnection(fx.Src.ConnectionString))
        {
            await direct.OpenAsync();
            var bare = await Preflight.SourceEstimateAsync(direct, plan, default);
            Assert.Contains("was not a number", bare.Detail);
            Assert.Contains("2,700 rows across 2 tasks", bare.Detail);
        }

        var result = await Preflight.RunAsync(s, new TransferOptions(), default);

        var estimate = Check(result.Checks, "estimated_rows");
        Assert.False(estimate.Ok);
        Assert.Equal("error", estimate.Severity);
        Assert.Contains("T01", estimate.Detail);
        Assert.Contains("was not a number", estimate.Detail);
        Assert.Contains("2,700 rows across 2 tasks", estimate.Detail);       // the other two tasks were still counted
        Assert.False(result.Passed);
        foreach (var name in new[] { "sql_plan", "plan_valid", "source_connection", "target_connection", "schema_drift",
                                     "target_tables", "insert_permission", "control_table", "target_rows" })
            Assert.Contains(result.Checks, c => c.Name == name);             // every line that ran before it is still here
    }

    /// <summary>
    /// Harm (N1, ruling 124): a source connection that dies part-way through the estimate makes every remaining task's command throw
    /// InvalidOperationException, which escaped the same way. A connection in that state is what the re-reviewer's PP3 produced by
    /// KILLing the session mid-count; closing it reaches the identical arm with no server tricks and no timing.
    /// </summary>
    [Fact]
    public async Task A_source_connection_that_cannot_be_used_yields_a_checklist_line_not_an_exception()
    {
        await using var conn = new SqlConnection(fx.Src.ConnectionString);
        await conn.OpenAsync();
        await conn.CloseAsync();

        var check = await Preflight.SourceEstimateAsync(conn, TransferEngineTests.Plan(), default);

        Assert.False(check.Ok);
        Assert.Equal("error", check.Severity);
        Assert.Contains("0 rows across 0 tasks", check.Detail);
        foreach (var id in new[] { "T01", "T02", "T03" }) Assert.Contains(id, check.Detail);
    }
}
