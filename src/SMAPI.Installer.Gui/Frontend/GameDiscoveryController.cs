using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Gui.Backend;

namespace StardewModdingAPI.Installer.Gui.Frontend;

internal enum GameDiscoveryState
{
    Idle,
    Discovering,
    Ready,
    NoCandidates,
    ValidatingManual,
    ManualInvalid,
    ManualValid,
    Cancelling,
    Cancelled,
    Failed,
    SessionFaulted,
    Disposed
}

internal sealed record GameDiscoverySnapshot(
    long Generation,
    long Revision,
    GameDiscoveryState State,
    IReadOnlyList<ProtocolGameCandidate> Candidates,
    ProtocolGameCandidate? SelectedCandidate,
    bool CanRetry,
    bool CanBrowse,
    bool CanCancel,
    bool CanContinue
);

/// <summary>Serializes read-only game discovery and validation through one live verified installer session.</summary>
internal sealed class GameDiscoveryController : IAsyncDisposable
{
    private readonly object Sync = new();
    private readonly IVerifiedInstallerSession Session;
    private readonly TaskCompletionSource StopWatching = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task SessionWatcher;
    private ProtocolGameCandidate[] DiscoveredCandidatesValue = [];
    private ProtocolGameCandidate[] CandidatesValue = [];
    private ProtocolGameCandidate? SelectedCandidateValue;
    private GameDiscoveryState StateValue = GameDiscoveryState.Idle;
    private ActiveOperation? Operation;
    private Task? DisposalTask;
    private long GenerationValue;
    private long RevisionValue;
    private bool SessionHasFaulted;
    private bool DisposeStarted;
    internal Action? BeforeDiscoveryCommitForTesting { get; set; }
    internal Action? BeforeManualValidationCommitForTesting { get; set; }
    internal Action? BeforeOutcomeCommitForTesting { get; set; }
    internal Action? BeforeOperationCompletionForTesting { get; set; }

    public GameDiscoveryController(IVerifiedInstallerSession session)
    {
        this.Session = session ?? throw new ArgumentNullException(nameof(session));
        this.SessionWatcher = this.WatchSessionAsync();
    }

    public event EventHandler? Changed;

    public ProtocolReleaseIdentity Release => this.Session.Release;

    public GameDiscoverySnapshot Snapshot
    {
        get
        {
            lock (this.Sync)
                return this.CreateSnapshot();
        }
    }

    public Task DiscoverAsync(CancellationToken cancellationToken = default)
    {
        ActiveOperation operation;
        lock (this.Sync)
        {
            this.AssertCanStart();
            operation = this.BeginOperation(cancellationToken);
            this.DiscoveredCandidatesValue = [];
            this.CandidatesValue = [];
            this.SelectedCandidateValue = null;
            this.StateValue = GameDiscoveryState.Discovering;
        }
        this.PublishChanged();
        _ = this.RunDiscoveryAsync(operation);
        return operation.Completion.Task;
    }

    public Task ValidateManualAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ActiveOperation operation;
        lock (this.Sync)
        {
            this.AssertCanStart();
            operation = this.BeginOperation(cancellationToken);
            this.SelectedCandidateValue = null;
            this.StateValue = GameDiscoveryState.ValidatingManual;
        }
        this.PublishChanged();
        _ = this.RunManualValidationAsync(operation, path);
        return operation.Completion.Task;
    }

    public void SelectCandidate(ProtocolGameCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (this.Sync)
        {
            this.AssertNotDisposed();
            if (this.SessionHasFaulted)
                throw new InvalidOperationException("The verified installer session is no longer available.");
            if (this.Operation is not null)
                throw new InvalidOperationException("A game-folder operation is still active.");
            ProtocolGameCandidate selected = this.CandidatesValue.SingleOrDefault(value => ReferenceEquals(value, candidate))
                ?? throw new ArgumentException("The candidate must be the exact current discovery result.", nameof(candidate));
            this.SelectedCandidateValue = selected;
            this.StateValue = GameDiscoveryState.Ready;
        }
        this.PublishChanged();
    }

    public async Task CancelAsync()
    {
        ActiveOperation? operation;
        lock (this.Sync)
        {
            this.AssertNotDisposed();
            operation = this.Operation;
            if (operation is null)
                return;
            operation.UserCancellation = true;
            this.StateValue = GameDiscoveryState.Cancelling;
        }
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
            this.StateValue = GameDiscoveryState.Cancelling;
            this.SelectedCandidateValue = null;
            return new ValueTask(this.DisposalTask = this.DisposeCoreAsync(this.Operation));
        }
    }

    private async Task RunDiscoveryAsync(ActiveOperation operation)
    {
        await Task.Yield();
        try
        {
            IReadOnlyList<ProtocolGameCandidate> discovered = await this.AwaitWithSessionFaultAsync(
                this.Session.DiscoverGamesAsync(operation.Cancellation.Token),
                operation
            ).ConfigureAwait(false);
            operation.Cancellation.Token.ThrowIfCancellationRequested();
            ProtocolGameCandidate[] candidates = ValidateCandidates(discovered);
            this.BeforeDiscoveryCommitForTesting?.Invoke();
            lock (this.Sync)
            {
                if (!this.IsCurrent(operation))
                    return;
                if (this.DisposeStarted)
                {
                    this.SelectedCandidateValue = null;
                    this.StateValue = GameDiscoveryState.Cancelling;
                    return;
                }
                if (this.SessionHasFaulted)
                {
                    this.SelectedCandidateValue = null;
                    this.StateValue = GameDiscoveryState.SessionFaulted;
                    return;
                }
                if (operation.UserCancellation)
                {
                    this.SelectedCandidateValue = null;
                    this.StateValue = GameDiscoveryState.Cancelled;
                    return;
                }
                this.DiscoveredCandidatesValue = candidates;
                this.CandidatesValue = candidates;
                this.SelectedCandidateValue = candidates is [{ State: LinuxGameFolderStatus.Valid }]
                    ? candidates[0]
                    : null;
                this.StateValue = candidates.Length == 0
                    ? GameDiscoveryState.NoCandidates
                    : GameDiscoveryState.Ready;
            }
        }
        catch (SessionFaultException)
        {
            this.SetOutcome(operation, GameDiscoveryState.SessionFaulted);
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested)
        {
            this.SetOutcome(operation, this.SessionHasFaulted
                ? GameDiscoveryState.SessionFaulted
                : GameDiscoveryState.Cancelled);
        }
        catch
        {
            this.SetOutcome(operation, GameDiscoveryState.Failed);
        }
        finally
        {
            this.CompleteOperation(operation);
        }
    }

    private async Task RunManualValidationAsync(ActiveOperation operation, string path)
    {
        await Task.Yield();
        try
        {
            ProtocolGameCandidate candidate = await this.AwaitWithSessionFaultAsync(
                this.Session.ValidateGameAsync(path, operation.Cancellation.Token),
                operation
            ).ConfigureAwait(false);
            operation.Cancellation.Token.ThrowIfCancellationRequested();
            ValidateCandidate(candidate);
            this.BeforeManualValidationCommitForTesting?.Invoke();
            lock (this.Sync)
            {
                if (!this.IsCurrent(operation))
                    return;
                if (this.DisposeStarted)
                {
                    this.SelectedCandidateValue = null;
                    this.StateValue = GameDiscoveryState.Cancelling;
                    return;
                }
                if (this.SessionHasFaulted)
                {
                    this.SelectedCandidateValue = null;
                    this.StateValue = GameDiscoveryState.SessionFaulted;
                    return;
                }
                if (operation.UserCancellation)
                {
                    this.SelectedCandidateValue = null;
                    this.StateValue = GameDiscoveryState.Cancelled;
                    return;
                }
                this.CandidatesValue = ReplaceManualCandidate(this.DiscoveredCandidatesValue, candidate);
                this.SelectedCandidateValue = candidate;
                this.StateValue = candidate.State == LinuxGameFolderStatus.Valid
                    ? GameDiscoveryState.ManualValid
                    : GameDiscoveryState.ManualInvalid;
            }
        }
        catch (SessionFaultException)
        {
            this.SetOutcome(operation, GameDiscoveryState.SessionFaulted);
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested)
        {
            this.SetOutcome(operation, this.SessionHasFaulted
                ? GameDiscoveryState.SessionFaulted
                : GameDiscoveryState.Cancelled);
        }
        catch
        {
            this.SetOutcome(operation, GameDiscoveryState.Failed);
        }
        finally
        {
            this.CompleteOperation(operation);
        }
    }

    private async Task<T> AwaitWithSessionFaultAsync<T>(Task<T> work, ActiveOperation operation)
    {
        Task completed = await Task.WhenAny(work, this.Session.SessionFaulted).ConfigureAwait(false);
        if (ReferenceEquals(completed, this.Session.SessionFaulted))
        {
            await CancelSafelyAsync(operation.Cancellation).ConfigureAwait(false);
            await ObserveAsync(work).ConfigureAwait(false);
            throw new SessionFaultException();
        }
        T result = await work.ConfigureAwait(false);
        if (this.Session.SessionFaulted.IsCompleted)
            throw new SessionFaultException();
        return result;
    }

    private async Task WatchSessionAsync()
    {
        Task completed = await Task.WhenAny(this.Session.SessionFaulted, this.StopWatching.Task).ConfigureAwait(false);
        if (ReferenceEquals(completed, this.StopWatching.Task))
            return;
        try
        {
            _ = await this.Session.SessionFaulted.ConfigureAwait(false);
        }
        catch
        {
            // A broken implementation is still a terminal session fault; raw details are never exposed.
        }

        ActiveOperation? operation;
        lock (this.Sync)
        {
            if (this.DisposeStarted)
                return;
            this.SessionHasFaulted = true;
            operation = this.Operation;
            this.SelectedCandidateValue = null;
            this.StateValue = GameDiscoveryState.SessionFaulted;
        }
        this.PublishChanged();
        if (operation is not null)
            await CancelSafelyAsync(operation.Cancellation).ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync(ActiveOperation? operation)
    {
        await Task.Yield();
        this.PublishChanged();
        if (operation is not null)
        {
            await CancelSafelyAsync(operation.Cancellation).ConfigureAwait(false);
            await operation.Completion.Task.ConfigureAwait(false);
        }
        this.StopWatching.TrySetResult();
        await this.SessionWatcher.ConfigureAwait(false);
        try
        {
            await this.Session.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // The terminal disposed state is sanitized; callers cannot safely retry a failed session cleanup.
        }
        lock (this.Sync)
        {
            this.DiscoveredCandidatesValue = [];
            this.CandidatesValue = [];
            this.SelectedCandidateValue = null;
            this.StateValue = GameDiscoveryState.Disposed;
        }
        this.PublishChanged();
    }

    private ActiveOperation BeginOperation(CancellationToken callerCancellation)
    {
        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation);
        ActiveOperation operation = new(++this.GenerationValue, cancellation);
        this.Operation = operation;
        return operation;
    }

    private void CompleteOperation(ActiveOperation operation)
    {
        this.BeforeOperationCompletionForTesting?.Invoke();
        lock (this.Sync)
        {
            if (this.IsCurrent(operation))
            {
                if (this.DisposeStarted)
                {
                    this.SelectedCandidateValue = null;
                    this.StateValue = GameDiscoveryState.Cancelling;
                }
                else if (this.SessionHasFaulted)
                {
                    this.SelectedCandidateValue = null;
                    this.StateValue = GameDiscoveryState.SessionFaulted;
                }
                else if (operation.UserCancellation)
                {
                    this.SelectedCandidateValue = null;
                    this.StateValue = GameDiscoveryState.Cancelled;
                }
                this.Operation = null;
            }
        }
        operation.Cancellation.Dispose();
        operation.Completion.TrySetResult();
        this.PublishChanged();
    }

    private void SetOutcome(ActiveOperation operation, GameDiscoveryState state)
    {
        this.BeforeOutcomeCommitForTesting?.Invoke();
        lock (this.Sync)
        {
            if (!this.IsCurrent(operation))
                return;
            this.SelectedCandidateValue = null;
            this.StateValue = this.DisposeStarted
                ? GameDiscoveryState.Cancelling
                : this.SessionHasFaulted
                    ? GameDiscoveryState.SessionFaulted
                    : operation.UserCancellation
                        ? GameDiscoveryState.Cancelled
                        : state;
        }
        this.PublishChanged();
    }

    private GameDiscoverySnapshot CreateSnapshot()
    {
        bool idle = this.Operation is null && !this.DisposeStarted && !this.SessionHasFaulted;
        return new(
            this.GenerationValue,
            this.RevisionValue,
            this.StateValue,
            Array.AsReadOnly(this.CandidatesValue.ToArray()),
            this.SelectedCandidateValue,
            idle && this.StateValue is GameDiscoveryState.NoCandidates or GameDiscoveryState.Cancelled or GameDiscoveryState.Failed,
            idle,
            this.Operation is not null && this.StateValue != GameDiscoveryState.Cancelling,
            idle && this.SelectedCandidateValue?.State == LinuxGameFolderStatus.Valid
        );
    }

    private bool IsCurrent(ActiveOperation operation)
    {
        return ReferenceEquals(this.Operation, operation) && operation.Generation == this.GenerationValue;
    }

    private void AssertCanStart()
    {
        this.AssertNotDisposed();
        if (this.SessionHasFaulted)
            throw new InvalidOperationException("The verified installer session is no longer available.");
        if (this.Operation is not null)
            throw new InvalidOperationException("A game-folder operation is already active.");
    }

    private void AssertNotDisposed()
    {
        ObjectDisposedException.ThrowIf(this.DisposeStarted, this);
    }

    private void PublishChanged()
    {
        lock (this.Sync)
            this.RevisionValue++;
        Delegate[] handlers = this.Changed?.GetInvocationList() ?? [];
        foreach (EventHandler handler in handlers.Cast<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch
            {
                // Presentation observers cannot break session sequencing or cleanup.
            }
        }
    }

    private static ProtocolGameCandidate[] ValidateCandidates(IReadOnlyList<ProtocolGameCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ProtocolGameCandidate[] values = candidates.ToArray();
        if (values.Length > ProtocolJsonSerializer.MaxGameCandidates)
            throw new InvalidOperationException("The verified session returned too many game candidates.");
        foreach (ProtocolGameCandidate candidate in values)
            ValidateCandidate(candidate);
        if (values.Select(value => value.CanonicalPath).Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new InvalidOperationException("The verified session returned duplicate game candidates.");
        return values;
    }

    private static void ValidateCandidate(ProtocolGameCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (
            !Enum.IsDefined(candidate.State)
            || string.IsNullOrWhiteSpace(candidate.CanonicalPath)
            || !Path.IsPathFullyQualified(candidate.CanonicalPath)
            || string.IsNullOrWhiteSpace(candidate.DisplayName)
        )
            throw new InvalidOperationException("The verified session returned an invalid game candidate.");
    }

    private static ProtocolGameCandidate[] ReplaceManualCandidate(
        IReadOnlyList<ProtocolGameCandidate> discovered,
        ProtocolGameCandidate candidate
    )
    {
        return discovered
            .Where(value => !string.Equals(value.CanonicalPath, candidate.CanonicalPath, StringComparison.Ordinal))
            .Take(ProtocolJsonSerializer.MaxGameCandidates - 1)
            .Append(candidate)
            .ToArray();
    }

    private static async Task CancelSafelyAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
        catch
        {
            // The operation still settles and the UI remains fail-closed if a dependency's callback is broken.
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // The terminal session-fault state wins; the raced task is still observed.
        }
    }

    private sealed class ActiveOperation(long generation, CancellationTokenSource cancellation)
    {
        public long Generation { get; } = generation;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool UserCancellation { get; set; }
    }

    private sealed class SessionFaultException : Exception;
}
