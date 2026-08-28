using System;
using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.Utilities;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for <see cref="TickCacheDictionary{TKey,TValue}"/>.</summary>
[TestFixture]
internal class TickCacheDictionaryTests
{
    [Test]
    public void RemoveWhere_RemovesEveryMatchingCompositeKey()
    {
        TickCacheDictionary<(string Name, Type Type), int> cache = new();
        cache.GetOrSet(("Target", typeof(string)), static () => 1);
        cache.GetOrSet(("Target", typeof(object)), static () => 2);
        cache.GetOrSet(("Other", typeof(string)), static () => 3);

        int removed = cache.RemoveWhere("Target", static (key, name) => key.Name == name);

        removed.Should().Be(2);
        cache.GetOrSet(("Target", typeof(string)), static () => 4).Should().Be(4);
        cache.GetOrSet(("Target", typeof(object)), static () => 5).Should().Be(5);
        cache.GetOrSet(("Other", typeof(string)), static () => 6).Should().Be(3);
    }

    [Test]
    [Category("PerformanceRegression")]
    public void RemoveWhere_ClearsMultipleNamesInOnePass()
    {
        TickCacheDictionary<(string Name, Type Type), int> cache = new();
        cache.GetOrSet(("First", typeof(string)), static () => 1);
        cache.GetOrSet(("First", typeof(object)), static () => 2);
        cache.GetOrSet(("Second", typeof(string)), static () => 3);
        cache.GetOrSet(("Other", typeof(string)), static () => 4);
        HashSet<string> invalidatedNames = ["First", "Second"];
        int visited = 0;

        int removed = cache.RemoveWhere(
            invalidatedNames,
            (key, names) =>
            {
                visited++;
                return names.Contains(key.Name);
            }
        );

        visited.Should().Be(4);
        removed.Should().Be(3);
        cache.GetOrSet(("First", typeof(string)), static () => 5).Should().Be(5);
        cache.GetOrSet(("First", typeof(object)), static () => 6).Should().Be(6);
        cache.GetOrSet(("Second", typeof(string)), static () => 7).Should().Be(7);
        cache.GetOrSet(("Other", typeof(string)), static () => 8).Should().Be(4);
    }

    [Test(Description = "Assert that a warmed stateful operation-cache invalidation scan doesn't allocate.")]
    [Category("PerformanceRegression")]
    [NonParallelizable]
    public void RemoveWhere_WarmedNonmatchingPassDoesNotAllocate()
    {
        TickCacheDictionary<(string Name, Type Type), int> cache = new();
        cache.GetOrSet(("Other", typeof(string)), static () => 1);
        HashSet<string> invalidatedNames = ["First", "Second"];
        Func<(string Name, Type Type), HashSet<string>, bool> shouldRemove = static (key, names) => names.Contains(key.Name);

        for (int i = 0; i < 10_000; i++)
            cache.RemoveWhere(invalidatedNames, shouldRemove);

        const int iterations = 10_000;
        int removed = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
            removed += cache.RemoveWhere(invalidatedNames, shouldRemove);
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        removed.Should().Be(0);
        allocatedBytes.Should().Be(0, "the invalidated-name set and static predicate should be passed without closures or iterator objects");
    }
}
