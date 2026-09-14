using Dbm.Core;

namespace Dbm.Web;

/// <summary>Online = at least one open /api/agent/await, or the CLI touched project.agent_seen_at within 120 s.</summary>
public sealed class AgentPresence(DbmServices services, Broadcaster? broadcaster = null)
{
    public static readonly TimeSpan SeenWindow = TimeSpan.FromSeconds(120);
    private int _open;

    public bool Online
    {
        get
        {
            if (Volatile.Read(ref _open) > 0) return true;
            try
            {
                return services.Project.Get().AgentSeenAt is { } seen && Clock.Now() - seen < SeenWindow;
            }
            catch (InvalidOperationException)
            {
                return false;   // project not initialised
            }
        }
    }

    public IDisposable Enter()
    {
        if (Interlocked.Increment(ref _open) == 1) broadcaster?.Publish("agent_presence", new { online = true }, persist: false);
        return new Exit(this);
    }

    private void Leave()
    {
        if (Interlocked.Decrement(ref _open) == 0) broadcaster?.Publish("agent_presence", new { online = Online }, persist: false);
    }

    private sealed class Exit(AgentPresence owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) owner.Leave();
        }
    }
}
