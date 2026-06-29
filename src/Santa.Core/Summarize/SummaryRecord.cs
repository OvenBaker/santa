namespace Santa.Core.Summarize;

public sealed record SessionSummary(
    string Title,
    string Short,
    string Long,
    string Model,
    DateTimeOffset GeneratedAt,
    int TurnCountAtGeneration);
