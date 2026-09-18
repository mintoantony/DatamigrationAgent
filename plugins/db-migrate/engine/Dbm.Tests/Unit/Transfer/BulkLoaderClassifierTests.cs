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
}
