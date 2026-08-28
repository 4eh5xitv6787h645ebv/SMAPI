using System.Runtime.CompilerServices;

namespace StardewModdingAPI.Framework.Content;

/// <summary>Decode a Tiled global tile ID into its tilesheet ID and xTile transform values.</summary>
/// <remarks>This allocation-free helper is shared by the real TMX converter and fixture-free CI gates.</remarks>
internal static class OptimizedTmxTileTransform
{
    private const uint FlippedHorizontallyFlag = 0x80000000;
    private const uint FlippedVerticallyFlag = 0x40000000;
    private const uint FlippedDiagonallyFlag = 0x20000000;
    private const uint FlipMask = OptimizedTmxTileTransform.FlippedHorizontallyFlag | OptimizedTmxTileTransform.FlippedVerticallyFlag | OptimizedTmxTileTransform.FlippedDiagonallyFlag;

    /// <summary>Decode one raw Tiled global tile ID.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DecodedTmxTileTransform Decode(uint rawGid)
    {
        bool horizontal = (rawGid & OptimizedTmxTileTransform.FlippedHorizontallyFlag) != 0;
        bool vertical = (rawGid & OptimizedTmxTileTransform.FlippedVerticallyFlag) != 0;
        bool diagonal = (rawGid & OptimizedTmxTileTransform.FlippedDiagonallyFlag) != 0;

        int flip;
        if (diagonal)
            flip = horizontal == vertical ? 2 : 0;
        else if (horizontal == vertical)
            flip = 0;
        else
            flip = horizontal ? 1 : 2;

        int rotation = diagonal
            ? horizontal == vertical
                ? horizontal ? -90 : 90
                : horizontal ? 90 : -90
            : horizontal && vertical
                ? 180
                : 0;

        return new DecodedTmxTileTransform(rawGid & ~OptimizedTmxTileTransform.FlipMask, rotation, flip);
    }
}

/// <summary>One decoded Tiled tile transform.</summary>
internal readonly record struct DecodedTmxTileTransform(uint Gid, int Rotation, int Flip);
