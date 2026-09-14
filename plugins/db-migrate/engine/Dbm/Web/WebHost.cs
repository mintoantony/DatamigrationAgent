using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Dbm.Core;
using Dbm.Core.Jobs;
using Dbm.Core.Workflow;
using Dbm.Web.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dbm.Web;

public static class WebHost
{
    /// <summary>
    /// Serves wwwroot + API on 127.0.0.1:<paramref name="port"/> (0 = pick a free port) until <paramref name="ct"/> fires or
    /// POST /api/shutdown. Writes server.json when started, deletes it when stopped. Returns immediately if another live
    /// server already owns the workspace.
    /// </summary>
    /// <param name="servicesFactory">Test hook (fake modules/handlers). Default: DbmServices.Open(ws).</param>
    /// <param name="onStarted">Called once the server listens (ServeCommand prints the URL).</param>
    public static async Task RunAsync(Workspace ws, int port, CancellationToken ct,
        Func<Workspace, DbmServices>? servicesFactory = null, Action<ServerInfo>? onStarted = null)
    {
        var existing = ServerControl.ReadInfo(ws);
        if (existing is not null && existing.Pid != Environment.ProcessId && await ServerControl.IsAliveAsync(existing, ct)) return;

        Directory.CreateDirectory(ws.Dir);
        var log = new FileLog(ws.ServerLogPath);
        var services = (servicesFactory ?? (w => DbmServices.Open(w)))(ws);
        try
        {
            var info = new ServerInfo(port == 0 ? FreePort() : port, Environment.ProcessId, NewToken(), Clock.Now());
            var broadcaster = new Broadcaster(services.Events);
            services.Sink = broadcaster;
            var state = new WebState
            {
                Services = services,
                Broadcaster = broadcaster,
                Presence = new AgentPresence(services, broadcaster),
                Info = info,
                Log = log,
            };

            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ContentRootPath = AppContext.BaseDirectory,
                WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            });
            builder.Logging.ClearProviders();
            builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, info.Port));

            await using var app = builder.Build();
            app.Use((ctx, next) => ErrorBoundaryAsync(ctx, next, log));
            app.Use((ctx, next) => TokenGuard.InvokeAsync(ctx, next, info));
            app.UseDefaultFiles();
            app.UseStaticFiles(new StaticFileOptions
            {
                OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-store",
            });
            EndpointRegistry.MapAll(app, state);

            app.Lifetime.ApplicationStarted.Register(() =>
            {
                WriteInfo(ws, info);
                log.Write("info", $"server started on {info.BaseUrl} (pid {info.Pid})");
                onStarted?.Invoke(info);
            });

            using var background = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var jobs = Task.Run(() => new JobRunner(services).RunLoopAsync(background.Token));
            var pump = Task.Run(() => new EventPump(services.Events, broadcaster, log).RunAsync(background.Token));
            try
            {
                await app.StartAsync(ct);
                await app.WaitForShutdownAsync(ct);
            }
            finally
            {
                background.Cancel();
                await Quietly(jobs);
                await Quietly(pump);
                DeleteInfo(ws, info);
                log.Write("info", "server stopped");
            }
        }
        finally
        {
            services.Dispose();
        }
    }

    private static async Task ErrorBoundaryAsync(HttpContext ctx, RequestDelegate next, FileLog log)
    {
        try
        {
            await next(ctx);
        }
        catch (ApiException ex) when (!ctx.Response.HasStarted)
        {
            await ApiResults.WriteAsync(ctx, ex.Status, new { error = ex.Code, message = ex.Message });
        }
        catch (WorkflowException ex) when (!ctx.Response.HasStarted)
        {
            await ApiResults.WriteAsync(ctx, StatusCodes.Status409Conflict, new { error = "conflict", message = ex.Message, details = ex.Details });
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            // client disconnected
        }
        catch (Exception ex) when (!ctx.Response.HasStarted)
        {
            log.Write("error", $"{ctx.Request.Method} {ctx.Request.Path}: {ex}");
            await ApiResults.WriteAsync(ctx, StatusCodes.Status500InternalServerError, new { error = "internal", message = ex.Message });
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static void WriteInfo(Workspace ws, ServerInfo info)
    {
        var tmp = ws.ServerJsonPath + ".tmp";
        File.WriteAllText(tmp, Json.Serialize(info));
        File.Move(tmp, ws.ServerJsonPath, overwrite: true);
    }

    private static void DeleteInfo(Workspace ws, ServerInfo info)
    {
        var current = ServerControl.ReadInfo(ws);
        if (current is not null && current.Pid == info.Pid && current.Token == info.Token)
        {
            try
            {
                File.Delete(ws.ServerJsonPath);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    private static async Task Quietly(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
            // background loops end with cancellation
        }
    }
}
