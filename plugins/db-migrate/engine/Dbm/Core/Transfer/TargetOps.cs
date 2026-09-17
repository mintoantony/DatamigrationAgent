using System.Data;
using System.Globalization;
using Dbm.Core.Sql;
using Dbm.Core.SqlGen;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

public static class TargetOps
{
    /// <summary>Runs each non-empty statement as its own autocommit batch (PreSql/PostSql are session-independent).</summary>
    public static async Task ExecAllAsync(SqlConnection conn, IEnumerable<string> statements, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(statements);
        foreach (var sql in statements)
        {
            if (string.IsNullOrWhiteSpace(sql)) continue;
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// The scalar as a number, or <c>null</c> when the query returned no row or a NULL. A caller that records this as a count must
    /// keep the two apart: a 0 that means "nobody could count" reads exactly like one that means "there is nothing there".
    /// </summary>
    public static async Task<long?> ScalarLongOrNullAsync(SqlConnection conn, string sql, CancellationToken ct, int timeoutSec = 0)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(sql);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = timeoutSec };
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>The scalar, with no result counted as 0. Only for queries that cannot fail to produce one, such as COUNT_BIG over a
    /// table; anything a plan supplies goes through <see cref="ScalarLongOrNullAsync"/> instead.</summary>
    public static async Task<long> ScalarLongAsync(SqlConnection conn, string sql, CancellationToken ct, int timeoutSec = 0)
        => await ScalarLongOrNullAsync(conn, sql, ct, timeoutSec) ?? 0;

    public static Task<long> CountTargetAsync(SqlConnection conn, string targetKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(targetKey);
        return ScalarLongAsync(conn, $"SELECT COUNT_BIG(*) FROM {SqlQuote.TableKey(targetKey)}", ct);
    }

    public static string CountSqlOf(TaskPlan task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return string.IsNullOrWhiteSpace(task.CountSql)
            ? $"SELECT COUNT_BIG(*) FROM (\n{(task.SourceQuery ?? "").Trim().TrimEnd(';')}\n) AS q"
            : task.CountSql;
    }

    /// <summary>True when ANOTHER table has an FK to this one (V10: TRUNCATE would fail; self-references do not block it).</summary>
    public static async Task<bool> IsReferencedAsync(SqlConnection conn, string targetKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(targetKey);
        await using var cmd = new SqlCommand(
            "SELECT COUNT(*) FROM sys.foreign_keys WHERE referenced_object_id = OBJECT_ID(@t) AND parent_object_id <> referenced_object_id", conn);
        cmd.Parameters.Add(new SqlParameter("@t", SqlDbType.NVarChar, 600) { Value = SqlQuote.TableKey(targetKey) });
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>Empties targets children-first (reverse load order). Returns log lines like "DELETE app.Parent".</summary>
    public static async Task<List<string>> TruncateAsync(SqlConnection conn, IReadOnlyList<string> targetsInLoadOrder, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(targetsInLoadOrder);
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = targetsInLoadOrder.Count - 1; i >= 0; i--)
        {
            string target = targetsInLoadOrder[i];
            if (!seen.Add(target)) continue;
            bool referenced = await IsReferencedAsync(conn, target, ct);
            string table = SqlQuote.TableKey(target);
            await ExecAllAsync(conn, [referenced ? $"DELETE FROM {table};" : $"TRUNCATE TABLE {table};"], ct);
            lines.Add((referenced ? "DELETE " : "TRUNCATE ") + target);
        }
        return lines;
    }
}
