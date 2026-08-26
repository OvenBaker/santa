namespace Santa.Core.Usage;

/// <summary>Per-model token usage rolled up over one session's transcript.</summary>
public sealed record ModelUsage(
    string Model,
    int Turns,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens)
{
    public double CostUsd => Pricing.Cost(Model, InputTokens, OutputTokens, CacheReadTokens, CacheWriteTokens);
}

/// <summary>
/// API-equivalent $/MTok weights. Duplicated by design with ~/tools/burn-sentinel/sentinel.py
/// and ~/tools/fable-usage (the cross-check oracle) — keep the three in sync when prices move.
/// Cache reads bill at 0.1× input, cache writes at 1.25× input.
/// </summary>
public static class Pricing
{
    private static readonly (string Key, double In, double Out)[] Table =
    [
        ("fable", 10, 50), ("mythos", 10, 50), ("opus", 5, 25),
        ("sonnet-4-6", 3, 15), ("sonnet", 2, 10), ("haiku", 1, 5),
    ];

    public static (double In, double Out) PerMTok(string? model)
    {
        foreach (var (key, i, o) in Table)
            if (model?.Contains(key, StringComparison.OrdinalIgnoreCase) == true)
                return (i, o);
        return (5, 25);
    }

    public static double Cost(string? model, long input, long output, long cacheRead, long cacheWrite)
    {
        var (i, o) = PerMTok(model);
        return (input * i + output * o + cacheRead * i * 0.1 + cacheWrite * i * 1.25) / 1e6;
    }
}
