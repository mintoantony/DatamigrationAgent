using Dbm.Core;
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Core;

public class DbmServicesTests
{
    [Fact]
    public void Default_open_uses_the_real_registries_and_a_persisting_sink()
    {
        using var tw = new TestWorkspace();
        tw.Ws.EnsureCreated();

        using var s = DbmServices.Open(tw.Ws);

        Assert.Equal(ModuleRegistry.Create(s).Select(m => m.Phase).Order(), s.Modules.Keys.Order());
        Assert.Equal(JobRegistry.Create(s).Select(h => h.Kind).Order(), s.JobHandlers.Keys.Order());
        Assert.IsType<DbEventSink>(s.Sink);
        Assert.NotNull(s.Workflow);
        Assert.True(File.Exists(tw.Ws.StateDbPath));
    }

    [Fact]
    public void Fakes_are_registered_by_phase_and_kind()
    {
        using var tw = new TestWorkspace();

        using var s = FakeServices.Open(tw.Ws);

        Assert.Equal(new[] { PhaseName.Analysis, PhaseName.Mapping, PhaseName.Sql }, s.Modules.Keys.Order());
        Assert.Equal(new[] { "analyze", "automap", "discover", "sqlgen" }, s.JobHandlers.Keys.Order());
    }

    [Fact]
    public void TestWorkspace_OpenServices_initialises_the_project_once()
    {
        using var tw = new TestWorkspace();

        var plain = tw.OpenServices();
        plain.Project.SetPaused(true);
        var faked = tw.OpenServices(_ => new FakeModules().All, _ => FakeJobHandler.Standard());

        Assert.Equal("test", faked.Project.Get().Name);
        Assert.True(faked.Project.Get().Paused);
        Assert.Equal(ModuleRegistry.Create(plain).Count(), plain.Modules.Count);
        Assert.Equal(3, faked.Modules.Count);
        Assert.Equal(4, faked.JobHandlers.Count);
    }

    [Fact]
    public void UiUrl_comes_from_server_json_or_is_empty()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);

        Assert.Equal("", s.UiUrl());
        File.WriteAllText(tw.Ws.ServerJsonPath, "{\"port\":5123,\"pid\":7,\"token\":\"abc\",\"startedAt\":\"2026-09-11T00:00:00+00:00\"}");
        Assert.Equal("http://127.0.0.1:5123/?t=abc", s.UiUrl());
        File.WriteAllText(tw.Ws.ServerJsonPath, "{ half written");
        Assert.Equal("", s.UiUrl());
    }

    [Fact]
    public void DbEventSink_persists_only_persistent_events()
    {
        using var tw = new TestWorkspace();
        using var s = FakeServices.Open(tw.Ws);

        s.Sink.Publish("log", new { message = "kept" });
        s.Sink.Publish("transfer_progress", new { rows = 1 }, persist: false);

        Assert.Equal(new[] { "log" }, s.Events.Since(0).Select(e => e.Type));
    }
}
