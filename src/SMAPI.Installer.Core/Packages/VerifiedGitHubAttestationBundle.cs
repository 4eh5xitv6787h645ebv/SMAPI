using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Packages;

/// <summary>
/// Opaque authority for one exact checksummed, immutable local GitHub attestation bundle.
/// The sidecar detects transport corruption; authenticity is established only when the pinned verifier validates the bundle.
/// </summary>
public sealed class VerifiedGitHubAttestationBundle : IDisposable
{
    private readonly SafeFileHandle RetainedBundle;
    private readonly SemaphoreSlim UseLock = new(1, 1);
    private bool Disposed;

    /// <summary>The exact tagged release whose local evidence this bundle contains.</summary>
    public InstallationReleaseIdentity Release { get; }

    /// <summary>The exact release-asset filename.</summary>
    public string AssetName { get; }

    /// <summary>The independently checksummed bundle digest.</summary>
    public Sha256Digest Sha256 { get; }

    /// <summary>The exact bounded bundle byte length.</summary>
    public long SizeBytes { get; }

    internal VerifiedGitHubAttestationBundle(
        InstallationReleaseIdentity release,
        string assetName,
        Sha256Digest sha256,
        long sizeBytes,
        SafeFileHandle retainedBundle
    )
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(sha256);
        ArgumentNullException.ThrowIfNull(retainedBundle);
        string expectedName = VerifiedGitHubAttestationBundleFactory.GetBundleAssetName(release);
        if (!string.Equals(assetName, expectedName, StringComparison.Ordinal))
            throw new ArgumentException("The local attestation-bundle name doesn't match its release identity.", nameof(assetName));
        if (sizeBytes is <= 0 or > VerifiedGitHubAttestationBundleFactory.MaximumBundleBytes)
            throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        using (LinuxSealedFile.LeaseForExternalRead(retainedBundle))
        {
        }

        this.Release = release;
        this.AssetName = assetName;
        this.Sha256 = sha256;
        this.SizeBytes = sizeBytes;
        this.RetainedBundle = retainedBundle;
    }

    /// <summary>Lease the exact immutable bundle descriptor for the pinned verifier.</summary>
    internal LinuxSealedFileLease LeaseForExternalRead()
    {
        this.UseLock.Wait();
        try
        {
            if (this.Disposed)
                throw new ObjectDisposedException(nameof(VerifiedGitHubAttestationBundle));
            return LinuxSealedFile.LeaseForExternalRead(this.RetainedBundle);
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
            this.RetainedBundle.Dispose();
        }
        finally
        {
            this.UseLock.Release();
        }
    }
}

/// <summary>Creates local attestation authority only from an exact named bundle and exact sidecar checksum.</summary>
public sealed class VerifiedGitHubAttestationBundleFactory
{
    /// <summary>The maximum accepted local bundle size.</summary>
    public const int MaximumBundleBytes = 2 * 1024 * 1024;

    /// <summary>The maximum accepted checksum-sidecar size.</summary>
    public const int MaximumChecksumBytes = 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Action<SafeFileHandle>? AfterRetainedFileCreatedForTesting;

    /// <summary>Create a bundle verifier.</summary>
    public VerifiedGitHubAttestationBundleFactory()
    {
    }

    internal VerifiedGitHubAttestationBundleFactory(Action<SafeFileHandle> afterRetainedFileCreatedForTesting)
    {
        this.AfterRetainedFileCreatedForTesting = afterRetainedFileCreatedForTesting
            ?? throw new ArgumentNullException(nameof(afterRetainedFileCreatedForTesting));
    }

    /// <summary>Get the exact separately published local bundle filename.</summary>
    public static string GetBundleAssetName(ForkReleaseIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return $"SMAPI-{identity.EmbeddedVersion}-linux-x64-attestation-bundle.jsonl";
    }

    /// <summary>Get the exact separately published checksum-sidecar filename.</summary>
    public static string GetChecksumAssetName(ForkReleaseIdentity identity)
    {
        return $"{GetBundleAssetName(identity)}.sha256";
    }

    internal static string GetBundleAssetName(InstallationReleaseIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return $"SMAPI-{identity.EmbeddedVersion}-linux-x64-attestation-bundle.jsonl";
    }

    internal static string GetChecksumAssetName(InstallationReleaseIdentity identity)
    {
        return $"{GetBundleAssetName(identity)}.sha256";
    }

    /// <summary>
    /// Open the exact bundle and checksum sidecar through retained no-follow ordinary-file handles, then publish
    /// only immutable anonymous-file authority bound to the verified installer release.
    /// </summary>
    public async Task<VerifiedGitHubAttestationBundle> VerifyAsync(
        VerifiedInstallerPackage package,
        string bundlePath,
        string checksumPath,
        CancellationToken cancellationToken = default
    )
    {
        LinuxPrivilegeGuard.AssertNotRoot();
        ArgumentNullException.ThrowIfNull(package);
        package.AssertUsable();
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Local attestation-bundle authority is only supported on Linux.");

        InstallationReleaseIdentity release = package.Release;
        string expectedBundleName = GetBundleAssetName(release);
        string expectedChecksumName = GetChecksumAssetName(release);
        AssertExactFilename(bundlePath, expectedBundleName, "attestation bundle");
        AssertExactFilename(checksumPath, expectedChecksumName, "attestation-bundle checksum");

        string checksumDocument;
        using (RetainedReleaseAssetFile checksum = RetainedReleaseAssetFile.Open(checksumPath, "attestation-bundle checksum"))
        {
            checksumDocument = await checksum.ReadUtf8TextAsync(MaximumChecksumBytes, cancellationToken).ConfigureAwait(false);
        }

        string suffix = $"  {expectedBundleName}\n";
        if (
            checksumDocument.Length != 64 + suffix.Length
            || !checksumDocument.EndsWith(suffix, StringComparison.Ordinal)
        )
        {
            throw new PackageSecurityException("The attestation-bundle checksum sidecar isn't canonical.");
        }
        Sha256Digest expectedSha256;
        try
        {
            expectedSha256 = Sha256Digest.Parse(checksumDocument[..64]);
        }
        catch (ArgumentException ex)
        {
            throw new PackageSecurityException("The attestation-bundle checksum sidecar isn't canonical.", ex);
        }

        byte[] bytes;
        using (RetainedReleaseAssetFile bundle = RetainedReleaseAssetFile.Open(bundlePath, "local attestation bundle"))
        {
            bytes = await bundle.ReadAllBytesAsync(MaximumBundleBytes, requireNonEmpty: true, cancellationToken).ConfigureAwait(false);
        }

        SafeFileHandle? retained = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _ = StrictUtf8.GetCharCount(bytes);
            }
            catch (DecoderFallbackException ex)
            {
                throw new PackageSecurityException("The local attestation bundle isn't valid UTF-8.", ex);
            }

            Sha256Digest actualSha256 = Sha256Digest.Hash(bytes);
            if (actualSha256 != expectedSha256)
                throw new PackageSecurityException("The local attestation bundle doesn't match its checksum sidecar.");

            retained = LinuxSealedFile.CreateAnonymous("smapi-installer-attestation-bundle");
            this.AfterRetainedFileCreatedForTesting?.Invoke(retained);
            cancellationToken.ThrowIfCancellationRequested();
            RandomAccess.Write(retained, bytes, 0);
            cancellationToken.ThrowIfCancellationRequested();
            LinuxSealedFile.SealImmutable(retained);
            AssertExactRetainedBytes(retained, bytes.LongLength, actualSha256);
            cancellationToken.ThrowIfCancellationRequested();

            VerifiedGitHubAttestationBundle result = new(
                release,
                expectedBundleName,
                actualSha256,
                bytes.LongLength,
                retained
            );
            retained = null;
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            retained?.Dispose();
        }
    }

    private static void AssertExactFilename(string path, string expectedName, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException($"The {description} path is required.", nameof(path));
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new PackageSecurityException($"The selected {description} path isn't valid.", ex);
        }
        if (!string.Equals(Path.GetFileName(fullPath), expectedName, StringComparison.Ordinal))
            throw new PackageSecurityException($"The selected {description} filename doesn't match its release identity.");
    }

    private static void AssertExactRetainedBytes(SafeFileHandle retained, long expectedSize, Sha256Digest expectedSha256)
    {
        if (RandomAccess.GetLength(retained) != expectedSize)
            throw new PackageSecurityException("The retained local attestation bundle has an unexpected byte length.");
        byte[] bytes = new byte[checked((int)expectedSize)];
        try
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                int count = RandomAccess.Read(retained, bytes.AsSpan(offset), offset);
                if (count <= 0)
                    throw new PackageSecurityException("The retained local attestation bundle ended early.");
                offset += count;
            }
            if (RandomAccess.GetLength(retained) != expectedSize || RandomAccess.Read(retained, bytes.AsSpan(0, 1), expectedSize) != 0)
                throw new PackageSecurityException("The retained local attestation bundle changed while it was verified.");
            if (Sha256Digest.Hash(bytes) != expectedSha256)
                throw new PackageSecurityException("The retained local attestation bundle has an unexpected digest.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
