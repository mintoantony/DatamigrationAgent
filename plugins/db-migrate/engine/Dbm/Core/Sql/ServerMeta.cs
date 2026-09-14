namespace Dbm.Core.Sql;

/// <summary>What SETUP captures about each side. Never contains credentials.</summary>
public sealed record ServerMeta(string Server, string Database, string Version, string ProductVersion, int MajorVersion,
    string Edition, string ServerCollation, string DatabaseCollation, int CompatLevel, string AuthSummary);
