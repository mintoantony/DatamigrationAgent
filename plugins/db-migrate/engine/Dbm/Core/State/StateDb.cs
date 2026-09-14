using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Dbm.Core.State;

/// <summary>
/// One SQLite connection per process. Thread-safe: every public member takes a re-entrant lock and
/// InTransaction holds it for the whole transaction, so commands on other threads wait instead of interleaving.
/// </summary>
public sealed class StateDb : IDisposable
{
    public const string SchemaResource = "Dbm.Core.State.schema.sql";

    private readonly object _gate = new();
    private SqliteTransaction? _tx;

    private StateDb(SqliteConnection connection, string path)
    {
        Connection = connection;
        Path = path;
    }

    public SqliteConnection Connection { get; }
    public string Path { get; }

    public static StateDb Open(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString();
        var connection = new SqliteConnection(cs);
        connection.Open();
        var db = new StateDb(connection, full);
        try
        {
            db.Execute("PRAGMA journal_mode=WAL;");
            db.Execute("PRAGMA busy_timeout=5000;");
            db.Execute("PRAGMA foreign_keys=ON;");
            db.Migrate();
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    public void InTransaction(Action work) => InTransaction<object?>(() =>
    {
        work();
        return null;
    });

    /// <summary>BEGIN IMMEDIATE; a nested call joins the outer transaction.</summary>
    public T InTransaction<T>(Func<T> work)
    {
        lock (_gate)
        {
            if (_tx is not null) return work();
            _tx = Connection.BeginTransaction(deferred: false);
            try
            {
                var result = work();
                _tx.Commit();
                return result;
            }
            catch
            {
                try { _tx.Rollback(); } catch (SqliteException) { /* already rolled back */ }
                throw;
            }
            finally
            {
                _tx.Dispose();
                _tx = null;
            }
        }
    }

    /// <summary>args: anonymous object or IDictionary; property X binds to $X.</summary>
    public int Execute(string sql, object? args = null)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, args);
            return cmd.ExecuteNonQuery();
        }
    }

    public T? Scalar<T>(string sql, object? args = null)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, args);
            return ConvertScalar<T>(cmd.ExecuteScalar());
        }
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, object? args = null)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, args);
            using var reader = cmd.ExecuteReader();
            var list = new List<T>();
            while (reader.Read()) list.Add(map(reader));
            return list;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _tx?.Dispose();
            _tx = null;
            Connection.Dispose();
        }
    }

    /// <summary>Converts a CLR value to what SQLite stores: enums as snake text, timestamps as "O", bool as 0/1.</summary>
    public static object ToDb(object? value) => value switch
    {
        null => DBNull.Value,
        Enum e => EnumText.ToText(e),
        DateTimeOffset d => d.ToUniversalTime().ToString("O"),
        DateTime d => new DateTimeOffset(d.ToUniversalTime()).ToString("O"),
        bool b => b ? 1L : 0L,
        _ => value,
    };

    internal static string LoadSchema()
    {
        using var stream = typeof(StateDb).Assembly.GetManifestResourceStream(SchemaResource)
                           ?? throw new InvalidOperationException($"embedded resource {SchemaResource} is missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void Migrate()
    {
        if (Scalar<long>("PRAGMA user_version") >= 1) return;
        InTransaction(() =>
        {
            if (Scalar<long>("PRAGMA user_version") >= 1) return;   // another process won the race
            Execute(LoadSchema());
            Execute("PRAGMA user_version = 1");
        });
    }

    private SqliteCommand Command(string sql, object? args)
    {
        var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _tx;
        Bind(cmd, args);
        return cmd;
    }

    private static void Bind(SqliteCommand cmd, object? args)
    {
        switch (args)
        {
            case null:
                return;
            case IEnumerable<KeyValuePair<string, object?>> pairs:
                foreach (var (key, value) in pairs) cmd.Parameters.AddWithValue("$" + key, ToDb(value));
                return;
            default:
                foreach (var p in args.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length == 0) cmd.Parameters.AddWithValue("$" + p.Name, ToDb(p.GetValue(args)));
                }
                return;
        }
    }

    private static T? ConvertScalar<T>(object? value)
    {
        if (value is null || value is DBNull) return default;
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (target.IsInstanceOfType(value)) return (T)value;
        if (target == typeof(bool)) return (T)(object)(Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0);
        return (T)Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }
}
