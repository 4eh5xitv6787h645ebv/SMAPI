using StardewModdingAPI.Installer.Gui.Frontend;

namespace StardewModdingAPI.Installer.Gui.Backend;

/// <summary>A prepared package projection serialized against the lifetime of its retained authority owner.</summary>
internal sealed class RetainedPreparedReleasePackage : IPreparedReleasePackage
{
    private readonly object Sync = new();
    private readonly InstallerPackageOpenInput PackageValue;
    private IAsyncDisposable? Owner;
    private Task? DisposalTask;

    public RetainedPreparedReleasePackage(InstallerPackageOpenInput package, IAsyncDisposable owner)
    {
        this.PackageValue = package ?? throw new ArgumentNullException(nameof(package));
        this.Owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public InstallerPackageOpenInput Package
    {
        get
        {
            lock (this.Sync)
            {
                ObjectDisposedException.ThrowIf(this.DisposalTask is not null, this);
                return this.PackageValue;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        IAsyncDisposable? owner = null;
        TaskCompletionSource? completion = null;
        Task disposal;
        lock (this.Sync)
        {
            if (this.DisposalTask is not null)
                return new ValueTask(this.DisposalTask);
            owner = this.Owner
                ?? throw new InvalidOperationException("The retained release authority was lost before disposal.");
            this.Owner = null;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = this.DisposalTask = completion.Task;
        }
        _ = DisposeOwnerAsync(owner, completion);
        return new ValueTask(disposal);
    }

    private static async Task DisposeOwnerAsync(IAsyncDisposable owner, TaskCompletionSource completion)
    {
        try
        {
            await owner.DisposeAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }
}
