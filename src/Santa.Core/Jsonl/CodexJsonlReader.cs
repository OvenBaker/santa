using System.Text.Json;

namespace Santa.Core.Jsonl;

/// <summary>
/// Reads a Codex CLI rollout file (~/.codex/sessions/YYYY/MM/DD/rollout-&lt;ts&gt;-&lt;uuid&gt;.jsonl)
/// and projects each line into the same <see cref="JsonlEvent"/> shape the Claude pipeline consumes,
/// so TurnBuilder/Chunker/ingest work unchanged.
///
/// Codex line shape is <c>{ "type", "timestamp", "payload" }</c>. We surface only what the
/// downstream pipeline needs:
///   • session_meta   → carries the session id, cwd, git branch (non-conversational)
///   • turn_context   → carries cwd (non-conversational; a fallback for session_meta)
///   • event_msg/user_message  → a real user prompt (Type="user")
///   • event_msg/agent_message → assistant prose (Type="assistant")
///   • event_msg/item_completed → the SAME two turns in the paginated history format Codex adopted
///     at ~0.147 (session_meta.history_mode "paginated"), where item.type is UserMessage/AgentMessage.
///     Rollouts in that format carry no user_message/agent_message at all, so without this branch they
///     projected zero turns and never entered the index (2026-08-25).
/// Everything else (response_item tool I/O, token_count, task_started/complete) is surfaced with
/// its raw type and ignored by TurnBuilder.
/// </summary>
public sealed class CodexJsonlReader
{
    private readonly string _path;

    public CodexJsonlReader(string path) => _path = path;

    public IEnumerable<(long StartOffset, long EndOffset, JsonlEvent Event)> Read(long startOffset = 0)
    {
        using var raw = new FileStream(_path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (startOffset > 0) raw.Seek(startOffset, SeekOrigin.Begin);
        using var fs = new BufferedStream(raw, 64 * 1024);

        var lineStart = startOffset;
        var line = new List<byte>(1024);
        int b;
        long pos = startOffset;
        while ((b = fs.ReadByte()) != -1)
        {
            pos++;
            if (b == '\n')
            {
                var bytes = TrimTrailingCr(line);
                if (bytes.Length > 0)
                {
                    var ev = CodexParser.Parse(bytes);
                    if (ev is not null)
                        yield return (lineStart, pos, ev);
                }
                line.Clear();
                lineStart = pos;
            }
            else
            {
                line.Add((byte)b);
            }
        }
    }

    private static ReadOnlySpan<byte> TrimTrailingCr(List<byte> buf)
    {
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buf);
        if (span.Length > 0 && span[^1] == (byte)'\r') span = span[..^1];
        return span;
    }
}

internal static class CodexParser
{
    private static readonly JsonDocumentOptions DocOpts = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static JsonlEvent? Parse(ReadOnlySpan<byte> utf8Line)
    {
        if (utf8Line.IsEmpty) return null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(utf8Line.ToArray(), DocOpts); }
        catch (JsonException) { return null; }

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) { doc.Dispose(); return null; }

        var lineType = GetString(root, "type") ?? "unknown";
        var ts = GetDateTimeOffset(root, "timestamp");
        root.TryGetProperty("payload", out var payload);
        var hasPayload = payload.ValueKind == JsonValueKind.Object;

        switch (lineType)
        {
            case "session_meta":
            {
                var id = hasPayload ? GetString(payload, "id") : null;
                var cwd = hasPayload ? GetString(payload, "cwd") : null;
                string? branch = null;
                if (hasPayload && payload.TryGetProperty("git", out var git) && git.ValueKind == JsonValueKind.Object)
                    branch = GetString(git, "branch");
                var version = hasPayload ? GetString(payload, "cli_version") : null;
                return new JsonlEvent("session_meta", id, null, null, ts, cwd, branch, version, null,
                    Array.Empty<ContentBlock>(), root.Clone());
            }
            case "turn_context":
            {
                var cwd = hasPayload ? GetString(payload, "cwd") : null;
                return new JsonlEvent("turn_context", null, null, null, ts, cwd, null, null, null,
                    Array.Empty<ContentBlock>(), root.Clone());
            }
            case "event_msg":
            {
                var pt = hasPayload ? GetString(payload, "type") : null;
                if (pt == "user_message" || pt == "agent_message")
                {
                    var msg = GetString(payload, "message");
                    var blocks = string.IsNullOrEmpty(msg)
                        ? (IReadOnlyList<ContentBlock>)Array.Empty<ContentBlock>()
                        : new[] { new ContentBlock("text", msg) };
                    var isUser = pt == "user_message";
                    return new JsonlEvent(
                        isUser ? "user" : "assistant", null, null, null, ts, null, null, null,
                        isUser ? "user" : "assistant", blocks, root.Clone());
                }
                // Paginated history (Codex ≥ ~0.147): the turn arrives as a completed ITEM instead of a
                // message event. Only the two conversational item types become turns; Reasoning,
                // CommandExecution, FileChange and friends stay non-conversational, exactly as the tool
                // I/O of the legacy format does.
                if (pt == "item_completed" && hasPayload
                    && payload.TryGetProperty("item", out var item)
                    && item.ValueKind == JsonValueKind.Object)
                {
                    var itemType = GetString(item, "type");
                    var isUserItem = string.Equals(itemType, "UserMessage", StringComparison.OrdinalIgnoreCase);
                    var isAgentItem = string.Equals(itemType, "AgentMessage", StringComparison.OrdinalIgnoreCase);
                    if (isUserItem || isAgentItem)
                    {
                        var blocks = ReadItemText(item);
                        return new JsonlEvent(
                            isUserItem ? "user" : "assistant", GetString(item, "id"), null, null, ts,
                            null, null, null, isUserItem ? "user" : "assistant", blocks, root.Clone());
                    }
                }
                // token_count, task_started, task_complete, … — keep for offset continuity, ignored downstream.
                return new JsonlEvent("event_msg", null, null, null, ts, null, null, null, null,
                    Array.Empty<ContentBlock>(), root.Clone());
            }
            default:
                // response_item (tool I/O, raw model messages), compacted, etc. Not used for turns.
                return new JsonlEvent(lineType, null, null, null, ts, null, null, null, null,
                    Array.Empty<ContentBlock>(), root.Clone());
        }
    }

    /// <summary>
    /// Text blocks of a paginated `item_completed` item. The block discriminator is spelled
    /// inconsistently by the producer — a UserMessage carries "text", an AgentMessage "Text" — so the
    /// match is case-insensitive rather than trusting either spelling.
    /// </summary>
    private static IReadOnlyList<ContentBlock> ReadItemText(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return Array.Empty<ContentBlock>();
        var blocks = new List<ContentBlock>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            if (!string.Equals(GetString(block, "type"), "text", StringComparison.OrdinalIgnoreCase)) continue;
            var text = GetString(block, "text");
            if (!string.IsNullOrEmpty(text)) blocks.Add(new ContentBlock("text", text));
        }
        return blocks.Count == 0 ? Array.Empty<ContentBlock>() : blocks;
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? GetDateTimeOffset(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        return DateTimeOffset.TryParse(v.GetString(), out var dt) ? dt : null;
    }
}
