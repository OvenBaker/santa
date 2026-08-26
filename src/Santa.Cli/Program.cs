using Santa.Cli.Commands;
using Santa.Cli.Tui;
using Spectre.Console.Cli;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("santa");
    config.AddCommand<IngestCommand>("ingest")
        .WithDescription("Scan ~/.claude/projects and (re)build the search index.");
    config.AddCommand<QueryCommand>("query")
        .WithDescription("Hybrid (BM25 + vec) search across past sessions. --keyword-only to skip embeddings.");
    config.AddCommand<ShowCommand>("show")
        .WithDescription("Show a session's metadata and projected prose.");
    config.AddCommand<RelatedCommand>("related")
        .WithDescription("Find sessions related to a given one (vector 'more like this' + same-repo boost).");
    config.AddCommand<ResumeCommand>("resume")
        .WithDescription("Spawn a new wt.exe tab at the session's cwd and `claude --resume <id>`.");
    config.AddCommand<ExportCommand>("export")
        .WithDescription("Export a session as md / json / jsonl / prose.");
    config.AddCommand<CompleteCommand>("complete")
        .WithDescription("Mark a session as completed (hides it from default search).");
    config.AddCommand<UncompleteCommand>("uncomplete")
        .WithDescription("Unset completed status on a session.");
    config.AddCommand<ClassifyCommand>("classify")
        .WithDescription("Run a classification recipe against a session via `claude -p`.");
    config.AddCommand<TriageCommand>("triage")
        .WithDescription("Batch-classify sessions on one or more recipes.");
    config.AddCommand<RecipesCommand>("recipes")
        .WithDescription("Manage classification recipes. Actions: list | show <id> | init.");
    config.AddCommand<SummarizeCommand>("summarize")
        .WithDescription("Generate per-session title + short + long summaries via `claude -p` (Max plan).");
    config.AddCommand<RefreshCommand>("refresh")
        .WithDescription("Single-shot ingest + summarise (cron-friendly). Idempotent.");
    config.AddCommand<RecentCommand>("recent")
        .WithDescription("Activity report for a date range, driven off the index. Optional --synthesize for cross-cutting themes.");
    config.AddCommand<BrowseCommand>("browse")
        .WithDescription("Quick Spectre prompt-based browser. Prefer `tui` for the full app.");
    config.AddCommand<TuiCommand>("tui")
        .WithDescription("Full-screen interactive TUI (Browse + Search tabs).");
    config.AddCommand<DevicesCommand>("devices")
        .WithDescription("Report the selected ONNX Runtime provider and device readiness.");
    config.AddCommand<ModelsCommand>("models")
        .WithDescription("Manage embedding model + sqlite-vec extension. Actions: status | download.");
});
return await app.RunAsync(args);
