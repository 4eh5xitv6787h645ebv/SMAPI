using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Netcode;
using StardewModdingAPI.Framework.StateTracking.Comparers;
using StardewModdingAPI.Framework.StateTracking.FieldWatchers;
using StardewValley;
using StardewValley.Objects;

namespace StardewModdingAPI.Framework.StateTracking;

/// <summary>Tracks changes to a chest's items.</summary>
internal class ChestTracker : IDisposable
{
    /*********
    ** Fields
    *********/
    /// <summary>Whether each runtime item type uses the game's event-capable <see cref="Item.Stack"/> implementation.</summary>
    private static readonly ConcurrentDictionary<Type, bool> CanTrackStackWithEvents = new();

    /// <summary>The custom item stack sizes which need polling, as of the last update.</summary>
    private readonly Dictionary<Item, int> StackSizes = new(new ObjectReferenceComparer<Item>());

    /// <summary>Event-driven items which changed, mapped to their stack size before the first change.</summary>
    private readonly Dictionary<Item, int> EventStackSizes = new(new ObjectReferenceComparer<Item>());

    /// <summary>Items whose stack sizes may have changed since the last update.</summary>
    private readonly HashSet<Item> DirtyStacks = new(new ObjectReferenceComparer<Item>());

    /// <summary>Event-capable stack fields mapped to their owning items.</summary>
    private readonly Dictionary<NetInt, Item> EventTrackedStacks = new(new ObjectReferenceComparer<NetInt>());

    /// <summary>Items with custom stack implementations which need the compatibility polling fallback.</summary>
    private readonly HashSet<Item> PolledStacks = new(new ObjectReferenceComparer<Item>());

    /// <summary>Items added since the last update.</summary>
    private readonly HashSet<Item> Added = new(new ObjectReferenceComparer<Item>());

    /// <summary>Items removed since the last update.</summary>
    private readonly HashSet<Item> Removed = new(new ObjectReferenceComparer<Item>());

    /// <summary>The underlying inventory watcher.</summary>
    private readonly InventoryWatcher InventoryWatcher;

    /// <summary>Notify the owning location when this chest changes.</summary>
    private readonly Action<ChestTracker>? OnChanged;

    /// <summary>The shared handler registered for event-capable item stacks.</summary>
    private readonly FieldChange<NetInt, int> StackChangedHandler;

    /// <summary>Whether inventory changes are currently being tracked.</summary>
    private bool IsTrackingInventoryChanges;


    /*********
    ** Accessors
    *********/
    /// <summary>The chest being tracked.</summary>
    public Chest Chest { get; }

    /// <summary>Whether this chest has any custom item stack implementations which need polling.</summary>
    public bool RequiresStackPolling => this.PolledStacks.Count > 0;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="name">A name which identifies what the watcher is watching, used for troubleshooting.</param>
    /// <param name="chest">The chest being tracked.</param>
    /// <param name="onChanged">Notify the owning location when this chest changes.</param>
    public ChestTracker(string name, Chest chest, Action<ChestTracker>? onChanged = null)
    {
        this.Chest = chest;
        this.OnChanged = onChanged;
        this.StackChangedHandler = this.OnStackChanged;
        this.InventoryWatcher = WatcherFactory.ForInventory($"{name}.{nameof(chest.Items)}", chest.Items, isEnabled: false, onChanged: this.NotifyChanged);
    }

    /// <summary>Update the current values if needed.</summary>
    /// <param name="trackInventoryChanges">Whether to track changes needed for the chest inventory event.</param>
    public void Update(bool trackInventoryChanges)
    {
        // activate/deactivate with a fresh baseline, so changes from while tracking was disabled aren't reported
        if (this.IsTrackingInventoryChanges != trackInventoryChanges)
        {
            this.IsTrackingInventoryChanges = trackInventoryChanges;
            this.UntrackAllStacks();
            this.StackSizes.Clear();
            this.EventStackSizes.Clear();
            this.DirtyStacks.Clear();
            this.Added.Clear();
            this.Removed.Clear();
            this.InventoryWatcher.SetEnabled(trackInventoryChanges);

            if (trackInventoryChanges)
            {
                foreach (Item? item in this.Chest.Items)
                {
                    if (item is not null)
                        this.TrackStack(item);
                }
            }
            return;
        }

        // no inventory notifications are registered when no mod needs the event
        if (!trackInventoryChanges)
            return;

        // update watcher
        this.InventoryWatcher.Update();
        foreach (Item item in this.InventoryWatcher.Added)
        {
            this.Added.Add(item);
            this.TrackStack(item);
        }
        foreach (Item item in this.InventoryWatcher.Removed)
        {
            if (!this.Added.Remove(item)) // item didn't change if it was both added and removed, so remove it from both lists
                this.Removed.Add(item);

            this.UntrackStack(item);
        }
    }

    /// <summary>Poll custom item stack implementations which can't use the game's stack field events.</summary>
    public void UpdatePolledStacks()
    {
        if (!this.IsTrackingInventoryChanges)
            return;

        bool changed = false;
        foreach (Item item in this.PolledStacks)
        {
            if (this.StackSizes.TryGetValue(item, out int oldStack) && item.Stack != oldStack)
                changed |= this.DirtyStacks.Add(item);
        }

        if (changed)
            this.NotifyChanged();
    }

    /// <summary>Reset all trackers so their current values are the baseline.</summary>
    public void Reset()
    {
        if (!this.IsTrackingInventoryChanges)
            return;

        // update stack sizes which changed, without scanning every item in every chest
        foreach (Item item in this.DirtyStacks)
        {
            if (this.StackSizes.ContainsKey(item))
                this.StackSizes[item] = item.Stack;
        }

        // update watcher
        this.InventoryWatcher.Reset();
        this.EventStackSizes.Clear();
        this.DirtyStacks.Clear();
        this.Added.Clear();
        this.Removed.Clear();
    }

    /// <summary>Get the inventory changes since the last update, if anything changed.</summary>
    /// <param name="changes">The inventory changes, or <c>null</c> if nothing changed.</param>
    /// <returns>Returns whether anything changed.</returns>
    public bool TryGetInventoryChanges([NotNullWhen(true)] out SnapshotItemListDiff? changes)
    {
        if (!this.IsTrackingInventoryChanges)
        {
            changes = null;
            return false;
        }

        return SnapshotItemListDiff.TryGetChanges(added: this.Added, removed: this.Removed, stackSizes: this.StackSizes, stackItemsToCheck: this.DirtyStacks, additionalStackSizes: this.EventStackSizes, out changes);
    }

    /// <summary>Release watchers and resources.</summary>
    public void Dispose()
    {
        this.UntrackAllStacks();
        this.StackSizes.Clear();
        this.EventStackSizes.Clear();
        this.DirtyStacks.Clear();
        this.Added.Clear();
        this.Removed.Clear();
        this.InventoryWatcher.Dispose();
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Start tracking an item's stack size.</summary>
    /// <param name="item">The item to track.</param>
    private void TrackStack(Item item)
    {
        if (this.EventTrackedStacks.ContainsKey(item.stack) || this.StackSizes.ContainsKey(item))
            return;

        bool canUseEvents = ChestTracker.CanTrackStackWithEvents.GetOrAdd(
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
    /// <param name="item">The item to untrack.</param>
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

    /// <summary>Mark an event-capable item stack as dirty.</summary>
    private void OnStackChanged(NetInt field, int oldValue, int newValue)
    {
        if (this.EventTrackedStacks.TryGetValue(field, out Item? item) && this.DirtyStacks.Add(item))
        {
            this.EventStackSizes.Add(item, Math.Max(0, oldValue));
            this.NotifyChanged();
        }
    }

    /// <summary>Notify the owning location that this chest changed.</summary>
    private void NotifyChanged()
    {
        this.OnChanged?.Invoke(this);
    }
}
