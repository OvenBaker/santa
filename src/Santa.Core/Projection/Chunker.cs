using System.Text;

namespace Santa.Core.Projection;

public sealed class ChunkerOptions
{
    public int TargetTokens { get; init; } = 1200;
    public int MaxTokens { get; init; } = 1800;
    public int OverlapTurns { get; init; } = 1;
}

/// <summary>
/// Greedy-packs Turns into Chunks of roughly TargetTokens each, with a 1-turn overlap between
/// adjacent chunks. A single oversized Turn becomes its own (oversize) chunk — we don't split mid-turn.
/// </summary>
public static class Chunker
{
    public static IEnumerable<Chunk> Pack(IReadOnlyList<Turn> turns, ChunkerOptions? opts = null)
    {
        opts ??= new ChunkerOptions();
        if (turns.Count == 0) yield break;

        int i = 0;
        int prevEndExclusive = 0; // exclusive index of last emitted chunk's end
        while (i < turns.Count)
        {
            int j = i;
            int tokens = 0;
            while (j < turns.Count)
            {
                var add = turns[j].ApproxTokens;
                if (j > i && tokens + add > opts.TargetTokens) break;
                tokens += add;
                j++;
                if (tokens >= opts.MaxTokens) break;
            }
            if (j == i) j = i + 1;

            // Skip if this chunk is fully contained in the previous one (overlap produced no new content).
            if (j > prevEndExclusive)
            {
                yield return BuildChunk(turns, i, j);
                prevEndExclusive = j;
            }

            if (j - i <= 1)
            {
                i = j;
            }
            else
            {
                var next = j - opts.OverlapTurns;
                i = next > i ? next : j;
            }
        }
    }

    private static Chunk BuildChunk(IReadOnlyList<Turn> turns, int from, int toExclusive)
    {
        var sb = new StringBuilder();
        int tokens = 0;
        for (int k = from; k < toExclusive; k++)
        {
            if (sb.Length > 0) sb.Append("\n\n---\n\n");
            sb.Append(turns[k].Render());
            tokens += turns[k].ApproxTokens;
        }
        var first = turns[from];
        var last = turns[toExclusive - 1];
        return new Chunk(
            SessionId: first.SessionId,
            StartSeq: first.Seq,
            EndSeq: last.Seq,
            StartTs: first.UserTimestamp,
            EndTs: last.AssistantTimestamp ?? last.UserTimestamp,
            Text: sb.ToString(),
            ApproxTokens: tokens);
    }
}
