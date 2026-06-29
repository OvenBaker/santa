using Santa.Core.Projection;
using Microsoft.Data.Sqlite;

namespace Santa.Core.Storage;

public sealed class ChunkRepository
{
    private readonly SqliteConnection _conn;
    public ChunkRepository(SqliteConnection conn) => _conn = conn;

    public long Insert(Chunk chunk, SqliteTransaction? tx = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO chunks (session_id, start_seq, end_seq, start_ts, end_ts, text, approx_tokens)
            VALUES ($sid, $ss, $es, $sts, $ets, $text, $tok)
            RETURNING id
            """;
        cmd.Parameters.AddWithValue("$sid", chunk.SessionId);
        cmd.Parameters.AddWithValue("$ss", chunk.StartSeq);
        cmd.Parameters.AddWithValue("$es", chunk.EndSeq);
        cmd.Parameters.AddWithValue("$sts", (object?)chunk.StartTs?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ets", (object?)chunk.EndTs?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$text", chunk.Text);
        cmd.Parameters.AddWithValue("$tok", chunk.ApproxTokens);
        var id = (long)cmd.ExecuteScalar()!;

        using var fts = _conn.CreateCommand();
        fts.Transaction = tx;
        fts.CommandText = "INSERT INTO chunks_fts(rowid, text, session_id, chunk_id) VALUES ($rid, $t, $s, $c)";
        fts.Parameters.AddWithValue("$rid", id);
        fts.Parameters.AddWithValue("$t", chunk.Text);
        fts.Parameters.AddWithValue("$s", chunk.SessionId);
        fts.Parameters.AddWithValue("$c", id);
        fts.ExecuteNonQuery();

        return id;
    }
}
