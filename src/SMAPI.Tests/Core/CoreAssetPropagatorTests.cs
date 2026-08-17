using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Framework.Content;
using StardewModdingAPI.Framework.ContentManagers;
using StardewModdingAPI.Metadata;
using StardewValley;
using OtherAssetRoute = StardewModdingAPI.Metadata.CoreAssetPropagator.OtherAssetRoute;
using TextureAssetRoute = StardewModdingAPI.Metadata.CoreAssetPropagator.TextureAssetRoute;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for <see cref="CoreAssetPropagator"/>.</summary>
[TestFixture]
internal class CoreAssetPropagatorTests
{
    /// <summary>The exact texture routes expected from the legacy propagation switch.</summary>
    private static readonly TestCaseData[] TextureAssetRoutes =
    [
        new("Characters/Farmer/farmer_base", TextureAssetRoute.PlayerSprites),
        new("Characters/Farmer/farmer_base_bald", TextureAssetRoute.PlayerSprites),
        new("Characters/Farmer/farmer_girl_base", TextureAssetRoute.PlayerSprites),
        new("Characters/Farmer/farmer_girl_base_bald", TextureAssetRoute.PlayerSprites),
        new("TileSheets/tools", TextureAssetRoute.Tools)
    ];

    /// <summary>The exact non-texture routes expected from the legacy propagation switch.</summary>
    private static readonly TestCaseData[] OtherAssetRoutes =
    [
        new("Data/Achievements", OtherAssetRoute.Achievements),
        new("Data/AudioChanges", OtherAssetRoute.AudioChanges),
        new("Data/BigCraftables", OtherAssetRoute.BigCraftables),
        new("Data/Boots", OtherAssetRoute.Boots),
        new("Data/Buildings", OtherAssetRoute.Buildings),
        new("Data/ChairTiles", OtherAssetRoute.ChairTiles),
        new("Data/Characters", OtherAssetRoute.Characters),
        new("Data/Concessions", OtherAssetRoute.Concessions),
        new("Data/ConcessionTastes", OtherAssetRoute.ConcessionTastes),
        new("Data/CookingRecipes", OtherAssetRoute.CookingRecipes),
        new("Data/CraftingRecipes", OtherAssetRoute.CraftingRecipes),
        new("Data/Crops", OtherAssetRoute.Crops),
        new("Data/FarmAnimals", OtherAssetRoute.FarmAnimals),
        new("Data/FloorsAndPaths", OtherAssetRoute.FloorsAndPaths),
        new("Data/Furniture", OtherAssetRoute.Furniture),
        new("Data/FruitTrees", OtherAssetRoute.FruitTrees),
        new("Data/HairData", OtherAssetRoute.HairData),
        new("Data/Hats", OtherAssetRoute.Hats),
        new("Data/JukeboxTracks", OtherAssetRoute.JukeboxTracks),
        new("Data/Locations", OtherAssetRoute.Locations),
        new("Data/LocationContexts", OtherAssetRoute.LocationContexts),
        new("Data/Movies", OtherAssetRoute.Movies),
        new("Data/MoviesReactions", OtherAssetRoute.Movies),
        new("Data/NPCGiftTastes", OtherAssetRoute.NpcGiftTastes),
        new("Data/Objects", OtherAssetRoute.Objects),
        new("Data/Pants", OtherAssetRoute.Pants),
        new("Data/Pets", OtherAssetRoute.Pets),
        new("Data/Shirts", OtherAssetRoute.Shirts),
        new("Data/Tools", OtherAssetRoute.Tools),
        new("Data/TriggerActions", OtherAssetRoute.TriggerActions),
        new("Data/Weapons", OtherAssetRoute.Weapons),
        new("Data/WildTrees", OtherAssetRoute.WildTrees),
        new("Data/WorldMap", OtherAssetRoute.WorldMap),
        new("Fonts/SpriteFont1", OtherAssetRoute.SpriteFont1),
        new("Fonts/SmallFont", OtherAssetRoute.SmallFont),
        new("Fonts/TinyFont", OtherAssetRoute.TinyFont),
        new("Strings/StringsFromCSFiles", OtherAssetRoute.StringsFromCsFiles)
    ];

    [TestCaseSource(nameof(TextureAssetRoutes))]
    public void GetTextureAssetRoute_MapsLegacyRoutes(string assetName, TextureAssetRoute expected)
    {
        CoreAssetPropagator.GetTextureAssetRoute(assetName.ToUpperInvariant()).Should().Be(expected);
    }

    [TestCaseSource(nameof(OtherAssetRoutes))]
    public void GetOtherAssetRoute_MapsLegacyRoutes(string assetName, OtherAssetRoute expected)
    {
        string normalizedName = AssetName.Parse(assetName.Replace('/', '\\'), _ => null).BaseName;
        CoreAssetPropagator.GetOtherAssetRoute(normalizedName.ToUpperInvariant()).Should().Be(expected);
    }

    [Test]
    public void GetAssetRoutes_ReturnNoneForDynamicNames()
    {
        CoreAssetPropagator.GetTextureAssetRoute("Buildings/Barn_PaintMask").Should().Be(TextureAssetRoute.None);
        CoreAssetPropagator.GetOtherAssetRoute("Characters/Dialogue/Abigail").Should().Be(OtherAssetRoute.None);
    }

    [TestCase(OtherAssetRoute.BigCraftables, true)]
    [TestCase(OtherAssetRoute.Boots, true)]
    [TestCase(OtherAssetRoute.Furniture, true)]
    [TestCase(OtherAssetRoute.Hats, true)]
    [TestCase(OtherAssetRoute.Objects, true)]
    [TestCase(OtherAssetRoute.Pants, true)]
    [TestCase(OtherAssetRoute.Pets, true)]
    [TestCase(OtherAssetRoute.Shirts, true)]
    [TestCase(OtherAssetRoute.Tools, true)]
    [TestCase(OtherAssetRoute.Weapons, true)]
    [TestCase(OtherAssetRoute.Achievements, false)]
    [TestCase(OtherAssetRoute.None, false)]
    public void ResetsItemRegistry_IdentifiesSharedCacheRoutes(OtherAssetRoute route, bool expected)
    {
        CoreAssetPropagator.ResetsItemRegistry(route).Should().Be(expected);
    }

    [Test(Description = "Assert that adjacent item-data propagation routes reset their shared registry only once.")]
    public void Propagate_AdjacentItemData_CoalescesRegistryReset()
    {
        int resets = 0;
        CoreAssetPropagator propagator = this.CreatePropagator(() => resets++);
        Dictionary<IAssetName, Type> assets = new()
        {
            [AssetName.Parse("Data/Boots", _ => null)] = typeof(object),
            [AssetName.Parse("Data/Furniture", _ => null)] = typeof(object),
            [AssetName.Parse("Data/Hats", _ => null)] = typeof(object)
        };

        propagator.Propagate([], assets, loadedTextureManagers: null, ignoreWorld: true, out Dictionary<IAssetName, bool> propagated, out _);

        resets.Should().Be(1);
        propagated.Values.Should().OnlyContain(changed => changed);
    }

    [Test(Description = "Assert that a non-item propagation boundary flushes pending registry state before later item data.")]
    public void Propagate_SeparatedItemData_PreservesResetBoundary()
    {
        int resets = 0;
        CoreAssetPropagator propagator = this.CreatePropagator(() => resets++);
        Dictionary<IAssetName, Type> assets = new()
        {
            [AssetName.Parse("Data/Boots", _ => null)] = typeof(object),
            [AssetName.Parse("Mods/Unknown", _ => null)] = typeof(object),
            [AssetName.Parse("Data/Hats", _ => null)] = typeof(object)
        };

        propagator.Propagate([], assets, loadedTextureManagers: null, ignoreWorld: true, out _, out _);

        resets.Should().Be(2);
    }

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
            propagator.Propagate([], assets, loadedTextureManagers: null, ignoreWorld: false, out Dictionary<IAssetName, bool> propagated, out bool changedWarpRoutes);

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

    [Test(Description = "Assert that texture propagation reuses the content-manager targets found during invalidation.")]
    public void Propagate_Texture_UsesKnownManagers()
    {
        // arrange
        IAssetName assetName = AssetName.Parse("Maps/Target", _ => null);
        Type textureType = Type.GetType("Microsoft.Xna.Framework.Graphics.Texture2D, MonoGame.Framework", throwOnError: true)!;
        Mock<IContentManager> targetManager = new(MockBehavior.Strict);
        targetManager.Setup(manager => manager.TryGetCachedAsset(It.Is<IAssetName>(name => name.Equals(assetName)), out It.Ref<object?>.IsAny)).Returns(false);
        Mock<IContentManager> unrelatedManager = new(MockBehavior.Strict);

        CoreAssetPropagator propagator = new(
            mainContent: null!,
            disposableContent: null!,
            monitor: null!,
            multiplayer: null!,
            reflection: null!,
            parseAssetName: rawName => AssetName.Parse(rawName, _ => null)
        );
        Dictionary<IAssetName, Type> assets = new() { [assetName] = textureType };
        Dictionary<IAssetName, List<IContentManager>> loadedTextureManagers = new() { [assetName] = [targetManager.Object] };

        // act
        propagator.Propagate([unrelatedManager.Object, targetManager.Object], assets, loadedTextureManagers, ignoreWorld: true, out Dictionary<IAssetName, bool> propagated, out bool changedWarpRoutes);

        // assert
        targetManager.Verify(manager => manager.TryGetCachedAsset(It.Is<IAssetName>(name => name.Equals(assetName)), out It.Ref<object?>.IsAny), Times.Once);
        unrelatedManager.VerifyNoOtherCalls();
        propagated[assetName].Should().BeFalse();
        changedWarpRoutes.Should().BeFalse();
    }

    /// <summary>Create a propagator suitable for routes which don't access game content.</summary>
    private CoreAssetPropagator CreatePropagator(Action resetItemRegistry)
    {
        return new CoreAssetPropagator(
            mainContent: null!,
            disposableContent: null!,
            monitor: null!,
            multiplayer: null!,
            reflection: null!,
            parseAssetName: rawName => AssetName.Parse(rawName, _ => null),
            resetItemRegistry: resetItemRegistry
        );
    }
}
