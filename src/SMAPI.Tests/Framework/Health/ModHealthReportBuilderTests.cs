using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;
using NUnit.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Framework.Health;
using StardewModdingAPI.Framework.Performance;

namespace SMAPI.Tests.Framework.Health;

[TestFixture]
internal sealed class ModHealthReportBuilderTests
{
    [Test]
    public void Build_MapsFrozenLedgerTimingEnvironmentAndNearestMarks()
    {
        long timestamp = 0;
        ModHealthLedger ledger = new(startedUtc: new DateTime(2026, 8, 26, 1, 0, 0, DateTimeKind.Utc), timestampFrequency: 1000, getTimestamp: () => timestamp);
        ModHealthModKey modKey = ledger.RegisterMod(new ModHealthModObservation(
            true, "Example.Mod", "Example Mod", "1.2.3", ModHealthLedgerModKind.CodeMod, null, ["Dependency.Mod"], ModHealthLedgerModStatus.Loaded,
            ModHealthModFailureReason.None, WarningFlags: 5, ModHealthUpdateStatus.Pending, null
        ));
        ModHealthLedgerBaseline baseline = ledger.CreateCaptureBaseline();
        ledger.UpdateMod(modKey, ModHealthLedgerModStatus.Loaded, new ModHealthModObservation(
            true, "Example.Mod", "Example Mod", "1.2.3", ModHealthLedgerModKind.CodeMod, null, ["Dependency.Mod"], ModHealthLedgerModStatus.Loaded,
            ModHealthModFailureReason.None, WarningFlags: 5, ModHealthUpdateStatus.UpdateAvailable, "2.0.0"
        ));
        timestamp = 250;
        ledger.ObserveLog(new ModHealthLogObservation("Example.Mod", "Example Mod", ModHealthLogSourceCategory.Mod, LogLevel.Error, 41, 1));
        ledger.ObserveCallbackFailure(new ModHealthCallbackFailureObservation("Example.Mod", "Example Mod", ModHealthExecutionPhase.Update, ModHealthOperationKind.ContentLoad, "Example.Load", "System.InvalidOperationException", "Pack.Mod", 1));

        ModHealthLedgerSnapshot ledgerSnapshot = ledger.GetSnapshot(baseline);
        ModPerformanceSnapshot performance = CreatePerformanceSnapshot();
        ModHealthExportRequest request = new(
            RequestId: Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            RequestedUtc: new DateTimeOffset(2026, 8, 26, 1, 1, 0, TimeSpan.Zero),
            Owner: ModHealthCaptureOwner.Health,
            Origin: ModHealthCaptureOrigin.Manual,
            CompletionReason: ModHealthCompletionReason.UserStop,
            Performance: performance,
            Ledger: ledgerSnapshot,
            Marks: ImmutableArray.Create(new ModHealthMark(1, 99, 300), new ModHealthMark(2, 101, 400)),
            SlowUpdateThresholdMilliseconds: 33.333,
            IsFinal: true
        );
        ModHealthEnvironmentSnapshot environment = CreateEnvironment();

        ModHealthReport report = new ModHealthReportBuilder().Build(request, environment);

        report.Header.ReportId.Should().Be("report-0011223344556677");
        report.ModInventory.Should().Match<ModHealthModInventorySummary>(inventory => inventory.TotalDiscovered == 1 && inventory.Loaded == 1 && inventory.Retained == 1);
        report.LogTotals.Should().Match<ModHealthLogTotals>(totals => totals.SinceLedgerStart.ErrorMessages == 1 && totals.DuringCapture.ErrorMessages == 1);
        report.CallbackFailureTotals.Should().Be(new ModHealthCallbackFailureTotals(1, 1));
        report.Environment.ProcessBitness.Should().Be(64);
        report.Environment.SessionType.Should().Be("wayland");
        report.Completeness.LifecycleTimingObserved.Should().BeTrue();
        report.Capture.Should().Match<ModHealthCapture>(capture => capture.Mode == ModHealthCaptureMode.Health && capture.CompletedUpdateCount == 700 && !capture.IsShortSample && capture.TimingValid);
        report.Mods.Should().ContainSingle().Which.Should().Match<ModHealthMod>(mod =>
            mod.Id == "Example.Mod"
            && mod.UpdateStatus == ModHealthReportUpdateStatus.UpdateAvailable
            && mod.SuggestedUpdateVersion == "2.0.0"
            && mod.WarningFlags.SequenceEqual(new[] { "broken-code-loaded", "patches-game" })
            && mod.SessionErrorCount == 1
            && mod.CaptureErrorCount == 1
            && mod.CallbackFailureCount == 1
            && mod.ObservedCallbackMilliseconds == 15
            && mod.ObservedCallbackCount == 2
            && mod.ObservedCallbackFailureCount == 1
            && mod.SlowUpdateParticipationCount == 1
            && mod.InstrumentedTimeShare == 1
        );
        report.Logs.Should().ContainSingle().Which.Should().Match<ModHealthLogSummary>(log => log.SinceLedgerStart.ErrorMessages == 1 && log.DuringCapture.ErrorMessages == 1 && log.SinceLedgerStart.ErrorCharacters == 41);
        report.CallbackFailures.Should().ContainSingle().Which.Should().Match<ModHealthCallbackFailure>(failure => failure.CaptureCount == 1 && failure.OnBehalfOfModId == "Pack.Mod");
        report.Performance.Callbacks.Should().ContainSingle().Which.Should().Match<ModHealthCallback>(callback => callback.Event == "AssetRequested" && callback.OnBehalfOfModId == "Pack.Mod");
        report.Performance.WorstUpdates.Should().ContainSingle().Which.Should().Match<ModHealthUpdate>(update =>
            update.NearbyMark == 1
            && update.SmapiOtherMilliseconds == 5
            && update.SmapiOtherTimingAvailable
            && update.ResidualMilliseconds == 5
            && update.Gen0Collections == 2
            && update.Gen1Collections == 1
            && update.Gen2Collections == 0
            && update.GcCollectionDataValid
        );
        report.Performance.Should().Match<ModHealthPerformance>(timing =>
            timing.TotalObservedModMilliseconds == 15
            && timing.TotalBaseGameExclusiveMilliseconds == 25
            && timing.TotalSmapiOtherMilliseconds == 5
            && timing.SmapiOtherTimingAvailable
            && timing.TotalResidualMilliseconds == 5
            && timing.TotalObservedModMilliseconds + timing.TotalBaseGameExclusiveMilliseconds + timing.TotalSmapiOtherMilliseconds + timing.TotalResidualMilliseconds == 50
            && timing.GcCollectionDataValid
        );
        report.Performance.Episodes.Should().ContainSingle().Which.NearbyMark.Should().Be(1, "the earlier mark wins an equal-distance tie");
        report.Capacities.Select(capacity => capacity.Name).Should().BeInAscendingOrder(StringComparer.Ordinal);
        report.Omissions.Select(omission => omission.Section).Should().BeInAscendingOrder(StringComparer.Ordinal);
        report.Omissions.Should().Contain(omission => omission.Section == "recentUpdates" && omission.Count == 100);
        report.Omissions.Should().Contain(omission => omission.Section == "worstUpdates" && omission.Count == 600);
        report.Header.IsTruncated.Should().BeTrue();

        string schemaPath = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestAssets", "ModHealthReport", "mod-health-report-schema-v1.json");
        JSchema schema = JSchema.Parse(File.ReadAllText(schemaPath));
        JToken.Parse(new ModHealthReportJsonSerializer().Serialize(report)).IsValid(schema, out IList<string> errors).Should().BeTrue(string.Join("\n", errors));
    }

    [Test]
    public void Build_MixedSmapiAvailabilityFoldsMeasuredDispatchBackIntoResidual()
    {
        ModPerformanceSnapshot performance = CreatePerformanceSnapshot();
        ModHealthUpdatePerformanceSnapshot unavailableUpdate = performance.Health!.WorstUpdates[0] with { SmapiUpdateTimingAvailable = false };
        performance = performance with
        {
            SmapiUpdateTimingAvailable = false,
            Health = performance.Health with
            {
                WorstUpdates = Array.AsReadOnly(new[] { unavailableUpdate }),
                RecentUpdates = Array.AsReadOnly(new[] { unavailableUpdate })
            }
        };

        ModHealthReport report = BuildReport(performance);

        report.Performance.Should().Match<ModHealthPerformance>(timing =>
            !timing.SmapiOtherTimingAvailable
            && timing.TotalSmapiOtherMilliseconds == 0
            && timing.TotalResidualMilliseconds == 10
            && timing.TotalObservedModMilliseconds + timing.TotalBaseGameExclusiveMilliseconds + timing.TotalSmapiOtherMilliseconds + timing.TotalResidualMilliseconds == 50
        );
        report.Performance.WorstUpdates.Should().ContainSingle().Which.Should().Match<ModHealthUpdate>(update =>
            !update.SmapiOtherTimingAvailable
            && update.SmapiOtherMilliseconds == 0
            && update.ResidualMilliseconds == 10
            && update.ObservedModMilliseconds + update.BaseGameExclusiveMilliseconds + update.SmapiOtherMilliseconds + update.ResidualMilliseconds == update.TotalMilliseconds
        );
        string text = new ModHealthReportTextFormatter().Format(report);
        text.Should().Contain("SMAPI update dispatch observed outside the base-game update: unavailable");
        text.Should().Contain("Remaining uncategorized residual time: 10 ms");
        text.Should().NotContain("SMAPI update dispatch observed outside the base-game update: 0 ms");
    }

    [Test]
    public void Build_ValidZeroSmapiDispatchRemainsAvailableAndReconciles()
    {
        ModPerformanceSnapshot performance = CreatePerformanceSnapshot();
        ModHealthUpdatePerformanceSnapshot zeroUpdate = performance.Health!.WorstUpdates[0] with
        {
            SmapiUpdateMilliseconds = 0,
            InstrumentedDuringSmapiUpdateMilliseconds = 0,
            SmapiUpdateTimingAvailable = true
        };
        performance = performance with
        {
            SmapiUpdateMilliseconds = 0,
            InstrumentedDuringSmapiUpdateMilliseconds = 0,
            SmapiUpdateTimingAvailable = true,
            Health = performance.Health with
            {
                WorstUpdates = Array.AsReadOnly(new[] { zeroUpdate }),
                RecentUpdates = Array.AsReadOnly(new[] { zeroUpdate })
            }
        };

        ModHealthReport report = BuildReport(performance);

        report.Performance.Should().Match<ModHealthPerformance>(timing =>
            timing.SmapiOtherTimingAvailable
            && timing.TotalSmapiOtherMilliseconds == 0
            && timing.TotalResidualMilliseconds == 10
            && timing.TotalObservedModMilliseconds + timing.TotalBaseGameExclusiveMilliseconds + timing.TotalSmapiOtherMilliseconds + timing.TotalResidualMilliseconds == 50
        );
        report.Performance.WorstUpdates.Should().ContainSingle().Which.Should().Match<ModHealthUpdate>(update =>
            update.SmapiOtherTimingAvailable
            && update.SmapiOtherMilliseconds == 0
            && update.ResidualMilliseconds == 10
            && update.ObservedModMilliseconds + update.BaseGameExclusiveMilliseconds + update.SmapiOtherMilliseconds + update.ResidualMilliseconds == update.TotalMilliseconds
        );
        string text = new ModHealthReportTextFormatter().Format(report);
        text.Should().Contain("SMAPI update dispatch observed outside the base-game update: 0 ms");
        text.Should().NotContain("SMAPI update dispatch observed outside the base-game update: unavailable");
    }

    [Test]
    public void Build_LedgerOnlyDoesNotMislabelSessionEvidenceAsDuringCapture()
    {
        ModHealthLedger ledger = new(timestampFrequency: 1000, getTimestamp: static () => 0);
        ledger.RegisterMod(new ModHealthModObservation(true, "Example.Mod", "Example", "1", ModHealthLedgerModKind.CodeMod, null, [], ModHealthLedgerModStatus.Discovered, ModHealthModFailureReason.None, 0, ModHealthUpdateStatus.Disabled, null));
        ledger.ObserveLog(new ModHealthLogObservation("Example.Mod", "Example", ModHealthLogSourceCategory.Mod, LogLevel.Error, 20, 1));
        ledger.ObserveCallbackFailure(new ModHealthCallbackFailureObservation("Example.Mod", "Example", ModHealthExecutionPhase.Unscoped, ModHealthOperationKind.Other, "Example.Run", "System.Exception", null, 1));
        ModHealthExportRequest request = new(Guid.NewGuid(), DateTimeOffset.UtcNow, ModHealthCaptureOwner.None, null, ModHealthCompletionReason.InterimReport, null, ledger.GetSnapshot(), ImmutableArray<ModHealthMark>.Empty, 33.333, false);

        ModHealthReport report = new ModHealthReportBuilder().Build(request, CreateEnvironment());

        report.Capture.Mode.Should().Be(ModHealthCaptureMode.LedgerOnly);
        report.Capture.IsShortSample.Should().BeFalse("ledger-only evidence has no timing sample to classify as short");
        report.Capture.TimingValid.Should().BeFalse();
        report.Mods.Single().Status.Should().Be(ModHealthModStatus.Discovered);
        report.Mods.Single().FailureCategory.Should().Be("status-incomplete");
        report.Mods.Single().UpdateStatus.Should().Be(ModHealthReportUpdateStatus.Disabled);
        report.Mods.Single().CaptureErrorCount.Should().Be(0);
        report.Logs.Single().DuringCapture.TotalMessages.Should().Be(0);
        report.CallbackFailures.Single().CaptureCount.Should().Be(0);
        report.Performance.Histogram.Count.Should().Be(0);
        report = report with { Findings = new ModHealthInsightAnalyzer().Analyze(report) };
        string text = new ModHealthReportTextFormatter().Format(report);
        text.Should().Contain("Timing data: unavailable; this report contains session-ledger evidence only");
        text.Should().NotContain("This timing sample is short");
    }

    [Test]
    public void Build_InvalidPartitionSuppressesPartitionTotalsAndSanitizesAllowlistedValues()
    {
        ModPerformanceSnapshot invalid = CreatePerformanceSnapshot() with
        {
            TimingPartitionIsValid = false,
            TickTotalMilliseconds = 10,
            GameUpdateMilliseconds = 20,
            TickInstrumentedMilliseconds = double.NaN,
            CaptureGen0Collections = 99,
            CaptureGen1Collections = 99,
            CaptureGen2Collections = 99,
            CaptureGcCollectionDataIsValid = false
        };
        ModHealthLedger ledger = new(timestampFrequency: 1000, getTimestamp: static () => 0);
        ModHealthExportRequest request = new(Guid.NewGuid(), DateTimeOffset.UtcNow, ModHealthCaptureOwner.Performance, ModHealthCaptureOrigin.Manual, ModHealthCompletionReason.InterimReport, invalid, ledger.GetSnapshot(ledger.CreateCaptureBaseline()), ImmutableArray<ModHealthMark>.Empty, double.NaN, false);
        ModHealthEnvironmentSnapshot environment = CreateEnvironment() with { SmapiCommit = "/home/private/commit", SessionType = "secret-session", MultiplayerRole = "private-role" };

        ModHealthReportPayload payload = new ModHealthReportBuilder().BuildPayload(request, environment);

        payload.Model.Capture.TimingValid.Should().BeFalse();
        payload.Model.Capture.SlowUpdateThresholdMilliseconds.Should().Be(33.333);
        payload.Model.Performance.TotalObservedModMilliseconds.Should().Be(0);
        payload.Model.Performance.TotalBaseGameExclusiveMilliseconds.Should().Be(0);
        payload.Model.Performance.TotalSmapiOtherMilliseconds.Should().Be(0);
        payload.Model.Performance.TotalResidualMilliseconds.Should().Be(0);
        payload.Model.Performance.GcCollectionDataValid.Should().BeFalse();
        payload.Model.Performance.Gen0Collections.Should().Be(0);
        payload.Model.Environment.SmapiCommit.Should().Be("[path]");
        payload.Model.Environment.SessionType.Should().Be("unknown");
        payload.Model.Environment.MultiplayerRole.Should().Be("unknown");
        payload.Json.Should().NotContain("/home/private").And.NotContain("secret-session").And.NotContain("private-role");
        payload.Text.Should().Contain("Process-wide GC collections during capture: unavailable");
    }

    [Test]
    public void Build_NearestMarkHandlesUpdateTickWraparound()
    {
        ModPerformanceSnapshot performance = CreatePerformanceSnapshot();
        ModHealthUpdatePerformanceSnapshot wrapped = performance.Health!.WorstUpdates[0] with { Tick = 1 };
        performance = performance with { Health = performance.Health with { WorstUpdates = Array.AsReadOnly(new[] { wrapped }) } };
        ModHealthLedger ledger = new(timestampFrequency: 1000, getTimestamp: static () => 0);
        ModHealthExportRequest request = new(Guid.NewGuid(), DateTimeOffset.UtcNow, ModHealthCaptureOwner.Health, ModHealthCaptureOrigin.Manual, ModHealthCompletionReason.InterimReport, performance, ledger.GetSnapshot(ledger.CreateCaptureBaseline()), ImmutableArray.Create(new ModHealthMark(1, uint.MaxValue, 1)), 33.333, false);

        ModHealthReport report = new ModHealthReportBuilder().Build(request, CreateEnvironment());

        report.Performance.WorstUpdates.Single().NearbyMark.Should().Be(1);
    }

    [Test]
    public void Build_EnvironmentReducesOsBannersToNonIdentifyingNumericValues()
    {
        ModHealthLedger ledger = new(timestampFrequency: 1000, getTimestamp: static () => 0);
        ModHealthExportRequest request = new(Guid.NewGuid(), DateTimeOffset.UtcNow, ModHealthCaptureOwner.None, null, ModHealthCompletionReason.InterimReport, null, ledger.GetSnapshot(), ImmutableArray<ModHealthMark>.Empty, 33.333, false);
        ModHealthEnvironmentSnapshot environment = CreateEnvironment() with
        {
            LinuxDistribution = "ubuntu 24.04.3",
            Kernel = "Linux 6.12.9-private-hostname"
        };

        ModHealthReportPayload payload = new ModHealthReportBuilder().BuildPayload(request, environment);

        payload.Model.Environment.LinuxDistribution.Should().Be("ubuntu 24.04.3");
        payload.Model.Environment.Kernel.Should().Be("6.12.9");
        payload.Json.Should().NotContain("private-admin-banner").And.NotContain("private-hostname");

        ModHealthReport unknown = new ModHealthReportBuilder().Build(request, environment with
        {
            LinuxDistribution = "ubuntu 24.04 private-admin-banner",
            Kernel = "host-name 6.12"
        });
        unknown.Environment.LinuxDistribution.Should().BeNull();
        unknown.Environment.Kernel.Should().BeNull();
    }

    private static ModHealthEnvironmentSnapshot CreateEnvironment()
    {
        return new("4.5.2-fork", "abc123", "1.6.15", ".NET 6.0.36", "x64", 64, "Linux", "6.12", "Wayland", "en-AU", 8, "single-player", 1, true, false);
    }

    private static ModHealthReport BuildReport(ModPerformanceSnapshot performance)
    {
        ModHealthLedger ledger = new(timestampFrequency: 1000, getTimestamp: static () => 0);
        ModHealthExportRequest request = new(Guid.NewGuid(), DateTimeOffset.UtcNow, ModHealthCaptureOwner.Performance, ModHealthCaptureOrigin.Manual, ModHealthCompletionReason.InterimReport, performance, ledger.GetSnapshot(ledger.CreateCaptureBaseline()), ImmutableArray<ModHealthMark>.Empty, 33.333, false);
        return new ModHealthReportBuilder().Build(request, CreateEnvironment());
    }

    private static ModPerformanceSnapshot CreatePerformanceSnapshot()
    {
        ModHealthUpdatePerformanceSnapshot update = new(
            CaptureSequence: 0,
            Tick: 100,
            OffsetMilliseconds: 350,
            TotalMilliseconds: 50,
            GameUpdateMilliseconds: 30,
            InstrumentedModMilliseconds: 15,
            InstrumentedDuringGameUpdateMilliseconds: 5,
            TimingPartitionIsValid: true,
            Context: new ModHealthTickContext(ModHealthTickPhase.Menu, true, 2),
            WarningCount: 1,
            ErrorCount: 1,
            CallbackFailureCount: 1,
            Gen0Collections: 2,
            Gen1Collections: 1,
            Gen2Collections: 0,
            GcCollectionDataIsValid: true,
            Contributors: Array.AsReadOnly(new[] { new ModHealthTickContributorSnapshot("Example.Mod", "Example Mod", 15) }),
            OmittedContributorIdentities: 0,
            OmittedContributorObservations: 0,
            SmapiUpdateMilliseconds: 6,
            InstrumentedDuringSmapiUpdateMilliseconds: 1,
            SmapiUpdateTimingAvailable: true
        );
        ModHealthPerformanceSnapshot health = new(
            33.333,
            3,
            Array.AsReadOnly(new[] { new ModHealthCallbackPerformanceSnapshot("Example.Mod", "Example Mod", ModHealthExecutionPhase.Startup, ModHealthOperationKind.ContentLoad, "AssetRequested", "Example.Load", "Pack.Mod", 2, 15, 12, 1) }),
            Array.AsReadOnly(new[] { update }),
            Array.AsReadOnly(new[] { update }),
            Array.AsReadOnly(new[] { new ModHealthSlowEpisodeSnapshot(0, 0, 100, 100, 1, 50, 50, 100) }),
            new ModHealthTimingHistogramSnapshot(Array.AsReadOnly(new long[256]), 700, 14000, 10, 50, 0, 0, Array.AsReadOnly(new[] { new ModHealthTimingThresholdSnapshot(33.333, 3) }), 16, 25, 40, 0.045),
            new ModHealthTimingCapacities(600, 100, 50, 5, 4096, 8192),
            new ModHealthTimingOmissions(100, 600, 0, 0, 0, 0, 0)
        );
        return new ModPerformanceSnapshot(
            IsTracking: false,
            StartedUtc: new DateTime(2026, 8, 26, 1, 0, 0, DateTimeKind.Utc),
            Elapsed: TimeSpan.FromSeconds(40),
            CompletedTickCount: 700,
            Handlers: Array.Empty<HandlerPerformanceSnapshot>(),
            ModLogs: Array.Empty<ModLogSnapshot>(),
            RecentTicks: Array.Empty<TickPerformanceSnapshot>(),
            OmittedHandlerInvocations: 0,
            LogIndividualTicks: false,
            TickLogThresholdMilliseconds: 0,
            TickTotalMilliseconds: 50,
            GameUpdateMilliseconds: 30,
            TickInstrumentedMilliseconds: 15,
            InstrumentedDuringGameUpdateMilliseconds: 5,
            Gen0Collections: 1,
            Gen1Collections: 0,
            Gen2Collections: 0,
            TimingPartitionIsValid: true,
            InvalidTimingPartitionTickCount: 0,
            GcCollectionDataIsValid: true,
            InvalidGcCollectionTickCount: 0,
            CaptureGen0Collections: 2,
            CaptureGen1Collections: 1,
            CaptureGen2Collections: 0,
            CaptureGcCollectionDataIsValid: true,
            SmapiUpdateMilliseconds: 6,
            InstrumentedDuringSmapiUpdateMilliseconds: 1,
            SmapiUpdateTimingAvailable: true,
            Health: health
        );
    }
}
