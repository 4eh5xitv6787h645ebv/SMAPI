using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.StateTracking;
using StardewValley;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for <see cref="LocationTracker"/>.</summary>
[TestFixture]
internal class LocationTrackerTests
{
    [Test(Description = "Assert that net collection changes push aggregate location state without an update-time watcher poll.")]
    public void Collections_PushAggregateChangeState()
    {
        // arrange
        GameLocation location = new();
        using LocationTracker tracker = new(location);
        Debris debris = new();

        // act: add debris through a tracked net collection
        location.debris.Add(debris);

        // assert: the aggregate flag changed before Update was called
        tracker.IsChanged.Should().BeTrue();
        tracker.DebrisWatcher.IsChanged.Should().BeTrue();

        // act: consume and reset the change
        tracker.Update(trackChestInventoryChanges: false);
        tracker.Reset();

        // assert: reset restores the baseline, and removal pushes another change
        tracker.IsChanged.Should().BeFalse();
        location.debris.Remove(debris);
        tracker.IsChanged.Should().BeTrue();
        tracker.DebrisWatcher.IsChanged.Should().BeTrue();
    }
}
