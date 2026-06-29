using System.ComponentModel;
using Santa.Core.Sessions;
using Santa.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Santa.Cli.Commands;

public sealed class ResumeCommand : Command<ResumeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<SESSION_ID>")]
        [Description("Full or prefix session id (e.g. first 8 hex chars).")]
        public string SessionId { get; init; } = "";

        [CommandOption("--cwd <PATH>")]
        [Description("Override target cwd (use when the original cwd was deleted).")]
        public string? Cwd { get; init; }

        [CommandOption("--print")]
        [Description("Print the launcher command instead of executing it.")]
        public bool Print { get; init; }

        [CommandOption("--db <PATH>")]
        public string? Db { get; init; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellationToken)
    {
        using var db = Database.Open(s.Db ?? Database.DefaultPath);
        var lookup = new SessionLookup(db);

        SessionInfo? sess;
        try { sess = lookup.FindByPrefix(s.SessionId); }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 2;
        }

        if (sess is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]No session matches[/] {s.SessionId}");
            return 2;
        }

        ResumePlan plan;
        try { plan = ResumeLauncher.Plan(sess, s.Cwd); }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 3;
        }

        AnsiConsole.MarkupLineInterpolated($"resume [bold]{sess.Id}[/]  cwd=[cyan]{plan.Cwd}[/]");
        if (s.Print)
        {
            AnsiConsole.WriteLine(plan.CommandLine);
            return 0;
        }

        try { ResumeLauncher.Launch(plan); }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]launch failed:[/] {ex.Message}");
            return 4;
        }
        return 0;
    }
}
