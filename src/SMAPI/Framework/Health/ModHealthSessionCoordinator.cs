using System;
using System.Collections.Immutable;
using System.Linq;
using StardewModdingAPI.Framework.Performance;

namespace StardewModdingAPI.Framework.Health;

/// <summary>The exact request selected or created for an in-game health report viewer.</summary>
internal sealed record ModHealthViewPreparation(
    Guid RequestId,
    ModHealthCoordinatorResult Operation,
    ModHealthPreparedReportSnapshot PreparedReport
);

/// <summary>Coordinates the single timing collector shared by the user health workflow, advanced performance commands, and persistent settings.</summary>
internal sealed class ModHealthSessionCoordinator
{
    private readonly object SyncRoot = new();
    private readonly ModPerformanceManager PerformanceManager;
    private readonly ModHealthLedger Ledger;
    private readonly IModHealthExportQueue ExportQueue;
    private readonly Func<DateTimeOffset> GetUtcNow;
    private readonly Func<ModHealthEnvironmentSnapshot>? GetEnvironment;
    private readonly Func<bool>? IsLifecycleTimingAvailable;
    private readonly Func<uint>? GetCurrentUpdateTick;

    private ModHealthCaptureState State;
    private ModHealthCaptureOwner Owner;
    private ModHealthCaptureOrigin? Origin;
    private ModHealthLedgerBaseline? CaptureBaseline;
    private FrozenCapture? RetainedCapture;
    private ImmutableArray<ModHealthMark> Marks = ImmutableArray<ModHealthMark>.Empty;
    private double SlowUpdateThresholdMilliseconds;
    private ModHealthDiagnosticSettings? PendingSettings;
    private bool LifecycleTimingObserved;
    private Guid? ActiveInterimExportRequestId;
    private Guid? LatestLedgerOnlyExportRequestId;

    /// <summary>Construct a coordinator.</summary>
    public ModHealthSessionCoordinator(ModPerformanceManager performanceManager, ModHealthLedger ledger, IModHealthExportQueue exportQueue, Func<DateTimeOffset>? getUtcNow = null, Func<ModHealthEnvironmentSnapshot>? getEnvironment = null, Func<bool>? isLifecycleTimingAvailable = null, Func<uint>? getCurrentUpdateTick = null)
    {
        this.PerformanceManager = performanceManager;
        this.Ledger = ledger;
        this.ExportQueue = exportQueue;
        this.GetUtcNow = getUtcNow ?? (() => DateTimeOffset.UtcNow);
        this.GetEnvironment = getEnvironment;
        this.IsLifecycleTimingAvailable = isLifecycleTimingAvailable;
        this.GetCurrentUpdateTick = getCurrentUpdateTick;
    }

    /// <summary>Start a fresh user-facing health capture.</summary>
    public ModHealthCoordinatorResult StartHealth(ModHealthCaptureOrigin origin = ModHealthCaptureOrigin.Manual)
    {
        lock (this.SyncRoot)
        {
            this.RefreshSucceededRetainedExport();
            if (this.State == ModHealthCaptureState.Active)
            {
                string command = this.Owner == ModHealthCaptureOwner.Health
                    ? "Use 'health stop' to finish it or 'health reset confirm' to discard and restart it."
                    : "Use 'performance stop' before starting a health sample.";
                return Refused($"A {GetOwnerName(this.Owner)} timing sample is already active. {command}");
            }

            if (this.State == ModHealthCaptureState.StoppedRetained && this.Owner == ModHealthCaptureOwner.Health && !this.CanReplaceRetainedHealthCapture())
            {
                ModHealthExportStatus export = this.GetRetainedExportStatus();
                string next = export.State == ModHealthExportState.Failed
                    ? "Use 'health retry' or 'health reset confirm'."
                    : "Use 'health report' or 'health reset confirm'.";
                return Refused($"The stopped health sample has not been exported successfully. {next}", export);
            }

            bool replaced = this.State == ModHealthCaptureState.StoppedRetained;
            this.StartCore(ModHealthCaptureOwner.Health, origin, logTicks: false, ModHealthReportLimits.SlowUpdateMilliseconds);
            return new ModHealthCoordinatorResult(
                replaced ? ModHealthCoordinatorResultCode.Replaced : ModHealthCoordinatorResultCode.Started,
                replaced
                    ? "Recording a new mod health sample. The previously exported sample was replaced. Reproduce the problem, then enter 'health stop'."
                    : "Recording a mod health sample. Reproduce the problem, then enter 'health stop'."
            );
        }
    }

    /// <summary>Start a fresh advanced performance capture.</summary>
    public ModHealthCoordinatorResult StartPerformance(bool logIndividualTicks, double tickLogThresholdMilliseconds, ModHealthCaptureOrigin origin = ModHealthCaptureOrigin.Manual)
    {
        lock (this.SyncRoot)
        {
            if (this.Owner == ModHealthCaptureOwner.Health && this.State != ModHealthCaptureState.Inactive)
                return Refused("A health-owned sample cannot be replaced by 'performance start'. Use 'health stop' or 'health reset confirm' first.");

            bool replaced = this.Owner == ModHealthCaptureOwner.Performance && this.State != ModHealthCaptureState.Inactive;
            this.StartCore(ModHealthCaptureOwner.Performance, origin, logIndividualTicks, tickLogThresholdMilliseconds);
            return new ModHealthCoordinatorResult(
                replaced ? ModHealthCoordinatorResultCode.Replaced : ModHealthCoordinatorResultCode.Started,
                replaced ? "Started a fresh advanced performance sample; the previous advanced sample was reset." : "Started a fresh advanced performance sample."
            );
        }
    }

    /// <summary>Change live advanced tick logging without changing capture ownership.</summary>
    public void ConfigureTickLogging(bool enabled, double thresholdMilliseconds)
    {
        lock (this.SyncRoot)
            this.PerformanceManager.ConfigureTickLogging(enabled, thresholdMilliseconds);
    }

    /// <summary>Add a numeric reproduction mark to the active capture.</summary>
    public ModHealthCoordinatorResult Mark()
    {
        lock (this.SyncRoot)
        {
            if (this.State != ModHealthCaptureState.Active)
                return Refused("No timing sample is active. Enter 'health start' before adding a mark.");
            if (this.Marks.Length >= ModHealthReportLimits.MaxMarks)
                return Refused($"The sample already contains the maximum of {ModHealthReportLimits.MaxMarks} marks.");

            ModPerformanceSnapshot snapshot = this.PerformanceManager.GetSnapshot();
            uint tick = this.GetCurrentUpdateTick?.Invoke()
                ?? (snapshot.RecentTicks.Count > 0 ? snapshot.RecentTicks[^1].Tick : 0);
            ModHealthMark mark = new(this.Marks.Length + 1, tick, snapshot.Elapsed.TotalMilliseconds);
            this.Marks = this.Marks.Add(mark);
            return new ModHealthCoordinatorResult(ModHealthCoordinatorResultCode.Marked, $"Added reproduction mark #{mark.Number} at update tick {mark.UpdateTick}.");
        }
    }

    /// <summary>Queue a health-format report without stopping the active capture.</summary>
    public ModHealthCoordinatorResult ReportHealth()
    {
        lock (this.SyncRoot)
        {
            this.RefreshSucceededRetainedExport();
            if (this.State == ModHealthCaptureState.StoppedRetained)
                return this.QueueRetainedCapture();

            if (this.State == ModHealthCaptureState.Active)
            {
                ModPerformanceSnapshot performance = FreezeSnapshot(this.PerformanceManager.GetSnapshot());
                ModHealthExportRequest request = this.CreateRequest(
                    this.Owner,
                    this.Origin,
                    ModHealthCompletionReason.InterimReport,
                    performance,
                    this.Ledger.GetSnapshot(this.CaptureBaseline),
                    this.Marks,
                    isFinal: false
                );
                ModHealthCoordinatorResult result = this.Enqueue(request, "Health report queued from an interim timing snapshot.");
                if (!result.IsError)
                    this.ActiveInterimExportRequestId = request.RequestId;
                return result;
            }

            ModHealthExportRequest ledgerOnly = this.CreateRequest(
                ModHealthCaptureOwner.None,
                origin: null,
                ModHealthCompletionReason.InterimReport,
                performance: null,
                this.Ledger.GetSnapshot(),
                ImmutableArray<ModHealthMark>.Empty,
                isFinal: false
            );
            ModHealthCoordinatorResult ledgerResult = this.Enqueue(ledgerOnly, "Session health report queued. No deep timing window was available.");
            if (!ledgerResult.IsError)
                this.LatestLedgerOnlyExportRequestId = ledgerOnly.RequestId;
            return ledgerResult;
        }
    }

    /// <summary>Select an exact current-process report for the viewer, preparing one snapshot only when needed.</summary>
    /// <param name="forceRefresh">Whether to prepare a fresh active interim or inactive ledger-only snapshot instead of reusing the current generation's request.</param>
    public ModHealthViewPreparation PrepareHealthView(bool forceRefresh = false)
    {
        lock (this.SyncRoot)
        {
            this.RefreshSucceededRetainedExport();

            if (this.State == ModHealthCaptureState.StoppedRetained)
            {
                Guid? retainedId = this.RetainedCapture?.ExportRequestId;
                if (retainedId is Guid existingId && this.HasExactRequest(existingId))
                    return this.CreateViewPreparation(existingId, "Using the exact retained final health report.");

                ModHealthCoordinatorResult queued = this.QueueRetainedCapture();
                Guid requestId = this.RetainedCapture!.ExportRequestId!.Value;
                return this.CreateViewPreparation(requestId, queued);
            }

            if (this.State == ModHealthCaptureState.Active)
            {
                if (!forceRefresh && this.ActiveInterimExportRequestId is Guid existingId && this.HasExactRequest(existingId))
                    return this.CreateViewPreparation(existingId, "Using the current capture's prepared interim report.");

                ModPerformanceSnapshot performance = FreezeSnapshot(this.PerformanceManager.GetSnapshot());
                ModHealthExportRequest request = this.CreateRequest(
                    this.Owner,
                    this.Origin,
                    ModHealthCompletionReason.InterimReport,
                    performance,
                    this.Ledger.GetSnapshot(this.CaptureBaseline),
                    this.Marks,
                    isFinal: false
                );
                ModHealthCoordinatorResult queued = this.Enqueue(request, "Health viewer report queued from an interim timing snapshot.");
                if (!queued.IsError)
                    this.ActiveInterimExportRequestId = request.RequestId;
                return this.CreateViewPreparation(request.RequestId, queued);
            }

            if (!forceRefresh && this.LatestLedgerOnlyExportRequestId is Guid ledgerId && this.HasExactRequest(ledgerId))
                return this.CreateViewPreparation(ledgerId, "Using the latest prepared session health report.");

            ModHealthExportRequest ledgerOnly = this.CreateRequest(
                ModHealthCaptureOwner.None,
                origin: null,
                ModHealthCompletionReason.InterimReport,
                performance: null,
                this.Ledger.GetSnapshot(),
                ImmutableArray<ModHealthMark>.Empty,
                isFinal: false
            );
            ModHealthCoordinatorResult ledgerResult = this.Enqueue(ledgerOnly, "Session health viewer report queued. No deep timing window was available.");
            if (!ledgerResult.IsError)
                this.LatestLedgerOnlyExportRequestId = ledgerOnly.RequestId;
            return this.CreateViewPreparation(ledgerOnly.RequestId, ledgerResult);
        }
    }

    /// <summary>Get the prepared state for one exact viewer request without substituting a newer report.</summary>
    public ModHealthPreparedReportSnapshot GetPreparedHealthReport(Guid requestId)
    {
        lock (this.SyncRoot)
            return this.ExportQueue.GetPreparedReport(requestId);
    }

    /// <summary>Stop any active capture and queue a health-format final report, or export retained/session-only evidence.</summary>
    public ModHealthCoordinatorResult StopHealth()
    {
        lock (this.SyncRoot)
        {
            if (this.State == ModHealthCaptureState.Active)
            {
                ModHealthCompletionReason reason = this.Owner == ModHealthCaptureOwner.Performance
                    ? ModHealthCompletionReason.PerformanceStop
                    : ModHealthCompletionReason.UserStop;
                this.FreezeActiveCapture(reason);
            }

            if (this.State == ModHealthCaptureState.StoppedRetained)
                return this.QueueRetainedCapture();

            ModHealthExportRequest request = this.CreateRequest(
                ModHealthCaptureOwner.None,
                origin: null,
                ModHealthCompletionReason.UserStop,
                performance: null,
                this.Ledger.GetSnapshot(),
                ImmutableArray<ModHealthMark>.Empty,
                isFinal: true
            );
            ModHealthCoordinatorResult result = this.Enqueue(request, "Session health report queued. No deep timing window was available.");
            if (!result.IsError)
                this.LatestLedgerOnlyExportRequestId = request.RequestId;
            return result;
        }
    }

    /// <summary>Stop advanced sampling. A health-owned sample is also queued for final health export.</summary>
    public ModHealthCoordinatorResult StopPerformance()
    {
        lock (this.SyncRoot)
        {
            if (this.State != ModHealthCaptureState.Active)
                return new ModHealthCoordinatorResult(ModHealthCoordinatorResultCode.Stopped, "Mod performance sampling is already stopped.");

            bool healthOwned = this.Owner == ModHealthCaptureOwner.Health;
            this.FreezeActiveCapture(ModHealthCompletionReason.PerformanceStop);
            if (healthOwned)
                return this.QueueRetainedCapture();
            return new ModHealthCoordinatorResult(ModHealthCoordinatorResultCode.Stopped, "Stopped the advanced performance sample.");
        }
    }

    /// <summary>Apply the explicit health reset confirmation.</summary>
    public ModHealthCoordinatorResult ResetHealth()
    {
        lock (this.SyncRoot)
            return this.ResetHealthCore(expectedRequestId: null);
    }

    /// <summary>Reset health evidence only if the viewer still owns the exact current request.</summary>
    public ModHealthCoordinatorResult ResetHealth(Guid expectedRequestId)
    {
        lock (this.SyncRoot)
        {
            ModHealthPreparedReportSnapshot prepared = this.ExportQueue.GetPreparedReport(expectedRequestId);
            if (prepared.State == ModHealthPreparedReportState.Superseded)
            {
                string supersededMessage = prepared.NewerRequestId is Guid newerId
                    ? $"That report was superseded by newer request {newerId}. View the newer report instead of resetting current evidence."
                    : "That report was superseded. Reopen the viewer before resetting current evidence.";
                return Refused(supersededMessage, this.ExportQueue.GetStatus(expectedRequestId));
            }
            if (prepared.State is ModHealthPreparedReportState.Canceled or ModHealthPreparedReportState.Disposed)
                return Refused("That report is no longer active. Reopen the viewer before resetting current evidence.", this.ExportQueue.GetStatus(expectedRequestId));

            Guid? currentRequestId = this.GetCurrentViewRequestId();
            if (currentRequestId != expectedRequestId)
            {
                return Refused("That viewer no longer owns the current health report. Reopen the viewer before resetting evidence.", this.ExportQueue.GetStatus(expectedRequestId));
            }
            return this.ResetHealthCore(expectedRequestId);
        }
    }

    private ModHealthCoordinatorResult ResetHealthCore(Guid? expectedRequestId)
    {
        ModHealthExportStatus export = this.ExportQueue.GetStatus();
        if (export.State is ModHealthExportState.Queued or ModHealthExportState.Writing)
            return Refused("A health report is queued or being written. Wait for it to finish before resetting.", export);
        if (this.Owner == ModHealthCaptureOwner.Performance && this.State != ModHealthCaptureState.Inactive)
            return Refused("The sample is performance-owned. Use 'performance reset' instead.");

        bool restart = this.State == ModHealthCaptureState.Active && this.Owner == ModHealthCaptureOwner.Health;
        bool applyPendingSettings = this.PendingSettings.HasValue;
        this.ExportQueue.DiscardRetryable(expectedRequestId);
        this.ClearCore();
        this.PerformanceManager.Reset();
        if (applyPendingSettings)
        {
            this.ApplyPendingSettingsIfPossible();
            return new ModHealthCoordinatorResult(ModHealthCoordinatorResultCode.Reset, "Discarded the timed evidence and applied the pending persistent diagnostic settings. The session ledger was kept.");
        }
        if (restart)
        {
            this.StartCore(ModHealthCaptureOwner.Health, ModHealthCaptureOrigin.Manual, logTicks: false, ModHealthReportLimits.SlowUpdateMilliseconds);
            return new ModHealthCoordinatorResult(ModHealthCoordinatorResultCode.Reset, "Discarded the timed evidence and started a fresh health window. The session ledger was kept.");
        }

        this.ApplyPendingSettingsIfPossible();
        return new ModHealthCoordinatorResult(ModHealthCoordinatorResultCode.Reset, "Discarded the retained timed evidence. The session ledger was kept.");
    }

    /// <summary>Clear advanced diagnostics without changing whether advanced sampling is active.</summary>
    public ModHealthCoordinatorResult ResetPerformance()
    {
        lock (this.SyncRoot)
        {
            ModHealthExportStatus export = this.ExportQueue.GetStatus();
            if (export.State is ModHealthExportState.Queued or ModHealthExportState.Writing)
                return Refused("A health report is queued or being written. Wait for it to finish before resetting performance diagnostics.", export);
            if (this.Owner == ModHealthCaptureOwner.Health && this.State != ModHealthCaptureState.Inactive)
                return Refused("A health-owned sample cannot be discarded by 'performance reset'. Use 'health reset confirm'.");

            bool active = this.State == ModHealthCaptureState.Active && this.Owner == ModHealthCaptureOwner.Performance;
            this.PerformanceManager.Reset();
            this.RetainedCapture = null;
            this.Marks = ImmutableArray<ModHealthMark>.Empty;
            if (active)
            {
                this.CaptureBaseline = this.Ledger.CreateCaptureBaseline();
                this.ActiveInterimExportRequestId = null;
                return new ModHealthCoordinatorResult(ModHealthCoordinatorResultCode.Reset, "Cleared the mod performance, warning, and error diagnostics.");
            }

            this.ClearCore();
            this.ApplyPendingSettingsIfPossible();
            return new ModHealthCoordinatorResult(ModHealthCoordinatorResultCode.Reset, "Cleared the mod performance, warning, and error diagnostics.");
        }
    }

    /// <summary>Retry the exact frozen request retained by the export queue.</summary>
    public ModHealthCoordinatorResult RetryHealthExport(Guid? requestId = null)
    {
        lock (this.SyncRoot)
        {
            ModHealthExportQueueResult queued = this.ExportQueue.Retry(requestId);
            return queued.Disposition switch
            {
                ModHealthExportDisposition.Retried => new(ModHealthCoordinatorResultCode.ExportRetried, "Retrying the exact frozen health report.", Export: queued.Status),
                ModHealthExportDisposition.Pending => new(ModHealthCoordinatorResultCode.ExportPending, "The retry is pending behind the report currently being written.", Export: queued.Status),
                ModHealthExportDisposition.Coalesced => new(ModHealthCoordinatorResultCode.ExportPending, "That exact frozen health report is already queued or being written.", Export: queued.Status),
                ModHealthExportDisposition.RejectedBusy => Refused("The report queue is busy. The failed frozen health report is still retained; enter 'health retry' again after the current export finishes.", queued.Status),
                _ when requestId is Guid exactId => this.CreateExactNothingToRetry(exactId),
                _ => new(ModHealthCoordinatorResultCode.NothingToRetry, "There is no failed frozen health report to retry.", IsError: true, Export: queued.Status)
            };
        }
    }

    /// <summary>Apply any pending persistent transition when its retained final export completes.</summary>
    public void HandleExportCompleted(ModHealthExportStatus status)
    {
        lock (this.SyncRoot)
        {
            if (status.State != ModHealthExportState.Succeeded
                || !status.IsFinal
                || this.RetainedCapture?.ExportRequestId != status.RequestId
                || !this.PendingSettings.HasValue)
            {
                return;
            }

            this.ClearCore();
            this.ApplyPendingSettingsIfPossible();
        }
    }

    /// <summary>Finalize any health-owned capture during orderly shutdown.</summary>
    public ModHealthCoordinatorResult? FinalizeNormalShutdown()
    {
        lock (this.SyncRoot)
        {
            if (this.Owner != ModHealthCaptureOwner.Health)
                return null;
            if (this.State == ModHealthCaptureState.Active)
                this.FreezeActiveCapture(ModHealthCompletionReason.NormalShutdown);
            return this.State == ModHealthCaptureState.StoppedRetained
                ? this.QueueRetainedCapture()
                : null;
        }
    }

    /// <summary>Apply launch or reloaded persistent settings through the ownership coordinator.</summary>
    public ModHealthCoordinatorResult ApplySettings(ModHealthDiagnosticSettings settings, bool initialLoad)
    {
        if (!double.IsFinite(settings.PerformanceTickThresholdMilliseconds) || settings.PerformanceTickThresholdMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(settings));

        lock (this.SyncRoot)
        {
            this.PerformanceManager.ConfigureTickLogging(settings.LogPerformanceTicks, settings.PerformanceTickThresholdMilliseconds);
            bool preserveActiveCapture = this.State == ModHealthCaptureState.Active
                && (this.Origin == ModHealthCaptureOrigin.Manual || this.Owner == ModHealthCaptureOwner.Health);
            if (!initialLoad && preserveActiveCapture)
            {
                this.PendingSettings = settings;
                return new ModHealthCoordinatorResult(ModHealthCoordinatorResultCode.SettingsPending, "Persistent diagnostic start/stop changes are pending until the active sample is exported or reset; live tick logging was updated.");
            }

            this.PendingSettings = null;
            this.ApplySettingsCore(settings);
            return new ModHealthCoordinatorResult(ModHealthCoordinatorResultCode.SettingsApplied, "Persistent diagnostic settings were applied.");
        }
    }

    /// <summary>Get an immutable status snapshot.</summary>
    public ModHealthSessionStatus GetStatus()
    {
        lock (this.SyncRoot)
        {
            this.RefreshSucceededRetainedExport();
            ModPerformanceSnapshot? performance = this.State switch
            {
                ModHealthCaptureState.Active => FreezeSnapshot(this.PerformanceManager.GetSnapshot()),
                ModHealthCaptureState.StoppedRetained => this.RetainedCapture?.Performance,
                _ => null
            };
            ModHealthLedgerSnapshot sessionLedger = this.Ledger.GetSnapshot();
            ModHealthLedgerSnapshot captureLedger = this.State == ModHealthCaptureState.StoppedRetained && this.RetainedCapture != null
                ? this.RetainedCapture.Ledger
                : this.Ledger.GetSnapshot(this.CaptureBaseline);
            ModHealthSeverityCountsSnapshot totals = sessionLedger.LogTotalsSinceLedgerStart;
            long warnings = SaturatingAdd(totals.WarningMessages, totals.AlertMessages);
            ModHealthSeverityCountsSnapshot captureTotals = captureLedger.LogTotalsDuringCapture;
            long captureWarnings = SaturatingAdd(captureTotals.WarningMessages, captureTotals.AlertMessages);
            long slowUpdateCount = performance?.Health?.SlowUpdateCount ?? 0;
            int retainedSlowMoments = performance?.Health?.WorstUpdates.Count(update => update.TotalMilliseconds >= ModHealthReportLimits.SlowUpdateMilliseconds) ?? 0;
            bool capacityReached = sessionLedger.Omissions.ModInventoryRecords > 0
                || sessionLedger.Omissions.LogIdentityObservations > 0
                || sessionLedger.Omissions.CallbackFailureObservations > 0
                || sessionLedger.Omissions.DependencyIds > 0
                || (performance?.OmittedHandlerInvocations ?? 0) > 0
                || HasTimingOmissions(performance?.Health?.Omissions);
            return new ModHealthSessionStatus(
                this.State,
                this.Owner,
                this.Origin,
                performance,
                this.Marks.Length,
                warnings,
                totals.ErrorMessages,
                captureWarnings,
                captureTotals.ErrorMessages,
                slowUpdateCount,
                retainedSlowMoments,
                capacityReached,
                this.ExportQueue.GetStatus(this.RetainedCapture?.ExportRequestId),
                this.PendingSettings.HasValue
            );
        }
    }

    /// <summary>Get the minimal state needed to choose in-game viewer actions without building diagnostic snapshots.</summary>
    public ModHealthViewerActionState GetViewerActionState()
    {
        lock (this.SyncRoot)
            return new(this.State);
    }

    /// <summary>Get the current or frozen performance snapshot for the advanced report command.</summary>
    public ModPerformanceSnapshot GetPerformanceSnapshot()
    {
        lock (this.SyncRoot)
        {
            if (this.State == ModHealthCaptureState.StoppedRetained && this.RetainedCapture != null)
            {
                ModPerformanceSnapshot retained = this.RetainedCapture.Performance;
                if (this.Owner == ModHealthCaptureOwner.Performance && this.RetainedCapture.ExportRequestId == null && this.PendingSettings.HasValue)
                {
                    this.ClearCore();
                    this.ApplyPendingSettingsIfPossible();
                }
                return retained;
            }
            return this.PerformanceManager.GetSnapshot();
        }
    }

    private void StartCore(ModHealthCaptureOwner owner, ModHealthCaptureOrigin origin, bool logTicks, double threshold)
    {
        this.PerformanceManager.Start(logTicks, threshold);
        this.State = ModHealthCaptureState.Active;
        this.Owner = owner;
        this.Origin = origin;
        this.SlowUpdateThresholdMilliseconds = ModHealthReportLimits.SlowUpdateMilliseconds;
        this.CaptureBaseline = this.Ledger.CreateCaptureBaseline();
        this.RetainedCapture = null;
        this.Marks = ImmutableArray<ModHealthMark>.Empty;
        this.ActiveInterimExportRequestId = null;
        this.LatestLedgerOnlyExportRequestId = null;
        this.LifecycleTimingObserved = this.IsLifecycleTimingAvailable?.Invoke()
            ?? origin is ModHealthCaptureOrigin.Configuration or ModHealthCaptureOrigin.HealthOnLaunch;
    }

    private void FreezeActiveCapture(ModHealthCompletionReason completionReason)
    {
        this.PerformanceManager.Stop();
        ModPerformanceSnapshot performance = FreezeSnapshot(this.PerformanceManager.GetSnapshot());
        ModHealthLedgerSnapshot ledger = this.Ledger.GetSnapshot(this.CaptureBaseline);
        this.RetainedCapture = new FrozenCapture(this.Owner, this.Origin ?? ModHealthCaptureOrigin.Manual, completionReason, performance, ledger, this.Marks, this.SlowUpdateThresholdMilliseconds, null);
        this.State = ModHealthCaptureState.StoppedRetained;
        this.ActiveInterimExportRequestId = null;
    }

    private ModHealthCoordinatorResult QueueRetainedCapture()
    {
        FrozenCapture retained = this.RetainedCapture!;
        if (retained.ExportRequestId is Guid existingId)
        {
            ModHealthExportStatus existing = this.ExportQueue.GetStatus(existingId);
            if (existing.State == ModHealthExportState.Succeeded)
                return new ModHealthCoordinatorResult(ModHealthCoordinatorResultCode.ExportAlreadySucceeded, FormatCompletedPaths(existing), Export: existing);
            if (existing.State is ModHealthExportState.Queued or ModHealthExportState.Writing)
                return new ModHealthCoordinatorResult(ModHealthCoordinatorResultCode.ExportPending, "That retained health report is already queued or being written.", Export: existing);
            if (existing.State == ModHealthExportState.Failed)
                return Refused("The retained report failed to write. Use 'health retry' to retry the exact frozen report, or 'health reset confirm' to discard it.", existing);
        }

        ModHealthExportRequest request = this.CreateRequest(retained.Owner, retained.Origin, retained.CompletionReason, retained.Performance, retained.Ledger, retained.Marks, isFinal: true);
        this.RetainedCapture = retained with { ExportRequestId = request.RequestId };
        return this.Enqueue(request, "Final health report queued.");
    }

    private ModHealthExportRequest CreateRequest(ModHealthCaptureOwner owner, ModHealthCaptureOrigin? origin, ModHealthCompletionReason completionReason, ModPerformanceSnapshot? performance, ModHealthLedgerSnapshot ledger, ImmutableArray<ModHealthMark> marks, bool isFinal)
    {
        ModHealthEnvironmentSnapshot? environment = this.GetEnvironment?.Invoke();
        if (environment is not null)
        {
            bool lifecycleObserved = performance is not null && this.LifecycleTimingObserved;
            environment = environment with
            {
                StartupObserved = true,
                LifecycleTimingObserved = lifecycleObserved
            };
        }
        return new ModHealthExportRequest(Guid.NewGuid(), this.GetUtcNow().ToUniversalTime(), owner, origin, completionReason, performance, ledger, marks, this.SlowUpdateThresholdMilliseconds > 0 ? this.SlowUpdateThresholdMilliseconds : ModHealthReportLimits.SlowUpdateMilliseconds, isFinal, environment);
    }

    private ModHealthCoordinatorResult Enqueue(ModHealthExportRequest request, string queuedMessage)
    {
        ModHealthExportQueueResult queued = this.ExportQueue.Enqueue(request);
        return queued.Disposition switch
        {
            ModHealthExportDisposition.Queued => new(ModHealthCoordinatorResultCode.ExportQueued, queuedMessage, Export: queued.Status),
            ModHealthExportDisposition.Pending or ModHealthExportDisposition.Coalesced => new(ModHealthCoordinatorResultCode.ExportPending, request.IsFinal ? "The final report is pending behind the report currently being written." : "The interim report is pending behind the report currently being written.", Export: queued.Status),
            ModHealthExportDisposition.AlreadySucceeded => new(ModHealthCoordinatorResultCode.ExportAlreadySucceeded, FormatCompletedPaths(queued.Status), Export: queued.Status),
            _ => Refused("The report queue is busy; no timing data was discarded.", queued.Status)
        };
    }

    private void ApplySettingsCore(ModHealthDiagnosticSettings settings)
    {
        if (settings.EnableHealthOnLaunch)
        {
            if (this.State == ModHealthCaptureState.Active && this.Owner == ModHealthCaptureOwner.Performance && this.Origin == ModHealthCaptureOrigin.Configuration)
            {
                this.PerformanceManager.Stop();
                this.ClearCore();
            }
            if (this.State == ModHealthCaptureState.Inactive)
                this.StartCore(ModHealthCaptureOwner.Health, ModHealthCaptureOrigin.HealthOnLaunch, logTicks: false, ModHealthReportLimits.SlowUpdateMilliseconds);
            return;
        }

        if (settings.EnablePerformanceTracking)
        {
            if (this.State == ModHealthCaptureState.Inactive)
                this.StartCore(ModHealthCaptureOwner.Performance, ModHealthCaptureOrigin.Configuration, settings.LogPerformanceTicks, settings.PerformanceTickThresholdMilliseconds);
            return;
        }

        if (this.State == ModHealthCaptureState.Active && this.Origin is ModHealthCaptureOrigin.Configuration or ModHealthCaptureOrigin.HealthOnLaunch)
        {
            this.PerformanceManager.Stop();
            this.ClearCore();
        }
    }

    private void ApplyPendingSettingsIfPossible()
    {
        if (this.PendingSettings is not ModHealthDiagnosticSettings pending || this.State != ModHealthCaptureState.Inactive)
            return;
        this.PendingSettings = null;
        this.ApplySettingsCore(pending);
    }

    private void RefreshSucceededRetainedExport()
    {
        if (this.RetainedCapture?.ExportRequestId is Guid requestId)
        {
            ModHealthExportStatus status = this.ExportQueue.GetStatus(requestId);
            if (status.State == ModHealthExportState.Succeeded && this.PendingSettings.HasValue)
            {
                this.ClearCore();
                this.ApplyPendingSettingsIfPossible();
            }
        }
    }

    private bool CanReplaceRetainedHealthCapture()
    {
        return this.GetRetainedExportStatus().State == ModHealthExportState.Succeeded;
    }

    private ModHealthExportStatus GetRetainedExportStatus()
    {
        return this.RetainedCapture?.ExportRequestId is Guid requestId
            ? this.ExportQueue.GetStatus(requestId)
            : ModHealthExportStatus.None;
    }

    private void ClearCore()
    {
        this.State = ModHealthCaptureState.Inactive;
        this.Owner = ModHealthCaptureOwner.None;
        this.Origin = null;
        this.CaptureBaseline = null;
        this.RetainedCapture = null;
        this.Marks = ImmutableArray<ModHealthMark>.Empty;
        this.SlowUpdateThresholdMilliseconds = 0;
        this.LifecycleTimingObserved = false;
        this.ActiveInterimExportRequestId = null;
        this.LatestLedgerOnlyExportRequestId = null;
    }

    private bool HasExactRequest(Guid requestId)
    {
        ModHealthPreparedReportSnapshot prepared = this.ExportQueue.GetPreparedReport(requestId);
        if (prepared.State is not (ModHealthPreparedReportState.Absent or ModHealthPreparedReportState.Disposed or ModHealthPreparedReportState.Canceled))
            return true;
        return this.ExportQueue.GetStatus(requestId).State != ModHealthExportState.None;
    }

    private ModHealthViewPreparation CreateViewPreparation(Guid requestId, string message)
    {
        ModHealthExportStatus export = this.ExportQueue.GetStatus(requestId);
        ModHealthPreparedReportSnapshot prepared = this.ExportQueue.GetPreparedReport(requestId);
        ModHealthCoordinatorResult operation = prepared.State switch
        {
            ModHealthPreparedReportState.Saved => new(ModHealthCoordinatorResultCode.ExportAlreadySucceeded, message, Export: export),
            ModHealthPreparedReportState.WriteFailed => Refused("Using the exact prepared report, but it was not saved. Retry the exact request to save it.", export),
            ModHealthPreparedReportState.FailedBeforeModel => Refused("That exact report failed before a model was prepared. Retry the exact request or refresh the viewer.", export),
            ModHealthPreparedReportState.Superseded when prepared.NewerRequestId is Guid newerId => Refused($"That exact report was superseded by newer request {newerId}.", export),
            ModHealthPreparedReportState.Superseded => Refused("That exact report was superseded.", export),
            ModHealthPreparedReportState.Canceled or ModHealthPreparedReportState.Disposed => Refused("That exact report is no longer available. Refresh the viewer to prepare another report.", export),
            _ => new(ModHealthCoordinatorResultCode.ExportPending, message, Export: export)
        };
        return new(requestId, operation, prepared);
    }

    private ModHealthViewPreparation CreateViewPreparation(Guid requestId, ModHealthCoordinatorResult operation)
    {
        ModHealthPreparedReportSnapshot prepared = this.ExportQueue.GetPreparedReport(requestId);
        if (operation.IsError && prepared.State == ModHealthPreparedReportState.Absent)
            prepared = new(ModHealthPreparedReportState.Rejected, requestId, operation.Export?.IsFinal ?? false);
        return new(requestId, operation, prepared);
    }

    private Guid? GetCurrentViewRequestId()
    {
        return this.State switch
        {
            ModHealthCaptureState.Active => this.ActiveInterimExportRequestId,
            ModHealthCaptureState.StoppedRetained => this.RetainedCapture?.ExportRequestId,
            _ => this.LatestLedgerOnlyExportRequestId
        };
    }

    private ModHealthCoordinatorResult CreateExactNothingToRetry(Guid requestId)
    {
        ModHealthPreparedReportSnapshot prepared = this.ExportQueue.GetPreparedReport(requestId);
        if (prepared.State == ModHealthPreparedReportState.Superseded && prepared.NewerRequestId is Guid newerId)
            return new(ModHealthCoordinatorResultCode.NothingToRetry, $"That report was superseded by newer request {newerId}. View the newer report instead.", IsError: true, Export: this.ExportQueue.GetStatus(requestId));
        if (prepared.State == ModHealthPreparedReportState.Saved)
            return new(ModHealthCoordinatorResultCode.NothingToRetry, "That exact report is already saved and does not need a retry.", IsError: true, Export: this.ExportQueue.GetStatus(requestId));
        return new(ModHealthCoordinatorResultCode.NothingToRetry, "That exact report is not the failed report retained for retry. Reopen the viewer to see its current state.", IsError: true, Export: this.ExportQueue.GetStatus(requestId));
    }

    private static ModPerformanceSnapshot FreezeSnapshot(ModPerformanceSnapshot snapshot)
    {
        return snapshot with
        {
            Handlers = Array.AsReadOnly(snapshot.Handlers.ToArray()),
            ModLogs = Array.AsReadOnly(snapshot.ModLogs.ToArray()),
            RecentTicks = Array.AsReadOnly(snapshot.RecentTicks.ToArray()),
            Health = FreezeHealthSnapshot(snapshot.Health)
        };
    }

    private static ModHealthPerformanceSnapshot? FreezeHealthSnapshot(ModHealthPerformanceSnapshot? snapshot)
    {
        if (snapshot == null)
            return null;

        ModHealthUpdatePerformanceSnapshot[] recent = snapshot.RecentUpdates.Select(FreezeHealthUpdate).ToArray();
        ModHealthUpdatePerformanceSnapshot[] worst = snapshot.WorstUpdates.Select(FreezeHealthUpdate).ToArray();
        return snapshot with
        {
            Callbacks = Array.AsReadOnly(snapshot.Callbacks.ToArray()),
            RecentUpdates = Array.AsReadOnly(recent),
            WorstUpdates = Array.AsReadOnly(worst),
            Episodes = Array.AsReadOnly(snapshot.Episodes.ToArray()),
            Histogram = snapshot.Histogram with
            {
                Buckets = Array.AsReadOnly(snapshot.Histogram.Buckets.ToArray()),
                Thresholds = Array.AsReadOnly(snapshot.Histogram.Thresholds.ToArray())
            }
        };
    }

    private static ModHealthUpdatePerformanceSnapshot FreezeHealthUpdate(ModHealthUpdatePerformanceSnapshot update)
    {
        return update with { Contributors = Array.AsReadOnly(update.Contributors.ToArray()) };
    }

    private static ModHealthCoordinatorResult Refused(string message, ModHealthExportStatus? export = null)
    {
        return new ModHealthCoordinatorResult(ModHealthCoordinatorResultCode.Refused, message, IsError: true, Export: export);
    }

    private static string GetOwnerName(ModHealthCaptureOwner owner)
    {
        return owner == ModHealthCaptureOwner.Health ? "health" : "advanced performance";
    }

    private static string FormatCompletedPaths(ModHealthExportStatus status)
    {
        if (status.TextPath != null && status.JsonPath != null)
            return $"That report was already saved as {status.TextPath} and {status.JsonPath}.";
        return "That report was already saved successfully.";
    }

    private static long SaturatingAdd(long left, long right)
    {
        if (right > 0 && left > long.MaxValue - right)
            return long.MaxValue;
        return left + right;
    }

    private static bool HasTimingOmissions(ModHealthTimingOmissions? omissions)
    {
        return omissions is ModHealthTimingOmissions value
            && (value.RecentUpdates > 0
                || value.WorstUpdates > 0
                || value.SlowEpisodes > 0
                || value.ContributorObservations > 0
                || value.SlowUpdateContributorIdentities > 0
                || value.CallbackInvocations > 0
                || value.InvalidHistogramUpdates > 0);
    }

    private sealed record FrozenCapture(
        ModHealthCaptureOwner Owner,
        ModHealthCaptureOrigin Origin,
        ModHealthCompletionReason CompletionReason,
        ModPerformanceSnapshot Performance,
        ModHealthLedgerSnapshot Ledger,
        ImmutableArray<ModHealthMark> Marks,
        double SlowUpdateThresholdMilliseconds,
        Guid? ExportRequestId
    );
}
