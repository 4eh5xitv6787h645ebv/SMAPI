using System.Collections.Generic;
using System.Collections.ObjectModel;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.StateTracking;
using StardewModdingAPI.Framework.StateTracking.Snapshots;
using StardewValley;
using StardewValley.Locations;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for <see cref="WorldLocationsSnapshot"/>.</summary>
[TestFixture]
internal class WorldLocationsSnapshotTests
{
    /*********
    ** Unit tests
    *********/
    [Test(Description = "Assert that location additions and removals keep their direction when copied into a snapshot.")]
    public void Update_PreservesLocationListChangeDirection()
    {
        // arrange
        ObservableCollection<GameLocation> locations = [];
        using WorldLocationsTracker tracker = new(locations, new List<MineShaft>(), new List<VolcanoDungeon>());
        WorldLocationsSnapshot snapshot = new();
        WorldSnapshotOptions options = new(
            TrackLocationList: true,
            TrackBuildings: false,
            TrackDebris: false,
            TrackLargeTerrainFeatures: false,
            TrackNpcs: false,
            TrackObjects: false,
            TrackChestInventories: false,
            TrackTerrainFeatures: false,
            TrackFurniture: false
        );
        GameLocation location = new();

        // act: add location
        locations.Add(location);
        tracker.Update();
        snapshot.Update(tracker, options);

        // assert: add location
        snapshot.LocationList.Added.Should().ContainSingle().Which.Should().BeSameAs(location);
        snapshot.LocationList.Removed.Should().BeEmpty();

        // act: remove location
        tracker.Reset();
        locations.Remove(location);
        tracker.Update();
        snapshot.Update(tracker, options);

        // assert: remove location
        snapshot.LocationList.Added.Should().BeEmpty();
        snapshot.LocationList.Removed.Should().ContainSingle().Which.Should().BeSameAs(location);
    }
}
