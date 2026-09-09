using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Santa.Core.Classify;

public sealed record ClaudeRunRequest(
    string Prompt,
    string Model,                          // haiku | sonnet | opus
    IReadOnlyList<string> AllowedTools,    // e.g. ["Bash(git:*)", "Bash(gh:*)", "Read"]
    string? PermissionMode = null,         // null | "acceptEdits" | "bypassPermissions"
    string? WorkingDirectory = null,
    TimeSpan? Timeout = null);

public sealed record ClaudeRunResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    string? ResultText,                    // parsed from JSON envelope
    string? CostUsd,                       // ditto
    JsonElement? Envelope);

/// <summary>
/// Shells out to <c>claude -p --output-format json …</c>. The resulting envelope shape is
/// <c>{ "type":"result", "result":"…", "total_cost_usd": …, … }</c> on success.
/// Every prompt is silently prefixed with <see cref="InternalSentinel"/> so the JSONL
/// the CLI writes to <c>~/.claude/projects/</c> can be detected and skipped at ingest time.
/// </summary>
public static class ClaudeCliRunner
{
    /// <summary>
    /// First-line marker prepended to every santa prompt sent through claude -p.
    /// Lets ingest filter out the phantom sessions claude-p creates as a side effect.
    /// </summary>
    public const string InternalSentinel = "<<santa-claude-internal>>";

    /// <summary>
    /// Code-owned default model for this router — model slug <c>claude-haiku-4-5</c>.
    /// It exists so <c>--model</c> is ALWAYS passed: a caller that supplies no model used
    /// to fall through to whatever default the `claude` CLI happens to ship, which is a
    /// model nobody chose and which can change under us without a commit or a review.
    /// Callers that do name a model still win — per-request choice is the point of the
    /// router; this is only the floor under it.
    /// </summary>
    public const string DefaultModel = "claude-haiku-4-5";

    /// <summary>
    /// The model used when a caller names none. <c>SANTA_CLAUDE_MODEL</c> overrides
    /// <see cref="DefaultModel"/>, mirroring <see cref="CodexCliRunner.Model"/>.
    /// </summary>
    public static string Model =>
        Environment.GetEnvironmentVariable("SANTA_CLAUDE_MODEL") is { Length: > 0 } m ? m : DefaultModel;

    /// <summary>
    /// The model that will actually be passed to <c>--model</c>: the caller's choice when it
    /// named one, otherwise <see cref="Model"/>. Never empty.
    /// </summary>
    public static string Resolve(string? model) =>
        string.IsNullOrWhiteSpace(model) ? Model : model;

    /// <summary>The model that will actually be passed to <c>--model</c> for this request.</summary>
    public static string ModelFor(ClaudeRunRequest req) => Resolve(req.Model);

    public static async Task<ClaudeRunResult> RunAsync(ClaudeRunRequest req, CancellationToken ct = default)
    {
        var prompt = InternalSentinel + "\n" + req.Prompt;
        // --no-session-persistence (print-mode only): don't write a transcript to
        // ~/.claude/projects. These internal runs are read from stdout, never resumed,
        // and otherwise flood the session store (hundreds of phantom transcripts that
        // ingest then has to filter and cockpit's candidate scan has to skip). The
        // InternalSentinel prefix stays as belt-and-suspenders for already-persisted ones.
        var args = new List<string> { "-p", prompt, "--output-format", "json", "--no-session-persistence" };
        // Always explicit: never let an empty caller value inherit the CLI's own default.
        args.AddRange(new[] { "--model", ModelFor(req) });
        if (req.AllowedTools.Count > 0)
            args.AddRange(new[] { "--allowed-tools", string.Join(" ", req.AllowedTools) });
        if (!string.IsNullOrEmpty(req.PermissionMode))
            args.AddRange(new[] { "--permission-mode", req.PermissionMode });

        var psi = new ProcessStartInfo("claude")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = req.WorkingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to launch `claude`.");

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        if (req.Timeout is { } t)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(t);
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                throw new TimeoutException($"`claude -p` timed out after {t}.");
            }
        }
        else
        {
            await proc.WaitForExitAsync(ct);
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        string? resultText = null;
        string? costUsd = null;
        JsonElement? envelope = null;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            envelope = doc.RootElement.Clone();
            if (envelope.Value.TryGetProperty("result", out var rEl) && rEl.ValueKind == JsonValueKind.String)
                resultText = rEl.GetString();
            if (envelope.Value.TryGetProperty("total_cost_usd", out var cEl))
                costUsd = cEl.ToString();
        }
        catch (JsonException) { }

        return new ClaudeRunResult(proc.ExitCode, stdout, stderr, resultText, costUsd, envelope);
    }
}
