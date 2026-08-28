using System;
using System.Collections.Generic;
using SMAPI.PerformanceBenchmarks.Framework;
using StardewModdingAPI.Framework.StateTracking;

namespace SMAPI.PerformanceBenchmarks;

/// <summary>Measure the shared idle decision used by inventory and chest trackers.</summary>
internal sealed class InventoryChestIdleScenario : IPerformanceScenario
{
    private static readonly Func<SyntheticItem, int> GetStackSize = static item => item.StackSize;
    private static readonly Action<SyntheticItem> UntrackStack = static _ => { };

    private SyntheticTrackerState[] Trackers = Array.Empty<SyntheticTrackerState>();

    /// <inheritdoc />
    public string Id => "tracking.inventory-chest-idle";

    /// <inheritdoc />
    public string Description => "Runs the production steady-state transition, diff, and reset core for an inventory and 32 chests.";

    /// <inheritdoc />
    public void Setup()
    {
        this.Trackers = new SyntheticTrackerState[33];
        for (int trackerIndex = 0; trackerIndex < this.Trackers.Length; trackerIndex++)
        {
            SyntheticTrackerState tracker = new(includeRemovedStackChanges: trackerIndex == 0);
            if (!InventoryTrackerHotPath.TrySetTracking(ref tracker.IsTracking, requestedTracking: true))
                throw new InvalidOperationException("The synthetic tracker didn't enter its initial tracking state.");
            this.Trackers[trackerIndex] = tracker;
        }
    }

    /// <inheritdoc />
    public ulong Execute(int operations)
    {
        ulong digest = ScenarioDigest.Offset;
        for (int operation = 0; operation < operations; operation++)
        {
            bool changed = false;
            foreach (SyntheticTrackerState tracker in this.Trackers)
            {
                bool transitioned = InventoryTrackerHotPath.TrySetTracking(ref tracker.IsTracking, requestedTracking: true);
                changed |= transitioned;
                changed |= InventoryTrackerHotPath.PrepareDiff(
                    tracker.IncludeRemovedStackChanges,
                    tracker.IsTracking,
                    tracker.Added,
                    tracker.Removed,
                    tracker.DirtyStacks,
                    tracker.EventStackSizes
                );
                InventoryTrackerHotPath.Reset(
                    tracker.StackSizes,
                    tracker.EventStackSizes,
                    tracker.DirtyStacks,
                    tracker.Added,
                    tracker.Removed,
                    tracker.BaselineItems,
                    InventoryChestIdleScenario.GetStackSize,
                    InventoryChestIdleScenario.UntrackStack
                );
            }

            digest = ScenarioDigest.Add(digest, changed ? 1UL : 0UL);
            digest = ScenarioDigest.Add(digest, (ulong)this.Trackers.Length);
        }
        return digest;
    }

    /// <inheritdoc />
    public void Cleanup()
    {
        this.Trackers = Array.Empty<SyntheticTrackerState>();
    }

    /// <summary>One game-independent state bundle with the same concrete collections as the production tracker.</summary>
    private sealed class SyntheticTrackerState
    {
        public bool IncludeRemovedStackChanges { get; }
        public bool IsTracking;
        public Dictionary<SyntheticItem, int> StackSizes { get; } = [];
        public Dictionary<SyntheticItem, int> EventStackSizes { get; } = [];
        public HashSet<SyntheticItem> DirtyStacks { get; } = [];
        public HashSet<SyntheticItem> Added { get; } = [];
        public HashSet<SyntheticItem> Removed { get; } = [];
        public HashSet<SyntheticItem> BaselineItems { get; } = [];

        public SyntheticTrackerState(bool includeRemovedStackChanges)
        {
            this.IncludeRemovedStackChanges = includeRemovedStackChanges;
        }
    }

    /// <summary>A minimal synthetic item used only by the game-independent tracker core.</summary>
    private sealed record SyntheticItem(int StackSize);
}
