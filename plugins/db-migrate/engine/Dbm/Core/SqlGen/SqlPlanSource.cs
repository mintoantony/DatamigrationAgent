using Dbm.Core.Mapping;
using Dbm.Core.State;

namespace Dbm.Core.SqlGen;

/// <summary>Loads the inputs of the SQL phase from the state store and runs live validation with the saved connections.
/// Shared by SqlGenJob, SqlModule, the `sql` CLI commands and SqlEndpoints.</summary>
public static class SqlPlanSource
{
    /// <summary>The approved Mapping artifact, or null when Mapping is not approved.</summary>
    public static MappingPayload? ApprovedMapping(DbmServices s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var version = s.Phases.Get(PhaseName.Mapping).ApprovedVersion;
        if (version is null) return null;
        var row = s.Artifacts.Get(PhaseName.Mapping, version.Value);
        return row is null ? null : Json.Deserialize<MappingPayload>(row.PayloadJson);
    }

    /// <summary>The artifact row of the sql phase's current version, or null.</summary>
    public static ArtifactRow? CurrentRow(DbmServices s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var version = s.Phases.Get(PhaseName.Sql).CurrentVersion;
        return version is null ? null : s.Artifacts.Get(PhaseName.Sql, version.Value);
    }

    /// <summary>Start of the engine-authored plan warning recording that live validation did NOT run. A stored plan carrying it is
    /// unvalidated: SqlModule.ApprovalBlockers blocks it, and SqlModule.Validate re-derives it on every run.</summary>
    public const string SkippedPrefix = "live validation skipped: ";

    /// <summary>"live validation skipped: source connection, target catalog missing" naming every missing input, or null when
    /// live validation can run.</summary>
    public static string? SkippedWarning(DbmServices s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var missing = new List<string>();
        if (!s.Connections.Has(Side.Src)) missing.Add("source connection");
        if (!s.Connections.Has(Side.Tgt)) missing.Add("target connection");
        if (s.Catalog.Get(Side.Tgt) is null) missing.Add("target catalog");
        return missing.Count == 0 ? null : SkippedPrefix + string.Join(", ", missing) + " missing";
    }

    /// <summary>True when both connections and the target catalog are available.</summary>
    public static bool CanValidate(DbmServices s) => SkippedWarning(s) is null;

    /// <summary>Validates against the live databases. Throws InvalidOperationException when <see cref="CanValidate"/> is false.</summary>
    public static Task<ValidationReport> ValidateLiveAsync(DbmServices s, SqlPlanPayload plan, string? onlyTaskId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(plan);
        var src = s.Connections.GetConnectionString(Side.Src);
        var tgt = s.Connections.GetConnectionString(Side.Tgt);
        var tgtCatalog = s.Catalog.Get(Side.Tgt);
        if (src is null || tgt is null) throw new InvalidOperationException("Both connections must be saved before SQL can be validated.");
        if (tgtCatalog is null) throw new InvalidOperationException("The target catalog is missing; run discovery first.");
        return SqlValidator.ValidateAsync(plan, src, tgt, tgtCatalog, onlyTaskId, ct);
    }
}
