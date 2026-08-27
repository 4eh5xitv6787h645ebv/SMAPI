using System;
using System.Collections.Immutable;
using StardewModdingAPI.Framework.Health;

namespace SMAPI.Tests.Framework.Health;

/// <summary>Creates deterministic schema-v1 report fixtures.</summary>
internal static class ModHealthReportFixtureFactory
{
    public static ModHealthReport CreateCanonical()
    {
        DateTimeOffset generated = new(2026, 8, 26, 12, 34, 56, TimeSpan.Zero);
        return new(
            SchemaVersion: 1,
            Header: new("sample-a1b2c3", generated, IsTruncated: false, IsMinimalFallback: false, WriteRetry: false),
            Completeness: new(generated.AddMinutes(-2), "SMAPI core initialized before LogManager; earlier launcher/native failures are unavailable.", StartupObserved: true, LifecycleTimingObserved: true),
            Environment: new("4.5.2-fork", "abc1234", "1.6.15", ".NET 6.0.36", "x64", 64, "Linux", "6.12", "wayland", "en-AU", 8, "single-player", 1),
            Capture: new(ModHealthCaptureMode.Health, ModHealthCompletionReason.UserStop, generated.AddMinutes(-1), generated, 60000, 3600, 33.333, IsShortSample: false, TimingValid: true, ImmutableArray.Create(new ModHealthMark(1, 1200, 20000))),
            Findings: ImmutableArray<ModHealthFinding>.Empty,
            Performance: new(
                new ModHealthHistogram(3600, 60480, 15.2, 41.2, 16.75, 18, 35, PercentilesApproximate: true, 0.044, 0, 0, ImmutableArray.Create(new ModHealthThresholdCount(33.333, 3))),
                TotalObservedModMilliseconds: 12,
                TotalBaseGameExclusiveMilliseconds: 60300,
                TotalSmapiOtherMilliseconds: 120,
                SmapiOtherTimingAvailable: true,
                TotalResidualMilliseconds: 48,
                SlowUpdateCount: 3,
                Callbacks: ImmutableArray.Create(new ModHealthCallback("Example.Mod", "Example Mod", ModHealthExecutionPhase.Update, ModHealthOperationKind.Event, "UpdateTicked", "Example.Mod.OnUpdate", null, 3600, 12, 0.08, 0)),
                WorstUpdates: ImmutableArray.Create(new ModHealthUpdate(1200, 20000, 41.2, 38, 0.2, 2, true, 1, true, "gameplay", true, 0, 0, 0, 0, 2, 0, 0, true, ImmutableArray.Create(new ModHealthContributor("Example.Mod", 0.2)), 1)),
                RecentUpdates: ImmutableArray<ModHealthUpdate>.Empty,
                Episodes: ImmutableArray.Create(new ModHealthEpisode(1200, 1202, 3, 41.2, 119.4, 1200, 1)),
                Gen0Collections: 2,
                Gen1Collections: 0,
                Gen2Collections: 0,
                GcCollectionDataValid: true
            ),
            ModInventory: new ModHealthModInventorySummary(1, 0, 1, 0, 0, 0, 0, 1),
            Mods: ImmutableArray.Create(new ModHealthMod("Example.Mod", "Example Mod", "1.2.3", ModHealthModKind.CodeMod, null, ModHealthModStatus.Loaded, null, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, ModHealthReportUpdateStatus.UpToDate, null, 0, 0, 0, 0, 12, 0.08, 3600, 0, 1, 1, 3, 120)),
            LogTotals: new ModHealthLogTotals(new ModHealthLogSeveritySummary(3, 120, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), new ModHealthLogSeveritySummary(2, 80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)),
            Logs: ImmutableArray.Create(new ModHealthLogSummary("Example.Mod", ModHealthReportLogSourceCategory.Mod, new ModHealthLogSeveritySummary(3, 120, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), new ModHealthLogSeveritySummary(2, 80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), 3, 120, 100, 59000)),
            CallbackFailureTotals: new ModHealthCallbackFailureTotals(0, 0),
            CallbackFailures: ImmutableArray<ModHealthCallbackFailure>.Empty,
            Capacities: ImmutableArray.Create(new ModHealthCapacity("callbacks", ModHealthReportLimits.MaxCallbacks, false)),
            Omissions: ImmutableArray.Create(new ModHealthOmission("callbacks", 0)),
            Privacy: new(true, false, ImmutableArray.Create("mod names", "mod IDs", "versions", "statuses"), ImmutableArray.Create("raw logs", "stack traces", "paths", "save data", "configuration")),
            Limitations: ImmutableArray.Create("SMAPI observes only named callback boundaries.", "Background, native, Harmony, GPU, I/O, and operating-system work can remain unattributed.")
        );
    }
}
