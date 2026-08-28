using System;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.Content;
using TMXTile;
using xTile;
using xTile.Dimensions;
using xTile.Format;
using xTile.Layers;
using xTile.Tiles;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for <see cref="OptimizedTmxFormat"/>.</summary>
[TestFixture]
internal class OptimizedTmxFormatTests
{
    [Test]
    public void Load_MatchesBundledFormat()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <map version="1.10" tiledversion="1.11.2" orientation="orthogonal" renderorder="right-down" width="2" height="2" tilewidth="16" tileheight="16" infinite="0" nextlayerid="4" nextobjectid="2">
              <properties>
                <property name="@Description" value="map description"/>
                <property name="Number" type="int" value="42"/>
                <property name="Flag" type="bool" value="true"/>
              </properties>
              <tileset firstgid="1" name="sheet" tilewidth="16" tileheight="16" tilecount="4" columns="2">
                <image source="sheet.png" width="32" height="32"/>
                <tile id="1">
                  <properties><property name="TouchAction" value="Message hello"/></properties>
                  <animation>
                    <frame tileid="1" duration="125"/>
                    <frame tileid="2" duration="125"/>
                  </animation>
                </tile>
              </tileset>
              <layer id="1" name="Back" width="2" height="2" offsetx="1" offsety="2" opacity="0.75">
                <properties>
                  <property name="@Description" value="layer description"/>
                  <property name="LayerFlag" type="bool" value="true"/>
                </properties>
                <data encoding="csv">1,2,0,2147483649</data>
              </layer>
              <imagelayer id="2" name="Overlay" offsetx="3" offsety="4" opacity="0.5">
                <image source="overlay.png" width="32" height="32"/>
              </imagelayer>
              <objectgroup id="3" name="Back">
                <object id="1" name="TileData" x="0" y="0" width="16" height="16">
                  <properties><property name="ObjectProperty" value="added"/></properties>
                </object>
              </objectgroup>
            </map>
            """;

        byte[] data = Encoding.UTF8.GetBytes(xml);
        Map original;
        Map optimized;
        using (MemoryStream input = new(data))
            original = ((IMapFormat)new TMXFormat(16, 16, 4, 4)).Load(input);
        using (MemoryStream input = new(data))
            optimized = ((IMapFormat)new OptimizedTmxFormat(16, 16, 4, 4)).Load(input);

        using MemoryStream originalData = new();
        using MemoryStream optimizedData = new();
        RemoveIdentityTransformProperties(original);
        FormatManager.Instance.BinaryFormat.Store(original, originalData);
        FormatManager.Instance.BinaryFormat.Store(optimized, optimizedData);

        optimizedData.ToArray().Should().Equal(originalData.ToArray());
    }

    [Test]
    public void Load_AppliesTileDataAcrossRectangularTiles()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <map version="1.10" tiledversion="1.11.2" orientation="orthogonal" renderorder="right-down" width="4" height="4" tilewidth="16" tileheight="32" infinite="0" nextlayerid="3" nextobjectid="2">
              <tileset firstgid="1" name="sheet" tilewidth="16" tileheight="32" tilecount="1" columns="1">
                <image source="sheet.png" width="16" height="32"/>
              </tileset>
              <layer id="1" name="Back" width="4" height="4">
                <data encoding="csv">1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1</data>
              </layer>
              <objectgroup id="2" name="Back">
                <object id="1" name="TileData" x="16" y="32" width="32" height="64">
                  <properties>
                    <property name="Number" type="int" value="42"/>
                    <property name="Flag" type="bool" value="true"/>
                    <property name="Text" value="value"/>
                  </properties>
                </object>
              </objectgroup>
            </map>
            """;

        Map map;
        using (MemoryStream input = new(Encoding.UTF8.GetBytes(xml)))
            map = ((IMapFormat)new OptimizedTmxFormat(16, 32, 4, 2)).Load(input);

        Layer layer = map.GetLayer("Back");
        for (int x = 0; x < layer.LayerWidth; x++)
        {
            for (int y = 0; y < layer.LayerHeight; y++)
            {
                Tile tile = layer.Tiles[x, y]!;
                if (x is 1 or 2 && y is 1 or 2)
                {
                    ((int)tile.Properties["Number"]).Should().Be(42);
                    ((bool)tile.Properties["Flag"]).Should().BeTrue();
                    ((string)tile.Properties["Text"]).Should().Be("value");
                }
                else
                    tile.Properties.Should().NotContainKey("Number");
            }
        }
    }

    [Test]
    public void LoadTile_ReturnsNullForEmptyTile()
    {
        (Layer layer, TMXMap source) = CreateMap();

        OptimizedTmxFormat.LoadTile(layer, source, 0).Should().BeNull();
    }

    [Test]
    public void LoadTile_OmitsIdentityTransformProperties()
    {
        (Layer layer, TMXMap source) = CreateMap();

        Tile result = OptimizedTmxFormat.LoadTile(layer, source, 1)!;

        result.Properties.Should().NotContainKey("@Rotation");
        result.Properties.Should().NotContainKey("@Flip");
        result.GetRotationValue().Should().Be(0);
        result.GetFlip().Should().Be(0);
    }

    [Test]
    public void LoadTile_SelectsTilesheetAndLocalIndex()
    {
        (Layer layer, TMXMap source) = CreateMap(
            ("first", 1, 10, null),
            ("second", 11, 20, null)
        );

        Tile result = OptimizedTmxFormat.LoadTile(layer, source, 15)!;

        result.Should().BeOfType<StaticTile>();
        result.TileSheet.Id.Should().Be("second");
        result.TileIndex.Should().Be(4);
    }

    [Test]
    public void LoadTile_CreatesAnimatedTileFromIndexedDefinition()
    {
        TMXFrame[] animation =
        [
            new() { TileId = 3, Duration = 125 },
            new() { TileId = 7, Duration = 125 }
        ];
        (Layer layer, TMXMap source) = CreateMap(("sheet", 1, 10, new TMXTileSetTile { Id = 4, Animations = animation }));

        AnimatedTile result = OptimizedTmxFormat.LoadTile(layer, source, 5).Should().BeOfType<AnimatedTile>().Subject;

        result.FrameInterval.Should().Be(125);
        result.TileFrames.Should().HaveCount(2);
        result.TileFrames[0].TileIndex.Should().Be(3);
        result.TileFrames[1].TileIndex.Should().Be(7);
    }

    [TestCase(0x80000001u, 0, 1)]
    [TestCase(0x40000001u, 0, 2)]
    [TestCase(0xC0000001u, 180, 0)]
    [TestCase(0x20000001u, 90, 2)]
    [TestCase(0xA0000001u, 90, 0)]
    [TestCase(0x60000001u, -90, 0)]
    [TestCase(0xE0000001u, -90, 2)]
    public void LoadTile_PreservesTiledFlipFlags(uint gid, int expectedRotation, int expectedFlip)
    {
        (Layer layer, TMXMap source) = CreateMap();

        Tile result = OptimizedTmxFormat.LoadTile(layer, source, gid)!;

        result.GetRotationValue().Should().Be(expectedRotation);
        result.GetFlip().Should().Be(expectedFlip);
    }

    [Test]
    public void LoadTile_RejectsUnknownGlobalId()
    {
        (Layer layer, TMXMap source) = CreateMap();

        Action action = () => OptimizedTmxFormat.LoadTile(layer, source, 101);

        action.Should().Throw<Exception>().WithMessage("Invalid tile gid: 101");
    }

    [Test(Description = "Assert that dense synthetic TMX conversion avoids the bundled format's per-tile metadata allocations.")]
    [Category("PerformanceRegression")]
    [NonParallelizable]
    public void Load_DenseSyntheticMapReducesAllocation()
    {
        const int width = 64;
        const int height = 64;
        string tileData = string.Join(',', Enumerable.Repeat("1", width * height));
        string xml = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <map version="1.10" tiledversion="1.11.2" orientation="orthogonal" renderorder="right-down" width="{{width}}" height="{{height}}" tilewidth="16" tileheight="16" infinite="0" nextlayerid="2">
              <tileset firstgid="1" name="sheet" tilewidth="16" tileheight="16" tilecount="1" columns="1">
                <image source="sheet.png" width="16" height="16"/>
              </tileset>
              <layer id="1" name="Back" width="{{width}}" height="{{height}}">
                <data encoding="csv">{{tileData}}</data>
              </layer>
            </map>
            """;
        byte[] data = Encoding.UTF8.GetBytes(xml);
        IMapFormat bundledFormat = new TMXFormat(16, 16, 4, 4);
        IMapFormat optimizedFormat = new OptimizedTmxFormat(16, 16, 4, 4);

        // Warm parsing, conversion, and runtime-generated accessors before measuring.
        _ = LoadAndMeasureAllocations(bundledFormat, data);
        _ = LoadAndMeasureAllocations(optimizedFormat, data);

        (Map bundled, long bundledAllocated) = LoadAndMeasureAllocations(bundledFormat, data);
        (Map optimized, long optimizedAllocated) = LoadAndMeasureAllocations(optimizedFormat, data);

        Layer bundledLayer = bundled.GetLayer("Back");
        Layer optimizedLayer = optimized.GetLayer("Back");
        int bundledTiles = 0;
        int optimizedTiles = 0;
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                if (bundledLayer.Tiles[x, y] is not null)
                    bundledTiles++;
                if (optimizedLayer.Tiles[x, y] is Tile optimizedTile)
                {
                    optimizedTiles++;
                    optimizedTile.Properties.Should().NotContainKey("@Rotation");
                    optimizedTile.Properties.Should().NotContainKey("@Flip");
                }
            }
        }

        optimizedTiles.Should().Be(width * height);
        bundledTiles.Should().Be(optimizedTiles);
        optimizedAllocated.Should().BeLessThan(
            bundledAllocated * 3 / 4,
            "the indexed converter should avoid the bundled converter's LINQ searches and identity-transform properties"
        );
    }

    /// <summary>Load a map and measure allocations made by the synchronous parse-and-convert call.</summary>
    private static (Map Map, long AllocatedBytes) LoadAndMeasureAllocations(IMapFormat format, byte[] data)
    {
        using MemoryStream input = new(data, writable: false);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Map map = format.Load(input);
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        return (map, allocatedBytes);
    }

    /// <summary>Create matching parsed TMX and target xTile maps.</summary>
    private static (Layer Layer, TMXMap Source) CreateMap(params (string Id, uint FirstGid, int TileCount, TMXTileSetTile? AnimatedTile)[] sheets)
    {
        if (sheets.Length == 0)
            sheets = [("sheet", 1, 100, null)];

        Map target = new();
        TMXMap source = new() { Tilesets = [] };
        foreach ((string id, uint firstGid, int tileCount, TMXTileSetTile? animatedTile) in sheets)
        {
            TileSheet targetSheet = new(id, target, id, new Size(tileCount, 1), new Size(16, 16));
            targetSheet.Properties["@FirstGid"] = (int)firstGid;
            targetSheet.Properties["@LastGid"] = (int)(firstGid + tileCount - 1);
            target.AddTileSheet(targetSheet);

            source.Tilesets.Add(new TMXTileset
            {
                Name = id,
                Firstgid = firstGid,
                Tilecount = tileCount,
                Tiles = animatedTile is null ? [] : [animatedTile]
            });
        }

        Layer layer = new("Back", target, new Size(1, 1), new Size(16, 16));
        target.AddLayer(layer);
        return (layer, source);
    }

    /// <summary>Remove explicit identity transforms written by the bundled converter.</summary>
    private static void RemoveIdentityTransformProperties(Map map)
    {
        foreach (Layer layer in map.Layers)
        {
            for (int x = 0; x < layer.LayerWidth; x++)
            {
                for (int y = 0; y < layer.LayerHeight; y++)
                {
                    Tile? tile = layer.Tiles[x, y];
                    if (tile is null)
                        continue;

                    if (tile.GetRotationValue() == 0)
                        tile.Properties.Remove("@Rotation");
                    if (tile.GetFlip() == 0)
                        tile.Properties.Remove("@Flip");
                }
            }
        }
    }
}
