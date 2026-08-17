using System;
using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Framework.Content;

namespace SMAPI.Tests.Core;

[TestFixture]
internal class AssetsInvalidatedEventArgsTests
{
    [Test(Description = "Assert that construction defers the locale-stripped set and avoids its allocation.")]
    public void Constructor_DefersNamesWithoutLocaleAllocations()
    {
        IAssetName[] names = [AssetName.Parse("Data/Test", static _ => null)];
        AssetsInvalidatedEventArgs? last = null;
        for (int i = 0; i < 1_000; i++)
            last = new AssetsInvalidatedEventArgs(names);

        const int iterations = 10_000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
            last = new AssetsInvalidatedEventArgs(names);
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(last);

        (allocatedBytes / iterations).Should().BeLessThan(280, "the locale-stripped set should be deferred until requested");
    }

    [Test(Description = "Assert that a nonlocalized invalidation reuses the original immutable view.")]
    public void NamesWithoutLocale_ReusesNonlocalizedSet()
    {
        AssetsInvalidatedEventArgs args = new([AssetName.Parse("Data/Test", static _ => null)]);

        args.NamesWithoutLocale.Should().BeSameAs(args.Names);
    }

    [Test(Description = "Assert that localized invalidations strip and cache locale suffixes.")]
    public void NamesWithoutLocale_StripsLocalesOnce()
    {
        IAssetName localized = new AssetName("Data/Test", "fr-FR", StardewValley.LocalizedContentManager.LanguageCode.fr);
        AssetsInvalidatedEventArgs args = new([localized]);

        IReadOnlySet<IAssetName> first = args.NamesWithoutLocale;

        args.NamesWithoutLocale.Should().BeSameAs(first);
        first.Should().ContainSingle().Which.Name.Should().Be("Data/Test");
        first.Should().NotContain(localized);
    }
}
