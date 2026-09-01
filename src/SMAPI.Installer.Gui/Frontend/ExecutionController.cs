using System.Diagnostics;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Core.Transactions;
using StardewModdingAPI.Installer.Gui.Backend;

namespace StardewModdingAPI.Installer.Gui.Frontend;

internal enum ExecutionState
{
    Ready,
    Starting,
    Running,
    CancellationRequested,
    CancelledBeforeStart,
    Terminal,
    RecoveryRequired,
    RecoveryStarting,
    RecoveryCancellationRequested,
    RecoveryRunning,
    RecoveryCompleted,
    PrestartFault,
    Disposing,
    Disposed
}

internal sealed record ExecutionPlanPresentation(
    InstallerOperation Operation,
    IReadOnlyList<PlanReviewOperationCount> OperationCounts,
    IReadOnlyList<ProtocolPlanRisk> Risks,
    int AdditionalNoticeCount
);

internal sealed record ExecutionSnapshot(
    long Revision,
    ExecutionState State,
    ExecutionPlanPresentation Plan,
    TransactionStage? ProgressStage,
    int CompletedUnits,
    int? TotalUnits,
    InstallerExecutionResult? ExecutionResult,
    InstallerRecoveryResult? RecoveryResult,
    bool CanRun,
    bool CanCancel,
    bool CanRecover,
    bool CanExit
);

/// <summary>
/// Owns one exact confirmed plan and its optional post-execution recovery capability. It publishes only bounded,
/// typed protocol projections; backend strings, paths, identifiers, digests, and authority never cross this layer.
/// </summary>
internal sealed class ExecutionController : IAsyncDisposable
{
    private static readonly TimeSpan ProgressPublishInterval = TimeSpan.FromMilliseconds(75);

    private readonly object Sync = new();
    private readonly IConfirmedInstallerSession Session;
    private readonly ExecutionPlanPresentation Plan;
    private readonly CancellationTokenSource Lifetime = new();
    private readonly Task<InstallerProtocolClientException> SessionFaultNotification;
    private readonly TaskCompletionSource StopWatching = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task SessionWatcher;
    private ExecutionState StateValue = ExecutionState.Ready;
    private InstallerExecutionOperation? Execution;
    private InstallerPostExecutionRecoveryOwner? RecoveryOwner;
    private InstallerExecutionResult? ExecutionResultValue;
    private InstallerRecoveryResult? RecoveryResultValue;
    private TransactionStage? ProgressStageValue;
    private int CompletedUnitsValue;
    private int? TotalUnitsValue;
    private Task? ActiveTask;
    private CancellationTokenSource? ExecutionStartCancellation;
    private Task? ExecutionStartCancellationTask;
    private Task? ExecutionCancellationTask;
    private CancellationTokenSource? RecoveryPreparationCancellation;
    private Task? RecoveryPreparationCancellationTask;
    private Task? DisposalTask;
    private Task? SessionCleanupTask;
    private long RevisionValue;
    private bool DisposeStarted;

    public ExecutionController(IConfirmedInstallerSession session, ExecutionPlanPresentation plan)
    {
        this.Session = session ?? throw new ArgumentNullException(nameof(session));
        this.Plan = ValidatePlan(plan);
        this.SessionFaultNotification = session.SessionFaulted
            ?? throw new InvalidOperationException("The confirmed installer session had no fault notification.");
        this.SessionWatcher = this.WatchSessionAsync();
    }

    public event EventHandler? Changed;

    public ExecutionSnapshot Snapshot
    {
        get
        {
            lock (this.Sync)
                return this.CreateSnapshotUnderLock();
        }
    }

    /// <summary>Consume the confirmed plan exactly once. Entering this method never happens implicitly.</summary>
    public Task RunAsync()
    {
        Task task;
        lock (this.Sync)
        {
            this.AssertNotDisposedUnderLock();
            if (this.StateValue != ExecutionState.Ready || this.ActiveTask is not null)
                throw new InvalidOperationException("The confirmed installer plan can only be run once from the ready state.");
            this.StateValue = ExecutionState.Starting;
            this.ExecutionStartCancellation = CancellationTokenSource.CreateLinkedTokenSource(this.Lifetime.Token);
            this.ActiveTask = task = this.RunExecutionAsync();
        }
        this.PublishChanged();
        return task;
    }

    /// <summary>Request cancellation at most once; completion still comes from the exact execution terminal.</summary>
    public Task RequestCancellationAsync()
    {
        Task result;
        bool publish = false;
        CancellationTokenSource? sourceToCancel = null;
        TaskCompletionSource? sourceSettlement = null;
        InstallerExecutionOperation? executionToCancel = null;
        TaskCompletionSource? executionSettlement = null;
        lock (this.Sync)
        {
            this.AssertNotDisposedUnderLock();
            if (this.StateValue == ExecutionState.Starting)
            {
                result = this.ExecutionStartCancellationTask ??= ReserveCancellation(out sourceSettlement);
                if (sourceSettlement is not null)
                    sourceToCancel = this.ExecutionStartCancellation!;
                this.StateValue = ExecutionState.CancellationRequested;
                publish = true;
            }
            else if (this.StateValue == ExecutionState.Running)
            {
                result = this.ExecutionCancellationTask ??= ReserveCancellation(out executionSettlement);
                if (executionSettlement is not null)
                    executionToCancel = this.Execution!;
                this.StateValue = ExecutionState.CancellationRequested;
                publish = true;
            }
            else if (this.StateValue == ExecutionState.CancellationRequested)
            {
                if (this.Execution is null)
                {
                    result = this.ExecutionStartCancellationTask ??= ReserveCancellation(out sourceSettlement);
                    if (sourceSettlement is not null)
                        sourceToCancel = this.ExecutionStartCancellation!;
                }
                else
                {
                    result = this.ExecutionCancellationTask ??= ReserveCancellation(out executionSettlement);
                    if (executionSettlement is not null)
                        executionToCancel = this.Execution;
                }
            }
            else if (this.StateValue == ExecutionState.RecoveryStarting)
            {
                result = this.RecoveryPreparationCancellationTask ??= ReserveCancellation(out sourceSettlement);
                if (sourceSettlement is not null)
                    sourceToCancel = this.RecoveryPreparationCancellation!;
                this.StateValue = ExecutionState.RecoveryCancellationRequested;
                publish = true;
            }
            else if (this.StateValue == ExecutionState.RecoveryCancellationRequested)
                result = this.RecoveryPreparationCancellationTask ?? Task.CompletedTask;
            else
                return Task.CompletedTask;
        }
        if (publish)
            this.PublishChanged();
        if (sourceSettlement is not null)
            StartSourceCancellation(sourceToCancel!, sourceSettlement);
        if (executionSettlement is not null)
            StartExecutionCancellation(executionToCancel!, executionSettlement);
        return result;
    }

    /// <summary>Start one explicit, non-cancellable fresh recovery attempt.</summary>
    public Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        Task task;
        lock (this.Sync)
        {
            this.AssertNotDisposedUnderLock();
            cancellationToken.ThrowIfCancellationRequested();
            if (this.StateValue != ExecutionState.RecoveryRequired || this.RecoveryOwner is null || this.ActiveTask is not null)
                throw new InvalidOperationException("Interrupted recovery is not available in the current state.");
            this.StateValue = ExecutionState.RecoveryStarting;
            this.ProgressStageValue = null;
            this.CompletedUnitsValue = 0;
            this.TotalUnitsValue = null;
            this.RecoveryResultValue = null;
            this.RecoveryPreparationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.Lifetime.Token);
            this.RecoveryPreparationCancellationTask = null;
            this.ActiveTask = task = this.RunRecoveryAsync(cancellationToken);
        }
        this.PublishChanged();
        return task;
    }

    public ValueTask DisposeAsync()
    {
        lock (this.Sync)
        {
            if (this.DisposalTask is not null)
                return new ValueTask(this.DisposalTask);
            if (this.StateValue == ExecutionState.Disposed)
                return ValueTask.CompletedTask;
            this.DisposeStarted = true;
            ExecutionState previous = this.StateValue;
            this.StateValue = ExecutionState.Disposing;
            return new ValueTask(this.DisposalTask = this.DisposeCoreAsync(previous));
        }
    }

    private async Task RunExecutionAsync()
    {
        await Task.Yield();
        InstallerExecutionOperation? operation = null;
        InstallerExecutionResult? result = null;
        InstallerPostExecutionRecoveryOwner? recoveryOwner = null;
        bool prestartFault = false;
        bool cancelledBeforeStart = false;
        try
        {
            CancellationToken startToken;
            lock (this.Sync)
                startToken = this.ExecutionStartCancellation!.Token;
            operation = await this.Session.ExecuteAsync(startToken).ConfigureAwait(false);
            if (operation?.Progress is null || operation.Completion is null)
                throw new InvalidOperationException("The confirmed installer operation was unavailable.");

            bool cancelAfterAdmission;
            TaskCompletionSource? cancellationSettlement = null;
            lock (this.Sync)
            {
                this.Execution = operation;
                cancelAfterAdmission = this.StateValue == ExecutionState.CancellationRequested || this.DisposeStarted;
                if (cancelAfterAdmission && this.ExecutionCancellationTask is null)
                    this.ExecutionCancellationTask = ReserveCancellation(out cancellationSettlement);
                if (!cancelAfterAdmission)
                    this.StateValue = ExecutionState.Running;
            }
            this.PublishChanged();
            if (cancellationSettlement is not null)
                StartExecutionCancellation(operation, cancellationSettlement);

            Task progress = this.ReadExecutionProgressAsync(operation);
            result = await operation.Completion.ConfigureAwait(false);
            try { await progress.ConfigureAwait(false); }
            catch { /* Progress is ancillary; the exact terminal remains authoritative. */ }

            if (RequiresRecovery(result))
            {
                try { recoveryOwner = await this.Session.TakePostExecutionRecoveryOwnerAsync().ConfigureAwait(false); }
                catch { recoveryOwner = null; }
            }
            else
                await this.SettleSessionAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operation is null && this.ExecutionStartCancellation?.IsCancellationRequested == true)
        {
            cancelledBeforeStart = !this.DisposeStarted && !this.SessionFaultNotification.IsCompleted;
            prestartFault = !cancelledBeforeStart;
            await this.SettleSessionAsync().ConfigureAwait(false);
        }
        catch
        {
            if (operation is null)
            {
                prestartFault = true;
                await this.SettleSessionAsync().ConfigureAwait(false);
            }
            else
            {
                result = new InstallerExecutionStateUnknownResult();
                try { recoveryOwner = await this.Session.TakePostExecutionRecoveryOwnerAsync().ConfigureAwait(false); }
                catch { recoveryOwner = null; }
            }
        }

        lock (this.Sync)
        {
            this.ExecutionStartCancellation?.Dispose();
            this.ExecutionStartCancellation = null;
            this.ExecutionStartCancellationTask = null;
            this.ActiveTask = null;
            this.ExecutionResultValue = result;
            this.RecoveryOwner = recoveryOwner;
            this.StateValue = cancelledBeforeStart
                ? ExecutionState.CancelledBeforeStart
                : prestartFault
                ? ExecutionState.PrestartFault
                : RequiresRecovery(result)
                    ? ExecutionState.RecoveryRequired
                    : ExecutionState.Terminal;
        }
        this.PublishChanged();
    }

    private async Task RunRecoveryAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        InstallerRecoveryResult result;
        try
        {
            InstallerPostExecutionRecoveryOwner owner;
            CancellationToken preparationToken;
            lock (this.Sync)
            {
                owner = this.RecoveryOwner!;
                preparationToken = this.RecoveryPreparationCancellation!.Token;
            }
            InstallerRecoveryOperation operation = await owner.RecoverInterruptedAsync(preparationToken).ConfigureAwait(false);
            lock (this.Sync)
            {
                this.RecoveryPreparationCancellation?.Dispose();
                this.RecoveryPreparationCancellation = null;
                this.RecoveryPreparationCancellationTask = null;
                if (this.StateValue is ExecutionState.RecoveryStarting or ExecutionState.RecoveryCancellationRequested or ExecutionState.Disposing)
                    this.StateValue = ExecutionState.RecoveryRunning;
            }
            this.PublishChanged();
            Task progress = this.ReadRecoveryProgressAsync(operation);
            result = await operation.Completion.ConfigureAwait(false);
            try { await progress.ConfigureAwait(false); }
            catch { /* Progress is ancillary; the exact recovery terminal remains authoritative. */ }
        }
        catch (OperationCanceledException) when (this.RecoveryPreparationCancellation?.IsCancellationRequested == true)
        {
            lock (this.Sync)
            {
                this.RecoveryPreparationCancellation.Dispose();
                this.RecoveryPreparationCancellation = null;
                this.RecoveryPreparationCancellationTask = null;
                this.ActiveTask = null;
                this.StateValue = ExecutionState.RecoveryRequired;
            }
            this.PublishChanged();
            return;
        }
        catch
        {
            result = new InstallerRecoveryStateUnknownResult();
        }

        lock (this.Sync)
        {
            this.RecoveryPreparationCancellation?.Dispose();
            this.RecoveryPreparationCancellation = null;
            this.RecoveryPreparationCancellationTask = null;
            this.ActiveTask = null;
            this.RecoveryResultValue = result;
            this.StateValue = RecoveryNeedsRetry(result)
                ? ExecutionState.RecoveryRequired
                : ExecutionState.RecoveryCompleted;
        }
        this.PublishChanged();
    }

    private async Task ReadExecutionProgressAsync(InstallerExecutionOperation operation)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        TransactionStage? lastPublishedStage = null;
        await foreach (InstallerExecutionProgress progress in operation.Progress.ReadAllAsync())
        {
            bool publish;
            lock (this.Sync)
            {
                if (!ReferenceEquals(this.Execution, operation) || this.StateValue is not (ExecutionState.Running or ExecutionState.CancellationRequested))
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

    private async Task ReadRecoveryProgressAsync(InstallerRecoveryOperation operation)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        TransactionStage? lastPublishedStage = null;
        await foreach (InstallerRecoveryProgress progress in operation.Progress.ReadAllAsync())
        {
            bool publish;
            lock (this.Sync)
            {
                if (this.StateValue != ExecutionState.RecoveryRunning)
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

        bool cleanup = false;
        bool publish = false;
        CancellationTokenSource? cancelStart = null;
        lock (this.Sync)
        {
            if (this.DisposeStarted || this.Execution is not null)
                return;
            if (this.StateValue == ExecutionState.Ready)
            {
                this.StateValue = ExecutionState.PrestartFault;
                cleanup = true;
                publish = true;
            }
            else if (this.StateValue is ExecutionState.Starting or ExecutionState.CancellationRequested)
                cancelStart = this.ExecutionStartCancellation;
        }
        if (cancelStart is not null)
            await CancelSourceIgnoringFaultAsync(cancelStart).ConfigureAwait(false);
        if (publish)
            this.PublishChanged();
        if (cleanup)
            await this.SettleSessionAsync().ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync(ExecutionState stateAtStart)
    {
        try
        {
            InstallerExecutionOperation? execution;
            Task? active;
            InstallerPostExecutionRecoveryOwner? recovery;
            CancellationTokenSource? executionStart;
            CancellationTokenSource? recoveryPreparation;
            lock (this.Sync)
            {
                execution = this.Execution;
                active = this.ActiveTask;
                recovery = this.RecoveryOwner;
                executionStart = execution is null ? this.ExecutionStartCancellation : null;
                recoveryPreparation = this.RecoveryPreparationCancellation;
            }

            if (executionStart is not null)
                await CancelSourceIgnoringFaultAsync(executionStart).ConfigureAwait(false);
            if (recoveryPreparation is not null)
                await CancelSourceIgnoringFaultAsync(recoveryPreparation).ConfigureAwait(false);

            bool executionWasActive = stateAtStart is ExecutionState.Starting or ExecutionState.Running or ExecutionState.CancellationRequested;
            if (executionWasActive && execution is not null)
            {
                try { await this.RequestExecutionCancellationForDisposeAsync(execution).ConfigureAwait(false); }
                catch { }
            }
            else if (executionWasActive)
            {
                try { this.Lifetime.Cancel(); }
                catch (ObjectDisposedException) { }
            }
            if (active is not null)
            {
                try { await active.ConfigureAwait(false); }
                catch { }
            }
            lock (this.Sync)
                recovery = this.RecoveryOwner;
            if (recovery is not null)
                await DisposeSafelyAsync(recovery).ConfigureAwait(false);
            else
                await this.SettleSessionAsync().ConfigureAwait(false);
        }
        finally
        {
            this.Lifetime.Dispose();
            this.StopWatching.TrySetResult();
            try { await this.SessionWatcher.ConfigureAwait(false); }
            catch { }
            lock (this.Sync)
            {
                this.ActiveTask = null;
                this.StateValue = ExecutionState.Disposed;
            }
            this.PublishChanged();
        }
    }

    private Task RequestExecutionCancellationForDisposeAsync(InstallerExecutionOperation operation)
    {
        Task result;
        TaskCompletionSource? settlement = null;
        lock (this.Sync)
            result = this.ExecutionCancellationTask ??= ReserveCancellation(out settlement);
        if (settlement is not null)
            StartExecutionCancellation(operation, settlement);
        return result;
    }

    private ExecutionSnapshot CreateSnapshotUnderLock()
    {
        return new(
            this.RevisionValue,
            this.StateValue,
            this.Plan,
            this.ProgressStageValue,
            this.CompletedUnitsValue,
            this.TotalUnitsValue,
            this.ExecutionResultValue,
            this.RecoveryResultValue,
            this.StateValue == ExecutionState.Ready && !this.DisposeStarted,
            this.StateValue is ExecutionState.Starting or ExecutionState.Running or ExecutionState.RecoveryStarting,
            this.StateValue == ExecutionState.RecoveryRequired && this.RecoveryOwner is not null && this.ActiveTask is null && !this.DisposeStarted,
            this.StateValue is ExecutionState.Terminal
                or ExecutionState.RecoveryRequired
                or ExecutionState.RecoveryCompleted
                or ExecutionState.CancelledBeforeStart
                or ExecutionState.PrestartFault
        );
    }

    private static ExecutionPlanPresentation ValidatePlan(ExecutionPlanPresentation plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!Enum.IsDefined(plan.Operation) || plan.Operation == InstallerOperation.Rollback)
            throw new ArgumentOutOfRangeException(nameof(plan));
        if (plan.OperationCounts is null || plan.Risks is null || plan.AdditionalNoticeCount is < 0 or > 256)
            throw new ArgumentException("The confirmed plan presentation is invalid.", nameof(plan));
        int aggregate = 0;
        HashSet<PlanOperationKind> kinds = [];
        foreach (PlanReviewOperationCount item in plan.OperationCounts)
        {
            if (!Enum.IsDefined(item.Kind) || item.Count is < 0 or > 20_000 || !kinds.Add(item.Kind))
                throw new ArgumentException("The confirmed plan operation summary is invalid.", nameof(plan));
            aggregate = checked(aggregate + item.Count);
            if (aggregate > 20_000)
                throw new ArgumentException("The confirmed plan operation summary is too large.", nameof(plan));
        }
        HashSet<ProtocolPlanRisk> risks = [];
        foreach (ProtocolPlanRisk risk in plan.Risks)
        {
            if (risk is not (ProtocolPlanRisk.Uninstall or ProtocolPlanRisk.Downgrade or ProtocolPlanRisk.ModifiedOrUnknownFileApproval)
                || !risks.Add(risk))
            {
                throw new ArgumentException("The confirmed plan risk summary is invalid.", nameof(plan));
            }
        }
        return plan with
        {
            OperationCounts = Array.AsReadOnly(plan.OperationCounts.ToArray()),
            Risks = Array.AsReadOnly(plan.Risks.ToArray())
        };
    }

    private static bool RequiresRecovery(InstallerExecutionResult? result)
        => result is InstallerExecutionStateUnknownResult
            || result is InstallerExecutionTerminalResult
            {
                RecoveryDisposition: ProtocolRecoveryDisposition.InterruptedRecoveryRequired
            };

    private static bool RecoveryNeedsRetry(InstallerRecoveryResult result)
        => result is InstallerRecoveryStateUnknownResult
            || result is InstallerRecoveryTerminalResult { NextAction: ProtocolNextAction.RecoverInterrupted };

    private static Task ReserveCancellation(out TaskCompletionSource? settlement)
    {
        settlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return settlement.Task;
    }

    private static void StartExecutionCancellation(InstallerExecutionOperation operation, TaskCompletionSource settlement)
    {
        Task request;
        try { request = operation.RequestCancellationAsync(); }
        catch (Exception error)
        {
            settlement.TrySetException(error);
            return;
        }
        _ = SettleCancellationAsync(request, settlement);
    }

    private static void StartSourceCancellation(CancellationTokenSource source, TaskCompletionSource settlement)
    {
        Task request;
        try { request = source.CancelAsync(); }
        catch (ObjectDisposedException)
        {
            settlement.TrySetResult();
            return;
        }
        catch (Exception error)
        {
            settlement.TrySetException(error);
            return;
        }
        _ = SettleCancellationAsync(request, settlement);
    }

    private static async Task SettleCancellationAsync(Task request, TaskCompletionSource settlement)
    {
        try
        {
            await request.ConfigureAwait(false);
            settlement.TrySetResult();
        }
        catch (Exception error)
        {
            settlement.TrySetException(error);
        }
    }

    private static async Task CancelSourceIgnoringFaultAsync(CancellationTokenSource source)
    {
        try { await source.CancelAsync().ConfigureAwait(false); }
        catch { }
    }

    private static async Task DisposeSafelyAsync(IAsyncDisposable owner)
    {
        try { await owner.DisposeAsync().ConfigureAwait(false); }
        catch { }
    }

    private Task SettleSessionAsync()
    {
        lock (this.Sync)
            return this.SessionCleanupTask ??= DisposeSafelyAsync(this.Session);
    }

    private void AssertNotDisposedUnderLock()
    {
        if (this.DisposeStarted || this.StateValue is ExecutionState.Disposing or ExecutionState.Disposed)
            throw new ObjectDisposedException(nameof(ExecutionController));
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
}
