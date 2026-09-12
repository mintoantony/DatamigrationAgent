namespace Dbm.Core;

public static class Clock
{
    /// <summary>UTC now. Tests may replace it (restore it in a finally block).</summary>
    public static Func<DateTimeOffset> Now = () => DateTimeOffset.UtcNow;

    public static string NowText() => Now().ToString("O");
}
