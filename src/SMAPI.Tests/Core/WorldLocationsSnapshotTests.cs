using System.Collections.Generic;
using System.Collections.ObjectModel;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.StateTracking;
using StardewModdingAPI.Framework.StateTracking.Snapshots;
using StardewValley;
using StardewValley.Buildings;
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

    [Test(Description = "Assert that building indoor locations are updated from net-field notifications and coalesced at the update boundary.")]
    public void Update_TracksChangedBuildingInteriors()
    {
        // arrange
        ObservableCollection<GameLocation> locations = [];
        using WorldLocationsTracker tracker = new(locations, new List<MineShaft>(), new List<VolcanoDungeon>());
        Building building = new();
        tracker.Add(building);
        tracker.Reset();

        // act: assign the first indoor location
        GameLocation firstIndoors = new();
        building.indoors.Value = firstIndoors;
        tracker.Update();

        // assert: first indoor location
        tracker.Added.Should().ContainSingle().Which.Should().BeSameAs(firstIndoors);
        tracker.Removed.Should().BeEmpty();

        // act: replace the indoor location
        tracker.Reset();
        GameLocation secondIndoors = new();
        building.indoors.Value = secondIndoors;
        tracker.Update();

        // assert: replacement
        tracker.Added.Should().ContainSingle().Which.Should().BeSameAs(secondIndoors);
        tracker.Removed.Should().ContainSingle().Which.Should().BeSameAs(firstIndoors);

        // act: change back and forth before the next tracker update
        tracker.Reset();
        building.indoors.Value = firstIndoors;
        building.indoors.Value = secondIndoors;
        tracker.Update();

        // assert: transient changes are coalesced to the final reference
        tracker.Added.Should().BeEmpty();
        tracker.Removed.Should().BeEmpty();
    }
}
