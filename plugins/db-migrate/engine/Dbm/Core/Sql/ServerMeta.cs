namespace Dbm.Core.Sql;

/// <summary>What SETUP captures about each side. Never contains credentials.</summary>
/// <param name="DataSource">Open item 49: the connection's stable data source for a LocalDB instance, <c>(localdb)\&lt;InstanceName&gt;</c>
/// from the connection string's Data Source - <see cref="Server"/> is <c>&lt;machine&gt;\LOCALDB#&lt;hex&gt;</c> there and changes on every
/// instance start. Null for any other server, and in every record written before it existed (additive: those still load).</param>
public sealed record ServerMeta(string Server, string Database, string Version, string ProductVersion, int MajorVersion,
    string Edition, string ServerCollation, string DatabaseCollation, int CompatLevel, string AuthSummary, string? DataSource = null);
