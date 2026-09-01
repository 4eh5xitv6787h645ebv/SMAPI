using Avalonia.Automation;
using Avalonia.Threading;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Gui.Backend;
using StardewModdingAPI.Installer.Gui.Frontend;

namespace StardewModdingAPI.Installer.Gui.ViewModels;

internal enum PlanReviewFocusTarget
{
    OperationList,
    RecoveryList,
    RecoveryStatus,
    InspectRollback,
    CandidateList,
    CandidateStatus,
    Status,
    Result,
    Error,
    Confirm,
    Retry,
    Exit
}

/// <summary>
/// Sanitized display-only recovery choice. The controller-minted point remains internal and is never exposed to
/// XAML, automation peers, or public presentation properties.
/// </summary>
internal sealed class PlanReviewRecoveryChoice
{
    public PlanReviewRecoveryChoice(PlanReviewRecoveryPoint point, string title, string detail)
    {
        this.Point = point ?? throw new ArgumentNullException(nameof(point));
        this.Title = title ?? throw new ArgumentNullException(nameof(title));
        this.Detail = detail ?? throw new ArgumentNullException(nameof(detail));
        this.AccessibleName = $"{this.Title}. {this.Detail.Replace('\n', ' ')}";
    }

    internal PlanReviewRecoveryPoint Point { get; }

    public string Title { get; }

    public string Detail { get; }

    public string AccessibleName { get; }
}

internal sealed class PlanReviewCandidateChoice : ObservableObject
{
    private readonly Action<PlanReviewCandidateChoice, bool> SelectionChanged;
    private bool isSelected;

    public PlanReviewCandidateChoice(
        PlanReviewCandidate candidate,
        string displayPath,
        string reasonDetail,
        string dispositionDetail,
        bool backendProvisionallyIncluded,
        Action<PlanReviewCandidateChoice, bool> selectionChanged
    )
    {
        this.Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        this.DisplayPath = displayPath ?? throw new ArgumentNullException(nameof(displayPath));
        this.ReasonDetail = reasonDetail ?? throw new ArgumentNullException(nameof(reasonDetail));
        this.DispositionDetail = dispositionDetail ?? throw new ArgumentNullException(nameof(dispositionDetail));
        this.BackendProvisionallyIncluded = backendProvisionallyIncluded;
        this.SelectionChanged = selectionChanged ?? throw new ArgumentNullException(nameof(selectionChanged));
    }

    internal PlanReviewCandidate Candidate { get; }

    public string DisplayPath { get; }

    public string ReasonDetail { get; }

    public string DispositionDetail { get; }

    public bool BackendProvisionallyIncluded { get; }

    public string ProvisionalDetail => this.BackendProvisionallyIncluded
        ? "The backend provisionally included this file. That is not your approval."
        : "The backend did not provisionally include this file. No choice is implied.";

    public string AccessibleName => $"{this.DisplayPath}. {this.ReasonDetail} {this.DispositionDetail} {this.ProvisionalDetail}";

    public bool IsSelected
    {
        get => this.isSelected;
        set
        {
            if (!this.SetProperty(ref this.isSelected, value))
                return;
            this.SelectionChanged(this, value);
        }
    }

    internal void SetSelectedFromSnapshot(bool value)
    {
        this.SetProperty(ref this.isSelected, value);
    }

    internal void Deactivate()
    {
        this.SetProperty(ref this.isSelected, false);
    }
}

internal sealed record PlanReviewOperationChoice(
    InstallerOperation Operation,
    string Label,
    string Summary
)
{
    public string AccessibleName => $"{this.Label}. {this.Summary}";
}

internal sealed record PlanReviewSummaryRow(
    string Label,
    string Detail,
    int Count
)
{
    public string CountText => this.Count == 1 ? "1 item" : $"{this.Count} items";

    public string AccessibleName => $"{this.Label}. {this.CountText}. {this.Detail}";
}

/// <summary>Presentation-only adapter for one bounded, game-bound plan-inspection session.</summary>
internal sealed class PlanReviewViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly IReadOnlyList<PlanReviewOperationChoice> FixedOperationChoices = Array.AsReadOnly<PlanReviewOperationChoice>(
    [
        new(
            InstallerOperation.Install,
            "Install",
            "Inspect adding the verified release when no managed fork installation is present."
        ),
        new(
            InstallerOperation.Update,
            "Update",
            "Inspect changing a receipt-authenticated fork installation to the verified release."
        ),
        new(
            InstallerOperation.Repair,
            "Repair",
            "Inspect restoring managed files for the verified release."
        ),
        new(
            InstallerOperation.Uninstall,
            "Uninstall",
            "Inspect removing receipt-owned SMAPI files and restoring the observed launcher where applicable."
        ),
        new(
            InstallerOperation.Backup,
            "Backup",
            "Inspect creating a checkpoint of a receipt-authenticated installation."
        )
    ]);

    private readonly PlanReviewController Controller;
    private PlanReviewSnapshot snapshot;
    private PlanReviewOperationChoice? selectedOperation;
    private string heading = "Choose a plan to inspect";
    private string message = "Select one operation. Nothing is inspected until you choose Inspect plan.";
    private string liveAnnouncement = "Choose a plan to inspect.";
    private string observedStateDetail = "No plan has been inspected.";
    private string currentReleaseDetail = "No inspected current release.";
    private string targetReleaseDetail = "No inspected target release.";
    private string safetyDetail = "No plan safety facts have been reported.";
    private string riskSummary = "No plan has been inspected.";
    private string operationSummary = "No plan has been inspected.";
    private string conflictSummary = "No plan has been inspected.";
    private string candidateSummary = "No plan has been inspected.";
    private string additionalNoticeDetail = "No plan has been inspected.";
    private IReadOnlyList<PlanReviewSummaryRow> riskRows = Array.Empty<PlanReviewSummaryRow>();
    private IReadOnlyList<PlanReviewSummaryRow> operationRows = Array.Empty<PlanReviewSummaryRow>();
    private IReadOnlyList<PlanReviewSummaryRow> conflictRows = Array.Empty<PlanReviewSummaryRow>();
    private IReadOnlyList<PlanReviewSummaryRow> candidateRows = Array.Empty<PlanReviewSummaryRow>();
    private IReadOnlyList<PlanReviewCandidateChoice> candidateChoices = Array.Empty<PlanReviewCandidateChoice>();
    private IReadOnlyList<PlanReviewRecoveryChoice> recoveryChoices = Array.Empty<PlanReviewRecoveryChoice>();
    private PlanReviewRecoveryChoice? selectedRecoveryChoice;
    private string candidateSelectionAnnouncement = "0 of 0 files selected.";
    private bool disposed;
    private bool confirmationReadyRaised;
    private bool transitionFailed;

    public PlanReviewViewModel(PlanReviewController controller)
    {
        this.Controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.snapshot = controller.Snapshot;
        this.InspectCommand = new(() => controller.InspectAsync(), () => this.snapshot.CanInspect, this.HandlePresentationFailure);
        this.LoadRecoveriesCommand = new(() => controller.ListRecoveriesAsync(), () => this.snapshot.CanListRecoveries, this.HandleRecoveryActionFailure);
        this.InspectRollbackCommand = new(() => controller.InspectRollbackAsync(), () => this.snapshot.CanInspectRollback, this.HandleRecoveryActionFailure);
        this.CancelCommand = new(controller.CancelAsync, () => this.snapshot.CanCancel, this.HandlePresentationFailure);
        this.RetryCommand = new(() => controller.InspectAsync(), () => this.snapshot.CanRetry, this.HandlePresentationFailure);
        this.ApplyCandidatesCommand = new(() => controller.ApplyCandidateSelectionAsync(), () => this.snapshot.CanApplyCandidates, this.HandleCandidateActionFailure);
        this.ClearCandidatesCommand = new(this.ClearCandidateSelectionSafely, () => this.snapshot.CanClearCandidates);
        this.StartFreshInspectionCommand = new(() => controller.StartFreshInspectionAsync(), () => this.snapshot.CanStartFreshInspection, this.HandleCandidateActionFailure);
        this.ConfirmCommand = new(() => controller.ConfirmAsync(), () => this.snapshot.CanConfirm, this.HandlePresentationFailure);
        this.ExitCommand = new(() => this.CloseRequested?.Invoke(this, EventArgs.Empty), () => this.snapshot.CanExit || this.transitionFailed);
        this.Controller.Changed += this.OnControllerChanged;
        this.ApplySnapshot(this.snapshot, requestFocus: false);
    }

    public event EventHandler<PlanReviewFocusTarget>? FocusRequested;

    public event EventHandler? CloseRequested;

    /// <summary>Signals that workflow code may privately take the confirmed owner from the controller.</summary>
    public event EventHandler? ConfirmationReady;

    public IReadOnlyList<PlanReviewOperationChoice> OperationChoices => FixedOperationChoices;

    public PlanReviewOperationChoice? SelectedOperation
    {
        get => this.selectedOperation;
        set
        {
            if (value is null || ReferenceEquals(value, this.selectedOperation))
                return;
            if (!FixedOperationChoices.Any(choice => ReferenceEquals(choice, value)))
                throw new ArgumentException("The operation must be one of this screen's exact read-only choices.", nameof(value));
            this.Controller.SelectOperation(value.Operation);
        }
    }

    public IReadOnlyList<PlanReviewRecoveryChoice> RecoveryChoices
    {
        get => this.recoveryChoices;
        private set => this.SetProperty(ref this.recoveryChoices, value);
    }

    public PlanReviewRecoveryChoice? SelectedRecoveryChoice
    {
        get => this.selectedRecoveryChoice;
        set
        {
            if (ReferenceEquals(value, this.selectedRecoveryChoice))
                return;
            if (value is not null && !this.RecoveryChoices.Any(choice => ReferenceEquals(choice, value)))
                throw new ArgumentException("The recovery point must be one of this screen's exact current choices.", nameof(value));
            this.Controller.SelectRecoveryPoint(value?.Point);
        }
    }

    public string Heading
    {
        get => this.heading;
        private set => this.SetProperty(ref this.heading, value);
    }

    public string Message
    {
        get => this.message;
        private set => this.SetProperty(ref this.message, value);
    }

    public string LiveAnnouncement
    {
        get => this.liveAnnouncement;
        private set => this.SetProperty(ref this.liveAnnouncement, value);
    }

    public string GameDetail => "Validated Stardew Valley game folder";

    public string GameAccessibleName => "Bound game folder";

    public string ReleaseDetail => $"Verified release: {this.Controller.VerifiedRelease.Tag}\nVersion: {this.Controller.VerifiedRelease.EmbeddedVersion}";

    public string ReleaseAccessibleName => $"Bound verified release: {this.Controller.VerifiedRelease.Tag}. Version: {this.Controller.VerifiedRelease.EmbeddedVersion}";

    public string DurableState => "Unchanged — no installer action has run";

    public string ObservedStateDetail
    {
        get => this.observedStateDetail;
        private set => this.SetProperty(ref this.observedStateDetail, value);
    }

    public string CurrentReleaseDetail
    {
        get => this.currentReleaseDetail;
        private set => this.SetProperty(ref this.currentReleaseDetail, value);
    }

    public string TargetReleaseDetail
    {
        get => this.targetReleaseDetail;
        private set => this.SetProperty(ref this.targetReleaseDetail, value);
    }

    public string SafetyDetail
    {
        get => this.safetyDetail;
        private set => this.SetProperty(ref this.safetyDetail, value);
    }

    public string RiskSummary
    {
        get => this.riskSummary;
        private set => this.SetProperty(ref this.riskSummary, value);
    }

    public string OperationSummary
    {
        get => this.operationSummary;
        private set => this.SetProperty(ref this.operationSummary, value);
    }

    public string ConflictSummary
    {
        get => this.conflictSummary;
        private set => this.SetProperty(ref this.conflictSummary, value);
    }

    public string CandidateSummary
    {
        get => this.candidateSummary;
        private set => this.SetProperty(ref this.candidateSummary, value);
    }

    public string AdditionalNoticeDetail
    {
        get => this.additionalNoticeDetail;
        private set => this.SetProperty(ref this.additionalNoticeDetail, value);
    }

    public IReadOnlyList<PlanReviewSummaryRow> RiskRows
    {
        get => this.riskRows;
        private set => this.SetProperty(ref this.riskRows, value);
    }

    public IReadOnlyList<PlanReviewSummaryRow> OperationRows
    {
        get => this.operationRows;
        private set => this.SetProperty(ref this.operationRows, value);
    }

    public IReadOnlyList<PlanReviewSummaryRow> ConflictRows
    {
        get => this.conflictRows;
        private set => this.SetProperty(ref this.conflictRows, value);
    }

    public IReadOnlyList<PlanReviewSummaryRow> CandidateRows
    {
        get => this.candidateRows;
        private set => this.SetProperty(ref this.candidateRows, value);
    }

    public IReadOnlyList<PlanReviewCandidateChoice> CandidateChoices
    {
        get => this.candidateChoices;
        private set => this.SetProperty(ref this.candidateChoices, value);
    }

    public string CandidateSelectionAnnouncement
    {
        get => this.candidateSelectionAnnouncement;
        private set => this.SetProperty(ref this.candidateSelectionAnnouncement, value);
    }

    public string CandidateReviewDetail
    {
        get
        {
            int applied = this.snapshot.AppliedCandidateApprovalCount;
            int remaining = this.CandidateChoices.Count;
            if (applied == 0)
                return "Every choice starts unchecked. Choose only files you want additively approved in a newly validated preview. Applying approvals does not confirm or run the plan, and no files change.";

            string appliedLabel = applied == 1 ? "approval is" : "approvals are";
            string remainingLabel = remaining == 1 ? "candidate remains" : "candidates remain";
            return $"{applied} additive file {appliedLabel} already applied and cannot be removed individually from this read-only preview. {remaining} {remainingLabel}. Accepted candidates no longer appear. Start a fresh inspection to revoke this preview and request a new initial plan. No files change, and this screen cannot confirm or execute a plan.";
        }
    }

    public bool IsCandidateApprovalCapacityFull
        => this.snapshot.AppliedCandidateApprovalCount >= ProtocolJsonSerializer.MaxPlanCandidates;

    public bool IsCandidateSelectionOverRemainingCapacity
        => this.snapshot.SelectedCandidates.Count > ProtocolJsonSerializer.MaxPlanCandidates - this.snapshot.AppliedCandidateApprovalCount;

    public bool IsCandidateCapacityDetailVisible
        => this.IsCandidateApprovalCapacityFull || this.IsCandidateSelectionOverRemainingCapacity;

    public string CandidateCapacityDetail
    {
        get
        {
            if (this.IsCandidateApprovalCapacityFull)
                return "This preview's bounded approval history is full, so no more candidate approvals fit in it. Clear local choices only unchecks this screen and does not free approval capacity. Start a fresh inspection to revoke this preview before approving another candidate. No files change, and this screen cannot confirm or execute a plan.";
            if (!this.IsCandidateSelectionOverRemainingCapacity)
                return "";

            int remaining = ProtocolJsonSerializer.MaxPlanCandidates - this.snapshot.AppliedCandidateApprovalCount;
            int selected = this.snapshot.SelectedCandidates.Count;
            string approvalLabel = remaining == 1 ? "approval fits" : "approvals fit";
            return $"Only {remaining} more {approvalLabel} in this preview, but {selected} files are selected. Apply is unavailable. Uncheck files or Clear local choices to reduce this selection, or start a fresh inspection to revoke this preview. No files change, and this screen cannot confirm or execute a plan.";
        }
    }

    public bool IsOperationSelectionEnabled => this.snapshot.CanSelect;

    public string RecoveryStatusDetail
    {
        get
        {
            return this.snapshot.RecoveryState switch
            {
                PlanReviewRecoveryState.NotLoaded => "Recovery history has not been loaded. Loading reads bounded local history only; it does not select, inspect, confirm, or run a rollback.",
                PlanReviewRecoveryState.Listing => "Loading bounded local recovery history. No recovery point is selected and no files are changing.",
                PlanReviewRecoveryState.Available when this.SelectedRecoveryChoice is { } selected => $"{selected.Title} is selected. Inspect rollback requests a read-only preview only; it does not confirm, run, or change files.",
                PlanReviewRecoveryState.Available => $"{this.RecoveryChoices.Count} {GetCountLabel(this.RecoveryChoices.Count, "recovery point is", "recovery points are")} available. Nothing is selected and no rollback has been inspected.",
                PlanReviewRecoveryState.NoHistory => "No committed recovery history was reported for this validated game folder. You can refresh this bounded local lookup.",
                PlanReviewRecoveryState.RelistRequired => "The previous recovery authority is no longer usable. Refresh history, then explicitly select a newly listed point before inspecting rollback again.",
                PlanReviewRecoveryState.Closed when this.snapshot.Result is PlanReviewPlan { Operation: InstallerOperation.Rollback } => "The exact selected rollback has been inspected. This is still a read-only preview; confirmation and a separate explicit Run are required before files can change.",
                PlanReviewRecoveryState.Closed when this.snapshot.State == PlanReviewState.Inspecting && this.snapshot.SelectedOperation is null => "Inspecting the exact selected recovery point as a read-only rollback plan. No files are changing.",
                PlanReviewRecoveryState.Closed => "Recovery history is closed for this read-only session. No recovery authority remains on this screen.",
                _ => throw new ArgumentOutOfRangeException(nameof(this.snapshot.RecoveryState))
            };
        }
    }

    public bool IsRecoverySectionVisible
        => this.snapshot.RecoveryState != PlanReviewRecoveryState.Closed
            || this.snapshot.Result is PlanReviewPlan { Operation: InstallerOperation.Rollback }
            || this.snapshot.State == PlanReviewState.Inspecting && this.snapshot.SelectedOperation is null;

    public bool IsRecoveryListVisible
        => this.snapshot.RecoveryState == PlanReviewRecoveryState.Available
            && this.RecoveryChoices.Count > 0;

    public bool IsRecoveryEmptyVisible => this.snapshot.RecoveryState == PlanReviewRecoveryState.NoHistory;

    public bool IsRecoveryBusy
        => this.snapshot.RecoveryState == PlanReviewRecoveryState.Listing
            || this.snapshot.State == PlanReviewState.Inspecting
                && this.snapshot.SelectedOperation is null
                && this.snapshot.RecoveryState == PlanReviewRecoveryState.Closed;

    public bool IsLoadRecoveriesVisible => this.snapshot.CanListRecoveries;

    public bool IsInspectRollbackVisible
        => this.snapshot.RecoveryState == PlanReviewRecoveryState.Available
            && this.RecoveryChoices.Count > 0;

    public bool IsInspectVisible => this.snapshot.State is PlanReviewState.Choosing
        or PlanReviewState.SelectionChanged;

    public bool IsBusy => this.snapshot.State is PlanReviewState.Inspecting
        or PlanReviewState.Approving
        or PlanReviewState.Confirming
        or PlanReviewState.Closing
        or PlanReviewState.Cancelling;

    public bool IsResultVisible => this.snapshot.State == PlanReviewState.Available;

    public bool IsConfirmVisible => this.snapshot.State == PlanReviewState.Available
        && this.snapshot.Result is PlanReviewPlan { HasBlockingConflicts: false };

    public bool IsConfirmationBlockedBySelection => this.IsConfirmVisible && this.snapshot.SelectedCandidates.Count > 0;

    public string ConfirmationDetail => this.IsConfirmationBlockedBySelection
        ? "Clear or apply the checked file choices before confirming. Unapplied choices cannot affect the exact plan."
        : "Confirming seals this exact reviewed plan for the final Run screen. Confirmation does not change files or start the operation.";

    public bool IsErrorVisible => this.snapshot.State is PlanReviewState.Rejected
        or PlanReviewState.Failed
        or PlanReviewState.SessionFaulted;

    public bool IsRetryVisible => this.snapshot.CanRetry;

    public bool IsCancelVisible => this.snapshot.CanCancel;

    public bool IsExitVisible => this.snapshot.CanExit || this.transitionFailed;

    public bool HasRiskRows => this.RiskRows.Count > 0;

    public bool HasOperationRows => this.OperationRows.Count > 0;

    public bool HasConflictRows => this.ConflictRows.Count > 0;

    public bool HasCandidateRows => this.CandidateRows.Count > 0;

    public bool IsCandidateReviewVisible => this.IsResultVisible
        && (this.CandidateChoices.Count > 0 || this.snapshot.HasAppliedCandidateApprovals)
        && this.snapshot.SelectedOperation != InstallerOperation.Backup;

    public bool HasCandidateChoices => this.CandidateChoices.Count > 0;

    public bool IsCandidateSelectionEnabled => this.snapshot.CanSelectCandidates;

    public AutomationLiveSetting StatusLiveSetting => this.IsResultVisible || this.IsErrorVisible
        ? AutomationLiveSetting.Off
        : AutomationLiveSetting.Polite;

    public AsyncRelayCommand InspectCommand { get; }

    public AsyncRelayCommand LoadRecoveriesCommand { get; }

    public AsyncRelayCommand InspectRollbackCommand { get; }

    public AsyncRelayCommand CancelCommand { get; }

    public AsyncRelayCommand RetryCommand { get; }

    public AsyncRelayCommand ApplyCandidatesCommand { get; }

    public RelayCommand ClearCandidatesCommand { get; }

    public AsyncRelayCommand StartFreshInspectionCommand { get; }

    public AsyncRelayCommand ConfirmCommand { get; }

    public RelayCommand ExitCommand { get; }

    public async ValueTask DisposeAsync()
    {
        if (this.disposed)
            return;
        this.disposed = true;
        await this.Controller.DisposeAsync().ConfigureAwait(true);
        this.Controller.Changed -= this.OnControllerChanged;
    }

    internal void ApplySnapshotForTesting(PlanReviewSnapshot value)
    {
        this.ApplySnapshot(value, requestFocus: false);
    }

    internal bool ToggleCandidate(PlanReviewCandidateChoice candidate)
    {
        if (!this.snapshot.CanSelectCandidates || !this.CandidateChoices.Any(choice => ReferenceEquals(choice, candidate)))
            return false;
        candidate.IsSelected = !candidate.IsSelected;
        return true;
    }

    internal void ReportExecutionTransitionFailure()
    {
        this.transitionFailed = true;
        this.Heading = "The final Run screen could not open";
        this.Message = "The confirmed owner was closed safely. No installer operation started and no game files were changed. Close and reopen the installer to inspect a fresh plan.";
        this.LiveAnnouncement = $"{this.Heading}. {this.Message}";
        this.NotifyDerivedProperties();
        this.FocusRequested?.Invoke(this, PlanReviewFocusTarget.Exit);
    }

    private void OnControllerChanged(object? sender, EventArgs e)
    {
        PlanReviewSnapshot next = this.Controller.Snapshot;
        if (Dispatcher.UIThread.CheckAccess())
            this.ApplySnapshot(next, requestFocus: true);
        else
            Dispatcher.UIThread.Post(() => this.ApplySnapshot(next, requestFocus: true));
    }

    private void ApplySnapshot(PlanReviewSnapshot next, bool requestFocus)
    {
        if (this.disposed && next.State != PlanReviewState.Disposed)
            return;
        if (next.Revision < this.snapshot.Revision)
            return;

        PlanReviewState previousState = this.snapshot.State;
        long previousGeneration = this.snapshot.Generation;
        this.snapshot = next;
        PlanReviewOperationChoice? selected = next.SelectedOperation is { } operation
            ? FixedOperationChoices.Single(choice => choice.Operation == operation)
            : null;
        this.SetProperty(ref this.selectedOperation, selected, nameof(this.SelectedOperation));
        this.ApplyResult(next.Result as PlanReviewPlan);
        this.ApplyCandidateChoices(next);
        this.ApplyRecoveryChoices(next);
        (this.Heading, this.Message) = this.GetCopy(next);
        this.LiveAnnouncement = next.State is PlanReviewState.Available
                or PlanReviewState.Rejected
                or PlanReviewState.Inspecting
                or PlanReviewState.Approving
                or PlanReviewState.Confirming
                or PlanReviewState.HandoffReady
                or PlanReviewState.HandedOff
                or PlanReviewState.Closing
                or PlanReviewState.Cancelling
                or PlanReviewState.Cancelled
                or PlanReviewState.Failed
                or PlanReviewState.SessionFaulted
            ? $"{this.Heading}. {this.Message}"
            : this.Heading;
        this.NotifyDerivedProperties();

        bool changed = previousState != next.State || previousGeneration != next.Generation;
        if (requestFocus && changed)
            this.FocusRequested?.Invoke(this, this.GetFocusTarget(next));
        if (next.HandoffReady && !this.confirmationReadyRaised)
        {
            this.confirmationReadyRaised = true;
            this.ConfirmationReady?.Invoke(this, EventArgs.Empty);
        }
    }

    private (string Heading, string Message) GetCopy(PlanReviewSnapshot value)
    {
        string operation = value.SelectedOperation is { } selected ? GetOperationLabel(selected) : "selected";
        return value.State switch
        {
            PlanReviewState.Choosing when value.SelectedOperation is null
                && value.RecoveryState == PlanReviewRecoveryState.Available
                && value.SelectedRecoveryPoint is not null => (
                "Recovery point selected — inspect rollback",
                "The selection is local to this screen. Inspect rollback requests a read-only preview; it does not confirm, run, or change files."
            ),
            PlanReviewState.Choosing when value.SelectedOperation is null
                && value.RecoveryState == PlanReviewRecoveryState.Available => (
                "Choose a recovery point",
                "Recovery history is loaded with no default selection. Select one exact point before inspecting a rollback preview."
            ),
            PlanReviewState.Choosing when value.SelectedOperation is null
                && value.RecoveryState == PlanReviewRecoveryState.NoHistory => (
                "No recovery history reported",
                "No committed recovery point was reported. Refresh performs another bounded local lookup and does not change files."
            ),
            PlanReviewState.Choosing when value.SelectedOperation is { } ordinaryOperation => (
                $"{GetOperationLabel(ordinaryOperation)} selected — inspect plan",
                "The selection is local to this screen. Inspect plan requests a read-only preview; it does not confirm, run, or change files."
            ),
            PlanReviewState.Choosing => (
                "Choose a plan to inspect",
                "Select one operation. Nothing is inspected until you choose Inspect plan."
            ),
            PlanReviewState.SelectionChanged => (
                "Operation changed — inspect the new selection",
                "The previous preview was cleared. No installer action has run."
            ),
            PlanReviewState.Inspecting when value.RecoveryState == PlanReviewRecoveryState.Listing => (
                "Loading local recovery history…",
                "Reading a bounded list from the local installer service. Nothing is selected, confirmed, run, or changed."
            ),
            PlanReviewState.Inspecting when value.SelectedOperation is null => (
                "Inspecting the rollback plan…",
                "The exact recovery selection has been consumed for one read-only inspection. No files are being changed."
            ),
            PlanReviewState.Inspecting => (
                $"Inspecting the {operation.ToLowerInvariant()} plan…",
                "Reading a bounded plan from the local installer service. No files are being changed."
            ),
            PlanReviewState.Approving => (
                "Applying additive candidate approvals to a refreshed preview…",
                "The current preview is being revoked and a newly validated read-only preview is being requested. No files are being changed, confirmed, or executed."
            ),
            PlanReviewState.Confirming => (
                "Confirming the exact reviewed plan…",
                "No files are being changed. Confirmation only prepares a separate final Run screen."
            ),
            PlanReviewState.HandoffReady => (
                "Plan confirmed — opening the final Run screen",
                "No files have changed and the operation has not started."
            ),
            PlanReviewState.HandedOff => (
                "Confirmed plan transferred safely",
                "No files have changed. The final Run screen now owns the one-time confirmed plan."
            ),
            PlanReviewState.Closing => (
                "Closing the read-only plan session safely…",
                "Waiting for backend cleanup to settle. Retry and exit remain unavailable until the session is closed; no installer action ran."
            ),
            PlanReviewState.Available when value.Result is PlanReviewPlan
            { Operation: InstallerOperation.Rollback, HasBlockingConflicts: true } rollback => (
                "Rollback preview has blocking conflicts",
                $"The inspection observed {Sum(rollback.ConflictCounts)} blocking conflict(s). This preview cannot be confirmed or run, and no file action ran."
            ),
            PlanReviewState.Available when value.Result is PlanReviewPlan { HasBlockingConflicts: true } plan => (
                $"{GetOperationLabel(plan.Operation)} preview has blocking conflicts",
                $"The inspection observed {Sum(plan.ConflictCounts)} blocking conflict(s). Additive candidate approval may refresh this preview, but this screen cannot confirm or execute the plan."
            ),
            PlanReviewState.Available when value.Result is PlanReviewPlan { Operation: InstallerOperation.Rollback } => (
                "Rollback plan inspected — preview only",
                "The exact selected recovery point produced this bounded preview. It is not confirmation, no file action ran, and a separate explicit Run is still required."
            ),
            PlanReviewState.Available when value.Result is PlanReviewPlan plan => (
                $"{GetOperationLabel(plan.Operation)} plan inspected — preview only",
                "No blocking conflicts were observed in this bounded inspection. That is not confirmation or a safety guarantee, and no file action ran."
            ),
            PlanReviewState.Rejected when value.Result is PlanReviewRejection rejection
                && value.RecoveryState == PlanReviewRecoveryState.RelistRequired => GetRecoveryRejectionCopy(rejection),
            PlanReviewState.Rejected when value.Result is PlanReviewRejection rejection => GetRejectionCopy(rejection),
            PlanReviewState.Cancelling => (
                "Stopping plan inspection safely…",
                "Waiting for the read-only request and its backend session to close. No files are being changed."
            ),
            PlanReviewState.Cancelled => (
                "Plan inspection cancelled and session closed",
                "No installer action ran. Close and reopen the installer to begin another verified session."
            ),
            PlanReviewState.Failed => (
                "The plan inspection stopped safely",
                "The verified session closed and no installer action ran. Close and reopen the installer before trying again."
            ),
            PlanReviewState.SessionFaulted => (
                "The plan-inspection service closed",
                "The verified session is no longer available and no installer action ran. Close and reopen the installer."
            ),
            PlanReviewState.Disposed => (
                "Closing safely…",
                "The read-only plan session has closed."
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
    }

    private static (string Heading, string Message) GetRejectionCopy(PlanReviewRejection rejection)
    {
        return rejection.ErrorCode switch
        {
            ProtocolPrePlanErrorCode.RequestCancelled => (
                "The backend did not finish this inspection",
                "No installer action ran. Try the same read-only inspection again."
            ),
            ProtocolPrePlanErrorCode.InvalidGameFolder => (
                "The selected game folder is no longer valid",
                "No installer action ran. Close and reopen the installer to choose and validate a game folder again."
            ),
            ProtocolPrePlanErrorCode.PackageRejected => (
                "The verified package is no longer available",
                "No installer action ran. Close and reopen the installer to verify the release again."
            ),
            ProtocolPrePlanErrorCode.RecoveryUnavailable => (
                "Recovery information is unavailable",
                "No installer action ran. Close and reopen the installer before reviewing recovery information."
            ),
            ProtocolPrePlanErrorCode.InspectionFailed => (
                "The plan could not be inspected",
                "No installer action ran. Try the same read-only inspection again."
            ),
            ProtocolPrePlanErrorCode.CandidateApprovalFailed => (
                "A candidate decision could not be accepted",
                "The additive candidate approvals were not accepted. No files changed. Start a fresh inspection or close and reopen the installer."
            ),
            ProtocolPrePlanErrorCode.PermissionDenied => (
                "The game folder could not be read with your permissions",
                "No installer action ran. Review this game folder’s user permissions, then try inspection again. Do not run the installer as root."
            ),
            ProtocolPrePlanErrorCode.InputOutputFailure => (
                "The game folder could not be read reliably",
                "No installer action ran. Check the filesystem, then close and reopen the installer."
            ),
            ProtocolPrePlanErrorCode.UnexpectedFailure when rejection.NextAction == ProtocolNextAction.ViewPrivateLog => (
                "The plan session closed after an unexpected problem",
                "The backend recorded a private local log, but this summary does not expose its location. No installer action ran; close and reopen the installer."
            ),
            ProtocolPrePlanErrorCode.UnexpectedFailure => (
                "The plan session closed after an unexpected problem",
                "No installer action ran. Close and reopen the installer to begin another verified session."
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(rejection))
        };
    }

    private static (string Heading, string Message) GetRecoveryRejectionCopy(PlanReviewRejection rejection)
    {
        return rejection.ErrorCode switch
        {
            ProtocolPrePlanErrorCode.RequestCancelled => (
                "The recovery request did not finish",
                "No installer action ran. Refresh recovery history before making a new explicit selection."
            ),
            ProtocolPrePlanErrorCode.RecoveryUnavailable => (
                "Recovery history changed or became unavailable",
                "The previous choices were revoked. Refresh recovery history before making a new explicit selection; no installer action ran."
            ),
            ProtocolPrePlanErrorCode.InspectionFailed => (
                "The rollback plan could not be inspected",
                "The selected authority was revoked. Refresh recovery history and select a newly listed point before trying again. No installer action ran."
            ),
            ProtocolPrePlanErrorCode.PermissionDenied => (
                "Recovery information could not be read with your permissions",
                "Review this game folder’s user permissions, then refresh recovery history. Do not run the installer as root; no installer action ran."
            ),
            _ => GetRejectionCopy(rejection)
        };
    }

    private void ApplyResult(PlanReviewPlan? plan)
    {
        if (plan is null)
        {
            this.ObservedStateDetail = "No plan has been inspected.";
            this.CurrentReleaseDetail = "No inspected current release.";
            this.TargetReleaseDetail = "No inspected target release.";
            this.SafetyDetail = "No plan safety facts have been reported.";
            this.RiskSummary = "No plan has been inspected.";
            this.OperationSummary = "No plan has been inspected.";
            this.ConflictSummary = "No plan has been inspected.";
            this.CandidateSummary = "No plan has been inspected.";
            this.AdditionalNoticeDetail = "No plan has been inspected.";
            this.RiskRows = Array.Empty<PlanReviewSummaryRow>();
            this.OperationRows = Array.Empty<PlanReviewSummaryRow>();
            this.ConflictRows = Array.Empty<PlanReviewSummaryRow>();
            this.CandidateRows = Array.Empty<PlanReviewSummaryRow>();
            return;
        }

        this.ObservedStateDetail = GetObservedStateDetail(plan.ObservedState);
        this.CurrentReleaseDetail = FormatRelease(plan.CurrentRelease, "No receipt-authenticated current fork release was observed.");
        this.TargetReleaseDetail = FormatTargetRelease(plan);
        this.SafetyDetail = plan.Operation == InstallerOperation.Rollback
            ? "Recommended default: Cancel. This preview is bound to the exact selected recovery point. Confirm plan seals it but does not change files; a separate explicit Run action is still required."
            : "Recommended default: Cancel. Candidate approval only requests a refreshed read-only preview. Confirm plan seals the exact current preview but does not change files; a separate explicit Run action is still required.";
        this.RiskRows = Array.AsReadOnly(plan.Risks
            .Select(risk => GetRiskRow(risk))
            .ToArray());
        this.OperationRows = Array.AsReadOnly(plan.OperationCounts
            .Select(item => GetOperationRow(item.Kind, item.Count))
            .ToArray());
        this.ConflictRows = Array.AsReadOnly(plan.ConflictCounts
            .Select(item => GetConflictRow(item.Code, item.Count))
            .ToArray());
        this.CandidateRows = Array.AsReadOnly(plan.CandidateCounts
            .Select(item => GetCandidateRow(item))
            .ToArray());
        this.RiskSummary = this.RiskRows.Count == 0
            ? "No plan risk categories were reported by this bounded inspection. This is not a safety guarantee."
            : $"{this.RiskRows.Count} plan risk category or categories were reported.";
        int operations = Sum(plan.OperationCounts);
        this.OperationSummary = operations == 0
            ? "0 planned file actions were reported. This does not mean an installer action ran."
            : $"{operations} planned file action(s) were reported. None have run.";
        int conflicts = Sum(plan.ConflictCounts);
        this.ConflictSummary = conflicts == 0
            ? "No blocking conflicts were reported by this bounded inspection. This is not approval."
            : $"{conflicts} blocking conflict(s) were reported.";
        int candidates = Sum(plan.CandidateCounts);
        this.CandidateSummary = candidates == 0
            ? "No modified or unknown file candidates were reported."
            : $"{candidates} modified or unknown file candidate(s) were reported. Review the individual choices below; provisional inclusion is not approval by you.";
        this.AdditionalNoticeDetail = plan.AdditionalNoticeCount == 0
            ? "No additional backend notices were reported."
            : $"Additional backend notices observed: {plan.AdditionalNoticeCount}. This summary projection does not expose their text.";
    }

    private void ApplyCandidateChoices(PlanReviewSnapshot value)
    {
        IReadOnlyList<PlanReviewCandidate> candidates = (value.Result as PlanReviewPlan)?.Candidates
            ?? Array.Empty<PlanReviewCandidate>();
        HashSet<PlanReviewCandidate> selected = new(value.SelectedCandidates, ReferenceEqualityComparer.Instance);
        bool sameCandidates = candidates.Count == this.CandidateChoices.Count
            && candidates.Select((candidate, index) => ReferenceEquals(candidate, this.CandidateChoices[index].Candidate)).All(value => value);
        if (!sameCandidates)
        {
            foreach (PlanReviewCandidateChoice previous in this.CandidateChoices)
                previous.Deactivate();
            PlanReviewCandidateChoice[] choices = candidates.Select(candidate => new PlanReviewCandidateChoice(
                candidate,
                candidate.DisplayPath,
                GetCandidateReasonDetail(candidate.Reason),
                GetCandidateDispositionDetail(candidate.Disposition),
                candidate.BackendProvisionallyIncluded,
                this.OnCandidateSelectionChanged
            )).ToArray();
            this.CandidateChoices = Array.AsReadOnly(choices);
        }
        foreach (PlanReviewCandidateChoice choice in this.CandidateChoices)
            choice.SetSelectedFromSnapshot(selected.Contains(choice.Candidate));
        this.CandidateSelectionAnnouncement = value.AppliedCandidateApprovalCount > 0
            ? $"{value.AppliedCandidateApprovalCount} {GetCountLabel(value.AppliedCandidateApprovalCount, "approval", "approvals")} already applied and fixed in this preview; {selected.Count} of {candidates.Count} remaining files selected."
            : $"{selected.Count} of {candidates.Count} files selected.";
    }

    private void ApplyRecoveryChoices(PlanReviewSnapshot value)
    {
        IReadOnlyList<PlanReviewRecoveryPoint> points = value.RecoveryPoints;
        bool samePoints = points.Count == this.RecoveryChoices.Count
            && points.Select((point, index) => ReferenceEquals(point, this.RecoveryChoices[index].Point)).All(match => match);
        if (!samePoints)
        {
            // Clear the two-way selection before replacing ItemsSource. Avalonia may otherwise write a stale null
            // selection back while the controller has already revoked the catalog for refresh or inspection.
            this.SetProperty(ref this.selectedRecoveryChoice, null, nameof(this.SelectedRecoveryChoice));
            PlanReviewRecoveryChoice[] choices = points
                .Select(CreateRecoveryChoice)
                .ToArray();
            this.RecoveryChoices = Array.AsReadOnly(choices);
        }

        PlanReviewRecoveryChoice? selected = value.SelectedRecoveryPoint is { } selectedPoint
            ? this.RecoveryChoices.SingleOrDefault(choice => ReferenceEquals(choice.Point, selectedPoint))
                ?? throw new InvalidOperationException("The selected recovery presentation did not match the bounded current catalog.")
            : null;
        this.SetProperty(ref this.selectedRecoveryChoice, selected, nameof(this.SelectedRecoveryChoice));
    }

    private static PlanReviewRecoveryChoice CreateRecoveryChoice(PlanReviewRecoveryPoint point)
    {
        string title = point.IsCurrent
            ? "Current recovery point"
            : point.IsUserCheckpoint
                ? $"Checkpoint {point.Ordinal}"
                : $"Recovery point {point.Ordinal}";
        string origin = point.OriginOperation switch
        {
            InstallerOperation.Install => "install",
            InstallerOperation.Update => "update",
            InstallerOperation.Repair => "repair",
            InstallerOperation.Uninstall => "uninstall",
            InstallerOperation.Backup => "user checkpoint",
            InstallerOperation.Rollback => "rollback",
            _ => throw new ArgumentOutOfRangeException(nameof(point), point.OriginOperation, null)
        };
        string target = point.RestoreTarget switch
        {
            PlanReviewRecoveryReleaseTarget release => $"Restore target: {release.Tag}\nVersion: {release.EmbeddedVersion}",
            PlanReviewRecoveryUninstalledTarget => "Restore target: no managed SMAPI installation",
            _ => throw new ArgumentOutOfRangeException(nameof(point), point.RestoreTarget, null)
        };
        string current = point.IsCurrent
            ? "Newest committed point."
            : "Earlier committed point.";
        return new(point, title, $"{current} Origin: {origin}. {target}.");
    }

    private void OnCandidateSelectionChanged(PlanReviewCandidateChoice choice, bool _)
    {
        if (
            !this.snapshot.CanSelectCandidates
            || !this.CandidateChoices.Any(candidate => ReferenceEquals(candidate, choice))
        )
        {
            choice.SetSelectedFromSnapshot(this.snapshot.SelectedCandidates.Any(candidate => ReferenceEquals(candidate, choice.Candidate)));
            return;
        }

        try
        {
            PlanReviewCandidate[] selection = this.CandidateChoices
                .Where(candidate => candidate.IsSelected)
                .Select(candidate => candidate.Candidate)
                .ToArray();
            this.Controller.SetCandidateSelection(Array.AsReadOnly(selection));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            PlanReviewSnapshot current = this.Controller.Snapshot;
            this.ApplySnapshot(current, requestFocus: false);
        }
        catch (Exception exception)
        {
            this.HandlePresentationFailure(exception);
        }
    }

    private void ClearCandidateSelectionSafely()
    {
        try
        {
            this.Controller.ClearCandidateSelection();
        }
        catch (Exception exception)
        {
            this.HandleCandidateActionFailure(exception);
        }
    }

    private void HandleCandidateActionFailure(Exception exception)
    {
        if (exception is ArgumentException or InvalidOperationException)
        {
            this.ApplySnapshot(this.Controller.Snapshot, requestFocus: false);
            return;
        }
        this.HandlePresentationFailure(exception);
    }

    private static string GetCandidateReasonDetail(FileReplacementCandidateReason reason)
    {
        return reason switch
        {
            FileReplacementCandidateReason.ModifiedReceiptOwned => "Reason: This receipt-owned file differs from its recorded identity. The cause was not observed.",
            FileReplacementCandidateReason.ModifiedInstalledLauncher => "Reason: This installed launcher differs from its recorded identity. The cause was not observed.",
            FileReplacementCandidateReason.LegacyInstaller => "Reason: This exact file was classified as a recognized legacy installer file.",
            FileReplacementCandidateReason.UnknownCollision => "Reason: This file occupies a verified package destination. Its owner and creator are unknown.",
            FileReplacementCandidateReason.OfficialOrLegacyLauncher => "Reason: Bounded evidence classified this as an official or legacy launcher; ownership is unconfirmed.",
            FileReplacementCandidateReason.OfficialLauncherBackup => "Reason: This exact backup meets the retained-official-launcher classification.",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
        };
    }

    private static string GetCandidateDispositionDetail(FileReplacementCandidateDisposition disposition)
    {
        return disposition switch
        {
            FileReplacementCandidateDisposition.Replace => "Proposed disposition: a later confirmed plan may replace this exact observed file.",
            FileReplacementCandidateDisposition.Remove => "Proposed disposition: a later confirmed plan may remove this exact observed file.",
            FileReplacementCandidateDisposition.Restore => "Proposed disposition: a later confirmed plan may restore this exact observed file.",
            FileReplacementCandidateDisposition.TrustRetained => "Proposed disposition: a later confirmed plan may retain and trust this exact observed file.",
            _ => throw new ArgumentOutOfRangeException(nameof(disposition), disposition, null)
        };
    }

    private static string GetObservedStateDetail(ObservedInstallState state)
    {
        return state switch
        {
            ObservedInstallState.NotInstalled => "No managed fork receipt was observed.",
            ObservedInstallState.KnownUnmodified => "A known fork installation was observed; managed files matched its receipt.",
            ObservedInstallState.KnownModified => "A known fork installation was observed; one or more managed files differed from its receipt.",
            ObservedInstallState.LegacyOrOfficial => "An official or legacy SMAPI installation was observed; ownership is not confirmed.",
            ObservedInstallState.Unknown => "The installation state could not be classified from bounded evidence.",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
        };
    }

    private static PlanReviewSummaryRow GetRiskRow(ProtocolPlanRisk risk)
    {
        return risk switch
        {
            ProtocolPlanRisk.Uninstall => new("Removal requested", "The preview would remove receipt-owned SMAPI files where observed.", 1),
            ProtocolPlanRisk.Rollback => new("Rollback requested", "The preview would restore only the exact explicitly selected committed recovery point.", 1),
            ProtocolPlanRisk.Downgrade => new("Older target release observed", "The verified target is earlier than the receipt-authenticated current release.", 1),
            ProtocolPlanRisk.ModifiedOrUnknownFileApproval => new("Modified or unknown files observed", "Explicit additive candidate approval can request a refreshed read-only preview. It does not change files, confirm, or execute the plan.", 1),
            ProtocolPlanRisk.RecoveryPrune => throw new ArgumentOutOfRangeException(nameof(risk), risk, "Recovery-pruning risks aren't accepted by this screen."),
            _ => throw new ArgumentOutOfRangeException(nameof(risk), risk, null)
        };
    }

    private static PlanReviewSummaryRow GetOperationRow(PlanOperationKind kind, int count)
    {
        (string label, string detail) = kind switch
        {
            PlanOperationKind.Backup => ("Back up", "Copy an observed file into bounded recovery storage."),
            PlanOperationKind.Remove => ("Remove", "Remove a receipt-owned observed file."),
            PlanOperationKind.Restore => ("Restore", "Restore a launcher or managed file from authenticated content."),
            PlanOperationKind.Create => ("Create", "Create a package-managed file where none was observed."),
            PlanOperationKind.Replace => ("Replace", "Replace an observed file with verified package content."),
            PlanOperationKind.Retain => ("Retain", "Keep an exact observed managed file unchanged."),
            PlanOperationKind.Preserve => ("Preserve", "Keep an observed unowned file unchanged."),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        return new(label, detail, count);
    }

    private static PlanReviewSummaryRow GetConflictRow(PlanConflictCode code, int count)
    {
        (string label, string detail) = code switch
        {
            PlanConflictCode.TargetManifestRequired => ("Target manifest required", "The verified target manifest was not available."),
            PlanConflictCode.ExistingInstallationRequiresUpdate => ("Existing installation requires Update", "Install cannot replace an existing managed installation."),
            PlanConflictCode.InstalledReceiptRequired => ("Installed receipt required", "This operation requires a receipt-authenticated fork installation."),
            PlanConflictCode.ReleaseDoesNotMatchReceipt => ("Release does not match receipt", "The requested release conflicts with the installed receipt."),
            PlanConflictCode.ReceiptDoesNotMatchManifest => ("Receipt does not match manifest", "The installed receipt and its release manifest disagree."),
            PlanConflictCode.ModifiedOwnedFile => ("Modified managed file", "A receipt-owned file differs from its recorded identity."),
            PlanConflictCode.UnknownCollision => ("Unknown file collision", "An unowned file occupies a package destination."),
            PlanConflictCode.LegacyOwnershipUnconfirmed => ("Legacy ownership unconfirmed", "The installer cannot safely claim the observed legacy files."),
            PlanConflictCode.PreservedTargetCollision => ("Preserved target collision", "A file marked for preservation blocks a package destination."),
            PlanConflictCode.MissingGameLauncher => ("Game launcher missing", "The observed game launcher is not available."),
            PlanConflictCode.ModifiedInstalledLauncher => ("Installed launcher modified", "The installed launcher differs from its receipt-authenticated identity."),
            PlanConflictCode.AmbiguousLauncherBackup => ("Launcher backup ambiguous", "The observed launcher backup cannot be identified safely."),
            PlanConflictCode.MissingOriginalLauncherBackup => ("Original launcher backup missing", "A required original launcher backup was not observed."),
            PlanConflictCode.RollbackSnapshotRequired => ("Rollback snapshot required", "An authenticated recovery snapshot is required."),
            PlanConflictCode.RollbackReceiptMismatch => ("Rollback receipt mismatch", "The recovery snapshot does not match the installed receipt."),
            PlanConflictCode.RollbackDrift => ("Rollback drift observed", "Files changed after the selected recovery snapshot."),
            PlanConflictCode.RecoveryCapacityReached => ("Recovery capacity reached", "No bounded recovery slot is currently available."),
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, null)
        };
        return new(label, detail, count);
    }

    private static PlanReviewSummaryRow GetCandidateRow(PlanReviewCandidateCount item)
    {
        string reason = item.Reason switch
        {
            FileReplacementCandidateReason.ModifiedReceiptOwned => "Modified receipt-owned file",
            FileReplacementCandidateReason.ModifiedInstalledLauncher => "Modified installed launcher",
            FileReplacementCandidateReason.LegacyInstaller => "Recognized legacy installer file",
            FileReplacementCandidateReason.UnknownCollision => "Unknown destination collision",
            FileReplacementCandidateReason.OfficialOrLegacyLauncher => "Official or legacy launcher",
            FileReplacementCandidateReason.OfficialLauncherBackup => "Official launcher backup",
            _ => throw new ArgumentOutOfRangeException(nameof(item), item.Reason, null)
        };
        string disposition = item.Disposition switch
        {
            FileReplacementCandidateDisposition.Replace => "proposed replacement",
            FileReplacementCandidateDisposition.Remove => "proposed removal",
            FileReplacementCandidateDisposition.Restore => "proposed restoration",
            FileReplacementCandidateDisposition.TrustRetained => "proposed trusted retention",
            _ => throw new ArgumentOutOfRangeException(nameof(item), item.Disposition, null)
        };
        string inclusion = item.ProvisionallyIncluded
            ? "Provisionally included by the inspected backend plan; not approved by you."
            : "Not provisionally included; no decision was made by you.";
        return new(reason, $"{disposition}. {inclusion}", item.Count);
    }

    private static string FormatRelease(PlanReviewRelease? release, string absent)
        => release is null ? absent : $"Tag: {release.Tag}\nVersion: {release.EmbeddedVersion}";

    private static string FormatTargetRelease(PlanReviewPlan plan)
    {
        if (plan.TargetRelease is not null)
            return FormatRelease(plan.TargetRelease, "");
        return plan.Operation switch
        {
            InstallerOperation.Uninstall => "No target release is present for this uninstall preview.",
            InstallerOperation.Rollback => "The selected recovery point would restore an uninstalled managed-SMAPI state.",
            _ => "No target release was reported by this bounded inspection."
        };
    }

    private static int Sum(IReadOnlyList<PlanReviewOperationCount> values)
        => values.Aggregate(0, (sum, value) => checked(sum + value.Count));

    private static int Sum(IReadOnlyList<PlanReviewConflictCount> values)
        => values.Aggregate(0, (sum, value) => checked(sum + value.Count));

    private static int Sum(IReadOnlyList<PlanReviewCandidateCount> values)
        => values.Aggregate(0, (sum, value) => checked(sum + value.Count));

    private static string GetCountLabel(int count, string singular, string plural)
        => count == 1 ? singular : plural;

    private static string GetOperationLabel(InstallerOperation operation)
    {
        return operation switch
        {
            InstallerOperation.Install => "Install",
            InstallerOperation.Update => "Update",
            InstallerOperation.Repair => "Repair",
            InstallerOperation.Uninstall => "Uninstall",
            InstallerOperation.Backup => "Backup",
            InstallerOperation.Rollback => "Rollback",
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };
    }

    private PlanReviewFocusTarget GetFocusTarget(PlanReviewSnapshot value)
    {
        if (value.SelectedOperation is null && value.RecoveryState == PlanReviewRecoveryState.Listing)
            return PlanReviewFocusTarget.RecoveryStatus;
        if (value.State == PlanReviewState.Choosing
            && value.SelectedOperation is null
            && value.RecoveryState == PlanReviewRecoveryState.Available)
            return value.SelectedRecoveryPoint is null
                ? PlanReviewFocusTarget.RecoveryList
                : PlanReviewFocusTarget.InspectRollback;
        if (value.State == PlanReviewState.Choosing
            && value.SelectedOperation is null
            && value.RecoveryState == PlanReviewRecoveryState.NoHistory)
            return PlanReviewFocusTarget.RecoveryStatus;
        return value.State switch
        {
            PlanReviewState.SelectionChanged => PlanReviewFocusTarget.OperationList,
            PlanReviewState.Inspecting or PlanReviewState.Approving or PlanReviewState.Confirming or PlanReviewState.Closing or PlanReviewState.Cancelling => PlanReviewFocusTarget.Status,
            PlanReviewState.Available => PlanReviewFocusTarget.Result,
            PlanReviewState.Rejected => PlanReviewFocusTarget.Error,
            PlanReviewState.Cancelled or PlanReviewState.Failed or PlanReviewState.SessionFaulted => PlanReviewFocusTarget.Exit,
            _ => PlanReviewFocusTarget.Status
        };
    }

    private void NotifyDerivedProperties()
    {
        this.OnPropertyChanged(nameof(this.IsOperationSelectionEnabled));
        this.OnPropertyChanged(nameof(this.RecoveryStatusDetail));
        this.OnPropertyChanged(nameof(this.IsRecoverySectionVisible));
        this.OnPropertyChanged(nameof(this.IsRecoveryListVisible));
        this.OnPropertyChanged(nameof(this.IsRecoveryEmptyVisible));
        this.OnPropertyChanged(nameof(this.IsRecoveryBusy));
        this.OnPropertyChanged(nameof(this.IsLoadRecoveriesVisible));
        this.OnPropertyChanged(nameof(this.IsInspectRollbackVisible));
        this.OnPropertyChanged(nameof(this.IsInspectVisible));
        this.OnPropertyChanged(nameof(this.IsBusy));
        this.OnPropertyChanged(nameof(this.IsResultVisible));
        this.OnPropertyChanged(nameof(this.IsConfirmVisible));
        this.OnPropertyChanged(nameof(this.IsConfirmationBlockedBySelection));
        this.OnPropertyChanged(nameof(this.ConfirmationDetail));
        this.OnPropertyChanged(nameof(this.IsErrorVisible));
        this.OnPropertyChanged(nameof(this.IsRetryVisible));
        this.OnPropertyChanged(nameof(this.IsCancelVisible));
        this.OnPropertyChanged(nameof(this.IsExitVisible));
        this.OnPropertyChanged(nameof(this.HasRiskRows));
        this.OnPropertyChanged(nameof(this.HasOperationRows));
        this.OnPropertyChanged(nameof(this.HasConflictRows));
        this.OnPropertyChanged(nameof(this.HasCandidateRows));
        this.OnPropertyChanged(nameof(this.IsCandidateReviewVisible));
        this.OnPropertyChanged(nameof(this.HasCandidateChoices));
        this.OnPropertyChanged(nameof(this.IsCandidateSelectionEnabled));
        this.OnPropertyChanged(nameof(this.CandidateReviewDetail));
        this.OnPropertyChanged(nameof(this.IsCandidateApprovalCapacityFull));
        this.OnPropertyChanged(nameof(this.IsCandidateSelectionOverRemainingCapacity));
        this.OnPropertyChanged(nameof(this.IsCandidateCapacityDetailVisible));
        this.OnPropertyChanged(nameof(this.CandidateCapacityDetail));
        this.OnPropertyChanged(nameof(this.StatusLiveSetting));
        this.InspectCommand.NotifyCanExecuteChanged();
        this.LoadRecoveriesCommand.NotifyCanExecuteChanged();
        this.InspectRollbackCommand.NotifyCanExecuteChanged();
        this.CancelCommand.NotifyCanExecuteChanged();
        this.RetryCommand.NotifyCanExecuteChanged();
        this.ApplyCandidatesCommand.NotifyCanExecuteChanged();
        this.ClearCandidatesCommand.NotifyCanExecuteChanged();
        this.StartFreshInspectionCommand.NotifyCanExecuteChanged();
        this.ConfirmCommand.NotifyCanExecuteChanged();
        this.ExitCommand.NotifyCanExecuteChanged();
    }

    private void HandlePresentationFailure(Exception exception)
    {
        this.Heading = "The plan-review action stopped safely";
        this.Message = "Close and reopen the installer before trying again. No installer action ran.";
        this.LiveAnnouncement = $"{this.Heading}. {this.Message}";
        this.FocusRequested?.Invoke(this, PlanReviewFocusTarget.Status);
    }

    private void HandleRecoveryActionFailure(Exception exception)
    {
        if (exception is ArgumentException or InvalidOperationException)
        {
            this.ApplySnapshot(this.Controller.Snapshot, requestFocus: false);
            this.FocusRequested?.Invoke(this, PlanReviewFocusTarget.RecoveryStatus);
            return;
        }
        this.HandlePresentationFailure(exception);
    }
}
