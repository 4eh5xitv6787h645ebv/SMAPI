using Avalonia.Automation;
using Avalonia.Threading;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Core.Transactions;
using StardewModdingAPI.Installer.Gui.Backend;
using StardewModdingAPI.Installer.Gui.Diagnostics;
using StardewModdingAPI.Installer.Gui.Frontend;

namespace StardewModdingAPI.Installer.Gui.ViewModels;

internal enum ExecutionFocusTarget
{
    Cancel,
    Run,
    Status,
    Problem,
    Recover,
    Result,
    Exit
}

internal sealed record ExecutionFactRow(string Label, string Value)
{
    public string AccessibleName => $"{this.Label}: {this.Value}";
}

/// <summary>Presentation-only adapter for one sealed confirmed plan and its optional recovery owner.</summary>
internal sealed class ExecutionViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ExecutionController Controller;
    private readonly Func<bool> HasUiThreadAccess;
    private readonly Action<Action> PostToUiThread;
    private readonly Action EnsureDiagnosticLoggingReady;
    private readonly object SnapshotDispatchSync = new();
    private ExecutionSnapshot snapshot;
    private ExecutionSnapshot? pendingSnapshot;
    private bool snapshotDispatchScheduled;
    private string heading = "Ready to run";
    private string message = "The plan is confirmed. No files have changed.";
    private string liveAnnouncement = "Ready to run. No files have changed.";
    private string stageAnnouncement = "No operation has started.";
    private string progressDetail = "No operation has started.";
    private readonly IReadOnlyList<ExecutionFactRow> confirmationRows;
    private IReadOnlyList<ExecutionFactRow> resultRows = Array.Empty<ExecutionFactRow>();
    private TransactionStage? announcedStage;
    private bool disposed;

    public ExecutionViewModel(
        ExecutionController controller,
        Func<bool>? hasUiThreadAccess = null,
        Action<Action>? postToUiThread = null,
        Action? ensureDiagnosticLoggingReady = null
    )
    {
        this.Controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.HasUiThreadAccess = hasUiThreadAccess ?? Dispatcher.UIThread.CheckAccess;
        this.PostToUiThread = postToUiThread ?? (action => Dispatcher.UIThread.Post(action));
        this.EnsureDiagnosticLoggingReady = ensureDiagnosticLoggingReady ?? (() => { });
        this.snapshot = controller.Snapshot;
        this.confirmationRows = CreateConfirmationRows(this.snapshot.Plan);
        this.RunCommand = new(() => this.StartMutation(controller.RunAsync), () => this.snapshot.CanRun, this.HandlePresentationFailure);
        this.CancelCommand = new(this.CancelOrCloseAsync, this.CanCancelOrClose, this.HandleCancellationFailure);
        this.RecoverCommand = new(() => this.StartMutation(() => controller.RecoverAsync()), () => this.snapshot.CanRecover, this.HandlePresentationFailure);
        this.ExitCommand = new(() => this.CloseRequested?.Invoke(this, EventArgs.Empty), () => this.snapshot.CanExit);
        this.Controller.Changed += this.OnControllerChanged;
        this.ApplySnapshot(this.snapshot, requestFocus: false);
    }

    public event EventHandler<ExecutionFocusTarget>? FocusRequested;
    public event EventHandler? CloseRequested;

    public string OperationLabel => GetOperationLabel(this.snapshot.Plan.Operation);
    public string RunLabel => this.snapshot.Plan.Operation == InstallerOperation.Rollback ? "_Run rollback" : "_Run operation";
    public string RunAccessibleName => this.snapshot.Plan.Operation == InstallerOperation.Rollback
        ? "Run the exact confirmed rollback"
        : "Run the exact confirmed operation";
    public string Heading { get => this.heading; private set => this.SetProperty(ref this.heading, value); }
    public string Message { get => this.message; private set => this.SetProperty(ref this.message, value); }
    public string LiveAnnouncement { get => this.liveAnnouncement; private set => this.SetProperty(ref this.liveAnnouncement, value); }
    public string StageAnnouncement { get => this.stageAnnouncement; private set => this.SetProperty(ref this.stageAnnouncement, value); }
    public string ProgressDetail { get => this.progressDetail; private set => this.SetProperty(ref this.progressDetail, value); }
    public IReadOnlyList<ExecutionFactRow> ConfirmationRows => this.confirmationRows;
    public IReadOnlyList<ExecutionFactRow> ResultRows { get => this.resultRows; private set => this.SetProperty(ref this.resultRows, value); }

    public string BoundaryDetail => this.snapshot.State == ExecutionState.Ready
        ? $"Final confirmation. Nothing runs until you choose {(this.snapshot.Plan.Operation == InstallerOperation.Rollback ? "Run rollback" : "Run operation")}. Cancel is the recommended default."
        : "This screen reports bounded local installer progress and durable results. Keep it open while an operation is active.";

    public string PlanDetail => $"Confirmed operation: {this.OperationLabel}. Intended result: {FormatResultTarget(this.snapshot.Plan.IntendedResult)}. {this.snapshot.Plan.OperationCounts.Sum(item => item.Count)} planned file action(s).";

    public bool IsReady => this.snapshot.State == ExecutionState.Ready;
    public bool IsBusy => this.snapshot.State is ExecutionState.Starting or ExecutionState.Running or ExecutionState.CancellationRequested or ExecutionState.RecoveryStarting or ExecutionState.RecoveryCancellationRequested or ExecutionState.RecoveryRunning or ExecutionState.Disposing;
    public bool IsProgressVisible => this.snapshot.State is ExecutionState.Starting or ExecutionState.Running or ExecutionState.CancellationRequested or ExecutionState.RecoveryStarting or ExecutionState.RecoveryCancellationRequested or ExecutionState.RecoveryRunning;
    public bool IsProgressIndeterminate => this.snapshot.TotalUnits is null or <= 0;
    public bool HasProgressStage => this.snapshot.ProgressStage is not null;
    public double ProgressMaximum => Math.Max(1, this.snapshot.TotalUnits ?? 1);
    public double ProgressValue => Math.Clamp(this.snapshot.CompletedUnits, 0, this.snapshot.TotalUnits is > 0 ? this.snapshot.TotalUnits.Value : 1);
    public bool IsRunVisible => this.snapshot.State == ExecutionState.Ready;
    public bool IsCancelVisible => this.snapshot.State is ExecutionState.Ready or ExecutionState.Starting or ExecutionState.Running or ExecutionState.CancellationRequested or ExecutionState.RecoveryStarting or ExecutionState.RecoveryCancellationRequested or ExecutionState.RecoveryRequired;
    public bool IsRecoverVisible => this.snapshot.CanRecover;
    public bool IsExitVisible => this.snapshot.CanExit;
    public bool IsResultVisible => this.snapshot.State is ExecutionState.Terminal or ExecutionState.RecoveryCompleted;
    public bool IsProblemVisible => this.snapshot.State is ExecutionState.RecoveryRequired or ExecutionState.PrestartFault;
    public bool IsSettlementWarningVisible => HasUnconfirmedSettlement(this.snapshot);
    public string SettlementWarning => SettlementWarningCopy;
    public string CancelLabel => this.snapshot.State switch
    {
        ExecutionState.Ready => "_Cancel",
        ExecutionState.Starting or ExecutionState.Running => "_Cancel operation",
        ExecutionState.CancellationRequested => "Cancellation requested",
        ExecutionState.RecoveryStarting => "_Cancel recovery preparation",
        ExecutionState.RecoveryCancellationRequested => "Recovery cancellation requested",
        ExecutionState.RecoveryRequired => "_Close installer",
        _ => "_Cancel"
    };
    public string CancelAccessibleName => this.snapshot.State switch
    {
        ExecutionState.Ready => "Cancel and close without running the confirmed plan",
        ExecutionState.Starting or ExecutionState.Running => "Request safe operation cancellation",
        ExecutionState.CancellationRequested => "Operation cancellation already requested",
        ExecutionState.RecoveryStarting => "Cancel recovery preparation before admission",
        ExecutionState.RecoveryCancellationRequested => "Recovery preparation cancellation already requested",
        ExecutionState.RecoveryRequired => "Close installer without starting recovery",
        _ => "Cancel safely"
    };
    public string CancelHelpText => this.snapshot.State switch
    {
        ExecutionState.Ready => "Closes this screen without starting the confirmed installer operation.",
        ExecutionState.Starting or ExecutionState.Running => "Requests cancellation without killing the installer. The result may be unchanged, fully rolled back, or committed if the final safe checkpoint already passed.",
        ExecutionState.CancellationRequested => "Cancellation is already requested. Keep this window open until the exact durable result appears.",
        ExecutionState.RecoveryStarting => "Requests cancellation only while recovery is still being prepared. Recovery cannot be stopped after admission.",
        ExecutionState.RecoveryCancellationRequested => "Recovery preparation cancellation is already requested. Wait for the exact result.",
        ExecutionState.RecoveryRequired => "Closes this screen without running the available recovery action.",
        _ => "Cancels the current safe action when available."
    };
    public AutomationLiveSetting StageLiveSetting => this.HasProgressStage && this.snapshot.State is ExecutionState.Running or ExecutionState.RecoveryRunning
        ? AutomationLiveSetting.Polite
        : AutomationLiveSetting.Off;
    public AutomationLiveSetting StatusLiveSetting => this.IsProblemVisible || this.IsResultVisible || this.StageLiveSetting != AutomationLiveSetting.Off
        ? AutomationLiveSetting.Off
        : AutomationLiveSetting.Polite;
    public AutomationLiveSetting ResultLiveSetting => IsAssertiveTerminal(this.snapshot)
        ? AutomationLiveSetting.Assertive
        : AutomationLiveSetting.Polite;
    internal ExecutionFocusTarget InitialFocusTarget => GetFocusTarget(this.snapshot);

    public AsyncRelayCommand RunCommand { get; }
    public AsyncRelayCommand CancelCommand { get; }
    public AsyncRelayCommand RecoverCommand { get; }
    public RelayCommand ExitCommand { get; }

    public async ValueTask DisposeAsync()
    {
        lock (this.SnapshotDispatchSync)
        {
            if (this.disposed)
                return;
            this.disposed = true;
            this.pendingSnapshot = null;
        }
        this.Controller.Changed -= this.OnControllerChanged;
        await this.Controller.DisposeAsync().ConfigureAwait(true);
    }

    /// <summary>Apply the window-close/Escape contract without exposing backend ownership to the window.</summary>
    internal Task<bool> PrepareToCloseAsync()
    {
        ExecutionSnapshot latest = this.Controller.Snapshot;
        if (latest.Revision > this.snapshot.Revision)
            this.ApplySnapshot(latest, requestFocus: false);
        switch (this.snapshot.State)
        {
            case ExecutionState.Starting:
            case ExecutionState.Running:
            case ExecutionState.RecoveryStarting:
                try { _ = this.ObserveCancellationAsync(this.Controller.RequestCancellationAsync()); }
                catch { this.HandleCancellationFailure(new InvalidOperationException()); }
                return Task.FromResult(false);
            case ExecutionState.CancellationRequested:
                this.LiveAnnouncement = "Cancellation was already requested. Keep this window open while the exact operation settles.";
                this.FocusRequested?.Invoke(this, ExecutionFocusTarget.Status);
                return Task.FromResult(false);
            case ExecutionState.RecoveryCancellationRequested:
                this.LiveAnnouncement = "Recovery preparation cancellation was already requested. Wait to confirm whether recovery was admitted.";
                this.FocusRequested?.Invoke(this, ExecutionFocusTarget.Status);
                return Task.FromResult(false);
            case ExecutionState.RecoveryRunning:
                this.LiveAnnouncement = "Recovery cannot be stopped after it starts. Wait for a durable result.";
                this.FocusRequested?.Invoke(this, ExecutionFocusTarget.Status);
                return Task.FromResult(false);
            case ExecutionState.Disposing:
                return Task.FromResult(false);
            default:
                return Task.FromResult(true);
        }
    }

    internal void ApplySnapshotForTesting(ExecutionSnapshot value)
        => this.ApplySnapshot(value, requestFocus: false);

    internal void QueueSnapshotForTesting(ExecutionSnapshot value)
        => this.QueueSnapshot(value);

    private bool CanCancelOrClose()
        => this.snapshot.State is ExecutionState.Ready
            or ExecutionState.Starting
            or ExecutionState.Running
            or ExecutionState.RecoveryStarting
            or ExecutionState.RecoveryRequired;

    private Task CancelOrCloseAsync()
    {
        if (this.snapshot.State is ExecutionState.Ready or ExecutionState.RecoveryRequired)
        {
            this.CloseRequested?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
        return this.Controller.RequestCancellationAsync();
    }

    private Task StartMutation(Func<Task> start)
    {
        this.EnsureDiagnosticLoggingReady();
        return start();
    }

    private async Task ObserveCancellationAsync(Task request)
    {
        try { await request.ConfigureAwait(true); }
        catch { this.HandleCancellationFailure(new InvalidOperationException()); }
    }

    private void OnControllerChanged(object? sender, EventArgs e)
        => this.QueueSnapshot(this.Controller.Snapshot);

    private void QueueSnapshot(ExecutionSnapshot next)
    {
        bool hasUiThreadAccess = this.HasUiThreadAccess();
        bool schedule = false;
        lock (this.SnapshotDispatchSync)
        {
            if (this.disposed || next.Revision < this.snapshot.Revision || this.pendingSnapshot is { } pending && next.Revision < pending.Revision)
                return;
            this.pendingSnapshot = next;
            if (!hasUiThreadAccess && !this.snapshotDispatchScheduled)
            {
                this.snapshotDispatchScheduled = true;
                schedule = true;
            }
        }

        if (hasUiThreadAccess)
            this.DrainPendingSnapshot(completingScheduledPost: false);
        else if (schedule)
            this.PostToUiThread(() => this.DrainPendingSnapshot(completingScheduledPost: true));
    }

    private void DrainPendingSnapshot(bool completingScheduledPost)
    {
        ExecutionSnapshot? next;
        lock (this.SnapshotDispatchSync)
        {
            if (completingScheduledPost)
                this.snapshotDispatchScheduled = false;
            if (this.disposed)
            {
                this.pendingSnapshot = null;
                return;
            }
            next = this.pendingSnapshot;
            this.pendingSnapshot = null;
        }
        if (next is not null)
            this.ApplySnapshot(next, requestFocus: true);
    }

    private void ApplySnapshot(ExecutionSnapshot next, bool requestFocus)
    {
        if (this.disposed && next.State != ExecutionState.Disposed || next.Revision < this.snapshot.Revision)
            return;
        ExecutionState previous = this.snapshot.State;
        if (previous != next.State && next.State is ExecutionState.Starting or ExecutionState.RecoveryStarting)
        {
            this.announcedStage = null;
            this.StageAnnouncement = "";
        }
        this.snapshot = next;
        (this.Heading, this.Message) = GetCopy(next);
        this.LiveAnnouncement = CreateLiveAnnouncement(next, this.Heading, this.Message);
        this.ApplyProgress(next);
        this.ResultRows = CreateResultRows(next);
        this.NotifyDerivedProperties();
        if (requestFocus && previous != next.State)
            this.FocusRequested?.Invoke(this, GetFocusTarget(next));
    }

    private void ApplyProgress(ExecutionSnapshot value)
    {
        if (value.ProgressStage is not { } stage)
        {
            this.ProgressDetail = value.State switch
            {
                ExecutionState.Starting => "Preparing the confirmed operation. No progress stage has been reported yet.",
                ExecutionState.RecoveryStarting => "Preparing a fresh authenticated recovery session. Recovery has not been admitted yet.",
                _ => "No active progress stage."
            };
            return;
        }
        string label = GetStageLabel(stage);
        this.ProgressDetail = value.TotalUnits is > 0
            ? $"{label}: {Math.Min(value.CompletedUnits, value.TotalUnits.Value)} of {value.TotalUnits.Value} units reported."
            : $"{label}: {value.CompletedUnits} units reported; the total is not known.";
        if (this.announcedStage != stage)
        {
            this.announcedStage = stage;
            this.StageAnnouncement = $"Installer stage changed to {label}. Intermediate stages may be coalesced.";
        }
    }

    private static (string Heading, string Message) GetCopy(ExecutionSnapshot value)
    {
        string operation = GetOperationLabel(value.Plan.Operation);
        return value.State switch
        {
            ExecutionState.Ready when value.Plan.Operation == InstallerOperation.Rollback => ("Ready to run rollback", "The rollback plan is confirmed. No files have changed. Choose Run rollback to restore the selected previous managed state, or Cancel."),
            ExecutionState.Ready => ($"Ready to run {operation}", "The plan is confirmed. No files have changed. Choose Run operation to begin, or Cancel."),
            ExecutionState.Starting when value.Plan.Operation == InstallerOperation.Rollback => ("Starting rollback…", "Submitting the exact confirmed rollback plan. Cancellation can still be requested safely."),
            ExecutionState.Starting => ($"Starting {operation}…", "Submitting the exact confirmed plan. Cancellation can still be requested safely."),
            ExecutionState.Running when value.Plan.Operation == InstallerOperation.Rollback => ("Rollback is running", "Restoring the selected previous managed state. Keep this window open; progress may coalesce intermediate updates."),
            ExecutionState.Running => ($"{operation} is running", "Keep this window open. Progress may coalesce intermediate updates."),
            ExecutionState.CancellationRequested => ("Cancellation requested — finishing safely", "The result may be unchanged, fully rolled back, or committed if the final safe checkpoint already passed. Keep this window open for the exact durable result."),
            ExecutionState.Terminal => GetExecutionTerminalCopy(value.Plan.Operation, value.ExecutionResult),
            ExecutionState.RecoveryRequired when value.RecoveryResult is not null => GetRecoveryCopy(value.RecoveryResult),
            ExecutionState.RecoveryRequired when value.ExecutionResult is InstallerExecutionStateUnknownResult && !value.CanRecover => ("Installer state could not be confirmed; recovery is required", "A recovery session could not be prepared here. Close this screen and start a fresh installer session; do not retry the original operation."),
            ExecutionState.RecoveryRequired when value.ExecutionResult is InstallerExecutionStateUnknownResult => ("Installer state could not be confirmed; recovery is required", "Do not run another installer action. Explicit recovery uses a fresh authenticated session, revalidates the exact selected target, and cannot be stopped after admission."),
            ExecutionState.RecoveryRequired when !value.CanRecover => ("Recovery is required before another installer action", "A recovery session could not be prepared here. Close this screen and start a fresh installer session; do not retry the original operation."),
            ExecutionState.RecoveryRequired => ("Recovery is required before another installer action", "Run recovery explicitly. A fresh authenticated session revalidates the exact selected target; after admission recovery cannot be cancelled."),
            ExecutionState.RecoveryStarting => ("Preparing interrupted recovery…", "A fresh authenticated session is revalidating the exact target. You may cancel until recovery is admitted."),
            ExecutionState.RecoveryCancellationRequested => ("Recovery preparation cancellation requested", "Waiting to confirm that recovery was not admitted. If admission already won, recovery cannot be stopped."),
            ExecutionState.RecoveryRunning => ("Recovering the interrupted installer operation…", "Recovery has started and cannot be stopped. Wait for a durable result before closing this window."),
            ExecutionState.RecoveryCompleted => GetRecoveryCopy(value.RecoveryResult),
            ExecutionState.CancelledBeforeStart => ($"{operation} cancelled before it started", "No installer operation was admitted. Close and reopen the installer to inspect a fresh plan."),
            ExecutionState.PrestartFault => ("The confirmed operation did not start", "No installer operation was admitted. Close and reopen the installer to inspect a fresh plan."),
            ExecutionState.Disposing => ("Finishing safely…", "Waiting for the active installer owner to reach a durable result and close."),
            ExecutionState.Disposed => ("Installer screen closed", "The local installer owner has been released."),
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
    }

    private static (string Heading, string Message) GetExecutionTerminalCopy(InstallerOperation operation, InstallerExecutionResult? result)
    {
        if (result is InstallerExecutionStateUnknownResult or null)
            return ("Installer state could not be confirmed; recovery is required", "Do not retry the operation. Use interrupted recovery in a fresh authenticated session.");
        InstallerExecutionTerminalResult terminal = (InstallerExecutionTerminalResult)result;
        string operationLabel = GetOperationLabel(operation);
        return terminal.Outcome switch
        {
            ProtocolExecutionOutcome.Succeeded when operation == InstallerOperation.Rollback => ("Rollback completed", "The exact terminal reports that the selected previous managed state was restored and committed."),
            ProtocolExecutionOutcome.Succeeded => ($"{operationLabel} completed", "The exact terminal reports that the planned changes committed."),
            ProtocolExecutionOutcome.SucceededWithCleanupWarning => ($"{operationLabel} completed; cleanup is pending", "The planned changes committed, but bounded cleanup remains for a fresh session."),
            ProtocolExecutionOutcome.FailedBeforeMutation => ($"{operationLabel} failed before changing files", $"No mutation was reported. {GetErrorAction(terminal.ErrorCode)}"),
            ProtocolExecutionOutcome.CancelledBeforeMutation => ($"{operationLabel} cancelled before changing files", "The exact terminal reports an unchanged durable state."),
            ProtocolExecutionOutcome.CancelledAndRolledBack => ("Cancellation completed and changes were rolled back", "The exact terminal reports a rolled-back durable state."),
            ProtocolExecutionOutcome.FailedAndRolledBack => ($"{operationLabel} failed and changes were rolled back", $"The exact terminal reports rollback completed. {GetErrorAction(terminal.ErrorCode)}"),
            ProtocolExecutionOutcome.InterruptedRecoveryRequired => ("Recovery is required before another installer action", "The operation did not reach a safe final state. Run interrupted recovery explicitly."),
            ProtocolExecutionOutcome.AutomaticRecoveryCompletedFreshInspectionRequired => ("Recovery completed; inspect again", "The prior interrupted state was recovered. Start a fresh verified session and inspect the operation again."),
            ProtocolExecutionOutcome.UnexpectedCoreFailure => ("Installer state could not be confirmed; recovery is required", "Do not retry the operation. Run interrupted recovery explicitly."),
            _ => throw new ArgumentOutOfRangeException(nameof(terminal.Outcome))
        };
    }

    private static string CreateLiveAnnouncement(ExecutionSnapshot value, string heading, string message)
        => HasUnconfirmedSettlement(value)
            ? $"{heading}. {message} {SettlementWarningCopy}"
            : $"{heading}. {message}";

    private static bool HasUnconfirmedSettlement(ExecutionSnapshot value)
        => value.RecoveryResult is not null
            ? value.RecoveryResult is InstallerRecoveryTerminalResult { BackendSettlement: InstallerBackendSettlement.Unconfirmed }
            : value.ExecutionResult is InstallerExecutionTerminalResult { BackendSettlement: InstallerBackendSettlement.Unconfirmed };

    private const string SettlementWarningCopy = "The durable result above is exact, but backend shutdown could not be confirmed. Continue only in a fresh installer session.";

    private static (string Heading, string Message) GetRecoveryCopy(InstallerRecoveryResult? result)
    {
        if (result is InstallerRecoveryStateUnknownResult or null)
            return ("Recovery state could not be confirmed", "Recovery is still required. Try recovery again; every attempt uses a fresh authenticated session.");
        InstallerRecoveryTerminalResult terminal = (InstallerRecoveryTerminalResult)result;
        return terminal.Outcome switch
        {
            ProtocolInterruptedRecoveryOutcome.RecoveryCompleted when terminal.NextAction == ProtocolNextAction.InspectAgain => ("Recovery completed — inspect again", "Start a fresh verified session and inspect a new plan for the same selected game."),
            ProtocolInterruptedRecoveryOutcome.RecoveryCompleted when terminal.NextAction == ProtocolNextAction.SelectGameFolder => ("Recovery completed — select a game folder", "Start a fresh verified session and choose a game folder before inspecting another plan."),
            ProtocolInterruptedRecoveryOutcome.CancelledBeforeRecovery => ("Recovery did not begin", "The interrupted state still requires recovery. Run recovery again when ready."),
            ProtocolInterruptedRecoveryOutcome.PartialFailure => ("Recovery is incomplete", $"Some bounded recovery work completed, but recovery is still required. {GetErrorAction(terminal.ErrorCode)}"),
            ProtocolInterruptedRecoveryOutcome.UnexpectedFailure => ("Recovery state could not be confirmed", "Recovery is still required. Try again in a fresh authenticated recovery session."),
            _ => throw new ArgumentOutOfRangeException(nameof(terminal.Outcome))
        };
    }

    private static IReadOnlyList<ExecutionFactRow> CreateResultRows(ExecutionSnapshot value)
    {
        if (value.RecoveryResult is InstallerRecoveryTerminalResult recovery)
        {
            List<ExecutionFactRow> rows =
            [
                new("Durable state", GetDurableStateLabel(recovery.DurableState)),
                new("Recovery disposition", GetRecoveryDispositionLabel(recovery.RecoveryDisposition)),
                new("Next safe action", GetNextActionLabel(recovery.NextAction))
            ];
            if (recovery.Attempt is { } attempt)
            {
                rows.Add(new("Recovered transactions", attempt.RecoveredTransactionCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                rows.Add(new("Recovered paths", attempt.RecoveredPathCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            return rows.AsReadOnly();
        }
        if (value.ExecutionResult is InstallerExecutionTerminalResult execution)
        {
            List<ExecutionFactRow> rows =
            [
                new("Durable state", GetDurableStateLabel(execution.DurableState)),
                new("Recovery disposition", GetRecoveryDispositionLabel(execution.RecoveryDisposition)),
                new("Next safe action", GetNextActionLabel(execution.NextAction))
            ];
            if (IsCommitted(execution))
            {
                rows.Add(new("Resulting managed state", FormatResultTarget(value.Plan.IntendedResult)));
                foreach (PlanReviewOperationCount operation in value.Plan.OperationCounts)
                    rows.Add(new(GetCommittedOperationLabel(operation.Kind), operation.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                if (!value.Plan.OperationCounts.Any(item => item.Kind == PlanOperationKind.Preserve))
                    rows.Add(new("Explicit preserve actions committed", "0"));
                rows.Add(new("Committed game-file scope", "No game files outside the confirmed managed target set were targeted; installer-internal recovery and receipt state was also written under .smapi-installer"));
                if (value.Plan.Operation == InstallerOperation.Backup
                    && value.Plan.CurrentRelease is { } restoreRelease)
                {
                    rows.Add(new("Recovery point created", "Current user checkpoint"));
                    rows.Add(new("Checkpoint restore target", FormatRelease(restoreRelease)));
                }
            }
            AddCount(rows, "Managed files changed", execution.Summary.ManagedFileChangeCount);
            AddCount(rows, "Managed files rolled back", execution.Summary.RolledBackManagedFileCount);
            AddCount(rows, "Installer-state changes", execution.Summary.InternalStateChangeCount);
            AddCount(rows, "Installer-state changes rolled back", execution.Summary.RolledBackInternalStateCount);
            AddCount(rows, "Recovered transactions", execution.Summary.RecoveredTransactionCount);
            AddCount(rows, "Recovered paths", execution.Summary.RecoveredPathCount);
            return rows.AsReadOnly();
        }
        return Array.Empty<ExecutionFactRow>();
    }

    private static IReadOnlyList<ExecutionFactRow> CreateConfirmationRows(ExecutionPlanPresentation plan)
    {
        List<ExecutionFactRow> rows =
        [
            new("Selected game", plan.GameDisplayName),
            new("Confirmed operation", GetOperationLabel(plan.Operation)),
            new("Current managed release", plan.CurrentRelease is null ? "No receipt-authenticated current fork release" : FormatRelease(plan.CurrentRelease)),
            new("Intended result if committed", FormatResultTarget(plan.IntendedResult)),
            new("Observed relationship", FormatRelationship(plan.Relationship)),
            new("Recovery location", ".smapi-installer/recovery inside the selected game folder"),
            new("Recovery capacity", $"{plan.RecoveryCapacity.Used} of {plan.RecoveryCapacity.Maximum} slots used; {plan.RecoveryCapacity.Remaining} remaining"),
            new("Pre-change recovery point", "A committed run creates one recovery point"),
            new("Confirmed game-file scope", "Only game-file paths in the exact confirmed plan are targeted; they are derived from verified package content, receipt-authenticated ownership or launcher state, and exact approvals. Installer-internal recovery and ownership state is also written under .smapi-installer"),
            new("Other game files", "Outside this confirmed plan and not targeted")
        ];
        foreach (PlanOperationKind kind in Enum.GetValues<PlanOperationKind>())
        {
            int count = plan.OperationCounts.SingleOrDefault(item => item.Kind == kind)?.Count ?? 0;
            rows.Add(new($"Planned {GetOperationLabel(kind)} actions", count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        foreach (PlanReviewPathFact fact in plan.PathFacts)
        {
            string detail = fact.FactKind switch
            {
                PlanReviewPathFactKind.MissingReceiptOwned => $"{fact.DisplayPath} — missing; create from verified package content",
                PlanReviewPathFactKind.ApprovedModifiedReceiptOwned => $"{fact.DisplayPath} — explicitly approved modified receipt-owned file; include in recovery point, then {GetOperationLabel(fact.PlannedAction).ToLowerInvariant()}",
                PlanReviewPathFactKind.ApprovedModifiedInstalledLauncher => $"{fact.DisplayPath} — explicitly approved installed launcher with a changed recorded identity; include in recovery point, then {GetOperationLabel(fact.PlannedAction).ToLowerInvariant()}",
                _ => throw new ArgumentOutOfRangeException(nameof(fact))
            };
            rows.Add(new("Managed path", detail));
        }
        if (plan.AdditionalPathFactCount > 0)
            rows.Add(new("Additional managed paths omitted", plan.AdditionalPathFactCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (plan.Operation == InstallerOperation.Backup)
            rows.Add(new("Backup scope", "Installer-managed state only; Mods and saves are not included"));
        return rows.AsReadOnly();
    }

    private static bool IsCommitted(InstallerExecutionTerminalResult terminal)
        => terminal.DurableState == ProtocolDurableState.Committed
            && terminal.Outcome is ProtocolExecutionOutcome.Succeeded or ProtocolExecutionOutcome.SucceededWithCleanupWarning;

    private static string FormatRelease(ExecutionReleasePresentation release)
        => $"{release.Tag} • Version: {release.Version}";

    private static string FormatResultTarget(ExecutionResultTarget target) => target switch
    {
        ExecutionReleaseTarget release => FormatRelease(release.Release),
        ExecutionUninstalledTarget => "No managed SMAPI installation",
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };

    private static string FormatRelationship(PlanReviewReleaseRelationship? relationship) => relationship switch
    {
        PlanReviewReleaseRelationship.Current => "Same version",
        PlanReviewReleaseRelationship.Upgrade => "Upgrade",
        PlanReviewReleaseRelationship.Downgrade => "Downgrade",
        null => "Not applicable",
        _ => throw new ArgumentOutOfRangeException(nameof(relationship))
    };

    private static string GetCommittedOperationLabel(PlanOperationKind kind) => kind switch
    {
        PlanOperationKind.Backup => "Backup actions committed",
        PlanOperationKind.Remove => "Remove actions committed",
        PlanOperationKind.Restore => "Restore actions committed",
        PlanOperationKind.Create => "Create actions committed",
        PlanOperationKind.Replace => "Replace actions committed",
        PlanOperationKind.Retain => "Retain actions committed",
        PlanOperationKind.Preserve => "Explicit preserve actions committed",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string GetOperationLabel(PlanOperationKind kind) => kind switch
    {
        PlanOperationKind.Backup => "Back up",
        PlanOperationKind.Remove => "Remove",
        PlanOperationKind.Restore => "Restore",
        PlanOperationKind.Create => "Create",
        PlanOperationKind.Replace => "Replace",
        PlanOperationKind.Retain => "Retain",
        PlanOperationKind.Preserve => "Preserve",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static void AddCount(List<ExecutionFactRow> rows, string label, int? count)
    {
        if (count is { } value)
            rows.Add(new(label, value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static ExecutionFocusTarget GetFocusTarget(ExecutionSnapshot value) => value.State switch
    {
        ExecutionState.Ready => ExecutionFocusTarget.Cancel,
        ExecutionState.Starting or ExecutionState.Running or ExecutionState.CancellationRequested or ExecutionState.RecoveryStarting or ExecutionState.RecoveryCancellationRequested or ExecutionState.RecoveryRunning or ExecutionState.Disposing => ExecutionFocusTarget.Status,
        ExecutionState.RecoveryRequired => value.CanRecover ? ExecutionFocusTarget.Cancel : ExecutionFocusTarget.Problem,
        ExecutionState.Terminal or ExecutionState.RecoveryCompleted => ExecutionFocusTarget.Result,
        ExecutionState.CancelledBeforeStart or ExecutionState.PrestartFault => ExecutionFocusTarget.Exit,
        _ => ExecutionFocusTarget.Status
    };

    private void NotifyDerivedProperties()
    {
        foreach (string property in new[]
        {
            nameof(this.OperationLabel), nameof(this.BoundaryDetail), nameof(this.PlanDetail), nameof(this.IsReady), nameof(this.IsBusy),
            nameof(this.IsProgressVisible), nameof(this.IsProgressIndeterminate), nameof(this.ProgressMaximum), nameof(this.ProgressValue),
            nameof(this.HasProgressStage),
            nameof(this.IsRunVisible), nameof(this.IsCancelVisible), nameof(this.IsRecoverVisible), nameof(this.IsExitVisible),
            nameof(this.IsResultVisible), nameof(this.IsProblemVisible), nameof(this.IsSettlementWarningVisible), nameof(this.CancelLabel),
            nameof(this.CancelAccessibleName), nameof(this.CancelHelpText),
            nameof(this.StatusLiveSetting), nameof(this.ResultLiveSetting), nameof(this.StageLiveSetting)
        })
            this.OnPropertyChanged(property);
        this.RunCommand.NotifyCanExecuteChanged();
        this.CancelCommand.NotifyCanExecuteChanged();
        this.RecoverCommand.NotifyCanExecuteChanged();
        this.ExitCommand.NotifyCanExecuteChanged();
    }

    private void HandlePresentationFailure(Exception error)
    {
        bool diagnosticsUnavailable = error is InstallerDiagnosticsUnavailableException;
        this.Heading = diagnosticsUnavailable
            ? "Private diagnostic logging is unavailable"
            : "The installer action could not be presented safely";
        this.Message = diagnosticsUnavailable
            ? "No operation was started. Close this installer, make sure another installer is not open, and retry as your normal desktop user."
            : "Keep this window open if work may be active. No private backend detail is shown here.";
        this.LiveAnnouncement = $"{this.Heading}. {this.Message}";
        this.FocusRequested?.Invoke(this, ExecutionFocusTarget.Problem);
    }

    private void HandleCancellationFailure(Exception _)
    {
        this.Heading = "Cancellation request could not be confirmed";
        this.Message = "Keep this window open. The installer is still waiting for the exact operation result.";
        this.LiveAnnouncement = $"{this.Heading}. {this.Message}";
        this.FocusRequested?.Invoke(this, ExecutionFocusTarget.Status);
    }

    private static bool IsAssertiveTerminal(ExecutionSnapshot value)
        => value.ExecutionResult is InstallerExecutionTerminalResult
        {
            Outcome: ProtocolExecutionOutcome.FailedBeforeMutation
                or ProtocolExecutionOutcome.FailedAndRolledBack
                or ProtocolExecutionOutcome.InterruptedRecoveryRequired
                or ProtocolExecutionOutcome.UnexpectedCoreFailure
        }
        || value.RecoveryResult is InstallerRecoveryTerminalResult
        {
            Outcome: ProtocolInterruptedRecoveryOutcome.PartialFailure
                or ProtocolInterruptedRecoveryOutcome.UnexpectedFailure
        };

    private static string GetOperationLabel(InstallerOperation operation) => operation switch
    {
        InstallerOperation.Install => "Install",
        InstallerOperation.Update => "Update",
        InstallerOperation.Repair => "Repair",
        InstallerOperation.Uninstall => "Uninstall",
        InstallerOperation.Backup => "Backup",
        InstallerOperation.Rollback => "Rollback",
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static string GetStageLabel(TransactionStage stage) => stage switch
    {
        TransactionStage.AcquiringLock => "Acquiring installer lock",
        TransactionStage.Recovering => "Recovering interrupted work",
        TransactionStage.Staging => "Staging verified changes",
        TransactionStage.Revalidating => "Revalidating the plan",
        TransactionStage.Applying => "Applying planned changes",
        TransactionStage.Verifying => "Verifying changed files",
        TransactionStage.Committing => "Committing installer state",
        TransactionStage.RollingBack => "Rolling back changes",
        TransactionStage.Completed => "Completing",
        TransactionStage.Inspecting => "Inspecting local state",
        TransactionStage.VerifyingRecovery => "Verifying recovery data",
        TransactionStage.PreparingRecovery => "Preparing recovery",
        TransactionStage.PreparingPayload => "Preparing verified payload",
        TransactionStage.WritingFiles => "Writing managed files",
        TransactionStage.RemovingFiles => "Removing managed files",
        TransactionStage.UpdatingLauncher => "Updating the launcher",
        TransactionStage.UpdatingInstallerState => "Updating installer state",
        TransactionStage.PublishingRecovery => "Publishing recovery state",
        TransactionStage.CleaningRecovery => "Cleaning recovery data",
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };

    private static string GetDurableStateLabel(ProtocolDurableState state) => state switch
    {
        ProtocolDurableState.Committed => "Committed",
        ProtocolDurableState.Unchanged => "Unchanged",
        ProtocolDurableState.RolledBack => "Rolled back",
        ProtocolDurableState.RecoveryRequired => "Recovery required",
        ProtocolDurableState.RecoveryCompleted => "Recovery completed",
        ProtocolDurableState.Unknown => "Unknown — recovery required",
        ProtocolDurableState.PruneApplied => "Recovery cleanup applied",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    private static string GetRecoveryDispositionLabel(ProtocolRecoveryDisposition disposition) => disposition switch
    {
        ProtocolRecoveryDisposition.NotRequired => "Not required",
        ProtocolRecoveryDisposition.CleanupPending => "Cleanup pending",
        ProtocolRecoveryDisposition.Completed => "Completed",
        ProtocolRecoveryDisposition.InterruptedRecoveryRequired => "Interrupted recovery required",
        ProtocolRecoveryDisposition.StateRefreshRequired => "Fresh inspection required",
        _ => throw new ArgumentOutOfRangeException(nameof(disposition))
    };

    private static string GetNextActionLabel(ProtocolNextAction action) => action switch
    {
        ProtocolNextAction.RetryRequest => "Retry the request",
        ProtocolNextAction.SelectGameFolder => "Select a game folder in a fresh session",
        ProtocolNextAction.ReopenVerifiedPackage => "Reopen the verified release",
        ProtocolNextAction.InspectAgain => "Inspect a fresh plan",
        ProtocolNextAction.ListRecoveries => "List recovery points",
        ProtocolNextAction.RecoverInterrupted => "Recover the interrupted operation",
        ProtocolNextAction.StartNewSession => "Start a fresh installer session",
        ProtocolNextAction.ReviewFilesystem => "Review the filesystem",
        ProtocolNextAction.ViewPrivateLog => "Review the private local log outside this screen",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private static string GetErrorAction(ProtocolTerminalErrorCode? error) => error switch
    {
        null => "Start a fresh verified session before another action.",
        ProtocolTerminalErrorCode.DiskFull => "Free disk space, then start a fresh verified session.",
        ProtocolTerminalErrorCode.ReadOnlyFileSystem => "Check that the game filesystem is writable by your user; do not run as root.",
        ProtocolTerminalErrorCode.PermissionDenied => "Check user permissions for the game folder; do not run as root.",
        ProtocolTerminalErrorCode.ConcurrentOperation => "Close other installer sessions before starting a fresh session.",
        ProtocolTerminalErrorCode.CrossDeviceBoundary => "Keep the game and installer recovery workspace on a supported filesystem boundary.",
        ProtocolTerminalErrorCode.InvalidPlan or ProtocolTerminalErrorCode.UnsafePath or ProtocolTerminalErrorCode.PathChanged
            or ProtocolTerminalErrorCode.ExistingFileMismatch or ProtocolTerminalErrorCode.PayloadMismatch
            or ProtocolTerminalErrorCode.WorkspaceConflict or ProtocolTerminalErrorCode.RecoveryFailed
            or ProtocolTerminalErrorCode.IoFailure or ProtocolTerminalErrorCode.UnexpectedCoreFailure
            => "Start a fresh verified session and follow the reported next safe action.",
        _ => throw new ArgumentOutOfRangeException(nameof(error))
    };
}
