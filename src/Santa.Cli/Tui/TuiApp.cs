using Santa.Cli.Theming;
using Santa.Core.Embedding;
using Santa.Core.Search;
using Santa.Core.Sessions;
using Santa.Core.Settings;
using Santa.Core.Storage;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Santa.Cli.Tui;

/// <summary>
/// Pure-Spectre TUI. Two tabs (Browse, Search) rendered into a single Layout that
/// re-renders on every keypress. No Terminal.Gui — all text is full-truecolor markup
/// driven by the active <see cref="Theme"/>, so theme changes apply live.
/// </summary>
public sealed class TuiApp
{
    private enum TabId { Browse, Search }
    private enum Mode { Normal, Filter, SearchInput, Options, Toast }

    private readonly Database _db;
    private readonly IEmbedder? _embedder;
    private readonly IReranker? _reranker;
    private readonly HybridSearch _search;

    private SantaSettings _settings = SantaSettings.Load();
    private Theme _theme;

    private TabId _tab = TabId.Browse;
    private Mode _mode = Mode.Normal;
    private bool _quit;

    // Browse state
    private List<TuiSessionRow> _all = new();
    private List<TuiSessionRow> _visible = new();
    private string _filter = "";
    private int _selected;
    private int _topRow;
    private bool _detailExpanded;

    // Live-session badge state — refreshed once on startup, then every LiveRefreshInterval while idle.
    private IReadOnlySet<string> _liveSet = new HashSet<string>();
    private DateTime _lastLiveRefreshUtc = DateTime.MinValue;
    private static readonly TimeSpan LiveRefreshInterval = TimeSpan.FromMinutes(5);

    // Search state
    private string _searchQuery = "";
    private List<SearchHit> _searchHits = new();
    private int _searchSelected;
    private int _searchTopRow;

    // Options state
    private int _optionsCursor;
    private Theme _optionsPreviewBackup = Theme.SlateAmber;

    // Toast state
    private string _toast = "";

    public TuiApp(Database db, IEmbedder? embedder, IReranker? reranker)
    {
        _db = db;
        _embedder = embedder;
        _reranker = reranker;
        _search = new HybridSearch(db);
        _theme = Theme.ByName(_settings.Theme);
    }

    public void Run()
    {
        ReloadSessions();
        RefreshLiveSet();
        AnsiConsole.AlternateScreen(() =>
        {
            AnsiConsole.Cursor.Hide();
            try
            {
                AnsiConsole.Live(BuildRoot())
                    .AutoClear(false)
                    .Overflow(VerticalOverflow.Crop)
                    .Start(ctx =>
                    {
                        // Polled key loop so we can also tick a live-set refresh on idle. Drawing
                        // happens after each event (key OR refresh), not on a wall-clock cadence —
                        // the 200ms KeyAvailable poll is just to keep input feeling instant.
                        while (!_quit)
                        {
                            ctx.UpdateTarget(BuildRoot());
                            ctx.Refresh();

                            ConsoleKeyInfo? key = null;
                            while (!_quit && key is null)
                            {
                                if (Console.KeyAvailable)
                                {
                                    key = Console.ReadKey(intercept: true);
                                    break;
                                }
                                if (DateTime.UtcNow - _lastLiveRefreshUtc >= LiveRefreshInterval)
                                {
                                    RefreshLiveSet();
                                    break; // fall out → redraw with fresh badges
                                }
                                Thread.Sleep(200);
                            }
                            if (key is { } k) HandleKey(k);
                        }
                    });
            }
            finally { AnsiConsole.Cursor.Show(); }
        });
    }

    private void RefreshLiveSet()
    {
        try { _liveSet = LiveSessionDetector.DetectLiveSessionIds(); }
        catch { _liveSet = new HashSet<string>(); }
        _lastLiveRefreshUtc = DateTime.UtcNow;
    }

    // ─────────────────────────────────────────────────────────────────
    // Layout assembly — fresh tree per frame; Live diffs and re-renders only the changed cells.
    // ─────────────────────────────────────────────────────────────────
    private Layout BuildRoot()
    {
        var root = new Layout("root").SplitRows(
            new Layout("header").Size(1),
            new Layout("body"),
            new Layout("footer").Size(1));
        root["header"].Update(BuildTabBar());
        root["body"].Update(BuildBody());
        root["footer"].Update(BuildFooter());
        return root;
    }

    private IRenderable BuildBody()
    {
        if (_mode == Mode.Options) return BuildOptionsPanel();
        return _tab == TabId.Browse ? BuildBrowseBody() : BuildSearchBody();
    }

    /// <summary>True when the user opted into side-by-side AND the terminal is wide enough.</summary>
    private bool SideBySide =>
        _settings.LayoutMode == "side-by-side"
        && AnsiConsole.Console.Profile.Width >= 140;

    private IRenderable BuildBrowseBody()
    {
        var hasFilter = _mode == Mode.Filter || !string.IsNullOrEmpty(_filter);

        IRenderable mainArea;
        if (SideBySide)
        {
            var split = new Layout().SplitColumns(
                new Layout("list").Ratio(2),
                new Layout("detail").Ratio(1));
            split["list"].Update(BuildSessionListPanel());
            split["detail"].Update(BuildDetailPanel());
            mainArea = split;
        }
        else
        {
            var detailSize = _detailExpanded ? 14 : 6;
            var stacked = new Layout().SplitRows(
                new Layout("list"),
                new Layout("detail").Size(detailSize));
            stacked["list"].Update(BuildSessionListPanel());
            stacked["detail"].Update(BuildDetailPanel());
            mainArea = stacked;
        }

        if (!hasFilter) return mainArea;

        var withFilter = new Layout().SplitRows(
            new Layout("main"),
            new Layout("filter").Size(1));
        withFilter["main"].Update(mainArea);
        withFilter["filter"].Update(BuildFilterLine());
        return withFilter;
    }

    private IRenderable BuildSearchBody()
    {
        // Side-by-side: query on top, then results | match-detail.
        // Stacked: query on top, results in the middle, match panel below (only when hits exist).
        var query = new Layout("query").Size(1);

        if (SideBySide && _searchHits.Count > 0)
        {
            var results = new Layout("results").Ratio(2);
            var match = new Layout("match").Ratio(1);
            var content = new Layout("content").SplitColumns(results, match);

            var body = new Layout().SplitRows(query, content);
            body["query"].Update(BuildSearchQueryLine());
            results.Update(BuildSearchResultsPanel());
            match.Update(BuildSearchDetailPanel());
            return body;
        }
        else
        {
            var sections = new List<Layout> { query, new Layout("results") };
            if (_searchHits.Count > 0)
                sections.Add(new Layout("match").Size(10));

            var body = new Layout().SplitRows(sections.ToArray());
            body["query"].Update(BuildSearchQueryLine());
            body["results"].Update(BuildSearchResultsPanel());
            if (_searchHits.Count > 0)
                body["match"].Update(BuildSearchDetailPanel());
            return body;
        }
    }

    private IRenderable BuildTabBar()
    {
        var t = _theme;
        string Tab(string label, bool active) => active
            ? $"[{t.TabActiveFg} on {t.TabActiveBg}] {label} [/]"
            : $"[{t.Dim}] {label} [/]";
        var bar = $"[{t.Title}]:santa_claus:  santa[/]   {Tab("Browse", _tab == TabId.Browse)}{Tab("Search", _tab == TabId.Search)}";
        return new Markup(bar);
    }

    private int VisibleListHeight()
    {
        var filter = _mode == Mode.Filter || !string.IsNullOrEmpty(_filter) ? 1 : 0;
        if (SideBySide)
            return Math.Max(3, AnsiConsole.Console.Profile.Height - 2 - filter - 2 /* panel borders */);
        var detailSize = _detailExpanded ? 14 : 6;
        return Math.Max(3, AnsiConsole.Console.Profile.Height - 2 - detailSize - filter - 2);
    }

    /// <summary>Effective inner width of the session-list panel (accounts for side-by-side split).</summary>
    private int ListPanelWidth()
    {
        var w = AnsiConsole.Console.Profile.Width;
        return SideBySide ? (w * 2 / 3) - 2 : w - 2;
    }

    private int VisibleSearchListHeight()
    {
        // Side-by-side: results panel gets full body height (less query line + panel borders).
        // Stacked: subtract the match panel size when hits exist.
        if (SideBySide && _searchHits.Count > 0)
            return Math.Max(3, AnsiConsole.Console.Profile.Height - 2 /*hdr+ftr*/ - 1 /*query*/ - 2 /*borders*/);
        var matchSize = _searchHits.Count > 0 ? 10 : 0;
        return Math.Max(3, AnsiConsole.Console.Profile.Height - 2 - matchSize - 1 - 2);
    }

    /// <summary>Effective inner width of the search-results panel.</summary>
    private int SearchListPanelWidth()
    {
        var w = AnsiConsole.Console.Profile.Width;
        return SideBySide && _searchHits.Count > 0 ? (w * 2 / 3) - 2 : w - 2;
    }

    private void ClampBrowseScroll()
    {
        var listHeight = VisibleListHeight();
        if (_selected < 0) _selected = 0;
        if (_selected >= _visible.Count) _selected = Math.Max(0, _visible.Count - 1);
        if (_selected < _topRow) _topRow = _selected;
        if (_selected >= _topRow + listHeight) _topRow = _selected - listHeight + 1;
        if (_topRow < 0) _topRow = 0;
    }

    private void ClampSearchScroll()
    {
        var listHeight = VisibleSearchListHeight();
        if (_searchSelected < 0) _searchSelected = 0;
        if (_searchSelected >= _searchHits.Count) _searchSelected = Math.Max(0, _searchHits.Count - 1);
        if (_searchSelected < _searchTopRow) _searchTopRow = _searchSelected;
        if (_searchSelected >= _searchTopRow + listHeight) _searchTopRow = _searchSelected - listHeight + 1;
        if (_searchTopRow < 0) _searchTopRow = 0;
    }

    private IRenderable BuildSessionListPanel()
    {
        ClampBrowseScroll();
        return RenderSessionList(ListPanelWidth(), VisibleListHeight());
    }

    private IRenderable BuildDetailPanel() =>
        RenderDetailPane(AnsiConsole.Console.Profile.Width, _detailExpanded ? 14 : 6);

    private IRenderable BuildFilterLine() => RenderFilterLine();

    private IRenderable BuildSearchQueryLine()
    {
        var t = _theme;
        var caret = _mode == Mode.SearchInput ? "_" : " ";
        var indicator = _mode == Mode.SearchInput ? $"[{t.Index}]›[/]" : $"[{t.Dim}]›[/]";
        return new Markup($"  {indicator} query: {Esc(_searchQuery)}{caret}   [{t.Dim}](Enter to run)[/]");
    }

    private IRenderable BuildSearchResultsPanel()
    {
        ClampSearchScroll();
        return RenderSearchResults(SearchListPanelWidth(), VisibleSearchListHeight());
    }

    private IRenderable BuildSearchDetailPanel() =>
        RenderSearchDetail(AnsiConsole.Console.Profile.Width, 10);

    private IRenderable BuildOptionsPanel() => RenderOptionsOverlay();

    private IRenderable BuildFooter() => RenderFooter();

    private IRenderable RenderSessionList(int width, int height)
    {
        var t = _theme;

        if (_visible.Count == 0)
        {
            return new Panel(new Markup($"[{t.Dim}](no sessions)[/]"))
                .Header($"[{t.Title}]Sessions[/] [{t.Dim}](0/{_all.Count})[/]")
                .Border(BoxBorder.Rounded).BorderStyle(t.Frame).Expand();
        }

        var grid = new Grid();
        grid.AddColumn(new GridColumn().NoWrap().Width(1));               // marker
        grid.AddColumn(new GridColumn().NoWrap().Width(1));               // status icon
        grid.AddColumn(new GridColumn().NoWrap().Width(1));               // live indicator
        grid.AddColumn(new GridColumn().NoWrap().Width(16));              // date
        grid.AddColumn(new GridColumn().NoWrap().Width(36));              // cwd
        grid.AddColumn(new GridColumn().NoWrap().Width(5).RightAligned()); // turns
        grid.AddColumn(new GridColumn().NoWrap().Width(4).RightAligned()); // duration
        grid.AddColumn(new GridColumn().NoWrap());                         // title — eats the rest

        for (int i = _topRow; i < Math.Min(_topRow + height, _visible.Count); i++)
        {
            var s = _visible[i];
            var sel = i == _selected;
            string Wrap(string markup) => sel ? $"[{t.SelectionFg} on {t.SelectionBg}]{markup}[/]" : markup;

            var marker = Wrap(sel ? $"[{t.Index}]►[/]" : " ");
            var icon = Wrap(s.Status switch
            {
                "completed" => $"[{t.Ok}]✓[/]",
                "archived"  => $"[{t.Dim}]·[/]",
                _ => " "
            });
            var live  = Wrap(_liveSet.Contains(s.Id) ? $"[{t.Ok}]●[/]" : " ");
            var date  = Wrap($"[{t.Dim}]{s.DateLabel}[/]");
            var cwd   = Wrap($"[{t.Path}]{Esc(Truncate(s.ShortCwd, 36))}[/]");
            var turns = Wrap($"[{t.Dim}]{s.TurnCount}t[/]");
            var dur   = Wrap(FormatDuration(s.Duration, t));
            var pvtag = s.IsCodex ? Wrap($"[{t.Branch}]cx[/] ") : "";
            var titleBudget = Math.Max(10, width - 77 - (s.IsCodex ? 3 : 0));
            var title = pvtag + Wrap($"[{t.Title}]{Esc(Truncate(s.DisplayTitle, titleBudget))}[/]");

            grid.AddRow(new Markup(marker), new Markup(icon), new Markup(live), new Markup(date),
                        new Markup(cwd), new Markup(turns), new Markup(dur), new Markup(title));
        }

        return new Panel(grid)
            .Header($"[{t.Title}]Sessions[/] [{t.Dim}]({_visible.Count}/{_all.Count})[/]")
            .Border(BoxBorder.Rounded)
            .BorderStyle(t.Frame)
            .Expand();
    }

    private IRenderable RenderDetailPane(int width, int height)
    {
        var t = _theme;
        var row = CurrentRow();
        if (row is null)
            return new Panel(new Markup($"[{t.Dim}](no session)[/]")).Expand().Border(BoxBorder.Rounded).BorderStyle(t.Frame);

        var detail = SessionsListAdapter.LoadDetail(_db, row.Id);
        var sb = new System.Text.StringBuilder();
        var span = string.IsNullOrEmpty(row.Duration) ? "" : $" · {row.Duration}";
        var liveTag = _liveSet.Contains(row.Id) ? $"[{t.Ok}]● live[/]  [{t.Dim}]·[/] " : "";
        var agentTag = row.IsCodex ? $"[{t.Branch}]codex[/]  [{t.Dim}]·[/] " : "";
        sb.Append($"[bold]{Esc(row.Id[..8])}[/]  [{t.Dim}]·[/] {agentTag}{liveTag}{Esc(row.Status)}  [{t.Dim}]·[/] {row.TurnCount} turns{span}\n");
        sb.Append($"[{t.Path}]{Esc(row.Cwd ?? "")}[/]  [{t.Branch}]{Esc(row.GitBranch ?? "")}[/]\n");
        if (!string.IsNullOrEmpty(row.SummaryTitle))
            sb.Append($"\n[{t.Title}]{Esc(row.SummaryTitle)}[/]\n");
        var body = _detailExpanded
            ? (detail.SummaryLong ?? detail.SummaryShort ?? row.FirstUserText ?? "(no summary)")
            : (detail.SummaryShort ?? row.FirstUserText ?? "");
        if (!string.IsNullOrEmpty(body)) sb.Append('\n').Append(Esc(body));

        var hdrLabel = _detailExpanded ? "Detail (expanded)" : "Detail";
        return new Panel(new Markup(sb.ToString()))
            .Header($"[{t.Title}]{hdrLabel}[/]")
            .Border(BoxBorder.Rounded)
            .BorderStyle(t.Frame)
            .Expand();
    }

    private IRenderable RenderFilterLine()
    {
        var t = _theme;
        var indicator = _mode == Mode.Filter ? $"[{t.Index}]/[/]" : $"[{t.Dim}]/[/]";
        var caret = _mode == Mode.Filter ? "_" : " ";
        return new Markup($"  {indicator} filter: {Esc(_filter)}{caret}");
    }

    private IRenderable RenderSearchResults(int width, int height)
    {
        var t = _theme;

        if (_searchHits.Count == 0)
        {
            return new Panel(new Markup($"[{t.Dim}](type a query and press Enter)[/]"))
                .Header($"[{t.Title}]Results[/] [{t.Dim}](0)[/]")
                .Border(BoxBorder.Rounded).BorderStyle(t.Frame).Expand();
        }

        var grid = new Grid();
        grid.AddColumn(new GridColumn().NoWrap().Width(1));   // marker
        grid.AddColumn(new GridColumn().NoWrap().Width(1));   // live indicator
        grid.AddColumn(new GridColumn().NoWrap().Width(4));   // [N]
        grid.AddColumn(new GridColumn().NoWrap().Width(16));  // date
        grid.AddColumn(new GridColumn().NoWrap().Width(28));  // cwd
        grid.AddColumn(new GridColumn().NoWrap().Width(12));  // rerank
        grid.AddColumn(new GridColumn().NoWrap());            // title — eats the rest

        for (int i = _searchTopRow; i < Math.Min(_searchTopRow + height, _searchHits.Count); i++)
        {
            var h = _searchHits[i];
            var sel = i == _searchSelected;
            string Wrap(string markup) => sel ? $"[{t.SelectionFg} on {t.SelectionBg}]{markup}[/]" : markup;

            var date = string.IsNullOrEmpty(h.StartedAt)
                ? "          ----"
                : DateTimeOffset.Parse(h.StartedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            var rrText = h.RerankerScore is { } rr ? $"rr={rr:F2}" : "";
            var titleText = h.SummaryTitle ?? h.FirstUserText ?? h.SessionId[..8];
            var titleBudget = Math.Max(10, width - 77);

            var marker  = Wrap(sel ? $"[{t.Index}]►[/]" : " ");
            var live    = Wrap(_liveSet.Contains(h.SessionId) ? $"[{t.Ok}]●[/]" : " ");
            var idx     = Wrap($"[{t.Index}][[{i + 1,2}]][/]");
            var dateCol = Wrap($"[{t.Dim}]{date}[/]");
            var cwdCol  = Wrap($"[{t.Path}]{Esc(Truncate(h.Cwd ?? "", 28))}[/]");
            var rrCol   = Wrap($"[{t.Dim}]{rrText}[/]");
            var title   = Wrap($"[{t.Title}]{Esc(Truncate(titleText, titleBudget))}[/]");

            grid.AddRow(new Markup(marker), new Markup(live), new Markup(idx), new Markup(dateCol),
                        new Markup(cwdCol), new Markup(rrCol), new Markup(title));
        }

        return new Panel(grid)
            .Header($"[{t.Title}]Results[/] [{t.Dim}]({_searchHits.Count})[/]")
            .Border(BoxBorder.Rounded)
            .BorderStyle(t.Frame)
            .Expand();
    }

    private IRenderable RenderSearchDetail(int width, int height)
    {
        var t = _theme;
        if (_searchHits.Count == 0 || _searchSelected < 0 || _searchSelected >= _searchHits.Count)
            return new Panel(new Markup("")).Expand().Border(BoxBorder.Rounded).BorderStyle(t.Frame);
        var h = _searchHits[_searchSelected];
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrEmpty(h.SummaryTitle)) sb.Append($"[{t.Title}]{Esc(h.SummaryTitle)}[/]\n");
        if (!string.IsNullOrEmpty(h.SummaryShort)) sb.Append($"\n{Esc(h.SummaryShort)}\n");
        sb.Append($"\n[{t.Dim}]match:[/] {RenderSnippet(h.Snippet)}\n");
        sb.Append($"[{t.Dim}]bm25={h.FtsRank:F2}  vec={h.VecDistance:F3}  fused={h.FusedScore:F3}");
        if (h.RerankerScore is { } rr) sb.Append($"  rerank={rr:F2}");
        sb.Append("[/]");
        return new Panel(new Markup(sb.ToString()))
            .Header($"[{t.Title}]Match detail[/]")
            .Border(BoxBorder.Rounded)
            .BorderStyle(t.Frame)
            .Expand();
    }

    private IRenderable RenderFooter()
    {
        var t = _theme;
        // When a resume override is active (e.g. cockpit), 'r' hands off and 't'
        // forces a new terminal; otherwise 'r' is just the normal terminal resume.
        var rh = ResumeLauncher.OverrideActive
            ? $"[{t.Index}]r[/] →{Esc(ResumeLauncher.OverrideLabel)} · [{t.Index}]t[/] →term"
            : $"[{t.Index}]r[/] resume";
        string hints = _mode switch
        {
            Mode.Filter       => $"type to filter · [{t.Index}]Esc[/] clear · [{t.Index}]Enter[/] back",
            Mode.SearchInput  => $"type · [{t.Index}]Enter[/] run · [{t.Index}]Esc[/] cancel",
            Mode.Options      => $"[{t.Index}]↑↓[/] preview · [{t.Index}]Enter[/] save · [{t.Index}]Esc[/] revert",
            _ => _tab == TabId.Browse
                ? $"[{t.Index}]↑↓[/] · [{t.Index}]s[/] detail · [{t.Index}]c[/] toggle · {rh} · [{t.Index}]v[/] layout · [{t.Index}]/[/] filter · [{t.Index}]Tab[/] tabs · [{t.Index}]^O[/] options · [{t.Index}]^Q[/] quit"
                : $"[{t.Index}]↑↓[/] results · [{t.Index}]Enter[/] run · {rh} · [{t.Index}]v[/] layout · [{t.Index}]/[/] edit query · [{t.Index}]Tab[/] tabs · [{t.Index}]^O[/] options · [{t.Index}]^Q[/] quit",
        };
        var status = string.IsNullOrEmpty(_toast) ? "" : $"  [{t.Warn}]{Esc(_toast)}[/]";
        return new Markup(hints + status);
    }

    private IRenderable RenderOptionsOverlay()
    {
        var t = _theme;
        var grid = new Grid();
        grid.AddColumn(new GridColumn().NoWrap().Width(2));
        grid.AddColumn(new GridColumn().NoWrap().Width(28));
        grid.AddColumn(new GridColumn().NoWrap());

        for (int i = 0; i < Theme.All.Count; i++)
        {
            var opt = Theme.All[i];
            var sel = i == _optionsCursor;
            string Wrap(string m) => sel ? $"[{t.SelectionFg} on {t.SelectionBg}]{m}[/]" : m;
            var marker = Wrap(sel ? $"[{t.Index}]►[/]" : " ");
            var name   = Wrap($"[{t.Title}]{Esc(opt.DisplayName)}[/]");
            var vibe   = Wrap($"[{t.Dim}]{Esc(opt.Vibe)}[/]");
            grid.AddRow(new Markup(marker), new Markup(name), new Markup(vibe));
        }
        return new Panel(grid)
            .Header($"[{t.Title}]Options · Theme[/]")
            .Border(BoxBorder.Rounded)
            .BorderStyle(t.Frame)
            .Padding(1, 1)
            .Expand();
    }

    // ─────────────────────────────────────────────────────────────────
    // Input
    // ─────────────────────────────────────────────────────────────────
    private void HandleKey(ConsoleKeyInfo k)
    {
        // Always-on hotkeys
        if (k.Key == ConsoleKey.Q && (k.Modifiers & ConsoleModifiers.Control) != 0) { _quit = true; return; }
        if (k.Key == ConsoleKey.O && (k.Modifiers & ConsoleModifiers.Control) != 0) { OpenOptions(); return; }

        if (_mode == Mode.Options) { HandleOptionsKey(k); return; }
        if (_mode == Mode.Filter)  { HandleFilterKey(k);  return; }
        if (_mode == Mode.SearchInput) { HandleSearchInputKey(k); return; }

        // Normal mode
        _toast = "";
        if (k.Key == ConsoleKey.Tab) { _tab = _tab == TabId.Browse ? TabId.Search : TabId.Browse; return; }

        if (_tab == TabId.Browse) HandleBrowseKey(k);
        else                       HandleSearchKey(k);
    }

    private void HandleBrowseKey(ConsoleKeyInfo k)
    {
        switch (k.Key)
        {
            case ConsoleKey.UpArrow:    _selected--; break;
            case ConsoleKey.DownArrow:  _selected++; break;
            case ConsoleKey.PageUp:     _selected -= 10; break;
            case ConsoleKey.PageDown:   _selected += 10; break;
            case ConsoleKey.Home:       _selected = 0; break;
            case ConsoleKey.End:        _selected = _visible.Count - 1; break;
            case ConsoleKey.Enter:
            case ConsoleKey.S:          _detailExpanded = !_detailExpanded; break;
            case ConsoleKey.C:          ToggleCompleted(); break;
            case ConsoleKey.R:          ResumeSelected(); break;
            case ConsoleKey.T:          ResumeSelected(toTerminal: true); break;
            case ConsoleKey.Oem2:       // '/'
            case ConsoleKey.Divide:     _mode = Mode.Filter; break;
            case ConsoleKey.V:          ToggleLayoutMode(); break;
            default:
                if (k.KeyChar == '/') _mode = Mode.Filter;
                break;
        }
    }

    private void ToggleLayoutMode()
    {
        var width = AnsiConsole.Console.Profile.Width;
        if (_settings.LayoutMode == "side-by-side")
        {
            _settings.LayoutMode = "stacked";
            _toast = "layout → stacked";
        }
        else if (width < 140)
        {
            _toast = $"side-by-side needs ≥140 cols (you have {width})";
            return;
        }
        else
        {
            _settings.LayoutMode = "side-by-side";
            _toast = "layout → side-by-side";
        }
        _settings.Save();
    }

    private void HandleFilterKey(ConsoleKeyInfo k)
    {
        switch (k.Key)
        {
            case ConsoleKey.Escape:
                _filter = ""; _mode = Mode.Normal; ApplyFilter(); break;
            case ConsoleKey.Enter:
                _mode = Mode.Normal; break;
            case ConsoleKey.Backspace:
                if (_filter.Length > 0) _filter = _filter[..^1];
                ApplyFilter();
                break;
            default:
                if (!char.IsControl(k.KeyChar))
                {
                    _filter += k.KeyChar;
                    ApplyFilter();
                }
                break;
        }
    }

    private void HandleSearchKey(ConsoleKeyInfo k)
    {
        switch (k.Key)
        {
            case ConsoleKey.UpArrow:    _searchSelected--; break;
            case ConsoleKey.DownArrow:  _searchSelected++; break;
            case ConsoleKey.PageUp:     _searchSelected -= 10; break;
            case ConsoleKey.PageDown:   _searchSelected += 10; break;
            case ConsoleKey.Enter:      _mode = Mode.SearchInput; break;
            case ConsoleKey.R:          ResumeSearchHit(); break;
            case ConsoleKey.T:          ResumeSearchHit(toTerminal: true); break;
            case ConsoleKey.V:          ToggleLayoutMode(); break;
            case ConsoleKey.Oem2:
            case ConsoleKey.Divide:     _mode = Mode.SearchInput; break;
            default:
                if (k.KeyChar == '/') _mode = Mode.SearchInput;
                else if (!char.IsControl(k.KeyChar)) { _mode = Mode.SearchInput; _searchQuery += k.KeyChar; }
                break;
        }
    }

    private void HandleSearchInputKey(ConsoleKeyInfo k)
    {
        switch (k.Key)
        {
            case ConsoleKey.Escape:
                _mode = Mode.Normal; break;
            case ConsoleKey.Enter:
                RunSearch();
                _mode = Mode.Normal;
                break;
            case ConsoleKey.Backspace:
                if (_searchQuery.Length > 0) _searchQuery = _searchQuery[..^1];
                break;
            default:
                if (!char.IsControl(k.KeyChar)) _searchQuery += k.KeyChar;
                break;
        }
    }

    private void OpenOptions()
    {
        _optionsPreviewBackup = _theme;
        _optionsCursor = Theme.All.ToList().FindIndex(t => t.Name == _theme.Name);
        if (_optionsCursor < 0) _optionsCursor = 0;
        _theme = Theme.All[_optionsCursor];   // preview live as we open
        _mode = Mode.Options;
    }

    private void HandleOptionsKey(ConsoleKeyInfo k)
    {
        switch (k.Key)
        {
            case ConsoleKey.UpArrow:
                _optionsCursor = (_optionsCursor - 1 + Theme.All.Count) % Theme.All.Count;
                _theme = Theme.All[_optionsCursor];
                break;
            case ConsoleKey.DownArrow:
                _optionsCursor = (_optionsCursor + 1) % Theme.All.Count;
                _theme = Theme.All[_optionsCursor];
                break;
            case ConsoleKey.Enter:
                _settings.Theme = _theme.Name;
                _settings.Save();
                _toast = $"theme → {_theme.DisplayName}";
                _mode = Mode.Normal;
                break;
            case ConsoleKey.Escape:
                _theme = _optionsPreviewBackup;
                _mode = Mode.Normal;
                break;
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Data + actions
    // ─────────────────────────────────────────────────────────────────
    private void ReloadSessions()
    {
        _all = SessionsListAdapter.LoadAll(_db);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (string.IsNullOrWhiteSpace(_filter))
            _visible = _all;
        else
        {
            var f = _filter.ToLowerInvariant();
            _visible = _all.Where(s =>
                (s.SummaryTitle?.ToLowerInvariant().Contains(f) ?? false) ||
                (s.SummaryShort?.ToLowerInvariant().Contains(f) ?? false) ||
                (s.FirstUserText?.ToLowerInvariant().Contains(f) ?? false) ||
                (s.Cwd?.ToLowerInvariant().Contains(f) ?? false) ||
                (s.GitBranch?.ToLowerInvariant().Contains(f) ?? false)).ToList();
        }
        if (_selected >= _visible.Count) _selected = Math.Max(0, _visible.Count - 1);
    }

    private TuiSessionRow? CurrentRow() =>
        _selected >= 0 && _selected < _visible.Count ? _visible[_selected] : null;

    private void ToggleCompleted()
    {
        var row = CurrentRow();
        if (row is null) return;
        var newStatus = row.Status == "completed" ? "active" : "completed";
        SessionsListAdapter.SetStatus(_db, row.Id, newStatus, "manual");
        _toast = $"{row.Id[..8]} → {newStatus}";
        var keepIdx = _selected;
        ReloadSessions();
        _selected = Math.Min(keepIdx, _visible.Count - 1);
    }

    // toTerminal: force a new wt.exe tab even when a resume override (e.g. cockpit)
    // is active — bound to 't'. The plain 'r' uses the override when present.
    private void ResumeSelected(bool toTerminal = false)
    {
        var row = CurrentRow();
        if (row is null) return;
        try
        {
            var info = new SessionLookup(_db).FindByPrefix(row.Id);
            if (info is null) { _toast = "session not in db"; return; }
            var plan = ResumeLauncher.Plan(info, null, useOverride: !toTerminal);
            ResumeLauncher.Launch(plan);
            _toast = ResumeDest(toTerminal, row.Id);
        }
        catch (Exception ex) { _toast = ex.Message; }
    }

    private static string ResumeDest(bool toTerminal, string id) =>
        !toTerminal && ResumeLauncher.OverrideActive
            ? $"→ {ResumeLauncher.OverrideLabel}: {id[..8]}"
            : $"→ new terminal: {id[..8]}";

    private void RunSearch()
    {
        if (string.IsNullOrWhiteSpace(_searchQuery))
        {
            _searchHits = new();
            _searchSelected = 0;
            return;
        }
        try
        {
            _searchHits = _search.Search(_searchQuery, _embedder, limit: 15, includeCompleted: false,
                reranker: _reranker, candidatePool: 50, maxPerSession: 1).ToList();
            _searchSelected = 0;
            _searchTopRow = 0;
        }
        catch (Exception ex) { _toast = ex.Message; }
    }

    private void ResumeSearchHit(bool toTerminal = false)
    {
        if (_searchHits.Count == 0) return;
        var hit = _searchHits[_searchSelected];
        try
        {
            var info = new SessionLookup(_db).FindByPrefix(hit.SessionId);
            if (info is null) { _toast = "session not in db"; return; }
            var plan = ResumeLauncher.Plan(info, null, useOverride: !toTerminal);
            ResumeLauncher.Launch(plan);
            _toast = ResumeDest(toTerminal, hit.SessionId);
        }
        catch (Exception ex) { _toast = ex.Message; }
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────
    private string RenderSnippet(string raw)
    {
        var t = _theme;
        var oneLine = System.Text.RegularExpressions.Regex.Replace(raw, @"\s+", " ").Trim();
        if (oneLine.Length > 220) oneLine = oneLine[..220] + "…";
        return Markup.Escape(oneLine)
            .Replace("«", $"[{t.Highlight}]")
            .Replace("»", "[/]");
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";

    private static string Esc(string s) => Markup.Escape(s ?? "");

    /// <summary>Pick a colour for the duration string based on its unit suffix (m/h/d/w).</summary>
    private static string FormatDuration(string duration, Theme t)
    {
        if (string.IsNullOrEmpty(duration)) return "";
        var slot = duration[^1] switch
        {
            'w' => t.DurWeek,
            'd' => t.DurDay,
            'h' => t.DurHour,
            'm' => t.DurMinute,
            _ => t.Dim,
        };
        return $"[{slot}]{duration}[/]";
    }
}
