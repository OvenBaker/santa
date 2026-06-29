using System.ComponentModel;
using Santa.Core.Export;
using Santa.Core.Sessions;
using Santa.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Santa.Cli.Commands;

public sealed class ExportCommand : Command<ExportCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<SESSION_ID>")]
        public string SessionId { get; init; } = "";

        [CommandOption("-f|--format <FMT>")]
        [Description("md | json | jsonl | prose")]
        [DefaultValue("md")]
        public string Format { get; init; } = "md";

        [CommandOption("-o|--output <PATH>")]
        [Description("Output file (default: stdout).")]
        public string? Output { get; init; }

        [CommandOption("--projects <PATH>")]
        public string? Projects { get; init; }

        [CommandOption("--db <PATH>")]
        public string? Db { get; init; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        var fmt = s.Format.ToLowerInvariant() switch
        {
            "md" or "markdown" => ExportFormat.Markdown,
            "json"             => ExportFormat.Json,
            "jsonl"            => ExportFormat.Jsonl,
            "prose"            => ExportFormat.Prose,
            _ => throw new InvalidOperationException("unknown format")
        };

        using var db = Database.Open(s.Db ?? Database.DefaultPath);
        var lookup = new SessionLookup(db);
        var sess = lookup.FindByPrefix(s.SessionId);
        if (sess is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]No session matches[/] {s.SessionId}");
            return 2;
        }

        var projects = s.Projects ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        var svc = new ExportService(db, projects);

        TextWriter writer;
        bool ownWriter = false;
        if (s.Output is null) writer = Console.Out;
        else
        {
            writer = new StreamWriter(s.Output);
            ownWriter = true;
        }

        try { svc.Export(sess.Id, fmt, writer); }
        finally
        {
            if (ownWriter) { writer.Flush(); writer.Dispose(); }
        }

        if (s.Output is not null)
            AnsiConsole.MarkupLineInterpolated($"[grey]wrote[/] {s.Output}");
        return 0;
    }
}
