using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI.Enums;
using StardewValley;

namespace StardewModdingAPI.Framework.StateTracking.Snapshots;

/// <summary>A frozen snapshot of a tracked player.</summary>
internal class PlayerSnapshot
{
    /*********
    ** Fields
    *********/
    /// <summary>An empty item list diff.</summary>
    private readonly SnapshotItemListDiff EmptyItemListDiff = new([], [], []);

    /// <summary>The skills with changes captured in the current snapshot.</summary>
    private readonly List<SkillType> ChangedSkillsImpl = [];


    /*********
    ** Accessors
    *********/
    /// <summary>The player being tracked.</summary>
    public Farmer Player { get; }

    /// <summary>The player's current location.</summary>
    public SnapshotDiff<GameLocation> Location { get; } = new();

    /// <summary>Tracks changes to the player's skill levels.</summary>
    public IDictionary<SkillType, SnapshotDiff<int>> Skills { get; } =
        Enum
            .GetValues<SkillType>()
            .ToDictionary(skill => skill, _ => new SnapshotDiff<int>());

    /// <summary>The skills with changes captured in the current snapshot, in event dispatch order.</summary>
    public IReadOnlyList<SkillType> ChangedSkills => this.ChangedSkillsImpl;

    /// <summary>Get a list of inventory changes.</summary>
    public SnapshotItemListDiff Inventory { get; private set; }


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="player">The player being tracked.</param>
    public PlayerSnapshot(Farmer player)
    {
        this.Player = player;
        this.Inventory = this.EmptyItemListDiff;
    }

    /// <summary>Update the tracked values.</summary>
    /// <param name="watcher">The player watcher to snapshot.</param>
    public void Update(PlayerTracker watcher)
    {
        this.Location.Update(watcher.LocationWatcher!);

        // Clear diffs copied on the previous tick, then copy only fields which changed now. Updating an
        // overlapping field twice is harmless and keeps the unchanged path free of skill traversal.
        foreach (SkillType skill in this.ChangedSkillsImpl)
            this.Skills[skill].Update(watcher.SkillWatchers[skill]);
        this.ChangedSkillsImpl.Clear();
        foreach (SkillType skill in watcher.ChangedSkills)
        {
            this.Skills[skill].Update(watcher.SkillWatchers[skill]);
            this.ChangedSkillsImpl.Add(skill);
        }

        this.Inventory = watcher.TryGetInventoryChanges(out SnapshotItemListDiff? itemChanges)
            ? itemChanges
            : this.EmptyItemListDiff;
    }
}
