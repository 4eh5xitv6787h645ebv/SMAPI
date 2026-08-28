using System;
using System.Collections.Generic;

namespace StardewModdingAPI.Framework.StateTracking;

/// <summary>The game-independent steady-state core shared by inventory and chest change tracking.</summary>
internal static class InventoryTrackerHotPath
{
    /// <summary>Apply a tracking-state transition if needed.</summary>
    /// <returns>Returns whether the caller must subscribe or unsubscribe the game inventory.</returns>
    public static bool TrySetTracking(ref bool isTracking, bool requestedTracking)
    {
        if (isTracking == requestedTracking)
            return false;

        isTracking = requestedTracking;
        return true;
    }

    /// <summary>Apply chest-specific suppression and get whether a diff needs to be materialized.</summary>
    public static bool PrepareDiff<TItem>(
        bool includeRemovedStackChanges,
        bool isTracking,
        HashSet<TItem> added,
        HashSet<TItem> removed,
        HashSet<TItem> dirtyStacks,
        Dictionary<TItem, int> eventStackSizes
    )
        where TItem : notnull
    {
        if (!includeRemovedStackChanges)
        {
            foreach (TItem item in removed)
            {
                dirtyStacks.Remove(item);
                eventStackSizes.Remove(item);
            }
        }

        return isTracking && (added.Count != 0 || removed.Count != 0 || dirtyStacks.Count != 0 || eventStackSizes.Count != 0);
    }

    /// <summary>Advance transient tracker state to the next baseline.</summary>
    public static void Reset<TItem>(
        Dictionary<TItem, int> stackSizes,
        Dictionary<TItem, int> eventStackSizes,
        HashSet<TItem> dirtyStacks,
        HashSet<TItem> added,
        HashSet<TItem> removed,
        HashSet<TItem> baselineItems,
        Func<TItem, int> getStackSize,
        Action<TItem> untrackStack
    )
        where TItem : notnull
    {
        foreach (TItem item in dirtyStacks)
        {
            if (stackSizes.ContainsKey(item))
                stackSizes[item] = getStackSize(item);
        }
        foreach (TItem item in added)
        {
            if (stackSizes.ContainsKey(item))
                stackSizes[item] = getStackSize(item);
        }

        foreach (TItem item in removed)
        {
            untrackStack(item);
            baselineItems.Remove(item);
        }
        foreach (TItem item in added)
            baselineItems.Add(item);

        eventStackSizes.Clear();
        dirtyStacks.Clear();
        added.Clear();
        removed.Clear();
    }
}
