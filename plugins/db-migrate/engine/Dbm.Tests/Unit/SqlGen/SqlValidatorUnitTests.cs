using Dbm.Core.SqlGen;
using Dbm.Tests.Support;
using Xunit;

namespace Dbm.Tests.Unit.SqlGen;

public class SqlValidatorUnitTests
{
    [Fact]
    public void CheckShape_reports_mode_staging_duplicates_and_go()
    {
        var task = new TaskPlan
        {
            Target = "app.Addresses",
            Mode = "staging_merge",
            SourceQuery = "SELECT 1 AS [City]\nGO",
            Columns = [new ColumnBinding("City", "City"), new ColumnBinding("City2", "city")],
        };
        var errors = SqlValidator.CheckShape(task);
        Assert.Contains("staging_merge requires stagingDdl", errors);
        Assert.Contains("staging_merge requires mergeSql", errors);
        Assert.Contains("City: bound more than once", errors);
        Assert.Contains("sourceQuery: GO batch separators are not allowed", errors);
    }

    [Fact]
    public void CheckShape_accepts_generated_tasks_and_go_inside_comments()
    {
        var plan = SqlGenerator.Generate(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target());
        Assert.All(plan.Tasks.Values, t => Assert.Empty(SqlValidator.CheckShape(t)));
        var task = plan.Tasks["T04"];
        task.PreSql.Add("-- GO live checklist\nUPDATE STATISTICS [app].[Addresses];");
        Assert.Empty(SqlValidator.CheckShape(task));
    }

    [Fact]
    public void CheckShape_reports_parseonly_in_target_statements_offline()
    {
        var plan = SqlGenerator.Generate(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target());
        var task = plan.Tasks["T04"];
        task.PostSql.Add("SET PARSEONLY OFF; DROP TABLE [app].[Addresses];");
        task.PreSql.Add("/* parseonly */ UPDATE STATISTICS [app].[Addresses];");
        var errors = SqlValidator.CheckShape(task);
        Assert.Contains($"postSql[{task.PostSql.Count - 1}]: SET PARSEONLY is not allowed", errors);
        Assert.Contains($"preSql[{task.PreSql.Count - 1}]: SET PARSEONLY is not allowed", errors);
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void Apply_keeps_generator_warnings_and_replaces_validator_warnings()
    {
        var plan = new SqlPlanPayload
        {
            Order = ["T01"],
            Tasks = { ["T01"] = new TaskPlan { Target = "app.X", Warnings = ["no key: single-transaction load", "validate: old"] } },
        };
        SqlValidator.Apply(plan, new ValidationReport(false, new() { ["T01"] = ["e1"] }, new() { ["T01"] = ["w1"] }, ["g1"]));
        Assert.Equal(["e1"], plan.Tasks["T01"].Errors);
        Assert.Equal(["no key: single-transaction load", "validate: w1"], plan.Tasks["T01"].Warnings);
        Assert.Equal(["g1"], plan.Errors);

        SqlValidator.Apply(plan, new ValidationReport(true, new() { ["T01"] = [] }, new() { ["T01"] = [] }, []), full: false);
        Assert.Empty(plan.Tasks["T01"].Errors);
        Assert.Equal(["no key: single-transaction load"], plan.Tasks["T01"].Warnings);
        Assert.Equal(["g1"], plan.Errors);
    }

    [Fact]
    public async Task Unknown_task_id_fails_before_connecting()
    {
        var plan = SqlGenerator.Generate(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target());
        var report = await SqlValidator.ValidateAsync(plan, "Server=nowhere;Database=x", "Server=nowhere;Database=y",
            SampleCatalogs.Target(), "T99", CancellationToken.None);
        Assert.False(report.Ok);
        Assert.Equal(["unknown task T99"], report.GlobalErrors);
    }
}
