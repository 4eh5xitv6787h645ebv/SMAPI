using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Gui.Backend;

namespace StardewModdingAPI.Installer.Gui.Frontend;

internal enum ReleaseVerificationState
{
    Idle,
    LoadingCatalog,
    NoCompatibleRelease,
    Ready,
    Handshaking,
    Preparing,
    OpeningPackage,
    CleaningUp,
    Verified,
    Cancelled,
    Failed,
    Disposed
}

internal enum ReleaseVerificationError
{
    None,
    CatalogUnavailable,
    PreparationFailed,
    PackageRejected,
    BackendUnavailable,
    SessionFaulted,
    RetryLimitReached,
    CleanupFailed
}

internal sealed record ReleaseVerificationSnapshot(
    long Generation,
    ReleaseVerificationState State,
    ReleaseVerificationError Error,
    IReadOnlyList<ReviewedReleaseCandidate> Releases,
    ReviewedReleaseCandidate? SelectedRelease,
    ReviewedReleasePreparationProgress? Progress,
    int AttemptNumber,
    int MaximumAttempts,
    bool CanStart,
    bool CanRetry,
    bool CanCancel,
    ProtocolReleaseIdentity? VerifiedRelease,
    ProtocolPrePlanErrorCode? RejectionCode,
    ProtocolNextAction? RejectionNextAction,
    bool RejectionIsTerminal
);

/// <summary>
/// Serializes the release-catalog, preparation, and backend package-open authorities without exposing remote text,
/// private paths, or raw failures to the presentation layer.
/// </summary>
internal sealed class ReleaseVerificationController : IAsyncDisposable
{
    internal const int MaximumAttempts = 3;
    private const string ProtocolClientName = "SMAPI Linux GUI";
    private const string ProtocolClientVersion = "1";

    private readonly object Sync = new();
    private readonly IReviewedReleaseService ReleaseService;
    private readonly Func<IInstallerProtocolClient> ClientFactory;
    private ReviewedReleaseCandidate[] ReleasesValue = [];
    private ReviewedReleaseCandidate? SelectedReleaseValue;
    private ReviewedReleasePreparationProgress? ProgressValue;
    private ReleaseVerificationState StateValue = ReleaseVerificationState.Idle;
    private ReleaseVerificationError ErrorValue;
    private ControllerOperation? ActiveOperation;
    private AttemptContext? VerifiedAttempt;
    private long GenerationValue;
    private int AttemptNumberValue;
    private bool DisposeStarted;
    private ProtocolReleaseIdentity? VerifiedReleaseValue;
    private ProtocolPrePlanErrorCode? RejectionCodeValue;
    private ProtocolNextAction? RejectionNextActionValue;
    private bool RejectionIsTerminalValue;
    private Task? DisposalTask;

    public ReleaseVerificationController(
        IReviewedReleaseService releaseService,
        Func<IInstallerProtocolClient> clientFactory
    )
    {
        this.ReleaseService = releaseService ?? throw new ArgumentNullException(nameof(releaseService));
        this.ClientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    public event EventHandler? Changed;

    public ReleaseVerificationSnapshot Snapshot
    {
        get
        {
            lock (this.Sync)
                return this.CreateSnapshot();
        }
    }

    public Task LoadCatalogAsync(CancellationToken cancellationToken = default)
    {
        ControllerOperation operation;
        lock (this.Sync)
        {
            this.AssertNotDisposed();
            if (this.ActiveOperation is not null || this.VerifiedAttempt is not null)
                throw new InvalidOperationException("Release verification already owns an active operation or verified backend session.");

            operation = this.BeginOperation(cancellationToken);
            this.ReleasesValue = [];
            this.SelectedReleaseValue = null;
            this.ProgressValue = null;
            this.AttemptNumberValue = 0;
            this.ClearResultAuthority();
            this.StateValue = ReleaseVerificationState.LoadingCatalog;
            this.ErrorValue = ReleaseVerificationError.None;
        }
        this.PublishChanged();
        _ = this.RunCatalogLoadAsync(operation);
        return operation.Completion.Task;
    }

    public void SelectRelease(ReviewedReleaseCandidate release)
    {
        ArgumentNullException.ThrowIfNull(release);
        lock (this.Sync)
        {
            this.AssertNotDisposed();
            if (this.ActiveOperation is not null || this.VerifiedAttempt is not null)
                throw new InvalidOperationException("A release cannot be selected while verification owns an active authority.");
            ReviewedReleaseCandidate selected = this.ReleasesValue.SingleOrDefault(value => ReferenceEquals(value, release))
                ?? throw new ArgumentException("The release must be the exact current catalog candidate instance.", nameof(release));
            if (!ReferenceEquals(this.SelectedReleaseValue, selected))
                this.AttemptNumberValue = 0;
            this.SelectedReleaseValue = selected;
            this.ProgressValue = null;
            this.ClearResultAuthority();
            this.StateValue = ReleaseVerificationState.Ready;
            this.ErrorValue = ReleaseVerificationError.None;
        }
        this.PublishChanged();
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        AttemptContext attempt;
        lock (this.Sync)
        {
            this.AssertNotDisposed();
            if (this.ActiveOperation is not null || this.VerifiedAttempt is not null)
                throw new InvalidOperationException("Release verification already owns an active operation or verified backend session.");
            if (this.StateValue != ReleaseVerificationState.Ready || this.SelectedReleaseValue is null)
                throw new InvalidOperationException("A current reviewed release must be selected before verification starts.");
            if (!this.ReleasesValue.Any(value => ReferenceEquals(value, this.SelectedReleaseValue)))
                throw new InvalidOperationException("The selected release is no longer in the current catalog.");
            if (this.AttemptNumberValue != 0)
                throw new InvalidOperationException("Use RetryAsync after a completed verification attempt.");

            attempt = this.BeginAttempt(cancellationToken);
        }
        this.PublishChanged();
        _ = this.RunAttemptAsync(attempt);
        return attempt.Operation.Completion.Task;
    }

    public Task RetryAsync(CancellationToken cancellationToken = default)
    {
        ControllerOperation operation;
        string selectedTag;
        int attemptNumber;
        lock (this.Sync)
        {
            this.AssertNotDisposed();
            if (this.ActiveOperation is not null || this.VerifiedAttempt is not null)
                throw new InvalidOperationException("Release verification hasn't finished disposing its prior authorities.");
            if (this.StateValue is not (ReleaseVerificationState.Failed or ReleaseVerificationState.Cancelled))
                throw new InvalidOperationException("Only a completed failed or cancelled attempt can be retried.");
            bool retryableOutcome = this.StateValue == ReleaseVerificationState.Cancelled
                || this.ErrorValue == ReleaseVerificationError.PreparationFailed
                || (this.ErrorValue == ReleaseVerificationError.PackageRejected && !this.RejectionIsTerminalValue);
            if (!retryableOutcome)
                throw new InvalidOperationException("This failure requires a new installer session and cannot be retried safely.");
            if (this.SelectedReleaseValue is null || !this.ReleasesValue.Any(value => ReferenceEquals(value, this.SelectedReleaseValue)))
                throw new InvalidOperationException("The selected release is no longer in the current catalog.");
            if (this.AttemptNumberValue >= MaximumAttempts)
            {
                this.ErrorValue = ReleaseVerificationError.RetryLimitReached;
                throw new InvalidOperationException("The bounded release-verification retry limit was reached.");
            }
            selectedTag = this.SelectedReleaseValue.Identity.Tag;
            attemptNumber = this.AttemptNumberValue + 1;
            operation = this.BeginOperation(cancellationToken);
            this.StateValue = ReleaseVerificationState.LoadingCatalog;
            this.ErrorValue = ReleaseVerificationError.None;
            this.ProgressValue = null;
            this.ClearResultAuthority();
        }
        this.PublishChanged();
        _ = this.RunRetryAsync(operation, selectedTag, attemptNumber);
        return operation.Completion.Task;
    }

    public async Task CancelAsync()
    {
        ControllerOperation? operation;
        lock (this.Sync)
        {
            this.AssertNotDisposed();
            operation = this.ActiveOperation;
            if (operation is null)
                return;
            this.StateValue = ReleaseVerificationState.CleaningUp;
            this.ProgressValue = null;
        }
        this.PublishChanged();
        operation.Cancellation.Cancel();
        await operation.Completion.Task.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        ControllerOperation? operation;
        AttemptContext? verified;
        lock (this.Sync)
        {
            if (this.DisposalTask is not null)
                return new ValueTask(this.DisposalTask);
            this.DisposeStarted = true;
            operation = this.ActiveOperation;
            verified = this.VerifiedAttempt;
            this.StateValue = ReleaseVerificationState.CleaningUp;
            this.ProgressValue = null;
            this.ClearResultAuthority();
            return new ValueTask(this.DisposalTask = this.DisposeCoreAsync(operation, verified));
        }
    }

    private async Task DisposeCoreAsync(ControllerOperation? operation, AttemptContext? verified)
    {
        await Task.Yield();
        bool cleanupFailed = false;
        this.PublishChanged();
        operation?.Cancellation.Cancel();

        if (operation is not null)
            await operation.Completion.Task.ConfigureAwait(false);
        if (verified is not null)
        {
            verified.StopWatching.TrySetResult();
            try
            {
                await this.DisposeClientOnceAsync(verified).ConfigureAwait(false);
            }
            catch
            {
                cleanupFailed = true;
            }
        }
        try
        {
            await this.ReleaseService.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            cleanupFailed = true;
        }

        lock (this.Sync)
        {
            this.VerifiedAttempt = null;
            this.StateValue = ReleaseVerificationState.Disposed;
            this.ErrorValue = cleanupFailed
                ? ReleaseVerificationError.CleanupFailed
                : ReleaseVerificationError.None;
            this.ProgressValue = null;
            this.ClearResultAuthority();
        }
        this.PublishChanged();
    }

    private ControllerOperation BeginOperation(CancellationToken callerCancellation)
    {
        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation);
        ControllerOperation operation = new(++this.GenerationValue, cancellation);
        this.ActiveOperation = operation;
        return operation;
    }

    private AttemptContext BeginAttempt(CancellationToken callerCancellation)
    {
        ReviewedReleaseCandidate selected = this.SelectedReleaseValue!;
        ControllerOperation operation = this.BeginOperation(callerCancellation);
        AttemptContext attempt = new(operation, selected, ++this.AttemptNumberValue);
        this.ProgressValue = null;
        this.ClearResultAuthority();
        this.StateValue = ReleaseVerificationState.Handshaking;
        this.ErrorValue = ReleaseVerificationError.None;
        return attempt;
    }

    private async Task RunRetryAsync(ControllerOperation operation, string selectedTag, int attemptNumber)
    {
        await Task.Yield();
        try
        {
            IReadOnlyList<ReviewedReleaseCandidate> loaded = await this.ReleaseService.LoadCatalogAsync(
                operation.Cancellation.Token
            ).ConfigureAwait(false);
            operation.Cancellation.Token.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(loaded);
            ReviewedReleaseCandidate[] releases = ValidateCatalog(loaded);
            ReviewedReleaseCandidate? refreshed = releases.SingleOrDefault(value => string.Equals(
                value.Identity.Tag,
                selectedTag,
                StringComparison.Ordinal
            ));

            lock (this.Sync)
            {
                if (!this.IsCurrent(operation))
                    return;
                this.ReleasesValue = releases;
                this.SelectedReleaseValue = refreshed;
                if (refreshed is null)
                {
                    this.ActiveOperation = null;
                    this.StateValue = releases.Length == 0
                        ? ReleaseVerificationState.NoCompatibleRelease
                        : ReleaseVerificationState.Failed;
                    this.ErrorValue = releases.Length == 0
                        ? ReleaseVerificationError.None
                        : ReleaseVerificationError.CatalogUnavailable;
                }
                else
                {
                    this.AttemptNumberValue = attemptNumber;
                    this.StateValue = ReleaseVerificationState.Handshaking;
                    this.ErrorValue = ReleaseVerificationError.None;
                }
            }
            this.PublishChanged();
            if (refreshed is null)
            {
                operation.Cancellation.Dispose();
                operation.Completion.TrySetResult();
                return;
            }

            await this.RunAttemptAsync(new AttemptContext(operation, refreshed, attemptNumber), yieldFirst: false)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested)
        {
            this.SetOperationOutcome(operation, ReleaseVerificationState.Cancelled, ReleaseVerificationError.None);
            this.CompleteOperation(operation);
        }
        catch
        {
            this.SetOperationOutcome(operation, ReleaseVerificationState.Failed, ReleaseVerificationError.CatalogUnavailable);
            this.CompleteOperation(operation);
        }
    }

    private async Task RunCatalogLoadAsync(ControllerOperation operation)
    {
        await Task.Yield();
        try
        {
            IReadOnlyList<ReviewedReleaseCandidate> loaded = await this.ReleaseService.LoadCatalogAsync(
                operation.Cancellation.Token
            ).ConfigureAwait(false);
            operation.Cancellation.Token.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(loaded);
            ReviewedReleaseCandidate[] releases = ValidateCatalog(loaded);

            lock (this.Sync)
            {
                if (!this.IsCurrent(operation))
                    return;
                this.ReleasesValue = releases;
                this.SelectedReleaseValue = releases.FirstOrDefault();
                this.StateValue = releases.Length == 0
                    ? ReleaseVerificationState.NoCompatibleRelease
                    : ReleaseVerificationState.Ready;
                this.ErrorValue = ReleaseVerificationError.None;
            }
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested)
        {
            this.SetOperationOutcome(operation, ReleaseVerificationState.Cancelled, ReleaseVerificationError.None);
        }
        catch
        {
            this.SetOperationOutcome(operation, ReleaseVerificationState.Failed, ReleaseVerificationError.CatalogUnavailable);
        }
        finally
        {
            this.CompleteOperation(operation);
        }
    }

    private Task RunAttemptAsync(AttemptContext attempt)
    {
        return this.RunAttemptAsync(attempt, yieldFirst: true);
    }

    private async Task RunAttemptAsync(AttemptContext attempt, bool yieldFirst)
    {
        if (yieldFirst)
            await Task.Yield();
        IPreparedReleasePackage? prepared = null;
        ReleaseVerificationState outcomeState = ReleaseVerificationState.Failed;
        ReleaseVerificationError outcomeError = ReleaseVerificationError.BackendUnavailable;
        bool transferVerifiedClient = false;
        bool preparedDisposalAttempted = false;
        try
        {
            attempt.Client = this.ClientFactory()
                ?? throw new InvalidOperationException("The installer protocol client factory returned null.");
            Task handshake = attempt.Client.HandshakeAsync(
                ProtocolClientName,
                ProtocolClientVersion,
                attempt.Operation.Cancellation.Token
            );
            await this.AwaitWithSessionFaultAsync(handshake, attempt).ConfigureAwait(false);
            attempt.Operation.Cancellation.Token.ThrowIfCancellationRequested();

            this.SetAttemptStage(attempt, ReleaseVerificationState.Preparing);
            PreparationProgressTracker progress = new(this, attempt);
            Task<IPreparedReleasePackage> preparation = this.ReleaseService.PrepareAsync(
                attempt.Candidate,
                progress,
                attempt.Operation.Cancellation.Token
            );
            prepared = await this.AwaitWithSessionFaultAsync(preparation, attempt).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(prepared);
            progress.SealAndAssertComplete();
            attempt.Operation.Cancellation.Token.ThrowIfCancellationRequested();

            this.SetAttemptStage(attempt, ReleaseVerificationState.OpeningPackage);
            Task<InstallerPackageOpenResult> opening = attempt.Client.OpenPackageAsync(
                prepared.Package,
                attempt.Operation.Cancellation.Token
            );
            InstallerPackageOpenResult result = await this.AwaitWithSessionFaultAsync(opening, attempt).ConfigureAwait(false);
            attempt.Operation.Cancellation.Token.ThrowIfCancellationRequested();
            if (attempt.Client.SessionFaulted.IsCompleted)
                throw new BackendSessionFaultException();

            if (result is InstallerPackageOpenSuccess success)
            {
                preparedDisposalAttempted = true;
                try
                {
                    await prepared.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    throw new PreparedReleaseDisposalException();
                }
                prepared = null;
                if (attempt.Client.SessionFaulted.IsCompleted)
                    throw new BackendSessionFaultException();
                lock (this.Sync)
                {
                    attempt.Operation.Cancellation.Token.ThrowIfCancellationRequested();
                    if (this.DisposeStarted || !this.IsCurrent(attempt.Operation))
                        throw new OperationCanceledException(attempt.Operation.Cancellation.Token);
                    if (attempt.Client.SessionFaulted.IsCompleted)
                        throw new BackendSessionFaultException();
                    this.VerifiedReleaseValue = success.Release;
                }
                transferVerifiedClient = true;
                outcomeState = ReleaseVerificationState.Verified;
                outcomeError = ReleaseVerificationError.None;
            }
            else if (result is InstallerPackageOpenRejection rejection)
            {
                outcomeState = ReleaseVerificationState.Failed;
                outcomeError = ReleaseVerificationError.PackageRejected;
                lock (this.Sync)
                {
                    attempt.Operation.Cancellation.Token.ThrowIfCancellationRequested();
                    if (!this.IsCurrent(attempt.Operation))
                        throw new OperationCanceledException(attempt.Operation.Cancellation.Token);
                    this.RejectionCodeValue = rejection.ErrorCode;
                    this.RejectionNextActionValue = rejection.NextAction;
                    this.RejectionIsTerminalValue = rejection.IsTerminal;
                }
            }
            else
            {
                outcomeState = ReleaseVerificationState.Failed;
                outcomeError = ReleaseVerificationError.BackendUnavailable;
            }
        }
        catch (BackendSessionFaultException)
        {
            outcomeState = ReleaseVerificationState.Failed;
            outcomeError = ReleaseVerificationError.SessionFaulted;
        }
        catch (PreparedReleaseDisposalException)
        {
            outcomeState = ReleaseVerificationState.Failed;
            outcomeError = ReleaseVerificationError.CleanupFailed;
        }
        catch (OperationCanceledException) when (attempt.Operation.Cancellation.IsCancellationRequested)
        {
            outcomeState = ReleaseVerificationState.Cancelled;
            outcomeError = ReleaseVerificationError.None;
        }
        catch
        {
            outcomeState = ReleaseVerificationState.Failed;
            outcomeError = attempt.Stage == ReleaseVerificationState.Preparing
                ? ReleaseVerificationError.PreparationFailed
                : ReleaseVerificationError.BackendUnavailable;
        }
        finally
        {
            lock (this.Sync)
            {
                if (this.IsCurrent(attempt.Operation) && !transferVerifiedClient)
                {
                    this.StateValue = ReleaseVerificationState.CleaningUp;
                    this.ProgressValue = null;
                }
            }
            this.PublishChanged();

            if (prepared is not null && !preparedDisposalAttempted)
            {
                try
                {
                    await prepared.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    outcomeState = ReleaseVerificationState.Failed;
                    outcomeError = ReleaseVerificationError.PreparationFailed;
                    lock (this.Sync)
                        this.ClearResultAuthority();
                    transferVerifiedClient = false;
                }
            }
            bool watchVerifiedFault = false;
            lock (this.Sync)
            {
                if (this.IsCurrent(attempt.Operation))
                {
                    if (
                        transferVerifiedClient
                        && !this.DisposeStarted
                        && attempt.Client?.SessionFaulted.IsCompleted == false
                    )
                    {
                        this.ActiveOperation = null;
                        this.ProgressValue = null;
                        this.VerifiedAttempt = attempt;
                        this.StateValue = ReleaseVerificationState.Verified;
                        this.ErrorValue = ReleaseVerificationError.None;
                        watchVerifiedFault = true;
                    }
                    else
                    {
                        if (attempt.Client?.SessionFaulted.IsCompleted == true)
                        {
                            outcomeState = ReleaseVerificationState.Failed;
                            outcomeError = ReleaseVerificationError.SessionFaulted;
                        }
                        transferVerifiedClient = false;
                        this.VerifiedReleaseValue = null;
                        this.StateValue = ReleaseVerificationState.CleaningUp;
                    }
                }
            }
            if (!transferVerifiedClient)
            {
                try
                {
                    await this.DisposeClientOnceAsync(attempt).ConfigureAwait(false);
                }
                catch
                {
                    outcomeState = ReleaseVerificationState.Failed;
                    outcomeError = ReleaseVerificationError.CleanupFailed;
                }
                lock (this.Sync)
                {
                    if (this.IsCurrent(attempt.Operation))
                    {
                        this.ActiveOperation = null;
                        this.ProgressValue = null;
                        this.StateValue = outcomeState;
                        this.ErrorValue = outcomeError;
                    }
                }
            }
            attempt.Operation.Cancellation.Dispose();
            attempt.Operation.Completion.TrySetResult();
            this.PublishChanged();
            if (watchVerifiedFault)
                _ = this.WatchVerifiedSessionAsync(attempt);
        }
    }

    private async Task WatchVerifiedSessionAsync(AttemptContext attempt)
    {
        Task completed = await Task.WhenAny(attempt.Client!.SessionFaulted, attempt.StopWatching.Task)
            .ConfigureAwait(false);
        if (ReferenceEquals(completed, attempt.StopWatching.Task))
            return;
        try
        {
            _ = await attempt.Client.SessionFaulted.ConfigureAwait(false);
        }
        catch
        {
            // The client contract normally returns a sanitized exception value. Treat a broken implementation the same.
        }

        lock (this.Sync)
        {
            if (!ReferenceEquals(this.VerifiedAttempt, attempt))
                return;
            this.StateValue = ReleaseVerificationState.CleaningUp;
            this.ErrorValue = ReleaseVerificationError.SessionFaulted;
            this.VerifiedReleaseValue = null;
        }
        this.PublishChanged();
        bool cleanupFailed = false;
        try
        {
            await this.DisposeClientOnceAsync(attempt).ConfigureAwait(false);
        }
        catch
        {
            cleanupFailed = true;
        }
        lock (this.Sync)
        {
            if (ReferenceEquals(this.VerifiedAttempt, attempt))
                this.VerifiedAttempt = null;
            if (!this.DisposeStarted && this.GenerationValue == attempt.Operation.Generation)
            {
                this.StateValue = ReleaseVerificationState.Failed;
                this.ErrorValue = cleanupFailed
                    ? ReleaseVerificationError.CleanupFailed
                    : ReleaseVerificationError.SessionFaulted;
            }
        }
        this.PublishChanged();
    }

    private async Task AwaitWithSessionFaultAsync(Task operation, AttemptContext attempt)
    {
        Task completed = await Task.WhenAny(operation, attempt.Client!.SessionFaulted).ConfigureAwait(false);
        if (ReferenceEquals(completed, attempt.Client.SessionFaulted))
        {
            attempt.Operation.Cancellation.Cancel();
            await ObserveAsync(operation).ConfigureAwait(false);
            throw new BackendSessionFaultException();
        }
        await operation.ConfigureAwait(false);
        if (attempt.Client.SessionFaulted.IsCompleted)
            throw new BackendSessionFaultException();
    }

    private async Task<T> AwaitWithSessionFaultAsync<T>(Task<T> operation, AttemptContext attempt)
    {
        Task completed = await Task.WhenAny(operation, attempt.Client!.SessionFaulted).ConfigureAwait(false);
        if (ReferenceEquals(completed, attempt.Client.SessionFaulted))
        {
            attempt.Operation.Cancellation.Cancel();
            await ObserveAsync(operation).ConfigureAwait(false);
            throw new BackendSessionFaultException();
        }
        T result = await operation.ConfigureAwait(false);
        if (attempt.Client.SessionFaulted.IsCompleted)
            throw new BackendSessionFaultException();
        return result;
    }

    private static async Task ObserveAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch
        {
            // The authoritative session-fault outcome wins; the abandoned operation is still observed and settled.
        }
    }

    private Task DisposeClientOnceAsync(AttemptContext attempt)
    {
        lock (attempt)
        {
            if (attempt.Client is null)
                return Task.CompletedTask;
            return attempt.ClientDisposal ??= attempt.Client.DisposeAsync().AsTask();
        }
    }

    private void ReportProgress(AttemptContext attempt, ReviewedReleasePreparationProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        lock (this.Sync)
        {
            if (!this.IsCurrent(attempt.Operation) || this.StateValue != ReleaseVerificationState.Preparing)
                return;
            this.ProgressValue = progress;
        }
        this.PublishChanged();
    }

    private void SetAttemptStage(AttemptContext attempt, ReleaseVerificationState state)
    {
        lock (this.Sync)
        {
            if (!this.IsCurrent(attempt.Operation))
                throw new OperationCanceledException(attempt.Operation.Cancellation.Token);
            attempt.Operation.Cancellation.Token.ThrowIfCancellationRequested();
            this.StateValue = state;
            attempt.Stage = state;
            this.ProgressValue = null;
        }
        this.PublishChanged();
    }

    private void SetOperationOutcome(
        ControllerOperation operation,
        ReleaseVerificationState state,
        ReleaseVerificationError error
    )
    {
        lock (this.Sync)
        {
            if (!this.IsCurrent(operation))
                return;
            this.StateValue = state;
            this.ErrorValue = error;
            this.ProgressValue = null;
        }
        this.PublishChanged();
    }

    private void CompleteOperation(ControllerOperation operation)
    {
        lock (this.Sync)
        {
            if (this.IsCurrent(operation))
                this.ActiveOperation = null;
        }
        operation.Cancellation.Dispose();
        operation.Completion.TrySetResult();
        this.PublishChanged();
    }

    private bool IsCurrent(ControllerOperation operation)
    {
        return ReferenceEquals(this.ActiveOperation, operation) && operation.Generation == this.GenerationValue;
    }

    private static ReviewedReleaseCandidate[] ValidateCatalog(IReadOnlyList<ReviewedReleaseCandidate> loaded)
    {
        ReviewedReleaseCandidate[] releases = loaded.ToArray();
        if (
            releases.Any(value => value is null)
            || releases.Distinct(ReferenceEqualityComparer.Instance).Count() != releases.Length
            || releases.Select(value => value.Identity.Tag).Distinct(StringComparer.Ordinal).Count() != releases.Length
        )
        {
            throw new InvalidOperationException("The reviewed release service returned an invalid catalog snapshot.");
        }
        return releases;
    }

    private ReleaseVerificationSnapshot CreateSnapshot()
    {
        bool idleAuthority = this.ActiveOperation is null && this.VerifiedAttempt is null && !this.DisposeStarted;
        bool retryableOutcome = this.StateValue == ReleaseVerificationState.Cancelled
            || (
                this.StateValue == ReleaseVerificationState.Failed
                && (
                    this.ErrorValue == ReleaseVerificationError.PreparationFailed
                    || (this.ErrorValue == ReleaseVerificationError.PackageRejected && !this.RejectionIsTerminalValue)
                )
            );
        bool canRetry = idleAuthority
            && retryableOutcome
            && this.SelectedReleaseValue is not null
            && this.ReleasesValue.Any(value => ReferenceEquals(value, this.SelectedReleaseValue))
            && this.AttemptNumberValue is > 0 and < MaximumAttempts;
        return new ReleaseVerificationSnapshot(
            this.GenerationValue,
            this.StateValue,
            this.ErrorValue,
            Array.AsReadOnly(this.ReleasesValue.ToArray()),
            this.SelectedReleaseValue,
            this.ProgressValue,
            this.AttemptNumberValue,
            MaximumAttempts,
            idleAuthority && this.StateValue == ReleaseVerificationState.Ready && this.AttemptNumberValue == 0,
            canRetry,
            this.ActiveOperation is not null && this.StateValue != ReleaseVerificationState.CleaningUp,
            this.VerifiedReleaseValue,
            this.RejectionCodeValue,
            this.RejectionNextActionValue,
            this.RejectionIsTerminalValue
        );
    }

    private void AssertNotDisposed()
    {
        ObjectDisposedException.ThrowIf(this.DisposeStarted, this);
    }

    private void PublishChanged()
    {
        Delegate[] handlers = this.Changed?.GetInvocationList() ?? [];
        foreach (EventHandler handler in handlers.Cast<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch
            {
                // Presentation observers cannot break authority sequencing, cleanup, or backend reap.
            }
        }
    }

    private void ClearResultAuthority()
    {
        this.VerifiedReleaseValue = null;
        this.RejectionCodeValue = null;
        this.RejectionNextActionValue = null;
        this.RejectionIsTerminalValue = false;
    }

    private sealed class ControllerOperation(
        long generation,
        CancellationTokenSource cancellation
    )
    {
        public long Generation { get; } = generation;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class AttemptContext(
        ControllerOperation operation,
        ReviewedReleaseCandidate candidate,
        int attemptNumber
    )
    {
        public ControllerOperation Operation { get; } = operation;
        public ReviewedReleaseCandidate Candidate { get; } = candidate;
        public int AttemptNumber { get; } = attemptNumber;
        public IInstallerProtocolClient? Client { get; set; }
        public Task? ClientDisposal { get; set; }
        public ReleaseVerificationState Stage { get; set; } = ReleaseVerificationState.Handshaking;
        public TaskCompletionSource StopWatching { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PreparationProgressTracker : IProgress<ReviewedReleasePreparationProgress>
    {
        private readonly ReleaseVerificationController Owner;
        private readonly AttemptContext Attempt;
        private ReviewedReleasePreparationStage? LastStage;
        private int LastCompletedAssets;
        private long LastTransferredBytes;
        private bool SawObserving;
        private bool SawCompleteDownload;
        private bool SawRefreshing;
        private bool Sealed;
        private int CurrentAssetIndex = -1;
        private bool CurrentAssetCompleted;
        private long? DownloadTotalBytes;

        public PreparationProgressTracker(ReleaseVerificationController owner, AttemptContext attempt)
        {
            this.Owner = owner;
            this.Attempt = attempt;
        }

        public void Report(ReviewedReleasePreparationProgress value)
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (this)
            {
                if (this.Sealed)
                    return;
                int stage = (int)value.Stage;
                if (!Enum.IsDefined(value.Stage) || (this.LastStage.HasValue && stage < (int)this.LastStage.Value))
                    throw new InvalidOperationException("Release preparation reported an invalid stage transition.");

                if (value.Stage == ReviewedReleasePreparationStage.Downloading)
                {
                    int assetIndex = value.AssetKind.HasValue ? (int)value.AssetKind.Value : -1;
                    int completed = value.CompletedAssets;
                    if (
                        !this.SawObserving
                        || value.AssetKind is null
                        || !Enum.IsDefined(value.AssetKind.Value)
                        || value.TotalAssets != Enum.GetValues<ReviewedReleaseAssetKind>().Length
                        || value.CompletedAssets < this.LastCompletedAssets
                        || value.CompletedAssets < 0
                        || value.CompletedAssets > value.TotalAssets
                        || value.TotalBytes <= 0
                        || value.TransferredBytes < this.LastTransferredBytes
                        || value.TransferredBytes < 0
                        || value.TransferredBytes > value.TotalBytes
                        || (this.DownloadTotalBytes.HasValue && value.TotalBytes != this.DownloadTotalBytes.Value)
                        || assetIndex < 0
                        || assetIndex >= value.TotalAssets
                        || (completed != assetIndex && completed != assetIndex + 1)
                        || assetIndex < this.CurrentAssetIndex
                        || assetIndex > this.CurrentAssetIndex + 1
                        || (assetIndex == this.CurrentAssetIndex && this.CurrentAssetCompleted)
                        || (assetIndex == this.CurrentAssetIndex + 1 && this.CurrentAssetIndex >= 0 && !this.CurrentAssetCompleted)
                    )
                    {
                        throw new InvalidOperationException("Release preparation reported invalid bounded download progress.");
                    }
                    if (assetIndex != this.CurrentAssetIndex)
                    {
                        this.CurrentAssetIndex = assetIndex;
                        this.CurrentAssetCompleted = false;
                    }
                    this.CurrentAssetCompleted = completed == assetIndex + 1;
                    this.DownloadTotalBytes ??= value.TotalBytes;
                    this.LastCompletedAssets = value.CompletedAssets;
                    this.LastTransferredBytes = value.TransferredBytes;
                    this.SawCompleteDownload = assetIndex == value.TotalAssets - 1
                        && value.CompletedAssets == value.TotalAssets
                        && value.TransferredBytes == value.TotalBytes;
                }
                else
                {
                    if (
                        value.AssetKind is not null
                        || value.CompletedAssets != 0
                        || value.TotalAssets != 0
                        || value.TransferredBytes != 0
                        || value.TotalBytes != 0
                    )
                    {
                        throw new InvalidOperationException("A non-download release-preparation stage reported download fields.");
                    }
                    if (value.Stage == ReviewedReleasePreparationStage.ObservingTag)
                    {
                        if (this.LastStage is not null)
                            throw new InvalidOperationException("Release preparation restarted its initial tag observation.");
                        this.SawObserving = true;
                    }
                    else if (value.Stage == ReviewedReleasePreparationStage.RefreshingTag)
                    {
                        if (!this.SawCompleteDownload || this.SawRefreshing)
                            throw new InvalidOperationException("The release tag was refreshed before all six downloads completed.");
                        this.SawRefreshing = true;
                    }
                }
                this.LastStage = value.Stage;
            }
            this.Owner.ReportProgress(this.Attempt, value);
        }

        public void SealAndAssertComplete()
        {
            lock (this)
            {
                this.Sealed = true;
                if (!this.SawObserving || !this.SawCompleteDownload || !this.SawRefreshing)
                    throw new InvalidOperationException("Release preparation completed without its exact observed-download-refresh sequence.");
            }
        }
    }

    private sealed class BackendSessionFaultException : Exception;

    private sealed class PreparedReleaseDisposalException : Exception;
}
