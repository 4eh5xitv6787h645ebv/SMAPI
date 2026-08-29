using System.Runtime.InteropServices;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Packages;

/// <summary>A retained private Linux workspace which removes only exact entries it created.</summary>
/// <remarks>
/// A process crash can leave one random mode-0700 directory below the configured temporary root. Normal cleanup is
/// deliberately non-recursive and won't chase a renamed/replaced directory or remove unknown entries.
/// </remarks>
internal sealed class ReviewedReleaseAssetWorkspace : IAsyncDisposable
{
    private const int PrivateDirectoryMode = 0x1c0;
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    private readonly LinuxAnchoredFileSystem Parent;
    private readonly LinuxFileIdentity DirectoryIdentity;
    private readonly string EntryName;
    private readonly uint EffectiveUserId;
    private readonly Dictionary<string, LinuxFileIdentity> OwnedFiles = new(StringComparer.Ordinal);
    private LinuxAnchoredFileSystem? DirectoryAuthority;
    private Task? CleanupTask;

    public string ProcPath => this.DirectoryAuthority?.ProcPath
        ?? throw new ObjectDisposedException(nameof(ReviewedReleaseAssetWorkspace));

    public LinuxAnchoredFileSystem FileSystem => this.DirectoryAuthority
        ?? throw new ObjectDisposedException(nameof(ReviewedReleaseAssetWorkspace));

    private ReviewedReleaseAssetWorkspace(
        LinuxAnchoredFileSystem parent,
        LinuxAnchoredFileSystem directory,
        LinuxFileIdentity directoryIdentity,
        string entryName,
        uint effectiveUserId
    )
    {
        this.Parent = parent;
        this.DirectoryAuthority = directory;
        this.DirectoryIdentity = directoryIdentity;
        this.EntryName = entryName;
        this.EffectiveUserId = effectiveUserId;
    }

    public static ReviewedReleaseAssetWorkspace Create(Action<string>? afterCreatedForTesting = null)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Reviewed release acquisition requires Linux.");
        LinuxPrivilegeGuard.AssertNotRoot();
        uint effectiveUserId = geteuid();
        LinuxAnchoredFileSystem? parent = null;
        LinuxAnchoredFileSystem? directory = null;
        try
        {
            parent = new LinuxAnchoredFileSystem(Path.GetFullPath(Path.GetTempPath()));
            string? entryName = null;
            LinuxFileIdentity? identity = null;
            for (int attempt = 0; attempt < 32; attempt++)
            {
                string candidate = $"smapi-reviewed-release-{Guid.NewGuid():N}";
                try
                {
                    directory = parent.CreateNewSubdirectory(
                        candidate,
                        PrivateDirectoryMode,
                        out LinuxFileIdentity created,
                        afterCreatedForTesting is null
                            ? null
                            : () => afterCreatedForTesting(Path.Combine(Path.GetFullPath(Path.GetTempPath()), candidate))
                    );
                    entryName = candidate;
                    identity = created;
                    break;
                }
                catch (IOException) when (parent.Stat(candidate) is not null)
                {
                    // A random collision is retried; a replacement is never removed by this loop.
                }
            }
            if (directory is null || entryName is null || identity is null)
                throw new PackageSecurityException("A private reviewed-release workspace couldn't be created safely.");
            ReviewedReleaseAssetWorkspace result = new(parent, directory, identity, entryName, effectiveUserId);
            parent = null;
            directory = null;
            try
            {
                result.AssertPrivate();
                AssertProcDirectoryAvailable(result.ProcPath);
                return result;
            }
            catch
            {
                result.Cleanup();
                throw;
            }
        }
        catch (Exception ex) when (ex is not PackageSecurityException and not PlatformNotSupportedException)
        {
            throw new PackageSecurityException($"A private reviewed-release workspace couldn't be created safely ({ex.GetType().Name}).");
        }
        finally
        {
            directory?.Dispose();
            parent?.Dispose();
        }
    }

    public AnchoredDownloadTarget CreateTarget(ReviewedReleaseAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        this.AssertPrivate();
        return new AnchoredDownloadTarget(
            this.FileSystem,
            asset.Name,
            this.ProcPath,
            this.EffectiveUserId,
            asset.SizeBytes,
            identity => this.RegisterPublished(asset, identity)
        );
    }

    public void RetainPublished(AnchoredDownloadTarget target, ReviewedReleaseAsset asset)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(asset);
        LinuxFileIdentity identity = target.PublishedIdentity
            ?? throw new PackageSecurityException("A reviewed release asset wasn't published safely.");
        LinuxFileIdentity current = this.FileSystem.Stat(asset.Name)
            ?? throw new PackageSecurityException("A reviewed release asset disappeared after publication.");
        if (
            current != identity
            || current.Kind != LinuxAnchoredEntryKind.RegularFile
            || current.OwnerUserId != this.EffectiveUserId
            || current.SpecialModeBits != 0
            || current.LinkCount != 1
            || current.UnixMode != 0x180
            || current.Size != asset.SizeBytes
            || !this.OwnedFiles.TryGetValue(asset.Name, out LinuxFileIdentity? owned)
            || owned != identity
        )
        {
            throw new PackageSecurityException("A reviewed release asset changed after publication.");
        }
    }

    private void RegisterPublished(ReviewedReleaseAsset asset, LinuxFileIdentity identity)
    {
        if (
            identity.Kind != LinuxAnchoredEntryKind.RegularFile
            || identity.OwnerUserId != this.EffectiveUserId
            || identity.SpecialModeBits != 0
            || identity.LinkCount != 1
            || identity.UnixMode != 0x180
            || identity.Size != asset.SizeBytes
            || !this.OwnedFiles.TryAdd(asset.Name, identity)
        )
        {
            throw new PackageSecurityException("A reviewed release asset wasn't retained after publication.");
        }
    }

    public string GetProcPath(string exactName)
    {
        this.AssertPrivate();
        if (!this.OwnedFiles.TryGetValue(exactName, out LinuxFileIdentity? expected))
            throw new PackageSecurityException("The requested reviewed release asset isn't retained by this workspace.");
        LinuxFileIdentity current = this.FileSystem.Stat(exactName)
            ?? throw new PackageSecurityException("A retained reviewed release asset disappeared.");
        if (current != expected)
            throw new PackageSecurityException("A retained reviewed release asset changed.");
        return Path.Combine(this.ProcPath, exactName);
    }

    public ValueTask DisposeAsync()
    {
        lock (this.OwnedFiles)
            this.CleanupTask ??= this.CleanupBoundedAsync();
        return new ValueTask(this.CleanupTask);
    }

    private async Task CleanupBoundedAsync()
    {
        Task cleanup = Task.Run(this.Cleanup);
        if (await Task.WhenAny(cleanup, Task.Delay(CleanupTimeout)).ConfigureAwait(false) == cleanup)
            await cleanup.ConfigureAwait(false);
        else
            _ = cleanup.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    private void Cleanup()
    {
        LinuxAnchoredFileSystem? directory = Interlocked.Exchange(ref this.DirectoryAuthority, null);
        if (directory is null)
            return;
        try
        {
            foreach ((string name, LinuxFileIdentity expected) in this.OwnedFiles.Reverse())
            {
                try
                {
                    LinuxFileIdentity? current = directory.Stat(name);
                    if (current == expected)
                        directory.UnlinkFile(name, expected);
                }
                catch
                {
                    // Cleanup is limited to the exact known identity; leave changed entries untouched.
                }
            }
            try
            {
                LinuxFileIdentity? named = this.Parent.Stat(this.EntryName);
                LinuxFileIdentity retained = directory.GetCurrentRootIdentity();
                if (
                    retained.IsSameObject(this.DirectoryIdentity)
                    && named == retained
                )
                {
                    this.Parent.RemoveEmptyDirectory(this.EntryName, retained);
                }
            }
            catch
            {
                // Never chase a renamed/replaced workspace or recursively remove unknown entries.
            }
        }
        finally
        {
            directory.Dispose();
            this.Parent.Dispose();
        }
    }

    private void AssertPrivate()
    {
        AssertPrivateDirectory(this.FileSystem.GetCurrentRootIdentity(), this.EffectiveUserId);
    }

    private static void AssertPrivateDirectory(LinuxFileIdentity identity, uint effectiveUserId)
    {
        if (
            identity.Kind != LinuxAnchoredEntryKind.Directory
            || identity.OwnerUserId != effectiveUserId
            || identity.SpecialModeBits != 0
            || identity.LinkCount < 1
            || identity.UnixMode != PrivateDirectoryMode
        )
        {
            throw new PackageSecurityException("The retained private reviewed-release workspace changed.");
        }
    }

    private static void AssertProcDirectoryAvailable(string procPath)
    {
        try
        {
            if (!Directory.Exists(procPath) || Directory.EnumerateFileSystemEntries(procPath).Any())
                throw new PackageSecurityException("The retained process-descriptor filesystem isn't available and empty.");
        }
        catch (PackageSecurityException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PackageSecurityException($"The retained process-descriptor filesystem isn't available ({ex.GetType().Name}).");
        }
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint geteuid();
}
