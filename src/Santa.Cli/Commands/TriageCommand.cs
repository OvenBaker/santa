using System.ComponentModel;
using Santa.Core.Classify;
using Santa.Core.Sessions;
using Santa.Core.Storage;
using Microsoft.Data.Sqlite;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Santa.Cli.Commands;

public sealed class TriageCommand : AsyncCommand<TriageCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--recipe <ID>")]
        public string[]? Recipes { get; init; }

        [CommandOption("--since <DUR>")]
        [Description("Only sessions started within this window (e.g. 30d, 7d, 24h). Default: all.")]
        public string? Since { get; init; }

        [CommandOption("--max <N>")]
        [DefaultValue(50)]
        public int Max { get; init; }

        [CommandOption("--unclassified")]
        [Description("Only sessions that don't already have a fresh classification for the recipe.")]
        public bool Unclassified { get; init; }

        [CommandOption("--status <STATUS>")]
        [DefaultValue("active")]
        public string Status { get; init; } = "active";

        [CommandOption("--has-derived-branches")]
        [Description("Only sessions where the JSONL tool calls created/switched to a feature branch.")]
        public bool HasDerivedBranches { get; init; }

        [CommandOption("--dry-run")]
        public bool DryRun { get; init; }

        [CommandOption("--db <PATH>")]
        public string? Db { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        if (s.Recipes is null || s.Recipes.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]pass at least one --recipe <id>[/]");
            return 2;
        }

        BuiltinRecipes.WriteIfMissing(RecipeLoader.DefaultRoot);
        var loader = new RecipeLoader(RecipeLoader.DefaultRoot);

        var recipes = new List<ClassificationRecipe>();
        foreach (var id in s.Recipes)
        {
            var r = loader.Find(id);
            if (r is null) { AnsiConsole.MarkupLineInterpolated($"[red]recipe not found:[/] {id}"); return 2; }
            recipes.Add(r);
        }

        using var db = Database.Open(s.Db ?? Database.DefaultPath);

        var sessions = SelectSessions(db, s);
        AnsiConsole.MarkupLineInterpolated(
            $"triage: [bold]{sessions.Count}[/] candidate session(s) × {recipes.Count} recipe(s)");

        var classifier = new ClassifierService(db);
        var renderer = new TranscriptRenderer(db);
        int processed = 0;
        foreach (var sess in sessions)
        {
            foreach (var recipe in recipes)
            {
                if (s.Unclassified && AlreadyClassified(db, sess.Id, recipe.Id))
                    continue;
                if (s.DryRun)
                {
                    var transcript = renderer.Render(sess.Id, recipe.TranscriptMode, recipe.TranscriptMaxTokens);
                    var prompt = PromptRenderer.Render(recipe.Prompt, sess, transcript);
                    AnsiConsole.MarkupLineInterpolated(
                        $"  {sess.Id[..8]}  [cyan]{recipe.Id}[/]  branch=[magenta]{sess.GitBranch ?? "-"}[/]  cwd={Markup.Escape(sess.Cwd ?? "-")}");
                    var toolsStr = recipe.AllowedTools.Count == 0 ? "(none)" : string.Join(", ", recipe.AllowedTools);
                    AnsiConsole.MarkupLineInterpolated(
                        $"    [grey]prompt={prompt.Length}c  transcript={transcript.Length}c  tools={toolsStr}[/]");
                    processed++;
                    if (processed >= s.Max) goto done;
                    continue;
                }
                AnsiConsole.MarkupLineInterpolated(
                    $"  {sess.Id[..8]}  [cyan]{recipe.Id}[/]");
                try
                {
                    var r = await classifier.ClassifyAsync(sess, recipe, false, _ => { }, ct);
                    AnsiConsole.MarkupLineInterpolated($"    → status=[green]{r.Status ?? "(unknown)"}[/]  {Markup.Escape(r.Evidence ?? "")}");
                    processed++;
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLineInterpolated($"    [red]error:[/] {Markup.Escape(ex.Message)}");
                }
                if (processed >= s.Max) goto done;
            }
        }
        done:
        AnsiConsole.MarkupLineInterpolated($"[grey]processed {processed} {(s.DryRun ? "dry-run " : "")}classifications[/]");
        return 0;
    }

    private static List<SessionInfo> SelectSessions(Database db, Settings s)
    {
        var clauses = new List<string> { "status = $status" };
        var prms = new Dictionary<string, object>{ ["$status"] = s.Status };
        if (TryParseDuration(s.Since, out var since))
        {
            clauses.Add("started_at >= $since");
            prms["$since"] = DateTimeOffset.UtcNow.Subtract(since).ToString("O");
        }
        if (s.HasDerivedBranches) clauses.Add("derived_branches IS NOT NULL");
        var sql = $"""
            SELECT id, project_path, cwd, git_branch, started_at, ended_at,
                   message_count, turn_count, status, first_user_text, last_user_text,
                   derived_branches
            FROM sessions
            WHERE {string.Join(" AND ", clauses)}
            ORDER BY started_at DESC
            LIMIT {Math.Max(s.Max * 4, 200)}
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

    private static bool AlreadyClassified(Database db, string sessionId, string recipeId)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM classifications WHERE session_id = $s AND recipe_id = $r";
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$r", recipeId);
        return cmd.ExecuteScalar() is not null;
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
