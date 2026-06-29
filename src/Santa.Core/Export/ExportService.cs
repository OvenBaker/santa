using System.Text;
using System.Text.Json;
using Santa.Core.Jsonl;
using Santa.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Santa.Core.Export;

public enum ExportFormat { Markdown, Json, Jsonl, Prose }

public sealed class ExportService
{
    private readonly Database _db;
    private readonly string _projectsRoot;

    public ExportService(Database db, string projectsRoot)
    {
        _db = db;
        _projectsRoot = projectsRoot;
    }

    public string ResolveSourcePath(string sessionId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT project_path FROM sessions WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", sessionId);
        var pp = cmd.ExecuteScalar() as string
            ?? throw new InvalidOperationException("session not in index");
        return Path.Combine(_projectsRoot, pp, sessionId + ".jsonl");
    }

    public void Export(string sessionId, ExportFormat format, TextWriter writer)
    {
        switch (format)
        {
            case ExportFormat.Jsonl:
                using (var sr = new StreamReader(ResolveSourcePath(sessionId)))
                {
                    string? line;
                    while ((line = sr.ReadLine()) is not null) writer.WriteLine(line);
                }
                break;
            case ExportFormat.Markdown:
                RenderMarkdown(sessionId, writer);
                break;
            case ExportFormat.Prose:
                RenderProse(sessionId, writer);
                break;
            case ExportFormat.Json:
                RenderJson(sessionId, writer);
                break;
        }
    }

    private void RenderMarkdown(string sessionId, TextWriter w)
    {
        var path = ResolveSourcePath(sessionId);
        w.WriteLine($"# Session {sessionId}");
        w.WriteLine($"**File:** `{path}`");
        w.WriteLine();

        var reader = new JsonlReader(path);
        foreach (var (_, _, ev) in reader.Read())
        {
            if (ev.IsUser)
            {
                var realText = ev.ContentBlocks.Where(b => b.IsText && !string.IsNullOrWhiteSpace(b.Text))
                    .Select(b => b.Text!).ToList();
                if (realText.Count > 0)
                {
                    w.WriteLine();
                    w.WriteLine($"## User  _{ev.Timestamp:u}_");
                    w.WriteLine();
                    foreach (var t in realText) w.WriteLine(t);
                }
                else
                {
                    // tool_result-only user message
                    foreach (var b in ev.ContentBlocks.Where(b => b.IsToolResult))
                        RenderToolResult(b, w);
                }
            }
            else if (ev.IsAssistant)
            {
                var hasContent = ev.ContentBlocks.Any(b =>
                    (b.IsText && !string.IsNullOrWhiteSpace(b.Text)) || b.IsToolUse);
                if (!hasContent) continue;

                w.WriteLine();
                w.WriteLine($"## Assistant  _{ev.Timestamp:u}_");
                w.WriteLine();
                foreach (var b in ev.ContentBlocks)
                {
                    if (b.IsText && !string.IsNullOrWhiteSpace(b.Text))
                    {
                        w.WriteLine(b.Text);
                        w.WriteLine();
                    }
                    else if (b.IsToolUse)
                    {
                        RenderToolUse(b, ev.Raw, w);
                    }
                }
            }
        }
    }

    private static void RenderToolUse(ContentBlock block, JsonElement raw, TextWriter w)
    {
        // Find the tool_use block in raw to get name + input.
        if (!raw.TryGetProperty("message", out var msg)) return;
        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
        // Just emit a generic tool-use header; we can't easily disambiguate which one this block is here.
        // For markdown export we accept that all tool_use blocks of this assistant turn appear in order below.
        foreach (var el in content.EnumerateArray())
        {
            if (el.TryGetProperty("type", out var t) && t.GetString() == "tool_use")
            {
                var name = el.TryGetProperty("name", out var n) ? n.GetString() : "?";
                w.WriteLine($"### Tool · {name}");
                if (el.TryGetProperty("input", out var input))
                {
                    w.WriteLine("```json");
                    w.WriteLine(JsonSerializer.Serialize(input, new JsonSerializerOptions { WriteIndented = true }));
                    w.WriteLine("```");
                }
                break; // emit once per assistant turn
            }
        }
    }

    private static void RenderToolResult(ContentBlock block, TextWriter w)
    {
        // we don't keep tool_result content text in our model; show a placeholder
        w.WriteLine();
        w.WriteLine("> _tool result (omitted from projection — see source jsonl for raw bytes)_");
        w.WriteLine();
    }

    private void RenderProse(string sessionId, TextWriter w)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT start_seq, end_seq, text FROM chunks WHERE session_id = $id ORDER BY start_seq";
        cmd.Parameters.AddWithValue("$id", sessionId);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            w.WriteLine($"--- chunk seq {rd.GetInt32(0)}..{rd.GetInt32(1)} ---");
            w.WriteLine(rd.GetString(2));
            w.WriteLine();
        }
    }

    private void RenderJson(string sessionId, TextWriter w)
    {
        using var meta = _db.Connection.CreateCommand();
        meta.CommandText = """
            SELECT id, project_path, cwd, git_branch, started_at, ended_at,
                   message_count, turn_count, status, first_user_text, last_user_text
              FROM sessions WHERE id = $id
            """;
        meta.Parameters.AddWithValue("$id", sessionId);
        using var rd = meta.ExecuteReader();
        if (!rd.Read())
            throw new InvalidOperationException("session not in index");
        var session = new
        {
            id = rd.GetString(0),
            project_path = rd.GetString(1),
            cwd = rd.IsDBNull(2) ? null : rd.GetString(2),
            git_branch = rd.IsDBNull(3) ? null : rd.GetString(3),
            started_at = rd.IsDBNull(4) ? null : rd.GetString(4),
            ended_at = rd.IsDBNull(5) ? null : rd.GetString(5),
            message_count = rd.GetInt32(6),
            turn_count = rd.GetInt32(7),
            status = rd.GetString(8),
            first_user_text = rd.IsDBNull(9) ? null : rd.GetString(9),
            last_user_text = rd.IsDBNull(10) ? null : rd.GetString(10),
        };
        rd.Close();

        // chunks
        var chunks = new List<object>();
        using var cc = _db.Connection.CreateCommand();
        cc.CommandText = "SELECT id, start_seq, end_seq, start_ts, end_ts, approx_tokens, text FROM chunks WHERE session_id = $id ORDER BY start_seq";
        cc.Parameters.AddWithValue("$id", sessionId);
        using var crd = cc.ExecuteReader();
        while (crd.Read())
        {
            chunks.Add(new
            {
                id = crd.GetInt64(0),
                start_seq = crd.GetInt32(1),
                end_seq = crd.GetInt32(2),
                start_ts = crd.IsDBNull(3) ? null : crd.GetString(3),
                end_ts = crd.IsDBNull(4) ? null : crd.GetString(4),
                approx_tokens = crd.GetInt32(5),
                text = crd.GetString(6),
            });
        }

        var doc = new { session, chunks };
        var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        w.Write(json);
    }
}
