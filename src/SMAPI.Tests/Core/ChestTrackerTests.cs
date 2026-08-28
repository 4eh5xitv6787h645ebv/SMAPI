using System;
using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Events;
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

    [Test(Description = "Assert that normal item stack changes push a notification and preserve the reset-time baseline.")]
    public void Update_TracksNormalStacksFromNetFieldEvents()
    {
        Chest chest = new();
        Item item = new StardewValley.Object { Stack = 2 };
        chest.Items.Add(item);
        int notifications = 0;
        using ChestTracker tracker = new("chest", chest, _ => notifications++);
        tracker.Update(trackInventoryChanges: true);
        tracker.Reset();

        item.Stack = 4;
        item.Stack = 7;

        notifications.Should().Be(1);
        tracker.RequiresStackPolling.Should().BeFalse();
        tracker.TryGetInventoryChanges(out SnapshotItemListDiff? changes).Should().BeTrue();
        changes!.QuantityChanged.Should().ContainSingle().Which.Should().Match<ItemStackSizeChange>(change =>
            ReferenceEquals(change.Item, item)
            && change.OldSize == 2
            && change.NewSize == 7
        );

        tracker.Reset();
        tracker.TryGetInventoryChanges(out _).Should().BeFalse();
    }

    [Test(Description = "Assert that custom Stack overrides retain a polling fallback for mod compatibility.")]
    public void Update_PollsCustomStackImplementations()
    {
        Chest chest = new();
        CustomStackObject item = new() { Stack = 2 };
        chest.Items.Add(item);
        int notifications = 0;
        using ChestTracker tracker = new("chest", chest, _ => notifications++);
        tracker.Update(trackInventoryChanges: true);
        tracker.Reset();

        item.Stack = 5;

        notifications.Should().Be(0);
        tracker.RequiresStackPolling.Should().BeTrue();
        tracker.UpdatePolledStacks();
        notifications.Should().Be(1);
        tracker.TryGetInventoryChanges(out SnapshotItemListDiff? changes).Should().BeTrue();
        changes!.QuantityChanged.Should().ContainSingle().Which.Should().Match<ItemStackSizeChange>(change =>
            ReferenceEquals(change.Item, item)
            && change.OldSize == 2
            && change.NewSize == 5
        );
    }

    [Test(Description = "Assert that item-list events push a notification and update stack subscriptions.")]
    public void Update_TracksAddedAndRemovedItemStacks()
    {
        Chest chest = new();
        Item original = new StardewValley.Object { Stack = 2 };
        chest.Items.Add(original);
        int notifications = 0;
        using ChestTracker tracker = new("chest", chest, _ => notifications++);
        tracker.Update(trackInventoryChanges: true);
        tracker.Reset();

        Item added = new StardewValley.Object { Stack = 1 };
        chest.Items.Add(added);
        notifications.Should().Be(1);
        tracker.Update(trackInventoryChanges: true);
        tracker.TryGetInventoryChanges(out SnapshotItemListDiff? addedChanges).Should().BeTrue();
        addedChanges!.Added.Should().ContainSingle().Which.Should().BeSameAs(added);

        tracker.Reset();
        chest.Items.Remove(original);
        tracker.Update(trackInventoryChanges: true);
        tracker.TryGetInventoryChanges(out SnapshotItemListDiff? removedChanges).Should().BeTrue();
        removedChanges!.Removed.Should().ContainSingle().Which.Should().BeSameAs(original);
    }

    [Test(Description = "Assert that unchanged observed chests have an allocation-free idle path.")]
    [Category("PerformanceRegression")]
    [NonParallelizable]
    public void Update_NormalIdleChestsDoNotAllocate()
    {
        const int chestCount = 32;
        const int itemsPerChest = 12;
        List<ChestTracker> trackers = new(chestCount);
        try
        {
            for (int chestIndex = 0; chestIndex < chestCount; chestIndex++)
            {
                Chest chest = new();
                for (int itemIndex = 0; itemIndex < itemsPerChest; itemIndex++)
                    chest.Items.Add(new StardewValley.Object { Stack = itemIndex + 1 });

                ChestTracker tracker = new($"chest-{chestIndex}", chest);
                tracker.Update(trackInventoryChanges: true);
                tracker.Reset();
                trackers.Add(tracker);
            }

            for (int iteration = 0; iteration < 100; iteration++)
            {
                foreach (ChestTracker tracker in trackers)
                {
                    tracker.Update(trackInventoryChanges: true);
                    tracker.TryGetInventoryChanges(out _);
                    tracker.Reset();
                }
            }

            const int iterations = 1_000;
            bool changed = false;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                foreach (ChestTracker tracker in trackers)
                {
                    tracker.Update(trackInventoryChanges: true);
                    changed |= tracker.TryGetInventoryChanges(out _);
                    tracker.Reset();
                }
            }
            long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

            changed.Should().BeFalse();
            allocatedBytes.Should().Be(0, "unchanged normal stacks should remain event-driven instead of being rescanned or snapshotted");
        }
        finally
        {
            foreach (ChestTracker tracker in trackers)
                tracker.Dispose();
        }
    }

    /// <summary>A representative mod item whose quantity isn't backed by <see cref="Item.stack"/>.</summary>
    private sealed class CustomStackObject : StardewValley.Object
    {
        private int CustomStack;

        public override int Stack
        {
            get => this.CustomStack;
            set => this.CustomStack = value;
        }
    }
}
