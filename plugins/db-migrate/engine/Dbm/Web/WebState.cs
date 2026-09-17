using Dbm.Core;

namespace Dbm.Web;

/// <summary>One per server process, passed to every endpoint group.</summary>
public sealed class WebState
{
    public required DbmServices Services { get; init; }
    public required Broadcaster Broadcaster { get; init; }
    public required AgentPresence Presence { get; init; }
    public required ServerInfo Info { get; init; }
    public FileLog? Log { get; init; }
    /// <summary>The one transfer service of this server process (T5.5). Set by <see cref="WebHost"/> before the endpoints are
    /// mapped and never replaced: it owns the single background run, so a second instance would be a second runner.</summary>
    public Dbm.Core.Transfer.TransferService? Transfer { get; set; }   // T5.5
}
