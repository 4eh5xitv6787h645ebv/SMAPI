using SMAPI.PerformanceBenchmarks.Framework;
using StardewModdingAPI.Framework.StateTracking;

namespace SMAPI.PerformanceBenchmarks;

/// <summary>Measure the shared idle decision used by inventory and chest trackers.</summary>
internal sealed class InventoryChestIdleScenario : IPerformanceScenario
{
    /// <inheritdoc />
    public string Id => "tracking.inventory-chest-idle";

    /// <inheritdoc />
    public string Description => "Checks the allocation-free no-change state shared by inventory and chest tracking.";

    /// <inheritdoc />
    public void Setup()
    {
    }

    /// <inheritdoc />
    public ulong Execute(int operations)
    {
        ulong digest = ScenarioDigest.Offset;
        for (int index = 0; index < operations; index++)
        {
            bool idle = InventoryTrackerHotPath.HasChanges(isTracking: true, addedCount: 0, removedCount: 0, dirtyStackCount: 0, eventStackCount: 0);
            bool disabled = InventoryTrackerHotPath.HasChanges(isTracking: false, addedCount: 1, removedCount: 1, dirtyStackCount: 1, eventStackCount: 1);
            bool changed = InventoryTrackerHotPath.HasChanges(isTracking: true, addedCount: index & 1, removedCount: 0, dirtyStackCount: 0, eventStackCount: 0);
            digest = ScenarioDigest.Add(digest, idle ? 1UL : 0UL);
            digest = ScenarioDigest.Add(digest, disabled ? 1UL : 0UL);
            digest = ScenarioDigest.Add(digest, changed ? 1UL : 0UL);
        }
        return digest;
    }

    /// <inheritdoc />
    public void Cleanup()
    {
    }
}
