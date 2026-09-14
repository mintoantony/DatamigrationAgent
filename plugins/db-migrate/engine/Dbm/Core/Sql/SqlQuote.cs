namespace Dbm.Core.Sql;

/// <summary>T-SQL quoting helpers. Every identifier or literal that the engine splices into SQL goes through here.</summary>
public static class SqlQuote
{
    /// <summary>Bracket-quotes an identifier: <c>Order]Lines</c> becomes <c>[Order]]Lines]</c>.</summary>
    public static string Ident(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return "[" + name.Replace("]", "]]") + "]";
    }

    /// <summary><c>[schema].[name]</c>.</summary>
    public static string Table(string schema, string name) => Ident(schema) + "." + Ident(name);

    /// <summary>"dbo.Customer" becomes "[dbo].[Customer]" (split on the first '.').</summary>
    public static string TableKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var dot = key.IndexOf('.');
        if (dot <= 0 || dot == key.Length - 1)
            throw new ArgumentException($"Expected a 'schema.table' key but got '{key}'.", nameof(key));
        return Table(key[..dot], key[(dot + 1)..]);
    }

    /// <summary>Unicode string literal with embedded quotes doubled: <c>O'Brien</c> becomes <c>N'O''Brien'</c>.</summary>
    public static string Literal(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "N'" + value.Replace("'", "''") + "'";
    }
}
