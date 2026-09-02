using System.Globalization;
using Avalonia.Automation;
using Avalonia.Threading;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Core.Transactions;
using StardewModdingAPI.Installer.Gui.Backend;
using StardewModdingAPI.Installer.Gui.Frontend;

namespace StardewModdingAPI.Installer.Gui.ViewModels;

internal enum RecoveryPruneFocusTarget
{
    Load,
    HistoryList,
    Status,
    Inspect,
    Review,
    Consent,
    Confirm,
    Cancel,
    Run,
    Result,
    Error,
    Exit
}

/// <summary>Sanitized display wrapper around one exact controller-minted recovery choice.</summary>
internal sealed class RecoveryPruneChoiceItem
{
    internal RecoveryPruneChoiceItem(RecoveryPruneChoice choice, string title, string detail)
    {
        this.Choice = choice ?? throw new ArgumentNullException(nameof(choice));
        this.Title = title ?? throw new ArgumentNullException(nameof(title));
        this.Detail = detail ?? throw new ArgumentNullException(nameof(detail));
        this.AccessibleName = $"{this.Title}. {this.Detail.Replace('\n', ' ')}";
    }

    internal RecoveryPruneChoice Choice { get; }

    public int Ordinal => this.Choice.Ordinal;

    public bool IsCurrent => this.Choice.IsCurrent;

    public bool IsUserCheckpoint => this.Choice.IsUserCheckpoint;

    public string Title { get; }

    public string Detail { get; }

    public string AccessibleName { get; }
}

internal sealed record RecoveryPruneFactRow(string Label, string Value)
{
    public string AccessibleName => $"{this.Label}: {this.Value}";
}

/// <summary>Presentation-only adapter for bounded recovery-history cleanup.</summary>
internal sealed class RecoveryPruneViewModel : ObservableObject, IAsyncDisposable
{
    private readonly RecoveryPruneController Controller;
    private readonly Func<bool> HasUiThreadAccess;
    private readonly Action<Action> PostToUiThread;
    private readonly object SnapshotDispatchSync = new();
    private RecoveryPruneSnapshot snapshot;
    private RecoveryPruneSnapshot? pendingSnapshot;
    private bool snapshotDispatchScheduled;
    private IReadOnlyList<RecoveryPruneChoiceItem> choices = Array.Empty<RecoveryPruneChoiceItem>();
    private RecoveryPruneChoiceItem? selectedChoice;
    private IReadOnlyList<RecoveryPruneFactRow> planRows = Array.Empty<RecoveryPruneFactRow>();
    private IReadOnlyList<RecoveryPruneFactRow> resultRows = Array.Empty<RecoveryPruneFactRow>();
    private string heading = "Load recovery history";
    private string message = "Nothing is loaded automatically. Loading reads bounded local recovery history and changes no files.";
    private string liveAnnouncement = "Load recovery history. Nothing is loaded automatically and no files are changed.";
    private string stageAnnouncement = "No recovery cleanup has started.";
    private string progressDetail = "No recovery cleanup has started.";
    private bool isDestructiveConsentChecked;
    private bool disposed;

    public RecoveryPruneViewModel(
        RecoveryPruneController controller,
        Func<bool>? hasUiThreadAccess = null,
        Action<Action>? postToUiThread = null
    )
    {
        this.Controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.HasUiThreadAccess = hasUiThreadAccess ?? Dispatcher.UIThread.CheckAccess;
        this.PostToUiThread = postToUiThread ?? (action => Dispatcher.UIThread.Post(action));
        this.snapshot = controller.Snapshot;
        this.ListCommand = new(() => controller.ListRecoveriesAsync(), () => this.snapshot.CanList, this.HandleActionFailure);
        this.InspectCommand = new(() => controller.InspectAsync(), () => this.snapshot.CanInspect, this.HandleActionFailure);
        this.ConfirmCommand = new(
            () => controller.ConfirmAsync(RecoveryPruneConsent.ConfirmDestructiveCleanup),
            () => this.snapshot.CanConfirm && this.IsDestructiveConsentChecked,
            this.HandleActionFailure
        );
        this.RunCommand = new(controller.RunAsync, () => this.snapshot.CanRun, this.HandleActionFailure);
        this.CancelCommand = new(this.CancelOrCloseAsync, this.CanCancelOrClose, this.HandleCancellationFailure);
        this.ExitCommand = new(() => this.CloseRequested?.Invoke(this, EventArgs.Empty), () => this.snapshot.CanExit);
        this.Controller.Changed += this.OnControllerChanged;
        this.ApplySnapshot(this.snapshot, requestFocus: false);
    }

    public event EventHandler<RecoveryPruneFocusTarget>? FocusRequested;

    public event EventHandler? CloseRequested;

    public string Heading { get => this.heading; private set => this.SetProperty(ref this.heading, value); }

    public string Message { get => this.message; private set => this.SetProperty(ref this.message, value); }

    public string LiveAnnouncement { get => this.liveAnnouncement; private set => this.SetProperty(ref this.liveAnnouncement, value); }

    public string StageAnnouncement { get => this.stageAnnouncement; private set => this.SetProperty(ref this.stageAnnouncement, value); }

    public string ProgressDetail { get => this.progressDetail; private set => this.SetProperty(ref this.progressDetail, value); }

    public string ReleaseDetail => $"Verified release: {this.snapshot.Release.Tag}\nVersion: {this.snapshot.Release.EmbeddedVersion}";

    public string ReleaseAccessibleName => $"Bound verified release: {this.snapshot.Release.Tag}. Version: {this.snapshot.Release.EmbeddedVersion}";

    public string GameDetail => $"Game: {this.snapshot.Game.DisplayName}\nFolder: {this.snapshot.Game.DisplayPath}";

    public string GameAccessibleName => $"Bound recovery-cleanup target. Game: {this.snapshot.Game.DisplayName}. Folder: {this.snapshot.Game.DisplayPath}";

    public string BoundaryDetail => this.snapshot.State is RecoveryPruneControllerState.ReviewReady or RecoveryPruneControllerState.Confirming
        ? "Review only. Cancel is the recommended default. Confirmation still performs no cleanup; Run cleanup is a separate action."
        : this.snapshot.State == RecoveryPruneControllerState.ReadyToRun
            ? "Confirmed but unchanged. Cancel is the recommended default. Nothing is removed until you choose Run cleanup."
            : "Loading, selecting, and inspecting change no files. Cleanup requires unchecked consent, confirmation, and a separate Run action.";

    public string CleanupScopeWarning => CreateCleanupScopeWarning(this.snapshot.Plan);

    public IReadOnlyList<RecoveryPruneChoiceItem> Choices
    {
        get => this.choices;
        private set => this.SetProperty(ref this.choices, value);
    }

    public RecoveryPruneChoiceItem? SelectedChoice
    {
        get => this.selectedChoice;
        set
        {
            if (ReferenceEquals(value, this.selectedChoice))
                return;
            if (value is not null && !this.Choices.Any(choice => ReferenceEquals(choice, value)))
                throw new ArgumentException("The recovery point must be one exact current choice from this screen.", nameof(value));
            this.Controller.SelectRecoveryPoint(value?.Choice);
        }
    }

    public IReadOnlyList<RecoveryPruneFactRow> PlanRows
    {
        get => this.planRows;
        private set => this.SetProperty(ref this.planRows, value);
    }

    public IReadOnlyList<RecoveryPruneFactRow> ResultRows
    {
        get => this.resultRows;
        private set => this.SetProperty(ref this.resultRows, value);
    }

    public bool IsDestructiveConsentChecked
    {
        get => this.isDestructiveConsentChecked;
        set
        {
            if (value && !this.IsConsentVisible)
                throw new InvalidOperationException("Destructive consent is available only for the current reviewed cleanup plan.");
            if (!this.SetProperty(ref this.isDestructiveConsentChecked, value))
                return;
            this.ConfirmCommand.NotifyCanExecuteChanged();
            if (value)
                this.FocusRequested?.Invoke(this, RecoveryPruneFocusTarget.Confirm);
        }
    }

    public bool IsHistoryListVisible => this.snapshot.State == RecoveryPruneControllerState.CatalogReady && this.Choices.Count > 0;

    public bool IsPlanVisible => this.PlanRows.Count > 0;

    public bool IsBusy => this.snapshot.State is RecoveryPruneControllerState.Listing
        or RecoveryPruneControllerState.Inspecting
        or RecoveryPruneControllerState.Confirming
        or RecoveryPruneControllerState.Starting
        or RecoveryPruneControllerState.Running
        or RecoveryPruneControllerState.CancellationRequested
        or RecoveryPruneControllerState.Disposing;

    public bool IsProgressVisible => this.snapshot.State is RecoveryPruneControllerState.Starting
        or RecoveryPruneControllerState.Running
        or RecoveryPruneControllerState.CancellationRequested;

    public bool IsProgressIndeterminate => this.snapshot.TotalUnits is null or <= 0;

    public bool HasProgressStage => this.snapshot.ProgressStage is not null;

    public double ProgressMaximum => Math.Max(1, this.snapshot.TotalUnits ?? 1);

    public double ProgressValue => Math.Clamp(
        this.snapshot.CompletedUnits,
        0,
        this.snapshot.TotalUnits is > 0 ? this.snapshot.TotalUnits.Value : 1
    );

    public bool IsConsentVisible => this.snapshot.State == RecoveryPruneControllerState.ReviewReady && this.snapshot.Plan is not null;

    public bool IsConfirmVisible => this.snapshot.State == RecoveryPruneControllerState.ReviewReady;

    public bool IsRunVisible => this.snapshot.State == RecoveryPruneControllerState.ReadyToRun;

    public bool IsCancelVisible => this.snapshot.CanCancel
        || this.snapshot.State is RecoveryPruneControllerState.ReviewReady or RecoveryPruneControllerState.ReadyToRun;

    public bool IsExitVisible => this.snapshot.CanExit
        && this.snapshot.State is not (RecoveryPruneControllerState.ReviewReady or RecoveryPruneControllerState.ReadyToRun);

    public bool IsResultVisible => this.snapshot.State == RecoveryPruneControllerState.Terminal;

    public bool IsErrorVisible => this.snapshot.State is RecoveryPruneControllerState.RelistRequired
        or RecoveryPruneControllerState.StateUnknown
        or RecoveryPruneControllerState.Failed
        or RecoveryPruneControllerState.SessionFaulted;

    public bool IsSettlementWarningVisible => this.snapshot.Result is RecoveryPruneTerminalPresentation
    {
        BackendSettlement: InstallerBackendSettlement.Unconfirmed
    };

    public string SettlementWarning => "The backend did not confirm a clean close. Do not repeat cleanup from this window; start a fresh verified installer session and list recovery history again.";

    public AutomationLiveSetting StatusLiveSetting => this.ActiveAnnouncementRegion == AnnouncementRegion.Status
        ? AutomationLiveSetting.Polite
        : AutomationLiveSetting.Off;

    public AutomationLiveSetting ReviewLiveSetting => this.ActiveAnnouncementRegion == AnnouncementRegion.Review
        ? AutomationLiveSetting.Polite
        : AutomationLiveSetting.Off;

    public AutomationLiveSetting StageLiveSetting => this.ActiveAnnouncementRegion == AnnouncementRegion.Stage
        ? AutomationLiveSetting.Polite
        : AutomationLiveSetting.Off;

    public AutomationLiveSetting ResultLiveSetting => this.ActiveAnnouncementRegion == AnnouncementRegion.Result
        ? IsAssertiveTerminal(this.snapshot)
            ? AutomationLiveSetting.Assertive
            : AutomationLiveSetting.Polite
        : AutomationLiveSetting.Off;

    public AutomationLiveSetting ErrorLiveSetting => this.ActiveAnnouncementRegion == AnnouncementRegion.Error
        ? IsAssertiveProblem(this.snapshot.State)
            ? AutomationLiveSetting.Assertive
            : AutomationLiveSetting.Polite
        : AutomationLiveSetting.Off;

    internal RecoveryPruneFocusTarget InitialFocusTarget => GetFocusTarget(this.snapshot);

    public AsyncRelayCommand ListCommand { get; }

    public AsyncRelayCommand InspectCommand { get; }

    public AsyncRelayCommand ConfirmCommand { get; }

    public AsyncRelayCommand RunCommand { get; }

    public AsyncRelayCommand CancelCommand { get; }

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

    /// <summary>Apply the window-close/Escape contract without exposing controller ownership to the window.</summary>
    internal Task<bool> PrepareToCloseAsync()
    {
        RecoveryPruneSnapshot latest = this.Controller.Snapshot;
        if (latest.Revision > this.snapshot.Revision)
            this.ApplySnapshot(latest, requestFocus: false);
        if (this.snapshot.CanCancel)
        {
            try { _ = this.ObserveCancellationAsync(this.Controller.RequestCancellationAsync()); }
            catch { this.HandleCancellationFailure(new InvalidOperationException()); }
            return Task.FromResult(false);
        }
        if (this.snapshot.State == RecoveryPruneControllerState.CancellationRequested)
        {
            this.LiveAnnouncement = "Cancellation was already requested. Keep this window open until the exact request settles.";
            this.FocusRequested?.Invoke(this, RecoveryPruneFocusTarget.Status);
            return Task.FromResult(false);
        }
        if (this.snapshot.State == RecoveryPruneControllerState.Disposing)
            return Task.FromResult(false);
        return Task.FromResult(true);
    }

    internal void ApplySnapshotForTesting(RecoveryPruneSnapshot value)
        => this.ApplySnapshot(value, requestFocus: false);

    internal void QueueSnapshotForTesting(RecoveryPruneSnapshot value)
        => this.QueueSnapshot(value);

    private AnnouncementRegion ActiveAnnouncementRegion => this.snapshot.State switch
    {
        RecoveryPruneControllerState.ReviewReady => AnnouncementRegion.Review,
        RecoveryPruneControllerState.Running when this.snapshot.ProgressStage is not null => AnnouncementRegion.Stage,
        RecoveryPruneControllerState.Terminal => AnnouncementRegion.Result,
        RecoveryPruneControllerState.RelistRequired
            or RecoveryPruneControllerState.StateUnknown
            or RecoveryPruneControllerState.Failed
            or RecoveryPruneControllerState.SessionFaulted => AnnouncementRegion.Error,
        _ => AnnouncementRegion.Status
    };

    private bool CanCancelOrClose()
        => this.snapshot.CanCancel
            || this.snapshot.State is RecoveryPruneControllerState.ReviewReady or RecoveryPruneControllerState.ReadyToRun;

    private Task CancelOrCloseAsync()
    {
        if (this.snapshot.State is RecoveryPruneControllerState.ReviewReady or RecoveryPruneControllerState.ReadyToRun)
        {
            this.CloseRequested?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
        return this.Controller.RequestCancellationAsync();
    }

    private async Task ObserveCancellationAsync(Task cancellation)
    {
        try { await cancellation.ConfigureAwait(true); }
        catch { this.HandleCancellationFailure(new InvalidOperationException()); }
    }

    private void OnControllerChanged(object? sender, EventArgs e)
        => this.QueueSnapshot(this.Controller.Snapshot);

    private void QueueSnapshot(RecoveryPruneSnapshot next)
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
        RecoveryPruneSnapshot? next;
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

    private void ApplySnapshot(RecoveryPruneSnapshot next, bool requestFocus)
    {
        if (this.disposed && next.State != RecoveryPruneControllerState.Disposed)
            return;
        if (next.Revision < this.snapshot.Revision)
            return;

        RecoveryPruneSnapshot previous = this.snapshot;
        this.snapshot = next;
        if (previous.Generation != next.Generation || next.State != RecoveryPruneControllerState.ReviewReady || next.Plan is null)
            this.SetConsentFromSnapshot(false);
        this.ApplyChoices(next);
        this.PlanRows = CreatePlanRows(next.Plan);
        this.ResultRows = CreateResultRows(next.Result);
        (this.Heading, this.Message) = GetCopy(next);
        this.LiveAnnouncement = $"{this.Heading}. {this.Message}";
        this.StageAnnouncement = next.ProgressStage is { } stage
            ? $"Recovery cleanup stage: {GetStageLabel(stage)}."
            : "No recovery cleanup stage has been reported.";
        this.ProgressDetail = next.ProgressStage is { } progressStage
            ? next.TotalUnits is { } total
                ? $"{GetStageLabel(progressStage)}: {FormatNumber(next.CompletedUnits)} of {FormatNumber(total)}."
                : $"{GetStageLabel(progressStage)}: {FormatNumber(next.CompletedUnits)} completed; total unavailable."
            : next.State is RecoveryPruneControllerState.Starting or RecoveryPruneControllerState.Running or RecoveryPruneControllerState.CancellationRequested
                ? "Waiting for bounded recovery-cleanup progress."
                : "No recovery cleanup has started.";
        this.NotifyDerivedProperties();

        bool selectionOnly = previous.State == RecoveryPruneControllerState.CatalogReady
            && next.State == RecoveryPruneControllerState.CatalogReady
            && previous.Choices.Count == next.Choices.Count
            && previous.Choices.Select((choice, index) => ReferenceEquals(choice, next.Choices[index])).All(match => match)
            && !ReferenceEquals(previous.Selected, next.Selected);
        bool focusChanged = !selectionOnly
            && (previous.State != next.State || previous.Generation != next.Generation);
        if (requestFocus && focusChanged)
            this.FocusRequested?.Invoke(this, GetFocusTarget(next));
    }

    private void ApplyChoices(RecoveryPruneSnapshot value)
    {
        bool sameChoices = value.Choices.Count == this.Choices.Count
            && value.Choices.Select((choice, index) => ReferenceEquals(choice, this.Choices[index].Choice)).All(match => match);
        if (!sameChoices)
        {
            this.SetProperty(ref this.selectedChoice, null, nameof(this.SelectedChoice));
            RecoveryPruneChoiceItem[] projected = value.Choices.Select(CreateChoice).ToArray();
            this.Choices = Array.AsReadOnly(projected);
        }
        RecoveryPruneChoiceItem? selected = value.Selected is { } exact
            ? this.Choices.SingleOrDefault(item => ReferenceEquals(item.Choice, exact))
            : null;
        this.SetProperty(ref this.selectedChoice, selected, nameof(this.SelectedChoice));
    }

    private void SetConsentFromSnapshot(bool value)
    {
        if (this.SetProperty(ref this.isDestructiveConsentChecked, value, nameof(this.IsDestructiveConsentChecked)))
            this.ConfirmCommand.NotifyCanExecuteChanged();
    }

    private void NotifyDerivedProperties()
    {
        foreach (string property in new[]
        {
            nameof(this.ReleaseDetail), nameof(this.ReleaseAccessibleName), nameof(this.BoundaryDetail), nameof(this.CleanupScopeWarning),
            nameof(this.IsHistoryListVisible), nameof(this.IsPlanVisible), nameof(this.IsBusy), nameof(this.IsProgressVisible),
            nameof(this.IsProgressIndeterminate), nameof(this.HasProgressStage), nameof(this.ProgressMaximum), nameof(this.ProgressValue),
            nameof(this.IsConsentVisible), nameof(this.IsConfirmVisible), nameof(this.IsRunVisible), nameof(this.IsCancelVisible),
            nameof(this.IsExitVisible), nameof(this.IsResultVisible), nameof(this.IsErrorVisible), nameof(this.StatusLiveSetting),
            nameof(this.IsSettlementWarningVisible), nameof(this.SettlementWarning), nameof(this.ReviewLiveSetting),
            nameof(this.StageLiveSetting), nameof(this.ResultLiveSetting), nameof(this.ErrorLiveSetting)
        })
            this.OnPropertyChanged(property);
        this.ListCommand.NotifyCanExecuteChanged();
        this.InspectCommand.NotifyCanExecuteChanged();
        this.ConfirmCommand.NotifyCanExecuteChanged();
        this.RunCommand.NotifyCanExecuteChanged();
        this.CancelCommand.NotifyCanExecuteChanged();
        this.ExitCommand.NotifyCanExecuteChanged();
    }

    private void HandleActionFailure(Exception _)
    {
        this.Heading = "The recovery-cleanup action could not be presented safely";
        this.Message = "No private backend detail is shown. If work may be active, keep this window open until a bounded result appears.";
        this.LiveAnnouncement = $"{this.Heading}. {this.Message}";
        this.FocusRequested?.Invoke(this, RecoveryPruneFocusTarget.Error);
    }

    private void HandleCancellationFailure(Exception _)
    {
        this.Heading = "The cancellation request could not be confirmed";
        this.Message = "Keep this window open. The installer is still waiting for the exact recovery-cleanup result.";
        this.LiveAnnouncement = $"{this.Heading}. {this.Message}";
        this.FocusRequested?.Invoke(this, RecoveryPruneFocusTarget.Status);
    }

    private static RecoveryPruneChoiceItem CreateChoice(RecoveryPruneChoice choice)
    {
        string title = choice.IsCurrent
            ? "Current recovery point"
            : choice.IsUserCheckpoint
                ? $"User checkpoint {FormatNumber(choice.Ordinal)}"
                : $"Recovery point {FormatNumber(choice.Ordinal)}";
        string origin = choice.OriginOperation switch
        {
            InstallerOperation.Install => "Created by install",
            InstallerOperation.Update => "Created by update",
            InstallerOperation.Repair => "Created by repair",
            InstallerOperation.Uninstall => "Created by uninstall",
            InstallerOperation.Backup => "Created by backup",
            InstallerOperation.Rollback => "Created by rollback",
            _ => throw new ArgumentOutOfRangeException(nameof(choice))
        };
        string target = choice.RestoreTarget switch
        {
            RecoveryPruneReleaseTarget release => $"Restore target: {release.Tag}, version {release.EmbeddedVersion}",
            RecoveryPruneUninstalledTarget => "Restore target: no managed SMAPI installation",
            _ => throw new ArgumentOutOfRangeException(nameof(choice))
        };
        return new(choice, title, $"Keep this point and every newer point. {origin}. {target}.");
    }

    private static IReadOnlyList<RecoveryPruneFactRow> CreatePlanRows(RecoveryPrunePlanPresentation? plan)
    {
        if (plan is null)
            return Array.Empty<RecoveryPruneFactRow>();
        RecoveryPruneFactRow[] rows =
        [
            new("Newest points retained", FormatNumber(plan.RetainNewest)),
            new("Retained recovery points", FormatNumber(plan.RetainedCount)),
            new("Older points to remove", FormatNumber(plan.RemovedCount)),
            new("Recovery generations to clean", FormatNumber(plan.CleanupGenerationCount)),
            new("Auxiliary cleanup", plan.AuxiliaryCleanupPlanned ? "Planned" : "Not planned"),
            new("Warnings", FormatNumber(plan.WarningCount)),
            new("Observed risk", FormatRisks(plan.Risks)),
            new("Recommended default", plan.RecommendedDefault == ProtocolRecommendedDefault.Cancel ? "Cancel" : throw new ArgumentOutOfRangeException(nameof(plan))),
            new("Confirmation", plan.RequiresConfirmation ? "Required before Run cleanup" : throw new ArgumentOutOfRangeException(nameof(plan)))
        ];
        return Array.AsReadOnly(rows);
    }

    private static string CreateCleanupScopeWarning(RecoveryPrunePlanPresentation? plan)
    {
        if (plan is null)
            return "No cleanup scope has been inspected. Loading and selection change no files.";
        string recoveryPointLabel = plan.RemovedCount == 1 ? "recovery point" : "recovery points";
        return (plan.RemovedCount, plan.AuxiliaryCleanupPlanned) switch
        {
            ( > 0, true) => $"This reviewed cleanup will permanently remove {FormatNumber(plan.RemovedCount)} older {recoveryPointLabel} and clean authenticated auxiliary recovery metadata. It cannot be restored unless you have a separate external backup.",
            ( > 0, false) => $"This reviewed cleanup will permanently remove {FormatNumber(plan.RemovedCount)} older {recoveryPointLabel}. It cannot be restored unless you have a separate external backup.",
            (0, true) => "No recovery points will be removed; authenticated auxiliary recovery metadata will be cleaned permanently. It cannot be restored unless you have a separate external backup.",
            _ => "This reviewed plan reports no recovery history to clean. Cancel and load fresh recovery history before taking another action."
        };
    }

    private static IReadOnlyList<RecoveryPruneFactRow> CreateResultRows(RecoveryPruneResultPresentation? result)
    {
        if (result is null)
            return Array.Empty<RecoveryPruneFactRow>();
        if (result is RecoveryPruneStateUnknownPresentation)
        {
            return Array.AsReadOnly<RecoveryPruneFactRow>(
            [
                new("Durable state", "Unknown"),
                new("Required next step", "Start a fresh verified session and list recovery history again")
            ]);
        }
        if (result is not RecoveryPruneTerminalPresentation terminal)
            throw new ArgumentOutOfRangeException(nameof(result));
        RecoveryPruneFactRow[] rows =
        [
            new("Outcome", GetOutcomeLabel(terminal.Outcome)),
            new("Durable state", GetDurableStateLabel(terminal.DurableState)),
            new("Error", terminal.ErrorCode is { } error ? GetErrorLabel(error) : "None reported"),
            new("Recovery disposition", GetRecoveryDispositionLabel(terminal.RecoveryDisposition)),
            new("Next step", terminal.NextAction == ProtocolNextAction.ListRecoveries ? "List recovery history again" : throw new ArgumentOutOfRangeException(nameof(terminal))),
            new("Logically removed generations", FormatCount(terminal.LogicallyRemovedGenerationCount)),
            new("Physically cleaned generations", FormatCount(terminal.PhysicallyCleanedGenerationCount)),
            new("Pending cleanup generations", FormatCount(terminal.PendingCleanupGenerationCount)),
            new("Auxiliary cleanup pending", terminal.AuxiliaryCleanupPending is { } pending ? pending ? "Yes" : "No" : "Unknown"),
            new("Backend settlement", terminal.BackendSettlement switch
            {
                InstallerBackendSettlement.ConfirmedClosed => "Confirmed closed",
                InstallerBackendSettlement.Unconfirmed => "Not confirmed; start a fresh session",
                _ => throw new ArgumentOutOfRangeException(nameof(terminal))
            })
        ];
        return Array.AsReadOnly(rows);
    }

    private static (string Heading, string Message) GetCopy(RecoveryPruneSnapshot value)
    {
        return value.State switch
        {
            RecoveryPruneControllerState.NotLoaded => (
                "Load recovery history",
                "Nothing is loaded automatically. Loading reads bounded local recovery history and changes no files."
            ),
            RecoveryPruneControllerState.Listing => (
                "Loading local recovery history…",
                "Reading a bounded list. Nothing is selected, inspected, confirmed, run, or changed."
            ),
            RecoveryPruneControllerState.CatalogReady when value.Selected is null => (
                "Choose the oldest recovery point to keep",
                "No point is selected by default. Select one exact point, then inspect the cleanup plan. No files have changed."
            ),
            RecoveryPruneControllerState.CatalogReady => (
                "Recovery retention boundary selected",
                "The selection is local to this screen. Inspect cleanup requests a read-only plan and changes no files."
            ),
            RecoveryPruneControllerState.NoHistory => (
                "No recovery history reported",
                "No committed recovery point was reported. Loading again performs another bounded read-only lookup."
            ),
            RecoveryPruneControllerState.Inspecting => (
                "Inspecting recovery cleanup…",
                "The exact retention boundary is being inspected. No recovery point is being removed."
            ),
            RecoveryPruneControllerState.RelistRequired => GetRejectionCopy(value.Rejection),
            RecoveryPruneControllerState.ReviewReady => (
                "Review recovery cleanup — consent unchecked",
                "Cancel is the recommended default. Review the bounded counts, then explicitly check consent before confirmation. Confirmation still does not run cleanup."
            ),
            RecoveryPruneControllerState.Confirming => (
                "Confirming the reviewed cleanup…",
                "No recovery point is being removed. Confirmation only prepares a separate Run cleanup action."
            ),
            RecoveryPruneControllerState.ReadyToRun => (
                "Recovery cleanup confirmed — not started",
                "No recovery point has been removed. Cancel is the recommended default; Run cleanup is a separate one-shot action."
            ),
            RecoveryPruneControllerState.Starting => (
                "Starting recovery cleanup…",
                "Waiting to learn whether the exact confirmed cleanup was admitted. Keep this window open."
            ),
            RecoveryPruneControllerState.Running => (
                "Recovery cleanup is running…",
                "Bounded local cleanup is active. Keep this window open until an exact durable result appears."
            ),
            RecoveryPruneControllerState.CancellationRequested => (
                "Cancellation requested…",
                "Keep this window open while the exact request or cleanup operation settles."
            ),
            RecoveryPruneControllerState.Cancelled => (
                "Recovery cleanup request cancelled",
                "Cleanup did not proceed through this screen. The session is closed; reopen the installer to start again."
            ),
            RecoveryPruneControllerState.CancelledBeforeStart => (
                "Recovery cleanup cancelled before start",
                "The confirmed cleanup did not start. The session is closed; reopen the installer to start again."
            ),
            RecoveryPruneControllerState.Terminal => GetTerminalCopy(value.Result),
            RecoveryPruneControllerState.StateUnknown => (
                "Recovery cleanup state is unknown",
                "Do not assume cleanup succeeded or failed. Start a fresh verified session and list recovery history again before another action."
            ),
            RecoveryPruneControllerState.Failed => (
                "Recovery cleanup stopped safely",
                "The bounded request could not continue and the session closed. Reopen the installer before trying again."
            ),
            RecoveryPruneControllerState.SessionFaulted => (
                "The recovery-cleanup service closed",
                "The verified session is unavailable. Reopen the installer before reviewing recovery history again."
            ),
            RecoveryPruneControllerState.Disposing => (
                "Closing recovery cleanup safely…",
                "Waiting for cancellation and backend cleanup to settle."
            ),
            RecoveryPruneControllerState.Disposed => (
                "Recovery cleanup closed",
                "The recovery-cleanup session has closed."
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
    }

    private static (string Heading, string Message) GetRejectionCopy(RecoveryPruneRejection? rejection)
    {
        if (rejection is null)
            throw new ArgumentNullException(nameof(rejection));
        return rejection.ErrorCode switch
        {
            ProtocolPrePlanErrorCode.NothingToPrune => (
                "No older recovery point needs cleanup",
                "Recovery history changed. Load a fresh bounded list before choosing another retention boundary."
            ),
            ProtocolPrePlanErrorCode.RequestCancelled => (
                "Recovery cleanup inspection did not finish",
                "No recovery point was removed. Load a fresh bounded list before trying again."
            ),
            ProtocolPrePlanErrorCode.InvalidGameFolder => (
                "The selected game folder is no longer valid",
                "No recovery point was removed. Close and reopen the installer to validate the game folder again."
            ),
            ProtocolPrePlanErrorCode.RecoveryUnavailable => (
                "Recovery history is unavailable",
                "No recovery point was removed. Close and reopen the installer before reviewing recovery history again."
            ),
            ProtocolPrePlanErrorCode.InspectionFailed => (
                "The cleanup plan could not be inspected",
                "No recovery point was removed. Load fresh recovery history before trying again."
            ),
            ProtocolPrePlanErrorCode.PermissionDenied => (
                "Recovery history could not be read with your permissions",
                "No recovery point was removed. Review user permissions and do not run the installer as root."
            ),
            ProtocolPrePlanErrorCode.UnexpectedFailure when rejection.NextAction == ProtocolNextAction.ViewPrivateLog => (
                "The cleanup session closed after an unexpected problem",
                "A private local log was recorded, but this screen does not expose its location. Reopen the installer before trying again."
            ),
            ProtocolPrePlanErrorCode.UnexpectedFailure => (
                "The cleanup session closed after an unexpected problem",
                "No recovery point was removed. Reopen the installer before trying again."
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(rejection))
        };
    }

    private static (string Heading, string Message) GetTerminalCopy(RecoveryPruneResultPresentation? result)
    {
        if (result is not RecoveryPruneTerminalPresentation terminal)
            throw new ArgumentOutOfRangeException(nameof(result));
        return terminal.Outcome switch
        {
            ProtocolPruneOutcome.Succeeded => (
                "Recovery cleanup completed",
                "The exact durable counts are shown below. List recovery history again in a fresh session before another cleanup."
            ),
            ProtocolPruneOutcome.FailedBeforePublication => (
                "Recovery cleanup failed before publication",
                "No logical recovery-history change was published. Review the exact cleanup disposition below before trying again."
            ),
            ProtocolPruneOutcome.CancelledBeforePublication => (
                "Recovery cleanup cancelled before publication",
                "No logical recovery-history change was published. Review the exact cleanup disposition below."
            ),
            ProtocolPruneOutcome.Interrupted => (
                "Recovery cleanup was interrupted",
                "Use the exact durable counts and recovery disposition below; do not infer unreported work."
            ),
            ProtocolPruneOutcome.CancelledWithCleanupPending => (
                "Recovery cleanup cancelled with cleanup pending",
                "The logical cleanup was applied, but authenticated cleanup remains. Start a fresh session before another action."
            ),
            ProtocolPruneOutcome.FailedWithCleanupPending => (
                "Recovery cleanup failed with cleanup pending",
                "The logical cleanup was applied, but authenticated cleanup remains. Start a fresh session before another action."
            ),
            ProtocolPruneOutcome.UnexpectedCoreFailure => (
                "Recovery cleanup ended with unknown durable state",
                "Do not infer what changed. Start a fresh verified session and list recovery history again."
            ),
            ProtocolPruneOutcome.CancelledAfterApply => (
                "Recovery cleanup applied before cancellation settled",
                "The exact cleanup was applied. List recovery history again in a fresh session before another action."
            ),
            ProtocolPruneOutcome.FailedAfterApply => (
                "Recovery cleanup applied before a later failure",
                "The exact cleanup was applied. List recovery history again in a fresh session before another action."
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(result))
        };
    }

    private static RecoveryPruneFocusTarget GetFocusTarget(RecoveryPruneSnapshot value) => value.State switch
    {
        RecoveryPruneControllerState.NotLoaded or RecoveryPruneControllerState.NoHistory or RecoveryPruneControllerState.RelistRequired => RecoveryPruneFocusTarget.Load,
        RecoveryPruneControllerState.CatalogReady when value.Selected is null => RecoveryPruneFocusTarget.HistoryList,
        RecoveryPruneControllerState.CatalogReady => RecoveryPruneFocusTarget.Inspect,
        RecoveryPruneControllerState.ReviewReady => RecoveryPruneFocusTarget.Consent,
        RecoveryPruneControllerState.ReadyToRun => RecoveryPruneFocusTarget.Cancel,
        RecoveryPruneControllerState.Terminal => RecoveryPruneFocusTarget.Result,
        RecoveryPruneControllerState.StateUnknown or RecoveryPruneControllerState.Failed or RecoveryPruneControllerState.SessionFaulted => RecoveryPruneFocusTarget.Error,
        RecoveryPruneControllerState.Cancelled or RecoveryPruneControllerState.CancelledBeforeStart or RecoveryPruneControllerState.Disposed => RecoveryPruneFocusTarget.Exit,
        _ => RecoveryPruneFocusTarget.Status
    };

    private static bool IsAssertiveTerminal(RecoveryPruneSnapshot value)
        => value.Result is RecoveryPruneTerminalPresentation
        {
            Outcome: ProtocolPruneOutcome.FailedBeforePublication
                or ProtocolPruneOutcome.Interrupted
                or ProtocolPruneOutcome.CancelledWithCleanupPending
                or ProtocolPruneOutcome.FailedWithCleanupPending
                or ProtocolPruneOutcome.UnexpectedCoreFailure
                or ProtocolPruneOutcome.FailedAfterApply
        };

    private static bool IsAssertiveProblem(RecoveryPruneControllerState state)
        => state is RecoveryPruneControllerState.StateUnknown
            or RecoveryPruneControllerState.Failed
            or RecoveryPruneControllerState.SessionFaulted;

    private static string FormatRisks(IReadOnlyList<ProtocolPlanRisk> risks)
    {
        if (risks.Count != 1 || risks[0] != ProtocolPlanRisk.RecoveryPrune)
            throw new ArgumentOutOfRangeException(nameof(risks));
        return "Destructive removal of older recovery history";
    }

    private static string FormatCount(int? count) => count is { } value ? FormatNumber(value) : "Unknown";

    private static string FormatNumber(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string GetOutcomeLabel(ProtocolPruneOutcome outcome) => outcome switch
    {
        ProtocolPruneOutcome.Succeeded => "Succeeded",
        ProtocolPruneOutcome.FailedBeforePublication => "Failed before publication",
        ProtocolPruneOutcome.CancelledBeforePublication => "Cancelled before publication",
        ProtocolPruneOutcome.Interrupted => "Interrupted",
        ProtocolPruneOutcome.CancelledWithCleanupPending => "Cancelled with cleanup pending",
        ProtocolPruneOutcome.FailedWithCleanupPending => "Failed with cleanup pending",
        ProtocolPruneOutcome.UnexpectedCoreFailure => "Unexpected core failure",
        ProtocolPruneOutcome.CancelledAfterApply => "Cancelled after apply",
        ProtocolPruneOutcome.FailedAfterApply => "Failed after apply",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };

    private static string GetDurableStateLabel(ProtocolDurableState state) => state switch
    {
        ProtocolDurableState.Unchanged => "Unchanged",
        ProtocolDurableState.PruneApplied => "Recovery cleanup applied",
        ProtocolDurableState.Unknown => "Unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    private static string GetErrorLabel(ProtocolTerminalErrorCode error) => error switch
    {
        ProtocolTerminalErrorCode.InvalidPlan => "Invalid plan",
        ProtocolTerminalErrorCode.UnsafePath => "Unsafe path",
        ProtocolTerminalErrorCode.PathChanged => "Path changed",
        ProtocolTerminalErrorCode.ExistingFileMismatch => "Existing file mismatch",
        ProtocolTerminalErrorCode.PayloadMismatch => "Payload mismatch",
        ProtocolTerminalErrorCode.ConcurrentOperation => "Concurrent operation",
        ProtocolTerminalErrorCode.WorkspaceConflict => "Workspace conflict",
        ProtocolTerminalErrorCode.RecoveryFailed => "Recovery failed",
        ProtocolTerminalErrorCode.DiskFull => "Disk full",
        ProtocolTerminalErrorCode.ReadOnlyFileSystem => "Read-only file system",
        ProtocolTerminalErrorCode.IoFailure => "Input/output failure",
        ProtocolTerminalErrorCode.PermissionDenied => "Permission denied",
        ProtocolTerminalErrorCode.CrossDeviceBoundary => "Cross-device boundary",
        ProtocolTerminalErrorCode.UnexpectedCoreFailure => "Unexpected core failure",
        _ => throw new ArgumentOutOfRangeException(nameof(error))
    };

    private static string GetRecoveryDispositionLabel(ProtocolRecoveryDisposition disposition) => disposition switch
    {
        ProtocolRecoveryDisposition.NotRequired => "Not required",
        ProtocolRecoveryDisposition.CleanupPending => "Cleanup pending",
        ProtocolRecoveryDisposition.StateRefreshRequired => "Fresh state inspection required",
        _ => throw new ArgumentOutOfRangeException(nameof(disposition))
    };

    private static string GetStageLabel(TransactionStage stage) => stage switch
    {
        TransactionStage.AcquiringLock => "Acquiring installer lock",
        TransactionStage.Recovering => "Recovering interrupted work",
        TransactionStage.Staging => "Staging verified changes",
        TransactionStage.Revalidating => "Revalidating recovery cleanup",
        TransactionStage.Applying => "Applying planned changes",
        TransactionStage.Verifying => "Verifying changed files",
        TransactionStage.Committing => "Committing installer state",
        TransactionStage.RollingBack => "Rolling back changes",
        TransactionStage.Inspecting => "Inspecting local state",
        TransactionStage.VerifyingRecovery => "Verifying recovery data",
        TransactionStage.PreparingRecovery => "Preparing recovery",
        TransactionStage.PreparingPayload => "Preparing verified payload",
        TransactionStage.WritingFiles => "Writing managed files",
        TransactionStage.RemovingFiles => "Removing files",
        TransactionStage.UpdatingLauncher => "Updating launcher",
        TransactionStage.UpdatingInstallerState => "Publishing recovery history",
        TransactionStage.PublishingRecovery => "Publishing recovery state",
        TransactionStage.CleaningRecovery => "Cleaning recovery data",
        TransactionStage.Completed => "Completing recovery cleanup",
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };

    private enum AnnouncementRegion
    {
        Status,
        Review,
        Stage,
        Result,
        Error
    }
}
