using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Framework;
using StardewModdingAPI.Framework.Content;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for <see cref="ContentCoordinator"/>.</summary>
[TestFixture]
internal class ContentCoordinatorTests
{
    [TestCase("SMAPI/Example.Mod/path/to/asset", "Example.Mod", "path/to/asset")]
    [TestCase("smapi\\Example.Mod\\path\\to\\asset", "Example.Mod", "path\\to\\asset")]
    [TestCase("SmApI/EXAMPLE/asset", "EXAMPLE", "asset")]
    public void TryParseManagedAssetKeyParts_ParsesValidKeys(string key, string expectedModId, string expectedRelativePath)
    {
        bool parsed = ContentCoordinator.TryParseManagedAssetKeyParts(key, out string? contentManagerId, out string? relativePath);

        parsed.Should().BeTrue();
        contentManagerId.Should().Be(Path.Combine("SMAPI", expectedModId));
        relativePath.Should().Be(expectedRelativePath);
    }

    [TestCase("SMAPI")]
    [TestCase("SMAPI/")]
    [TestCase("SMAPIFoo/mod/asset")]
    [TestCase("SMAPI//asset")]
    [TestCase("SMAPI/mod")]
    [TestCase("SMAPI/mod///")]
    public void TryParseManagedAssetKeyParts_RejectsInvalidKeys(string key)
    {
        ContentCoordinator.TryParseManagedAssetKeyParts(key, out string? contentManagerId, out string? relativePath).Should().BeFalse();
        contentManagerId.Should().BeNull();
        relativePath.Should().BeNull();
    }

    [TestCase("SMAPI/mod/asset", true)]
    [TestCase("smapi\\mod\\asset", true)]
    [TestCase("SMAPIFoo/mod/asset", false)]
    [TestCase("Other/mod/asset", false)]
    public void HasManagedAssetPrefix_RequiresExactPathSegment(string key, bool expected)
    {
        ContentCoordinator.HasManagedAssetPrefix(key).Should().Be(expected);
    }

    [TestCase("SMAPI/Example.Mod", "smapi/example.mod", true)]
    [TestCase("SMAPI/Example.Mod", "SMAPI/Other.Mod", false)]
    public void AreManagedContentManagerNamesEqual_MatchesRoutingIdentity(string left, string right, bool expected)
    {
        ContentCoordinator.AreManagedContentManagerNamesEqual(left, right).Should().Be(expected);
    }

    [Test(Description = "Assert that the deferred invalidation report preserves its contents and case-insensitive asset order.")]
    public void FormatInvalidationReport_FormatsSortedSummary()
    {
        IAssetName alpha = new AssetName("Data/alpha", localeCode: null, languageCode: null);
        IAssetName zed = new AssetName("Data/Zed", localeCode: null, languageCode: null);
        Dictionary<IAssetName, Type> invalidated = new()
        {
            [zed] = typeof(string),
            [alpha] = typeof(int)
        };
        Dictionary<IAssetName, bool> propagated = new()
        {
            [zed] = false,
            [alpha] = true
        };

        string result = ContentCoordinator.FormatInvalidationReport(invalidated, propagated, updatedWarpRoutes: true);

        result.Should().Be(string.Join(
            Environment.NewLine,
            "Invalidated 2 asset names (Data/alpha, Data/Zed).",
            "Propagated 1 core assets (Data/alpha).",
            "Updated NPC warp route cache."
        ));
    }
}
