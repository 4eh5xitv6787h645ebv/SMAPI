using System.Runtime.InteropServices;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Packages;

/// <summary>
/// Ephemeral process-descriptor paths for one local six-asset snapshot. The copied input remains untrusted until the
/// direct-child backend independently verifies its checksums, metadata, install manifest, and GitHub attestation.
/// </summary>
/// <remarks>
/// The paths, inferred tag, untrusted commit hint, and package bytes are ephemeral input capabilities. Callers must
/// never persist, log, display as verified, or trust them before the backend emits its authenticated PackageOpened
/// result.
/// </remarks>
public sealed class LocalReleaseProtocolAssetPaths
{
    public string ReleaseTag { get; }
    public string UntrustedSourceCommitHint { get; }
    public string InstallerPackagePath { get; }
    public string InstallManifestPath { get; }
    public string ChecksumsPath { get; }
    public string BuildMetadataPath { get; }
    public string AttestationBundlePath { get; }
    public string AttestationBundleChecksumPath { get; }
    public uint WorkspaceDeviceMajor { get; }
    public uint WorkspaceDeviceMinor { get; }
    public ulong WorkspaceInode { get; }
    public long WorkspaceChangeSeconds { get; }
    public uint WorkspaceChangeNanoseconds { get; }

    internal LocalReleaseProtocolAssetPaths(
        string releaseTag,
        string untrustedSourceCommitHint,
        string installerPackagePath,
        string installManifestPath,
        string checksumsPath,
        string buildMetadataPath,
        string attestationBundlePath,
        string attestationBundleChecksumPath,
        uint workspaceDeviceMajor,
        uint workspaceDeviceMinor,
        ulong workspaceInode,
        long workspaceChangeSeconds,
        uint workspaceChangeNanoseconds
    )
    {
        this.ReleaseTag = releaseTag;
        this.UntrustedSourceCommitHint = untrustedSourceCommitHint;
        this.InstallerPackagePath = installerPackagePath;
        this.InstallManifestPath = installManifestPath;
        this.ChecksumsPath = checksumsPath;
        this.BuildMetadataPath = buildMetadataPath;
        this.AttestationBundlePath = attestationBundlePath;
        this.AttestationBundleChecksumPath = attestationBundleChecksumPath;
        this.WorkspaceDeviceMajor = workspaceDeviceMajor;
        this.WorkspaceDeviceMinor = workspaceDeviceMinor;
        this.WorkspaceInode = workspaceInode;
        this.WorkspaceChangeSeconds = workspaceChangeSeconds;
        this.WorkspaceChangeNanoseconds = workspaceChangeNanoseconds;
    }
}

/// <summary>A lifetime-bound private snapshot of a user-selected local release-asset directory.</summary>
public sealed class LocalReleaseAssetLease : IAsyncDisposable
{
    private readonly object Gate = new();
    private readonly ForkReleaseIdentity Identity;
    private readonly string UntrustedSourceCommitHint;
    private readonly PrivateReleaseAssetWorkspace Workspace;
    private Task? CleanupTask;

    internal LocalReleaseAssetLease(
        ForkReleaseIdentity identity,
        string untrustedSourceCommitHint,
        PrivateReleaseAssetWorkspace workspace
    )
    {
        this.Identity = identity;
        this.UntrustedSourceCommitHint = untrustedSourceCommitHint;
        this.Workspace = workspace;
    }

    /// <summary>The canonical fork release tag inferred from the exact local asset names.</summary>
    public string ReleaseTag => this.Identity.Tag;

    /// <summary>
    /// Mint an ephemeral private-path projection for the direct-child verifier. This does not authenticate the local
    /// files, their origin, or their source commit.
    /// </summary>
    public LocalReleaseProtocolAssetPaths Bind()
    {
        lock (this.Gate)
        {
            if (this.CleanupTask is not null)
                throw new ObjectDisposedException(nameof(LocalReleaseAssetLease));

            string[] names = GetExpectedNames(this.Identity);
            this.Workspace.AssertContainsExactly(names);
            LinuxFileIdentity workspaceIdentity = this.Workspace.Identity;
            return new LocalReleaseProtocolAssetPaths(
                this.Identity.Tag,
                this.UntrustedSourceCommitHint,
                this.GetPath(ReviewedReleaseAssetKind.InstallerPackage),
                this.GetPath(ReviewedReleaseAssetKind.InstallManifest),
                this.GetPath(ReviewedReleaseAssetKind.Checksums),
                this.GetPath(ReviewedReleaseAssetKind.BuildMetadata),
                this.GetPath(ReviewedReleaseAssetKind.AttestationBundle),
                this.GetPath(ReviewedReleaseAssetKind.AttestationBundleChecksum),
                workspaceIdentity.DeviceMajor,
                workspaceIdentity.DeviceMinor,
                workspaceIdentity.Inode,
                workspaceIdentity.ChangeSeconds,
                workspaceIdentity.ChangeNanoseconds
            );
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (this.Gate)
        {
            this.CleanupTask ??= this.Workspace.DisposeAsync().AsTask();
            return new ValueTask(this.CleanupTask);
        }
    }

    private string GetPath(ReviewedReleaseAssetKind kind)
    {
        return this.Workspace.GetProcPath(ReviewedGitHubReleaseUris.GetAssetName(this.Identity, kind));
    }

    private static string[] GetExpectedNames(ForkReleaseIdentity identity)
    {
        return Enum.GetValues<ReviewedReleaseAssetKind>()
            .Select(kind => ReviewedGitHubReleaseUris.GetAssetName(identity, kind))
            .ToArray();
    }
}

/// <summary>
/// Snapshots exactly six canonical local release assets into Core-owned private storage. The selected directory is
/// treated as untrusted input and its raw path is never included in the returned protocol projection.
/// </summary>
public static class LocalReleaseAssetImporter
{
    private const int PrivateFileMode = 0x180;
    private const int MaximumSelectedPathCharacters = 4096;
    private const int OwnerRead = 0x100;
    private const int OwnerExecute = 0x40;
    private const int AnyExecute = 0x49;
    private const int GroupOrOtherWrite = 0x12;

    /// <summary>Copy one selected directory containing exactly the six canonical assets into a private lease.</summary>
    public static Task<LocalReleaseAssetLease> ImportDirectoryAsync(
        string selectedDirectory,
        CancellationToken cancellationToken = default
    )
    {
        ValidateSelectedDirectoryArgument(selectedDirectory);
        return Task.Run(
            () => ImportDirectory(selectedDirectory, geteuid(), cancellationToken),
            cancellationToken
        );
    }

    internal static LocalReleaseAssetLease ImportDirectory(
        string selectedDirectory,
        uint effectiveUserId,
        CancellationToken cancellationToken = default,
        Func<PrivateReleaseAssetWorkspace>? workspaceFactory = null,
        Action<LocalReleaseImportCheckpoint, ReviewedReleaseAssetKind?>? checkpoint = null
    )
    {
        ValidateSelectedDirectoryArgument(selectedDirectory);
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Local release import requires Linux.");
        LinuxPrivilegeGuard.AssertNotRoot(effectiveUserId);
        cancellationToken.ThrowIfCancellationRequested();

        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedDirectory));
        string leaf = Path.GetFileName(fullPath);
        string? parentPath = Path.GetDirectoryName(fullPath);
        if (leaf.Length == 0 || parentPath is null)
            throw new ArgumentException("A selected release-asset directory below the filesystem root is required.", nameof(selectedDirectory));

        PrivateReleaseAssetWorkspace? workspace = null;
        List<SourceAsset> sourceAssets = [];
        try
        {
            using LinuxAnchoredFileSystem parent = new(parentPath);
            using LinuxAnchoredFileSystem source = parent.OpenSubdirectory(leaf);
            LinuxFileIdentity sourceRootIdentity = source.GetCurrentRootIdentity();
            AssertSourceDirectory(sourceRootIdentity, effectiveUserId);
            checkpoint?.Invoke(LocalReleaseImportCheckpoint.SourceOpened, null);
            cancellationToken.ThrowIfCancellationRequested();

            string[] observedNames = source.EnumerateEntryNames(maximumEntries: 7).ToArray();
            string[] packageNames = observedNames
                .Where(name => name.StartsWith("SMAPI-", StringComparison.Ordinal) && name.EndsWith("-linux-x64-installer.zip", StringComparison.Ordinal))
                .ToArray();
            if (packageNames.Length != 1)
                throw new PackageSecurityException("The selected directory doesn't contain one canonical fork Linux package asset.");
            ForkReleaseIdentity identity = ForkReleaseIdentity.ParsePackageAssetName(packageNames[0]);
            string[] expectedNames = GetExpectedNames(identity);
            if (!observedNames.SequenceEqual(expectedNames.OrderBy(name => name, StringComparer.Ordinal), StringComparer.Ordinal))
                throw new PackageSecurityException("The selected directory must contain exactly the six canonical release assets.");

            long aggregateBytes = 0;
            foreach (ReviewedReleaseAssetKind kind in Enum.GetValues<ReviewedReleaseAssetKind>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = ReviewedGitHubReleaseUris.GetAssetName(identity, kind);
                LinuxAnchoredFile file = source.OpenRegularFileForRead(name);
                try
                {
                    AssertSourceAsset(file.Identity, effectiveUserId, ReviewedGitHubReleaseUris.GetMaximumAssetBytes(kind));
                    aggregateBytes = checked(aggregateBytes + file.Identity.Size);
                    if (aggregateBytes > ReviewedGitHubReleaseUris.GetMaximumAssetSetBytes())
                        throw new PackageSecurityException("The selected release assets exceed their fixed aggregate size bound.");
                    sourceAssets.Add(new SourceAsset(kind, name, file));
                }
                catch
                {
                    file.Dispose();
                    throw;
                }
            }
            checkpoint?.Invoke(LocalReleaseImportCheckpoint.AssetsOpened, null);
            cancellationToken.ThrowIfCancellationRequested();

            workspace = (workspaceFactory ?? (() => PrivateReleaseAssetWorkspace.Create()))();
            foreach (SourceAsset asset in sourceAssets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long maximumBytes = ReviewedGitHubReleaseUris.GetMaximumAssetBytes(asset.Kind);
                LinuxFileIdentity copied = workspace.FileSystem.CopyFileBounded(
                    asset.File,
                    asset.Name,
                    PrivateFileMode,
                    asset.File.Identity.Size,
                    maximumBytes,
                    cancellationToken
                );
                workspace.RetainCopied(asset.Name, asset.File.Identity.Size, copied);
                checkpoint?.Invoke(LocalReleaseImportCheckpoint.AssetCopied, asset.Kind);
            }
            checkpoint?.Invoke(LocalReleaseImportCheckpoint.BeforeFinalFence, null);
            cancellationToken.ThrowIfCancellationRequested();

            AssertUnchangedSource(parent, leaf, source, sourceRootIdentity, expectedNames, sourceAssets);
            workspace.AssertContainsExactly(expectedNames);
            string metadataName = ReviewedGitHubReleaseUris.GetAssetName(identity, ReviewedReleaseAssetKind.BuildMetadata);
            using LinuxAnchoredFile metadata = workspace.FileSystem.OpenRegularFileForRead(metadataName);
            byte[] metadataBytes = workspace.FileSystem.ReadAllBytesExact(
                metadata,
                PackageVerificationLimits.Default.MaxMetadataBytes,
                cancellationToken
            );
            string untrustedSourceCommitHint = ReleaseMetadataIdentityHintParser.ParseSourceCommit(metadataBytes);
            workspace.AssertContainsExactly(expectedNames);
            checkpoint?.Invoke(LocalReleaseImportCheckpoint.BeforeLeaseTransfer, null);
            cancellationToken.ThrowIfCancellationRequested();

            LocalReleaseAssetLease lease = new(identity, untrustedSourceCommitHint, workspace);
            workspace = null;
            return lease;
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException
            and not PackageSecurityException
            and not PlatformNotSupportedException
            and not PrivilegedInstallerException
            and not ArgumentException
        )
        {
            throw new PackageSecurityException($"The local release assets couldn't be snapshotted safely ({ex.GetType().Name}).");
        }
        finally
        {
            foreach (SourceAsset asset in sourceAssets)
                asset.File.Dispose();
            if (workspace is not null)
                workspace.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    internal static void AssertSourceDirectory(LinuxFileIdentity identity, uint effectiveUserId)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (
            identity.Kind != LinuxAnchoredEntryKind.Directory
            || identity.OwnerUserId != effectiveUserId
            || identity.SpecialModeBits != 0
            || identity.LinkCount < 1
            || (identity.UnixMode & (OwnerRead | OwnerExecute)) != (OwnerRead | OwnerExecute)
            || (identity.UnixMode & GroupOrOtherWrite) != 0
        )
        {
            throw new PackageSecurityException("The selected release-asset directory has unsafe ownership, type, or permissions.");
        }
    }

    internal static void AssertSourceAsset(LinuxFileIdentity identity, uint effectiveUserId, long maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (
            identity.Kind != LinuxAnchoredEntryKind.RegularFile
            || identity.OwnerUserId != effectiveUserId
            || identity.SpecialModeBits != 0
            || identity.LinkCount != 1
            || identity.Size <= 0
            || identity.Size > maximumBytes
            || (identity.UnixMode & OwnerRead) == 0
            || (identity.UnixMode & (AnyExecute | GroupOrOtherWrite)) != 0
        )
        {
            throw new PackageSecurityException("A selected release asset has unsafe ownership, type, permissions, links, or size.");
        }
    }

    private static void AssertUnchangedSource(
        LinuxAnchoredFileSystem parent,
        string leaf,
        LinuxAnchoredFileSystem source,
        LinuxFileIdentity expectedRoot,
        IReadOnlyCollection<string> expectedNames,
        IEnumerable<SourceAsset> sourceAssets
    )
    {
        LinuxFileIdentity retainedRoot = source.GetCurrentRootIdentity();
        LinuxFileIdentity? namedRoot = parent.Stat(leaf);
        if (retainedRoot != expectedRoot || namedRoot != expectedRoot)
            throw new PackageSecurityException("The selected release-asset directory changed while it was snapshotted.");
        string[] observedNames = source.EnumerateEntryNames(maximumEntries: expectedNames.Count + 1).ToArray();
        if (!observedNames.SequenceEqual(expectedNames.OrderBy(name => name, StringComparer.Ordinal), StringComparer.Ordinal))
            throw new PackageSecurityException("The selected release-asset directory contents changed while they were snapshotted.");
        foreach (SourceAsset asset in sourceAssets)
        {
            if (source.Stat(asset.Name) != asset.File.Identity)
                throw new PackageSecurityException("A selected release asset changed while it was snapshotted.");
        }
    }

    private static string[] GetExpectedNames(ForkReleaseIdentity identity)
    {
        return Enum.GetValues<ReviewedReleaseAssetKind>()
            .Select(kind => ReviewedGitHubReleaseUris.GetAssetName(identity, kind))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ValidateSelectedDirectoryArgument(string selectedDirectory)
    {
        if (
            string.IsNullOrWhiteSpace(selectedDirectory)
            || selectedDirectory.Length > MaximumSelectedPathCharacters
            || !Path.IsPathFullyQualified(selectedDirectory)
            || selectedDirectory.Any(char.IsControl)
        )
        {
            throw new ArgumentException("An absolute bounded local release-asset directory is required.", nameof(selectedDirectory));
        }
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint geteuid();

    private sealed record SourceAsset(ReviewedReleaseAssetKind Kind, string Name, LinuxAnchoredFile File);
}

internal enum LocalReleaseImportCheckpoint
{
    SourceOpened,
    AssetsOpened,
    AssetCopied,
    BeforeFinalFence,
    BeforeLeaseTransfer
}
