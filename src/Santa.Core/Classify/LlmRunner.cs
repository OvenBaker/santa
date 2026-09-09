namespace Santa.Core.Classify;

/// <summary>
/// Routes a text-generation request to either <c>claude -p</c> or <c>codex exec</c>.
///
/// Backend is chosen by <c>SANTA_SUMMARIZER</c> (<c>codex</c> | <c>claude</c>, default
/// <c>codex</c> - currently faster and keeps load off the Claude API). The escape hatch is
/// <c>SANTA_SUMMARIZER=claude</c>.
///
/// Agentic requests (<see cref="ClaudeRunRequest.AllowedTools"/> non-empty — i.e. Agent-mode
/// classification recipes that actually run tools) ALWAYS go to Claude: codex-as-agent is a
/// different execution model (sandbox, approvals) and isn't a drop-in there. Only tool-less
/// completions — every summary, and non-agent classification — are eligible for Codex.
/// </summary>
public static class LlmRunner
{
    public static bool CodexSelected =>
        (Environment.GetEnvironmentVariable("SANTA_SUMMARIZER") is { Length: > 0 } v ? v : "codex")
        .Trim().Equals("codex", StringComparison.OrdinalIgnoreCase);

    /// <summary>The backend model id that will actually run this request (for DB records/logs).</summary>
    public static string ModelFor(ClaudeRunRequest req) =>
        (CodexSelected && req.AllowedTools.Count == 0)
            ? CodexCliRunner.Model
            : ClaudeCliRunner.ModelFor(req);

    public static Task<ClaudeRunResult> RunAsync(ClaudeRunRequest req, CancellationToken ct = default)
        => (CodexSelected && req.AllowedTools.Count == 0)
            ? CodexCliRunner.RunAsync(req, ct)
            : ClaudeCliRunner.RunAsync(req, ct);
}
