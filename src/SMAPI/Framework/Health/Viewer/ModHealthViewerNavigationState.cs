using System;

namespace StardewModdingAPI.Framework.Health.Viewer;

/// <summary>A navigation operation for the mod health viewer.</summary>
internal enum ModHealthViewerNavigationCommand
{
    PreviousRow,
    NextRow,
    PageUp,
    PageDown,
    FirstRow,
    LastRow,
    PreviousSection,
    NextSection
}

/// <summary>Mutable, game-thread-only navigation state with deterministic scroll clamping.</summary>
internal sealed class ModHealthViewerNavigationState
{
    /// <summary>The selected section index.</summary>
    public int SectionIndex { get; private set; }

    /// <summary>The selected row index within the current section.</summary>
    public int RowIndex { get; private set; }

    /// <summary>The first visible row index within the current section.</summary>
    public int FirstVisibleRow { get; private set; }

    /// <summary>The number of rows which fit in the current layout.</summary>
    public int VisibleRowCount { get; private set; } = 1;

    /// <summary>Set the number of visible rows and clamp the current selection and scroll position.</summary>
    public void SetVisibleRowCount(int visibleRowCount, int rowCount)
    {
        this.VisibleRowCount = Math.Max(1, visibleRowCount);
        this.ClampRows(rowCount);
    }

    /// <summary>Select a section explicitly.</summary>
    public void SelectSection(int sectionIndex, int sectionCount, int rowCount)
    {
        this.SectionIndex = Math.Clamp(sectionIndex, 0, Math.Max(0, sectionCount - 1));
        this.RowIndex = 0;
        this.FirstVisibleRow = 0;
        this.ClampRows(rowCount);
    }

    /// <summary>Apply one navigation command.</summary>
    /// <returns>Whether the selected section or row changed.</returns>
    public bool Apply(ModHealthViewerNavigationCommand command, int sectionCount, int rowCount)
    {
        int oldSection = this.SectionIndex;
        int oldRow = this.RowIndex;
        switch (command)
        {
            case ModHealthViewerNavigationCommand.PreviousRow:
                this.RowIndex--;
                break;

            case ModHealthViewerNavigationCommand.NextRow:
                this.RowIndex++;
                break;

            case ModHealthViewerNavigationCommand.PageUp:
                this.RowIndex -= this.VisibleRowCount;
                break;

            case ModHealthViewerNavigationCommand.PageDown:
                this.RowIndex += this.VisibleRowCount;
                break;

            case ModHealthViewerNavigationCommand.FirstRow:
                this.RowIndex = 0;
                break;

            case ModHealthViewerNavigationCommand.LastRow:
                this.RowIndex = Math.Max(0, rowCount - 1);
                break;

            case ModHealthViewerNavigationCommand.PreviousSection:
                this.SectionIndex = Mod(this.SectionIndex - 1, Math.Max(1, sectionCount));
                this.RowIndex = 0;
                this.FirstVisibleRow = 0;
                break;

            case ModHealthViewerNavigationCommand.NextSection:
                this.SectionIndex = Mod(this.SectionIndex + 1, Math.Max(1, sectionCount));
                this.RowIndex = 0;
                this.FirstVisibleRow = 0;
                break;
        }

        this.ClampSections(sectionCount);
        this.ClampRows(rowCount);
        return oldSection != this.SectionIndex || oldRow != this.RowIndex;
    }

    /// <summary>Move the selection to a row clicked in the current visible page.</summary>
    public void SelectVisibleRow(int visibleRowIndex, int rowCount)
    {
        this.RowIndex = this.FirstVisibleRow + Math.Max(0, visibleRowIndex);
        this.ClampRows(rowCount);
    }

    private void ClampSections(int sectionCount)
    {
        this.SectionIndex = Math.Clamp(this.SectionIndex, 0, Math.Max(0, sectionCount - 1));
    }

    private void ClampRows(int rowCount)
    {
        int maximumRow = Math.Max(0, rowCount - 1);
        this.RowIndex = Math.Clamp(this.RowIndex, 0, maximumRow);
        int maximumFirst = Math.Max(0, rowCount - this.VisibleRowCount);
        if (this.RowIndex < this.FirstVisibleRow)
            this.FirstVisibleRow = this.RowIndex;
        else if (this.RowIndex >= this.FirstVisibleRow + this.VisibleRowCount)
            this.FirstVisibleRow = this.RowIndex - this.VisibleRowCount + 1;
        this.FirstVisibleRow = Math.Clamp(this.FirstVisibleRow, 0, maximumFirst);
    }

    private static int Mod(int value, int divisor)
    {
        int remainder = value % divisor;
        return remainder < 0 ? remainder + divisor : remainder;
    }
}
