using FluentAssertions;
using Microsoft.Xna.Framework;
using NUnit.Framework;
using StardewModdingAPI.Framework.Content;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for <see cref="AssetDataForImage"/>.</summary>
[TestFixture]
internal class AssetDataForImageTests
{
    [Test]
    public void BlendOverlay_ComposesPremultipliedColorAndAlpha()
    {
        AssetDataForImage.BlendOverlay(
            above: new Color(64, 0, 0, 128),
            below: new Color(0, 0, 64, 128)
        ).Should().Be(new Color(64, 0, 31, 191));

        AssetDataForImage.BlendOverlay(
            above: new Color(20, 30, 40, 64),
            below: new Color(100, 80, 60, 192)
        ).Should().Be(new Color(94, 89, 84, 207));

        AssetDataForImage.BlendOverlay(Color.Transparent, new Color(10, 20, 30, 40)).Should().Be(new Color(10, 20, 30, 40));
        AssetDataForImage.BlendOverlay(new Color(10, 20, 30, 255), new Color(40, 50, 60, 255)).Should().Be(new Color(10, 20, 30, 255));
    }

    [Test]
    public void IsFullyOpaque_RequiresEveryPixel()
    {
        AssetDataForImage.IsFullyOpaque([Color.Red, Color.Green, Color.Blue]).Should().BeTrue();
        AssetDataForImage.IsFullyOpaque([Color.Red, Color.Transparent, Color.Blue]).Should().BeFalse();
        AssetDataForImage.IsFullyOpaque([Color.Red, new Color(10, 20, 30, 254), Color.Blue]).Should().BeFalse();
    }

    [Test]
    public void IsFullyOpaque_RejectsEmptySpan()
    {
        AssetDataForImage.IsFullyOpaque([]).Should().BeFalse();
    }

    [Test]
    public void IsFullyOpaqueRectangle_UsesOffsetsAndRowStride()
    {
        Color transparent = Color.Transparent;
        Color opaque = Color.White;
        Color[] pixels =
        [
            transparent, transparent, transparent, transparent, transparent,
            transparent, opaque,      opaque,      opaque,      transparent,
            transparent, opaque,      opaque,      opaque,      transparent,
            transparent, transparent, transparent, transparent, transparent
        ];

        AssetDataForImage.IsFullyOpaqueRectangle(pixels, firstPixel: 0, rowWidth: 5, left: 1, top: 1, width: 3, height: 2).Should().BeTrue();
        AssetDataForImage.IsFullyOpaqueRectangle(pixels, firstPixel: 0, rowWidth: 5, left: 0, top: 1, width: 4, height: 2).Should().BeFalse();
        AssetDataForImage.IsFullyOpaqueRectangle(pixels, firstPixel: 5, rowWidth: 5, left: 1, top: 0, width: 3, height: 2).Should().BeTrue();
    }
}
