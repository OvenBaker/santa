using System.Text;
using Santa.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Santa.Core.Classify;

/// <summary>
/// Renders a session's projected prose (already tool-I/O-stripped at ingest time)
/// as compact markdown for feeding to claude -p.
/// </summary>
public sealed class TranscriptRenderer
{
    private readonly Database _db;
    public TranscriptRenderer(Database db) => _db = db;

    public string Render(string sessionId, string mode = "tail", int? maxTokens = null)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT text, approx_tokens FROM chunks WHERE session_id = $sid ORDER BY start_seq";
        cmd.Parameters.AddWithValue("$sid", sessionId);
        using var rd = cmd.ExecuteReader();

        var rows = new List<(string Text, int Tokens)>();
        while (rd.Read())
            rows.Add((rd.GetString(0), rd.GetInt32(1)));
        if (rows.Count == 0) return "(no transcript)";

        // Dedupe overlap-introduced repeated text by simply concatenating chunks; chunks already
        // overlap by 1 turn, so we accept that minor redundancy here.
        IEnumerable<(string Text, int Tokens)> selected = mode.ToLowerInvariant() switch
        {
            "full" => rows,
            "head" => SelectByBudget(rows, maxTokens, fromHead: true),
            _      => SelectByBudget(rows, maxTokens, fromHead: false),
        };

        var sb = new StringBuilder();
        foreach (var r in selected)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(r.Text);
        }
        return sb.ToString();
    }

    private static IEnumerable<(string Text, int Tokens)> SelectByBudget(
        List<(string Text, int Tokens)> rows, int? maxTokens, bool fromHead)
    {
        if (maxTokens is null or <= 0) return rows;
        var picked = new List<(string Text, int Tokens)>();
        int total = 0;
        var ordered = fromHead ? rows : Enumerable.Reverse(rows);
        foreach (var r in ordered)
        {
            if (total + r.Tokens > maxTokens) break;
            picked.Add(r);
            total += r.Tokens;
        }
        if (!fromHead) picked.Reverse();
        return picked;
    }
}
