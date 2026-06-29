using System.ComponentModel;
using Santa.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Santa.Cli.Commands;

public sealed class ShowCommand : Command<ShowCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<SESSION_ID>")]
        public string SessionId { get; init; } = "";

        [CommandOption("--db <PATH>")]
        public string? Db { get; init; }

        [CommandOption("--chunks")]
        [Description("Print the projected chunk text in addition to metadata.")]
        public bool Chunks { get; init; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellationToken)
    {
        using var db = Database.Open(s.Db ?? Database.DefaultPath);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, project_path, cwd, git_branch, started_at, ended_at,
                   message_count, turn_count, status, first_user_text
            FROM sessions
            WHERE id LIKE $q || '%'
            LIMIT 2
            """;
        cmd.Parameters.AddWithValue("$q", s.SessionId);
        using var rd = cmd.ExecuteReader();
        if (!rd.Read())
        {
            AnsiConsole.MarkupLine("[red]No matching session.[/]");
            return 2;
        }
        var id = rd.GetString(0);
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[bold]session[/]", id);
        grid.AddRow("project", rd.GetString(1));
        grid.AddRow("cwd", rd.IsDBNull(2) ? "" : rd.GetString(2));
        grid.AddRow("branch", rd.IsDBNull(3) ? "" : rd.GetString(3));
        grid.AddRow("started", rd.IsDBNull(4) ? "" : rd.GetString(4));
        grid.AddRow("ended", rd.IsDBNull(5) ? "" : rd.GetString(5));
        grid.AddRow("messages", rd.GetInt32(6).ToString());
        grid.AddRow("turns", rd.GetInt32(7).ToString());
        grid.AddRow("status", rd.GetString(8));
        grid.AddRow("first prompt", rd.IsDBNull(9) ? "" : rd.GetString(9));
        rd.Close();
        AnsiConsole.Write(grid);

        if (!s.Chunks) return 0;

        using var c2 = db.Connection.CreateCommand();
        c2.CommandText = "SELECT start_seq, end_seq, approx_tokens, text FROM chunks WHERE session_id = $sid ORDER BY start_seq";
        c2.Parameters.AddWithValue("$sid", id);
        using var rd2 = c2.ExecuteReader();
        while (rd2.Read())
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLineInterpolated($"[grey]--- seq {rd2.GetInt32(0)}..{rd2.GetInt32(1)}  ~{rd2.GetInt32(2)}t ---[/]");
            AnsiConsole.WriteLine(rd2.GetString(3));
        }
        return 0;
    }
}
