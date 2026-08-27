using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.Health.Viewer;

namespace SMAPI.Tests.Framework.Health.Viewer;

[TestFixture]
internal sealed class ModHealthViewerNavigationStateTests
{
    [Test]
    public void Rows_ClampAndRemainVisibleAcrossPagingAndResize()
    {
        ModHealthViewerNavigationState state = new();
        state.SetVisibleRowCount(5, 20);

        state.Apply(ModHealthViewerNavigationCommand.PageDown, 8, 20).Should().BeTrue();
        state.RowIndex.Should().Be(5);
        state.FirstVisibleRow.Should().Be(1);
        state.Apply(ModHealthViewerNavigationCommand.LastRow, 8, 20);
        state.RowIndex.Should().Be(19);
        state.FirstVisibleRow.Should().Be(15);

        state.SetVisibleRowCount(10, 7);

        state.RowIndex.Should().Be(6);
        state.FirstVisibleRow.Should().Be(0);
    }

    [Test]
    public void Sections_WrapAndResetRowPosition()
    {
        ModHealthViewerNavigationState state = new();
        state.SetVisibleRowCount(4, 10);
        state.Apply(ModHealthViewerNavigationCommand.LastRow, 8, 10);

        state.Apply(ModHealthViewerNavigationCommand.PreviousSection, 8, 3);

        state.SectionIndex.Should().Be(7);
        state.RowIndex.Should().Be(0);
        state.FirstVisibleRow.Should().Be(0);
        state.Apply(ModHealthViewerNavigationCommand.NextSection, 8, 2);
        state.SectionIndex.Should().Be(0);
    }

    [Test]
    public void EmptyRows_StayAtZero()
    {
        ModHealthViewerNavigationState state = new();

        state.Apply(ModHealthViewerNavigationCommand.NextRow, 8, 0);
        state.Apply(ModHealthViewerNavigationCommand.PageDown, 8, 0);
        state.SelectVisibleRow(20, 0);

        state.RowIndex.Should().Be(0);
        state.FirstVisibleRow.Should().Be(0);
    }
}
