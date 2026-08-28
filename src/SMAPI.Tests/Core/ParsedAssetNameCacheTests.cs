using System;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.Content;
using StardewValley;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for <see cref="ParsedAssetNameCache"/>.</summary>
[TestFixture]
internal class ParsedAssetNameCacheTests
{
    [Test(Description = "Assert that exact repeated inputs reuse an immutable parsed asset name.")]
    public void GetOrAdd_ReusesExactInput()
    {
        ParsedAssetNameCache cache = new(ParseLocale);

        AssetName first = cache.GetOrAdd("Characters/Dialogue/Abigail.fr-FR", allowLocales: true);
        AssetName second = cache.GetOrAdd("Characters/Dialogue/Abigail.fr-FR", allowLocales: true);

        second.Should().BeSameAs(first);
        second.BaseName.Should().Be("Characters/Dialogue/Abigail");
        second.LocaleCode.Should().Be("fr-FR");
    }

    [Test(Description = "Assert that locale parsing and exact input casing are distinct cache semantics.")]
    public void GetOrAdd_SeparatesParsingModesAndCasing()
    {
        ParsedAssetNameCache cache = new(ParseLocale);

        AssetName localized = cache.GetOrAdd("Data/Mail.fr-FR", allowLocales: true);
        AssetName unlocalized = cache.GetOrAdd("Data/Mail.fr-FR", allowLocales: false);
        AssetName differentlyCased = cache.GetOrAdd("data/mail.fr-FR", allowLocales: true);

        unlocalized.Should().NotBeSameAs(localized);
        unlocalized.BaseName.Should().Be("Data/Mail.fr-FR");
        differentlyCased.Should().NotBeSameAs(localized);
        differentlyCased.Name.Should().Be("data/mail.fr-FR");
    }

    [Test(Description = "Assert that insertion-order eviction bounds retained parsed names.")]
    public void GetOrAdd_EvictsOldestEntry()
    {
        ParsedAssetNameCache cache = new(ParseLocale, capacity: 2);
        AssetName first = cache.GetOrAdd("Data/First", allowLocales: true);
        AssetName second = cache.GetOrAdd("Data/Second", allowLocales: true);

        cache.GetOrAdd("Data/Third", allowLocales: true);

        cache.GetOrAdd("Data/Second", allowLocales: true).Should().BeSameAs(second);
        cache.GetOrAdd("Data/First", allowLocales: true).Should().NotBeSameAs(first);
    }

    [Test(Description = "Assert that concurrent first access converges on one cached instance.")]
    public void GetOrAdd_ConvergesAcrossThreads()
    {
        ParsedAssetNameCache cache = new(ParseLocale);
        AssetName[] results = new AssetName[1_000];

        Parallel.For(0, results.Length, i => results[i] = cache.GetOrAdd("Maps/Farm", allowLocales: true));

        results.Should().OnlyContain(value => ReferenceEquals(value, results[0]));
    }

    [Test(Description = "Assert that clearing the cache reparses names against updated locale definitions.")]
    public void Clear_ReparsesLocaleSuffixes()
    {
        bool localeExists = false;
        ParsedAssetNameCache cache = new(locale => localeExists && locale == "custom" ? LocalizedContentManager.LanguageCode.mod : null);
        AssetName before = cache.GetOrAdd("Data/Mail.custom", allowLocales: true);

        localeExists = true;
        cache.Clear();
        AssetName after = cache.GetOrAdd("Data/Mail.custom", allowLocales: true);

        after.Should().NotBeSameAs(before);
        before.LocaleCode.Should().BeNull();
        after.LocaleCode.Should().Be("custom");
        after.LanguageCode.Should().Be(LocalizedContentManager.LanguageCode.mod);
    }

    [Test(Description = "Assert that warmed cache hits allocate no managed memory.")]
    [Category("PerformanceRegression")]
    [NonParallelizable]
    public void GetOrAdd_CacheHitDoesNotAllocate()
    {
        ParsedAssetNameCache cache = new(ParseLocale);
        for (int i = 0; i < 1_000; i++)
            cache.GetOrAdd("Maps/Farm", allowLocales: true);

        const int iterations = 10_000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
            cache.GetOrAdd("Maps/Farm", allowLocales: true);
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        allocatedBytes.Should().Be(0);
    }

    /// <summary>Parse the test locale.</summary>
    private static LocalizedContentManager.LanguageCode? ParseLocale(string locale)
    {
        return locale.Equals("fr-FR", StringComparison.OrdinalIgnoreCase)
            ? LocalizedContentManager.LanguageCode.fr
            : null;
    }
}
