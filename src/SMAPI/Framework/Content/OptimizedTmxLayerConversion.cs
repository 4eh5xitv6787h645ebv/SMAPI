using System;
using System.Collections.Generic;

namespace StardewModdingAPI.Framework.Content;

/// <summary>Handle one populated cell decoded by the production TMX layer traversal.</summary>
internal delegate void DecodedTmxTileConsumer<TState>(ref TState state, int x, int y, DecodedTmxTileTransform transform);

/// <summary>Game-independent layer traversal and indexed tile resolution used by the optimized TMX converter.</summary>
internal static class OptimizedTmxLayerConversion
{
    /// <summary>Visit each populated tile in row-major order without materializing empty cells.</summary>
    public static void ForEachPopulated<TTile, TState>(
        IReadOnlyList<TTile> sourceTiles,
        int layerWidth,
        int originX,
        int originY,
        Func<TTile, uint> getRawGid,
        ref TState state,
        DecodedTmxTileConsumer<TState> consume
    )
    {
        if (layerWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(layerWidth));

        int x = originX;
        int y = originY;
        for (int index = 0; index < sourceTiles.Count; index++)
        {
            uint rawGid = getRawGid(sourceTiles[index]);
            if (rawGid != 0)
                consume(ref state, x, y, OptimizedTmxTileTransform.Decode(rawGid));

            x++;
            if (x >= layerWidth)
            {
                x = 0;
                y++;
            }
        }
    }
}

/// <summary>An indexed global-ID range and optional animated-tile lookup.</summary>
internal readonly record struct TmxTileRange<TSheet, TAnimation>(
    uint FirstGid,
    uint LastGid,
    TSheet TileSheet,
    IReadOnlyDictionary<int, TAnimation>? Animations
);

/// <summary>A resolved global tile ID.</summary>
internal readonly record struct ResolvedTmxTile<TSheet, TAnimation>(
    TSheet TileSheet,
    int TileIndex,
    TAnimation? Animation,
    bool IsAnimated
);

/// <summary>The allocation-free range and animation lookup used for each populated TMX cell.</summary>
internal sealed class OptimizedTmxTileIndex<TSheet, TAnimation>
{
    private readonly TmxTileRange<TSheet, TAnimation>[] Ranges;

    /// <summary>Construct an index from map-level tilesheet metadata.</summary>
    public OptimizedTmxTileIndex(TmxTileRange<TSheet, TAnimation>[] ranges)
    {
        this.Ranges = ranges;
    }

    /// <summary>Resolve a decoded global tile ID.</summary>
    public bool TryResolve(uint gid, out ResolvedTmxTile<TSheet, TAnimation> result)
    {
        foreach (TmxTileRange<TSheet, TAnimation> range in this.Ranges)
        {
            if (gid < range.FirstGid || gid > range.LastGid)
                continue;

            int tileIndex = (int)(gid - range.FirstGid);
            if (range.Animations?.TryGetValue(tileIndex, out TAnimation? animation) is true)
                result = new ResolvedTmxTile<TSheet, TAnimation>(range.TileSheet, tileIndex, animation, IsAnimated: true);
            else
                result = new ResolvedTmxTile<TSheet, TAnimation>(range.TileSheet, tileIndex, Animation: default, IsAnimated: false);
            return true;
        }

        result = default;
        return false;
    }
}
