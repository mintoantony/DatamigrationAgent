using System.Data;
using Dbm.Core.Sql;
using Dbm.Core.SqlGen;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

/// <summary>Keyset chunk queries over the task's SourceQuery used as a derived table (V6).</summary>
public static class ChunkPlanner
{
    /// <summary>(k0 > @k0) for one key; for n keys a seekable prefix plus the lexicographic OR chain.</summary>
    public static string Predicate(IReadOnlyList<string> keyColumns)
    {
        ArgumentNullException.ThrowIfNull(keyColumns);
        CheckKeys(keyColumns, "Keyset chunking");
        string K(int i) => "q." + SqlQuote.Ident(keyColumns[i]);
        var terms = new List<string>();
        for (int i = 0; i < keyColumns.Count; i++)
        {
            var parts = new List<string>();
            for (int j = 0; j < i; j++) parts.Add($"{K(j)} = @k{j}");
            parts.Add($"{K(i)} > @k{i}");
            terms.Add("(" + string.Join(" AND ", parts) + ")");
        }
        return keyColumns.Count == 1 ? terms[0] : $"{K(0)} >= @k0 AND ({string.Join(" OR ", terms)})";
    }

    /// <summary>SELECT TOP (@__n) over the source query ordered by every key; <paramref name="afterKey"/> adds the keyset predicate.</summary>
    public static string Sql(TaskPlan task, bool afterKey)
    {
        ArgumentNullException.ThrowIfNull(task);
        CheckKeys(task.KeyColumns, $"Task for {task.Target}");
        string source = task.SourceQuery ?? throw new TransferException("bad_task", $"Task for {task.Target} has no source query.");
        string trimmed;
        do
        {
            trimmed = source;
            source = source.Trim().TrimEnd(';');
        } while (source != trimmed);
        if (source.Length == 0) throw new TransferException("bad_task", $"Task for {task.Target} has an empty source query.");

        string order = string.Join(", ", task.KeyColumns.Select(k => "q." + SqlQuote.Ident(k)));
        string where = afterKey ? " WHERE " + Predicate(task.KeyColumns) : "";
        return $"SELECT TOP (@__n) q.* FROM (\n{source}\n) AS q{where} ORDER BY {order}";
    }

    /// <summary>The chunk command: first chunk when <paramref name="lastKey"/> is null, else the rows strictly after it.</summary>
    public static SqlCommand Command(SqlConnection conn, TaskPlan task, KeyValue? lastKey, int chunkSize)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(task);
        // TOP (0) would return no rows, which a caller reads as "task finished".
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        string sql = Sql(task, lastKey is not null);
        if (lastKey is not null
            && (lastKey.Types is null || lastKey.Values is null
                || lastKey.Types.Count != task.KeyColumns.Count || lastKey.Values.Count != task.KeyColumns.Count))
            throw new TransferException("bad_key",
                $"Resume key for {task.Target} has {lastKey.Types?.Count ?? 0} types and {lastKey.Values?.Count ?? 0} values but the task has {task.KeyColumns.Count} key columns.");

        var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
        try
        {
            cmd.Parameters.Add(new SqlParameter("@__n", SqlDbType.BigInt) { Value = (long)chunkSize });
            if (lastKey is not null)
                for (int i = 0; i < task.KeyColumns.Count; i++)
                    cmd.Parameters.Add(KeyCodec.Parameter($"@k{i}", lastKey.Types![i], lastKey.Values![i]));
            return cmd;
        }
        catch
        {
            cmd.Dispose();
            throw;
        }
    }

    private static void CheckKeys(IReadOnlyList<string>? keys, string who)
    {
        if (keys is null || keys.Count == 0) throw new TransferException("no_key", $"{who} needs at least one key column.");
        if (keys.Any(string.IsNullOrWhiteSpace)) throw new TransferException("bad_task", $"{who} has a blank key column name.");
    }
}
