using System.Globalization;
using Dbm.Core.State;

namespace Dbm.Core.Transfer;

/// <summary>One rejected row, as shown to an operator. A null <see cref="Key"/> means the task had no key at all; a keyed task whose key
/// could not be encoded carries the reason inside <see cref="Error"/> instead (5.3 <c>TaskRunner.Capture</c>), so null keeps the two
/// meanings it already had and gains no third.</summary>
public sealed record ErrorSample(string? Key, string Error)
{
    /// <summary>The row's SQL Server error number (Ruling 208); null - and so absent from the JSON - when the row was recorded without
    /// one: its failure carried none, or it was recorded before the state database stored numbers.</summary>
    public int? ErrorNumber { get; init; }
}

/// <summary>
/// One task in the final report. The nullable fields are absences, and each one has a sibling that says why it is absent, because
/// <c>Json.Options</c> is <c>WhenWritingNull</c>: a null field is not a field at all in the JSON, so nothing may be left to a null.
/// </summary>
/// <param name="RowsSource">Rows at the source, or null when the run never established one - never 0, which would say the source table
/// was empty.</param>
/// <param name="DurationSec">Null when the task has no start or no end time; 0 means it really took no measurable time.</param>
/// <param name="CountMatch">Null means one thing only: <b>no validation was recorded for this task</b> (see <see cref="CountNote"/>).
/// False means validation ran and did not confirm the counts - <see cref="CountCompared"/> says whether that is a mismatch or an
/// impossible comparison.</param>
public sealed record TaskReport(string TaskId, string Target, TransferTaskStatus Status, long? RowsSource, long RowsLoaded, long RowsError,
    double? DurationSec, bool? CountMatch, List<ChecksumResult> Checksums, string? ChecksumsSkipped, List<ErrorSample> ErrorSamples,
    string? Error)
{
    /// <summary>False when no row-count comparison was made - either none was recorded, or validation could not make one.</summary>
    public bool CountCompared { get; init; }

    /// <summary>Why <see cref="CountMatch"/> is not a plain true: the arithmetic that did not balance, the reason no comparison could be
    /// made, or <see cref="RunValidator.NoValidation"/>.</summary>
    public string? CountNote { get; init; }

    /// <summary>Where <see cref="RowsSource"/> came from, when it is not the snapshot the run took as the task started.</summary>
    public string? RowsSourceNote { get; init; }

    /// <summary>The bound columns no checksum could cover, and why.</summary>
    public string? ChecksumColumnsNote { get; init; }

    /// <summary>How many of them there are, so <see cref="FinalReportBuilder.Summary"/> can carry the shortfall in its one line.</summary>
    public int ChecksumColumnsNotCompared { get; init; }

    /// <summary>Why <see cref="ErrorSamples"/> holds fewer rows than <see cref="RowsError"/> counts (ruling 105).</summary>
    public string? ErrorSamplesNote { get; init; }

    /// <summary>What <see cref="Status"/> means once the run's own status is taken into account - a "paused" task under a cancelled run
    /// was not paused by an operator (5.3 review F9).</summary>
    public string? StatusNote { get; init; }

    /// <summary>Rows the target table held before this run loaded anything (after Truncate target first, when chosen); null when the
    /// run never counted. Ruling 186: above 0, the row counts compare rows <b>added</b>, not the table.</summary>
    public long? RowsBefore { get; init; }
}

/// <summary>
/// What the operator is shown when a run ends, and what <c>summary_json</c> stores. <see cref="Notes"/> is the part that has to be read:
/// every number above it that could not be established is explained there in words.
/// </summary>
/// <param name="RowsSource">The sum over the tasks that have a source count. <see cref="TasksWithoutSource"/> says how many do not, and
/// while it is non-zero this is a floor, not a total.</param>
/// <param name="DurationSec">Null when the run has no recorded start time.</param>
/// <param name="RowsPerSec">Null when there is no duration to divide by - never 0, which would read as a stalled run.</param>
public sealed record FinalReport(long RunId, RunStatus Status, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, double? DurationSec,
    long RowsSource, long RowsLoaded, long RowsError, double? RowsPerSec, List<TaskReport> Tasks, List<string> Notes, TransferOptions Options)
{
    /// <summary>Tasks whose source row count was never established. Serialised always (it is not nullable), so a reader sees the 0.</summary>
    public int TasksWithoutSource { get; init; }
}

public static class FinalReportBuilder
{
    public const int MaxErrorSamples = 5;

    /// <summary>
    /// Builds the report for one run.
    /// <para><b>Today it has exactly one production caller</b>, <c>TransferEngine.FinishCompletedAsync</c>, which is reached only after
    /// every task is <c>Done</c> and always passes <see cref="RunStatus.Completed"/> and a non-null <paramref name="errorRowCount"/>.
    /// So a completed run cannot contain a non-completed task, and the arms below that key on a task status other than <c>Done</c>, or
    /// on a missing <paramref name="errorRowCount"/>, are <b>contract assertions against a trigger unreachable from this task</b>
    /// (rulings 104, 112, 121) - they are exercised only by their unit tests, and only become live if 5.5 builds a report for a paused,
    /// failed or cancelled run. They are written and kept so that doing so cannot silently produce a report that reads clean.</para>
    /// </summary>
    /// <param name="status">The run's status as the caller is about to record it, which is not always what the task rows say - a
    /// keyless task stopped by Cancel records "paused" (5.3 review F9), and only this join can tell that apart from an operator's pause.</param>
    /// <param name="errorRows">Rejected rows per task; at most <see cref="MaxErrorSamples"/> are shown.</param>
    /// <param name="runNotes">The notes the run itself collected (<c>RunContext.Notes</c>): a control table that was not ours, rejected
    /// rows counted but never recorded. They are carried into <see cref="FinalReport.Notes"/> verbatim, because this report replaces the
    /// summary that used to carry them and they would otherwise leave the run's outcome record altogether.</param>
    /// <param name="errorRowCount">Rejected rows actually recorded for a task. Without it a shortfall against <c>rows_error</c> can only
    /// be reported when nothing at all was recorded (ruling 105).</param>
    public static FinalReport Build(TransferRunRow run, RunStatus status, IReadOnlyList<TransferTaskRow> tasks,
        Func<string, IReadOnlyList<ErrorRowEntry>> errorRows, DateTimeOffset endedAt,
        IReadOnlyList<string>? runNotes = null, Func<string, long>? errorRowCount = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(errorRows);

        var reports = tasks.OrderBy(t => t.Ordinal).Select(t => TaskOf(t, status, errorRows, errorRowCount)).ToList();
        double? elapsed = run.StartedAt is { } started ? Math.Max(0, (endedAt - started).TotalSeconds) : null;
        long loaded = reports.Sum(r => r.RowsLoaded);
        int without = reports.Count(r => r.RowsSource is null);
        return new FinalReport(run.Id, status, run.StartedAt, endedAt,
            elapsed is { } e ? Math.Round(e, 1) : null,
            reports.Sum(r => r.RowsSource ?? 0), loaded, reports.Sum(r => r.RowsError),
            // A rate priced from a duration nobody established is not 0 rows/sec; it is no rate.
            elapsed is > 0 ? Math.Round(loaded / elapsed.Value, 1) : null,
            reports, Notes(reports, run.Options, runNotes, without, elapsed), run.Options)
        {
            TasksWithoutSource = without,
        };
    }

    /// <summary>One line, used as the Complete artifact summary. It never says "validated" about a count nobody made.</summary>
    public static string Summary(FinalReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        int tables = report.Tasks.Select(t => t.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        bool anyMismatch = report.Tasks.Any(t => t.CountMatch == false && t.CountCompared);
        bool anyUnconfirmed = report.Tasks.Count == 0 || report.Tasks.Any(t => !t.CountCompared);
        int sums = report.Tasks.Sum(t => t.Checksums.Count);
        int matched = report.Tasks.Sum(t => t.Checksums.Count(c => c.Match));
        // The columns nobody could compare belong in the headline, not only in the notes: "checksums 5/5 matched" over a six-column
        // binding is the clean sweep this line is read as, and the sentence explaining it is several screens further down.
        int notCompared = report.Tasks.Sum(t => t.ChecksumColumnsNotCompared);
        string rejected = report.RowsError > 0 ? $" ({N(report.RowsError)} rejected)" : "";
        string checks = sums > 0
            ? $", checksums {matched}/{sums} matched" + (notCompared > 0 ? $", {notCompared} column{(notCompared == 1 ? "" : "s")} not compared" : "")
            : "";
        // "of at least": while some task has no source count, the total below it is a floor and must not be offered as the whole.
        string of = report.TasksWithoutSource > 0 ? "of at least" : "of";
        string duration = report.DurationSec is { } d ? Dur(d) : "an unknown time";
        // Ruling 186: over a target that already held rows the counts balance rows ADDED - a doubled keyless table balances too - so
        // the headline must not call that "validated" without saying what was compared.
        string counts = anyMismatch ? "row counts MISMATCH" : anyUnconfirmed ? "row counts NOT CONFIRMED" : "row counts validated";
        if (NonEmptyBefore(report))
            counts = anyMismatch || anyUnconfirmed ? counts + " (" + NotEmptyBefore + ")" : NotEmptyBefore;
        // Ruling 192: counts that balance over a table that received nothing - every row rejected - are not a validation of anything,
        // and "validated" over it is the green light open item 30 was about. Such tasks are named in place of the word.
        var nothing = LoadedNothing(report.Tasks);
        if (nothing.Count > 0)
        {
            string named = string.Join(", ", nothing.Select(t => $"{t.Target} loaded 0 of {N(t.RowsSource!.Value)} rows"));
            counts = counts == "row counts validated" ? named : counts + "; " + named;
        }
        return $"Transferred {N(report.RowsLoaded)} {of} {N(report.RowsSource)} rows into {tables} {(tables == 1 ? "table" : "tables")} "
               + $"in {duration}{rejected}; {counts}{checks}.";
    }

    /// <summary>Ruling 186's headline wording for a run whose target tables were not empty when it started.</summary>
    public const string NotEmptyBefore = "target tables were not empty before this run; counts compare rows added";

    public static bool NonEmptyBefore(FinalReport report) => report.Tasks.Any(t => t.RowsBefore > 0);

    /// <summary>Ruling 192: the tasks that loaded no row of a source that had rows. A report never calls such a run "validated".</summary>
    public static IReadOnlyList<TaskReport> LoadedNothing(IEnumerable<TaskReport> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return tasks.Where(t => t.RowsLoaded == 0 && t.RowsSource > 0).ToList();
    }

    public static string Dur(double seconds)
    {
        long s = (long)Math.Round(Math.Max(0, seconds));
        return s < 60 ? $"{s}s" : s < 3600 ? $"{s / 60}m {s % 60:00}s" : $"{s / 3600}h {s % 3600 / 60:00}m";
    }

    private static TaskReport TaskOf(TransferTaskRow t, RunStatus status, Func<string, IReadOnlyList<ErrorRowEntry>> errorRows,
        Func<string, long>? errorRowCount)
    {
        var v = t.ValidationJson is null ? null : Json.Deserialize<TaskValidation>(t.ValidationJson);
        double? duration = t.StartedAt is { } s && t.EndedAt is { } e ? Math.Round(Math.Max(0, (e - s).TotalSeconds), 1) : null;
        var samples = errorRows(t.TaskId).Take(MaxErrorSamples).Select(r => new ErrorSample(r.KeyJson, r.Error) { ErrorNumber = r.ErrorNumber }).ToList();
        long? rowsSource = t.RowsSource ?? v?.RowsSource;
        return new TaskReport(t.TaskId, t.Target, t.Status, rowsSource, t.RowsDone, t.RowsError, duration,
            v?.CountMatch, v?.Checksums ?? [], v is null ? RunValidator.NoValidation : v.ChecksumsSkipped, samples, t.Error)
        {
            CountCompared = v?.CountCompared ?? false,
            CountNote = v is null ? RunValidator.NoValidation : v.CountNote,
            RowsSourceNote = t.RowsSource is not null ? null
                : v?.RowsSourceNote ?? "the source row count is unknown: this run never recorded one",
            ChecksumColumnsNote = v?.ChecksumColumnsNote,
            ChecksumColumnsNotCompared = v?.ChecksumColumnsNotCompared ?? 0,
            ErrorSamplesNote = ErrorSamplesNote(t.RowsError, samples.Count, errorRowCount?.Invoke(t.TaskId)),
            StatusNote = StatusNote(t.Status, status),
            RowsBefore = t.RowsBefore,
        };
    }

    /// <summary>
    /// Ruling 105. A crash between a chunk's commit and its error rows being written loses which rows were rejected while rows_error
    /// still counts them: the operator is told "5 rejected" and handed two. 5.3 declares it at task end, so a task that never finished
    /// never does - which is why this comparison is made again here, for every task, whether or not the note was written.
    /// <para>The <paramref name="recorded"/> shortfall branch is <b>reachable in production</b> - ruling 105's crash window leaves a
    /// <c>Done</c> task in a <c>Completed</c> run exactly there. The <c>recorded is null</c> branch below is not: the only production
    /// caller always supplies a count, so that one is a contract assertion for a future caller that does not (ruling 121).</para>
    /// </summary>
    internal static string? ErrorSamplesNote(long rowsError, int shown, long? recorded)
    {
        if (rowsError <= 0) return null;
        if (recorded is { } n)
        {
            if (n >= rowsError) return shown < n ? $"showing {shown} of {N(n)} recorded rejected rows." : null;
            return $"{N(rowsError - n)} of the {N(rowsError)} rejected rows were counted but never recorded and cannot be shown - the "
                   + "run was interrupted between a chunk committing and its rejected rows being written down. The rows that did load "
                   + "are correct, and resuming cannot recover the missing ones.";
        }
        // No count to compare against, but an empty list is unambiguous whatever limit was applied: nothing was recorded.
        return shown == 0
            ? $"none of the {N(rowsError)} rejected rows are recorded, so none can be shown - they were counted but never written down."
            : null;
    }

    /// <summary>
    /// A task's own status is not the whole story once the run's is known. A keyless task stopped by Cancel or Fail rolls back and
    /// records "paused" (5.3 review F9) - a reader of the task alone is told an operator paused it and that a resume will pick it up,
    /// and neither is true.
    /// <para><b>Every arm here is a contract assertion against a trigger unreachable from this task</b> (rulings 104, 112, 121):
    /// <see cref="Build"/>'s only production caller passes <c>Completed</c> with every task <c>Done</c>, so none of these pairs can
    /// occur today. They become live the moment 5.5 builds a report for a paused, failed or cancelled run.</para>
    /// </summary>
    internal static string? StatusNote(TransferTaskStatus task, RunStatus run) => (task, run) switch
    {
        (TransferTaskStatus.Paused, RunStatus.Cancelled) =>
            "it did not finish: the run was cancelled, so the task was stopped and rolled back, not paused by an operator.",
        (TransferTaskStatus.Paused, RunStatus.Failed) =>
            "it did not finish: the run failed, so the task was stopped and rolled back, not paused by an operator.",
        (TransferTaskStatus.Paused, RunStatus.Completed) =>
            "it reads as paused although the run completed; the task row was not updated.",
        (TransferTaskStatus.Pending, RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled) =>
            "it never started, so nothing of it was transferred.",
        (TransferTaskStatus.Running, RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled) =>
            "it still reads as running although the run has ended; recovery leaves this state alone deliberately, so it means the row "
            + "was hand-edited or a process died without recovering.",
        _ => null,
    };

    private static List<string> Notes(List<TaskReport> tasks, TransferOptions options, IReadOnlyList<string>? runNotes,
        int tasksWithoutSource, double? elapsed)
    {
        var notes = new List<string>();
        // First, and word for word: these are the run's own findings, and this report is what replaced the summary that used to carry
        // them. A blank one is dropped because a blank line is not a note.
        if (runNotes is not null) notes.AddRange(runNotes.Where(n => !string.IsNullOrWhiteSpace(n)));

        long rejected = tasks.Sum(t => t.RowsError);
        if (rejected > 0) notes.Add($"{N(rejected)} rows were rejected and logged; see the error samples per task.");
        foreach (var t in tasks.Where(t => t.ErrorSamplesNote is not null)) notes.Add($"{t.Target}: {t.ErrorSamplesNote}");

        var mismatch = tasks.Where(t => t.CountMatch == false && t.CountCompared).Select(t => t.Target).ToList();
        var notCompared = tasks.Where(t => t.CountMatch is not null && !t.CountCompared).Select(t => t.Target).ToList();
        var notValidated = tasks.Where(t => t.CountMatch is null).Select(t => t.Target).ToList();
        if (mismatch.Count > 0) notes.Add("Row counts do not match for: " + string.Join(", ", mismatch) + ".");
        if (notCompared.Count > 0)
            notes.Add("Row counts could not be compared for: " + string.Join(", ", notCompared)
                      + ". They are neither confirmed nor contradicted; see each task's countNote.");
        if (notValidated.Count > 0)
            notes.Add("No validation was recorded for: " + string.Join(", ", notValidated)
                      + ". Nothing was checked for these tasks - their row counts and values are unconfirmed.");
        var before = tasks.Where(t => t.RowsBefore > 0).Select(t => $"{t.Target} ({N(t.RowsBefore!.Value)})").ToList();
        if (before.Count > 0)
            notes.Add("Target tables were not empty before this run: " + string.Join(", ", before) + ". Their row counts compare the rows "
                      + "this run added against the source, not the table's contents; a table with no key can hold its rows twice.");
        var nothing = LoadedNothing(tasks);
        foreach (var t in nothing)
            notes.Add(t.RowsError > 0
                ? $"{t.Target} loaded 0 of {N(t.RowsSource!.Value)} source rows ({N(t.RowsError)} rejected). A table that received "
                  + "nothing is not validated by counts that balance: when every row is rejected, the mapping or SQL is the likelier "
                  + "cause than the data - read the rejected rows' errors. In a re-run into a table that already held rows, duplicate-key "
                  + "rejects mean those rows were already there."
                // Review F7: nothing loaded and nothing rejected - the source had rows when it was counted and none when it was read.
                : $"{t.Target} loaded 0 of {N(t.RowsSource!.Value)} source rows and rejected none: the source query returned no rows "
                  + "although the source count said it had some - the source changed during the run, or the count and the query disagree.");
        if (mismatch.Count == 0 && notCompared.Count == 0 && notValidated.Count == 0 && tasks.Count > 0)
            notes.Add(before.Count > 0 ? $"Row counts balance for all {tasks.Count} tasks as rows added."
                      : nothing.Count > 0 ? $"Row counts balance for all {tasks.Count} tasks, but {nothing.Count} of them loaded nothing."
                      : $"Row counts validated for all {tasks.Count} tasks.");

        var badSums = tasks.SelectMany(t => t.Checksums.Where(c => !c.Match).Select(c => $"{t.Target}.{c.Column}")).ToList();
        if (badSums.Count > 0) notes.Add("Column checksums differ for: " + string.Join(", ", badSums) + ".");
        // What a matching checksum does and does not prove, said once, and only where a checksum actually ran.
        if (tasks.Any(t => t.Checksums.Count > 0))
            notes.Add("Column checksums are sums of per-row BINARY_CHECKSUM values; equal sums do not prove identical rows, because two "
                      + "changed rows can cancel out. The row counts are the other half of the check.");
        foreach (var t in tasks.Where(t => t.ChecksumsSkipped is not null && t.ChecksumsSkipped != "disabled"))
            notes.Add($"Checksums skipped for {t.Target}: {t.ChecksumsSkipped}.");
        foreach (var t in tasks.Where(t => t.ChecksumColumnsNote is not null)) notes.Add($"{t.Target}: {t.ChecksumColumnsNote}");
        foreach (var t in tasks.Where(t => t.StatusNote is not null)) notes.Add($"{t.Target}: {t.StatusNote}");
        // Carry-forward 8: a source figure that is NOT the snapshot taken when the task started says so here as well as on the task,
        // because the number beside it looks exactly like a snapshot. (The "unknown" case has its own note below.)
        foreach (var t in tasks.Where(t => t.RowsSource is not null && t.RowsSourceNote is not null))
            notes.Add($"{t.Target}: the source row count was {t.RowsSourceNote}.");

        if (tasksWithoutSource > 0)
            notes.Add("The source row count is unknown for: "
                      + string.Join(", ", tasks.Where(t => t.RowsSource is null).Select(t => t.Target))
                      + ". The row totals in this report count only the tasks that have one, so they are a floor, not a total.");
        if (elapsed is null) notes.Add("The run has no start time recorded, so its duration and its rows-per-second are unknown.");

        if (!options.ValidateChecksums) notes.Add("Column checksums were disabled for this run.");
        if (options.TruncateTarget) notes.Add("Target tables were emptied before loading (truncate target first).");
        if (options.KeepControlTable) notes.Add($"The checkpoint table {ControlTable.Name} was kept in the target.");
        return notes;
    }

    private static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
