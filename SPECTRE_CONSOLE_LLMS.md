# Spectre.Console Reference (llms.txt style)

> Condensed, LLM-friendly reference distilled from the official site docs at
> https://github.com/spectreconsole/website (`Spectre.Docs/Content/{cli,console}`).
> Two libraries:
> 1. **Spectre.Console** — rich rendering, widgets, prompts, live displays.
> 2. **Spectre.Console.Cli** — convention-driven command-line app framework.
>
> Both target .NET 6.0+. NuGet: `Spectre.Console`, `Spectre.Console.Cli`,
> `Spectre.Console.Json`, `Spectre.Console.Testing`, `Spectre.Console.Cli.Testing`.

## Table of contents

- [Spectre.Console](#spectreconsole)
  - [Markup syntax](#markup-syntax)
  - [Colors](#colors)
  - [Text styles / decorations](#text-styles--decorations)
  - [Widgets — output building blocks](#widgets--output-building-blocks)
    - [Markup](#markup-widget) · [Text](#text-widget) · [Panel](#panel) · [Table](#table) · [Grid](#grid) · [Rows](#rows) · [Columns](#columns)
    - [Tree](#tree) · [Rule](#rule) · [Layout](#layout) · [FigletText](#figlettext) · [JsonText](#jsontext)
    - [BarChart](#barchart) · [BreakdownChart](#breakdownchart) · [Calendar](#calendar) · [Padder / Align / Canvas / TextPath](#misc-widgets)
  - [Prompts (interactive input)](#prompts-interactive-input)
    - [TextPrompt / Ask / Confirm](#textprompt) · [SelectionPrompt](#selectionprompt) · [MultiSelectionPrompt](#multiselectionprompt)
  - [Live rendering](#live-rendering)
    - [Status (spinner)](#status-spinner) · [Progress (progress bars)](#progress-progress-bars) · [LiveDisplay](#livedisplay)
  - [Exceptions](#exceptions)
  - [Capabilities & terminal detection](#capabilities--terminal-detection)
  - [Rendering model & custom renderables](#rendering-model--custom-renderables)
  - [Testing console output](#testing-console-output)
- [Spectre.Console.Cli](#spectreconsolecli)
  - [Quick start](#cli-quick-start)
  - [Settings classes & attributes](#settings-classes--attributes)
  - [Multiple commands & branches](#multiple-commands--branches)
  - [App & command configuration](#app--command-configuration)
  - [Async & cancellation](#async--cancellation)
  - [Type converters](#type-converters)
  - [FlagValue, dictionaries, lookups](#flagvalue-dictionaries-lookups)
  - [Required options & validation](#required-options--validation)
  - [Hidden commands / options](#hidden-commands--options)
  - [Help customization](#help-customization)
  - [Error handling & exit codes](#error-handling--exit-codes)
  - [Interceptors](#interceptors)
  - [Dependency injection](#dependency-injection)
  - [CommandContext](#commandcontext)
  - [Built-in commands (`cli ...`)](#built-in-commands-cli-)
  - [Testing CLI apps](#testing-cli-apps)
- [Common pitfalls / gotchas](#common-pitfalls--gotchas)

---

# Spectre.Console

Install:
```bash
dotnet add package Spectre.Console
dotnet add package Spectre.Console.Json   # only if you use JsonText
```

Most output goes through the static `AnsiConsole` facade (or any `IAnsiConsole`):

```csharp
AnsiConsole.WriteLine("plain");
AnsiConsole.MarkupLine("[red]error[/]");
AnsiConsole.Write(new Panel("hi"));
```

Prefer **injecting `IAnsiConsole`** in classes that need testability — see
[Testing console output](#testing-console-output).

## Markup syntax

Inline tags: `[style]text[/]`. Every `[style]` needs a matching `[/]`.
Tag names are case-insensitive; multiple styles separate by spaces; order
inside a tag doesn't matter.

```csharp
AnsiConsole.MarkupLine("[red]Error[/]");
AnsiConsole.MarkupLine("[bold red]Critical[/]");           // combined
AnsiConsole.MarkupLine("[white on red]highlighted[/]");    // bg with `on`
AnsiConsole.MarkupLine("[on blue]default fg, blue bg[/]");
AnsiConsole.MarkupLine("[#FF5733]hex color[/]");
AnsiConsole.MarkupLine("[rgb(255,87,51)]rgb color[/]");
AnsiConsole.MarkupLine("[link=https://x.dev]click me[/]");
AnsiConsole.MarkupLine("[blue underline link=https://gh.com]GitHub[/]");
AnsiConsole.MarkupLine(":check_mark: done");               // emoji shortcode
```

Nesting: each `[/]` closes the most recent open tag.

```csharp
AnsiConsole.MarkupLine("[blue]Blue [bold]+bold[/] just blue[/]");
```

**Escaping** — required whenever dynamic content might contain `[` or `]`,
otherwise parsing throws.

```csharp
// escape a string
AnsiConsole.MarkupLine($"[green]ok:[/] {Markup.Escape(userInput)}");

// or use auto-escaping interpolation:
AnsiConsole.MarkupLineInterpolated($"[green]ok:[/] {userInput} ({count})");

// literal brackets in markup strings:
AnsiConsole.MarkupLine("Array [[0]]");      // prints: Array [0]

// strip markup back to plain text (for logging, width calc, etc.):
string plain = Markup.Remove("[bold red]Error:[/] thing");  // "Error: thing"
```

Pitfalls: unclosed `[red]` tag, unescaped user input, misspelled style name
(e.g. `[rde]`) — all throw.

## Colors

256 named colors plus hex `#RRGGBB` and `rgb(r,g,b)`. Spectre auto-degrades
colors based on detected terminal capability.

```csharp
AnsiConsole.MarkupLine("[red]named[/]");
AnsiConsole.MarkupLine("[deepskyblue1]exotic[/]");
AnsiConsole.MarkupLine("[grey]muted[/]");

// Color struct:
var style = new Style(foreground: Color.Aqua, background: Color.Black);
AnsiConsole.Write(new Text("styled", style));
```

Use `Color.Default` to mean "the terminal's default" rather than a specific RGB.
Common categories: `red`, `green`, `blue`, `yellow`, `cyan`, `magenta`,
`white`, `black`, `grey`/`grey0`–`grey100`, plus dozens of named hues
(`deepskyblue1`, `mediumvioletred`, etc.). The full list is rendered by the
`<ColorList />` component on the docs site.

## Text styles / decorations

| Tag | Effect |
|---|---|
| `bold` | bright/bold |
| `dim` | faint |
| `italic` | italic |
| `underline` | underline |
| `strikethrough` | strikethrough |
| `invert` | swap fg/bg |
| `conceal` | hide (passwords) |
| `slowblink` / `rapidblink` | blink |
| `link[=URL]` | OSC-8 hyperlink |

Programmatic equivalent — `Decoration` flags:

```csharp
var s = new Style(Color.White, decoration: Decoration.Bold | Decoration.Underline);
AnsiConsole.Write(new Text("hi", s));
// or compact form:
var s2 = Style.Parse("bold underline white on red");
```

Terminal support varies — blink and conceal are widely ignored.

## Widgets — output building blocks

Every widget implements `IRenderable`. Pass them to `AnsiConsole.Write(...)`.
You can compose: tables in panels, panels in trees, anything in layouts.

### Markup widget

Use `AnsiConsole.MarkupLine()` for one-off styled lines. Use `new Markup(...)`
when you need a renderable to embed in another widget:

```csharp
var inner = new Markup("[yellow]warn[/] something happened");
AnsiConsole.Write(new Panel(inner));
```

### Text widget

Programmatic styling, useful when style is computed at runtime:

```csharp
var t = new Text("hello", new Style(Color.Green, decoration: Decoration.Bold));
AnsiConsole.Write(t);

// multi-line and overflow:
var multi = new Text("very very long text here\nsecond line")
{
    Justification = Justify.Center,   // Left | Right | Center
    Overflow = Overflow.Ellipsis,     // Fold (default) | Crop | Ellipsis
};

// reusable static instances:
AnsiConsole.Write(Text.NewLine);
AnsiConsole.Write(Text.Empty);
```

### Panel

Bordered box around any renderable.

```csharp
var p = new Panel(new Markup("[green]success[/]"))
    .Header("[blue]Status[/]")
    .HeaderAlignment(Justify.Center)        // Left/Center/Right
    .Border(BoxBorder.Rounded)              // Ascii/Square/Rounded/Heavy/Double/None
    .BorderColor(Color.Grey)
    .Padding(2, 1, 2, 1)                    // left, top, right, bottom
    .Expand();                              // fill available width

AnsiConsole.Write(p);
```

`NoBorder()` keeps padding/header without a visible frame. Set explicit
`Width` for fixed sizing.

### Table

```csharp
var table = new Table()
    .Border(TableBorder.Rounded)            // see Table border list below
    .BorderColor(Color.Grey)
    .Title("[yellow]Users[/]")
    .Caption("[grey]rows: 3[/]")
    .ShowRowSeparators();                   // horizontal lines between rows

table.AddColumn(new TableColumn("Name").LeftAligned());
table.AddColumn(new TableColumn("Age").Centered().Width(6));
table.AddColumn(new TableColumn("Balance").RightAligned().NoWrap());

table.AddRow("Alice", "30", "$1,234");
table.AddRow("Bob",   "42", "$5,678");
table.AddEmptyRow();                        // visual gap
table.AddRow(new Markup("Carol"), new Markup("[red]?[/]"), new Markup("—"));

// Footers (e.g. totals):
table.Columns[2].Footer = new Markup("[bold]$6,912[/]");

// Hidden headers — gives a key-value style:
table.HideHeaders();

// Fill console width:
table.Expand();

AnsiConsole.Write(table);

// Dynamic updates (e.g. inside LiveDisplay):
table.UpdateCell(0, 2, "$2,000");
table.InsertRow(0, "Zed", "27", "$0");
table.RemoveRow(0);
```

**Cell content can be any IRenderable** — embed Markup, Panel, Table, etc.

Table border list (selection): `Ascii`, `Ascii2`, `AsciiDoubleHead`, `Square`,
`Rounded`, `Minimal`, `MinimalHeavyHead`, `MinimalDoubleHead`, `Simple`,
`SimpleHeavy`, `Horizontal`, `Heavy`, `HeavyEdge`, `HeavyHead`, `Double`,
`DoubleEdge`, `Markdown`, `None`. Each `TableBorder` has a `.SafeBorder`
ASCII fallback used automatically on terminals without Unicode.

### Grid

Like Table but borderless — for aligned key/value or dashboard-ish output.

```csharp
var g = new Grid();
g.AddColumn(new GridColumn().NoWrap().PadRight(2));
g.AddColumn(new GridColumn());
g.AddRow("Name:",     "Alice");
g.AddRow("Email:",    "alice@example.com");
g.AddRow("Verified:", "[green]yes[/]");
AnsiConsole.Write(g);

// shortcuts:
var g2 = new Grid();
g2.AddColumns(3);
g2.AddRow("a", "b", "c");
g2.Expand = true;
```

### Rows

Stack renderables vertically as a single unit (so they compose in panels,
columns, etc.).

```csharp
var r = new Rows(
    new Markup("[bold]Header[/]"),
    new Rule(),
    new Panel("body content"),
    new Markup("[grey]footer[/]"));
AnsiConsole.Write(r);
```

### Columns

Auto-flowing horizontal layout with automatic wrapping.

```csharp
var cards = new[]
{
    new Panel("Alice")  { Header = new PanelHeader("[blue]User[/]") },
    new Panel("Bob")    { Header = new PanelHeader("[blue]User[/]") },
    new Panel("Carol")  { Header = new PanelHeader("[blue]User[/]") },
};
AnsiConsole.Write(new Columns(cards).Expand());   // Collapse() to fit content
```

### Tree

Hierarchical view using box-drawing.

```csharp
var tree = new Tree("[yellow]project[/]");
var src  = tree.AddNode("src");
src.AddNode("Program.cs");
src.AddNode("Util.cs").AddNode("[grey]Helpers.cs[/]");   // chained
tree.AddNode("README.md").Collapse();                    // hide its children
tree.Style(Style.Parse("dim"));                          // guide-line style
tree.Guide = TreeGuide.Line;                             // Line / Ascii / DoubleLine / BoldLine
AnsiConsole.Write(tree);

// nodes can hold any IRenderable:
tree.AddNode(new Panel("notes"));

// Tree.Expanded = false collapses everything to root.
```

### Rule

Horizontal divider, optionally titled.

```csharp
AnsiConsole.Write(new Rule());                                  // plain line
AnsiConsole.Write(new Rule("[yellow]Section[/]"));               // with title
AnsiConsole.Write(new Rule("[blue]Status[/]")
    .LeftJustified()                                             // RuleLeft/Center/Right
    .RuleStyle(Style.Parse("dim"))
    .Border(BoxBorder.Heavy));                                   // line style
```

### Layout

Divide the screen into named regions you update by name.

```csharp
var layout = new Layout("Root")
    .SplitColumns(
        new Layout("Left").Size(20),                       // fixed 20 cols
        new Layout("Right")
            .SplitRows(
                new Layout("Top").Ratio(2),                // proportional
                new Layout("Bottom").MinimumSize(5)));     // floor
layout["Left"].Update(new Panel("nav"));
layout["Top"].Update(new Markup("hello"));
layout["Bottom"].Visible = false;                          // hide
AnsiConsole.Write(layout);
```

Pair with `LiveDisplay` for dashboards.

### FigletText

Big ASCII-art banners.

```csharp
AnsiConsole.Write(new FigletText("Spectre")
    .Color(Color.Aqua)
    .Centered());                                          // LeftJustified/Centered/RightJustified

// Custom .flf font:
var font = FigletFont.Load("starwars.flf");
AnsiConsole.Write(new FigletText(font, "BOOM"));
```

### JsonText

Syntax-highlighted JSON. Requires `Spectre.Console.Json`.

```csharp
using Spectre.Console.Json;

var json = new JsonText("""{ "name": "alice", "age": 30, "tags": ["a","b"] }""")
    .MemberColor(Color.Blue)
    .StringStyle(Style.Parse("green"))
    .NumberColor(Color.Yellow)
    .BooleanColor(Color.Red)
    .NullColor(Color.Grey)
    .BracesColor(Color.White)
    .BracketColor(Color.White)
    .ColonColor(Color.White)
    .CommaColor(Color.White);

AnsiConsole.Write(new Panel(json).Header("API response"));
```

### BarChart

```csharp
AnsiConsole.Write(new BarChart()
    .Width(60)
    .Label("[green bold]Sales[/]")
    .CenterLabel()                                  // LeftAlignLabel/CenterLabel/RightAlignLabel
    .AddItem("Jan", 12, Color.Yellow)
    .AddItem("Feb", 54, Color.Green)
    .AddItem("Mar", 33, Color.Red)
    .UseValueFormatter(v => $"${v:N0}")
    .WithMaxValue(100));                            // fixed scale (default = max value)

// Hide values:
new BarChart().HideValues();

// Bulk add:
new BarChart().AddItems(items, item => new BarChartItem(item.Name, item.Value, Color.Blue));
```

### BreakdownChart

Single horizontal bar split into proportional segments — for part/whole.

```csharp
AnsiConsole.Write(new BreakdownChart()
    .Width(60)
    .ShowPercentage()
    .UseValueFormatter(v => $"{v:N0} GB")
    .AddItem("Used",  120, Color.Red)
    .AddItem("Cache",  20, Color.Yellow)
    .AddItem("Free",  360, Color.Green)
    .Compact());                                    // .FullSize() = legend has more spacing
// .HideTags() / .HideTagValues() to control legend
```

### Calendar

```csharp
var cal = new Calendar(2026, 4)                      // year, month
    .HighlightStyle(Style.Parse("blue bold"))
    .HeaderStyle(Style.Parse("yellow"));
cal.AddCalendarEvent(2026, 4, 28);
cal.AddCalendarEvent(new DateTime(2026, 4, 30));
cal.AddCalendarEvent("review",
    new DateTime(2026, 4, 15),
    Style.Parse("red"));                             // per-event style
cal.HideHeader();
cal.Border = TableBorder.Rounded;                    // uses TableBorder
cal.Culture = new CultureInfo("de-DE");              // localized day/month names
AnsiConsole.Write(cal);
```

### Misc widgets

- **Padder** — wrap any renderable with extra padding.
  `new Padder(child).Padding(1,1,1,1)`.
- **Align** — center/justify a renderable inside available space.
  `Align.Center(child, VerticalAlignment.Middle)`.
- **Canvas / CanvasImage** — draw raw pixels or render images (`SixLabors.ImageSharp`).
  `new CanvasImage("logo.png").MaxWidth(40)`.
- **TextPath** — render filesystem paths with separators highlighted.
  `new TextPath("/usr/local/bin/dotnet").RootStyle(Style.Parse("red"))`.

## Prompts (interactive input)

> ⚠ **Prompts, Status, Progress, and LiveDisplay are NOT thread-safe and CANNOT
> be nested or run concurrently with each other.** Only one interactive
> component at a time.

### TextPrompt

`AnsiConsole.Ask<T>()` is the convenient form — uses .NET `TypeConverter`s, so
`int`, `decimal`, `DateTime`, `Guid`, etc. work automatically.

```csharp
string name  = AnsiConsole.Ask<string>("Your [green]name[/]?");
string user  = AnsiConsole.Ask("Username:", "alice");      // with default
int    age   = AnsiConsole.Ask<int>("Age?");
bool   ok    = AnsiConsole.Confirm("Proceed?", defaultValue: true);
```

`TextPrompt<T>` for fine-grained control:

```csharp
var pwd = AnsiConsole.Prompt(
    new TextPrompt<string>("Password:")
        .PromptStyle("red")
        .Secret('*'));                              // null = fully hidden, char = mask

var port = AnsiConsole.Prompt(
    new TextPrompt<int>("Port?")
        .DefaultValue(8080)
        .ShowDefaultValue()                          // visible to user
        .Validate(p => p is > 0 and < 65536, "Invalid port"));

var color = AnsiConsole.Prompt(
    new TextPrompt<string>("Favorite color?")
        .AddChoices("red", "green", "blue")          // restrict to set
        .DefaultValue("blue")
        .HideChoices()                               // accept the set, but don't show it
        .InvalidChoiceMessage("[red]Not a valid color[/]"));

// Allow blank input:
var optional = AnsiConsole.Prompt(
    new TextPrompt<string>("Notes (optional):")
        .AllowEmpty());

// Rich validation:
var v = AnsiConsole.Prompt(
    new TextPrompt<string>("Username:")
        .Validate(static u =>
            u.Length < 3   ? ValidationResult.Error("[red]too short[/]")
          : u.Length > 20  ? ValidationResult.Error("[red]too long[/]")
          : ValidationResult.Success()));

// Custom display for choices:
var item = AnsiConsole.Prompt(
    new TextPrompt<MyItem>("Pick:")
        .AddChoices(items)
        .WithConverter(i => i.DisplayName));
```

### SelectionPrompt

Arrow-key menu for one of N options.

```csharp
var fruit = AnsiConsole.Prompt(
    new SelectionPrompt<string>()
        .Title("Pick a [green]fruit[/]:")
        .PageSize(10)                                // visible window
        .MoreChoicesText("[grey](more below)[/]")
        .EnableSearch()                              // user types to filter
        .SearchHighlightStyle(Style.Parse("yellow bold"))
        .HighlightStyle(Style.Parse("blue bold"))
        .WrapAround()                                // wrap top↔bottom
        .AddChoices("Apple", "Banana", "Cherry"));

// Hierarchical:
var prompt = new SelectionPrompt<string>()
    .Mode(SelectionMode.Leaf);                       // or Independent
prompt.AddChoiceGroup("Fruit", "Apple", "Banana");
prompt.AddChoiceGroup("Veg",   "Carrot", "Potato");

// Custom objects + display converter:
var project = AnsiConsole.Prompt(
    new SelectionPrompt<Project>()
        .AddChoices(allProjects)
        .UseConverter(p => $"{p.Name} ([grey]{p.Id}[/])"));
```

### MultiSelectionPrompt

Same UX as SelectionPrompt but spacebar toggles, returns `List<T>`.

```csharp
var picks = AnsiConsole.Prompt(
    new MultiSelectionPrompt<string>()
        .Title("Features?")
        .Required()                                  // or .NotRequired()
        .PageSize(8)
        .InstructionsText("[grey](space toggle, enter confirm)[/]")
        .AddChoices("auth", "metrics", "tracing", "i18n")
        .Select("auth")                              // pre-check
        .HighlightStyle(Style.Parse("green bold"))
        .WrapAround()
        .UseConverter(s => s.ToUpperInvariant()));

// Hierarchical via AddChoiceGroup or programmatic AddChild on items.
// Modes: SelectionMode.Leaf (default — only leaves selectable) /
//        SelectionMode.Independent (any node).
```

## Live rendering

> Same warning as prompts — Status, Progress, LiveDisplay are not thread-safe
> and can't run concurrently with prompts or each other.

### Status (spinner)

For indeterminate work.

```csharp
AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)                     // see Spinner.Known.* for many styles
    .SpinnerStyle(Style.Parse("green"))
    .Start("Connecting...", ctx =>
    {
        DoWork();
        ctx.Status("Authenticating...");             // update message
        ctx.Spinner(Spinner.Known.Star);             // change spinner
        ctx.Refresh();                               // when AutoRefresh is off
        DoMore();
    });

// async + return value:
var result = await AnsiConsole.Status()
    .StartAsync("Loading...", async ctx =>
    {
        return await client.GetAsync(...);
    });
```

Many built-in `Spinner.Known.*` animations (`Dots`, `Dots2`, `Line`, `Star`,
`Arc`, `BouncingBar`, `Clock`, `Earth`, `Moon`, `Pong`, etc.).

### Progress (progress bars)

For determinate (or indeterminate) tasks with visible bars.

```csharp
AnsiConsole.Progress()
    .AutoClear(false)                                // remove after done?
    .HideCompleted(false)                            // hide finished tasks?
    .Columns(new ProgressColumn[]
    {
        new TaskDescriptionColumn(),
        new ProgressBarColumn(),
        new PercentageColumn(),
        new RemainingTimeColumn(),
        new SpinnerColumn(),
        // For downloads:
        // new DownloadedColumn(),
        // new TransferSpeedColumn(),
    })
    .Start(ctx =>
    {
        var t1 = ctx.AddTask("[green]Step 1[/]", maxValue: 100);
        var t2 = ctx.AddTask("[blue]Step 2[/]");

        while (!ctx.IsFinished)
        {
            t1.Increment(2);
            t2.Value = Math.Min(100, t2.Value + 1);
            t2.Description = $"[blue]Step 2 — {t2.Value:N0}%[/]";

            // discover work mid-flight:
            if (t1.Value > 50 && t1.MaxValue == 100)
                ctx.AddTask("dynamic");

            Thread.Sleep(20);
        }
    });

// Indeterminate (unknown total):
var t = ctx.AddTask("scanning").IsIndeterminate(true);

// async + returning a value:
var r = await AnsiConsole.Progress()
    .StartAsync(async ctx =>
    {
        var task = ctx.AddTask("download");
        // ...
        return data;
    });

// Style the bar:
new ProgressBarColumn { CompletedStyle = new Style(Color.Green),
                       FinishedStyle  = new Style(Color.Grey),
                       RemainingStyle = new Style(Color.Grey15) };
```

### LiveDisplay

Update arbitrary renderable in place — best for dashboards that aren't a fit
for Progress.

```csharp
var table = new Table().AddColumn("metric").AddColumn("value");

AnsiConsole.Live(table)
    .AutoClear(false)
    .Overflow(VerticalOverflow.Ellipsis)             // Visible / Crop / Ellipsis
    .Cropping(VerticalOverflowCropping.Top)          // Top / Bottom
    .Start(ctx =>
    {
        for (var i = 0; i < 5; i++)
        {
            table.AddRow($"row {i}", $"{i * 10}");
            ctx.Refresh();                           // re-render

            // swap whole renderable:
            // ctx.UpdateTarget(new Panel(table));
            Thread.Sleep(200);
        }
    });

// async + return:
var r = await AnsiConsole.Live(layout).StartAsync<int>(async ctx =>
{
    // ...
    return 42;
});
```

## Exceptions

```csharp
try { Risky(); }
catch (Exception ex)
{
    AnsiConsole.WriteException(ex);                                  // default
    AnsiConsole.WriteException(ex, ExceptionFormats.ShortenPaths);   // tidy paths
    AnsiConsole.WriteException(ex,
        ExceptionFormats.ShortenEverything | ExceptionFormats.ShowLinks);

    // Custom colors:
    AnsiConsole.WriteException(ex, new ExceptionSettings
    {
        Format = ExceptionFormats.Default,
        Style  = new ExceptionStyle
        {
            Exception   = new Style(Color.Grey),
            Message     = new Style(Color.White),
            NonEmphasized = new Style(Color.Cornsilk1),
            Parenthesis = new Style(Color.Cornsilk1),
            Method      = new Style(Color.Red),
            ParameterName  = new Style(Color.Cornsilk1),
            ParameterType  = new Style(Color.Red),
            Path        = new Style(Color.Red),
            LineNumber  = new Style(Color.Cornsilk1),
        }
    });
}
```

## Capabilities & terminal detection

`AnsiConsole.Profile.Capabilities` exposes:

| Capability | Meaning |
|---|---|
| `ColorSystem` | `NoColors` / `Standard` (8) / `EightBit` (256) / `TrueColor` (24-bit) |
| `Ansi` | terminal understands ANSI escape codes |
| `Links` | OSC-8 hyperlink support |
| `Interactive` | a human is there (false in CI) |
| `Unicode` | terminal can draw `─│┌` etc. |
| `Legacy` | old Windows console |

Influencing env vars:

| Var | Effect |
|---|---|
| `NO_COLOR=1` | disable all color (https://no-color.org) |
| `COLORTERM=truecolor` (or `24bit`) | enable 24-bit color on Unix |
| `TERM` | terminal type (e.g. `xterm-256color`) |
| `ConEmuANSI=On` | ConEmu on Windows |

CI auto-detection (`Interactive=false`): GitHub Actions (`GITHUB_ACTIONS`),
Azure Pipelines (`TF_BUILD`), GitLab (`CI_SERVER`), Jenkins (`JENKINS_URL`),
Travis (`TRAVIS`), TeamCity (`TEAMCITY_VERSION`), AppVeyor, Bitbucket,
CircleCI, Bamboo, Bitrise, GoCD, MyGet, Continua. GitHub Actions also gets
`Ansi=true` even when standard detection would say no.

Override (e.g. for unit tests, weird terminals):

```csharp
var console = AnsiConsole.Create(new AnsiConsoleSettings
{
    Ansi        = AnsiSupport.Yes,
    ColorSystem = ColorSystemSupport.TrueColor,
    Interactive = InteractionSupport.No,
});

AnsiConsole.Profile.Capabilities.Unicode = false;     // tweak default
```

Windows + UTF-8: in code,
`Console.OutputEncoding = System.Text.Encoding.UTF8;` (or `chcp 65001`).

## Rendering model & custom renderables

```csharp
public interface IRenderable
{
    Measurement Measure(RenderOptions options, int maxWidth);
    IEnumerable<Segment> Render(RenderOptions options, int maxWidth);
}
```

- **Measure** returns min/max widths *without* producing output, so containers
  (Panel, Table, …) can plan layout.
- **Render** returns a stream of `Segment`s — atomic styled text units (text,
  style, optional line-break / control-code flag). `Segment.CellCount()` gives
  the actual on-screen width (handles wide CJK and emoji as 2 cells).

Width chain: `Profile.Width` → output stream width → `Console.BufferWidth` →
80 fallback. Implement a custom widget by subclassing `Renderable` and
overriding `Measure` / `Render` (or `IHasCulture`, `IHasJustification`, etc.,
when those mixins make sense).

Live displays (Progress / Status / LiveDisplay) work via:
1. Hide cursor.
2. Attach a render hook to the pipeline.
3. A background timer (default ~100ms) re-renders.
4. Each frame moves cursor up N lines and overwrites in place — no full
   clear/redraw, so no flicker. Padding spaces erase leftovers from the
   previous (taller) frame.

The whole render pipeline is serialized under a render lock — no interleaved
output between threads, and that's also why prompts & live displays can't
run concurrently.

## Testing console output

```bash
dotnet add package Spectre.Console.Testing
```

Inject `IAnsiConsole` instead of using static `AnsiConsole` — pass
`TestConsole` in tests:

```csharp
public void Greet(IAnsiConsole console, string name)
    => console.MarkupLine($"[green]Hello[/], {name}!");

[Fact]
public void GreetsByName()
{
    var console = new TestConsole();
    Greet(console, "Alice");

    // .Output strips markup; .Lines is per-line
    Assert.Contains("Hello, Alice!", console.Output);
}

// For prompts, queue input first:
[Fact]
public void AsksForName()
{
    var console = new TestConsole();
    console.Input.PushTextWithEnter("Alice");
    // for arrow keys / enter directly:
    console.Input.PushKey(ConsoleKey.DownArrow);
    console.Input.PushKey(ConsoleKey.Enter);

    var name = console.Ask<string>("Name?");
    Assert.Equal("Alice", name);
}

// To test ANSI emission, set capabilities:
console.Profile.Capabilities.Ansi = true;
console.Profile.Capabilities.ColorSystem = ColorSystem.TrueColor;
```

---

# Spectre.Console.Cli

Convention-over-configuration command-line app framework. Commands are
classes that bind to a typed `Settings` class via attributes; the framework
parses argv, validates types, generates help, and reports errors.

Install:
```bash
dotnet add package Spectre.Console.Cli
```

## CLI quick start

A single-command app:

```csharp
// Program.cs
using Spectre.Console.Cli;

var app = new CommandApp<GreetCommand>();
return app.Run(args);

// GreetCommand.cs
public sealed class GreetCommand : Command<GreetCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("The name to greet")]
        public string Name { get; init; } = string.Empty;

        [CommandOption("-c|--count")]
        [Description("How many times to greet")]
        [DefaultValue(1)]
        public int Count { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        for (var i = 0; i < settings.Count; i++)
            AnsiConsole.MarkupLine($"Hello, [green]{settings.Name}[/]!");
        return 0;
    }
}
```

Run: `dotnet run -- Alice -c 3`. Help (`--help` / `-h`) is generated for
free; missing required arg gives a clear error; invalid `-c abc` gives a
clear type-conversion error.

## Settings classes & attributes

Settings inherit from `CommandSettings`. Attributes:

| Attribute | From | Purpose |
|---|---|---|
| `[CommandArgument(int pos, string template)]` | `Spectre.Console.Cli` | positional argument |
| `[CommandOption(string template, bool isRequired = false)]` | `Spectre.Console.Cli` | named option/flag |
| `[Description("...")]` | `System.ComponentModel` | help text |
| `[DefaultValue(...)]` | `System.ComponentModel` | default value |
| `[TypeConverter(typeof(C))]` | `System.ComponentModel` | custom parsing |

**Argument template syntax** (positional):
- `<name>` — required.
- `[name]` — optional.
- Array argument captures all remaining positional values; **must be the last
  argument**.

**Option template syntax**:
- `-v` short, `--verbose` long, `-v|--verbose` both, multi-alias allowed
  (`-v|--verbose|--debug`).
- `[CommandOption("-o|--output <PATH>")]` — `<PATH>` is the value placeholder
  for help display.
- Boolean → flag: presence sets `true`, absence `false`.
- `IsHidden = true` hides from help; `ValueIsOptional = true` allows `--flag`
  without a value.

```csharp
public class CopySettings : CommandSettings
{
    [CommandArgument(0, "<source>")]
    [Description("Source path")]
    public string Source { get; init; } = "";

    [CommandArgument(1, "[destination]")]
    public string? Destination { get; init; }

    [CommandOption("-f|--force")]
    [Description("Overwrite existing files")]
    public bool Force { get; init; }

    [CommandOption("-b|--buffer-size <KB>")]
    [DefaultValue(64)]
    public int BufferSizeKb { get; init; }

    [CommandOption("--preserve-timestamps")]
    public bool PreserveTimestamps { get; init; }

    // array-arg + array-option:
    [CommandArgument(0, "[files]")]
    public string[] Files { get; init; } = Array.Empty<string>();

    [CommandOption("-t|--tag <TAG>")]
    public string[] Tags { get; init; } = Array.Empty<string>();

    // enum — parsed by name (case-insensitive) or numeric value:
    [CommandOption("--level <LEVEL>")]
    public LogLevel Level { get; init; }

    public override ValidationResult Validate()
    {
        if (BufferSizeKb < 1)
            return ValidationResult.Error("--buffer-size must be > 0");
        return ValidationResult.Success();
    }
}
```

Custom reusable validators inherit from `ParameterValidationAttribute`:

```csharp
public sealed class FileExistsAttribute : ParameterValidationAttribute
{
    public FileExistsAttribute() : base("File does not exist") { }
    public override ValidationResult Validate(CommandParameterContext context)
        => context.Value is string p && File.Exists(p)
            ? ValidationResult.Success()
            : ValidationResult.Error(ErrorMessage);
}

[FileExists, CommandArgument(0, "<path>")]
public string Path { get; init; } = "";
```

## Multiple commands & branches

```csharp
var app = new CommandApp();
app.Configure(config =>
{
    config.AddCommand<AddCommand>("add");
    config.AddCommand<ListCommand>("list");

    // grouped subcommands:
    config.AddBranch("remote", remote =>
    {
        remote.AddCommand<RemoteAddCommand>("add");
        remote.AddCommand<RemoteRemoveCommand>("remove");
        remote.AddCommand<RemoteListCommand>("list");
    });

    // shared settings across a branch (every subcommand's settings must inherit
    // from RemoteSettings):
    config.AddBranch<RemoteSettings>("cloud", cloud =>
    {
        cloud.AddCommand<UploadCommand>("upload");
        cloud.AddCommand<DownloadCommand>("download");
    });
});
return app.Run(args);
```

Settings inheritance for shared options:

```csharp
public class GlobalSettings : CommandSettings
{
    [CommandOption("-v|--verbose")]
    public bool Verbose { get; init; }
}

public class AddSettings : GlobalSettings
{
    [CommandArgument(0, "<package>")]
    public string Package { get; init; } = "";
}
```

When using `AddBranch<TSettings>`, every subcommand's settings class must
inherit from `TSettings` — even subcommands that add nothing should declare a
dedicated empty settings class that inherits, otherwise binding breaks.

## App & command configuration

```csharp
app.Configure(config =>
{
    config.SetApplicationName("myapp");
    config.SetApplicationVersion("1.2.3");           // shown by --version

    config.AddExample(["production"]);
    config.AddExample(["staging", "--force"]);

    config.AddCommand<AddCommand>("add")
          .WithAlias("a")
          .WithDescription("Add a new item")
          .WithExample(["add", "foo"])
          .WithData(new { Tag = "user" });           // -> CommandContext.Data

    config.AddCommand<RemoveCommand>("remove")
          .WithAlias("rm")
          .WithAlias("delete");

    // global parsing settings:
    config.Settings.CaseSensitivity = CaseSensitivity.None;
    config.Settings.StrictParsing  = false;          // unknown -flags become Remaining

    // dev-only safety nets:
#if DEBUG
    config.PropagateExceptions();                    // bypass framework error handler
    config.ValidateExamples();                       // verify WithExample at startup
#endif
});
```

## Async & cancellation

```csharp
public sealed class FetchCommand : AsyncCommand<FetchCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<url>")]
        public string Url { get; init; } = "";
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken ct)
    {
        using var http = new HttpClient();
        var resp = await http.GetStringAsync(settings.Url, ct);
        AnsiConsole.WriteLine(resp);
        return 0;
    }
}

// Wire up Ctrl+C → token:
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var app = new CommandApp<FetchCommand>();
return await app.RunAsync(args, cts.Token);
```

## Type converters

Built-in (no setup): `string`, `int`, `long`, `short`, `byte`, `float`,
`double`, `decimal`, `bool`, `char`, `FileInfo`, `DirectoryInfo`, `Uri`,
`Guid`, `DateTime`, `TimeSpan`, enums (case-insensitive name or numeric),
nullable variants of all of these, and arrays/collections of any supported
type.

Custom converter:

```csharp
public sealed class ConnectionStringConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? ctx, Type src)
        => src == typeof(string) || base.CanConvertFrom(ctx, src);

    public override object? ConvertFrom(
        ITypeDescriptorContext? ctx, CultureInfo? culture, object value)
    {
        if (value is string s)
        {
            var parts = s.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out var port))
                throw new InvalidOperationException(
                    $"Invalid format: '{s}'. Expected host:port");
            return new ConnectionInfo(parts[0], port);
        }
        return base.ConvertFrom(ctx, culture, value);
    }
}

[TypeConverter(typeof(ConnectionStringConverter))]
public sealed record ConnectionInfo(string Host, int Port);

// Property — automatic if the type itself has [TypeConverter]:
[CommandOption("--connection")]
public ConnectionInfo? Connection { get; init; }

// Or attach per-property:
[TypeConverter(typeof(ConnectionStringConverter))]
[CommandOption("--cn")]
public ConnectionInfo? Cn { get; init; }
```

For types you don't own, register through the type registrar (DI):

```csharp
services.AddSingleton<TypeConverter, ThirdPartyTypeConverter>();
```

Use the supplied `culture` for locale-aware parsing
(`Settings.Culture` controls it).

## FlagValue, dictionaries, lookups

**`FlagValue<T>`** — flag that can be: absent, present (default value), or
present with a value.

```csharp
[CommandOption("--port [PORT]")]              // square brackets → optional value
public FlagValue<int> Port { get; init; } = new();
[DefaultValue(3000)] public int PortDefault { get; init; }   // optional pattern

// inside Execute:
if (settings.Port.IsSet)                      // flag present?
{
    var p = settings.Port.Value;              // 0 if no value supplied
}
```

**Key/value options**:

```csharp
[CommandOption("--value <VALUE>")]
public IDictionary<string, int>? Values { get; init; }

[CommandOption("--lookup <VALUE>")]
public ILookup<string, string>? Lookups { get; init; }   // multiple values per key

[CommandOption("--setting <VALUE>")]
public IReadOnlyDictionary<string, string>? Settings { get; init; }
```

CLI form:
```bash
myapp --value port=8080 --value timeout=30
myapp --lookup env=dev --lookup env=stage --lookup region=us
```

`ILookup` lets one key carry multiple values; `IDictionary` is one-per-key
(later wins, depending on framework version). Key and value types can be any
convertible type.

## Required options & validation

```csharp
[CommandOption("-e|--environment <ENV>", IsRequired = true)]
public string Environment { get; init; } = "";

// or via constructor parameter:
[CommandOption("-v|--version <VERSION>", isRequired: true)]
public string Version { get; init; } = "";
```

Cross-field validation lives in `Settings.Validate()`:

```csharp
public override ValidationResult Validate()
{
    if (string.IsNullOrEmpty(ConnectionString) && string.IsNullOrEmpty(Host))
        return ValidationResult.Error("Provide either --connection-string or --host");
    return ValidationResult.Success();
}
```

## Hidden commands / options

```csharp
config.AddCommand<DiagnosticsCommand>("diagnostics").IsHidden();

[CommandOption("--skip-hooks", IsHidden = true)]
public bool SkipHooks { get; init; }
```

Hidden items still work — they're just absent from `--help`.

## Help customization

```csharp
config.SetApplicationName("myapp");
config.AddExample(["production"]);
config.AddExample(["staging", "--force"]);

// styled help:
config.Settings.HelpProviderStyles = new HelpProviderStyle
{
    Description = new DescriptionStyle    { Header = "yellow bold" },
    Arguments   = new ArgumentStyle       { Header = "yellow bold",
                                            RequiredArgument = "aqua",
                                            OptionalArgument = "silver" },
    Options     = new OptionStyle         { Header = "yellow bold",
                                            DefaultValue = "bold" },
    Commands    = new CommandStyle        { Header = "yellow bold",
                                            ChildCommand = "aqua" },
    Examples    = new ExampleStyle        { Header = "yellow bold" },
};

// plain text (no styling):
config.Settings.HelpProviderStyles = null;

// totally custom help:
config.SetHelpProvider(new MyHelpProvider(config.Settings));
```

## Error handling & exit codes

By default, parsing/execution errors are caught, formatted, and produce
exit code `-1`. Two ways to customize:

```csharp
// (1) centralized handler — applies to parsing AND execution:
config.SetExceptionHandler((ex, resolver) => ex switch
{
    FileNotFoundException     => 3,
    InvalidOperationException => 2,
    _                         => { AnsiConsole.WriteException(ex); 1 },
});
// resolver is null during parsing (before command resolution).

// (2) propagate — disables framework handling, you write try/catch:
config.PropagateExceptions();

try { return app.Run(args); }
catch (CommandRuntimeException ex) { /* parsing/binding error */ }
catch (Exception ex)               { /* command threw */ }
```

`SetExceptionHandler` is **not** called when `PropagateExceptions()` is set.

## Interceptors

Cross-cutting before/after hooks for every command (logging, timing, auth):

```csharp
public sealed class TimingInterceptor : ICommandInterceptor
{
    private readonly Stopwatch _sw = new();
    public void Intercept(CommandContext context, CommandSettings settings)
        => _sw.Start();
    public void InterceptResult(CommandContext context, CommandSettings settings, ref int result)
    {
        _sw.Stop();
        AnsiConsole.MarkupLine($"[grey]elapsed: {_sw.ElapsedMilliseconds} ms[/]");
        // ref int — you can mutate the exit code here
    }
}

config.SetInterceptor(new TimingInterceptor());
```

`InterceptResult` exit-code is `ref int` — you can override it post-execution.

## Dependency injection

Spectre.Console.Cli has its own `ITypeRegistrar` / `ITypeResolver` abstractions
so it can wire into any container. Microsoft DI bridge:

```csharp
public sealed class TypeRegistrar(IServiceCollection services) : ITypeRegistrar
{
    public ITypeResolver Build() => new TypeResolver(services.BuildServiceProvider());
    public void Register(Type service, Type impl)              => services.AddSingleton(service, impl);
    public void RegisterInstance(Type service, object impl)    => services.AddSingleton(service, impl);
    public void RegisterLazy(Type service, Func<object> factory)
        => services.AddSingleton(service, _ => factory());
}

public sealed class TypeResolver(IServiceProvider provider) : ITypeResolver, IDisposable
{
    public object? Resolve(Type? type) => type is null ? null : provider.GetService(type);
    public void Dispose() => (provider as IDisposable)?.Dispose();
}
```

Wire-up:

```csharp
var services = new ServiceCollection();
services.AddSingleton<IGreetingService, GreetingService>();
// IAnsiConsole is auto-registered by the framework — inject it instead of using the static class.

var app = new CommandApp<GreetCommand>(new TypeRegistrar(services));
return app.Run(args);
```

The framework auto-registers each command's `Settings` instance, so a
factory or service can take `Settings` as a constructor parameter to make
runtime-driven decisions (e.g., keyed-service factories for .NET 8+):

```csharp
services.AddKeyedSingleton<IGreetingService, CasualGreetingService>(GreetingStyle.Casual);
services.AddKeyedSingleton<IGreetingService, FormalGreetingService>(GreetingStyle.Formal);
services.AddScoped<IGreetingFactory, GreetingFactory>();    // takes GreetSettings via DI
```

## CommandContext

Passed to every `Execute` / `ExecuteAsync`:

| Property | Type | Description |
|---|---|---|
| `Name` | `string` | Command name as invoked |
| `Arguments` | `IReadOnlyList<string>` | Full raw argv |
| `Remaining` | `IRemainingArguments` | unmatched / pass-through args |
| `Data` | `object?` | data attached via `.WithData(...)` |

`IRemainingArguments`:
- `.Parsed` — unknown options as `ILookup<string, string?>`
  (`myapp --known x --unknown foo` → `["unknown"] = ["foo"]`).
- `.Raw` — everything after `--` literally
  (`myapp --verbose -- a b --c` → `Raw = ["a","b","--c"]`).

Useful for wrapping another tool:

```csharp
public override int Execute(CommandContext ctx, Settings _)
{
    var passthrough = string.Join(' ', ctx.Remaining.Raw);
    return Process.Start("other-tool", passthrough)!.ExitCode;
}
```

## Built-in commands (`cli ...`)

Spectre.Console.Cli ships with a hidden `cli` branch:

| Command | Access | Purpose |
|---|---|---|
| `cli version` | also `--version` / `-v` (needs `SetApplicationVersion`) | print library + app versions |
| `cli explain [cmd]` | options: `-d/--detailed`, `--hidden` | diagnostic tree of CLI config |
| `cli xmldoc` | — | machine-readable XML of the whole CLI |
| `cli opencli` | also `--help-dump-opencli` | OpenCli-spec dump |

Help (`-h` / `--help`) is automatic on every command and branch.

## Testing CLI apps

```bash
dotnet add package Spectre.Console.Cli.Testing
```

Inject `IAnsiConsole` (auto-registered) into commands, then:

```csharp
public sealed class GreetCommand(IAnsiConsole console) : Command<GreetCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<name>")] public string Name { get; init; } = "";
    }
    public override int Execute(CommandContext _, Settings s)
    {
        console.MarkupLine($"Hello, [green]{s.Name}[/]!");
        return 0;
    }
}

[Fact]
public void Greet_returns_zero_and_prints()
{
    var app = new CommandAppTester();
    app.Configure(c => c.AddCommand<GreetCommand>("greet"));

    var r = app.Run("greet", "Alice");

    Assert.Equal(0, r.ExitCode);
    Assert.Contains("Hello, Alice!", r.Output);
    Assert.IsType<GreetCommand.Settings>(r.Settings);
    var s = (GreetCommand.Settings)r.Settings;
    Assert.Equal("Alice", s.Name);
}

// Interactive prompts in commands — queue input via TestConsole:
var console = new TestConsole();
console.Input.PushTextWithEnter("Alice");           // for text
console.Input.PushKey(ConsoleKey.DownArrow);        // arrows / enter
console.Input.PushKey(ConsoleKey.Enter);

var app = new CommandAppTester(registrar);          // pass a registrar that
                                                    //   binds the TestConsole as IAnsiConsole
```

---

# Common pitfalls / gotchas

- **Unescaped markup brackets in dynamic content throw.** Use
  `Markup.Escape(s)` or `MarkupLineInterpolated($"…{x}…")`.
- **Prompts / Status / Progress / LiveDisplay are not thread-safe and CANNOT
  be nested or run concurrently.** The framework will throw if you try.
- **Live-rendering APIs hide the cursor and hijack stdout** — third-party
  libraries that write to `Console.Out` directly during a live display will
  corrupt the frame; route them through `IAnsiConsole` or write before/after.
- **In CI, `Interactive` is auto-set to false.** Prompts will throw rather than
  hang. Prefer `--flag` overrides or environment variables for non-interactive
  runs; in tests, force `console.Profile.Capabilities.Interactive = true`.
- **Array `[CommandArgument]` must be the last positional argument.**
- **`AddBranch<T>` requires every subcommand's `Settings` to inherit from
  `T`** — even empty subcommand settings need a dedicated class that inherits.
- **Boolean options have no value** — `[CommandOption("--force")] bool Force`
  is set by presence; if you want tri-state, use `bool?` or `FlagValue<bool>`.
- **Custom `TypeConverter` errors should throw `InvalidOperationException` or
  `FormatException` with a descriptive message** — that text is shown to the
  user verbatim.
- **`--version` / `-v` only work after `config.SetApplicationVersion("…")`.**
- **`PropagateExceptions()` disables `SetExceptionHandler`** — pick one path.
- **`ValidateExamples()` is dev-time only** — wrap in `#if DEBUG` so it
  doesn't slow startup in production.
- **Windows legacy console (`cmd.exe` without VT)** has limited color & no
  Unicode box-drawing. Spectre auto-falls-back to `SafeBorder` ASCII for
  table/box borders.
- **`Markup.Escape` only escapes brackets, not formatting** — markup tags in
  user input become literal text after escape, which is normally what you
  want.

---

## See also (canonical sources)

- Repo: https://github.com/spectreconsole/spectre.console
- Docs site source: https://github.com/spectreconsole/website (under
  `Spectre.Docs/Content/{cli,console}`)
- Live docs: https://spectreconsole.net
- Examples / cookbook: https://github.com/spectreconsole/spectre.console/tree/main/examples
