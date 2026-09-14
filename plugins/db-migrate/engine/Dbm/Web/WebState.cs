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
    // T5.5 adds: public Dbm.Core.Transfer.TransferService? Transfer { get; set; }
}
