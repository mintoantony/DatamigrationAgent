using Dbm.Core.Jobs;
using Dbm.Core.State;

namespace Dbm.Core.SqlGen;

/// <summary>Job "sqlgen": approved mapping + catalogs (+ latest Sql artifact as carry-over) → validated plan draft.</summary>
public sealed class SqlGenJob : IJobHandler
{
    public string Kind => "sqlgen";

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var s = ctx.Services;
        var mapping = SqlPlanSource.ApprovedMapping(s)
            ?? throw new InvalidOperationException("The mapping phase has no approved version.");
        var src = s.Catalog.Get(Side.Src) ?? throw new InvalidOperationException("The source catalog is missing; run discovery first.");
        var tgt = s.Catalog.Get(Side.Tgt) ?? throw new InvalidOperationException("The target catalog is missing; run discovery first.");
        var latest = s.Artifacts.Latest(PhaseName.Sql);
        var carryOver = latest is null ? null : Json.Deserialize<SqlPlanPayload>(latest.PayloadJson);

        var plan = SqlGenerator.Generate(mapping, src, tgt, carryOver);
        ctx.Log($"sqlgen: {plan.Tasks.Count} task(s) generated{(carryOver is null ? "" : $", carry-over from v{latest!.Version}")}");

        var validated = false;
        if (SqlPlanSource.SkippedWarning(s) is { } skipped) plan.Warnings.Add(skipped);   // stored: the plan is unvalidated
        else
        {
            validated = true;
            var report = await SqlPlanSource.ValidateLiveAsync(s, plan, null, ct);
            SqlValidator.Apply(plan, report);
            ctx.Log($"sqlgen: validation {(report.Ok ? "ok" : "found errors")}");
        }
        // Ruling 57: this draft is stored without passing SqlModule.Validate, and carried-over or mapping-derived SQL can hold a lone CR.
        SqlValidator.RecordBareCarriageReturns(plan);
        // Ruling 204: positive evidence that live validation ran over this draft (never carried over: Generate builds a new plan).
        if (validated) plan.Validation = new SqlValidation(Clock.Now(), plan.ErrorCount() == 0);

        return new JobResult(Json.ToNode(plan), Summary(plan));
    }

    /// <summary>"6 tasks, 0 errors, 9 warnings".</summary>
    public static string Summary(SqlPlanPayload plan) =>
        $"{plan.Tasks.Count} tasks, {plan.ErrorCount()} errors, {plan.WarningCount()} warnings";
}
