using System.IO.Compression;
using Dbm.Core.SqlGen;
using Dbm.Tests.Support;
using Xunit;

namespace Dbm.Tests.Unit.SqlGen;

public class ScriptPackTests
{
    /// <summary>The generated sample plan with the evidence a live validation stores (Ruling 204): a validated version unless a test
    /// clears <see cref="SqlPlanPayload.Validation"/>.</summary>
    static SqlPlanPayload Plan()
    {
        var plan = SqlGenerator.Generate(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target());
        plan.Validation = SqlValidation.From(new DateTimeOffset(2026, 9, 18, 9, 30, 0, TimeSpan.Zero), true);
        return plan;
    }

    static List<(string Name, string Content)> Unzip(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        return zip.Entries.Select(e =>
        {
            using var r = new StreamReader(e.Open());
            return (e.FullName, r.ReadToEnd());
        }).ToList();
    }

    [Fact]
    public void Zip_has_pre_one_file_per_task_post_and_readme_in_order()
    {
        var entries = Unzip(ScriptPack.BuildZip(Plan(), "LegacyShop to ShopV2"));
        Assert.Equal(
            ["00_pre.sql", "01_app_AuditEvents.sql", "02_app_Customers.sql", "03_app_Products.sql", "04_app_Addresses.sql",
             "05_app_Orders.sql", "06_app_OrderLines.sql", "99_post.sql", "README.md"],
            entries.Select(e => e.Name));
    }

    [Fact]
    public void Task_file_has_header_source_query_and_count_query()
    {
        var plan = Plan();
        var files = Unzip(ScriptPack.BuildZip(plan, "demo")).ToDictionary(e => e.Name, e => e.Content);
        var orders = files["05_app_Orders.sql"];
        Assert.Contains("-- demo: task T05  app.Orders\n", orders);
        Assert.Contains("-- Mode:            direct\n", orders);
        Assert.Contains("-- Keys:            __k0\n", orders);
        Assert.Contains("-- Depends on:      T02, T04\n", orders);
        Assert.Contains("-- Identity insert: yes\n", orders);
        Assert.Contains("--   - target has 1 trigger(s); not fired unless FireTriggers\n", orders);
        Assert.Contains("-- ---- Source query ----\n-- runs on SOURCE\n", orders);
        Assert.Contains(plan.Tasks["T05"].SourceQuery + "\nGO\n", orders);
        Assert.Contains(plan.Tasks["T05"].CountSql + ";\nGO\n", orders);
        Assert.Contains("-- Keys:            (none - the table loads in a single transaction)\n", files["01_app_AuditEvents.sql"]);
    }

    /// <summary>Ruling 57: a bare-CR error is flagged in the pack exactly as any other stored task error — in the task file's
    /// "Errors (validation and export checks)" header (open item 19's wording).</summary>
    [Fact]
    public void A_bare_carriage_return_error_is_listed_like_any_other_task_error()
    {
        var plan = Plan();
        plan.Tasks["T05"].PreSql.Insert(0, "-- note\rDELETE FROM app.Customers");
        SqlValidator.RecordBareCarriageReturns(plan);
        plan.Tasks["T04"].Errors.Add("sourceQuery: Invalid column name 'X'.");

        var files = ScriptPack.BuildFiles(plan, "demo").ToDictionary(f => f.Name, f => f.Content);

        Assert.Contains("-- Errors (validation and export checks):\n--   - sourceQuery: Invalid column name 'X'.\n", files["04_app_Addresses.sql"]);
        Assert.Contains("-- Errors (validation and export checks):\n--   - preSql[0]: bare carriage return at line 1 (SQL Server treats it as a line break; use CRLF or LF)\n",
            files["05_app_Orders.sql"]);
    }

    /// <summary>Ruling 96: the pack DERIVES bare carriage returns from the SQL, exactly as <see cref="SqlModule.ApprovalBlockers"/>
    /// does, instead of trusting stored evidence. Nothing on the export path checks approval, so a version stored without the scan —
    /// written before the rule, or by any writer that skips it — would otherwise hand a DBA a pack that says nothing at all: no
    /// PLAN HAS ERRORS, no TASK(S) HAVE ERRORS, no error line anywhere, while shipping the hidden statement verbatim in the same
    /// file. An absence the approval gate refuses to trust is not an absence the pack may turn into a positive claim.</summary>
    [Fact]
    public void A_pack_derives_bare_carriage_returns_that_no_stored_error_records()
    {
        var plan = Plan();
        plan.PreSql.Add("-- disable the audit trigger\rDELETE FROM app.Customers");
        plan.Tasks["T05"].PreSql.Insert(0, "-- note\rDROP TABLE app.Orders");
        Assert.Empty(plan.Errors);                                    // nothing ever scanned this plan:
        Assert.All(plan.Tasks.Values, t => Assert.Empty(t.Errors));   // neither carriage return has any stored evidence

        var files = ScriptPack.BuildFiles(plan, "demo");

        // Open item 19: no validation produced these lines (the export derived them), so no heading or body may say one did.
        foreach (var (name, content) in files)
            Assert.False(content.Contains("last validation", StringComparison.Ordinal), $"{name} credits the export's derived error lines to \"the last validation\"");
        const string advice = " (SQL Server treats it as a line break; use CRLF or LF)";
        Assert.Contains("-- WARNING: PLAN HAS ERRORS (see README.md)\n--   - preSql[1]: bare carriage return at line 3" + advice + "\n", files[0].Content);
        Assert.Contains("-- WARNING: 1 TASK(S) HAVE ERRORS (see each task file's header)\n--   - T05 (app.Orders): 1 error(s)\n", files[0].Content);
        Assert.Contains("-- Errors (validation and export checks):\n--   - preSql[0]: bare carriage return at line 1" + advice + "\n",
            files.Single(f => f.Name == "05_app_Orders.sql").Content);
        var readme = files.Single(f => f.Name == "README.md").Content;
        Assert.Contains("## WARNING: PLAN HAS ERRORS\n\n", readme);
        Assert.Contains("## WARNING: 1 TASK(S) HAVE ERRORS\n\n", readme);
        Assert.Contains("## WARNING: PLAN HAS ERRORS\n\nValidation and the export's own checks recorded these plan-level errors", readme);
        Assert.Contains("These tasks carry errors from validation and the export's own checks. Each error is listed in that task's own file, under\n"
            + "*Errors (validation and export checks)* in its header.", readme);
        // The executable carriage return still ships in the same file as its warning: the pack warns, it does not withhold.
        Assert.Contains(files, f => f.Content.Contains("\rDROP TABLE app.Orders", StringComparison.Ordinal));

        // A stored line is not printed twice: the same plan, scanned, gives byte-identical files.
        var scanned = Plan();
        scanned.PreSql.Add("-- disable the audit trigger\rDELETE FROM app.Customers");
        scanned.Tasks["T05"].PreSql.Insert(0, "-- note\rDROP TABLE app.Orders");
        SqlValidator.RecordBareCarriageReturns(scanned);
        Assert.NotEmpty(scanned.Errors);
        Assert.Equal(ScriptPack.BuildFiles(scanned, "demo"), files);
    }

    [Fact]
    public void Global_files_hold_the_cycle_statements()
    {
        var files = Unzip(ScriptPack.BuildZip(Plan(), "demo")).ToDictionary(e => e.Name, e => e.Content);
        Assert.Contains("ALTER TABLE [app].[Customers] NOCHECK CONSTRAINT [FK_Customers_PrimaryAddress];\nGO\n", files["00_pre.sql"]);
        Assert.Contains("ALTER TABLE [app].[Customers] WITH CHECK CHECK CONSTRAINT [FK_Customers_PrimaryAddress];\nGO\n", files["99_post.sql"]);
        Assert.Contains("-- runs on TARGET", files["00_pre.sql"]);
    }

    [Fact]
    public void Empty_global_scripts_say_so()
    {
        var plan = Plan();
        plan.PreSql.Clear();
        var files = Unzip(ScriptPack.BuildZip(plan, "demo")).ToDictionary(e => e.Name, e => e.Content);
        Assert.Contains("-- (no global pre-load statements)", files["00_pre.sql"]);
    }

    [Fact]
    public void Staging_task_file_contains_staging_and_merge()
    {
        var mapping = SampleMappings.Approved();
        mapping.Tables["app.Addresses"].Kind = "lookup";
        var plan = SqlGenerator.Generate(mapping, SampleCatalogs.Source(), SampleCatalogs.Target());
        var file = Unzip(ScriptPack.BuildZip(plan, "demo")).Single(e => e.Name == "04_app_Addresses.sql").Content;
        Assert.Contains("-- Mode:            staging_merge\n", file);
        Assert.Contains(plan.Tasks["T04"].StagingDdl + "\nGO\n", file);
        Assert.Contains(plan.Tasks["T04"].MergeSql + "\nGO\n", file);
    }

    [Fact]
    public void Readme_explains_streaming_and_lists_files()
    {
        var readme = Unzip(ScriptPack.BuildZip(Plan(), "demo")).Single(e => e.Name == "README.md").Content;
        Assert.StartsWith("# demo - migration script pack", readme);
        Assert.Contains("no linked server", readme);
        Assert.Contains("SqlBulkCopy", readme);
        Assert.Contains("| `05_app_Orders.sql` | T05 | app.Orders | direct | T02, T04 |", readme);
    }

    [Fact]
    public void Zip_is_byte_identical_for_the_same_plan()
    {
        var first = ScriptPack.BuildZip(Plan(), "demo");
        var second = ScriptPack.BuildZip(Plan(), "demo");
        Assert.Equal(first, second);
        Assert.Equal(ScriptPack.BuildFiles(Plan(), "demo"), Unzip(first));
    }

    /// <summary>M5: every generated fixture inserts tasks in Order, so a pack iterating the dictionary would pass the test above.
    /// Here the dictionary's insertion order is the reverse of Order; the pack must not change by a single byte.</summary>
    [Fact]
    public void Pack_follows_order_never_dictionary_insertion_order()
    {
        var plan = Plan();
        var reversed = Plan();
        reversed.Tasks = reversed.Tasks.Reverse().ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        Assert.NotEqual(reversed.Order, reversed.Tasks.Keys);   // fixture guard: the orders really differ

        Assert.Equal(ScriptPack.BuildZip(plan, "demo"), ScriptPack.BuildZip(reversed, "demo"));
        Assert.Equal(ScriptPack.BuildFiles(plan, "demo").Select(f => f.Name), ScriptPack.BuildFiles(reversed, "demo").Select(f => f.Name));
    }

    /// <summary>M5: two zips built within one 2-second DOS-time bucket cannot tell a fixed timestamp from "now"; the entry time can.</summary>
    [Fact]
    public void Zip_entries_carry_a_fixed_timestamp()
    {
        using var zip = new ZipArchive(new MemoryStream(ScriptPack.BuildZip(Plan(), "demo")), ZipArchiveMode.Read);
        Assert.NotEmpty(zip.Entries);
        Assert.All(zip.Entries, e => Assert.Equal(new DateTime(2000, 1, 1, 0, 0, 0), e.LastWriteTime.DateTime));
    }

    /// <summary>M2: the pack is handed to a DBA; a task missing from Order (or an Order id with no task) must never make it
    /// silently incomplete.</summary>
    [Fact]
    public void A_pack_whose_order_does_not_match_its_tasks_says_so_and_omits_nothing()
    {
        var plan = Plan();
        plan.Order.Remove("T03");
        plan.Order.Insert(2, "T99");

        var files = ScriptPack.BuildFiles(plan, "demo");

        Assert.Contains(files, f => f.Content.Contains("-- demo: task T03  app.Products\n", StringComparison.Ordinal));
        var readme = files.Single(f => f.Name == "README.md").Content;
        Assert.Contains("## WARNING: this pack does not match the plan's execution order", readme);
        Assert.Contains("- T03: task is missing from order", readme);
        Assert.Contains("- order[2]: T99 is not a task", readme);
        var pre = files[0].Content;
        Assert.Contains("-- WARNING: this pack does not match the plan's execution order (see README.md)\n", pre);
        Assert.Contains("--   - T03: task is missing from order\n", pre);
    }

    /// <summary>N2: a duplicate id in Order exports its task exactly once, at the first position; the WARNING block names the duplicate.</summary>
    [Fact]
    public void A_duplicate_order_id_exports_its_task_once()
    {
        var plan = Plan();
        plan.Order.Add("T01");

        var files = ScriptPack.BuildFiles(plan, "demo");

        var t01 = Assert.Single(files, f => f.Content.Contains("-- demo: task T01  app.AuditEvents\n", StringComparison.Ordinal));
        Assert.Equal("01_app_AuditEvents.sql", t01.Name);
        Assert.Contains("- order[6]: T01 is listed more than once", files.Single(f => f.Name == "README.md").Content);
        Assert.Contains("--   - order[6]: T01 is listed more than once\n", files[0].Content);
        Assert.Single(files.Single(f => f.Name == "README.md").Content.Split('\n'), l => l.StartsWith("| `", StringComparison.Ordinal) && l.Contains("| T01 |", StringComparison.Ordinal));
    }

    /// <summary>Fix round 2 (concern 3): the DBA never sees ApprovalBlockers. A pack of a plan nothing validated says so, with the stored
    /// line verbatim; a validated plan's pack says nothing of the kind.</summary>
    [Fact]
    public void A_never_validated_plan_says_so_in_the_pack_and_a_validated_one_does_not()
    {
        const string marker = SqlPlanSource.SkippedPrefix + "source connection, target connection missing";
        var plan = Plan();
        plan.Warnings.Add(marker);

        var files = ScriptPack.BuildFiles(plan, "demo");

        Assert.Contains("-- WARNING: NOT VALIDATED - no live validation is recorded for this plan\n-- " + marker + "\n", files[0].Content);
        var readme = files.Single(f => f.Name == "README.md").Content;
        Assert.Contains("## WARNING: NOT VALIDATED - no live validation is recorded for this plan\n\n" + marker + "\n", readme);

        foreach (var (name, content) in ScriptPack.BuildFiles(Plan(), "demo"))
            Assert.DoesNotContain("NOT VALIDATED", content, StringComparison.Ordinal);
        Assert.DoesNotContain(SqlPlanSource.SkippedPrefix, string.Concat(ScriptPack.BuildFiles(Plan(), "demo").Select(f => f.Content)), StringComparison.Ordinal);

        // Ruling 204: no evidence reads as NOT validated, marker or not - an old record, stored before the evidence existed, has neither.
        var old = Plan();
        old.Validation = null;
        var oldPre = ScriptPack.BuildFiles(old, "demo")[0].Content;
        Assert.True(oldPre.Contains("-- WARNING: NOT VALIDATED - no live validation is recorded for this plan\n-- " + SqlModule.NoEvidence + "\n", StringComparison.Ordinal),
            "a pack of a version with no validation evidence does not say it is not validated:\n" + oldPre[..Math.Min(600, oldPre.Length)]);

        // Sweep I review, follow-up (a): evidence with ok:false is NOT validated either - the pack must say so, not stay silent
        // the way it would for a plan that passed cleanly.
        var failed = Plan();
        failed.Validation = SqlValidation.From(new DateTimeOffset(2026, 9, 18, 9, 30, 0, TimeSpan.Zero), false);
        var failedPre = ScriptPack.BuildFiles(failed, "demo")[0].Content;
        Assert.True(failedPre.Contains("-- WARNING: NOT VALIDATED - no live validation is recorded for this plan\n-- " + SqlModule.ValidatedWithErrors + "\n", StringComparison.Ordinal),
            "a pack of a version whose evidence reports errors does not say it is not validated:\n" + failedPre[..Math.Min(600, failedPre.Length)]);

        // The stored line is untrusted text like any other: it cannot start a line of its own.
        var hostile = Plan();
        hostile.Warnings.Add(marker + "\nDROP TABLE [app].[Orders];");
        foreach (var (name, content) in ScriptPack.BuildFiles(hostile, "demo"))
            Assert.DoesNotContain("\nDROP TABLE", content, StringComparison.Ordinal);
    }

    /// <summary>Ruling 71: the DBA never sees ApprovalBlockers, and a bare carriage return in a global pre/post statement is a
    /// plan-level error, not a task one. The pack lists the plan's stored errors verbatim; a plan without them says nothing of the
    /// kind, so "no block" means "no plan-level errors", not "the pack never prints them". Whether an errored plan may be exported
    /// at all is unchanged: both packs are built.</summary>
    [Fact]
    public void A_plan_with_plan_level_errors_says_so_in_the_pack_and_a_clean_one_does_not()
    {
        var plan = Plan();
        plan.PreSql.Add("-- disable the audit trigger\rDELETE FROM app.Customers");
        SqlValidator.RecordBareCarriageReturns(plan);
        var cr = Assert.Single(plan.Errors);   // stored without the scope prefix
        Assert.Contains(SqlValidator.BareCarriageReturnMarker, cr, StringComparison.Ordinal);
        plan.Errors.Add("target connection failed: timeout");

        var files = ScriptPack.BuildFiles(plan, "demo");

        Assert.Contains("-- WARNING: PLAN HAS ERRORS (see README.md)\n--   - " + cr + "\n--   - target connection failed: timeout\n", files[0].Content);
        var readme = files.Single(f => f.Name == "README.md").Content;
        Assert.Contains("## WARNING: PLAN HAS ERRORS\n\n", readme);
        Assert.Contains("\n- " + cr + "\n- target connection failed: timeout\n", readme);

        Assert.Empty(Plan().Errors);   // fixture guard: the generated plan really has no plan-level errors
        foreach (var (name, content) in ScriptPack.BuildFiles(Plan(), "demo"))
            Assert.DoesNotContain("PLAN HAS ERRORS", content, StringComparison.Ordinal);
    }

    /// <summary>Ruling 71, H2: a stored plan-level error is untrusted text like every other value. It reaches the pre file and the
    /// README only through the neutraliser, so it can never end its line and start one of its own.</summary>
    [Fact]
    public void A_hostile_plan_level_error_cannot_start_a_line_of_its_own()
    {
        var plan = Plan();
        plan.Errors.Add("target connection failed: " + Hostile);

        var files = ScriptPack.BuildFiles(plan, "demo");

        Assert.Contains("CANARY_LF", files[0].Content, StringComparison.Ordinal);   // the error did reach both files
        Assert.Contains("CANARY_LF", files.Single(f => f.Name == "README.md").Content, StringComparison.Ordinal);
        foreach (var (name, content) in files)
            foreach (var line in content.Split(LineBreaks, StringSplitOptions.None))
                Assert.False(line.TrimStart().StartsWith("CANARY", StringComparison.Ordinal), $"{name}: a value escaped its comment: [{line}]");
    }

    /// <summary>Ruling 77: 00_pre.sql carries the plan-level errors, but a task's errors live in its own file's header. Without a
    /// roll-up the DBA who opens 00_pre.sql cannot tell "no task has errors" from "this pack never says". The ids are named, with a
    /// count each so one error reads differently from nine, and the error text is not repeated. Exporting an errored plan stays
    /// allowed.
    /// <para>Ruling 95: the roll-up follows the pack's own file order, which is every task — those Order lists, then those it misses.
    /// T06 here is errored and missing from Order: a roll-up restricted to ordered tasks would under-count in exactly the plan shape
    /// that makes an inconsistent Order likely, while T06's file still ships with its errors.</para></summary>
    [Fact]
    public void The_pack_says_which_tasks_have_errors_and_says_nothing_when_none_do()
    {
        var plan = Plan();
        plan.Tasks["T05"].Errors.Add("sourceQuery: Invalid column name 'X'.");
        plan.Tasks["T05"].Errors.Add("preSql[0]: GO batch separators are not allowed");
        plan.Tasks["T02"].Errors.Add("target table app.Customers not found in the target catalog");
        plan.Tasks["T06"].Errors.Add("mergeSql: Invalid object name 'app.OrderLines_stg'.");
        plan.Order.Remove("T06");
        Assert.DoesNotContain("T06", plan.Order);              // fixture guard: the third errored task really is unordered
        Assert.True(plan.Tasks.ContainsKey("T06"));            // and it is still a task, so its file still ships

        var files = ScriptPack.BuildFiles(plan, "demo");

        Assert.Contains("-- WARNING: 3 TASK(S) HAVE ERRORS (see each task file's header)\n--   - T02 (app.Customers): 1 error(s)\n--   - T05 (app.Orders): 2 error(s)\n--   - T06 (app.OrderLines): 1 error(s)\n",
            files[0].Content);
        var readme = files.Single(f => f.Name == "README.md").Content;
        Assert.Contains("## WARNING: 3 TASK(S) HAVE ERRORS\n\n", readme);
        Assert.Contains("\n- T02 (app.Customers): 1 error(s)\n- T05 (app.Orders): 2 error(s)\n- T06 (app.OrderLines): 1 error(s)\n", readme);
        Assert.Equal(1, files.Count(f => f.Content.Contains("Invalid column name 'X'.", StringComparison.Ordinal)));   // the text stays in the task file
        Assert.Single(files, f => f.Content.Contains("Invalid object name 'app.OrderLines_stg'.", StringComparison.Ordinal));

        Assert.All(Plan().Tasks.Values, t => Assert.Empty(t.Errors));   // fixture guard: the generated tasks really have no errors
        foreach (var (name, content) in ScriptPack.BuildFiles(Plan(), "demo"))
            Assert.DoesNotContain("TASK(S) HAVE ERRORS", content, StringComparison.Ordinal);
    }

    /// <summary>Open item 18: 99_post.sql used to get <c>PackNotes.None</c> because nothing constructed its notes, not because there was
    /// nothing to say — the "silence reads as none" shape Ruling 77 closed for 00_pre.sql. The post file runs after every task (it
    /// re-enables constraints <c>WITH CHECK</c>), so a DBA who opens it alone must read the same warnings the pre file carries.</summary>
    [Fact]
    public void The_post_file_carries_the_same_warnings_as_the_pre_file()
    {
        var plan = Plan();
        plan.Warnings.Add(SqlPlanSource.SkippedPrefix + "source connection, target connection missing");
        plan.Errors.Add("target connection failed: timeout");
        plan.Tasks["T05"].Errors.Add("sourceQuery: Invalid column name 'X'.");
        plan.Order.Remove("T03");

        var files = ScriptPack.BuildFiles(plan, "demo");
        var pre = Warnings(files.Single(f => f.Name == "00_pre.sql").Content);
        var post = Warnings(files.Single(f => f.Name == "99_post.sql").Content);

        Assert.Equal(4, pre.Count(l => l.StartsWith("-- WARNING: ", StringComparison.Ordinal)));   // fixture guard: all four lists speak
        Assert.True(pre.SequenceEqual(post), "99_post.sql does not carry 00_pre.sql's warnings:\npre:\n" + string.Join('\n', pre) + "\npost:\n" + string.Join('\n', post));

        var clean = ScriptPack.BuildFiles(Plan(), "demo").Single(f => f.Name == "99_post.sql").Content;
        Assert.Empty(Warnings(clean));   // a plan with nothing to say says nothing in the post file either

        // The header lines after the two that name the file and say when it runs, up to the closing rule.
        static List<string> Warnings(string file) =>
            file.Split('\n').Skip(3).TakeWhile(l => !l.StartsWith("-- ====", StringComparison.Ordinal)).ToList();
    }

    /// <summary>Ruling 97, which makes Ruling 79's condition mechanical instead of remembered. Four warning lists have four heading
    /// tests today only because someone recalls the rule; nothing makes a fifth list arrive with one. This pins the list-typed
    /// properties of <c>PackNotes</c> against a literal map to the test that asserts each one's heading, so a list added without a
    /// heading test fails here with the name of the list. The map's values are <c>nameof</c>, so renaming a heading test is a compile
    /// error rather than a silent gap, and the attribute check catches a heading test that quietly stops being a test.</summary>
    [Fact]
    public void Every_PackNotes_warning_list_has_a_heading_test()
    {
        var headingTest = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Problems"] = nameof(A_pack_whose_order_does_not_match_its_tasks_says_so_and_omits_nothing),
            ["NotValidated"] = nameof(A_never_validated_plan_says_so_in_the_pack_and_a_validated_one_does_not),
            ["PlanErrors"] = nameof(A_plan_with_plan_level_errors_says_so_in_the_pack_and_a_clean_one_does_not),
            ["TaskErrors"] = nameof(The_pack_says_which_tasks_have_errors_and_says_nothing_when_none_do),
        };

        var notes = typeof(ScriptPack).GetNestedType("PackNotes", System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ScriptPack.PackNotes is gone: the pack's warning lists moved, and this guard no longer guards anything.");
        var lists = notes.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.PropertyType != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(p.PropertyType))
            .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.Equal(headingTest.Keys.OrderBy(n => n, StringComparer.Ordinal), lists);
        foreach (var (list, test) in headingTest)
            Assert.True(typeof(ScriptPackTests).GetMethod(test)!.GetCustomAttributes(typeof(FactAttribute), false).Length == 1,
                $"{list}: its heading test {test} is not a [Fact]");
    }

    const string Hostile ="x\nCANARY_LF;\rCANARY_CR;\r\nCANARY_CRLF;\u2028CANARY_LS;\u2029CANARY_PS;\u0085CANARY_NEL;\vCANARY_VT;\fCANARY_FF;";

    /// <summary>Every line terminator a T-SQL parser or an editor may honour - the ones <see cref="ScriptPack.OneLine"/> neutralises,
    /// and so the ones after which a hostile value must never be found.</summary>
    static readonly string[] LineBreaks = ["\r\n", "\n", "\r", "\u2028", "\u2029", "\u0085", "\v", "\f"];

    /// <summary>Plan properties the poisoning skips: the global SQL bodies (executable by design) and Order (its ids are poisoned by
    /// renaming one task, below, so Order and Tasks keep matching). Pinned by <see cref="Poison_exclusions_are_exactly_the_sql_bodies_and_order"/>.</summary>
    static readonly HashSet<string> Excluded =
    [
        $"{nameof(SqlPlanPayload)}.{nameof(SqlPlanPayload.PreSql)}", $"{nameof(SqlPlanPayload)}.{nameof(SqlPlanPayload.PostSql)}",
        $"{nameof(SqlPlanPayload)}.{nameof(SqlPlanPayload.Order)}",
        $"{nameof(TaskPlan)}.{nameof(TaskPlan.SourceQuery)}", $"{nameof(TaskPlan)}.{nameof(TaskPlan.StagingDdl)}",
        $"{nameof(TaskPlan)}.{nameof(TaskPlan.MergeSql)}", $"{nameof(TaskPlan)}.{nameof(TaskPlan.PreSql)}",
        $"{nameof(TaskPlan)}.{nameof(TaskPlan.PostSql)}", $"{nameof(TaskPlan)}.{nameof(TaskPlan.CountSql)}",
    ];

    /// <summary>Growing the exclusion list must be a visible decision: this literal fails when it changes.</summary>
    [Fact]
    public void Poison_exclusions_are_exactly_the_sql_bodies_and_order()
    {
        Assert.Equal(
            ["SqlPlanPayload.Order", "SqlPlanPayload.PostSql", "SqlPlanPayload.PreSql",
             "TaskPlan.CountSql", "TaskPlan.MergeSql", "TaskPlan.PostSql", "TaskPlan.PreSql", "TaskPlan.SourceQuery", "TaskPlan.StagingDdl"],
            Excluded.Order(StringComparer.Ordinal));
        Assert.Equal("TaskPlan.Mode", ModeKey);
    }

    const string ModeKey = $"{nameof(TaskPlan)}.{nameof(TaskPlan.Mode)}";

    /// <summary>Poisons, by reflection and recursively, every text value of the plan that is not excluded: string properties, any
    /// string sequence (List, array, ...), nested records/classes (ColumnBinding), and collections or dictionaries of them — so a field
    /// added later is hostile here without anyone remembering to list it. A property type it cannot poison fails the test.</summary>
    static SqlPlanPayload Poisoned(SqlPlanPayload plan, bool keepMode)
    {
        PoisonObject(plan, keepMode ? [.. Excluded, ModeKey] : Excluded);
        var id = plan.Order[^1];
        var hostileId = id + Hostile;
        plan.Tasks = plan.Tasks.ToDictionary(kv => kv.Key == id ? hostileId : kv.Key, kv => kv.Value, StringComparer.Ordinal);
        plan.Order[^1] = hostileId;
        return plan;
    }

    static void PoisonObject(object target, HashSet<string> excluded)
    {
        foreach (var p in target.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || excluded.Contains($"{target.GetType().Name}.{p.Name}")) continue;
            var type = p.PropertyType;
            var value = p.GetValue(target);
            if (type == typeof(string)) p.SetValue(target, (string?)value + Hostile);
            else if (Nullable.GetUnderlyingType(type) is { } inner ? IsInert(inner) : IsInert(type)) { }
            else if (type == typeof(string[])) p.SetValue(target, ((string[]?)value ?? Array.Empty<string>()).Append(Hostile).ToArray());
            else if (value is IList<string> strings && !strings.IsReadOnly) strings.Add(Hostile);
            else if (value is System.Collections.IDictionary dict) { foreach (var item in dict.Values) PoisonItem(item, excluded, p); }
            else if (value is System.Collections.IEnumerable items and not IEnumerable<string>) { foreach (var item in items) PoisonItem(item, excluded, p); }
            else if (value is null && typeof(System.Collections.IEnumerable).IsAssignableFrom(type)) { }
            else if (type.IsClass && type.Namespace?.StartsWith("Dbm.", StringComparison.Ordinal) == true) { if (value is not null) PoisonObject(value, excluded); }
            else throw new InvalidOperationException($"Poison cannot reach {target.GetType().Name}.{p.Name} ({type}); teach it, or the H2 test goes blind to that field.");
        }
    }

    static void PoisonItem(object? item, HashSet<string> excluded, System.Reflection.PropertyInfo p)
    {
        if (item is null) return;
        if (item is string) throw new InvalidOperationException($"Poison cannot append to the read-only string sequence {p.DeclaringType!.Name}.{p.Name}.");
        PoisonObject(item, excluded);
    }

    static bool IsInert(Type t) => t.IsPrimitive || t.IsEnum || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(Guid);

    /// <summary>The poisoning itself must reach nested and array-typed values, or the test above proves nothing about them.</summary>
    [Fact]
    public void Poisoning_reaches_nested_records_and_string_arrays()
    {
        var holder = new PoisonProbe { Notes = ["a"], Bindings = [new ColumnBinding("s", "t")], Nested = new PoisonProbe { Name = "n" } };
        PoisonObject(holder, []);
        Assert.Equal(["a", Hostile], holder.Notes);
        Assert.Equal(new ColumnBinding("s" + Hostile, "t" + Hostile), holder.Bindings[0]);
        Assert.Equal("n" + Hostile, holder.Nested!.Name);
        Assert.Throws<InvalidOperationException>(() => PoisonObject(new UnpoisonableProbe(), []));
    }

    public sealed class PoisonProbe
    {
        public string Name { get; set; } = "";
        public string[] Notes { get; set; } = [];
        public List<ColumnBinding> Bindings { get; set; } = [];
        public PoisonProbe? Nested { get; set; }
    }

    public sealed class UnpoisonableProbe
    {
        public Uri Where { get; set; } = new("http://example.invalid/");
    }

    /// <summary>H2: the pack is run by a DBA with production credentials. No interpolated value may end a `--` comment and start a
    /// line of its own — for any line terminator a parser or editor honours.</summary>
    [Fact]
    public void Hostile_values_never_break_out_of_a_comment_line()
    {
        var mapping = SampleMappings.Approved();
        mapping.Tables["app.Addresses"].Kind = "lookup";   // a staging_merge task exercises the staging header lines too
        var packs = new[]
        {
            ScriptPack.BuildFiles(Poisoned(Plan(), keepMode: false), Hostile),
            ScriptPack.BuildFiles(Poisoned(SqlGenerator.Generate(mapping, SampleCatalogs.Source(), SampleCatalogs.Target()), keepMode: true), Hostile),
        };
        Assert.Contains(packs[1], f => f.Content.Contains("-- Mode:            staging_merge\n", StringComparison.Ordinal));

        foreach (var (name, content) in packs.SelectMany(p => p))
        {
            Assert.Contains("CANARY_LF", content);   // the values did reach the file
            foreach (var line in content.Split(LineBreaks, StringSplitOptions.None))
                Assert.False(line.TrimStart().StartsWith("CANARY", StringComparison.Ordinal), $"{name}: a value escaped its comment: [{line}]");
        }
    }
}
