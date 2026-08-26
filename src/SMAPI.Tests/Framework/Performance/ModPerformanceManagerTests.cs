using System;
using System.Threading.Tasks;
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
        long timestamp = 0;
        ModPerformanceManager manager = new(timestampFrequency: 1000, getTimestamp: () => timestamp, getGcCollectionCount: _ => 0);
        manager.Start();
        manager.BeginTick(tick: 1, startTimestamp: 0);

        timestamp = 2;
        manager.BeginGameUpdate();
        manager.RecordHandler("During.Mod", "During Mod", "GameLoop.UpdateTicked", "During.Mod.OnTicked", elapsedTimestampTicks: 3, failed: false);
        timestamp = 12;
        manager.EndGameUpdate();
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
    public void TickAttribution_ExcludesOverlappingBackgroundCallbacksAndErrors()
    {
        long timestamp = 0;
        ModPerformanceManager manager = new(timestampFrequency: 1000, getTimestamp: () => timestamp, getGcCollectionCount: _ => 0);
        manager.Start();
        manager.BeginTick(tick: 1, startTimestamp: 0);
        timestamp = 2;
        manager.BeginGameUpdate();

        Task.Run(() =>
        {
            manager.RecordHandler("Background.Mod", "Background Mod", "Background", "Background.Run", elapsedTimestampTicks: 50, failed: false);
            manager.RecordLog("Background.Mod", "Background Mod", LogLevel.Error);
        }).GetAwaiter().GetResult();

        manager.RecordHandler("Main.Mod", "Main Mod", "GameLoop.UpdateTicked", "Main.Mod.OnTicked", elapsedTimestampTicks: 3, failed: false);
        timestamp = 12;
        manager.EndGameUpdate();
        manager.CompleteTick(endTimestamp: 20);

        ModPerformanceSnapshot snapshot = manager.GetSnapshot();
        snapshot.Handlers.Should().Contain(entry => entry.ModId == "Background.Mod" && entry.TotalMilliseconds == 50);
        snapshot.ModLogs.Should().Contain(entry => entry.ModId == "Background.Mod" && entry.ErrorCount == 1);
        snapshot.RecentTicks.Should().ContainSingle().Which.Should().Match<TickPerformanceSnapshot>(tick =>
            tick.InstrumentedModMilliseconds == 3
            && tick.InstrumentedDuringGameUpdateMilliseconds == 3
            && tick.ErrorCount == 0
            && tick.SlowestModId == "Main.Mod"
            && tick.TimingPartitionIsValid
        );
    }

    [Test]
    public void GameUpdate_AccumulatesMultipleSequentialWindows()
    {
        long timestamp = 0;
        ModPerformanceManager manager = new(timestampFrequency: 1000, getTimestamp: () => timestamp, getGcCollectionCount: _ => 0);
        manager.Start();
        manager.BeginTick(tick: 1, startTimestamp: 0);

        timestamp = 2;
        manager.BeginGameUpdate();
        timestamp = 7;
        manager.EndGameUpdate();
        timestamp = 10;
        manager.BeginGameUpdate();
        manager.RecordHandler("Second.Mod", "Second Mod", "GameLoop.UpdateTicked", "Second.Mod.OnTicked", elapsedTimestampTicks: 2, failed: false);
        timestamp = 16;
        manager.EndGameUpdate();
        manager.CompleteTick(endTimestamp: 20);

        TickPerformanceSnapshot tick = manager.GetSnapshot().RecentTicks.Should().ContainSingle().Subject;
        tick.GameUpdateMilliseconds.Should().Be(11);
        tick.InstrumentedDuringGameUpdateMilliseconds.Should().Be(2);
        tick.GameUpdateExclusiveMilliseconds.Should().Be(9);
        tick.OutsideGameUpdateMilliseconds.Should().Be(9);
        tick.TimingPartitionIsValid.Should().BeTrue();
    }

    [Test]
    public void GameUpdateBoundary_RejectsWrongThread()
    {
        ModPerformanceManager manager = new(timestampFrequency: 1000, getTimestamp: () => 0, getGcCollectionCount: _ => 0);
        manager.Start();
        manager.BeginTick(tick: 1, startTimestamp: 0);

        Task.Run(() =>
        {
            manager.BeginGameUpdate();
            manager.EndGameUpdate();
        }).GetAwaiter().GetResult();
        manager.CompleteTick(endTimestamp: 10);

        TickPerformanceSnapshot tick = manager.GetSnapshot().RecentTicks.Should().ContainSingle().Subject;
        tick.GameUpdateMilliseconds.Should().Be(0);
        tick.TimingPartitionIsValid.Should().BeFalse();
    }

    [Test]
    public void GameUpdateBoundary_CanBeClosedInFinallyAfterException()
    {
        long timestamp = 0;
        ModPerformanceManager manager = new(timestampFrequency: 1000, getTimestamp: () => timestamp, getGcCollectionCount: _ => 0);
        manager.Start();
        manager.BeginTick(tick: 1, startTimestamp: 0);

        Action run = () =>
        {
            timestamp = 2;
            manager.BeginGameUpdate();
            try
            {
                throw new InvalidOperationException("test");
            }
            finally
            {
                timestamp = 8;
                manager.EndGameUpdate();
            }
        };
        run.Should().Throw<InvalidOperationException>();
        manager.CompleteTick(endTimestamp: 10);

        TickPerformanceSnapshot tick = manager.GetSnapshot().RecentTicks.Should().ContainSingle().Subject;
        tick.GameUpdateMilliseconds.Should().Be(6);
        tick.TimingPartitionIsValid.Should().BeTrue();
    }

    [Test]
    public void CompleteTick_PreservesInvalidRawPartitionAndFlagsIt()
    {
        ModPerformanceManager manager = new(timestampFrequency: 1000, getTimestamp: () => 0, getGcCollectionCount: _ => 0);
        manager.Start();
        manager.BeginTick(tick: 1, startTimestamp: 0);
        manager.RecordHandler("Impossible.Mod", "Impossible Mod", "Event", "Handler", elapsedTimestampTicks: 15, failed: false);
        manager.CompleteTick(endTimestamp: 10);

        ModPerformanceSnapshot snapshot = manager.GetSnapshot();
        TickPerformanceSnapshot tick = snapshot.RecentTicks.Should().ContainSingle().Subject;
        tick.TimingPartitionIsValid.Should().BeFalse();
        tick.OutsideGameUpdateMilliseconds.Should().Be(-5);
        snapshot.TimingPartitionIsValid.Should().BeFalse();
        snapshot.InvalidTimingPartitionTickCount.Should().Be(1);
        snapshot.OutsideGameUpdateMilliseconds.Should().Be(-5);
    }

    [Test]
    public void EndHandler_DoesNotRecordInvocationFromPreviousSampleGeneration()
    {
        long timestamp = 0;
        ModPerformanceManager manager = new(timestampFrequency: 1000, getTimestamp: () => timestamp, getGcCollectionCount: _ => 0);
        manager.Start();
        HandlerTimingToken stale = manager.BeginHandler("Stale.Mod", "Stale Mod", "Event", "Handler");

        timestamp = 5;
        manager.Start();
        timestamp = 10;
        manager.EndHandler(stale, failed: false);

        manager.GetSnapshot().Handlers.Should().BeEmpty();
    }

    [Test]
    public void GameUpdateBoundary_DoesNotAllocateAfterWarmup()
    {
        ModPerformanceManager manager = new(timestampFrequency: 1000, getTimestamp: () => 0, getGcCollectionCount: _ => 0);
        manager.Start();
        manager.BeginTick(tick: 1, startTimestamp: 0);
        manager.BeginGameUpdate();
        manager.EndGameUpdate();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            manager.BeginGameUpdate();
            manager.EndGameUpdate();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().Be(0);
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
        snapshot.CaptureGen0Collections.Should().Be(3);
        snapshot.CaptureGen1Collections.Should().Be(1);
        snapshot.CaptureGen2Collections.Should().Be(1);
        snapshot.CaptureGcCollectionDataIsValid.Should().BeTrue();
    }

    [Test]
    public void GcCollections_StopFreezesCaptureDelta()
    {
        int[] collections = [0, 0, 0];
        ModPerformanceManager manager = new(timestampFrequency: 1000, getTimestamp: () => 0, getGcCollectionCount: generation => collections[generation]);
        manager.Start();
        collections = [5, 2, 1];
        manager.Stop();
        collections = [9, 4, 3];
        manager.Stop();

        ModPerformanceSnapshot snapshot = manager.GetSnapshot();
        snapshot.CaptureGen0Collections.Should().Be(5);
        snapshot.CaptureGen1Collections.Should().Be(2);
        snapshot.CaptureGen2Collections.Should().Be(1);
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
