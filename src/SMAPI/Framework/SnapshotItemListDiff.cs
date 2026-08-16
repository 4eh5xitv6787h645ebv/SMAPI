using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using StardewModdingAPI.Events;
using StardewValley;

namespace StardewModdingAPI.Framework;

/// <summary>A snapshot of a tracked item list.</summary>
internal class SnapshotItemListDiff
{
    /*********
    ** Accessors
    *********/
    /// <summary>Whether the item list changed.</summary>
    public bool IsChanged { get; }

    /// <summary>The removed values.</summary>
    public Item[] Removed { get; }

    /// <summary>The added values.</summary>
    public Item[] Added { get; }

    /// <summary>The items whose stack sizes changed.</summary>
    public ItemStackSizeChange[] QuantityChanged { get; }


    /*********
    ** Public methods
    *********/
    /// <summary>Update the snapshot.</summary>
    /// <param name="added">The added values.</param>
    /// <param name="removed">The removed values.</param>
    /// <param name="sizesChanged">The items whose stack sizes changed.</param>
    public SnapshotItemListDiff(Item[] added, Item[] removed, ItemStackSizeChange[] sizesChanged)
    {
        this.Removed = removed;
        this.Added = added;
        this.QuantityChanged = sizesChanged;

        this.IsChanged = removed.Length > 0 || added.Length > 0 || sizesChanged.Length > 0;
    }

    /// <summary>Get a snapshot diff if anything changed in the given data.</summary>
    /// <param name="added">The added item stacks.</param>
    /// <param name="removed">The removed item stacks.</param>
    /// <param name="stackSizes">The items with their previous stack sizes.</param>
    /// <param name="stackItemsToCheck">The items which may have stack changes, or <c>null</c> to check every item in <paramref name="stackSizes"/>.</param>
    /// <param name="additionalStackSizes">Additional changed items and their previous stack sizes, if any.</param>
    /// <param name="changes">The inventory changes, or <c>null</c> if nothing changed.</param>
    /// <returns>Returns whether anything changed.</returns>
    public static bool TryGetChanges(ISet<Item> added, ISet<Item> removed, IDictionary<Item, int> stackSizes, IEnumerable<Item>? stackItemsToCheck, IDictionary<Item, int>? additionalStackSizes, [NotNullWhen(true)] out SnapshotItemListDiff? changes)
    {
        // detect item stack size changes
        List<ItemStackSizeChange>? sizesChanges = null;
        IEnumerable<Item> itemsToCheck = stackItemsToCheck ?? stackSizes.Keys;
        foreach (Item item in itemsToCheck)
        {
            if (stackSizes.TryGetValue(item, out int oldStack) && item.Stack != oldStack)
            {
                sizesChanges ??= [];
                sizesChanges.Add(new ItemStackSizeChange(item, oldStack, item.Stack));
            }
        }
        if (additionalStackSizes != null)
        {
            foreach ((Item item, int oldStack) in additionalStackSizes)
            {
                if (item.Stack != oldStack)
                {
                    sizesChanges ??= [];
                    sizesChanges.Add(new ItemStackSizeChange(item, oldStack, item.Stack));
                }
            }
        }

        // build diff if needed
        if (added.Count > 0 || removed.Count > 0 || sizesChanges != null)
        {
            changes = new SnapshotItemListDiff(
                added: added.ToArray(),
                removed: removed.ToArray(),
                sizesChanged: sizesChanges?.ToArray() ?? []
            );
            return true;
        }

        changes = null;
        return false;
    }
}
