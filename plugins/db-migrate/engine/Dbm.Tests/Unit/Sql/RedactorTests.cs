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

    /// <summary>
    /// Open item 3.3: the double-quoted capture <c>"(?&lt;dq&gt;[^"]*)"</c> already handles a doubled quote inside a
    /// double-quoted value (<c>""</c> is the SQL-connection-string escape for a literal <c>"</c>), so
    /// <c>Password="ab""cd"</c> redacts the whole <c>ab"cd</c>. The single-quoted capture did not have the matching
    /// escape, so <c>Password='ab''cd'</c> only captured <c>ab</c>: the true secret (7 characters with the escaped
    /// quote) never reached <c>Scrub</c>, and the truncated 2-character capture fell under the length-4 floor, so
    /// <b>nothing was redacted at all</b>. Reachable only on the fallback (unparsable connection string) path.
    /// <b>Harm:</b> a message built from an unparsable connection string with a single-quoted, quote-escaped
    /// password would print the password in full.
    /// </summary>
    [Fact]
    public void SecretsOf_handles_doubled_single_quotes_the_same_way_as_doubled_double_quotes()
    {
        Assert.Equal(new[] { "ab\"cd" }, Redactor.SecretsOf("this is ; not = valid ; Password=\"ab\"\"cd\""));
        var sqSecrets = Redactor.SecretsOf("this is ; not = valid ; Password='ab''cd'").ToList();
        Assert.True(sqSecrets.SequenceEqual(new[] { "ab'cd" }),
            $"SecretsOf did not unescape a doubled single quote ('') the way it already unescapes a doubled double " +
            $"quote (\"\"): got [{string.Join(", ", sqSecrets)}], expected [ab'cd].");
    }

    [Fact]
    public void Scrub_redacts_a_single_quoted_password_with_a_doubled_quote_through_the_public_entry_point()
    {
        var secrets = Redactor.SecretsOf("this is ; not = valid ; Password='ab''cd'");

        var scrubbed = Redactor.Scrub("connect failed: password ab'cd was rejected", secrets);

        Assert.True(scrubbed == "connect failed: password *** was rejected",
            $"Scrub did not redact the password recovered from a doubled-single-quote capture: got \"{scrubbed}\".");
    }

    [Fact]
    public void Scrub_replaces_each_secret_of_four_or_more_characters()
    {
        var text = Redactor.Scrub("login failed for S3cr3t!pw and abc", ["S3cr3t!pw", "abc", null, ""]);

        Assert.Equal("login failed for *** and abc", text);
    }

    /// <summary>
    /// Open item 51 (sweep K review HIGH-1), measured on LocalDB: <c>dbm demo --attach</c> printed
    /// <c>Password=ab1</c> in clear. <c>Scrub</c>'s per-secret substring replacement keeps a 4-character floor
    /// (Ruling: a 1-character secret must not mangle every occurrence of that character in a message), so a
    /// short secret like <c>ab1</c> was never masked at all. Decision H-3: the floor stays for bare substring
    /// replacement, but <c>Scrub</c> now also masks any <c>Password=</c>/<c>Pwd=</c> key/value pair visible in
    /// the text itself, whatever its length - that is what a short secret still inside a live connection
    /// string looks like. <b>Harm:</b> a 1-, 2- or 3-character password survives every log line, error message
    /// and CLI printout that echoes the connection string.
    /// </summary>
    [Fact]
    public void Scrub_masks_a_password_value_under_the_four_character_floor_via_its_key_value_pair()
    {
        var text = "dbm demo --attach failed for Server=a;Password=ab1;Encrypt=true";

        var scrubbed = Redactor.Scrub(text, Redactor.SecretsOf("Server=a;Password=ab1;Encrypt=true"));

        Assert.True(!scrubbed.Contains("ab1", StringComparison.Ordinal),
            $"Scrub left a password shorter than the 4-character floor in clear text, because only the " +
            $"per-secret substring replacement (which skips it) ran: \"{scrubbed}\".");
        Assert.True(scrubbed.Contains("Password=***", StringComparison.Ordinal),
            $"Scrub did not replace the short Password= key/value pair with Password=***: \"{scrubbed}\".");
    }

    /// <summary>
    /// Open item 51: measured on LocalDB, <c>dbm demo --attach</c> also printed <c>Password="Pa""ss'word1"</c> -
    /// <see cref="SqlConnectionStringBuilder"/> re-quotes a value containing both a <c>"</c> and a <c>'</c> by
    /// wrapping it in double quotes and doubling the internal <c>"</c> (verified directly against the builder:
    /// <c>Pa"ss'word1</c> renders as <c>"Pa""ss'word1"</c>). The bare secret <c>Pa"ss'word1</c> never matches
    /// that rendered text as a substring, so it slipped through when it appeared without a <c>Password=</c>
    /// prefix (e.g. quoted on its own inside a driver error message). <b>Harm:</b> a re-quoted password printed
    /// by anything other than a literal <c>Password=</c> key/value pair reaches the log in clear text.
    /// </summary>
    [Fact]
    public void Scrub_redacts_the_builders_re_quoted_rendering_of_a_secret_with_both_quote_characters()
    {
        var secret = "Pa\"ss'word1";
        var rendered = new SqlConnectionStringBuilder { DataSource = "a", Password = secret }.ConnectionString;
        var quotedValueOnly = rendered[rendered.IndexOf('"', StringComparison.Ordinal)..];   // "Pa""ss'word1" (no Password= prefix)

        var scrubbed = Redactor.Scrub($"driver reported value {quotedValueOnly} was rejected", [secret]);

        Assert.True(!scrubbed.Contains("ss'word1", StringComparison.Ordinal),
            $"Scrub did not redact the builder's doubled-quote rendering of a secret that appeared without a " +
            $"Password= prefix: \"{scrubbed}\" (rendered form was {quotedValueOnly}).");
    }

    /// <summary>
    /// Open item 51 / sweep H review "Pre-existing": for an unterminated quote (<c>Password="abcdEFGH</c>, no
    /// closing <c>"</c>), the dq and sq alternatives both fail to close, so the match falls through to the raw
    /// alternative, which captures from right after "=" - including the opening quote that started the failed
    /// attempt. <see cref="Redactor.SecretsOf"/> now strips that leading unmatched quote so the real secret
    /// value is what gets returned (and later matched as a substring elsewhere); <see cref="Redactor.Scrub"/>'s
    /// key/value pass masks the whole malformed key/value pair directly, regardless. <b>Harm:</b> without the
    /// strip, the "secret" SecretsOf hands to any other Scrub call is <c>"abcdEFGH</c> (with the leading quote),
    /// which never matches the bare password text (no leading quote) anywhere else it might be echoed.
    /// </summary>
    [Fact]
    public void SecretsOf_strips_the_leading_quote_of_an_unterminated_quoted_password()
    {
        var text = "connection failed: Password=\"abcdEFGH";

        var secrets = Redactor.SecretsOf(text).ToList();
        Assert.True(secrets.Contains("abcdEFGH"),
            $"SecretsOf's raw fallback kept the leading unmatched quote instead of stripping it: [{string.Join(", ", secrets)}].");

        var scrubbed = Redactor.Scrub(text, secrets);
        Assert.True(!scrubbed.Contains("abcdEFGH", StringComparison.Ordinal),
            $"Scrub did not redact an unterminated-quote password: \"{scrubbed}\".");
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
