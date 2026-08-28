using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Packages;

namespace StardewModdingAPI.Installer.Core.Tests.Packages;

[TestFixture]
public sealed class ReleasePackageVerifierTests
{
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Tree = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private string TempRoot = null!;
    private ForkReleaseIdentity Identity = null!;

    [SetUp]
    public void SetUp()
    {
        this.TempRoot = Path.Combine(Path.GetTempPath(), $"smapi-package-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.TempRoot);
        this.Identity = ForkReleaseIdentity.Parse(
            "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1"
        );
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(this.TempRoot))
            Directory.Delete(this.TempRoot, recursive: true);
    }

    [Test]
    public async Task VerifyAsync_AllArtifactsAgree_ReturnsVerifiedIdentity()
    {
        (string path, byte[] bytes, string hash) = this.CreatePackage();
        ReleasePackageVerifier verifier = new();

        VerifiedReleasePackage result = await verifier.VerifyAsync(
            path,
            $"{hash}  {this.Identity.PackageAssetName}\n",
            this.CreateMetadata(hash, bytes.Length),
            this.Identity,
            ReleasePackageVerifierTests.Commit
        );

        result.Sha256.Should().Be(hash);
        result.SizeBytes.Should().Be(bytes.Length);
        result.SourceCommit.Should().Be(ReleasePackageVerifierTests.Commit);
        result.SourceTree.Should().Be(ReleasePackageVerifierTests.Tree);
    }

    [Test]
    public async Task VerifyAsync_ChecksumDoesNotMatchPackage_Rejects()
    {
        (string path, byte[] bytes, string hash) = this.CreatePackage();
        string otherHash = new('0', 64);
        ReleasePackageVerifier verifier = new();

        Func<Task> action = () => verifier.VerifyAsync(
            path,
            $"{otherHash}  {this.Identity.PackageAssetName}\n",
            this.CreateMetadata(hash, bytes.Length),
            this.Identity
        );

        await action.Should().ThrowAsync<PackageSecurityException>()
            .WithMessage("*SHA256SUMS*");
    }

    [Test]
    public async Task VerifyAsync_MetadataHashDoesNotMatchPackage_Rejects()
    {
        (string path, byte[] bytes, string hash) = this.CreatePackage();
        ReleasePackageVerifier verifier = new();

        Func<Task> action = () => verifier.VerifyAsync(
            path,
            $"{hash}  {this.Identity.PackageAssetName}\n",
            this.CreateMetadata(new string('0', 64), bytes.Length),
            this.Identity
        );

        await action.Should().ThrowAsync<PackageSecurityException>()
            .WithMessage("*build-metadata.json*");
    }

    [Test]
    public async Task VerifyAsync_MetadataIdentityMismatch_Rejects()
    {
        (string path, byte[] bytes, string hash) = this.CreatePackage();
        ReleasePackageVerifier verifier = new();
        string metadata = this.CreateMetadata(hash, bytes.Length, repository: "https://github.com/other/repo");

        Func<Task> action = () => verifier.VerifyAsync(
            path,
            $"{hash}  {this.Identity.PackageAssetName}\n",
            metadata,
            this.Identity
        );

        await action.Should().ThrowAsync<PackageSecurityException>()
            .WithMessage("*repository*");
    }

    [Test]
    public async Task VerifyAsync_ReleaseTargetCommitMismatch_Rejects()
    {
        (string path, byte[] bytes, string hash) = this.CreatePackage();
        ReleasePackageVerifier verifier = new();

        Func<Task> action = () => verifier.VerifyAsync(
            path,
            $"{hash}  {this.Identity.PackageAssetName}\n",
            this.CreateMetadata(hash, bytes.Length),
            this.Identity,
            new string('c', 40)
        );

        await action.Should().ThrowAsync<PackageSecurityException>()
            .WithMessage("*release target*");
    }

    [Test]
    public async Task VerifyAsync_DuplicateOrUnexpectedChecksumEntry_Rejects()
    {
        (string path, byte[] bytes, string hash) = this.CreatePackage();
        ReleasePackageVerifier verifier = new();
        string checksums = $"{hash}  {this.Identity.PackageAssetName}\n{hash}  other.zip\n";

        Func<Task> action = () => verifier.VerifyAsync(
            path,
            checksums,
            this.CreateMetadata(hash, bytes.Length),
            this.Identity
        );

        await action.Should().ThrowAsync<PackageSecurityException>();
    }

    [Test]
    public async Task VerifyAsync_BoundedDocumentsAndPackage_Rejects()
    {
        (string path, byte[] bytes, string hash) = this.CreatePackage();
        ReleasePackageVerifier verifier = new();
        PackageVerificationLimits limits = new(
            maxPackageBytes: bytes.Length - 1,
            maxChecksumBytes: 1024,
            maxMetadataBytes: 4096
        );

        Func<Task> action = () => verifier.VerifyAsync(
            path,
            $"{hash}  {this.Identity.PackageAssetName}\n",
            this.CreateMetadata(hash, bytes.Length),
            this.Identity,
            limits: limits
        );

        await action.Should().ThrowAsync<PackageSecurityException>()
            .WithMessage("*size*");
    }

    private (string Path, byte[] Bytes, string Hash) CreatePackage()
    {
        byte[] bytes = "synthetic installer package"u8.ToArray();
        string path = Path.Combine(this.TempRoot, this.Identity.PackageAssetName);
        File.WriteAllBytes(path, bytes);
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return (path, bytes, hash);
    }

    private string CreateMetadata(string hash, long size, string? repository = null)
    {
        return JsonSerializer.Serialize(new
        {
            schema_version = 1,
            release = new { version = this.Identity.EmbeddedVersion, tag = this.Identity.Tag },
            source = new
            {
                repository = repository ?? ForkReleaseIdentity.RepositoryUrl,
                commit = ReleasePackageVerifierTests.Commit,
                tree = ReleasePackageVerifierTests.Tree
            },
            build = new
            {
                workflow = $"{ForkReleaseIdentity.Repository}/.github/workflows/linux-alpha-release.yml@refs/tags/{this.Identity.Tag}",
                configuration = "Release",
                runtime_identifier = "linux-x64"
            },
            artifact = new { name = this.Identity.PackageAssetName, size_bytes = size, sha256 = hash }
        });
    }
}
