using System;
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
}
