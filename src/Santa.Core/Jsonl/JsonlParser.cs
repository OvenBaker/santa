using System.Text.Json;

namespace Santa.Core.Jsonl;

public static class JsonlParser
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

        var type = GetString(root, "type") ?? "unknown";
        var sessionId = GetString(root, "sessionId");
        var uuid = GetString(root, "uuid");
        var parentUuid = GetString(root, "parentUuid");
        var ts = GetDateTimeOffset(root, "timestamp");
        var cwd = GetString(root, "cwd");
        var branch = GetString(root, "gitBranch");
        var version = GetString(root, "version");

        string? role = null;
        var blocks = new List<ContentBlock>();
        if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
        {
            role = GetString(msg, "role");
            if (msg.TryGetProperty("content", out var content))
                ExtractBlocks(content, blocks);
        }

        return new JsonlEvent(type, sessionId, uuid, parentUuid, ts, cwd, branch, version, role, blocks, root.Clone());
    }

    private static void ExtractBlocks(JsonElement content, List<ContentBlock> sink)
    {
        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                sink.Add(new ContentBlock("text", content.GetString()));
                break;
            case JsonValueKind.Array:
                foreach (var el in content.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;
                    var bt = GetString(el, "type") ?? "unknown";
                    string? text = bt switch
                    {
                        "text" => GetString(el, "text"),
                        "thinking" => GetString(el, "thinking"),
                        _ => null
                    };
                    sink.Add(new ContentBlock(bt, text));
                }
                break;
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
