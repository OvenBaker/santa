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

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? GetDateTimeOffset(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        return DateTimeOffset.TryParse(v.GetString(), out var dt) ? dt : null;
    }
}
