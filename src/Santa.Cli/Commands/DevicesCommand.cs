using System.ComponentModel;
using Santa.Cli;
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

        [CommandOption("--provider <PROVIDER>")]
        [Description("Provider to probe: cuda (default) or cpu.")]
        [DefaultValue("cuda")]
        public string Provider { get; init; } = "cuda";
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellationToken)
    {
        if (!InferenceProviderOption.TryResolve(s.Provider, false, out var provider)) return 2;
        if (provider is null)
        {
            AnsiConsole.MarkupLine("[red]devices requires --provider cpu or --provider cuda.[/]");
            return 2;
        }

        GpuInfoReport report;
        try { report = GpuInfo.Probe(provider.Value, s.DeviceId); }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]ORT init failed:[/] {Markup.Escape(ex.Message)}");
            return 2;
        }

        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("ORT version", report.OrtVersion);
        grid.AddRow("build flavor", report.BuildFlavor);
        grid.AddRow("selected provider", report.SelectedProvider.ToString().ToLowerInvariant());
        grid.AddRow("providers", Markup.Escape(string.Join(", ", report.AvailableProviders)));
        grid.AddRow("provider available", report.ProviderAvailable ? "[green]yes[/]" : "[red]no[/]");
        grid.AddRow("pinned device", report.PinnedDeviceId?.ToString() ?? "[grey](n/a)[/]");
        if (report.ProviderError is not null)
            grid.AddRow("provider error", $"[red]{Markup.Escape(report.ProviderError)}[/]");
        AnsiConsole.Write(grid);

        return report.ProviderAvailable ? 0 : 1;
    }
}
