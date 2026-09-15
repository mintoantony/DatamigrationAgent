using Dbm.Core.Matching;
using Dbm.Core.SqlGen;
using Xunit;

namespace Dbm.Tests.Unit.SqlGen;

public class SqlTypeTextTests
{
    [Theory]
    [InlineData("varchar(100)", 0, 0, "varchar", 100, 0, 0)]
    [InlineData("nvarchar(max)", 0, 0, "nvarchar", -1, 0, 0)]
    [InlineData("decimal(19,4)", 19, 4, "decimal", 0, 19, 4)]
    [InlineData("datetime2(0)", 19, 0, "datetime2", 0, 19, 0)]
    [InlineData("int", 10, 0, "int", 0, 10, 0)]
    [InlineData("xml", 0, 0, "xml", -1, 0, 0)]
    public void Parse_reads_system_type_names(string name, int precision, int scale, string dataType, int maxLength, int p, int s) =>
        Assert.Equal(new ColumnType(dataType, maxLength, p, s), SqlTypeText.Parse(name, precision, scale));

    [Fact]
    public void Format_writes_tsql_type_names()
    {
        Assert.Equal("nvarchar(max)", SqlTypeText.Format(new ColumnType("nvarchar", -1, 0, 0)));
        Assert.Equal("decimal(19,4)", SqlTypeText.Format(new ColumnType("decimal", 0, 19, 4)));
        Assert.Equal("datetime2(3)", SqlTypeText.Format(new ColumnType("datetime2", 0, 23, 3)));
        Assert.Equal("int", SqlTypeText.Format(new ColumnType("int", 0, 10, 0)));
    }

    [Fact]
    public void IsLob_covers_max_and_legacy_types()
    {
        Assert.True(SqlTypeText.IsLob(new ColumnType("text", 0, 0, 0)));
        Assert.True(SqlTypeText.IsLob(new ColumnType("nvarchar", -1, 0, 0)));
        Assert.False(SqlTypeText.IsLob(new ColumnType("nvarchar", 50, 0, 0)));
    }
}
