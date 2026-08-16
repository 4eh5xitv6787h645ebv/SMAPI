using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.Content;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for <see cref="PixelBufferUtility"/>.</summary>
[TestFixture]
internal class PixelBufferUtilityTests
{
    [Test(Description = "Assert that horizontal alpha bounds include every nontransparent column across rows.")]
    public void GetHorizontalBounds_FindsOpaqueColumns()
    {
        // arrange: opaque pixels at row 0/x2, row 1/x1, and row 1/x3
        uint[] pixels =
        [
            0, 0, 0x05000000, 0,
            0, 0xFF000000, 0, 0x06000000
        ];

        // act
        PixelBufferUtility.GetHorizontalBounds(pixels, startIndex: 2, endIndex: 7, rowWidth: 4, minOpacity: 5, out int left, out int right);

        // assert
        left.Should().Be(1);
        right.Should().Be(3);
    }

    [Test(Description = "Assert that contiguous RGBA pixel data is bulk-copied unchanged.")]
    public void CopyRows_CopiesContiguousData()
    {
        // arrange
        byte[] rawPixels = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
        byte[] output = new byte[rawPixels.Length];

        // act
        PixelBufferUtility.CopyRows(rawPixels, sourceRowBytes: 8, output, outputRowBytes: 8, height: 2);

        // assert
        output.Should().Equal(rawPixels);
    }

    [Test(Description = "Assert that RGBA pixel rows are bulk-copied without including source padding.")]
    public void CopyRows_OmitsSourcePadding()
    {
        // arrange
        byte[] rawPixels =
        [
            1, 2, 3, 4,
            5, 6, 7, 8,
            101, 102, 103, 104,
            9, 10, 11, 12,
            13, 14, 15, 16,
            105, 106, 107, 108
        ];

        byte[] output = new byte[16];

        // act
        PixelBufferUtility.CopyRows(rawPixels, sourceRowBytes: 12, output, outputRowBytes: 8, height: 2);

        // assert
        output.Should().Equal(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16);
    }
}
