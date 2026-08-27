using System;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.Health.Viewer.Layout;

namespace SMAPI.Tests.Framework.Health.Viewer.Layout;

[TestFixture]
internal sealed class ModHealthViewerLayoutTests
{
    [Test]
    public void Recompute_UsesWideEightSectionLayoutAt1280By720()
    {
        ModHealthViewerLayout layout = new();

        layout.Recompute(new(1280, 720, 1, 308, 0, 4, PreferredNavigationWidth: 292, PreferredActionWidth: 180));

        layout.Mode.Should().Be(ModHealthViewerLayoutMode.WideSidebar);
        layout.MeetsMinimumHitTarget.Should().BeTrue();
        layout.PrivacyNoticeBounds.Width.Should().BeGreaterThan(0);
        layout.PrivacyNoticeBounds.Height.Should().BeGreaterThanOrEqualTo(ModHealthViewerLayout.BaseMinimumHitTarget);
        layout.VisibleRowCount.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(ModHealthViewerLayout.MaximumVisibleRows);
        layout.ActionCount.Should().Be(4);
        for (int i = 0; i < ModHealthViewerLayout.SectionCount; i++)
        {
            ModHealthLayoutRectangle bounds = layout.GetSectionBounds(i);
            bounds.Width.Should().BeGreaterThanOrEqualTo(layout.MinimumHitTarget);
            bounds.Height.Should().BeGreaterThanOrEqualTo(layout.MinimumHitTarget);
            layout.HitTest(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2)
                .Should().Be(new ModHealthViewerFocusTarget(ModHealthViewerTargetKind.Section, i));
        }
    }

    [Test]
    public void Recompute_RespondsToResizeScaleAndLongChrome()
    {
        ModHealthViewerLayout layout = new();
        layout.Recompute(new(1280, 720, 1, 20, 0, 3, PreferredNavigationWidth: 340));
        layout.Recompute(new(800, 600, 0.75f, 20, 0, 3, PreferredNavigationWidth: 900, PreferredActionWidth: 500));

        layout.Mode.Should().Be(ModHealthViewerLayoutMode.CompactTabs);
        layout.MinimumHitTarget.Should().Be(59);
        layout.NavigationBounds.Right.Should().BeLessThanOrEqualTo(layout.MenuBounds.Right);
        layout.MeetsMinimumHitTarget.Should().BeTrue();
        for (int i = 0; i < ModHealthViewerLayout.SectionCount; i++)
            layout.GetSectionBounds(i).Width.Should().BeGreaterThanOrEqualTo(layout.MinimumHitTarget);
        for (int i = 0; i < layout.ActionCount; i++)
        {
            layout.GetActionBounds(i).Width.Should().BeGreaterThanOrEqualTo(layout.MinimumHitTarget);
            layout.GetActionBounds(i).Height.Should().BeGreaterThanOrEqualTo(layout.MinimumHitTarget);
        }
    }

    [Test]
    public void Recompute_ClampsVirtualRowsAndUsesAbsoluteHitIndexes()
    {
        ModHealthViewerLayout layout = new();

        layout.Recompute(new(2560, 1440, 1, 10_000, 20_000, 99));

        layout.ActionCount.Should().Be(ModHealthViewerLayout.MaximumActions);
        layout.VisibleRowCapacity.Should().BeLessThanOrEqualTo(ModHealthViewerLayout.MaximumVisibleRows);
        layout.VisibleRowCount.Should().Be(layout.VisibleRowCapacity);
        layout.FirstVisibleRow.Should().Be(10_000 - layout.VisibleRowCapacity);
        ModHealthLayoutRectangle first = layout.GetVisibleRowBounds(0);
        layout.HitTest(first.X + 1, first.Y + 1)
            .Should().Be(new ModHealthViewerFocusTarget(ModHealthViewerTargetKind.Row, layout.FirstVisibleRow));
        layout.ScrollThumbBounds.Height.Should().BeGreaterThan(0);
        layout.ScrollThumbBounds.Bottom.Should().BeLessThanOrEqualTo(layout.ScrollTrackBounds.Bottom);
    }

    [Test]
    public void FocusTargets_SupportTabAndCardinalNavigation()
    {
        ModHealthViewerLayout layout = new();
        layout.Recompute(new(1280, 720, 1, 100, 10, 3));

        ModHealthViewerFocusTarget section = new(ModHealthViewerTargetKind.Section, 0);
        layout.TryCycleFocus(section, backwards: false, out ModHealthViewerFocusTarget next).Should().BeTrue();
        next.Should().Be(new ModHealthViewerFocusTarget(ModHealthViewerTargetKind.Section, 1));

        ModHealthViewerFocusTarget lastSection = new(ModHealthViewerTargetKind.Section, 7);
        layout.TryCycleFocus(lastSection, backwards: false, out next).Should().BeTrue();
        next.Should().Be(new ModHealthViewerFocusTarget(ModHealthViewerTargetKind.Row, layout.FirstVisibleRow));

        layout.TryMoveFocus(section, ModHealthViewerFocusDirection.Right, out next).Should().BeTrue();
        next.Kind.Should().Be(ModHealthViewerTargetKind.Row);

        ModHealthViewerFocusTarget close = new(ModHealthViewerTargetKind.Close);
        layout.TryCycleFocus(close, backwards: false, out next).Should().BeTrue();
        next.Should().Be(section);
        layout.TryCycleFocus(section, backwards: true, out next).Should().BeTrue();
        next.Should().Be(close);
    }

    [Test]
    public void Recompute_NormalizesInvalidInputWithoutLeavingStaleTargets()
    {
        ModHealthViewerLayout layout = new();
        layout.Recompute(new(1280, 720, 1, 100, 30, 6));

        layout.Recompute(new(-1, 0, float.NaN, -10, -20, -4));

        layout.UiScale.Should().Be(1);
        layout.VisibleRowCount.Should().Be(0);
        layout.ActionCount.Should().Be(0);
        layout.TryGetBounds(new(ModHealthViewerTargetKind.Row, 30), out _).Should().BeFalse();
        layout.TryGetBounds(new(ModHealthViewerTargetKind.Action, 0), out _).Should().BeFalse();
    }

    [Test]
    public void RecomputeAndFocusTraversal_DoNotAllocateAfterWarmup()
    {
        ModHealthViewerLayout layout = new();
        ModHealthViewerLayoutInput input = new(1280, 720, 1, 308, 120, 4);
        ModHealthViewerFocusTarget focus = new(ModHealthViewerTargetKind.Row, 120);
        layout.Recompute(input);
        layout.TryMoveFocus(focus, ModHealthViewerFocusDirection.Down, out _);
        layout.TryCycleFocus(focus, backwards: false, out _);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            layout.Recompute(input);
            layout.TryMoveFocus(focus, ModHealthViewerFocusDirection.Down, out _);
            layout.TryCycleFocus(focus, backwards: false, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().Be(0);
    }
}
