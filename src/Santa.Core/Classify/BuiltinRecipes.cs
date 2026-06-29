namespace Santa.Core.Classify;

public static class BuiltinRecipes
{
    public static readonly (string Filename, string Yaml)[] All =
    {
        ("is_resolved.yaml", """
id: is_resolved
name: Did this session reach a natural completion point?
mode: transcript
model: haiku
transcript_mode: tail
transcript_max_tokens: 4000
prompt: |
  You are reviewing the tail of a Claude Code session to decide whether the work
  ended at a natural completion point or was abandoned mid-task.

  Original goal:
  {{ first_user_message | truncate(600) }}

  Tail of the session prose (tool I/O already stripped):

  {{ transcript }}

  Output strict JSON only, no prose around it:
  {"status":"resolved|in_progress|abandoned|unknown","evidence":"<one sentence>"}
status_map:
  resolved: completed
  in_progress: active
  abandoned: archived
  unknown: active
"""),
        ("branch_merged.yaml", """
id: branch_merged
name: Was the working branch merged?
mode: agent
model: haiku
allowed_tools:
  - "Bash(git:*)"
  - "Bash(gh:*)"
  - "Bash(glab:*)"
prompt: |
  Determine whether the work in this session has been merged.

  Repo (cwd): {{ cwd }}
  Recorded branch at session start: {{ git_branch }}
  Branches the session itself created/switched to via tool calls: {{ derived_branches }}
  Best candidate branches to check: {{ candidate_branches }}

  Original goal of the session:
  {{ first_user_message | truncate(400) }}

  Use git/gh/glab to verify whether the candidate branch(es) have been merged into
  the default branch on origin. If multiple candidates, treat the session as
  merged if any of them landed. If no real candidate exists (only HEAD/main/master),
  reply "unknown" and explain.

  Reply with strict JSON only:
  {"status":"merged|open|abandoned|unknown","evidence":"<one sentence>"}
status_map:
  merged: completed
  open: active
  abandoned: archived
  unknown: active
"""),
    };

    public static void WriteIfMissing(string root)
    {
        Directory.CreateDirectory(root);
        foreach (var (file, yaml) in All)
        {
            var path = Path.Combine(root, file);
            if (!File.Exists(path)) File.WriteAllText(path, yaml);
        }
    }
}
