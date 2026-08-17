/*
Portions derived from TMXTile's TMXFormat.cs.

MIT License

Copyright (c) 2019 Platonymous

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMXTile;
using xTile;
using xTile.Dimensions;
using xTile.Format;
using xTile.Layers;
using xTile.ObjectModel;
using xTile.Tiles;
using ParsedTile = TMXTile.TMXTile;

namespace StardewModdingAPI.Framework.Content;

/// <summary>A TMX map format which avoids repeated tileset and animation searches while converting parsed tiles.</summary>
internal sealed class OptimizedTmxFormat : TMXFormat, IMapFormat
{
    /*********
    ** Fields
    *********/
    /// <summary>The horizontal tile-flip bit used by Tiled global tile IDs.</summary>
    private const uint FlippedHorizontallyFlag = 0x80000000;

    /// <summary>The vertical tile-flip bit used by Tiled global tile IDs.</summary>
    private const uint FlippedVerticallyFlag = 0x40000000;

    /// <summary>The diagonal tile-flip bit used by Tiled global tile IDs.</summary>
    private const uint FlippedDiagonallyFlag = 0x20000000;

    /// <summary>The combined tile-flip bits which aren't part of a tilesheet index.</summary>
    private const uint FlipMask = OptimizedTmxFormat.FlippedHorizontallyFlag | OptimizedTmxFormat.FlippedVerticallyFlag | OptimizedTmxFormat.FlippedDiagonallyFlag;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    public OptimizedTmxFormat(int tileWidth, int tileHeight, int tileSizeMultiplierX, int tileSizeMultiplierY)
        : base(tileWidth, tileHeight, tileSizeMultiplierX, tileSizeMultiplierY) { }

    /// <inheritdoc />
    public new Map Load(Stream stream)
    {
        TMXMap source = new TMXParser().Parse(stream);
        return this.LoadOptimized(source);
    }

    /// <inheritdoc />
    Map IMapFormat.Load(Stream stream)
    {
        return this.Load(stream);
    }

    /// <summary>Convert a parsed TMX map to xTile.</summary>
    /// <param name="source">The parsed TMX map.</param>
    internal Map LoadOptimized(TMXMap source)
    {
        if (source.Orientation != "orthogonal")
            throw new Exception("Only orthogonal Tiled maps are supported.");

        Map map = new();
        TMXProperty[]? properties = GetOrderedProperties(source.Properties);
        if (properties is not null)
        {
            foreach (TMXProperty property in properties)
            {
                if (property.Name == "@Description")
                    map.Description = property.StringValue;
                else
                    map.Properties[property.Name] = GetPropertyValue(property);
            }
        }

        if (source.Backgroundcolor is TMXColor background)
            map.Properties["@BackgroundColor"] = background.ToString();

        this.LoadTileSets(source, ref map);
        this.LoadLayers(source, map);
        this.LoadImageLayers(source, ref map);
        this.LoadObjects(source, map);
        return map;
    }

    /// <summary>Convert one parsed Tiled tile into its xTile representation.</summary>
    /// <param name="layer">The target xTile layer.</param>
    /// <param name="source">The parsed Tiled map.</param>
    /// <param name="rawGid">The global Tiled tile ID, including flip flags.</param>
    /// <returns>The converted tile, or <c>null</c> for an empty tile.</returns>
    internal static Tile? LoadTile(Layer layer, TMXMap source, uint rawGid)
    {
        return OptimizedTmxFormat.LoadTile(layer, new MapTileIndex(layer.Map, source), rawGid);
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Load the parsed map layers.</summary>
    private void LoadLayers(TMXMap source, Map map)
    {
        if (source.Layers is null)
            return;

        MapTileIndex index = new(map, source);
        foreach (TMXLayer sourceLayer in source.Layers)
        {
            Layer layer = new(sourceLayer.Name, map, new Size((int)sourceLayer.Width, (int)sourceLayer.Height), this.FixedTileSizeMultiplied);

            if (sourceLayer.Properties is not null)
            {
                foreach (TMXProperty property in sourceLayer.Properties)
                {
                    if (property.Name == "@Description")
                        layer.Description = property.StringValue;
                    else
                        layer.Properties[property.Name] = GetPropertyValue(property);
                }
            }

            if (sourceLayer.Data?.Chunks is { Count: > 0 })
            {
                foreach (TMXChunk chunk in sourceLayer.Data.Chunks)
                {
                    Location origin = new(chunk.X, chunk.Y);
                    foreach (ParsedTile sourceTile in chunk.Tiles)
                    {
                        if (sourceTile.Gid != 0)
                            layer.Tiles[origin] = OptimizedTmxFormat.LoadTile(layer, index, sourceTile.Gid);
                        ++origin.X;
                        if (origin.X >= layer.LayerWidth)
                        {
                            origin.X = 0;
                            ++origin.Y;
                        }
                    }
                }
            }
            else if (sourceLayer.Data?.Tiles is { Count: > 0 })
            {
                Location origin = Location.Origin;
                foreach (ParsedTile sourceTile in sourceLayer.Data.Tiles)
                {
                    if (sourceTile.Gid != 0)
                        layer.Tiles[origin] = OptimizedTmxFormat.LoadTile(layer, index, sourceTile.Gid);
                    ++origin.X;
                    if (origin.X >= layer.LayerWidth)
                    {
                        origin.X = 0;
                        ++origin.Y;
                    }
                }
            }

            layer.SetOffset(new Location((int)Math.Floor(sourceLayer.Offsetx * this.TileSizeMultiplier.Width), (int)Math.Floor(sourceLayer.Offsety * this.TileSizeMultiplier.Height)));
            layer.SetOpacity(sourceLayer.Opacity);
            map.AddLayer(layer);
        }
    }

    /// <summary>Apply Tiled object-layer tile properties.</summary>
    private void LoadObjects(TMXMap source, Map map)
    {
        if (source.Objectgroups is null || source.Objectgroups.Count == 0)
            return;

        foreach (TMXObjectgroup objectGroup in source.Objectgroups)
        {
            if (map.GetLayer(objectGroup.Name) is not Layer layer)
                continue;

            foreach (TMXObject sourceObject in objectGroup.Objects)
            {
                if (sourceObject.Name != "TileData")
                    continue;

                int tileX = (int)sourceObject.X / this.FixedTileSize.Width;
                int tileWidth = (int)sourceObject.Width / this.FixedTileSize.Height;
                int tileY = (int)sourceObject.Y / this.FixedTileSize.Width;
                int tileHeight = (int)sourceObject.Height / this.FixedTileSize.Height;

                for (int x = tileX; x < tileX + tileWidth; x++)
                {
                    for (int y = tileY; y < tileY + tileHeight; y++)
                    {
                        if (layer.Tiles[x, y] is not Tile tile)
                            continue;

                        foreach (TMXProperty property in sourceObject.Properties)
                            tile.Properties[property.Name] = GetPropertyValue(property);
                    }
                }
            }
        }
    }

    /// <summary>Convert one parsed Tiled tile using a map-level lookup index.</summary>
    private static Tile? LoadTile(Layer layer, MapTileIndex index, uint rawGid)
    {
        if (rawGid == 0)
            return null;

        bool flippedHorizontally = (rawGid & OptimizedTmxFormat.FlippedHorizontallyFlag) != 0;
        bool flippedVertically = (rawGid & OptimizedTmxFormat.FlippedVerticallyFlag) != 0;
        bool flippedDiagonally = (rawGid & OptimizedTmxFormat.FlippedDiagonallyFlag) != 0;
        uint gid = rawGid & ~OptimizedTmxFormat.FlipMask;

        if (!index.TryGetRange(gid, out SheetRange range))
            throw new Exception($"Invalid tile gid: {gid}");

        int tileIndex = (int)(gid - range.FirstGid);
        Tile result;
        if (range.Animations?.TryGetValue(tileIndex, out TMXFrame[]? animation) is true)
        {
            StaticTile[] frames = new StaticTile[animation.Length];
            for (int i = 0; i < animation.Length; i++)
                frames[i] = new StaticTile(layer, range.TileSheet, BlendMode.Alpha, (int)animation[i].TileId);

            result = new AnimatedTile(layer, frames, animation[0].Duration);
        }
        else
            result = new StaticTile(layer, range.TileSheet, BlendMode.Alpha, tileIndex);

        int rotation = GetRotationForFlippedTile(flippedHorizontally, flippedVertically, flippedDiagonally);
        if (rotation != 0)
            result.SetRotationValue(rotation);

        int flip = GetEffectForFlippedTile(flippedHorizontally, flippedVertically, flippedDiagonally);
        if (flip != 0)
            result.SetFlip(flip);

        return result;
    }

    /// <summary>Order map properties using TMXTile's existing compatibility rules.</summary>
    private static TMXProperty[]? GetOrderedProperties(IEnumerable<TMXProperty>? properties)
    {
        return properties?
            .OrderBy(property => property?.Name[0] is char first && first.ToString().Equals(first.ToString().ToLower()) ? $"B_{property?.Name}" : $"A_{property?.Name}")
            .ToArray();
    }

    /// <summary>Convert a parsed Tiled property to xTile.</summary>
    private static PropertyValue GetPropertyValue(TMXProperty property)
    {
        return property.Type?.ToLower() switch
        {
            null => property.StringValue,
            "int" => property.IntValue,
            "string" => property.StringValue,
            "bool" => property.BoolValue,
            "color" => property.ColorValue.ToString(),
            "float" => property.FloatValue,
            _ => property.StringValue
        };
    }

    /// <summary>Get the xTile flip effect for Tiled flip flags.</summary>
    private static int GetEffectForFlippedTile(bool horizontal, bool vertical, bool diagonal)
    {
        if (diagonal)
        {
            if (!vertical && !horizontal)
                return 2;
            if (horizontal && vertical)
                return 2;
            if (horizontal || vertical)
                return 0;
        }

        if (horizontal && vertical)
            return 0;
        if (horizontal)
            return 1;
        if (vertical)
            return 2;

        return 0;
    }

    /// <summary>Get the xTile rotation for Tiled flip flags.</summary>
    private static int GetRotationForFlippedTile(bool horizontal, bool vertical, bool diagonal)
    {
        if (diagonal)
        {
            if (!vertical && !horizontal)
                return 90;
            if (horizontal && vertical)
                return -90;
            if (horizontal)
                return 90;
            if (vertical)
                return -90;
        }

        return horizontal && vertical
            ? 180
            : 0;
    }


    /*********
    ** Private models
    *********/
    /// <summary>An indexed view of the tilesheets and animated tiles for one map conversion.</summary>
    private sealed class MapTileIndex
    {
        /// <summary>The indexed target tilesheets.</summary>
        private readonly SheetRange[] Ranges;

        /// <summary>Construct an instance.</summary>
        public MapTileIndex(Map map, TMXMap sourceMap)
        {
            Dictionary<string, TMXTileset> sourceByName = new(StringComparer.Ordinal);
            foreach (TMXTileset sourceSheet in sourceMap.Tilesets)
                sourceByName.TryAdd(sourceSheet.Name, sourceSheet);

            this.Ranges = new SheetRange[map.TileSheets.Count];
            for (int i = 0; i < map.TileSheets.Count; i++)
            {
                TileSheet targetSheet = map.TileSheets[i];
                uint firstGid = (uint)targetSheet.Properties["@FirstGid"];
                uint lastGid = (uint)targetSheet.Properties["@LastGid"];
                Dictionary<int, TMXFrame[]>? animations = null;

                if (sourceByName.TryGetValue(targetSheet.Id, out TMXTileset? sourceSheet) && sourceSheet.Tiles is not null)
                {
                    foreach (TMXTileSetTile tile in sourceSheet.Tiles)
                    {
                        if (tile.Animations is { Length: > 0 })
                        {
                            animations ??= [];
                            animations.TryAdd(tile.Id, tile.Animations);
                        }
                    }
                }

                this.Ranges[i] = new SheetRange(firstGid, lastGid, targetSheet, animations);
            }
        }

        /// <summary>Get the tilesheet range containing a global tile ID.</summary>
        public bool TryGetRange(uint gid, out SheetRange result)
        {
            foreach (SheetRange range in this.Ranges)
            {
                if (gid >= range.FirstGid && gid <= range.LastGid)
                {
                    result = range;
                    return true;
                }
            }

            result = default;
            return false;
        }
    }

    /// <summary>An indexed target tilesheet and its animated tile definitions.</summary>
    private readonly record struct SheetRange(uint FirstGid, uint LastGid, TileSheet TileSheet, Dictionary<int, TMXFrame[]>? Animations);
}
