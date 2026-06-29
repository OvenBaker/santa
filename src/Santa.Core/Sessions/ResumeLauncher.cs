using System.Diagnostics;

namespace Santa.Core.Sessions;

public sealed record ResumePlan(string Cwd, string SessionId, string CommandLine, string DistroName, string Script);

public static class ResumeLauncher
{
    // When set, resume hands the session off to this command instead of opening a
    // Windows Terminal tab. Invoked as:  <cmd> <sessionId> --cwd <cwd>
    // (e.g. cockpit's `cockpit-send`, which resumes the session as a pane in the
    // grid). Still wrapped in `setsid -f` so it never holds the TUI's TTY.
    private const string OverrideEnv = "SANTA_RESUME_CMD";
    private const string OverrideLabelEnv = "SANTA_RESUME_LABEL";

    /// <summary>True when SANTA_RESUME_CMD is set (resume hands off instead of opening wt.exe).</summary>
    public static bool OverrideActive => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OverrideEnv));

    /// <summary>Short label for where an overridden resume goes (default "handoff"); for UI hints.</summary>
    public static string OverrideLabel
    {
        get
        {
            var l = Environment.GetEnvironmentVariable(OverrideLabelEnv);
            return string.IsNullOrWhiteSpace(l) ? "handoff" : l;
        }
    }

    /// <summary>
    /// Build the launcher invocation but don't execute. Useful for --dry-run / unit testing.
    /// Pass useOverride: false to force the default new-wt.exe-tab launcher even when
    /// SANTA_RESUME_CMD is set (so the UI can offer "resume to a new terminal as before").
    /// </summary>
    public static ResumePlan Plan(SessionInfo session, string? cwdOverride = null, bool useOverride = true)
    {
        var cwd = cwdOverride ?? session.Cwd
            ?? throw new InvalidOperationException("Session has no recorded cwd; pass --cwd to override.");
        if (!Directory.Exists(cwd))
            throw new DirectoryNotFoundException($"Session cwd does not exist: {cwd}");

        var distro = Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") ?? "";
        var script = BuildScript(cwd, session.Id, distro, useOverride, session.Provider);
        var cmd = $"bash -c {ShellQuote(script)}";
        return new ResumePlan(cwd, session.Id, cmd, distro, script);
    }

    public static void Launch(ResumePlan plan)
    {
        // Run via bash + setsid + /dev/null redirection so the child is fully detached from our TTY.
        // Without this, wt.exe inherits stdio and blocks the parent's Console.ReadKey() forever —
        // which manifests as the TUI hanging until Ctrl+C. Only the bash launcher is our child;
        // setsid moves the child into its own session, so any handle inheritance is severed.
        // Use the script Plan already built, so Launch honours whatever Plan decided
        // (override → handoff command, or forced default → new wt.exe tab).
        var script = plan.Script;

        var psi = new ProcessStartInfo("bash")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start launcher.");
        proc.StandardInput.Close();
        proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit();
    }

    // The detached shell script that resumes the session — either a custom handoff
    // command (SANTA_RESUME_CMD) or the default new-wt.exe-tab launcher.
    private static string BuildScript(string cwd, string sessionId, string distro, bool useOverride, string provider)
    {
        var custom = Environment.GetEnvironmentVariable(OverrideEnv);
        if (useOverride && !string.IsNullOrWhiteSpace(custom))
        {
            // Pass the provider through so the handoff target (e.g. cockpit-send) can resume
            // with the right agent without re-detecting. Older handoff scripts ignore extra args.
            var handoff = $"{ShellQuote(custom)} {ShellQuote(sessionId)} --cwd {ShellQuote(cwd)} --agent {ShellQuote(ProviderToAgent(provider))}";
            return $"setsid -f {handoff} </dev/null >/dev/null 2>&1";
        }

        var distroFlag = string.IsNullOrEmpty(distro) ? "" : $" -d {distro}";
        // codex resumes by uuid; claude resumes by session id. cwd is set first either way.
        var resume = provider == "codex"
            ? $"codex resume {sessionId}"
            : $"claude --resume {sessionId}";
        var inner = $"cd {ShellQuote(cwd)} && {resume}";
        return $"setsid -f wt.exe -w new wsl.exe{distroFlag} bash -ilc {ShellQuote(inner)} </dev/null >/dev/null 2>&1";
    }

    /// <summary>Maps the stored provider id to the short agent token cockpit uses ("claude"|"codex").</summary>
    private static string ProviderToAgent(string provider) => provider == "codex" ? "codex" : "claude";

    private static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";
}
