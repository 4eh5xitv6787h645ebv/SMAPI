using FluentAssertions;
using Microsoft.Xna.Framework;
using NUnit.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Framework.Content;
using StardewModdingAPI.Framework.Reflection;
using xTile;
using xTile.Dimensions;
using xTile.Layers;
using xTile.Tiles;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for <see cref="AssetDataForMap"/>.</summary>
[TestFixture]
internal class AssetDataForMapTests
{
    [Test(Description = "Assert that map patches copy existing and new layer properties.")]
    public void PatchMap_CopiesLayerProperties()
    {
        Map target = CreateMap("Back", "Front");
        target.GetLayer("Back").Properties["Old"] = "retained";

        Map source = CreateMap("Back", "Buildings");
        source.GetLayer("Back").Properties["Existing"] = "copied";
        source.GetLayer("Buildings").Properties["New"] = "copied";

        CreateAssetData(target).PatchMap(source);

        target.GetLayer("Back").Properties["Old"].ToString().Should().Be("retained");
        target.GetLayer("Back").Properties["Existing"].ToString().Should().Be("copied");
        target.GetLayer("Buildings").Properties["New"].ToString().Should().Be("copied");
        target.GetLayer("Front").Should().NotBeNull();
    }

    [Test(Description = "Assert that a zero-sized map patch doesn't create or change layers.")]
    public void PatchMap_ZeroArea_DoesNotChangeLayers()
    {
        Map target = CreateMap("Back");
        target.GetLayer("Back").Properties["Value"] = "old";

        Map source = CreateMap("Back", "Buildings");
        source.GetLayer("Back").Properties["Value"] = "new";

        Rectangle emptyArea = new(0, 0, 0, 0);
        CreateAssetData(target).PatchMap(source, emptyArea, emptyArea);

        target.GetLayer("Back").Properties["Value"].ToString().Should().Be("old");
        target.GetLayer("Buildings").Should().BeNull();
    }

    [TestCase("Maps/Town/Sheet.png", "maps\\town\\SHEET.PNG")]
    [TestCase("Town/Sheet", "TOWN/SHEET.png")]
    public void PatchMap_ReusesEquivalentTilesheetPathsIgnoringCase(string targetPath, string sourcePath)
    {
        Map target = CreateMap("Back");
        TileSheet original = AddTileSheet(target, "outdoors", targetPath);
        Map source = CreateMap("Back");
        AddTileSheet(source, "outdoors", sourcePath);

        CreateAssetData(target).PatchMap(source);

        target.TileSheets.Should().ContainSingle().Which.Should().BeSameAs(original);
    }

    [Test]
    public void PatchMap_DisambiguatesGenuinelyDifferentTilesheetPaths()
    {
        Map target = CreateMap("Back");
        AddTileSheet(target, "outdoors", "Maps/Town/Sheet");
        Map source = CreateMap("Back");
        AddTileSheet(source, "outdoors", "Maps/Town/Different");

        CreateAssetData(target).PatchMap(source);

        target.TileSheets.Should().HaveCount(2);
        target.GetTileSheet("z_outdoors").Should().NotBeNull();
    }

    [TestCase(PatchMapMode.Overlay, true, true)]
    [TestCase(PatchMapMode.ReplaceByLayer, false, true)]
    [TestCase(PatchMapMode.Replace, false, false)]
    public void PatchMap_LayerFirstCopyPreservesPatchModes(PatchMapMode patchMode, bool retainsEmptySourceTile, bool retainsTargetOnlyLayer)
    {
        Map target = CreateMap("Back", "Front");
        TileSheet targetSheet = AddTileSheet(target, "outdoors", "Maps/outdoors");
        SetTile(target, "Back", targetSheet, 0, 0);
        Tile retainedBackTile = SetTile(target, "Back", targetSheet, 1, 0);
        Tile retainedFrontTile = SetTile(target, "Front", targetSheet, 0, 0);

        Map source = CreateMap("Back", "Buildings");
        TileSheet sourceSheet = AddTileSheet(source, "outdoors", "maps/OUTDOORS.png");
        Tile sourceBackTile = SetTile(source, "Back", sourceSheet, 0, 0);
        sourceBackTile.Properties["Copied"] = "yes";
        SetTile(source, "Buildings", sourceSheet, 0, 0);

        CreateAssetData(target).PatchMap(source, patchMode: patchMode);

        target.GetLayer("Back").Tiles[0, 0].Should().NotBeSameAs(sourceBackTile);
        target.GetLayer("Back").Tiles[0, 0].TileSheet.Should().BeSameAs(targetSheet);
        target.GetLayer("Back").Tiles[0, 0].Properties["Copied"].ToString().Should().Be("yes");
        if (retainsEmptySourceTile)
            target.GetLayer("Back").Tiles[1, 0].Should().BeSameAs(retainedBackTile);
        else
            target.GetLayer("Back").Tiles[1, 0].Should().BeNull();

        if (retainsTargetOnlyLayer)
            target.GetLayer("Front").Tiles[0, 0].Should().BeSameAs(retainedFrontTile);
        else
            target.GetLayer("Front").Tiles[0, 0].Should().BeNull();

        target.GetLayer("Buildings").Tiles[0, 0].Should().NotBeNull();
        target.GetLayer("Buildings").Tiles[0, 0].TileSheet.Should().BeSameAs(targetSheet);
    }

    /// <summary>Create a map with the given two-by-two layers.</summary>
    private static Map CreateMap(params string[] layerIds)
    {
        Map map = new();
        foreach (string id in layerIds)
            map.AddLayer(new Layer(id, map, new Size(2, 2), new Size(16, 16)));

        return map;
    }

    /// <summary>Add a one-tile tilesheet to a map.</summary>
    private static TileSheet AddTileSheet(Map map, string id, string imageSource)
    {
        TileSheet sheet = new(id, map, imageSource, new Size(1, 1), new Size(16, 16));
        map.AddTileSheet(sheet);
        return sheet;
    }

    /// <summary>Add a static tile to a map layer.</summary>
    private static Tile SetTile(Map map, string layerId, TileSheet sheet, int x, int y)
    {
        Layer layer = map.GetLayer(layerId);
        Tile tile = new StaticTile(layer, sheet, BlendMode.Alpha, tileIndex: 0);
        layer.Tiles[x, y] = tile;
        return tile;
    }

    /// <summary>Wrap a map in editable asset metadata.</summary>
    private static AssetDataForMap CreateAssetData(Map map)
    {
        return new AssetDataForMap(
            locale: null,
            assetName: AssetName.Parse("Maps/Test", _ => null),
            data: map,
            getNormalizedPath: static path => path,
            onDataReplaced: _ => { },
            reflection: new Reflector()
        );
    }
}
