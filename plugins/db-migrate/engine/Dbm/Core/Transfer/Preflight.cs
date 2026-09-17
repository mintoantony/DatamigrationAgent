using System.Data;
using System.Globalization;
using System.Text.Json;
using Dbm.Core.Catalog;
using Dbm.Core.Sql;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Transfer;

/// <summary>One line of the execute-screen checklist. <see cref="Ok"/> false with severity "error" blocks the run; "warning" does not.</summary>
public sealed record PreflightCheck(string Name, bool Ok, string Severity, string Detail);

public sealed record PreflightResult(int SqlVersion, DateTimeOffset At, List<PreflightCheck> Checks)
{
    public bool Passed => Checks.All(c => c.Ok || c.Severity != "error");
}

public sealed record ApprovedPlan(int Version, SqlPlanPayload Plan);

/// <summary>
/// Execute-screen checklist (spec section 8): connectivity, drift, approved SQL, permissions, target rows, volume.
/// <para><b>A check that did not run is in the list, marked as not run.</b> Leaving it out would show the operator a screen of checks
/// that all passed above a single red connection line, and nothing at all about the target - which reads as "nothing wrong there".</para>
/// <para>Preflight opens its own connections and closes them again; it takes no <see cref="RunLock"/> and is refused by none, because it
/// only reads.</para>
/// </summary>
public static class Preflight
{
    /// <summary>The approved SQL plan and its version, or null when the SQL phase is not approved or its artifact is gone.</summary>
    public static ApprovedPlan? LoadApprovedPlan(DbmServices s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var phase = s.Phases.Get(PhaseName.Sql);
        if (phase.Status != PhaseStatus.Approved || phase.ApprovedVersion is not int version) return null;
        var artifact = s.Artifacts.Get(PhaseName.Sql, version);
        return artifact is null ? null : new ApprovedPlan(version, Json.Deserialize<SqlPlanPayload>(artifact.PayloadJson));
    }

    public static async Task<PreflightResult> RunAsync(DbmServices s, TransferOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(options);
        var checks = new List<PreflightCheck>();
        ApprovedPlan? approved;
        try
        {
            approved = LoadApprovedPlan(s);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
        {
            checks.Add(Err("sql_plan", "The approved SQL plan could not be read: " + Describe(ex)));
            return new PreflightResult(0, Clock.Now(), checks);
        }
        if (approved is null)
        {
            checks.Add(Err("sql_plan", "The SQL phase is not approved yet."));
            return new PreflightResult(0, Clock.Now(), checks);
        }
        var phase = s.Phases.Get(PhaseName.Sql);
        checks.Add(phase.CurrentVersion == approved.Version
            ? Ok("sql_plan", $"Approved SQL plan v{approved.Version} ({approved.Plan.Tasks.Count} tasks).")
            : Err("sql_plan", $"The current SQL version (v{phase.CurrentVersion}) is not the approved one (v{approved.Version}); approve it again."));
        checks.Add(PlanCheck(approved.Plan));

        string? srcCs = s.Connections.GetConnectionString(Side.Src);
        string? tgtCs = s.Connections.GetConnectionString(Side.Tgt);
        var secrets = Redactor.SecretsOf(srcCs ?? "").Concat(Redactor.SecretsOf(tgtCs ?? "")).ToList();
        var src = await TryOpenAsync("source_connection", srcCs, checks, secrets, ct);
        var tgt = await TryOpenAsync("target_connection", tgtCs, checks, secrets, ct);
        try
        {
            if (src is not null && tgt is not null)
            {
                try
                {
                    var drift = await DriftChecker.CheckAsync(s, ct);
                    string sides = string.Join(" and ", new[] { drift.SrcChanged ? "source" : null, drift.TgtChanged ? "target" : null }.OfType<string>());
                    checks.Add(drift.Any
                        ? Err("schema_drift", $"The {sides} schema changed since discovery. Re-run discovery and review before transferring.")
                        : Ok("schema_drift", "Both schemas match the discovered catalogs."));
                }
                catch (Exception ex) when (ex is SqlException or InvalidOperationException)
                {
                    checks.Add(NotRun("schema_drift", "Drift check failed: " + Describe(ex)));
                }
            }
            else
            {
                checks.Add(NotRun("schema_drift",
                    "Not checked: comparing the schemas against the discovered catalogs needs both connections open."));
            }

            if (tgt is not null)
            {
                checks.AddRange(await TargetChecksAsync(tgt, approved.Plan, options, ct));
            }
            else
            {
                // One entry standing in for the whole group, rather than nothing: the target's tables, permissions, checkpoint table
                // and row counts are all unknown, and an absent check reads as a check that found nothing wrong.
                checks.Add(NotRun("target_checks",
                    "Not checked: the target connection could not be opened, so nothing is known about the target's tables, INSERT and "
                    + "ALTER permissions, the checkpoint table, or whether the target tables already hold rows."));
            }

            checks.Add(src is not null
                ? await SourceEstimateAsync(src, approved.Plan, ct)
                : NotRun("estimated_rows",
                    "Not counted: the source connection could not be opened, so the number of rows this run would move is unknown."));

            var keyless = TransferEngine.PlanOrder(approved.Plan).Where(id => approved.Plan.Tasks[id].KeyColumns.Count == 0)
                .Select(id => approved.Plan.Tasks[id].Target).ToList();
            if (keyless.Count > 0)
                checks.Add(Ok("keyless_tasks",
                    $"No usable key, loaded in one transaction (pause waits for them; failures restart them): {string.Join(", ", keyless)}."));
        }
        finally
        {
            if (src is not null) await src.DisposeAsync();
            if (tgt is not null) await tgt.DisposeAsync();
        }
        return new PreflightResult(approved.Version, Clock.Now(),
            checks.Select(c => c with { Detail = Scrubbed(c.Detail, secrets) }).ToList());
    }

    /// <summary>
    /// A plan with no tasks has no task with a validation error, so "none with validation errors" would pass it. It then reaches
    /// CreateRun, which refuses a run with no tasks (ruling L2) with an ArgumentException - an exception where a preflight error belongs.
    /// </summary>
    public static PreflightCheck PlanCheck(SqlPlanPayload plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Tasks.Count == 0) return Err("plan_valid", "The approved plan has no tasks, so there is nothing to transfer.");
        var bad = TransferEngine.PlanOrder(plan)
            .Where(id => plan.Tasks[id].Errors.Count > 0 || string.IsNullOrWhiteSpace(plan.Tasks[id].SourceQuery)).ToList();
        return bad.Count == 0
            ? Ok("plan_valid", $"{plan.Tasks.Count} tasks, none with validation errors.")
            : Err("plan_valid", "Tasks with validation errors: " + string.Join(", ", bad) + ". Fix them in the SQL phase.");
    }

    /// <summary>
    /// Target tables, INSERT/ALTER/DELETE permissions, the checkpoint table and existing rows.
    /// <para>A table that does not exist is skipped by every later check in the loop, so each verdict below names the tables it could
    /// not cover: "INSERT permission on every target table" pronounced over a set that silently excludes one is the same lie as no
    /// check at all.</para>
    /// </summary>
    public static async Task<List<PreflightCheck>> TargetChecksAsync(SqlConnection tgt, SqlPlanPayload plan, TransferOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tgt);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        var targets = TransferEngine.PlanOrder(plan).Select(id => plan.Tasks[id])
            .GroupBy(t => t.Target, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Target: g.Key, Identity: g.Any(t => t.IdentityInsert))).ToList();
        var missing = new List<string>();
        var missingIdentity = new List<string>();
        var noInsert = new List<string>();
        var noAlter = new List<string>();
        var noTruncate = new List<string>();
        var nonEmpty = new List<string>();
        foreach (var (target, identity) in targets)
        {
            int exists, insert, alter, delete;
            await using (var cmd = new SqlCommand(
                "SELECT CASE WHEN OBJECT_ID(@t, N'U') IS NULL THEN 0 ELSE 1 END, ISNULL(HAS_PERMS_BY_NAME(@t, N'OBJECT', N'INSERT'), 0), " +
                "ISNULL(HAS_PERMS_BY_NAME(@t, N'OBJECT', N'ALTER'), 0), ISNULL(HAS_PERMS_BY_NAME(@t, N'OBJECT', N'DELETE'), 0)", tgt))
            {
                cmd.Parameters.Add(new SqlParameter("@t", SqlDbType.NVarChar, 600) { Value = SqlQuote.TableKey(target) });
                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                (exists, insert, alter, delete) = (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3));
            }
            if (exists == 0)
            {
                missing.Add(target);
                if (identity) missingIdentity.Add(target);
                continue;
            }
            if (insert == 0) noInsert.Add(target);
            if (identity && alter == 0) noAlter.Add(target);
            if (options.TruncateTarget && (alter == 0 || delete == 0)) noTruncate.Add(target);
            long rows = await TargetOps.CountTargetAsync(tgt, target, ct);
            if (rows > 0) nonEmpty.Add($"{target} ({rows.ToString("N0", CultureInfo.InvariantCulture)})");
        }

        string skipped = missing.Count == 0 ? "" : " Not checked, because they do not exist: " + string.Join(", ", missing) + ".";
        string skippedIdentity = missingIdentity.Count == 0
            ? "" : " Not checked, because they do not exist: " + string.Join(", ", missingIdentity) + ".";
        var checks = new List<PreflightCheck>
        {
            missing.Count == 0 ? Ok("target_tables", $"All {targets.Count} target tables exist.")
                : Err("target_tables", "Missing target tables: " + string.Join(", ", missing) + "."),
            noInsert.Count == 0 ? Ok("insert_permission", "INSERT permission on every target table that exists." + skipped)
                : Err("insert_permission", "No INSERT permission on: " + string.Join(", ", noInsert) + "." + skipped),
        };
        if (targets.Any(t => t.Identity))
            checks.Add(noAlter.Count == 0
                ? Ok("identity_insert_permission", "ALTER permission for identity-preserving loads." + skippedIdentity)
                : Err("identity_insert_permission", "Keeping identity values needs ALTER on: " + string.Join(", ", noAlter) + "." + skippedIdentity));
        if (options.TruncateTarget)
            checks.Add(noTruncate.Count == 0
                ? Ok("truncate_permission", "ALTER and DELETE permission to empty the target tables." + skipped)
                : Err("truncate_permission", "Emptying targets needs ALTER and DELETE on: " + string.Join(", ", noTruncate) + "." + skipped));

        int ctlExists, canCreate, canAlterDbo;
        await using (var cmd = new SqlCommand(
            $"SELECT CASE WHEN OBJECT_ID(N'{ControlTable.Name}', N'U') IS NULL THEN 0 ELSE 1 END, " +
            "ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CREATE TABLE'), 0), ISNULL(HAS_PERMS_BY_NAME(N'dbo', N'SCHEMA', N'ALTER'), 0)", tgt))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            (ctlExists, canCreate, canAlterDbo) = (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2));
        }
        checks.Add(ctlExists == 1 ? Ok("control_table", $"The checkpoint table {ControlTable.Name} already exists and will be reused.")
            : canCreate == 1 && canAlterDbo == 1 ? Ok("control_table", $"Can create the checkpoint table {ControlTable.Name}.")
            : Err("control_table", $"Creating {ControlTable.Name} needs CREATE TABLE and ALTER on schema dbo."));

        checks.Add(nonEmpty.Count == 0 ? Ok("target_rows", "All target tables that exist are empty." + skipped)
            : options.TruncateTarget ? Ok("target_rows", "Will be emptied first: " + string.Join(", ", nonEmpty) + "." + skipped)
            : new PreflightCheck("target_rows", false, "warning",
                "Target tables already contain rows: " + string.Join(", ", nonEmpty)
                + ". Loading may hit duplicate keys; consider 'Truncate target first'." + skipped));
        return checks;
    }

    /// <summary>
    /// How many rows the run would move. A count that comes back as NULL or as no row at all - which a plan-supplied CountSql can do -
    /// is not a count of zero: it is left out of the total and named, so the estimate never understates the volume in silence.
    /// </summary>
    public static async Task<PreflightCheck> SourceEstimateAsync(SqlConnection src, SqlPlanPayload plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(plan);
        long total = 0;
        int counted = 0;
        (string Target, long Rows) largest = ("", -1);
        var couldNotCount = new List<string>();
        foreach (var id in TransferEngine.PlanOrder(plan))
        {
            var task = plan.Tasks[id];
            try
            {
                long? n = await TargetOps.ScalarLongOrNullAsync(src, TargetOps.CountSqlOf(task), ct, timeoutSec: 120);
                if (n is null)
                {
                    couldNotCount.Add($"{id} (the query returned no number, so its rows are not in the total)");
                    continue;
                }
                total += n.Value;
                counted++;
                if (n.Value > largest.Rows) largest = (task.Target, n.Value);
            }
            catch (SqlException ex)
            {
                couldNotCount.Add($"{id} ({Describe(ex)})");
            }
        }
        string detail = $"{total.ToString("N0", CultureInfo.InvariantCulture)} rows across {counted} tasks" +
                        (largest.Rows >= 0 ? $" (largest: {largest.Target} {largest.Rows.ToString("N0", CultureInfo.InvariantCulture)})" : "") + ".";
        return couldNotCount.Count == 0
            ? Ok("estimated_rows", detail)
            : new PreflightCheck("estimated_rows", false, "warning", detail + " Could not count: " + string.Join("; ", couldNotCount) + ".");
    }

    private static async Task<SqlConnection?> TryOpenAsync(string name, string? cs, List<PreflightCheck> checks, IReadOnlyList<string> secrets,
        CancellationToken ct)
    {
        if (cs is null)
        {
            checks.Add(Err(name, "No connection saved."));
            return null;
        }
        try
        {
            var conn = await SqlConnect.OpenAsync(cs, ct);
            checks.Add(Ok(name, "Connected: " + Redactor.Describe(cs)));
            return conn;
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException)
        {
            checks.Add(Err(name, "Cannot connect: " + Redactor.Scrub(Describe(ex), secrets)));
            return null;
        }
    }

    /// <summary>An exception's message is never allowed to be blank here: a check whose detail is empty is a check that says nothing.</summary>
    private static string Describe(Exception ex) => TransferFailure.Describe(ex, t => t);

    /// <summary>Scrubbing can empty a detail whose whole content was a secret; an empty detail would then be an invisible reason.</summary>
    private static string Scrubbed(string detail, IReadOnlyList<string> secrets)
        => TransferFailure.NonBlank(Redactor.Scrub(detail, secrets), "(removed: the detail consisted of a secret)");

    private static PreflightCheck Ok(string name, string detail) => new(name, true, "info", detail);
    private static PreflightCheck Err(string name, string detail) => new(name, false, "error", detail);

    /// <summary>A check that did not run. Not "ok", so it cannot be read as a pass; not an "error", so it does not block a run on its
    /// own - whatever stopped it is already an error of its own in the list.</summary>
    private static PreflightCheck NotRun(string name, string detail) => new(name, false, "warning", detail);
}
