using System.Text.Json;
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
    /// <summary>Per-model token usage (Claude transcripts only; empty for Codex).</summary>
    public IReadOnlyList<Usage.ModelUsage> UsageByModel { get; private set; } = Array.Empty<Usage.ModelUsage>();
    /// <summary>True when this JSONL was produced by santa itself shelling out to
    /// <c>claude -p</c> — should be filtered out of the index.</summary>
    public bool IsInternal { get; private set; }

    /// <summary>True when this transcript is an automated or controlled agent run rather than one of the
    /// user's primary interactive sessions. These runs are machine-driven, high-volume, and not useful as
    /// standalone resume targets, so they are filtered out like <see cref="IsInternal"/>.</summary>
    public bool IsExcludedAgentRun { get; private set; }

    /// <summary>Codex only: the rollout's <c>session_meta.originator</c> — who launched the run. Observed values:
    /// <c>"codex-tui"</c> (interactive TUI session the user drives), <c>"codex_exec"</c> (direct <c>codex exec</c>),
    /// and <c>"Claude Code"</c> (a codex sub-agent spawned by the Claude Code codex plugin). Only the first is
    /// interactive. Null for Claude sessions or rollouts lacking the field.</summary>
    public string? CodexOriginator { get; private set; }

    /// <summary>Codex only: true when <c>session_meta.source</c> identifies this rollout as a subagent of a
    /// primary session. This covers controlled guardian sessions and explicitly spawned worker/reviewer
    /// threads, none of which are useful as standalone resume targets.</summary>
    public bool IsCodexSubagent { get; private set; }

    /// <summary>Claude only: the transcript's <c>entrypoint</c> — how the session was launched. Observed values:
    /// <c>"cli"</c> (interactive terminal session) and <c>"sdk-cli"</c> (headless Agent SDK run). Null for Codex
    /// sessions or transcripts predating the field (~mid-2026).</summary>
    public string? ClaudeEntrypoint { get; private set; }

    /// <summary>Claude only: the first prompt's <c>promptSource</c>. <c>"typed"</c> and <c>"queued"</c> are the
    /// user at the keyboard; <c>"sdk"</c> marks a machine-driven run. Null when the field is absent.</summary>
    public string? ClaudePromptSource { get; private set; }

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
        var usage = new Usage.UsageAccumulator();
        var reader = IsCodex
            ? new CodexJsonlReader(FilePath).Read()
            : new JsonlReader(FilePath).Read();
        foreach (var (_, _, ev) in reader)
        {
            events.Add(ev);
            if (!IsCodex) usage.Add(ev);
            // Codex records both the launcher and whether a run is subordinate to a primary driver in its
            // session metadata. Capture those structural signals for the exclusion check below.
            if (IsCodex && ev.Type == "session_meta")
            {
                ReadCodexMetadata(ev.Raw, out var originator, out var isSubagent);
                CodexOriginator ??= originator;
                if (isSubagent) IsCodexSubagent = true;
            }
            // Claude stamps launch metadata on each user event (entrypoint / promptSource). Keep scanning
            // until an event actually carries a field — caveat/meta user lines may omit them.
            if (!IsCodex && ev.IsUser && ClaudeEntrypoint is null && ClaudePromptSource is null)
            {
                ReadClaudeMetadata(ev.Raw, out var entrypoint, out var promptSource);
                ClaudeEntrypoint = entrypoint;
                ClaudePromptSource = promptSource;
            }
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
        UsageByModel = usage.ToRollups();

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

        // Automated background runs (cockpit orderlies + the morning brief) — exclude from the index. They're
        // machine-driven and high-volume, and would bury the user's real sessions in browse + search.
        if (Turns.Count > 0)
        {
            if (IsCodex)
            {
                // source.subagent covers controlled guardians and spawned workers even though they can share
                // the driver's "codex-tui" originator. The originator additionally catches script/plugin runs
                // that have no interactive primary session. Prose checks remain as a fallback for old metadata.
                if (IsExcludedCodexMetadata(CodexOriginator, IsCodexSubagent))
                    IsExcludedAgentRun = true;

                var first = Turns[0].UserText ?? "";
                if (first.Contains("cos-aide morning-brief brain", StringComparison.OrdinalIgnoreCase) ||
                    first.Contains("aide-de-camp orderly", StringComparison.OrdinalIgnoreCase))
                    IsExcludedAgentRun = true;
            }
            else
            {
                // Headless Agent SDK runs (fusion workers, gate reviewers, harness scripts…) carry structural
                // launch metadata since ~2.1.x. Prefix-match the entrypoint so future sdk-* variants are also
                // caught. The prose checks below remain for older transcripts and cli-launched automation.
                if (IsExcludedClaudeMetadata(ClaudeEntrypoint, ClaudePromptSource))
                    IsExcludedAgentRun = true;

                // The poke prefixes the run with a /clear (each of /clear and its local-command-caveat is its
                // own turn), so the real entrypoint command lands a few turns in — caveat, /clear, caveat,
                // /orderly ≈ turn 3. Scan the opening turns; a real session never *starts a turn* with these.
                int scan = Math.Min(Turns.Count, 6);
                bool loopSeen = false, briefRefSeen = false;
                for (int i = 0; i < scan && !IsExcludedAgentRun; i++)
                {
                    var t = (Turns[i].UserText ?? "").TrimStart();
                    // Direct invocations: an orderly poke (/orderly) or the brief command (/morning-brief).
                    if (t.StartsWith("/orderly", StringComparison.OrdinalIgnoreCase) ||
                        t.StartsWith("/morning-brief", StringComparison.OrdinalIgnoreCase))
                    {
                        IsExcludedAgentRun = true;
                        break;
                    }
                    // The brief is also started as `/loop /morning-brief`: the /loop command and the scheduled
                    // /morning-brief land in SEPARATE opening turns, so track both across the scan window.
                    if (t.StartsWith("/loop", StringComparison.OrdinalIgnoreCase)) loopSeen = true;
                    if (t.Contains("/morning-brief", StringComparison.OrdinalIgnoreCase)) briefRefSeen = true;
                    if (loopSeen && briefRefSeen) { IsExcludedAgentRun = true; break; }
                }
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

    /// <summary>
    /// Cheap header-only exclusion check used before the incremental cursor short-circuit. Without this,
    /// controlled sessions indexed by an older Santa build would never be revisited and pruned unless their
    /// rollout file changed or the user forced a full ingest.
    /// </summary>
    public static bool ShouldExcludeCodexFile(string filePath)
    {
        foreach (var (_, _, ev) in new CodexJsonlReader(filePath).Read())
        {
            if (ev.Type != "session_meta") continue;
            ReadCodexMetadata(ev.Raw, out var originator, out var isSubagent);
            return IsExcludedCodexMetadata(originator, isSubagent);
        }

        return false;
    }

    /// <summary>
    /// Cheap header-only exclusion check for Claude transcripts, the counterpart of
    /// <see cref="ShouldExcludeCodexFile"/>: runs before the incremental cursor short-circuit so
    /// sdk-driven sessions indexed by an older Santa build get pruned even though their files never
    /// change again. Scans only the opening user events — transcripts predating the metadata fields
    /// would otherwise force a full-file read on every refresh.
    /// </summary>
    public static bool ShouldExcludeClaudeFile(string filePath)
    {
        int userEventsSeen = 0;
        foreach (var (_, _, ev) in new JsonlReader(filePath).Read())
        {
            if (!ev.IsUser) continue;
            ReadClaudeMetadata(ev.Raw, out var entrypoint, out var promptSource);
            if (entrypoint is not null || promptSource is not null)
                return IsExcludedClaudeMetadata(entrypoint, promptSource);
            if (++userEventsSeen >= 5) break;
        }

        return false;
    }

    private static void ReadCodexMetadata(JsonElement raw, out string? originator, out bool isSubagent)
    {
        originator = null;
        isSubagent = false;
        if (raw.ValueKind != JsonValueKind.Object ||
            !raw.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            return;

        if (payload.TryGetProperty("originator", out var orig) && orig.ValueKind == JsonValueKind.String)
            originator = orig.GetString();

        isSubagent = payload.TryGetProperty("source", out var source) &&
                     source.ValueKind == JsonValueKind.Object &&
                     source.TryGetProperty("subagent", out _);
    }

    private static bool IsExcludedCodexMetadata(string? originator, bool isSubagent) =>
        isSubagent ||
        string.Equals(originator, "codex_exec", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(originator, "Claude Code", StringComparison.OrdinalIgnoreCase);

    private static void ReadClaudeMetadata(JsonElement raw, out string? entrypoint, out string? promptSource)
    {
        entrypoint = null;
        promptSource = null;
        if (raw.ValueKind != JsonValueKind.Object) return;

        if (raw.TryGetProperty("entrypoint", out var ep) && ep.ValueKind == JsonValueKind.String)
            entrypoint = ep.GetString();

        if (raw.TryGetProperty("promptSource", out var ps) && ps.ValueKind == JsonValueKind.String)
            promptSource = ps.GetString();
    }

    private static bool IsExcludedClaudeMetadata(string? entrypoint, string? promptSource) =>
        (entrypoint is not null && entrypoint.StartsWith("sdk", StringComparison.OrdinalIgnoreCase)) ||
        string.Equals(promptSource, "sdk", StringComparison.OrdinalIgnoreCase);
}
