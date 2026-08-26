using Microsoft.Data.Sqlite;
using Santa.Core.Usage;

namespace Santa.Core.Storage;

/// <summary>
/// Persists per-session, per-model usage rollups. session_usage deliberately has NO
/// foreign key to sessions: excluded/pruned agent runs (sdk workers, workflow fan-outs)
/// keep their usage rows even though their session row is deleted — they are precisely
/// where burn hides, while staying out of browse and search.
/// </summary>
public sealed class UsageRepository
{
    private readonly SqliteConnection _conn;
    public UsageRepository(SqliteConnection conn) => _conn = conn;

    /// <summary>Replace all usage rows for a session and refresh the sessions.cost_usd
    /// rollup (a no-op for pruned sessions with no sessions row).</summary>
    public void Replace(string sessionId, IReadOnlyList<ModelUsage> rollups, SqliteTransaction? tx = null)
    {
        using (var del = _conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM session_usage WHERE session_id = $id";
            del.Parameters.AddWithValue("$id", sessionId);
            del.ExecuteNonQuery();
        }

        foreach (var u in rollups)
        {
            using var ins = _conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO session_usage (session_id, model, turns, input_tokens, output_tokens,
                                           cache_read_tokens, cache_write_tokens, cost_usd, updated_at)
                VALUES ($id, $model, $turns, $in, $out, $cr, $cw, $cost, $ts)
                """;
            ins.Parameters.AddWithValue("$id", sessionId);
            ins.Parameters.AddWithValue("$model", u.Model);
            ins.Parameters.AddWithValue("$turns", u.Turns);
            ins.Parameters.AddWithValue("$in", u.InputTokens);
            ins.Parameters.AddWithValue("$out", u.OutputTokens);
            ins.Parameters.AddWithValue("$cr", u.CacheReadTokens);
            ins.Parameters.AddWithValue("$cw", u.CacheWriteTokens);
            ins.Parameters.AddWithValue("$cost", Math.Round(u.CostUsd, 4));
            ins.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
            ins.ExecuteNonQuery();
        }

        using var up = _conn.CreateCommand();
        up.Transaction = tx;
        up.CommandText = """
            UPDATE sessions
               SET cost_usd = (SELECT COALESCE(SUM(cost_usd), 0) FROM session_usage WHERE session_id = $id)
             WHERE id = $id
            """;
        up.Parameters.AddWithValue("$id", sessionId);
        up.ExecuteNonQuery();
    }
}
