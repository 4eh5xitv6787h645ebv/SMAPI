using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.StateTracking.FieldWatchers;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for set-based list change tracking.</summary>
[TestFixture]
internal class ComparableListWatcherTests
{
    [Test(Description = "Assert that an unchanged ordered list bypasses hash-set rebuilding.")]
    public void Update_UnchangedSequenceDoesNotRehash()
    {
        object first = new();
        object second = new();
        List<object> values = [first, second];
        CountingReferenceComparer comparer = new();
        using ComparableListWatcher<object> watcher = new("values", values, comparer);

        watcher.Update();
        watcher.Reset();
        comparer.ResetCounts();

        watcher.Update();

        watcher.IsChanged.Should().BeFalse();
        comparer.HashCalls.Should().Be(0);
        comparer.EqualsCalls.Should().Be(2);
    }

    [Test(Description = "Assert that reordering doesn't report set changes and establishes a new fast-path sequence.")]
    public void Update_ReorderRetainsSetSemantics()
    {
        object first = new();
        object second = new();
        List<object> values = [first, second];
        CountingReferenceComparer comparer = new();
        using ComparableListWatcher<object> watcher = new("values", values, comparer);
        watcher.Update();
        watcher.Reset();

        values.Reverse();
        watcher.Update();
        watcher.IsChanged.Should().BeFalse();
        watcher.Added.Should().BeEmpty();
        watcher.Removed.Should().BeEmpty();

        comparer.ResetCounts();
        watcher.Update();
        comparer.HashCalls.Should().Be(0);
    }

    [Test(Description = "Assert that reference additions and removals retain the existing set diff behavior.")]
    public void Update_ReportsReferenceSetChanges()
    {
        object retained = new();
        object removed = new();
        object added = new();
        List<object> values = [retained, removed];
        using ComparableListWatcher<object> watcher = new("values", values, new CountingReferenceComparer());
        watcher.Update();
        watcher.Reset();

        values[1] = added;
        watcher.Update();

        watcher.IsChanged.Should().BeTrue();
        watcher.Added.Should().Equal(added);
        watcher.Removed.Should().Equal(removed);
    }

    [Test(Description = "Assert that duplicate-count changes don't become public set changes.")]
    public void Update_DuplicateCountRetainsSetSemantics()
    {
        object value = new();
        List<object> values = [value, value];
        using ComparableListWatcher<object> watcher = new("values", values, new CountingReferenceComparer());
        watcher.Update();
        watcher.Reset();

        values.RemoveAt(1);
        watcher.Update();

        watcher.IsChanged.Should().BeFalse();
        watcher.Added.Should().BeEmpty();
        watcher.Removed.Should().BeEmpty();
    }

    /// <summary>A reference comparer which records whether the hash-set path ran.</summary>
    private sealed class CountingReferenceComparer : IEqualityComparer<object>
    {
        public int EqualsCalls { get; private set; }
        public int HashCalls { get; private set; }

        bool IEqualityComparer<object>.Equals(object? x, object? y)
        {
            this.EqualsCalls++;
            return ReferenceEquals(x, y);
        }

        int IEqualityComparer<object>.GetHashCode(object obj)
        {
            this.HashCalls++;
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        public void ResetCounts()
        {
            this.EqualsCalls = 0;
            this.HashCalls = 0;
        }
    }
}
