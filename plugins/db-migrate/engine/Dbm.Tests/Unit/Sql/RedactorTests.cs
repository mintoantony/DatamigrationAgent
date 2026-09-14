using Dbm.Core.Sql;
using Microsoft.Data.SqlClient;

namespace Dbm.Tests.Unit.Sql;

public class RedactorTests
{
    [Fact]
    public void Describe_sql_login_shows_user_but_never_the_password()
    {
        var text = Redactor.Describe("Server=tcp:db.example.com,1433;Database=Shop;User ID=migrator;Password=S3cr3t!pw;Encrypt=true");

        Assert.Equal("server=tcp:db.example.com,1433; database=Shop; auth=sql; user=migrator", text);
    }

    [Theory]
    [InlineData(@"Server=(localdb)\MSSQLLocalDB;Integrated Security=true", @"server=(localdb)\MSSQLLocalDB; database=(default); auth=integrated")]
    [InlineData("Server=x.database.windows.net;Database=D;Authentication=Active Directory Default", "server=x.database.windows.net; database=D; auth=active_directory_default")]
    [InlineData("this is ; not = valid ; = ;", "invalid connection string")]
    public void Describe_summarises_the_auth_mode(string cs, string expected)
    {
        Assert.Equal(expected, Redactor.Describe(cs));
    }

    [Fact]
    public void SecretsOf_returns_passwords_even_for_unparsable_strings()
    {
        Assert.Equal(new[] { "S3cr3t!pw" }, Redactor.SecretsOf("Server=a;User ID=u;Password=S3cr3t!pw"));
        Assert.Empty(Redactor.SecretsOf("Server=a;Integrated Security=true"));
        Assert.Equal(new[] { "p;w=x" }, Redactor.SecretsOf("Server=a;Bogus Key=1;Pwd='p;w=x'"));
    }

    [Fact]
    public void Scrub_replaces_each_secret_of_four_or_more_characters()
    {
        var text = Redactor.Scrub("login failed for S3cr3t!pw and abc", ["S3cr3t!pw", "abc", null, ""]);

        Assert.Equal("login failed for *** and abc", text);
    }

    [Fact]
    public void Normalize_sets_application_name_only_when_left_default()
    {
        var normalized = new SqlConnectionStringBuilder(SqlConnect.Normalize("Server=a;Integrated Security=true"));
        var custom = new SqlConnectionStringBuilder(SqlConnect.Normalize("Server=a;Integrated Security=true;Application Name=etl"));

        Assert.Equal("dbm", normalized.ApplicationName);
        Assert.Equal("etl", custom.ApplicationName);
    }

    [Fact]
    public void Entra_id_provider_comes_from_the_azure_extension_package()
    {
        // Guards the Microsoft.Data.SqlClient.Extensions.Azure reference: without it this returns null.
        var provider = SqlAuthenticationProvider.GetProvider(SqlAuthenticationMethod.ActiveDirectoryDefault);

        Assert.NotNull(provider);
        Assert.Equal("Microsoft.Data.SqlClient.ActiveDirectoryAuthenticationProvider", provider!.GetType().FullName);
    }
}
