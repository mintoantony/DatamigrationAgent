using System.Security.Cryptography;
using System.Text;
using Dbm.Web.Endpoints;
using Microsoft.AspNetCore.Http;

namespace Dbm.Web;

/// <summary>403 for foreign Host headers (DNS rebinding); 401 for /api/* without the project token.</summary>
public static class TokenGuard
{
    public const string Header = "X-Dbm-Token";

    public static async Task InvokeAsync(HttpContext ctx, RequestDelegate next, ServerInfo info)
    {
        if (!HostAllowed(ctx.Request.Host.Value, info.Port))
        {
            await ApiResults.WriteAsync(ctx, StatusCodes.Status403Forbidden, new { error = "forbidden_host", message = "Use http://127.0.0.1:<port>/ or http://localhost:<port>/." });
            return;
        }
        if (ctx.Request.Path.StartsWithSegments("/api"))
        {
            var header = ctx.Request.Headers[Header].ToString();
            var query = HttpMethods.IsGet(ctx.Request.Method) ? ctx.Request.Query["t"].ToString() : "";
            if (!TokenMatches(header, query, info.Token))
            {
                await ApiResults.WriteAsync(ctx, StatusCodes.Status401Unauthorized, new { error = "unauthorized", message = "Missing or wrong project token." });
                return;
            }
        }
        await next(ctx);
    }

    public static bool HostAllowed(string? hostHeader, int port)
    {
        if (string.IsNullOrEmpty(hostHeader)) return false;
        var host = new HostString(hostHeader);
        return host.Port == port
               && (host.Host.Equals("127.0.0.1", StringComparison.Ordinal) || host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Header token, or the ?t= query value (the caller passes "" for non-GET requests).</summary>
    public static bool TokenMatches(string? header, string? query, string expected)
    {
        var candidate = !string.IsNullOrEmpty(header) ? header : query;
        if (string.IsNullOrEmpty(candidate)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(expected));
    }
}
