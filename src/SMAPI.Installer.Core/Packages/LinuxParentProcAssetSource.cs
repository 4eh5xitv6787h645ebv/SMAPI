using System.Runtime.InteropServices;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Packages;

internal sealed record LinuxParentProcAssetSourceFaults(
    Action<int>? AfterLeafOpened = null,
    Action? BeforeFinalFence = null
);

/// <summary>One production-session authority for the direct controller process's proc descriptor directory.</summary>
internal sealed class LinuxParentProcFdAuthority : IDisposable
{
    private readonly LinuxAnchoredFileSystem ParentFdDirectory;
    private readonly LinuxParentProcAssetSourceFaults? Faults;
    private readonly uint EffectiveUserId;
    private readonly Func<int> GetCapturedParentProcessId;
    private readonly Func<uint> GetCapturedEffectiveUserId;
    private bool Disposed;

    public int ParentProcessId { get; }

    public LinuxParentProcFdAuthority()
        : this(GetParentProcessId, path => new LinuxAnchoredFileSystem(path), GetEffectiveUserId)
    {
    }

    internal LinuxParentProcFdAuthority(
        Func<int> getParentProcessId,
        Func<string, LinuxAnchoredFileSystem> openAnchoredDirectory,
        Func<uint>? getEffectiveUserId = null,
        Func<LinuxAnchoredFileSystem, LinuxFileIdentity>? getDirectoryIdentity = null,
        LinuxParentProcAssetSourceFaults? faults = null
    )
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Parent proc descriptor authority requires Linux.");
        ArgumentNullException.ThrowIfNull(getParentProcessId);
        ArgumentNullException.ThrowIfNull(openAnchoredDirectory);
        getEffectiveUserId ??= GetEffectiveUserId;
        getDirectoryIdentity ??= directory => directory.GetCurrentRootIdentity();

        int before = getParentProcessId();
        uint effectiveUserId = getEffectiveUserId();
        LinuxPrivilegeGuard.AssertNotRoot(effectiveUserId);
        if (before <= 0)
            throw new PackageSecurityException("The controller process authority couldn't be established safely.");
        LinuxAnchoredFileSystem? directory = null;
        try
        {
            directory = openAnchoredDirectory($"/proc/{before}/fd");
            LinuxFileIdentity identity = getDirectoryIdentity(directory);
            if (
                !directory.IsProcFileSystem
                || identity.Kind != LinuxAnchoredEntryKind.Directory
                || identity.OwnerUserId != effectiveUserId
                || identity.UnixMode != 0x140
                || identity.SpecialModeBits != 0
                || identity.LinkCount != 2
                || getParentProcessId() != before
            )
                throw new PackageSecurityException("The controller process authority changed during capture.");
            this.ParentProcessId = before;
            this.EffectiveUserId = effectiveUserId;
            this.GetCapturedParentProcessId = getParentProcessId;
            this.GetCapturedEffectiveUserId = getEffectiveUserId;
            this.ParentFdDirectory = directory;
            this.Faults = faults;
            directory = null;
        }
        catch (PackageSecurityException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new PackageSecurityException("The controller process authority couldn't be established safely.", ex);
        }
        finally
        {
            directory?.Dispose();
        }
    }

    public LinuxParentProcAssetSource Capture(
        LinuxTaggedReleaseAssetSet assets,
        ForkReleaseIdentity identity,
        Protocol.V1.ProtocolProcWorkspaceIdentity expectedWorkspaceIdentity,
        CancellationToken cancellationToken
    )
    {
        if (this.Disposed)
            throw new ObjectDisposedException(nameof(LinuxParentProcFdAuthority));
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(expectedWorkspaceIdentity);
        cancellationToken.ThrowIfCancellationRequested();
        if (this.GetCapturedParentProcessId() != this.ParentProcessId || this.GetCapturedEffectiveUserId() != this.EffectiveUserId)
            throw new PackageSecurityException("The controller process authority is no longer direct.");

        (string Path, string ExpectedLeaf)[] paths =
        [
            (assets.PackagePath, identity.PackageAssetName),
            (assets.ChecksumsPath, ReleasePackageVerifier.ChecksumAssetName),
            (assets.BuildMetadataPath, ReleasePackageVerifier.BuildMetadataAssetName),
            (assets.InstallManifestPath, VerifiedInstallerPackageFactory.GetManifestAssetName(identity)),
            (assets.AttestationBundlePath, VerifiedGitHubAttestationBundleFactory.GetBundleAssetName(identity)),
            (assets.AttestationBundleChecksumPath, VerifiedGitHubAttestationBundleFactory.GetChecksumAssetName(identity))
        ];

        ParsedProcAssetPath[] parsed = paths.Select(value => Parse(value.Path, value.ExpectedLeaf)).ToArray();
        ParsedProcAssetPath first = parsed[0];
        if (first.ProcessId != this.ParentProcessId || parsed.Any(value => value.ProcessId != first.ProcessId || value.Descriptor != first.Descriptor))
            throw new PackageSecurityException("The selected release assets don't share the controller's retained directory authority.");

        LinuxAnchoredFileSystem? workspace = null;
        Dictionary<string, LinuxAnchoredFile>? retained = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            workspace = this.ParentFdDirectory.OpenProcFileDescriptorDirectory(first.Descriptor.ToString(System.Globalization.CultureInfo.InvariantCulture));
            uint effectiveUserId = this.EffectiveUserId;
            LinuxFileIdentity openedWorkspace = workspace.GetCurrentRootIdentity();
            AssertDirectory(openedWorkspace, effectiveUserId);
            AssertExpectedWorkspace(openedWorkspace, expectedWorkspaceIdentity);
            AssertExactNames(workspace, paths.Select(value => value.ExpectedLeaf));

            retained = new Dictionary<string, LinuxAnchoredFile>(StringComparer.Ordinal);
            for (int index = 0; index < paths.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string leaf = paths[index].ExpectedLeaf;
                LinuxAnchoredFile file = workspace.OpenRegularFileForRead(leaf);
                try
                {
                    AssertFile(file.Identity, effectiveUserId);
                    retained.Add(leaf, file);
                    file = null!;
                }
                finally
                {
                    file?.Dispose();
                }
                this.Faults?.AfterLeafOpened?.Invoke(index);
            }

            cancellationToken.ThrowIfCancellationRequested();
            this.Faults?.BeforeFinalFence?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            LinuxFileIdentity finalWorkspace = workspace.GetCurrentRootIdentity();
            AssertDirectory(finalWorkspace, effectiveUserId);
            AssertExpectedWorkspace(finalWorkspace, expectedWorkspaceIdentity);
            if (
                !finalWorkspace.IsSameObject(openedWorkspace)
                || this.GetCapturedParentProcessId() != this.ParentProcessId
                || this.GetCapturedEffectiveUserId() != this.EffectiveUserId
            )
                throw new PackageSecurityException("The controller process authority changed during retained asset capture.");
            AssertExactNames(workspace, paths.Select(value => value.ExpectedLeaf));
            foreach ((string name, LinuxAnchoredFile file) in retained)
            {
                LinuxFileIdentity? named = workspace.Stat(name);
                if (named != file.Identity)
                    throw new PackageSecurityException("A retained release asset changed during capture.");
                AssertFile(named, effectiveUserId);
            }
            cancellationToken.ThrowIfCancellationRequested();

            LinuxParentProcAssetSource result = new(workspace, retained);
            workspace = null;
            retained = null;
            return result;
        }
        catch (PackageSecurityException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new PackageSecurityException("The retained release assets failed safe local validation.", ex);
        }
        finally
        {
            if (retained is not null)
            {
                foreach (LinuxAnchoredFile file in retained.Values)
                    file.Dispose();
            }
            workspace?.Dispose();
        }
    }

    public void Dispose()
    {
        if (this.Disposed)
            return;
        this.Disposed = true;
        this.ParentFdDirectory.Dispose();
    }

    internal static bool IsProcProjectionPath(string path) => path?.StartsWith("/proc/", StringComparison.Ordinal) == true;

    private static ParsedProcAssetPath Parse(string path, string expectedLeaf)
    {
        if (string.IsNullOrEmpty(path))
            throw new PackageSecurityException("A retained release asset path is invalid.");
        string[] segments = path.Split('/');
        if (
            segments.Length != 6
            || segments[0].Length != 0
            || segments[1] != "proc"
            || !TryParseCanonicalDecimal(segments[2], requirePositive: true, out int processId)
            || segments[3] != "fd"
            || !TryParseCanonicalDecimal(segments[4], requirePositive: false, out int descriptor)
            || !string.Equals(segments[5], expectedLeaf, StringComparison.Ordinal)
        )
        {
            throw new PackageSecurityException("A retained release asset path is invalid.");
        }
        return new ParsedProcAssetPath(processId, descriptor);
    }

    private static bool TryParseCanonicalDecimal(string value, bool requirePositive, out int result)
    {
        result = 0;
        if (string.IsNullOrEmpty(value) || (value.Length > 1 && value[0] == '0'))
            return false;
        foreach (char character in value)
        {
            if (character is < '0' or > '9')
                return false;
            try
            {
                result = checked((result * 10) + (character - '0'));
            }
            catch (OverflowException)
            {
                return false;
            }
        }
        return !requirePositive || result > 0;
    }

    private static void AssertDirectory(LinuxFileIdentity identity, uint effectiveUserId)
    {
        if (
            identity.Kind != LinuxAnchoredEntryKind.Directory
            || identity.OwnerUserId != effectiveUserId
            || identity.UnixMode != 0x1c0
            || identity.SpecialModeBits != 0
        )
        {
            throw new PackageSecurityException("The retained release workspace isn't a private user directory.");
        }
    }

    private static void AssertFile(LinuxFileIdentity identity, uint effectiveUserId)
    {
        if (
            identity.Kind != LinuxAnchoredEntryKind.RegularFile
            || identity.OwnerUserId != effectiveUserId
            || identity.LinkCount != 1
            || identity.UnixMode != 0x180
            || identity.SpecialModeBits != 0
        )
        {
            throw new PackageSecurityException("A retained release asset isn't a private single-link regular file.");
        }
    }

    private static void AssertExpectedWorkspace(
        LinuxFileIdentity identity,
        Protocol.V1.ProtocolProcWorkspaceIdentity expected
    )
    {
        if (
            identity.DeviceMajor != expected.DeviceMajor
            || identity.DeviceMinor != expected.DeviceMinor
            || identity.Inode != expected.Inode
            || identity.ChangeSeconds != expected.ChangeSeconds
            || identity.ChangeNanoseconds != expected.ChangeNanoseconds
        )
            throw new PackageSecurityException("The retained release workspace doesn't match the selected acquisition authority.");
    }

    private static void AssertExactNames(LinuxAnchoredFileSystem workspace, IEnumerable<string> expected)
    {
        string[] names = workspace.EnumerateEntryNames(maximumEntries: 7).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        string[] expectedNames = expected.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (!names.SequenceEqual(expectedNames, StringComparer.Ordinal))
            throw new PackageSecurityException("The retained release workspace doesn't contain the exact reviewed asset set.");
    }

    [DllImport("libc", EntryPoint = "getppid")]
    private static extern int GetParentProcessId();

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    private sealed record ParsedProcAssetPath(int ProcessId, int Descriptor);
}

/// <summary>Six synchronously retained files which can each be transferred exactly once to a verifier.</summary>
internal sealed class LinuxParentProcAssetSource : IRetainedReleaseAssetSource
{
    private readonly LinuxAnchoredFileSystem Workspace;
    private readonly Dictionary<string, LinuxAnchoredFile> Files;
    private bool Disposed;

    public LinuxParentProcAssetSource(LinuxAnchoredFileSystem workspace, Dictionary<string, LinuxAnchoredFile> files)
    {
        this.Workspace = workspace;
        this.Files = files;
    }

    public RetainedReleaseAssetFile Open(string logicalName, string description)
    {
        if (this.Disposed)
            throw new ObjectDisposedException(nameof(LinuxParentProcAssetSource));
        if (!this.Files.Remove(logicalName, out LinuxAnchoredFile? file))
            throw new PackageSecurityException($"The retained {description} authority is unavailable.");
        return RetainedReleaseAssetFile.Adopt(this.Workspace, file);
    }

    public void Dispose()
    {
        if (this.Disposed)
            return;
        this.Disposed = true;
        foreach (LinuxAnchoredFile file in this.Files.Values)
            file.Dispose();
        this.Files.Clear();
        this.Workspace.Dispose();
    }
}
