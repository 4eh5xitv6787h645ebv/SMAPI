using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Ownership.Persistence;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Packages;

/// <summary>
/// Opaque authority for an exact release package and its independently checksummed canonical install manifest.
/// Callers can inspect identity, but can't construct or substitute the trusted package rules.
/// </summary>
public sealed class VerifiedInstallerPackage : IDisposable, IAsyncDisposable
{
    private readonly SafeFileHandle? RetainedManifest;
    private VerifiedTaggedPackageTrust? ReleaseTrust;
    private int Disposed;

    /// <summary>The exact cross-verified release identity.</summary>
    public InstallationReleaseIdentity Release => this.Manifest.Release;

    /// <summary>The canonical install-manifest digest.</summary>
    public Sha256Digest ManifestSha256 { get; }

    /// <summary>The exact checksummed install-manifest asset name.</summary>
    internal string ManifestAssetName { get; }

    /// <summary>The exact checksummed install-manifest byte length.</summary>
    internal long ManifestSizeBytes { get; }

    internal VerifiedReleasePackage Package { get; }
    internal PackageManifest Manifest { get; }

    internal VerifiedTaggedPackageTrust? Trust => Volatile.Read(ref this.ReleaseTrust);

    internal VerifiedInstallerPackage(
        VerifiedReleasePackage package,
        PackageManifest manifest,
        Sha256Digest manifestSha256,
        string manifestAssetName,
        long manifestSizeBytes,
        SafeFileHandle? retainedManifest
    )
    {
        this.Package = package ?? throw new ArgumentNullException(nameof(package));
        this.Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        this.ManifestSha256 = manifestSha256 ?? throw new ArgumentNullException(nameof(manifestSha256));
        if (string.IsNullOrWhiteSpace(manifestAssetName))
            throw new ArgumentException("The exact install-manifest asset name is required.", nameof(manifestAssetName));
        if (manifestSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(manifestSizeBytes));
        if (OperatingSystem.IsLinux() != (retainedManifest is not null))
            throw new ArgumentException("Linux installer authority requires one retained immutable manifest descriptor.", nameof(retainedManifest));
        if (retainedManifest is not null)
        {
            using LinuxSealedFileLease _ = LinuxSealedFile.LeaseForExternalRead(retainedManifest);
        }
        this.ManifestAssetName = manifestAssetName;
        this.ManifestSizeBytes = manifestSizeBytes;
        this.RetainedManifest = retainedManifest;
    }

    internal void AssertUsable()
    {
        if (Volatile.Read(ref this.Disposed) != 0)
            throw new ObjectDisposedException(nameof(VerifiedInstallerPackage));
        _ = this.Package.GetArtifact(this.Release.PackageAssetName);
    }

    internal void BindTrust(VerifiedTaggedPackageTrust trust)
    {
        this.AssertUsable();
        ArgumentNullException.ThrowIfNull(trust);
        if (this.Manifest.SchemaVersion != PackageManifest.CurrentSchemaVersion || this.Manifest.ReleaseAuthorityPolicy is null)
            throw new PackageSecurityException("Tagged release authority requires a schema-4 install manifest.");
        if (!trust.Identity.Equals(this.Release) || !this.Manifest.ReleaseAuthorityPolicy.Matches(trust))
            throw new PackageSecurityException("Verified release evidence doesn't match the exact manifest authority policy.");
        if (trust.ManifestSubject.Sha256 != this.ManifestSha256 || trust.ManifestSubject.ObservedSizeBytes != this.ManifestSizeBytes)
            throw new PackageSecurityException("Verified release evidence doesn't bind the exact retained install manifest.");
        if (Interlocked.CompareExchange(ref this.ReleaseTrust, trust, null) is not null)
            throw new InvalidOperationException("Release trust was already bound to this package authority.");
    }

    /// <summary>Lease the exact immutable manifest descriptor for an external verifier.</summary>
    internal LinuxSealedFileLease LeaseManifestForExternalRead()
    {
        this.AssertUsable();
        SafeFileHandle handle = this.RetainedManifest
            ?? throw new PlatformNotSupportedException("External manifest descriptor leases are only supported on Linux.");
        return LinuxSealedFile.LeaseForExternalRead(handle);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref this.Disposed, 1) != 0)
            return;
        this.RetainedManifest?.Dispose();
        this.Package.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.Disposed, 1) != 0)
            return;
        this.RetainedManifest?.Dispose();
        await this.Package.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Creates installer-package authority only from release-metadata-bound companion bytes.</summary>
public sealed class VerifiedInstallerPackageFactory
{
    /// <summary>Get the exact manifest asset name for a selected release.</summary>
    public static string GetManifestAssetName(ForkReleaseIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return $"SMAPI-{identity.EmbeddedVersion}-linux-x64-install-manifest.json";
    }

    /// <summary>
    /// Verify and parse the companion manifest through one retained read handle. On success ownership of
    /// <paramref name="package"/> transfers to the returned authority.
    /// </summary>
    public async Task<VerifiedInstallerPackage> VerifyAsync(
        VerifiedReleasePackage package,
        string manifestPath,
        OwnershipPersistenceLimits? limits = null,
        CancellationToken cancellationToken = default
    )
    {
        LinuxPrivilegeGuard.AssertNotRoot();
        ArgumentNullException.ThrowIfNull(package);
        if (string.IsNullOrWhiteSpace(manifestPath))
            throw new ArgumentException("The install-manifest path is required.", nameof(manifestPath));

        limits ??= OwnershipPersistenceLimits.Default;
        string expectedName = VerifiedInstallerPackageFactory.GetManifestAssetName(package.Identity);
        VerifiedReleaseArtifactIdentity expected = package.GetArtifact(expectedName);
        if (expected.SizeBytes > limits.MaxDocumentBytes)
            throw new PackageSecurityException("The verified install manifest exceeds its configured size limit.");

        string fullPath = Path.GetFullPath(manifestPath);
        if (!string.Equals(Path.GetFileName(fullPath), expectedName, StringComparison.Ordinal))
            throw new PackageSecurityException("The selected install-manifest filename doesn't match its release identity.");

        byte[] bytes;
        using (RetainedReleaseAssetFile file = RetainedReleaseAssetFile.Open(fullPath, "install manifest"))
        {
            if (file.Size != expected.SizeBytes || file.Size <= 0 || file.Size > limits.MaxDocumentBytes)
                throw new PackageSecurityException("The selected install manifest doesn't match its verified size.");
            bytes = await file.ReadAllBytesAsync(limits.MaxDocumentBytes, requireNonEmpty: true, cancellationToken).ConfigureAwait(false);
        }

        string actualSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(actualSha256, expected.Sha256, StringComparison.Ordinal))
            throw new PackageSecurityException("The selected install manifest doesn't match SHA256SUMS and build-metadata.json.");

        SafeFileHandle? retainedManifest = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OperatingSystem.IsLinux())
            {
                retainedManifest = LinuxSealedFile.CreateAnonymous("smapi-installer-verified-manifest");
                RandomAccess.Write(retainedManifest, bytes, 0);
                cancellationToken.ThrowIfCancellationRequested();
                LinuxSealedFile.SealImmutable(retainedManifest);
                AssertRetainedManifest(retainedManifest, bytes.LongLength, actualSha256, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }

            PackageManifest manifest = CanonicalOwnershipDocuments.ParseManifest(bytes, limits);
            if (!manifest.Release.Equals(package.InstallationIdentity))
                throw new PackageSecurityException("The verified install manifest names a different release package.");

            VerifiedInstallerPackage result = new(
                package,
                manifest,
                Sha256Digest.Parse(actualSha256),
                expectedName,
                bytes.LongLength,
                retainedManifest
            );
            retainedManifest = null;
            return result;
        }
        catch (OwnershipDocumentException ex)
        {
            throw new PackageSecurityException("The verified install manifest isn't canonical or valid.", ex);
        }
        finally
        {
            retainedManifest?.Dispose();
        }
    }

    private static void AssertRetainedManifest(
        SafeFileHandle retainedManifest,
        long expectedSize,
        string expectedSha256,
        CancellationToken cancellationToken
    )
    {
        if (RandomAccess.GetLength(retainedManifest) != expectedSize)
            throw new PackageSecurityException("The retained install manifest has an unexpected byte length.");

        using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[Math.Min(128 * 1024, checked((int)expectedSize))];
        long offset = 0;
        while (offset < expectedSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = RandomAccess.Read(
                retainedManifest,
                buffer.AsSpan(0, (int)Math.Min(buffer.Length, expectedSize - offset)),
                offset
            );
            if (count <= 0)
                throw new PackageSecurityException("The retained install manifest ended before its verified byte length.");
            hasher.AppendData(buffer, 0, count);
            offset = checked(offset + count);
        }

        if (RandomAccess.GetLength(retainedManifest) != expectedSize || RandomAccess.Read(retainedManifest, buffer.AsSpan(0, 1), expectedSize) != 0)
            throw new PackageSecurityException("The retained install manifest changed while it was verified.");
        string actualSha256 = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal))
            throw new PackageSecurityException("The retained install manifest doesn't match its verified digest.");
    }
}
