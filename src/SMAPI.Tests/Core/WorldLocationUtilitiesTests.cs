using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.Utilities;
using StardewValley;
using StardewValley.Buildings;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for <see cref="WorldLocationUtilities"/>.</summary>
[TestFixture]
internal class WorldLocationUtilitiesTests
{
    [Test(Description = "Assert that overlapping live-location sources are reference-deduplicated in stable root order.")]
    public void GetLocations_DeduplicatesOverlappingRoots()
    {
        GameLocation first = new();
        GameLocation second = new();
        GameLocation third = new();
        GameLocation mine = new();
        GameLocation volcano = new();

        IReadOnlyList<WorldLocationUtilities.WorldLocationInfo> locations = WorldLocationUtilities.GetLocations(
            rootLocations: [first, second],
            loadedSaveLocations: [second, third],
            activeMineLocations: [third, mine],
            activeVolcanoLocations: [first, volcano],
            includeBuildingInteriors: true
        );

        locations.Should().SatisfyRespectively(
            value => value.Location.Should().BeSameAs(first),
            value => value.Location.Should().BeSameAs(second),
            value => value.Location.Should().BeSameAs(third),
            value => value.Location.Should().BeSameAs(mine),
            value => value.Location.Should().BeSameAs(volcano)
        );
        locations.Should().OnlyContain(value => value.ParentBuilding == null);
    }

    [Test(Description = "Assert that building interiors are traversed recursively and only after every root.")]
    public void GetLocations_TraversesNestedInteriorsRootFirst()
    {
        GameLocation root = CreateLocationForBuildingTests();
        GameLocation secondRoot = CreateLocationForBuildingTests();
        GameLocation firstInterior = CreateLocationForBuildingTests();
        GameLocation nestedInterior = CreateLocationForBuildingTests();
        Building firstBuilding = new() { indoors = { Value = firstInterior } };
        Building nestedBuilding = new() { indoors = { Value = nestedInterior } };
        root.buildings.Add(firstBuilding);
        firstInterior.buildings.Add(nestedBuilding);

        IReadOnlyList<WorldLocationUtilities.WorldLocationInfo> locations = WorldLocationUtilities.GetLocations(
            rootLocations: [root, secondRoot],
            loadedSaveLocations: null,
            activeMineLocations: null,
            activeVolcanoLocations: null,
            includeBuildingInteriors: true
        );

        locations.Should().SatisfyRespectively(
            value => value.Location.Should().BeSameAs(root),
            value => value.Location.Should().BeSameAs(secondRoot),
            value =>
            {
                value.Location.Should().BeSameAs(firstInterior);
                value.ParentBuilding.Should().BeSameAs(firstBuilding);
            },
            value =>
            {
                value.Location.Should().BeSameAs(nestedInterior);
                value.ParentBuilding.Should().BeSameAs(nestedBuilding);
            }
        );
    }

    [Test(Description = "Assert that an interior which is also a root is returned once while retaining every parent building.")]
    public void GetLocations_DeduplicatesInteriorsAndRetainsParents()
    {
        GameLocation root = CreateLocationForBuildingTests();
        GameLocation otherRoot = CreateLocationForBuildingTests();
        GameLocation sharedInterior = CreateLocationForBuildingTests();
        Building firstBuilding = new() { indoors = { Value = sharedInterior } };
        Building secondBuilding = new() { indoors = { Value = sharedInterior } };
        root.buildings.Add(firstBuilding);
        otherRoot.buildings.Add(secondBuilding);

        IReadOnlyList<WorldLocationUtilities.WorldLocationInfo> locations = WorldLocationUtilities.GetLocations(
            rootLocations: [root, otherRoot],
            loadedSaveLocations: [sharedInterior],
            activeMineLocations: null,
            activeVolcanoLocations: null,
            includeBuildingInteriors: true
        );

        locations.Should().HaveCount(3);
        locations[2].Location.Should().BeSameAs(sharedInterior);
        locations[2].ParentBuilding.Should().BeSameAs(firstBuilding);
        locations[2].AdditionalParentBuildings.Should().ContainSingle().Which.Should().BeSameAs(secondBuilding);
    }

    [Test(Description = "Assert that callers can exclude interiors without excluding generated root locations.")]
    public void GetLocations_CanExcludeBuildingInteriors()
    {
        GameLocation root = CreateLocationForBuildingTests();
        GameLocation interior = CreateLocationForBuildingTests();
        GameLocation generated = new();
        root.buildings.Add(new Building { indoors = { Value = interior } });

        IReadOnlyList<WorldLocationUtilities.WorldLocationInfo> locations = WorldLocationUtilities.GetLocations(
            rootLocations: [root],
            loadedSaveLocations: null,
            activeMineLocations: [generated],
            activeVolcanoLocations: null,
            includeBuildingInteriors: false
        );

        locations.Select(value => value.Location).Should().Equal(root, generated);
    }

    /// <summary>Create a raw location whose building list doesn't call uninitialized game-global callbacks.</summary>
    private static GameLocation CreateLocationForBuildingTests()
    {
        GameLocation location = new();
        Type collectionType = location.buildings.GetType();
        collectionType.GetField("OnValueAdded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(location.buildings, null);
        collectionType.GetField("OnValueRemoved", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(location.buildings, null);
        return location;
    }
}
