using System.ComponentModel;
using Santa.Core.Embedding;
using Santa.Core.Ingest;
using Santa.Core.Sessions;
using Santa.Core.Settings;
using Santa.Core.Storage;
using Santa.Core.Summarize;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Santa.Cli.Commands;

/// <summary>
/// Single-shot "do everything" refresh for cron / scheduled runs.
/// Runs incremental ingest (auto-embedding when models exist) + summarise stale missing sessions.
/// Idempotent — safe to call as often as you like.
/// </summary>
public sealed class RefreshCommand : AsyncCommand<RefreshCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--db <PATH>")]
        public string? Db { get; init; }

        [CommandOption("--device <ID>")]
        public int DeviceId { get; init; } = 0;

        [CommandOption("--no-summary")]
        [Description("Skip the summarisation pass.")]
        public bool NoSummary { get; init; }

        [CommandOption("--concurrency <N>")]
        [DefaultValue(4)]
        public int Concurrency { get; init; }

        [CommandOption("--quiet")]
        [Description("Single-line output suitable for cron logs.")]
        public bool Quiet { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.Now;
        var dbPath = s.Db ?? Database.DefaultPath;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var projects = Path.Combine(home, ".claude", "projects");
        var codexRoot = Path.Combine(home, ".codex", "sessions");
        if (!Directory.Exists(projects))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Projects root not found:[/] {projects}");
            return 2;
        }

        using var db = Database.Open(dbPath);

        // Ingest with auto-embed (mirrors the IngestCommand heuristic).
        IEmbedder? embedder = null;
        try
        {
            var ecfg = EmbedderConfig.NomicV15(EmbedderConfig.DefaultRoot) with { DeviceId = s.DeviceId };
            if (File.Exists(ecfg.OnnxPath) && File.Exists(ecfg.VocabPath)
                && db.TryEnableVec(ecfg.Dimensions)
                && new VectorRepository(db.Connection).HasEmbeddings(ecfg.ModelId))
            {
                try { embedder = new LocalEmbedder(ecfg); } catch { embedder = null; }
            }

            var stats = new IngestService(db).Run(new IngestOptions(
                ProjectsRoot: projects,
                CodexRoot: codexRoot,
                Embedder: embedder,
                Log: _ => { }));

            if (!s.Quiet)
                AnsiConsole.MarkupLineInterpolated(
                    $"[grey]ingest:[/] scanned={stats.FilesScanned} touched={stats.SessionsTouched} chunks+={stats.ChunksWritten}");

            // Summarise stale missing sessions when Max plan is in play.
            int summarised = 0, failed = 0;
            if (!s.NoSummary
                && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))
                && AnyExistingSummary(db))
            {
                var summarizer = new Summarizer(db);
                var pending = LoadActiveSessions(db).Where(x => summarizer.NeedsSummary(x.Id, x.TurnCount)).ToList();
                if (pending.Count > 0)
                {
                    var sem = new SemaphoreSlim(Math.Max(1, s.Concurrency));
                    int doneCount = 0;
                    var tasks = pending.Select(async sess =>
                    {
                        await sem.WaitAsync(ct);
                        try
                        {
                            await summarizer.SummariseAsync(sess, false, _ => { }, ct);
                            Interlocked.Increment(ref summarised);
                        }
                        catch { Interlocked.Increment(ref failed); }
                        finally { sem.Release(); Interlocked.Increment(ref doneCount); }
                    });
                    await Task.WhenAll(tasks);
                }
                if (!s.Quiet)
                    AnsiConsole.MarkupLineInterpolated(
                        $"[grey]summary:[/] candidates={pending.Count} ok={summarised} failed={failed}");
            }

            var elapsed = (DateTimeOffset.Now - startedAt).TotalSeconds;
            if (s.Quiet)
            {
                Console.WriteLine(
                    $"{startedAt:O} ok ingested={stats.SessionsTouched} chunks+={stats.ChunksWritten} summarised={summarised} elapsed={elapsed:F1}s");
            }
            else
            {
                AnsiConsole.MarkupLineInterpolated($"[grey]done in {elapsed:F1}s[/]");
            }
            return 0;
        }
        finally { embedder?.Dispose(); }
    }

    private static bool AnyExistingSummary(Database db)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM sessions WHERE summary_at IS NOT NULL)";
        return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
    }

    private static List<SessionInfo> LoadActiveSessions(Database db)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, project_path, cwd, git_branch, started_at, ended_at,
                   message_count, turn_count, status, first_user_text, last_user_text, derived_branches
            FROM sessions
            WHERE status = 'active'
            """;
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
}
