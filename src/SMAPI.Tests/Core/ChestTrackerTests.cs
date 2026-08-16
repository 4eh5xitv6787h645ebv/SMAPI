using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework;
using StardewModdingAPI.Framework.StateTracking;
using StardewValley;
using StardewValley.Objects;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for <see cref="ChestTracker"/>.</summary>
[TestFixture]
internal class ChestTrackerTests
{
    [Test(Description = "Assert that disabled chest tracking ignores changes and establishes a fresh baseline when enabled.")]
    public void Update_TracksInventoryOnlyWhenEnabled()
    {
        // arrange
        Chest chest = new();
        Item originalItem = new StardewValley.Object { Stack = 2 };
        chest.Items.Add(originalItem);
        using ChestTracker tracker = new("chest", chest);

        // act: enable tracking and establish a baseline
        tracker.Update(trackInventoryChanges: true);

        // assert: the existing inventory isn't reported as new
        tracker.TryGetInventoryChanges(out _).Should().BeFalse();

        // act: change a stack while tracking
        tracker.Reset();
        originalItem.Stack = 3;
        tracker.Update(trackInventoryChanges: true);

        // assert: the observed stack change is reported
        tracker.TryGetInventoryChanges(out SnapshotItemListDiff? trackedChanges).Should().BeTrue();
        trackedChanges!.QuantityChanged.Should().ContainSingle();

        // act: disable tracking and change the inventory
        tracker.Reset();
        tracker.Update(trackInventoryChanges: false);
        chest.Items.Add(new StardewValley.Object { Stack = 1 });
        tracker.Update(trackInventoryChanges: false);

        // assert: disabled changes aren't retained or reported later
        tracker.TryGetInventoryChanges(out _).Should().BeFalse();
        tracker.Update(trackInventoryChanges: true);
        tracker.TryGetInventoryChanges(out _).Should().BeFalse();
    }
}
