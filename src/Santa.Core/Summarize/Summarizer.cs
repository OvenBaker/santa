using System.Text.Json;
using Santa.Core.Classify;
using Santa.Core.Sessions;
using Santa.Core.Storage;

namespace Santa.Core.Summarize;

public sealed class SummarizerOptions
{
    public string Model { get; init; } = "haiku";
    public string TranscriptMode { get; init; } = "tail"; // tail | head | full
    public int? TranscriptMaxTokens { get; init; } = 6000;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(8);
}

public sealed class Summarizer
{
    private readonly Database _db;
    private readonly SummarizerOptions _opts;
    private readonly TranscriptRenderer _renderer;
    // Microsoft.Data.Sqlite is not safe for concurrent commands on a single connection.
    // We allow concurrent claude-p calls but serialise the read+write hits.
    private readonly object _dbLock = new();

    public Summarizer(Database db, SummarizerOptions? opts = null)
    {
        _db = db;
        _opts = opts ?? new SummarizerOptions();
        _renderer = new TranscriptRenderer(db);
    }

    /// <summary>Hard-fails if ANTHROPIC_API_KEY is set, so we never accidentally bill API.</summary>
    public static void EnsureSubscriptionMode()
    {
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException(
                "ANTHROPIC_API_KEY is set — summarisation would bill the API. " +
                "Unset it (or run with `env -u ANTHROPIC_API_KEY santa …`) so claude -p uses your Max subscription.");
    }

    public async Task<SessionSummary?> SummariseAsync(SessionInfo session,
        bool dryRun = false, Action<string>? log = null, CancellationToken ct = default)
    {
        log ??= _ => { };
        // ANTHROPIC_API_KEY guard only matters for the claude -p path; codex uses ChatGPT auth.
        if (!LlmRunner.CodexSelected) EnsureSubscriptionMode();

        string transcript;
        lock (_dbLock)
            transcript = _renderer.Render(session.Id, _opts.TranscriptMode, _opts.TranscriptMaxTokens);
        var prompt = BuildPrompt(session, transcript);

        if (dryRun)
        {
            log($"  [dry-run] model={_opts.Model} prompt={prompt.Length}c transcript={transcript.Length}c");
            return null;
        }

        var req = new ClaudeRunRequest(
            Prompt: prompt,
            Model: _opts.Model,
            AllowedTools: Array.Empty<string>(),
            PermissionMode: null,
            WorkingDirectory: null,
            Timeout: _opts.Timeout);

        var run = await LlmRunner.RunAsync(req, ct);
        if (run.ExitCode != 0)
            throw new InvalidOperationException(
                $"summariser ({LlmRunner.ModelFor(req)}) exited {run.ExitCode}: {run.Stderr.Trim()}");

        var parsed = ParseResponse(run.ResultText);
        if (parsed is null) return null;

        var summary = new SessionSummary(
            Title: parsed.Title,
            Short: parsed.Short,
            Long: parsed.Long,
            Model: LlmRunner.ModelFor(req),
            GeneratedAt: DateTimeOffset.UtcNow,
            TurnCountAtGeneration: session.TurnCount);

        lock (_dbLock) Persist(session.Id, summary);
        return summary;
    }

    public bool NeedsSummary(string sessionId, int currentTurnCount)
    {
        lock (_dbLock)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = "SELECT summary_at, summary_turn_count FROM sessions WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", sessionId);
            using var rd = cmd.ExecuteReader();
            if (!rd.Read()) return true;
            if (rd.IsDBNull(0)) return true;
            if (rd.IsDBNull(1)) return true;
            var summarisedTurns = rd.GetInt32(1);
            return summarisedTurns != currentTurnCount;
        }
    }

    private void Persist(string sessionId, SessionSummary summary)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            UPDATE sessions
               SET summary_title       = $t,
                   summary_short       = $s,
                   summary_long        = $l,
                   summary_model       = $m,
                   summary_at          = $at,
                   summary_turn_count  = $tc
             WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$t",  summary.Title);
        cmd.Parameters.AddWithValue("$s",  summary.Short);
        cmd.Parameters.AddWithValue("$l",  summary.Long);
        cmd.Parameters.AddWithValue("$m",  summary.Model);
        cmd.Parameters.AddWithValue("$at", summary.GeneratedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$tc", summary.TurnCountAtGeneration);
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.ExecuteNonQuery();
    }

    private string BuildPrompt(SessionInfo session, string transcript)
    {
        var firstUser = session.FirstUserText is null ? "" : Trim(session.FirstUserText, 800);
        const string schema =
            """{"title":"<≤80 chars: noun-phrase headline of what the session was about>","summary_short":"<one sentence: what was discussed/decided/built>","summary_long":"<2-3 short paragraphs: the arc of the conversation, key decisions, the outcome>"}""";
        return $"""
            You are summarising a Claude Code conversation log so the user can find it later by topic.
            Tool calls have already been stripped from the transcript — what you see is the planning + narrative spine of the session.

            Repo (cwd): {session.Cwd ?? "-"}
            Branch at start: {session.GitBranch ?? "-"}
            Started: {session.StartedAt?.ToString("u") ?? "-"}
            Turn count: {session.TurnCount}

            First user prompt:
            {firstUser}

            Session transcript (tail-truncated; tool I/O removed):

            {transcript}

            Output strict JSON only — no surrounding prose, no code fences:
            {schema}
            """;
    }

    private record ParsedResponse(string Title, string Short, string Long);

    private static ParsedResponse? ParseResponse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var json = TryFindJsonObject(text);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? Get(string k) => root.TryGetProperty(k, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString() : null;
            var title = Get("title");
            var s = Get("summary_short");
            var l = Get("summary_long");
            if (title is null || s is null || l is null) return null;
            return new ParsedResponse(title.Trim(), s.Trim(), l.Trim());
        }
        catch (JsonException) { return null; }
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

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
