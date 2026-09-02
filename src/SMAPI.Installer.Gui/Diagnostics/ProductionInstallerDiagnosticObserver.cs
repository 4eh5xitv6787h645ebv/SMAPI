using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Core.Transactions;
using StardewModdingAPI.Installer.Gui.Backend;
using StardewModdingAPI.Installer.Gui.Frontend;

namespace StardewModdingAPI.Installer.Gui.Diagnostics;

/// <summary>A narrow test seam over the GUI-owned diagnostic session.</summary>
internal interface IProductionInstallerDiagnosticSink
{
    void Record(InstallerDiagnosticCode code);
    void Record(InstallerDiagnosticCode code, ProtocolPrePlanErrorCode? error);
    void Record(InstallerDiagnosticCode code, ProtocolTerminalErrorCode? error);
    void RecordProgress(InstallerDiagnosticCode code, ReviewedReleasePreparationStage stage);
    void RecordProgress(InstallerDiagnosticCode code, TransactionStage stage);
}

/// <summary>
/// Projects controller-owned, bounded snapshots into fixed diagnostic events. This observer deliberately never
/// reads presentation prose, filesystem paths, release identifiers, digests, URLs, or exception details.
/// </summary>
internal sealed class ProductionInstallerDiagnosticObserver
{
    private readonly object Sync = new();
    private readonly IProductionInstallerDiagnosticSink Sink;
    private ReleaseCursor Release;
    private VersionedCursor<GameDiscoveryState> Game;
    private VersionedCursor<PlanReviewState> Plan;
    private ProgressCursor<ExecutionState> Execution;
    private ProgressCursor<RecoveryPruneControllerState> Prune;
    private bool executionResultObserved;
    private bool recoveryResultObserved;
    private bool pruneResultObserved;

    internal ProductionInstallerDiagnosticObserver(IProductionInstallerDiagnosticSink sink)
    {
        this.Sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    /// <summary>Observe one release snapshot without allowing diagnostics to affect controller publication.</summary>
    public void Observe(ReleaseVerificationSnapshot snapshot)
        => this.ObserveSafely(() => this.ObserveReleaseUnderLock(snapshot));

    /// <summary>Observe one game-discovery snapshot without allowing diagnostics to affect controller publication.</summary>
    public void Observe(GameDiscoverySnapshot snapshot)
        => this.ObserveSafely(() => this.ObserveGameUnderLock(snapshot));

    /// <summary>Observe one plan-review snapshot without allowing diagnostics to affect controller publication.</summary>
    public void Observe(PlanReviewSnapshot snapshot)
        => this.ObserveSafely(() => this.ObservePlanUnderLock(snapshot));

    /// <summary>Observe one execution snapshot without allowing diagnostics to affect controller publication.</summary>
    public void Observe(ExecutionSnapshot snapshot)
        => this.ObserveSafely(() => this.ObserveExecutionUnderLock(snapshot));

    /// <summary>Observe one recovery-prune snapshot without allowing diagnostics to affect controller publication.</summary>
    public void Observe(RecoveryPruneSnapshot snapshot)
        => this.ObserveSafely(() => this.ObservePruneUnderLock(snapshot));

    internal static InstallerDiagnosticCode? MapReleaseState(ReleaseVerificationState state) => state switch
    {
        ReleaseVerificationState.Idle => null,
        ReleaseVerificationState.LoadingCatalog => InstallerDiagnosticCode.ReleaseCatalogLoading,
        ReleaseVerificationState.NoCompatibleRelease => InstallerDiagnosticCode.ReleaseCatalogEmpty,
        ReleaseVerificationState.Ready => InstallerDiagnosticCode.ReleaseCatalogReady,
        ReleaseVerificationState.Handshaking => InstallerDiagnosticCode.ReleaseVerificationStarted,
        ReleaseVerificationState.Preparing => null,
        ReleaseVerificationState.OpeningPackage => InstallerDiagnosticCode.ReleaseOpening,
        ReleaseVerificationState.CleaningUp => null,
        ReleaseVerificationState.Verified => InstallerDiagnosticCode.ReleaseVerified,
        ReleaseVerificationState.Cancelled => InstallerDiagnosticCode.ReleaseCancelled,
        ReleaseVerificationState.Failed => InstallerDiagnosticCode.ReleaseFailed,
        ReleaseVerificationState.Disposed => null,
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    internal static InstallerDiagnosticCode MapReleaseProgressStage(ReviewedReleasePreparationStage stage) => stage switch
    {
        ReviewedReleasePreparationStage.ObservingTag => InstallerDiagnosticCode.ReleaseVerifying,
        ReviewedReleasePreparationStage.Downloading => InstallerDiagnosticCode.ReleaseDownloading,
        ReviewedReleasePreparationStage.RefreshingTag => InstallerDiagnosticCode.ReleaseVerifying,
        ReviewedReleasePreparationStage.ImportingLocalPackage => InstallerDiagnosticCode.ReleaseVerifying,
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };

    internal static InstallerDiagnosticCode? MapGameState(GameDiscoveryState state) => state switch
    {
        GameDiscoveryState.Idle => null,
        GameDiscoveryState.Discovering => InstallerDiagnosticCode.GameDiscoveryStarted,
        GameDiscoveryState.Ready => InstallerDiagnosticCode.GameDiscoveryReady,
        GameDiscoveryState.NoCandidates => InstallerDiagnosticCode.GameDiscoveryEmpty,
        GameDiscoveryState.ValidatingManual => InstallerDiagnosticCode.GameManualValidating,
        GameDiscoveryState.ManualInvalid => InstallerDiagnosticCode.GameManualInvalid,
        GameDiscoveryState.ManualValid => InstallerDiagnosticCode.GameManualValid,
        GameDiscoveryState.Cancelling => null,
        GameDiscoveryState.Cancelled => InstallerDiagnosticCode.GameDiscoveryCancelled,
        GameDiscoveryState.Failed => InstallerDiagnosticCode.GameDiscoveryFailed,
        GameDiscoveryState.SessionFaulted => InstallerDiagnosticCode.GameDiscoveryFailed,
        GameDiscoveryState.Transferred => null,
        GameDiscoveryState.Disposed => null,
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    internal static InstallerDiagnosticCode? MapPlanState(PlanReviewState state) => state switch
    {
        PlanReviewState.Choosing => InstallerDiagnosticCode.PlanChoosing,
        PlanReviewState.SelectionChanged => InstallerDiagnosticCode.PlanChoosing,
        PlanReviewState.Inspecting => InstallerDiagnosticCode.PlanInspecting,
        PlanReviewState.Approving => InstallerDiagnosticCode.PlanInspecting,
        PlanReviewState.Confirming => null,
        PlanReviewState.HandoffReady => InstallerDiagnosticCode.PlanConfirmed,
        PlanReviewState.HandedOff => null,
        PlanReviewState.Closing => null,
        PlanReviewState.Available => InstallerDiagnosticCode.PlanReady,
        PlanReviewState.Rejected => InstallerDiagnosticCode.PlanRejected,
        PlanReviewState.Cancelling => null,
        PlanReviewState.Cancelled => null,
        PlanReviewState.Failed => InstallerDiagnosticCode.PlanFailed,
        PlanReviewState.SessionFaulted => InstallerDiagnosticCode.PlanFailed,
        PlanReviewState.Disposed => null,
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    internal static InstallerDiagnosticCode? MapExecutionState(ExecutionState state) => state switch
    {
        ExecutionState.Ready => InstallerDiagnosticCode.ExecutionReady,
        ExecutionState.Starting => InstallerDiagnosticCode.ExecutionStarting,
        ExecutionState.Running => null,
        ExecutionState.CancellationRequested => InstallerDiagnosticCode.ExecutionCancellationRequested,
        ExecutionState.CancelledBeforeStart => null,
        ExecutionState.Terminal => null,
        ExecutionState.RecoveryRequired => InstallerDiagnosticCode.ExecutionRecoveryRequired,
        ExecutionState.RecoveryStarting => InstallerDiagnosticCode.ExecutionRecoveryStarting,
        ExecutionState.RecoveryCancellationRequested => InstallerDiagnosticCode.ExecutionCancellationRequested,
        ExecutionState.RecoveryRunning => null,
        ExecutionState.RecoveryCompleted => null,
        ExecutionState.PrestartFault => InstallerDiagnosticCode.ExecutionFailed,
        ExecutionState.Disposing => null,
        ExecutionState.Disposed => null,
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    internal static InstallerDiagnosticCode? MapPruneState(RecoveryPruneControllerState state) => state switch
    {
        RecoveryPruneControllerState.NotLoaded => null,
        RecoveryPruneControllerState.Listing => InstallerDiagnosticCode.RecoveryPruneLoading,
        RecoveryPruneControllerState.CatalogReady => InstallerDiagnosticCode.RecoveryPruneReady,
        RecoveryPruneControllerState.NoHistory => null,
        RecoveryPruneControllerState.Inspecting => InstallerDiagnosticCode.RecoveryPruneInspecting,
        RecoveryPruneControllerState.RelistRequired => InstallerDiagnosticCode.RecoveryPruneFailed,
        RecoveryPruneControllerState.ReviewReady => InstallerDiagnosticCode.RecoveryPrunePlanReady,
        RecoveryPruneControllerState.Confirming => null,
        RecoveryPruneControllerState.ReadyToRun => InstallerDiagnosticCode.RecoveryPruneConfirmed,
        RecoveryPruneControllerState.Starting => InstallerDiagnosticCode.RecoveryPruneStarting,
        RecoveryPruneControllerState.Running => null,
        RecoveryPruneControllerState.CancellationRequested => InstallerDiagnosticCode.RecoveryPruneCancellationRequested,
        RecoveryPruneControllerState.Cancelled => null,
        RecoveryPruneControllerState.CancelledBeforeStart => null,
        RecoveryPruneControllerState.Terminal => null,
        RecoveryPruneControllerState.StateUnknown => InstallerDiagnosticCode.RecoveryPruneFailed,
        RecoveryPruneControllerState.Failed => InstallerDiagnosticCode.RecoveryPruneFailed,
        RecoveryPruneControllerState.SessionFaulted => InstallerDiagnosticCode.RecoveryPruneFailed,
        RecoveryPruneControllerState.Disposing => null,
        RecoveryPruneControllerState.Disposed => null,
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    internal static InstallerDiagnosticCode MapExecutionProgressStage(TransactionStage stage, bool recovery)
    {
        _ = stage switch
        {
            TransactionStage.AcquiringLock => true,
            TransactionStage.Recovering => true,
            TransactionStage.Staging => true,
            TransactionStage.Revalidating => true,
            TransactionStage.Applying => true,
            TransactionStage.Verifying => true,
            TransactionStage.Committing => true,
            TransactionStage.RollingBack => true,
            TransactionStage.Completed => true,
            TransactionStage.Inspecting => true,
            TransactionStage.VerifyingRecovery => true,
            TransactionStage.PreparingRecovery => true,
            TransactionStage.PreparingPayload => true,
            TransactionStage.WritingFiles => true,
            TransactionStage.RemovingFiles => true,
            TransactionStage.UpdatingLauncher => true,
            TransactionStage.UpdatingInstallerState => true,
            TransactionStage.PublishingRecovery => true,
            TransactionStage.CleaningRecovery => true,
            _ => throw new ArgumentOutOfRangeException(nameof(stage))
        };
        return recovery ? InstallerDiagnosticCode.ExecutionRecoveryProgress : InstallerDiagnosticCode.ExecutionProgress;
    }

    internal static InstallerDiagnosticCode MapPruneProgressStage(TransactionStage stage)
    {
        _ = MapExecutionProgressStage(stage, recovery: false);
        return InstallerDiagnosticCode.RecoveryPruneProgress;
    }

    private void ObserveSafely(Action observe)
    {
        try
        {
            lock (this.Sync)
                observe();
        }
        catch
        {
            // Diagnostics are ancillary after mutation admission and must never disrupt a controller callback.
        }
    }

    private void ObserveReleaseUnderLock(ReleaseVerificationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateReleaseError(snapshot.Error);
        ReviewedReleasePreparationStage? stage = snapshot.Progress?.Stage;
        if (stage is { } definedStage)
            _ = MapReleaseProgressStage(definedStage);
        if (!this.Release.TryAdvance(snapshot.Generation, snapshot.State, stage, out bool stateChanged, out bool stageChanged))
            return;

        if (stateChanged && MapReleaseState(snapshot.State) is { } stateCode)
        {
            if (snapshot.State == ReleaseVerificationState.Failed && snapshot.RejectionCode is { } rejection)
                this.Sink.Record(stateCode, rejection);
            else
                this.Sink.Record(stateCode);
        }
        if (stageChanged && stage is { } progressStage)
            this.Sink.RecordProgress(MapReleaseProgressStage(progressStage), progressStage);
    }

    private void ObserveGameUnderLock(GameDiscoverySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!this.Game.TryAdvance(snapshot.Generation, snapshot.Revision, snapshot.State, out bool stateChanged) || !stateChanged)
            return;
        if (MapGameState(snapshot.State) is { } code)
            this.Sink.Record(code);
    }

    private void ObservePlanUnderLock(PlanReviewSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!this.Plan.TryAdvance(snapshot.Generation, snapshot.Revision, snapshot.State, out bool stateChanged) || !stateChanged)
            return;
        if (MapPlanState(snapshot.State) is not { } code)
            return;
        if (snapshot.State == PlanReviewState.Rejected && snapshot.Result is PlanReviewRejection rejection)
            this.Sink.Record(code, rejection.ErrorCode);
        else if (snapshot.State == PlanReviewState.Rejected)
            this.Sink.Record(InstallerDiagnosticCode.PlanFailed);
        else
            this.Sink.Record(code);
    }

    private void ObserveExecutionUnderLock(ExecutionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.ProgressStage is { } progressStage)
            _ = MapExecutionProgressStage(progressStage, snapshot.State == ExecutionState.RecoveryRunning);
        if (!this.Execution.TryAdvance(snapshot.Revision, snapshot.State, snapshot.ProgressStage, out bool stateChanged, out bool stageChanged))
            return;

        if (!this.executionResultObserved && snapshot.ExecutionResult is { } executionResult)
        {
            this.executionResultObserved = true;
            if (executionResult is InstallerExecutionTerminalResult terminal)
                this.Sink.Record(InstallerDiagnosticCode.ExecutionTerminal, terminal.ErrorCode);
            else if (executionResult is InstallerExecutionStateUnknownResult)
                this.Sink.Record(InstallerDiagnosticCode.ExecutionFailed);
            else
                throw new ArgumentOutOfRangeException(nameof(snapshot));
        }
        if (!this.recoveryResultObserved && snapshot.RecoveryResult is { } recoveryResult)
        {
            this.recoveryResultObserved = true;
            if (recoveryResult is InstallerRecoveryTerminalResult terminal)
                this.Sink.Record(InstallerDiagnosticCode.ExecutionRecoveryTerminal, terminal.ErrorCode);
            else if (recoveryResult is InstallerRecoveryStateUnknownResult)
                this.Sink.Record(InstallerDiagnosticCode.ExecutionFailed);
            else
                throw new ArgumentOutOfRangeException(nameof(snapshot));
        }

        if (stateChanged && MapExecutionState(snapshot.State) is { } stateCode)
            this.Sink.Record(stateCode);
        if (stageChanged && snapshot.ProgressStage is { } stage)
        {
            if (snapshot.State == ExecutionState.Running)
                this.Sink.RecordProgress(MapExecutionProgressStage(stage, recovery: false), stage);
            else if (snapshot.State == ExecutionState.RecoveryRunning)
                this.Sink.RecordProgress(MapExecutionProgressStage(stage, recovery: true), stage);
        }
        if (stateChanged && snapshot.State == ExecutionState.Terminal && snapshot.ExecutionResult is null)
            this.Sink.Record(InstallerDiagnosticCode.ExecutionFailed);
        if (stateChanged && snapshot.State == ExecutionState.RecoveryCompleted && snapshot.RecoveryResult is null)
            this.Sink.Record(InstallerDiagnosticCode.ExecutionFailed);
    }

    private void ObservePruneUnderLock(RecoveryPruneSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.ProgressStage is { } progressStage)
            _ = MapPruneProgressStage(progressStage);
        if (!this.Prune.TryAdvance(snapshot.Generation, snapshot.Revision, snapshot.State, snapshot.ProgressStage, out bool stateChanged, out bool stageChanged))
            return;

        if (!this.pruneResultObserved && snapshot.Result is { } result)
        {
            this.pruneResultObserved = true;
            if (result is RecoveryPruneTerminalPresentation terminal)
                this.Sink.Record(InstallerDiagnosticCode.RecoveryPruneTerminal, terminal.ErrorCode);
            else if (result is RecoveryPruneStateUnknownPresentation)
                this.Sink.Record(InstallerDiagnosticCode.RecoveryPruneFailed);
            else
                throw new ArgumentOutOfRangeException(nameof(snapshot));
        }

        if (stateChanged && MapPruneState(snapshot.State) is { } stateCode)
        {
            if (snapshot.State is RecoveryPruneControllerState.RelistRequired or RecoveryPruneControllerState.Failed
                && snapshot.Rejection is { } rejection)
            {
                this.Sink.Record(stateCode, rejection.ErrorCode);
            }
            else
                this.Sink.Record(stateCode);
        }
        if (stageChanged && snapshot.State == RecoveryPruneControllerState.Running && snapshot.ProgressStage is { } stage)
            this.Sink.RecordProgress(MapPruneProgressStage(stage), stage);
        if (stateChanged && snapshot.State == RecoveryPruneControllerState.Terminal && snapshot.Result is null)
            this.Sink.Record(InstallerDiagnosticCode.RecoveryPruneFailed);
    }

    internal static void ValidateReleaseError(ReleaseVerificationError error)
    {
        _ = error switch
        {
            ReleaseVerificationError.None => true,
            ReleaseVerificationError.CatalogUnavailable => true,
            ReleaseVerificationError.PreparationFailed => true,
            ReleaseVerificationError.PackageRejected => true,
            ReleaseVerificationError.BackendUnavailable => true,
            ReleaseVerificationError.SessionFaulted => true,
            ReleaseVerificationError.RetryLimitReached => true,
            ReleaseVerificationError.CleanupFailed => true,
            _ => throw new ArgumentOutOfRangeException(nameof(error))
        };
    }

    private struct ReleaseCursor
    {
        private long Generation;
        private ReleaseVerificationState State;
        private ReviewedReleasePreparationStage? Stage;
        private bool Initialized;

        public bool TryAdvance(long generation, ReleaseVerificationState state, ReviewedReleasePreparationStage? stage, out bool stateChanged, out bool stageChanged)
        {
            if (this.Initialized && generation < this.Generation)
            {
                stateChanged = false;
                stageChanged = false;
                return false;
            }
            stateChanged = !this.Initialized || generation != this.Generation || state != this.State;
            stageChanged = stage is not null && (!this.Initialized || generation != this.Generation || stage != this.Stage);
            if (!stateChanged && !stageChanged)
                return false;
            this.Generation = generation;
            this.State = state;
            this.Stage = stage;
            this.Initialized = true;
            return true;
        }
    }

    private struct VersionedCursor<TState> where TState : struct, Enum
    {
        private long Generation;
        private long Revision;
        private TState State;
        private bool Initialized;

        public bool TryAdvance(long generation, long revision, TState state, out bool stateChanged)
        {
            if (this.Initialized && (generation < this.Generation || generation == this.Generation && revision <= this.Revision))
            {
                stateChanged = false;
                return false;
            }
            stateChanged = !this.Initialized || generation != this.Generation || !EqualityComparer<TState>.Default.Equals(state, this.State);
            this.Generation = generation;
            this.Revision = revision;
            this.State = state;
            this.Initialized = true;
            return true;
        }
    }

    private struct ProgressCursor<TState> where TState : struct, Enum
    {
        private long Generation;
        private long Revision;
        private TState State;
        private TransactionStage? Stage;
        private bool Initialized;

        public bool TryAdvance(long revision, TState state, TransactionStage? stage, out bool stateChanged, out bool stageChanged)
            => this.TryAdvance(0, revision, state, stage, out stateChanged, out stageChanged);

        public bool TryAdvance(long generation, long revision, TState state, TransactionStage? stage, out bool stateChanged, out bool stageChanged)
        {
            if (this.Initialized && (generation < this.Generation || generation == this.Generation && revision <= this.Revision))
            {
                stateChanged = false;
                stageChanged = false;
                return false;
            }
            stateChanged = !this.Initialized || generation != this.Generation || !EqualityComparer<TState>.Default.Equals(state, this.State);
            stageChanged = stage is not null && (!this.Initialized || generation != this.Generation || stage != this.Stage);
            this.Generation = generation;
            this.Revision = revision;
            this.State = state;
            this.Stage = stage;
            this.Initialized = true;
            return true;
        }
    }
}
