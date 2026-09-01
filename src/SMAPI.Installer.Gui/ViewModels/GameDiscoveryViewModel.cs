using System.Globalization;
using System.Text;
using Avalonia.Automation;
using Avalonia.Threading;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Gui.Frontend;

namespace StardewModdingAPI.Installer.Gui.ViewModels;

internal enum GameDiscoveryFocusTarget
{
    Status,
    CandidateList,
    Browse,
    Retry,
    Exit
}

internal sealed class GameCandidateItem
{
    private const int MaximumAccessibleNameLength = 1024;
    private const int MaximumAccessibleDisplayNameLength = 192;

    internal GameCandidateItem(ProtocolGameCandidate candidate)
    {
        this.Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        this.DisplayName = FormatCanonicalPathForDisplay(candidate.DisplayName);
        this.CanonicalPath = FormatCanonicalPathForDisplay(candidate.CanonicalPath);
        (this.StatusLabel, this.StatusDetail) = Describe(candidate.State);
        string accessiblePrefix = $"{TruncateMiddle(this.DisplayName, MaximumAccessibleDisplayNameLength)}. {this.StatusLabel}. {this.StatusDetail} Folder: ";
        int pathLength = MaximumAccessibleNameLength - accessiblePrefix.Length;
        this.AccessibleName = accessiblePrefix + TruncateMiddle(this.CanonicalPath, pathLength);
    }

    internal ProtocolGameCandidate Candidate { get; }

    public string DisplayName { get; }

    public string CanonicalPath { get; }

    public string StatusLabel { get; }

    public string StatusDetail { get; }

    public string AccessibleName { get; }

    public bool IsValid => this.Candidate.State == LinuxGameFolderStatus.Valid;

    internal static string FormatCanonicalPathForDisplay(string canonicalPath)
    {
        ArgumentNullException.ThrowIfNull(canonicalPath);
        StringBuilder? escaped = null;
        for (int index = 0; index < canonicalPath.Length; index++)
        {
            char character = canonicalPath[index];
            if (char.IsSurrogate(character) || char.GetUnicodeCategory(character) == UnicodeCategory.Format)
            {
                escaped ??= new StringBuilder(canonicalPath.Length + 8).Append(canonicalPath, 0, index);
                escaped.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
            }
            else
                escaped?.Append(character);
        }
        return escaped?.ToString() ?? canonicalPath;
    }

    private static string TruncateMiddle(string value, int maximumLength)
    {
        if (value.Length <= maximumLength)
            return value;
        int prefixLength = (maximumLength - 1) / 2;
        int suffixLength = maximumLength - prefixLength - 1;
        return value[..prefixLength] + "…" + value[^suffixLength..];
    }

    private static (string Label, string Detail) Describe(LinuxGameFolderStatus status)
    {
        return status switch
        {
            LinuxGameFolderStatus.Valid => (
                "Ready",
                "This is a supported Stardew Valley installation. Nothing has been changed."
            ),
            LinuxGameFolderStatus.MissingDirectory => (
                "Folder not found",
                "The selected folder does not exist. Choose the folder which contains Stardew Valley."
            ),
            LinuxGameFolderStatus.UnsafeRoot => (
                "Folder cannot be inspected safely",
                "Choose a normal user-owned game folder which is not a link or special filesystem entry."
            ),
            LinuxGameFolderStatus.MissingGameAssembly => (
                "Game files are missing",
                "The Stardew Valley game assembly was not found in this folder."
            ),
            LinuxGameFolderStatus.UnsafeGameAssembly => (
                "Game assembly cannot be inspected safely",
                "The game assembly is linked, replaced, or otherwise unsafe for installer inspection."
            ),
            LinuxGameFolderStatus.InvalidGameAssembly => (
                "Game assembly is invalid",
                "The observed assembly is not a valid supported Stardew Valley game assembly."
            ),
            LinuxGameFolderStatus.UnsupportedGameVersion => (
                "Game version is unsupported",
                "Update Stardew Valley to a supported version, then validate the folder again."
            ),
            LinuxGameFolderStatus.MissingGameDependencies => (
                "Game dependencies are missing",
                "Required Stardew Valley dependency files were not found in this folder."
            ),
            LinuxGameFolderStatus.UnsafeGameDependencies => (
                "Game dependencies cannot be inspected safely",
                "Required dependency files are linked, replaced, or otherwise unsafe for installer inspection."
            ),
            LinuxGameFolderStatus.InvalidGameDependencies => (
                "Game dependencies are invalid",
                "The observed dependency files do not match a supported Stardew Valley installation."
            ),
            LinuxGameFolderStatus.MissingLauncher => (
                "Game launcher is missing",
                "The Stardew Valley launcher was not found in this folder."
            ),
            LinuxGameFolderStatus.UnsafeLauncher => (
                "Game launcher cannot be inspected safely",
                "The launcher is linked, replaced, or otherwise unsafe for installer inspection."
            ),
            _ => ("Folder is not usable", "Choose another Stardew Valley game folder.")
        };
    }
}

/// <summary>Presentation adapter for bounded, read-only game discovery through a verified backend session.</summary>
internal sealed class GameDiscoveryViewModel : ObservableObject, IAsyncDisposable
{
    private readonly GameDiscoveryController Controller;
    private GameDiscoverySnapshot snapshot;
    private IReadOnlyList<GameCandidateItem> candidates = Array.Empty<GameCandidateItem>();
    private GameCandidateItem? selectedCandidate;
    private string heading = "Ready to find Stardew Valley";
    private string message = "The installer can inspect common Linux game locations or validate a folder you choose.";
    private string liveAnnouncement = "Ready to find Stardew Valley.";
    private EventHandler? FolderPickerRequestedValue;
    private bool folderPickerPending;
    private bool folderPickerFailed;
    private bool started;
    private bool disposed;

    public GameDiscoveryViewModel(GameDiscoveryController controller)
    {
        this.Controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.snapshot = controller.Snapshot;
        this.RetryCommand = new AsyncRelayCommand(() => controller.DiscoverAsync(), () => this.snapshot.CanRetry, this.HandlePresentationFailure);
        this.BrowseCommand = new RelayCommand(this.RequestFolderPicker, this.CanBrowse);
        this.CancelCommand = new AsyncRelayCommand(controller.CancelAsync, () => this.snapshot.CanCancel, this.HandlePresentationFailure);
        this.ExitCommand = new RelayCommand(() => this.CloseRequested?.Invoke(this, EventArgs.Empty), () => this.IsExitVisible);
        this.Controller.Changed += this.OnControllerChanged;
        this.ApplySnapshot(this.snapshot, requestFocus: false);
    }

    public event EventHandler? FolderPickerRequested
    {
        add
        {
            this.FolderPickerRequestedValue += value;
            this.OnPropertyChanged(nameof(this.IsBrowseVisible));
            this.BrowseCommand.NotifyCanExecuteChanged();
        }
        remove
        {
            this.FolderPickerRequestedValue -= value;
            this.OnPropertyChanged(nameof(this.IsBrowseVisible));
            this.BrowseCommand.NotifyCanExecuteChanged();
        }
    }

    public event EventHandler<GameDiscoveryFocusTarget>? FocusRequested;

    public event EventHandler? CloseRequested;

    public IReadOnlyList<GameCandidateItem> Candidates
    {
        get => this.candidates;
        private set => this.SetProperty(ref this.candidates, value);
    }

    public GameCandidateItem? SelectedCandidate
    {
        get => this.selectedCandidate;
        set
        {
            if (value is null || ReferenceEquals(value, this.selectedCandidate))
                return;
            this.Controller.SelectCandidate(value.Candidate);
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

    public string ReleaseDetail => $"Verified release: {this.Controller.Release.Tag}";

    public string SelectionDetail => this.selectedCandidate is null
        ? "No game folder selected."
        : $"{this.selectedCandidate.StatusLabel}: {this.selectedCandidate.StatusDetail}";

    public string DurableState => "Unchanged — no game files have been modified";

    public bool IsBusy => this.snapshot.State is GameDiscoveryState.Discovering
        or GameDiscoveryState.ValidatingManual
        or GameDiscoveryState.Cancelling;

    public bool IsCandidateListVisible => this.Candidates.Count > 0;

    public bool IsEmptyVisible => this.snapshot.State == GameDiscoveryState.NoCandidates;

    public bool IsProblemVisible => this.folderPickerFailed
        || this.snapshot.SelectedCandidate is { State: not LinuxGameFolderStatus.Valid }
        || this.snapshot.State is GameDiscoveryState.ManualInvalid
        or GameDiscoveryState.Failed
        or GameDiscoveryState.SessionFaulted;

    public AutomationLiveSetting StatusLiveSetting => this.IsProblemVisible
        ? AutomationLiveSetting.Off
        : AutomationLiveSetting.Polite;

    public AutomationLiveSetting ProblemLiveSetting => !this.folderPickerFailed
        && (this.snapshot.SelectedCandidate is { State: not LinuxGameFolderStatus.Valid }
            || this.snapshot.State == GameDiscoveryState.ManualInvalid)
        ? AutomationLiveSetting.Polite
        : AutomationLiveSetting.Assertive;

    public bool IsBrowseVisible => this.snapshot.CanBrowse && this.FolderPickerRequestedValue is not null;

    public bool IsRetryVisible => this.snapshot.CanRetry;

    public bool IsCancelVisible => this.snapshot.CanCancel;

    public bool IsExitVisible => this.snapshot.State is GameDiscoveryState.Cancelled
        or GameDiscoveryState.Failed
        or GameDiscoveryState.SessionFaulted;

    public AsyncRelayCommand RetryCommand { get; }

    public RelayCommand BrowseCommand { get; }

    public AsyncRelayCommand CancelCommand { get; }

    public RelayCommand ExitCommand { get; }

    /// <summary>Publish a sanitized retryable presentation failure from the desktop folder picker.</summary>
    internal void ReportFolderPickerFailure()
    {
        if (this.disposed)
            return;
        this.folderPickerPending = false;
        this.folderPickerFailed = true;
        this.Heading = "The desktop folder picker could not open";
        this.Message = "No folder was selected and no game files were changed. Try Browse again, or use the documented manual installer.";
        this.LiveAnnouncement = $"{this.Heading}. {this.Message}";
        this.NotifyDerivedProperties();
        this.FocusRequested?.Invoke(this, GameDiscoveryFocusTarget.Browse);
    }

    /// <summary>Publish a sanitized terminal failure if a selected folder couldn't enter backend validation.</summary>
    internal void ReportFolderValidationFailure()
    {
        if (this.disposed)
            return;
        this.folderPickerPending = false;
        this.folderPickerFailed = true;
        this.Heading = "The selected folder could not be checked";
        this.Message = "The verified installer session is not available. Close and reopen the installer; no game files were changed.";
        this.LiveAnnouncement = $"{this.Heading}. {this.Message}";
        this.NotifyDerivedProperties();
        this.FocusRequested?.Invoke(this, GameDiscoveryFocusTarget.Status);
    }

    public async Task StartAsync()
    {
        if (this.started || this.disposed)
            return;
        this.started = true;
        await this.Controller.DiscoverAsync().ConfigureAwait(true);
    }

    public Task ApplyManualFolderAsync(string? path)
    {
        this.folderPickerPending = false;
        this.BrowseCommand.NotifyCanExecuteChanged();
        return path is null || this.disposed
            ? Task.CompletedTask
            : this.Controller.ValidateManualAsync(path);
    }

    public async ValueTask DisposeAsync()
    {
        if (this.disposed)
            return;
        this.disposed = true;
        await this.Controller.DisposeAsync().ConfigureAwait(true);
        this.Controller.Changed -= this.OnControllerChanged;
    }

    private bool CanBrowse()
    {
        return !this.disposed
            && !this.folderPickerPending
            && this.snapshot.CanBrowse
            && this.FolderPickerRequestedValue is not null;
    }

    private void RequestFolderPicker()
    {
        if (!this.CanBrowse())
            return;
        this.folderPickerPending = true;
        this.BrowseCommand.NotifyCanExecuteChanged();
        this.FolderPickerRequestedValue?.Invoke(this, EventArgs.Empty);
    }

    private void OnControllerChanged(object? sender, EventArgs e)
    {
        GameDiscoverySnapshot next = this.Controller.Snapshot;
        if (Dispatcher.UIThread.CheckAccess())
            this.ApplySnapshot(next, requestFocus: true);
        else
            Dispatcher.UIThread.Post(() => this.ApplySnapshot(next, requestFocus: true));
    }

    private void ApplySnapshot(GameDiscoverySnapshot next, bool requestFocus)
    {
        if (this.disposed && next.State != GameDiscoveryState.Disposed)
            return;
        if (next.Revision < this.snapshot.Revision)
            return;
        GameDiscoveryState priorState = this.snapshot.State;
        long priorGeneration = this.snapshot.Generation;
        this.snapshot = next;
        this.folderPickerFailed = false;
        GameCandidateItem[] items = next.Candidates.Select(candidate => new GameCandidateItem(candidate)).ToArray();
        this.Candidates = items;
        GameCandidateItem? selected = next.SelectedCandidate is null
            ? null
            : items.SingleOrDefault(item => ReferenceEquals(item.Candidate, next.SelectedCandidate));
        this.SetProperty(ref this.selectedCandidate, selected, nameof(this.SelectedCandidate));
        (this.Heading, this.Message) = this.GetCopy(next, selected);
        this.LiveAnnouncement = this.IsProblemVisible || next.State == GameDiscoveryState.Transferred
            ? $"{this.Heading}. {this.Message}"
            : this.Heading;
        this.OnPropertyChanged(nameof(this.SelectionDetail));
        this.NotifyDerivedProperties();

        if (requestFocus && (priorState != next.State || priorGeneration != next.Generation))
            this.FocusRequested?.Invoke(this, this.GetFocusTarget(next));
    }

    internal void ApplySnapshotForTesting(GameDiscoverySnapshot next)
    {
        this.ApplySnapshot(next, requestFocus: false);
    }

    private (string Heading, string Message) GetCopy(
        GameDiscoverySnapshot value,
        GameCandidateItem? selected
    )
    {
        return value.State switch
        {
            GameDiscoveryState.Idle => (
                "Ready to find Stardew Valley",
                "The installer can inspect common Linux game locations or validate a folder you choose."
            ),
            GameDiscoveryState.Discovering => (
                "Looking for Stardew Valley…",
                "Checking bounded common Linux locations. Nothing is being installed or changed."
            ),
            GameDiscoveryState.NoCandidates => (
                "No Stardew Valley folder was found automatically",
                "Choose the game folder manually, or try automatic detection again after installing the game."
            ),
            GameDiscoveryState.Ready when selected is not null => (
                selected.IsValid ? "Game folder selected" : "This folder needs attention",
                selected.StatusDetail
            ),
            GameDiscoveryState.Ready => (
                value.Candidates.Count == 1 ? "One possible game folder was found" : $"{value.Candidates.Count} possible game folders were found",
                "Select one folder to review its validation result. Unsupported folders show a safe next step."
            ),
            GameDiscoveryState.ValidatingManual => (
                "Checking the selected folder…",
                "Validating the folder through the local installer service. Nothing is being changed."
            ),
            GameDiscoveryState.ManualInvalid => (
                selected?.StatusLabel ?? "The selected folder is not usable",
                selected?.StatusDetail ?? "Choose another Stardew Valley folder."
            ),
            GameDiscoveryState.ManualValid => (
                "Selected game folder is valid",
                "The folder is a supported Stardew Valley installation. Nothing has been changed."
            ),
            GameDiscoveryState.Cancelling => (
                "Stopping safely…",
                "Waiting for the current read-only check to settle. No game files are being changed."
            ),
            GameDiscoveryState.Cancelled => (
                "Game-folder check cancelled and session closed",
                "No game files were changed. Close and reopen the installer before trying again."
            ),
            GameDiscoveryState.Failed => (
                "The verified installer session stopped safely",
                "No game files were changed. Close and reopen the installer before trying again."
            ),
            GameDiscoveryState.SessionFaulted => (
                "The verified installer session closed",
                "No game files were changed. Close and reopen the installer before trying again."
            ),
            GameDiscoveryState.Transferred => (
                "Opening plan review…",
                "The validated game folder and verified release are moving to the read-only plan screen. Nothing has been changed."
            ),
            GameDiscoveryState.Disposed => (
                "Closing safely…",
                "The read-only game-folder session has closed."
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
    }

    private GameDiscoveryFocusTarget GetFocusTarget(GameDiscoverySnapshot value)
    {
        return value.State switch
        {
            GameDiscoveryState.Ready => GameDiscoveryFocusTarget.CandidateList,
            GameDiscoveryState.NoCandidates or GameDiscoveryState.ManualInvalid when this.BrowseCommand.CanExecute(null) => GameDiscoveryFocusTarget.Browse,
            GameDiscoveryState.Cancelled or GameDiscoveryState.Failed or GameDiscoveryState.SessionFaulted => GameDiscoveryFocusTarget.Exit,
            _ => GameDiscoveryFocusTarget.Status
        };
    }

    private void NotifyDerivedProperties()
    {
        this.OnPropertyChanged(nameof(this.IsBusy));
        this.OnPropertyChanged(nameof(this.IsCandidateListVisible));
        this.OnPropertyChanged(nameof(this.IsEmptyVisible));
        this.OnPropertyChanged(nameof(this.IsProblemVisible));
        this.OnPropertyChanged(nameof(this.StatusLiveSetting));
        this.OnPropertyChanged(nameof(this.ProblemLiveSetting));
        this.OnPropertyChanged(nameof(this.IsBrowseVisible));
        this.OnPropertyChanged(nameof(this.IsRetryVisible));
        this.OnPropertyChanged(nameof(this.IsCancelVisible));
        this.OnPropertyChanged(nameof(this.IsExitVisible));
        this.RetryCommand.NotifyCanExecuteChanged();
        this.BrowseCommand.NotifyCanExecuteChanged();
        this.CancelCommand.NotifyCanExecuteChanged();
        this.ExitCommand.NotifyCanExecuteChanged();
    }

    private void HandlePresentationFailure(Exception exception)
    {
        this.Heading = "The game-folder action stopped safely";
        this.Message = "Close and reopen the installer before trying again. No game files were changed.";
        this.LiveAnnouncement = $"{this.Heading}. {this.Message}";
        this.FocusRequested?.Invoke(this, GameDiscoveryFocusTarget.Status);
    }
}
