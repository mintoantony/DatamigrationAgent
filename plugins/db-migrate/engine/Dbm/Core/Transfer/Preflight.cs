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
        PhaseRow phase;
        ApprovedPlan? approved;
        try
        {
            phase = s.Phases.Get(PhaseName.Sql);
            approved = LoadApprovedPlan(s);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
        {
            checks.Add(Err("sql_plan", "The approved SQL plan could not be read: " + Describe(ex)));
            return new PreflightResult(0, Clock.Now(), checks);
        }
        if (approved is null)
        {
            // Two different states reach a null plan, and they need two different sentences: telling an operator to approve a plan
            // they have already approved sends them to do the one thing that cannot help, because what is missing is the payload.
            checks.Add(phase.Status == PhaseStatus.Approved && phase.ApprovedVersion is int approvedVersion
                ? Err("sql_plan", $"The SQL phase is approved at v{approvedVersion}, but that version's artifact is missing from this "
                                  + "workspace, so the plan cannot be read. Re-generate the SQL phase and approve it again.")
                : Err("sql_plan", "The SQL phase is not approved yet."));
            return new PreflightResult(0, Clock.Now(), checks);
        }
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
                // JsonException too: the drift check reads the ServerMeta discovery stored, and a stored value that will not parse must
                // cost a line of the checklist, not the checklist.
                catch (Exception ex) when (ex is SqlException or InvalidOperationException or TimeoutException or JsonException)
                {
                    // Ruling 120: both connections are open here, so nothing else in the list is an error. Drift DETECTED blocks the
                    // run; drift left UNVERIFIABLE must block it too, or the one-bit gate says "go" about the check that catches a
                    // schema changed since discovery.
                    // The JSON route names what would not parse and what to do about it: a parser message on its own blocks the
                    // operator with a complaint about a file they have never seen, which is a line they cannot act on.
                    string why = ex is JsonException
                        ? "The schemas could not be verified because the saved catalog metadata from discovery could not be read; "
                          + "re-run discovery. " + Describe(ex)
                        : "The schemas could not be verified against the discovered catalogs: " + Describe(ex);
                    checks.Add(NotRun("schema_drift", why, causeIsAlreadyAnError: false));
                }
            }
            else
            {
                checks.Add(NotRun("schema_drift",
                    "Not checked: comparing the schemas against the discovered catalogs needs both connections open.",
                    causeIsAlreadyAnError: true));
            }

            if (tgt is not null)
            {
                try
                {
                    checks.AddRange(await TargetChecksAsync(tgt, approved.Plan, options, ct));
                }
                catch (Exception ex) when (ex is SqlException or InvalidOperationException or TimeoutException)
                {
                    // Ruling 119: a fault that stops the whole group (the connection died mid-way) is reported the way TryOpenAsync
                    // reports its own - the fault as an error, the checks behind it as not run beside it. RunAsync never throws for a
                    // SQL fault, because an exception loses every line that already succeeded, including the one naming the problem.
                    checks.Add(Err("target_probe", "The target checks stopped part-way: " + Describe(ex)));
                    checks.Add(NotRun("target_checks",
                        "Not checked: the target checks stopped before finishing, so the target's tables, permissions, checkpoint table "
                        + "and row counts are only partly known.", causeIsAlreadyAnError: true));
                }
            }
            else
            {
                // One entry standing in for the whole group, rather than nothing: the target's tables, permissions, checkpoint table
                // and row counts are all unknown, and an absent check reads as a check that found nothing wrong.
                checks.Add(NotRun("target_checks",
                    "Not checked: the target connection could not be opened, so nothing is known about the target's tables, INSERT and "
                    + "ALTER permissions, the checkpoint table, or whether the target tables already hold rows.",
                    causeIsAlreadyAnError: true));
            }

            if (src is not null)
            {
                try
                {
                    checks.Add(await SourceEstimateAsync(src, approved.Plan, ct));
                }
                catch (Exception ex) when (ex is SqlException or FormatException or InvalidCastException or InvalidOperationException
                                              or TimeoutException)
                {
                    // Symmetric with the target group's wrap, and for the same reason. Every fault the per-task catch above can see is
                    // already handled there, so nothing reachable today lands here: this is a contract assertion (rulings 104, 112) so
                    // that a future probe added outside that loop cannot turn one fault back into a lost checklist.
                    checks.Add(Err("source_probe", "The source estimate stopped part-way: " + Describe(ex)));
                    checks.Add(NotRun("estimated_rows",
                        "Not counted: the source estimate stopped before finishing, so the number of rows this run would move is unknown.",
                        causeIsAlreadyAnError: true));
                }
            }
            else
            {
                checks.Add(NotRun("estimated_rows",
                    "Not counted: the source connection could not be opened, so the number of rows this run would move is unknown.",
                    causeIsAlreadyAnError: true));
            }

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
        var probeErrors = new List<string>();        // "app.X: <server message>" - what the target_probe check reports
        var permFailed = new List<string>();         // tables whose permission probe never answered
        var permFailedIdentity = new List<string>();
        var countFailed = new List<string>();        // tables whose row count never answered
        foreach (var (target, identity) in targets)
        {
            int exists, insert, alter, delete;
            // Ruling 119. An INSERT-without-SELECT loader account passes HAS_PERMS_BY_NAME('INSERT') and then fails COUNT_BIG - the
            // ordinary least-privilege case this checklist exists for. Uncaught, one denied table costs the operator every line that
            // already succeeded, including the sentence naming what to fix. Each table is probed on its own and reports on its own.
            try
            {
                await using (var cmd = new SqlCommand(
                    "SELECT CASE WHEN OBJECT_ID(@t, N'U') IS NULL THEN 0 ELSE 1 END, ISNULL(HAS_PERMS_BY_NAME(@t, N'OBJECT', N'INSERT'), 0), " +
                    "ISNULL(HAS_PERMS_BY_NAME(@t, N'OBJECT', N'ALTER'), 0), ISNULL(HAS_PERMS_BY_NAME(@t, N'OBJECT', N'DELETE'), 0)", tgt))
                {
                    cmd.Parameters.Add(new SqlParameter("@t", SqlDbType.NVarChar, 600) { Value = SqlQuote.TableKey(target) });
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    await r.ReadAsync(ct);
                    (exists, insert, alter, delete) = (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3));
                }
            }
            catch (Exception ex) when (ex is SqlException or TimeoutException)
            {
                probeErrors.Add($"{target}: {Describe(ex)}");
                permFailed.Add(target);
                if (identity) permFailedIdentity.Add(target);
                continue;
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
            try
            {
                long rows = await TargetOps.CountTargetAsync(tgt, target, ct);
                if (rows > 0) nonEmpty.Add($"{target} ({rows.ToString("N0", CultureInfo.InvariantCulture)})");
            }
            catch (Exception ex) when (ex is SqlException or TimeoutException)
            {
                // The permission verdicts above are real and are kept; only "is it empty?" is unanswered for this table.
                probeErrors.Add($"{target}: {Describe(ex)}");
                countFailed.Add(target);
            }
        }

        string gone = Clause("they do not exist", missing);
        string skipped = gone + Clause("probing them failed", permFailed);
        string skippedIdentity = Clause("they do not exist", missingIdentity) + Clause("probing them failed", permFailedIdentity);
        string skippedRows = gone + Clause("probing them failed", permFailed.Concat(countFailed));
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

        try
        {
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
        }
        catch (Exception ex) when (ex is SqlException or TimeoutException)
        {
            probeErrors.Add($"{ControlTable.Name}: {Describe(ex)}");
            checks.Add(NotRun("control_table",
                $"Not checked: whether {ControlTable.Name} exists or can be created could not be established.",
                causeIsAlreadyAnError: true));
        }

        checks.Add(nonEmpty.Count == 0 ? Ok("target_rows", "All target tables that could be counted are empty." + skippedRows)
            : options.TruncateTarget ? Ok("target_rows", "Will be emptied first: " + string.Join(", ", nonEmpty) + "." + skippedRows)
            : new PreflightCheck("target_rows", false, "warning",
                "Target tables already contain rows: " + string.Join(", ", nonEmpty)
                + ". Loading may hit duplicate keys; consider 'Truncate target first'." + skippedRows));

        // The faults themselves, named, as an error: the verdicts above have each said which tables they could not cover, but the
        // reason lives here, and without it the operator is told what was not checked and never why.
        if (probeErrors.Count > 0)
            checks.Add(Err("target_probe", "Some target tables could not be checked: " + string.Join("; ", probeErrors) + "."));
        return checks;
    }

    private static string Clause(string why, IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count == 0 ? "" : $" Not checked, because {why}: {string.Join(", ", list)}.";
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
        var noNumber = new List<string>();      // the query answered, with something that is not a count
        var faulted = new List<string>();       // the query could not be run at all
        foreach (var id in TransferEngine.PlanOrder(plan))
        {
            var task = plan.Tasks[id];
            try
            {
                long? n = await TargetOps.ScalarLongOrNullAsync(src, TargetOps.CountSqlOf(task), ct, timeoutSec: 120);
                if (n is null)
                {
                    noNumber.Add($"{id} (the query returned no number, so its rows are not in the total)");
                    continue;
                }
                total += n.Value;
                counted++;
                if (n.Value > largest.Rows) largest = (task.Target, n.Value);
            }
            // Ruling 124. CountSql is a plan field the generator returns verbatim, so anything a human can write can come back from it:
            // a non-number raises FormatException or InvalidCastException out of Convert.ToInt64 with no server fault at all, and a
            // source connection that died part-way through raises InvalidOperationException from the next task's command. One task's
            // fault must cost that task's line, never the checklist - which is what the operator is holding to find out what to fix.
            catch (Exception ex) when (ex is SqlException or FormatException or InvalidCastException or InvalidOperationException
                                          or TimeoutException)
            {
                faulted.Add($"{id} ({(ex is FormatException or InvalidCastException ? "its count was not a number: " : "")}{Describe(ex)})");
            }
        }
        string detail = $"{total.ToString("N0", CultureInfo.InvariantCulture)} rows across {counted} tasks" +
                        (largest.Rows >= 0 ? $" (largest: {largest.Target} {largest.Rows.ToString("N0", CultureInfo.InvariantCulture)})" : "") + ".";
        if (noNumber.Count == 0 && faulted.Count == 0) return Ok("estimated_rows", detail);
        // Ruling 120's rule applied here too: a task whose count could not be run at all is a fault nothing else in the list records,
        // so it is an error. A query that answered with no number is the documented, non-fatal case and stays a warning.
        return new PreflightCheck("estimated_rows", false, faulted.Count > 0 ? "error" : "warning",
            detail + " Could not count: " + string.Join("; ", faulted.Concat(noNumber)) + ".");
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
    internal static string Scrubbed(string detail, IReadOnlyList<string> secrets)
        => TransferFailure.NonBlank(Redactor.Scrub(detail, secrets), "(removed: the detail consisted of a secret)");

    private static PreflightCheck Ok(string name, string detail) => new(name, true, "info", detail);
    private static PreflightCheck Err(string name, string detail) => new(name, false, "error", detail);

    /// <summary>
    /// A check that did not run. Never "ok", so it cannot be read as a pass.
    /// <para>Ruling 120: it is a <b>warning</b> only when the thing that stopped it is already an error in the list - a failed
    /// connection, say, which blocks the run on its own account and which the operator is already being sent to fix. When nothing else
    /// records the fault it is an <b>error</b>, because <see cref="PreflightResult.Passed"/> is one bit: a check that was going to
    /// catch a changed schema and never ran must not leave that bit saying "go".</para>
    /// </summary>
    private static PreflightCheck NotRun(string name, string detail, bool causeIsAlreadyAnError) =>
        new(name, false, causeIsAlreadyAnError ? "warning" : "error", detail);
}
