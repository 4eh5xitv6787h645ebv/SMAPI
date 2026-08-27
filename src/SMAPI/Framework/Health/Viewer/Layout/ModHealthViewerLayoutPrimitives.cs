using System;

namespace StardewModdingAPI.Framework.Health.Viewer.Layout;

/// <summary>The fixed, schema-v1 sections shown by the mod health viewer.</summary>
internal enum ModHealthViewerSection
{
    Overview,
    Findings,
    Capture,
    Attention,
    Performance,
    Errors,
    Inventory,
    Context
}

/// <summary>A rectangular area in the game's UI coordinate space.</summary>
internal readonly record struct ModHealthLayoutRectangle(int X, int Y, int Width, int Height)
{
    /// <summary>The coordinate immediately after the right edge.</summary>
    public int Right => this.X + this.Width;

    /// <summary>The coordinate immediately after the bottom edge.</summary>
    public int Bottom => this.Y + this.Height;

    /// <summary>Whether the rectangle contains a UI-coordinate point.</summary>
    public bool Contains(int x, int y)
    {
        return this.Width > 0
            && this.Height > 0
            && x >= this.X
            && x < this.Right
            && y >= this.Y
            && y < this.Bottom;
    }
}

/// <summary>The semantic kind of a mouse or focus target.</summary>
internal enum ModHealthViewerTargetKind
{
    None,
    Section,
    Row,
    Action,
    Close
}

/// <summary>A stable semantic target. Row indexes are absolute indexes in the current section.</summary>
internal readonly record struct ModHealthViewerFocusTarget(ModHealthViewerTargetKind Kind, int Index = 0)
{
    public static ModHealthViewerFocusTarget None => new(ModHealthViewerTargetKind.None);
}

/// <summary>A cardinal focus movement used by keyboard and controller navigation.</summary>
internal enum ModHealthViewerFocusDirection
{
    Up,
    Down,
    Left,
    Right
}

/// <summary>The responsive arrangement selected for the available UI viewport.</summary>
internal enum ModHealthViewerLayoutMode
{
    WideSidebar,
    CompactTabs
}

/// <summary>Inputs for one deterministic layout recomputation.</summary>
/// <param name="ViewportWidth">The width of the current screen's UI viewport in UI coordinates.</param>
/// <param name="ViewportHeight">The height of the current screen's UI viewport in UI coordinates.</param>
/// <param name="UiScale">The current game UI scale. Values below one enlarge targets to retain their physical hit area.</param>
/// <param name="RowCount">The total number of rows in the selected section.</param>
/// <param name="FirstVisibleRow">The requested absolute index of the first visible row.</param>
/// <param name="ActionCount">The number of currently visible footer actions.</param>
/// <param name="PreferredNavigationWidth">The measured preferred width for translated section chrome.</param>
/// <param name="PreferredActionWidth">The measured preferred width of each translated footer action.</param>
internal readonly record struct ModHealthViewerLayoutInput(
    int ViewportWidth,
    int ViewportHeight,
    float UiScale,
    int RowCount,
    int FirstVisibleRow,
    int ActionCount,
    int PreferredNavigationWidth = 220,
    int PreferredActionWidth = 176
);
