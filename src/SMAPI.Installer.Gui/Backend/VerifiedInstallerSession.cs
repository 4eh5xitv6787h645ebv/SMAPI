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
        Terminal,
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
    private ProtocolGameCandidate? LatestManualCandidate;
    private Task? DisposalTask;
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
            if (this.DisposalTask is not null)
                return new ValueTask(this.DisposalTask);
            if (this.Stage is SessionStage.Bound or SessionStage.Terminal)
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
            if (this.DisposalTask is not null)
                return new ValueTask(this.DisposalTask);
            if (this.Stage is not (SessionStage.Bound or SessionStage.Terminal))
                return this.Stage == SessionStage.Disposed
                    ? ValueTask.CompletedTask
                    : throw new InvalidOperationException("The bound installer session no longer owns backend cleanup.");
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
            }
            result = await this.Client.InspectPlanAsync(
                exactCanonicalPath,
                operation,
                request.Token
            ).ConfigureAwait(false);
            lock (this.DisposeLock)
            {
                if (this.Stage != SessionStage.Bound)
                    throw new ObjectDisposedException(nameof(IPlanInspectionSession));
                if (this.Client.SessionFaulted.IsCompleted)
                    throw new InstallerProtocolClientException("The verified installer session faulted before the plan result could be accepted.");
                request.Token.ThrowIfCancellationRequested();
                if (result is InstallerReadOnlyPlanRejection { IsTerminal: true })
                    this.Stage = SessionStage.Terminal;
                if (result is InstallerReadOnlyPlanSuccess success)
                {
                    if (!this.TryRetainIssuedPlanCandidates(success.Candidates))
                        throw new InstallerProtocolClientException("The installer backend returned invalid candidate capabilities.");
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
            }

            try
            {
                result = await this.Client.ApprovePlanCandidatesAsync(requested, request.Token).ConfigureAwait(false);
                lock (this.DisposeLock)
                {
                    if (this.Stage != SessionStage.Bound)
                        throw new ObjectDisposedException(nameof(IPlanInspectionSession));
                    if (this.Client.SessionFaulted.IsCompleted)
                        throw new InstallerProtocolClientException("The verified installer session faulted before the replacement plan could be accepted.");
                    request.Token.ThrowIfCancellationRequested();
                    if (result is InstallerReadOnlyPlanRejection { IsTerminal: true })
                        this.Stage = SessionStage.Terminal;
                    if (result is InstallerReadOnlyPlanSuccess success)
                    {
                        if (!this.TryRetainIssuedPlanCandidates(success.Candidates))
                            throw new InstallerProtocolClientException("The installer backend returned invalid replacement candidate capabilities.");
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

    /// <remarks>The caller must hold <see cref="DisposeLock"/>.</remarks>
    private bool TryRetainIssuedPlanCandidates(IReadOnlyList<InstallerReadOnlyPlanCandidate>? candidates)
    {
        int capacity = this.IssuedPlanCandidateCapacityForTesting;
        if (
            candidates is null
            || candidates.Count > ProtocolJsonSerializer.MaxPlanCandidates
            || capacity is < ProtocolJsonSerializer.MaxPlanCandidates or > InstallerCandidateSelection.MaximumIssuedCandidatesPerSession
            || this.IssuedPlanCandidates.Count > capacity - candidates.Count
        )
            return false;
        HashSet<InstallerReadOnlyPlanCandidate> current = new(ReferenceEqualityComparer.Instance);
        for (int index = 0; index < candidates.Count; index++)
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
            if (this.Stage == SessionStage.Bound)
                this.Stage = SessionStage.Terminal;
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
    }
}
