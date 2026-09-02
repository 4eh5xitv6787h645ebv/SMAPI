using System.Runtime.InteropServices;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Packages;

/// <summary>A retained private Linux workspace which removes only exact entries it created.</summary>
/// <remarks>
/// A process crash can leave one random mode-0700 directory below the configured temporary root. Normal cleanup is
/// deliberately non-recursive and won't chase a renamed/replaced directory or remove unknown entries.
/// </remarks>
internal sealed class PrivateReleaseAssetWorkspace : IAsyncDisposable
{
    private const int PrivateDirectoryMode = 0x1c0;
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    private readonly LinuxAnchoredFileSystem Parent;
    private readonly LinuxFileIdentity DirectoryIdentity;
    private readonly string EntryName;
    private readonly uint EffectiveUserId;
    private readonly Action? BeforeCleanupForTesting;
    private readonly Dictionary<string, LinuxFileIdentity> OwnedFiles = new(StringComparer.Ordinal);
    private LinuxAnchoredFileSystem? DirectoryAuthority;
    private Task? CleanupTask;

    public string ProcPath => this.DirectoryAuthority?.ProcPath
        ?? throw new ObjectDisposedException(nameof(PrivateReleaseAssetWorkspace));

    public LinuxAnchoredFileSystem FileSystem => this.DirectoryAuthority
        ?? throw new ObjectDisposedException(nameof(PrivateReleaseAssetWorkspace));

    public LinuxFileIdentity Identity => this.FileSystem.GetCurrentRootIdentity();

    private PrivateReleaseAssetWorkspace(
        LinuxAnchoredFileSystem parent,
        LinuxAnchoredFileSystem directory,
        LinuxFileIdentity directoryIdentity,
        string entryName,
        uint effectiveUserId,
        Action? beforeCleanupForTesting
    )
    {
        this.Parent = parent;
        this.DirectoryAuthority = directory;
        this.DirectoryIdentity = directoryIdentity;
        this.EntryName = entryName;
        this.EffectiveUserId = effectiveUserId;
        this.BeforeCleanupForTesting = beforeCleanupForTesting;
    }

    public static PrivateReleaseAssetWorkspace Create()
    {
        return Create(afterCreatedForTesting: null, beforeCleanupForTesting: null);
    }

    public static PrivateReleaseAssetWorkspace Create(Action<string>? afterCreatedForTesting)
    {
        return Create(afterCreatedForTesting, beforeCleanupForTesting: null);
    }

    internal static PrivateReleaseAssetWorkspace Create(
        Action<string>? afterCreatedForTesting,
        Action? beforeCleanupForTesting
    )
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Private release-asset staging requires Linux.");
        uint effectiveUserId = geteuid();
        LinuxPrivilegeGuard.AssertNotRoot(effectiveUserId);
        LinuxAnchoredFileSystem? parent = null;
        LinuxAnchoredFileSystem? directory = null;
        try
        {
            parent = new LinuxAnchoredFileSystem(Path.GetFullPath(Path.GetTempPath()));
            string? entryName = null;
            LinuxFileIdentity? identity = null;
            for (int attempt = 0; attempt < 32; attempt++)
            {
                string candidate = $"smapi-private-release-assets-{Guid.NewGuid():N}";
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
                throw new PackageSecurityException("A private release-asset workspace couldn't be created safely.");
            PrivateReleaseAssetWorkspace result = new(
                parent,
                directory,
                identity,
                entryName,
                effectiveUserId,
                beforeCleanupForTesting
            );
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
                result.DisposeAsync().AsTask().GetAwaiter().GetResult();
                throw;
            }
        }
        catch (Exception ex) when (ex is not PackageSecurityException and not PlatformNotSupportedException)
        {
            throw new PackageSecurityException($"A private release-asset workspace couldn't be created safely ({ex.GetType().Name}).");
        }
        finally
        {
            directory?.Dispose();
            parent?.Dispose();
        }
    }

    public AnchoredDownloadTarget CreateTarget(string exactName, long expectedSizeBytes)
    {
        AssertValidAsset(exactName, expectedSizeBytes);
        this.AssertPrivate();
        return new AnchoredDownloadTarget(
            this.FileSystem,
            exactName,
            this.ProcPath,
            this.EffectiveUserId,
            expectedSizeBytes,
            identity => this.RegisterOwned(exactName, expectedSizeBytes, identity)
        );
    }

    public void RetainPublished(AnchoredDownloadTarget target, string exactName, long expectedSizeBytes)
    {
        ArgumentNullException.ThrowIfNull(target);
        AssertValidAsset(exactName, expectedSizeBytes);
        LinuxFileIdentity identity = target.PublishedIdentity
            ?? throw new PackageSecurityException("A release asset wasn't published safely.");
        this.AssertRetained(exactName, expectedSizeBytes, identity);
    }

    /// <summary>Register an exact bounded copy as an owned retained release asset.</summary>
    public void RetainCopied(string exactName, long expectedSizeBytes, LinuxFileIdentity identity)
    {
        AssertValidAsset(exactName, expectedSizeBytes);
        ArgumentNullException.ThrowIfNull(identity);
        this.RegisterOwned(exactName, expectedSizeBytes, identity);
        this.AssertRetained(exactName, expectedSizeBytes, identity);
    }

    private void AssertRetained(string exactName, long expectedSizeBytes, LinuxFileIdentity identity)
    {
        this.AssertPrivate();
        LinuxFileIdentity current = this.FileSystem.Stat(exactName)
            ?? throw new PackageSecurityException("A release asset disappeared after staging.");
        if (
            current != identity
            || current.Kind != LinuxAnchoredEntryKind.RegularFile
            || current.OwnerUserId != this.EffectiveUserId
            || current.SpecialModeBits != 0
            || current.LinkCount != 1
            || current.UnixMode != 0x180
            || current.Size != expectedSizeBytes
            || !this.OwnedFiles.TryGetValue(exactName, out LinuxFileIdentity? owned)
            || owned != identity
        )
        {
            throw new PackageSecurityException("A release asset changed after staging.");
        }
    }

    private void RegisterOwned(string exactName, long expectedSizeBytes, LinuxFileIdentity identity)
    {
        if (!this.OwnedFiles.TryAdd(exactName, identity))
            throw new PackageSecurityException("A release asset was staged more than once.");
        if (
            identity.Kind != LinuxAnchoredEntryKind.RegularFile
            || identity.OwnerUserId != this.EffectiveUserId
            || identity.SpecialModeBits != 0
            || identity.LinkCount != 1
            || identity.UnixMode != 0x180
            || identity.Size != expectedSizeBytes
        )
        {
            throw new PackageSecurityException("A release asset wasn't retained after staging.");
        }
    }

    public void AssertContainsExactly(IEnumerable<string> exactNames)
    {
        ArgumentNullException.ThrowIfNull(exactNames);
        this.AssertPrivate();
        string[] expectedNames = exactNames
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        string[] observedNames = this.FileSystem.EnumerateEntryNames(maximumEntries: expectedNames.Length + 1)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (!observedNames.SequenceEqual(expectedNames, StringComparer.Ordinal))
            throw new PackageSecurityException("The private release-asset workspace doesn't contain exactly the expected files.");
        foreach (string name in expectedNames)
            _ = this.GetProcPath(name);
    }

    public string GetProcPath(string exactName)
    {
        this.AssertPrivate();
        if (!this.OwnedFiles.TryGetValue(exactName, out LinuxFileIdentity? expected))
            throw new PackageSecurityException("The requested release asset isn't retained by this workspace.");
        LinuxFileIdentity current = this.FileSystem.Stat(exactName)
            ?? throw new PackageSecurityException("A retained release asset disappeared.");
        if (current != expected)
            throw new PackageSecurityException("A retained release asset changed.");
        return Path.Combine(this.ProcPath, exactName);
    }

    public ValueTask DisposeAsync()
    {
        lock (this.OwnedFiles)
            this.CleanupTask ??= this.RevokeAndCleanupBoundedAsync();
        return new ValueTask(this.CleanupTask);
    }

    private Task RevokeAndCleanupBoundedAsync()
    {
        LinuxAnchoredFileSystem? publishedAuthority = Interlocked.Exchange(ref this.DirectoryAuthority, null);
        if (publishedAuthority is null)
            return Task.CompletedTask;

        LinuxAnchoredFileSystem? cleanupAuthority = null;
        try
        {
            cleanupAuthority = publishedAuthority.DuplicateAuthority();
        }
        catch
        {
            // Revoke the published capability even if a cleanup-only authority can't be retained.
            this.Parent.Dispose();
        }
        finally
        {
            publishedAuthority.Dispose();
        }
        return cleanupAuthority is null
            ? Task.CompletedTask
            : this.CleanupBoundedAsync(cleanupAuthority);
    }

    private async Task CleanupBoundedAsync(LinuxAnchoredFileSystem cleanupAuthority)
    {
        Task cleanup = Task.Run(() => this.Cleanup(cleanupAuthority));
        if (await Task.WhenAny(cleanup, Task.Delay(CleanupTimeout)).ConfigureAwait(false) == cleanup)
            await cleanup.ConfigureAwait(false);
        else
            _ = cleanup.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    private void Cleanup(LinuxAnchoredFileSystem directory)
    {
        try
        {
            this.BeforeCleanupForTesting?.Invoke();
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
            throw new PackageSecurityException("The retained private release-asset workspace changed.");
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

    private static void AssertValidAsset(string exactName, long expectedSizeBytes)
    {
        ArgumentNullException.ThrowIfNull(exactName);
        if (string.IsNullOrEmpty(exactName) || exactName.Contains('/') || exactName.Any(char.IsControl))
            throw new PackageSecurityException("The release asset name isn't a safe exact leaf.");
        if (expectedSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedSizeBytes));
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint geteuid();
}
