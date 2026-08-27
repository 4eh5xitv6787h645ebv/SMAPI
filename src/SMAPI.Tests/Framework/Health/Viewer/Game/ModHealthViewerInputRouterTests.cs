using System;
using FluentAssertions;
using Microsoft.Xna.Framework.Input;
using NUnit.Framework;
using StardewModdingAPI.Framework.Health.Viewer;
using StardewModdingAPI.Framework.Health.Viewer.Game;
using StardewModdingAPI.Framework.Health.Viewer.Layout;

namespace SMAPI.Tests.Framework.Health.Viewer.Game;

[TestFixture]
internal sealed class ModHealthViewerInputRouterTests
{
    [TestCase(Keys.Up, ModHealthViewerInputKind.Navigate, ModHealthViewerNavigationCommand.PreviousRow)]
    [TestCase(Keys.Down, ModHealthViewerInputKind.Navigate, ModHealthViewerNavigationCommand.NextRow)]
    [TestCase(Keys.Left, ModHealthViewerInputKind.Navigate, ModHealthViewerNavigationCommand.PreviousSection)]
    [TestCase(Keys.Right, ModHealthViewerInputKind.Navigate, ModHealthViewerNavigationCommand.NextSection)]
    [TestCase(Keys.PageUp, ModHealthViewerInputKind.Navigate, ModHealthViewerNavigationCommand.PageUp)]
    [TestCase(Keys.PageDown, ModHealthViewerInputKind.Navigate, ModHealthViewerNavigationCommand.PageDown)]
    [TestCase(Keys.Home, ModHealthViewerInputKind.Navigate, ModHealthViewerNavigationCommand.FirstRow)]
    [TestCase(Keys.End, ModHealthViewerInputKind.Navigate, ModHealthViewerNavigationCommand.LastRow)]
    [TestCase(Keys.Enter, ModHealthViewerInputKind.Activate, default(ModHealthViewerNavigationCommand))]
    [TestCase(Keys.Tab, ModHealthViewerInputKind.CycleFocus, default(ModHealthViewerNavigationCommand))]
    [TestCase(Keys.I, ModHealthViewerInputKind.ExpandStatus, default(ModHealthViewerNavigationCommand))]
    [TestCase(Keys.P, ModHealthViewerInputKind.ExpandPrivacy, default(ModHealthViewerNavigationCommand))]
    [TestCase(Keys.Escape, ModHealthViewerInputKind.Close, default(ModHealthViewerNavigationCommand))]
    public void Keyboard_MapsRequiredNavigation(Keys key, ModHealthViewerInputKind kind, ModHealthViewerNavigationCommand command)
    {
        ModHealthViewerInputRouter.TryMapKey(key, out ModHealthViewerInput input).Should().BeTrue();
        input.Kind.Should().Be(kind);
        input.Navigation.Should().Be(command);
    }

    [TestCase(Buttons.A, ModHealthViewerInputKind.Activate)]
    [TestCase(Buttons.Y, ModHealthViewerInputKind.ExpandStatus)]
    [TestCase(Buttons.X, ModHealthViewerInputKind.ExpandPrivacy)]
    [TestCase(Buttons.B, ModHealthViewerInputKind.Close)]
    [TestCase(Buttons.LeftShoulder, ModHealthViewerInputKind.Navigate)]
    [TestCase(Buttons.RightShoulder, ModHealthViewerInputKind.Navigate)]
    [TestCase(Buttons.DPadUp, ModHealthViewerInputKind.MoveFocus)]
    [TestCase(Buttons.DPadDown, ModHealthViewerInputKind.MoveFocus)]
    [TestCase(Buttons.DPadLeft, ModHealthViewerInputKind.MoveFocus)]
    [TestCase(Buttons.DPadRight, ModHealthViewerInputKind.MoveFocus)]
    public void Controller_MapsRequiredNavigation(Buttons button, ModHealthViewerInputKind kind)
    {
        ModHealthViewerInputRouter.TryMapButton(button, out ModHealthViewerInput input).Should().BeTrue();
        input.Kind.Should().Be(kind);
    }

    [Test]
    public void Wheel_MapsBothDirections()
    {
        ModHealthViewerInputRouter.MapWheel(120).Navigation.Should().Be(ModHealthViewerNavigationCommand.PreviousRow);
        ModHealthViewerInputRouter.MapWheel(-120).Navigation.Should().Be(ModHealthViewerNavigationCommand.NextRow);
    }

    [Test]
    public void UnrelatedInput_IsNotClaimed()
    {
        ModHealthViewerInputRouter.TryMapKey(Keys.F12, out _).Should().BeFalse();
        ModHealthViewerInputRouter.TryMapButton(Buttons.RightStick, out _).Should().BeFalse();
    }

    [Test]
    public void ZeroRowSections_NeverExposeASelectableRowZero()
    {
        ModHealthViewerInputRouter.CanActivateRow(0, 0).Should().BeFalse();
        ModHealthViewerInputRouter.CanActivateRow(-1, 0).Should().BeFalse();
        ModHealthViewerInputRouter.GetRowFocus(3, 0, 0)
            .Should().Be(new ModHealthViewerFocusTarget(ModHealthViewerTargetKind.Section, 3));
    }

    [Test]
    public void CloseInput_ClosesDetailsBeforeViewer()
    {
        ModHealthViewerInputRouter.ResolveClose(showingDetails: true).Should().Be(ModHealthViewerCloseBehavior.CloseDetails);
        ModHealthViewerInputRouter.ResolveClose(showingDetails: false).Should().Be(ModHealthViewerCloseBehavior.CloseViewer);
        ModHealthViewerInputRouter.ResolveClose(showingDetails: true, showingExpanded: true).Should().Be(ModHealthViewerCloseBehavior.CloseExpanded);
    }

    [Test]
    public void ContentMode_LeavesOnExactRequestStateOrContentChange()
    {
        Guid requestId = Guid.NewGuid();
        object content = new();

        ModHealthViewerInputRouter.ShouldLeaveContentMode(requestId, Guid.NewGuid(), 1, 1, content, content).Should().BeTrue();
        ModHealthViewerInputRouter.ShouldLeaveContentMode(requestId, requestId, 1, 1, content, null).Should().BeTrue();
        ModHealthViewerInputRouter.ShouldLeaveContentMode(requestId, requestId, 1, 2, content, content).Should().BeTrue("Preparing to Saved on one exact request invalidates copied status text");
        ModHealthViewerInputRouter.ShouldLeaveContentMode(requestId, requestId, 2, 2, content, new object()).Should().BeTrue("a same-request replacement invalidates the selected semantic row");
        ModHealthViewerInputRouter.ShouldLeaveContentMode(requestId, requestId, 2, 2, content, content).Should().BeFalse();
    }
}
