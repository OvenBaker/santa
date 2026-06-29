using System.ComponentModel;
using Santa.Core.Classify;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Santa.Cli.Commands;

public sealed class RecipesCommand : Command<RecipesCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ACTION>")]
        [Description("list | show <id> | init")]
        public string Action { get; init; } = "list";

        [CommandArgument(1, "[ID]")]
        public string? Id { get; init; }

        [CommandOption("--root <PATH>")]
        public string? Root { get; init; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellationToken)
    {
        var root = s.Root ?? RecipeLoader.DefaultRoot;
        Directory.CreateDirectory(root);

        switch (s.Action)
        {
            case "init":
                BuiltinRecipes.WriteIfMissing(root);
                AnsiConsole.MarkupLineInterpolated($"[grey]wrote built-in recipes to[/] {root}");
                return 0;
            case "list":
                BuiltinRecipes.WriteIfMissing(root);
                List(root);
                return 0;
            case "show":
                if (string.IsNullOrEmpty(s.Id))
                {
                    AnsiConsole.MarkupLine("[red]usage:[/] recipes show <id>");
                    return 2;
                }
                return Show(root, s.Id);
            default:
                AnsiConsole.MarkupLineInterpolated($"[red]unknown action:[/] {s.Action}");
                return 2;
        }
    }

    private static void List(string root)
    {
        var loader = new RecipeLoader(root);
        var recipes = loader.LoadAll();
        if (recipes.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]no recipes[/]");
            return;
        }
        var table = new Table().AddColumns("id", "mode", "model", "name");
        foreach (var r in recipes)
            table.AddRow(r.Id, r.Mode.ToString().ToLowerInvariant(), r.Model, r.Name ?? "");
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLineInterpolated($"[grey]{root}[/]");
    }

    private static int Show(string root, string id)
    {
        var loader = new RecipeLoader(root);
        var r = loader.Find(id);
        if (r is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]not found:[/] {id}  [grey]({root})[/]");
            return 2;
        }
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("id", r.Id);
        grid.AddRow("name", r.Name ?? "");
        grid.AddRow("mode", r.Mode.ToString().ToLowerInvariant());
        grid.AddRow("model", r.Model);
        grid.AddRow("allowed_tools", string.Join(", ", r.AllowedTools));
        grid.AddRow("transcript", $"{r.TranscriptMode} (max {r.TranscriptMaxTokens?.ToString() ?? "∞"} tok)");
        grid.AddRow("status_map", string.Join(", ", r.StatusMap.Select(kv => $"{kv.Key}→{kv.Value}")));
        AnsiConsole.Write(grid);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]--- prompt ---[/]");
        AnsiConsole.WriteLine(r.Prompt);
        return 0;
    }
}
