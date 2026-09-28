namespace Santa.Core.Sessions;

/// <summary>Account roots registered by Cockpit; the original root contains mixed legacy history.</summary>
public static class ClaudeProfiles
{
    public static IEnumerable<(string Account, string Projects)> Roots(string primary)
    {
        yield return ("legacy", primary);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Path.GetFullPath(primary) != Path.Combine(home, ".claude", "projects")
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COCKPIT_ACCOUNTS_DIR"))) yield break;
        var registry = Environment.GetEnvironmentVariable("COCKPIT_ACCOUNTS_DIR")
            ?? Path.Combine(home, ".config", "cockpit", "accounts");
        if (!Directory.Exists(registry)) yield break;
        var seen = new HashSet<string> { Path.GetFullPath(primary) };
        foreach (var file in Directory.EnumerateFiles(registry, "*.configdir").Order())
        {
            var root = File.ReadAllText(file).Trim();
            if (!Path.IsPathFullyQualified(root)) continue;
            var projects = Path.Combine(root, "projects");
            if (seen.Add(Path.GetFullPath(projects))) yield return (Path.GetFileNameWithoutExtension(file), projects);
        }
    }
}
