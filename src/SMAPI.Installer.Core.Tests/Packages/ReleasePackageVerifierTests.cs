using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Ownership;
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

        await using VerifiedReleasePackage result = await verifier.VerifyAsync(
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
        result.InstallationIdentity.Tag.Should().Be(this.Identity.Tag);
        result.InstallationIdentity.EmbeddedVersion.Should().Be(this.Identity.EmbeddedVersion);
        result.InstallationIdentity.PackageAssetName.Should().Be(this.Identity.PackageAssetName);
        result.InstallationIdentity.SourceCommit.Should().Be(ReleasePackageVerifierTests.Commit);
        result.InstallationIdentity.SourceTree.Should().Be(ReleasePackageVerifierTests.Tree);
        result.InstallationIdentity.PackageSha256.Value.Should().Be(hash);
        result.InstallationIdentity.PackageSizeBytes.Should().Be(bytes.Length);
        result.InstallationIdentity.BuildWorkflow.Should().Be(
            $"{ForkReleaseIdentity.Repository}/.github/workflows/linux-alpha-release.yml@refs/tags/{this.Identity.Tag}"
        );
        result.InstallationIdentity.BuildConfiguration.Should().Be("Release");
        result.InstallationIdentity.RuntimeIdentifier.Should().Be("linux-x64");
    }

    [Test]
    public async Task VerifyAsync_ArtifactsArrayMetadata_RemainsCompatible()
    {
        (string path, byte[] bytes, string hash) = this.CreatePackage();

        await using VerifiedReleasePackage result = await new ReleasePackageVerifier().VerifyAsync(
            path,
            $"{hash}  {this.Identity.PackageAssetName}\n",
            this.CreateMetadata(hash, bytes.Length, useArtifactsArray: true),
            this.Identity,
            ReleasePackageVerifierTests.Commit
        );

        result.Sha256.Should().Be(hash);
    }

    [Test]
    public async Task VerifyInstallerPackage_CrossVerifiedCompanion_ReturnsOpaqueAuthority()
    {
        (string packagePath, byte[] packageBytes, string packageHash) = this.CreatePackage();
        string workflow = $"{ForkReleaseIdentity.Repository}/.github/workflows/linux-alpha-release.yml@refs/tags/{this.Identity.Tag}";
        InstallationReleaseIdentity release = new(
            InstallationReleaseIdentity.ReviewedRepository,
            this.Identity.Tag,
            this.Identity.EmbeddedVersion,
            this.Identity.PackageAssetName,
            ReleasePackageVerifierTests.Commit,
            ReleasePackageVerifierTests.Tree,
            Sha256Digest.Parse(packageHash),
            packageBytes.Length,
            workflow,
            "Release",
            "linux-x64"
        );
        PackageManifest manifest = new(
            release,
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
        byte[] manifestBytes = System.Text.Encoding.UTF8.GetBytes(manifest.ToCanonicalJson());
        string manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        string manifestName = VerifiedInstallerPackageFactory.GetManifestAssetName(this.Identity);
        string manifestPath = Path.Combine(this.TempRoot, manifestName);
        File.WriteAllBytes(manifestPath, manifestBytes);
        string checksums = $"{packageHash}  {this.Identity.PackageAssetName}\n{manifestHash}  {manifestName}\n";

        VerifiedReleasePackage verified = await new ReleasePackageVerifier().VerifyAsync(
            packagePath,
            checksums,
            this.CreateMetadata(
                packageHash,
                packageBytes.Length,
                companion: (manifestName, manifestBytes.Length, manifestHash)
            ),
            this.Identity,
            ReleasePackageVerifierTests.Commit
        );
        await using VerifiedInstallerPackage authority = await new VerifiedInstallerPackageFactory().VerifyAsync(
            verified,
            manifestPath
        );

        authority.Release.Should().Be(release);
        authority.ManifestSha256.Value.Should().Be(manifestHash);
        authority.Manifest.Entries.Should().ContainSingle(entry => entry.Path.Value == "StardewValley");
    }

    [Test]
    public async Task VerifyInstallerPackage_ChangedCompanion_Rejects()
    {
        (string packagePath, byte[] packageBytes, string packageHash) = this.CreatePackage();
        string manifestName = VerifiedInstallerPackageFactory.GetManifestAssetName(this.Identity);
        byte[] manifestBytes = "not a canonical manifest"u8.ToArray();
        string manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        string manifestPath = Path.Combine(this.TempRoot, manifestName);
        File.WriteAllBytes(manifestPath, manifestBytes);
        string checksums = $"{packageHash}  {this.Identity.PackageAssetName}\n{manifestHash}  {manifestName}\n";
        await using VerifiedReleasePackage verified = await new ReleasePackageVerifier().VerifyAsync(
            packagePath,
            checksums,
            this.CreateMetadata(
                packageHash,
                packageBytes.Length,
                companion: (manifestName, manifestBytes.Length, manifestHash)
            ),
            this.Identity,
            ReleasePackageVerifierTests.Commit
        );
        File.WriteAllBytes(manifestPath, "same size but different bytes"u8.ToArray());

        Func<Task> action = () => new VerifiedInstallerPackageFactory().VerifyAsync(verified, manifestPath);

        await action.Should().ThrowAsync<PackageSecurityException>();
    }

    [Test]
    public async Task VerifyAsync_DuplicateUnknownOrOverdeepMetadata_Rejects()
    {
        (string path, byte[] bytes, string hash) = this.CreatePackage();
        string valid = this.CreateMetadata(hash, bytes.Length);
        string duplicate = "{\"schema_version\":999," + valid[1..];
        string unknownRoot = valid[..^1] + ",\"unknown\":true}";
        string unknownArtifact = valid.Replace(
            $"\"sha256\":\"{hash}\"",
            $"\"sha256\":\"{hash}\",\"unknown\":true",
            StringComparison.Ordinal
        );
        string duplicateArtifact = valid.Replace(
            $"\"sha256\":\"{hash}\"",
            $"\"sha256\":\"{hash}\",\"sha256\":\"{hash}\"",
            StringComparison.Ordinal
        );
        string bothArtifactShapes = valid[..^1] + ",\"artifacts\":[]}";
        string overdeep = "{\"schema_version\":1,\"release\":"
            + new string('[', 12)
            + "null"
            + new string(']', 12)
            + "}";

        foreach (string metadata in new[]
        {
            duplicate,
            unknownRoot,
            unknownArtifact,
            duplicateArtifact,
            bothArtifactShapes,
            overdeep
        })
        {
            Func<Task> action = () => new ReleasePackageVerifier().VerifyAsync(
                path,
                $"{hash}  {this.Identity.PackageAssetName}\n",
                metadata,
                this.Identity,
                ReleasePackageVerifierTests.Commit
            );
            await action.Should().ThrowAsync<PackageSecurityException>();
        }
    }

    [Test]
    public async Task VerifyThenReplaceSource_ExtractionUsesRetainedVerifiedHandleAndPrivateModes()
    {
        const string expectedRoot = "SMAPI synthetic Linux installer";
        (string path, byte[] bytes, string hash) = this.CreateZipPackage(expectedRoot, "verified");
        VerifiedReleasePackage verified = await new ReleasePackageVerifier().VerifyAsync(
            path,
            $"{hash}  {this.Identity.PackageAssetName}\n",
            this.CreateMetadata(hash, bytes.Length),
            this.Identity,
            ReleasePackageVerifierTests.Commit
        );
        string stagingDirectory = GetPrivatePath(verified, "StagingDirectory");
        string stagingPath = GetPrivatePath(verified, "StagingPath");
        if (OperatingSystem.IsLinux())
        {
            Convert.ToInt32(File.GetUnixFileMode(stagingDirectory) & (UnixFileMode)0x1ff)
                .Should().Be(Convert.ToInt32("700", 8));
            Convert.ToInt32(File.GetUnixFileMode(stagingPath) & (UnixFileMode)0x1ff)
                .Should().Be(Convert.ToInt32("600", 8));
        }

        File.WriteAllBytes(path, CreateZipBytes(expectedRoot, "replacement"));
        string extraction = Path.Combine(this.TempRoot, "extracted");
        await new BoundedZipPackage().InspectAndExtractAsync(
            verified,
            expectedRoot,
            extraction,
            new ZipPackageLimits(1024 * 1024, 10, 10, 1024, 4096, 1000)
        );

        File.ReadAllText(Path.Combine(extraction, expectedRoot, "payload.txt")).Should().Be("verified");
        if (OperatingSystem.IsLinux())
        {
            Convert.ToInt32(File.GetUnixFileMode(extraction) & (UnixFileMode)0x1ff)
                .Should().Be(Convert.ToInt32("700", 8));
            Convert.ToInt32(File.GetUnixFileMode(Path.Combine(extraction, expectedRoot)) & (UnixFileMode)0x1ff)
                .Should().Be(Convert.ToInt32("700", 8));
            Convert.ToInt32(File.GetUnixFileMode(Path.Combine(extraction, expectedRoot, "payload.txt")) & (UnixFileMode)0x1ff)
                .Should().Be(Convert.ToInt32("600", 8));
        }

        await verified.DisposeAsync();
        Directory.Exists(stagingDirectory).Should().BeFalse();
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
            this.Identity,
            ReleasePackageVerifierTests.Commit
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
            this.Identity,
            ReleasePackageVerifierTests.Commit
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
            this.Identity,
            ReleasePackageVerifierTests.Commit
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
            this.Identity,
            ReleasePackageVerifierTests.Commit
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
            ReleasePackageVerifierTests.Commit,
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

    private string CreateMetadata(
        string hash,
        long size,
        string? repository = null,
        bool useArtifactsArray = false,
        (string Name, long Size, string Hash)? companion = null
    )
    {
        object release = new { version = this.Identity.EmbeddedVersion, tag = this.Identity.Tag };
        object source = new
        {
            repository = repository ?? ForkReleaseIdentity.RepositoryUrl,
            commit = ReleasePackageVerifierTests.Commit,
            tree = ReleasePackageVerifierTests.Tree
        };
        object build = new
        {
            workflow = $"{ForkReleaseIdentity.Repository}/.github/workflows/linux-alpha-release.yml@refs/tags/{this.Identity.Tag}",
            run = "https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/1/attempts/1",
            runner_image = "ubuntu24",
            runner_arch = "X64",
            reference_assemblies_commit = new string('c', 40),
            configuration = "Release",
            runtime_identifier = "linux-x64",
            timestamp_utc = "2026-08-28T00:00:00Z",
            dotnet_info = ".NET SDK synthetic"
        };
        object artifact = new { name = this.Identity.PackageAssetName, size_bytes = size, sha256 = hash };
        object[] artifacts = companion is { } companionArtifact
            ?
            [
                artifact,
                new { name = companionArtifact.Name, size_bytes = companionArtifact.Size, sha256 = companionArtifact.Hash }
            ]
            : [artifact];
        return useArtifactsArray || companion != null
            ? JsonSerializer.Serialize(new
            {
                schema_version = 1,
                release,
                source,
                build,
                artifacts,
                reproducibility = "Inputs recorded; byte equality not claimed."
            })
            : JsonSerializer.Serialize(new
            {
                schema_version = 1,
                release,
                source,
                build,
                artifact,
                reproducibility = "Inputs recorded; byte equality not claimed."
            });
    }

    private (string Path, byte[] Bytes, string Hash) CreateZipPackage(string expectedRoot, string contents)
    {
        byte[] bytes = CreateZipBytes(expectedRoot, contents);
        string path = Path.Combine(this.TempRoot, this.Identity.PackageAssetName);
        File.WriteAllBytes(path, bytes);
        return (path, bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static byte[] CreateZipBytes(string expectedRoot, string contents)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry root = archive.CreateEntry(expectedRoot + "/", CompressionLevel.NoCompression);
            root.ExternalAttributes = unchecked((int)((uint)(0x4000 | 0x1ED) << 16));
            ZipArchiveEntry payload = archive.CreateEntry(expectedRoot + "/payload.txt", CompressionLevel.Optimal);
            using StreamWriter writer = new(payload.Open());
            writer.Write(contents);
        }
        return stream.ToArray();
    }

    private static string GetPrivatePath(VerifiedReleasePackage package, string fieldName)
    {
        return (string)typeof(VerifiedReleasePackage)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(package)!;
    }
}
