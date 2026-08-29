using StardewModdingAPI.Installer.Core.Ownership;

namespace StardewModdingAPI.Installer.Core.Tests.Ownership;

internal static class OwnershipTestData
{
    public static NormalizedRelativePath Path(string value) => NormalizedRelativePath.Parse(value);

    public static Sha256Digest Digest(char value) => Sha256Digest.Parse(new string(value, 64));

    public static InstallationReleaseIdentity Release(int alpha = 1, char packageHash = 'a')
    {
        string version = $"4.5.{alpha + 2}";
        return new InstallationReleaseIdentity(
            InstallationReleaseIdentity.ReviewedRepository,
            $"fork-4eh5xitv6787h645ebv-linux-v{version}-alpha.{alpha}",
            $"{version}-unofficial.4eh5xitv6787h645ebv.linux.alpha.{alpha}",
            $"SMAPI-{version}-unofficial.4eh5xitv6787h645ebv.linux.alpha.{alpha}-linux-x64-installer.zip",
            new string('b', 40),
            new string('c', 40),
            Digest(packageHash),
            123456,
            $"4eh5xitv6787h645ebv/SMAPI/.github/workflows/linux-alpha-release.yml@refs/tags/fork-4eh5xitv6787h645ebv-linux-v{version}-alpha.{alpha}",
            "Release",
            "linux-x64"
        );
    }

    public static PackageManifestEntry Entry(
        string path,
        char digest,
        OwnedEntryKind kind,
        int mode = 420,
        long size = 10
    ) => new(Path(path), Digest(digest), size, mode, kind);

    public static PackageManifest Manifest(
        InstallationReleaseIdentity? release = null,
        char launcherDigest = '1',
        params PackageManifestEntry[] otherEntries
    )
    {
        return new PackageManifest(
            release ?? Release(),
            otherEntries.Append(Entry("StardewValley", launcherDigest, OwnedEntryKind.Launcher, mode: 493))
        );
    }

    public static InstallationReceipt Receipt(PackageManifest manifest, char originalLauncherDigest = 'f')
    {
        PackageManifestEntry launcher = manifest.Entries.Single(entry => entry.Kind == OwnedEntryKind.Launcher);
        return new InstallationReceipt(
            manifest.Release,
            manifest.GetCanonicalDigest(),
            new string('d', 32),
            manifest.Entries.Select(entry => new InstallationReceiptEntry(entry.Path, entry.Sha256, entry.UnixMode, entry.Kind)),
            new LauncherReceipt(launcher.Sha256, Digest(originalLauncherDigest))
        );
    }

    public static PackageManifest AuthorityManifest(InstallationReleaseIdentity? release = null)
    {
        InstallationReleaseIdentity actualRelease = release ?? Release();
        return new PackageManifest(
            actualRelease,
            [Entry("StardewValley", '1', OwnedEntryKind.Launcher, mode: 493)],
            [GeneratedFileRecipe.CreateCopyGameDepsTemplate()],
            PackageManifest.CurrentSchemaVersion,
            TaggedReleaseAuthorityPolicy.Create(actualRelease)
        );
    }

    public static VerifiedTaggedPackageTrust Trust(PackageManifest manifest)
    {
        (Sha256Digest manifestSha256, long manifestSizeBytes) = manifest.GetAttestedTemplateIdentity();
        InstallationReleaseIdentity release = manifest.Release;
        VerifiedGitHubWorkflowEvidence evidence = new(
            release,
            release.Repository,
            $"refs/tags/{release.Tag}",
            release.SourceCommit,
            release.BuildWorkflow,
            $"https://github.com/{release.BuildWorkflow}",
            $"https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/123456/attempts/2",
            VerifiedGitHubWorkflowEvidence.RequiredRunnerEnvironment,
            VerifiedGitHubWorkflowEvidence.RequiredTrigger,
            VerifiedGitHubWorkflowEvidence.ReviewedRepositoryIdentifier,
            VerifiedGitHubWorkflowEvidence.ReviewedRepositoryOwnerIdentifier,
            new DateTimeOffset(2026, 8, 29, 1, 2, 3, TimeSpan.Zero)
        );
        return new VerifiedTaggedPackageTrust(
            release,
            new VerifiedAttestedSubject(release.PackageAssetName, release.PackageSha256, release.PackageSizeBytes),
            new VerifiedAttestedSubject($"SMAPI-{release.EmbeddedVersion}-linux-x64-install-manifest.json", manifestSha256, manifestSizeBytes),
            manifestSha256,
            manifestSizeBytes,
            evidence
        );
    }

    public static InstallationReceipt AuthorityReceipt(PackageManifest manifest)
    {
        PackageManifestEntry launcher = manifest.Entries.Single(entry => entry.Kind == OwnedEntryKind.Launcher);
        return new InstallationReceipt(
            manifest.Release,
            manifest.GetCanonicalDigest(),
            new string('d', 32),
            manifest.Entries.Select(entry => new InstallationReceiptEntry(entry.Path, entry.Sha256, entry.UnixMode, entry.Kind)),
            new LauncherReceipt(launcher.Sha256, Digest('f')),
            Trust(manifest),
            InstallationReceipt.CurrentSchemaVersion
        );
    }

    public static CurrentFile Current(PackageManifestEntry entry, char? digest = null, int? mode = null)
    {
        return new CurrentFile(entry.Path, digest.HasValue ? Digest(digest.Value) : entry.Sha256, mode ?? entry.UnixMode);
    }
}
