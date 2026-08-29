using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Packages;

/// <summary>A test-only pinned executable identity which can't alter the production GitHub CLI pin.</summary>
internal sealed record PinnedGitHubCliTestIdentity
{
    public long SizeBytes { get; }
    public string Sha256 { get; }

    public PinnedGitHubCliTestIdentity(long sizeBytes, string sha256)
    {
        if (sizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        if (
            sha256 is null
            || sha256.Length != 64
            || sha256.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
        )
        {
            throw new ArgumentException("A lowercase SHA-256 digest is required.", nameof(sha256));
        }

        this.SizeBytes = sizeBytes;
        this.Sha256 = sha256;
    }
}

/// <summary>Owns the exact pinned, kernel-immutable GitHub CLI executable used for attestation verification.</summary>
internal sealed class PinnedGitHubCli : IDisposable
{
    internal const string OfficialVersion = "2.92.0";
    internal const string OfficialArchiveSha256 = "b57848131bdf0c229cd35e1f2a51aa718199858b2e728410b37e89a428943ec4";
    internal const long OfficialBinarySizeBytes = 39_805_090;
    internal const string OfficialBinarySha256 = "b58e487e37c00c114aa07f14987ce12f5e5abf12b9da8a38937b65ef218f6772";
    internal const string ExecutableFilename = "gh";

    private const uint UserReadExecuteMode = 0x140; // 0500
    private readonly FileStream Executable;
    private readonly SemaphoreSlim UseLock = new(1, 1);
    private bool Disposed;

    private PinnedGitHubCli(FileStream executable)
    {
        this.Executable = executable;
    }

    /// <summary>Open the bundled GitHub CLI only if it matches the immutable official production pin.</summary>
    internal static Task<PinnedGitHubCli> OpenAsync(string bundledExecutablePath, CancellationToken cancellationToken = default)
    {
        return OpenCoreAsync(
            bundledExecutablePath,
            PinnedGitHubCli.OfficialBinarySizeBytes,
            PinnedGitHubCli.OfficialBinarySha256,
            cancellationToken,
            createExecutableOverride: null,
            changeModeOverride: null
        );
    }

    /// <summary>Open a tiny deterministic fixture without changing the official production identity.</summary>
    internal static Task<PinnedGitHubCli> OpenForTestingAsync(
        string bundledExecutablePath,
        PinnedGitHubCliTestIdentity identity,
        CancellationToken cancellationToken = default,
        Func<uint, SafeFileHandle>? createExecutableOverride = null,
        Func<SafeFileHandle, uint, int>? changeModeOverride = null
    )
    {
        ArgumentNullException.ThrowIfNull(identity);
        return OpenCoreAsync(
            bundledExecutablePath,
            identity.SizeBytes,
            identity.Sha256,
            cancellationToken,
            createExecutableOverride,
            changeModeOverride
        );
    }

    /// <summary>Lease the exact immutable executable descriptor for one child-process launch.</summary>
    internal LinuxSealedFileLease LeaseForExecution()
    {
        this.UseLock.Wait();
        try
        {
            if (this.Disposed)
                throw new ObjectDisposedException(nameof(PinnedGitHubCli));
            if (!OperatingSystem.IsLinux())
                throw new PlatformNotSupportedException("Pinned GitHub CLI execution is only supported on Linux.");
            return LinuxSealedFile.LeaseForExternalRead(this.Executable.SafeFileHandle);
        }
        finally
        {
            this.UseLock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        this.UseLock.Wait();
        try
        {
            if (this.Disposed)
                return;
            this.Disposed = true;
            this.Executable.Dispose();
        }
        finally
        {
            this.UseLock.Release();
        }
    }

    private static async Task<PinnedGitHubCli> OpenCoreAsync(
        string bundledExecutablePath,
        long expectedSize,
        string expectedSha256,
        CancellationToken cancellationToken,
        Func<uint, SafeFileHandle>? createExecutableOverride,
        Func<SafeFileHandle, uint, int>? changeModeOverride
    )
    {
        LinuxPrivilegeGuard.AssertNotRoot();
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Pinned GitHub CLI execution is only supported on Linux.");
        AssertExactExecutableFilename(bundledExecutablePath);

        RetainedReleaseAssetFile source;
        try
        {
            source = RetainedReleaseAssetFile.Open(bundledExecutablePath, "bundled GitHub CLI executable");
        }
        catch (PackageSecurityException)
        {
            throw new PackageSecurityException("The bundled GitHub CLI isn't a safe accessible single-link regular file.");
        }

        using (source)
        {
            if (source.Size != expectedSize)
                throw new PackageSecurityException("The bundled GitHub CLI doesn't match its pinned byte length.");

            FileStream? stagedExecutable = null;
            SafeFileHandle? anonymousFile = null;
            try
            {
                anonymousFile = LinuxSealedFile.CreateExecutableAnonymous(
                    "smapi-installer-pinned-gh",
                    createExecutableOverride
                );
                try
                {
                    stagedExecutable = new FileStream(
                        anonymousFile,
                        FileAccess.ReadWrite,
                        bufferSize: 128 * 1024,
                        isAsync: false
                    );
                    anonymousFile = null;
                }
                catch
                {
                    anonymousFile?.Dispose();
                    anonymousFile = null;
                    throw;
                }
                cancellationToken.ThrowIfCancellationRequested();

                string copiedSha256;
                try
                {
                    copiedSha256 = await source.CopyAndHashAsync(stagedExecutable, expectedSize, cancellationToken).ConfigureAwait(false);
                }
                catch (PackageSecurityException)
                {
                    throw new PackageSecurityException("The bundled GitHub CLI changed or failed validation while it was staged.");
                }
                if (!string.Equals(copiedSha256, expectedSha256, StringComparison.Ordinal))
                    throw new PackageSecurityException("The bundled GitHub CLI doesn't match its pinned SHA-256 digest.");

                await stagedExecutable.FlushAsync(cancellationToken).ConfigureAwait(false);
                stagedExecutable.Flush(flushToDisk: true);
                cancellationToken.ThrowIfCancellationRequested();
                int changeModeError = changeModeOverride is null
                    ? ChangeMode(stagedExecutable.SafeFileHandle, PinnedGitHubCli.UserReadExecuteMode)
                    : changeModeOverride(stagedExecutable.SafeFileHandle, PinnedGitHubCli.UserReadExecuteMode);
                if (changeModeError != 0)
                {
                    if (changeModeError < 0)
                        throw new PackageSecurityException("The GitHub CLI mode test seam returned an invalid result.");
                    throw new PackageSecurityException(
                        "Linux couldn't restrict the pinned GitHub CLI executable mode.",
                        new LinuxNativeIOException("fchmod failed", changeModeError)
                    );
                }
                _ = LinuxSealedFile.SealExecutableImmutable(stagedExecutable.SafeFileHandle);
                await AssertExactSealedBytesAsync(
                    stagedExecutable,
                    expectedSize,
                    expectedSha256,
                    cancellationToken
                ).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                PinnedGitHubCli result = new(stagedExecutable);
                stagedExecutable = null;
                return result;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PackageSecurityException("Linux couldn't stage the pinned GitHub CLI executable.");
            }
            finally
            {
                stagedExecutable?.Dispose();
                anonymousFile?.Dispose();
            }
        }
    }

    private static void AssertExactExecutableFilename(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("The bundled GitHub CLI path is required.", nameof(path));

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new PackageSecurityException("The bundled GitHub CLI path isn't valid.");
        }
        if (!string.Equals(Path.GetFileName(fullPath), PinnedGitHubCli.ExecutableFilename, StringComparison.Ordinal))
            throw new PackageSecurityException("The bundled GitHub CLI filename must be exactly 'gh'.");
    }

    private static async Task AssertExactSealedBytesAsync(
        FileStream executable,
        long expectedSize,
        string expectedSha256,
        CancellationToken cancellationToken
    )
    {
        if (executable.Length != expectedSize)
            throw new PackageSecurityException("The sealed GitHub CLI has an unexpected byte length.");

        executable.Position = 0;
        using SHA256 hasher = SHA256.Create();
        byte[] hash = await hasher.ComputeHashAsync(executable, cancellationToken).ConfigureAwait(false);
        if (
            executable.Position != expectedSize
            || executable.Length != expectedSize
            || executable.ReadByte() != -1
            || !string.Equals(Convert.ToHexString(hash).ToLowerInvariant(), expectedSha256, StringComparison.Ordinal)
        )
        {
            throw new PackageSecurityException("The exact sealed GitHub CLI doesn't match its pinned identity.");
        }
        executable.Position = 0;
    }

    private static int ChangeMode(SafeFileHandle descriptor, uint mode)
    {
        return fchmod(descriptor, mode) == 0 ? 0 : Marshal.GetLastWin32Error();
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int fchmod(SafeFileHandle descriptor, uint mode);
}
