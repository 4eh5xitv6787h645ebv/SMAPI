using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Packages;

namespace StardewModdingAPI.Installer.Core.Tests.Packages;

[TestFixture]
[Platform("Linux")]
[NonParallelizable]
[SupportedOSPlatform("linux")]
internal sealed class LinuxTaggedReleasePackageOpenerTests
{
    private const string Tag = "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1";
    private const string Commit = "1111111111111111111111111111111111111111";
    private const string Tree = "2222222222222222222222222222222222222222";
    private static readonly byte[] PackageBytes = "synthetic package bytes that are deliberately not a ZIP"u8.ToArray();
    private static readonly byte[] LauncherBytes = "#!/bin/sh\nexec smapi\n"u8.ToArray();

    private string TempRoot = null!;
    private string GameRoot = null!;
    private string GameSentinel = null!;
    private ForkReleaseIdentity Identity = null!;

    [SetUp]
    public void SetUp()
    {
        this.TempRoot = Path.Combine(Path.GetTempPath(), $"smapi-tagged-opener-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.TempRoot);
        this.GameRoot = Path.Combine(this.TempRoot, "unrelated-game");
        Directory.CreateDirectory(this.GameRoot);
        this.GameSentinel = Path.Combine(this.GameRoot, "StardewValley");
        File.WriteAllBytes(this.GameSentinel, "untouched game launcher"u8.ToArray());
        File.SetUnixFileMode(this.GameSentinel, (UnixFileMode)493);
        this.Identity = ForkReleaseIdentity.Parse(Tag);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(this.TempRoot))
            Directory.Delete(this.TempRoot, recursive: true);
    }

    [Test]
    public async Task OpenAsync_NonForkTagIsReleaseIdentityRejectedBeforeAnyAssetOrGameAccess()
    {
        LinuxTaggedReleaseAssetSet assets = new(
            "4.5.2",
            Commit,
            Path.Combine(this.TempRoot, "missing-package"),
            Path.Combine(this.TempRoot, "missing-checksums"),
            Path.Combine(this.TempRoot, "missing-metadata"),
            Path.Combine(this.TempRoot, "missing-manifest"),
            Path.Combine(this.TempRoot, "missing-bundle"),
            Path.Combine(this.TempRoot, "missing-bundle-checksum"),
            Path.Combine(this.TempRoot, "missing-gh")
        );
        HashSet<string> descriptorsBefore = FindRetainedPackageDescriptors();

        PackageSecurityException exception = await CaptureFailureAsync(assets);

        exception.FailureKind.Should().Be(PackageSecurityFailureKind.ReleaseIdentityRejected);
        this.AssertNoGameMutationOrRetainedAuthority(descriptorsBefore, exception);
    }

    [Test]
    public async Task OpenAsync_PackageDigestMismatchIsIntegrityRejectedBeforeManifestOrGameAccess()
    {
        LinuxTaggedReleaseAssetSet assets = this.CreateAssets(packageDigestMismatch: true);
        File.Delete(assets.InstallManifestPath);
        HashSet<string> descriptorsBefore = FindRetainedPackageDescriptors();

        PackageSecurityException exception = await CaptureFailureAsync(assets);

        exception.FailureKind.Should().Be(PackageSecurityFailureKind.IntegrityRejected);
        this.AssertNoGameMutationOrRetainedAuthority(descriptorsBefore, exception);
    }

    [Test]
    public async Task OpenAsync_MalformedBuildMetadataIsMetadataRejectedBeforeManifestOrGameAccess()
    {
        LinuxTaggedReleaseAssetSet assets = this.CreateAssets();
        File.WriteAllText(assets.BuildMetadataPath, "{not-json", new UTF8Encoding(false));
        File.Delete(assets.InstallManifestPath);
        HashSet<string> descriptorsBefore = FindRetainedPackageDescriptors();

        PackageSecurityException exception = await CaptureFailureAsync(assets);

        exception.FailureKind.Should().Be(PackageSecurityFailureKind.MetadataRejected);
        this.AssertNoGameMutationOrRetainedAuthority(descriptorsBefore, exception);
    }

    [Test]
    public async Task OpenAsync_InvalidChecksummedManifestIsMetadataRejectedBeforeAttestationOrGameAccess()
    {
        LinuxTaggedReleaseAssetSet assets = this.CreateAssets(invalidManifest: true);
        File.Delete(assets.AttestationBundlePath);
        HashSet<string> descriptorsBefore = FindRetainedPackageDescriptors();

        PackageSecurityException exception = await CaptureFailureAsync(assets);

        exception.FailureKind.Should().Be(PackageSecurityFailureKind.MetadataRejected);
        this.AssertNoGameMutationOrRetainedAuthority(descriptorsBefore, exception);
    }

    [Test]
    public async Task OpenAsync_MissingManifestAuthorityRemainsUnclassifiedInsteadOfClaimingMetadataRejection()
    {
        LinuxTaggedReleaseAssetSet assets = this.CreateAssets();
        File.Delete(assets.InstallManifestPath);
        HashSet<string> descriptorsBefore = FindRetainedPackageDescriptors();

        PackageSecurityException exception = await CaptureFailureAsync(assets);

        exception.FailureKind.Should().Be(PackageSecurityFailureKind.Unclassified);
        this.AssertNoGameMutationOrRetainedAuthority(descriptorsBefore, exception);
    }

    [Test]
    public async Task OpenAsync_AttestationBundleDigestMismatchIsIntegrityRejectedBeforeVerifierOrGameAccess()
    {
        LinuxTaggedReleaseAssetSet assets = this.CreateAssets(attestationDigestMismatch: true);
        File.Delete(assets.GitHubCliPath);
        HashSet<string> descriptorsBefore = FindRetainedPackageDescriptors();

        PackageSecurityException exception = await CaptureFailureAsync(assets);

        exception.FailureKind.Should().Be(PackageSecurityFailureKind.IntegrityRejected);
        this.AssertNoGameMutationOrRetainedAuthority(descriptorsBefore, exception);
    }

    [Test]
    public async Task OpenAsync_MissingBundleAuthorityRemainsUnclassifiedInsteadOfClaimingProvenanceRejection()
    {
        LinuxTaggedReleaseAssetSet assets = this.CreateAssets();
        File.Delete(assets.AttestationBundlePath);
        HashSet<string> descriptorsBefore = FindRetainedPackageDescriptors();

        PackageSecurityException exception = await CaptureFailureAsync(assets);

        exception.FailureKind.Should().Be(PackageSecurityFailureKind.Unclassified);
        this.AssertNoGameMutationOrRetainedAuthority(descriptorsBefore, exception);
    }

    [Test]
    public async Task OpenAsync_UntrustedVerifierBinaryRemainsUnclassifiedInsteadOfClaimingProvenanceRejection()
    {
        LinuxTaggedReleaseAssetSet assets = this.CreateAssets();
        HashSet<string> descriptorsBefore = FindRetainedPackageDescriptors();

        PackageSecurityException exception = await CaptureFailureAsync(assets);

        exception.FailureKind.Should().Be(PackageSecurityFailureKind.Unclassified);
        exception.Message.Should().Contain("pinned byte length");
        this.AssertNoGameMutationOrRetainedAuthority(descriptorsBefore, exception);
    }

    [Test]
    public async Task OpenAsync_ProductionVerifierParserRejectsInvalidAcceptedOutputAsProvenance()
    {
        LinuxTaggedReleaseAssetSet assets = this.CreateAssets();
        LinuxTaggedReleasePackageOpener opener = CreateProcessBackedOpener(assets, "{");
        HashSet<string> descriptorsBefore = FindRetainedPackageDescriptors();

        Func<Task> open = async () => await opener.OpenAsync(assets);
        PackageSecurityException exception = (await open.Should().ThrowAsync<PackageSecurityException>()).Which;

        exception.FailureKind.Should().Be(PackageSecurityFailureKind.ProvenanceRejected);
        this.AssertNoGameMutationOrRetainedAuthority(descriptorsBefore, exception);
    }

    [Test]
    public async Task OpenAsync_ProductionProcessAndVerifierReachArchiveRejectionAfterValidTrust()
    {
        LinuxTaggedReleaseAssetSet assets = this.CreateAssets();
        string validEvidence = GitHubArtifactAttestationVerifierTests.WriteJson(
            new GitHubArtifactAttestationVerifierTests.FixtureOptions
            {
                PackageSubjectSha256 = Hash(PackageBytes),
                ManifestSubjectSha256 = Hash(File.ReadAllBytes(assets.InstallManifestPath))
            }
        );
        LinuxTaggedReleasePackageOpener opener = CreateProcessBackedOpener(assets, validEvidence);
        HashSet<string> descriptorsBefore = FindRetainedPackageDescriptors();

        Func<Task> open = async () => await opener.OpenAsync(assets);
        PackageSecurityException exception = (await open.Should().ThrowAsync<PackageSecurityException>()).Which;

        exception.FailureKind.Should().Be(PackageSecurityFailureKind.PackageArchiveRejected);
        this.AssertNoGameMutationOrRetainedAuthority(descriptorsBefore, exception);
    }

    [Test]
    public async Task PackageArchiveBoundary_RealExtractionFailureIsClassifiedWithoutPublishingContentOrMutatingGame()
    {
        LinuxTaggedReleaseAssetSet assets = this.CreateAssets();
        HashSet<string> descriptorsBefore = FindRetainedPackageDescriptors();
        VerifiedReleasePackage? release = await new ReleasePackageVerifier().VerifyFilesAsync(
            assets.PackagePath,
            assets.ChecksumsPath,
            assets.BuildMetadataPath,
            this.Identity,
            Commit
        );
        VerifiedInstallerPackage? installer = null;
        try
        {
            installer = await new VerifiedInstallerPackageFactory().VerifyAsync(release, assets.InstallManifestPath);
            release = null;
            installer.BindTrust(
                global::StardewModdingAPI.Installer.Core.Tests.Ownership.OwnershipTestData.Trust(installer.Manifest)
            );

            Func<Task> extraction = async () => await new VerifiedPackageContentFactory().ExtractAsync(installer);

            PackageSecurityException exception = (await extraction.Should().ThrowAsync<PackageSecurityException>()).Which;
            exception.FailureKind.Should().Be(PackageSecurityFailureKind.PackageArchiveRejected);
            exception.InnerException.Should().BeOfType<InvalidDataException>();
            this.AssertGameSentinelUnchanged(exception);
        }
        finally
        {
            installer?.Dispose();
            release?.Dispose();
        }

        FindRetainedPackageDescriptors().Should().BeEquivalentTo(descriptorsBefore);
    }

    private LinuxTaggedReleaseAssetSet CreateAssets(
        bool packageDigestMismatch = false,
        bool invalidManifest = false,
        bool attestationDigestMismatch = false
    )
    {
        string packagePath = Path.Combine(this.TempRoot, this.Identity.PackageAssetName);
        File.WriteAllBytes(packagePath, PackageBytes);
        string actualPackageHash = Hash(PackageBytes);
        string declaredPackageHash = packageDigestMismatch ? new string('0', 64) : actualPackageHash;

        InstallationReleaseIdentity release = new(
            InstallationReleaseIdentity.ReviewedRepository,
            this.Identity.Tag,
            this.Identity.EmbeddedVersion,
            this.Identity.PackageAssetName,
            Commit,
            Tree,
            Sha256Digest.Parse(actualPackageHash),
            PackageBytes.LongLength,
            $"{ForkReleaseIdentity.Repository}/.github/workflows/linux-alpha-release.yml@refs/tags/{this.Identity.Tag}",
            "Release",
            "linux-x64"
        );
        PackageManifest manifest = new(
            release,
            [
                new PackageManifestEntry(
                    NormalizedRelativePath.Parse("StardewValley"),
                    Sha256Digest.Hash(LauncherBytes),
                    LauncherBytes.LongLength,
                    493,
                    OwnedEntryKind.Launcher
                )
            ],
            schemaVersion: PackageManifest.CurrentSchemaVersion,
            releaseAuthorityPolicy: TaggedReleaseAuthorityPolicy.Create(release)
        );
        byte[] manifestBytes = invalidManifest
            ? "not a canonical install manifest"u8.ToArray()
            : Encoding.UTF8.GetBytes(manifest.ToCanonicalJson());
        string manifestName = VerifiedInstallerPackageFactory.GetManifestAssetName(this.Identity);
        string manifestPath = Path.Combine(this.TempRoot, manifestName);
        File.WriteAllBytes(manifestPath, manifestBytes);
        string manifestHash = Hash(manifestBytes);

        string checksumsPath = Path.Combine(this.TempRoot, ReleasePackageVerifier.ChecksumAssetName);
        File.WriteAllText(
            checksumsPath,
            $"{declaredPackageHash}  {this.Identity.PackageAssetName}\n{manifestHash}  {manifestName}\n",
            new UTF8Encoding(false)
        );
        string metadataPath = Path.Combine(this.TempRoot, ReleasePackageVerifier.BuildMetadataAssetName);
        File.WriteAllText(
            metadataPath,
            this.CreateMetadata(declaredPackageHash, manifestName, manifestBytes.LongLength, manifestHash),
            new UTF8Encoding(false)
        );

        byte[] bundleBytes = "synthetic local public attestation evidence"u8.ToArray();
        string bundleName = VerifiedGitHubAttestationBundleFactory.GetBundleAssetName(release);
        string bundlePath = Path.Combine(this.TempRoot, bundleName);
        File.WriteAllBytes(bundlePath, bundleBytes);
        string bundleChecksumName = VerifiedGitHubAttestationBundleFactory.GetChecksumAssetName(release);
        string bundleChecksumPath = Path.Combine(this.TempRoot, bundleChecksumName);
        string bundleHash = attestationDigestMismatch ? new string('0', 64) : Hash(bundleBytes);
        File.WriteAllText(bundleChecksumPath, $"{bundleHash}  {bundleName}\n", new UTF8Encoding(false));

        string ghPath = Path.Combine(this.TempRoot, PinnedGitHubCli.ExecutableFilename);
        File.WriteAllText(ghPath, "#!/bin/sh\nexit 23\n", new UTF8Encoding(false));
        File.SetUnixFileMode(ghPath, (UnixFileMode)493);

        return new LinuxTaggedReleaseAssetSet(
            this.Identity.Tag,
            Commit,
            packagePath,
            checksumsPath,
            metadataPath,
            manifestPath,
            bundlePath,
            bundleChecksumPath,
            ghPath
        );
    }

    private string CreateMetadata(string packageHash, string manifestName, long manifestSize, string manifestHash)
    {
        return JsonSerializer.Serialize(
            new
            {
                schema_version = 1,
                release = new { version = this.Identity.EmbeddedVersion, tag = this.Identity.Tag },
                source = new { repository = ForkReleaseIdentity.RepositoryUrl, commit = Commit, tree = Tree },
                build = new
                {
                    workflow = $"{ForkReleaseIdentity.Repository}/.github/workflows/linux-alpha-release.yml@refs/tags/{this.Identity.Tag}",
                    configuration = "Release",
                    runtime_identifier = "linux-x64"
                },
                artifacts = new object[]
                {
                    new { name = this.Identity.PackageAssetName, size_bytes = PackageBytes.LongLength, sha256 = packageHash },
                    new { name = manifestName, size_bytes = manifestSize, sha256 = manifestHash }
                }
            }
        );
    }

    private static async Task<PackageSecurityException> CaptureFailureAsync(LinuxTaggedReleaseAssetSet assets)
    {
        Func<Task> open = async () => await new LinuxTaggedReleasePackageOpener().OpenAsync(assets);
        return (await open.Should().ThrowAsync<PackageSecurityException>()).Which;
    }

    private static LinuxTaggedReleasePackageOpener CreateProcessBackedOpener(
        LinuxTaggedReleaseAssetSet assets,
        string verifierOutput
    )
    {
        string encodedOutput = Convert.ToBase64String(Encoding.UTF8.GetBytes(verifierOutput));
        byte[] script = Encoding.UTF8.GetBytes(
            $"#!/bin/sh\n/usr/bin/printf '%s' '{encodedOutput}' | /usr/bin/base64 -d\n"
        );
        File.WriteAllBytes(assets.GitHubCliPath, script);
        File.SetUnixFileMode(assets.GitHubCliPath, (UnixFileMode)493);
        PinnedGitHubCliTestIdentity identity = new(script.LongLength, Hash(script));
        return new LinuxTaggedReleasePackageOpener(
            (path, cancellationToken) => PinnedGitHubCli.OpenForTestingAsync(path, identity, cancellationToken),
            new GitHubAttestationProcessRunner()
        );
    }

    private void AssertNoGameMutationOrRetainedAuthority(
        HashSet<string> descriptorsBefore,
        PackageSecurityException exception
    )
    {
        this.AssertGameSentinelUnchanged(exception);
        FindRetainedPackageDescriptors().Should().BeEquivalentTo(descriptorsBefore);
    }

    private void AssertGameSentinelUnchanged(PackageSecurityException exception)
    {
        File.ReadAllBytes(this.GameSentinel).Should().Equal("untouched game launcher"u8.ToArray());
        File.GetUnixFileMode(this.GameSentinel).Should().Be((UnixFileMode)493);
        Directory.EnumerateFileSystemEntries(this.GameRoot).Should().Equal(this.GameSentinel);
        exception.ToString().Should().NotContain(this.TempRoot);
    }

    private static HashSet<string> FindRetainedPackageDescriptors()
    {
        HashSet<string> paths = new(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles($"/proc/{Environment.ProcessId}/fd"))
        {
            try
            {
                string target = new FileInfo(path).LinkTarget ?? "";
                if (
                    target.Contains("smapi-installer-verified-package", StringComparison.Ordinal)
                    || target.Contains("smapi-installer-verified-manifest", StringComparison.Ordinal)
                    || target.Contains("smapi-installer-attestation-bundle", StringComparison.Ordinal)
                )
                {
                    paths.Add(path);
                }
            }
            catch (IOException)
            {
                // Another runtime descriptor can close during procfs enumeration.
            }
        }
        return paths;
    }

    private static string Hash(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
