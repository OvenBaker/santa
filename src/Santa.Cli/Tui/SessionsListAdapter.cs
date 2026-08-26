using Santa.Core.Sessions;
using Santa.Core.Storage;

namespace Santa.Cli.Tui;

/// <summary>
/// Lightweight session row pulled for the TUI list — just what the rows need to render.
/// Detail-pane content (summary_long) is lazy-loaded by id when a row is selected.
/// </summary>
public sealed record TuiSessionRow(
    string Id,
    string? Cwd,
    string? GitBranch,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastActiveAt,
    string Status,
    string? FirstUserText,
    string? SummaryTitle,
    string? SummaryShort,
    int TurnCount,
    int TotalChunks,
    string Provider = "claude-code",
    double? CostUsd = null)
{
    public bool IsCodex => Provider == "codex";

    public string DateLabel =>
        (LastActiveAt ?? StartedAt) is null
            ? "          ----"
            : (LastActiveAt ?? StartedAt)!.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string Duration => DurationFormat.Compact(StartedAt, LastActiveAt);

    public string StatsBlurb
    {
        get
        {
            var s = string.IsNullOrEmpty(Duration) ? $"{TurnCount}t" : $"{TurnCount}t · {Duration}";
            if (CostUsd is > 0.005 and var c)
                s += c >= 10 ? $" · ${c:F0}" : $" · ${c:F2}";
            return s;
        }
    }

    public string StatusIcon => Status switch
    {
        "completed" => "✓",
        "archived"  => "·",
        _ => " ",
    };

    public string DisplayTitle =>
        !string.IsNullOrEmpty(SummaryTitle) ? SummaryTitle :
        !string.IsNullOrEmpty(SummaryShort) ? SummaryShort :
        FirstUserText is null ? "(no prompt)" : Truncate(FirstUserText.Replace('\n', ' '), 90);

    public string ShortCwd => Cwd is null ? "" :
        Cwd.Length <= 36 ? Cwd : "…" + Cwd[^35..];

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

public static class SessionsListAdapter
{
    public static List<TuiSessionRow> LoadAll(Database db)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.cwd, s.git_branch, s.started_at, s.last_active_at, s.status,
                   s.first_user_text, s.summary_title, s.summary_short,
                   s.turn_count,
                   (SELECT COUNT(*) FROM chunks c WHERE c.session_id = s.id) AS chunk_count,
                   s.provider, s.cost_usd
            FROM sessions s
            ORDER BY COALESCE(s.last_active_at, s.started_at) DESC
            """;
        using var rd = cmd.ExecuteReader();
        var list = new List<TuiSessionRow>();
        while (rd.Read())
        {
            list.Add(new TuiSessionRow(
                Id: rd.GetString(0),
                Cwd: rd.IsDBNull(1) ? null : rd.GetString(1),
                GitBranch: rd.IsDBNull(2) ? null : rd.GetString(2),
                StartedAt: rd.IsDBNull(3) ? null : DateTimeOffset.Parse(rd.GetString(3)),
                LastActiveAt: rd.IsDBNull(4) ? null : DateTimeOffset.Parse(rd.GetString(4)),
                Status: rd.GetString(5),
                FirstUserText: rd.IsDBNull(6) ? null : rd.GetString(6),
                SummaryTitle: rd.IsDBNull(7) ? null : rd.GetString(7),
                SummaryShort: rd.IsDBNull(8) ? null : rd.GetString(8),
                TurnCount: rd.GetInt32(9),
                TotalChunks: rd.GetInt32(10),
                Provider: rd.IsDBNull(11) ? "claude-code" : rd.GetString(11),
                CostUsd: rd.IsDBNull(12) ? null : rd.GetDouble(12)));
        }
        return list;
    }

    public static (string? SummaryShort, string? SummaryLong, string? FirstUserText, string? Cwd, string? GitBranch)
        LoadDetail(Database db, string sessionId)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT summary_short, summary_long, first_user_text, cwd, git_branch FROM sessions WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", sessionId);
        using var rd = cmd.ExecuteReader();
        if (!rd.Read()) return (null, null, null, null, null);
        return (
            rd.IsDBNull(0) ? null : rd.GetString(0),
            rd.IsDBNull(1) ? null : rd.GetString(1),
            rd.IsDBNull(2) ? null : rd.GetString(2),
            rd.IsDBNull(3) ? null : rd.GetString(3),
            rd.IsDBNull(4) ? null : rd.GetString(4));
    }

    public static void SetStatus(Database db, string sessionId, string status, string source)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            UPDATE sessions SET status=$st, status_source=$src, status_set_at=$ts
             WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$st", status);
        cmd.Parameters.AddWithValue("$src", source);
        cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.ExecuteNonQuery();
    }
}
