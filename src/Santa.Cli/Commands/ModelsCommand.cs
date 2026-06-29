using System.ComponentModel;
using Santa.Core.Embedding;
using Santa.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Santa.Cli.Commands;

public sealed class ModelsCommand : AsyncCommand<ModelsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ACTION>")]
        [Description("download | status")]
        public string Action { get; init; } = "status";

        [CommandOption("--root <PATH>")]
        public string? Root { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken cancellationToken)
    {
        var root = s.Root ?? EmbedderConfig.DefaultRoot;
        var embCfg = EmbedderConfig.NomicV15(root);
        var rrCfg  = RerankerConfig.MsMarcoMiniLmL12(root);

        switch (s.Action)
        {
            case "status":
                Status(embCfg, rrCfg);
                return 0;
            case "download":
                AnsiConsole.MarkupLine("[grey]embedder:[/] nomic-embed-text-v1.5 (~500 MB)");
                await ModelDownloader.DownloadNomicV15Async(embCfg, AnsiConsole.WriteLine);
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[grey]reranker:[/] ms-marco-MiniLM-L-12-v2 (~120 MB)");
                await ModelDownloader.DownloadMsMarcoRerankerAsync(rrCfg, AnsiConsole.WriteLine);
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[grey]sqlite-vec extension:[/]");
                if (VecExtension.IsInstalled())
                    AnsiConsole.MarkupLine("  [green]already installed[/]");
                else
                    await VecExtension.DownloadAsync(log: AnsiConsole.WriteLine);
                return 0;
            default:
                AnsiConsole.MarkupLineInterpolated($"[red]unknown action:[/] {s.Action}");
                return 2;
        }
    }

    private static void Status(EmbedderConfig emb, RerankerConfig rr)
    {
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[bold]embedder[/]", emb.ModelId);
        grid.AddRow("  dim", emb.Dimensions.ToString());
        grid.AddRow("  onnx", FmtPresence(emb.OnnxPath));
        grid.AddRow("  vocab", FmtPresence(emb.VocabPath));
        grid.AddRow("[bold]reranker[/]", rr.ModelId);
        grid.AddRow("  onnx", FmtPresence(rr.OnnxPath));
        grid.AddRow("  vocab", FmtPresence(rr.VocabPath));
        grid.AddRow("[bold]sqlite-vec[/]", VecExtension.IsInstalled() ? "[green]installed[/]" : "[yellow]missing[/]");
        AnsiConsole.Write(grid);
    }

    private static string FmtPresence(string path)
    {
        if (!File.Exists(path)) return "[yellow]missing[/]";
        var size = new FileInfo(path).Length;
        var sz = size < 1024 * 1024 ? $"{size / 1024.0:F0} KB" : $"{size / (1024.0 * 1024):F1} MB";
        return $"[green]✓[/] {sz}  [grey]{path}[/]";
    }
}
