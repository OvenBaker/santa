using System.ComponentModel;
using Santa.Cli;
using Santa.Core.Embedding;
using Santa.Core.Search;
using Santa.Core.Sessions;
using Santa.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Santa.Cli.Commands;

public sealed class BrowseCommand : Command<BrowseCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--db <PATH>")]
        public string? Db { get; init; }

        [CommandOption("--device <ID>")]
        public int DeviceId { get; init; } = 0;

        [CommandOption("--keyword-only")]
        public bool KeywordOnly { get; init; }

        [CommandOption("--provider <PROVIDER>")]
        [Description("Inference provider: cuda (default), cpu, or keyword-only.")]
        [DefaultValue("cuda")]
        public string Provider { get; init; } = "cuda";

        [CommandOption("-q|--query <TEXT>")]
        [Description("Initial query (skip the prompt).")]
        public string? Query { get; init; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        if (!InferenceProviderOption.TryResolve(s.Provider, s.KeywordOnly, out var provider)) return 2;
        if (!InferenceProviderOption.TryAcquireGpuCourtesy(provider, s.DeviceId, out var gpuLease)) return 0;
        using var gpuLeaseScope = gpuLease;
        using var db = Database.Open(s.Db ?? Database.DefaultPath);

        IEmbedder? embedder = null;
        if (provider is not null)
        {
            var cfg = InferenceProviderOption.Apply(
                EmbedderConfig.NomicV15(EmbedderConfig.DefaultRoot), provider.Value, s.DeviceId);
            if (File.Exists(cfg.OnnxPath) && File.Exists(cfg.VocabPath) && db.TryEnableVec(cfg.Dimensions))
            {
                try { embedder = new LocalEmbedder(cfg); }
                catch (Exception ex) { return InferenceProviderOption.ReportInitializationFailure(provider.Value, ex); }
            }
        }

        var search = new HybridSearch(db);
        var lookup = new SessionLookup(db);
        string? query = s.Query;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                AnsiConsole.WriteLine();
                query = AnsiConsole.Prompt(
                    new TextPrompt<string>("[cyan]search[/] ([grey]empty for recent, q to quit[/]):")
                        .DefaultValue(query ?? "")
                        .ShowDefaultValue(false)
                        .AllowEmpty());

                if (string.Equals(query?.Trim(), "q", StringComparison.OrdinalIgnoreCase)) return 0;

                var sessions = string.IsNullOrWhiteSpace(query)
                    ? RecentSessions(db, 25)
                    : SearchSessions(search, embedder, query, 25);

                if (sessions.Count == 0)
                {
                    AnsiConsole.MarkupLine("[grey]no results[/]");
                    continue;
                }

                var picker = new SelectionPrompt<SessionRow>()
                    .Title("[cyan]pick a session[/] (esc to re-search)")
                    .PageSize(15)
                    .UseConverter(FormatRow);
                picker.AddChoices(sessions);
                var pick = AnsiConsole.Prompt(picker);

                while (true)
                {
                    var action = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title($"[cyan]{pick.Id[..8]}[/] · {Markup.Escape(pick.Cwd ?? "")}")
                            .AddChoices("resume", "show", "show + chunks", "export md", "complete", "uncomplete", "back", "quit"));

                    var r = HandleAction(action, db, lookup, pick);
                    if (r is "back") break;
                    if (r is "quit") return 0;
                    if (r is "exit") return 0;
                }
            }
        }
        finally { embedder?.Dispose(); }
        return 0;
    }

    private static string HandleAction(string action, Database db, SessionLookup lookup, SessionRow pick)
    {
        switch (action)
        {
            case "resume":
                {
                    var sess = lookup.FindByPrefix(pick.Id);
                    if (sess is null) return "back";
                    try
                    {
                        var plan = ResumeLauncher.Plan(sess, null);
                        ResumeLauncher.Launch(plan);
                        AnsiConsole.MarkupLineInterpolated($"[green]launched[/] {plan.Cwd}");
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLineInterpolated($"[red]{Markup.Escape(ex.Message)}[/]");
                    }
                    return "exit"; // resume opens new tab; current process can exit
                }
            case "show":
            case "show + chunks":
                {
                    using var cmd = db.Connection.CreateCommand();
                    cmd.CommandText = "SELECT first_user_text, last_user_text FROM sessions WHERE id = $id";
                    cmd.Parameters.AddWithValue("$id", pick.Id);
                    using var rd = cmd.ExecuteReader();
                    if (rd.Read())
                    {
                        AnsiConsole.MarkupLine("[grey]first prompt:[/]");
                        AnsiConsole.WriteLine(rd.GetString(0));
                        AnsiConsole.MarkupLine("[grey]last prompt:[/]");
                        AnsiConsole.WriteLine(rd.IsDBNull(1) ? "" : rd.GetString(1));
                    }
                    if (action == "show + chunks")
                    {
                        rd.Close();
                        using var c2 = db.Connection.CreateCommand();
                        c2.CommandText = "SELECT start_seq, end_seq, text FROM chunks WHERE session_id = $id ORDER BY start_seq";
                        c2.Parameters.AddWithValue("$id", pick.Id);
                        using var rd2 = c2.ExecuteReader();
                        while (rd2.Read())
                        {
                            AnsiConsole.WriteLine();
                            AnsiConsole.MarkupLineInterpolated($"[grey]--- seq {rd2.GetInt32(0)}..{rd2.GetInt32(1)} ---[/]");
                            AnsiConsole.WriteLine(rd2.GetString(2));
                        }
                    }
                    return "stay";
                }
            case "export md":
                {
                    var path = $"/tmp/{pick.Id}.md";
                    var projects = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
                    var svc = new Core.Export.ExportService(db, projects);
                    using var w = new StreamWriter(path);
                    svc.Export(pick.Id, Core.Export.ExportFormat.Markdown, w);
                    AnsiConsole.MarkupLineInterpolated($"[grey]wrote[/] {path}");
                    return "stay";
                }
            case "complete":
                SetStatus(db, pick.Id, "completed", "manual");
                return "stay";
            case "uncomplete":
                SetStatus(db, pick.Id, "active", "manual");
                return "stay";
            case "back": return "back";
            case "quit": return "quit";
        }
        return "stay";
    }

    private static void SetStatus(Database db, string id, string status, string source)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE sessions SET status=$st, status_source=$src, status_set_at=$ts WHERE id=$id";
        cmd.Parameters.AddWithValue("$st", status);
        cmd.Parameters.AddWithValue("$src", source);
        cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        AnsiConsole.MarkupLineInterpolated($"  → status=[bold]{status}[/]");
    }

    private static List<SessionRow> RecentSessions(Database db, int limit)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, cwd, git_branch, started_at, first_user_text, status
            FROM sessions
            WHERE status != 'completed'
            ORDER BY started_at DESC
            LIMIT $lim
            """;
        cmd.Parameters.AddWithValue("$lim", limit);
        using var rd = cmd.ExecuteReader();
        var list = new List<SessionRow>();
        while (rd.Read())
            list.Add(new SessionRow(
                rd.GetString(0),
                rd.IsDBNull(1) ? null : rd.GetString(1),
                rd.IsDBNull(2) ? null : rd.GetString(2),
                rd.IsDBNull(3) ? null : rd.GetString(3),
                rd.IsDBNull(4) ? null : rd.GetString(4),
                rd.GetString(5)));
        return list;
    }

    private static List<SessionRow> SearchSessions(HybridSearch search, IEmbedder? embedder, string query, int limit)
    {
        var hits = search.Search(query, embedder, limit, includeCompleted: false);
        return hits.Select(h => new SessionRow(
            h.SessionId, h.Cwd, h.GitBranch, h.StartedAt, h.FirstUserText, "active"))
            .DistinctBy(r => r.Id)
            .ToList();
    }

    private static string FormatRow(SessionRow r)
    {
        var when = r.StartedAt is null ? "          " : DateTimeOffset.Parse(r.StartedAt).ToLocalTime().ToString("MM-dd HH:mm");
        var first = r.FirstUserText ?? "";
        if (first.Length > 90) first = first[..90] + "…";
        first = first.Replace('\n', ' ').Replace('\r', ' ');
        var cwd = r.Cwd ?? "";
        if (cwd.Length > 40) cwd = "…" + cwd[^39..];
        return $"{r.Id[..8]}  {when}  {cwd,-40}  {first}";
    }

    public sealed record SessionRow(string Id, string? Cwd, string? GitBranch, string? StartedAt, string? FirstUserText, string Status);
}
