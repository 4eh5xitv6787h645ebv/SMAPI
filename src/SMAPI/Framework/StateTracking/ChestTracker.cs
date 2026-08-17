using System;
using System.Diagnostics.CodeAnalysis;
using StardewValley.Objects;

namespace StardewModdingAPI.Framework.StateTracking;

/// <summary>Tracks changes to a chest's items.</summary>
internal class ChestTracker : IDisposable
{
    /*********
    ** Fields
    *********/
    /// <summary>The underlying inventory tracker.</summary>
    private readonly InventoryTracker InventoryTracker;

    /// <summary>Notify the owning location when this chest changes.</summary>
    private readonly Action<ChestTracker>? OnChanged;


    /*********
    ** Accessors
    *********/
    /// <summary>The chest being tracked.</summary>
    public Chest Chest { get; }

    /// <summary>Whether this chest has any custom item stack implementations which need polling.</summary>
    public bool RequiresStackPolling => this.InventoryTracker.RequiresStackPolling;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="name">A name which identifies what is being watched, used for troubleshooting.</param>
    /// <param name="chest">The chest being tracked.</param>
    /// <param name="onChanged">Notify the owning location when this chest changes.</param>
    public ChestTracker(string name, Chest chest, Action<ChestTracker>? onChanged = null)
    {
        this.Chest = chest;
        this.OnChanged = onChanged;
        this.InventoryTracker = new InventoryTracker(chest.Items, includeRemovedStackChanges: false, onChanged: this.NotifyChanged);
    }

    /// <summary>Update the current values if needed.</summary>
    /// <param name="trackInventoryChanges">Whether to track changes needed for the chest inventory event.</param>
    public void Update(bool trackInventoryChanges)
    {
        this.InventoryTracker.Update(trackInventoryChanges);
    }

    /// <summary>Poll only custom item stack implementations which can't use the game's stack field events.</summary>
    public void UpdatePolledStacks()
    {
        this.InventoryTracker.UpdatePolledStacks();
    }

    /// <summary>Reset all trackers so their current values are the baseline.</summary>
    public void Reset()
    {
        this.InventoryTracker.Reset();
    }

    /// <summary>Get the inventory changes since the last reset, if anything changed.</summary>
    /// <param name="changes">The inventory changes, or <c>null</c> if nothing changed.</param>
    /// <returns>Returns whether anything changed.</returns>
    public bool TryGetInventoryChanges([NotNullWhen(true)] out SnapshotItemListDiff? changes)
    {
        return this.InventoryTracker.TryGetChanges(out changes);
    }

    /// <summary>Release watchers and resources.</summary>
    public void Dispose()
    {
        this.InventoryTracker.Dispose();
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Notify the owning location that this chest changed.</summary>
    private void NotifyChanged()
    {
        this.OnChanged?.Invoke(this);
    }
}
