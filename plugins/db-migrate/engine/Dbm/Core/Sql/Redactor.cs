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

    /// <summary>Replaces each secret value (length ≥ 4) with "***".</summary>
    public static string Scrub(string text, IEnumerable<string?> secrets)
    {
        foreach (var s in secrets)
        {
            if (!string.IsNullOrEmpty(s) && s.Length >= 4) text = text.Replace(s, "***", StringComparison.Ordinal);
        }
        return text;
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
                .Select(m => m.Groups["dq"].Success ? m.Groups["dq"].Value : m.Groups["sq"].Success ? m.Groups["sq"].Value : m.Groups["raw"].Value)
                .Where(v => v.Length > 0)
                .ToList();
        }
    }

    private static string AuthMode(SqlConnectionStringBuilder b)
    {
        if (b.Authentication != SqlAuthenticationMethod.NotSpecified) return EnumText.ToText(b.Authentication);
        if (b.IntegratedSecurity) return "integrated";
        return string.IsNullOrEmpty(b.UserID) ? "default" : "sql";
    }

    [GeneratedRegex("""(?i)(?:password|pwd)\s*=\s*(?:"(?<dq>[^"]*)"|'(?<sq>[^']*)'|(?<raw>[^;]*))""")]
    private static partial Regex PasswordPattern();
}
