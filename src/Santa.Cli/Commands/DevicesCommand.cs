using Santa.Core.Embedding;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Santa.Cli.Commands;

public sealed class DevicesCommand : Command<DevicesCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--device <ID>")]
        public int DeviceId { get; init; } = 0;
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellationToken)
    {
        GpuInfoReport report;
        try { report = GpuInfo.Probe(s.DeviceId); }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]ORT init failed:[/] {Markup.Escape(ex.Message)}");
            return 2;
        }

        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("ORT version", report.OrtVersion);
        grid.AddRow("providers", Markup.Escape(string.Join(", ", report.AvailableProviders)));
        grid.AddRow("CUDA available", report.CudaAvailable ? "[green]yes[/]" : "[red]no[/]");
        grid.AddRow("pinned device", report.PinnedDeviceId?.ToString() ?? "[grey](n/a)[/]");
        if (report.CudaError is not null)
            grid.AddRow("CUDA error", $"[red]{Markup.Escape(report.CudaError)}[/]");
        AnsiConsole.Write(grid);

        if (!report.CudaAvailable)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]CUDA runtime libraries are not on the loader path.[/]");
            AnsiConsole.MarkupLine("With the wsl-ubuntu CUDA repo set up:");
            AnsiConsole.MarkupLine("  [grey]sudo apt install -y cuda-libraries-12-9[/]");
            AnsiConsole.MarkupLine("Re-run `santa devices`. If a later step errors on a cuDNN symbol,");
            AnsiConsole.MarkupLine("drop the cuDNN .so files in (NVIDIA's redist tarball is distro-independent):");
            AnsiConsole.MarkupLine("  [grey]/usr/lib/x86_64-linux-gnu/[/] (then `sudo ldconfig`)");
        }
        return report.CudaAvailable ? 0 : 1;
    }
}
