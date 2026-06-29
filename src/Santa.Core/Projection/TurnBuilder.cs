using System.Text;
using System.Text.RegularExpressions;
using Santa.Core.Jsonl;

namespace Santa.Core.Projection;

/// <summary>
/// Folds a stream of JSONL events into Turns. Drops tool_use, tool_result, thinking,
/// system events, attachments, snapshots — keeps only user prompts + assistant prose.
/// </summary>
public static class TurnBuilder
{
    private static readonly Regex SlashCommandRe = new(
        @"<command-name>\s*(/[\w\-:]+)\s*</command-name>",
        RegexOptions.Compiled);

    public static IEnumerable<Turn> Build(IEnumerable<JsonlEvent> events)
    {
        int seq = 0;
        string? pendingUserText = null;
        DateTimeOffset? pendingUserTs = null;
        var assistantBuf = new StringBuilder();
        DateTimeOffset? assistantTs = null;
        string? sessionId = null;

        foreach (var ev in events)
        {
            sessionId ??= ev.SessionId;

            if (ev.IsUser)
            {
                // Flush any in-progress turn before starting a new one.
                if (pendingUserText is not null)
                {
                    yield return MakeTurn(seq++, sessionId ?? "", pendingUserTs, assistantTs,
                        pendingUserText, assistantBuf.ToString());
                    assistantBuf.Clear();
                    assistantTs = null;
                }

                var text = ExtractUserText(ev);
                if (text is null)
                {
                    // user message that's purely tool_result feedback — not a real prompt
                    pendingUserText = null;
                    pendingUserTs = null;
                    continue;
                }
                pendingUserText = text;
                pendingUserTs = ev.Timestamp;
            }
            else if (ev.IsAssistant && pendingUserText is not null)
            {
                foreach (var b in ev.ContentBlocks)
                {
                    if (b.IsText && !string.IsNullOrWhiteSpace(b.Text))
                    {
                        if (assistantBuf.Length > 0) assistantBuf.Append("\n\n");
                        assistantBuf.Append(b.Text);
                    }
                }
                assistantTs ??= ev.Timestamp;
            }
            // everything else (system, file-history-snapshot, attachment, last-prompt,
            // permission-mode, sidechain) is intentionally ignored
        }

        if (pendingUserText is not null)
        {
            yield return MakeTurn(seq, sessionId ?? "", pendingUserTs, assistantTs,
                pendingUserText, assistantBuf.ToString());
        }
    }

    private static Turn MakeTurn(int seq, string sessionId, DateTimeOffset? uTs, DateTimeOffset? aTs,
        string userText, string assistantText)
    {
        var tokens = ApproxTokens(userText) + ApproxTokens(assistantText);
        return new Turn(seq, sessionId, uTs, aTs, userText, assistantText, tokens);
    }

    /// <summary>
    /// Returns the user-intent text for a user event, or null if the event is just tool_result feedback.
    /// </summary>
    public static string? ExtractUserText(JsonlEvent ev)
    {
        // Walk content blocks; concat text, ignore tool_result.
        var sb = new StringBuilder();
        foreach (var b in ev.ContentBlocks)
        {
            if (b.IsText && !string.IsNullOrWhiteSpace(b.Text))
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(b.Text);
            }
        }
        if (sb.Length == 0) return null;

        var raw = sb.ToString();
        return NormalizeSlashCommand(raw);
    }

    /// <summary>
    /// User-prompt strings carry slash commands wrapped as
    /// <c>&lt;command-message&gt;…&lt;/command-message&gt;&lt;command-name&gt;/foo&lt;/command-name&gt;</c>.
    /// Reduce to a clean "/foo" prefix so search hits are readable.
    /// </summary>
    private static string NormalizeSlashCommand(string raw)
    {
        var m = SlashCommandRe.Match(raw);
        if (!m.Success) return raw;
        var cmd = m.Groups[1].Value;
        // Strip <command-*> tags entirely, prefix with "/cmd ".
        var stripped = Regex.Replace(raw, @"<command-[^>]*>.*?</command-[^>]*>", "", RegexOptions.Singleline).Trim();
        return stripped.Length == 0 ? cmd : $"{cmd} {stripped}";
    }

    /// <summary>Char-quartile heuristic; swap for a real tokenizer when embeddings land.</summary>
    public static int ApproxTokens(string s) => s.Length == 0 ? 0 : (s.Length + 3) / 4;
}
