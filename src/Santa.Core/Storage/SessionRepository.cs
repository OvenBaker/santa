using Microsoft.Data.Sqlite;

namespace Santa.Core.Storage;

public sealed record SessionRow(
    string Id,
    string ProjectPath,
    string? Cwd,
    string? GitBranch,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    int MessageCount,
    int TurnCount,
    string? FirstUserText,
    string? LastUserText,
    string? DerivedBranchesJson = null,
    DateTimeOffset? LastActiveAt = null,
    string Provider = "claude-code");

public sealed class SessionRepository
{
    private readonly SqliteConnection _conn;
    public SessionRepository(SqliteConnection conn) => _conn = conn;

    public void Upsert(SessionRow row, SqliteTransaction? tx = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO sessions (id, project_path, cwd, git_branch, derived_branches, started_at, ended_at,
                                  last_active_at, message_count, turn_count, first_user_text, last_user_text, provider)
            VALUES ($id, $proj, $cwd, $br, $db, $started, $ended, $la, $mc, $tc, $first, $last, $provider)
            ON CONFLICT(id) DO UPDATE SET
                project_path     = excluded.project_path,
                cwd              = COALESCE(excluded.cwd, sessions.cwd),
                git_branch       = COALESCE(excluded.git_branch, sessions.git_branch),
                derived_branches = excluded.derived_branches,
                started_at       = COALESCE(sessions.started_at, excluded.started_at),
                ended_at         = excluded.ended_at,
                last_active_at   = excluded.last_active_at,
                message_count    = excluded.message_count,
                turn_count       = excluded.turn_count,
                first_user_text  = COALESCE(sessions.first_user_text, excluded.first_user_text),
                last_user_text   = excluded.last_user_text,
                provider         = excluded.provider
            """;
        cmd.Parameters.AddWithValue("$id", row.Id);
        cmd.Parameters.AddWithValue("$proj", row.ProjectPath);
        cmd.Parameters.AddWithValue("$cwd", (object?)row.Cwd ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$br", (object?)row.GitBranch ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$db", (object?)row.DerivedBranchesJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$started", (object?)row.StartedAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ended", (object?)row.EndedAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$la", (object?)row.LastActiveAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mc", row.MessageCount);
        cmd.Parameters.AddWithValue("$tc", row.TurnCount);
        cmd.Parameters.AddWithValue("$first", (object?)Truncate(row.FirstUserText, 500) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$last", (object?)Truncate(row.LastUserText, 500) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$provider", string.IsNullOrWhiteSpace(row.Provider) ? "claude-code" : row.Provider);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Wipes a session row and all its dependents (chunks via FK cascade, plus FTS rows).</summary>
    public void Delete(string sessionId, SqliteTransaction? tx = null)
    {
        DeleteChunks(sessionId, tx);
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM sessions WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.ExecuteNonQuery();
    }

    public void DeleteChunks(string sessionId, SqliteTransaction? tx = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DELETE FROM chunks_fts WHERE session_id = $id;
            DELETE FROM chunks WHERE session_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.ExecuteNonQuery();
    }

    private static string? Truncate(string? s, int max) =>
        s is null ? null : s.Length <= max ? s : s[..max];
}
