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

/// <summary>Unit tests for <see cref="InventoryTracker"/>.</summary>
[TestFixture]
internal class InventoryTrackerTests
{
    [Test(Description = "Assert that normal item stack fields push one change with the reset-time baseline.")]
    public void Update_TracksNormalStacksFromEvents()
    {
        Chest chest = new();
        Item item = new StardewValley.Object { Stack = 2 };
        chest.Items.Add(item);
        using InventoryTracker tracker = new(chest.Items, includeRemovedStackChanges: true);
        tracker.Update(trackChanges: true);
        tracker.Reset();

        item.Stack = 4;
        item.Stack = 7;

        tracker.RequiresStackPolling.Should().BeFalse();
        tracker.TryGetChanges(out SnapshotItemListDiff? changes).Should().BeTrue();
        changes!.QuantityChanged.Should().ContainSingle().Which.Should().Match<ItemStackSizeChange>(change =>
            ReferenceEquals(change.Item, item)
            && change.OldSize == 2
            && change.NewSize == 7
        );

        tracker.Reset();
        tracker.TryGetChanges(out _).Should().BeFalse();
    }

    [Test(Description = "Assert that custom Stack overrides retain a narrow polling fallback.")]
    public void UpdatePolledStacks_TracksCustomImplementations()
    {
        Chest chest = new();
        CustomStackObject item = new() { Stack = 2 };
        chest.Items.Add(item);
        using InventoryTracker tracker = new(chest.Items, includeRemovedStackChanges: true);
        tracker.Update(trackChanges: true);
        tracker.Reset();

        item.Stack = 5;

        tracker.RequiresStackPolling.Should().BeTrue();
        tracker.TryGetChanges(out _).Should().BeFalse();
        tracker.UpdatePolledStacks();
        tracker.TryGetChanges(out SnapshotItemListDiff? changes).Should().BeTrue();
        changes!.QuantityChanged.Should().ContainSingle().Which.Should().Match<ItemStackSizeChange>(change =>
            ReferenceEquals(change.Item, item)
            && change.OldSize == 2
            && change.NewSize == 5
        );
    }

    [Test(Description = "Assert that a newly added stack uses its final size without a duplicate quantity change.")]
    public void Update_NewStackPreservesAdditionSemantics()
    {
        Chest chest = new();
        using InventoryTracker tracker = new(chest.Items, includeRemovedStackChanges: true);
        tracker.Update(trackChanges: true);
        tracker.Reset();

        Item normal = new StardewValley.Object { Stack = 1 };
        CustomStackObject custom = new() { Stack = 2 };
        chest.Items.Add(normal);
        chest.Items.Add(custom);
        tracker.Update(trackChanges: true);

        normal.Stack = 4;
        custom.Stack = 6;
        tracker.UpdatePolledStacks();

        tracker.TryGetChanges(out SnapshotItemListDiff? changes).Should().BeTrue();
        changes!.Added.Should().BeEquivalentTo([normal, custom]);
        changes.QuantityChanged.Should().BeEmpty();

        tracker.Reset();
        tracker.UpdatePolledStacks();
        tracker.TryGetChanges(out _).Should().BeFalse();

        normal.Stack = 5;
        custom.Stack = 7;
        tracker.UpdatePolledStacks();
        tracker.TryGetChanges(out changes).Should().BeTrue();
        changes!.QuantityChanged.Should().HaveCount(2);
    }

    [Test(Description = "Assert that removed-stack quantity reporting can preserve player and chest event semantics.")]
    [TestCase(true, 1)]
    [TestCase(false, 0)]
    public void Update_RemovedStackPreservesOwnerSemantics(bool includeRemovedStackChanges, int expectedQuantityChanges)
    {
        Chest chest = new();
        Item item = new StardewValley.Object { Stack = 2 };
        chest.Items.Add(item);
        using InventoryTracker tracker = new(chest.Items, includeRemovedStackChanges);
        tracker.Update(trackChanges: true);
        tracker.Reset();

        item.Stack = 5;
        chest.Items.Remove(item);

        tracker.TryGetChanges(out SnapshotItemListDiff? changes).Should().BeTrue();
        changes!.Removed.Should().ContainSingle().Which.Should().BeSameAs(item);
        changes.QuantityChanged.Should().HaveCount(expectedQuantityChanges);
    }

    [Test(Description = "Assert that removing and re-adding a stack retains its reset-time quantity baseline.")]
    [TestCase(true)]
    [TestCase(false)]
    public void Update_ReaddedStackRetainsOriginalBaseline(bool includeRemovedStackChanges)
    {
        Chest chest = new();
        Item item = new StardewValley.Object { Stack = 2 };
        chest.Items.Add(item);
        using InventoryTracker tracker = new(chest.Items, includeRemovedStackChanges);
        tracker.Update(trackChanges: true);
        tracker.Reset();

        item.Stack = 3;
        chest.Items.Remove(item);
        chest.Items.Add(item);
        item.Stack = 4;

        tracker.TryGetChanges(out SnapshotItemListDiff? changes).Should().BeTrue();
        changes!.Added.Should().BeEmpty();
        changes.Removed.Should().BeEmpty();
        changes.QuantityChanged.Should().ContainSingle().Which.Should().Match<ItemStackSizeChange>(change =>
            ReferenceEquals(change.Item, item)
            && change.OldSize == 2
            && change.NewSize == 4
        );
    }

    [Test(Description = "Assert that duplicate slot references retain unique-item snapshot semantics.")]
    public void Update_TracksDuplicateReferencesByUniqueItem()
    {
        Chest chest = new();
        Item item = new StardewValley.Object { Stack = 2 };
        chest.Items.Add(item);
        chest.Items.Add(item);
        using InventoryTracker tracker = new(chest.Items, includeRemovedStackChanges: true);
        tracker.Update(trackChanges: true);
        tracker.Reset();

        chest.Items.Remove(item);
        tracker.TryGetChanges(out _).Should().BeFalse();

        chest.Items.Remove(item);
        tracker.TryGetChanges(out SnapshotItemListDiff? removed).Should().BeTrue();
        removed!.Removed.Should().ContainSingle().Which.Should().BeSameAs(item);

        tracker.Reset();
        chest.Items.Add(item);
        chest.Items.Add(item);
        chest.Items.Remove(item);
        tracker.TryGetChanges(out SnapshotItemListDiff? added).Should().BeTrue();
        added!.Added.Should().ContainSingle().Which.Should().BeSameAs(item);
    }

    [Test(Description = "Assert that replacing the inventory value list produces the same unique-item diff.")]
    public void Update_TracksFullInventoryReplacement()
    {
        Chest chest = new();
        Item original = new StardewValley.Object { Stack = 2 };
        Item replacement = new StardewValley.Object { Stack = 3 };
        chest.Items.Add(original);
        using InventoryTracker tracker = new(chest.Items, includeRemovedStackChanges: true);
        tracker.Update(trackChanges: true);
        tracker.Reset();

        chest.Items.OverwriteWith(new List<Item> { replacement });

        tracker.TryGetChanges(out SnapshotItemListDiff? changes).Should().BeTrue();
        changes!.Removed.Should().ContainSingle().Which.Should().BeSameAs(original);
        changes.Added.Should().ContainSingle().Which.Should().BeSameAs(replacement);
    }

    [Test(Description = "Assert that an unchanged normal inventory has an allocation-free observed-tick path.")]
    [Category("PerformanceRegression")]
    [NonParallelizable]
    public void Update_NormalIdlePathDoesNotAllocate()
    {
        Chest chest = new();
        for (int i = 0; i < 36; i++)
            chest.Items.Add(new StardewValley.Object { Stack = i + 1 });

        using InventoryTracker tracker = new(chest.Items, includeRemovedStackChanges: true);
        tracker.Update(trackChanges: true);
        tracker.Reset();

        for (int i = 0; i < 100; i++)
        {
            tracker.Update(trackChanges: true);
            tracker.TryGetChanges(out _);
            tracker.Reset();
        }

        const int iterations = 10_000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool changed = false;
        for (int i = 0; i < iterations; i++)
        {
            tracker.Update(trackChanges: true);
            changed |= tracker.TryGetChanges(out _);
            tracker.Reset();
        }
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        changed.Should().BeFalse();
        allocatedBytes.Should().Be(0);
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
