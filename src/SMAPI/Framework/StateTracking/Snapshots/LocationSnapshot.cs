using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;

namespace StardewModdingAPI.Framework.StateTracking.Snapshots;

/// <summary>A frozen snapshot of a tracked game location.</summary>
internal class LocationSnapshot
{
    /*********
    ** Accessors
    *********/
    /// <summary>The tracked location.</summary>
    public GameLocation Location { get; }

    /// <summary>Tracks added or removed buildings.</summary>
    public SnapshotListDiff<Building> Buildings { get; } = new();

    /// <summary>Tracks added or removed debris.</summary>
    public SnapshotListDiff<Debris> Debris { get; } = new();

    /// <summary>Tracks added or removed large terrain features.</summary>
    public SnapshotListDiff<LargeTerrainFeature> LargeTerrainFeatures { get; } = new();

    /// <summary>Tracks added or removed NPCs.</summary>
    public SnapshotListDiff<NPC> Npcs { get; } = new();

    /// <summary>Tracks added or removed objects.</summary>
    public SnapshotListDiff<KeyValuePair<Vector2, Object>> Objects { get; } = new();

    /// <summary>Tracks added or removed terrain features.</summary>
    public SnapshotListDiff<KeyValuePair<Vector2, TerrainFeature>> TerrainFeatures { get; } = new();

    /// <summary>Tracks added or removed furniture.</summary>
    public SnapshotListDiff<Furniture> Furniture { get; } = new();

    /// <summary>Tracks changed chest inventories.</summary>
    public IDictionary<Chest, SnapshotItemListDiff> ChestItems { get; } = new Dictionary<Chest, SnapshotItemListDiff>();


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="location">The tracked location.</param>
    public LocationSnapshot(GameLocation location)
    {
        this.Location = location;
    }

    /// <summary>Update the tracked values.</summary>
    /// <param name="watcher">The watcher to snapshot.</param>
    /// <param name="options">Which world changes need to be copied into the snapshot.</param>
    /// <returns>Returns whether the snapshot contains any changes.</returns>
    public bool Update(LocationTracker watcher, WorldSnapshotOptions options)
    {
        // main lists
        this.Update(this.Buildings, watcher.BuildingsWatcher, options.TrackBuildings);
        this.Update(this.Debris, watcher.DebrisWatcher, options.TrackDebris);
        this.Update(this.LargeTerrainFeatures, watcher.LargeTerrainFeaturesWatcher, options.TrackLargeTerrainFeatures);
        this.Update(this.Npcs, watcher.NpcsWatcher, options.TrackNpcs);
        this.Update(this.Objects, watcher.ObjectsWatcher, options.TrackObjects);
        this.Update(this.TerrainFeatures, watcher.TerrainFeaturesWatcher, options.TrackTerrainFeatures);
        this.Update(this.Furniture, watcher.FurnitureWatcher, options.TrackFurniture);

        // chest inventories
        this.ChestItems.Clear();
        if (options.TrackChestInventories)
        {
            foreach (ChestTracker tracker in watcher.ChangedChestWatchers)
            {
                if (tracker.TryGetInventoryChanges(out SnapshotItemListDiff? changes))
                    this.ChestItems[tracker.Chest] = changes;
            }
        }

        return
            this.Buildings.IsChanged
            || this.Debris.IsChanged
            || this.LargeTerrainFeatures.IsChanged
            || this.Npcs.IsChanged
            || this.Objects.IsChanged
            || this.ChestItems.Count > 0
            || this.TerrainFeatures.IsChanged
            || this.Furniture.IsChanged;
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Update a collection snapshot if needed for the current tick.</summary>
    /// <typeparam name="T">The collection value type.</typeparam>
    /// <param name="snapshot">The snapshot to update.</param>
    /// <param name="watcher">The underlying collection watcher.</param>
    /// <param name="trackChanges">Whether the corresponding event has listeners.</param>
    private void Update<T>(SnapshotListDiff<T> snapshot, ICollectionWatcher<T> watcher, bool trackChanges)
    {
        if (trackChanges)
            snapshot.Update(watcher);
        else
            snapshot.Update(isChanged: false, removed: null, added: null);
    }
}
