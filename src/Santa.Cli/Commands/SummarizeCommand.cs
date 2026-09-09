using System.ComponentModel;
using Santa.Core.Classify;
using Santa.Core.Sessions;
using Santa.Core.Storage;
using Santa.Core.Summarize;
using Microsoft.Data.Sqlite;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Santa.Cli.Commands;

public sealed class SummarizeCommand : AsyncCommand<SummarizeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[SESSION_ID]")]
        [Description("Single session id (prefix). Omit when using --all / --all-active / --since.")]
        public string? SessionId { get; init; }

        [CommandOption("--all")]
        [Description("All sessions regardless of status (active, completed, archived). Pairs with --missing-only.")]
        public bool All { get; init; }

        [CommandOption("--all-active")]
        [Description("Only sessions with status='active'.")]
        public bool AllActive { get; init; }

        [CommandOption("--since <DUR>")]
        public string? Since { get; init; }

        [CommandOption("--max <N>")]
        [DefaultValue(500)]
        public int Max { get; init; }

        [CommandOption("--missing-only")]
        [Description("Skip sessions whose summary is already up to date with current turn_count.")]
        [DefaultValue(true)]
        public bool MissingOnly { get; init; }

        [CommandOption("--stale-only")]
        [Description("Skip sessions that have been active in the last 30 minutes (avoids re-summarising threads still in progress).")]
        public bool StaleOnly { get; init; }

        [CommandOption("--model <NAME>")]
        [DefaultValue("haiku")]
        public string Model { get; init; } = "haiku";

        [CommandOption("--concurrency <N>")]
        [DefaultValue(4)]
        public int Concurrency { get; init; }

        [CommandOption("--dry-run")]
        public bool DryRun { get; init; }

        [CommandOption("--db <PATH>")]
        public string? Db { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        // ANTHROPIC_API_KEY guard only applies to the claude -p backend; codex uses ChatGPT auth.
        if (!LlmRunner.CodexSelected)
        {
            try { Summarizer.EnsureSubscriptionMode(); }
            catch (InvalidOperationException ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]{Markup.Escape(ex.Message)}[/]");
                return 4;
            }
        }

        using var db = Database.Open(s.Db ?? Database.DefaultPath);
        var summarizer = new Summarizer(db, new SummarizerOptions { Model = s.Model });

        var sessions = SelectSessions(db, s);
        if (s.MissingOnly)
            sessions = sessions.Where(x => summarizer.NeedsSummary(x.Id, x.TurnCount)).ToList();

        var batch = sessions.Take(s.Max).ToList();
        var backend = LlmRunner.CodexSelected
            ? $"codex/{CodexCliRunner.Model}"
            : ClaudeCliRunner.Resolve(s.Model);
        AnsiConsole.MarkupLineInterpolated(
            $"summarising [bold]{batch.Count}[/] session(s) with [cyan]{backend}[/] · concurrency={s.Concurrency}");

        int done = 0, ok = 0, failed = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sem = new SemaphoreSlim(Math.Max(1, s.Concurrency));
        var lockObj = new object();

        var tasks = batch.Select(async sess =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var preview = (sess.FirstUserText ?? "").Replace('\n', ' ');
                if (preview.Length > 70) preview = preview[..70] + "…";
                try
                {
                    var summary = await summarizer.SummariseAsync(sess, s.DryRun, _ => { }, ct);
                    lock (lockObj)
                    {
                        done++;
                        if (summary is not null)
                        {
                            ok++;
                            AnsiConsole.MarkupLineInterpolated(
                                $"[grey]{done,3}/{batch.Count}[/] [bold]{sess.Id[..8]}[/]  {Markup.Escape(preview)}");
                            AnsiConsole.MarkupLineInterpolated($"     → [green]{Markup.Escape(summary.Title)}[/]");
                        }
                        else if (!s.DryRun)
                        {
                            failed++;
                            AnsiConsole.MarkupLineInterpolated(
                                $"[grey]{done,3}/{batch.Count}[/] [bold]{sess.Id[..8]}[/]  [yellow]couldn't parse JSON[/]");
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (lockObj)
                    {
                        done++; failed++;
                        AnsiConsole.MarkupLineInterpolated(
                            $"[grey]{done,3}/{batch.Count}[/] [bold]{sess.Id[..8]}[/]  [red]error:[/] {Markup.Escape(ex.Message)}");
                    }
                }
            }
            finally { sem.Release(); }
        });
        await Task.WhenAll(tasks);

        AnsiConsole.MarkupLineInterpolated(
            $"[grey]done in {sw.Elapsed.TotalSeconds:F0}s · {ok} ok · {failed} failed[/]");
        return 0;
    }

    private static List<SessionInfo> SelectSessions(Database db, Settings s)
    {
        var clauses = new List<string>();
        var prms = new Dictionary<string, object>();

        if (!string.IsNullOrEmpty(s.SessionId))
        {
            clauses.Add("id LIKE $q || '%'");
            prms["$q"] = s.SessionId;
        }
        else
        {
            if (s.AllActive) clauses.Add("status = 'active'");
            if (TryParseDuration(s.Since, out var since))
            {
                clauses.Add("started_at >= $since");
                prms["$since"] = DateTimeOffset.UtcNow.Subtract(since).ToString("O");
            }
            if (s.StaleOnly)
            {
                // 30-minute quiet window — sessions still being typed into are skipped.
                clauses.Add("(last_active_at IS NULL OR last_active_at < $stale)");
                prms["$stale"] = DateTimeOffset.UtcNow.AddMinutes(-30).ToString("O");
            }
            // --all is the catch-all — no status filter — but still requires
            // an explicit selector so we never accidentally summarise everything.
            if (clauses.Count == 0 && !s.All)
            {
                AnsiConsole.MarkupLine("[red]Pass a session id, or one of --all / --all-active / --since <DUR>.[/]");
                return new();
            }
            if (clauses.Count == 0) clauses.Add("1=1");
        }

        var sql = $"""
            SELECT id, project_path, cwd, git_branch, started_at, ended_at,
                   message_count, turn_count, status, first_user_text, last_user_text,
                   derived_branches
            FROM sessions
            WHERE {string.Join(" AND ", clauses)}
            ORDER BY started_at DESC
            """;
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in prms) cmd.Parameters.AddWithValue(k, v);
        using var rd = cmd.ExecuteReader();
        var list = new List<SessionInfo>();
        while (rd.Read())
        {
            list.Add(new SessionInfo(
                Id: rd.GetString(0),
                ProjectPath: rd.GetString(1),
                Cwd: rd.IsDBNull(2) ? null : rd.GetString(2),
                GitBranch: rd.IsDBNull(3) ? null : rd.GetString(3),
                StartedAt: rd.IsDBNull(4) ? null : DateTimeOffset.Parse(rd.GetString(4)),
                EndedAt: rd.IsDBNull(5) ? null : DateTimeOffset.Parse(rd.GetString(5)),
                MessageCount: rd.GetInt32(6),
                TurnCount: rd.GetInt32(7),
                Status: rd.GetString(8),
                FirstUserText: rd.IsDBNull(9) ? null : rd.GetString(9),
                LastUserText: rd.IsDBNull(10) ? null : rd.GetString(10),
                DerivedBranches: rd.IsDBNull(11) ? null : SessionLookup.ParseBranches(rd.GetString(11))));
        }
        return list;
    }

    private static bool TryParseDuration(string? s, out TimeSpan span)
    {
        span = default;
        if (string.IsNullOrEmpty(s)) return false;
        if (s.EndsWith("d") && int.TryParse(s[..^1], out var d)) { span = TimeSpan.FromDays(d); return true; }
        if (s.EndsWith("h") && int.TryParse(s[..^1], out var h)) { span = TimeSpan.FromHours(h); return true; }
        if (s.EndsWith("m") && int.TryParse(s[..^1], out var m)) { span = TimeSpan.FromMinutes(m); return true; }
        return false;
    }
}
