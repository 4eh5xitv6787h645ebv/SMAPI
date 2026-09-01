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
    private readonly HashSet<ProtocolGameCandidate> IssuedCandidates = new(ReferenceEqualityComparer.Instance);
    private Task? DisposalTask;
    private SessionStage Stage = SessionStage.Discovery;
    private int AdmittedDiscoveryCommands;

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
                if (snapshot.Any(candidate => candidate is null))
                    throw new InstallerProtocolClientException("The installer backend returned an invalid game-folder result.");
                this.IssuedCandidates.Clear();
                foreach (ProtocolGameCandidate candidate in snapshot)
                    this.IssuedCandidates.Add(candidate);
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
            if (candidate.State != Core.Engine.LinuxGameFolderStatus.Valid || !this.IssuedCandidates.Contains(candidate))
                throw new ArgumentException("The game must be the exact valid result issued by this verified session.", nameof(candidate));

            VerifiedGamePresentation game = new(candidate.CanonicalPath, candidate.DisplayName);
            BoundPlanInspectionSession result = new(this, this.Release, game);
            this.IssuedCandidates.Clear();
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
                this.IssuedCandidates.Add(candidate);
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
        VerifiedGamePresentation game,
        InstallerOperation operation,
        CancellationToken cancellationToken
    )
    {
        CancellationToken lifetime;
        lock (this.DisposeLock)
        {
            if (this.Stage != SessionStage.Bound)
                throw new ObjectDisposedException(nameof(IPlanInspectionSession));
            lifetime = this.Lifetime.Token;
        }
        using CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        await this.CommandGate.WaitAsync(request.Token).ConfigureAwait(false);
        InstallerReadOnlyPlanResult result;
        try
        {
            lock (this.DisposeLock)
            {
                if (this.Stage != SessionStage.Bound)
                    throw new ObjectDisposedException(nameof(IPlanInspectionSession));
            }
            result = await this.Client.InspectPlanAsync(
                game.CanonicalPath,
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
            }
        }
        finally
        {
            this.CommandGate.Release();
        }
        if (result is InstallerReadOnlyPlanRejection { IsTerminal: true })
            await this.DisposeFromBoundSessionAsync().ConfigureAwait(false);
        return result;
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
                this.IssuedCandidates.Clear();
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

    private sealed class BoundPlanInspectionSession : IPlanInspectionSession
    {
        private readonly VerifiedInstallerSession Owner;

        public ProtocolReleaseIdentity Release { get; }
        public VerifiedGamePresentation Game { get; }
        public Task<InstallerProtocolClientException> SessionFaulted => this.Owner.SessionFaulted;

        public BoundPlanInspectionSession(VerifiedInstallerSession owner, ProtocolReleaseIdentity release, VerifiedGamePresentation game)
        {
            this.Owner = owner;
            this.Release = release;
            this.Game = game;
        }

        public ValueTask DisposeAsync() => this.Owner.DisposeFromBoundSessionAsync();

        public Task<InstallerReadOnlyPlanResult> InspectPlanAsync(InstallerOperation operation, CancellationToken cancellationToken = default)
            => this.Owner.InspectBoundPlanAsync(this.Game, operation, cancellationToken);
    }
}
