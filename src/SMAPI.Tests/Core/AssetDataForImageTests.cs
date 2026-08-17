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
}
