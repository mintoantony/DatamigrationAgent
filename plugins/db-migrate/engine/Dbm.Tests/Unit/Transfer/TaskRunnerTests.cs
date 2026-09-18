using System.Data;
using Dbm.Core.SqlGen;
using Dbm.Core.Transfer;
using Dbm.Tests.Support;
using Xunit;

namespace Dbm.Tests.Unit.Transfer;

/// <summary>The runner's three "an absence must say why" helpers, tested on the damage each one prevents.</summary>
public sealed class TaskRunnerTests
{
    private static TaskPlan Keyed() => new()
    {
        Target = "app.T",
        SourceQuery = "SELECT 1",
        KeyColumns = ["__k0"],
        Columns = [new ColumnBinding("V", "V")],
    };

    private static DataTable OneRow(long key, string v)
    {
        var t = new DataTable { Locale = System.Globalization.CultureInfo.InvariantCulture };
        t.Columns.Add("__k0", typeof(long));
        t.Columns.Add("V", typeof(string));
        t.Rows.Add(key, v);
        return t;
    }

    /// <summary>
    /// Harm: TransferRepo refuses a blank error on a failed task and on an error row, and ArgumentException is deliberately not a
    /// TransferException, so it escapes the runner's catch and takes the whole migration down. One exception with an empty message
    /// must not cost a run.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_exception_with_a_blank_message_still_gives_the_repo_something_to_record(string message)
    {
        using var svc = new XferServices();
        var repo = svc.Services.Transfers;
        long runId = repo.CreateRun(1, new TransferOptions(), [("T01", "app.T")]);

        string reason = TransferFailure.Describe(new InvalidOperationException(message), s => s);

        repo.UpdateTaskStatus(runId, "T01", TransferTaskStatus.Failed, reason);   // would throw ArgumentException on a blank reason
        repo.AddErrorRow(runId, "T01", null, "{}", reason);                       // and so would this

        Assert.Equal(TransferTaskStatus.Failed, repo.Task(runId, "T01")!.Status);
        Assert.Contains("InvalidOperationException", repo.Task(runId, "T01")!.Error);
        Assert.Equal(1, repo.ErrorRowCount(runId, "T01"));
    }

    [Fact]
    public void A_blank_TransferException_message_is_described_by_its_code()
    {
        string reason = TransferFailure.Describe(new TransferException("control_table_mismatch", " "), s => s);
        Assert.Contains("control_table_mismatch", reason);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void A_message_that_scrubbing_empties_still_gives_the_repo_something_to_record()
    {
        string reason = TransferFailure.Describe(new InvalidOperationException("secret"), _ => "");
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.DoesNotContain("secret", reason);
    }

    /// <summary>
    /// Harm: a keyed task whose key could not be encoded records key_json = NULL, which reads exactly like a keyless task's error row -
    /// "this task has no key". The operator then cannot tell "no key exists" from "we lost the key of the row that failed", and the one
    /// row they need to re-run is unidentifiable.
    /// </summary>
    [Fact]
    public void An_error_row_whose_key_could_not_be_encoded_says_so_instead_of_reading_as_keyless()
    {
        var table = OneRow(42L, "v");
        var wrongTypes = new[] { new KeyType("int", 4, 0, 0) };   // the reader said bigint; an int key refuses a long value

        var lost = TaskRunner.Capture(Keyed(), table, [new RowFailure(0, "FK violation")], wrongTypes);
        var keyless = TaskRunner.Capture(new TaskPlan { Target = "app.T", Columns = [new ColumnBinding("V", "V")] },
            table, [new RowFailure(0, "FK violation")], null);

        Assert.Null(lost[0].Key);
        Assert.Null(keyless[0].Key);
        Assert.NotEqual(keyless[0].Error, lost[0].Error);                       // the two nulls are told apart
        Assert.Contains(TaskRunner.KeyFailedNote, lost[0].Error);
        Assert.Contains("FK violation", lost[0].Error);                        // without losing why the row failed
        Assert.DoesNotContain(TaskRunner.KeyFailedNote, keyless[0].Error);
    }

    [Fact]
    public void An_error_row_keeps_the_key_when_it_can_be_encoded()
    {
        var rows = TaskRunner.Capture(Keyed(), OneRow(42L, "v"), [new RowFailure(0, "FK violation")],
            [new KeyType("bigint", 8, 0, 0)]);
        Assert.Equal("{\"__k0\":42}", rows[0].Key);
        Assert.Equal("FK violation", rows[0].Error);
    }

    /// <summary>
    /// Harm: the same ArgumentException as above, one level down. A row failure can arrive with no text at all, and scrubbing can empty
    /// a text whose whole content was a secret - either way AddErrorRow refuses it and the refusal escapes as a run-ending exception.
    /// </summary>
    [Fact]
    public void An_error_row_with_a_blank_error_is_still_recordable()
    {
        using var svc = new XferServices();
        var repo = svc.Services.Transfers;
        long runId = repo.CreateRun(1, new TransferOptions(), [("T01", "app.T")]);

        var rows = TaskRunner.Capture(Keyed(), OneRow(42L, "v"), [new RowFailure(0, "  ")], [new KeyType("bigint", 8, 0, 0)]);
        Assert.False(string.IsNullOrWhiteSpace(rows[0].Error));                  // the failure's own blank text

        repo.AddErrorRow(runId, "T01", rows[0].Key, rows[0].Row, TaskRunner.ErrorText(rows[0].Error, _ => ""));   // and scrubbing it away
        Assert.Equal(1, repo.ErrorRowCount(runId, "T01"));
    }

    /// <summary>
    /// NOT a harm test - a contract assertion (ruling 104). Rulings 73/74 say <c>Loaded + Failed</c> need not equal
    /// <c>Attempted</c>, but no route reaches that state through today's loader: stop mode rolls back and throws before the check, and
    /// skip mode attempts every row. The reviewer confirmed it by deleting both call sites, and nothing failed. This test therefore
    /// pins only that the assertion says the right thing if a future 5.2 change ever makes the state reachable; it is not evidence of
    /// a defect prevented, and the guard's call sites have no mutation behind them.
    /// </summary>
    [Fact]
    public void The_accounting_contract_assertion_names_the_rows_that_are_in_neither_list()
    {
        var short_ = new ChunkOutcome(4, [new RowFailure(4, "bad")], 0)
            { Attempted = 20, MergeStatus = MergeStatus.NotApplicable };   // a direct task: there was never a merge to run
        var ex = Assert.Throws<TransferException>(() => TaskRunner.CheckAccounted("T02", 7, short_));
        Assert.Equal("rows_unaccounted", ex.Code);
        Assert.Contains("15", ex.Message);      // 20 handed over, 5 accounted for, 15 nobody knows about
        Assert.Contains("T02", ex.Message);

        TaskRunner.CheckAccounted("T02", 7, new ChunkOutcome(19, [new RowFailure(4, "bad")], 0)
            { Attempted = 20, MergeStatus = MergeStatus.NotApplicable });
    }

    /// <summary>
    /// Ruling 89 in the operator's log line: a null merge count means three different things and only <c>MergeStatus</c> says which.
    /// The harm this pins is the sentence the runner used to write for a merge that never ran - "the merge reported no row count",
    /// which reads as a merge that executed and stayed quiet - and, on the other side, the false "merge affected 0" a status-blind
    /// reader produces for a task whose merge never ran at all.
    /// </summary>
    [Fact]
    public void The_merge_note_is_chosen_by_the_status_not_by_the_count()
    {
        Assert.Equal("", TaskRunner.MergeNote(MergeStatus.NotApplicable, null));
        Assert.Equal("", TaskRunner.MergeNote(MergeStatus.NotApplicable, 0L));       // a count beside "no merge" is still no sentence

        Assert.Equal(", the merge did not run", TaskRunner.MergeNote(MergeStatus.DidNotRun, null));
        Assert.Equal(", the merge did not run", TaskRunner.MergeNote(MergeStatus.DidNotRun, 0L));   // never "merge affected 0"

        Assert.Equal(", merge affected 0", TaskRunner.MergeNote(MergeStatus.Ran, 0L));   // 0 from a merge that ran is a real count
        Assert.Equal(", merge affected 1,234", TaskRunner.MergeNote(MergeStatus.Ran, 1234L));
        Assert.Equal(", the merge ran and reported no row count", TaskRunner.MergeNote(MergeStatus.Ran, null));
    }

    private static TaskRunner.RejectTally Tally(params (int? Number, string Text)[] rejects)
    {
        var tally = new TaskRunner.RejectTally();
        tally.Add(rejects.Select((r, i) => new RowFailure(i, r.Text) { ErrorNumber = r.Number }));
        return tally;
    }

    private const string Dup = "Violation of PRIMARY KEY constraint 'PK_P'. Cannot insert duplicate key in object 'app.P'. The duplicate key value is (1).";
    private const string Fk = "The INSERT statement conflicted with the FOREIGN KEY constraint \"FK_C_P\".";

    /// <summary>
    /// Ruling 201: the re-run exemption must be <b>shown</b>. With a tally that covers only part of the rejects, the recorded rows decide,
    /// and only when there are as many of them as rejects and every one reads as a duplicate key. <b>Harm:</b> an exemption taken on
    /// fewer recorded rows than rejects (ruling 105's crash window) would let an unrecorded FK reject pass as a duplicate.
    /// </summary>
    [Fact]
    public void The_rerun_exemption_needs_every_reject_shown_as_a_duplicate_key()
    {
        var partial = Tally((2627, Dup));   // this segment saw 1 of the 3 rejects
        Assert.True(TaskRunner.LoadedNothingReason("app.P", 2, 0, 3, 10, partial, () => [Dup, Dup, Dup], keyed: true) is null,
            "a re-run whose 3 rejects are all recorded duplicate keys was judged: the recorded rows were not read, or not believed");
        Assert.True(TaskRunner.LoadedNothingReason("app.P", 2, 0, 3, 10, partial, () => [Dup, Dup], keyed: true) is not null,
            "the re-run exemption was taken on 2 recorded rows for 3 rejects: the unrecorded one was assumed to be a duplicate");
        Assert.True(TaskRunner.LoadedNothingReason("app.P", 2, 0, 3, 10, partial, () => [Dup, Fk, Dup], keyed: true) is not null,
            "a recorded FK reject was read as a duplicate key");
        Assert.True(TaskRunner.LoadedNothingReason("app.P", 2, 0, 3, 0, Tally((2627, Dup), (2627, Dup), (2627, Dup)), null, keyed: true) is not null,
            "duplicate keys into a table that was empty before the run were exempted: the exemption is for the confirmed re-run only");
        Assert.True(TaskRunner.LoadedNothingReason("app.P", 2, 0, 2, 10, Tally((2627, Dup), (547, Fk)), null, keyed: true) is not null,
            "a tally holding a 547 beside duplicates was exempted");
        Assert.True(TaskRunner.LoadedNothingReason("app.P", 2, 0, 2, 10, Tally((2627, Dup), (2601, "x")), null, keyed: true) is null,
            "a re-run whose tally holds only 2627 and 2601 was judged: the confirmed re-run is stopped over its duplicate keys");
    }

    [Theory]
    [InlineData(true, "Violation of UNIQUE KEY constraint 'UQ_P'. Cannot insert duplicate key in object 'app.P'.")]
    [InlineData(true, "Cannot insert duplicate key row in object 'app.P' with unique index 'IX_P'.")]
    [InlineData(true, Dup)]
    [InlineData(false, Fk)]
    [InlineData(false, "Violation of PRIMARY KEY constraint")]   // not the message, only its prefix without the name
    public void Only_the_servers_duplicate_key_messages_read_as_duplicates(bool expected, string text)
        => Assert.True(TaskRunner.IsDuplicateKeyText(text) == expected, $"\"{text}\" was {(expected ? "not " : "")}read as a duplicate key");

    /// <summary>Review F8: the doc says ties go to the lowest number, and a number says more than its absence. <b>Harm:</b> an equal
    /// count of client-side failures hid the server's error number from the reason.</summary>
    [Fact]
    public void A_tie_between_a_number_and_no_number_names_the_number()
    {
        var best = Tally((null, "client-side"), (547, Fk)).MostCommon();
        Assert.True(best?.Number == 547, $"a tie named {(best?.Number is int n ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "no number")}, not error 547");
        Assert.Null(Tally((null, "a"), (null, "b"), (547, Fk)).MostCommon()?.Number);   // strictly more rows still wins
    }
}
