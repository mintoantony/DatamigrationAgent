namespace Dbm.Core.SqlGen;

public sealed record ListingLine(int No, string Section, string Text);

/// <summary>
/// The canonical line numbering of ONE task's SQL. It is the meaning of a feedback anchor <c>sql:&lt;taskId&gt;:&lt;line&gt;</c>:
/// the browser (views/sql.js, <c>DBM.sqlView.listing</c>, T4.5) numbers lines to create the anchor and the engine
/// (<see cref="SqlModule.ParseAnchor"/> + this class) numbers them to resolve it. The two implementations MUST agree exactly;
/// this comment is the shared definition. Mirror it rule by rule, not by eye.
/// <list type="number">
/// <item><b>Inputs</b> are the fields of the task only (never the plan-level global preSql/postSql), in this fixed order, each
/// with its section name: every element of <c>task.preSql</c> in list order → <c>"pre"</c>; <c>task.sourceQuery</c> →
/// <c>"source"</c>; <c>task.stagingDdl</c> → <c>"staging"</c>; <c>task.mergeSql</c> → <c>"merge"</c>; every element of
/// <c>task.postSql</c> in list order → <c>"post"</c>.</item>
/// <item><b>Skip rule:</b> a text that is null/undefined or the empty string <c>""</c> contributes no lines. Nothing else is skipped:
/// a whitespace-only text (e.g. <c>" "</c> or <c>"\n"</c>) is NOT empty and is numbered. Each list element is tested on its own,
/// so an empty element in the middle of preSql is skipped without affecting its neighbours.</item>
/// <item><b>Newline normalisation:</b> replace every <c>"\r\n"</c> with <c>"\n"</c> (JS: <c>text.split("\r\n").join("\n")</c> or
/// <c>replace(/\r\n/g, "\n")</c>). A lone <c>"\r"</c> is NOT a line break and stays in the text.</item>
/// <item><b>Splitting:</b> split the normalised text on <c>"\n"</c>, keeping empty pieces (C# <c>Split('\n')</c> = JS
/// <c>split("\n")</c>). A trailing newline therefore yields a final empty line, which is numbered. No trimming of any line.</item>
/// <item><b>Numbering:</b> 1-based and continuous across every section and every list element: no separator lines, no restart per
/// section or per statement. The first line of the first non-skipped text is 1.</item>
/// <item><b>Lines</b> are compared by UTF-16 content only; no other transformation (no tab expansion, no Unicode line separators).</item>
/// </list>
/// <see cref="Render"/> is the text form used in work packets (the browser renders its own markup from the same numbers).
/// </summary>
public static class TaskListing
{
    public static List<ListingLine> Build(TaskPlan task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var lines = new List<ListingLine>();
        var no = 0;
        foreach (var s in task.PreSql ?? []) Add("pre", s);
        Add("source", task.SourceQuery);
        Add("staging", task.StagingDdl);
        Add("merge", task.MergeSql);
        foreach (var s in task.PostSql ?? []) Add("post", s);
        return lines;

        void Add(string section, string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')) lines.Add(new ListingLine(++no, section, line));
        }
    }

    /// <summary>One line per <see cref="ListingLine"/>, joined with "\n": the number right-aligned in 4 columns, a space, the section
    /// left-aligned in 7 columns, a space, the marker (">>" on the highlighted line, "| " on every other line), a space, the text —
    /// e.g. <c>"   9 source  &gt;&gt; FROM [dbo].[ADDR] AS s"</c>. A highlight that matches no line marks nothing.</summary>
    public static string Render(TaskPlan task, int? highlight = null) =>
        string.Join("\n", Build(task).Select(l =>
            FormattableString.Invariant($"{l.No,4} {l.Section,-7} {(l.No == highlight ? ">>" : "| ")} {l.Text}")));
}
