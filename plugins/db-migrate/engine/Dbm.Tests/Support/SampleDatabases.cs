using Dbm.Core;
using Dbm.Core.Samples;
using Dbm.Core.Sql;
using Dbm.Core.State;
using Microsoft.Data.SqlClient;

namespace Dbm.Tests.Support;

/// <summary>A LegacyShop (source) + ShopV2 (target) pair of temporary databases on the test server.</summary>
public sealed class SamplePair : IAsyncDisposable
{
    public required TempDatabase Source { get; init; }
    public required TempDatabase Target { get; init; }
    public string SourceCs => Source.ConnectionString;
    public string TargetCs => Target.ConnectionString;

    public async ValueTask DisposeAsync()
    {
        await Source.DisposeAsync();
        await Target.DisposeAsync();
    }
}

public static class SampleDatabases
{
    /// <summary>Creates both databases from the embedded scripts; seeds LegacyShop at <paramref name="scale"/> when <paramref name="seed"/>.</summary>
    public static async Task<SamplePair> CreateAsync(int scale = 1, bool seed = true)
    {
        var source = await TempDatabase.CreateAsync("legacyshop");
        TempDatabase? target = null;
        try
        {
            target = await TempDatabase.CreateAsync("shopv2");
            await RunAsync(source.ConnectionString, SampleSql.LegacyShopSchema);
            await RunAsync(source.ConnectionString, seed ? SampleSql.Seed(scale) : SampleSql.UntrustedForeignKey);
            await RunAsync(target.ConnectionString, SampleSql.ShopV2Schema);
            return new SamplePair { Source = source, Target = target };
        }
        catch
        {
            await source.DisposeAsync();
            if (target is not null) await target.DisposeAsync();
            throw;
        }
    }

    public static async Task RunAsync(string connectionString, string script)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await SampleSql.ExecuteAsync(conn, script, CancellationToken.None);
    }
}

/// <summary>xUnit class fixture: one seeded (scale 1) sample pair shared by all tests of a class.</summary>
public sealed class SamplePairFixture : IAsyncLifetime
{
    public SamplePair Pair { get; private set; } = null!;
    public async Task InitializeAsync() => Pair = await SampleDatabases.CreateAsync();
    public async Task DisposeAsync() => await Pair.DisposeAsync();
}

/// <summary>
/// A <see cref="TestWorkspace"/> with an initialised project named <c>name</c>, opened through DbmServices with the real
/// registries (M2 modules and jobs included). Disposing it disposes the services and deletes the folder.
/// </summary>
public sealed class TempProject : IDisposable
{
    private readonly TestWorkspace _workspace;

    private TempProject(TestWorkspace workspace, DbmServices services)
    {
        _workspace = workspace;
        Services = services;
    }

    public string Root => _workspace.Root;
    public Workspace Ws => _workspace.Ws;
    public DbmServices Services { get; }

    public static TempProject Create(string name = "test")
    {
        var workspace = new TestWorkspace();
        workspace.Ws.EnsureCreated();
        var services = DbmServices.Open(workspace.Ws);
        if (!services.Project.Exists()) services.Project.Init(name);
        return new TempProject(workspace, services);
    }

    public void Dispose()
    {
        Services.Dispose();
        _workspace.Dispose();
    }
}

public static class SampleProject
{
    /// <summary>TempProject named "sample" whose src/tgt connections point at <paramref name="pair"/> (probed + saved).</summary>
    public static async Task<TempProject> CreateAsync(SamplePair pair)
    {
        var project = TempProject.Create("sample");
        project.Services.Connections.Save(Side.Src, pair.SourceCs, await SqlConnect.ProbeAsync(pair.SourceCs, CancellationToken.None));
        project.Services.Connections.Save(Side.Tgt, pair.TargetCs, await SqlConnect.ProbeAsync(pair.TargetCs, CancellationToken.None));
        return project;
    }
}
