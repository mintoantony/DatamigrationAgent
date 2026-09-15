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
    const string MismatchTitle = "WARNING: this pack does not match the plan's execution order";

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
        var ordered = plan.Order.Select(id => (Id: id, Known: plan.Tasks.ContainsKey(id))).ToList();
        var listed = new HashSet<string>(plan.Order, StringComparer.Ordinal);
        var slots = ordered.Concat(plan.Tasks.Keys.Where(k => !listed.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).Select(k => (Id: k, Known: true))).ToList();

        var width = Math.Max(2, (slots.Count + 1).ToString(CultureInfo.InvariantCulture).Length);
        var preName = new string('0', width) + "_pre.sql";
        var postName = new string('9', width) + "_post.sql";
        var files = new List<(string, string)> { (preName, GlobalFile(projectName, preName, "pre-load", "before the first task", plan.PreSql, problems)) };
        var index = new List<(string File, string Id, TaskPlan Task)>();
        for (var i = 0; i < slots.Count; i++)
        {
            if (!slots[i].Known) continue;
            var id = slots[i].Id;
            var task = plan.Tasks[id];
            var name = $"{(i + 1).ToString("D" + width.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)}_{FileSafe(task.Target.Replace('.', '_'))}.sql";
            files.Add((name, TaskFile(projectName, id, task)));
            index.Add((name, id, task));
        }
        files.Add((postName, GlobalFile(projectName, postName, "post-load", "after the last task", plan.PostSql, [])));
        files.Add(("README.md", Readme(projectName, preName, postName, index, problems)));
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

    static string GlobalFile(string project, string name, string what, string when, List<string> statements, List<string> problems)
    {
        var sb = new StringBuilder();
        sb.Append(Rule).Append('\n');
        Comment(sb, $"{project}: {name} - global {what} statements");
        Comment(sb, $"runs on TARGET, {when}");
        if (problems.Count > 0)
        {
            Comment(sb, $"{MismatchTitle} (see README.md)");
            foreach (var p in problems) Comment(sb, "  - " + p);
        }
        sb.Append(Rule).Append("\n\n");
        if (statements.Count == 0) Comment(sb, $"(no global {what} statements)");
        foreach (var s in statements) sb.Append(s.TrimEnd()).Append("\nGO\n\n");
        return sb.ToString();
    }

    static string TaskFile(string project, string id, TaskPlan t)
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
        AppendList(sb, "Errors (last validation)", t.Errors);
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

    static string Readme(string project, string preName, string postName, List<(string File, string Id, TaskPlan Task)> index, List<string> problems)
    {
        var sb = new StringBuilder();
        sb.Append($"# {OneLine(project)} - migration script pack\n\n");
        if (problems.Count > 0)
        {
            sb.Append($"## {MismatchTitle}\n\n")
              .Append("The plan's order does not list every task exactly once. Every task is still exported: tasks missing from the order\n")
              .Append("come after the ordered ones, in task-id order, and their real position in the run is unknown. Do not run this pack as-is.\n\n");
            foreach (var p in problems) sb.Append("- ").Append(OneLine(p)).Append('\n');
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
