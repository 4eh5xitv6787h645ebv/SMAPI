namespace StardewModdingAPI.Framework.StateTracking;

/// <summary>Shared allocation-free decisions for inventory and chest change tracking.</summary>
internal static class InventoryTrackerHotPath
{
    /// <summary>Get whether an enabled tracker has any recorded change to materialize.</summary>
    public static bool HasChanges(bool isTracking, int addedCount, int removedCount, int dirtyStackCount, int eventStackCount)
    {
        return isTracking && (addedCount != 0 || removedCount != 0 || dirtyStackCount != 0 || eventStackCount != 0);
    }
}
