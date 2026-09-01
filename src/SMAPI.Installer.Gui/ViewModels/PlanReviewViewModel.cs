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
    Status,
    Result,
    Error,
    Retry,
    Exit
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
    private bool disposed;

    public PlanReviewViewModel(PlanReviewController controller)
    {
        this.Controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.snapshot = controller.Snapshot;
        this.InspectCommand = new(() => controller.InspectAsync(), () => this.snapshot.CanInspect, this.HandlePresentationFailure);
        this.CancelCommand = new(controller.CancelAsync, () => this.snapshot.CanCancel, this.HandlePresentationFailure);
        this.RetryCommand = new(() => controller.InspectAsync(), () => this.snapshot.CanRetry, this.HandlePresentationFailure);
        this.ExitCommand = new(() => this.CloseRequested?.Invoke(this, EventArgs.Empty), () => this.snapshot.CanExit);
        this.Controller.Changed += this.OnControllerChanged;
        this.ApplySnapshot(this.snapshot, requestFocus: false);
    }

    public event EventHandler<PlanReviewFocusTarget>? FocusRequested;

    public event EventHandler? CloseRequested;

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

    public bool IsOperationSelectionEnabled => this.snapshot.CanSelect;

    public bool IsInspectVisible => this.snapshot.State is PlanReviewState.Choosing
        or PlanReviewState.SelectionChanged
        or PlanReviewState.Available;

    public bool IsBusy => this.snapshot.State is PlanReviewState.Inspecting
        or PlanReviewState.Closing
        or PlanReviewState.Cancelling;

    public bool IsResultVisible => this.snapshot.State == PlanReviewState.Available;

    public bool IsErrorVisible => this.snapshot.State is PlanReviewState.Rejected
        or PlanReviewState.Failed
        or PlanReviewState.SessionFaulted;

    public bool IsRetryVisible => this.snapshot.CanRetry;

    public bool IsCancelVisible => this.snapshot.CanCancel;

    public bool IsExitVisible => this.snapshot.CanExit;

    public bool HasRiskRows => this.RiskRows.Count > 0;

    public bool HasOperationRows => this.OperationRows.Count > 0;

    public bool HasConflictRows => this.ConflictRows.Count > 0;

    public bool HasCandidateRows => this.CandidateRows.Count > 0;

    public AutomationLiveSetting StatusLiveSetting => this.IsResultVisible || this.IsErrorVisible
        ? AutomationLiveSetting.Off
        : AutomationLiveSetting.Polite;

    public AsyncRelayCommand InspectCommand { get; }

    public AsyncRelayCommand CancelCommand { get; }

    public AsyncRelayCommand RetryCommand { get; }

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
        (this.Heading, this.Message) = this.GetCopy(next);
        this.LiveAnnouncement = next.State is PlanReviewState.Available
                or PlanReviewState.Rejected
                or PlanReviewState.Inspecting
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
    }

    private (string Heading, string Message) GetCopy(PlanReviewSnapshot value)
    {
        string operation = value.SelectedOperation is { } selected ? GetOperationLabel(selected) : "selected";
        return value.State switch
        {
            PlanReviewState.Choosing => (
                "Choose a plan to inspect",
                "Select one operation. Nothing is inspected until you choose Inspect plan."
            ),
            PlanReviewState.SelectionChanged => (
                "Operation changed — inspect the new selection",
                "The previous preview was cleared. No installer action has run."
            ),
            PlanReviewState.Inspecting => (
                $"Inspecting the {operation.ToLowerInvariant()} plan…",
                "Reading a bounded plan from the local installer service. No files are being changed."
            ),
            PlanReviewState.Closing => (
                "Closing the read-only plan session safely…",
                "Waiting for backend cleanup to settle. Retry and exit remain unavailable until the session is closed; no installer action ran."
            ),
            PlanReviewState.Available when value.Result is PlanReviewPlan { HasBlockingConflicts: true } plan => (
                $"{GetOperationLabel(plan.Operation)} preview has blocking conflicts",
                $"The inspection observed {Sum(plan.ConflictCounts)} blocking conflict(s). This plan cannot proceed as observed; this screen cannot approve or run it."
            ),
            PlanReviewState.Available when value.Result is PlanReviewPlan plan => (
                $"{GetOperationLabel(plan.Operation)} plan inspected — preview only",
                "No blocking conflicts were observed in this bounded inspection. That is not approval and no action ran."
            ),
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
                "This preview cannot make candidate decisions. Close and reopen the installer; no installer action ran."
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
        this.SafetyDetail = "Recommended default for any later decision: Cancel. A separate confirmation would be required in a future reviewed workflow. This build has no confirmation control.";
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
            : $"{candidates} modified or unknown file candidate(s) were reported. Provisional inclusion is not approval by you.";
        this.AdditionalNoticeDetail = plan.AdditionalNoticeCount == 0
            ? "No additional backend notices were reported."
            : $"Additional backend notices observed: {plan.AdditionalNoticeCount}. This summary projection does not expose their text.";
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
            ProtocolPlanRisk.Rollback => throw new ArgumentOutOfRangeException(nameof(risk), risk, "Rollback risks aren't accepted by this screen."),
            ProtocolPlanRisk.Downgrade => new("Older target release observed", "The verified target is earlier than the receipt-authenticated current release.", 1),
            ProtocolPlanRisk.ModifiedOrUnknownFileApproval => new("Modified or unknown files observed", "A later separate approval would be required. This screen cannot approve files.", 1),
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
        return plan.Operation == InstallerOperation.Uninstall
            ? "No target release is present for this uninstall preview."
            : "No target release was reported by this bounded inspection.";
    }

    private static int Sum(IReadOnlyList<PlanReviewOperationCount> values)
        => values.Aggregate(0, (sum, value) => checked(sum + value.Count));

    private static int Sum(IReadOnlyList<PlanReviewConflictCount> values)
        => values.Aggregate(0, (sum, value) => checked(sum + value.Count));

    private static int Sum(IReadOnlyList<PlanReviewCandidateCount> values)
        => values.Aggregate(0, (sum, value) => checked(sum + value.Count));

    private static string GetOperationLabel(InstallerOperation operation)
    {
        return operation switch
        {
            InstallerOperation.Install => "Install",
            InstallerOperation.Update => "Update",
            InstallerOperation.Repair => "Repair",
            InstallerOperation.Uninstall => "Uninstall",
            InstallerOperation.Backup => "Backup",
            InstallerOperation.Rollback => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Rollback isn't available on this screen."),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };
    }

    private PlanReviewFocusTarget GetFocusTarget(PlanReviewSnapshot value)
    {
        return value.State switch
        {
            PlanReviewState.SelectionChanged => PlanReviewFocusTarget.OperationList,
            PlanReviewState.Inspecting or PlanReviewState.Closing or PlanReviewState.Cancelling => PlanReviewFocusTarget.Status,
            PlanReviewState.Available => PlanReviewFocusTarget.Result,
            PlanReviewState.Rejected => PlanReviewFocusTarget.Error,
            PlanReviewState.Cancelled or PlanReviewState.Failed or PlanReviewState.SessionFaulted => PlanReviewFocusTarget.Exit,
            _ => PlanReviewFocusTarget.Status
        };
    }

    private void NotifyDerivedProperties()
    {
        this.OnPropertyChanged(nameof(this.IsOperationSelectionEnabled));
        this.OnPropertyChanged(nameof(this.IsInspectVisible));
        this.OnPropertyChanged(nameof(this.IsBusy));
        this.OnPropertyChanged(nameof(this.IsResultVisible));
        this.OnPropertyChanged(nameof(this.IsErrorVisible));
        this.OnPropertyChanged(nameof(this.IsRetryVisible));
        this.OnPropertyChanged(nameof(this.IsCancelVisible));
        this.OnPropertyChanged(nameof(this.IsExitVisible));
        this.OnPropertyChanged(nameof(this.HasRiskRows));
        this.OnPropertyChanged(nameof(this.HasOperationRows));
        this.OnPropertyChanged(nameof(this.HasConflictRows));
        this.OnPropertyChanged(nameof(this.HasCandidateRows));
        this.OnPropertyChanged(nameof(this.StatusLiveSetting));
        this.InspectCommand.NotifyCanExecuteChanged();
        this.CancelCommand.NotifyCanExecuteChanged();
        this.RetryCommand.NotifyCanExecuteChanged();
        this.ExitCommand.NotifyCanExecuteChanged();
    }

    private void HandlePresentationFailure(Exception exception)
    {
        this.Heading = "The plan-review action stopped safely";
        this.Message = "Close and reopen the installer before trying again. No installer action ran.";
        this.LiveAnnouncement = $"{this.Heading}. {this.Message}";
        this.FocusRequested?.Invoke(this, PlanReviewFocusTarget.Status);
    }
}
