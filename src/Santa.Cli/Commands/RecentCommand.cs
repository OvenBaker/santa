using System.ComponentModel;
using System.Diagnostics;
using Santa.Cli.Theming;
using Santa.Core.Classify;
using Santa.Core.Sessions;
using Santa.Core.Settings;
using Santa.Core.Storage;
using Santa.Core.Summarize;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Santa.Cli.Commands;

/// <summary>
/// Activity report for a date range, driven entirely off the index. Replaces the
/// re-summarise-each-time pipeline of the legacy /recent-work skill: per-session summaries
/// are already pre-computed and refreshed hourly by the cron, so the report is a single
/// SQL query + grouping + optional cross-cutting synthesis call.
/// </summary>
public sealed class RecentCommand : AsyncCommand<RecentCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[RANGE]")]
        [Description("yesterday | today | last week | since monday | 2026-04-13 | 2026-04-10..2026-04-13 | new (advances marker). Default: new — i.e. since the last invocation.")]
        public string? Range { get; init; }

        [CommandOption("--synthesize")]
        [Description("Feed all summary_shorts to claude -p (Max plan) for a cross-cutting themed synthesis.")]
        public bool Synthesize { get; init; }

        [CommandOption("--max <N>")]
        [DefaultValue(80)]
        public int Max { get; init; }

        [CommandOption("--db <PATH>")]
        public string? Db { get; init; }
    }

    private static readonly string MarkerPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     ".claude", ".recent-work-last-run");

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        DateTimeOffset rangeStart, rangeEnd;
        bool advanceMarker = false;
        try
        {
            (rangeStart, rangeEnd, advanceMarker) = ParseRange(s.Range);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]bad range:[/] {Markup.Escape(ex.Message)}");
            return 2;
        }

        var theme = Theme.ByName(SantaSettings.Load().Theme);

        using var db = Database.Open(s.Db ?? Database.DefaultPath);
        var rows = LoadInRange(db, rangeStart, rangeEnd, s.Max);

        AnsiConsole.MarkupLineInterpolated(
            $"[{theme.Title}]🎅 recent work[/]   [{theme.Dim}]{rangeStart:yyyy-MM-dd HH:mm} → {rangeEnd:yyyy-MM-dd HH:mm}[/]");
        AnsiConsole.WriteLine();

        if (rows.Count == 0)
        {
            AnsiConsole.MarkupLineInterpolated($"[{theme.Dim}](no sessions in range)[/]");
            if (advanceMarker) WriteMarker(rangeEnd);
            return 0;
        }

        RenderGrouped(rows, theme);

        if (s.Synthesize)
        {
            try
            {
                Summarizer.EnsureSubscriptionMode();
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[{theme.Dim}]synthesising themes via claude -p…[/]");
                var synthesis = await SynthesiseAsync(rows, rangeStart, rangeEnd, ct);
                AnsiConsole.WriteLine();
                AnsiConsole.WriteLine(synthesis ?? "(no synthesis returned)");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[{theme.Err}]synthesis failed:[/] {Markup.Escape(ex.Message)}");
            }
        }

        if (advanceMarker) WriteMarker(rangeEnd);
        return 0;
    }

    // ── range parsing ───────────────────────────────────────────────────
    private static (DateTimeOffset start, DateTimeOffset end, bool advanceMarker)
        ParseRange(string? raw)
    {
        var arg = (raw ?? "").Trim().ToLowerInvariant();
        var now = DateTimeOffset.Now;

        // Default: since the last successful run (falls back to 24h ago on first use).
        if (string.IsNullOrEmpty(arg)) arg = "new";

        if (arg == "new")
        {
            var since = ReadMarker() ?? now.AddDays(-1);
            return (since, now, advanceMarker: true);
        }
        if (arg == "today")
        {
            var t = new DateTimeOffset(now.Date, now.Offset);
            return (t, t.AddDays(1).AddTicks(-1), false);
        }
        if (arg == "yesterday")
        {
            var y = new DateTimeOffset(now.Date.AddDays(-1), now.Offset);
            return (y, y.AddDays(1).AddTicks(-1), false);
        }
        if (arg == "last week")
        {
            return (now.AddDays(-7), now, false);
        }
        if (arg.StartsWith("since "))
        {
            return (ResolveNaturalDate(arg[6..]), now, false);
        }
        if (arg.Contains(".."))
        {
            var parts = arg.Split("..", 2);
            var s = ResolveNaturalDate(parts[0].Trim());
            var e = ResolveNaturalDate(parts[1].Trim());
            // If end is a date (no time component), bump to end-of-day.
            if (e.TimeOfDay == TimeSpan.Zero) e = e.AddDays(1).AddTicks(-1);
            return (s, e, false);
        }
        // single date / natural-language
        var d = ResolveNaturalDate(arg);
        return (d, d.AddDays(1).AddTicks(-1), false);
    }

    /// <summary>Parses ISO-ish first; falls back to GNU `date -d` for everything else.</summary>
    private static DateTimeOffset ResolveNaturalDate(string expr)
    {
        if (DateTimeOffset.TryParse(expr, out var iso)) return iso;

        var psi = new ProcessStartInfo("date", new[] { "-d", expr, "+%Y-%m-%dT%H:%M:%S%z" })
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("could not spawn `date`");
        var output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        if (p.ExitCode != 0 || string.IsNullOrEmpty(output))
            throw new ArgumentException($"unrecognised date expression: {expr}");
        return DateTimeOffset.Parse(output);
    }

    private static DateTimeOffset? ReadMarker()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return null;
            var s = File.ReadAllText(MarkerPath).Trim();
            return DateTimeOffset.TryParse(s, out var dt) ? dt : null;
        }
        catch { return null; }
    }

    private static void WriteMarker(DateTimeOffset end)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            var tmp = MarkerPath + ".tmp";
            File.WriteAllText(tmp, end.ToString("O"));
            File.Move(tmp, MarkerPath, overwrite: true);
        }
        catch { /* best-effort */ }
    }

    // ── data ────────────────────────────────────────────────────────────
    private record Row(string Id, string ProjectPath, string? Cwd, string? GitBranch,
        DateTimeOffset? StartedAt, DateTimeOffset? LastActiveAt, int TurnCount, string Status,
        string? Title, string? Short);

    private static List<Row> LoadInRange(Database db, DateTimeOffset start, DateTimeOffset end, int max)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, project_path, cwd, git_branch, started_at, last_active_at, turn_count, status,
                   summary_title, summary_short
              FROM sessions
             WHERE COALESCE(last_active_at, started_at) BETWEEN $s AND $e
             ORDER BY COALESCE(last_active_at, started_at) ASC
             LIMIT $lim
            """;
        cmd.Parameters.AddWithValue("$s", start.ToString("O"));
        cmd.Parameters.AddWithValue("$e", end.ToString("O"));
        cmd.Parameters.AddWithValue("$lim", max);
        using var rd = cmd.ExecuteReader();
        var list = new List<Row>();
        while (rd.Read())
        {
            list.Add(new Row(
                rd.GetString(0),
                rd.GetString(1),
                rd.IsDBNull(2) ? null : rd.GetString(2),
                rd.IsDBNull(3) ? null : rd.GetString(3),
                rd.IsDBNull(4) ? null : DateTimeOffset.Parse(rd.GetString(4)),
                rd.IsDBNull(5) ? null : DateTimeOffset.Parse(rd.GetString(5)),
                rd.GetInt32(6),
                rd.GetString(7),
                rd.IsDBNull(8) ? null : rd.GetString(8),
                rd.IsDBNull(9) ? null : rd.GetString(9)));
        }
        return list;
    }

    // ── rendering ───────────────────────────────────────────────────────
    private static void RenderGrouped(List<Row> rows, Theme t)
    {
        var groups = rows
            .GroupBy(r => CleanProjectKey(r.Cwd, r.ProjectPath))
            .OrderByDescending(g => g.Sum(r => r.TurnCount))
            .ToList();

        foreach (var g in groups)
        {
            var totalTurns = g.Sum(r => r.TurnCount);
            var span = DurationFormat.Compact(g.Min(r => r.StartedAt), g.Max(r => r.LastActiveAt ?? r.StartedAt));
            var spanLabel = string.IsNullOrEmpty(span) ? "" : $", {span}";
            AnsiConsole.MarkupLineInterpolated(
                $"[{t.Path}]{Markup.Escape(g.Key)}[/]   [{t.Dim}]({g.Count()} sessions, {totalTurns} turns{spanLabel})[/]");

            foreach (var r in g.OrderBy(r => r.LastActiveAt ?? r.StartedAt))
            {
                var when = (r.LastActiveAt ?? r.StartedAt)?.ToLocalTime().ToString("MM-dd HH:mm") ?? "          ";
                var statusIcon = r.Status switch
                {
                    "completed" => $"[{t.Ok}]✓[/]",
                    "archived"  => $"[{t.Dim}]·[/]",
                    _ => " "
                };
                var title = r.Title ?? r.Short ?? "(no summary)";
                AnsiConsole.MarkupLineInterpolated(
                    $"  [{t.Dim}]{when}[/]  {statusIcon}  [{t.Title}]{Markup.Escape(Trim(title, 80))}[/]   [{t.Branch}]{Markup.Escape(r.GitBranch ?? "-")}[/]");
            }
            AnsiConsole.WriteLine();
        }

        AnsiConsole.MarkupLineInterpolated(
            $"[{t.Dim}]{rows.Count} sessions across {groups.Count} project(s)[/]");
    }

    /// <summary>Strip the trailing per-session worktree to keep all of a repo's worktrees grouped.</summary>
    private static string CleanProjectKey(string? cwd, string projectPath)
    {
        if (!string.IsNullOrEmpty(cwd))
        {
            // /home/you/repos/myrepo/master → myrepo/master
            // /home/you/workspaces/foo → foo
            var parts = cwd.TrimEnd('/').Split('/');
            if (parts.Length >= 2) return string.Join('/', parts.TakeLast(2));
        }
        return projectPath;
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    // ── synthesis ───────────────────────────────────────────────────────
    private static async Task<string?> SynthesiseAsync(
        List<Row> rows, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        var prompt = BuildSynthPrompt(rows, start, end);
        var req = new ClaudeRunRequest(
            Prompt: prompt,
            Model: "haiku",
            AllowedTools: Array.Empty<string>(),
            Timeout: TimeSpan.FromMinutes(2));
        var run = await ClaudeCliRunner.RunAsync(req, ct);
        if (run.ExitCode != 0)
            throw new InvalidOperationException($"claude -p exited {run.ExitCode}: {run.Stderr.Trim()}");
        return run.ResultText;
    }

    private static string BuildSynthPrompt(List<Row> rows, DateTimeOffset start, DateTimeOffset end)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"Activity report for {start:yyyy-MM-dd HH:mm} to {end:yyyy-MM-dd HH:mm}.\n\n");
        sb.Append("Per-session summaries from a Claude Code work index:\n\n");
        foreach (var r in rows.OrderBy(r => r.LastActiveAt ?? r.StartedAt))
        {
            var when = (r.LastActiveAt ?? r.StartedAt)?.ToString("MM-dd HH:mm") ?? "?";
            var title = r.Title ?? "(no title)";
            var shortSum = r.Short ?? "";
            sb.Append($"- [{when}] {r.Cwd ?? r.ProjectPath} ({r.GitBranch ?? "-"}, {r.TurnCount}t): {title}");
            if (!string.IsNullOrEmpty(shortSum)) sb.Append("\n    ").Append(shortSum);
            sb.Append('\n');
        }
        sb.Append(
            "\nWrite a 2–4 paragraph synthesis grouped by theme, not by project. " +
            "Identify the dominant lines of work, decisions made, things that landed, " +
            "and anything that's still open or in progress. Be concrete; cite specific " +
            "session subjects when relevant. No preamble, no list of bullets — flowing paragraphs.");
        return sb.ToString();
    }
}
