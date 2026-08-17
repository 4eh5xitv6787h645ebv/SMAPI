using System.Collections.ObjectModel;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.StateTracking.FieldWatchers;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for <see cref="ObservableCollectionWatcher{TValue}"/>.</summary>
[TestFixture]
internal class ObservableCollectionWatcherTests
{
    [Test]
    public void Constructor_ReportsExistingItemsThenTracksTheirRemoval()
    {
        object first = new();
        object second = new();
        ObservableCollection<object> values = [first, second];
        using ObservableCollectionWatcher<object> watcher = new("test", values);

        watcher.IsChanged.Should().BeTrue();
        watcher.Added.Should().Equal(first, second);
        watcher.Removed.Should().BeEmpty();

        watcher.Reset();
        values.RemoveAt(0);

        watcher.IsChanged.Should().BeTrue();
        watcher.Added.Should().BeEmpty();
        watcher.Removed.Should().ContainSingle().Which.Should().BeSameAs(first);
    }

    [Test]
    public void ExistingItems_SupportReplacementAndReset()
    {
        object first = new();
        object second = new();
        object replacement = new();
        ObservableCollection<object> values = [first, second];
        using ObservableCollectionWatcher<object> watcher = new("test", values);
        watcher.Reset();

        values[0] = replacement;

        watcher.Added.Should().ContainSingle().Which.Should().BeSameAs(replacement);
        watcher.Removed.Should().ContainSingle().Which.Should().BeSameAs(first);

        watcher.Reset();
        values.Clear();

        watcher.Added.Should().BeEmpty();
        watcher.Removed.Should().BeEquivalentTo([replacement, second]);
    }
}
