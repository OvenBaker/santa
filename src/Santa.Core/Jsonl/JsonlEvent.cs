using System.Text.Json;

namespace Santa.Core.Jsonl;

public sealed record JsonlEvent(
    string Type,
    string? SessionId,
    string? Uuid,
    string? ParentUuid,
    DateTimeOffset? Timestamp,
    string? Cwd,
    string? GitBranch,
    string? Version,
    string? Role,
    IReadOnlyList<ContentBlock> ContentBlocks,
    JsonElement Raw)
{
    public bool IsUser => Type == "user";
    public bool IsAssistant => Type == "assistant";
    public bool IsConversational => IsUser || IsAssistant;
}

public sealed record ContentBlock(string Type, string? Text)
{
    public bool IsText => Type == "text";
    public bool IsThinking => Type == "thinking";
    public bool IsToolUse => Type == "tool_use";
    public bool IsToolResult => Type == "tool_result";
}
