using System.Runtime.ExceptionServices;
using StardewModdingAPI.Installer.Core.Protocol.V1;

namespace StardewModdingAPI.Installer.Gui.Backend;

/// <summary>Owns a verified backend client after its one-time handoff from release verification.</summary>
internal sealed class VerifiedInstallerSession : IVerifiedInstallerSession, IVerifiedInstallerSessionBinder
{
    private enum SessionStage
    {
        Discovery,
        Bound,
        Confirming,
        Confirmed,
        Executing,
        BoundTerminal,
        ConfirmedTerminal,
        Disposing,
        Disposed
    }

    private readonly IInstallerProtocolClient Client;
    private readonly SemaphoreSlim CommandGate = new(1, 1);
    private readonly CancellationTokenSource Lifetime = new();
    private readonly object DisposeLock = new();
    private readonly HashSet<ProtocolGameCandidate> DiscoveredCandidates = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<InstallerReadOnlyPlanCandidate> CurrentPlanCandidates = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<InstallerReadOnlyPlanCandidate> IssuedPlanCandidates = new(ReferenceEqualityComparer.Instance);
    private InstallerPlanConfirmation? CurrentPlanConfirmation;
    private InstallerPlanConfirmation? CurrentClientConfirmation;
    private InstallerConfirmedPlanAuthority? CurrentConfirmedAuthority;
    private ConfirmedInstallerSession? CurrentConfirmedOwner;
    private InstallerExecutionOperation? CurrentExecution;
    private CancellationTokenSource? CurrentExecutionLifetime;
    private TaskCompletionSource<InstallerExecutionOperation?>? CurrentExecutionPublication;
    private ProtocolGameCandidate? LatestManualCandidate;
    private Task? DisposalTask;
    private bool ConfirmedOwnershipTransferred;
    private SessionStage Stage = SessionStage.Discovery;
    private int AdmittedDiscoveryCommands;
    internal int IssuedPlanCandidateCapacityForTesting { get; set; } = InstallerCandidateSelection.MaximumIssuedCandidatesPerSession;

    public ProtocolReleaseIdentity Release { get; }
    public Task<InstallerProtocolClientException> SessionFaulted => this.Client.SessionFaulted;

    public VerifiedInstallerSession(ProtocolReleaseIdentity release, IInstallerProtocolClient client)
    {
        this.Release = release ?? throw new ArgumentNullException(nameof(release));
        this.Client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<IReadOnlyList<ProtocolGameCandidate>> DiscoverGamesAsync(CancellationToken cancellationToken = default)
    {
        return await this.ExecuteDiscoveryAsync(
            this.Client.DiscoverGamesAsync,
            candidates =>
            {
                ProtocolGameCandidate[] snapshot = candidates?.ToArray()
                    ?? throw new InstallerProtocolClientException("The installer backend returned an invalid game-folder result.");
                if (snapshot.Length > ProtocolJsonSerializer.MaxGameCandidates || snapshot.Any(candidate => candidate is null))
                    throw new InstallerProtocolClientException("The installer backend returned an invalid game-folder result.");
                this.DiscoveredCandidates.Clear();
                foreach (ProtocolGameCandidate candidate in snapshot)
                    this.DiscoveredCandidates.Add(candidate);
                return Array.AsReadOnly(snapshot);
            },
            cancellationToken
        ).ConfigureAwait(false);
    }

    public Task<ProtocolGameCandidate> ValidateGameAsync(string canonicalPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(canonicalPath);
        return this.ValidateAndRetainGameAsync(canonicalPath, cancellationToken);
    }

    public IPlanInspectionSession BindToGame(ProtocolGameCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (this.DisposeLock)
        {
            this.AssertDiscoveryStage();
            if (this.AdmittedDiscoveryCommands != 0)
                throw new InvalidOperationException("A game-folder operation is still active.");
            if (this.Client.SessionFaulted.IsCompleted)
                throw new InvalidOperationException("The verified installer session has already faulted.");
            if (
                candidate.State != Core.Engine.LinuxGameFolderStatus.Valid
                || !this.DiscoveredCandidates.Contains(candidate) && !ReferenceEquals(this.LatestManualCandidate, candidate)
            )
                throw new ArgumentException("The game must be the exact valid result issued by this verified session.", nameof(candidate));

            VerifiedGamePresentation game = new(candidate.CanonicalPath, candidate.DisplayName);
            BoundPlanInspectionSession result = new(this, this.Release, candidate.CanonicalPath, game);
            this.ClearIssuedCandidates();
            this.Stage = SessionStage.Bound;
            return result;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (this.DisposeLock)
        {
            if (this.ConfirmedOwnershipTransferred)
                return ValueTask.CompletedTask;
            if (this.DisposalTask is not null)
                return new ValueTask(this.DisposalTask);
            if (this.Stage is SessionStage.Bound or SessionStage.Confirming or SessionStage.Confirmed or SessionStage.Executing or SessionStage.BoundTerminal or SessionStage.ConfirmedTerminal)
                return ValueTask.CompletedTask;
            this.Stage = SessionStage.Disposing;
            return new ValueTask(this.DisposalTask = this.DisposeCoreAsync());
        }
    }

    private async Task<ProtocolGameCandidate> ValidateAndRetainGameAsync(string canonicalPath, CancellationToken cancellationToken)
    {
        return await this.ExecuteDiscoveryAsync(
            token => this.Client.ValidateGameAsync(canonicalPath, token),
            candidate =>
            {
                if (candidate is null)
                    throw new InstallerProtocolClientException("The installer backend returned an invalid game-folder result.");
                this.LatestManualCandidate = candidate;
                return candidate;
            },
            cancellationToken
        ).ConfigureAwait(false);
    }

    private async Task<TResult> ExecuteDiscoveryAsync<TResponse, TResult>(
        Func<CancellationToken, Task<TResponse>> command,
        Func<TResponse, TResult> commit,
        CancellationToken cancellationToken
    )
    {
        CancellationToken lifetime;
        lock (this.DisposeLock)
        {
            this.AssertDiscoveryStage();
            this.AdmittedDiscoveryCommands++;
            lifetime = this.Lifetime.Token;
        }
        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        bool gateEntered = false;
        try
        {
            await this.CommandGate.WaitAsync(operation.Token).ConfigureAwait(false);
            gateEntered = true;
            lock (this.DisposeLock)
                this.AssertDiscoveryStage();
            TResponse response = await command(operation.Token).ConfigureAwait(false);
            lock (this.DisposeLock)
            {
                this.AssertDiscoveryStage();
                if (this.Client.SessionFaulted.IsCompleted)
                    throw new InstallerProtocolClientException("The verified installer session faulted before the game-folder result could be accepted.");
                operation.Token.ThrowIfCancellationRequested();
                return commit(response);
            }
        }
        finally
        {
            if (gateEntered)
                this.CommandGate.Release();
            lock (this.DisposeLock)
                this.AdmittedDiscoveryCommands--;
        }
    }

    private ValueTask DisposeFromBoundSessionAsync()
    {
        lock (this.DisposeLock)
        {
            if (this.ConfirmedOwnershipTransferred)
                return ValueTask.CompletedTask;
            if (this.DisposalTask is not null)
                return new ValueTask(this.DisposalTask);
            if (this.Stage is SessionStage.Confirmed or SessionStage.Executing or SessionStage.ConfirmedTerminal)
                return ValueTask.CompletedTask;
            if (this.Stage is not (SessionStage.Bound or SessionStage.Confirming or SessionStage.BoundTerminal))
                return this.Stage == SessionStage.Disposed
                    ? ValueTask.CompletedTask
                    : throw new InvalidOperationException("The bound installer session no longer owns backend cleanup.");
            this.Stage = SessionStage.Disposing;
            return new ValueTask(this.DisposalTask = this.DisposeCoreAsync());
        }
    }

    private ValueTask DisposeFromConfirmedSessionAsync(ConfirmedInstallerSession owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (this.DisposeLock)
        {
            if (this.DisposalTask is not null)
                return new ValueTask(this.DisposalTask);
            if (!ReferenceEquals(this.CurrentConfirmedOwner, owner))
                return this.Stage == SessionStage.Disposed
                    ? ValueTask.CompletedTask
                    : throw new InvalidOperationException("The confirmed installer session no longer owns backend cleanup.");
            if (this.Stage == SessionStage.Executing && this.CurrentExecutionPublication is { } publication)
            {
                this.Stage = SessionStage.Disposing;
                return new ValueTask(this.DisposalTask = this.DisposeExecutingCoreAsync(
                    publication,
                    this.CurrentExecutionLifetime
                        ?? throw new InvalidOperationException("The admitted execution had no lifetime owner.")
                ));
            }
            if (this.Stage is not (SessionStage.Confirmed or SessionStage.ConfirmedTerminal))
                return this.Stage == SessionStage.Disposed
                    ? ValueTask.CompletedTask
                    : throw new InvalidOperationException("The confirmed installer session no longer owns backend cleanup.");
            this.Stage = SessionStage.Disposing;
            return new ValueTask(this.DisposalTask = this.DisposeCoreAsync());
        }
    }

    private async Task<InstallerReadOnlyPlanResult> InspectBoundPlanAsync(
        string exactCanonicalPath,
        InstallerOperation operation,
        CancellationToken cancellationToken
    )
    {
        AssertSupportedPlanOperation(operation);
        CancellationToken lifetime;
        lock (this.DisposeLock)
        {
            if (this.Stage != SessionStage.Bound)
                throw new ObjectDisposedException(nameof(IPlanInspectionSession));
            lifetime = this.Lifetime.Token;
        }
        using CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        InstallerReadOnlyPlanResult? result = null;
        InstallerReadOnlyPlanCandidate[]? stableResultCandidates = null;
        InstallerPlanConfirmation? clientConfirmation = null;
        InstallerPlanConfirmation? sessionConfirmation = null;
        Exception? failure = null;
        try
        {
            await this.CommandGate.WaitAsync(request.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = this.GetPlanFailure(exception);
        }
        if (failure is not null)
        {
            await this.DisposeFromBoundSessionAsync().ConfigureAwait(false);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        try
        {
            lock (this.DisposeLock)
            {
                if (this.Stage != SessionStage.Bound)
                    throw new ObjectDisposedException(nameof(IPlanInspectionSession));
                this.CurrentPlanCandidates.Clear();
                this.ClearCurrentPlanConfirmation();
            }
            result = await this.Client.InspectPlanAsync(
                exactCanonicalPath,
                operation,
                request.Token
            ).ConfigureAwait(false);
            if (result is InstallerReadOnlyPlanSuccess success)
            {
                stableResultCandidates = SnapshotBackendResultCandidates(success.Candidates);
                (clientConfirmation, sessionConfirmation) = RemintConfirmation(success);
                result = success with
                {
                    Candidates = Array.AsReadOnly(stableResultCandidates),
                    Confirmation = sessionConfirmation
                };
            }
            lock (this.DisposeLock)
            {
                if (this.Stage != SessionStage.Bound)
                    throw new ObjectDisposedException(nameof(IPlanInspectionSession));
                if (this.Client.SessionFaulted.IsCompleted)
                    throw new InstallerProtocolClientException("The verified installer session faulted before the plan result could be accepted.");
                request.Token.ThrowIfCancellationRequested();
                if (result is InstallerReadOnlyPlanRejection { IsTerminal: true })
                    this.Stage = SessionStage.BoundTerminal;
                if (result is InstallerReadOnlyPlanSuccess)
                {
                    if (!this.TryRetainIssuedPlanCandidates(stableResultCandidates!))
                        throw new InstallerProtocolClientException("The installer backend returned invalid candidate capabilities.");
                    this.CurrentClientConfirmation = clientConfirmation;
                    this.CurrentPlanConfirmation = sessionConfirmation;
                }
            }
        }
        catch (Exception exception)
        {
            failure = this.GetPlanFailure(exception);
        }
        finally
        {
            this.CommandGate.Release();
        }

        if (failure is not null)
        {
            await this.DisposeFromBoundSessionAsync().ConfigureAwait(false);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        if (result is InstallerReadOnlyPlanRejection { IsTerminal: true })
            await this.DisposeFromBoundSessionAsync().ConfigureAwait(false);
        return result!;
    }

    private async Task<InstallerReadOnlyPlanResult> ApproveBoundPlanCandidatesAsync(
        IReadOnlyList<InstallerReadOnlyPlanCandidate> candidates,
        CancellationToken cancellationToken
    )
    {
        InstallerReadOnlyPlanCandidate[] requested = InstallerCandidateSelection.Snapshot(candidates, nameof(candidates));

        CancellationToken lifetime;
        lock (this.DisposeLock)
        {
            if (this.Stage != SessionStage.Bound)
                throw new ObjectDisposedException(nameof(IPlanInspectionSession));
            lifetime = this.Lifetime.Token;
        }
        using CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        InstallerReadOnlyPlanResult? result = null;
        InstallerReadOnlyPlanCandidate[]? stableResultCandidates = null;
        InstallerPlanConfirmation? clientConfirmation = null;
        InstallerPlanConfirmation? sessionConfirmation = null;
        Exception? failure = null;
        try
        {
            await this.CommandGate.WaitAsync(request.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = this.GetPlanFailure(exception);
        }
        if (failure is not null)
        {
            await this.DisposeFromBoundSessionAsync().ConfigureAwait(false);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        try
        {
            lock (this.DisposeLock)
            {
                if (this.Stage != SessionStage.Bound)
                    throw new ObjectDisposedException(nameof(IPlanInspectionSession));
                HashSet<InstallerReadOnlyPlanCandidate> unique = new(ReferenceEqualityComparer.Instance);
                foreach (InstallerReadOnlyPlanCandidate candidate in requested)
                {
                    if (!unique.Add(candidate))
                        throw new ArgumentException("The candidate selection contains a duplicate capability.", nameof(candidates));
                    if (!this.CurrentPlanCandidates.Contains(candidate))
                        throw new ArgumentException("Every candidate must be an exact current capability issued by this bound session.", nameof(candidates));
                }
                this.CurrentPlanCandidates.Clear();
                this.ClearCurrentPlanConfirmation();
            }

            try
            {
                result = await this.Client.ApprovePlanCandidatesAsync(requested, request.Token).ConfigureAwait(false);
                if (result is InstallerReadOnlyPlanSuccess success)
                {
                    stableResultCandidates = SnapshotBackendResultCandidates(success.Candidates);
                    (clientConfirmation, sessionConfirmation) = RemintConfirmation(success);
                    result = success with
                    {
                        Candidates = Array.AsReadOnly(stableResultCandidates),
                        Confirmation = sessionConfirmation
                    };
                }
                lock (this.DisposeLock)
                {
                    if (this.Stage != SessionStage.Bound)
                        throw new ObjectDisposedException(nameof(IPlanInspectionSession));
                    if (this.Client.SessionFaulted.IsCompleted)
                        throw new InstallerProtocolClientException("The verified installer session faulted before the replacement plan could be accepted.");
                    request.Token.ThrowIfCancellationRequested();
                    if (result is InstallerReadOnlyPlanRejection { IsTerminal: true })
                        this.Stage = SessionStage.BoundTerminal;
                    if (result is InstallerReadOnlyPlanSuccess)
                    {
                        if (!this.TryRetainIssuedPlanCandidates(stableResultCandidates!))
                            throw new InstallerProtocolClientException("The installer backend returned invalid replacement candidate capabilities.");
                        this.CurrentClientConfirmation = clientConfirmation;
                        this.CurrentPlanConfirmation = sessionConfirmation;
                    }
                }
            }
            catch (Exception exception)
            {
                failure = this.GetPlanFailure(exception);
            }
        }
        finally
        {
            this.CommandGate.Release();
        }

        if (failure is not null)
        {
            await this.DisposeFromBoundSessionAsync().ConfigureAwait(false);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        if (result is InstallerReadOnlyPlanRejection { IsTerminal: true })
            await this.DisposeFromBoundSessionAsync().ConfigureAwait(false);
        return result!;
    }

    private async Task<IConfirmedInstallerSession> ConfirmBoundPlanAsync(
        VerifiedGamePresentation game,
        InstallerPlanConfirmation confirmation,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        CancellationToken lifetime;
        InstallerPlanConfirmation clientConfirmation;
        lock (this.DisposeLock)
        {
            if (this.Stage != SessionStage.Bound)
                throw new ObjectDisposedException(nameof(IPlanInspectionSession));
            if (!ReferenceEquals(this.CurrentPlanConfirmation, confirmation) || this.CurrentClientConfirmation is null)
                throw new ArgumentException("The confirmation must be the exact current capability issued by this bound session.", nameof(confirmation));
            clientConfirmation = this.CurrentClientConfirmation;

            // Admission is synchronous and exclusive. Cancellation, fault, or disposal after this point revokes the
            // plan and settles the backend session instead of restoring a consumed confirmation capability.
            this.ClearCurrentPlanConfirmation();
            this.CurrentPlanCandidates.Clear();
            this.Stage = SessionStage.Confirming;
            lifetime = this.Lifetime.Token;
        }
        using CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        bool gateEntered = false;
        InstallerConfirmedPlanAuthority? authority = null;
        Exception? failure = null;
        try
        {
            await this.CommandGate.WaitAsync(request.Token).ConfigureAwait(false);
            gateEntered = true;
            lock (this.DisposeLock)
            {
                if (this.Stage != SessionStage.Confirming)
                    throw new ObjectDisposedException(nameof(IPlanInspectionSession));
            }

            authority = await this.Client.ConfirmPlanAsync(clientConfirmation, request.Token).ConfigureAwait(false);
            if (authority is null)
                throw new InstallerProtocolClientException("The installer backend returned an invalid confirmed-plan authority.");
            lock (this.DisposeLock)
            {
                if (this.Stage != SessionStage.Confirming)
                    throw new ObjectDisposedException(nameof(IPlanInspectionSession));
                if (this.Client.SessionFaulted.IsCompleted)
                    throw new InstallerProtocolClientException("The verified installer session faulted before confirmation ownership could be accepted.");
                request.Token.ThrowIfCancellationRequested();
                ConfirmedInstallerSession confirmed = new(this, this.Release, game);
                this.CurrentConfirmedAuthority = authority;
                this.CurrentConfirmedOwner = confirmed;
                this.ConfirmedOwnershipTransferred = true;
                this.Stage = SessionStage.Confirmed;
                return confirmed;
            }
        }
        catch (Exception exception)
        {
            failure = this.GetPlanFailure(exception);
        }
        finally
        {
            if (gateEntered)
                this.CommandGate.Release();
        }

        await this.DisposeFromBoundSessionAsync().ConfigureAwait(false);
        ExceptionDispatchInfo.Capture(failure!).Throw();
        throw new InvalidOperationException("The confirmation failure did not propagate.");
    }

    private async Task<InstallerExecutionOperation> ExecuteConfirmedPlanAsync(
        ConfirmedInstallerSession owner,
        CancellationToken cancellationToken
    )
    {
        CancellationTokenSource request;
        TaskCompletionSource<InstallerExecutionOperation?> publication;
        InstallerConfirmedPlanAuthority authority;
        lock (this.DisposeLock)
        {
            if (
                this.Stage != SessionStage.Confirmed
                || !ReferenceEquals(this.CurrentConfirmedOwner, owner)
                || this.CurrentConfirmedAuthority is not { } current
            )
            {
                throw new ObjectDisposedException(nameof(IConfirmedInstallerSession));
            }
            cancellationToken.ThrowIfCancellationRequested();

            // Execution admission is synchronous: the exact authority disappears before this method first awaits.
            authority = current;
            this.CurrentConfirmedAuthority = null;
            this.Stage = SessionStage.Executing;
            request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.Lifetime.Token);
            this.CurrentExecutionLifetime = request;
            publication = new(TaskCreationOptions.RunContinuationsAsynchronously);
            this.CurrentExecutionPublication = publication;
        }

        InstallerExecutionOperation? execution = null;
        try
        {
            InstallerExecutionOperation? returned = await this.Client.ExecutePlanAsync(authority, request.Token).ConfigureAwait(false);
            if (returned is null || returned.Completion is null || returned.Progress is null)
                throw new InstallerProtocolClientException("The installer backend returned an invalid execution operation.");
            execution = returned;
            lock (this.DisposeLock)
            {
                if (!ReferenceEquals(this.CurrentConfirmedOwner, owner) || this.Stage is not (SessionStage.Executing or SessionStage.Disposing))
                    throw new ObjectDisposedException(nameof(IConfirmedInstallerSession));
                this.CurrentExecution = execution;
            }
            publication.TrySetResult(execution);
            _ = this.TrackExecutionAsync(owner, execution, request);
            return execution;
        }
        catch (Exception error)
        {
            bool cancellationRequested = request.IsCancellationRequested;
            publication.TrySetResult(execution);
            lock (this.DisposeLock)
            {
                if (ReferenceEquals(this.CurrentExecutionLifetime, request) && execution is null && this.DisposalTask is null)
                {
                    request.Dispose();
                    this.CurrentExecutionLifetime = null;
                }
                if (this.Stage == SessionStage.Executing)
                    this.Stage = SessionStage.ConfirmedTerminal;
            }
            await this.DisposeFromConfirmedSessionAsync(owner).ConfigureAwait(false);
            if (error is OperationCanceledException && cancellationRequested)
            {
                CancellationToken cancelled = cancellationToken.IsCancellationRequested
                    ? cancellationToken
                    : new CancellationToken(canceled: true);
                throw new OperationCanceledException("The verified installer execution was cancelled safely.", cancelled);
            }
            throw new InstallerProtocolClientException("The verified installer execution could not be started safely.");
        }
    }

    private async Task TrackExecutionAsync(
        ConfirmedInstallerSession owner,
        InstallerExecutionOperation execution,
        CancellationTokenSource request
    )
    {
        try { _ = await execution.Completion.ConfigureAwait(false); }
        catch { }
        finally
        {
            try
            {
                request.Dispose();
            }
            finally
            {
                lock (this.DisposeLock)
                {
                    if (ReferenceEquals(this.CurrentExecutionLifetime, request))
                        this.CurrentExecutionLifetime = null;
                    if (ReferenceEquals(this.CurrentExecution, execution))
                    {
                        this.CurrentExecution = null;
                        this.CurrentExecutionPublication = null;
                    }
                    if (ReferenceEquals(this.CurrentConfirmedOwner, owner) && this.Stage == SessionStage.Executing)
                        this.Stage = SessionStage.ConfirmedTerminal;
                }
            }
        }
    }

    private async Task DisposeExecutingCoreAsync(
        TaskCompletionSource<InstallerExecutionOperation?> publication,
        CancellationTokenSource request
    )
    {
        bool cleanupFailed = false;
        try { await request.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        catch { cleanupFailed = true; }
        InstallerExecutionOperation? execution = null;
        try { execution = await publication.Task.ConfigureAwait(false); }
        catch { cleanupFailed = true; }
        if (execution is not null)
        {
            try { await execution.RequestCancellationAsync().ConfigureAwait(false); }
            catch { cleanupFailed = true; }
            try { _ = await execution.Completion.ConfigureAwait(false); }
            catch { cleanupFailed = true; }
        }
        try { await this.Client.DisposeAsync().ConfigureAwait(false); }
        catch { cleanupFailed = true; }
        try { this.CurrentExecutionLifetime?.Dispose(); }
        catch { cleanupFailed = true; }
        try { this.Lifetime.Dispose(); }
        catch { cleanupFailed = true; }
        this.CommandGate.Dispose();
        lock (this.DisposeLock)
        {
            this.ClearIssuedCandidates();
            this.CurrentPlanCandidates.Clear();
            this.IssuedPlanCandidates.Clear();
            this.ClearCurrentPlanConfirmation();
            this.CurrentExecution = null;
            this.CurrentExecutionPublication = null;
            this.CurrentExecutionLifetime = null;
            this.CurrentConfirmedAuthority = null;
            this.CurrentConfirmedOwner = null;
            this.Stage = SessionStage.Disposed;
        }
        if (cleanupFailed)
            throw new InstallerProtocolClientException("The verified installer session could not be cleaned up safely.");
    }

    private static (InstallerPlanConfirmation? Client, InstallerPlanConfirmation? Session) RemintConfirmation(
        InstallerReadOnlyPlanSuccess success
    )
    {
        InstallerPlanConfirmation? client = success.Confirmation;
        if (success.HasBlockingConflicts)
        {
            if (client is not null)
                throw new InstallerProtocolClientException("The installer backend issued confirmation authority for a blocked plan.");
            return (null, null);
        }
        if (client is null)
            throw new InstallerProtocolClientException("The installer backend omitted confirmation authority for an executable plan.");
        return (client, new InstallerPlanConfirmation());
    }

    /// <remarks>The caller must hold <see cref="DisposeLock"/>.</remarks>
    private void ClearCurrentPlanConfirmation()
    {
        this.CurrentPlanConfirmation = null;
        this.CurrentClientConfirmation = null;
    }

    private static InstallerReadOnlyPlanCandidate[] SnapshotBackendResultCandidates(IReadOnlyList<InstallerReadOnlyPlanCandidate>? candidates)
    {
        if (candidates is null)
            throw new InstallerProtocolClientException("The installer backend returned an invalid candidate-capability collection.");
        int count;
        try { count = candidates.Count; }
        catch
        {
            throw new InstallerProtocolClientException("The installer backend returned an invalid candidate-capability collection.");
        }
        if (count is < 0 or > ProtocolJsonSerializer.MaxPlanCandidates)
            throw new InstallerProtocolClientException("The installer backend returned an invalid candidate-capability collection.");

        InstallerReadOnlyPlanCandidate[] result = new InstallerReadOnlyPlanCandidate[count];
        for (int index = 0; index < count; index++)
        {
            try { result[index] = candidates[index]; }
            catch
            {
                throw new InstallerProtocolClientException("The installer backend returned an invalid candidate-capability collection.");
            }
            if (result[index] is null)
                throw new InstallerProtocolClientException("The installer backend returned an invalid candidate-capability collection.");
        }
        return result;
    }

    /// <remarks>The caller must hold <see cref="DisposeLock"/> and pass only a stable local array.</remarks>
    private bool TryRetainIssuedPlanCandidates(InstallerReadOnlyPlanCandidate[] candidates)
    {
        int capacity = this.IssuedPlanCandidateCapacityForTesting;
        if (
            candidates.Length > ProtocolJsonSerializer.MaxPlanCandidates
            || capacity is < ProtocolJsonSerializer.MaxPlanCandidates or > InstallerCandidateSelection.MaximumIssuedCandidatesPerSession
            || this.IssuedPlanCandidates.Count > capacity - candidates.Length
        )
            return false;
        HashSet<InstallerReadOnlyPlanCandidate> current = new(ReferenceEqualityComparer.Instance);
        for (int index = 0; index < candidates.Length; index++)
        {
            InstallerReadOnlyPlanCandidate candidate = candidates[index];
            if (candidate is null || this.IssuedPlanCandidates.Contains(candidate) || !current.Add(candidate))
                return false;
        }
        foreach (InstallerReadOnlyPlanCandidate candidate in current)
        {
            this.IssuedPlanCandidates.Add(candidate);
            this.CurrentPlanCandidates.Add(candidate);
        }
        return true;
    }

    private Exception GetPlanFailure(Exception failure)
    {
        lock (this.DisposeLock)
        {
            if (this.Stage is SessionStage.Disposing or SessionStage.Disposed)
                return new ObjectDisposedException(nameof(IPlanInspectionSession));
            if (this.Client.SessionFaulted.IsCompleted)
                return new InstallerProtocolClientException("The verified installer session faulted before the plan result could be accepted.");
            if (this.Stage is SessionStage.Bound or SessionStage.Confirming)
                this.Stage = SessionStage.BoundTerminal;
            return failure;
        }
    }

    private static void AssertSupportedPlanOperation(InstallerOperation operation)
    {
        if (operation is not (InstallerOperation.Install
            or InstallerOperation.Update
            or InstallerOperation.Repair
            or InstallerOperation.Uninstall
            or InstallerOperation.Backup))
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Only non-rollback read-only plan inspection is available.");
        }
    }

    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        bool cleanupFailed = false;
        try
        {
            await this.Lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch
        {
            cleanupFailed = true;
        }
        await this.CommandGate.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                await this.Client.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                cleanupFailed = true;
            }
        }
        finally
        {
            try
            {
                this.Lifetime.Dispose();
            }
            catch
            {
                cleanupFailed = true;
            }
            this.CommandGate.Release();
            this.CommandGate.Dispose();
            lock (this.DisposeLock)
            {
                this.ClearIssuedCandidates();
                this.CurrentPlanCandidates.Clear();
                this.IssuedPlanCandidates.Clear();
                this.ClearCurrentPlanConfirmation();
                this.CurrentConfirmedAuthority = null;
                this.CurrentConfirmedOwner = null;
                this.CurrentExecution = null;
                this.CurrentExecutionPublication = null;
                this.CurrentExecutionLifetime = null;
                this.Stage = SessionStage.Disposed;
            }
        }
        if (cleanupFailed)
            throw new InstallerProtocolClientException("The verified installer session could not be cleaned up safely.");
    }

    private void AssertDiscoveryStage()
    {
        if (this.Stage == SessionStage.Discovery)
            return;
        if (this.Stage == SessionStage.Bound)
            throw new InvalidOperationException("The verified installer session is already bound to a selected game.");
        throw new ObjectDisposedException(nameof(VerifiedInstallerSession));
    }

    private void ClearIssuedCandidates()
    {
        this.DiscoveredCandidates.Clear();
        this.LatestManualCandidate = null;
    }

    private sealed class BoundPlanInspectionSession : IPlanInspectionSession
    {
        private readonly VerifiedInstallerSession Owner;
        private readonly string ExactCanonicalPath;

        public ProtocolReleaseIdentity Release { get; }
        public VerifiedGamePresentation Game { get; }
        public Task<InstallerProtocolClientException> SessionFaulted => this.Owner.SessionFaulted;

        public BoundPlanInspectionSession(VerifiedInstallerSession owner, ProtocolReleaseIdentity release, string exactCanonicalPath, VerifiedGamePresentation game)
        {
            this.Owner = owner;
            this.ExactCanonicalPath = exactCanonicalPath;
            this.Release = release;
            this.Game = game;
        }

        public ValueTask DisposeAsync() => this.Owner.DisposeFromBoundSessionAsync();

        public Task<InstallerReadOnlyPlanResult> InspectPlanAsync(InstallerOperation operation, CancellationToken cancellationToken = default)
            => this.Owner.InspectBoundPlanAsync(this.ExactCanonicalPath, operation, cancellationToken);

        public Task<InstallerReadOnlyPlanResult> ApprovePlanCandidatesAsync(IReadOnlyList<InstallerReadOnlyPlanCandidate> candidates, CancellationToken cancellationToken = default)
            => this.Owner.ApproveBoundPlanCandidatesAsync(candidates, cancellationToken);

        public Task<IConfirmedInstallerSession> ConfirmPlanAsync(InstallerPlanConfirmation confirmation, CancellationToken cancellationToken = default)
            => this.Owner.ConfirmBoundPlanAsync(this.Game, confirmation, cancellationToken);
    }

    private sealed class ConfirmedInstallerSession : IConfirmedInstallerSession
    {
        private readonly VerifiedInstallerSession Owner;

        public ProtocolReleaseIdentity Release { get; }
        public VerifiedGamePresentation Game { get; }
        public Task<InstallerProtocolClientException> SessionFaulted => this.Owner.SessionFaulted;

        public ConfirmedInstallerSession(
            VerifiedInstallerSession owner,
            ProtocolReleaseIdentity release,
            VerifiedGamePresentation game
        )
        {
            this.Owner = owner;
            this.Release = release;
            this.Game = game;
        }

        public Task<InstallerExecutionOperation> ExecuteAsync(CancellationToken cancellationToken = default)
            => this.Owner.ExecuteConfirmedPlanAsync(this, cancellationToken);

        public ValueTask DisposeAsync() => this.Owner.DisposeFromConfirmedSessionAsync(this);
    }
}
