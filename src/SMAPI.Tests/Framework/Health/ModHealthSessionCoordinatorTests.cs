using System;
using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Framework.Health;
using StardewModdingAPI.Framework.Performance;

namespace SMAPI.Tests.Framework.Health;

/// <summary>Tests for <see cref="ModHealthSessionCoordinator"/>.</summary>
[TestFixture]
internal sealed class ModHealthSessionCoordinatorTests
{
    [Test]
    public void HealthStart_UsesHealthOwnerAndDefaultThreshold()
    {
        Context context = new();

        ModHealthCoordinatorResult result = context.Coordinator.StartHealth();

        result.Code.Should().Be(ModHealthCoordinatorResultCode.Started);
        ModHealthSessionStatus status = context.Coordinator.GetStatus();
        status.CaptureState.Should().Be(ModHealthCaptureState.Active);
        status.Owner.Should().Be(ModHealthCaptureOwner.Health);
        status.Performance!.TickLogThresholdMilliseconds.Should().Be(ModHealthReportLimits.SlowUpdateMilliseconds);
        status.Performance.LogIndividualTicks.Should().BeFalse();
    }

    [Test]
    public void HealthStart_DoesNotReplaceUnexportedRetainedHealthSample()
    {
        Context context = new();
        context.Coordinator.StartHealth();
        context.Coordinator.StopHealth();
        ModPerformanceSnapshot frozen = context.Coordinator.GetPerformanceSnapshot();

        ModHealthCoordinatorResult result = context.Coordinator.StartHealth();

        result.IsError.Should().BeTrue();
        context.Coordinator.GetStatus().CaptureState.Should().Be(ModHealthCaptureState.StoppedRetained);
        context.Coordinator.GetPerformanceSnapshot().Should().BeSameAs(frozen);
    }

    [Test]
    public void HealthStart_ReplacesOnlySuccessfullyExportedRetainedHealthSample()
    {
        Context context = new();
        context.Coordinator.StartHealth();
        context.Coordinator.StopHealth();
        context.Queue.SetLastState(ModHealthExportState.Succeeded);

        ModHealthCoordinatorResult result = context.Coordinator.StartHealth();

        result.Code.Should().Be(ModHealthCoordinatorResultCode.Replaced);
        context.Coordinator.GetStatus().CaptureState.Should().Be(ModHealthCaptureState.Active);
    }

    [Test]
    public void PerformanceDuplicateStart_PreservesAdvancedResetCompatibility()
    {
        Context context = new();
        context.Coordinator.StartPerformance(logIndividualTicks: false, tickLogThresholdMilliseconds: 0);
        context.Manager.RecordHandler("example.mod", "Example", "event", "callback", 10, failed: false);

        ModHealthCoordinatorResult result = context.Coordinator.StartPerformance(logIndividualTicks: true, tickLogThresholdMilliseconds: 12.5);

        result.Code.Should().Be(ModHealthCoordinatorResultCode.Replaced);
        ModPerformanceSnapshot snapshot = context.Coordinator.GetPerformanceSnapshot();
        snapshot.Handlers.Should().BeEmpty();
        snapshot.IsTracking.Should().BeTrue();
        snapshot.LogIndividualTicks.Should().BeTrue();
    }

    [Test]
    public void StartCommands_RefuseCrossOwnerReplacement()
    {
        Context health = new();
        health.Coordinator.StartHealth();
        health.Coordinator.StartPerformance(false, 0).IsError.Should().BeTrue();
        health.Coordinator.GetStatus().Owner.Should().Be(ModHealthCaptureOwner.Health);

        Context performance = new();
        performance.Coordinator.StartPerformance(false, 0);
        performance.Coordinator.StartHealth().IsError.Should().BeTrue();
        performance.Coordinator.GetStatus().Owner.Should().Be(ModHealthCaptureOwner.Performance);
    }

    [Test]
    public void HealthStart_ReplacesStoppedPerformanceSampleButPerformanceStartNeverReplacesHealthSample()
    {
        Context performance = new();
        performance.Coordinator.StartPerformance(false, 0);
        performance.Coordinator.StopPerformance();
        performance.Coordinator.StartHealth().Code.Should().Be(ModHealthCoordinatorResultCode.Replaced);
        performance.Coordinator.GetStatus().Owner.Should().Be(ModHealthCaptureOwner.Health);

        Context health = new();
        health.Coordinator.StartHealth();
        health.Coordinator.StopHealth();
        health.Queue.SetLastState(ModHealthExportState.Succeeded);
        health.Coordinator.StartPerformance(false, 0).IsError.Should().BeTrue();
        health.Coordinator.GetStatus().Owner.Should().Be(ModHealthCaptureOwner.Health);
    }

    [Test]
    public void PerformanceCommands_CannotResetHealthButStopQueuesFinalHealthExport()
    {
        Context context = new();
        context.Coordinator.StartHealth();

        context.Coordinator.ResetPerformance().IsError.Should().BeTrue();
        ModHealthCoordinatorResult stopped = context.Coordinator.StopPerformance();

        stopped.Code.Should().Be(ModHealthCoordinatorResultCode.ExportQueued);
        context.Queue.LastRequest!.CompletionReason.Should().Be(ModHealthCompletionReason.PerformanceStop);
        context.Queue.LastRequest.IsFinal.Should().BeTrue();
        context.Coordinator.GetStatus().CaptureState.Should().Be(ModHealthCaptureState.StoppedRetained);
    }

    [Test]
    public void HealthReport_FromActivePerformanceCapture_IsInterimAndDoesNotChangeOwnership()
    {
        Context context = new();
        context.Coordinator.StartPerformance(false, 0);

        context.Coordinator.ReportHealth();

        context.Queue.LastRequest!.Owner.Should().Be(ModHealthCaptureOwner.Performance);
        context.Queue.LastRequest.CompletionReason.Should().Be(ModHealthCompletionReason.InterimReport);
        context.Queue.LastRequest.IsFinal.Should().BeFalse();
        context.Coordinator.GetStatus().Owner.Should().Be(ModHealthCaptureOwner.Performance);
        context.Coordinator.GetStatus().CaptureState.Should().Be(ModHealthCaptureState.Active);
    }

    [Test]
    public void HealthStop_FromActivePerformanceCapture_FreezesItAndExcludesOpenTick()
    {
        Context context = new();
        context.Coordinator.StartPerformance(false, 0);
        context.Manager.BeginTick(42, 10);

        context.Coordinator.StopHealth();

        context.Queue.LastRequest!.Performance!.CompletedTickCount.Should().Be(0);
        context.Queue.LastRequest.Performance.IsTracking.Should().BeFalse();
        context.Queue.LastRequest.Owner.Should().Be(ModHealthCaptureOwner.Performance);
        context.Queue.LastRequest.CompletionReason.Should().Be(ModHealthCompletionReason.PerformanceStop);
    }

    [Test]
    public void Stop_FreezesElapsedHealthTimingAndLedgerCutoff()
    {
        Context context = new();
        context.Coordinator.StartHealth();
        context.Timestamp = 5000;

        context.Coordinator.StopHealth();
        ModHealthExportRequest request = context.Queue.LastRequest!;
        double frozenElapsed = request.Performance!.Elapsed.TotalMilliseconds;
        long frozenCutoff = request.Ledger.CutoffSequence;
        request.Performance.Health.Should().NotBeNull();
        request.Performance.Health!.SlowUpdateThresholdMilliseconds.Should().Be(ModHealthReportLimits.SlowUpdateMilliseconds);

        context.Timestamp = 9000;
        context.Ledger.ObserveLog(new ModHealthLogObservation("example.mod", "Example", ModHealthLogSourceCategory.Mod, LogLevel.Error, 12, 1));

        context.Coordinator.GetPerformanceSnapshot().Elapsed.TotalMilliseconds.Should().Be(frozenElapsed);
        request.Ledger.CutoffSequence.Should().Be(frozenCutoff);
        request.Ledger.LogTotalsSinceLedgerStart.ErrorMessages.Should().Be(0);
    }

    [Test]
    public void RepeatedReportOfRetainedCapture_ReusesCompletedExport()
    {
        Context context = new();
        context.Coordinator.StartHealth();
        context.Coordinator.StopHealth();
        Guid requestId = context.Queue.LastRequest!.RequestId;
        context.Queue.SetLastState(ModHealthExportState.Succeeded, "ErrorLogs/HealthReports/report.txt", "ErrorLogs/HealthReports/report.json");

        ModHealthCoordinatorResult result = context.Coordinator.ReportHealth();

        result.Code.Should().Be(ModHealthCoordinatorResultCode.ExportAlreadySucceeded);
        context.Queue.Requests.Should().ContainSingle();
        context.Queue.LastRequest.RequestId.Should().Be(requestId);
    }

    [Test]
    public void NoCapture_ReportAndStopQueueLedgerOnlyRequests()
    {
        Context report = new();
        report.Coordinator.ReportHealth();
        report.Queue.LastRequest!.Performance.Should().BeNull();
        report.Queue.LastRequest.Owner.Should().Be(ModHealthCaptureOwner.None);
        report.Queue.LastRequest.IsFinal.Should().BeFalse();

        Context stop = new();
        stop.Coordinator.StopHealth();
        stop.Queue.LastRequest!.Performance.Should().BeNull();
        stop.Queue.LastRequest.IsFinal.Should().BeTrue();
    }

    [Test]
    public void HealthReset_RequiresConfirmationAtCommandLayerAndRestartsActiveHealthWindow()
    {
        Context context = new();
        context.Coordinator.StartHealth();
        context.Manager.RecordHandler("example.mod", "Example", "event", "callback", 10, failed: false);

        ModHealthCoordinatorResult result = context.Coordinator.ResetHealth();

        result.Code.Should().Be(ModHealthCoordinatorResultCode.Reset);
        ModHealthSessionStatus status = context.Coordinator.GetStatus();
        status.CaptureState.Should().Be(ModHealthCaptureState.Active);
        status.Owner.Should().Be(ModHealthCaptureOwner.Health);
        status.Performance!.Handlers.Should().BeEmpty();
    }

    [Test]
    public void HealthReset_ClearsStoppedHealthButRefusesPerformanceOwner()
    {
        Context health = new();
        health.Coordinator.StartHealth();
        health.Coordinator.StopHealth();
        health.Queue.SetLastState(ModHealthExportState.Failed);
        health.Coordinator.ResetHealth().IsError.Should().BeFalse();
        health.Coordinator.GetStatus().CaptureState.Should().Be(ModHealthCaptureState.Inactive);

        Context performance = new();
        performance.Coordinator.StartPerformance(false, 0);
        performance.Coordinator.ResetHealth().IsError.Should().BeTrue();
        performance.Coordinator.GetStatus().Owner.Should().Be(ModHealthCaptureOwner.Performance);
    }

    [TestCase(ModHealthExportState.Queued)]
    [TestCase(ModHealthExportState.Writing)]
    public void HealthReset_RefusesWhileExportIsInFlight(ModHealthExportState state)
    {
        Context context = new();
        context.Coordinator.ReportHealth();
        context.Queue.SetLastState(state);

        context.Coordinator.ResetHealth().IsError.Should().BeTrue();
    }

    [TestCase(ModHealthExportState.Queued)]
    [TestCase(ModHealthExportState.Writing)]
    public void PerformanceReset_RefusesWhileExportIsInFlight(ModHealthExportState state)
    {
        Context context = new();
        context.Coordinator.StartPerformance(false, 0);
        context.Manager.RecordHandler("example.mod", "Example", "event", "callback", 10, failed: false);
        context.Coordinator.ReportHealth();
        context.Queue.SetLastState(state);

        ModHealthCoordinatorResult result = context.Coordinator.ResetPerformance();

        result.IsError.Should().BeTrue();
        result.Export!.State.Should().Be(state);
        context.Coordinator.GetStatus().Owner.Should().Be(ModHealthCaptureOwner.Performance);
        context.Coordinator.GetPerformanceSnapshot().Handlers.Should().ContainSingle();
    }

    [Test]
    public void Retry_DelegatesExactFrozenRequest()
    {
        Context context = new();
        context.Coordinator.StartHealth();
        context.Coordinator.StopHealth();
        ModHealthExportRequest original = context.Queue.LastRequest!;
        context.Queue.SetLastState(ModHealthExportState.Failed);

        ModHealthCoordinatorResult result = context.Coordinator.RetryHealthExport();

        result.Code.Should().Be(ModHealthCoordinatorResultCode.ExportRetried);
        context.Queue.LastRequest.Should().BeSameAs(original);
    }

    [TestCase(ModHealthExportDisposition.Coalesced, ModHealthCoordinatorResultCode.ExportPending, false)]
    [TestCase(ModHealthExportDisposition.RejectedBusy, ModHealthCoordinatorResultCode.Refused, true)]
    public void Retry_ReportsAlreadyScheduledAndBusyStatesExplicitly(ModHealthExportDisposition disposition, ModHealthCoordinatorResultCode expectedCode, bool expectedError)
    {
        Context context = new();
        context.Queue.SetNextRetryResult(disposition, ModHealthExportState.Queued);

        ModHealthCoordinatorResult result = context.Coordinator.RetryHealthExport();

        result.Code.Should().Be(expectedCode);
        result.IsError.Should().Be(expectedError);
        result.Message.Should().NotContain("no failed frozen health report");
    }

    [Test]
    public void FailedRetainedReport_RequiresExactRetryInsteadOfRebuilding()
    {
        Context context = new();
        context.Coordinator.StartHealth();
        context.Coordinator.StopHealth();
        context.Queue.SetLastState(ModHealthExportState.Failed);

        ModHealthCoordinatorResult result = context.Coordinator.ReportHealth();

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("health retry");
        context.Queue.Requests.Should().ContainSingle();
    }

    [Test]
    public void Marks_AreNumericBoundedAndRequireAnActiveCapture()
    {
        Context context = new();
        context.Coordinator.Mark().IsError.Should().BeTrue();
        context.Coordinator.StartHealth();

        for (int i = 0; i < ModHealthReportLimits.MaxMarks; i++)
            context.Coordinator.Mark().IsError.Should().BeFalse();

        context.Coordinator.Mark().IsError.Should().BeTrue();
        context.Coordinator.GetStatus().MarkCount.Should().Be(ModHealthReportLimits.MaxMarks);
    }

    [Test]
    public void Mark_BeforeFirstCompletedUpdateUsesCurrentTickAndAssociatesWithThatUpdate()
    {
        Context context = new(currentUpdateTick: 42);
        context.Coordinator.StartHealth();

        context.Coordinator.Mark();
        context.Manager.BeginTick(42, 0);
        context.Manager.CompleteTick(40);
        context.Coordinator.StopHealth();

        ModHealthExportRequest request = context.Queue.LastRequest!;
        request.Marks.Should().ContainSingle().Which.UpdateTick.Should().Be(42);
        ModHealthReport report = new ModHealthReportBuilder().Build(request, request.Environment!);
        report.Performance.WorstUpdates.Should().ContainSingle().Which.NearbyMark.Should().Be(1);
    }

    [Test]
    public void InitialSettings_GiveHealthOnLaunchPrecedence()
    {
        Context context = new();

        context.Coordinator.ApplySettings(new(true, true, true, 5), initialLoad: true);

        ModHealthSessionStatus status = context.Coordinator.GetStatus();
        status.Owner.Should().Be(ModHealthCaptureOwner.Health);
        status.Origin.Should().Be(ModHealthCaptureOrigin.HealthOnLaunch);
        status.Performance!.LogIndividualTicks.Should().BeFalse();
        status.Performance.TickLogThresholdMilliseconds.Should().Be(ModHealthReportLimits.SlowUpdateMilliseconds);
    }

    [Test]
    public void ReloadSettings_DuringManualCaptureUpdatesTickLoggingAndQueuesDestructiveChange()
    {
        Context context = new();
        context.Coordinator.StartPerformance(false, 0);

        ModHealthCoordinatorResult result = context.Coordinator.ApplySettings(new(false, false, true, 44), initialLoad: false);

        result.Code.Should().Be(ModHealthCoordinatorResultCode.SettingsPending);
        ModHealthSessionStatus status = context.Coordinator.GetStatus();
        status.CaptureState.Should().Be(ModHealthCaptureState.Active);
        status.HasPendingConfiguration.Should().BeTrue();
        status.Performance!.LogIndividualTicks.Should().BeTrue();
        status.Performance.TickLogThresholdMilliseconds.Should().Be(44);
    }

    [Test]
    public void ReloadSettings_DoesNotDiscardHealthOnLaunchAndAppliesAlternateAfterExport()
    {
        Context context = new();
        context.Coordinator.ApplySettings(new(true, false, false, 0), initialLoad: true);
        context.Manager.RecordHandler("example.mod", "Example", "event", "callback", 10, failed: false);

        ModHealthCoordinatorResult reload = context.Coordinator.ApplySettings(new(false, true, true, 44), initialLoad: false);

        reload.Code.Should().Be(ModHealthCoordinatorResultCode.SettingsPending);
        ModHealthSessionStatus active = context.Coordinator.GetStatus();
        active.CaptureState.Should().Be(ModHealthCaptureState.Active);
        active.Owner.Should().Be(ModHealthCaptureOwner.Health);
        active.Origin.Should().Be(ModHealthCaptureOrigin.HealthOnLaunch);
        active.Performance!.Handlers.Should().ContainSingle();

        context.Coordinator.StopHealth();
        context.Queue.SetLastState(ModHealthExportState.Succeeded);
        ModHealthSessionStatus switched = context.Coordinator.GetStatus();

        switched.CaptureState.Should().Be(ModHealthCaptureState.Active);
        switched.Owner.Should().Be(ModHealthCaptureOwner.Performance);
        switched.Origin.Should().Be(ModHealthCaptureOrigin.Configuration);
        switched.HasPendingConfiguration.Should().BeFalse();
        switched.Performance!.LogIndividualTicks.Should().BeTrue();
        switched.Performance.TickLogThresholdMilliseconds.Should().Be(44);
    }

    [Test]
    public void FinalExportCompletion_AppliesPendingConfigurationWithoutStatusPolling()
    {
        Context context = new();
        context.Coordinator.ApplySettings(new(true, false, false, 0), initialLoad: true);
        context.Coordinator.ApplySettings(new(false, true, false, 20), initialLoad: false);
        context.Coordinator.StopHealth();
        Guid requestId = context.Queue.LastRequest!.RequestId;

        context.Coordinator.HandleExportCompleted(new ModHealthExportStatus(ModHealthExportState.Succeeded, requestId, IsFinal: true, "ErrorLogs/HealthReports/report.txt", "ErrorLogs/HealthReports/report.json"));

        context.Manager.IsTracking.Should().BeTrue();
        ModHealthSessionStatus status = context.Coordinator.GetStatus();
        status.Owner.Should().Be(ModHealthCaptureOwner.Performance);
        status.Origin.Should().Be(ModHealthCaptureOrigin.Configuration);
        status.HasPendingConfiguration.Should().BeFalse();
    }

    [Test]
    public void ResetHealth_AppliesPendingAlternateConfiguration()
    {
        Context context = new();
        context.Coordinator.ApplySettings(new(true, false, false, 0), initialLoad: true);
        context.Coordinator.ApplySettings(new(false, true, false, 20), initialLoad: false);

        ModHealthCoordinatorResult reset = context.Coordinator.ResetHealth();

        reset.Code.Should().Be(ModHealthCoordinatorResultCode.Reset);
        ModHealthSessionStatus status = context.Coordinator.GetStatus();
        status.CaptureState.Should().Be(ModHealthCaptureState.Active);
        status.Owner.Should().Be(ModHealthCaptureOwner.Performance);
        status.Origin.Should().Be(ModHealthCaptureOrigin.Configuration);
        status.HasPendingConfiguration.Should().BeFalse();
    }

    [Test]
    public void PendingSettings_ApplyAfterAdvancedStopSnapshotIsConsumed()
    {
        Context context = new();
        context.Coordinator.StartPerformance(false, 0);
        context.Coordinator.ApplySettings(new(true, true, false, 20), initialLoad: false);

        context.Coordinator.StopPerformance();
        ModPerformanceSnapshot finalAdvancedSnapshot = context.Coordinator.GetPerformanceSnapshot();

        finalAdvancedSnapshot.IsTracking.Should().BeFalse();
        ModHealthSessionStatus status = context.Coordinator.GetStatus();
        status.Owner.Should().Be(ModHealthCaptureOwner.Health);
        status.Origin.Should().Be(ModHealthCaptureOrigin.HealthOnLaunch);
        status.CaptureState.Should().Be(ModHealthCaptureState.Active);
        status.HasPendingConfiguration.Should().BeFalse();
    }

    [Test]
    public void NormalShutdown_FinalizesHealthOwnedCaptures()
    {
        Context configured = new();
        configured.Coordinator.ApplySettings(new(true, false, false, 0), initialLoad: true);

        ModHealthCoordinatorResult? result = configured.Coordinator.FinalizeNormalShutdown();

        result!.Code.Should().Be(ModHealthCoordinatorResultCode.ExportQueued);
        configured.Queue.LastRequest!.CompletionReason.Should().Be(ModHealthCompletionReason.NormalShutdown);
        configured.Queue.LastRequest.IsFinal.Should().BeTrue();

        Context manual = new();
        manual.Coordinator.StartHealth();
        manual.Coordinator.FinalizeNormalShutdown()!.Code.Should().Be(ModHealthCoordinatorResultCode.ExportQueued);
        manual.Queue.LastRequest!.CompletionReason.Should().Be(ModHealthCompletionReason.NormalShutdown);
    }

    [Test]
    public void ExportRequest_CapturesEnvironmentAndLifecycleCompletenessBeforeBackgroundBuild()
    {
        Context configured = new();
        configured.Coordinator.ApplySettings(new(true, false, false, 0), initialLoad: true);
        configured.Coordinator.StopHealth();

        configured.Queue.LastRequest!.Environment.Should().NotBeNull();
        configured.Queue.LastRequest.Environment!.StartupObserved.Should().BeTrue();
        configured.Queue.LastRequest.Environment.LifecycleTimingObserved.Should().BeTrue();

        Context manual = new();
        manual.Coordinator.StartHealth();
        manual.Coordinator.StopHealth();
        manual.Queue.LastRequest!.Environment!.StartupObserved.Should().BeTrue();
        manual.Queue.LastRequest.Environment.LifecycleTimingObserved.Should().BeFalse();
    }

    [Test]
    public void ConfigurationCapture_OnlyClaimsLifecycleTimingWhenStartedBeforeLifecycleCallbacks()
    {
        Context startup = new(isLifecycleTimingAvailable: true);
        startup.Coordinator.ApplySettings(new(true, false, false, 0), initialLoad: true);
        startup.Coordinator.StopHealth();
        startup.Queue.LastRequest!.Environment!.LifecycleTimingObserved.Should().BeTrue();

        Context reload = new(isLifecycleTimingAvailable: false);
        reload.Coordinator.ApplySettings(new(true, false, false, 0), initialLoad: false);
        reload.Coordinator.StopHealth();
        reload.Queue.LastRequest!.Environment!.LifecycleTimingObserved.Should().BeFalse();
    }

    private sealed class Context
    {
        public long Timestamp = 0;
        public ModPerformanceManager Manager { get; }
        public ModHealthLedger Ledger { get; }
        public FakeExportQueue Queue { get; } = new();
        public ModHealthSessionCoordinator Coordinator { get; }

        public Context(bool? isLifecycleTimingAvailable = null, uint? currentUpdateTick = null)
        {
            this.Manager = new ModPerformanceManager(timestampFrequency: 1000, getTimestamp: () => this.Timestamp, getGcCollectionCount: _ => 0);
            this.Ledger = new ModHealthLedger(timestampFrequency: 1000, getTimestamp: () => this.Timestamp);
            this.Coordinator = new ModHealthSessionCoordinator(
                this.Manager,
                this.Ledger,
                this.Queue,
                getUtcNow: () => new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero),
                getEnvironment: () => new ModHealthEnvironmentSnapshot("4.5.2", "abcdef0", "1.6.15", ".NET 10", "x64", 64, "Linux", "6.0", "wayland", "en", 8, "single-player", 1, false, false),
                isLifecycleTimingAvailable: isLifecycleTimingAvailable.HasValue ? () => isLifecycleTimingAvailable.Value : null,
                getCurrentUpdateTick: currentUpdateTick.HasValue ? () => currentUpdateTick.Value : null
            );
        }
    }

    private sealed class FakeExportQueue : IModHealthExportQueue
    {
        private readonly Dictionary<Guid, ModHealthExportStatus> Statuses = [];
        private ModHealthExportRequest? Retryable;
        private ModHealthExportQueueResult? NextRetryResult;

        public List<ModHealthExportRequest> Requests { get; } = [];
        public ModHealthExportRequest? LastRequest => this.Requests.Count > 0 ? this.Requests[^1] : null;

        public ModHealthExportQueueResult Enqueue(ModHealthExportRequest request)
        {
            this.Requests.Add(request);
            ModHealthExportStatus status = new(ModHealthExportState.Queued, request.RequestId, request.IsFinal);
            this.Statuses[request.RequestId] = status;
            return new ModHealthExportQueueResult(ModHealthExportDisposition.Queued, status);
        }

        public ModHealthExportQueueResult Retry()
        {
            if (this.NextRetryResult is ModHealthExportQueueResult result)
            {
                this.NextRetryResult = null;
                return result;
            }
            if (this.Retryable == null)
                return new ModHealthExportQueueResult(ModHealthExportDisposition.NoRetryableExport, ModHealthExportStatus.None);
            ModHealthExportStatus status = new(ModHealthExportState.Queued, this.Retryable.RequestId, this.Retryable.IsFinal);
            this.Statuses[this.Retryable.RequestId] = status;
            return new ModHealthExportQueueResult(ModHealthExportDisposition.Retried, status);
        }

        public void DiscardRetryable()
        {
            this.Retryable = null;
        }

        public ModHealthExportStatus GetStatus(Guid? requestId = null)
        {
            if (requestId is Guid id && this.Statuses.TryGetValue(id, out ModHealthExportStatus? status))
                return status;
            return this.LastRequest != null && this.Statuses.TryGetValue(this.LastRequest.RequestId, out status)
                ? status
                : ModHealthExportStatus.None;
        }

        public void SetLastState(ModHealthExportState state, string? textPath = null, string? jsonPath = null)
        {
            ModHealthExportRequest request = this.LastRequest!;
            this.Statuses[request.RequestId] = new ModHealthExportStatus(state, request.RequestId, request.IsFinal, textPath, jsonPath, state == ModHealthExportState.Failed ? "test failure" : null);
            this.Retryable = state == ModHealthExportState.Failed ? request : null;
        }

        public void SetNextRetryResult(ModHealthExportDisposition disposition, ModHealthExportState state)
        {
            this.NextRetryResult = new ModHealthExportQueueResult(disposition, new ModHealthExportStatus(state, Guid.NewGuid(), IsFinal: true));
        }
    }
}
