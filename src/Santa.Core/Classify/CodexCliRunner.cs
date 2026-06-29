using System.Diagnostics;

namespace Santa.Core.Classify;

/// <summary>
/// Drop-in alternative to <see cref="ClaudeCliRunner"/> for pure text-generation tasks
/// (session summaries, non-agent classification). Shells out to
/// <c>codex exec --ephemeral …</c>, which runs the GPT-5.x family via the user's ChatGPT
/// auth — useful when the Claude API is overloaded.
///
/// Reuses the <see cref="ClaudeRunRequest"/>/<see cref="ClaudeRunResult"/> shape so the
/// call sites don't care which backend ran. <c>req.Model</c> (a Claude name like "haiku")
/// is ignored; the Codex model comes from <c>SANTA_CODEX_MODEL</c> (default gpt-5.5 — the
/// only general model offered on a ChatGPT account; gpt-5.5-mini/-fast are not supported).
///
/// <c>--ephemeral</c> is load-bearing: it stops Codex writing a rollout to
/// <c>~/.codex/sessions</c>, which santa would otherwise re-ingest as a phantom session
/// (the Codex analogue of the claude-p phantom problem). The clean final message is read
/// from the <c>-o</c> file, so reasoning/event noise on stdout is never parsed.
/// </summary>
public static class CodexCliRunner
{
    public static string Model =>
        Environment.GetEnvironmentVariable("SANTA_CODEX_MODEL") is { Length: > 0 } m ? m : "gpt-5.5";

    public static async Task<ClaudeRunResult> RunAsync(ClaudeRunRequest req, CancellationToken ct = default)
    {
        var outFile = Path.Combine(Path.GetTempPath(), $"santa-codex-{Guid.NewGuid():N}.txt");
        var args = new List<string>
        {
            "exec",
            "--ephemeral",              // don't persist a rollout (else santa re-ingests it)
            "-m", Model,
            "-s", "read-only",          // summaries/classification never need to write
            "--skip-git-repo-check",    // runs fine outside a git repo
            "--color", "never",
            "-o", outFile,              // clean final message → file
            "-",                        // prompt is read from stdin
        };

        var psi = new ProcessStartInfo("codex")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = req.WorkingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to launch `codex`.");

        // Feed the prompt on stdin (avoids arg-length limits on large transcripts).
        await proc.StandardInput.WriteAsync(req.Prompt.AsMemory(), ct);
        proc.StandardInput.Close();

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
                try { File.Delete(outFile); } catch { }
                throw new TimeoutException($"`codex exec` timed out after {t}.");
            }
        }
        else
        {
            await proc.WaitForExitAsync(ct);
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        string? resultText = null;
        try { if (File.Exists(outFile)) resultText = (await File.ReadAllTextAsync(outFile, ct)).Trim(); }
        catch { /* fall through to stdout */ }
        finally { try { File.Delete(outFile); } catch { } }

        if (string.IsNullOrWhiteSpace(resultText) && !string.IsNullOrWhiteSpace(stdout))
            resultText = stdout.Trim();

        return new ClaudeRunResult(proc.ExitCode, stdout, stderr, resultText, CostUsd: null, Envelope: null);
    }
}
