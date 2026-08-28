using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace StardewModdingAPI.Framework.Content;

/// <summary>Read a raw global ID from a source tile without delegate dispatch.</summary>
internal interface ITmxRawGidReader<TTile>
{
    uint GetRawGid(TTile tile);
}

/// <summary>Handle one populated cell without delegate dispatch.</summary>
internal interface IDecodedTmxTileConsumer<TState>
{
    void Consume(ref TState state, int x, int y, DecodedTmxTileTransform transform);
}

/// <summary>Game-independent layer traversal and indexed tile resolution used by the optimized TMX converter.</summary>
internal static class OptimizedTmxLayerConversion
{
    /// <summary>Visit each populated tile in row-major order without materializing empty cells.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void ForEachPopulated<TTile, TState, TReader, TConsumer>(
        List<TTile> sourceTiles,
        int rowWidth,
        int originX,
        int originY,
        ref TState state,
        TReader reader,
        TConsumer consumer
    )
        where TReader : struct, ITmxRawGidReader<TTile>
        where TConsumer : struct, IDecodedTmxTileConsumer<TState>
    {
        if (rowWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(rowWidth));

        int x = originX;
        int y = originY;
        int rowEnd = checked(originX + rowWidth);
        int count = sourceTiles.Count;
        for (int index = 0; index < count; index++)
        {
            uint rawGid = reader.GetRawGid(sourceTiles[index]);
            if (rawGid != 0)
                consumer.Consume(ref state, x, y, OptimizedTmxTileTransform.Decode(rawGid));

            x++;
            if (x >= rowEnd)
            {
                x = originX;
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
    Dictionary<int, TAnimation>? Animations
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
