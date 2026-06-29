using Santa.Core.Embedding;
using Santa.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Santa.Core.Search;

public sealed record SearchHit(
    long ChunkId,
    string SessionId,
    string? Cwd,
    string? GitBranch,
    string? StartedAt,
    string? FirstUserText,
    string Snippet,
    double FtsRank,
    double VecDistance,
    double FusedScore,
    double? RerankerScore = null,
    string? Status = null,
    int TurnCount = 0,
    int TotalChunks = 0,
    int MatchedChunkCount = 1,
    string? SummaryTitle = null,
    string? SummaryShort = null,
    string? LastActiveAt = null,
    string? Duration = null);

/// <summary>
/// Reciprocal Rank Fusion over BM25 (FTS) + cosine distance (vec0).
/// Either input can be empty; missing one degrades gracefully.
/// </summary>
public sealed class HybridSearch
{
    private const double RrfK = 60.0;

    private readonly Database _db;
    public HybridSearch(Database db) => _db = db;

    public IReadOnlyList<SearchHit> Search(string queryText, IEmbedder? embedder, int limit = 5,
        bool includeCompleted = false, IReranker? reranker = null, int candidatePool = 50,
        int maxPerSession = 1)
    {
        var ftsHits = QueryFts(queryText, includeCompleted, k: candidatePool);
        var vecHits = embedder is null || !_db.VecEnabled
            ? new Dictionary<long, (int rank, double dist)>()
            : QueryVec(embedder, queryText, includeCompleted, k: candidatePool);

        // RRF over BM25 + vec.
        var fused = new Dictionary<long, double>();
        for (int i = 0; i < ftsHits.Count; i++)
            fused[ftsHits[i].ChunkId] = fused.GetValueOrDefault(ftsHits[i].ChunkId) + 1.0 / (RrfK + i + 1);
        foreach (var (id, (rank, _)) in vecHits)
            fused[id] = fused.GetValueOrDefault(id) + 1.0 / (RrfK + rank + 1);

        var ftsByChunk = ftsHits.ToDictionary(h => h.ChunkId);

        // Build the candidate pool. With a reranker we hydrate up to candidatePool to score precisely
        // against the query; without one we just take the top `limit` by RRF.
        var candidateIds = fused
            .OrderByDescending(kv => kv.Value)
            .Take(reranker is null ? limit : candidatePool)
            .Select(kv => kv.Key)
            .ToList();

        var hydrated = new List<SearchHit>(candidateIds.Count);
        foreach (var id in candidateIds)
        {
            ftsByChunk.TryGetValue(id, out var fts);
            vecHits.TryGetValue(id, out var vec);
            var h = Hydrate(id, fused[id], fts, vec.dist);
            if (h is not null) hydrated.Add(h);
        }

        if (reranker is not null && hydrated.Count > 0)
        {
            // Reranker takes (query, full chunk text). The Snippet is good for display but truncated;
            // load the actual chunk text for scoring.
            var docs = hydrated.Select(h => GetChunkText(h.ChunkId) ?? h.Snippet).ToList();
            var scores = reranker.Score(queryText, docs);
            for (int i = 0; i < hydrated.Count; i++)
                hydrated[i] = hydrated[i] with { RerankerScore = scores[i] };
            hydrated = hydrated
                .OrderByDescending(h => h.RerankerScore)
                .ToList();
        }
        else
        {
            hydrated = hydrated.OrderByDescending(h => h.FusedScore).ToList();
        }

        // Session-level dedup: keep at most maxPerSession chunks from each session, then truncate to limit.
        // Same session repeated in the top-N is noise — the user wants to find the conversation, not surf chunks.
        var matchedPerSession = hydrated.GroupBy(h => h.SessionId).ToDictionary(g => g.Key, g => g.Count());
        var perSessionTaken = new Dictionary<string, int>();
        var deduped = new List<SearchHit>(limit);
        foreach (var h in hydrated)
        {
            perSessionTaken.TryGetValue(h.SessionId, out var taken);
            if (taken >= maxPerSession) continue;
            perSessionTaken[h.SessionId] = taken + 1;
            deduped.Add(h with { MatchedChunkCount = matchedPerSession[h.SessionId] });
            if (deduped.Count >= limit) break;
        }

        EnrichSessionMetadata(deduped);
        return deduped;
    }

    private void EnrichSessionMetadata(List<SearchHit> hits)
    {
        if (hits.Count == 0) return;
        var ids = string.Join(",", hits.Select(h => $"'{h.SessionId.Replace("'", "''")}'"));
        var meta = new Dictionary<string, (string Status, int Turns, int Chunks, string? Title, string? Short, string? StartedAt, string? LastActiveAt)>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT s.id, s.status, s.turn_count, s.summary_title, s.summary_short,
                   s.started_at, s.last_active_at,
                   (SELECT COUNT(*) FROM chunks c WHERE c.session_id = s.id) AS chunk_count
            FROM sessions s
            WHERE s.id IN ({ids})
            """;
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            meta[rd.GetString(0)] = (
                rd.GetString(1),
                rd.GetInt32(2),
                rd.GetInt32(7),
                rd.IsDBNull(3) ? null : rd.GetString(3),
                rd.IsDBNull(4) ? null : rd.GetString(4),
                rd.IsDBNull(5) ? null : rd.GetString(5),
                rd.IsDBNull(6) ? null : rd.GetString(6));

        for (int i = 0; i < hits.Count; i++)
        {
            if (meta.TryGetValue(hits[i].SessionId, out var m))
            {
                var dur = Santa.Core.Sessions.DurationFormat.Compact(
                    m.StartedAt is null ? null : DateTimeOffset.Parse(m.StartedAt),
                    m.LastActiveAt is null ? null : DateTimeOffset.Parse(m.LastActiveAt));
                hits[i] = hits[i] with
                {
                    Status = m.Status, TurnCount = m.Turns, TotalChunks = m.Chunks,
                    SummaryTitle = m.Title, SummaryShort = m.Short,
                    LastActiveAt = m.LastActiveAt, Duration = dur,
                };
            }
        }
    }

    private string? GetChunkText(long chunkId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT text FROM chunks WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", chunkId);
        return cmd.ExecuteScalar() as string;
    }

    private record FtsRow(long ChunkId, string SessionId, string? Cwd, string? Branch, string? Started, string? First, string Snippet, double Rank);

    private List<FtsRow> QueryFts(string queryText, bool includeCompleted, int k)
    {
        var results = new List<FtsRow>();
        var fts = FtsEscape(queryText);
        if (string.IsNullOrWhiteSpace(fts)) return results;

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT c.id, s.id, s.cwd, s.git_branch, s.started_at, s.first_user_text,
                   snippet(chunks_fts, 0, '«', '»', '…', 12) AS snip,
                   bm25(chunks_fts) AS rank
            FROM chunks_fts
            JOIN chunks c ON c.id = chunks_fts.rowid
            JOIN sessions s ON s.id = c.session_id
            WHERE chunks_fts MATCH $q
              AND ($all = 1 OR s.status != 'completed')
            ORDER BY rank
            LIMIT $k
            """;
        cmd.Parameters.AddWithValue("$q", fts);
        cmd.Parameters.AddWithValue("$all", includeCompleted ? 1 : 0);
        cmd.Parameters.AddWithValue("$k", k);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            results.Add(new FtsRow(
                rd.GetInt64(0),
                rd.GetString(1),
                rd.IsDBNull(2) ? null : rd.GetString(2),
                rd.IsDBNull(3) ? null : rd.GetString(3),
                rd.IsDBNull(4) ? null : rd.GetString(4),
                rd.IsDBNull(5) ? null : rd.GetString(5),
                rd.GetString(6),
                rd.GetDouble(7)));
        }
        return results;
    }

    private Dictionary<long, (int rank, double dist)> QueryVec(IEmbedder embedder, string queryText, bool includeCompleted, int k)
    {
        var qvec = embedder.Embed(queryText, EmbedKind.Query);
        var vecRepo = new VectorRepository(_db.Connection);
        var hits = vecRepo.Search(qvec, k);

        // Filter by status
        var allowed = includeCompleted ? null : ActiveChunkIds(hits.Select(h => h.ChunkId).ToList());
        var dict = new Dictionary<long, (int, double)>();
        int rank = 0;
        foreach (var (id, dist) in hits)
        {
            if (allowed is not null && !allowed.Contains(id)) continue;
            dict[id] = (rank, dist);
            rank++;
        }
        return dict;
    }

    private HashSet<long> ActiveChunkIds(List<long> ids)
    {
        if (ids.Count == 0) return new();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT c.id FROM chunks c
            JOIN sessions s ON s.id = c.session_id
            WHERE s.status != 'completed' AND c.id IN ({string.Join(",", ids)})
            """;
        using var rd = cmd.ExecuteReader();
        var set = new HashSet<long>();
        while (rd.Read()) set.Add(rd.GetInt64(0));
        return set;
    }

    private SearchHit? Hydrate(long chunkId, double fused, FtsRow? fts, double vecDist)
    {
        if (fts is not null)
            return new SearchHit(chunkId, fts.SessionId, fts.Cwd, fts.Branch, fts.Started, fts.First, fts.Snippet, fts.Rank, vecDist, fused);

        // vec-only hit: fetch session metadata
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.cwd, s.git_branch, s.started_at, s.first_user_text, substr(c.text, 1, 240)
            FROM chunks c JOIN sessions s ON s.id = c.session_id WHERE c.id = $id
            """;
        cmd.Parameters.AddWithValue("$id", chunkId);
        using var rd = cmd.ExecuteReader();
        if (!rd.Read()) return null;
        return new SearchHit(
            chunkId,
            rd.GetString(0),
            rd.IsDBNull(1) ? null : rd.GetString(1),
            rd.IsDBNull(2) ? null : rd.GetString(2),
            rd.IsDBNull(3) ? null : rd.GetString(3),
            rd.IsDBNull(4) ? null : rd.GetString(4),
            rd.GetString(5),
            FtsRank: 0,
            VecDistance: vecDist,
            FusedScore: fused);
    }

    /// <summary>
    /// Turns a natural-language query into an FTS5 expression that won't choke on '?', '"',
    /// ':', '*', etc. and won't require every word to appear. Tokens are quoted (so dots,
    /// slashes, hyphens in identifiers are treated as a phrase) and OR'd together — BM25
    /// will surface chunks with the rarer / higher-IDF terms (e.g. "Kuzu") on top.
    /// Stop-words are dropped so they don't dominate the OR.
    /// </summary>
    private static string FtsEscape(string q)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace(q, @"[^\w\s\-./]", " ");
        var tokens = cleaned
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 1)
            .Where(t => !StopWords.Contains(t.ToLowerInvariant()))
            .Select(t => "\"" + t.Replace("\"", "\"\"") + "\"")
            .ToList();
        return tokens.Count == 0 ? "" : string.Join(" OR ", tokens);
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "but", "by", "did", "do", "does", "for",
        "from", "had", "has", "have", "how", "i", "in", "into", "is", "it", "its", "of",
        "on", "or", "our", "should", "so", "than", "that", "the", "their", "them", "these",
        "they", "this", "to", "was", "we", "were", "what", "when", "where", "which", "who",
        "why", "will", "with", "you", "your"
    };
}
