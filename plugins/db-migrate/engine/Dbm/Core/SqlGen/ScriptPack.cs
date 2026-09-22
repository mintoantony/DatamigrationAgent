using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Dbm.Core.SqlGen;

/// <summary>DBA-readable export of a plan: 00_pre.sql, one NN_schema_table.sql per task (execution order), 99_post.sql, README.md.
/// Deterministic: the same plan gives the same files in the same order and a byte-identical zip (fixed entry timestamps; nothing
/// depends on dictionary enumeration order — files follow <see cref="SqlPlanPayload.Order"/>, then any task Order misses, ordinal).
/// <para><b>The pack is run by a DBA with production credentials.</b> Only the SQL bodies (source query, staging DDL, merge, pre/post
/// statements, count query) are executable by design. Every other value — ids, target, mode, keys, dependencies, column map,
/// warnings, errors, the project name, and any field added later — is untrusted text: it reaches a <c>--</c> comment only through
/// <see cref="Comment"/>, and the README only through <see cref="OneLine"/>, so it can never end its line and start one of its own.</para></summary>
public static class ScriptPack
{
    const string Rule = "-- =====================================================================";
    /// <summary>Heading over <see cref="SqlPlanPayload.NotValidatedReasons"/>, which follow verbatim.</summary>
    const string NotValidatedTitle = "WARNING: NOT VALIDATED - no database checked this plan";
    const string MismatchTitle ="WARNING: this pack does not match the plan's execution order";
    /// <summary>Open item 19: the task file's heading over its errors. Not "last validation": Ruling 96 makes the pack derive bare
    /// carriage returns itself, and those lines come from the export's own check, not from any validation.</summary>
    const string TaskErrorsHeading = "Errors (validation and export checks)";
    /// <summary>Heading over the plan's plan-level <see cref="SqlPlanPayload.Errors"/> — stored, plus the ones the export derives — which
    /// follow verbatim. Task-level errors have their own place (the task file's <see cref="TaskErrorsHeading"/> header); without this
    /// block a plan-level error — a bare carriage return in a global statement, a connection that failed — would reach the DBA nowhere.</summary>
    const string PlanErrorsTitle = "WARNING: PLAN HAS ERRORS";
    /// <summary>Heading over the ids of the tasks whose own files carry errors. The error text is not repeated here — it lives in each
    /// task file's <see cref="TaskErrorsHeading"/> header — but without this line 00_pre.sql cannot say whether the task files hold
    /// errors at all, and a DBA who opens it alone would read that silence as "none".</summary>
    static string TaskErrorsTitle(int count) => FormattableString.Invariant($"WARNING: {count} TASK(S) HAVE ERRORS");

    /// <summary>The warning lists 00_pre.sql, 99_post.sql and README.md print, ahead of anything else, in this order. They travel
    /// together and in named fields on purpose: four <c>List&lt;string&gt;</c> parameters in a row is a transposition no compiler can
    /// catch, and a swapped pair would quietly file one warning under another's heading. There is no ready-made empty instance: every
    /// file's notes come from the checks in <see cref="BuildFiles"/>, so "nothing to say" is always a finding (open item 18).</summary>
    sealed record PackNotes
    {
        /// <summary>Every way <see cref="SqlPlanPayload.Order"/> fails to list each task exactly once.</summary>
        public required List<string> Problems { get; init; }
        /// <summary><see cref="SqlPlanPayload.NotValidatedReasons"/>: the stored <see cref="SqlPlanSource.SkippedPrefix"/> line(s), or
        /// <see cref="SqlModule.NoEvidence"/> for a version with no validation evidence.</summary>
        public required List<string> NotValidated { get; init; }
        /// <summary>The plan's stored plan-level <see cref="SqlPlanPayload.Errors"/>.</summary>
        public required List<string> PlanErrors { get; init; }
        /// <summary>"T04 (app.Addresses): 9 error(s)" per errored task; the error text itself stays in the task file.</summary>
        public required List<string> TaskErrors { get; init; }
    }

    public static byte[] BuildZip(SqlPlanPayload plan, string projectName)
    {
        var files = BuildFiles(plan, projectName);
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                w.Write(content);
            }
        }
        return ms.ToArray();
    }

    /// <summary>File name → content, in zip order. A plan whose Order does not list every task exactly once still exports every
    /// task (the ones Order misses after the ordered ones) and says so, loudly, in 00_pre.sql and README.md.</summary>
    public static List<(string Name, string Content)> BuildFiles(SqlPlanPayload plan, string projectName)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(projectName);
        var problems = plan.OrderProblems();
        var notValidated = plan.NotValidatedReasons();   // Ruling 204: from the evidence, exactly as ApprovalBlockers reads it
        var exported = new HashSet<string>(StringComparer.Ordinal);   // each task exactly once, at its first position in Order
        var ordered = plan.Order.Select(id => (Id: id, Known: plan.Tasks.ContainsKey(id) && exported.Add(id))).ToList();
        var listed = new HashSet<string>(plan.Order, StringComparer.Ordinal);
        var slots = ordered.Concat(plan.Tasks.Keys.Where(k => !listed.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).Select(k => (Id: k, Known: true))).ToList();

        var width = Math.Max(2, (slots.Count + 1).ToString(CultureInfo.InvariantCulture).Length);
        var preName = new string('0', width) + "_pre.sql";
        var postName = new string('9', width) + "_post.sql";
        // Ruling 96: bare carriage returns are DERIVED here, exactly as SqlModule.ApprovalBlockers derives them, never read from
        // stored evidence alone. Nothing on the export path checks approval, so a version stored without the scan would otherwise
        // export a pack that says nothing at all while shipping the hidden statement verbatim. The scan is pure and offline.
        var derived = SqlValidator.FindBareCarriageReturns(plan);
        var planErrors = WithDerived(plan.Errors, derived, "plan");
        var errorsOf = slots.Where(s => s.Known).ToDictionary(s => s.Id, s => WithDerived(plan.Tasks[s.Id].Errors, derived, s.Id), StringComparer.Ordinal);

        // "T04 (app.Addresses): 9 error(s)" per errored task, in pack-file order: enough to triage, while the error text itself
        // stays in the task file.
        var taskErrors = slots.Where(s => s.Known && errorsOf[s.Id].Count > 0)
            .Select(s => FormattableString.Invariant($"{s.Id} ({plan.Tasks[s.Id].Target}): {errorsOf[s.Id].Count} error(s)")).ToList();
        var notes = new PackNotes { Problems = problems, NotValidated = notValidated, PlanErrors = planErrors, TaskErrors = taskErrors };
        var files = new List<(string, string)> { (preName, GlobalFile(projectName, preName, "pre-load", "before the first task", plan.PreSql, notes)) };
        var index = new List<(string File, string Id, TaskPlan Task)>();
        for (var i = 0; i < slots.Count; i++)
        {
            if (!slots[i].Known) continue;
            var id = slots[i].Id;
            var task = plan.Tasks[id];
            var name = $"{(i + 1).ToString("D" + width.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)}_{FileSafe(task.Target.Replace('.', '_'))}.sql";
            files.Add((name, TaskFile(projectName, id, task, errorsOf[id])));
            index.Add((name, id, task));
        }
        // Open item 18: the post file carries the same roll-up as the pre file. It runs after every task, and a DBA who opens it alone
        // must not read "nothing to report" from notes nobody constructed.
        files.Add((postName, GlobalFile(projectName, postName, "post-load", "after the last task", plan.PostSql, notes)));
        files.Add(("README.md", Readme(projectName, preName, postName, index, notes)));
        return files;
    }

    /// <summary>Every line terminator a T-SQL parser or an editor may honour becomes a space.</summary>
    public static string OneLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (chars[i] is '\r' or '\n' or '\v' or '\f' or '\u0085' or '\u2028' or '\u2029') chars[i] = ' ';
        return new string(chars);
    }

    /// <summary>The ONLY way an interpolated value reaches a <c>--</c> comment line.</summary>
    static StringBuilder Comment(StringBuilder sb, string text) => sb.Append("-- ").Append(OneLine(text)).Append('\n');

    static string GlobalFile(string project, string name, string what, string when, List<string> statements, PackNotes notes)
    {
        var sb = new StringBuilder();
        sb.Append(Rule).Append('\n');
        Comment(sb, $"{project}: {name} - global {what} statements");
        Comment(sb, $"runs on TARGET, {when}");
        if (notes.NotValidated.Count > 0)
        {
            Comment(sb, NotValidatedTitle);
            foreach (var line in notes.NotValidated) Comment(sb, line);   // the stored line verbatim, flattened
        }
        if (notes.PlanErrors.Count > 0)
        {
            Comment(sb, $"{PlanErrorsTitle} (see README.md)");
            foreach (var e in notes.PlanErrors) Comment(sb, "  - " + e);   // the stored line verbatim, flattened
        }
        if (notes.TaskErrors.Count > 0)
        {
            Comment(sb, $"{TaskErrorsTitle(notes.TaskErrors.Count)} (see each task file's header)");
            foreach (var t in notes.TaskErrors) Comment(sb, "  - " + t);
        }
        if (notes.Problems.Count > 0)
        {
            Comment(sb, $"{MismatchTitle} (see README.md)");
            foreach (var p in notes.Problems) Comment(sb, "  - " + p);
        }
        sb.Append(Rule).Append("\n\n");
        if (statements.Count == 0) Comment(sb, $"(no global {what} statements)");
        foreach (var s in statements) sb.Append(s.TrimEnd()).Append("\nGO\n\n");
        return sb.ToString();
    }

    /// <summary>Stored lines, then every derived line of that scope the stored ones do not already record. Duplicates among the
    /// derived lines are kept: two carriage returns on one line are two occurrences, and <see cref="SqlModule.ApprovalBlockers"/>
    /// counts them the same way.</summary>
    static List<string> WithDerived(List<string> stored, List<(string Scope, string Line)> derived, string scope)
    {
        var listed = new HashSet<string>(stored, StringComparer.Ordinal);
        return [.. stored, .. derived.Where(f => f.Scope == scope && !listed.Contains(f.Line)).Select(f => f.Line)];
    }

    static string TaskFile(string project, string id, TaskPlan t, List<string> errors)
    {
        var sb = new StringBuilder();
        sb.Append(Rule).Append('\n');
        Comment(sb, $"{project}: task {id}  {t.Target}");
        Comment(sb, $"Mode:            {t.Mode}");
        Comment(sb, $"Keys:            {(t.KeyColumns.Count == 0 ? "(none - the table loads in a single transaction)" : string.Join(", ", t.KeyColumns))}");
        Comment(sb, $"Depends on:      {(t.DependsOn.Count == 0 ? "(none)" : string.Join(", ", t.DependsOn))}");
        Comment(sb, $"Identity insert: {(t.IdentityInsert ? "yes" : "no")}");
        Comment(sb, $"Chunk size:      {(t.ChunkSize?.ToString(CultureInfo.InvariantCulture) ?? "run default")}");
        Comment(sb, $"Custom SQL:      {(t.Custom ? "yes" : "no")}");
        AppendList(sb, "Warnings", t.Warnings);
        AppendList(sb, TaskErrorsHeading, errors);
        sb.Append(Rule).Append("\n\n");

        if (t.PreSql.Count > 0)
        {
            sb.Append("-- ---- Task pre-load ----\n-- runs on TARGET\n");
            foreach (var s in t.PreSql) sb.Append(s.TrimEnd()).Append("\nGO\n");
            sb.Append('\n');
        }

        sb.Append("-- ---- Source query ----\n-- runs on SOURCE\n")
          .Append("-- dbm reads this result set on the source server and streams it to the target with SqlBulkCopy.\n")
          .Append("-- Chunks are keyset ranges on the key aliases; aliases that are not target columns are not loaded.\n")
          .Append(t.SourceQuery.TrimEnd()).Append("\nGO\n\n");

        sb.Append("-- ---- Load ----\n-- runs on TARGET\n");
        var map = string.Join(", ", t.Columns.Select(c => c.Source == c.Target ? c.Target : $"{c.Source} -> {c.Target}"));
        if (t.Mode == "staging_merge")
        {
            sb.Append("-- Each chunk is bulk-copied into #stg, then the merge statement runs in the same transaction.\n");
            Comment(sb, $"Columns: {map}");
            sb.Append((t.StagingDdl ?? "-- (missing staging DDL)").TrimEnd()).Append("\nGO\n")
              .Append((t.MergeSql ?? "-- (missing merge SQL)").TrimEnd()).Append("\nGO\n\n");
        }
        else
        {
            Comment(sb, $"Each chunk is bulk-copied straight into {t.Target}{(t.IdentityInsert ? " (KeepIdentity)" : "")}.");
            Comment(sb, $"Columns: {map}").Append('\n');
        }

        if (t.PostSql.Count > 0)
        {
            sb.Append("-- ---- Task post-load ----\n-- runs on TARGET\n");
            foreach (var s in t.PostSql) sb.Append(s.TrimEnd()).Append("\nGO\n");
            sb.Append('\n');
        }

        sb.Append("-- ---- Validation ----\n-- runs on SOURCE: expected row count, compared with the rows the run added to the target\n")
          .Append(t.CountSql.TrimEnd()).Append(";\nGO\n");
        return sb.ToString();
    }

    static void AppendList(StringBuilder sb, string title, List<string> items)
    {
        if (items.Count == 0) return;
        Comment(sb, $"{title}:");
        foreach (var i in items) Comment(sb, "  - " + i);
    }

    static string Readme(string project, string preName, string postName, List<(string File, string Id, TaskPlan Task)> index, PackNotes notes)
    {
        var sb = new StringBuilder();
        sb.Append($"# {OneLine(project)} - migration script pack\n\n");
        if (notes.NotValidated.Count > 0)
        {
            sb.Append($"## {NotValidatedTitle}\n\n");
            foreach (var line in notes.NotValidated) sb.Append(OneLine(line)).Append('\n');
            sb.Append('\n');
        }
        if (notes.PlanErrors.Count > 0)
        {
            sb.Append($"## {PlanErrorsTitle}\n\n")
              .Append("Validation and the export's own checks recorded these plan-level errors - in the global statements, or in reaching the databases at all.\n")
              .Append("They are listed here because no task file holds them. Do not run this pack as-is.\n\n");
            foreach (var e in notes.PlanErrors) sb.Append("- ").Append(OneLine(e)).Append('\n');
            sb.Append('\n');
        }
        if (notes.TaskErrors.Count > 0)
        {
            sb.Append($"## {TaskErrorsTitle(notes.TaskErrors.Count)}\n\n")
              .Append("These tasks carry errors from validation and the export's own checks. Each error is listed in that task's own file, under\n")
              .Append($"*{TaskErrorsHeading}* in its header. Do not run this pack as-is.\n\n");
            foreach (var t in notes.TaskErrors) sb.Append("- ").Append(OneLine(t)).Append('\n');
            sb.Append('\n');
        }
        if (notes.Problems.Count > 0)
        {
            sb.Append($"## {MismatchTitle}\n\n")
              .Append("The plan's order does not list every task exactly once. Every task is still exported: tasks missing from the order\n")
              .Append("come after the ordered ones, in task-id order, and their real position in the run is unknown. Do not run this pack as-is.\n\n");
            foreach (var p in notes.Problems) sb.Append("- ").Append(OneLine(p)).Append('\n');
            sb.Append('\n');
        }
        sb.Append("Generated by **dbm** from the SQL plan. These files are for review, audit and rehearsal; the dbm engine runs the migration itself.\n\n")
          .Append("## How dbm moves the data\n\n")
          .Append("Source and target may be on different servers and **no linked server is used**. For every task dbm runs the *source query* on the\n")
          .Append("source server, streams the rows through its own process and writes them to the target with `SqlBulkCopy`. Scripts marked\n")
          .Append("`-- runs on SOURCE` are executed on the source database; everything else runs on the target database.\n\n")
          .Append("## Execution order\n\n")
          .Append($"1. `{preName}` - global statements on the target (for example disabling FK constraints that form a cycle).\n")
          .Append("2. One file per task, in file-name order. A task starts only when the tasks in its *Depends on* list have finished;\n")
          .Append("   independent tasks run in parallel. Inside a task: task pre-load -> source query -> load -> task post-load.\n")
          .Append("   - `direct`: each chunk is bulk-copied straight into the target table (identity values kept when *Identity insert* is yes).\n")
          .Append("   - `staging_merge`: each chunk is bulk-copied into the session table `#stg`, then the merge statement moves it into the target\n")
          .Append("     in the same transaction.\n")
          .Append($"3. `{postName}` - global statements after the last task (for example re-enabling constraints `WITH CHECK`).\n\n")
          .Append("## Chunks, checkpoints and resume\n\n")
          .Append("The key aliases (`__k0`, `__k1`, ...) are the source key. dbm reads keyset chunks (`WHERE key > last ORDER BY key`) and commits\n")
          .Append("each chunk together with its checkpoint row in `dbo.__dbm_checkpoint` on the target, so a paused or crashed run resumes at the\n")
          .Append("last committed chunk. Tasks without a key load in a single transaction and restart from scratch.\n\n")
          .Append("## Validation\n\n")
          .Append("Each task file ends with a row-count query that runs on the source; dbm compares it with the rows the run added to the target.\n\n")
          .Append("## Files\n\n")
          .Append("| File | Task | Target | Mode | Depends on |\n|---|---|---|---|---|\n");
        foreach (var (file, id, t) in index)
            sb.Append($"| `{file}` | {Cell(id)} | {Cell(t.Target)} | {Cell(t.Mode)} | {(t.DependsOn.Count == 0 ? "-" : Cell(string.Join(", ", t.DependsOn)))} |\n");
        return sb.ToString();
    }

    static string Cell(string text) => OneLine(text).Replace("|", "\\|", StringComparison.Ordinal);

    static string FileSafe(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray();
        return new string(chars);
    }
}
