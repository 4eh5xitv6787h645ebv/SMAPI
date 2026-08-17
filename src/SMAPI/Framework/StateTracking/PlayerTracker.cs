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

    /// <summary>The skills which changed since the last reset, in event dispatch order.</summary>
    private readonly List<SkillType> ChangedSkillsImpl = [];


    /*********
    ** Accessors
    *********/
    /// <summary>The player being tracked.</summary>
    public Farmer Player { get; }

    /// <summary>The player's current location.</summary>
    public IValueWatcher<GameLocation?> LocationWatcher { get; }

    /// <summary>Tracks changes to the player's skill levels.</summary>
    public IDictionary<SkillType, IValueWatcher<int>> SkillWatchers { get; }

    /// <summary>The skills which changed since the last reset, in event dispatch order.</summary>
    public IReadOnlyList<SkillType> ChangedSkills => this.ChangedSkillsImpl;


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
            [SkillType.Combat] = WatcherFactory.ForNetValue($"player.{nameof(player.combatLevel)}", player.combatLevel, () => this.OnSkillChanged(SkillType.Combat)),
            [SkillType.Farming] = WatcherFactory.ForNetValue($"player.{nameof(player.farmingLevel)}", player.farmingLevel, () => this.OnSkillChanged(SkillType.Farming)),
            [SkillType.Fishing] = WatcherFactory.ForNetValue($"player.{nameof(player.fishingLevel)}", player.fishingLevel, () => this.OnSkillChanged(SkillType.Fishing)),
            [SkillType.Foraging] = WatcherFactory.ForNetValue($"player.{nameof(player.foragingLevel)}", player.foragingLevel, () => this.OnSkillChanged(SkillType.Foraging)),
            [SkillType.Luck] = WatcherFactory.ForNetValue($"player.{nameof(player.luckLevel)}", player.luckLevel, () => this.OnSkillChanged(SkillType.Luck)),
            [SkillType.Mining] = WatcherFactory.ForNetValue($"player.{nameof(player.miningLevel)}", player.miningLevel, () => this.OnSkillChanged(SkillType.Mining))
        };
    }

    /// <summary>Update the current values if needed.</summary>
    /// <param name="trackInventoryChanges">Whether to track changes needed for the player inventory event.</param>
    public void Update(bool trackInventoryChanges)
    {
        // Update the polled location once. Skill watchers are pushed by their net fields and don't need polling.
        this.LocationWatcher.Update();
        this.LastValidLocation = this.LocationWatcher.CurrentValue;

        // Normal items push slot and stack notifications. Retain per-tick polling only for mod items
        // which override Item.Stack and therefore don't use the game's observable stack field.
        this.InventoryTracker.Update(trackInventoryChanges);
        if (this.InventoryTracker.RequiresStackPolling)
            this.InventoryTracker.UpdatePolledStacks();
    }

    /// <summary>Reset all trackers so their current values are the baseline.</summary>
    public void Reset()
    {
        this.LocationWatcher.Reset();
        foreach (SkillType skill in this.ChangedSkillsImpl)
            this.SkillWatchers[skill].Reset();
        this.ChangedSkillsImpl.Clear();

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

        this.LocationWatcher.Dispose();
        foreach (IWatcher watcher in this.SkillWatchers.Values)
            watcher.Dispose();
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Mark a skill as changed, preserving the existing public event order.</summary>
    private void OnSkillChanged(SkillType skill)
    {
        PlayerTracker.AddChangedSkill(this.ChangedSkillsImpl, skill);
    }

    /// <summary>Add a changed skill to a list in public event dispatch order.</summary>
    /// <param name="skills">The changed skill list.</param>
    /// <param name="skill">The skill to add.</param>
    internal static void AddChangedSkill(List<SkillType> skills, SkillType skill)
    {
        int order = GetSkillOrder(skill);
        for (int i = 0; i < skills.Count; i++)
        {
            SkillType existing = skills[i];
            if (existing == skill)
                return;
            if (GetSkillOrder(existing) > order)
            {
                skills.Insert(i, skill);
                return;
            }
        }

        skills.Add(skill);

        static int GetSkillOrder(SkillType value)
        {
            return value switch
            {
                SkillType.Farming => 0,
                SkillType.Fishing => 1,
                SkillType.Foraging => 2,
                SkillType.Mining => 3,
                SkillType.Combat => 4,
                SkillType.Luck => 5,
                _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown skill type.")
            };
        }
    }
}
