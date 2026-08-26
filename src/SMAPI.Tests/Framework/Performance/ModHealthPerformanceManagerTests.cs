using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Framework.Health;
using StardewModdingAPI.Framework.Performance;

namespace SMAPI.Tests.Framework.Performance;

/// <summary>Tests for the bounded health-oriented timing view.</summary>
[TestFixture]
internal sealed class ModHealthPerformanceManagerTests
{
    [Test]
    public void Callbacks_RetainOrthogonalDimensionsAndOnBehalfOfIdentity()
    {
        ModPerformanceManager manager = CreateManager();
        manager.Start();
        manager.RecordHandler("Framework.Mod", "Framework", "Content.Load", "Framework.Load", ModHealthExecutionPhase.Update, ModHealthOperationKind.ContentLoad, "Pack.One", 4, failed: false);
        manager.RecordHandler("Framework.Mod", "Framework", "Content.Load", "Framework.Load", ModHealthExecutionPhase.Background, ModHealthOperationKind.ContentLoad, "Pack.Two", 6, failed: true);

        ModHealthPerformanceSnapshot? healthOrNull = manager.GetSnapshot().Health;
        healthOrNull.Should().NotBeNull();
        ModHealthPerformanceSnapshot health = healthOrNull!;
        health.Callbacks.Should().SatisfyRespectively(
            first =>
            {
                first.Phase.Should().Be(ModHealthExecutionPhase.Update);
                first.Operation.Should().Be(ModHealthOperationKind.ContentLoad);
                first.EventName.Should().Be("Content.Load");
                first.OnBehalfOfModId.Should().Be("Pack.One");
                first.FailureCount.Should().Be(0);
            },
            second =>
            {
                second.Phase.Should().Be(ModHealthExecutionPhase.Background);
                second.OnBehalfOfModId.Should().Be("Pack.Two");
                second.FailureCount.Should().Be(1);
            }
        );
    }

    [Test]
    public void RecentAndWorstUpdates_RolloverIndependentlyAndUseCaptureSequenceForTies()
    {
        ModPerformanceManager manager = CreateManager(tickHistoryCapacity: 2, worstTickCapacity: 3);
        manager.Start();
        CompleteTick(manager, uint.MaxValue, 10);
        CompleteTick(manager, 0, 50);
        CompleteTick(manager, 1, 20);
        CompleteTick(manager, 2, 50);

        ModHealthPerformanceSnapshot health = manager.GetSnapshot().Health!;
        health.RecentUpdates.Select(update => update.Tick).Should().Equal(1, 2);
        health.WorstUpdates.Select(update => update.Tick).Should().Equal(0, 2, 1);
        health.Omissions.RecentUpdates.Should().Be(2);
        health.Omissions.WorstUpdates.Should().Be(1);
    }

    [Test]
    public void Histogram_UsesFixedBucketsBoundariesThresholdsAndOverflowAccounting()
    {
        ModPerformanceManager manager = CreateManager(timestampFrequency: 8000);
        manager.Start();
        CompleteTick(manager, 1, timestampTicks: 0); // under 0.125ms
        CompleteTick(manager, 2, timestampTicks: 1); // exactly 0.125ms
        CompleteTick(manager, 3, timestampTicks: 2); // exactly 0.25ms
        CompleteTick(manager, 4, timestampTicks: 65_536); // exactly 8192ms
        CompleteTick(manager, 5, timestampTicks: 65_537); // overflow

        ModHealthTimingHistogramSnapshot histogram = manager.GetSnapshot().Health!.Histogram;
        histogram.Buckets.Should().HaveCount(ModHealthTimingHistogramSnapshot.BucketCount);
        histogram.Count.Should().Be(5);
        histogram.UnderflowCount.Should().Be(1);
        histogram.OverflowCount.Should().Be(1);
        histogram.Buckets[0].Should().Be(1);
        histogram.Buckets[16].Should().Be(1);
        histogram.Buckets[^1].Should().Be(1);
        histogram.MinimumMilliseconds.Should().Be(0);
        histogram.MaximumMilliseconds.Should().BeApproximately(8192.125, 0.000001);
        histogram.SumMilliseconds.Should().BeApproximately(16_384.5, 0.000001);
        histogram.P50Milliseconds.Should().BeGreaterThan(0.25).And.BeLessThan(0.27);
        histogram.P99Milliseconds.Should().BeApproximately(8192.125, 0.000001);
        histogram.MaximumRelativeBucketError.Should().BeApproximately(Math.Pow(2, 1d / 16) - 1, 0.000000001);
        histogram.Thresholds.Single(entry => entry.Milliseconds == 1000).Count.Should().Be(2);
    }

    [Test]
    public void Histogram_RecordsExactNamedThresholdBoundaries()
    {
        ModPerformanceManager manager = CreateManager(timestampFrequency: 1_000_000);
        manager.Start();
        foreach ((uint tick, long microseconds) in new (uint, long)[]
        {
            (1, 16_666),
            (2, 16_667),
            (3, 33_333),
            (4, 50_000),
            (5, 100_000),
            (6, 250_000),
            (7, 500_000),
            (8, 1_000_000)
        })
            CompleteTick(manager, tick, microseconds);

        ModHealthTimingHistogramSnapshot histogram = manager.GetSnapshot().Health!.Histogram;
        histogram.Thresholds.Select(entry => entry.Count).Should().Equal(7, 6, 5, 4, 3, 2, 1);
    }

    [Test]
    public void Episodes_BridgeOneFastUpdateCloseAfterTwoAndProjectOpenEpisode()
    {
        ModPerformanceManager manager = CreateManager();
        manager.Start(logIndividualTicks: false, tickLogThresholdMilliseconds: 10);
        CompleteTick(manager, 1, 12);
        CompleteTick(manager, 2, 5);
        CompleteTick(manager, 3, 20);
        CompleteTick(manager, 4, 5);
        CompleteTick(manager, 5, 5);
        CompleteTick(manager, 6, 30);

        ModHealthPerformanceSnapshot active = manager.GetSnapshot().Health!;
        active.Episodes.Should().HaveCount(2);
        active.Episodes[0].RepresentativeTick.Should().Be(6);
        active.Episodes[1].Should().Match<ModHealthSlowEpisodeSnapshot>(episode =>
            episode.FirstTick == 1
            && episode.LastTick == 3
            && episode.QualifyingUpdateCount == 2
            && episode.MaximumMilliseconds == 20
            && episode.SummedQualifyingMilliseconds == 32
            && episode.RepresentativeTick == 3
        );

        manager.Stop();
        manager.GetSnapshot().Health!.Episodes.Should().BeEquivalentTo(active.Episodes, options => options.WithStrictOrdering());
    }

    [Test]
    public void Episodes_RankByMaximumThenSumThenEarliestAndReportCapacityOmissions()
    {
        ModPerformanceManager manager = CreateManager(slowEpisodeCapacity: 2);
        manager.Start(logIndividualTicks: false, tickLogThresholdMilliseconds: 10);
        CompleteEpisode(manager, firstTick: 1, 20);
        CompleteEpisode(manager, firstTick: 10, 20, 20);
        CompleteEpisode(manager, firstTick: 20, 20);
        manager.Stop();

        ModHealthPerformanceSnapshot health = manager.GetSnapshot().Health!;
        health.Episodes.Select(episode => episode.FirstTick).Should().Equal(10, 1);
        health.Omissions.SlowEpisodes.Should().Be(1);
    }

    [Test]
    public void SlowUpdate_RetainsContextTopContributorsAndSameThreadCounts()
    {
        ModPerformanceManager manager = CreateManager(slowTickContributorCapacity: 2);
        manager.Start(logIndividualTicks: false, tickLogThresholdMilliseconds: 10);
        manager.BeginTick(7, 500, new ModHealthTickContext(ModHealthTickPhase.Menu, IsFocused: true, ScreenId: 3));
        manager.RecordHandler("C.Mod", "C", "Event", "C.Run", 10, failed: true);
        manager.RecordHandler("B.Mod", "B", "Event", "B.Run", 5, failed: false);
        manager.RecordHandler("A.Mod", "A", "Event", "A.Run", 5, failed: false);
        manager.RecordLog("A.Mod", "A", LogLevel.Warn);
        manager.RecordLog("A.Mod", "A", LogLevel.Error);
        Task.Run(() =>
        {
            manager.RecordHandler("Background.Mod", "Background", "Event", "Background.Run", 50, failed: true);
            manager.RecordLog("Background.Mod", "Background", LogLevel.Warn);
            manager.RecordLog("Background.Mod", "Background", LogLevel.Error);
        }).GetAwaiter().GetResult();
        manager.CompleteTick(530);

        ModHealthUpdatePerformanceSnapshot update = manager.GetSnapshot().Health!.RecentUpdates.Should().ContainSingle().Subject;
        update.OffsetMilliseconds.Should().Be(500);
        update.Context.Should().Be(new ModHealthTickContext(ModHealthTickPhase.Menu, true, 3));
        update.WarningCount.Should().Be(1);
        update.ErrorCount.Should().Be(1);
        update.CallbackFailureCount.Should().Be(1);
        update.InstrumentedModMilliseconds.Should().Be(20);
        update.Contributors.Select(entry => entry.ModId).Should().Equal("C.Mod", "A.Mod");
        update.OmittedContributors.Should().Be(1);
    }

    [Test]
    public void ContributorIdentityCapacity_OmitsIdentityButPreservesTickTotal()
    {
        ModPerformanceManager manager = CreateManager(slowTickContributorCapacity: 5, tickContributorIdentityCapacity: 1);
        manager.Start(logIndividualTicks: false, tickLogThresholdMilliseconds: 10);
        manager.BeginTick(1, 0);
        manager.RecordHandler("A.Mod", "A", "Event", "A.Run", 6, failed: false);
        manager.RecordHandler("B.Mod", "B", "Event", "B.Run", 6, failed: false);
        manager.CompleteTick(20);

        ModHealthPerformanceSnapshot health = manager.GetSnapshot().Health!;
        health.RecentUpdates[0].InstrumentedModMilliseconds.Should().Be(12);
        health.RecentUpdates[0].Contributors.Should().ContainSingle().Which.ModId.Should().Be("A.Mod");
        health.RecentUpdates[0].OmittedContributors.Should().Be(1);
        health.Omissions.ContributorIdentities.Should().Be(1);
    }

    [Test]
    public void InvalidDuration_IsRetainedButExcludedFromHistogramAndWorstRanking()
    {
        ModPerformanceManager manager = CreateManager();
        manager.Start();
        manager.BeginTick(1, 10);
        manager.CompleteTick(5);

        ModHealthPerformanceSnapshot health = manager.GetSnapshot().Health!;
        health.RecentUpdates.Should().ContainSingle().Which.TimingPartitionIsValid.Should().BeFalse();
        health.Histogram.Count.Should().Be(0);
        health.WorstUpdates.Should().BeEmpty();
        health.Omissions.InvalidHistogramUpdates.Should().Be(1);
    }

    [Test]
    public void LegacyBeginTick_UsesSafeDefaultContext()
    {
        ModPerformanceManager manager = CreateManager();
        manager.Start();
        CompleteTick(manager, 1, 1);

        manager.GetSnapshot().Health!.RecentUpdates[0].Context.Should().Be(default(ModHealthTickContext));
    }

    [Test]
    public void DisabledHealthTimingHotPath_DoesNotAllocateAfterWarmup()
    {
        ModPerformanceManager manager = CreateManager();
        manager.BeginTick(1, 0, new ModHealthTickContext(ModHealthTickPhase.Gameplay, true, 0));
        manager.BeginHandler("Example.Mod", "Example", "Event", "Handler", ModHealthExecutionPhase.Update, ModHealthOperationKind.Event, null);
        manager.RecordHandler("Example.Mod", "Example", "Event", "Handler", ModHealthExecutionPhase.Update, ModHealthOperationKind.Event, null, 1, failed: false);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            manager.BeginTick(1, 0, default);
            manager.BeginHandler("Example.Mod", "Example", "Event", "Handler", ModHealthExecutionPhase.Update, ModHealthOperationKind.Event, null);
            manager.RecordHandler("Example.Mod", "Example", "Event", "Handler", ModHealthExecutionPhase.Update, ModHealthOperationKind.Event, null, 1, failed: false);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().Be(0);
    }

    private static ModPerformanceManager CreateManager(
        int tickHistoryCapacity = 600,
        int worstTickCapacity = 100,
        int slowEpisodeCapacity = 50,
        int slowTickContributorCapacity = 5,
        int tickContributorIdentityCapacity = 4096,
        long timestampFrequency = 1000
    )
    {
        return new ModPerformanceManager(
            tickHistoryCapacity: tickHistoryCapacity,
            timestampFrequency: timestampFrequency,
            getTimestamp: () => 0,
            getGcCollectionCount: _ => 0,
            worstTickCapacity: worstTickCapacity,
            slowEpisodeCapacity: slowEpisodeCapacity,
            slowTickContributorCapacity: slowTickContributorCapacity,
            tickContributorIdentityCapacity: tickContributorIdentityCapacity
        );
    }

    private static void CompleteTick(ModPerformanceManager manager, uint tick, long timestampTicks)
    {
        manager.BeginTick(tick, 0);
        manager.CompleteTick(timestampTicks);
    }

    private static void CompleteEpisode(ModPerformanceManager manager, uint firstTick, params long[] slowDurations)
    {
        uint tick = firstTick;
        foreach (long duration in slowDurations)
            CompleteTick(manager, tick++, duration);
        CompleteTick(manager, tick++, 1);
        CompleteTick(manager, tick, 1);
    }
}
