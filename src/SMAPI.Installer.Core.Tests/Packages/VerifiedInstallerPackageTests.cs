using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Tests.Packages;

[TestFixture]
[Platform("Linux")]
[NonParallelizable]
public sealed class VerifiedInstallerPackageTests
{
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Tree = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private string TempRoot = null!;
    private ForkReleaseIdentity Identity = null!;

    [SetUp]
    public void SetUp()
    {
        this.TempRoot = Path.Combine(Path.GetTempPath(), $"smapi-verified-manifest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.TempRoot);
        this.Identity = ForkReleaseIdentity.Parse("fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(this.TempRoot))
            Directory.Delete(this.TempRoot, recursive: true);
    }

    [Test]
    public async Task VerifyAsync_SourceReplacementAndDeletionCannotChangeRetainedManifestAuthority()
    {
        Quartet quartet = await this.CreateQuartetAsync();
        await using VerifiedReleasePackage release = quartet.Release;
        await using VerifiedInstallerPackage authority = await new VerifiedInstallerPackageFactory().VerifyAsync(
            release,
            quartet.ManifestPath
        );
        string moved = quartet.ManifestPath + ".original";
        File.Move(quartet.ManifestPath, moved);
        File.WriteAllBytes(quartet.ManifestPath, "replacement manifest bytes"u8.ToArray());
        File.Delete(quartet.ManifestPath);

        using LinuxSealedFileLease lease = authority.LeaseManifestForExternalRead();

        authority.ManifestAssetName.Should().Be(quartet.ManifestName);
        authority.ManifestSizeBytes.Should().Be(quartet.ManifestBytes.LongLength);
        authority.ManifestSha256.Should().Be(Sha256Digest.Hash(quartet.ManifestBytes));
        File.ReadAllBytes(lease.ProcPath).Should().Equal(quartet.ManifestBytes);
        Action overwrite = () => File.WriteAllBytes(lease.ProcPath, "changed"u8.ToArray());
        Exception error = overwrite.Should().Throw<Exception>().Which;
        (error is IOException or UnauthorizedAccessException).Should().BeTrue();
        File.ReadAllBytes(lease.ProcPath).Should().Equal(quartet.ManifestBytes);
    }

    [Test]
    public async Task Dispose_ExistingLeaseRetainsExactBytesButNewAuthorityUseFailsClosed()
    {
        Quartet quartet = await this.CreateQuartetAsync();
        await using VerifiedReleasePackage release = quartet.Release;
        VerifiedInstallerPackage authority = await new VerifiedInstallerPackageFactory().VerifyAsync(
            release,
            quartet.ManifestPath
        );
        LinuxSealedFileLease lease = authority.LeaseManifestForExternalRead();
        string procPath = lease.ProcPath;

        await authority.DisposeAsync();

        File.ReadAllBytes(procPath).Should().Equal(quartet.ManifestBytes);
        Action reuse = () => authority.LeaseManifestForExternalRead().Dispose();
        reuse.Should().Throw<ObjectDisposedException>();
        lease.Dispose();
        File.Exists(procPath).Should().BeFalse();
        authority.Dispose();
    }

    [Test]
    public async Task VerifyAsync_CanonicalParseFailureClosesManifestDescriptorAndKeepsPackageOwnership()
    {
        byte[] invalidManifest = "not a canonical ownership manifest"u8.ToArray();
        Quartet quartet = await this.CreateQuartetAsync(invalidManifest);
        await using VerifiedReleasePackage release = quartet.Release;
        int before = CountRetainedManifestDescriptors();

        Func<Task> verify = () => new VerifiedInstallerPackageFactory().VerifyAsync(release, quartet.ManifestPath);

        PackageSecurityException exception = (await verify.Should().ThrowAsync<PackageSecurityException>())
            .WithMessage("*canonical or valid*")
            .Which;
        exception.FailureKind.Should().Be(PackageSecurityFailureKind.MetadataRejected);
        CountRetainedManifestDescriptors().Should().Be(before);
        release.GetArtifact(this.Identity.PackageAssetName).Sha256.Should().Be(quartet.PackageHash);
    }

    [Test]
    public async Task VerifyAsync_DigestFailureAndCancellationPublishNoManifestAuthority()
    {
        Quartet quartet = await this.CreateQuartetAsync();
        await using VerifiedReleasePackage release = quartet.Release;
        int before = CountRetainedManifestDescriptors();
        byte[] changed = quartet.ManifestBytes.ToArray();
        changed[^1] ^= 1;
        File.WriteAllBytes(quartet.ManifestPath, changed);

        Func<Task> digestFailure = () => new VerifiedInstallerPackageFactory().VerifyAsync(release, quartet.ManifestPath);
        PackageSecurityException exception = (await digestFailure.Should().ThrowAsync<PackageSecurityException>())
            .WithMessage("*doesn't match SHA256SUMS*")
            .Which;
        exception.FailureKind.Should().Be(PackageSecurityFailureKind.MetadataRejected);
        CountRetainedManifestDescriptors().Should().Be(before);
        release.GetArtifact(this.Identity.PackageAssetName).Sha256.Should().Be(quartet.PackageHash);

        File.WriteAllBytes(quartet.ManifestPath, quartet.ManifestBytes);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Func<Task> cancelled = () => new VerifiedInstallerPackageFactory().VerifyAsync(
            release,
            quartet.ManifestPath,
            cancellationToken: cancellation.Token
        );
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        CountRetainedManifestDescriptors().Should().Be(before);
        release.GetArtifact(this.Identity.PackageAssetName).Sha256.Should().Be(quartet.PackageHash);
    }

    [Test]
    public async Task VerifyAsync_MissingManifestAuthorityRemainsUnclassified()
    {
        Quartet quartet = await this.CreateQuartetAsync();
        await using VerifiedReleasePackage release = quartet.Release;
        File.Delete(quartet.ManifestPath);
        int before = CountRetainedManifestDescriptors();

        Func<Task> verify = () => new VerifiedInstallerPackageFactory().VerifyAsync(release, quartet.ManifestPath);

        PackageSecurityException exception = (await verify.Should().ThrowAsync<PackageSecurityException>()).Which;
        exception.FailureKind.Should().Be(PackageSecurityFailureKind.Unclassified);
        CountRetainedManifestDescriptors().Should().Be(before);
        release.GetArtifact(this.Identity.PackageAssetName).Sha256.Should().Be(quartet.PackageHash);
    }

    private async Task<Quartet> CreateQuartetAsync(byte[]? manifestBytesOverride = null)
    {
        byte[] packageBytes = "synthetic installer package"u8.ToArray();
        string packageHash = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
        string packagePath = Path.Combine(this.TempRoot, this.Identity.PackageAssetName);
        File.WriteAllBytes(packagePath, packageBytes);
        string workflow = $"{ForkReleaseIdentity.Repository}/.github/workflows/linux-alpha-release.yml@refs/tags/{this.Identity.Tag}";
        InstallationReleaseIdentity releaseIdentity = new(
            InstallationReleaseIdentity.ReviewedRepository,
            this.Identity.Tag,
            this.Identity.EmbeddedVersion,
            this.Identity.PackageAssetName,
            VerifiedInstallerPackageTests.Commit,
            VerifiedInstallerPackageTests.Tree,
            Sha256Digest.Parse(packageHash),
            packageBytes.Length,
            workflow,
            "Release",
            "linux-x64"
        );
        PackageManifest manifest = new(
            releaseIdentity,
            [
                new PackageManifestEntry(
                    NormalizedRelativePath.Parse("StardewValley"),
                    Sha256Digest.Parse(new string('d', 64)),
                    42,
                    493,
                    OwnedEntryKind.Launcher
                )
            ]
        );
        byte[] manifestBytes = manifestBytesOverride ?? Encoding.UTF8.GetBytes(manifest.ToCanonicalJson());
        string manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        string manifestName = VerifiedInstallerPackageFactory.GetManifestAssetName(this.Identity);
        string manifestPath = Path.Combine(this.TempRoot, manifestName);
        File.WriteAllBytes(manifestPath, manifestBytes);
        string checksums = $"{packageHash}  {this.Identity.PackageAssetName}\n{manifestHash}  {manifestName}\n";
        string metadata = JsonSerializer.Serialize(new
        {
            schema_version = 1,
            release = new { version = this.Identity.EmbeddedVersion, tag = this.Identity.Tag },
            source = new { repository = ForkReleaseIdentity.RepositoryUrl, commit = VerifiedInstallerPackageTests.Commit, tree = VerifiedInstallerPackageTests.Tree },
            build = new { workflow, configuration = "Release", runtime_identifier = "linux-x64" },
            artifacts = new object[]
            {
                new { name = this.Identity.PackageAssetName, size_bytes = packageBytes.Length, sha256 = packageHash },
                new { name = manifestName, size_bytes = manifestBytes.Length, sha256 = manifestHash }
            }
        });
        VerifiedReleasePackage release = await new ReleasePackageVerifier().VerifyAsync(
            packagePath,
            checksums,
            metadata,
            this.Identity,
            VerifiedInstallerPackageTests.Commit
        );
        return new Quartet(release, manifestPath, manifestName, manifestBytes, packageHash);
    }

    private static int CountRetainedManifestDescriptors()
    {
        int count = 0;
        foreach (string path in Directory.EnumerateFiles($"/proc/{Environment.ProcessId}/fd"))
        {
            try
            {
                if (new FileInfo(path).LinkTarget?.Contains("memfd:smapi-installer-verified-manifest", StringComparison.Ordinal) == true)
                    count++;
            }
            catch (IOException)
            {
                // Another runtime descriptor can close during enumeration.
            }
        }
        return count;
    }

    private sealed record Quartet(
        VerifiedReleasePackage Release,
        string ManifestPath,
        string ManifestName,
        byte[] ManifestBytes,
        string PackageHash
    );
}
