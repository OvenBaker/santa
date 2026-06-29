using System.ComponentModel;
using Santa.Cli.Theming;
using Santa.Core.Embedding;
using Santa.Core.Search;
using Santa.Core.Settings;
using Santa.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Santa.Cli.Commands;

public sealed class QueryCommand : Command<QueryCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<TEXT>")]
        public string Text { get; init; } = "";

        [CommandOption("--limit <N>")]
        [DefaultValue(5)]
        public int Limit { get; init; }

        [CommandOption("--per-session <N>")]
        [Description("Max chunks shown per session (default 1: dedupes the noise of one session winning multiple slots).")]
        [DefaultValue(1)]
        public int PerSession { get; init; }

        [CommandOption("--include-completed")]
        public bool IncludeCompleted { get; init; }

        [CommandOption("--keyword-only")]
        [Description("Skip vector search even if a model is configured.")]
        public bool KeywordOnly { get; init; }

        [CommandOption("--no-rerank")]
        [Description("Skip cross-encoder reranking (default: rerank if model is downloaded).")]
        public bool NoRerank { get; init; }

        [CommandOption("--pool <N>")]
        [Description("Candidate pool size pulled before reranking (default 50).")]
        [DefaultValue(50)]
        public int Pool { get; init; }

        [CommandOption("--db <PATH>")]
        public string? Db { get; init; }

        [CommandOption("--device <ID>")]
        public int DeviceId { get; init; } = 0;
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellationToken)
    {
        using var db = Database.Open(s.Db ?? Database.DefaultPath);

        IEmbedder? embedder = null;
        if (!s.KeywordOnly)
        {
            var cfg = EmbedderConfig.NomicV15(EmbedderConfig.DefaultRoot) with { DeviceId = s.DeviceId };
            if (File.Exists(cfg.OnnxPath) && File.Exists(cfg.VocabPath) && db.TryEnableVec(cfg.Dimensions))
            {
                try { embedder = new LocalEmbedder(cfg); }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLineInterpolated($"[yellow]vec disabled:[/] {Markup.Escape(ex.Message)}");
                }
            }
        }

        IReranker? reranker = null;
        if (!s.NoRerank)
        {
            var rcfg = RerankerConfig.MsMarcoMiniLmL12(EmbedderConfig.DefaultRoot) with { DeviceId = s.DeviceId };
            if (File.Exists(rcfg.OnnxPath) && File.Exists(rcfg.VocabPath))
            {
                try { reranker = new LocalReranker(rcfg); }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLineInterpolated($"[yellow]reranker disabled:[/] {Markup.Escape(ex.Message)}");
                }
            }
        }

        var search = new HybridSearch(db);
        var hits = search.Search(s.Text, embedder, s.Limit, s.IncludeCompleted, reranker, s.Pool, s.PerSession);
        embedder?.Dispose();
        reranker?.Dispose();

        var theme = Theme.ByName(SantaSettings.Load().Theme);

        if (hits.Count == 0)
        {
            AnsiConsole.MarkupLine($"[{theme.Dim}]no matches[/]");
            return 0;
        }

        var modeBits = new List<string>();
        modeBits.Add(embedder is null ? "bm25" : "bm25+vec");
        if (reranker is not null) modeBits.Add("rerank");
        AnsiConsole.MarkupLine($"{hits.Count} hits [{theme.Dim}]({string.Join(" → ", modeBits)})[/]");
        AnsiConsole.WriteLine();

        // When stdout is a TTY, Spectre reports the real terminal width; when piped, it's clamped
        // to 80. Use whichever is larger so direct interactive use gets long snippets while piped
        // output stays compact.
        var width = Math.Max(AnsiConsole.Console.Profile.Width, 80);
        var summaryBudget = Math.Max(180, width - 20);
        var matchBudget   = Math.Max(200, width - 20);
        var intentBudget  = Math.Max(160, width - 20);

        int idx = 0;
        foreach (var h in hits)
        {
            idx++;
            // Show last-active (when meaningful work last happened) and fall back to started_at.
            var dateSrc = !string.IsNullOrEmpty(h.LastActiveAt) ? h.LastActiveAt : h.StartedAt;
            var date = string.IsNullOrEmpty(dateSrc)
                ? "          "
                : DateTimeOffset.Parse(dateSrc).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            var statusTag = h.Status switch
            {
                "completed" => $" [{theme.Ok}]✓[/]",
                "archived"  => $" [{theme.Dim}]✗[/]",
                _ => ""
            };
            var span = string.IsNullOrEmpty(h.Duration) ? "" : $" · {h.Duration}";
            var stats = h.MatchedChunkCount > 1
                ? $"[{theme.Dim}]{h.TurnCount}t{span} · {h.MatchedChunkCount} matched[/]"
                : $"[{theme.Dim}]{h.TurnCount}t{span}[/]";

            // Header. Build markup by hand: stats/statusTag already contain markup, and
            // MarkupLineInterpolated would re-escape them.
            var cwd = Markup.Escape(h.Cwd ?? "");
            var branch = Markup.Escape(h.GitBranch ?? "");
            AnsiConsole.MarkupLine(
                $"[{theme.Index}][[{idx}]][/] [bold]{h.SessionId[..8]}[/]{statusTag}  [{theme.Dim}]{date}[/]  [{theme.Path}]{cwd}[/]  [{theme.Branch}]{branch}[/]  · {stats}");

            if (!string.IsNullOrEmpty(h.SummaryTitle))
                AnsiConsole.MarkupLine($"    [{theme.Title}]{EscapePlain(h.SummaryTitle, 120)}[/]");
            if (!string.IsNullOrEmpty(h.SummaryShort))
                AnsiConsole.MarkupLine($"    summary: {EscapePlain(h.SummaryShort, summaryBudget)}");
            else if (!string.IsNullOrEmpty(h.FirstUserText))
                AnsiConsole.MarkupLine($"    intent : {EscapePlain(h.FirstUserText, intentBudget)}");
            AnsiConsole.MarkupLine($"    match  : {RenderSnippet(h.Snippet, theme, matchBudget)}");

            var line = $"    [{theme.Dim}]bm25={h.FtsRank:F2}  vec={h.VecDistance:F3}  fused={h.FusedScore:F3}[/]";
            if (h.RerankerScore is { } rr) line += $"  rerank=[bold]{rr:F2}[/]";
            AnsiConsole.MarkupLine(line);
            AnsiConsole.WriteLine();
        }
        AnsiConsole.MarkupLine($"[{theme.Dim}]santa resume <id>  ·  show <id>  ·  export <id>[/]");
        return 0;
    }

    private static string EscapePlain(string s, int n)
    {
        var t = s.Length <= n ? s : s[..n] + "…";
        return Markup.Escape(t);
    }

    private static string RenderSnippet(string raw, Theme theme, int budget = 220)
    {
        var oneLine = System.Text.RegularExpressions.Regex.Replace(raw, @"\s+", " ").Trim();
        if (oneLine.Length > budget) oneLine = oneLine[..budget] + "…";
        return Markup.Escape(oneLine)
            .Replace("«", $"[{theme.Highlight}]")
            .Replace("»", "[/]");
    }
}
