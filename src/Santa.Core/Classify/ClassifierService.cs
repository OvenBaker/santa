using System.Text.Json;
using Santa.Core.Sessions;
using Santa.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Santa.Core.Classify;

public sealed class ClassifierService
{
    private readonly Database _db;
    private readonly TranscriptRenderer _transcript;

    public ClassifierService(Database db)
    {
        _db = db;
        _transcript = new TranscriptRenderer(db);
    }

    public async Task<ClassificationResult> ClassifyAsync(
        SessionInfo session,
        ClassificationRecipe recipe,
        bool dryRun = false,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        log ??= _ => { };

        var transcript = _transcript.Render(session.Id, recipe.TranscriptMode, recipe.TranscriptMaxTokens);
        var prompt = PromptRenderer.Render(recipe.Prompt, session, transcript);

        var req = new ClaudeRunRequest(
            Prompt: prompt,
            Model: recipe.Model,
            AllowedTools: recipe.Mode == ClassificationMode.Agent ? recipe.AllowedTools : Array.Empty<string>(),
            PermissionMode: null,
            WorkingDirectory: session.Cwd,
            Timeout: TimeSpan.FromMinutes(5));

        if (dryRun)
        {
            log($"  [dry-run] model={recipe.Model} mode={recipe.Mode} tools=[{string.Join(",", req.AllowedTools)}]");
            log($"  [dry-run] prompt ({prompt.Length} chars):");
            log("  ---");
            foreach (var line in prompt.Split('\n').Take(40)) log("  " + line);
            if (prompt.Split('\n').Length > 40) log("  …");
            log("  ---");
            return new ClassificationResult(session.Id, recipe.Id, DateTimeOffset.UtcNow,
                Status: null, Evidence: "(dry-run)", Model: recipe.Model, RawJson: "");
        }

        var run = await LlmRunner.RunAsync(req, ct);
        if (run.ExitCode != 0)
            throw new InvalidOperationException(
                $"classifier ({LlmRunner.ModelFor(req)}) exited {run.ExitCode}: {run.Stderr.Trim()}");

        var (status, evidence) = ExtractVerdict(run.ResultText, recipe);
        var result = new ClassificationResult(
            SessionId: session.Id,
            RecipeId: recipe.Id,
            Ts: DateTimeOffset.UtcNow,
            Status: status,
            Evidence: evidence,
            Model: LlmRunner.ModelFor(req),
            RawJson: run.Envelope?.GetRawText() ?? "");

        Persist(result, recipe);
        return result;
    }

    private void Persist(ClassificationResult r, ClassificationRecipe recipe)
    {
        using var tx = _db.BeginTransaction();
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO classifications(session_id, recipe_id, ts, status, evidence, model, raw_json)
                VALUES($s, $r, $t, $st, $ev, $m, $j)
                ON CONFLICT(session_id, recipe_id) DO UPDATE SET
                    ts=excluded.ts, status=excluded.status, evidence=excluded.evidence,
                    model=excluded.model, raw_json=excluded.raw_json
                """;
            cmd.Parameters.AddWithValue("$s", r.SessionId);
            cmd.Parameters.AddWithValue("$r", r.RecipeId);
            cmd.Parameters.AddWithValue("$t", r.Ts.ToString("O"));
            cmd.Parameters.AddWithValue("$st", (object?)r.Status ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ev", (object?)r.Evidence ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$m", r.Model);
            cmd.Parameters.AddWithValue("$j", r.RawJson);
            cmd.ExecuteNonQuery();
        }

        // Apply status_map → sessions.status, but never overwrite a manual override.
        if (r.Status is not null && recipe.StatusMap.TryGetValue(r.Status, out var sessionStatus))
        {
            using var upd = _db.Connection.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = """
                UPDATE sessions
                   SET status = $st, status_source = $src, status_set_at = $ts
                 WHERE id = $id AND (status_source IS NULL OR status_source != 'manual')
                """;
            upd.Parameters.AddWithValue("$st", sessionStatus);
            upd.Parameters.AddWithValue("$src", recipe.Id);
            upd.Parameters.AddWithValue("$ts", r.Ts.ToString("O"));
            upd.Parameters.AddWithValue("$id", r.SessionId);
            upd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// Pulls a JSON object out of the assistant's response text. We accept either a clean
    /// JSON-only response or one with surrounding prose — the recipe is asking for JSON.
    /// </summary>
    private static (string? Status, string? Evidence) ExtractVerdict(string? text, ClassificationRecipe recipe)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        var json = TryFindJsonObject(text);
        if (json is null) return (null, text.Trim());
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? status = root.TryGetProperty("status", out var sEl) && sEl.ValueKind == JsonValueKind.String
                ? sEl.GetString() : null;
            string? evidence = root.TryGetProperty("evidence", out var eEl) && eEl.ValueKind == JsonValueKind.String
                ? eEl.GetString() : null;
            return (status, evidence);
        }
        catch (JsonException) { return (null, text.Trim()); }
    }

    private static string? TryFindJsonObject(string s)
    {
        int start = s.IndexOf('{');
        if (start < 0) return null;
        int depth = 0;
        bool inStr = false;
        bool esc = false;
        for (int i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (esc) { esc = false; continue; }
            if (c == '\\' && inStr) { esc = true; continue; }
            if (c == '"') { inStr = !inStr; continue; }
            if (inStr) continue;
            if (c == '{') depth++;
            else if (c == '}') { depth--; if (depth == 0) return s[start..(i + 1)]; }
        }
        return null;
    }
}
