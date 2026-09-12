namespace Dbm.Core;

/// <summary>Where engine events go. persist:false events are live-only (SSE) and never stored.</summary>
public interface IEventSink
{
    void Publish(string type, object? payload = null, bool persist = true);
}
