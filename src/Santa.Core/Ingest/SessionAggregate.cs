using Santa.Core.Jsonl;
using Santa.Core.Projection;

namespace Santa.Core.Ingest;

/// <summary>
/// Reads a single JSONL file, projects it into Turns, and exposes session-level metadata
/// derived from the events.
/// </summary>
public sealed class SessionAggregate
{
    public string FilePath { get; }
    public string ProjectDirName { get; private set; }
    /// <summary>"claude-code" | "codex" — which agent produced this transcript.</summary>
    public string Provider { get; }
    public string SessionId { get; private set; } = "";
    public string? Cwd { get; private set; }
    public string? GitBranch { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }
    public DateTimeOffset? LastActiveAt { get; private set; }
    public int MessageCount { get; private set; }
    public IReadOnlyList<Turn> Turns { get; private set; } = Array.Empty<Turn>();
    public IReadOnlyList<string> DerivedBranches { get; private set; } = Array.Empty<string>();
    /// <summary>True when this JSONL was produced by santa itself shelling out to
    /// <c>claude -p</c> — should be filtered out of the index.</summary>
    public bool IsInternal { get; private set; }

    public SessionAggregate(string filePath, string provider = "claude-code")
    {
        FilePath = filePath;
        Provider = string.IsNullOrWhiteSpace(provider) ? "claude-code" : provider;
        ProjectDirName = new DirectoryInfo(Path.GetDirectoryName(filePath)!).Name;
    }

    private bool IsCodex => Provider == "codex";

    public void Build()
    {
        var events = new List<JsonlEvent>();
        var reader = IsCodex
            ? new CodexJsonlReader(FilePath).Read()
            : new JsonlReader(FilePath).Read();
        foreach (var (_, _, ev) in reader)
        {
            events.Add(ev);
            if (ev.SessionId is { } sid && SessionId.Length == 0) SessionId = sid;
            if (ev.Cwd is { } cwd && Cwd is null) Cwd = cwd;
            if (ev.GitBranch is { } br && GitBranch is null) GitBranch = br;
            if (ev.Timestamp is { } ts)
            {
                StartedAt ??= ts;
                EndedAt = ts;
            }
            if (ev.IsConversational) MessageCount++;
        }

        // SessionId can also be inferred from the filename (UUID). Codex names files
        // rollout-<ts>-<uuid>.jsonl, so pull the trailing uuid rather than the whole stem.
        if (SessionId.Length == 0)
            SessionId = IsCodex
                ? ExtractCodexUuid(Path.GetFileNameWithoutExtension(FilePath))
                : Path.GetFileNameWithoutExtension(FilePath);

        // Codex rollouts live under date dirs (…/2026/06/15) — useless as a project label.
        // Use the working directory's leaf instead so sessions group by repo like Claude's do.
        if (IsCodex)
            ProjectDirName = Cwd is { Length: > 0 } c ? new DirectoryInfo(c).Name : "codex";

        Turns = (IsCodex ? CodexTurnBuilder.Build(events) : TurnBuilder.Build(events)).ToList();
        DerivedBranches = BranchExtractor.Extract(events);

        // Detect santa's own claude-p invocations so we don't pollute the index with
        // copies of our own prompts. Modern calls carry the sentinel; legacy ones we recognise
        // by their distinctive openers (summarise / is_resolved / branch_merged). Codex never
        // produces these (santa shells out to `claude -p`, not codex), so skip the check there.
        if (!IsCodex && Turns.Count > 0)
        {
            var first = Turns[0].UserText;
            if (first.StartsWith(Classify.ClaudeCliRunner.InternalSentinel) ||
                first.StartsWith("You are summarising a Claude Code conversation log") ||
                first.StartsWith("You are reviewing the tail of a Claude Code session") ||
                first.StartsWith("Determine whether the work in this session has been merged"))
            {
                IsInternal = true;
            }
        }

        // "Last active" = last turn where the assistant actually produced prose.
        // Filters out resume-then-/exit noise where bookkeeping events update EndedAt
        // without representing real interaction. Falls back to last user prompt's ts,
        // then to EndedAt as a last resort.
        for (int i = Turns.Count - 1; i >= 0; i--)
        {
            if (!string.IsNullOrWhiteSpace(Turns[i].AssistantText) && Turns[i].AssistantTimestamp is { } ats)
            {
                LastActiveAt = ats;
                break;
            }
        }
        if (LastActiveAt is null && Turns.Count > 0)
            LastActiveAt = Turns[^1].UserTimestamp;
        LastActiveAt ??= EndedAt;
    }

    /// <summary>
    /// Pull the trailing UUID out of a Codex rollout stem like
    /// <c>rollout-2026-06-15T19-41-31-019ecc5f-ce1e-7052-8eb7-a318af9c62b8</c>.
    /// Falls back to the whole stem if no UUID is found.
    /// </summary>
    private static string ExtractCodexUuid(string stem)
    {
        var m = System.Text.RegularExpressions.Regex.Match(stem,
            "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        return m.Success ? m.Value : stem;
    }
}
