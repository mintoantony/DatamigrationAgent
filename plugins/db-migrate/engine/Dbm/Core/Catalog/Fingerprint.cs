using System.Security.Cryptography;
using System.Text;

namespace Dbm.Core.Catalog;

/// <summary>
/// Structural fingerprint of a catalog snapshot: stable across row-count/size and profiling changes, but changes
/// whenever a table, column, index, foreign key or trigger is added, removed or redefined.
/// </summary>
public static class Fingerprint
{
    public static string Compute(CatalogSnapshot snapshot)
    {
        var projection = snapshot.Tables
            .OrderBy(t => t.Key, StringComparer.Ordinal)
            .Select(t => new
            {
                t.Key,
                Columns = t.Columns.Select(c => new
                {
                    c.Name, c.DataType, c.MaxLength, c.Precision, c.Scale, c.IsNullable, c.IsIdentity,
                    c.IsComputed, c.ComputedDefinition, c.DefaultDefinition, c.Collation,
                }),
                // Sorted by name (not index_id, a creation-order artifact) so differently-ordered DDL hashes identically.
                Indexes = t.Indexes.OrderBy(i => i.Name, StringComparer.Ordinal)
                    .Select(i => new { i.Name, i.IsPrimaryKey, i.IsUnique, i.IsClustered, i.Columns }),
                ForeignKeys = t.ForeignKeys.Select(f => new { f.Name, f.Columns, f.RefSchema, f.RefTable, f.RefColumns }),
                t.TriggerNames,
                t.TemporalType,
            });
        var json = Json.Serialize(projection);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}
