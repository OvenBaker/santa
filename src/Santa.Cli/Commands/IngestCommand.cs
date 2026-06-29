using System.ComponentModel;
using Santa.Core.Embedding;
using Santa.Core.Ingest;
using Santa.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Santa.Cli.Commands;

public sealed class IngestCommand : AsyncCommand<IngestCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--full")]
        [Description("Re-ingest every JSONL file regardless of cursor.")]
        public bool Full { get; init; }

        [CommandOption("--dry-run")]
        [Description("Print projected chunks instead of writing to the index.")]
        public bool DryRun { get; init; }

        [CommandOption("--limit <N>")]
        [Description("Stop after ingesting N files (useful with --dry-run).")]
        public int? Limit { get; init; }

        [CommandOption("--projects <DIR>")]
        [Description("Override projects root (default: ~/.claude/projects).")]
        public string? Projects { get; init; }

        [CommandOption("--codex <DIR>")]
        [Description("Override Codex rollout root (default: ~/.codex/sessions).")]
        public string? Codex { get; init; }

        [CommandOption("--no-codex")]
        [Description("Skip Codex rollouts; ingest Claude sessions only.")]
        public bool NoCodex { get; init; }

        [CommandOption("--db <PATH>")]
        [Description("Override database path (default: ~/.claude/search/index.db).")]
        public string? Db { get; init; }

        [CommandOption("--embed")]
        [Description("Compute embeddings + write to chunk_vec. Requires CUDA + downloaded model + sqlite-vec.")]
        public bool Embed { get; init; }

        [CommandOption("--metadata-only")]
        [Description("Refresh per-session metadata (cwd, git_branch, derived_branches, …) without re-chunking or re-embedding.")]
        public bool MetadataOnly { get; init; }

        [CommandOption("--summarize")]
        [Description("After ingest, run summarise on any session whose summary is missing or stale (uses Max via claude -p).")]
        public bool Summarize { get; init; }

        [CommandOption("--summarize-concurrency <N>")]
        [DefaultValue(4)]
        public int SummarizeConcurrency { get; init; }

        [CommandOption("--device <ID>")]
        public int DeviceId { get; init; } = 0;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken cancellationToken)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var projects = s.Projects ?? Path.Combine(home, ".claude", "projects");

        if (!Directory.Exists(projects))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Projects root not found:[/] {projects}");
            return 2;
        }

        var codexRoot = s.NoCodex ? null : (s.Codex ?? Path.Combine(home, ".codex", "sessions"));

        var dbPath = s.Db ?? Database.DefaultPath;
        AnsiConsole.MarkupLineInterpolated($"[grey]projects:[/] {projects}");
        if (codexRoot is not null && Directory.Exists(codexRoot))
            AnsiConsole.MarkupLineInterpolated($"[grey]codex:   [/] {codexRoot}");
        AnsiConsole.MarkupLineInterpolated($"[grey]index:   [/] {dbPath}{(s.DryRun ? "  [yellow](dry-run)[/]" : "")}");

        using var db = Database.Open(dbPath);

        // Auto-enable embedding when the prerequisites are clearly in place AND there's evidence
        // we've embedded before. Stops new chunks from silently going un-embedded on subsequent
        // ingests after the user opted into embeddings the first time.
        var embedderCfg = EmbedderConfig.NomicV15(EmbedderConfig.DefaultRoot) with { DeviceId = s.DeviceId };
        bool embedRequested = s.Embed;
        if (!embedRequested
            && File.Exists(embedderCfg.OnnxPath) && File.Exists(embedderCfg.VocabPath)
            && db.TryEnableVec(embedderCfg.Dimensions)
            && new Core.Storage.VectorRepository(db.Connection).HasEmbeddings(embedderCfg.ModelId))
        {
            embedRequested = true;
            AnsiConsole.MarkupLineInterpolated($"[grey]embedder:[/] auto-on (existing embeddings detected for {embedderCfg.ModelId})");
        }

        IEmbedder? embedder = null;
        if (embedRequested)
        {
            embedder = new LocalEmbedder(embedderCfg);
            if (!db.TryEnableVec(embedderCfg.Dimensions))
            {
                AnsiConsole.MarkupLine("[red]sqlite-vec extension failed to load. Run `santa models download`.[/]");
                return 3;
            }
            AnsiConsole.MarkupLineInterpolated($"[grey]embedder:[/] {embedder.ModelId} (dim={embedder.Dimensions}, device={s.DeviceId})");
        }

        var svc = new IngestService(db);

        IngestStats stats;
        try
        {
            stats = svc.Run(new IngestOptions(
                ProjectsRoot: projects,
                CodexRoot: codexRoot,
                ForceFull: s.Full,
                Limit: s.Limit,
                DryRun: s.DryRun,
                MetadataOnly: s.MetadataOnly,
                Embedder: embedder,
                Log: AnsiConsole.WriteLine));
        }
        finally { embedder?.Dispose(); }

        var table = new Table().AddColumns("metric", "count");
        table.AddRow("scanned", stats.FilesScanned.ToString());
        table.AddRow("ingested", stats.FilesIngested.ToString());
        table.AddRow("skipped", stats.FilesSkipped.ToString());
        table.AddRow("sessions touched", stats.SessionsTouched.ToString());
        table.AddRow("chunks written", stats.ChunksWritten.ToString());
        AnsiConsole.Write(table);

        // Same auto-detection for summaries: if any session has been summarised before AND we're
        // running on the Max plan (no API key set), refresh stale summaries without needing the
        // explicit --summarize flag every time.
        var summarizeRequested = s.Summarize;
        if (!summarizeRequested && stats.SessionsTouched > 0
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))
            && AnyExistingSummary(db))
        {
            summarizeRequested = true;
            AnsiConsole.MarkupLine("[grey]summariser:[/] auto-on (existing summaries detected; Max plan)");
        }

        if (summarizeRequested && stats.SessionsTouched > 0)
        {
            await SummariseTouchedSessions(db, s.SummarizeConcurrency, cancellationToken);
        }
        return 0;
    }

    private static bool AnyExistingSummary(Database db)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM sessions WHERE summary_at IS NOT NULL)";
        return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
    }

    private static async Task SummariseTouchedSessions(Database db, int concurrency, CancellationToken ct)
    {
        // ANTHROPIC_API_KEY guard only applies to the claude -p backend; codex uses ChatGPT auth.
        try { if (!Core.Classify.LlmRunner.CodexSelected) Core.Summarize.Summarizer.EnsureSubscriptionMode(); }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]skipping summarise:[/] {Markup.Escape(ex.Message)}");
            return;
        }

        var summarizer = new Core.Summarize.Summarizer(db);
        var pending = new List<Core.Sessions.SessionInfo>();
        using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, project_path, cwd, git_branch, started_at, ended_at,
                       message_count, turn_count, status, first_user_text, last_user_text, derived_branches
                FROM sessions
                WHERE status = 'active'
                ORDER BY started_at DESC
                """;
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var info = new Core.Sessions.SessionInfo(
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
                    DerivedBranches: rd.IsDBNull(11) ? null : Core.Sessions.SessionLookup.ParseBranches(rd.GetString(11)));
                if (summarizer.NeedsSummary(info.Id, info.TurnCount))
                    pending.Add(info);
            }
        }
        if (pending.Count == 0) return;

        AnsiConsole.MarkupLineInterpolated($"[grey]summarising {pending.Count} session(s) with concurrency={concurrency}…[/]");
        var sem = new SemaphoreSlim(Math.Max(1, concurrency));
        var tasks = pending.Select(async sess =>
        {
            await sem.WaitAsync(ct);
            try { await summarizer.SummariseAsync(sess, false, _ => { }, ct); }
            catch { /* logged elsewhere; don't fail ingest */ }
            finally { sem.Release(); }
        });
        await Task.WhenAll(tasks);
    }
}
