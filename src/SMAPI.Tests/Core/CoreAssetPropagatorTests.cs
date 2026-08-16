using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Framework.Content;
using StardewModdingAPI.Metadata;
using StardewValley;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for <see cref="CoreAssetPropagator"/>.</summary>
[TestFixture]
internal class CoreAssetPropagatorTests
{
    [Test(Description = "Assert that a multi-map propagation batch indexes world map paths once.")]
    public void Propagate_MultipleMaps_IndexesLocationsOnce()
    {
        // arrange
        Game1? previousGameInstance = Game1.game1;
        Game1 testGameInstance = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        List<GameLocation> locations =
        [
            new GameLocation { mapPath = { Value = "Maps/First" } },
            new GameLocation { mapPath = { Value = "Maps/Second" } }
        ];
        typeof(Game1).GetField("_locations", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(testGameInstance, locations);
        Game1.game1 = testGameInstance;

        try
        {
            int parsedMapPaths = 0;
            CoreAssetPropagator propagator = new(
                mainContent: null!,
                disposableContent: null!,
                monitor: null!,
                multiplayer: null!,
                reflection: null!,
                parseAssetName: rawName =>
                {
                    parsedMapPaths++;
                    return AssetName.Parse(rawName, _ => null);
                }
            );
            Type mapType = Type.GetType("xTile.Map, xTile", throwOnError: true)!;
            Dictionary<IAssetName, Type> assets = new()
            {
                [AssetName.Parse("Maps/NotLoadedOne", _ => null)] = mapType,
                [AssetName.Parse("Maps/NotLoadedTwo", _ => null)] = mapType
            };

            // act
            propagator.Propagate([], assets, ignoreWorld: false, out Dictionary<IAssetName, bool> propagated, out bool changedWarpRoutes);

            // assert
            parsedMapPaths.Should().Be(2, "each loaded location should be indexed once for the whole map batch");
            propagated.Values.Should().OnlyContain(changed => !changed);
            changedWarpRoutes.Should().BeFalse();
        }
        finally
        {
            Game1.game1 = previousGameInstance;
        }
    }
}
