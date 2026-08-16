using System.Collections.Generic;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;

namespace StardewModdingAPI.Framework.Utilities;

/// <summary>Provides a consistent view of the live game-location topology.</summary>
internal static class WorldLocationUtilities
{
    /// <summary>Get the live locations in deterministic root-first order, de-duplicated by reference.</summary>
    /// <param name="includeBuildingInteriors">Whether to recursively include constructable-building interiors.</param>
    internal static IReadOnlyList<WorldLocationInfo> GetLocations(bool includeBuildingInteriors = true)
    {
        return WorldLocationUtilities.GetLocations(
            rootLocations: Game1.locations,
            loadedSaveLocations: SaveGame.loaded?.locations,
            activeMineLocations: MineShaft.activeMines,
            activeVolcanoLocations: VolcanoDungeon.activeLevels,
            includeBuildingInteriors: includeBuildingInteriors
        );
    }

    /// <summary>Get locations from the given live topology sources in deterministic root-first order, de-duplicated by reference.</summary>
    /// <param name="rootLocations">The game's primary location list.</param>
    /// <param name="loadedSaveLocations">The locations attached to the save being loaded, if any.</param>
    /// <param name="activeMineLocations">The active generated mine levels.</param>
    /// <param name="activeVolcanoLocations">The active generated volcano levels.</param>
    /// <param name="includeBuildingInteriors">Whether to recursively include constructable-building interiors.</param>
    internal static IReadOnlyList<WorldLocationInfo> GetLocations(IEnumerable<GameLocation>? rootLocations, IEnumerable<GameLocation>? loadedSaveLocations, IEnumerable<GameLocation>? activeMineLocations, IEnumerable<GameLocation>? activeVolcanoLocations, bool includeBuildingInteriors)
    {
        List<WorldLocationInfo> locations = [];
        Dictionary<GameLocation, int> locationIndexes = new(ReferenceEqualityComparer.Instance);

        // Add every root before traversing interiors. This gives root ordering precedence when the same location
        // also appears as an interior, and keeps the relative order within each vanilla source stable.
        WorldLocationUtilities.AddLocations(locations, locationIndexes, rootLocations);
        WorldLocationUtilities.AddLocations(locations, locationIndexes, loadedSaveLocations);
        WorldLocationUtilities.AddLocations(locations, locationIndexes, activeMineLocations);
        WorldLocationUtilities.AddLocations(locations, locationIndexes, activeVolcanoLocations);

        // Traverse the growing list so interiors nested to any depth are included exactly once.
        if (includeBuildingInteriors)
        {
            for (int i = 0; i < locations.Count; i++)
            {
                foreach (Building building in locations[i].Location.buildings)
                    WorldLocationUtilities.AddLocation(locations, locationIndexes, building.indoors.Value, building);
            }
        }

        return locations;
    }

    /// <summary>Add a sequence of root locations.</summary>
    private static void AddLocations(List<WorldLocationInfo> locations, Dictionary<GameLocation, int> locationIndexes, IEnumerable<GameLocation>? source)
    {
        if (source == null)
            return;

        foreach (GameLocation location in source)
            WorldLocationUtilities.AddLocation(locations, locationIndexes, location, parentBuilding: null);
    }

    /// <summary>Add a location if it hasn't already been reached, or retain another parent building for a shared interior.</summary>
    private static void AddLocation(List<WorldLocationInfo> locations, Dictionary<GameLocation, int> locationIndexes, GameLocation? location, Building? parentBuilding)
    {
        if (location == null)
            return;

        if (locationIndexes.TryGetValue(location, out int index))
        {
            if (parentBuilding == null)
                return;

            WorldLocationInfo existing = locations[index];
            if (existing.ParentBuilding == null)
                locations[index] = existing with { ParentBuilding = parentBuilding };
            else if (!ReferenceEquals(existing.ParentBuilding, parentBuilding))
            {
                List<Building>? additionalParents = existing.AdditionalParentBuildings;
                if (additionalParents == null)
                {
                    additionalParents = [];
                    locations[index] = existing with { AdditionalParentBuildings = additionalParents };
                }

                bool alreadyTracked = false;
                foreach (Building building in additionalParents)
                {
                    if (ReferenceEquals(building, parentBuilding))
                    {
                        alreadyTracked = true;
                        break;
                    }
                }
                if (!alreadyTracked)
                    additionalParents.Add(parentBuilding);
            }
            return;
        }

        locationIndexes.Add(location, locations.Count);
        locations.Add(new WorldLocationInfo(location, parentBuilding, AdditionalParentBuildings: null));
    }

    /// <summary>Metadata about a location in the live world topology.</summary>
    /// <param name="Location">The location instance.</param>
    /// <param name="ParentBuilding">The first building which contains the location, if any.</param>
    /// <param name="AdditionalParentBuildings">Any other buildings which reference the same interior location.</param>
    internal readonly record struct WorldLocationInfo(GameLocation Location, Building? ParentBuilding, List<Building>? AdditionalParentBuildings);
}
