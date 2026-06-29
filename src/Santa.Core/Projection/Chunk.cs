namespace Santa.Core.Projection;

public sealed record Chunk(
    string SessionId,
    int StartSeq,
    int EndSeq,
    DateTimeOffset? StartTs,
    DateTimeOffset? EndTs,
    string Text,
    int ApproxTokens);
