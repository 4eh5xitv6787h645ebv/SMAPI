namespace StardewModdingAPI.Framework.StateTracking.Snapshots;

/// <summary>Indicates which world changes need to be copied into a snapshot for the current tick.</summary>
internal readonly record struct WorldSnapshotOptions(
    bool TrackLocationList,
    bool TrackBuildings,
    bool TrackDebris,
    bool TrackLargeTerrainFeatures,
    bool TrackNpcs,
    bool TrackObjects,
    bool TrackChestInventories,
    bool TrackTerrainFeatures,
    bool TrackFurniture
)
{
    /// <summary>Whether to snapshot any changes within locations.</summary>
    public bool TrackLocationContents =>
        this.TrackBuildings
        || this.TrackDebris
        || this.TrackLargeTerrainFeatures
        || this.TrackNpcs
        || this.TrackObjects
        || this.TrackChestInventories
        || this.TrackTerrainFeatures
        || this.TrackFurniture;
}
