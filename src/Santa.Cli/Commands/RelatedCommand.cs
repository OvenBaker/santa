using System.ComponentModel;
using System.Diagnostics;
using Santa.Cli;
using Santa.Cli.Theming;
using Santa.Core.Embedding;
using Santa.Core.Search;
using Santa.Core.Sessions;
using Santa.Core.Settings;
using Santa.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Santa.Cli.Commands;

/// <summary>
/// Find sessions related to a given one — "more like this" over the vector
/// index, using the source session's own summary as the query. Sessions in the
/// same codebase (matched by normalised git remote, so different worktrees /
/// clones count) are boosted to the top.
/// </summary>
public sealed class RelatedCommand : Command<RelatedCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<SESSION_ID>")]
        [Description("Full or prefix session id to find relatives of.")]
        public string SessionId { get; init; } = "";

        [CommandOption("--limit <N>")]
        [DefaultValue(8)]
        public int Limit { get; init; }

        [CommandOption("--active-only")]
        [Description("Exclude completed/archived sessions (default: include them).")]
        public bool ActiveOnly { get; init; }

        [CommandOption("--no-rerank")]
        public bool NoRerank { get; init; }

        [CommandOption("--keyword-only")]
        public bool KeywordOnly { get; init; }

        [CommandOption("--provider <PROVIDER>")]
        [Description("Inference provider: cuda (default), cpu, or keyword-only.")]
        [DefaultValue("cuda")]
        public string Provider { get; init; } = "cuda";

        [CommandOption("--porcelain")]
        [Description("Machine-readable TSV: id<TAB>cwd<TAB>title<TAB>same_repo. For cockpit.")]
        public bool Porcelain { get; init; }

        [CommandOption("--db <PATH>")]
        public string? Db { get; init; }

        [CommandOption("--device <ID>")]
        public int DeviceId { get; init; } = 0;
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellationToken)
    {
        if (!InferenceProviderOption.TryResolve(s.Provider, s.KeywordOnly, out var provider)) return 2;
        // "more like this" IS the vector query, so there is nothing to degrade to — refuse where the
        // operator can read it rather than exiting 0 with no rows, which Alt-f showed as "no related
        // sessions found" whenever the refresh cron happened to hold the lease (2026-08-26).
        if (!InferenceProviderOption.TryAcquireGpuCourtesy(provider, s.DeviceId, out var gpuLease,
                out var gpuReason, announce: false))
            return InferenceProviderOption.ReportGpuRequired("finding related sessions", gpuReason);
        using var gpuLeaseScope = gpuLease;
        using var db = Database.Open(s.Db ?? Database.DefaultPath);
        var lookup = new SessionLookup(db);

        SessionInfo? src;
        try { src = lookup.FindByPrefix(s.SessionId); }
        catch (InvalidOperationException ex) { return Fail(s, ex.Message); }
        if (src is null) return Fail(s, $"no session matches {s.SessionId}");

        var query = BuildQuery(db, src);
        if (string.IsNullOrWhiteSpace(query)) return Fail(s, "source session has no summary/text to match on");

        IEmbedder? embedder = null;
        if (provider is not null)
        {
            var cfg = InferenceProviderOption.Apply(
                EmbedderConfig.NomicV15(EmbedderConfig.DefaultRoot), provider.Value, s.DeviceId);
            if (File.Exists(cfg.OnnxPath) && File.Exists(cfg.VocabPath) && db.TryEnableVec(cfg.Dimensions))
            {
                try { embedder = new LocalEmbedder(cfg); }
                catch (Exception ex) { return InferenceProviderOption.ReportInitializationFailure(provider.Value, ex); }
            }
        }
        IReranker? reranker = null;
        if (provider is not null && !s.NoRerank)
        {
            var rcfg = InferenceProviderOption.Apply(
                RerankerConfig.MsMarcoMiniLmL12(EmbedderConfig.DefaultRoot), provider.Value, s.DeviceId);
            if (File.Exists(rcfg.OnnxPath) && File.Exists(rcfg.VocabPath))
            {
                try { reranker = new LocalReranker(rcfg); }
                catch (Exception ex)
                {
                    embedder?.Dispose();
                    return InferenceProviderOption.ReportInitializationFailure(provider.Value, ex);
                }
            }
        }

        // Pull a few extra to absorb dropping the source itself and the repo re-sort.
        var search = new HybridSearch(db);
        var raw = search.Search(query, embedder, s.Limit + 6, includeCompleted: !s.ActiveOnly,
                                reranker: reranker, candidatePool: 50, maxPerSession: 1);
        embedder?.Dispose(); reranker?.Dispose();

        var srcRepo = RepoId(src.Cwd);
        var repoCache = new Dictionary<string, string?>();
        string? repoOf(string? cwd)
        {
            if (string.IsNullOrEmpty(cwd)) return null;
            if (!repoCache.TryGetValue(cwd, out var r)) { r = RepoId(cwd); repoCache[cwd] = r; }
            return r;
        }

        var hits = raw
            .Where(h => h.SessionId != src.Id)
            .Select(h => (h, same: srcRepo is not null && repoOf(h.Cwd) == srcRepo))
            .OrderByDescending(x => x.same)          // same-repo first; stable within group
            .Take(s.Limit)
            .ToList();

        if (s.Porcelain)
        {
            // id<TAB>cwd<TAB>title<TAB>same_repo. Never emit an empty middle field
            // (a bash IFS=$'\t' read would collapse it and shift the rest): cwd
            // falls back to "-", title to the id prefix.
            foreach (var (h, same) in hits)
            {
                var cwd = string.IsNullOrEmpty(h.Cwd) ? "-" : h.Cwd;
                var title = (h.SummaryTitle ?? h.FirstUserText ?? h.SessionId[..8]).ReplaceLineEndings(" ");
                Console.WriteLine($"{h.SessionId}\t{cwd}\t{title}\t{(same ? "1" : "0")}");
            }
            return 0;
        }

        var theme = Theme.ByName(SantaSettings.Load().Theme);
        AnsiConsole.MarkupLine($"related to [bold]{src.Id[..8]}[/] [{theme.Dim}]{Markup.Escape(src.Cwd ?? "")}[/]");
        if (hits.Count == 0) { AnsiConsole.MarkupLine($"[{theme.Dim}]no related sessions[/]"); return 0; }
        AnsiConsole.WriteLine();
        int idx = 0;
        foreach (var (h, same) in hits)
        {
            idx++;
            var dateSrc = !string.IsNullOrEmpty(h.LastActiveAt) ? h.LastActiveAt : h.StartedAt;
            var date = string.IsNullOrEmpty(dateSrc) ? "" : DateTimeOffset.Parse(dateSrc).ToLocalTime().ToString("yyyy-MM-dd");
            var tag = same ? $" [{theme.Ok}]↳ same repo[/]" : "";
            var st = h.Status == "completed" ? $" [{theme.Ok}]✓[/]" : "";
            AnsiConsole.MarkupLine($"[{theme.Index}][[{idx}]][/] [bold]{h.SessionId[..8]}[/]{st}  [{theme.Dim}]{date}[/]  [{theme.Path}]{Markup.Escape(h.Cwd ?? "")}[/]{tag}");
            if (!string.IsNullOrEmpty(h.SummaryTitle))
                AnsiConsole.MarkupLine($"    [{theme.Title}]{Esc(h.SummaryTitle, 110)}[/]");
            if (!string.IsNullOrEmpty(h.SummaryShort))
                AnsiConsole.MarkupLine($"    [{theme.Dim}]{Esc(h.SummaryShort, 200)}[/]");
            AnsiConsole.WriteLine();
        }
        AnsiConsole.MarkupLine($"[{theme.Dim}]santa resume <id>  ·  show <id>[/]");
        return 0;
    }

    private static string BuildQuery(Database db, SessionInfo src)
    {
        string? title = null, shortS = null;
        using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT summary_title, summary_short FROM sessions WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", src.Id);
            using var rd = cmd.ExecuteReader();
            if (rd.Read()) { title = rd.IsDBNull(0) ? null : rd.GetString(0); shortS = rd.IsDBNull(1) ? null : rd.GetString(1); }
        }
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(title)) parts.Add(title!);
        if (!string.IsNullOrWhiteSpace(shortS)) parts.Add(shortS!);
        if (parts.Count == 0 && !string.IsNullOrWhiteSpace(src.FirstUserText)) parts.Add(src.FirstUserText!);
        if (!string.IsNullOrWhiteSpace(src.LastUserText)) parts.Add(src.LastUserText!);
        return string.Join(". ", parts);
    }

    // Codebase identity: normalised git remote (so different worktrees/clones of
    // the same repo match), falling back to the worktree root.
    private static string? RepoId(string? cwd)
    {
        if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd)) return null;
        var url = Git(cwd, "remote", "get-url", "origin");
        if (!string.IsNullOrWhiteSpace(url)) return NormalizeRemote(url);
        var top = Git(cwd, "rev-parse", "--show-toplevel");
        return string.IsNullOrWhiteSpace(top) ? null : top.Trim();
    }

    private static string NormalizeRemote(string url)
    {
        var u = url.Trim().ToLowerInvariant();
        if (u.EndsWith(".git")) u = u[..^4];
        u = u.Replace("git@", "").Replace("ssh://", "").Replace("https://", "").Replace("http://", "").Replace("git://", "");
        u = u.Replace(":", "/");                 // git@host:org/repo → host/org/repo
        return u.TrimEnd('/');
    }

    private static string? Git(string cwd, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("-C"); psi.ArgumentList.Add(cwd);
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var outp = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(2000);
            return p.ExitCode == 0 ? outp.Trim() : null;
        }
        catch { return null; }
    }

    private static string Esc(string s, int n) => Markup.Escape(s.Length <= n ? s : s[..n] + "…");

    private int Fail(Settings s, string msg)
    {
        if (!s.Porcelain) AnsiConsole.MarkupLineInterpolated($"[red]{msg}[/]");
        else Console.Error.WriteLine(msg);
        return 2;
    }
}
