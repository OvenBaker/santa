using Santa.Core.Embedding;
using Santa.Core.Projection;
using Santa.Core.Storage;

namespace Santa.Core.Ingest;

public sealed record IngestOptions(
    string ProjectsRoot,
    /// <summary>Codex rollout root (~/.codex/sessions). Null/missing → Codex ingest is skipped.</summary>
    string? CodexRoot = null,
    bool ForceFull = false,
    int? Limit = null,
    bool DryRun = false,
    bool MetadataOnly = false,           // refresh session-level fields without re-chunking/re-embedding
    IEmbedder? Embedder = null,
    Action<string>? Log = null,
    /// <summary>(scanned, total) — fires for every file as it's scanned, including skips.</summary>
    Action<int, int>? OnProgress = null);

public sealed record IngestStats(int FilesScanned, int FilesIngested, int FilesSkipped, int SessionsTouched, int ChunksWritten);

public sealed class IngestService
{
    private readonly Database _db;
    private readonly ChunkerOptions _chunkerOpts;

    public IngestService(Database db, ChunkerOptions? chunkerOpts = null)
    {
        _db = db;
        _chunkerOpts = chunkerOpts ?? new ChunkerOptions();
    }

    public IngestStats Run(IngestOptions opts)
    {
        var sessions = new SessionRepository(_db.Connection);
        var chunks = new ChunkRepository(_db.Connection);
        var cursors = new FileCursorRepository(_db.Connection);
        var vectors = opts.Embedder is not null ? new VectorRepository(_db.Connection) : null;
        var log = opts.Log ?? (_ => { });
        if (opts.Embedder is not null && !_db.VecEnabled)
            throw new InvalidOperationException("Embedder set but sqlite-vec is not loaded; call db.TryEnableVec(dim) first.");

        int scanned = 0, ingested = 0, skipped = 0, sessionsTouched = 0, chunksWritten = 0;

        // Materialise the file list up front so progress consumers know the total.
        // Claude projects first, then Codex rollouts (each tagged with its provider).
        var files = DiscoverJsonl(opts.ProjectsRoot).Select(p => (Path: p, Provider: "claude-code"))
            .Concat(DiscoverCodex(opts.CodexRoot).Select(p => (Path: p, Provider: "codex")))
            .ToList();
        int total = files.Count;

        foreach (var (path, provider) in files)
        {
            if (opts.Limit is int lim && ingested >= lim) break;
            scanned++;
            opts.OnProgress?.Invoke(scanned, total);

            var fi = new FileInfo(path);
            var size = fi.Length;
            var mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();

            var existing = cursors.Get(path);
            if (!opts.ForceFull && !opts.MetadataOnly && existing is not null
                && existing.Size == size && existing.MtimeUnix == mtime)
            {
                skipped++;
                continue;
            }

            var agg = new SessionAggregate(path, provider);
            try { agg.Build(); }
            catch (Exception ex)
            {
                log($"  ! parse failed {path}: {ex.Message}");
                continue;
            }

            if (agg.IsInternal)
            {
                // Echo of our own claude -p invocation. Drop any existing record and skip.
                if (!opts.DryRun)
                {
                    sessions.Delete(agg.SessionId);
                    cursors.Upsert(new FileCursor(path, size, mtime, size, -1, agg.SessionId));
                }
                skipped++;
                continue;
            }

            if (agg.Turns.Count == 0)
            {
                // still update cursor so we don't re-scan empty/system-only sessions every time
                if (!opts.DryRun)
                    cursors.Upsert(new FileCursor(path, size, mtime, size, -1, agg.SessionId));
                skipped++;
                continue;
            }

            var packed = Chunker.Pack(agg.Turns, _chunkerOpts).ToList();

            log($"  {Path.GetFileName(path)}  session={agg.SessionId[..8]}  turns={agg.Turns.Count}  chunks={packed.Count}");

            if (opts.DryRun)
            {
                ingested++; sessionsTouched++; chunksWritten += packed.Count;
                foreach (var c in packed) DryRunPrint(c, log);
                continue;
            }

            using (var tx = _db.BeginTransaction())
            {
                var derivedJson = agg.DerivedBranches.Count == 0
                    ? null
                    : System.Text.Json.JsonSerializer.Serialize(agg.DerivedBranches);

                sessions.Upsert(new SessionRow(
                    Id: agg.SessionId,
                    ProjectPath: agg.ProjectDirName,
                    Cwd: agg.Cwd,
                    GitBranch: agg.GitBranch,
                    StartedAt: agg.StartedAt,
                    EndedAt: agg.EndedAt,
                    MessageCount: agg.MessageCount,
                    TurnCount: agg.Turns.Count,
                    FirstUserText: agg.Turns[0].UserText,
                    LastUserText: agg.Turns[^1].UserText,
                    DerivedBranchesJson: derivedJson,
                    LastActiveAt: agg.LastActiveAt,
                    Provider: agg.Provider), tx);

                if (!opts.MetadataOnly)
                {
                    // vec0 doesn't FK-cascade. Wipe vec rows for this session BEFORE we delete
                    // the chunks they reference, otherwise we'd leave orphaned vec rows pointing
                    // at deleted chunk ids.
                    if (_db.VecEnabled)
                        new VectorRepository(_db.Connection).DeleteForSession(agg.SessionId, tx);

                    sessions.DeleteChunks(agg.SessionId, tx);
                    var insertedIds = new List<long>(packed.Count);
                    foreach (var c in packed)
                    {
                        var id = chunks.Insert(c, tx);
                        insertedIds.Add(id);
                        chunksWritten++;
                    }

                    if (opts.Embedder is { } embedder && vectors is not null)
                    {
                        var texts = packed.Select(c => c.Text).ToList();
                        var vecs = embedder.EmbedBatch(texts, EmbedKind.Document);
                        for (int k = 0; k < insertedIds.Count; k++)
                            vectors.Upsert(insertedIds[k], vecs[k], embedder.ModelId, tx);
                    }

                    cursors.Upsert(new FileCursor(path, size, mtime, size,
                        agg.Turns[^1].Seq, agg.SessionId), tx);
                }

                tx.Commit();
            }

            ingested++;
            sessionsTouched++;
        }

        return new IngestStats(scanned, ingested, skipped, sessionsTouched, chunksWritten);
    }

    private static IEnumerable<string> DiscoverJsonl(string root)
    {
        if (!Directory.Exists(root)) yield break;
        // Top-level project dirs only; skip subagent sidechains for v1.
        foreach (var projectDir in Directory.EnumerateDirectories(root))
        {
            foreach (var file in Directory.EnumerateFiles(projectDir, "*.jsonl", SearchOption.TopDirectoryOnly))
                yield return file;
        }
    }

    /// <summary>
    /// Codex rollouts live under date-partitioned dirs: &lt;root&gt;/YYYY/MM/DD/rollout-*.jsonl.
    /// We recurse the whole tree and match the rollout prefix.
    /// </summary>
    private static IEnumerable<string> DiscoverCodex(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return Enumerable.Empty<string>();
        return Directory.EnumerateFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories);
    }

    private static void DryRunPrint(Chunk c, Action<string> log)
    {
        var preview = c.Text.Length > 600 ? c.Text[..600] + "…" : c.Text;
        log($"    --- chunk seq {c.StartSeq}..{c.EndSeq}  ~{c.ApproxTokens}t ---");
        foreach (var line in preview.Split('\n'))
            log($"    {line}");
    }
}
