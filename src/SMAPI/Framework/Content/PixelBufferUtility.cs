using System;

namespace StardewModdingAPI.Framework.Content;

/// <summary>Provides low-level pixel-buffer operations.</summary>
internal static class PixelBufferUtility
{
    /// <summary>Find the horizontal bounds containing nontransparent packed RGBA pixels.</summary>
    /// <param name="pixels">The packed RGBA pixels.</param>
    /// <param name="startIndex">The first known nontransparent pixel.</param>
    /// <param name="endIndex">The last known nontransparent pixel.</param>
    /// <param name="rowWidth">The number of pixels in each row.</param>
    /// <param name="minOpacity">The minimum alpha value to consider nontransparent.</param>
    /// <param name="left">The leftmost nontransparent column.</param>
    /// <param name="right">The rightmost nontransparent column.</param>
    public static void GetHorizontalBounds(ReadOnlySpan<uint> pixels, int startIndex, int endIndex, int rowWidth, byte minOpacity, out int left, out int right)
    {
        left = rowWidth - 1;
        right = 0;

        for (int i = startIndex; i <= endIndex; i++)
        {
            if ((byte)(pixels[i] >> 24) < minOpacity)
                continue;

            int x = i % rowWidth;
            if (x < left)
                left = x;
            if (x > right)
                right = x;

            if (left == 0 && right == rowWidth - 1)
                return;
        }
    }

    /// <summary>Copy tightly packed pixel rows while omitting any source row padding.</summary>
    /// <param name="source">The source pixel buffer.</param>
    /// <param name="sourceRowBytes">The number of source bytes per row, including padding.</param>
    /// <param name="output">The destination pixel buffer.</param>
    /// <param name="outputRowBytes">The number of bytes to copy for each row.</param>
    /// <param name="height">The number of rows to copy.</param>
    public static void CopyRows(ReadOnlySpan<byte> source, int sourceRowBytes, Span<byte> output, int outputRowBytes, int height)
    {
        if (sourceRowBytes == outputRowBytes)
            source[..checked(outputRowBytes * height)].CopyTo(output);
        else
        {
            for (int y = 0; y < height; y++)
            {
                source.Slice(y * sourceRowBytes, outputRowBytes).CopyTo(
                    output.Slice(y * outputRowBytes, outputRowBytes)
                );
            }
        }
    }
}
