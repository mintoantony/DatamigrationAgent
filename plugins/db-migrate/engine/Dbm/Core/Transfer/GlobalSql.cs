using System.Text.RegularExpressions;
using Dbm.Core.Sql;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

/// <summary>
/// Ruling 184 (final review I-2): the plan's global PreSql - for every FK cycle the generator emits <c>ALTER TABLE … NOCHECK
/// CONSTRAINT</c>, and the sql-engineer may add more - must never be left in force in the target in silence. A completed run runs the
/// plan's PostSql as before. A <b>cancelled</b> run now runs it too, best effort, statement by statement, and records what each one did
/// in the run's notes. A <b>failed</b> or paused run keeps PreSql in force on purpose (Resume expects it) and says so, naming the
/// statements.
/// <para>Both lists must be safe to run twice: PreSql runs at the start of every segment (a resume re-applies it after a cancel/reopen
/// cycle just as after a pause), and PostSql can run on a cancel after a completed segment's own. The generator's
/// <c>NOCHECK CONSTRAINT</c> / <c>WITH CHECK CHECK CONSTRAINT</c> pairs are idempotent in SQL Server; hand-written ones must be too.</para>
/// </summary>
public static class GlobalSql
{
    /// <summary>The note a failed or paused run carries while PreSql is in force; a cancel replaces it with what it restored.</summary>
    public const string InForcePrefix = "The plan's pre-load SQL is still in force in the target:";

    /// <summary>Every note a cancel writes about PostSql starts with this.</summary>
    public const string RestorePrefix = "Cancel ran the plan's post-load SQL:";

    private static readonly Regex WithCheck = new(
        @"^\s*ALTER\s+TABLE\s+(?<table>.+?)\s+WITH\s+CHECK\s+CHECK\s+CONSTRAINT\s+(?<name>.+?)\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    public static List<string> Statements(IEnumerable<string>? sql) =>
        (sql ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();

    /// <summary>The note a failed or paused run carries, or null when the plan has no PreSql.</summary>
    public static string? InForceNote(IEnumerable<string>? preSql)
    {
        var pre = Statements(preSql);
        return pre.Count == 0 ? null
            : $"{InForcePrefix} {string.Join(" ", pre)} Resume expects it; Cancel runs the plan's post-load SQL to restore it.";
    }

    /// <summary>
    /// Runs <paramref name="postSql"/> one statement at a time and returns one note per statement, in order. Never throws for a SQL
    /// fault: a statement that fails is a note with the server's own text. A <c>WITH CHECK CHECK CONSTRAINT</c> that the rows already
    /// loaded violate is retried as a plain <c>CHECK CONSTRAINT</c>, so the constraint at least guards new writes, and the note says it
    /// is enabled but not trusted.
    /// </summary>
    public static async Task<List<string>> RestoreAsync(string targetCs, IEnumerable<string>? postSql, Func<string, string> scrub,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scrub);
        var statements = Statements(postSql);
        var notes = new List<string>();
        if (statements.Count == 0) return notes;
        SqlConnection conn;
        try
        {
            conn = await SqlConnect.OpenAsync(targetCs, ct);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException or TimeoutException)
        {
            notes.Add($"{RestorePrefix} it could not run, because the target could not be opened ({scrub(Describe(ex))}). Not restored: "
                      + string.Join(" ", statements) + " Run these statements in the target yourself.");
            return notes;
        }
        await using (conn)
        {
            foreach (var sql in statements)
            {
                try
                {
                    await TargetOps.ExecAllAsync(conn, [sql], ct);
                    notes.Add($"{RestorePrefix} restored - {sql}");
                }
                catch (SqlException ex)
                {
                    string why = scrub(Describe(ex));
                    var m = WithCheck.Match(sql);
                    if (!m.Success)
                    {
                        notes.Add($"{RestorePrefix} NOT restored - {sql} The server said: {why}");
                        continue;
                    }
                    string plain = $"ALTER TABLE {m.Groups["table"].Value} CHECK CONSTRAINT {m.Groups["name"].Value};";
                    try
                    {
                        await TargetOps.ExecAllAsync(conn, [plain], ct);
                        notes.Add($"{RestorePrefix} NOT restored WITH CHECK - {sql} The server said: {why} It was re-enabled for new "
                                  + "rows instead, but it is not trusted: rows already in the target violate it. Fix or remove them, then "
                                  + "run the statement again.");
                    }
                    catch (SqlException ex2)
                    {
                        notes.Add($"{RestorePrefix} NOT restored - {sql} The server said: {why} Re-enabling it without the check "
                                  + $"failed too: {scrub(Describe(ex2))} It is still disabled.");
                    }
                }
            }
        }
        return notes;
    }

    /// <summary>Drops the "still in force" notes a failed or paused run carried; a cancel's outcome notes replace them.</summary>
    public static IEnumerable<string> WithoutInForce(IEnumerable<string> notes) =>
        notes.Where(n => !n.StartsWith(InForcePrefix, StringComparison.Ordinal));

    private static string Describe(Exception ex) => TransferFailure.Describe(ex, t => t);
}
