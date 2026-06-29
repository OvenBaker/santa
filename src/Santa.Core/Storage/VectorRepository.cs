using Microsoft.Data.Sqlite;

namespace Santa.Core.Storage;

public sealed class VectorRepository
{
    private readonly SqliteConnection _conn;
    public VectorRepository(SqliteConnection conn) => _conn = conn;

    public void Upsert(long chunkId, ReadOnlySpan<float> embedding, string modelId, SqliteTransaction? tx = null)
    {
        // chunk_vec doesn't support UPDATE in older sqlite-vec; delete then insert.
        using (var del = _conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM chunk_vec WHERE rowid = $rid";
            del.Parameters.AddWithValue("$rid", chunkId);
            del.ExecuteNonQuery();
        }

        using (var ins = _conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO chunk_vec(rowid, embedding) VALUES($rid, $emb)";
            ins.Parameters.AddWithValue("$rid", chunkId);
            ins.Parameters.AddWithValue("$emb", FloatArrayToBlob(embedding));
            ins.ExecuteNonQuery();
        }

        using var meta = _conn.CreateCommand();
        meta.Transaction = tx;
        meta.CommandText = """
            INSERT INTO chunk_embeddings(chunk_id, model_id, dim, embedded_at)
            VALUES($id, $m, $d, $t)
            ON CONFLICT(chunk_id) DO UPDATE SET
                model_id    = excluded.model_id,
                dim         = excluded.dim,
                embedded_at = excluded.embedded_at
            """;
        meta.Parameters.AddWithValue("$id", chunkId);
        meta.Parameters.AddWithValue("$m", modelId);
        meta.Parameters.AddWithValue("$d", embedding.Length);
        meta.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        meta.ExecuteNonQuery();
    }

    public IReadOnlyList<(long ChunkId, double Distance)> Search(ReadOnlySpan<float> query, int k = 50)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT rowid, distance FROM chunk_vec
            WHERE embedding MATCH $q AND k = $k
            ORDER BY distance
            """;
        cmd.Parameters.AddWithValue("$q", FloatArrayToBlob(query));
        cmd.Parameters.AddWithValue("$k", k);
        using var rd = cmd.ExecuteReader();
        var results = new List<(long, double)>(k);
        while (rd.Read()) results.Add((rd.GetInt64(0), rd.GetDouble(1)));
        return results;
    }

    public bool HasEmbeddings(string modelId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM chunk_embeddings WHERE model_id = $m)";
        cmd.Parameters.AddWithValue("$m", modelId);
        return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
    }

    /// <summary>Wipe chunk_vec rows belonging to a session. vec0 has no FK CASCADE so we do this
    /// explicitly when chunks are about to be re-built.</summary>
    public void DeleteForSession(string sessionId, SqliteTransaction? tx = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        // Find chunk ids belonging to the session BEFORE chunks are deleted; vec0 doesn't allow
        // joining inside DELETE WHERE rowid IN (SELECT …) reliably across all sqlite-vec builds,
        // so collect ids first then loop.
        cmd.CommandText = "SELECT id FROM chunks WHERE session_id = $sid";
        cmd.Parameters.AddWithValue("$sid", sessionId);
        var ids = new List<long>();
        using (var rd = cmd.ExecuteReader())
            while (rd.Read()) ids.Add(rd.GetInt64(0));
        if (ids.Count == 0) return;

        using var del = _conn.CreateCommand();
        del.Transaction = tx;
        del.CommandText = "DELETE FROM chunk_vec WHERE rowid = $rid";
        var p = del.Parameters.Add("$rid", SqliteType.Integer);
        foreach (var id in ids) { p.Value = id; del.ExecuteNonQuery(); }
    }

    private static byte[] FloatArrayToBlob(ReadOnlySpan<float> floats)
        => System.Runtime.InteropServices.MemoryMarshal.AsBytes(floats).ToArray();
}
