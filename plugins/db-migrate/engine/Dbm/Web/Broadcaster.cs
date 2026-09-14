using System.Threading.Channels;
using Dbm.Core;
using Dbm.Core.State;

namespace Dbm.Web;

/// <summary>One server-sent event. Id is the event-table id (null for live-only events).</summary>
public sealed record SseMessage(long? Id, string Type, string Data);

/// <summary>
/// The server's IEventSink. Persisted events go to the event table (EventPump delivers them to SSE clients, so events
/// written by other processes — e.g. `dbm apply` — reach the browser too); persist:false events go to clients directly.
/// </summary>
public sealed class Broadcaster(EventRepo events) : IEventSink
{
    private readonly object _gate = new();
    private readonly List<Channel<SseMessage>> _clients = new();
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Publish(string type, object? payload = null, bool persist = true)
    {
        if (persist) events.Append(type, payload);
        else Deliver(new SseMessage(null, type, Json.Serialize(payload ?? new { })));
    }

    public int ClientCount
    {
        get
        {
            lock (_gate) return _clients.Count;
        }
    }

    public void Deliver(SseMessage message)
    {
        lock (_gate)
        {
            foreach (var client in _clients) client.Writer.TryWrite(message);
        }
    }

    public Subscription Subscribe()
    {
        var channel = Channel.CreateBounded<SseMessage>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_gate) _clients.Add(channel);
        return new Subscription(this, channel);
    }

    /// <summary>Completes on the next <see cref="Signal"/> (EventPump signals after delivering new rows).</summary>
    public Task NextSignal()
    {
        lock (_gate) return _signal.Task;
    }

    public void Signal()
    {
        TaskCompletionSource old;
        lock (_gate)
        {
            old = _signal;
            _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        old.TrySetResult();
    }

    private void Remove(Channel<SseMessage> channel)
    {
        lock (_gate) _clients.Remove(channel);
        channel.Writer.TryComplete();
    }

    public sealed class Subscription(Broadcaster owner, Channel<SseMessage> channel) : IDisposable
    {
        public ChannelReader<SseMessage> Reader => channel.Reader;
        public void Dispose() => owner.Remove(channel);
    }
}
