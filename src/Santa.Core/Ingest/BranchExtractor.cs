using System.Text.Json;
using System.Text.RegularExpressions;
using Santa.Core.Jsonl;

namespace Santa.Core.Ingest;

/// <summary>
/// Pulls branch names out of <c>git</c> / <c>gh</c> / worktree-related tool_use commands
/// in the JSONL stream. The session's recorded <c>gitBranch</c> often says "HEAD" because
/// claude was launched at a detached / default branch and the user then created or switched
/// branches via tool calls — those are the branches we actually want for "is it merged?" checks.
/// </summary>
public static class BranchExtractor
{
    // Branch tokens are alphanumeric + - / _ . — and must NOT be a 40-char SHA.
    private const string BranchToken = @"(?<name>(?!-)[A-Za-z0-9._\-/]+)";

    private static readonly Regex[] Patterns =
    {
        // git worktree add [opts] <path> <branch>
        new($@"\bgit\s+worktree\s+add(?:\s+(?:-\S+|--\S+(?:=\S+)?))*\s+\S+\s+{BranchToken}\b", RegexOptions.Compiled),
        // git worktree add -b <new-branch> <path>
        new($@"\bgit\s+worktree\s+add\s+(?:[^|;&]*?\s)?-b\s+{BranchToken}\b", RegexOptions.Compiled),
        // git checkout -b <branch>
        new($@"\bgit\s+checkout\s+-b\s+{BranchToken}\b", RegexOptions.Compiled),
        // git switch -c <branch>
        new($@"\bgit\s+switch\s+-c\s+{BranchToken}\b", RegexOptions.Compiled),
        // git switch <branch> (skip flags, skip a leading dash)
        new($@"\bgit\s+switch\s+(?!-){BranchToken}\b", RegexOptions.Compiled),
        // git checkout <branch> — only when followed by branchy chars; skip if SHA-shaped
        new($@"\bgit\s+checkout\s+(?!-){BranchToken}\b", RegexOptions.Compiled),
        // git push -u origin <branch>
        new($@"\bgit\s+push\s+(?:-u\s+|--set-upstream\s+)origin\s+{BranchToken}\b", RegexOptions.Compiled),
        // gh pr checkout <num-or-branch>
        new(@"\bgh\s+pr\s+checkout\s+(?<name>\S+)\b", RegexOptions.Compiled),
    };

    private static readonly Regex Sha = new(@"^[0-9a-f]{7,40}$", RegexOptions.Compiled);
    private static readonly Regex FileExt = new(@"\.[a-zA-Z]{1,4}$", RegexOptions.Compiled);

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "HEAD", "main", "master", "origin", "FETCH_HEAD", "ORIG_HEAD",
        ".", "..", "list", "prune", "remove", "add",
    };

    /// <summary>Extracts branch names from a stream of JSONL events. Order-preserving, deduplicated.</summary>
    public static IReadOnlyList<string> Extract(IEnumerable<JsonlEvent> events)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();
        foreach (var ev in events)
        {
            if (!ev.IsAssistant) continue;
            if (!ev.Raw.TryGetProperty("message", out var msg)) continue;
            if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;

            foreach (var block in content.EnumerateArray())
            {
                if (!IsBashToolUse(block, out var command)) continue;
                foreach (var name in ExtractFromCommand(command))
                {
                    if (seen.Add(name)) ordered.Add(name);
                }
            }
        }
        return ordered;
    }

    public static IEnumerable<string> ExtractFromCommand(string command)
    {
        foreach (var re in Patterns)
        {
            var matches = re.Matches(command);
            for (int i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                var name = m.Groups["name"].Value;
                if (string.IsNullOrEmpty(name)) continue;
                if (Reserved.Contains(name)) continue;
                if (Sha.IsMatch(name)) continue;
                if (name.StartsWith("../") || name.StartsWith("./") || name.StartsWith("/")) continue;
                if (name.StartsWith("origin/", StringComparison.OrdinalIgnoreCase)) continue;
                if (FileExt.IsMatch(name)) continue;
                yield return name;
            }
        }
    }

    private static bool IsBashToolUse(JsonElement block, out string command)
    {
        command = "";
        if (block.ValueKind != JsonValueKind.Object) return false;
        if (!block.TryGetProperty("type", out var t) || t.GetString() != "tool_use") return false;
        if (!block.TryGetProperty("name", out var n) || n.GetString() != "Bash") return false;
        if (!block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object) return false;
        if (!input.TryGetProperty("command", out var c) || c.ValueKind != JsonValueKind.String) return false;
        command = c.GetString() ?? "";
        return command.Length > 0;
    }
}
