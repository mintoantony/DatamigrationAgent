using Dbm.Core;
using Dbm.Core.Catalog;
using Dbm.Core.Sql;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Dbm.Core.Transfer;
using Dbm.Tests.Support;
using Xunit;

namespace Dbm.Tests.Integration.Transfer;

/// <summary>
/// Ruling 184 (final review I-2). The C15 plan cuts the Customers/Addresses FK cycle with a global PreSql
/// <c>ALTER TABLE [app].[Customers] NOCHECK CONSTRAINT [FK_Customers_PrimaryAddress]</c>. Before 184 only a completed run ran the
/// matching PostSql: a cancel left the production target with that FK disabled and untrusted, and nothing said so. The reviewer's
/// reproduction was "start, cancel, <c>is_disabled, is_not_trusted</c> = 1 1".
/// </summary>
[Trait("Category", "Integration")]
public sealed class GlobalSqlOnStopTests(SamplePlanFixture fx) : IClassFixture<SamplePlanFixture>
{
    private const string Fk = "FK_Customers_PrimaryAddress";

    private sealed class Rig(TempProject project, TempDatabase tgt) : IAsyncDisposable
    {
        public DbmServices S => project.Services;
        public TempDatabase Tgt { get; } = tgt;
        public TransferService Service { get; } = new(project.Services);
        public async ValueTask DisposeAsync()
        {
            try { await Service.Current; } catch (Exception) { }
            project.Dispose();
            await Tgt.DisposeAsync();
        }
    }

    private async Task<Rig> RigAsync()
    {
        var tgt = await SamplePlanFixture.NewTargetAsync();
        var project = TempProject.Create("stop");
        foreach (var (side, cs) in new[] { (Side.Src, fx.Src.ConnectionString), (Side.Tgt, tgt.ConnectionString) })
        {
            var meta = await SqlConnect.ProbeAsync(cs, default);
            project.Services.Connections.Save(side, cs, meta);
            var snapshot = await SampleExtract.CatalogAsync(cs, profile: false);
            project.Services.Catalog.Save(side, snapshot, Fingerprint.Compute(snapshot));
        }
        var s = project.Services;
        foreach (var p in new[] { PhaseName.Setup, PhaseName.Discovery, PhaseName.Analysis, PhaseName.Mapping }) s.Phases.SetApproved(p, 1, null);
        var plan = fx.Plan();
        Assert.Contains(plan.PreSql, sql => sql.Contains(Fk, StringComparison.Ordinal));
        s.Artifacts.Add(PhaseName.Sql, 1, Json.Serialize(plan), "script", "plan");
        s.Phases.SetCurrentVersion(PhaseName.Sql, 1);
        s.Phases.SetApproved(PhaseName.Sql, 1, null);
        s.Phases.SetStatus(PhaseName.Ready, PhaseStatus.AwaitingReview);
        return new Rig(project, tgt);
    }

    private static Task<int> DisabledAsync(TempDatabase db) =>
        db.ScalarAsync<int>($"SELECT CAST(is_disabled AS int) FROM sys.foreign_keys WHERE name = '{Fk}'");

    private static Task<int> UntrustedAsync(TempDatabase db) =>
        db.ScalarAsync<int>($"SELECT CAST(is_not_trusted AS int) FROM sys.foreign_keys WHERE name = '{Fk}'");

    private static string TaskOf(SqlPlanPayload plan, string target) =>
        plan.Tasks.Single(kv => string.Equals(kv.Value.Target, target, StringComparison.OrdinalIgnoreCase)).Key;

    /// <summary>The reviewer's reproduction: start, cancel while running. The FK is enabled and trusted again, and the run says so.</summary>
    [Fact]
    public async Task A_cancel_while_running_restores_the_cut_foreign_key_and_says_so()
    {
        await using var rig = await RigAsync();
        string orders = TaskOf(fx.Plan(), "app.Orders");   // Customers and Addresses are both loaded by the time Orders starts
        int cancels = 0;
        rig.Service.ChunkCommitted += c =>
        {
            if (c.TaskId == orders && Interlocked.Increment(ref cancels) == 1) rig.Service.CancelAsync(default).GetAwaiter().GetResult();
        };
        long runId = await rig.Service.StartAsync(new TransferOptions { ChunkSize = 500, Parallelism = 1, ErrorMode = "skip" }, rig.Tgt.Name, default);
        await rig.Service.Current;

        Assert.Equal(RunStatus.Cancelled, rig.S.Transfers.GetRun(runId)!.Status);
        int disabled = await DisabledAsync(rig.Tgt), untrusted = await UntrustedAsync(rig.Tgt);
        Assert.True(disabled == 0 && untrusted == 0,
            $"after the cancel {Fk} reads is_disabled={disabled}, is_not_trusted={untrusted}: the plan's PreSql was left in force in the target");
        var notes = rig.Service.View().Run!.Notes;
        Assert.True(notes.Any(n => n.StartsWith(GlobalSql.RestorePrefix + " restored", StringComparison.Ordinal) && n.Contains(Fk, StringComparison.Ordinal)),
            "the run does not say what the cancel restored: " + string.Join(" | ", notes));
        Assert.DoesNotContain(notes, n => n.StartsWith(GlobalSql.InForcePrefix, StringComparison.Ordinal));
    }

    /// <summary>Cancelled while Customers is half loaded and Addresses not at all: the rows already in the target violate the FK, so
    /// WITH CHECK fails. The server's text is in the note, and the FK guards new writes (enabled, untrusted) rather than none.</summary>
    [Fact]
    public async Task A_constraint_the_loaded_rows_violate_is_reported_with_the_servers_text_and_still_guards_new_rows()
    {
        await using var rig = await RigAsync();
        string customers = TaskOf(fx.Plan(), "app.Customers");
        int cancels = 0;
        rig.Service.ChunkCommitted += c =>
        {
            if (c.TaskId == customers && Interlocked.Increment(ref cancels) == 1) rig.Service.CancelAsync(default).GetAwaiter().GetResult();
        };
        long runId = await rig.Service.StartAsync(new TransferOptions { ChunkSize = 100, Parallelism = 1, ErrorMode = "skip" }, rig.Tgt.Name, default);
        await rig.Service.Current;

        Assert.Equal(RunStatus.Cancelled, rig.S.Transfers.GetRun(runId)!.Status);
        Assert.Equal(0, await rig.Tgt.CountAsync("app.Addresses"));
        var notes = rig.Service.View().Run!.Notes;
        var note = notes.SingleOrDefault(n => n.Contains(Fk, StringComparison.Ordinal));
        Assert.True(note is not null && note.Contains("NOT restored WITH CHECK", StringComparison.Ordinal)
                                     && note.Contains("The server said:", StringComparison.Ordinal),
            "the cancel's outcome for a constraint it could not restore: " + (note ?? string.Join(" | ", notes)));
        Assert.Equal(0, await DisabledAsync(rig.Tgt));
        Assert.Equal(1, await UntrustedAsync(rig.Tgt));
    }

    /// <summary>A failed run keeps PreSql in force (Resume expects it) and says so, naming the constraint; the cancel that abandons it
    /// then restores it and replaces the "still in force" note with what it did.</summary>
    [Fact]
    public async Task A_failed_run_names_what_is_still_in_force_and_its_cancel_restores_it()
    {
        await using var rig = await RigAsync();
        long runId = await rig.Service.StartAsync(new TransferOptions { ChunkSize = 500, Parallelism = 1, ErrorMode = "stop" }, rig.Tgt.Name, default);
        await rig.Service.Current;

        var view = rig.Service.View();
        Assert.Equal(RunStatus.Failed, view.Run!.Status);
        Assert.Equal(1, await DisabledAsync(rig.Tgt));                                   // in force, on purpose: Resume expects it
        Assert.True(view.Run.Notes.Any(n => n.StartsWith(GlobalSql.InForcePrefix, StringComparison.Ordinal) && n.Contains(Fk, StringComparison.Ordinal)),
            "a failed run does not say the plan's PreSql is still in force: " + string.Join(" | ", view.Run.Notes));
        Assert.Contains(view.Run.PreSqlInForce, sql => sql.Contains(Fk, StringComparison.Ordinal));

        await rig.Service.CancelAsync(default);

        int disabled = await DisabledAsync(rig.Tgt), untrusted = await UntrustedAsync(rig.Tgt);
        Assert.True(disabled == 0 && untrusted == 0,
            $"cancelling the failed run left {Fk} is_disabled={disabled}, is_not_trusted={untrusted}: the abandoned run's PreSql stays in force");
        var after = rig.Service.View().Run!;
        Assert.Equal(RunStatus.Cancelled, after.Status);
        Assert.Empty(after.PreSqlInForce);
        Assert.DoesNotContain(after.Notes, n => n.StartsWith(GlobalSql.InForcePrefix, StringComparison.Ordinal));
        Assert.Contains(after.Notes, n => n.StartsWith(GlobalSql.RestorePrefix + " restored", StringComparison.Ordinal) && n.Contains(Fk, StringComparison.Ordinal));
        Assert.Equal(runId, after.Id);
    }

    /// <summary>PreSql runs at every segment start and PostSql can run after a completed segment's own, so both must survive a second
    /// run - and PreSql must re-apply after PostSql (a new run after a cancel or a reopen).</summary>
    [Fact]
    public async Task The_generated_pre_and_post_sql_can_each_run_twice_and_in_either_order()
    {
        await using var tgt = await SamplePlanFixture.NewTargetAsync();
        var plan = fx.Plan();
        await using var conn = await SqlConnect.OpenAsync(tgt.ConnectionString, default);

        await TargetOps.ExecAllAsync(conn, plan.PreSql, default);
        await TargetOps.ExecAllAsync(conn, plan.PreSql, default);
        Assert.Equal(1, await DisabledAsync(tgt));
        await TargetOps.ExecAllAsync(conn, plan.PostSql, default);
        await TargetOps.ExecAllAsync(conn, plan.PostSql, default);
        Assert.Equal(0, await DisabledAsync(tgt));
        Assert.Equal(0, await UntrustedAsync(tgt));
        await TargetOps.ExecAllAsync(conn, plan.PreSql, default);
        Assert.Equal(1, await DisabledAsync(tgt));
    }
}
