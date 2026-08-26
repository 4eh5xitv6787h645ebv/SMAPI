using System;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.Performance;

namespace SMAPI.Tests.Framework.Performance;

/// <summary>Unit tests for <see cref="ModPerformanceReportFormatter"/>.</summary>
[TestFixture]
internal sealed class ModPerformanceReportFormatterTests
{
    [Test]
    public void Format_ExplainsAttributionAndRanksObservedData()
    {
        ModPerformanceSnapshot snapshot = new(
            IsTracking: true,
            StartedUtc: DateTime.UtcNow,
            Elapsed: TimeSpan.FromSeconds(30),
            CompletedTickCount: 1800,
            Handlers:
            [
                new HandlerPerformanceSnapshot("Slow.Mod", "Slow Mod", "GameLoop.UpdateTicked", "Slow.Mod.OnTick", 60, 120, 8, 2),
                new HandlerPerformanceSnapshot("Fast.Mod", "Fast Mod", "Display.Rendered", "Fast.Mod.OnRendered", 60, 3, 0.1, 0)
            ],
            ModLogs:
            [
                new ModLogSnapshot("Slow.Mod", "Slow Mod", 1, 2)
            ],
            RecentTicks:
            [
                new TickPerformanceSnapshot(99, 20, 8, "Slow.Mod", "Slow Mod", 8, 1, GameUpdateMilliseconds: 10, InstrumentedDuringGameUpdateMilliseconds: 2, Gen0Collections: 1)
            ],
            OmittedHandlerInvocations: 0,
            LogIndividualTicks: false,
            TickLogThresholdMilliseconds: 16.667,
            TickTotalMilliseconds: 36000,
            GameUpdateMilliseconds: 18000,
            TickInstrumentedMilliseconds: 12000,
            InstrumentedDuringGameUpdateMilliseconds: 3000,
            Gen0Collections: 500,
            Gen1Collections: 50,
            Gen2Collections: 5,
            CaptureGen0Collections: 550,
            CaptureGen1Collections: 55,
            CaptureGen2Collections: 6
        );

        string report = ModPerformanceReportFormatter.Format(snapshot, limit: 1);

        report.Should().Contain("Slow Mod (Slow.Mod): 120.0ms total");
        report.Should().Contain("1 warnings, 2 errors, 2 failed callbacks");
        report.Should().Contain("Measured tick time: 36000.0ms total (20.000ms/tick): base game update excluding observed callbacks 15000.0ms (8.333ms/tick), observed mod callbacks 12000.0ms (6.667ms/tick), residual outside measured game/callback boundaries 9000.0ms (5.000ms/tick).");
        report.Should().Contain("Process-wide GC collections during measured ticks: 500 gen0, 50 gen1, 5 gen2.");
        report.Should().Contain("Process-wide GC collections across the sample: 550 gen0, 55 gen1, 6 gen2 (correlation only, not mod attribution).");
        report.Should().Contain("tick 99: 20.000ms total; 8.000ms base game update excluding observed callbacks; 8.000ms observed mod callbacks; 4.000ms residual outside measured boundaries; GC 1/0/0 process-wide");
        report.Should().Contain("Harmony patches");
        report.Should().NotContain("Fast Mod");
    }

    [Test]
    public void FormatTick_HandlesNoInstrumentedMod()
    {
        string result = ModPerformanceReportFormatter.FormatTick(new TickPerformanceSnapshot(12, 18, 0, null, null, 0, 0));

        result.Should().Be("tick 12: 18.000ms total; 0.000ms base game update excluding observed callbacks; 0.000ms observed mod callbacks; 18.000ms residual outside measured boundaries; GC 0/0/0 process-wide; slowest mod none; 0 mod errors");
    }

    [Test]
    public void Format_SuppressesInvalidTimingPartition()
    {
        TickPerformanceSnapshot tick = new(12, 10, 15, "Example.Mod", "Example Mod", 15, 0, TimingPartitionIsValid: false);
        ModPerformanceSnapshot snapshot = new(
            IsTracking: false,
            StartedUtc: DateTime.UtcNow,
            Elapsed: TimeSpan.FromSeconds(1),
            CompletedTickCount: 1,
            Handlers: [],
            ModLogs: [],
            RecentTicks: [tick],
            OmittedHandlerInvocations: 0,
            LogIndividualTicks: false,
            TickLogThresholdMilliseconds: 0,
            TickTotalMilliseconds: 10,
            TickInstrumentedMilliseconds: 15,
            TimingPartitionIsValid: false,
            InvalidTimingPartitionTickCount: 1
        );

        string report = ModPerformanceReportFormatter.Format(snapshot);

        report.Should().Contain("Timing split unavailable because 1 completed tick(s) contained an invalid timing partition");
        report.Should().Contain("timing partition invalid (raw game window 0.000ms; raw observed callbacks 15.000ms)");
        report.Should().NotContain("-5.000ms residual");
    }
}
