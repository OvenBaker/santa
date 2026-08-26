namespace Santa.Core;

/// <summary>
/// Single source of truth for where santa keeps its state. Defaults to XDG
/// (<c>~/.local/share/santa/</c>); override the whole tree with <c>SANTA_HOME</c>.
/// </summary>
public static class SantaPaths
{
    public static string Home =>
        Environment.GetEnvironmentVariable("SANTA_HOME")
        ?? Environment.GetEnvironmentVariable("SANTA_CLAUDE_HOME")   // deprecated alias, pre-rename
        ?? DefaultHome;

    public static string IndexDb     => Path.Combine(Home, "index.db");
    public static string ModelsRoot  => Home;                                  // models/<model-id>/...
    public static string NativeDir   => Path.Combine(Home, "native");
    public static string VecLibPath  => Path.Combine(NativeDir, "vec0");
    public static string RecipesDir  => Path.Combine(Home, "recipes");
    public static string SharedGpuLockPath => Path.Combine(StateHome, "agent-tooling", "gpu-inference.lock");

    private static string DefaultHome
    {
        get
        {
            var root = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrEmpty(root))
            {
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                root = Path.Combine(profile, ".local", "share");
            }

            var current = Path.Combine(root, "santa");
            // Back-compat: if the tool was installed before the santa-claude → santa
            // rename, keep using the existing state dir rather than orphaning the index.
            if (!Directory.Exists(current))
            {
                var legacy = Path.Combine(root, "santa-claude");
                if (Directory.Exists(legacy)) return legacy;
            }
            return current;
        }
    }

    private static string StateHome => Environment.GetEnvironmentVariable("XDG_STATE_HOME") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
}
