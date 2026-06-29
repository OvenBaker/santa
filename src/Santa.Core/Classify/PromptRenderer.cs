using System.Text.RegularExpressions;
using Santa.Core.Sessions;

namespace Santa.Core.Classify;

/// <summary>
/// Light <c>{{ var }}</c> substitution. Available vars:
///   session_id, cwd, git_branch, started_at, ended_at,
///   message_count, turn_count, first_user_message, last_user_message,
///   transcript  (rendered separately, injected by caller).
/// </summary>
public static class PromptRenderer
{
    private static readonly Regex VarRe = new(@"\{\{\s*(?<name>\w+)\s*(?:\|\s*truncate\((?<n>\d+)\))?\s*\}\}", RegexOptions.Compiled);

    /// <summary>
    /// Union of recorded git_branch and tool-call-derived branches, with HEAD/main/master
    /// dropped if there's a real candidate to use instead.
    /// </summary>
    private static string CandidateBranches(SessionInfo session)
    {
        var all = new List<string>();
        if (!string.IsNullOrEmpty(session.GitBranch)) all.Add(session.GitBranch);
        if (session.DerivedBranches is { } extra) all.AddRange(extra);
        var distinct = all.Distinct().ToList();
        var nonTrivial = distinct.Where(b =>
            !string.Equals(b, "HEAD",   StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(b, "main",   StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(b, "master", StringComparison.OrdinalIgnoreCase)).ToList();
        return string.Join(", ", nonTrivial.Count > 0 ? nonTrivial : distinct);
    }

    public static string Render(string template, SessionInfo session, string transcript)
    {
        return VarRe.Replace(template, m =>
        {
            var name = m.Groups["name"].Value;
            var raw = name switch
            {
                "session_id"          => session.Id,
                "cwd"                 => session.Cwd ?? "",
                "git_branch"          => session.GitBranch ?? "",
                "derived_branches"    => session.DerivedBranches is { Count: > 0 } b ? string.Join(", ", b) : "",
                "candidate_branches"  => CandidateBranches(session),
                "started_at"          => session.StartedAt?.ToString("u") ?? "",
                "ended_at"            => session.EndedAt?.ToString("u") ?? "",
                "message_count"       => session.MessageCount.ToString(),
                "turn_count"          => session.TurnCount.ToString(),
                "first_user_message"  => session.FirstUserText ?? "",
                "last_user_message"   => session.LastUserText ?? "",
                "transcript"          => transcript,
                _ => "{{" + name + "}}"
            };
            if (m.Groups["n"].Success && int.TryParse(m.Groups["n"].Value, out var n) && raw.Length > n)
                raw = raw[..n] + "…";
            return raw;
        });
    }
}
