using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Web;

namespace Dbm.Tests.Support;

/// <summary>Runs WebHost in-process on a free loopback port; HttpClient pre-configured with the token.</summary>
public sealed class WebTestServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts;

    private WebTestServer(Workspace ws, ServerInfo info, DbmServices services, CancellationTokenSource cts, Task run)
    {
        Ws = ws;
        Info = info;
        Services = services;
        _cts = cts;
        Completion = run;
        Client = new HttpClient { BaseAddress = new Uri(info.BaseUrl), Timeout = TimeSpan.FromSeconds(60) };
        Client.DefaultRequestHeaders.Add(TokenGuard.Header, info.Token);
    }

    public Workspace Ws { get; }
    public ServerInfo Info { get; }

    /// <summary>The server's own services instance (events published here reach SSE clients).</summary>
    public DbmServices Services { get; }

    public HttpClient Client { get; }
    public Task Completion { get; }

    public static async Task<WebTestServer> StartAsync(Workspace ws, Func<Workspace, DbmServices>? factory = null)
    {
        factory ??= FakeServices.Factory();
        ws.EnsureCreated();
        var cts = new CancellationTokenSource();
        DbmServices? services = null;
        var started = new TaskCompletionSource<ServerInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = Task.Run(() => WebHost.RunAsync(ws, 0, cts.Token, w => services = factory(w), info => started.TrySetResult(info)));
        var first = await Task.WhenAny(started.Task, run, Task.Delay(TimeSpan.FromSeconds(30)));
        if (first != started.Task)
        {
            cts.Cancel();
            if (run.IsFaulted) await run;
            throw new TimeoutException("test server did not start");
        }
        return new WebTestServer(ws, await started.Task, services!, cts, run);
    }

    public HttpClient Anonymous() => new() { BaseAddress = new Uri(Info.BaseUrl), Timeout = TimeSpan.FromSeconds(30) };

    public async Task<(HttpStatusCode Status, JsonNode? Body)> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(Json.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text));
    }

    public async Task<JsonNode> GetJsonAsync(string path) => JsonNode.Parse(await Client.GetStringAsync(path))!;

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await Completion.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (Exception)
        {
            // already stopped (e.g. /api/shutdown) or failed: nothing to clean up
        }
        Client.Dispose();
        _cts.Dispose();
    }
}
