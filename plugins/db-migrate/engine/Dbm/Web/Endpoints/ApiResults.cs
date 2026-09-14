using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Dbm.Web.Endpoints;

/// <summary>Thrown by endpoint code to answer {"error":Code,"message":Message} with Status.</summary>
public sealed class ApiException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>JSON helpers that always use Dbm.Core.Json.Options (camelCase, snake enums).</summary>
public static class ApiResults
{
    public static IResult Json(object? value, int status = StatusCodes.Status200OK) =>
        Results.Text(Dbm.Core.Json.Serialize(value), "application/json", Encoding.UTF8, status);

    public static IResult Ok() => Json(new { ok = true });

    public static IResult Error(int status, string code, string message, IReadOnlyList<string>? details = null) =>
        Json(new { error = code, message, details }, status);

    public static async Task<T> ReadAsync<T>(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(request.HttpContext.RequestAborted);
        if (string.IsNullOrWhiteSpace(text)) throw new ApiException(StatusCodes.Status400BadRequest, "bad_request", "A JSON body is required.");
        try
        {
            return Dbm.Core.Json.Deserialize<T>(text);
        }
        catch (JsonException ex)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "bad_request", $"Invalid JSON body: {ex.Message}");
        }
    }

    public static async Task<string> ReadTextAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync(request.HttpContext.RequestAborted);
    }

    public static Task WriteAsync(HttpContext ctx, int status, object value)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        return ctx.Response.WriteAsync(Dbm.Core.Json.Serialize(value));
    }
}
