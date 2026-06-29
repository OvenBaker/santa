using System.ComponentModel;
using Santa.Core.Embedding;
using Santa.Core.Ingest;
using Santa.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Santa.Cli.Tui;

public sealed class TuiCommand : Command<TuiCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--db <PATH>")]
        public string? Db { get; init; }

        [CommandOption("--device <ID>")]
        public int DeviceId { get; init; } = 0;

        [CommandOption("--keyword-only")]
        [Description("Disable embeddings + reranker (search tab will use BM25 only).")]
        public bool KeywordOnly { get; init; }

        [CommandOption("--no-refresh")]
        [Description("Skip the silent incremental ingest at TUI startup.")]
        public bool NoRefresh { get; init; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        using var db = Database.Open(s.Db ?? Database.DefaultPath);

        if (!s.NoRefresh) RefreshIndex(db, s.DeviceId);

        IEmbedder? embedder = null;
        IReranker? reranker = null;
        if (!s.KeywordOnly)
        {
            var ecfg = EmbedderConfig.NomicV15(EmbedderConfig.DefaultRoot) with { DeviceId = s.DeviceId };
            if (File.Exists(ecfg.OnnxPath) && File.Exists(ecfg.VocabPath) && db.TryEnableVec(ecfg.Dimensions))
            {
                try { embedder = new LocalEmbedder(ecfg); } catch { embedder = null; }
            }

            var rcfg = RerankerConfig.MsMarcoMiniLmL12(EmbedderConfig.DefaultRoot) with { DeviceId = s.DeviceId };
            if (File.Exists(rcfg.OnnxPath) && File.Exists(rcfg.VocabPath))
            {
                try { reranker = new LocalReranker(rcfg); } catch { reranker = null; }
            }
        }

        try
        {
            var app = new TuiApp(db, embedder, reranker);
            app.Run();
        }
        finally
        {
            embedder?.Dispose();
            reranker?.Dispose();
        }
        return 0;
    }

    /// <summary>
    /// Silent incremental ingest before the TUI takes over the screen. Auto-embeds when
    /// embeddings are configured (mirrors IngestCommand's heuristic). Skips summarisation
    /// because that can take many minutes — the cron path is for that.
    /// </summary>
    private static void RefreshIndex(Database db, int deviceId)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var projects = Path.Combine(home, ".claude", "projects");
        var codexRoot = Path.Combine(home, ".codex", "sessions");
        if (!Directory.Exists(projects)) return;

        IEmbedder? embedder = null;
        try
        {
            var ecfg = EmbedderConfig.NomicV15(EmbedderConfig.DefaultRoot) with { DeviceId = deviceId };
            if (File.Exists(ecfg.OnnxPath) && File.Exists(ecfg.VocabPath)
                && db.TryEnableVec(ecfg.Dimensions)
                && new Core.Storage.VectorRepository(db.Connection).HasEmbeddings(ecfg.ModelId))
            {
                try { embedder = new LocalEmbedder(ecfg); } catch { embedder = null; }
            }

            AnsiConsole.Progress()
                .AutoClear(true)         // wipe the bar before the TUI takes over
                .HideCompleted(true)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new RemainingTimeColumn(),
                    new SpinnerColumn())
                .Start(ctx =>
                {
                    ProgressTask? task = null;
                    var svc = new IngestService(db);
                    svc.Run(new IngestOptions(
                        ProjectsRoot: projects,
                        CodexRoot: codexRoot,
                        Embedder: embedder,
                        Log: _ => { },
                        OnProgress: (scanned, total) =>
                        {
                            if (task is null)
                                task = ctx.AddTask("refreshing index", maxValue: total);
                            task.Value = scanned;
                        }));
                    task?.StopTask();
                });
        }
        finally { embedder?.Dispose(); }
    }
}
