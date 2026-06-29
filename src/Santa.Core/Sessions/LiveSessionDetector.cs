using System.Text;

namespace Santa.Core.Sessions;

/// <summary>
/// Detects which Claude Code sessions are currently open in some terminal.
///
/// Strategy (Linux/WSL only — relies on /proc):
///   1. Walk /proc/&lt;pid&gt;/cmdline for processes whose argv[0] basename is exactly "claude".
///   2. If the cmdline contains <c>--resume &lt;uuid&gt;</c> (or <c>-r &lt;uuid&gt;</c>), take that id.
///   3. Otherwise, read /proc/&lt;pid&gt;/cwd and map it to its ~/.claude/projects/&lt;encoded&gt;/ dir,
///      then pick the .jsonl file with the most recent mtime — that's the file Claude Code is
///      appending to for this terminal.
///
/// Claude Code does not hold the .jsonl open between turns, so lsof is no help; mtime-of-latest
/// in the matching project dir is the cleanest correlate of "this terminal's session".
/// </summary>
public static class LiveSessionDetector
{
    /// <summary>
    /// Returns the set of Claude Code session ids that appear to be live in some terminal right now.
    /// Cheap (~ms): a few /proc reads and one Directory.EnumerateFiles per matching dir.
    /// Returns an empty set on non-Linux platforms or if /proc is unavailable.
    /// </summary>
    public static IReadOnlySet<string> DetectLiveSessionIds()
    {
        var result = new HashSet<string>();
        if (!Directory.Exists("/proc")) return result;

        var projectsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects");
        var projectsExists = Directory.Exists(projectsRoot);

        foreach (var pidDir in Directory.EnumerateDirectories("/proc"))
        {
            var pidName = Path.GetFileName(pidDir);
            if (!int.TryParse(pidName, out _)) continue;

            string[]? argv = TryReadCmdline(pidDir);
            if (argv is null || argv.Length == 0) continue;

            // Match the `claude` binary specifically — avoids picking up santa or sibling
            // forks, helper shells with "claude" in argv[0], etc.
            var exe = Path.GetFileName(argv[0]);
            if (exe != "claude") continue;

            // Direct id from --resume / -r <uuid>
            for (int i = 1; i < argv.Length - 1; i++)
            {
                if ((argv[i] == "--resume" || argv[i] == "-r") && LooksLikeSessionId(argv[i + 1]))
                {
                    result.Add(argv[i + 1]);
                    break;
                }
            }

            if (!projectsExists) continue;

            // Fallback / cross-check: derive id from cwd → project dir → most recent jsonl
            var cwd = TryReadCwd(pidDir);
            if (cwd is null) continue;

            var encoded = EncodeCwdForProjectDir(cwd);
            var projectDir = Path.Combine(projectsRoot, encoded);
            if (!Directory.Exists(projectDir)) continue;

            var liveId = MostRecentJsonlSessionId(projectDir);
            if (liveId is not null) result.Add(liveId);
        }

        return result;
    }

    private static string[]? TryReadCmdline(string pidDir)
    {
        try
        {
            var raw = File.ReadAllText(Path.Combine(pidDir, "cmdline"));
            if (string.IsNullOrEmpty(raw)) return null;
            return raw.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        catch { return null; }
    }

    private static string? TryReadCwd(string pidDir)
    {
        try
        {
            var link = File.ResolveLinkTarget(Path.Combine(pidDir, "cwd"), returnFinalTarget: false);
            return link?.FullName;
        }
        catch { return null; }
    }

    /// <summary>
    /// Mirrors Claude Code's project-dir naming: every char that isn't [A-Za-z0-9-] becomes '-'.
    /// E.g. "/home/you/repos/santa" → "-home-you-repos-santa".
    /// </summary>
    private static string EncodeCwdForProjectDir(string cwd)
    {
        var sb = new StringBuilder(cwd.Length);
        foreach (var c in cwd)
            sb.Append(char.IsLetterOrDigit(c) || c == '-' ? c : '-');
        return sb.ToString();
    }

    private static string? MostRecentJsonlSessionId(string projectDir)
    {
        string? bestId = null;
        DateTime bestMtime = DateTime.MinValue;
        foreach (var file in Directory.EnumerateFiles(projectDir, "*.jsonl"))
        {
            var mt = File.GetLastWriteTimeUtc(file);
            if (mt > bestMtime)
            {
                bestMtime = mt;
                bestId = Path.GetFileNameWithoutExtension(file);
            }
        }
        return bestId;
    }

    private static bool LooksLikeSessionId(string s) =>
        s.Length == 36 && s[8] == '-' && s[13] == '-' && s[18] == '-' && s[23] == '-';
}
