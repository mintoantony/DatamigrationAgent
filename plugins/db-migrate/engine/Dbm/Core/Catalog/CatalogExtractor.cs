using Dbm.Core.Sql;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Catalog;

/// <summary>Reads the structure of every user table from the sys.* catalog views (no data access except row counts).</summary>
public static class CatalogExtractor
{
    private const int CommandTimeoutSeconds = 300;

    // Excludes dbm's own control table (dbo.__dbm_checkpoint, M5) and the SSMS diagram table.
    private const string UserTableFilter =
        "t.is_ms_shipped = 0 AND t.name <> N'sysdiagrams' AND t.name NOT LIKE N'[_][_]dbm[_]%'";

    private const string TablesSql = """
        SELECT t.object_id, s.name AS schema_name, t.name, CAST(ep.value AS nvarchar(4000)) AS description, {temporal} AS temporal_type
        FROM sys.tables AS t
        JOIN sys.schemas AS s ON s.schema_id = t.schema_id
        LEFT JOIN sys.extended_properties AS ep
          ON ep.class = 1 AND ep.major_id = t.object_id AND ep.minor_id = 0 AND ep.name = N'MS_Description'
        WHERE {filter}
        ORDER BY s.name, t.name;
        """;

    private const string ColumnsSql = """
        SELECT c.object_id, c.column_id, c.name,
          CASE WHEN ty.user_type_id <> ty.system_type_id AND ty.is_assembly_type = 0 THEN bt.name ELSE ty.name END AS type_name,
          CAST(c.max_length AS int) AS max_length, CAST(c.precision AS int) AS precision, CAST(c.scale AS int) AS scale,
          c.is_nullable, c.is_identity, c.is_computed, c.collation_name, dc.definition AS default_definition,
          cc.definition AS computed_definition, CAST(ep.value AS nvarchar(4000)) AS description
        FROM sys.columns AS c
        JOIN sys.tables AS t ON t.object_id = c.object_id
        JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
        LEFT JOIN sys.types AS bt ON bt.user_type_id = ty.system_type_id
        LEFT JOIN sys.default_constraints AS dc ON dc.object_id = c.default_object_id
        LEFT JOIN sys.computed_columns AS cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
        LEFT JOIN sys.extended_properties AS ep
          ON ep.class = 1 AND ep.major_id = c.object_id AND ep.minor_id = c.column_id AND ep.name = N'MS_Description'
        WHERE t.is_ms_shipped = 0
        ORDER BY c.object_id, c.column_id;
        """;

    private const string IndexesSql = """
        SELECT i.object_id, i.index_id, i.name, i.is_primary_key, i.is_unique,
          CAST(CASE WHEN i.type IN (1, 5) THEN 1 ELSE 0 END AS bit) AS is_clustered
        FROM sys.indexes AS i
        JOIN sys.tables AS t ON t.object_id = i.object_id
        WHERE t.is_ms_shipped = 0 AND i.type IN (1, 2, 5, 7) AND i.is_hypothetical = 0 AND i.name IS NOT NULL
        ORDER BY i.object_id, i.index_id;
        """;

    private const string IndexColumnsSql = """
        SELECT ic.object_id, ic.index_id, c.name
        FROM sys.index_columns AS ic
        JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        JOIN sys.tables AS t ON t.object_id = ic.object_id
        WHERE t.is_ms_shipped = 0 AND ic.key_ordinal > 0 AND ic.is_included_column = 0
        ORDER BY ic.object_id, ic.index_id, ic.key_ordinal;
        """;

    private const string ForeignKeysSql = """
        SELECT fk.object_id, fk.parent_object_id, fk.name, rs.name AS ref_schema, rt.name AS ref_table, fk.is_disabled, fk.is_not_trusted
        FROM sys.foreign_keys AS fk
        JOIN sys.tables AS t ON t.object_id = fk.parent_object_id
        JOIN sys.tables AS rt ON rt.object_id = fk.referenced_object_id
        JOIN sys.schemas AS rs ON rs.schema_id = rt.schema_id
        WHERE t.is_ms_shipped = 0
        ORDER BY fk.parent_object_id, fk.name;
        """;

    private const string ForeignKeyColumnsSql = """
        SELECT fkc.constraint_object_id, pc.name AS column_name, rc.name AS ref_column_name
        FROM sys.foreign_key_columns AS fkc
        JOIN sys.columns AS pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
        JOIN sys.columns AS rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
        ORDER BY fkc.constraint_object_id, fkc.constraint_column_id;
        """;

    private const string TriggersSql = """
        SELECT tr.parent_id, tr.name FROM sys.triggers AS tr WHERE tr.parent_class = 1 ORDER BY tr.parent_id, tr.name;
        """;

    private const string ObjectCountsSql = """
        SELECT COUNT(CASE WHEN o.type = 'V' THEN 1 END) AS views,
          COUNT(CASE WHEN o.type IN ('P', 'PC') THEN 1 END) AS procedures,
          COUNT(CASE WHEN o.type IN ('FN', 'IF', 'TF', 'FS', 'FT') THEN 1 END) AS functions,
          COUNT(CASE WHEN o.type = 'TR' THEN 1 END) AS triggers,
          COUNT(CASE WHEN o.type = 'SN' THEN 1 END) AS synonyms
        FROM sys.objects AS o WHERE o.is_ms_shipped = 0;
        """;

    // Needs VIEW DATABASE STATE; falls back to PartitionsSql (rows only, SizeMb = 0) when denied.
    private const string PartitionStatsSql = """
        SELECT ps.object_id, SUM(ps.row_count) AS row_count, CAST(SUM(ps.used_page_count) * 8 / 1024.0 AS float) AS size_mb
        FROM sys.dm_db_partition_stats AS ps
        JOIN sys.tables AS t ON t.object_id = ps.object_id
        WHERE t.is_ms_shipped = 0 AND ps.index_id IN (0, 1)
        GROUP BY ps.object_id;
        """;

    private const string PartitionsSql = """
        SELECT p.object_id, SUM(p.rows) AS row_count
        FROM sys.partitions AS p
        JOIN sys.tables AS t ON t.object_id = p.object_id
        WHERE t.is_ms_shipped = 0 AND p.index_id IN (0, 1)
        GROUP BY p.object_id;
        """;

    public static async Task<CatalogSnapshot> ExtractAsync(SqlConnection conn, ServerMeta meta, CancellationToken ct)
    {
        var temporal = meta.MajorVersion >= 13 ? "CAST(t.temporal_type AS int)" : "CAST(0 AS int)";
        var tables = new List<TableBuilder>();
        var byId = new Dictionary<int, TableBuilder>();

        var tablesSql = TablesSql.Replace("{temporal}", temporal, StringComparison.Ordinal)
            .Replace("{filter}", UserTableFilter, StringComparison.Ordinal);
        await ReadAsync(conn, tablesSql, r =>
        {
            var table = new TableBuilder(r.GetString(1), r.GetString(2), Str(r, 3), TemporalText(r.GetInt32(4)));
            tables.Add(table);
            byId[r.GetInt32(0)] = table;
        }, ct);

        await ReadAsync(conn, ColumnsSql, r =>
        {
            if (!byId.TryGetValue(r.GetInt32(0), out var table)) return;
            var type = r.GetString(3).ToLowerInvariant();
            table.Columns.Add(new ColumnInfo(
                Name: r.GetString(2),
                Ordinal: table.Columns.Count + 1,
                DataType: type,
                MaxLength: NormalizeMaxLength(type, r.GetInt32(4)),
                Precision: r.GetInt32(5),
                Scale: r.GetInt32(6),
                IsNullable: Bool(r, 7),
                IsIdentity: Bool(r, 8),
                IsComputed: Bool(r, 9),
                IsRowVersion: type == "timestamp",
                DefaultDefinition: Str(r, 11),
                Collation: Str(r, 10),
                Description: Str(r, 13))
            {
                ComputedDefinition = Str(r, 12),
            });
        }, ct);

        var indexes = new Dictionary<(int ObjectId, int IndexId), IndexBuilder>();
        await ReadAsync(conn, IndexesSql, r =>
        {
            if (!byId.TryGetValue(r.GetInt32(0), out var table)) return;
            var index = new IndexBuilder(r.GetString(2), Bool(r, 3), Bool(r, 4), Bool(r, 5));
            table.Indexes.Add(index);
            indexes[(r.GetInt32(0), r.GetInt32(1))] = index;
        }, ct);
        await ReadAsync(conn, IndexColumnsSql, r =>
        {
            if (indexes.TryGetValue((r.GetInt32(0), r.GetInt32(1)), out var index)) index.Columns.Add(r.GetString(2));
        }, ct);

        var foreignKeys = new Dictionary<int, ForeignKeyBuilder>();
        await ReadAsync(conn, ForeignKeysSql, r =>
        {
            if (!byId.TryGetValue(r.GetInt32(1), out var table)) return;
            var fk = new ForeignKeyBuilder(r.GetString(2), r.GetString(3), r.GetString(4), Bool(r, 5), Bool(r, 6));
            table.ForeignKeys.Add(fk);
            foreignKeys[r.GetInt32(0)] = fk;
        }, ct);
        await ReadAsync(conn, ForeignKeyColumnsSql, r =>
        {
            if (!foreignKeys.TryGetValue(r.GetInt32(0), out var fk)) return;
            fk.Columns.Add(r.GetString(1));
            fk.RefColumns.Add(r.GetString(2));
        }, ct);

        await ReadAsync(conn, TriggersSql, r =>
        {
            if (byId.TryGetValue(r.GetInt32(0), out var table)) table.TriggerNames.Add(r.GetString(1));
        }, ct);

        var objects = new ObjectCounts(0, 0, 0, 0, 0);
        await ReadAsync(conn, ObjectCountsSql, r =>
            objects = new ObjectCounts(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4)), ct);

        try
        {
            await ReadAsync(conn, PartitionStatsSql, r =>
            {
                if (!byId.TryGetValue(r.GetInt32(0), out var table)) return;
                table.Rows = r.GetInt64(1);
                table.SizeMb = r.GetDouble(2);
            }, ct);
        }
        catch (SqlException)
        {
            // VIEW DATABASE STATE denied: row counts from sys.partitions, size unknown.
            await ReadAsync(conn, PartitionsSql, r =>
            {
                if (!byId.TryGetValue(r.GetInt32(0), out var table)) return;
                table.Rows = r.GetInt64(1);
                table.SizeMb = 0;
            }, ct);
        }

        return new CatalogSnapshot(meta, tables.Select(t => t.Build()).ToList(), objects, Clock.Now());
    }

    /// <summary>nchar/nvarchar store 2 bytes per character; only character and binary types keep a length.</summary>
    public static int NormalizeMaxLength(string dataType, int rawMaxLength) => dataType switch
    {
        "nchar" or "nvarchar" => rawMaxLength == -1 ? -1 : rawMaxLength / 2,
        "char" or "varchar" or "binary" or "varbinary" => rawMaxLength,
        _ => 0,
    };

    private static string? TemporalText(int temporalType) => temporalType switch
    {
        1 => "history",
        2 => "system_versioned",
        _ => null,
    };

    private static async Task ReadAsync(SqlConnection conn, string sql, Action<SqlDataReader> onRow, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = CommandTimeoutSeconds };
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) onRow(reader);
    }

    private static string? Str(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static bool Bool(SqlDataReader r, int i) => !r.IsDBNull(i) && r.GetBoolean(i);

    private sealed class TableBuilder(string schema, string name, string? description, string? temporalType)
    {
        public long Rows;
        public double SizeMb;
        public List<ColumnInfo> Columns { get; } = new();
        public List<IndexBuilder> Indexes { get; } = new();
        public List<ForeignKeyBuilder> ForeignKeys { get; } = new();
        public List<string> TriggerNames { get; } = new();

        public TableInfo Build() => new(schema, name, Rows, Math.Round(SizeMb, 3), Columns,
            Indexes.Select(i => i.Build()).ToList(), ForeignKeys.Select(f => f.Build()).ToList(),
            TriggerNames.Count, temporalType, description)
        {
            TriggerNames = TriggerNames.ToList(),
        };
    }

    private sealed class IndexBuilder(string name, bool isPrimaryKey, bool isUnique, bool isClustered)
    {
        public List<string> Columns { get; } = new();
        public IndexInfo Build() => new(name, isPrimaryKey, isUnique, isClustered, Columns.ToList());
    }

    private sealed class ForeignKeyBuilder(string name, string refSchema, string refTable, bool isDisabled, bool isNotTrusted)
    {
        public List<string> Columns { get; } = new();
        public List<string> RefColumns { get; } = new();
        public ForeignKeyInfo Build() => new(name, Columns.ToList(), refSchema, refTable, RefColumns.ToList(), isDisabled, isNotTrusted);
    }
}
