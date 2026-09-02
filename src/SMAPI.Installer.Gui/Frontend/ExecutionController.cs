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

internal sealed record ExecutionReleasePresentation(string Tag, string Version);

internal abstract record ExecutionResultTarget;

internal sealed record ExecutionReleaseTarget(ExecutionReleasePresentation Release) : ExecutionResultTarget;

internal sealed record ExecutionUninstalledTarget : ExecutionResultTarget;

internal sealed record ExecutionPlanPresentation(
    InstallerOperation Operation,
    string GameDisplayName,
    ExecutionReleasePresentation? CurrentRelease,
    ExecutionResultTarget IntendedResult,
    PlanReviewReleaseRelationship? Relationship,
    PlanReviewRecoveryCapacity RecoveryCapacity,
    IReadOnlyList<PlanReviewOperationCount> OperationCounts,
    IReadOnlyList<PlanReviewPathFact> PathFacts,
    int AdditionalPathFactCount,
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
        this.Plan = ValidatePlan(session, plan);
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

    private static ExecutionPlanPresentation ValidatePlan(IConfirmedInstallerSession session, ExecutionPlanPresentation plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!Enum.IsDefined(plan.Operation))
            throw new ArgumentOutOfRangeException(nameof(plan));
        if (plan.OperationCounts is null
            || plan.PathFacts is null
            || plan.Risks is null
            || plan.AdditionalNoticeCount is < 0 or > 256
            || plan.AdditionalPathFactCount is < 0 or > 20_000)
            throw new ArgumentException("The confirmed plan presentation is invalid.", nameof(plan));
        if (string.IsNullOrWhiteSpace(plan.GameDisplayName)
            || plan.GameDisplayName.Length > 4096 * 6
            || !string.Equals(plan.GameDisplayName, session.Game.DisplayName, StringComparison.Ordinal)
            || !string.Equals(plan.GameDisplayName, InstallerDisplayText.Escape(plan.GameDisplayName), StringComparison.Ordinal))
        {
            throw new ArgumentException("The confirmed game label is invalid.", nameof(plan));
        }
        ValidateRelease(plan.CurrentRelease, nameof(plan));
        if (plan.IntendedResult is ExecutionReleaseTarget releaseTarget)
            ValidateRelease(releaseTarget.Release, nameof(plan));
        else if (plan.IntendedResult is not ExecutionUninstalledTarget)
            throw new ArgumentException("The confirmed intended result is invalid.", nameof(plan));
        ValidateResultSemantics(session, plan);
        if (plan.RecoveryCapacity is not { } capacity
            || capacity.Maximum != ProtocolJsonSerializer.MaxRecoveryGenerations
            || capacity.Used < 0
            || capacity.Used > capacity.Maximum)
        {
            throw new ArgumentException("The confirmed recovery capacity is invalid.", nameof(plan));
        }

        PlanReviewOperationCount[] operationCounts;
        PlanReviewPathFact[] pathFacts;
        ProtocolPlanRisk[] planRisks;
        try
        {
            operationCounts = plan.OperationCounts.ToArray();
            pathFacts = plan.PathFacts.ToArray();
            planRisks = plan.Risks.ToArray();
        }
        catch
        {
            throw new ArgumentException("The confirmed plan presentation could not be read safely.", nameof(plan));
        }

        if (pathFacts.Length > ProcessInstallerProtocolClient.MaximumVisiblePlanPathFacts
            || plan.AdditionalPathFactCount > 0 && pathFacts.Length != ProcessInstallerProtocolClient.MaximumVisiblePlanPathFacts)
        {
            throw new ArgumentException("The confirmed receipt-owned path facts are outside their bounds.", nameof(plan));
        }
        HashSet<string> factPaths = new(StringComparer.Ordinal);
        (int Priority, string Path, PlanOperationKind Action)? previousFact = null;
        Dictionary<PlanOperationKind, int> visibleFactsByAction = [];
        foreach (PlanReviewPathFact fact in pathFacts)
        {
            if (fact is null)
                throw new ArgumentException("The confirmed receipt-owned path facts are invalid.", nameof(plan));
            bool valid = !string.IsNullOrEmpty(fact.DisplayPath)
                && fact.DisplayPath.Length <= 4096 * 6
                && InstallerDisplayText.IsEscapedCanonicalRelativePath(fact.DisplayPath)
                && factPaths.Add(fact.DisplayPath)
                && (fact.FactKind, fact.PlannedAction, plan.Operation) switch
                {
                    (PlanReviewPathFactKind.MissingReceiptOwned, PlanOperationKind.Create, InstallerOperation.Repair) => true,
                    (PlanReviewPathFactKind.ApprovedModifiedReceiptOwned, PlanOperationKind.Replace or PlanOperationKind.Remove, _) => true,
                    (PlanReviewPathFactKind.ApprovedModifiedInstalledLauncher, PlanOperationKind.Replace or PlanOperationKind.Restore, _) => true,
                    _ => false
                };
            if (!valid)
                throw new ArgumentException("The confirmed receipt-owned path facts are invalid.", nameof(plan));
            int priority = fact.FactKind == PlanReviewPathFactKind.MissingReceiptOwned ? 1 : 0;
            (int Priority, string Path, PlanOperationKind Action) key = (priority, fact.DisplayPath, fact.PlannedAction);
            if (previousFact is { } old && (key.Priority < old.Priority
                || key.Priority == old.Priority && StringComparer.Ordinal.Compare(key.Path, old.Path) < 0
                || key.Priority == old.Priority && key.Path == old.Path && key.Action < old.Action))
            {
                throw new ArgumentException("The confirmed receipt-owned path facts are not deterministic.", nameof(plan));
            }
            previousFact = key;
            visibleFactsByAction[fact.PlannedAction] = visibleFactsByAction.GetValueOrDefault(fact.PlannedAction) + 1;
        }

        int aggregate = 0;
        HashSet<PlanOperationKind> kinds = [];
        PlanOperationKind? previousKind = null;
        foreach (PlanReviewOperationCount item in operationCounts)
        {
            if (item is null
                || !Enum.IsDefined(item.Kind)
                || item.Count is <= 0 or > 20_000
                || !kinds.Add(item.Kind)
                || previousKind is { } oldKind && item.Kind <= oldKind)
            {
                throw new ArgumentException("The confirmed plan operation summary is invalid.", nameof(plan));
            }
            previousKind = item.Kind;
            aggregate = checked(aggregate + item.Count);
            if (aggregate > 20_000)
                throw new ArgumentException("The confirmed plan operation summary is too large.", nameof(plan));
        }
        int totalPathFactCount = checked(pathFacts.Length + plan.AdditionalPathFactCount);
        if (totalPathFactCount > aggregate)
            throw new ArgumentException("The confirmed managed-path facts exceed the exact plan operations.", nameof(plan));
        foreach ((PlanOperationKind action, int visibleCount) in visibleFactsByAction)
        {
            int plannedCount = operationCounts.SingleOrDefault(item => item.Kind == action)?.Count ?? 0;
            if (visibleCount > plannedCount)
                throw new ArgumentException("The confirmed managed-path facts exceed their matching plan actions.", nameof(plan));
        }

        if (plan.Operation == InstallerOperation.Rollback)
        {
            bool validRollbackRisks = planRisks.AsSpan().SequenceEqual([ProtocolPlanRisk.Rollback])
                || planRisks.AsSpan().SequenceEqual([ProtocolPlanRisk.Rollback, ProtocolPlanRisk.Downgrade]);
            if (!validRollbackRisks)
                throw new ArgumentException("The confirmed rollback risk summary is invalid.", nameof(plan));
        }
        else
        {
            HashSet<ProtocolPlanRisk> risks = [];
            foreach (ProtocolPlanRisk risk in planRisks)
            {
                if (risk is not (ProtocolPlanRisk.Uninstall or ProtocolPlanRisk.Downgrade or ProtocolPlanRisk.ModifiedOrUnknownFileApproval)
                    || !risks.Add(risk))
                {
                    throw new ArgumentException("The confirmed plan risk summary is invalid.", nameof(plan));
                }
            }
        }
        return plan with
        {
            OperationCounts = Array.AsReadOnly(operationCounts),
            PathFacts = Array.AsReadOnly(pathFacts),
            Risks = Array.AsReadOnly(planRisks)
        };
    }

    private static void ValidateRelease(ExecutionReleasePresentation? release, string parameterName)
    {
        if (release is null)
            return;
        if (string.IsNullOrWhiteSpace(release.Tag)
            || string.IsNullOrWhiteSpace(release.Version)
            || release.Tag.Length > 512
            || release.Version.Length > 512
            || !string.Equals(release.Tag, InstallerDisplayText.Escape(release.Tag), StringComparison.Ordinal)
            || !string.Equals(release.Version, InstallerDisplayText.Escape(release.Version), StringComparison.Ordinal))
        {
            throw new ArgumentException("A confirmed release label is invalid.", parameterName);
        }
        try
        {
            ForkReleaseIdentity identity = ForkReleaseIdentity.Parse(release.Tag);
            if (!string.Equals(release.Version, identity.EmbeddedVersion, StringComparison.Ordinal))
                throw new ArgumentException("A confirmed release label is invalid.", parameterName);
        }
        catch (Exception error) when (error is ArgumentException or PackageSecurityException)
        {
            throw new ArgumentException("A confirmed release label is invalid.", parameterName);
        }
    }

    private static void ValidateResultSemantics(IConfirmedInstallerSession session, ExecutionPlanPresentation plan)
    {
        ExecutionReleasePresentation verified = new(session.Release.Tag, session.Release.EmbeddedVersion);
        ExecutionReleasePresentation? target = (plan.IntendedResult as ExecutionReleaseTarget)?.Release;
        if (plan.Relationship is { } relationship && !Enum.IsDefined(relationship))
            throw new ArgumentException("The confirmed release/result relationship is invalid.", nameof(plan));
        PlanReviewReleaseRelationship? expectedRelationship = GetExpectedRelationship(plan.CurrentRelease, target);
        bool valid = plan.Operation switch
        {
            InstallerOperation.Install => plan.CurrentRelease is null && target == verified,
            InstallerOperation.Update or InstallerOperation.Repair => plan.CurrentRelease is not null && target == verified,
            InstallerOperation.Uninstall => plan.CurrentRelease is not null && plan.IntendedResult is ExecutionUninstalledTarget && plan.Relationship is null,
            InstallerOperation.Backup => plan.CurrentRelease is not null && target == plan.CurrentRelease && plan.Relationship == PlanReviewReleaseRelationship.Current,
            InstallerOperation.Rollback => target is not null || plan.IntendedResult is ExecutionUninstalledTarget,
            _ => false
        };
        if (!valid || plan.Relationship != expectedRelationship)
            throw new ArgumentException("The confirmed release/result relationship is invalid.", nameof(plan));
        bool downgrade = plan.Risks?.Contains(ProtocolPlanRisk.Downgrade) == true;
        if (downgrade != (plan.Relationship == PlanReviewReleaseRelationship.Downgrade))
            throw new ArgumentException("The confirmed downgrade label is inconsistent.", nameof(plan));
    }

    private static PlanReviewReleaseRelationship? GetExpectedRelationship(
        ExecutionReleasePresentation? current,
        ExecutionReleasePresentation? target
    )
    {
        if (current is null || target is null)
            return null;
        if (current == target)
            return PlanReviewReleaseRelationship.Current;
        int comparison = ForkReleaseIdentity.Compare(
            ForkReleaseIdentity.Parse(target.Tag),
            ForkReleaseIdentity.Parse(current.Tag)
        );
        return comparison < 0
            ? PlanReviewReleaseRelationship.Downgrade
            : PlanReviewReleaseRelationship.Upgrade;
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
