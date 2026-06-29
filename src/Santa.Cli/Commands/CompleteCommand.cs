using Santa.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Santa.Cli.Commands;

public sealed class SessionStatusSettings : CommandSettings
{
    [CommandArgument(0, "<SESSION_ID>")]
    public string SessionId { get; init; } = "";

    [CommandOption("--db <PATH>")]
    public string? Db { get; init; }
}

public sealed class CompleteCommand : Command<SessionStatusSettings>
{
    protected override int Execute(CommandContext context, SessionStatusSettings s, CancellationToken cancellationToken)
        => SessionStatus.Set(s, "completed", "manual");
}

public sealed class UncompleteCommand : Command<SessionStatusSettings>
{
    protected override int Execute(CommandContext context, SessionStatusSettings s, CancellationToken cancellationToken)
        => SessionStatus.Set(s, "active", "manual");
}

internal static class SessionStatus
{
    public static int Set(SessionStatusSettings s, string status, string source)
    {
        using var db = Database.Open(s.Db ?? Database.DefaultPath);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            UPDATE sessions SET status = $st, status_source = $src, status_set_at = $ts
            WHERE id LIKE $q || '%'
            """;
        cmd.Parameters.AddWithValue("$st", status);
        cmd.Parameters.AddWithValue("$src", source);
        cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$q", s.SessionId);
        var n = cmd.ExecuteNonQuery();
        AnsiConsole.MarkupLineInterpolated($"updated {n} session(s) → [bold]{status}[/]");
        return n > 0 ? 0 : 2;
    }
}
