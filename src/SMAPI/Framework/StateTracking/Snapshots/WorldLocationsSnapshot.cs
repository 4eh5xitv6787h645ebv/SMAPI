using System.Collections.Generic;
using StardewModdingAPI.Framework.StateTracking.Comparers;
using StardewValley;

namespace StardewModdingAPI.Framework.StateTracking.Snapshots;

/// <summary>A frozen snapshot of the tracked game locations.</summary>
internal class WorldLocationsSnapshot
{
    /*********
    ** Fields
    *********/
    /// <summary>A map of tracked locations.</summary>
    private readonly Dictionary<GameLocation, LocationSnapshot> LocationsDict = new(new ObjectReferenceComparer<GameLocation>());

    /// <summary>The pooled list instance for <see cref="GetMissingLocations"/>.</summary>
    private static readonly List<GameLocation> PooledMissingLocations = [];

    /// <summary>The locations with changes captured in the current snapshot.</summary>
    private readonly List<LocationSnapshot> ChangedLocations = [];


    /*********
    ** Accessors
    *********/
    /// <summary>Tracks changes to the location list.</summary>
    public SnapshotListDiff<GameLocation> LocationList { get; } = new();

    /// <summary>The locations with changes captured in the current snapshot.</summary>
    public IReadOnlyCollection<LocationSnapshot> Locations => this.ChangedLocations;


    /*********
    ** Public methods
    *********/
    /// <summary>Update the tracked values.</summary>
    /// <param name="watcher">The watcher to snapshot.</param>
    /// <param name="options">Which world changes need to be copied into the snapshot.</param>
    public void Update(WorldLocationsTracker watcher, WorldSnapshotOptions options)
    {
        this.ChangedLocations.Clear();

        // update location list
        if (options.TrackLocationList)
            this.LocationList.Update(watcher.IsLocationListChanged, removed: watcher.Removed, added: watcher.Added);
        else
            this.LocationList.Update(isChanged: false, removed: null, added: null);

        // remove missing cached snapshots when the tracked topology changes
        if (watcher.IsLocationListChanged && this.LocationsDict.Count > 0)
        {
            foreach (GameLocation key in this.GetMissingLocations(watcher))
                this.LocationsDict.Remove(key);
        }

        // skip location traversal when no corresponding event is subscribed or no relevant location changed
        if (!options.TrackLocationContents || !watcher.HaveLocationContentsChanged)
            return;

        foreach (LocationTracker locationWatcher in watcher.ChangedLocations)
        {
            if (!this.LocationsDict.TryGetValue(locationWatcher.Location, out LocationSnapshot? snapshot))
                this.LocationsDict[locationWatcher.Location] = snapshot = new LocationSnapshot(locationWatcher.Location);

            if (snapshot.Update(locationWatcher, options))
                this.ChangedLocations.Add(snapshot);
        }
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Get the watched locations which no longer exist in the world, if any.</summary>
    /// <param name="watcher">The location list tracker.</param>
    private List<GameLocation> GetMissingLocations(WorldLocationsTracker watcher)
    {
        List<GameLocation> list = WorldLocationsSnapshot.PooledMissingLocations;
        if (list.Count > 0)
            list.Clear();

        foreach (GameLocation location in this.LocationsDict.Keys)
        {
            if (!watcher.HasLocationTracker(location))
                list.Add(location);
        }

        return list;
    }
}
