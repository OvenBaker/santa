using System.ComponentModel;
using Microsoft.Data.Sqlite;
using Santa.Cli;
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
    private const int DatabaseLockTimeoutSeconds = 2;

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--db <PATH>")]
        public string? Db { get; init; }

        [CommandOption("--device <ID>")]
        public int DeviceId { get; init; } = 0;

        [CommandOption("--provider <PROVIDER>")]
        [Description("Inference provider: cuda (default), cpu, or keyword-only.")]
        [DefaultValue("cuda")]
        public string Provider { get; init; } = "cuda";

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
        if (!InferenceProviderOption.TryResolve(s.Provider, false, out var provider)) return 2;
        var dbPath = s.Db ?? Database.DefaultPath;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var projects = Path.Combine(home, ".claude", "projects");
        var codexRoot = Path.Combine(home, ".codex", "sessions");
        if (!Directory.Exists(projects))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Projects root not found:[/] {projects}");
            return 2;
        }

        string? deferredEmbedding = null;
        using var refreshLock = TryAcquireRefreshLock(dbPath);
        if (refreshLock is null)
            return ReportSkipped(s.Quiet, startedAt, "refresh-already-running");

        // The GPU gates EMBEDDING ONLY. Ingest is pure file/SQLite work and summarisation runs `claude -p`
        // on the Max plan — neither touches the card. Refusing the whole run when the lease is unavailable is
        // how a cron PATH that could not see nvidia-smi silently stopped indexing for 124 consecutive runs,
        // taking every cockpit pane title down with it (2026-08-25). A busy or absent GPU now costs vector
        // freshness for this run and nothing else.
        GpuCourtesyLease? gpuLease = null;
        if (provider == InferenceProvider.Cuda)
        {
            var acquisition = await new GpuCourtesyGate(s.DeviceId).TryAcquireAsync(ct);
            if (acquisition.Lease is null)
            {
                deferredEmbedding = acquisition.Reason;
                provider = null;
            }
            else gpuLease = acquisition.Lease;
        }
        using var gpuLeaseScope = gpuLease;

        try
        {
            // Refresh is an unattended best-effort job. Do not let Microsoft.Data.Sqlite's normal
            // busy retries keep a cron process alive (and potentially holding CUDA memory) behind
            // another writer. Interactive commands retain the normal provider timeout.
            using var db = Database.Open(dbPath, DatabaseLockTimeoutSeconds);

            // Ingest with auto-embed (mirrors the IngestCommand heuristic).
            IEmbedder? embedder = null;
            try
            {
                var ecfg = EmbedderConfig.NomicV15(EmbedderConfig.DefaultRoot);
                if (provider is not null
                    && File.Exists(ecfg.OnnxPath) && File.Exists(ecfg.VocabPath)
                    && db.TryEnableVec(ecfg.Dimensions)
                    && new VectorRepository(db.Connection).HasEmbeddings(ecfg.ModelId))
                {
                    try
                    {
                        embedder = new LocalEmbedder(
                            InferenceProviderOption.Apply(ecfg, provider.Value, s.DeviceId));
                    }
                    catch (Exception ex)
                    {
                        return ReportFailed(s.Quiet, startedAt, provider.Value, ex);
                    }
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
                // A run that skipped embedding must SAY so: it is otherwise indistinguishable in the log
                // from a complete one, and the vector index quietly falls behind.
                var embedNote = deferredEmbedding is null
                    ? string.Empty
                    : $" embedding=deferred:{deferredEmbedding.Replace(' ', '-').ToLowerInvariant()}";
                if (s.Quiet)
                {
                    Console.WriteLine(
                        $"{startedAt:O} ok ingested={stats.SessionsTouched} chunks+={stats.ChunksWritten} summarised={summarised}{embedNote} elapsed={elapsed:F1}s");
                }
                else
                {
                    if (deferredEmbedding is not null)
                        AnsiConsole.MarkupLineInterpolated(
                            $"[yellow]embedding deferred:[/] {Markup.Escape(deferredEmbedding)} — index updated without new vectors");
                    AnsiConsole.MarkupLineInterpolated($"[grey]done in {elapsed:F1}s[/]");
                }
                return 0;
            }
            finally { embedder?.Dispose(); }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            return ReportSkipped(s.Quiet, startedAt, "database-busy");
        }
    }

    private static FileStream? TryAcquireRefreshLock(string dbPath)
    {
        var fullDbPath = Path.GetFullPath(dbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDbPath)!);
        try
        {
            // The file intentionally remains after disposal: FileShare.None is the live lease, so a
            // crash releases it automatically and a stale on-disk file never blocks a later refresh.
            return new FileStream(fullDbPath + ".refresh.lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static int ReportSkipped(bool quiet, DateTimeOffset startedAt, string reason)
    {
        var elapsed = (DateTimeOffset.Now - startedAt).TotalSeconds;
        if (quiet)
            Console.WriteLine($"{startedAt:O} skipped reason={reason} elapsed={elapsed:F1}s");
        else
            AnsiConsole.MarkupLineInterpolated($"[yellow]skipped:[/] {reason} [grey]({elapsed:F1}s)[/]");
        return 0;
    }

    private static int ReportFailed(
        bool quiet,
        DateTimeOffset startedAt,
        InferenceProvider provider,
        Exception ex)
    {
        var elapsed = (DateTimeOffset.Now - startedAt).TotalSeconds;
        if (quiet)
            Console.Error.WriteLine(
                $"{startedAt:O} failed provider={provider.ToString().ToLowerInvariant()} error={ex.Message.ReplaceLineEndings(" ")} elapsed={elapsed:F1}s");
        else
            InferenceProviderOption.ReportInitializationFailure(provider, ex);
        return 3;
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
