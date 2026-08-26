using System.Text.Json;
using Santa.Core.Jsonl;

namespace Santa.Core.Usage;

/// <summary>
/// Accumulates <c>message.usage</c> across a Claude transcript's assistant events.
/// Streaming appends repeat a message id with growing usage totals, so events are
/// keyed by message id and the largest-output observation wins (the same dedup the
/// burn sentinel applies).
/// </summary>
public sealed class UsageAccumulator
{
    private sealed record Obs(string Model, long In, long Out, long Cr, long Cw);

    private readonly Dictionary<string, Obs> _byMessage = new();

    public void Add(JsonlEvent ev)
    {
        if (!ev.IsAssistant || ev.Raw.ValueKind != JsonValueKind.Object) return;
        if (!ev.Raw.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return;
        if (!msg.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return;

        string model = msg.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString() ?? "" : "";
        if (model.Length == 0 || model.Contains("synthetic", StringComparison.OrdinalIgnoreCase)) return;

        string key = msg.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString() ?? "" : "";
        if (key.Length == 0) key = ev.Uuid ?? Guid.NewGuid().ToString();

        var obs = new Obs(model,
            GetLong(usage, "input_tokens"),
            GetLong(usage, "output_tokens"),
            GetLong(usage, "cache_read_input_tokens"),
            GetLong(usage, "cache_creation_input_tokens"));

        if (!_byMessage.TryGetValue(key, out var prev) || obs.Out >= prev.Out)
            _byMessage[key] = obs;
    }

    public IReadOnlyList<ModelUsage> ToRollups() =>
        _byMessage.Values
            .GroupBy(o => o.Model)
            .Select(g => new ModelUsage(
                Model: g.Key,
                Turns: g.Count(),
                InputTokens: g.Sum(o => o.In),
                OutputTokens: g.Sum(o => o.Out),
                CacheReadTokens: g.Sum(o => o.Cr),
                CacheWriteTokens: g.Sum(o => o.Cw)))
            .OrderByDescending(u => u.CostUsd)
            .ToList();

    private static long GetLong(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt64(out var n) ? n : 0;
}
