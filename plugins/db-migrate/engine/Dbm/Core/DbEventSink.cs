using Dbm.Core.State;

namespace Dbm.Core;

/// <summary>Default sink outside the server: persists events; live-only (persist:false) events are dropped.</summary>
public sealed class DbEventSink(EventRepo events) : IEventSink
{
    public void Publish(string type, object? payload = null, bool persist = true)
    {
        if (persist) events.Append(type, payload);
    }
}
