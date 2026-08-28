using System.Security.Cryptography;
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
    private bool Disposed;

    /// <summary>The exact cross-verified release identity.</summary>
    public InstallationReleaseIdentity Release => this.Manifest.Release;

    /// <summary>The canonical install-manifest digest.</summary>
    public Sha256Digest ManifestSha256 { get; }

    internal VerifiedReleasePackage Package { get; }
    internal PackageManifest Manifest { get; }

    internal VerifiedInstallerPackage(
        VerifiedReleasePackage package,
        PackageManifest manifest,
        Sha256Digest manifestSha256
    )
    {
        this.Package = package;
        this.Manifest = manifest;
        this.ManifestSha256 = manifestSha256;
    }

    internal void AssertUsable()
    {
        if (this.Disposed)
            throw new ObjectDisposedException(nameof(VerifiedInstallerPackage));
        _ = this.Package.GetArtifact(this.Release.PackageAssetName);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (this.Disposed)
            return;
        this.Disposed = true;
        this.Package.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (this.Disposed)
            return;
        this.Disposed = true;
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
        try
        {
            await using FileStream stream = new(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan
            );
            if (stream.Length != expected.SizeBytes || stream.Length <= 0 || stream.Length > limits.MaxDocumentBytes)
                throw new PackageSecurityException("The selected install manifest doesn't match its verified size.");

            bytes = new byte[checked((int)stream.Length)];
            int total = 0;
            while (total < bytes.Length)
            {
                int read = await stream.ReadAsync(bytes.AsMemory(total), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    throw new PackageSecurityException("The selected install manifest ended before its verified size.");
                total += read;
            }
            if (await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0 || stream.Length != expected.SizeBytes)
                throw new PackageSecurityException("The selected install manifest changed while it was being read.");
        }
        catch (FileNotFoundException ex)
        {
            throw new PackageSecurityException("The selected install manifest doesn't exist.", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new PackageSecurityException("The selected install-manifest directory doesn't exist.", ex);
        }

        string actualSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(actualSha256, expected.Sha256, StringComparison.Ordinal))
            throw new PackageSecurityException("The selected install manifest doesn't match SHA256SUMS and build-metadata.json.");

        PackageManifest manifest;
        try
        {
            manifest = CanonicalOwnershipDocuments.ParseManifest(bytes, limits);
        }
        catch (OwnershipDocumentException ex)
        {
            throw new PackageSecurityException("The verified install manifest isn't canonical or valid.", ex);
        }

        if (!manifest.Release.Equals(package.InstallationIdentity))
            throw new PackageSecurityException("The verified install manifest names a different release package.");

        return new VerifiedInstallerPackage(package, manifest, Sha256Digest.Parse(actualSha256));
    }
}
