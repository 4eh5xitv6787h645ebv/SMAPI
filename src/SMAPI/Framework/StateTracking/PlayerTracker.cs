using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using StardewModdingAPI.Enums;
using StardewModdingAPI.Framework.StateTracking.FieldWatchers;
using StardewValley;

namespace StardewModdingAPI.Framework.StateTracking;

/// <summary>Tracks changes to a player's data.</summary>
internal class PlayerTracker : IDisposable
{
    /*********
    ** Fields
    *********/
    /// <summary>Tracks the player's inventory through slot and stack notifications.</summary>
    private readonly InventoryTracker InventoryTracker;

    /// <summary>The player's last valid location.</summary>
    private GameLocation? LastValidLocation;

    /// <summary>The underlying watchers.</summary>
    private readonly List<IWatcher> Watchers = [];


    /*********
    ** Accessors
    *********/
    /// <summary>The player being tracked.</summary>
    public Farmer Player { get; }

    /// <summary>The player's current location.</summary>
    public IValueWatcher<GameLocation?> LocationWatcher { get; }

    /// <summary>Tracks changes to the player's skill levels.</summary>
    public IDictionary<SkillType, IValueWatcher<int>> SkillWatchers { get; }


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="player">The player to track.</param>
    public PlayerTracker(Farmer player)
    {
        // init player data
        this.Player = player;
        this.InventoryTracker = new InventoryTracker(player.Items, includeRemovedStackChanges: true);

        // init trackers
        this.LocationWatcher = WatcherFactory.ForReference($"player.{nameof(player.currentLocation)}", this.GetCurrentLocation);
        this.SkillWatchers = new Dictionary<SkillType, IValueWatcher<int>>
        {
            [SkillType.Combat] = WatcherFactory.ForNetValue($"player.{nameof(player.combatLevel)}", player.combatLevel),
            [SkillType.Farming] = WatcherFactory.ForNetValue($"player.{nameof(player.farmingLevel)}", player.farmingLevel),
            [SkillType.Fishing] = WatcherFactory.ForNetValue($"player.{nameof(player.fishingLevel)}", player.fishingLevel),
            [SkillType.Foraging] = WatcherFactory.ForNetValue($"player.{nameof(player.foragingLevel)}", player.foragingLevel),
            [SkillType.Luck] = WatcherFactory.ForNetValue($"player.{nameof(player.luckLevel)}", player.luckLevel),
            [SkillType.Mining] = WatcherFactory.ForNetValue($"player.{nameof(player.miningLevel)}", player.miningLevel)
        };

        // track watchers for convenience
        this.Watchers.Add(this.LocationWatcher);
        this.Watchers.AddRange(this.SkillWatchers.Values);
    }

    /// <summary>Update the current values if needed.</summary>
    /// <param name="trackInventoryChanges">Whether to track changes needed for the player inventory event.</param>
    public void Update(bool trackInventoryChanges)
    {
        // update valid location
        this.LastValidLocation = this.GetCurrentLocation();

        // update watchers
        foreach (IWatcher watcher in this.Watchers)
            watcher.Update();

        // Normal items push slot and stack notifications. Retain per-tick polling only for mod items
        // which override Item.Stack and therefore don't use the game's observable stack field.
        this.InventoryTracker.Update(trackInventoryChanges);
        if (this.InventoryTracker.RequiresStackPolling)
            this.InventoryTracker.UpdatePolledStacks();
    }

    /// <summary>Reset all trackers so their current values are the baseline.</summary>
    public void Reset()
    {
        // reset watchers
        foreach (IWatcher watcher in this.Watchers)
            watcher.Reset();

        this.InventoryTracker.Reset();
    }

    /// <summary>Get the player's current location, ignoring temporary null values.</summary>
    /// <remarks>The game will set <see cref="Character.currentLocation"/> to null in some cases, e.g. when they're a secondary player in multiplayer and transition to a location that hasn't been synced yet. While that's happening, this returns the player's last valid location instead.</remarks>
    public GameLocation? GetCurrentLocation()
    {
        return this.Player.currentLocation ?? this.LastValidLocation;
    }

    /// <summary>Get the inventory changes since the last update, if anything changed.</summary>
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

        foreach (IWatcher watcher in this.Watchers)
            watcher.Dispose();
    }
}
