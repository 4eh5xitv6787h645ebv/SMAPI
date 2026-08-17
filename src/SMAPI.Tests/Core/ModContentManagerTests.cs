using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.ContentManagers;
using StardewModdingAPI.Toolkit.Utilities;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for <see cref="ModContentManager"/>.</summary>
[TestFixture]
internal class ModContentManagerTests
{
    [TestCase("../foo", false)]
    [TestCase("foo/../bar", true)]
    [TestCase("../../foo", true)]
    [TestCase("..foo/bar", false)]
    [TestCase("../..foo", false)]
    [TestCase("foo/bar", false)]
    public void HasInvalidTilesheetDirectoryClimb_RequiresSingleLeadingSegment(string path, bool expected)
    {
        ModContentManager.HasInvalidTilesheetDirectoryClimb(path).Should().Be(expected);
    }

    [TestCase("../foo", true)]
    [TestCase("..", true)]
    [TestCase("..foo/bar", false)]
    [TestCase("foo/../bar", false)]
    public void HasLeadingTilesheetDirectoryClimb_MatchesExactSegment(string path, bool expected)
    {
        ModContentManager.HasLeadingTilesheetDirectoryClimb(path).Should().Be(expected);
    }

    [TestCase("../LooseSprites/Cursors.png", "LooseSprites/Cursors")]
    [TestCase("Maps/spring_town.png", "Maps/spring_town")]
    [TestCase("maps/spring_town.PNG", "maps/spring_town")]
    [TestCase("..foo/bar.png", "Maps/..foo/bar")]
    [TestCase("local/bar", "Maps/local/bar")]
    public void GetContentKeyForTilesheetImageSource_RoutesWithoutSplitting(string path, string expected)
    {
        PathUtilities.NormalizeAssetName(ModContentManager.GetContentKeyForTilesheetImageSource(path)).Should().Be(expected);
    }
}
