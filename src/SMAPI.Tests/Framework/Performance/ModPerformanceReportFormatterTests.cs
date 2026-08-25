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
                new TickPerformanceSnapshot(99, 20, 8, "Slow.Mod", "Slow Mod", 8, 1)
            ],
            OmittedHandlerInvocations: 0,
            LogIndividualTicks: false,
            TickLogThresholdMilliseconds: 16.667
        );

        string report = ModPerformanceReportFormatter.Format(snapshot, limit: 1);

        report.Should().Contain("Slow Mod (Slow.Mod): 120.0ms total");
        report.Should().Contain("1 warnings, 2 errors, 2 failed callbacks");
        report.Should().Contain("tick 99: 20.000ms total; 8.000ms instrumented mod callbacks; 12.000ms unattributed");
        report.Should().Contain("Harmony patches");
        report.Should().NotContain("Fast Mod");
    }

    [Test]
    public void FormatTick_HandlesNoInstrumentedMod()
    {
        string result = ModPerformanceReportFormatter.FormatTick(new TickPerformanceSnapshot(12, 18, 0, null, null, 0, 0));

        result.Should().Be("tick 12: 18.000ms total; 0.000ms instrumented mod callbacks; 18.000ms unattributed; slowest mod none; 0 mod errors");
    }
}
