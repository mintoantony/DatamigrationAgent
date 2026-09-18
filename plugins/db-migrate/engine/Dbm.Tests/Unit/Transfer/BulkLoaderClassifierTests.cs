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
        => Assert.Equal(expected, BulkLoader.IsRowFault(numbers));

    /// <summary>
    /// No numbers is not a row fault, and that is the load-bearing default: a failure with no <c>SqlError</c> at all is a client-side
    /// or a non-SQL fault (SqlBulkCopy raises truncation and NULL-into-NOT-NULL as <c>InvalidOperationException</c>, measured), and
    /// reading "nothing said otherwise" as "the rows are individually bad" would switch H2 off for every one of them.
    /// </summary>
    [Fact]
    public void An_attempt_with_no_server_error_is_not_a_row_fault() => Assert.False(BulkLoader.IsRowFault([]));

    /// <summary>
    /// Ruling 165, the class filter. What SQL Server 2025 actually sends for an FK conflict out of SqlBulkCopy, measured: 547 at class
    /// 16, then 3621 "The statement has been terminated." at class 0. 3621 is informational, not an error, so it is not among the
    /// numbers the rule sees. <b>Harm:</b> counted, it makes every constraint violation fail the rule, and ruling 147 does nothing.
    /// </summary>
    [Theory]
    [InlineData(547, (byte)16)]    // FK / CHECK
    [InlineData(2627, (byte)14)]   // PK / UNIQUE
    public void The_informational_trailer_on_a_constraint_violation_is_not_counted_as_an_error(int number, byte errorClass)
    {
        var numbers = BulkLoader.ErrorNumbers([(number, errorClass), (3621, (byte)0)]);
        Assert.True(numbers.SequenceEqual([number]),
            $"the class filter let informational messages through ({string.Join(", ", numbers)}), so a constraint violation is no "
            + "longer a row fault and H2 fails the task again");
        Assert.True(BulkLoader.IsRowFault(numbers));
    }

    /// <summary>The filter drops only informational messages: a real error beside a constraint violation still reaches the rule and
    /// still keeps H2.</summary>
    [Fact]
    public void An_error_beside_a_constraint_violation_is_still_counted()
    {
        var numbers = BulkLoader.ErrorNumbers([(547, (byte)16), (207, (byte)16), (3621, (byte)0)]);
        Assert.Equal([547, 207], numbers);
        Assert.False(BulkLoader.IsRowFault(numbers));
    }
}
