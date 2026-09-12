using Dbm.Core;

namespace Dbm.Cli;

/// <summary>Every command ends through one of these so stdout is always one compact JSON line (or text).</summary>
public static class Output
{
    public static int Ok(CliContext ctx, object payload) => Write(ctx, payload, 0);

    public static int Fail(CliContext ctx, string code, string message) =>
        Write(ctx, new { error = code, message }, 1);

    public static int Text(CliContext ctx, string text)
    {
        ctx.Out.WriteLine(text);
        return 0;
    }

    /// <summary>Writes <paramref name="payload"/> as compact JSON and returns <paramref name="exitCode"/>.</summary>
    public static int Write(CliContext ctx, object payload, int exitCode)
    {
        ctx.Out.WriteLine(Json.Serialize(payload));
        return exitCode;
    }
}
