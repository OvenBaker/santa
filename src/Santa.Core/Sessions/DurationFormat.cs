namespace Santa.Core.Sessions;

public static class DurationFormat
{
    /// <summary>
    /// Compact human-readable span. Returns "" when either bound is missing or the span is &lt; 1m,
    /// since sub-minute spans are noise (single-prompt sessions, instant slash commands).
    /// </summary>
    public static string Compact(DateTimeOffset? from, DateTimeOffset? to)
    {
        if (from is null || to is null) return "";
        var span = to.Value - from.Value;
        if (span.TotalSeconds < 60) return "";
        if (span.TotalMinutes < 60)  return $"{(int)span.TotalMinutes}m";
        if (span.TotalHours < 24)    return $"{(int)span.TotalHours}h";
        if (span.TotalDays < 14)     return $"{(int)span.TotalDays}d";
        return $"{(int)(span.TotalDays / 7)}w";
    }
}
