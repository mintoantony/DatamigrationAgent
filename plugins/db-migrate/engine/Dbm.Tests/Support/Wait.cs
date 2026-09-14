using System.Diagnostics;

namespace Dbm.Tests.Support;

/// <summary>Polling helper for asynchronous effects (job loops, servers, SSE).</summary>
public static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("condition was not met in time");
            await Task.Delay(50);
        }
    }

    public static async Task UntilAsync(Func<Task<bool>> condition, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!await condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("condition was not met in time");
            await Task.Delay(50);
        }
    }
}
