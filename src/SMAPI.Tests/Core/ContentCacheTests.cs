using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.Content;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for <see cref="ContentCache"/>.</summary>
[TestFixture]
internal class ContentCacheTests
{
    [Test(Description = "Assert that enumerating cache entries doesn't re-hash their keys.")]
    public void GetEntries_EnumeratesWithoutKeyLookups()
    {
        CountingStringComparer comparer = new();
        object value = new();
        Dictionary<string, object> values = new(comparer) { ["asset"] = value };
        ContentCache cache = new(values);
        comparer.HashCalls = 0;

        KeyValuePair<string, object> entry = cache.GetEntries().Single();

        entry.Key.Should().Be("asset");
        entry.Value.Should().BeSameAs(value);
        comparer.HashCalls.Should().Be(0);
    }

    [Test(Description = "Assert that removing a cache entry performs one key lookup and reports whether it existed.")]
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
