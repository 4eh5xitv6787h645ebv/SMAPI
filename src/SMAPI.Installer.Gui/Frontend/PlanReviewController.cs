using System.Collections.ObjectModel;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Gui.Backend;

namespace StardewModdingAPI.Installer.Gui.Frontend;

internal enum PlanReviewState
{
    Choosing,
    SelectionChanged,
    Inspecting,
    Approving,
    Closing,
    Available,
    Rejected,
    Cancelling,
    Cancelled,
    Failed,
    SessionFaulted,
    Disposed
}

internal sealed record PlanReviewRelease(string Tag, string EmbeddedVersion);

internal abstract record PlanReviewResult;

internal sealed record PlanReviewPlan(
    InstallerOperation Operation,
    ObservedInstallState ObservedState,
    PlanReviewRelease? CurrentRelease,
    PlanReviewRelease? TargetRelease,
    bool HasBlockingConflicts,
    IReadOnlyList<ProtocolPlanRisk> Risks,
    ProtocolRecommendedDefault RecommendedDefault,
    bool SeparateConfirmationRequired,
    IReadOnlyList<PlanReviewOperationCount> OperationCounts,
    IReadOnlyList<PlanReviewConflictCount> ConflictCounts,
    IReadOnlyList<PlanReviewCandidateCount> CandidateCounts,
    int AdditionalNoticeCount
) : PlanReviewResult
{
    public IReadOnlyList<PlanReviewCandidate> Candidates { get; init; } = [];
}

internal sealed record PlanReviewRejection(
    ProtocolPrePlanErrorCode ErrorCode,
    ProtocolNextAction NextAction,
    bool IsTerminal
) : PlanReviewResult;

internal sealed record PlanReviewOperationCount(PlanOperationKind Kind, int Count);

internal sealed record PlanReviewConflictCount(PlanConflictCode Code, int Count);

internal sealed record PlanReviewCandidateCount(
    FileReplacementCandidateReason Reason,
    FileReplacementCandidateDisposition Disposition,
    bool ProvisionallyIncluded,
    int Count
);

/// <summary>
/// One sanitized, reference-identity candidate choice. The corresponding backend capability remains private to
/// <see cref="PlanReviewController"/> and reconstructed or stale choices have no authority.
/// </summary>
internal sealed class PlanReviewCandidate
{
    public string DisplayPath { get; }
    public FileReplacementCandidateReason Reason { get; }
    public FileReplacementCandidateDisposition Disposition { get; }
    public bool BackendProvisionallyIncluded { get; }

    internal PlanReviewCandidate(
        string displayPath,
        FileReplacementCandidateReason reason,
        FileReplacementCandidateDisposition disposition,
        bool backendProvisionallyIncluded
    )
    {
        this.DisplayPath = displayPath;
        this.Reason = reason;
        this.Disposition = disposition;
        this.BackendProvisionallyIncluded = backendProvisionallyIncluded;
    }
}

internal sealed record PlanReviewSnapshot(
    long Generation,
    long Revision,
    PlanReviewState State,
    InstallerOperation? SelectedOperation,
    PlanReviewResult? Result,
    bool CanSelect,
    bool CanInspect,
    bool CanCancel,
    bool CanRetry,
    bool CanExit
)
{
    public IReadOnlyList<PlanReviewCandidate> Candidates { get; init; } = [];
    public IReadOnlyList<PlanReviewCandidate> SelectedCandidates { get; init; } = [];
    public int AppliedCandidateApprovalCount { get; init; }
    public bool HasAppliedCandidateApprovals { get; init; }
    public bool CanSelectCandidates { get; init; }
    public bool CanApplyCandidates { get; init; }
    public bool CanClearCandidates { get; init; }
    public bool CanStartFreshInspection { get; init; }
}

/// <summary>Serializes bounded read-only plan inspection through one game-bound backend session.</summary>
internal sealed class PlanReviewController : IAsyncDisposable
{
    private const int MaximumConflictCount = 256;
    private const int MaximumNoticeCount = 256;
    private const int MaximumOperationCount = 20_000;
    private const int MaximumEscapedCandidatePathLength = 4096 * 6;

    private static readonly InstallerOperation[] AllowedOperations =
    [
        InstallerOperation.Install,
        InstallerOperation.Update,
        InstallerOperation.Repair,
        InstallerOperation.Uninstall,
        InstallerOperation.Backup
    ];

    private readonly object Sync = new();
    private readonly IPlanInspectionSession Session;
    private readonly Task<InstallerProtocolClientException> SessionFaultNotification;
    private readonly TaskCompletionSource StopWatching = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task SessionWatcher;
    private PlanReviewState StateValue = PlanReviewState.Choosing;
    private InstallerOperation? SelectedOperationValue;
    private PlanReviewResult? ResultValue;
    private Dictionary<PlanReviewCandidate, InstallerReadOnlyPlanCandidate> CurrentCandidateAuthorities = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PlanReviewCandidate> SelectedCandidates = new(ReferenceEqualityComparer.Instance);
    private int AppliedCandidateApprovalCountValue;
    private ActiveOperation? Operation;
    private Task? SessionCleanupTask;
    private Task? DisposalTask;
    private long GenerationValue;
    private long RevisionValue;
    private bool SessionHasFaulted;
    private bool DisposeStarted;
    internal Action? BeforeResultCommitForTesting { get; set; }
    internal Action? BeforeSessionFaultCommitForTesting { get; set; }

    public PlanReviewController(IPlanInspectionSession session)
    {
        this.Session = session ?? throw new ArgumentNullException(nameof(session));
        this.VerifiedRelease = ProjectVerifiedRelease(session.Release);
        this.SessionFaultNotification = session.SessionFaulted
            ?? throw new InvalidOperationException("The plan-review session had no fault notification.");
        this.SessionWatcher = this.WatchSessionAsync();
    }

    public event EventHandler? Changed;

    public PlanReviewRelease VerifiedRelease { get; }

    public PlanReviewSnapshot Snapshot
    {
        get
        {
            lock (this.Sync)
                return this.CreateSnapshot();
        }
    }

    public void SelectOperation(InstallerOperation operation)
    {
        AssertAllowedOperation(operation);
        lock (this.Sync)
        {
            this.AssertCanUseSession();
            if (!this.CanSelectUnderLock())
                throw new InvalidOperationException("The plan-review session cannot select an operation now.");
            if (this.SelectedOperationValue == operation)
                return;
            bool hadResult = this.ResultValue is not null;
            this.SelectedOperationValue = operation;
            this.ResultValue = null;
            this.RevokeCandidateAuthorityUnderLock(clearAppliedApprovals: true);
            this.StateValue = hadResult ? PlanReviewState.SelectionChanged : PlanReviewState.Choosing;
        }
        this.PublishChanged();
    }

    public Task InspectAsync(CancellationToken cancellationToken = default)
    {
        return this.StartFreshInspection(requireAppliedApprovals: false, cancellationToken);
    }

    /// <summary>
    /// Revoke the current preview and request a new initial plan. This does not undo or deselect an existing
    /// backend plan; Protocol V1 candidate approval is additive within each exact plan generation.
    /// </summary>
    public Task StartFreshInspectionAsync()
        => this.StartFreshInspectionAsync(CancellationToken.None);

    public Task StartFreshInspectionAsync(CancellationToken cancellationToken)
    {
        return this.StartFreshInspection(requireAppliedApprovals: true, cancellationToken);
    }

    public void SetCandidateSelection(IReadOnlyList<PlanReviewCandidate> candidates)
    {
        PlanReviewCandidate[] requested = SnapshotCandidateChoices(candidates, nameof(candidates));
        bool changed;
        lock (this.Sync)
        {
            this.AssertCanUseSession();
            if (!this.CanSelectCandidatesUnderLock())
                throw new InvalidOperationException("The current plan has no selectable candidate authority.");
            HashSet<PlanReviewCandidate> unique = new(ReferenceEqualityComparer.Instance);
            foreach (PlanReviewCandidate candidate in requested)
            {
                if (!unique.Add(candidate))
                    throw new ArgumentException("The candidate selection contains a duplicate choice.", nameof(candidates));
                if (!this.CurrentCandidateAuthorities.ContainsKey(candidate))
                    throw new ArgumentException("Every candidate must be an exact current choice issued by this plan.", nameof(candidates));
            }
            changed = !this.SelectedCandidates.SetEquals(unique);
            if (changed)
            {
                this.SelectedCandidates.Clear();
                this.SelectedCandidates.UnionWith(unique);
            }
        }
        if (changed)
            this.PublishChanged();
    }

    public void ClearCandidateSelection()
    {
        bool changed;
        lock (this.Sync)
        {
            this.AssertCanUseSession();
            if (!this.CanSelectCandidatesUnderLock())
                throw new InvalidOperationException("The current plan has no selectable candidate authority.");
            changed = this.SelectedCandidates.Count > 0;
            this.SelectedCandidates.Clear();
        }
        if (changed)
            this.PublishChanged();
    }

    public Task ApplyCandidateSelectionAsync()
        => this.ApplyCandidateSelectionAsync(CancellationToken.None);

    public Task ApplyCandidateSelectionAsync(CancellationToken cancellationToken)
    {
        ActiveOperation operation;
        lock (this.Sync)
        {
            this.AssertCanUseSession();
            if (!this.CanSelectCandidatesUnderLock() || this.SelectedOperationValue is not { } selected)
                throw new InvalidOperationException("Inspect a supported candidate plan before applying a selection.");
            if (selected == InstallerOperation.Backup)
                throw new InvalidOperationException("Backup candidates cannot be approved.");
            if (this.SelectedCandidates.Count == 0)
                throw new InvalidOperationException("Select at least one exact current candidate before applying.");

            PlanReviewCandidate[] selectedChoices = this.CurrentCandidateAuthorities.Keys
                .Where(this.SelectedCandidates.Contains)
                .ToArray();
            if (selectedChoices.Length != this.SelectedCandidates.Count)
                throw new InvalidOperationException("The candidate selection is stale and requires a fresh inspection.");
            InstallerReadOnlyPlanCandidate[] backendCandidates = selectedChoices
                .Select(candidate => this.CurrentCandidateAuthorities[candidate])
                .ToArray();
            if (backendCandidates.Length > ProtocolJsonSerializer.MaxPlanCandidates - this.AppliedCandidateApprovalCountValue)
                throw new InvalidOperationException("The bounded candidate-approval history is full; start a fresh inspection.");
            operation = new(
                ++this.GenerationValue,
                selected,
                PlanRequestKind.CandidateApproval,
                backendCandidates,
                checked(this.AppliedCandidateApprovalCountValue + backendCandidates.Length),
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            );
            this.Operation = operation;
            this.ResultValue = null;
            this.RevokeCandidateAuthorityUnderLock(clearAppliedApprovals: false);
            this.StateValue = PlanReviewState.Approving;
        }
        this.PublishChanged();
        _ = this.RunPlanRequestAsync(operation);
        return operation.Completion.Task;
    }

    public async Task CancelAsync()
    {
        ActiveOperation? operation;
        bool publish;
        lock (this.Sync)
        {
            this.AssertNotDisposed();
            operation = this.Operation;
            if (operation is null)
                return;
            publish = !this.SessionHasFaulted && !this.SessionFaultNotification.IsCompleted;
            this.ResultValue = null;
            this.RevokeCandidateAuthorityUnderLock(clearAppliedApprovals: true);
            if (publish)
                this.StateValue = PlanReviewState.Cancelling;
        }
        if (publish)
            this.PublishChanged();
        await CancelSafelyAsync(operation.Cancellation).ConfigureAwait(false);
        await operation.Completion.Task.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (this.Sync)
        {
            if (this.DisposalTask is not null)
                return new ValueTask(this.DisposalTask);
            this.DisposeStarted = true;
            ActiveOperation? operation = this.Operation;
            this.ResultValue = null;
            this.RevokeCandidateAuthorityUnderLock(clearAppliedApprovals: true);
            this.StateValue = operation is null ? PlanReviewState.Closing : PlanReviewState.Cancelling;
            this.DisposalTask = this.DisposeCoreAsync(operation);
            return new ValueTask(this.DisposalTask);
        }
    }

    private Task StartFreshInspection(bool requireAppliedApprovals, CancellationToken cancellationToken)
    {
        ActiveOperation operation;
        lock (this.Sync)
        {
            this.AssertCanUseSession();
            if (!this.CanSelectUnderLock() || this.SelectedOperationValue is not { } selected)
                throw new InvalidOperationException("Select one supported operation before inspecting a plan.");
            if (requireAppliedApprovals && this.AppliedCandidateApprovalCountValue == 0)
                throw new InvalidOperationException("A fresh initial plan is only needed after candidate approvals were applied.");
            operation = new(
                ++this.GenerationValue,
                selected,
                PlanRequestKind.FreshInspection,
                [],
                0,
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            );
            this.Operation = operation;
            this.ResultValue = null;
            this.RevokeCandidateAuthorityUnderLock(clearAppliedApprovals: true);
            this.StateValue = PlanReviewState.Inspecting;
        }
        this.PublishChanged();
        _ = this.RunPlanRequestAsync(operation);
        return operation.Completion.Task;
    }

    private async Task RunPlanRequestAsync(ActiveOperation operation)
    {
        await Task.Yield();
        PlanReviewResult? projected = null;
        Dictionary<PlanReviewCandidate, InstallerReadOnlyPlanCandidate>? projectedAuthorities = null;
        Exception? failure = null;
        bool cancelled = false;
        try
        {
            InstallerReadOnlyPlanResult result = operation.Kind switch
            {
                PlanRequestKind.FreshInspection => await this.Session.InspectPlanAsync(
                    operation.SelectedOperation,
                    operation.Cancellation.Token
                ).ConfigureAwait(false),
                PlanRequestKind.CandidateApproval => await this.Session.ApprovePlanCandidatesAsync(
                    operation.BackendCandidates,
                    operation.Cancellation.Token
                ).ConfigureAwait(false),
                _ => throw new InvalidOperationException("The plan request kind is unsupported.")
            };
            operation.Cancellation.Token.ThrowIfCancellationRequested();
            ProjectedPlanResult projection = this.ProjectResult(result, operation.SelectedOperation, operation.Kind);
            projected = projection.Result;
            projectedAuthorities = projection.CandidateAuthorities;
            this.BeforeResultCommitForTesting?.Invoke();
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        if (projected is PlanReviewPlan && projectedAuthorities is null)
        {
            failure = new InvalidOperationException("The projected candidate authority was missing.");
            projected = null;
        }

        PlanReviewState finalState;
        PlanReviewResult? finalResult = null;
        bool requiresCleanup;
        bool publishClosing;
        lock (this.Sync)
        {
            if (!ReferenceEquals(this.Operation, operation))
                return;
            if (this.DisposeStarted)
            {
                finalState = PlanReviewState.Disposed;
                requiresCleanup = true;
                publishClosing = false;
            }
            else if (this.SessionHasFaulted || this.SessionFaultNotification.IsCompleted)
            {
                finalState = PlanReviewState.SessionFaulted;
                requiresCleanup = true;
                publishClosing = this.StateValue != PlanReviewState.Closing;
                this.StateValue = PlanReviewState.Closing;
            }
            else if (cancelled || operation.Cancellation.IsCancellationRequested)
            {
                finalState = PlanReviewState.Cancelled;
                requiresCleanup = true;
                publishClosing = this.StateValue != PlanReviewState.Cancelling;
                this.StateValue = PlanReviewState.Cancelling;
            }
            else if (failure is not null || projected is null)
            {
                finalState = PlanReviewState.Failed;
                requiresCleanup = true;
                publishClosing = this.StateValue != PlanReviewState.Closing;
                this.StateValue = PlanReviewState.Closing;
            }
            else if (projected is PlanReviewRejection rejection && IsWorkflowTerminal(rejection))
            {
                finalState = PlanReviewState.Rejected;
                finalResult = rejection;
                requiresCleanup = true;
                publishClosing = this.StateValue != PlanReviewState.Closing;
                this.StateValue = PlanReviewState.Closing;
            }
            else
            {
                finalState = projected is PlanReviewRejection ? PlanReviewState.Rejected : PlanReviewState.Available;
                finalResult = projected;
                requiresCleanup = false;
                publishClosing = false;
            }

            if (requiresCleanup)
            {
                this.ResultValue = null;
                this.RevokeCandidateAuthorityUnderLock(clearAppliedApprovals: true);
            }
            else
            {
                this.Operation = null;
                this.StateValue = finalState;
                this.ResultValue = finalResult;
                if (finalResult is PlanReviewPlan)
                {
                    this.CurrentCandidateAuthorities = projectedAuthorities!;
                    this.SelectedCandidates.Clear();
                    this.AppliedCandidateApprovalCountValue = operation.AppliedCandidateApprovalCountAfterSuccess;
                }
                else
                    this.RevokeCandidateAuthorityUnderLock(clearAppliedApprovals: true);
                operation.Cancellation.Dispose();
            }
        }
        if (publishClosing)
            this.PublishChanged();
        if (!requiresCleanup)
        {
            this.PublishChanged();
            operation.Completion.TrySetResult();
            return;
        }

        Task cleanup;
        lock (this.Sync)
            cleanup = this.StartSessionCleanupUnderLock();
        await cleanup.ConfigureAwait(false);

        lock (this.Sync)
        {
            if (ReferenceEquals(this.Operation, operation))
                this.Operation = null;
            this.ResultValue = null;
            this.RevokeCandidateAuthorityUnderLock(clearAppliedApprovals: true);
            this.StateValue = this.DisposeStarted
                ? PlanReviewState.Disposed
                : this.SessionHasFaulted || this.SessionFaultNotification.IsCompleted
                    ? PlanReviewState.SessionFaulted
                    : cancelled || operation.Cancellation.IsCancellationRequested
                        ? PlanReviewState.Cancelled
                        : finalState;
            if (this.StateValue == PlanReviewState.Rejected)
                this.ResultValue = finalResult;
            operation.Cancellation.Dispose();
        }
        this.PublishChanged();
        operation.Completion.TrySetResult();
    }

    private async Task WatchSessionAsync()
    {
        Task completed = await Task.WhenAny(this.SessionFaultNotification, this.StopWatching.Task).ConfigureAwait(false);
        if (completed == this.StopWatching.Task)
            return;
        try
        {
            _ = await this.SessionFaultNotification.ConfigureAwait(false);
        }
        catch
        {
            // A broken fault-notification task is still a terminal generic session fault.
        }
        this.BeforeSessionFaultCommitForTesting?.Invoke();

        ActiveOperation? operation;
        lock (this.Sync)
        {
            if (this.DisposeStarted)
                return;
            this.SessionHasFaulted = true;
            this.ResultValue = null;
            this.RevokeCandidateAuthorityUnderLock(clearAppliedApprovals: true);
            operation = this.Operation;
            this.StateValue = PlanReviewState.Closing;
        }
        if (operation is not null)
        {
            this.PublishChanged();
            await CancelSafelyAsync(operation.Cancellation).ConfigureAwait(false);
            return;
        }

        this.PublishChanged();
        Task cleanup;
        lock (this.Sync)
            cleanup = this.StartSessionCleanupUnderLock();
        await cleanup.ConfigureAwait(false);
        lock (this.Sync)
        {
            if (!this.DisposeStarted)
                this.StateValue = PlanReviewState.SessionFaulted;
        }
        this.PublishChanged();
    }

    private async Task DisposeCoreAsync(ActiveOperation? operation)
    {
        await Task.Yield();
        if (operation is not null)
        {
            await CancelSafelyAsync(operation.Cancellation).ConfigureAwait(false);
            await operation.Completion.Task.ConfigureAwait(false);
        }
        this.StopWatching.TrySetResult();
        Task cleanup;
        lock (this.Sync)
            cleanup = this.StartSessionCleanupUnderLock();
        await cleanup.ConfigureAwait(false);
        await this.SessionWatcher.ConfigureAwait(false);
        lock (this.Sync)
        {
            this.ResultValue = null;
            this.RevokeCandidateAuthorityUnderLock(clearAppliedApprovals: true);
            this.StateValue = PlanReviewState.Disposed;
        }
        this.PublishChanged();
    }

    private Task StartSessionCleanupUnderLock()
        => this.SessionCleanupTask ??= DisposeSessionAsync(this.Session);

    private PlanReviewSnapshot CreateSnapshot()
    {
        bool canSelect = this.CanSelectUnderLock();
        bool canSelectCandidates = this.CanSelectCandidatesUnderLock();
        bool canRetry = this.StateValue == PlanReviewState.Rejected
            && this.ResultValue is PlanReviewRejection rejection
            && !IsWorkflowTerminal(rejection);
        PlanReviewCandidate[] candidates = this.ResultValue is PlanReviewPlan plan
            ? plan.Candidates.ToArray()
            : [];
        PlanReviewCandidate[] selectedCandidates = candidates
            .Where(this.SelectedCandidates.Contains)
            .ToArray();
        return new(
            this.GenerationValue,
            this.RevisionValue,
            this.StateValue,
            this.SelectedOperationValue,
            this.ResultValue,
            canSelect,
            canSelect && this.SelectedOperationValue is not null,
            this.Operation is not null && this.StateValue is PlanReviewState.Inspecting or PlanReviewState.Approving,
            canRetry,
            this.StateValue is PlanReviewState.Cancelled or PlanReviewState.Failed or PlanReviewState.SessionFaulted
                || this.StateValue == PlanReviewState.Rejected && this.ResultValue is PlanReviewRejection terminal && IsWorkflowTerminal(terminal)
        )
        {
            Candidates = Array.AsReadOnly(candidates),
            SelectedCandidates = Array.AsReadOnly(selectedCandidates),
            AppliedCandidateApprovalCount = this.AppliedCandidateApprovalCountValue,
            HasAppliedCandidateApprovals = this.AppliedCandidateApprovalCountValue > 0,
            CanSelectCandidates = canSelectCandidates,
            CanApplyCandidates = canSelectCandidates && selectedCandidates.Length > 0,
            CanClearCandidates = canSelectCandidates && selectedCandidates.Length > 0,
            CanStartFreshInspection = canSelect && this.AppliedCandidateApprovalCountValue > 0
        };
    }

    private bool CanSelectUnderLock()
        => !this.DisposeStarted
            && !this.SessionHasFaulted
            && this.Operation is null
            && this.StateValue is PlanReviewState.Choosing
                or PlanReviewState.SelectionChanged
                or PlanReviewState.Available
                or PlanReviewState.Rejected
            && !(this.ResultValue is PlanReviewRejection rejection && IsWorkflowTerminal(rejection));

    private bool CanSelectCandidatesUnderLock()
        => this.CanSelectUnderLock()
            && this.StateValue == PlanReviewState.Available
            && this.SelectedOperationValue is InstallerOperation.Install
                or InstallerOperation.Update
                or InstallerOperation.Repair
                or InstallerOperation.Uninstall
            && this.ResultValue is PlanReviewPlan { Candidates.Count: > 0 };

    private void AssertCanUseSession()
    {
        this.AssertNotDisposed();
        if (this.SessionHasFaulted || this.SessionFaultNotification.IsCompleted)
            throw new InvalidOperationException("The plan-review session is no longer available.");
    }

    private void AssertNotDisposed()
    {
        if (this.DisposeStarted)
            throw new ObjectDisposedException(nameof(PlanReviewController));
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
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch
            {
                // Presentation observers cannot affect backend lifetime or state.
            }
        }
    }

    private ProjectedPlanResult ProjectResult(
        InstallerReadOnlyPlanResult result,
        InstallerOperation requested,
        PlanRequestKind requestKind
    )
    {
        ArgumentNullException.ThrowIfNull(result);
        return result switch
        {
            InstallerReadOnlyPlanSuccess success => this.ProjectPlan(success, requested),
            InstallerReadOnlyPlanRejection rejection => new(ProjectRejection(rejection, requestKind), []),
            _ => throw new InvalidOperationException("The plan service returned an unsupported result.")
        };
    }

    private ProjectedPlanResult ProjectPlan(InstallerReadOnlyPlanSuccess plan, InstallerOperation requested)
    {
        if (plan.Operation != requested)
            throw new InvalidOperationException("The plan operation did not match the request.");
        AssertAllowedOperation(plan.Operation);
        if (!Enum.IsDefined(plan.ObservedState)
            || plan.RecommendedDefault != ProtocolRecommendedDefault.Cancel
            || !plan.SeparateConfirmationRequired
            || plan.AdditionalNoticeCount is < 0 or > MaximumNoticeCount)
        {
            throw new InvalidOperationException("The plan summary contained unsafe semantics.");
        }

        PlanReviewRelease? current = ProjectPlanRelease(plan.CurrentRelease);
        PlanReviewRelease? target = ProjectPlanRelease(plan.TargetRelease);
        ValidateReleaseSemantics(plan.Operation, plan.ObservedState, current, target, this.VerifiedRelease);

        ArgumentNullException.ThrowIfNull(plan.Risks);
        ArgumentNullException.ThrowIfNull(plan.OperationCounts);
        ArgumentNullException.ThrowIfNull(plan.ConflictCounts);
        ArgumentNullException.ThrowIfNull(plan.CandidateCounts);
        if (plan.Risks.Count > Enum.GetValues<ProtocolPlanRisk>().Length
            || plan.OperationCounts.Count > Enum.GetValues<PlanOperationKind>().Length
            || plan.ConflictCounts.Count > Enum.GetValues<PlanConflictCode>().Length
            || plan.CandidateCounts.Count > Enum.GetValues<FileReplacementCandidateReason>().Length * Enum.GetValues<FileReplacementCandidateDisposition>().Length * 2)
        {
            throw new InvalidOperationException("The plan summary exceeded its group bounds.");
        }

        ProtocolPlanRisk[] risks = plan.Risks.ToArray();
        if (risks.Any(risk => !Enum.IsDefined(risk) || risk is ProtocolPlanRisk.Rollback or ProtocolPlanRisk.RecoveryPrune)
            || risks.Distinct().Count() != risks.Length)
        {
            throw new InvalidOperationException("The plan risk summary was invalid.");
        }

        PlanReviewOperationCount[] operations = plan.OperationCounts.Select(item =>
        {
            if (item is null || !Enum.IsDefined(item.Kind) || item.Count <= 0)
                throw new InvalidOperationException("The plan operation summary was invalid.");
            return new PlanReviewOperationCount(item.Kind, item.Count);
        }).ToArray();
        PlanReviewConflictCount[] conflicts = plan.ConflictCounts.Select(item =>
        {
            if (item is null || !Enum.IsDefined(item.Code) || item.Count <= 0)
                throw new InvalidOperationException("The plan conflict summary was invalid.");
            return new PlanReviewConflictCount(item.Code, item.Count);
        }).ToArray();
        PlanReviewCandidateCount[] candidates = plan.CandidateCounts.Select(item =>
        {
            if (item is null || !IsValidCandidatePair(item.Reason, item.Disposition) || item.Count <= 0)
                throw new InvalidOperationException("The plan candidate summary was invalid.");
            return new PlanReviewCandidateCount(item.Reason, item.Disposition, item.Selected, item.Count);
        }).ToArray();

        if (operations.Select(item => item.Kind).Distinct().Count() != operations.Length
            || conflicts.Select(item => item.Code).Distinct().Count() != conflicts.Length
            || candidates.Select(item => (item.Reason, item.Disposition, item.ProvisionallyIncluded)).Distinct().Count() != candidates.Length)
        {
            throw new InvalidOperationException("The plan summary contained duplicate groups.");
        }
        int operationTotal = operations.Aggregate(0, (sum, item) => checked(sum + item.Count));
        int conflictTotal = conflicts.Aggregate(0, (sum, item) => checked(sum + item.Count));
        int candidateTotal = candidates.Aggregate(0, (sum, item) => checked(sum + item.Count));
        if (operationTotal > MaximumOperationCount
            || conflictTotal > MaximumConflictCount
            || candidateTotal > ProtocolJsonSerializer.MaxPlanCandidates
            || plan.HasBlockingConflicts != (conflictTotal > 0))
        {
            throw new InvalidOperationException("The plan summary counts were invalid.");
        }
        ValidateRisks(plan.Operation, current, target, candidateTotal, risks);

        (PlanReviewCandidate[] detailedCandidates, Dictionary<PlanReviewCandidate, InstallerReadOnlyPlanCandidate> authorities) = ProjectCandidates(plan.Candidates);
        PlanReviewCandidateCount[] detailedCounts = detailedCandidates
            .GroupBy(candidate => (candidate.Reason, candidate.Disposition, candidate.BackendProvisionallyIncluded))
            .OrderBy(group => group.Key.Reason)
            .ThenBy(group => group.Key.Disposition)
            .ThenBy(group => group.Key.BackendProvisionallyIncluded)
            .Select(group => new PlanReviewCandidateCount(
                group.Key.Reason,
                group.Key.Disposition,
                group.Key.BackendProvisionallyIncluded,
                group.Count()
            ))
            .ToArray();
        PlanReviewCandidateCount[] aggregateCounts = candidates
            .OrderBy(item => item.Reason)
            .ThenBy(item => item.Disposition)
            .ThenBy(item => item.ProvisionallyIncluded)
            .ToArray();
        if (!detailedCounts.SequenceEqual(aggregateCounts))
            throw new InvalidOperationException("The plan candidate detail did not match its aggregate summary.");

        PlanReviewPlan result = new(
            plan.Operation,
            plan.ObservedState,
            current,
            target,
            plan.HasBlockingConflicts,
            Array.AsReadOnly(risks),
            plan.RecommendedDefault,
            plan.SeparateConfirmationRequired,
            Array.AsReadOnly(operations),
            Array.AsReadOnly(conflicts),
            Array.AsReadOnly(candidates),
            plan.AdditionalNoticeCount
        )
        {
            Candidates = Array.AsReadOnly(detailedCandidates)
        };
        return new(result, authorities);
    }

    private static (
        PlanReviewCandidate[] Candidates,
        Dictionary<PlanReviewCandidate, InstallerReadOnlyPlanCandidate> Authorities
    ) ProjectCandidates(IReadOnlyList<InstallerReadOnlyPlanCandidate>? source)
    {
        if (source is null)
            throw new InvalidOperationException("The plan candidate detail was missing.");
        int count;
        try { count = source.Count; }
        catch
        {
            throw new InvalidOperationException("The plan candidate detail could not be read safely.");
        }
        if (count is < 0 or > ProtocolJsonSerializer.MaxPlanCandidates)
            throw new InvalidOperationException("The plan candidate detail exceeded its bound.");

        PlanReviewCandidate[] candidates = new PlanReviewCandidate[count];
        Dictionary<PlanReviewCandidate, InstallerReadOnlyPlanCandidate> authorities = new(ReferenceEqualityComparer.Instance);
        HashSet<InstallerReadOnlyPlanCandidate> backendReferences = new(ReferenceEqualityComparer.Instance);
        HashSet<string> displayPaths = new(StringComparer.Ordinal);
        for (int index = 0; index < count; index++)
        {
            InstallerReadOnlyPlanCandidate backend;
            try { backend = source[index]; }
            catch
            {
                throw new InvalidOperationException("The plan candidate detail could not be read safely.");
            }
            if (backend is null
                || !backendReferences.Add(backend)
                || string.IsNullOrEmpty(backend.DisplayPath)
                || backend.DisplayPath.Length > MaximumEscapedCandidatePathLength
                || !string.Equals(backend.DisplayPath, InstallerDisplayText.Escape(backend.DisplayPath), StringComparison.Ordinal)
                || !displayPaths.Add(backend.DisplayPath)
                || !IsValidCandidatePair(backend.Reason, backend.Disposition))
            {
                throw new InvalidOperationException("The plan candidate detail was invalid.");
            }
            PlanReviewCandidate candidate = new(
                backend.DisplayPath,
                backend.Reason,
                backend.Disposition,
                backend.BackendProvisionallyIncluded
            );
            candidates[index] = candidate;
            authorities.Add(candidate, backend);
        }
        return (candidates, authorities);
    }

    /// <remarks>The caller must hold <see cref="Sync"/>.</remarks>
    private void RevokeCandidateAuthorityUnderLock(bool clearAppliedApprovals)
    {
        this.CurrentCandidateAuthorities.Clear();
        this.SelectedCandidates.Clear();
        if (clearAppliedApprovals)
            this.AppliedCandidateApprovalCountValue = 0;
    }

    private static PlanReviewCandidate[] SnapshotCandidateChoices(
        IReadOnlyList<PlanReviewCandidate> candidates,
        string parameterName
    )
    {
        ArgumentNullException.ThrowIfNull(candidates, parameterName);
        int count;
        try { count = candidates.Count; }
        catch
        {
            throw new ArgumentException("The candidate selection could not be read safely.", parameterName);
        }
        if (count is < 0 or > ProtocolJsonSerializer.MaxPlanCandidates)
            throw new ArgumentException("The candidate selection exceeded its bound.", parameterName);
        PlanReviewCandidate[] result = new PlanReviewCandidate[count];
        for (int index = 0; index < count; index++)
        {
            try { result[index] = candidates[index]; }
            catch
            {
                throw new ArgumentException("The candidate selection could not be read safely.", parameterName);
            }
            if (result[index] is null)
                throw new ArgumentException("The candidate selection contained an invalid choice.", parameterName);
        }
        return result;
    }

    private static PlanReviewRejection ProjectRejection(
        InstallerReadOnlyPlanRejection rejection,
        PlanRequestKind requestKind
    )
    {
        bool valid = requestKind == PlanRequestKind.CandidateApproval
            ? rejection.ErrorCode == ProtocolPrePlanErrorCode.CandidateApprovalFailed
                && rejection.NextAction == ProtocolNextAction.InspectAgain
                && !rejection.IsTerminal
            : rejection.ErrorCode switch
        {
            ProtocolPrePlanErrorCode.RequestCancelled => rejection.NextAction == ProtocolNextAction.RetryRequest && !rejection.IsTerminal,
            ProtocolPrePlanErrorCode.InvalidGameFolder => rejection.NextAction == ProtocolNextAction.SelectGameFolder && !rejection.IsTerminal,
            ProtocolPrePlanErrorCode.PackageRejected => rejection.NextAction == ProtocolNextAction.ReopenVerifiedPackage && !rejection.IsTerminal,
            ProtocolPrePlanErrorCode.InspectionFailed => rejection.NextAction == ProtocolNextAction.InspectAgain && !rejection.IsTerminal,
            ProtocolPrePlanErrorCode.PermissionDenied => rejection.NextAction == ProtocolNextAction.ReviewFilesystem && !rejection.IsTerminal,
            ProtocolPrePlanErrorCode.UnexpectedFailure => rejection.IsTerminal
                && rejection.NextAction is ProtocolNextAction.StartNewSession or ProtocolNextAction.ViewPrivateLog,
            _ => false
        };
        if (!valid)
            throw new InvalidOperationException("The plan rejection semantics were invalid.");
        return new(rejection.ErrorCode, rejection.NextAction, rejection.IsTerminal);
    }

    private static PlanReviewRelease ProjectVerifiedRelease(ProtocolReleaseIdentity release)
    {
        ArgumentNullException.ThrowIfNull(release);
        ForkReleaseIdentity identity = ForkReleaseIdentity.Parse(release.Tag);
        if (!string.Equals(release.Repository, ForkReleaseIdentity.RepositoryUrl, StringComparison.Ordinal)
            || !string.Equals(release.EmbeddedVersion, identity.EmbeddedVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The verified release presentation was invalid.");
        }
        return new(identity.Tag, identity.EmbeddedVersion);
    }

    private static PlanReviewRelease? ProjectPlanRelease(InstallerPlanRelease? release)
    {
        if (release is null)
            return null;
        ForkReleaseIdentity identity = ForkReleaseIdentity.Parse(release.Tag);
        if (!string.Equals(release.EmbeddedVersion, identity.EmbeddedVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("The plan release presentation was invalid.");
        return new(identity.Tag, identity.EmbeddedVersion);
    }

    private static void ValidateReleaseSemantics(
        InstallerOperation operation,
        ObservedInstallState observed,
        PlanReviewRelease? current,
        PlanReviewRelease? target,
        PlanReviewRelease verified
    )
    {
        bool receiptKnown = observed is ObservedInstallState.KnownUnmodified or ObservedInstallState.KnownModified;
        if (receiptKnown != (current is not null))
            throw new InvalidOperationException("The observed state and current release were inconsistent.");
        bool valid = operation switch
        {
            InstallerOperation.Install => current is null && target == verified,
            InstallerOperation.Update or InstallerOperation.Repair => target == verified,
            InstallerOperation.Uninstall => target is null,
            InstallerOperation.Backup => current is null && target is null || current is not null && target == current,
            _ => false
        };
        if (!valid)
            throw new InvalidOperationException("The plan release semantics were inconsistent.");
    }

    private static void ValidateRisks(
        InstallerOperation operation,
        PlanReviewRelease? current,
        PlanReviewRelease? target,
        int candidateCount,
        IReadOnlyList<ProtocolPlanRisk> actual
    )
    {
        List<ProtocolPlanRisk> expected = [];
        if (operation == InstallerOperation.Uninstall)
            expected.Add(ProtocolPlanRisk.Uninstall);
        if (current is not null && target is not null && IsEarlierRelease(target.Tag, current.Tag))
            expected.Add(ProtocolPlanRisk.Downgrade);
        if (candidateCount > 0)
            expected.Add(ProtocolPlanRisk.ModifiedOrUnknownFileApproval);
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException("The plan risk semantics were inconsistent.");
    }

    private static bool IsEarlierRelease(string candidateTag, string currentTag)
    {
        ForkReleaseIdentity candidate = ForkReleaseIdentity.Parse(candidateTag);
        ForkReleaseIdentity current = ForkReleaseIdentity.Parse(currentTag);
        Version candidateVersion = Version.Parse(candidate.Version);
        Version currentVersion = Version.Parse(current.Version);
        int comparison = candidateVersion.CompareTo(currentVersion);
        return comparison < 0 || comparison == 0 && candidate.AlphaSequence < current.AlphaSequence;
    }

    private static bool IsValidCandidatePair(
        FileReplacementCandidateReason reason,
        FileReplacementCandidateDisposition disposition
    ) => reason switch
    {
        FileReplacementCandidateReason.ModifiedReceiptOwned => disposition is FileReplacementCandidateDisposition.Replace or FileReplacementCandidateDisposition.Remove,
        FileReplacementCandidateReason.ModifiedInstalledLauncher => disposition is FileReplacementCandidateDisposition.Replace or FileReplacementCandidateDisposition.Restore,
        FileReplacementCandidateReason.LegacyInstaller
            or FileReplacementCandidateReason.UnknownCollision
            or FileReplacementCandidateReason.OfficialOrLegacyLauncher => disposition == FileReplacementCandidateDisposition.Replace,
        FileReplacementCandidateReason.OfficialLauncherBackup => disposition == FileReplacementCandidateDisposition.TrustRetained,
        _ => false
    };

    private static bool IsWorkflowTerminal(PlanReviewRejection rejection)
        => rejection.IsTerminal
            || rejection.NextAction is ProtocolNextAction.SelectGameFolder
                or ProtocolNextAction.ReopenVerifiedPackage
                or ProtocolNextAction.StartNewSession
                or ProtocolNextAction.ViewPrivateLog;

    private static void AssertAllowedOperation(InstallerOperation operation)
    {
        if (!AllowedOperations.Contains(operation))
            throw new ArgumentOutOfRangeException(nameof(operation), "Only read-only review operations are supported.");
    }

    private static async Task CancelSafelyAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The active operation already settled.
        }
        catch
        {
            // Hostile dependency callbacks cannot prevent terminal cleanup.
        }
    }

    private static async Task DisposeSessionAsync(IPlanInspectionSession session)
    {
        await Task.Yield();
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Backend cleanup failures never escape into presentation state.
        }
    }

    private enum PlanRequestKind
    {
        FreshInspection,
        CandidateApproval
    }

    private sealed record ProjectedPlanResult(
        PlanReviewResult Result,
        Dictionary<PlanReviewCandidate, InstallerReadOnlyPlanCandidate> CandidateAuthorities
    );

    private sealed class ActiveOperation(
        long generation,
        InstallerOperation selectedOperation,
        PlanRequestKind kind,
        InstallerReadOnlyPlanCandidate[] backendCandidates,
        int appliedCandidateApprovalCountAfterSuccess,
        CancellationTokenSource cancellation
    )
    {
        public long Generation { get; } = generation;
        public InstallerOperation SelectedOperation { get; } = selectedOperation;
        public PlanRequestKind Kind { get; } = kind;
        public IReadOnlyList<InstallerReadOnlyPlanCandidate> BackendCandidates { get; } = Array.AsReadOnly(backendCandidates);
        public int AppliedCandidateApprovalCountAfterSuccess { get; } = appliedCandidateApprovalCountAfterSuccess;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
