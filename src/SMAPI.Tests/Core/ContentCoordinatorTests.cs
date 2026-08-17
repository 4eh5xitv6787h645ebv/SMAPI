using System;
using System.Collections.Generic;
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
