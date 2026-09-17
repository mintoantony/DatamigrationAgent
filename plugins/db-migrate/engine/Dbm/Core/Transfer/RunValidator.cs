using System.Globalization;
using Dbm.Core.Sql;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

/// <summary>One column compared end to end. <see cref="Source"/> and <see cref="Target"/> are null when the SUM was NULL, which is what
/// an empty table gives: two nulls are a match between two empty tables, and one null against a number is not.</summary>
public sealed record ChecksumResult(string Column, bool Match, long? Source, long? Target);

/// <summary>
/// What validation established about one task. Every field that can be absent says so rather than defaulting:
/// <see cref="RowsSource"/> and <see cref="RowsBefore"/> are nullable because a 0 there would mean both "the table was empty" and
/// "nobody ever counted", and <see cref="ChecksumsSkipped"/> exists so that an empty <see cref="Checksums"/> list can say why it is
/// empty.
/// </summary>
/// <param name="CountMatch">True only when the comparison was actually made and balanced. False therefore covers two different facts -
/// the numbers disagree, or there were no numbers to compare - which is what <see cref="CountCompared"/> and <see cref="CountNote"/>
/// keep apart. It is never true by default.</param>
/// <param name="RowsSource">Rows at the source, or null when this run never established one. See <see cref="RowsSourceNote"/> for where
/// the number came from: the run's own snapshot, or a later count taken by validation itself.</param>
/// <param name="RowsBefore">Rows already in the target when the task started, or null when the run never recorded it.</param>
public sealed record TaskValidation(bool CountMatch, long? RowsSource, long RowsError, long? RowsBefore, long RowsAfter,
    List<ChecksumResult> Checksums, string? ChecksumsSkipped)
{
    /// <summary>False when the row-count comparison could not be made at all. <see cref="CountMatch"/> is false then too - "not
    /// confirmed" - and this is what says the difference between that and a genuine mismatch.</summary>
    public bool CountCompared { get; init; } = true;

    /// <summary>Set whenever <see cref="CountMatch"/> is false: the arithmetic that did not balance, or the reason there was none.</summary>
    public string? CountNote { get; init; }

    /// <summary>Set when <see cref="RowsSource"/> is not the snapshot the run took when the task started (F10) - because there was no
    /// snapshot, so validation counted the source afterwards, or because even that produced no number.</summary>
    public string? RowsSourceNote { get; init; }

    /// <summary>Set when some bound columns were left out of the checksum comparison. "checksums 1/1 matched" over a table whose other
    /// columns nobody could compare is the same green light as no validation at all, so the drop has to be visible.</summary>
    public string? ChecksumColumnsNote { get; init; }

    /// <summary>How many bound columns <see cref="ChecksumColumnsNote"/> is about, as a number, so the report's one-line headline can
    /// carry the shortfall too - the sentence alone only reaches a reader who scrolls to the notes.</summary>
    public int ChecksumColumnsNotCompared { get; init; }
}

/// <summary>Source alias in the task's SourceQuery, the target column it is bound to, and the target's type as SQL text.</summary>
public sealed record ChecksumColumn(string Source, string Target, string TypeText);

/// <summary>
/// Post-run checks (spec section 9 Validation): counts always, column checksums when the target started empty and nothing was rejected.
/// <para><b>Validation reads; it does not run.</b> Every entry point here works on connections the caller opened and opens none of its
/// own, takes no <see cref="RunLock"/> and is refused by none - a finished run's report must be renderable while another run against the
/// same target is in flight (ruling 103 governs runners, not readers).</para>
/// <para>Nothing here judges a merge count: the merge status is the loader's and the runner's business (ruling 89). What this class
/// does instead is the net rulings 73/74 promised - the counts and the values themselves are compared, whatever a merge reported.</para>
/// </summary>
public static class RunValidator
{
    /// <summary>The one text a report uses for a task whose validation never ran. One string, one meaning: not "nothing was wrong".</summary>
    public const string NoValidation = "no validation was recorded for this task";

    /// <summary>Seconds allowed for the source recount, matching <see cref="Preflight.SourceEstimateAsync"/>'s bound on the identical
    /// query. Both run plan-supplied <c>CountSql</c>; this one runs inside the completion hook with the run lock held.</summary>
    public const int RecountTimeoutSec = 120;

    private static readonly AsyncLocal<int?> RecountTimeoutOverride = new();

    /// <summary>
    /// Test seam for <see cref="RecountTimeoutSec"/>. A bound of 120 s cannot be witnessed in a test without waiting two minutes, so
    /// without this the bound is a constant nobody can prove is applied - and dropping it restores an unbounded query inside the
    /// completion hook while the note still tells the operator it failed within 120 s. Held in an <see cref="AsyncLocal{T}"/> so one
    /// test's override cannot reach another test's run, and null in production.
    /// </summary>
    internal static int? RecountTimeoutSecOverride
    {
        get => RecountTimeoutOverride.Value;
        set => RecountTimeoutOverride.Value = value;
    }

    /// <summary>The bound actually applied. The note's wording is derived from this same value, so the sentence cannot claim a bound
    /// the query was not given.</summary>
    private static int RecountTimeout => RecountTimeoutSecOverride ?? RecountTimeoutSec;

    /// <summary>
    /// System scalar types BINARY_CHECKSUM compares exactly after V8 normalisation. text/ntext/image/xml/spatial/hierarchyid/sql_variant/
    /// rowversion and UDTs are out because BINARY_CHECKSUM either refuses them or is documented not to be value-faithful for them.
    /// <para><c>datetime</c> is deliberately absent although it is a system scalar type: <see cref="TargetShape.Normalize"/> rounds
    /// smalldatetime, datetime2, datetimeoffset and time to match the server's CAST before the load, but it does not round for
    /// <c>datetime</c>'s 1/300-second grid - so a checksum over a datetime column could not claim to be exact, and a checksum that can
    /// be wrong is worse than none. Columns left out this way are named in <see cref="TaskValidation.ChecksumColumnsNote"/>.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> ChecksumTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "bigint", "int", "smallint", "tinyint", "bit", "decimal", "numeric", "money", "smallmoney", "float", "real",
        "date", "datetime2", "smalldatetime", "datetimeoffset", "time",
        "char", "varchar", "nchar", "nvarchar", "binary", "varbinary", "uniqueidentifier",
    };

    /// <summary>
    /// Counts always, checksums when they can mean something. Never throws on a comparison it cannot make: it records why instead.
    /// It does throw if the target cannot be read at all (the table is gone, the connection is dead) - the caller decides what that
    /// means, and <see cref="TransferEngine"/>'s caller turns it into a note rather than a failed run.
    /// </summary>
    public static async Task<TaskValidation> ValidateTaskAsync(SqlConnection src, SqlConnection tgt, TaskPlan task, TransferTaskRow row,
        bool checksums, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(tgt);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(row);

        long after = await TargetOps.CountTargetAsync(tgt, task.Target, ct);

        // rows_source is a snapshot taken when the task's segment started and never recounted on resume (5.3 review F10, correct keyset
        // semantics). Null means this run never took one - PrepareAsync was interrupted, or the plan's CountSql yielded no number. A
        // count taken now is a different, later figure and is labelled as one; it is never silently substituted for the snapshot.
        long? source = row.RowsSource;
        string? sourceNote = null;
        if (source is null)
        {
            try
            {
                // Bounded, like SourceEstimateAsync's identical query: this is plan-supplied SQL running inside the completion hook
                // with the run lock held, and an unbounded wait there is a run nobody can finish and nobody can cancel softly.
                source = await TargetOps.ScalarLongOrNullAsync(src, TargetOps.CountSqlOf(task), ct, timeoutSec: RecountTimeout);
                sourceNote = source is null
                    ? "the source row count is unknown: this run never recorded one, and counting the source now produced no number either"
                    : "counted after the run, not the snapshot this run takes when a task starts - the source may have changed in between";
            }
            catch (Exception ex) when (ex is SqlException or TimeoutException)
            {
                // Reported, not thrown: the count is one input to the report, and losing it must not lose the rest of the validation.
                source = null;
                // The bound in this sentence is read from the same place the command was given it, so it cannot claim one the query
                // never had.
                sourceNote = $"the source row count is unknown: this run never recorded one, and counting it now failed within "
                             + $"{RecountTimeout} s ({TransferFailure.NonBlank(ex.Message, ex.GetType().Name)})";
            }
        }

        // A task that has not finished holds part of a load, so neither its counts nor its checksums can mean anything yet: reporting
        // either would be a finding against the data where there is none, and a false alarm costs the same investigation as a real one.
        bool finished = row.Status == TransferTaskStatus.Done;
        long? before = row.RowsBefore;
        bool compared = finished && source is not null && before is not null;
        long expected = compared ? source!.Value - row.RowsError : 0;
        long found = compared ? after - before!.Value : 0;
        bool match = compared && expected == found;
        string? countNote =
            !compared ? "the row counts could not be compared: " + (!finished
                ? $"the task did not finish (it is {EnumText.ToText(row.Status)}), so the target is not expected to hold all its rows yet"
                : source is null
                ? "no source row count was ever established for this task"
                : "the target's row count before the run was never recorded, so the rows this run added cannot be told apart from rows "
                  + "that were already there")
            : match ? null
            : string.Create(CultureInfo.InvariantCulture,
                $"expected {expected:N0} new rows ({source!.Value:N0} at source less {row.RowsError:N0} rejected), found {found:N0} "
                + $"({after:N0} in the target now, {before!.Value:N0} before the run)");

        var results = new List<ChecksumResult>();
        string? columnsNote = null;
        int notCompared = 0;
        // Every skip route sets a reason, including the ones that are not about the option: an empty Checksums list must never be able
        // to mean "compared and found nothing to say".
        string? skipped =
            !checksums ? "disabled"
            : !finished
                ? $"the task did not finish (it is {EnumText.ToText(row.Status)}), so the target holds part of a load"
            : before is null ? "the target's row count before the run was never recorded, so the table cannot be shown to have started empty"
            : before.Value != 0 ? "target table was not empty before the run"
            : row.RowsError != 0 ? "rows were rejected"
            : null;
        if (skipped is null)
        {
            var shape = await TargetShape.LoadAsync(tgt, task.Target, ct);
            var cols = ChecksumColumns(task, shape);
            var uncomparable = UncomparableColumns(task, shape);
            notCompared = uncomparable.Count;
            if (uncomparable.Count > 0)
                columnsNote = string.Create(CultureInfo.InvariantCulture,
                    $"{uncomparable.Count} of {task.Columns.Count} bound columns were not compared: {string.Join(", ", uncomparable)}.");
            if (cols.Count == 0)
            {
                skipped = "no comparable columns";
            }
            else
            {
                try
                {
                    var s = await SumsAsync(src, SourceChecksumSql(task, cols), cols.Count, ct);
                    var t = await SumsAsync(tgt, TargetChecksumSql(task.Target, cols), cols.Count, ct);
                    for (int i = 0; i < cols.Count; i++) results.Add(new ChecksumResult(cols[i].Target, s[i] == t[i], s[i], t[i]));
                }
                catch (SqlException ex)
                {
                    // A half-finished comparison is worse than none: two of five columns "matched" reads as a verdict on the table.
                    results.Clear();
                    skipped = "checksum query failed: " + TransferFailure.NonBlank(ex.Message, ex.GetType().Name);
                }
            }
        }

        return new TaskValidation(match, source, row.RowsError, before, after, results, skipped)
        {
            CountCompared = compared,
            CountNote = countNote,
            RowsSourceNote = sourceNote,
            ChecksumColumnsNote = columnsNote,
            ChecksumColumnsNotCompared = notCompared,
        };
    }

    /// <summary>The bound columns BINARY_CHECKSUM can compare exactly. What it leaves out is <see cref="UncomparableColumns"/>'s answer,
    /// and one is useless without the other.</summary>
    public static List<ChecksumColumn> ChecksumColumns(TaskPlan task, TargetShape shape)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(shape);
        var list = new List<ChecksumColumn>();
        foreach (var b in task.Columns)
        {
            var c = shape.Find(b.Target);
            if (c is null || c.IsComputed || !ChecksumTypes.Contains(c.DataType)) continue;
            list.Add(new ChecksumColumn(b.Source, c.Name, c.TypeText));
        }
        return list;
    }

    /// <summary>The bound columns <see cref="ChecksumColumns"/> dropped, each with the reason. Without this the comparison silently
    /// narrows to whatever it happened to be able to do, and reports a clean result over it.</summary>
    public static List<string> UncomparableColumns(TaskPlan task, TargetShape shape)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(shape);
        var list = new List<string>();
        foreach (var b in task.Columns)
        {
            var c = shape.Find(b.Target);
            string? why =
                c is null ? "not a column of the target"
                : c.IsComputed ? "computed"
                : !ChecksumTypes.Contains(c.DataType) ? c.DataType + " cannot be checksummed exactly"
                : null;
            if (why is not null) list.Add($"{b.Target} ({why})");
        }
        return list;
    }

    /// <summary>
    /// The source side, over the task's own SourceQuery so that every expression, join and filter the plan applies is included. Each
    /// value is CAST to the target's type first, which is exactly what the load did to it (V8/V9), so a match is a real match rather
    /// than an artefact of two different type representations.
    /// </summary>
    public static string SourceChecksumSql(TaskPlan task, IReadOnlyList<ChecksumColumn> cols)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(cols);
        return "SELECT " + string.Join(", ", cols.Select((c, i) =>
                   $"SUM(CAST(BINARY_CHECKSUM(CAST(q.{SqlQuote.Ident(c.Source)} AS {c.TypeText})) AS bigint)) AS [c{i}]"))
               // The newline before ") AS q" is load-bearing: a "--" comment at the end of the plan's query runs to the next line break,
               // and without it the closing parenthesis would be swallowed by it.
               + $" FROM (\n{(task.SourceQuery ?? "").Trim().TrimEnd(';').TrimEnd()}\n) AS q";
    }

    public static string TargetChecksumSql(string targetKey, IReadOnlyList<ChecksumColumn> cols)
    {
        ArgumentNullException.ThrowIfNull(targetKey);
        ArgumentNullException.ThrowIfNull(cols);
        return "SELECT " + string.Join(", ", cols.Select((c, i) => $"SUM(CAST(BINARY_CHECKSUM({SqlQuote.Ident(c.Target)}) AS bigint)) AS [c{i}]"))
               + $" FROM {SqlQuote.TableKey(targetKey)}";
    }

    private static async Task<long?[]> SumsAsync(SqlConnection conn, string sql, int count, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var result = new long?[count];
        if (await r.ReadAsync(ct))
            for (int i = 0; i < count; i++) result[i] = r.IsDBNull(i) ? null : r.GetInt64(i);
        return result;
    }
}
