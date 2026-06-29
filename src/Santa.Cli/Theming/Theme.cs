namespace Santa.Cli.Theming;

/// <summary>
/// Pure Spectre.Console palette. Each slot is a markup payload (no surrounding [ ]),
/// e.g. "#fbbf24 bold" — used as <c>[{theme.Title}]…[/]</c>. All values are truecolor;
/// theme changes are live (next render reads from whichever palette is active).
/// </summary>
public sealed record Theme(
    string Name,
    string DisplayName,
    string Vibe,
    string Index,        // "[1]" prefix
    string Title,        // session title
    string Path,         // cwd
    string Branch,       // git branch
    string Ok,           // ✓ status
    string Warn,         // amber/orange
    string Err,          // soft red
    string Highlight,    // FTS snippet match
    string Dim,           // timestamps, scores
    string Frame,         // panel borders
    string SelectionFg,   // selected list-row foreground
    string SelectionBg,   // selected list-row background
    string TabActiveFg,   // active tab text
    string TabActiveBg,   // active tab background
    // Duration ramp — gentle escalation by unit. Minutes are quiet, weeks stand out.
    string DurMinute,
    string DurHour,
    string DurDay,
    string DurWeek)
{
    public static readonly Theme SlateAmber = new(
        Name: "slate-amber",
        DisplayName: "Slate & Amber",
        Vibe: "Warm professional. Amber + teal + mauve on dark slate.",
        Index:        "#fbbf24 bold",
        Title:        "#fcd34d bold",
        Path:         "#5eead4",
        Branch:       "#c4b5fd",
        Ok:           "#10b981",
        Warn:         "#f59e0b",
        Err:          "#f87171",
        Highlight:    "#fde047 bold",
        Dim:          "#737373",
        Frame:        "#475569",
        SelectionFg:  "#fef3c7",
        SelectionBg:  "#1f2937",
        TabActiveFg:  "#0c0a09 bold",
        TabActiveBg:  "#fbbf24",
        DurMinute:    "#737373",   // dim slate
        DurHour:      "#94a3b8",   // light slate
        DurDay:       "#d97706",   // mid amber
        DurWeek:      "#fbbf24 bold"   // saturated amber
    );

    public static readonly Theme Tokyo = new(
        Name: "tokyo",
        DisplayName: "Tokyo",
        Vibe: "Cool and restrained. Steel blue + sage + dusk violet.",
        Index:        "#7aa2f7 bold",
        Title:        "#bb9af7 bold",
        Path:         "#7dcfff",
        Branch:       "#9ece6a",
        Ok:           "#9ece6a",
        Warn:         "#e0af68",
        Err:          "#f7768e",
        Highlight:    "#e0af68 bold",
        Dim:          "#565f89",
        Frame:        "#3b4261",
        SelectionFg:  "#c0caf5",
        SelectionBg:  "#283457",
        TabActiveFg:  "#1a1b26 bold",
        TabActiveBg:  "#7aa2f7",
        DurMinute:    "#565f89",   // dim navy
        DurHour:      "#7d8eb6",   // mid blue-grey
        DurDay:       "#d4ad65",   // muted mustard
        DurWeek:      "#e0af68 bold"  // mustard accent
    );

    public static readonly Theme MonoTeal = new(
        Name: "mono-teal",
        DisplayName: "Mono Accent — Teal",
        Vibe: "Minimalist. Greyscale text, teal index, emerald OK.",
        Index:        "#14b8a6 bold",
        Title:        "default bold",
        Path:         "#a3a3a3",
        Branch:       "#737373",
        Ok:           "#10b981",
        Warn:         "default bold",
        Err:          "#ef4444",
        Highlight:    "default invert",
        Dim:          "#525252",
        Frame:        "#404040",
        SelectionFg:  "default bold",
        SelectionBg:  "#1c1917",
        TabActiveFg:  "#0c0a09 bold",
        TabActiveBg:  "#14b8a6",
        DurMinute:    "#525252",   // charcoal
        DurHour:      "#a3a3a3",   // mid grey
        DurDay:       "#5eead4",   // light teal
        DurWeek:      "#14b8a6 bold"  // brand teal
    );

    public static readonly IReadOnlyList<Theme> All = new[] { SlateAmber, Tokyo, MonoTeal };

    public static Theme ByName(string name) =>
        All.FirstOrDefault(t => t.Name == name) ?? SlateAmber;
}
