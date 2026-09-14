using Dbm.Core.State;

namespace Dbm.Web;

/// <summary>Polls the event table (every 300 ms) and forwards new rows to SSE clients and await waiters.</summary>
public sealed class EventPump(EventRepo events, Broadcaster broadcaster, FileLog? log = null)
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);

    public async Task RunAsync(CancellationToken ct)
    {
        var lastId = events.LastId();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var rows = events.Since(lastId);
                foreach (var row in rows)
                {
                    broadcaster.Deliver(new SseMessage(row.Id, row.Type, row.PayloadJson));
                    lastId = row.Id;
                }
                if (rows.Count > 0) broadcaster.Signal();
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log?.Write("error", $"event pump: {ex.Message}");
            }
            try
            {
                await Task.Delay(PollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
