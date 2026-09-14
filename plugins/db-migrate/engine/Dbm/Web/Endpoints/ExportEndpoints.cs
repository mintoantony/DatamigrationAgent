using Dbm.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace Dbm.Web.Endpoints;

/// <summary>
/// GET /api/export/{what} → the file as a download; POST /api/export/{what} → {ok, file, path, download}.
/// Both save a copy under .dbmigrate/exports/. Later milestones add kinds with <see cref="Register"/> (e.g. "mapping", "sql", "report", "sqlpack").
/// </summary>
public static class ExportEndpoints
{
    public delegate Task<ExportFile> Builder(WebState state, IWebAssets assets, CancellationToken ct);

    private static readonly Dictionary<string, Builder> Builders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["analysis"] = (state, assets, _) => Task.FromResult(WebExport.Analysis(state.Services, assets)),
    };

    public static void Register(string what, Builder builder)
    {
        lock (Builders) Builders[what] = builder;
    }

    public static void Map(IEndpointRouteBuilder app, WebState state)
    {
        var webRoot = app.ServiceProvider.GetService<IWebHostEnvironment>()?.WebRootFileProvider;

        app.MapGet("/api/export/{what}", async (string what, CancellationToken ct) =>
        {
            var (file, _, error) = await BuildAsync(state, webRoot, what, ct);
            return file is null ? error! : Results.File(file.Content, file.ContentType, file.FileName);
        });

        app.MapPost("/api/export/{what}", async (string what, CancellationToken ct) =>
        {
            var (file, path, error) = await BuildAsync(state, webRoot, what, ct);
            if (file is null) return error!;
            return Results.Json(new
            {
                ok = true,
                file = file.FileName,
                path = state.Services.Ws.Relative(path!),
                download = $"/api/export/{Uri.EscapeDataString(what)}?t={Uri.EscapeDataString(state.Info.Token)}",
            }, Json.Options);
        });
    }

    private static async Task<(ExportFile? File, string? Path, IResult? Error)> BuildAsync(WebState state, IFileProvider? webRoot,
        string what, CancellationToken ct)
    {
        Builder? builder;
        lock (Builders) Builders.TryGetValue(what, out builder);
        if (builder is null)
            return (null, null, Results.Json(new { error = "not_found", message = $"Unknown export '{what}'." }, Json.Options, statusCode: 404));
        try
        {
            var file = await builder(state, WebExport.LocateAssets(webRoot), ct);
            return (file, WebExport.SaveCopy(state.Services.Ws, file), null);
        }
        catch (ExportException ex)
        {
            return (null, null, Results.Json(new { error = "export_unavailable", message = ex.Message }, Json.Options, statusCode: 404));
        }
    }
}
