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

/// <summary>What <see cref="WebHost.RunAsync"/> did — only known once it returns, since a started server blocks in
/// this call for its entire lifetime. Replaces the "started" flag callers used to infer from whether their
/// <c>onStarted</c> callback fired.</summary>
public enum WebHostStartResult
{
    /// <summary>Ran the server to completion (until shutdown).</summary>
    Started,

    /// <summary>Returned immediately: another process already owns this workspace (live server, or holds server.lock).</summary>
    AlreadyRunning,
}

public static class WebHost
{
    private const int LockRetryAttempts = 5;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Serves wwwroot + API on 127.0.0.1:<paramref name="port"/> (0 = pick a free port) until <paramref name="ct"/> fires or
    /// POST /api/shutdown. Writes server.json when started, deletes it when stopped. Returns
    /// <see cref="WebHostStartResult.AlreadyRunning"/> immediately if another live server already owns the workspace,
    /// or if another process already holds the workspace's exclusive server.lock (the OS releases the lock if that
    /// process crashes, so a stale lock never blocks a later start).
    /// </summary>
    /// <param name="servicesFactory">Test hook (fake modules/handlers). Default: DbmServices.Open(ws).</param>
    /// <param name="onStarted">Called once the server listens (ServeCommand prints the URL).</param>
    public static async Task<WebHostStartResult> RunAsync(Workspace ws, int port, CancellationToken ct,
        Func<Workspace, DbmServices>? servicesFactory = null, Action<ServerInfo>? onStarted = null)
    {
        var existing = ServerControl.ReadInfo(ws);
        if (existing is not null && existing.Pid != Environment.ProcessId && await ServerControl.IsAliveAsync(existing, ct))
            return WebHostStartResult.AlreadyRunning;

        Directory.CreateDirectory(ws.Dir);
        var log = new FileLog(ws.ServerLogPath);
        var serverLock = await TryAcquireLockAsync(ws, log, ct);
        if (serverLock is null) return WebHostStartResult.AlreadyRunning;
        try
        {
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
                // Spec section 9 crash recovery, and ruling 103's ordering: runs and tasks a dead process left "running" become
                // "paused" ONCE, before any RunAsync can be reached through an endpoint. A run left "running" is exactly what a
                // crash leaves behind, so without this the UI cannot tell a resumable run from a live one.
                var transfer = new Dbm.Core.Transfer.TransferService(services);
                transfer.RecoverInterrupted();
                state.Transfer = transfer;
                EndpointRegistry.MapAll(app, state);

                app.Lifetime.ApplicationStarted.Register(() =>
                {
                    WriteInfo(ws, info);
                    log.Write("info", $"server started on {info.BaseUrl} (pid {info.Pid})");
                    onStarted?.Invoke(info);
                });

                using var background = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var jobs = Task.CompletedTask;
                var pump = Task.CompletedTask;
                try
                {
                    // Bind first: a process that fails to bind (lost the free-port race, port in use, …) must never
                    // touch the job table, so the loops below only start once Kestrel is actually accepting requests.
                    await app.StartAsync(ct);
                    jobs = Task.Run(() => new JobRunner(services).RunLoopAsync(background.Token));
                    pump = Task.Run(() => new EventPump(services.Events, broadcaster, log).RunAsync(background.Token));
                    await app.WaitForShutdownAsync(ct);
                }
                finally
                {
                    background.Cancel();
                    // Hard-stops a running transfer before services (and the state db it writes to) are disposed. This is crash
                    // semantics on purpose: the run stays "running" and the next start recovers it as "paused", which is resumable
                    // from its checkpoints. Draining it instead would hold shutdown for as long as the table takes.
                    await Quietly(transfer.StopAsync());
                    // Removed before draining: once Kestrel has stopped, server.json must not keep advertising a
                    // server that can no longer answer requests, however long the job/pump drain takes.
                    DeleteInfo(ws, info);
                    await Quietly(jobs);
                    await Quietly(pump);
                    log.Write("info", "server stopped");
                }
            }
            finally
            {
                services.Dispose();
            }
            return WebHostStartResult.Started;
        }
        finally
        {
            serverLock.Dispose();
        }
    }

    /// <summary>
    /// Opens server.lock exclusively, retrying briefly first — a momentary open by antivirus/OneDrive on a
    /// freshly created file should not be mistaken for another server owning the workspace. Only a genuine sharing
    /// violation is treated as "held"; any other <see cref="IOException"/> (missing directory, disk error, …)
    /// surfaces instead of being swallowed as "already running". Shared with <c>dbm run-jobs</c> (<c>AgentCommands</c>),
    /// which takes the same lock so it can never race a concurrently starting server over the job table.
    /// </summary>
    internal static async Task<FileStream?> TryAcquireLockAsync(Workspace ws, FileLog? log, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= LockRetryAttempts; attempt++)
        {
            try
            {
                return new FileStream(ws.ServerLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (IsLockHeldByAnotherProcess(ex))
            {
                if (attempt == LockRetryAttempts)
                {
                    log?.Write("info", "server.lock is held by another process; not starting (workspace already served).");
                    return null;
                }
                await Task.Delay(LockRetryDelay, ct);
            }
        }
        return null;
    }

    /// <summary>True only for a genuine lock/sharing conflict (Win32 ERROR_SHARING_VIOLATION/ERROR_LOCK_VIOLATION,
    /// or EAGAIN/EWOULDBLOCK on POSIX) — never for an unrelated <see cref="IOException"/> such as a missing
    /// directory, which must surface rather than be read as "another server is running".</summary>
    internal static bool IsLockHeldByAnotherProcess(IOException ex)
    {
        const int ErrorSharingViolation = 32;
        const int ErrorLockViolation = 33;
        var code = ex.HResult & 0xFFFF;
        if (OperatingSystem.IsWindows()) return code == ErrorSharingViolation || code == ErrorLockViolation;
        return code == 11 /* EAGAIN (Linux) */ || code == 35 /* EWOULDBLOCK (macOS/BSD) */;
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
