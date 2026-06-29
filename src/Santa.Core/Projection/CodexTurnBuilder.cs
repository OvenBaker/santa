using System.Text;
using Santa.Core.Jsonl;

namespace Santa.Core.Projection;

/// <summary>
/// Folds a Codex event stream into Turns. Codex sessions are far more agentic than Claude's —
/// a single user prompt can drive dozens of <c>agent_message</c> events with no user interjection.
/// The shared <see cref="TurnBuilder"/> would fold all of those into one enormous Turn, which the
/// Chunker then emits as a single oversize chunk (only the first ~2k tokens get embedded).
///
/// So here each <c>agent_message</c> becomes its own Turn: the user prompt rides on the first
/// assistant message after it, and subsequent assistant messages are user-less continuation turns.
/// That gives the Chunker natural message-sized units to pack into ~1200-token chunks.
/// </summary>
public static class CodexTurnBuilder
{
    public static IEnumerable<Turn> Build(IEnumerable<JsonlEvent> events)
    {
        int seq = 0;
        string? pendingUser = null;
        DateTimeOffset? pendingUserTs = null;
        string? sessionId = null;

        foreach (var ev in events)
        {
            sessionId ??= ev.SessionId;

            if (ev.IsUser)
            {
                var text = JoinText(ev);
                // A new prompt with no assistant reply yet flushes the previous pending prompt
                // as a reply-less turn so it isn't silently dropped.
                if (pendingUser is not null)
                {
                    yield return MakeTurn(seq++, sessionId ?? "", pendingUserTs, null, pendingUser, "");
                }
                pendingUser = text;
                pendingUserTs = ev.Timestamp;
            }
            else if (ev.IsAssistant)
            {
                var atext = JoinText(ev);
                if (string.IsNullOrWhiteSpace(atext)) continue;
                var uText = pendingUser ?? "";
                var uTs = pendingUser is not null ? pendingUserTs : null;
                pendingUser = null;   // consume: only the first reply carries the prompt
                pendingUserTs = null;
                yield return MakeTurn(seq++, sessionId ?? "", uTs, ev.Timestamp, uText, atext);
            }
        }

        if (pendingUser is not null)
            yield return MakeTurn(seq, sessionId ?? "", pendingUserTs, null, pendingUser, "");
    }

    private static string JoinText(JsonlEvent ev)
    {
        var sb = new StringBuilder();
        foreach (var b in ev.ContentBlocks)
        {
            if (b.IsText && !string.IsNullOrWhiteSpace(b.Text))
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(b.Text);
            }
        }
        return sb.ToString();
    }

    private static Turn MakeTurn(int seq, string sessionId, DateTimeOffset? uTs, DateTimeOffset? aTs,
        string userText, string assistantText)
    {
        var tokens = TurnBuilder.ApproxTokens(userText) + TurnBuilder.ApproxTokens(assistantText);
        return new Turn(seq, sessionId, uTs, aTs, userText, assistantText, tokens);
    }
}
