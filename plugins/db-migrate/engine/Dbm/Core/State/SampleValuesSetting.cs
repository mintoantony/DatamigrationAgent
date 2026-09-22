using Dbm.Core.Catalog;

namespace Dbm.Core.State;

/// <summary>The answer of a switch: the setting now, how many stored column profiles lost values, and what it means.</summary>
public sealed record SampleValuesChange(bool SampleValues, int ScrubbedColumns, string Note);

/// <summary>
/// Ruling 196 (open item 33): the switch for <see cref="ProjectSettings.SampleValues"/> - whether the profiles Claude reads carry
/// sample values (up to 3 distinct values per text column, and a text column's MIN/MAX, which are real values too). Discovery
/// collects them only while it is on. Turning it off also removes the ones already stored, so from then on neither a work packet
/// nor `dbm show` can carry one; turning it on takes effect at the next discovery.
/// </summary>
public static class SampleValuesSetting
{
    public const string OffNote = "Sample values are off: the stored catalogs hold none, so work packets and dbm show carry none. "
                                  + "Claude still sees null shares, distinct counts, lengths, value patterns and classes; its analysis "
                                  + "and mapping suggestions may be less precise. Files exported earlier to .dbmigrate/exports keep "
                                  + "the values they were written with.";

    public const string OnNote = "Sample values are on. Profiles collected while they were off have none: run discovery again "
                                 + "(Re-run discovery on the Analysis screen, or dbm discover) to collect them.";

    public static SampleValuesChange Set(DbmServices services, bool on)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (on)
        {
            services.Db.InTransaction(() => services.Project.SaveSettings(services.Project.GetSettings() with { SampleValues = true }));
            return new SampleValuesChange(true, 0, OnNote);
        }

        // Ruling 199: the catalog rows are rewritten with SQLite's secure_delete on, so the pages that held the old JSON are zeroed
        // rather than left in the file; the write-ahead log is then checkpointed and truncated, and the work packets written from
        // the old catalogs are deleted (dbm next writes the current one again). Agent patch files (*.patch.json) are left alone.
        var db = services.Db;
        var secure = db.Scalar<long>("PRAGMA secure_delete;");
        db.Execute("PRAGMA secure_delete = ON;");
        int scrubbed;
        try
        {
            scrubbed = db.InTransaction(() =>
            {
                services.Project.SaveSettings(services.Project.GetSettings() with { SampleValues = false });
                return Scrub(services);
            });
        }
        finally
        {
            if (secure == 0) db.Execute("PRAGMA secure_delete = OFF;");
        }
        db.Execute("PRAGMA wal_checkpoint(TRUNCATE);");
        DeletePackets(services.Ws);
        return new SampleValuesChange(false, scrubbed, OffNote);
    }

    private static void DeletePackets(Workspace ws)
    {
        if (!Directory.Exists(ws.WorkDir)) return;
        foreach (var file in Directory.EnumerateFiles(ws.WorkDir, "*.json"))
        {
            if (file.EndsWith(".patch.json", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // a packet being read right now: the next `dbm next` overwrites it
            }
        }
    }

    /// <summary>
    /// Removes every sample value from the stored catalogs - each profile's samples, and a text column's MIN/MAX - exactly what a
    /// discovery with the setting off leaves out (<c>Profiler.Build</c>). Structure, fingerprints, numeric ranges, patterns and
    /// classes stay, so this causes no drift. Returns the number of columns changed.
    /// </summary>
    public static int Scrub(DbmServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var changed = 0;
        foreach (var side in new[] { Side.Src, Side.Tgt })
        {
            if (services.Catalog.Get(side) is not { } snapshot || services.Catalog.Fingerprint(side) is not { } fingerprint) continue;
            var (stripped, count) = Strip(snapshot);
            if (count == 0) continue;
            services.Catalog.Save(side, stripped, fingerprint);
            changed += count;
        }
        return changed;
    }

    /// <summary>The snapshot without sample values (each profile's samples, and a text column's MIN/MAX) and how many columns lost
    /// some. Structure is untouched, so its fingerprint is the same.</summary>
    public static (CatalogSnapshot Snapshot, int Columns) Strip(CatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var changed = 0;
        var tables = snapshot.Tables.Select(t => t with
        {
            Columns = t.Columns.Select(c =>
            {
                if (c.Profile is not { } p) return c;
                var text = TypeTraits.IsString(c.DataType);
                if (p.Samples.Count == 0 && !(text && (p.Min is not null || p.Max is not null))) return c;
                changed++;
                return c with { Profile = p with { Samples = [], Min = text ? null : p.Min, Max = text ? null : p.Max } };
            }).ToList(),
        }).ToList();
        return (changed == 0 ? snapshot : snapshot with { Tables = tables }, changed);
    }
}
