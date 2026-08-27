using System;

namespace StardewModdingAPI.Framework.Health.Viewer.Layout;

/// <summary>
/// A reusable, game-independent layout cache for the mod health viewer.
/// Recomputing and navigating the populated cache don't allocate after construction.
/// </summary>
internal sealed class ModHealthViewerLayout
{
    public const int SectionCount = (int)ModHealthViewerSection.Context + 1;
    public const int MaximumVisibleRows = 50;
    public const int MaximumActions = 6;
    public const int BaseMinimumHitTarget = 44;

    private const int MaximumMenuWidth = 1440;
    private const int MaximumMenuHeight = 900;
    private const int MinimumWideWidth = 920;
    private const int StandardRowHeight = 48;
    private const int Gap = 8;
    private const int SmallGap = 4;

    private readonly ModHealthLayoutRectangle[] SectionBounds = new ModHealthLayoutRectangle[SectionCount];
    private readonly ModHealthLayoutRectangle[] RowBounds = new ModHealthLayoutRectangle[MaximumVisibleRows];
    private readonly ModHealthLayoutRectangle[] ActionBounds = new ModHealthLayoutRectangle[MaximumActions];

    /// <summary>The responsive mode chosen by the last recomputation.</summary>
    public ModHealthViewerLayoutMode Mode { get; private set; }

    /// <summary>The normalized UI scale used by the last recomputation.</summary>
    public float UiScale { get; private set; } = 1;

    /// <summary>The minimum target size in UI coordinates needed to retain a 44-pixel target at the current scale.</summary>
    public int MinimumHitTarget { get; private set; } = BaseMinimumHitTarget;

    /// <summary>Whether the current viewport can present all interactive targets at their minimum size.</summary>
    public bool MeetsMinimumHitTarget { get; private set; }

    public ModHealthLayoutRectangle MenuBounds { get; private set; }
    public ModHealthLayoutRectangle HeaderBounds { get; private set; }
    public ModHealthLayoutRectangle PrivacyNoticeBounds { get; private set; }
    public ModHealthLayoutRectangle NavigationBounds { get; private set; }
    public ModHealthLayoutRectangle ContentBounds { get; private set; }
    public ModHealthLayoutRectangle ContentHeaderBounds { get; private set; }
    public ModHealthLayoutRectangle RowViewportBounds { get; private set; }
    public ModHealthLayoutRectangle FooterBounds { get; private set; }
    public ModHealthLayoutRectangle CloseBounds { get; private set; }
    public ModHealthLayoutRectangle ScrollTrackBounds { get; private set; }
    public ModHealthLayoutRectangle ScrollThumbBounds { get; private set; }

    /// <summary>The clamped absolute index of the first row represented by the visible row pool.</summary>
    public int FirstVisibleRow { get; private set; }

    /// <summary>The current fixed-pool row count. It is always at most <see cref="MaximumVisibleRows"/>.</summary>
    public int VisibleRowCount { get; private set; }

    /// <summary>The number of rows which fit in this layout, including empty slots at the end of a section.</summary>
    public int VisibleRowCapacity { get; private set; }

    /// <summary>The number of populated action targets.</summary>
    public int ActionCount { get; private set; }

    /// <summary>Recompute all rectangles into arrays allocated by the constructor.</summary>
    public void Recompute(ModHealthViewerLayoutInput input)
    {
        int viewportWidth = Math.Max(1, input.ViewportWidth);
        int viewportHeight = Math.Max(1, input.ViewportHeight);
        float scale = float.IsFinite(input.UiScale) && input.UiScale > 0
            ? input.UiScale
            : 1;
        scale = Math.Clamp(scale, 0.5f, 2f);
        this.UiScale = scale;
        this.MinimumHitTarget = Math.Max(BaseMinimumHitTarget, (int)Math.Ceiling(BaseMinimumHitTarget / scale));

        int outerMargin = viewportWidth >= 1000 && viewportHeight >= 600 ? 24 : viewportWidth >= 640 ? 8 : 4;
        int menuWidth = Math.Min(MaximumMenuWidth, Math.Max(1, viewportWidth - outerMargin * 2));
        int menuHeight = Math.Min(MaximumMenuHeight, Math.Max(1, viewportHeight - outerMargin * 2));
        int menuX = (viewportWidth - menuWidth) / 2;
        int menuY = (viewportHeight - menuHeight) / 2;
        this.MenuBounds = Rect(menuX, menuY, menuWidth, menuHeight);

        int padding = menuWidth >= 900 && menuHeight >= 500 ? 16 : 12;
        int innerX = this.MenuBounds.X + padding;
        int innerY = this.MenuBounds.Y + padding;
        int innerWidth = Math.Max(0, this.MenuBounds.Width - padding * 2);
        int innerHeight = Math.Max(0, this.MenuBounds.Height - padding * 2);
        int target = this.MinimumHitTarget;

        int headerHeight = Math.Min(innerHeight, Math.Max(target, menuHeight >= 600 ? 56 : target));
        this.HeaderBounds = Rect(innerX, innerY, innerWidth, headerHeight);
        this.CloseBounds = Rect(Math.Max(innerX, innerX + innerWidth - target), innerY, Math.Min(target, innerWidth), Math.Min(target, headerHeight));

        int afterHeaderY = this.HeaderBounds.Bottom + Gap;
        int remainingAfterHeader = Math.Max(0, innerY + innerHeight - afterHeaderY);
        int privacyHeight = Math.Min(remainingAfterHeader, Math.Max(target, menuHeight >= 600 ? 64 : target));
        this.PrivacyNoticeBounds = Rect(innerX, afterHeaderY, innerWidth, privacyHeight);

        this.ActionCount = Math.Clamp(input.ActionCount, 0, MaximumActions);
        int footerHeight = this.ActionCount > 0 ? Math.Min(target, innerHeight) : 0;
        int footerY = innerY + innerHeight - footerHeight;
        this.FooterBounds = Rect(innerX, footerY, innerWidth, footerHeight);
        this.PopulateActions(Math.Max(target, input.PreferredActionWidth));

        int bodyY = this.PrivacyNoticeBounds.Bottom + Gap;
        int bodyBottom = footerHeight > 0 ? this.FooterBounds.Y - Gap : innerY + innerHeight;
        int bodyHeight = Math.Max(0, bodyBottom - bodyY);
        bool wide = innerWidth >= MinimumWideWidth && bodyHeight >= SectionCount * target + (SectionCount - 1) * SmallGap;
        this.Mode = wide ? ModHealthViewerLayoutMode.WideSidebar : ModHealthViewerLayoutMode.CompactTabs;

        if (wide)
            this.PopulateWideBody(innerX, bodyY, innerWidth, bodyHeight, input.PreferredNavigationWidth);
        else
            this.PopulateCompactBody(innerX, bodyY, innerWidth, bodyHeight);

        this.PopulateRows(input.RowCount, input.FirstVisibleRow);
        this.MeetsMinimumHitTarget = this.CheckMinimumHitTargets();
    }

    /// <summary>Get the bounds for one of the eight fixed sections.</summary>
    public ModHealthLayoutRectangle GetSectionBounds(int sectionIndex)
    {
        if ((uint)sectionIndex >= SectionCount)
            throw new ArgumentOutOfRangeException(nameof(sectionIndex));
        return this.SectionBounds[sectionIndex];
    }

    /// <summary>Get a visible row's bounds by its zero-based pool slot.</summary>
    public ModHealthLayoutRectangle GetVisibleRowBounds(int visibleRowIndex)
    {
        if ((uint)visibleRowIndex >= this.VisibleRowCount)
            throw new ArgumentOutOfRangeException(nameof(visibleRowIndex));
        return this.RowBounds[visibleRowIndex];
    }

    /// <summary>Get the bounds for a zero-based visible action slot.</summary>
    public ModHealthLayoutRectangle GetActionBounds(int actionIndex)
    {
        if ((uint)actionIndex >= this.ActionCount)
            throw new ArgumentOutOfRangeException(nameof(actionIndex));
        return this.ActionBounds[actionIndex];
    }

    /// <summary>Resolve a mouse-coordinate hit to a semantic target without allocating.</summary>
    public ModHealthViewerFocusTarget HitTest(int x, int y)
    {
        if (this.CloseBounds.Contains(x, y))
            return new(ModHealthViewerTargetKind.Close);

        for (int i = 0; i < this.ActionCount; i++)
        {
            if (this.ActionBounds[i].Contains(x, y))
                return new(ModHealthViewerTargetKind.Action, i);
        }
        for (int i = 0; i < this.VisibleRowCount; i++)
        {
            if (this.RowBounds[i].Contains(x, y))
                return new(ModHealthViewerTargetKind.Row, this.FirstVisibleRow + i);
        }
        for (int i = 0; i < SectionCount; i++)
        {
            if (this.SectionBounds[i].Contains(x, y))
                return new(ModHealthViewerTargetKind.Section, i);
        }
        return ModHealthViewerFocusTarget.None;
    }

    /// <summary>Cycle focus in the stable Tab order: sections, visible rows, actions, then Close.</summary>
    public bool TryCycleFocus(ModHealthViewerFocusTarget current, bool backwards, out ModHealthViewerFocusTarget next)
    {
        int count = SectionCount + this.VisibleRowCount + this.ActionCount + 1;
        int ordinal = this.GetFocusOrdinal(current);
        if (ordinal < 0)
            ordinal = backwards ? 0 : -1;
        ordinal = Mod(ordinal + (backwards ? -1 : 1), count);
        next = this.GetFocusTarget(ordinal);
        return true;
    }

    /// <summary>Find the nearest focus target in a cardinal direction.</summary>
    public bool TryMoveFocus(ModHealthViewerFocusTarget current, ModHealthViewerFocusDirection direction, out ModHealthViewerFocusTarget next)
    {
        if (!this.TryGetBounds(current, out ModHealthLayoutRectangle origin))
        {
            next = new(ModHealthViewerTargetKind.Section, 0);
            return true;
        }

        int originX = origin.X + origin.Width / 2;
        int originY = origin.Y + origin.Height / 2;
        long bestScore = long.MaxValue;
        ModHealthViewerFocusTarget best = ModHealthViewerFocusTarget.None;
        int count = SectionCount + this.VisibleRowCount + this.ActionCount + 1;
        for (int i = 0; i < count; i++)
        {
            ModHealthViewerFocusTarget candidate = this.GetFocusTarget(i);
            if (candidate == current || !this.TryGetBounds(candidate, out ModHealthLayoutRectangle bounds))
                continue;

            int deltaX = bounds.X + bounds.Width / 2 - originX;
            int deltaY = bounds.Y + bounds.Height / 2 - originY;
            int primary;
            int secondary;
            switch (direction)
            {
                case ModHealthViewerFocusDirection.Up when deltaY < 0:
                    primary = -deltaY;
                    secondary = Math.Abs(deltaX);
                    break;
                case ModHealthViewerFocusDirection.Down when deltaY > 0:
                    primary = deltaY;
                    secondary = Math.Abs(deltaX);
                    break;
                case ModHealthViewerFocusDirection.Left when deltaX < 0:
                    primary = -deltaX;
                    secondary = Math.Abs(deltaY);
                    break;
                case ModHealthViewerFocusDirection.Right when deltaX > 0:
                    primary = deltaX;
                    secondary = Math.Abs(deltaY);
                    break;
                default:
                    continue;
            }

            long score = (long)primary * primary + (long)secondary * secondary * 4;
            if (score < bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        next = best;
        return best.Kind != ModHealthViewerTargetKind.None;
    }

    /// <summary>Try to get the current bounds of a stable semantic target.</summary>
    public bool TryGetBounds(ModHealthViewerFocusTarget target, out ModHealthLayoutRectangle bounds)
    {
        switch (target.Kind)
        {
            case ModHealthViewerTargetKind.Section when (uint)target.Index < SectionCount:
                bounds = this.SectionBounds[target.Index];
                return bounds.Width > 0 && bounds.Height > 0;
            case ModHealthViewerTargetKind.Row when target.Index >= this.FirstVisibleRow && target.Index < this.FirstVisibleRow + this.VisibleRowCount:
                bounds = this.RowBounds[target.Index - this.FirstVisibleRow];
                return true;
            case ModHealthViewerTargetKind.Action when (uint)target.Index < this.ActionCount:
                bounds = this.ActionBounds[target.Index];
                return true;
            case ModHealthViewerTargetKind.Close:
                bounds = this.CloseBounds;
                return bounds.Width > 0 && bounds.Height > 0;
            default:
                bounds = default;
                return false;
        }
    }

    private void PopulateWideBody(int x, int y, int width, int height, int preferredNavigationWidth)
    {
        int navigationWidth = Math.Clamp(Math.Max(this.MinimumHitTarget * 3, preferredNavigationWidth), this.MinimumHitTarget * 3, Math.Min(360, Math.Max(this.MinimumHitTarget * 3, width / 3)));
        this.NavigationBounds = Rect(x, y, navigationWidth, height);
        int tabHeight = Math.Min(56, Math.Max(this.MinimumHitTarget, (height - SmallGap * (SectionCount - 1)) / SectionCount));
        for (int i = 0; i < SectionCount; i++)
            this.SectionBounds[i] = Rect(x, y + i * (tabHeight + SmallGap), navigationWidth, tabHeight);

        int contentX = this.NavigationBounds.Right + Gap;
        this.ContentBounds = Rect(contentX, y, Math.Max(0, x + width - contentX), height);
        this.PopulateContentRegions();
    }

    private void PopulateCompactBody(int x, int y, int width, int height)
    {
        int tabGap = width >= SectionCount * this.MinimumHitTarget + (SectionCount - 1) * SmallGap ? SmallGap : 0;
        int tabsWidth = Math.Max(0, width - tabGap * (SectionCount - 1));
        int tabWidth = tabsWidth / SectionCount;
        int navigationHeight = Math.Min(height, this.MinimumHitTarget);
        this.NavigationBounds = Rect(x, y, width, navigationHeight);
        int tabX = x;
        for (int i = 0; i < SectionCount; i++)
        {
            int nextX = i == SectionCount - 1 ? x + width : tabX + tabWidth;
            this.SectionBounds[i] = Rect(tabX, y, Math.Max(0, nextX - tabX), navigationHeight);
            tabX = nextX + tabGap;
        }

        int contentY = this.NavigationBounds.Bottom + Gap;
        this.ContentBounds = Rect(x, contentY, width, Math.Max(0, y + height - contentY));
        this.PopulateContentRegions();
    }

    private void PopulateContentRegions()
    {
        int headerHeight = Math.Min(this.ContentBounds.Height, Math.Max(32, Math.Min(this.MinimumHitTarget, this.ContentBounds.Height / 3)));
        this.ContentHeaderBounds = Rect(this.ContentBounds.X, this.ContentBounds.Y, this.ContentBounds.Width, headerHeight);
        int rowY = this.ContentHeaderBounds.Bottom + SmallGap;
        int scrollbarWidth = this.ContentBounds.Width >= 240 ? 20 : 0;
        this.RowViewportBounds = Rect(this.ContentBounds.X, rowY, Math.Max(0, this.ContentBounds.Width - scrollbarWidth - SmallGap), Math.Max(0, this.ContentBounds.Bottom - rowY));
        this.ScrollTrackBounds = Rect(this.RowViewportBounds.Right + SmallGap, rowY, scrollbarWidth, this.RowViewportBounds.Height);
    }

    private void PopulateRows(int requestedRowCount, int requestedFirstVisibleRow)
    {
        int rowCount = Math.Max(0, requestedRowCount);
        int rowHeight = Math.Max(StandardRowHeight, this.MinimumHitTarget);
        int rowStride = rowHeight + SmallGap;
        this.VisibleRowCapacity = Math.Min(MaximumVisibleRows, Math.Max(0, (this.RowViewportBounds.Height + SmallGap) / rowStride));
        int maximumFirst = Math.Max(0, rowCount - this.VisibleRowCapacity);
        this.FirstVisibleRow = Math.Clamp(requestedFirstVisibleRow, 0, maximumFirst);
        this.VisibleRowCount = Math.Min(this.VisibleRowCapacity, Math.Max(0, rowCount - this.FirstVisibleRow));

        for (int i = 0; i < this.VisibleRowCount; i++)
            this.RowBounds[i] = Rect(this.RowViewportBounds.X, this.RowViewportBounds.Y + i * rowStride, this.RowViewportBounds.Width, rowHeight);
        for (int i = this.VisibleRowCount; i < MaximumVisibleRows; i++)
            this.RowBounds[i] = default;

        if (rowCount <= this.VisibleRowCapacity || this.ScrollTrackBounds.Height <= 0)
        {
            this.ScrollThumbBounds = default;
            return;
        }

        int thumbHeight = Math.Max(this.MinimumHitTarget, (int)((long)this.ScrollTrackBounds.Height * this.VisibleRowCapacity / rowCount));
        thumbHeight = Math.Min(this.ScrollTrackBounds.Height, thumbHeight);
        int travel = this.ScrollTrackBounds.Height - thumbHeight;
        int thumbOffset = maximumFirst == 0 ? 0 : (int)((long)travel * this.FirstVisibleRow / maximumFirst);
        this.ScrollThumbBounds = Rect(this.ScrollTrackBounds.X, this.ScrollTrackBounds.Y + thumbOffset, this.ScrollTrackBounds.Width, thumbHeight);
    }

    private void PopulateActions(int preferredWidth)
    {
        for (int i = 0; i < MaximumActions; i++)
            this.ActionBounds[i] = default;
        if (this.ActionCount == 0 || this.FooterBounds.Width <= 0 || this.FooterBounds.Height <= 0)
            return;

        int gap = this.ActionCount == 1 ? 0 : Gap;
        int available = Math.Max(0, this.FooterBounds.Width - gap * (this.ActionCount - 1));
        int width = Math.Min(preferredWidth, available / this.ActionCount);
        int total = width * this.ActionCount + gap * (this.ActionCount - 1);
        int x = this.FooterBounds.X + Math.Max(0, (this.FooterBounds.Width - total) / 2);
        for (int i = 0; i < this.ActionCount; i++)
            this.ActionBounds[i] = Rect(x + i * (width + gap), this.FooterBounds.Y, width, this.FooterBounds.Height);
    }

    private bool CheckMinimumHitTargets()
    {
        if (this.CloseBounds.Width < this.MinimumHitTarget || this.CloseBounds.Height < this.MinimumHitTarget)
            return false;
        for (int i = 0; i < SectionCount; i++)
        {
            if (this.SectionBounds[i].Width < this.MinimumHitTarget || this.SectionBounds[i].Height < this.MinimumHitTarget)
                return false;
        }
        for (int i = 0; i < this.VisibleRowCount; i++)
        {
            if (this.RowBounds[i].Width < this.MinimumHitTarget || this.RowBounds[i].Height < this.MinimumHitTarget)
                return false;
        }
        for (int i = 0; i < this.ActionCount; i++)
        {
            if (this.ActionBounds[i].Width < this.MinimumHitTarget || this.ActionBounds[i].Height < this.MinimumHitTarget)
                return false;
        }
        return true;
    }

    private int GetFocusOrdinal(ModHealthViewerFocusTarget target)
    {
        return target.Kind switch
        {
            ModHealthViewerTargetKind.Section when (uint)target.Index < SectionCount => target.Index,
            ModHealthViewerTargetKind.Row when target.Index >= this.FirstVisibleRow && target.Index < this.FirstVisibleRow + this.VisibleRowCount => SectionCount + target.Index - this.FirstVisibleRow,
            ModHealthViewerTargetKind.Action when (uint)target.Index < this.ActionCount => SectionCount + this.VisibleRowCount + target.Index,
            ModHealthViewerTargetKind.Close => SectionCount + this.VisibleRowCount + this.ActionCount,
            _ => -1
        };
    }

    private ModHealthViewerFocusTarget GetFocusTarget(int ordinal)
    {
        if (ordinal < SectionCount)
            return new(ModHealthViewerTargetKind.Section, ordinal);
        ordinal -= SectionCount;
        if (ordinal < this.VisibleRowCount)
            return new(ModHealthViewerTargetKind.Row, this.FirstVisibleRow + ordinal);
        ordinal -= this.VisibleRowCount;
        if (ordinal < this.ActionCount)
            return new(ModHealthViewerTargetKind.Action, ordinal);
        return new(ModHealthViewerTargetKind.Close);
    }

    private static ModHealthLayoutRectangle Rect(int x, int y, int width, int height)
    {
        return new(x, y, Math.Max(0, width), Math.Max(0, height));
    }

    private static int Mod(int value, int divisor)
    {
        int remainder = value % divisor;
        return remainder < 0 ? remainder + divisor : remainder;
    }
}
