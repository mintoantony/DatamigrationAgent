using Dbm.Core.Transfer;
using Xunit;

namespace Dbm.Tests.Unit.Transfer;

/// <summary>
/// Ruling 147's rule, on its own: which server error numbers mean "this row's values broke a rule" rather than "this load is broken".
/// The rule is split from the exception chain deliberately - the test project has no way to construct a <c>SqlException</c> without
/// reflection, so the rule is driven here as data and the real chain is covered by <c>BulkLoaderTests</c> against a live server.
/// </summary>
public sealed class BulkLoaderClassifierTests
{
    [Theory]
    [InlineData(true, 547)]          // FK or CHECK conflict
    [InlineData(true, 2627)]         // PRIMARY KEY / UNIQUE constraint violation
    [InlineData(true, 2601)]         // duplicate key in a unique index
    [InlineData(true, 547, 2627)]    // a chunk's worth of both is still N bad rows
    [InlineData(false, 547, 207)]    // ... but one invalid column name among them is a plan defect: H2 keeps it
    [InlineData(false, 515)]         // NULL into NOT NULL: deliberately NOT a row fault (an unmapped NOT NULL column shreds a table)
    [InlineData(false, 208)]         // invalid object name
    [InlineData(false, 245)]         // conversion failed
    [InlineData(false, 8152)]        // string or binary data would be truncated
    [InlineData(false, 8115)]        // arithmetic overflow
    [InlineData(false, 229)]         // permission denied
    public void Only_constraint_violations_are_row_faults(bool expected, params int[] numbers)
    {
        string list = string.Join(", ", numbers);
        Assert.True(BulkLoader.IsRowFault(numbers) == expected, expected
            ? $"[{list}] is no longer a row fault, so H2 fails a chunk of these constraint rejects as a broken load (ruling 147)"
            : $"[{list}] is read as a row fault, so a plan defect that fails every row alike comes back as a table of rejected rows "
              + "instead of a failed task (H2)");
    }

    /// <summary>
    /// No numbers is not a row fault, and that is the load-bearing default: a failure with no <c>SqlError</c> at all is a client-side
    /// or a non-SQL fault (SqlBulkCopy raises truncation and NULL-into-NOT-NULL as <c>InvalidOperationException</c>, measured), and
    /// reading "nothing said otherwise" as "the rows are individually bad" would switch H2 off for every one of them.
    /// </summary>
    [Fact]
    public void An_attempt_with_no_server_error_is_not_a_row_fault()
        => Assert.False(BulkLoader.IsRowFault([]),
            "a failure that carried no server error at all was read as a row fault, so H2 no longer fails a chunk of client-side "
            + "truncations or NULLs as a broken load");

    /// <summary>
    /// Ruling 165, the class filter. What SQL Server 2025 actually sends for an FK conflict out of SqlBulkCopy, measured: 547 at class
    /// 16, then 3621 "The statement has been terminated." at class 0. 3621 is informational, not an error, so it is not among the
    /// numbers the rule sees. <b>Harm:</b> counted, it makes every constraint violation fail the rule, and ruling 147 does nothing.
    /// The last two rows are the boundary (5.7 review F6, mutation M8): a trigger's <c>RAISERROR (…, 10, …)</c> beside a constraint
    /// violation is informational too, and only class 10 against class 11 tells a filter of "above 10" from "above 0".
    /// </summary>
    [Theory]
    [InlineData(547, (byte)16, 3621, (byte)0)]      // FK / CHECK
    [InlineData(2627, (byte)14, 3621, (byte)0)]     // PK / UNIQUE
    [InlineData(547, (byte)16, 50000, (byte)10)]    // a trigger's informational RAISERROR at class 10, the highest that is not an error
    public void The_informational_trailer_on_a_constraint_violation_is_not_counted_as_an_error(int number, byte errorClass, int info,
        byte infoClass)
    {
        var numbers = BulkLoader.ErrorNumbers([(number, errorClass), (info, infoClass)]);
        Assert.True(numbers.SequenceEqual([number]),
            $"the class filter let informational messages through ({string.Join(", ", numbers)}), so a constraint violation is no "
            + "longer a row fault and H2 fails the task again");
        Assert.True(BulkLoader.IsRowFault(numbers), $"[{string.Join(", ", numbers)}] is not a row fault");
    }

    /// <summary>The filter drops only informational messages: a real error beside a constraint violation still reaches the rule and
    /// still keeps H2. Class 11 is the lowest that is an error.</summary>
    [Fact]
    public void An_error_beside_a_constraint_violation_is_still_counted()
    {
        var numbers = BulkLoader.ErrorNumbers([(547, (byte)16), (207, (byte)11), (3621, (byte)0)]);
        Assert.True(numbers.SequenceEqual([547, 207]),
            $"the class filter dropped a real error beside a constraint violation (kept: {string.Join(", ", numbers)})");
        Assert.False(BulkLoader.IsRowFault(numbers),
            "an invalid column name beside a constraint violation was read as a row fault, so a plan defect comes back as rejected rows");
    }

    /// <summary>
    /// 5.7 review F6, mutation M7: a constraint violation that also ended the transaction (the bisector restarts it) is still a row fault.
    /// Both of the loader's failure returns build their attempt here, so the doomed one cannot drop the verdict on its own.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_failed_attempt_carries_its_row_fault_verdict_and_numbers_whether_or_not_it_ended_the_transaction(bool doomed)
    {
        var attempt = BulkLoader.FailedAttempt("FK conflict", [547], doomed);
        Assert.True(attempt.RowFault,
            $"a constraint violation {(doomed ? "that ended the transaction " : "")}lost its row-fault verdict, so H2 fails a chunk of them "
            + "as a broken load");
        Assert.Equal(doomed, attempt.Doomed);
        Assert.False(attempt.Ok);
        Assert.True(attempt.ErrorNumbers.SequenceEqual([547]),
            $"the failed attempt carries error numbers [{string.Join(", ", attempt.ErrorNumbers)}], not [547], so the bisector cannot see "
            + "that every attempt failed on the same error (ruling 193)");
        Assert.False(BulkLoader.FailedAttempt("bad column", [207], doomed).RowFault, "an invalid column name was read as a row fault");
    }
}
