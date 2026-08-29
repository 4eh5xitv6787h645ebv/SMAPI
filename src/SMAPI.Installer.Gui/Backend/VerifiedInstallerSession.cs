using StardewModdingAPI.Installer.Core.Protocol.V1;

namespace StardewModdingAPI.Installer.Gui.Backend;

/// <summary>Owns a verified backend client after its one-time handoff from release verification.</summary>
internal sealed class VerifiedInstallerSession : IVerifiedInstallerSession
{
    private readonly IInstallerProtocolClient Client;
    private readonly SemaphoreSlim CommandGate = new(1, 1);
    private readonly CancellationTokenSource Lifetime = new();
    private readonly object DisposeLock = new();
    private Task? DisposalTask;
    private int DisposeStarted;

    public ProtocolReleaseIdentity Release { get; }
    public Task<InstallerProtocolClientException> SessionFaulted => this.Client.SessionFaulted;

    public VerifiedInstallerSession(ProtocolReleaseIdentity release, IInstallerProtocolClient client)
    {
        this.Release = release ?? throw new ArgumentNullException(nameof(release));
        this.Client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public Task<IReadOnlyList<ProtocolGameCandidate>> DiscoverGamesAsync(CancellationToken cancellationToken = default)
        => this.ExecuteAsync(token => this.Client.DiscoverGamesAsync(token), cancellationToken);

    public Task<ProtocolGameCandidate> ValidateGameAsync(string canonicalPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(canonicalPath);
        return this.ExecuteAsync(token => this.Client.ValidateGameAsync(canonicalPath, token), cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        lock (this.DisposeLock)
        {
            if (this.DisposalTask is not null)
                return new ValueTask(this.DisposalTask);
            Volatile.Write(ref this.DisposeStarted, 1);
            return new ValueTask(this.DisposalTask = this.DisposeCoreAsync());
        }
    }

    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> command, CancellationToken cancellationToken)
    {
        CancellationToken lifetime;
        lock (this.DisposeLock)
        {
            ObjectDisposedException.ThrowIf(this.DisposeStarted != 0, this);
            lifetime = this.Lifetime.Token;
        }
        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        await this.CommandGate.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref this.DisposeStarted) != 0, this);
            return await command(operation.Token).ConfigureAwait(false);
        }
        finally
        {
            this.CommandGate.Release();
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
        }
        if (cleanupFailed)
            throw new InstallerProtocolClientException("The verified installer session could not be cleaned up safely.");
    }
}
