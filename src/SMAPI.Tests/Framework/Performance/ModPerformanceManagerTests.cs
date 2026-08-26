using System;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Framework.Performance;

namespace SMAPI.Tests.Framework.Performance;

/// <summary>Unit tests for <see cref="ModPerformanceManager"/>.</summary>
[TestFixture]
internal sealed class ModPerformanceManagerTests
{
    [Test]
    public void RecordHandler_AggregatesHandlerTickAndLogData()
    {
        long timestamp = 0;
        ModPerformanceManager manager = new(timestampFrequency: 1000, getTimestamp: () => timestamp, getGcCollectionCount: _ => 0);
        manager.Start();
        manager.BeginTick(tick: 42, startTimestamp: 100);

        manager.RecordHandler("Example.Mod", "Example Mod", "GameLoop.UpdateTicked", "Example.Mod.OnTicked", elapsedTimestampTicks: 3, failed: false);
        manager.RecordHandler("Example.Mod", "Example Mod", "GameLoop.UpdateTicked", "Example.Mod.OnTicked", elapsedTimestampTicks: 5, failed: true);
        manager.RecordHandler("Other.Mod", "Other Mod", "Input.ButtonPressed", "Other.Mod.OnButton", elapsedTimestampTicks: 2, failed: false);
        manager.RecordLog("Example.Mod", "Example Mod", LogLevel.Warn);
        manager.RecordLog("Example.Mod", "Example Mod", LogLevel.Error);

        manager.CompleteTick(endTimestamp: 120).Should().BeNull();
        ModPerformanceSnapshot snapshot = manager.GetSnapshot();

        snapshot.CompletedTickCount.Should().Be(1);
        snapshot.Handlers.Should().HaveCount(2);
        snapshot.Handlers.Should().ContainEquivalentOf(new HandlerPerformanceSnapshot("Example.Mod", "Example Mod", "GameLoop.UpdateTicked", "Example.Mod.OnTicked", 2, 8, 5, 1));
        snapshot.ModLogs.Should().ContainEquivalentOf(new ModLogSnapshot("Example.Mod", "Example Mod", 1, 1));
        snapshot.RecentTicks.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new TickPerformanceSnapshot(42, 20, 10, "Example.Mod", "Example Mod", 8, 1)
        );
        snapshot.RecentTicks[0].UnattributedMilliseconds.Should().Be(10);
    }

    [Test]
    public void TickHistory_IsBoundedAndKeepsNewestTicks()
    {
        ModPerformanceManager manager = new(tickHistoryCapacity: 2, timestampFrequency: 1000, getTimestamp: () => 0, getGcCollectionCount: _ => 0);
        manager.Start(logIndividualTicks: true, tickLogThresholdMilliseconds: 0);

        for (uint tick = 1; tick <= 3; tick++)
        {
            manager.BeginTick(tick, startTimestamp: tick * 10);
            manager.CompleteTick(endTimestamp: tick * 10 + tick).Should().NotBeNull();
        }

        ModPerformanceSnapshot snapshot = manager.GetSnapshot();
        snapshot.CompletedTickCount.Should().Be(3);
        snapshot.RecentTicks.Should().SatisfyRespectively(
            second => second.Tick.Should().Be(2),
            third => third.Tick.Should().Be(3)
        );
    }

    [Test]
    public void HandlerCapacity_DoesNotLosePerTickAttribution()
    {
        ModPerformanceManager manager = new(handlerCapacity: 1, timestampFrequency: 1000, getTimestamp: () => 0, getGcCollectionCount: _ => 0);
        manager.Start();
        manager.BeginTick(7, 0);
        manager.RecordHandler("First.Mod", "First", "Event.One", "First.Handler", 3, failed: false);
        manager.RecordHandler("Second.Mod", "Second", "Event.Two", "Second.Handler", 4, failed: false);
        manager.CompleteTick(10);

        ModPerformanceSnapshot snapshot = manager.GetSnapshot();
        snapshot.Handlers.Should().ContainSingle();
        snapshot.OmittedHandlerInvocations.Should().Be(1);
        snapshot.RecentTicks.Should().ContainSingle().Which.InstrumentedModMilliseconds.Should().Be(7);
    }

    [Test]
    public void NestedHandlers_RecordExclusiveTimeWithoutDoubleCounting()
    {
        long timestamp = 0;
        ModPerformanceManager manager = new(timestampFrequency: 1000, getTimestamp: () => timestamp, getGcCollectionCount: _ => 0);
        manager.Start();
        manager.BeginTick(1, 0);

        HandlerTimingToken outer = manager.BeginHandler("Outer.Mod", "Outer", "GameLoop.UpdateTicked", "Outer.OnTick");
        timestamp = 10;
        HandlerTimingToken inner = manager.BeginHandler("Inner.Mod", "Inner", "Content.AssetRequested", "Inner.OnAsset");
        timestamp = 15;
        manager.EndHandler(inner, failed: false);
        timestamp = 20;
        manager.EndHandler(outer, failed: false);
        manager.CompleteTick(25);

        ModPerformanceSnapshot snapshot = manager.GetSnapshot();
        snapshot.Handlers.Should().Contain(entry => entry.ModId == "Outer.Mod" && entry.TotalMilliseconds == 15);
        snapshot.Handlers.Should().Contain(entry => entry.ModId == "Inner.Mod" && entry.TotalMilliseconds == 5);
        snapshot.RecentTicks.Should().ContainSingle().Which.InstrumentedModMilliseconds.Should().Be(20);
        snapshot.RecentTicks[0].UnattributedMilliseconds.Should().Be(5);
    }

    [Test]
    public void CompleteTick_OnlyReturnsTicksAtConfiguredThreshold()
    {
        ModPerformanceManager manager = new(timestampFrequency: 1000, getTimestamp: () => 0, getGcCollectionCount: _ => 0);
        manager.Start(logIndividualTicks: true, tickLogThresholdMilliseconds: 16.667);

        manager.BeginTick(1, 0);
        manager.CompleteTick(16).Should().BeNull();

        manager.BeginTick(2, 20);
        TickPerformanceSnapshot? loggedTick = manager.CompleteTick(37);
        loggedTick.Should().NotBeNull();
        loggedTick!.Value.Tick.Should().Be(2);
    }

    [Test]
    public void GameUpdate_SplitsTickAttribution()
    {
        ModPerformanceManager manager = new(timestampFrequency: 1000, getTimestamp: () => 0, getGcCollectionCount: _ => 0);
        manager.Start();
        manager.BeginTick(tick: 1, startTimestamp: 0);

        manager.BeginGameUpdate(startTimestamp: 2);
        manager.RecordHandler("During.Mod", "During Mod", "GameLoop.UpdateTicked", "During.Mod.OnTicked", elapsedTimestampTicks: 3, failed: false);
        manager.EndGameUpdate(endTimestamp: 12);
        manager.RecordHandler("Outside.Mod", "Outside Mod", "Display.Rendered", "Outside.Mod.OnRendered", elapsedTimestampTicks: 4, failed: false);
        manager.CompleteTick(endTimestamp: 20);

        ModPerformanceSnapshot snapshot = manager.GetSnapshot();
        TickPerformanceSnapshot tick = snapshot.RecentTicks.Should().ContainSingle().Subject;
        tick.TotalMilliseconds.Should().Be(20);
        tick.InstrumentedModMilliseconds.Should().Be(7);
        tick.GameUpdateMilliseconds.Should().Be(10);
        tick.InstrumentedDuringGameUpdateMilliseconds.Should().Be(3);
        tick.GameUpdateExclusiveMilliseconds.Should().Be(7);
        tick.OutsideGameUpdateMilliseconds.Should().Be(6);

        snapshot.TickTotalMilliseconds.Should().Be(20);
        snapshot.GameUpdateMilliseconds.Should().Be(10);
        snapshot.TickInstrumentedMilliseconds.Should().Be(7);
        snapshot.InstrumentedDuringGameUpdateMilliseconds.Should().Be(3);
        snapshot.GameUpdateExclusiveMilliseconds.Should().Be(7);
        snapshot.OutsideGameUpdateMilliseconds.Should().Be(6);
    }

    [Test]
    public void GcCollections_AreCountedPerTickAndSample()
    {
        int[] collections = [0, 0, 0];
        ModPerformanceManager manager = new(timestampFrequency: 1000, getTimestamp: () => 0, getGcCollectionCount: generation => collections[generation]);
        manager.Start();

        manager.BeginTick(tick: 1, startTimestamp: 0);
        collections = [2, 1, 0];
        manager.CompleteTick(endTimestamp: 10);

        manager.BeginTick(tick: 2, startTimestamp: 10);
        collections = [3, 1, 1];
        manager.CompleteTick(endTimestamp: 20);

        ModPerformanceSnapshot snapshot = manager.GetSnapshot();
        snapshot.RecentTicks.Should().SatisfyRespectively(
            first =>
            {
                first.Gen0Collections.Should().Be(2);
                first.Gen1Collections.Should().Be(1);
                first.Gen2Collections.Should().Be(0);
            },
            second =>
            {
                second.Gen0Collections.Should().Be(1);
                second.Gen1Collections.Should().Be(0);
                second.Gen2Collections.Should().Be(1);
            }
        );
        snapshot.Gen0Collections.Should().Be(3);
        snapshot.Gen1Collections.Should().Be(1);
        snapshot.Gen2Collections.Should().Be(1);
    }

    [Test]
    public void DisabledManager_DoesNotCreateHandlerCounters()
    {
        ModPerformanceManager manager = new(timestampFrequency: 1000, getTimestamp: () => 0, getGcCollectionCount: _ => 0);
        manager.RecordHandler("Example.Mod", "Example", "Event", "Handler", 10, failed: true);
        manager.RecordLog("Example.Mod", "Example", LogLevel.Error);
        manager.RecordLog("SMAPI", "SMAPI", LogLevel.Error);
        manager.RecordLog("game", "game", LogLevel.Error);

        manager.GetSnapshot().Handlers.Should().BeEmpty();
        manager.GetSnapshot().ModLogs.Should().ContainSingle().Which.Should().BeEquivalentTo(new ModLogSnapshot("Example.Mod", "Example", 0, 1));
    }
}
