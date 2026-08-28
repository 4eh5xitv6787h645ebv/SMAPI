using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Netcode;
using StardewModdingAPI.Framework.StateTracking.Comparers;
using StardewValley;
using StardewValley.Inventories;

namespace StardewModdingAPI.Framework.StateTracking;

/// <summary>Tracks structural and stack-size changes in an item inventory.</summary>
internal sealed class InventoryTracker : IDisposable
{
    /*********
    ** Fields
    *********/
    /// <summary>Whether each runtime item type uses the game's event-capable <see cref="Item.Stack"/> implementation.</summary>
    private static readonly ConcurrentDictionary<Type, bool> CanTrackStackWithEvents = new();

    /// <summary>Read a tracked item's current stack size without allocating a delegate per reset.</summary>
    private static readonly Func<Item, int> GetItemStackSize = static item => item.Stack;

    /// <summary>The custom item stack sizes as of the last reset.</summary>
    private readonly Dictionary<Item, int> StackSizes = new(new ObjectReferenceComparer<Item>());

    /// <summary>Event-driven items which changed, mapped to their stack size before the first change.</summary>
    private readonly Dictionary<Item, int> EventStackSizes = new(new ObjectReferenceComparer<Item>());

    /// <summary>Items whose stack sizes may have changed since the last reset.</summary>
    private readonly HashSet<Item> DirtyStacks = new(new ObjectReferenceComparer<Item>());

    /// <summary>Event-capable stack fields mapped to their owning items.</summary>
    private readonly Dictionary<NetInt, Item> EventTrackedStacks = new(new ObjectReferenceComparer<NetInt>());

    /// <summary>Items with custom stack implementations which need the compatibility polling fallback.</summary>
    private readonly HashSet<Item> PolledStacks = new(new ObjectReferenceComparer<Item>());

    /// <summary>Items added since the last reset.</summary>
    private readonly HashSet<Item> Added = new(new ObjectReferenceComparer<Item>());

    /// <summary>Items removed since the last reset.</summary>
    private readonly HashSet<Item> Removed = new(new ObjectReferenceComparer<Item>());

    /// <summary>The number of inventory slots which reference each current item.</summary>
    private readonly Dictionary<Item, int> ReferenceCounts = new(new ObjectReferenceComparer<Item>());

    /// <summary>The unique items which were present at the last reset.</summary>
    private readonly HashSet<Item> BaselineItems = new(new ObjectReferenceComparer<Item>());

    /// <summary>The inventory being tracked.</summary>
    private readonly Inventory Inventory;

    /// <summary>Whether to report quantity changes for items which were also removed in the same reset window.</summary>
    private readonly bool IncludeRemovedStackChanges;

    /// <summary>Notify the owner when this inventory changes.</summary>
    private readonly Action? OnChanged;

    /// <summary>The shared handler registered for event-capable item stacks.</summary>
    private readonly FieldChange<NetInt, int> StackChangedHandler;

    /// <summary>The cached stack-unsubscribe callback passed to the game-independent reset core.</summary>
    private readonly Action<Item> UntrackStackHandler;

    /// <summary>Whether inventory changes are currently being tracked.</summary>
    private bool IsTrackingChanges;


    /*********
    ** Accessors
    *********/
    /// <summary>Whether this inventory contains any custom item stack implementations which need polling.</summary>
    public bool RequiresStackPolling => this.PolledStacks.Count > 0;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="inventory">The inventory to track.</param>
    /// <param name="includeRemovedStackChanges">Whether to report quantity changes for items which were also removed in the same reset window.</param>
    /// <param name="onChanged">Notify the owner when the inventory first changes after a reset.</param>
    public InventoryTracker(Inventory inventory, bool includeRemovedStackChanges, Action? onChanged = null)
    {
        this.Inventory = inventory;
        this.IncludeRemovedStackChanges = includeRemovedStackChanges;
        this.OnChanged = onChanged;
        this.StackChangedHandler = this.OnStackChanged;
        this.UntrackStackHandler = this.UntrackStack;
    }

    /// <summary>Update the current values if needed.</summary>
    /// <param name="trackChanges">Whether to track changes needed for an inventory event.</param>
    public void Update(bool trackChanges)
    {
        if (!InventoryTrackerHotPath.TrySetTracking(ref this.IsTrackingChanges, trackChanges))
            return;

        if (trackChanges)
        {
            // Establish a fresh unique-item baseline before subscribing, so changes from while tracking was
            // disabled aren't reported. Reference counts preserve the old snapshot behavior for duplicate slots.
            foreach (Item? item in this.Inventory)
            {
                if (item is null)
                    continue;

                if (this.ReferenceCounts.TryGetValue(item, out int count))
                    this.ReferenceCounts[item] = count + 1;
                else
                {
                    this.ReferenceCounts.Add(item, 1);
                    this.BaselineItems.Add(item);
                    this.TrackStack(item);
                }
            }

            this.Inventory.OnSlotChanged += this.OnSlotChanged;
            this.Inventory.OnInventoryReplaced += this.OnInventoryReplaced;
            return;
        }

        this.Inventory.OnSlotChanged -= this.OnSlotChanged;
        this.Inventory.OnInventoryReplaced -= this.OnInventoryReplaced;
        this.ClearTrackingState();
    }

    /// <summary>Poll only custom item stack implementations which can't use the game's stack field events.</summary>
    public void UpdatePolledStacks()
    {
        if (!this.IsTrackingChanges)
            return;

        bool changed = false;
        foreach (Item item in this.PolledStacks)
        {
            // A newly added stack is represented by Added at its final size, matching the previous snapshot semantics.
            if (this.BaselineItems.Contains(item) && this.StackSizes.TryGetValue(item, out int oldStack) && item.Stack != oldStack)
                changed |= this.DirtyStacks.Add(item);
        }

        if (changed)
            this.NotifyChanged();
    }

    /// <summary>Reset the current values as the baseline.</summary>
    public void Reset()
    {
        if (!this.IsTrackingChanges)
            return;

        // Removed baseline items remain subscribed until reset. This preserves a quantity change which occurs
        // before removal, while still avoiding any scan of unchanged/current normal items.
        InventoryTrackerHotPath.Reset(
            this.StackSizes,
            this.EventStackSizes,
            this.DirtyStacks,
            this.Added,
            this.Removed,
            this.BaselineItems,
            InventoryTracker.GetItemStackSize,
            this.UntrackStackHandler
        );
    }

    /// <summary>Get the inventory changes since the last reset, if anything changed.</summary>
    /// <param name="changes">The inventory changes, or <c>null</c> if nothing changed.</param>
    /// <returns>Returns whether anything changed.</returns>
    public bool TryGetChanges([NotNullWhen(true)] out SnapshotItemListDiff? changes)
    {
        // Chests historically omit quantity changes for an item which is absent from the final inventory,
        // while player inventory snapshots include them. Delay suppression until snapshot creation so a
        // remove/re-add within the same window retains the original stack baseline for both owners.
        // Besides avoiding unnecessary diff work, this preserves an allocation-free idle path despite the
        // enumerable-based compatibility API used to build nonempty snapshots.
        if (!InventoryTrackerHotPath.PrepareDiff(this.IncludeRemovedStackChanges, this.IsTrackingChanges, this.Added, this.Removed, this.DirtyStacks, this.EventStackSizes))
        {
            changes = null;
            return false;
        }

        return SnapshotItemListDiff.TryGetChanges(
            added: this.Added,
            removed: this.Removed,
            stackSizes: this.StackSizes,
            stackItemsToCheck: this.DirtyStacks,
            additionalStackSizes: this.EventStackSizes,
            out changes
        );
    }

    /// <summary>Release watchers and resources.</summary>
    public void Dispose()
    {
        if (this.IsTrackingChanges)
        {
            this.IsTrackingChanges = false;
            this.Inventory.OnSlotChanged -= this.OnSlotChanged;
            this.Inventory.OnInventoryReplaced -= this.OnInventoryReplaced;
        }
        this.ClearTrackingState();
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Track one item added to an inventory slot.</summary>
    private void AddItem(Item? item)
    {
        if (item is null)
            return;

        if (this.ReferenceCounts.TryGetValue(item, out int count))
        {
            this.ReferenceCounts[item] = count + 1;
            return;
        }

        this.ReferenceCounts.Add(item, 1);
        if (this.BaselineItems.Contains(item))
            this.Removed.Remove(item);
        else
        {
            this.Added.Add(item);
            this.TrackStack(item);
        }
        this.NotifyChanged();
    }

    /// <summary>Track one item removed from an inventory slot.</summary>
    private void RemoveItem(Item? item)
    {
        if (item is null || !this.ReferenceCounts.TryGetValue(item, out int count))
            return;

        if (count > 1)
        {
            this.ReferenceCounts[item] = count - 1;
            return;
        }

        this.ReferenceCounts.Remove(item);
        if (this.BaselineItems.Contains(item))
            this.Removed.Add(item);
        else
        {
            this.Added.Remove(item);
            this.UntrackStack(item);
        }
        this.NotifyChanged();
    }

    /// <summary>Track a changed inventory slot.</summary>
    private void OnSlotChanged(Inventory inventory, int index, Item? oldValue, Item? newValue)
    {
        this.RemoveItem(oldValue);
        this.AddItem(newValue);
    }

    /// <summary>Track replacement of the inventory's full value list.</summary>
    private void OnInventoryReplaced(Inventory inventory, IList<Item> oldValues, IList<Item> newValues)
    {
        foreach (Item? item in oldValues)
            this.RemoveItem(item);
        foreach (Item? item in newValues)
            this.AddItem(item);
    }

    /// <summary>Start tracking an item's stack size.</summary>
    private void TrackStack(Item item)
    {
        if (this.EventTrackedStacks.ContainsKey(item.stack) || this.StackSizes.ContainsKey(item))
            return;

        bool canUseEvents = InventoryTracker.CanTrackStackWithEvents.GetOrAdd(
            item.GetType(),
            type => type.GetProperty(nameof(Item.Stack))?.GetMethod?.DeclaringType == typeof(Item)
        );
        if (canUseEvents)
        {
            this.EventTrackedStacks[item.stack] = item;
            item.stack.fieldChangeEvent += this.StackChangedHandler;
            item.stack.fieldChangeVisibleEvent += this.StackChangedHandler;
        }
        else
        {
            this.StackSizes.Add(item, item.Stack);
            this.PolledStacks.Add(item);
        }
    }

    /// <summary>Stop tracking an item's stack size.</summary>
    private void UntrackStack(Item item)
    {
        if (this.EventTrackedStacks.Remove(item.stack))
        {
            item.stack.fieldChangeEvent -= this.StackChangedHandler;
            item.stack.fieldChangeVisibleEvent -= this.StackChangedHandler;
        }
        this.PolledStacks.Remove(item);
        this.DirtyStacks.Remove(item);
        this.EventStackSizes.Remove(item);
        this.StackSizes.Remove(item);
    }

    /// <summary>Stop tracking every item stack.</summary>
    private void UntrackAllStacks()
    {
        foreach (NetInt stack in this.EventTrackedStacks.Keys)
        {
            stack.fieldChangeEvent -= this.StackChangedHandler;
            stack.fieldChangeVisibleEvent -= this.StackChangedHandler;
        }
        this.EventTrackedStacks.Clear();
        this.PolledStacks.Clear();
    }

    /// <summary>Clear all subscriptions and current/baseline changes.</summary>
    private void ClearTrackingState()
    {
        this.UntrackAllStacks();
        this.StackSizes.Clear();
        this.EventStackSizes.Clear();
        this.DirtyStacks.Clear();
        this.Added.Clear();
        this.Removed.Clear();
        this.ReferenceCounts.Clear();
        this.BaselineItems.Clear();
    }

    /// <summary>Mark an event-capable item stack as dirty.</summary>
    private void OnStackChanged(NetInt field, int oldValue, int newValue)
    {
        if (
            this.EventTrackedStacks.TryGetValue(field, out Item? item)
            && this.BaselineItems.Contains(item)
            && this.DirtyStacks.Add(item)
        )
        {
            this.EventStackSizes.Add(item, Math.Max(0, oldValue));
            this.NotifyChanged();
        }
    }

    /// <summary>Notify the owner that this inventory changed.</summary>
    private void NotifyChanged()
    {
        this.OnChanged?.Invoke();
    }
}
