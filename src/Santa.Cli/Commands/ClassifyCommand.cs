using System.ComponentModel;
using Santa.Core.Classify;
using Santa.Core.Sessions;
using Santa.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Santa.Cli.Commands;

public sealed class ClassifyCommand : AsyncCommand<ClassifyCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<SESSION_ID>")]
        public string SessionId { get; init; } = "";

        [CommandOption("--recipe <ID>")]
        [Description("Recipe id (file basename in ~/.claude/search/recipes/).")]
        public string? Recipe { get; init; }

        [CommandOption("--all-recipes")]
        public bool AllRecipes { get; init; }

        [CommandOption("--dry-run")]
        public bool DryRun { get; init; }

        [CommandOption("--db <PATH>")]
        public string? Db { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        BuiltinRecipes.WriteIfMissing(RecipeLoader.DefaultRoot);
        var loader = new RecipeLoader(RecipeLoader.DefaultRoot);

        var recipes = SelectRecipes(s, loader, out var bad);
        if (bad) return 2;

        using var db = Database.Open(s.Db ?? Database.DefaultPath);
        var session = new SessionLookup(db).FindByPrefix(s.SessionId);
        if (session is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]No session matches[/] {s.SessionId}");
            return 2;
        }

        var classifier = new ClassifierService(db);
        foreach (var recipe in recipes)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[bold]classify[/] session={session.Id[..8]} recipe=[cyan]{recipe.Id}[/] mode={recipe.Mode} model={recipe.Model}");
            try
            {
                var r = await classifier.ClassifyAsync(session, recipe, s.DryRun, AnsiConsole.WriteLine, ct);
                if (!s.DryRun)
                    AnsiConsole.MarkupLineInterpolated($"  → status=[green]{r.Status ?? "(unknown)"}[/]  evidence={r.Evidence ?? ""}");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLineInterpolated($"  [red]error:[/] {Markup.Escape(ex.Message)}");
                return 3;
            }
        }
        return 0;
    }

    private static List<ClassificationRecipe> SelectRecipes(Settings s, RecipeLoader loader, out bool bad)
    {
        bad = false;
        if (s.AllRecipes) return loader.LoadAll().ToList();
        if (s.Recipe is null)
        {
            AnsiConsole.MarkupLine("[red]must pass --recipe <id> or --all-recipes[/]");
            bad = true;
            return new();
        }
        var r = loader.Find(s.Recipe);
        if (r is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]recipe not found:[/] {s.Recipe}  ([grey]{loader.Root}[/])");
            bad = true;
            return new();
        }
        return new() { r };
    }
}
