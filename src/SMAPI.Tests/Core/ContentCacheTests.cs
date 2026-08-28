using System;
using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.Content;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for <see cref="ContentCache"/>.</summary>
[TestFixture]
internal class ContentCacheTests
{
    [Test]
    public void Count_TracksCachedEntries()
    {
        Dictionary<string, object> values = new() { ["first"] = new object() };
        ContentCache cache = new(values);

        cache.Count.Should().Be(1);
        values["second"] = new object();
        cache.Count.Should().Be(2);
        cache.Remove("first", dispose: false);
        cache.Count.Should().Be(1);
    }

    [Test(Description = "Assert that enumerating cache entries doesn't re-hash their keys.")]
    public void GetEntries_EnumeratesWithoutKeyLookups()
    {
        CountingStringComparer comparer = new();
        object value = new();
        Dictionary<string, object> values = new(comparer) { ["asset"] = value };
        ContentCache cache = new(values);
        comparer.HashCalls = 0;

        Dictionary<string, object>.Enumerator enumerator = cache.GetEntries().GetEnumerator();
        enumerator.MoveNext().Should().BeTrue();
        KeyValuePair<string, object> entry = enumerator.Current;

        entry.Key.Should().Be("asset");
        entry.Value.Should().BeSameAs(value);
        enumerator.MoveNext().Should().BeFalse();
        comparer.HashCalls.Should().Be(0);
    }

    [Test(Description = "Assert that warmed cache-entry enumeration doesn't allocate.")]
    [Category("PerformanceRegression")]
    [NonParallelizable]
    public void GetEntries_DoesNotAllocate()
    {
        ContentCache cache = new(new Dictionary<string, object> { ["asset"] = new object() });
        ContentCache.EntryEnumerable entries = cache.GetEntries();
        int count = 0;

        foreach (KeyValuePair<string, object> _ in entries)
            count++;

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            foreach (KeyValuePair<string, object> _ in entries)
                count++;
        }
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        allocatedBytes.Should().Be(0);
        count.Should().Be(10_001);
    }

    [Test(Description = "Assert that removing a cache entry performs one key lookup and reports whether it existed.")]
    [Category("PerformanceRegression")]
    public void Remove_UsesOneLookupAndReportsSuccess()
    {
        CountingStringComparer comparer = new();
        Dictionary<string, object> values = new(comparer) { ["asset"] = new object() };
        ContentCache cache = new(values);
        comparer.HashCalls = 0;

        cache.Remove("asset", dispose: false).Should().BeTrue();
        comparer.HashCalls.Should().Be(1);

        comparer.HashCalls = 0;
        cache.Remove("asset", dispose: false).Should().BeFalse();
        comparer.HashCalls.Should().Be(1);
    }

    /// <summary>A string comparer which counts hash operations.</summary>
    private sealed class CountingStringComparer : IEqualityComparer<string>
    {
        public int HashCalls { get; set; }

        public bool Equals(string? x, string? y)
        {
            return string.Equals(x, y, StringComparison.Ordinal);
        }

        public int GetHashCode(string value)
        {
            this.HashCalls++;
            return value.GetHashCode(StringComparison.Ordinal);
        }
    }
}
