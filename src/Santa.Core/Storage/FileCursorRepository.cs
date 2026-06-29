using Microsoft.Data.Sqlite;

namespace Santa.Core.Storage;

public sealed record FileCursor(string Path, long Size, long MtimeUnix, long LastOffset, int LastSeq, string? SessionId);

public sealed class FileCursorRepository
{
    private readonly SqliteConnection _conn;
    public FileCursorRepository(SqliteConnection conn) => _conn = conn;

    public FileCursor? Get(string path)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT path, size, mtime_unix, last_offset, last_seq, session_id FROM files WHERE path = $p";
        cmd.Parameters.AddWithValue("$p", path);
        using var rd = cmd.ExecuteReader();
        if (!rd.Read()) return null;
        return new FileCursor(
            rd.GetString(0),
            rd.GetInt64(1),
            rd.GetInt64(2),
            rd.GetInt64(3),
            rd.GetInt32(4),
            rd.IsDBNull(5) ? null : rd.GetString(5));
    }

    public void Upsert(FileCursor cur, SqliteTransaction? tx = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO files (path, size, mtime_unix, last_offset, last_seq, session_id)
            VALUES ($p, $sz, $mt, $off, $seq, $sid)
            ON CONFLICT(path) DO UPDATE SET
                size        = excluded.size,
                mtime_unix  = excluded.mtime_unix,
                last_offset = excluded.last_offset,
                last_seq    = excluded.last_seq,
                session_id  = excluded.session_id
            """;
        cmd.Parameters.AddWithValue("$p", cur.Path);
        cmd.Parameters.AddWithValue("$sz", cur.Size);
        cmd.Parameters.AddWithValue("$mt", cur.MtimeUnix);
        cmd.Parameters.AddWithValue("$off", cur.LastOffset);
        cmd.Parameters.AddWithValue("$seq", cur.LastSeq);
        cmd.Parameters.AddWithValue("$sid", (object?)cur.SessionId ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
