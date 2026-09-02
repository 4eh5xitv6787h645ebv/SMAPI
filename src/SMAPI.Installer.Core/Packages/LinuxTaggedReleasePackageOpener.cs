using StardewModdingAPI.Installer.Core.Ownership;

namespace StardewModdingAPI.Installer.Core.Packages;

/// <summary>The complete local asset set required to open one attested Linux tagged release.</summary>
public sealed record LinuxTaggedReleaseAssetSet(
    string ReleaseTag,
    string ExpectedSourceCommit,
    string PackagePath,
    string ChecksumsPath,
    string BuildMetadataPath,
    string InstallManifestPath,
    string AttestationBundlePath,
    string AttestationBundleChecksumPath,
    string GitHubCliPath
);

/// <summary>
/// Opens a Linux tagged release only after the release quartet, schema-4 manifest, local attestation bundle,
/// and pinned GitHub verifier establish one closed package authority.
/// </summary>
public sealed class LinuxTaggedReleasePackageOpener
{
    private readonly Func<string, CancellationToken, Task<PinnedGitHubCli>> OpenGitHubCli;
    private readonly IGitHubAttestationProcessRunner AttestationRunner;

    /// <summary>Create the production tagged-release opener with the exact bundled GitHub CLI pin.</summary>
    public LinuxTaggedReleasePackageOpener()
        : this(PinnedGitHubCli.OpenAsync, new GitHubAttestationProcessRunner())
    {
    }

    internal LinuxTaggedReleasePackageOpener(
        Func<string, CancellationToken, Task<PinnedGitHubCli>> openGitHubCli,
        IGitHubAttestationProcessRunner attestationRunner
    )
    {
        this.OpenGitHubCli = openGitHubCli ?? throw new ArgumentNullException(nameof(openGitHubCli));
        this.AttestationRunner = attestationRunner ?? throw new ArgumentNullException(nameof(attestationRunner));
    }

    /// <summary>Verify and extract one exact tagged Linux release. The caller owns the returned handle.</summary>
    public async Task<VerifiedPackageContent> OpenAsync(
        LinuxTaggedReleaseAssetSet assets,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(assets);
        ForkReleaseIdentity identity = Classify(
            () => ForkReleaseIdentity.Parse(assets.ReleaseTag),
            PackageSecurityFailureKind.ReleaseIdentityRejected
        );
        VerifiedReleasePackage? release = await new ReleasePackageVerifier().VerifyFilesAsync(
            assets.PackagePath,
            assets.ChecksumsPath,
            assets.BuildMetadataPath,
            identity,
            assets.ExpectedSourceCommit,
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);
        try
        {
            VerifiedInstallerPackage? installer = await new VerifiedInstallerPackageFactory().VerifyAsync(
                release,
                assets.InstallManifestPath,
                cancellationToken: cancellationToken
            ).ConfigureAwait(false);
            release = null;
            try
            {
                using VerifiedGitHubAttestationBundle bundle = await new VerifiedGitHubAttestationBundleFactory().VerifyAsync(
                    installer,
                    assets.AttestationBundlePath,
                    assets.AttestationBundleChecksumPath,
                    cancellationToken
                ).ConfigureAwait(false);
                using PinnedGitHubCli cli = await this.OpenGitHubCli(assets.GitHubCliPath, cancellationToken).ConfigureAwait(false);
                VerifiedTaggedPackageTrust trust = await new GitHubArtifactAttestationVerifier(
                    this.AttestationRunner
                ).VerifyAsync(installer, bundle, cli, cancellationToken).ConfigureAwait(false);
                installer.BindTrust(trust);

                VerifiedPackageContent result = await new VerifiedPackageContentFactory().ExtractAsync(
                    installer,
                    cancellationToken: cancellationToken
                ).ConfigureAwait(false);
                installer = null;
                return result;
            }
            finally
            {
                installer?.Dispose();
            }
        }
        finally
        {
            release?.Dispose();
        }
    }

    internal async Task<VerifiedPackageContent> OpenAsync(
        LinuxTaggedReleaseAssetSet assets,
        IRetainedReleaseAssetSource source,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(source);
        ForkReleaseIdentity identity = Classify(
            () => ForkReleaseIdentity.Parse(assets.ReleaseTag),
            PackageSecurityFailureKind.ReleaseIdentityRejected
        );
        VerifiedReleasePackage? release = await new ReleasePackageVerifier().VerifyFilesAsync(
            source,
            identity,
            assets.ExpectedSourceCommit,
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);
        try
        {
            VerifiedInstallerPackage? installer = await new VerifiedInstallerPackageFactory().VerifyAsync(
                release,
                source,
                cancellationToken: cancellationToken
            ).ConfigureAwait(false);
            release = null;
            try
            {
                using VerifiedGitHubAttestationBundle bundle = await new VerifiedGitHubAttestationBundleFactory().VerifyAsync(
                    installer,
                    source,
                    cancellationToken
                ).ConfigureAwait(false);
                using PinnedGitHubCli cli = await this.OpenGitHubCli(assets.GitHubCliPath, cancellationToken).ConfigureAwait(false);
                VerifiedTaggedPackageTrust trust = await new GitHubArtifactAttestationVerifier(
                    this.AttestationRunner
                ).VerifyAsync(installer, bundle, cli, cancellationToken).ConfigureAwait(false);
                installer.BindTrust(trust);

                VerifiedPackageContent result = await new VerifiedPackageContentFactory().ExtractAsync(
                    installer,
                    cancellationToken: cancellationToken
                ).ConfigureAwait(false);
                installer = null;
                return result;
            }
            finally
            {
                installer?.Dispose();
            }
        }
        finally
        {
            release?.Dispose();
        }
    }

    private static T Classify<T>(
        Func<T> action,
        PackageSecurityFailureKind failureKind
    )
    {
        try
        {
            return action();
        }
        catch (PackageSecurityException ex) when (ex.FailureKind == PackageSecurityFailureKind.Unclassified)
        {
            throw new PackageSecurityException(failureKind, ex.Message, ex);
        }
    }

}
