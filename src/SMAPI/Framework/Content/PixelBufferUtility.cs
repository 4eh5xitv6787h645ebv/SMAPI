using System;

namespace StardewModdingAPI.Framework.Content;

/// <summary>Provides low-level pixel-buffer operations.</summary>
internal static class PixelBufferUtility
{
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
