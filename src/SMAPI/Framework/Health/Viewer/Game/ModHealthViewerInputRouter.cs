using System;
using Microsoft.Xna.Framework.Input;

namespace StardewModdingAPI.Framework.Health.Viewer.Game;

internal enum ModHealthViewerInputKind
{
    Navigate,
    MoveFocus,
    CycleFocus,
    Activate,
    ExpandStatus,
    ExpandPrivacy,
    Close
}

internal enum ModHealthViewerCloseBehavior
{
    CloseExpanded,
    CloseDetails,
    CloseViewer
}

internal readonly record struct ModHealthViewerInput(
    ModHealthViewerInputKind Kind,
    ModHealthViewerNavigationCommand Navigation = default,
    Layout.ModHealthViewerFocusDirection FocusDirection = default
);

/// <summary>Pure keyboard/controller/wheel mapping used by the thin game menu.</summary>
internal static class ModHealthViewerInputRouter
{
    /// <summary>Whether an open detail/expanded view is no longer tied to displayable content for its exact request.</summary>
    public static bool ShouldLeaveContentMode(
        Guid displayedRequestId,
        Guid currentRequestId,
        long displayedProjectionRevision,
        long currentProjectionRevision,
        object? displayedContent,
        object? currentContent
    )
    {
        return displayedRequestId != currentRequestId
            || displayedProjectionRevision != currentProjectionRevision
            || !ReferenceEquals(displayedContent, currentContent);
    }

    public static ModHealthViewerCloseBehavior ResolveClose(bool showingDetails, bool showingExpanded = false)
    {
        return showingExpanded
            ? ModHealthViewerCloseBehavior.CloseExpanded
            : showingDetails ? ModHealthViewerCloseBehavior.CloseDetails : ModHealthViewerCloseBehavior.CloseViewer;
    }

    public static bool CanActivateRow(int rowIndex, int rowCount)
    {
        return (uint)rowIndex < (uint)rowCount;
    }

    public static Layout.ModHealthViewerFocusTarget GetRowFocus(int sectionIndex, int rowIndex, int rowCount)
    {
        return rowCount > 0
            ? new(Layout.ModHealthViewerTargetKind.Row, rowIndex)
            : new(Layout.ModHealthViewerTargetKind.Section, sectionIndex);
    }

    public static bool TryMapKey(Keys key, out ModHealthViewerInput input)
    {
        input = key switch
        {
            Keys.Escape => new(ModHealthViewerInputKind.Close),
            Keys.Up => Navigate(ModHealthViewerNavigationCommand.PreviousRow),
            Keys.Down => Navigate(ModHealthViewerNavigationCommand.NextRow),
            Keys.Left => Navigate(ModHealthViewerNavigationCommand.PreviousSection),
            Keys.Right => Navigate(ModHealthViewerNavigationCommand.NextSection),
            Keys.PageUp => Navigate(ModHealthViewerNavigationCommand.PageUp),
            Keys.PageDown => Navigate(ModHealthViewerNavigationCommand.PageDown),
            Keys.Home => Navigate(ModHealthViewerNavigationCommand.FirstRow),
            Keys.End => Navigate(ModHealthViewerNavigationCommand.LastRow),
            Keys.Tab => new(ModHealthViewerInputKind.CycleFocus),
            Keys.I => new(ModHealthViewerInputKind.ExpandStatus),
            Keys.P => new(ModHealthViewerInputKind.ExpandPrivacy),
            Keys.Enter or Keys.Space => new(ModHealthViewerInputKind.Activate),
            _ => default
        };
        return key is Keys.Escape or Keys.Up or Keys.Down or Keys.Left or Keys.Right
            or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End or Keys.Tab or Keys.I or Keys.P or Keys.Enter or Keys.Space;
    }

    public static bool TryMapButton(Buttons button, out ModHealthViewerInput input)
    {
        input = button switch
        {
            Buttons.B or Buttons.Back => new(ModHealthViewerInputKind.Close),
            Buttons.A => new(ModHealthViewerInputKind.Activate),
            Buttons.Y => new(ModHealthViewerInputKind.ExpandStatus),
            Buttons.X => new(ModHealthViewerInputKind.ExpandPrivacy),
            Buttons.DPadUp => Move(Layout.ModHealthViewerFocusDirection.Up),
            Buttons.DPadDown => Move(Layout.ModHealthViewerFocusDirection.Down),
            Buttons.DPadLeft => Move(Layout.ModHealthViewerFocusDirection.Left),
            Buttons.DPadRight => Move(Layout.ModHealthViewerFocusDirection.Right),
            Buttons.LeftShoulder => Navigate(ModHealthViewerNavigationCommand.PreviousSection),
            Buttons.RightShoulder => Navigate(ModHealthViewerNavigationCommand.NextSection),
            _ => default
        };
        return button is Buttons.B or Buttons.Back or Buttons.A or Buttons.Y or Buttons.X or Buttons.DPadUp or Buttons.DPadDown
            or Buttons.DPadLeft or Buttons.DPadRight or Buttons.LeftShoulder or Buttons.RightShoulder;
    }

    public static ModHealthViewerInput MapWheel(int direction)
    {
        return Navigate(direction > 0 ? ModHealthViewerNavigationCommand.PreviousRow : ModHealthViewerNavigationCommand.NextRow);
    }

    private static ModHealthViewerInput Navigate(ModHealthViewerNavigationCommand command) => new(ModHealthViewerInputKind.Navigate, Navigation: command);

    private static ModHealthViewerInput Move(Layout.ModHealthViewerFocusDirection direction) => new(ModHealthViewerInputKind.MoveFocus, FocusDirection: direction);
}
