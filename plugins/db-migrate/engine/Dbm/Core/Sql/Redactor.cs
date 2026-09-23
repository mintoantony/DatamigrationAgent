using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Sql;

/// <summary>Everything that may show a connection string to a human, a log or Claude goes through here.</summary>
public static partial class Redactor
{
    /// <summary>"server=X; database=Y; auth=&lt;mode&gt;[; user=U]" — never secrets.</summary>
    public static string Describe(string connectionString)
    {
        SqlConnectionStringBuilder b;
        try
        {
            b = new SqlConnectionStringBuilder(connectionString);
        }
        catch (Exception)
        {
            return "invalid connection string";
        }
        var database = string.IsNullOrEmpty(b.InitialCatalog) ? "(default)" : b.InitialCatalog;
        var text = $"server={b.DataSource}; database={database}; auth={AuthMode(b)}";
        return string.IsNullOrEmpty(b.UserID) ? text : $"{text}; user={b.UserID}";
    }

    /// <summary>"integrated", "sql", or the snake_case Authentication keyword (e.g. "active_directory_default").</summary>
    public static string AuthMode(string connectionString)
    {
        try
        {
            return AuthMode(new SqlConnectionStringBuilder(connectionString));
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    /// <summary>
    /// Replaces each secret (length &#8805; 4) with "***" - both bare and in the doubled-quote form
    /// <see cref="SqlConnectionStringBuilder"/> renders it in when it re-quotes a value containing both a
    /// <c>"</c> and a <c>'</c> - then masks every <c>Password=</c>/<c>Pwd=</c> key/value pair still visible in
    /// the text, whatever its length, quoted or raw (item 51 / decision H-3: this is what catches a secret
    /// under the 4-character floor without lowering that floor for bare substring replacement elsewhere,
    /// where it would mangle every occurrence of a one-character secret; the residual risk that decision
    /// accepts is a short secret appearing outside any <c>Password=</c>/<c>Pwd=</c> key/value pair).
    /// </summary>
    public static string Scrub(string text, IEnumerable<string?> secrets)
    {
        foreach (var s in secrets)
        {
            if (string.IsNullOrEmpty(s)) continue;
            if (s.Length >= 4) text = text.Replace(s, "***", StringComparison.Ordinal);

            // A value containing both quote characters is re-quoted with whichever one is the wrapper doubled
            // (confirmed against SqlConnectionStringBuilder: Password="Pa""ss'word1" for Pa"ss'word1) - the
            // bare secret above does not match that rendered text as a substring, so scrub it too.
            var dqEscaped = s.Replace("\"", "\"\"", StringComparison.Ordinal);
            if (dqEscaped != s && dqEscaped.Length >= 4) text = text.Replace(dqEscaped, "***", StringComparison.Ordinal);
            var sqEscaped = s.Replace("'", "''", StringComparison.Ordinal);
            if (sqEscaped != s && sqEscaped.Length >= 4) text = text.Replace(sqEscaped, "***", StringComparison.Ordinal);
        }
        // Ruling 225: this pass runs over arbitrary free text (an error message, a log line), not a real
        // connection string, so it must not reuse PasswordPattern - that regex's raw alternative is [^;]*,
        // correct for ;-separated connection-string values but wrong here: on free text it ran to the next ;
        // or the end and swallowed everything after the password (e.g. "Password=ab1 was rejected for login x"
        // became "Password=***", losing "was rejected for login x"). FreeTextPasswordPattern stops a raw value
        // at whitespace too, and only the value is replaced - the key and everything after it are kept.
        return FreeTextPasswordPattern().Replace(text, MaskFreeTextValue);
    }

    /// <summary>
    /// H-4: a raw, unquoted password containing a space, written directly into free text outside a connection
    /// string, is masked only up to its first space (the raw alternative stops at whitespace); the rest of the
    /// value is covered only by <see cref="Scrub"/>'s per-secret substring pass above, which needs the exact
    /// secret to be in the secrets list and to clear the 4-character floor. Not fixed: confirmed against
    /// <see cref="SqlConnectionStringBuilder"/> that it double-quotes any value containing a space when
    /// it renders a connection string (e.g. a password of "has space" renders as <c>Password="has space"</c>),
    /// so a password with a space inside a real, builder-produced connection string is already quoted and
    /// caught by the dq alternative in full - this residual risk is limited to a raw, unquoted occurrence
    /// written directly into free text, outside any connection string a builder produced.
    /// </summary>
    private static string MaskFreeTextValue(Match m)
    {
        var key = m.Groups["key"].Value;
        var raw = m.Groups["raw"];
        // Trailing sentence or wrapper punctuation reads as prose, not part of the value - keep it outside the mask,
        // so "(Password=abc)" does not lose its closing bracket (the H re-review's LOW).
        if (raw.Success && raw.Value.Length > 0 && raw.Value[^1] is ',' or '.' or ')' or ']' or '}' or '!' or '?' or ':')
            return $"{key}=***{raw.Value[^1]}";
        return $"{key}=***";
    }

    /// <summary>Password / client-secret values; falls back to a regex when the string does not parse.</summary>
    public static IEnumerable<string> SecretsOf(string connectionString)
    {
        try
        {
            var b = new SqlConnectionStringBuilder(connectionString);
            return string.IsNullOrEmpty(b.Password) ? [] : [b.Password];
        }
        catch (Exception)
        {
            return PasswordPattern().Matches(connectionString)
                .Select(m => m.Groups["dq"].Success ? m.Groups["dq"].Value.Replace("\"\"", "\"", StringComparison.Ordinal)
                    : m.Groups["sq"].Success ? m.Groups["sq"].Value.Replace("''", "'", StringComparison.Ordinal)
                    : StripLeadingUnmatchedQuote(m.Groups["raw"].Value))
                .Where(v => v.Length > 0)
                .ToList();
        }
    }

    /// <summary>
    /// Item 51 (and sweep H review "Pre-existing"): for an unterminated quote (e.g. <c>Password="abcdEFGH</c>),
    /// neither the dq nor the sq alternative closes, so the match falls through to the raw alternative, which
    /// captures from right after "=" - including the opening quote that started the failed attempt. That
    /// leading quote is not part of the real secret, so a caller matching this value as a bare substring
    /// elsewhere would never find it.
    /// </summary>
    private static string StripLeadingUnmatchedQuote(string raw) =>
        raw.Length > 0 && raw[0] is '"' or '\'' ? raw[1..] : raw;

    private static string AuthMode(SqlConnectionStringBuilder b)
    {
        if (b.Authentication != SqlAuthenticationMethod.NotSpecified) return EnumText.ToText(b.Authentication);
        if (b.IntegratedSecurity) return "integrated";
        return string.IsNullOrEmpty(b.UserID) ? "default" : "sql";
    }

    [GeneratedRegex("""(?i)(?<key>password|pwd)\s*=\s*(?:"(?<dq>(?:[^"]|"")*)"|'(?<sq>(?:[^']|'')*)'|(?<raw>[^;]*))""")]
    private static partial Regex PasswordPattern();

    /// <summary>
    /// Ruling 225: <see cref="PasswordPattern"/> is for parsing real, ;-separated connection strings (used by
    /// <see cref="SecretsOf"/>) and must stay that way. This is <see cref="Scrub"/>'s free-text pass: same
    /// key/quoting rules, but the raw value also stops at whitespace, not just ";" or the end of the string.
    /// </summary>
    [GeneratedRegex("""(?i)\b(?<key>password|pwd)\s*=\s*(?:"(?<dq>(?:[^"]|"")*)"|'(?<sq>(?:[^']|'')*)'|(?<raw>[^;\s]*))""")]
    private static partial Regex FreeTextPasswordPattern();
}
