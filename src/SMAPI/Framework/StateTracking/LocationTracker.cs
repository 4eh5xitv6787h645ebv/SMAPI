using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI.Framework.StateTracking.FieldWatchers;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;

namespace StardewModdingAPI.Framework.StateTracking;

/// <summary>Tracks changes to a location's data.</summary>
internal class LocationTracker : IWatcher
{
    /*********
    ** Fields
    *********/
    /// <summary>The underlying watchers.</summary>
    private readonly List<IWatcher> Watchers = [];

    /// <summary>Whether chest inventory changes are currently being tracked.</summary>
    private bool IsTrackingChestInventoryChanges;

    /// <summary>Notify the world tracker when this location first changes after a reset.</summary>
    private readonly Action<LocationTracker>? OnChanged;

    /// <summary>The chest trackers which changed since the last reset.</summary>
    private readonly HashSet<ChestTracker> ChangedChestWatchersImpl = [];

    /// <summary>Chest trackers with custom item stack implementations which need polling.</summary>
    private readonly HashSet<ChestTracker> PolledChestWatchers = [];


    /*********
    ** Accessors
    *********/
    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    /// <remarks>This covers added/removed chests in the location, but *not* changes to chest inventories; see <see cref="ChestWatchers"/> for those.</remarks>
    public bool IsChanged { get; private set; }

    /// <summary>The tracked location.</summary>
    public GameLocation Location { get; }

    /// <summary>Tracks added or removed buildings.</summary>
    public ICollectionWatcher<Building> BuildingsWatcher { get; }

    /// <summary>Tracks added or removed debris.</summary>
    public ICollectionWatcher<Debris> DebrisWatcher { get; }

    /// <summary>Tracks added or removed large terrain features.</summary>
    public ICollectionWatcher<LargeTerrainFeature> LargeTerrainFeaturesWatcher { get; }

    /// <summary>Tracks added or removed NPCs.</summary>
    public ICollectionWatcher<NPC> NpcsWatcher { get; }

    /// <summary>Tracks added or removed objects.</summary>
    public IDictionaryWatcher<Vector2, SObject> ObjectsWatcher { get; }

    /// <summary>Tracks added or removed terrain features.</summary>
    public IDictionaryWatcher<Vector2, TerrainFeature> TerrainFeaturesWatcher { get; }

    /// <summary>Tracks added or removed furniture.</summary>
    public ICollectionWatcher<Furniture> FurnitureWatcher { get; }

    /// <summary>Tracks items added or removed to chests.</summary>
    public IDictionary<Vector2, ChestTracker> ChestWatchers { get; } = new Dictionary<Vector2, ChestTracker>();

    /// <summary>The chest trackers which changed since the last reset.</summary>
    public IReadOnlyCollection<ChestTracker> ChangedChestWatchers => this.ChangedChestWatchersImpl;

    /// <summary>Whether any chest contains a custom item stack implementation which needs polling.</summary>
    public bool RequiresChestStackPolling => this.PolledChestWatchers.Count > 0;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="location">The location to track.</param>
    /// <param name="onChanged">Notify the world tracker when this location first changes after a reset.</param>
    public LocationTracker(GameLocation location, Action<LocationTracker>? onChanged = null)
    {
        this.Name = $"Locations.{location.NameOrUniqueName}";
        this.Location = location;
        this.OnChanged = onChanged;

        // init watchers
        this.BuildingsWatcher = WatcherFactory.ForNetCollection($"{this.Name}.{nameof(location.buildings)}", location.buildings, this.OnContentChanged);
        this.DebrisWatcher = WatcherFactory.ForNetCollection($"{this.Name}.{nameof(location.debris)}", location.debris, this.OnContentChanged);
        this.LargeTerrainFeaturesWatcher = WatcherFactory.ForNetCollection($"{this.Name}.{nameof(location.largeTerrainFeatures)}", location.largeTerrainFeatures, this.OnContentChanged);
        this.NpcsWatcher = WatcherFactory.ForNetCollection($"{this.Name}.{nameof(location.characters)}", location.characters, this.OnContentChanged);
        this.ObjectsWatcher = WatcherFactory.ForNetDictionary($"{this.Name}.{nameof(location.netObjects)}", location.netObjects, this.OnContentChanged);
        this.TerrainFeaturesWatcher = WatcherFactory.ForNetDictionary($"{this.Name}.{nameof(location.terrainFeatures)}", location.terrainFeatures, this.OnContentChanged);
        this.FurnitureWatcher = WatcherFactory.ForNetCollection($"{this.Name}.{nameof(location.furniture)}", location.furniture, this.OnContentChanged);

        this.Watchers.AddRange([
            this.BuildingsWatcher,
            this.DebrisWatcher,
            this.LargeTerrainFeaturesWatcher,
            this.NpcsWatcher,
            this.ObjectsWatcher,
            this.TerrainFeaturesWatcher,
            this.FurnitureWatcher
        ]);

        this.UpdateChestWatcherList(added: location.Objects.Pairs, removed: []);
    }

    /// <inheritdoc />
    public void Update()
    {
        this.Update(trackChestInventoryChanges: true);
    }

    /// <summary>Update the current values.</summary>
    /// <param name="trackChestInventoryChanges">Whether to track changes needed for the chest inventory event.</param>
    public void Update(bool trackChestInventoryChanges)
    {
        // add/remove chests to match
        if (this.ObjectsWatcher.IsChanged)
            this.UpdateChestWatcherList(added: this.ObjectsWatcher.Added, removed: this.ObjectsWatcher.Removed);

        // Toggle every chest watcher when listener state changes. Otherwise process only chests which
        // pushed a structural or stack notification since the last reset.
        if (this.IsTrackingChestInventoryChanges != trackChestInventoryChanges)
        {
            this.IsTrackingChestInventoryChanges = trackChestInventoryChanges;
            foreach (ChestTracker watcher in this.ChestWatchers.Values)
            {
                watcher.Update(trackChestInventoryChanges);
                this.UpdatePollingState(watcher);
            }
        }
        else if (trackChestInventoryChanges)
        {
            foreach (ChestTracker watcher in this.ChangedChestWatchersImpl)
            {
                watcher.Update(trackInventoryChanges: true);
                this.UpdatePollingState(watcher);
            }
        }
    }

    /// <summary>Poll only custom item stack implementations which can't use the game's stack field events.</summary>
    public void UpdatePolledChestStacks()
    {
        if (!this.IsTrackingChestInventoryChanges)
            return;

        foreach (ChestTracker watcher in this.PolledChestWatchers)
            watcher.UpdatePolledStacks();
    }

    /// <inheritdoc />
    public void Reset()
    {
        this.IsChanged = false;

        foreach (IWatcher watcher in this.Watchers)
            watcher.Reset();

        foreach (ChestTracker watcher in this.ChangedChestWatchersImpl)
            watcher.Reset();
        this.ChangedChestWatchersImpl.Clear();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (IWatcher watcher in this.Watchers)
            watcher.Dispose();

        foreach (ChestTracker watcher in this.ChestWatchers.Values)
            watcher.Dispose();
        this.ChangedChestWatchersImpl.Clear();
        this.PolledChestWatchers.Clear();
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Mark the location content as changed when an underlying net collection changes.</summary>
    private void OnContentChanged()
    {
        if (!this.IsChanged)
        {
            this.IsChanged = true;
            this.OnChanged?.Invoke(this);
        }
    }

    /// <summary>Update the watcher list for added or removed chests.</summary>
    /// <param name="added">The objects added to the location.</param>
    /// <param name="removed">The objects removed from the location.</param>
    private void UpdateChestWatcherList(IEnumerable<KeyValuePair<Vector2, SObject>> added, IEnumerable<KeyValuePair<Vector2, SObject>> removed)
    {
        // remove unused watchers
        foreach ((Vector2 tile, SObject? obj) in removed)
        {
            if (obj is Chest && this.ChestWatchers.TryGetValue(tile, out ChestTracker? watcher))
            {
                watcher.Dispose();
                this.ChestWatchers.Remove(tile);
                this.ChangedChestWatchersImpl.Remove(watcher);
                this.PolledChestWatchers.Remove(watcher);
            }
        }

        // add new watchers
        foreach ((Vector2 tile, SObject? obj) in added)
        {
            if (obj is Chest chest && !this.ChestWatchers.ContainsKey(tile))
            {
                ChestTracker watcher = new($"{this.Name}.chest({tile})", chest, this.OnChestChanged);
                this.ChestWatchers.Add(tile, watcher);
                if (this.IsTrackingChestInventoryChanges)
                {
                    watcher.Update(trackInventoryChanges: true);
                    this.UpdatePollingState(watcher);
                }
            }
        }
    }

    /// <summary>Mark a chest as changed and push this location into the world dirty set.</summary>
    private void OnChestChanged(ChestTracker watcher)
    {
        this.ChangedChestWatchersImpl.Add(watcher);
        this.OnChanged?.Invoke(this);
    }

    /// <summary>Update whether a chest needs custom-stack polling.</summary>
    private void UpdatePollingState(ChestTracker watcher)
    {
        if (watcher.RequiresStackPolling)
            this.PolledChestWatchers.Add(watcher);
        else
            this.PolledChestWatchers.Remove(watcher);
    }
}
