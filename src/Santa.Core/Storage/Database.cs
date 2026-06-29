using Microsoft.Data.Sqlite;

namespace Santa.Core.Storage;

public sealed class Database : IDisposable
{
    public SqliteConnection Connection { get; }
    public bool VecEnabled { get; private set; }

    private Database(SqliteConnection conn) => Connection = conn;

    public static Database Open(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var conn = new SqliteConnection($"Data Source={path};Cache=Shared");
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = Schema.Sql;
            cmd.ExecuteNonQuery();
        }
        Migrate(conn);
        SetMeta(conn, "schema_version", Schema.Version.ToString());

        return new Database(conn);
    }

    /// <summary>Attempt to load sqlite-vec. Idempotent; safe to call when missing.</summary>
    public bool TryEnableVec(int dim, string? libPath = null)
    {
        if (VecEnabled) return true;
        try
        {
            VecExtension.Load(Connection, libPath);
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = string.Format(Schema.VecSchemaSqlTemplate, dim);
            cmd.ExecuteNonQuery();
            VecEnabled = true;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Migrate(SqliteConnection conn)
    {
        // Idempotent ALTER TABLEs for fields added after v1. SQLite has no IF NOT EXISTS on
        // ADD COLUMN, so check pragma table_info first.
        var existing = new HashSet<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(sessions)";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) existing.Add(rd.GetString(1));
        }
        if (!existing.Contains("derived_branches"))
            AddColumn(conn, "sessions", "derived_branches TEXT");
        if (!existing.Contains("summary_title"))
            AddColumn(conn, "sessions", "summary_title TEXT");
        if (!existing.Contains("summary_short"))
            AddColumn(conn, "sessions", "summary_short TEXT");
        if (!existing.Contains("summary_long"))
            AddColumn(conn, "sessions", "summary_long TEXT");
        if (!existing.Contains("summary_model"))
            AddColumn(conn, "sessions", "summary_model TEXT");
        if (!existing.Contains("summary_at"))
            AddColumn(conn, "sessions", "summary_at TEXT");
        if (!existing.Contains("summary_turn_count"))
            AddColumn(conn, "sessions", "summary_turn_count INTEGER");
        if (!existing.Contains("last_active_at"))
            AddColumn(conn, "sessions", "last_active_at TEXT");
        if (!existing.Contains("provider"))
            AddColumn(conn, "sessions", "provider TEXT NOT NULL DEFAULT 'claude-code'");
    }

    private static void AddColumn(SqliteConnection conn, string table, string columnDef)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {columnDef}";
        cmd.ExecuteNonQuery();
    }

    private static void SetMeta(SqliteConnection conn, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO schema_meta(key, value) VALUES($k, $v) " +
                          "ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public SqliteTransaction BeginTransaction() => Connection.BeginTransaction();

    public void Dispose()
    {
        Connection.Dispose();
    }

    public static string DefaultPath => SantaPaths.IndexDb;
}
