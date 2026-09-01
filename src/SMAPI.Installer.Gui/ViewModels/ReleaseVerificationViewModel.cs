using Avalonia.Automation;
using Avalonia.Threading;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Gui.Frontend;

namespace StardewModdingAPI.Installer.Gui.ViewModels;

internal enum ReleaseVerificationFocusTarget
{
    Status,
    ReleaseSelector,
    Retry,
    Continue
}

/// <summary>Presentation-only adapter for the serialized production release-verification controller.</summary>
internal sealed class ReleaseVerificationViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ReleaseVerificationController Controller;
    private IReadOnlyList<ReviewedReleaseCandidate> releases = Array.Empty<ReviewedReleaseCandidate>();
    private ReviewedReleaseCandidate? selectedRelease;
    private ReleaseVerificationSnapshot snapshot;
    private string heading = "Ready to check for releases";
    private string message = "The installer will list only complete experimental Linux releases reviewed by its built-in policy.";
    private string liveAnnouncement = "Ready to check for compatible Linux releases.";
    private string progressText = "";
    private string releaseDetail = "No release selected.";
    private string verifiedIdentityDetail = "";
    private double progressValue;
    private EventHandler? ContinueRequestedValue;
    private long AnnouncedGeneration = -1;
    private ReleaseVerificationState? AnnouncedState;
    private int AnnouncedAsset = -1;
    private int AnnouncedPercentBucket = -1;
    private bool transitionFailed;
    private bool started;
    private bool disposed;

    public ReleaseVerificationViewModel(ReleaseVerificationController controller)
    {
        this.Controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.snapshot = controller.Snapshot;
        this.LoadCatalogCommand = new AsyncRelayCommand(this.LoadCatalogAsync, this.CanLoadCatalog, this.HandlePresentationFailure);
        this.DownloadAndVerifyCommand = new AsyncRelayCommand(this.StartVerificationAsync, () => this.snapshot.CanStart, this.HandlePresentationFailure);
        this.RetryCommand = new AsyncRelayCommand(this.RetryAsync, this.CanRetry, this.HandlePresentationFailure);
        this.CancelCommand = new AsyncRelayCommand(this.Controller.CancelAsync, () => this.snapshot.CanCancel, this.HandlePresentationFailure);
        this.ContinueCommand = new RelayCommand(
            () => this.ContinueRequestedValue?.Invoke(this, EventArgs.Empty),
            () => !this.transitionFailed
                && this.snapshot.State == ReleaseVerificationState.Verified
                && this.ContinueRequestedValue is not null
        );
        this.Controller.Changed += this.OnControllerChanged;
        this.ApplySnapshot(this.snapshot, requestFocus: false);
    }

    public event EventHandler<ReleaseVerificationFocusTarget>? FocusRequested;

    /// <summary>Raised only after the backend has authoritatively opened the package.</summary>
    public event EventHandler? ContinueRequested
    {
        add
        {
            this.ContinueRequestedValue += value;
            this.OnPropertyChanged(nameof(this.IsContinueVisible));
            this.ContinueCommand.NotifyCanExecuteChanged();
        }
        remove
        {
            this.ContinueRequestedValue -= value;
            this.OnPropertyChanged(nameof(this.IsContinueVisible));
            this.ContinueCommand.NotifyCanExecuteChanged();
        }
    }

    public IReadOnlyList<ReviewedReleaseCandidate> Releases
    {
        get => this.releases;
        private set => this.SetProperty(ref this.releases, value);
    }

    public ReviewedReleaseCandidate? SelectedRelease
    {
        get => this.selectedRelease;
        set
        {
            if (value is null || ReferenceEquals(value, this.selectedRelease))
                return;
            this.Controller.SelectRelease(value);
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

    public string ProgressText
    {
        get => this.progressText;
        private set => this.SetProperty(ref this.progressText, value);
    }

    public string ReleaseDetail
    {
        get => this.releaseDetail;
        private set => this.SetProperty(ref this.releaseDetail, value);
    }

    public string VerifiedIdentityDetail
    {
        get => this.verifiedIdentityDetail;
        private set => this.SetProperty(ref this.verifiedIdentityDetail, value);
    }

    public double ProgressValue
    {
        get => this.progressValue;
        private set => this.SetProperty(ref this.progressValue, value);
    }

    public string DurableState => "Unchanged — nothing has been installed";

    public bool IsReleaseSelectorEnabled => this.snapshot.State == ReleaseVerificationState.Ready && this.snapshot.CanStart;

    public bool IsDownloadActionVisible => this.snapshot.State == ReleaseVerificationState.Ready;

    public bool IsRetryVisible => this.CanRetry();

    public bool IsContinueVisible => !this.transitionFailed
        && this.snapshot.State == ReleaseVerificationState.Verified
        && this.ContinueRequestedValue is not null;

    public bool IsCancelVisible => this.snapshot.CanCancel;

    public bool IsProgressVisible => this.snapshot.State is ReleaseVerificationState.LoadingCatalog
        or ReleaseVerificationState.Handshaking
        or ReleaseVerificationState.Preparing
        or ReleaseVerificationState.OpeningPackage
        or ReleaseVerificationState.CleaningUp;

    public bool IsProgressIndeterminate => this.snapshot.Progress is not
    {
        Stage: ReviewedReleasePreparationStage.Downloading,
        TotalBytes: > 0
    };

    public bool IsErrorVisible => this.transitionFailed || this.snapshot.State == ReleaseVerificationState.Failed;

    public AutomationLiveSetting StatusLiveSetting => this.IsErrorVisible
        ? AutomationLiveSetting.Off
        : AutomationLiveSetting.Polite;

    public bool IsVerifiedVisible => !this.transitionFailed && this.snapshot.State == ReleaseVerificationState.Verified;

    public bool IsEmptyVisible => this.snapshot.State == ReleaseVerificationState.NoCompatibleRelease;

    public AsyncRelayCommand LoadCatalogCommand { get; }

    public AsyncRelayCommand DownloadAndVerifyCommand { get; }

    public AsyncRelayCommand RetryCommand { get; }

    public AsyncRelayCommand CancelCommand { get; }

    public RelayCommand ContinueCommand { get; }

    /// <summary>Publish a sanitized fail-closed state if the next local window couldn't take ownership.</summary>
    internal void ReportTransitionFailure()
    {
        if (this.disposed)
            return;
        this.transitionFailed = true;
        this.Heading = "The game-folder step could not open";
        this.Message = "The verified session was closed safely. Close and reopen the installer before trying again; no game files were changed.";
        this.LiveAnnouncement = $"{this.Heading}. {this.Message}";
        this.NotifyDerivedProperties();
        this.FocusRequested?.Invoke(this, ReleaseVerificationFocusTarget.Status);
    }

    public async Task StartAsync()
    {
        if (this.disposed || this.started)
            return;
        this.started = true;
        await this.LoadCatalogAsync().ConfigureAwait(true);
    }

    public async ValueTask DisposeAsync()
    {
        if (this.disposed)
            return;
        this.disposed = true;
        await this.Controller.DisposeAsync().ConfigureAwait(true);
        this.Controller.Changed -= this.OnControllerChanged;
    }

    private bool CanLoadCatalog()
    {
        return !this.disposed
            && !this.snapshot.CanCancel
            && this.snapshot.State is ReleaseVerificationState.Idle
                or ReleaseVerificationState.NoCompatibleRelease
                or ReleaseVerificationState.Cancelled
                or ReleaseVerificationState.Failed;
    }

    private bool CanRetry()
    {
        if (this.disposed || this.snapshot.CanCancel)
            return false;
        if (this.snapshot.State == ReleaseVerificationState.NoCompatibleRelease)
            return true;
        if (this.snapshot.Error == ReleaseVerificationError.CatalogUnavailable || this.snapshot.AttemptNumber == 0)
            return this.snapshot.State is ReleaseVerificationState.Cancelled or ReleaseVerificationState.Failed;
        return this.snapshot.CanRetry
            && (
                this.snapshot.State == ReleaseVerificationState.Cancelled
                || this.snapshot.Error is ReleaseVerificationError.PreparationFailed
                    or ReleaseVerificationError.PackageRejected
            );
    }

    private Task LoadCatalogAsync()
    {
        return this.Controller.LoadCatalogAsync();
    }

    private Task StartVerificationAsync()
    {
        return this.Controller.StartAsync();
    }

    private Task RetryAsync()
    {
        if (
            this.snapshot.State == ReleaseVerificationState.NoCompatibleRelease
            || this.snapshot.Error == ReleaseVerificationError.CatalogUnavailable
            || this.snapshot.AttemptNumber == 0
        )
        {
            return this.Controller.LoadCatalogAsync();
        }
        return this.Controller.RetryAsync();
    }

    private void OnControllerChanged(object? sender, EventArgs e)
    {
        ReleaseVerificationSnapshot next = this.Controller.Snapshot;
        if (Dispatcher.UIThread.CheckAccess())
            this.ApplySnapshot(next, requestFocus: true);
        else
            Dispatcher.UIThread.Post(() => this.ApplySnapshot(next, requestFocus: true));
    }

    private void ApplySnapshot(ReleaseVerificationSnapshot next, bool requestFocus)
    {
        if (this.disposed && next.State != ReleaseVerificationState.Disposed)
            return;

        ReleaseVerificationState previousState = this.snapshot.State;
        long previousGeneration = this.snapshot.Generation;
        this.snapshot = next;
        this.Releases = next.Releases;
        this.SetProperty(ref this.selectedRelease, next.SelectedRelease, nameof(this.SelectedRelease));
        this.ReleaseDetail = FormatReleaseDetail(next.SelectedRelease);
        this.VerifiedIdentityDetail = FormatVerifiedIdentity(next.VerifiedRelease);
        (this.Heading, this.Message) = GetCopy(next);
        this.ProgressText = GetProgressText(next);
        this.ProgressValue = GetProgressValue(next.Progress);
        this.UpdateLiveAnnouncement(next);
        this.NotifyDerivedProperties();

        bool stateChanged = previousState != next.State || previousGeneration != next.Generation;
        if (requestFocus && stateChanged)
            this.FocusRequested?.Invoke(this, this.GetFocusTarget(next));
    }

    private void NotifyDerivedProperties()
    {
        this.OnPropertyChanged(nameof(this.IsReleaseSelectorEnabled));
        this.OnPropertyChanged(nameof(this.IsDownloadActionVisible));
        this.OnPropertyChanged(nameof(this.IsRetryVisible));
        this.OnPropertyChanged(nameof(this.IsContinueVisible));
        this.OnPropertyChanged(nameof(this.IsCancelVisible));
        this.OnPropertyChanged(nameof(this.IsProgressVisible));
        this.OnPropertyChanged(nameof(this.IsProgressIndeterminate));
        this.OnPropertyChanged(nameof(this.IsErrorVisible));
        this.OnPropertyChanged(nameof(this.StatusLiveSetting));
        this.OnPropertyChanged(nameof(this.IsVerifiedVisible));
        this.OnPropertyChanged(nameof(this.IsEmptyVisible));
        this.LoadCatalogCommand.NotifyCanExecuteChanged();
        this.DownloadAndVerifyCommand.NotifyCanExecuteChanged();
        this.RetryCommand.NotifyCanExecuteChanged();
        this.CancelCommand.NotifyCanExecuteChanged();
        this.ContinueCommand.NotifyCanExecuteChanged();
    }

    private void HandlePresentationFailure(Exception exception)
    {
        // Controller failures are normally represented by a sanitized snapshot. A presentation race must still fail closed.
        this.Heading = "The release action stopped safely";
        this.Message = "Close and reopen the installer before trying again. No game files were changed.";
        this.LiveAnnouncement = this.Heading + ". " + this.Message;
        this.FocusRequested?.Invoke(this, ReleaseVerificationFocusTarget.Status);
    }

    private static (string Heading, string Message) GetCopy(ReleaseVerificationSnapshot value)
    {
        return value.State switch
        {
            ReleaseVerificationState.Idle => (
                "Ready to check for releases",
                "The installer will list only complete experimental Linux releases reviewed by its built-in policy."
            ),
            ReleaseVerificationState.LoadingCatalog => (
                "Checking for compatible Linux releases…",
                "Contacting the reviewed GitHub release catalog. Nothing is being installed."
            ),
            ReleaseVerificationState.NoCompatibleRelease => (
                "No compatible graphical-installer release is available",
                "Published releases do not yet contain the complete six-file package required by this installer. Nothing was downloaded and your game is unchanged."
            ),
            ReleaseVerificationState.Ready => (
                "Choose an experimental Linux release",
                "Review the selected prerelease, then download and verify it before choosing a game folder."
            ),
            ReleaseVerificationState.Handshaking => (
                "Starting the verification service…",
                "Opening the local installer service. Nothing is being installed."
            ),
            ReleaseVerificationState.Preparing => value.Progress?.Stage switch
            {
                ReviewedReleasePreparationStage.ObservingTag => (
                    "Checking the selected release identity…",
                    "Confirming that the annotated release tag identifies the selected release."
                ),
                ReviewedReleasePreparationStage.Downloading => (
                    "Downloading release files…",
                    "Downloading the complete six-file package into private temporary storage."
                ),
                ReviewedReleasePreparationStage.RefreshingTag => (
                    "Rechecking the selected release identity…",
                    "Confirming that the selected tag did not move while its files were downloaded."
                ),
                _ => (
                    "Preparing the selected release…",
                    "Checking release identity before downloading. Nothing is being installed."
                )
            },
            ReleaseVerificationState.OpeningPackage => (
                "Verifying the downloaded release…",
                "Checking package integrity, release metadata, GitHub provenance, and the local installer package."
            ),
            ReleaseVerificationState.CleaningUp => (
                "Cleaning up safely…",
                "Waiting for the current bounded step to settle and releasing retained session resources."
            ),
            ReleaseVerificationState.Verified => (
                "Release verified — ready to review",
                "Nothing has been installed yet. Continue to choose a game folder and review a change plan."
            ),
            ReleaseVerificationState.Cancelled => (
                "Download cancelled",
                "No complete installer package was retained. Your game is unchanged."
            ),
            ReleaseVerificationState.Failed => value.Error switch
            {
                ReleaseVerificationError.CatalogUnavailable => (
                    "Couldn’t check for releases",
                    "No package was downloaded. Check your connection, then try again."
                ),
                ReleaseVerificationError.PreparationFailed => (
                    "The release could not be prepared safely",
                    value.CanRetry
                        ? "No complete package was retained and your game is unchanged. Try the download once more."
                        : "No complete package was retained and your game is unchanged. Close and reopen the installer before trying again."
                ),
                ReleaseVerificationError.PackageRejected => (
                    "The release could not be verified",
                    $"Installation is blocked and your game is unchanged. {GetRejectionNextStep(value)}"
                ),
                ReleaseVerificationError.SessionFaulted or ReleaseVerificationError.BackendUnavailable => (
                    "The verification service stopped safely",
                    "Close and reopen the installer before trying again. No game files were changed."
                ),
                ReleaseVerificationError.RetryLimitReached => (
                    "The release could not be verified after several attempts",
                    "Close and reopen the installer before trying again. No game files were changed."
                ),
                ReleaseVerificationError.CleanupFailed => (
                    "Release cleanup did not finish normally",
                    "Close and reopen the installer before trying again. No game files were changed."
                ),
                _ => (
                    "The release action stopped safely",
                    "No game files were changed. Close and reopen the installer before trying again."
                )
            },
            ReleaseVerificationState.Disposed => (
                "Closing safely…",
                "The release session has been closed and retained files were released."
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
    }

    private static string GetProgressText(ReleaseVerificationSnapshot value)
    {
        ReviewedReleasePreparationProgress? progress = value.Progress;
        if (progress is not { Stage: ReviewedReleasePreparationStage.Downloading, TotalBytes: > 0 })
            return "";

        int visibleAsset = progress.AssetKind.HasValue
            ? (int)progress.AssetKind.Value + 1
            : Math.Min(progress.CompletedAssets + 1, progress.TotalAssets);
        int percent = (int)Math.Clamp((progress.TransferredBytes * 100L) / progress.TotalBytes, 0, 100);
        string asset = progress.AssetKind.HasValue ? GetAssetLabel(progress.AssetKind.Value) : "release file";
        return $"File {visibleAsset} of {progress.TotalAssets}: {asset} — Overall: {FormatBytes(progress.TransferredBytes)} of {FormatBytes(progress.TotalBytes)} ({percent}%)";
    }

    private static double GetProgressValue(ReviewedReleasePreparationProgress? progress)
    {
        return progress is { Stage: ReviewedReleasePreparationStage.Downloading, TotalBytes: > 0 }
            ? Math.Clamp((double)progress.TransferredBytes / progress.TotalBytes * 100d, 0d, 100d)
            : 0d;
    }

    private void UpdateLiveAnnouncement(ReleaseVerificationSnapshot value)
    {
        bool newState = value.Generation != this.AnnouncedGeneration || value.State != this.AnnouncedState;
        if (value.Progress is { Stage: ReviewedReleasePreparationStage.Downloading, TotalBytes: > 0 } progress)
        {
            int percentBucket = Math.Min((int)GetProgressValue(progress) / 10 * 10, 100);
            int asset = progress.AssetKind.HasValue
                ? (int)progress.AssetKind.Value + 1
                : Math.Min(progress.CompletedAssets + 1, progress.TotalAssets);
            if (newState || asset != this.AnnouncedAsset || percentBucket != this.AnnouncedPercentBucket)
            {
                this.LiveAnnouncement = $"Downloading file {asset} of {progress.TotalAssets}, overall {percentBucket} percent.";
                this.AnnouncedAsset = asset;
                this.AnnouncedPercentBucket = percentBucket;
            }
        }
        else if (newState || value.Progress?.Stage != ReviewedReleasePreparationStage.Downloading)
        {
            this.LiveAnnouncement = value.State == ReleaseVerificationState.Failed
                ? $"{this.Heading}. {this.Message}"
                : this.Heading;
            this.AnnouncedAsset = -1;
            this.AnnouncedPercentBucket = -1;
        }
        this.AnnouncedGeneration = value.Generation;
        this.AnnouncedState = value.State;
    }

    private ReleaseVerificationFocusTarget GetFocusTarget(ReleaseVerificationSnapshot value)
    {
        return value.State switch
        {
            ReleaseVerificationState.Ready => ReleaseVerificationFocusTarget.ReleaseSelector,
            ReleaseVerificationState.NoCompatibleRelease or ReleaseVerificationState.Cancelled => ReleaseVerificationFocusTarget.Retry,
            ReleaseVerificationState.Verified when this.ContinueCommand.CanExecute(null) => ReleaseVerificationFocusTarget.Continue,
            _ => ReleaseVerificationFocusTarget.Status
        };
    }

    private static string FormatReleaseDetail(ReviewedReleaseCandidate? release)
    {
        if (release is null)
            return "No compatible release selected.";
        long totalBytes = release.Assets.Sum(asset => asset.SizeBytes);
        return $"{release.DisplayLabel}\nExact tag: {release.Identity.Tag}\nSix advertised files • {FormatBytes(totalBytes)} total";
    }

    private static string FormatVerifiedIdentity(ProtocolReleaseIdentity? release)
    {
        if (release is null)
            return "";
        return $"Verified tag: {release.Tag}\nVerified source commit: {release.SourceCommit}";
    }

    private static string GetRejectionNextStep(ReleaseVerificationSnapshot value)
    {
        if (value.AttemptNumber >= value.MaximumAttempts)
            return "The retry limit has been reached. Close and reopen the installer to start a new verification session.";
        if (value.RejectionIsTerminal)
            return "Close and reopen the installer to start a new verification session.";
        if (!value.CanRetry)
            return "Close and reopen the installer to start a new verification session.";
        return value.RejectionNextAction switch
        {
            ProtocolNextAction.RetryRequest or ProtocolNextAction.ReopenVerifiedPackage =>
                "Try the download once more.",
            ProtocolNextAction.StartNewSession =>
                "Close and reopen the installer before trying again.",
            ProtocolNextAction.ViewPrivateLog =>
                "Review the private local log, then close and reopen the installer.",
            _ => "Close and reopen the installer before trying again."
        };
    }

    private static string GetAssetLabel(ReviewedReleaseAssetKind kind)
    {
        return kind switch
        {
            ReviewedReleaseAssetKind.InstallerPackage => "installer package",
            ReviewedReleaseAssetKind.InstallManifest => "installation manifest",
            ReviewedReleaseAssetKind.Checksums => "checksums",
            ReviewedReleaseAssetKind.BuildMetadata => "build metadata",
            ReviewedReleaseAssetKind.AttestationBundle => "provenance bundle",
            ReviewedReleaseAssetKind.AttestationBundleChecksum => "provenance checksum",
            _ => "release file"
        };
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024d:0.0} KiB";
        return $"{bytes / (1024d * 1024d):0.0} MiB";
    }
}
