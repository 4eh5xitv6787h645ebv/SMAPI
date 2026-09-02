using System.Diagnostics;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Core.Transactions;
using StardewModdingAPI.Installer.Gui.Backend;

namespace StardewModdingAPI.Installer.Gui.Frontend;

internal enum RecoveryPruneControllerState
{
    NotLoaded,
    Listing,
    CatalogReady,
    NoHistory,
    Inspecting,
    RelistRequired,
    ReviewReady,
    Confirming,
    ReadyToRun,
    Starting,
    Running,
    CancellationRequested,
    Cancelled,
    CancelledBeforeStart,
    Terminal,
    StateUnknown,
    Failed,
    SessionFaulted,
    Disposing,
    Disposed
}

internal enum RecoveryPruneConsent
{
    Cancel = 0,
    ConfirmDestructiveCleanup = 1
}

internal sealed record RecoveryPruneRelease(string Tag, string EmbeddedVersion);
internal sealed record RecoveryPruneGame(string DisplayName, string DisplayPath);

internal sealed class RecoveryPruneChoice
{
    public int Ordinal { get; }
    public bool IsCurrent { get; }
    public bool IsUserCheckpoint { get; }
    public InstallerOperation OriginOperation { get; }
    public RecoveryPruneRestoreTarget RestoreTarget { get; }

    internal RecoveryPruneChoice(
        int ordinal,
        bool isCurrent,
        bool isUserCheckpoint,
        InstallerOperation originOperation,
        RecoveryPruneRestoreTarget restoreTarget
    )
    {
        this.Ordinal = ordinal;
        this.IsCurrent = isCurrent;
        this.IsUserCheckpoint = isUserCheckpoint;
        this.OriginOperation = originOperation;
        this.RestoreTarget = restoreTarget ?? throw new ArgumentNullException(nameof(restoreTarget));
    }
}

internal abstract record RecoveryPruneRestoreTarget;
internal sealed record RecoveryPruneReleaseTarget(string Tag, string EmbeddedVersion) : RecoveryPruneRestoreTarget;
internal sealed record RecoveryPruneUninstalledTarget : RecoveryPruneRestoreTarget;

internal sealed record RecoveryPrunePlanPresentation(
    int RetainNewest,
    int RetainedCount,
    int RemovedCount,
    int CleanupGenerationCount,
    bool AuxiliaryCleanupPlanned,
    int WarningCount,
    IReadOnlyList<ProtocolPlanRisk> Risks,
    ProtocolRecommendedDefault RecommendedDefault,
    bool RequiresConfirmation
);

internal sealed record RecoveryPruneRejection(
    ProtocolPrePlanErrorCode ErrorCode,
    ProtocolNextAction NextAction,
    bool IsTerminal
);

internal abstract record RecoveryPruneResultPresentation;

internal sealed record RecoveryPruneTerminalPresentation(
    ProtocolPruneOutcome Outcome,
    ProtocolDurableState DurableState,
    ProtocolTerminalErrorCode? ErrorCode,
    ProtocolRecoveryDisposition RecoveryDisposition,
    ProtocolNextAction NextAction,
    int? LogicallyRemovedGenerationCount,
    int? PhysicallyCleanedGenerationCount,
    int? PendingCleanupGenerationCount,
    bool? AuxiliaryCleanupPending,
    InstallerBackendSettlement BackendSettlement
) : RecoveryPruneResultPresentation;

internal sealed record RecoveryPruneStateUnknownPresentation : RecoveryPruneResultPresentation;

internal sealed record RecoveryPruneSnapshot(
    long Generation,
    long Revision,
    RecoveryPruneControllerState State,
    RecoveryPruneRelease Release,
    RecoveryPruneGame Game,
    IReadOnlyList<RecoveryPruneChoice> Choices,
    RecoveryPruneChoice? Selected,
    RecoveryPrunePlanPresentation? Plan,
    RecoveryPruneRejection? Rejection,
    TransactionStage? ProgressStage,
    int CompletedUnits,
    int? TotalUnits,
    RecoveryPruneResultPresentation? Result,
    bool CanList,
    bool CanSelect,
    bool CanInspect,
    bool CanConfirm,
    bool CanRun,
    bool CanCancel,
    bool CanExit
);

/// <summary>
/// Exclusively owns one game-bound session through recovery listing, destructive-plan review, explicit confirmation,
/// and explicit one-shot execution. Every published value is bounded and sanitized; backend authority and private
/// transport data stay below this boundary.
/// </summary>
internal sealed class RecoveryPruneController : IAsyncDisposable
{
    private const int MaximumProgressEvents = 256;
    private static readonly TimeSpan ProgressPublishInterval = TimeSpan.FromMilliseconds(75);

    private readonly object Sync = new();
    private readonly IPlanInspectionSession Session;
    private readonly CancellationTokenSource Lifetime = new();
    private readonly Task<InstallerProtocolClientException> SessionFaultNotification;
    private readonly TaskCompletionSource StopWatching = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task SessionWatcher;
    private readonly RecoveryPruneRelease ReleaseValue;
    private readonly RecoveryPruneGame GameValue;
    private Dictionary<RecoveryPruneChoice, BoundInstallerRecoveryPoint> CurrentChoices = new(ReferenceEqualityComparer.Instance);
    private RecoveryPruneChoice[] ChoicePresentations = [];
    private RecoveryPruneChoice? SelectedValue;
    private BoundInstallerRecoveryPruneConfirmation? CurrentConfirmation;
    private IConfirmedRecoveryPruneSession? ConfirmedSession;
    private InstallerRecoveryPruneOperation? Execution;
    private RecoveryPrunePlanPresentation? PlanValue;
    private RecoveryPruneRejection? RejectionValue;
    private RecoveryPruneResultPresentation? ResultValue;
    private TransactionStage? ProgressStageValue;
    private int CompletedUnitsValue;
    private int? TotalUnitsValue;
    private RecoveryPruneControllerState StateValue = RecoveryPruneControllerState.NotLoaded;
    private ActiveRequest? Request;
    private Task? ActiveTask;
    private CancellationTokenSource? ExecutionStartCancellation;
    private Task? ExecutionStartCancellationTask;
    private Task? ExecutionCancellationTask;
    private Task? SessionCleanupTask;
    private Task? DisposalTask;
    private long GenerationValue;
    private long RevisionValue;
    private bool DisposeStarted;
    private bool CancellationWasRequested;

    public RecoveryPruneController(IPlanInspectionSession session)
    {
        this.Session = session ?? throw new ArgumentNullException(nameof(session));
        this.ReleaseValue = ProjectRelease(session.Release);
        this.GameValue = ProjectGame(session.Game);
        this.SessionFaultNotification = session.SessionFaulted
            ?? throw new InvalidOperationException("The recovery-cleanup session had no fault notification.");
        this.SessionWatcher = this.WatchSessionAsync();
    }

    public event EventHandler? Changed;

    public RecoveryPruneSnapshot Snapshot
    {
        get
        {
            lock (this.Sync)
                return this.CreateSnapshotUnderLock();
        }
    }

    public Task ListRecoveriesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task task;
        lock (this.Sync)
        {
            this.AssertUsableUnderLock();
            if (!this.CanListUnderLock() || this.ActiveTask is not null)
                throw new InvalidOperationException("Recovery history cannot be listed in the current state.");
            this.GenerationValue++;
            ActiveRequest request = new(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.Lifetime.Token));
            this.Request = request;
            this.RevokeCatalogUnderLock();
            this.RevokePlanUnderLock();
            this.RejectionValue = null;
            this.ResultValue = null;
            this.StateValue = RecoveryPruneControllerState.Listing;
            this.ActiveTask = task = this.RunListAsync(request);
        }
        this.PublishChanged();
        return task;
    }

    public void SelectRecoveryPoint(RecoveryPruneChoice? point)
    {
        lock (this.Sync)
        {
            this.AssertUsableUnderLock();
            if (this.StateValue != RecoveryPruneControllerState.CatalogReady || this.ActiveTask is not null)
                throw new InvalidOperationException("A current recovery catalog is required before selecting a retention boundary.");
            if (point is not null && !this.CurrentChoices.ContainsKey(point))
                throw new ArgumentException("The recovery point must be an exact current choice issued by this controller.", nameof(point));
            if (ReferenceEquals(this.SelectedValue, point))
                return;
            this.SelectedValue = point;
            this.RevokePlanUnderLock();
        }
        this.PublishChanged();
    }

    public Task InspectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task task;
        lock (this.Sync)
        {
            this.AssertUsableUnderLock();
            if (
                this.StateValue != RecoveryPruneControllerState.CatalogReady
                || this.SelectedValue is not { } selected
                || !this.CurrentChoices.TryGetValue(selected, out BoundInstallerRecoveryPoint? backendPoint)
                || this.ActiveTask is not null
            )
            {
                throw new InvalidOperationException("An exact current recovery point must be selected before cleanup inspection.");
            }
            int catalogCount = this.CurrentChoices.Count;
            this.GenerationValue++;
            ActiveRequest request = new(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.Lifetime.Token));
            this.Request = request;
            this.CurrentChoices.Clear();
            this.RevokePlanUnderLock();
            this.RejectionValue = null;
            this.StateValue = RecoveryPruneControllerState.Inspecting;
            this.ActiveTask = task = this.RunInspectAsync(request, backendPoint, selected.Ordinal, catalogCount);
        }
        this.PublishChanged();
        return task;
    }

    public Task ConfirmAsync(RecoveryPruneConsent consent, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(consent))
            throw new ArgumentOutOfRangeException(nameof(consent));
        cancellationToken.ThrowIfCancellationRequested();
        Task task;
        lock (this.Sync)
        {
            this.AssertUsableUnderLock();
            if (this.StateValue != RecoveryPruneControllerState.ReviewReady || this.ActiveTask is not null || this.CurrentConfirmation is null)
                throw new InvalidOperationException("A current reviewed recovery-cleanup plan is required before confirmation.");
            if (consent == RecoveryPruneConsent.Cancel)
                return Task.CompletedTask;

            BoundInstallerRecoveryPruneConfirmation confirmation = this.CurrentConfirmation;
            this.CurrentConfirmation = null;
            this.GenerationValue++;
            ActiveRequest request = new(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.Lifetime.Token));
            this.Request = request;
            this.StateValue = RecoveryPruneControllerState.Confirming;
            this.ActiveTask = task = this.RunConfirmAsync(request, confirmation);
        }
        this.PublishChanged();
        return task;
    }

    /// <summary>Execute only after a separate explicit destructive confirmation. This is the sole execution entry.</summary>
    public Task RunAsync()
    {
        Task task;
        lock (this.Sync)
        {
            this.AssertUsableUnderLock();
            if (this.StateValue != RecoveryPruneControllerState.ReadyToRun || this.ConfirmedSession is null || this.ActiveTask is not null)
                throw new InvalidOperationException("The confirmed recovery-cleanup plan can only be run once from the ready state.");
            this.CancellationWasRequested = false;
            this.ExecutionStartCancellationTask = null;
            this.ExecutionCancellationTask = null;
            this.GenerationValue++;
            this.ExecutionStartCancellation = CancellationTokenSource.CreateLinkedTokenSource(this.Lifetime.Token);
            this.StateValue = RecoveryPruneControllerState.Starting;
            this.ActiveTask = task = this.RunExecutionAsync();
        }
        this.PublishChanged();
        return task;
    }

    public Task RequestCancellationAsync()
    {
        Task result;
        CancellationTokenSource? source = null;
        InstallerRecoveryPruneOperation? execution = null;
        TaskCompletionSource? settlement = null;
        bool executionLane = false;
        lock (this.Sync)
        {
            this.AssertUsableUnderLock();
            if (this.StateValue is RecoveryPruneControllerState.Listing or RecoveryPruneControllerState.Inspecting or RecoveryPruneControllerState.Confirming)
            {
                ActiveRequest request = this.Request!;
                request.CancellationWasRequested = true;
                result = request.CancellationTask ??= ReserveCancellation(out settlement);
                if (settlement is not null)
                    source = request.Cancellation;
            }
            else if (this.StateValue == RecoveryPruneControllerState.Starting)
            {
                executionLane = true;
                result = this.ExecutionStartCancellationTask ??= ReserveCancellation(out settlement);
                if (settlement is not null)
                    source = this.ExecutionStartCancellation!;
            }
            else if (this.StateValue == RecoveryPruneControllerState.Running)
            {
                executionLane = true;
                result = this.ExecutionCancellationTask ??= ReserveCancellation(out settlement);
                if (settlement is not null)
                    execution = this.Execution!;
            }
            else if (this.StateValue == RecoveryPruneControllerState.CancellationRequested)
                return this.Execution is null
                    ? this.ExecutionStartCancellationTask ?? this.Request?.CancellationTask ?? Task.CompletedTask
                    : this.ExecutionCancellationTask ?? Task.CompletedTask;
            else
                return Task.CompletedTask;

            if (executionLane)
                this.CancellationWasRequested = true;
            this.StateValue = RecoveryPruneControllerState.CancellationRequested;
        }
        this.PublishChanged();
        if (source is not null)
            StartSourceCancellation(source, settlement!);
        else if (execution is not null)
            StartExecutionCancellation(execution, settlement!);
        return result;
    }

    public ValueTask DisposeAsync()
    {
        lock (this.Sync)
        {
            if (this.DisposalTask is not null)
                return new ValueTask(this.DisposalTask);
            if (this.StateValue == RecoveryPruneControllerState.Disposed)
                return ValueTask.CompletedTask;
            this.DisposeStarted = true;
            if (this.StateValue is RecoveryPruneControllerState.Starting or RecoveryPruneControllerState.Running or RecoveryPruneControllerState.CancellationRequested)
                this.CancellationWasRequested = true;
            this.StateValue = RecoveryPruneControllerState.Disposing;
            this.CurrentChoices.Clear();
            this.CurrentConfirmation = null;
            return new ValueTask(this.DisposalTask = this.DisposeCoreAsync());
        }
    }

    private async Task RunListAsync(ActiveRequest request)
    {
        await Task.Yield();
        BoundInstallerRecoveryCatalogResult? backend = null;
        Exception? failure = null;
        try { backend = await this.Session.ListRecoveriesAsync(request.Cancellation.Token).ConfigureAwait(false); }
        catch (Exception error) { failure = error; }

        Dictionary<RecoveryPruneChoice, BoundInstallerRecoveryPoint>? authorities = null;
        RecoveryPruneChoice[] choices = [];
        RecoveryPruneRejection? rejection = null;
        RecoveryPruneControllerState next = RecoveryPruneControllerState.Failed;
        if (failure is null)
        {
            try
            {
                switch (backend)
                {
                    case BoundInstallerRecoveryCatalogSuccess success:
                        (choices, authorities) = ProjectCatalog(success);
                        next = RecoveryPruneControllerState.CatalogReady;
                        break;
                    case BoundInstallerNoRecoveryHistory:
                        next = RecoveryPruneControllerState.NoHistory;
                        break;
                    case BoundInstallerRecoveryCatalogRejection value:
                        rejection = ProjectCatalogRejection(value);
                        next = value.IsTerminal ? RecoveryPruneControllerState.Failed : RecoveryPruneControllerState.RelistRequired;
                        break;
                    default:
                        throw new InvalidOperationException("The recovery catalog projection was invalid.");
                }
            }
            catch (Exception error) { failure = error; }
        }

        bool cleanup = false;
        lock (this.Sync)
        {
            if (!ReferenceEquals(this.Request, request) || this.DisposeStarted)
                cleanup = true;
            else
            {
                bool cancelled = request.CancellationWasRequested || request.Cancellation.IsCancellationRequested;
                request.Dispose();
                this.Request = null;
                this.ActiveTask = null;
                if (this.SessionFaultNotification.IsCompleted)
                {
                    this.StateValue = RecoveryPruneControllerState.SessionFaulted;
                    cleanup = true;
                }
                else if (cancelled)
                {
                    this.StateValue = RecoveryPruneControllerState.Cancelled;
                    cleanup = true;
                }
                else if (failure is not null)
                {
                    this.StateValue = RecoveryPruneControllerState.Failed;
                    cleanup = true;
                }
                else
                {
                    this.StateValue = next;
                    this.RejectionValue = rejection;
                    this.ChoicePresentations = choices;
                    this.CurrentChoices = authorities ?? new(ReferenceEqualityComparer.Instance);
                    cleanup = rejection?.IsTerminal == true;
                }
            }
        }
        this.PublishChanged();
        if (cleanup)
            await this.SettleOwnedSessionAsync().ConfigureAwait(false);
    }

    private async Task RunInspectAsync(ActiveRequest request, BoundInstallerRecoveryPoint point, int selectedOrdinal, int catalogCount)
    {
        await Task.Yield();
        BoundInstallerRecoveryPrunePlanResult? backend = null;
        Exception? failure = null;
        try { backend = await this.Session.InspectRecoveryPruneAsync(point, request.Cancellation.Token).ConfigureAwait(false); }
        catch (Exception error) { failure = error; }

        RecoveryPrunePlanPresentation? plan = null;
        RecoveryPruneRejection? rejection = null;
        BoundInstallerRecoveryPruneConfirmation? confirmation = null;
        RecoveryPruneControllerState next = RecoveryPruneControllerState.Failed;
        if (failure is null)
        {
            try
            {
                switch (backend)
                {
                    case BoundInstallerRecoveryPrunePlanSuccess success:
                        (plan, confirmation) = ProjectPlan(success, selectedOrdinal, catalogCount);
                        next = RecoveryPruneControllerState.ReviewReady;
                        break;
                    case BoundInstallerRecoveryPrunePlanRejection value:
                        rejection = ProjectPruneRejection(value, selectedOrdinal, catalogCount);
                        next = value.IsTerminal ? RecoveryPruneControllerState.Failed : RecoveryPruneControllerState.RelistRequired;
                        break;
                    default:
                        throw new InvalidOperationException("The recovery-cleanup plan projection was invalid.");
                }
            }
            catch (Exception error) { failure = error; }
        }

        bool cleanup = false;
        lock (this.Sync)
        {
            if (!ReferenceEquals(this.Request, request) || this.DisposeStarted)
                cleanup = true;
            else
            {
                bool cancelled = request.CancellationWasRequested || request.Cancellation.IsCancellationRequested;
                request.Dispose();
                this.Request = null;
                this.ActiveTask = null;
                if (this.SessionFaultNotification.IsCompleted)
                {
                    this.StateValue = RecoveryPruneControllerState.SessionFaulted;
                    cleanup = true;
                }
                else if (cancelled)
                {
                    this.StateValue = RecoveryPruneControllerState.Cancelled;
                    cleanup = true;
                }
                else if (failure is not null)
                {
                    this.StateValue = RecoveryPruneControllerState.Failed;
                    cleanup = true;
                }
                else
                {
                    this.StateValue = next;
                    this.PlanValue = plan;
                    this.RejectionValue = rejection;
                    this.CurrentConfirmation = confirmation;
                    if (next == RecoveryPruneControllerState.RelistRequired)
                    {
                        this.ChoicePresentations = [];
                        this.SelectedValue = null;
                    }
                    cleanup = rejection?.IsTerminal == true;
                }
            }
        }
        this.PublishChanged();
        if (cleanup)
            await this.SettleOwnedSessionAsync().ConfigureAwait(false);
    }

    private async Task RunConfirmAsync(ActiveRequest request, BoundInstallerRecoveryPruneConfirmation confirmation)
    {
        await Task.Yield();
        IConfirmedRecoveryPruneSession? confirmed = null;
        Exception? failure = null;
        bool exactConfirmed = false;
        try { confirmed = await this.Session.ConfirmRecoveryPruneAsync(confirmation, request.Cancellation.Token).ConfigureAwait(false); }
        catch (Exception error) { failure = error; }

        if (failure is null)
        {
            try
            {
                ValidateConfirmedOwner(confirmed, this.Session, this.SessionFaultNotification);
                exactConfirmed = true;
            }
            catch (Exception error) { failure = error; }
        }

        bool accepted = false;
        lock (this.Sync)
        {
            if (ReferenceEquals(this.Request, request) && !this.DisposeStarted)
            {
                bool cancelled = request.CancellationWasRequested || request.Cancellation.IsCancellationRequested;
                request.Dispose();
                this.Request = null;
                if (this.SessionFaultNotification.IsCompleted)
                    this.StateValue = RecoveryPruneControllerState.SessionFaulted;
                else if (cancelled)
                    this.StateValue = RecoveryPruneControllerState.Cancelled;
                else if (failure is not null)
                    this.StateValue = RecoveryPruneControllerState.Failed;
                else
                {
                    this.ConfirmedSession = confirmed;
                    this.StateValue = RecoveryPruneControllerState.ReadyToRun;
                    this.ActiveTask = null;
                    accepted = true;
                }
            }
        }
        if (accepted)
        {
            this.PublishChanged();
            return;
        }

        if (confirmed is not null)
        {
            if (exactConfirmed)
                await this.SettleRejectedConfirmedOwnerAsync(confirmed).ConfigureAwait(false);
            else
            {
                await DisposeSafelyAsync(confirmed).ConfigureAwait(false);
                await this.SettleOwnedSessionAsync().ConfigureAwait(false);
            }
        }
        else
            await this.SettleOwnedSessionAsync().ConfigureAwait(false);

        lock (this.Sync)
        {
            if (ReferenceEquals(this.Request, request))
            {
                request.Dispose();
                this.Request = null;
            }
            this.ActiveTask = null;
        }
        this.PublishChanged();
    }

    private async Task RunExecutionAsync()
    {
        await Task.Yield();
        InstallerRecoveryPruneOperation? operation = null;
        RecoveryPruneResultPresentation result = new RecoveryPruneStateUnknownPresentation();
        bool cancelledBeforeStart = false;
        try
        {
            IConfirmedRecoveryPruneSession confirmed;
            CancellationToken startToken;
            lock (this.Sync)
            {
                confirmed = this.ConfirmedSession!;
                startToken = this.ExecutionStartCancellation!.Token;
            }
            operation = await confirmed.ExecuteAsync(startToken).ConfigureAwait(false);
            if (operation is null || operation.Progress is null || operation.Completion is null)
                throw new InvalidOperationException("The confirmed recovery-cleanup operation was unavailable.");

            TaskCompletionSource? cancellationSettlement = null;
            bool cancelAfterPublication;
            lock (this.Sync)
            {
                this.Execution = operation;
                cancelAfterPublication = this.CancellationWasRequested || this.DisposeStarted;
                if (cancelAfterPublication && this.ExecutionCancellationTask is null)
                    this.ExecutionCancellationTask = ReserveCancellation(out cancellationSettlement);
                if (!cancelAfterPublication)
                    this.StateValue = RecoveryPruneControllerState.Running;
            }
            this.PublishChanged();
            if (cancellationSettlement is not null)
                StartExecutionCancellation(operation, cancellationSettlement);

            using CancellationTokenSource stopProgress = CancellationTokenSource.CreateLinkedTokenSource(this.Lifetime.Token);
            Task progress = this.ReadProgressAsync(operation, stopProgress.Token);
            InstallerRecoveryPruneResult backend = await operation.Completion.ConfigureAwait(false);
            try { await stopProgress.CancelAsync().ConfigureAwait(false); }
            catch { }
            try { await progress.ConfigureAwait(false); }
            catch { }
            result = ProjectExecutionResult(backend, this.PlanValue!, this.CancellationWasRequested);
        }
        catch (OperationCanceledException) when (operation is null && this.ExecutionStartCancellation?.IsCancellationRequested == true)
        {
            cancelledBeforeStart = !this.DisposeStarted && !this.SessionFaultNotification.IsCompleted;
        }
        catch { }

        await this.SettleOwnedSessionAsync().ConfigureAwait(false);
        lock (this.Sync)
        {
            this.ExecutionStartCancellation?.Dispose();
            this.ExecutionStartCancellation = null;
            this.ExecutionStartCancellationTask = null;
            this.ActiveTask = null;
            this.ResultValue = cancelledBeforeStart ? null : result;
            if (!this.DisposeStarted)
            {
                this.StateValue = cancelledBeforeStart
                    ? RecoveryPruneControllerState.CancelledBeforeStart
                    : result is RecoveryPruneStateUnknownPresentation
                        ? RecoveryPruneControllerState.StateUnknown
                        : RecoveryPruneControllerState.Terminal;
            }
        }
        this.PublishChanged();
    }

    private async Task ReadProgressAsync(InstallerRecoveryPruneOperation operation, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        TransactionStage? lastPublishedStage = null;
        int observed = 0;
        await foreach (InstallerRecoveryPruneProgress progress in operation.Progress.ReadAllAsync(cancellationToken))
        {
            observed++;
            if (observed > MaximumProgressEvents || !IsValidProgress(progress))
                return;
            bool publish;
            lock (this.Sync)
            {
                if (!ReferenceEquals(this.Execution, operation))
                    return;
                this.ProgressStageValue = progress.Stage;
                this.CompletedUnitsValue = progress.CompletedUnits;
                this.TotalUnitsValue = progress.TotalUnits;
                publish = lastPublishedStage != progress.Stage || stopwatch.Elapsed >= ProgressPublishInterval;
            }
            if (publish)
            {
                lastPublishedStage = progress.Stage;
                stopwatch.Restart();
                this.PublishChanged();
            }
        }
    }

    private async Task WatchSessionAsync()
    {
        Task completed = await Task.WhenAny(this.SessionFaultNotification, this.StopWatching.Task).ConfigureAwait(false);
        if (completed == this.StopWatching.Task)
            return;
        try { _ = await this.SessionFaultNotification.ConfigureAwait(false); }
        catch { }

        CancellationTokenSource? cancel = null;
        bool cleanup = false;
        bool publish = false;
        lock (this.Sync)
        {
            if (this.DisposeStarted || this.Execution is not null)
                return;
            if (this.Request is not null)
                cancel = this.Request.Cancellation;
            else if (this.StateValue is RecoveryPruneControllerState.Starting or RecoveryPruneControllerState.CancellationRequested)
                cancel = this.ExecutionStartCancellation;
            else
            {
                this.StateValue = RecoveryPruneControllerState.SessionFaulted;
                this.CurrentChoices.Clear();
                this.CurrentConfirmation = null;
                cleanup = true;
                publish = true;
            }
        }
        if (publish)
            this.PublishChanged();
        if (cancel is not null)
            await CancelSourceIgnoringFaultAsync(cancel).ConfigureAwait(false);
        if (cleanup)
            await this.SettleOwnedSessionAsync().ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        this.PublishChanged();
        try
        {
            ActiveRequest? request;
            CancellationTokenSource? start;
            InstallerRecoveryPruneOperation? execution;
            Task? active;
            lock (this.Sync)
            {
                request = this.Request;
                start = this.ExecutionStartCancellation;
                execution = this.Execution;
                active = this.ActiveTask;
            }
            if (request is not null)
                await CancelSourceIgnoringFaultAsync(request.Cancellation).ConfigureAwait(false);
            if (start is not null)
                await CancelSourceIgnoringFaultAsync(start).ConfigureAwait(false);
            if (execution is not null)
                await this.RequestExecutionCancellationIgnoringFaultAsync(execution).ConfigureAwait(false);
            if (active is not null)
            {
                try { await active.ConfigureAwait(false); }
                catch { }
            }
            await this.SettleOwnedSessionAsync().ConfigureAwait(false);
        }
        finally
        {
            try { await this.Lifetime.CancelAsync().ConfigureAwait(false); }
            catch { }
            this.Lifetime.Dispose();
            this.StopWatching.TrySetResult();
            try { await this.SessionWatcher.ConfigureAwait(false); }
            catch { }
            lock (this.Sync)
            {
                this.Request?.Dispose();
                this.Request = null;
                this.ExecutionStartCancellation?.Dispose();
                this.ExecutionStartCancellation = null;
                this.ExecutionStartCancellationTask = null;
                this.ActiveTask = null;
                this.StateValue = RecoveryPruneControllerState.Disposed;
            }
            this.PublishChanged();
        }
    }

    private RecoveryPruneSnapshot CreateSnapshotUnderLock()
    {
        bool idle = this.ActiveTask is null && !this.DisposeStarted;
        return new(
            this.GenerationValue,
            this.RevisionValue,
            this.StateValue,
            this.ReleaseValue,
            this.GameValue,
            Array.AsReadOnly(this.ChoicePresentations.ToArray()),
            this.SelectedValue,
            this.PlanValue,
            this.RejectionValue,
            this.ProgressStageValue,
            this.CompletedUnitsValue,
            this.TotalUnitsValue,
            this.ResultValue,
            idle && this.CanListUnderLock(),
            idle && this.StateValue == RecoveryPruneControllerState.CatalogReady,
            idle && this.StateValue == RecoveryPruneControllerState.CatalogReady && this.SelectedValue is not null,
            idle && this.StateValue == RecoveryPruneControllerState.ReviewReady && this.CurrentConfirmation is not null,
            idle && this.StateValue == RecoveryPruneControllerState.ReadyToRun && this.ConfirmedSession is not null,
            this.StateValue is RecoveryPruneControllerState.Listing
                or RecoveryPruneControllerState.Inspecting
                or RecoveryPruneControllerState.Confirming
                or RecoveryPruneControllerState.Starting
                or RecoveryPruneControllerState.Running,
            this.StateValue is RecoveryPruneControllerState.NotLoaded
                or RecoveryPruneControllerState.NoHistory
                or RecoveryPruneControllerState.CatalogReady
                or RecoveryPruneControllerState.RelistRequired
                or RecoveryPruneControllerState.ReviewReady
                or RecoveryPruneControllerState.ReadyToRun
                or RecoveryPruneControllerState.Cancelled
                or RecoveryPruneControllerState.CancelledBeforeStart
                or RecoveryPruneControllerState.Terminal
                or RecoveryPruneControllerState.StateUnknown
                or RecoveryPruneControllerState.Failed
                or RecoveryPruneControllerState.SessionFaulted
        );
    }

    private static (RecoveryPruneChoice[] Choices, Dictionary<RecoveryPruneChoice, BoundInstallerRecoveryPoint> Authorities) ProjectCatalog(
        BoundInstallerRecoveryCatalogSuccess success
    )
    {
        IReadOnlyList<BoundInstallerRecoveryPoint> source = success.RecoveryPoints
            ?? throw new InvalidOperationException("The recovery catalog was unavailable.");
        int count;
        try { count = source.Count; }
        catch { throw new InvalidOperationException("The recovery catalog was unavailable."); }
        if (count is < 1 or > ProtocolJsonSerializer.MaxRecoveryGenerations)
            throw new InvalidOperationException("The recovery catalog was outside its bounds.");
        RecoveryPruneChoice[] choices = new RecoveryPruneChoice[count];
        Dictionary<RecoveryPruneChoice, BoundInstallerRecoveryPoint> authorities = new(ReferenceEqualityComparer.Instance);
        HashSet<BoundInstallerRecoveryPoint> exact = new(ReferenceEqualityComparer.Instance);
        for (int index = 0; index < count; index++)
        {
            BoundInstallerRecoveryPoint point;
            try { point = source[index]; }
            catch { throw new InvalidOperationException("The recovery catalog was unavailable."); }
            if (
                point is null
                || !exact.Add(point)
                || point.Ordinal != index + 1
                || point.IsCurrent != (index == 0)
                || !Enum.IsDefined(point.OriginOperation)
                || point.IsUserCheckpoint != (point.OriginOperation == InstallerOperation.Backup)
            )
                throw new InvalidOperationException("The recovery catalog semantics were invalid.");
            RecoveryPruneChoice projected = new(
                point.Ordinal,
                point.IsCurrent,
                point.IsUserCheckpoint,
                point.OriginOperation,
                ProjectRestoreTarget(point.RestoreTarget)
            );
            choices[index] = projected;
            authorities.Add(projected, point);
        }
        return (choices, authorities);
    }

    private static (RecoveryPrunePlanPresentation Plan, BoundInstallerRecoveryPruneConfirmation Confirmation) ProjectPlan(
        BoundInstallerRecoveryPrunePlanSuccess success,
        int selectedOrdinal,
        int catalogCount
    )
    {
        IReadOnlyList<ProtocolPlanRisk> source = success.Risks
            ?? throw new InvalidOperationException("The recovery-cleanup risk summary was unavailable.");
        int count;
        ProtocolPlanRisk risk;
        try
        {
            count = source.Count;
            risk = count == 1 ? source[0] : default;
        }
        catch { throw new InvalidOperationException("The recovery-cleanup risk summary was unavailable."); }
        if (
            selectedOrdinal is < 1 or > ProtocolJsonSerializer.MaxRecoveryGenerations
            || catalogCount is < 1 or > ProtocolJsonSerializer.MaxRecoveryGenerations
            || success.RetainNewest != selectedOrdinal
            || success.RetainedCount != selectedOrdinal
            || success.RemovedCount != catalogCount - selectedOrdinal
            || success.CleanupGenerationCount < success.RemovedCount
            || success.CleanupGenerationCount > ProtocolJsonSerializer.MaxRecoveryGenerations
            || success.WarningCount is < 0 or > 256
            || count != 1
            || risk != ProtocolPlanRisk.RecoveryPrune
            || success.RecommendedDefault != ProtocolRecommendedDefault.Cancel
            || !success.RequiresConfirmation
            || success.Confirmation is null
            || success.RemovedCount == 0 && success.CleanupGenerationCount == 0 && !success.AuxiliaryCleanupPlanned
        )
            throw new InvalidOperationException("The recovery-cleanup plan semantics were invalid.");
        return (
            new(
                success.RetainNewest,
                success.RetainedCount,
                success.RemovedCount,
                success.CleanupGenerationCount,
                success.AuxiliaryCleanupPlanned,
                success.WarningCount,
                Array.AsReadOnly(new[] { risk }),
                success.RecommendedDefault,
                success.RequiresConfirmation
            ),
            success.Confirmation
        );
    }

    private static RecoveryPruneResultPresentation ProjectExecutionResult(
        InstallerRecoveryPruneResult result,
        RecoveryPrunePlanPresentation plan,
        bool cancellationRequested
    )
    {
        if (result is InstallerRecoveryPruneStateUnknownResult)
            return new RecoveryPruneStateUnknownPresentation();
        if (result is not InstallerRecoveryPruneTerminalResult terminal || !ValidateTerminal(terminal, plan, cancellationRequested))
            return new RecoveryPruneStateUnknownPresentation();
        return new RecoveryPruneTerminalPresentation(
            terminal.Outcome,
            terminal.DurableState,
            terminal.ErrorCode,
            terminal.RecoveryDisposition,
            terminal.NextAction,
            terminal.Summary.LogicallyRemovedGenerationCount,
            terminal.Summary.PhysicallyCleanedGenerationCount,
            terminal.Summary.PendingCleanupGenerationCount,
            terminal.Summary.AuxiliaryCleanupPending,
            terminal.BackendSettlement
        );
    }

    private static bool ValidateTerminal(
        InstallerRecoveryPruneTerminalResult terminal,
        RecoveryPrunePlanPresentation plan,
        bool cancellationRequested
    )
    {
        if (
            !Enum.IsDefined(terminal.Outcome)
            || !Enum.IsDefined(terminal.DurableState)
            || terminal.ErrorCode is { } error && !Enum.IsDefined(error)
            || !Enum.IsDefined(terminal.RecoveryDisposition)
            || terminal.NextAction != ProtocolNextAction.ListRecoveries
            || !Enum.IsDefined(terminal.BackendSettlement)
            || terminal.Summary is null
            || (terminal.Outcome is ProtocolPruneOutcome.CancelledBeforePublication
                or ProtocolPruneOutcome.CancelledWithCleanupPending
                or ProtocolPruneOutcome.CancelledAfterApply) && !cancellationRequested
        )
            return false;

        InstallerRecoveryPruneSummary summary = terminal.Summary;
        if (terminal.Outcome == ProtocolPruneOutcome.UnexpectedCoreFailure)
        {
            return terminal.DurableState == ProtocolDurableState.Unknown
                && terminal.ErrorCode == ProtocolTerminalErrorCode.UnexpectedCoreFailure
                && terminal.RecoveryDisposition == ProtocolRecoveryDisposition.StateRefreshRequired
                && summary.LogicallyRemovedGenerationCount is null
                && summary.PhysicallyCleanedGenerationCount is null
                && summary.PendingCleanupGenerationCount is null
                && summary.AuxiliaryCleanupPending is null;
        }
        if (
            summary.LogicallyRemovedGenerationCount is not { } logical
            || summary.PhysicallyCleanedGenerationCount is not { } cleaned
            || summary.PendingCleanupGenerationCount is not { } pending
            || summary.AuxiliaryCleanupPending is not { } auxiliaryPending
            || logical is < 0 or > ProtocolJsonSerializer.MaxRecoveryGenerations
            || cleaned is < 0 or > ProtocolJsonSerializer.MaxRecoveryGenerations
            || pending is < 0 or > ProtocolJsonSerializer.MaxRecoveryGenerations
        )
            return false;

        bool pendingWork = pending > 0 || auxiliaryPending;
        ProtocolDurableState observed = logical > 0 || cleaned > 0 ? ProtocolDurableState.PruneApplied : ProtocolDurableState.Unchanged;
        (ProtocolDurableState Durable, bool ErrorRequired, ProtocolRecoveryDisposition Recovery) expected = terminal.Outcome switch
        {
            ProtocolPruneOutcome.Succeeded => (ProtocolDurableState.PruneApplied, false, ProtocolRecoveryDisposition.NotRequired),
            ProtocolPruneOutcome.FailedBeforePublication => (ProtocolDurableState.Unchanged, true, pendingWork ? ProtocolRecoveryDisposition.CleanupPending : ProtocolRecoveryDisposition.NotRequired),
            ProtocolPruneOutcome.CancelledBeforePublication => (ProtocolDurableState.Unchanged, false, pendingWork ? ProtocolRecoveryDisposition.CleanupPending : ProtocolRecoveryDisposition.NotRequired),
            ProtocolPruneOutcome.Interrupted => (observed, true, pendingWork ? ProtocolRecoveryDisposition.CleanupPending : ProtocolRecoveryDisposition.StateRefreshRequired),
            ProtocolPruneOutcome.CancelledWithCleanupPending => (observed, false, ProtocolRecoveryDisposition.CleanupPending),
            ProtocolPruneOutcome.FailedWithCleanupPending => (observed, true, ProtocolRecoveryDisposition.CleanupPending),
            ProtocolPruneOutcome.CancelledAfterApply => (ProtocolDurableState.PruneApplied, false, ProtocolRecoveryDisposition.StateRefreshRequired),
            ProtocolPruneOutcome.FailedAfterApply => (ProtocolDurableState.PruneApplied, true, ProtocolRecoveryDisposition.StateRefreshRequired),
            _ => ((ProtocolDurableState)(-1), false, (ProtocolRecoveryDisposition)(-1))
        };
        bool errorValid = expected.ErrorRequired
            ? terminal.ErrorCode is not null and not ProtocolTerminalErrorCode.UnexpectedCoreFailure
            : terminal.ErrorCode is null;
        if (terminal.DurableState != expected.Durable || terminal.RecoveryDisposition != expected.Recovery || !errorValid)
            return false;

        int removed = plan.RemovedCount;
        int cleanup = plan.CleanupGenerationCount;
        bool logicalPublished = logical > 0;
        if (
            logical is not 0 && logical != removed
            || cleaned > cleanup
            || pending > cleanup
            || cleaned > cleanup - pending
            || !logicalPublished && removed > 0 && cleaned > 0
            || auxiliaryPending && !plan.AuxiliaryCleanupPlanned && (removed == 0 || logicalPublished)
        )
            return false;
        int expectedAccounted = logicalPublished ? cleanup : cleanup - removed;
        bool completeAccounting = terminal.Outcome is ProtocolPruneOutcome.Interrupted
            or ProtocolPruneOutcome.CancelledWithCleanupPending
            or ProtocolPruneOutcome.FailedWithCleanupPending
            || terminal.Outcome is ProtocolPruneOutcome.FailedBeforePublication or ProtocolPruneOutcome.CancelledBeforePublication
                && pendingWork;
        if (completeAccounting && cleaned + pending != expectedAccounted)
            return false;
        if (
            terminal.Outcome is ProtocolPruneOutcome.Succeeded or ProtocolPruneOutcome.CancelledAfterApply or ProtocolPruneOutcome.FailedAfterApply
            && (logical != removed || cleaned != cleanup || pending != 0 || auxiliaryPending)
        )
            return false;
        return terminal.Outcome switch
        {
            ProtocolPruneOutcome.Succeeded => !pendingWork,
            ProtocolPruneOutcome.FailedBeforePublication or ProtocolPruneOutcome.CancelledBeforePublication => logical == 0 && cleaned == 0,
            ProtocolPruneOutcome.CancelledWithCleanupPending or ProtocolPruneOutcome.FailedWithCleanupPending => pendingWork,
            ProtocolPruneOutcome.CancelledAfterApply or ProtocolPruneOutcome.FailedAfterApply => !pendingWork && observed == ProtocolDurableState.PruneApplied,
            ProtocolPruneOutcome.Interrupted => true,
            _ => false
        };
    }

    private static bool IsValidProgress(InstallerRecoveryPruneProgress progress)
        => progress is not null
            && Enum.IsDefined(progress.Stage)
            && progress.CompletedUnits is >= 0 and <= ProtocolJsonSerializer.MaxRecoveryGenerations
            && progress.TotalUnits is null or >= 0 and <= ProtocolJsonSerializer.MaxRecoveryGenerations
            && (progress.TotalUnits is null || progress.CompletedUnits <= progress.TotalUnits);

    private static RecoveryPruneRestoreTarget ProjectRestoreTarget(BoundInstallerRecoveryRestoreTarget target)
        => target switch
        {
            BoundInstallerRecoveryReleaseTarget release when IsValidRelease(release.Tag, release.EmbeddedVersion)
                => new RecoveryPruneReleaseTarget(release.Tag, release.EmbeddedVersion),
            BoundInstallerRecoveryUninstalledTarget => new RecoveryPruneUninstalledTarget(),
            _ => throw new InvalidOperationException("The recovery restore target was invalid.")
        };

    private static RecoveryPruneRelease ProjectRelease(ProtocolReleaseIdentity release)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!IsValidRelease(release.Tag, release.EmbeddedVersion)
            || !string.Equals(release.Repository, ForkReleaseIdentity.RepositoryUrl, StringComparison.Ordinal))
            throw new InvalidOperationException("The verified recovery-cleanup release was invalid.");
        return new(release.Tag, release.EmbeddedVersion);
    }

    private static RecoveryPruneGame ProjectGame(VerifiedGamePresentation game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (string.IsNullOrWhiteSpace(game.DisplayName) || string.IsNullOrWhiteSpace(game.DisplayPath))
            throw new InvalidOperationException("The verified recovery-cleanup game presentation was invalid.");
        return new(game.DisplayName, game.DisplayPath);
    }

    private static bool IsValidRelease(string tag, string embeddedVersion)
    {
        try
        {
            ForkReleaseIdentity identity = ForkReleaseIdentity.Parse(tag);
            return string.Equals(identity.EmbeddedVersion, embeddedVersion, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static RecoveryPruneRejection ProjectCatalogRejection(BoundInstallerRecoveryCatalogRejection rejection)
    {
        bool valid = rejection switch
        {
            { ErrorCode: ProtocolPrePlanErrorCode.RequestCancelled, NextAction: ProtocolNextAction.RetryRequest, IsTerminal: false } => true,
            { ErrorCode: ProtocolPrePlanErrorCode.InvalidGameFolder, NextAction: ProtocolNextAction.SelectGameFolder, IsTerminal: false } => true,
            { ErrorCode: ProtocolPrePlanErrorCode.RecoveryUnavailable, NextAction: ProtocolNextAction.ListRecoveries, IsTerminal: false } => true,
            { ErrorCode: ProtocolPrePlanErrorCode.PermissionDenied, NextAction: ProtocolNextAction.ReviewFilesystem, IsTerminal: false } => true,
            { ErrorCode: ProtocolPrePlanErrorCode.UnexpectedFailure, NextAction: ProtocolNextAction.StartNewSession or ProtocolNextAction.ViewPrivateLog, IsTerminal: true } => true,
            _ => false
        };
        return valid ? new(rejection.ErrorCode, rejection.NextAction, rejection.IsTerminal) : throw new InvalidOperationException("The recovery catalog rejection was invalid.");
    }

    private static RecoveryPruneRejection ProjectPruneRejection(
        BoundInstallerRecoveryPrunePlanRejection rejection,
        int selectedOrdinal,
        int catalogCount
    )
    {
        bool valid = rejection switch
        {
            { ErrorCode: ProtocolPrePlanErrorCode.NothingToPrune, NextAction: ProtocolNextAction.ListRecoveries, IsTerminal: false } when selectedOrdinal == catalogCount => true,
            { ErrorCode: ProtocolPrePlanErrorCode.RequestCancelled, NextAction: ProtocolNextAction.RetryRequest, IsTerminal: false } => true,
            { ErrorCode: ProtocolPrePlanErrorCode.InvalidGameFolder, NextAction: ProtocolNextAction.SelectGameFolder, IsTerminal: false } => true,
            { ErrorCode: ProtocolPrePlanErrorCode.InspectionFailed, NextAction: ProtocolNextAction.InspectAgain, IsTerminal: false } => true,
            { ErrorCode: ProtocolPrePlanErrorCode.PermissionDenied, NextAction: ProtocolNextAction.ReviewFilesystem, IsTerminal: false } => true,
            { ErrorCode: ProtocolPrePlanErrorCode.UnexpectedFailure, NextAction: ProtocolNextAction.StartNewSession or ProtocolNextAction.ViewPrivateLog, IsTerminal: true } => true,
            _ => false
        };
        return valid ? new(rejection.ErrorCode, rejection.NextAction, rejection.IsTerminal) : throw new InvalidOperationException("The recovery-cleanup rejection was invalid.");
    }

    private static void ValidateConfirmedOwner(
        IConfirmedRecoveryPruneSession? confirmed,
        IPlanInspectionSession source,
        Task<InstallerProtocolClientException> sessionFaultNotification
    )
    {
        if (confirmed is null
            || !ReferenceEquals(confirmed.Release, source.Release)
            || !ReferenceEquals(confirmed.Game, source.Game)
            || !ReferenceEquals(confirmed.SessionFaulted, sessionFaultNotification))
            throw new InvalidOperationException("The confirmed recovery-cleanup owner was invalid.");
    }

    private bool CanListUnderLock() => this.StateValue is RecoveryPruneControllerState.NotLoaded
        or RecoveryPruneControllerState.CatalogReady
        or RecoveryPruneControllerState.NoHistory
        or RecoveryPruneControllerState.RelistRequired;

    private void RevokeCatalogUnderLock()
    {
        this.CurrentChoices.Clear();
        this.ChoicePresentations = [];
        this.SelectedValue = null;
    }

    private void RevokePlanUnderLock()
    {
        this.CurrentConfirmation = null;
        this.PlanValue = null;
    }

    private Task SettleOwnedSessionAsync()
    {
        lock (this.Sync)
            return this.SessionCleanupTask ??= this.ConfirmedSession is { } confirmed
                ? DisposeSafelyAsync(confirmed)
                : DisposeSafelyAsync(this.Session);
    }

    private async Task SettleRejectedConfirmedOwnerAsync(IConfirmedRecoveryPruneSession confirmed)
    {
        Task cleanup = DisposeSafelyAsync(confirmed);
        Task? prior;
        lock (this.Sync)
        {
            prior = this.SessionCleanupTask;
            this.SessionCleanupTask ??= cleanup;
        }
        if (prior is null)
            await cleanup.ConfigureAwait(false);
        else
            await Task.WhenAll(prior, cleanup).ConfigureAwait(false);
    }

    private Task RequestExecutionCancellationIgnoringFaultAsync(InstallerRecoveryPruneOperation execution)
    {
        Task result;
        TaskCompletionSource? settlement = null;
        lock (this.Sync)
            result = this.ExecutionCancellationTask ??= ReserveCancellation(out settlement);
        if (settlement is not null)
            StartExecutionCancellation(execution, settlement);
        return IgnoreFaultAsync(result);
    }

    private void AssertUsableUnderLock()
    {
        if (this.DisposeStarted || this.StateValue is RecoveryPruneControllerState.Disposing or RecoveryPruneControllerState.Disposed)
            throw new ObjectDisposedException(nameof(RecoveryPruneController));
        if (this.StateValue == RecoveryPruneControllerState.SessionFaulted)
            throw new InvalidOperationException("The recovery-cleanup session has faulted.");
    }

    private void PublishChanged()
    {
        EventHandler? changed;
        lock (this.Sync)
        {
            this.RevisionValue++;
            changed = this.Changed;
        }
        if (changed is null)
            return;
        foreach (EventHandler handler in changed.GetInvocationList().Cast<EventHandler>())
        {
            try { handler(this, EventArgs.Empty); }
            catch { }
        }
    }

    private static Task ReserveCancellation(out TaskCompletionSource? settlement)
    {
        settlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return settlement.Task;
    }

    private static void StartSourceCancellation(CancellationTokenSource source, TaskCompletionSource settlement)
    {
        Task request;
        try { request = source.CancelAsync(); }
        catch (ObjectDisposedException) { settlement.TrySetResult(); return; }
        catch { settlement.TrySetResult(); return; }
        _ = SettleCancellationAsync(request, settlement);
    }

    private static void StartExecutionCancellation(InstallerRecoveryPruneOperation execution, TaskCompletionSource settlement)
    {
        Task request;
        try { request = execution.RequestCancellationAsync(); }
        catch { settlement.TrySetResult(); return; }
        _ = SettleCancellationAsync(request, settlement);
    }

    private static async Task SettleCancellationAsync(Task request, TaskCompletionSource settlement)
    {
        try { await request.ConfigureAwait(false); settlement.TrySetResult(); }
        catch { settlement.TrySetResult(); }
    }

    private static async Task CancelSourceIgnoringFaultAsync(CancellationTokenSource source)
    {
        try { await source.CancelAsync().ConfigureAwait(false); }
        catch { }
    }

    private static async Task IgnoreFaultAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { }
    }

    private static async Task DisposeSafelyAsync(IAsyncDisposable owner)
    {
        try { await owner.DisposeAsync().ConfigureAwait(false); }
        catch { }
    }

    private sealed class ActiveRequest : IDisposable
    {
        public CancellationTokenSource Cancellation { get; }
        public Task? CancellationTask { get; set; }
        public bool CancellationWasRequested { get; set; }

        public ActiveRequest(CancellationTokenSource cancellation)
        {
            this.Cancellation = cancellation;
        }

        public void Dispose() => this.Cancellation.Dispose();
    }
}
