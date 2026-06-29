using System.Text.Json;
using Santa.Core.Storage;

namespace Santa.Core.Sessions;

public sealed record SessionInfo(
    string Id,
    string ProjectPath,
    string? Cwd,
    string? GitBranch,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    int MessageCount,
    int TurnCount,
    string Status,
    string? FirstUserText,
    string? LastUserText,
    IReadOnlyList<string>? DerivedBranches = null,
    DateTimeOffset? LastActiveAt = null,
    string Provider = "claude-code");

public sealed class SessionLookup
{
    private readonly Database _db;
    public SessionLookup(Database db) => _db = db;

    public SessionInfo? FindByPrefix(string prefix)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, project_path, cwd, git_branch, started_at, ended_at,
                   message_count, turn_count, status, first_user_text, last_user_text,
                   derived_branches, last_active_at, provider
            FROM sessions
            WHERE id LIKE $q || '%'
            ORDER BY started_at DESC
            LIMIT 2
            """;
        cmd.Parameters.AddWithValue("$q", prefix);
        using var rd = cmd.ExecuteReader();
        if (!rd.Read()) return null;

        var info = new SessionInfo(
            Id: rd.GetString(0),
            ProjectPath: rd.GetString(1),
            Cwd: rd.IsDBNull(2) ? null : rd.GetString(2),
            GitBranch: rd.IsDBNull(3) ? null : rd.GetString(3),
            StartedAt: rd.IsDBNull(4) ? null : DateTimeOffset.Parse(rd.GetString(4)),
            EndedAt: rd.IsDBNull(5) ? null : DateTimeOffset.Parse(rd.GetString(5)),
            MessageCount: rd.GetInt32(6),
            TurnCount: rd.GetInt32(7),
            Status: rd.GetString(8),
            FirstUserText: rd.IsDBNull(9) ? null : rd.GetString(9),
            LastUserText: rd.IsDBNull(10) ? null : rd.GetString(10),
            DerivedBranches: rd.IsDBNull(11) ? null : ParseBranches(rd.GetString(11)),
            LastActiveAt: rd.IsDBNull(12) ? null : DateTimeOffset.Parse(rd.GetString(12)),
            Provider: rd.IsDBNull(13) ? "claude-code" : rd.GetString(13));

        if (rd.Read())
            throw new InvalidOperationException(
                $"Prefix '{prefix}' is ambiguous (matches at least {info.Id} and {rd.GetString(0)}). Use a longer prefix.");

        return info;
    }

    public static IReadOnlyList<string>? ParseBranches(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json); }
        catch { return null; }
    }
}
