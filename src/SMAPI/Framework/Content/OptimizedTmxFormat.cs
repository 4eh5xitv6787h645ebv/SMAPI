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

            // Populate the new backing array directly. Tile constructors already validate their sheets,
            // and the layer isn't observable until it's complete and added to the map. Its first draw
            // initializes every skip-map row, so dirty marks aren't needed while constructing it.
            Tile?[,] tiles = layer.Tiles.Array;

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
                    LayerTileLoadState state = new(layer, index, tiles);
                    OptimizedTmxLayerConversion.ForEachPopulated(
                        chunk.Tiles,
                        layer.LayerWidth,
                        chunk.X,
                        chunk.Y,
                        OptimizedTmxFormat.GetRawGid,
                        ref state,
                        OptimizedTmxFormat.LoadLayerTile
                    );
                }
            }
            else if (sourceLayer.Data?.Tiles is { Count: > 0 })
            {
                LayerTileLoadState state = new(layer, index, tiles);
                OptimizedTmxLayerConversion.ForEachPopulated(
                    sourceLayer.Data.Tiles,
                    layer.LayerWidth,
                    0,
                    0,
                    OptimizedTmxFormat.GetRawGid,
                    ref state,
                    OptimizedTmxFormat.LoadLayerTile
                );
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
                int tileWidth = (int)sourceObject.Width / this.FixedTileSize.Width;
                int tileY = (int)sourceObject.Y / this.FixedTileSize.Height;
                int tileHeight = (int)sourceObject.Height / this.FixedTileSize.Height;
                if (tileWidth <= 0 || tileHeight <= 0)
                    continue;

                TMXProperty[] properties = sourceObject.Properties;
                if (properties is null || properties.Length == 0)
                    continue;

                PropertyValue firstPropertyValue = GetPropertyValue(properties[0]);
                PropertyValue[]? remainingPropertyValues = null;
                if (properties.Length > 1)
                {
                    remainingPropertyValues = new PropertyValue[properties.Length - 1];
                    for (int i = 1; i < properties.Length; i++)
                        remainingPropertyValues[i - 1] = GetPropertyValue(properties[i]);
                }

                for (int x = tileX; x < tileX + tileWidth; x++)
                {
                    for (int y = tileY; y < tileY + tileHeight; y++)
                    {
                        if (layer.Tiles[x, y] is not Tile tile)
                            continue;

                        tile.Properties[properties[0].Name] = firstPropertyValue;
                        for (int i = 1; i < properties.Length; i++)
                            tile.Properties[properties[i].Name] = remainingPropertyValues![i - 1];
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

        return OptimizedTmxFormat.LoadTile(layer, index, OptimizedTmxTileTransform.Decode(rawGid));
    }

    /// <summary>Convert one decoded populated tile using a map-level lookup index.</summary>
    private static Tile LoadTile(Layer layer, MapTileIndex index, DecodedTmxTileTransform transform)
    {
        uint gid = transform.Gid;

        if (!index.TryResolve(gid, out ResolvedTmxTile<TileSheet, TMXFrame[]> resolved))
            throw new Exception($"Invalid tile gid: {gid}");

        Tile result;
        if (resolved.IsAnimated)
        {
            TMXFrame[] animation = resolved.Animation!;
            StaticTile[] frames = new StaticTile[animation.Length];
            for (int i = 0; i < animation.Length; i++)
                frames[i] = new StaticTile(layer, resolved.TileSheet, BlendMode.Alpha, (int)animation[i].TileId);

            result = new AnimatedTile(layer, frames, animation[0].Duration);
        }
        else
            result = new StaticTile(layer, resolved.TileSheet, BlendMode.Alpha, resolved.TileIndex);

        if (transform.Rotation != 0)
            result.SetRotationValue(transform.Rotation);

        if (transform.Flip != 0)
            result.SetFlip(transform.Flip);

        return result;
    }

    /// <summary>Get a parsed tile's raw global ID.</summary>
    private static uint GetRawGid(ParsedTile tile)
    {
        return tile.Gid;
    }

    /// <summary>Populate one decoded layer cell.</summary>
    private static void LoadLayerTile(ref LayerTileLoadState state, int x, int y, DecodedTmxTileTransform transform)
    {
        state.Tiles[x, y] = OptimizedTmxFormat.LoadTile(state.Layer, state.Index, transform);
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

    /*********
    ** Private models
    *********/
    /// <summary>An indexed view of the tilesheets and animated tiles for one map conversion.</summary>
    private sealed class MapTileIndex
    {
        /// <summary>The game-independent indexed target tilesheets.</summary>
        private readonly OptimizedTmxTileIndex<TileSheet, TMXFrame[]> Index;

        /// <summary>Construct an instance.</summary>
        public MapTileIndex(Map map, TMXMap sourceMap)
        {
            Dictionary<string, TMXTileset> sourceByName = new(StringComparer.Ordinal);
            foreach (TMXTileset sourceSheet in sourceMap.Tilesets)
                sourceByName.TryAdd(sourceSheet.Name, sourceSheet);

            TmxTileRange<TileSheet, TMXFrame[]>[] ranges = new TmxTileRange<TileSheet, TMXFrame[]>[map.TileSheets.Count];
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

                ranges[i] = new TmxTileRange<TileSheet, TMXFrame[]>(firstGid, lastGid, targetSheet, animations);
            }
            this.Index = new OptimizedTmxTileIndex<TileSheet, TMXFrame[]>(ranges);
        }

        /// <summary>Resolve a global tile ID to its indexed tilesheet and animation metadata.</summary>
        public bool TryResolve(uint gid, out ResolvedTmxTile<TileSheet, TMXFrame[]> result)
        {
            return this.Index.TryResolve(gid, out result);
        }
    }

    /// <summary>Mutable state passed through one game-independent layer traversal.</summary>
    private readonly record struct LayerTileLoadState(Layer Layer, MapTileIndex Index, Tile?[,] Tiles);
}
