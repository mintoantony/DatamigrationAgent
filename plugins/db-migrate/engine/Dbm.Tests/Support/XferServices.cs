using Dbm.Core;

namespace Dbm.Tests.Support;

public sealed class RecordingSink : IEventSink
{
    private readonly List<(string Type, object? Payload, bool Persist)> _events = new();

    public void Publish(string type, object? payload = null, bool persist = true)
    {
        lock (_events) _events.Add((type, payload, persist));
    }

    public IReadOnlyList<(string Type, object? Payload, bool Persist)> Events
    {
        get { lock (_events) return _events.ToList(); }
    }

    public int Count(string type) => Events.Count(e => e.Type == type);
}

/// <summary>TestWorkspace.OpenServices (real registries, project "test") whose event sink records every event.</summary>
public sealed class XferServices : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public XferServices()
    {
        Services = _workspace.OpenServices();
        Services.Sink = Sink;
    }

    public string Root => _workspace.Root;
    public RecordingSink Sink { get; } = new();
    public DbmServices Services { get; }

    public void Dispose() => _workspace.Dispose();
}
