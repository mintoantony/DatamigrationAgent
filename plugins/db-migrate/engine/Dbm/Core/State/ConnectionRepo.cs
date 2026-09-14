using Dbm.Core.Crypto;
using Dbm.Core.Sql;

namespace Dbm.Core.State;

/// <summary>Connection strings are stored only in encrypted form.</summary>
public sealed class ConnectionRepo(StateDb db, ISecretProtector protector)
{
    public void Save(Side side, string connectionString, ServerMeta meta) =>
        db.Execute("""
            INSERT INTO connection (side, encrypted, server_meta_json, updated_at) VALUES ($Side, $Encrypted, $Meta, $Now)
            ON CONFLICT (side) DO UPDATE SET encrypted = excluded.encrypted, server_meta_json = excluded.server_meta_json,
                                             updated_at = excluded.updated_at
            """, new { Side = side, Encrypted = protector.Protect(connectionString), Meta = Json.Serialize(meta), Now = Clock.NowText() });

    public string? GetConnectionString(Side side)
    {
        var encrypted = db.Scalar<string>("SELECT encrypted FROM connection WHERE side = $Side", new { Side = side });
        return encrypted is null ? null : protector.Unprotect(encrypted);
    }

    public ServerMeta? GetMeta(Side side)
    {
        var json = db.Scalar<string>("SELECT server_meta_json FROM connection WHERE side = $Side", new { Side = side });
        return json is null ? null : Json.Deserialize<ServerMeta>(json);
    }

    public bool Has(Side side) =>
        db.Scalar<long>("SELECT COUNT(*) FROM connection WHERE side = $Side", new { Side = side }) > 0;
}
