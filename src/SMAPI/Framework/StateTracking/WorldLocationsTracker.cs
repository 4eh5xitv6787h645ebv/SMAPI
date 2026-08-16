using System.Collections.Generic;
using System.Collections.ObjectModel;
using Netcode;
using StardewModdingAPI.Framework.StateTracking.Comparers;
using StardewModdingAPI.Framework.StateTracking.FieldWatchers;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;

namespace StardewModdingAPI.Framework.StateTracking;

/// <summary>Detects changes to the game's locations.</summary>
internal class WorldLocationsTracker : IWatcher
{
    /*********
    ** Fields
    *********/
    /// <summary>Tracks changes to the location list.</summary>
    private readonly ICollectionWatcher<GameLocation> LocationListWatcher;

    /// <summary>Tracks changes to the list of active mine locations.</summary>
    private readonly ICollectionWatcher<MineShaft> MineLocationListWatcher;

    /// <summary>Tracks changes to the list of active volcano locations.</summary>
    private readonly ICollectionWatcher<GameLocation> VolcanoLocationListWatcher;

    /// <summary>A lookup of the tracked locations.</summary>
    private Dictionary<GameLocation, LocationTracker> LocationDict { get; } = new(new ObjectReferenceComparer<GameLocation>());

    /// <summary>The locations whose content changed since the last reset.</summary>
    private readonly HashSet<LocationTracker> LocationsWithContentChanges = new(new ObjectReferenceComparer<LocationTracker>());

    /// <summary>A stable buffer of changed locations used while topology changes are processed.</summary>
    private readonly List<LocationTracker> LocationsWithContentChangesBuffer = [];

    /// <summary>A lookup of registered buildings and their indoor location.</summary>
    private readonly Dictionary<Building, GameLocation?> BuildingIndoors = new(new ObjectReferenceComparer<Building>());

    /// <summary>The field-change handlers registered for each building's indoor location.</summary>
    private readonly Dictionary<Building, FieldChange<NetRef<GameLocation>, GameLocation>> BuildingIndoorsChangedHandlers = new(new ObjectReferenceComparer<Building>());

    /// <summary>The buildings whose indoor location changed since the last update.</summary>
    private readonly HashSet<Building> BuildingsWithChangedIndoors = new(new ObjectReferenceComparer<Building>());

    /// <summary>A pooled buffer used to process <see cref="BuildingsWithChangedIndoors"/> without being affected by nested location changes.</summary>
    private readonly List<Building> BuildingsWithChangedIndoorsBuffer = [];

    /// <summary>The backing field for <see cref="Added"/>.</summary>
    private readonly HashSet<GameLocation> AddedImpl = new(new ObjectReferenceComparer<GameLocation>());

    /// <summary>The backing field for <see cref="Removed"/>.</summary>
    private readonly HashSet<GameLocation> RemovedImpl = new(new ObjectReferenceComparer<GameLocation>());

    /// <summary>Whether chest inventory changes were tracked during the previous update.</summary>
    private bool IsTrackingChestInventoryChanges;


    /*********
    ** Accessors
    *********/
    /// <inheritdoc />
    public string Name => nameof(WorldLocationsTracker);

    /// <summary>Whether locations were added or removed since the last reset.</summary>
    public bool IsLocationListChanged { get; private set; }

    /// <summary>Whether any tracked location has collection changes since the last reset.</summary>
    public bool HaveLocationContentsChanged => this.LocationsWithContentChanges.Count > 0;

    /// <inheritdoc />
    public bool IsChanged => this.IsLocationListChanged || this.HaveLocationContentsChanged;

    /// <summary>The tracked locations.</summary>
    public IReadOnlyCollection<LocationTracker> Locations => this.LocationDict.Values;

    /// <summary>The tracked locations whose content changed since the last reset.</summary>
    public IReadOnlyCollection<LocationTracker> ChangedLocations => this.LocationsWithContentChanges;

    /// <summary>Whether to track changes needed for the chest inventory event.</summary>
    public bool TrackChestInventoryChanges { get; set; } = true;

    /// <summary>The locations added since the last update.</summary>
    public IReadOnlySet<GameLocation> Added => this.AddedImpl;

    /// <summary>The locations removed since the last update.</summary>
    public IReadOnlySet<GameLocation> Removed => this.RemovedImpl;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="locations">The game's list of locations.</param>
    /// <param name="activeMineLocations">The game's list of active mine locations.</param>
    /// <param name="activeVolcanoLocations">The game's list of active volcano locations.</param>
    public WorldLocationsTracker(ObservableCollection<GameLocation> locations, IList<MineShaft> activeMineLocations, IList<VolcanoDungeon> activeVolcanoLocations)
    {
        this.LocationListWatcher = WatcherFactory.ForObservableCollection($"{this.Name}.{nameof(locations)}", locations);
        this.MineLocationListWatcher = WatcherFactory.ForReferenceList($"{this.Name}.{nameof(activeMineLocations)}", activeMineLocations);
        this.VolcanoLocationListWatcher = WatcherFactory.ForReferenceList($"{this.Name}.{nameof(activeVolcanoLocations)}", activeVolcanoLocations);
    }

    /// <inheritdoc />
    public void Update()
    {
        this.IsLocationListChanged = false;

        this.LocationsWithContentChangesBuffer.Clear();
        this.LocationsWithContentChangesBuffer.AddRange(this.LocationsWithContentChanges);

        // update watchers
        this.LocationListWatcher.Update();
        this.MineLocationListWatcher.Update();
        this.VolcanoLocationListWatcher.Update();

        // update location content watchers. Chest stack changes aren't event-driven, so they still
        // require every location while observed; otherwise only dirty locations need processing.
        bool updateAllLocations = this.TrackChestInventoryChanges || this.IsTrackingChestInventoryChanges != this.TrackChestInventoryChanges;
        if (updateAllLocations)
        {
            foreach (LocationTracker watcher in this.Locations)
                watcher.Update(this.TrackChestInventoryChanges);
        }
        else
        {
            foreach (LocationTracker watcher in this.LocationsWithContentChangesBuffer)
                watcher.Update(trackChestInventoryChanges: false);
        }
        this.IsTrackingChestInventoryChanges = this.TrackChestInventoryChanges;

        // detect added/removed locations
        // Process every removal before any addition so moving a location between source lists in
        // add-then-remove order can't leave the newly added destination untracked.
        if (this.LocationListWatcher.IsChanged)
            this.Remove(this.LocationListWatcher.Removed);
        if (this.MineLocationListWatcher.IsChanged)
            this.Remove(this.MineLocationListWatcher.Removed);
        if (this.VolcanoLocationListWatcher.IsChanged)
            this.Remove(this.VolcanoLocationListWatcher.Removed);

        if (this.LocationListWatcher.IsChanged)
            this.Add(this.LocationListWatcher.Added);
        if (this.MineLocationListWatcher.IsChanged)
            this.Add(this.MineLocationListWatcher.Added);
        if (this.VolcanoLocationListWatcher.IsChanged)
            this.Add(this.VolcanoLocationListWatcher.Added);

        // detect building changes
        // As with top-level locations, remove across every parent before adding to any parent. A
        // building may be inserted into its destination before its source collection is updated.
        foreach (LocationTracker watcher in this.LocationsWithContentChangesBuffer)
        {
            if (
                !watcher.BuildingsWatcher.IsChanged
                || !this.LocationDict.TryGetValue(watcher.Location, out LocationTracker? currentWatcher)
                || !object.ReferenceEquals(watcher, currentWatcher)
            )
                continue;

            this.Remove(watcher.BuildingsWatcher.Removed);
        }
        foreach (LocationTracker watcher in this.LocationsWithContentChangesBuffer)
        {
            if (
                !watcher.BuildingsWatcher.IsChanged
                || !this.LocationDict.TryGetValue(watcher.Location, out LocationTracker? currentWatcher)
                || !object.ReferenceEquals(watcher, currentWatcher)
            )
                continue;

            this.Add(watcher.BuildingsWatcher.Added);
        }

        // detect building interiors changed (e.g. construction completed)
        this.BuildingsWithChangedIndoorsBuffer.Clear();
        this.BuildingsWithChangedIndoorsBuffer.AddRange(this.BuildingsWithChangedIndoors);
        this.BuildingsWithChangedIndoors.Clear();
        foreach (Building building in this.BuildingsWithChangedIndoorsBuffer)
        {
            if (!this.BuildingIndoors.TryGetValue(building, out GameLocation? oldIndoors))
                continue;

            GameLocation? newIndoors = building.indoors.Value;
            if (object.ReferenceEquals(oldIndoors, newIndoors))
                continue;

            this.Remove(oldIndoors);
            this.Add(newIndoors);

            this.BuildingIndoors[building] = newIndoors;
        }
    }

    /// <summary>Set the current location list as the baseline.</summary>
    public void ResetLocationList()
    {
        this.RemovedImpl.Clear();
        this.AddedImpl.Clear();

        this.LocationListWatcher.Reset();
        this.MineLocationListWatcher.Reset();
        this.VolcanoLocationListWatcher.Reset();

        this.IsLocationListChanged = false;
    }

    /// <inheritdoc />
    public void Reset()
    {
        this.ResetLocationList();

        if (this.IsTrackingChestInventoryChanges)
        {
            foreach (LocationTracker watcher in this.Locations)
                watcher.Reset();
        }
        else
        {
            foreach (LocationTracker watcher in this.LocationsWithContentChanges)
                watcher.Reset();
        }
        this.LocationsWithContentChanges.Clear();
    }

    /// <summary>Get whether the given location is tracked.</summary>
    /// <param name="location">The location to check.</param>
    public bool HasLocationTracker(GameLocation location)
    {
        return this.LocationDict.ContainsKey(location);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        this.LocationListWatcher.Dispose();
        this.MineLocationListWatcher.Dispose();
        this.VolcanoLocationListWatcher.Dispose();

        foreach ((Building building, FieldChange<NetRef<GameLocation>, GameLocation> handler) in this.BuildingIndoorsChangedHandlers)
        {
            building.indoors.fieldChangeEvent -= handler;
            building.indoors.fieldChangeVisibleEvent -= handler;
        }
        this.BuildingIndoorsChangedHandlers.Clear();
        this.BuildingsWithChangedIndoors.Clear();
        this.LocationsWithContentChanges.Clear();

        foreach (LocationTracker watcher in this.Locations)
            watcher.Dispose();
    }


    /*********
    ** Private methods
    *********/
    /****
    ** Enumerable wrappers
    ****/
    /// <summary>Add the given buildings.</summary>
    /// <param name="buildings">The buildings to add.</param>
    public void Add(IEnumerable<Building> buildings)
    {
        foreach (Building building in buildings)
            this.Add(building);
    }

    /// <summary>Add the given locations.</summary>
    /// <param name="locations">The locations to add.</param>
    public void Add(IEnumerable<GameLocation> locations)
    {
        foreach (GameLocation location in locations)
            this.Add(location);
    }

    /// <summary>Remove the given buildings.</summary>
    /// <param name="buildings">The buildings to remove.</param>
    public void Remove(IEnumerable<Building> buildings)
    {
        foreach (Building building in buildings)
            this.Remove(building);
    }

    /// <summary>Remove the given locations.</summary>
    /// <param name="locations">The locations to remove.</param>
    public void Remove(IEnumerable<GameLocation> locations)
    {
        foreach (GameLocation location in locations)
            this.Remove(location);
    }

    /****
    ** Main add/remove logic
    ****/
    /// <summary>Add the given building.</summary>
    /// <param name="building">The building to add.</param>
    public void Add(Building? building)
    {
        if (building == null || this.BuildingIndoors.ContainsKey(building))
            return;

        GameLocation? indoors = building.indoors.Value;
        this.BuildingIndoors[building] = indoors;

        FieldChange<NetRef<GameLocation>, GameLocation> handler = (_, _, _) => this.BuildingsWithChangedIndoors.Add(building);
        this.BuildingIndoorsChangedHandlers[building] = handler;
        building.indoors.fieldChangeEvent += handler;
        building.indoors.fieldChangeVisibleEvent += handler;

        this.Add(indoors);
    }

    /// <summary>Add the given location.</summary>
    /// <param name="location">The location to add.</param>
    public void Add(GameLocation? location)
    {
        if (location == null)
            return;

        // remove old location if needed
        this.Remove(location);

        // add location
        this.AddedImpl.Add(location);
        this.LocationDict[location] = new LocationTracker(location, this.OnLocationContentChanged);

        // add buildings
        this.Add(location.buildings);

        this.IsLocationListChanged = true;
    }

    /// <summary>Remove the given building.</summary>
    /// <param name="building">The building to remove.</param>
    public void Remove(Building? building)
    {
        if (building == null)
            return;

        if (this.BuildingIndoorsChangedHandlers.Remove(building, out FieldChange<NetRef<GameLocation>, GameLocation>? handler))
        {
            building.indoors.fieldChangeEvent -= handler;
            building.indoors.fieldChangeVisibleEvent -= handler;
        }
        this.BuildingsWithChangedIndoors.Remove(building);

        if (this.BuildingIndoors.Remove(building, out GameLocation? indoors))
            this.Remove(indoors);
    }

    /// <summary>Remove the given location.</summary>
    /// <param name="location">The location to remove.</param>
    public void Remove(GameLocation? location)
    {
        if (location == null)
            return;

        if (this.LocationDict.TryGetValue(location, out LocationTracker? watcher))
        {
            // track change
            this.RemovedImpl.Add(location);

            // remove
            this.LocationDict.Remove(location);
            this.LocationsWithContentChanges.Remove(watcher);
            watcher.Dispose();
            this.Remove(location.buildings);

            this.IsLocationListChanged = true;
        }
    }

    /// <summary>Mark a location as having content changes.</summary>
    /// <param name="watcher">The changed location tracker.</param>
    private void OnLocationContentChanged(LocationTracker watcher)
    {
        this.LocationsWithContentChanges.Add(watcher);
    }
}
