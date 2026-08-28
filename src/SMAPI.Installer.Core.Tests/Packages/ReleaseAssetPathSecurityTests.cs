using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Ownership.Persistence;
using StardewModdingAPI.Installer.Core.Packages;

namespace StardewModdingAPI.Installer.Core.Tests.Packages;

[TestFixture]
[Platform("Linux")]
public sealed class ReleaseAssetPathSecurityTests
{
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Tree = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private string TempRoot = null!;
    private ForkReleaseIdentity Identity = null!;

    [SetUp]
    public void SetUp()
    {
        // Keep the root short enough to create Unix sockets at the exact (intentionally long) release filenames.
        this.TempRoot = Path.Combine(Path.GetTempPath(), ($"s{Guid.NewGuid():N}")[..11]);
        Directory.CreateDirectory(this.TempRoot);
        this.Identity = ForkReleaseIdentity.Parse("fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(this.TempRoot))
            Directory.Delete(this.TempRoot, recursive: true);
    }

    [Test]
    public async Task VerifyFilesAsync_ExactRegularFiles_Succeeds()
    {
        Quartet quartet = this.CreateQuartet();

        await using VerifiedReleasePackage package = await new ReleasePackageVerifier().VerifyFilesAsync(
            quartet.PackagePath,
            quartet.ChecksumPath,
            quartet.MetadataPath,
            this.Identity,
            ReleaseAssetPathSecurityTests.Commit
        );

        package.Sha256.Should().Be(quartet.PackageHash);
    }

    [TestCase("checksums", "checksums.txt")]
    [TestCase("metadata", "metadata.json")]
    public async Task VerifyFilesAsync_NonExactMetadataFilename_Rejects(string asset, string wrongName)
    {
        Quartet quartet = this.CreateQuartet();
        string original = asset == "checksums" ? quartet.ChecksumPath : quartet.MetadataPath;
        string renamed = Path.Combine(this.TempRoot, wrongName);
        File.Move(original, renamed);

        Func<Task> action = () => new ReleasePackageVerifier().VerifyFilesAsync(
            quartet.PackagePath,
            asset == "checksums" ? renamed : quartet.ChecksumPath,
            asset == "metadata" ? renamed : quartet.MetadataPath,
            this.Identity,
            ReleaseAssetPathSecurityTests.Commit
        );

        await action.Should().ThrowAsync<PackageSecurityException>().WithMessage("*filename*");
    }

    [TestCase("package", "symlink")]
    [TestCase("package", "hardlink")]
    [TestCase("package", "fifo")]
    [TestCase("package", "socket")]
    [TestCase("checksums", "symlink")]
    [TestCase("checksums", "hardlink")]
    [TestCase("checksums", "fifo")]
    [TestCase("checksums", "socket")]
    [TestCase("metadata", "symlink")]
    [TestCase("metadata", "hardlink")]
    [TestCase("metadata", "fifo")]
    [TestCase("metadata", "socket")]
    [TestCase("manifest", "symlink")]
    [TestCase("manifest", "hardlink")]
    [TestCase("manifest", "fifo")]
    [TestCase("manifest", "socket")]
    [CancelAfter(5000)]
    public async Task VerifyCallerSelectedAsset_UnsafeEntry_RejectsPromptly(string asset, string kind)
    {
        Quartet quartet = this.CreateQuartet();
        VerifiedReleasePackage? release = null;
        if (asset == "manifest")
        {
            release = await new ReleasePackageVerifier().VerifyFilesAsync(
                quartet.PackagePath,
                quartet.ChecksumPath,
                quartet.MetadataPath,
                this.Identity,
                ReleaseAssetPathSecurityTests.Commit
            );
        }

        string selected = quartet.GetPath(asset);
        string target = selected + ".retained-target";
        File.Move(selected, target);
        Socket? socket = null;
        try
        {
            switch (kind)
            {
                case "symlink":
                    File.CreateSymbolicLink(selected, target);
                    break;
                case "hardlink":
                    link(target, selected).Should().Be(0, $"link(2) failed with errno {Marshal.GetLastWin32Error()}");
                    break;
                case "fifo":
                    mkfifo(selected, 0x180).Should().Be(0, $"mkfifo(2) failed with errno {Marshal.GetLastWin32Error()}");
                    break;
                case "socket":
                    socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    socket.Bind(new UnixDomainSocketEndPoint(selected));
                    break;
                default:
                    throw new AssertionException($"Unknown unsafe entry kind '{kind}'.");
            }

            Stopwatch timer = Stopwatch.StartNew();
            Func<Task> action = asset == "manifest"
                ? () => VerifyManifestForAssetAsync(release!, quartet.ManifestPath)
                : () => this.VerifyReleaseForAssetAsync(quartet);

            await action.Should().ThrowAsync<PackageSecurityException>();
            timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        }
        finally
        {
            socket?.Dispose();
            if (release is not null)
                await release.DisposeAsync();
        }
    }

    [TestCase("checksums")]
    [TestCase("metadata")]
    public async Task VerifyFilesAsync_InvalidUtf8Metadata_Rejects(string asset)
    {
        Quartet quartet = this.CreateQuartet();
        File.WriteAllBytes(quartet.GetPath(asset), [0xff, 0xfe, 0xfd]);

        Func<Task> action = () => this.VerifyReleaseForAssetAsync(quartet);

        await action.Should().ThrowAsync<PackageSecurityException>().WithMessage("*UTF-8*");
    }

    [TestCase("package")]
    [TestCase("checksums")]
    [TestCase("metadata")]
    public async Task VerifyFilesAsync_OversizedSelectedAsset_RejectsBeforeAllocation(string asset)
    {
        Quartet quartet = this.CreateQuartet();
        long packageLimit = asset == "package" ? quartet.PackageBytes.Length - 1 : quartet.PackageBytes.Length + 1;
        int checksumLimit = asset == "checksums" ? 1 : 64 * 1024;
        int metadataLimit = asset == "metadata" ? 1 : 256 * 1024;
        PackageVerificationLimits limits = new(packageLimit, checksumLimit, metadataLimit);

        Func<Task> action = () => new ReleasePackageVerifier().VerifyFilesAsync(
            quartet.PackagePath,
            quartet.ChecksumPath,
            quartet.MetadataPath,
            this.Identity,
            ReleaseAssetPathSecurityTests.Commit,
            limits
        );

        await action.Should().ThrowAsync<PackageSecurityException>().WithMessage("*size*");
    }

    [Test]
    public async Task VerifyManifest_OversizedSelectedAsset_RejectsBeforeAllocation()
    {
        Quartet quartet = this.CreateQuartet();
        await using VerifiedReleasePackage release = await new ReleasePackageVerifier().VerifyFilesAsync(
            quartet.PackagePath,
            quartet.ChecksumPath,
            quartet.MetadataPath,
            this.Identity,
            ReleaseAssetPathSecurityTests.Commit
        );
        OwnershipPersistenceLimits limits = new(quartet.ManifestBytes.Length - 1, 16, 20_000);

        Func<Task> action = () => new VerifiedInstallerPackageFactory().VerifyAsync(release, quartet.ManifestPath, limits);

        await action.Should().ThrowAsync<PackageSecurityException>().WithMessage("*size limit*");
    }

    [Test]
    public async Task VerifyManifest_InvalidUtf8Json_Rejects()
    {
        Quartet quartet = this.CreateQuartet([0xff, 0xfe, 0xfd]);
        await using VerifiedReleasePackage release = await new ReleasePackageVerifier().VerifyFilesAsync(
            quartet.PackagePath,
            quartet.ChecksumPath,
            quartet.MetadataPath,
            this.Identity,
            ReleaseAssetPathSecurityTests.Commit
        );

        Func<Task> action = () => new VerifiedInstallerPackageFactory().VerifyAsync(release, quartet.ManifestPath);

        await action.Should().ThrowAsync<PackageSecurityException>().WithMessage("*canonical or valid*");
    }

    [Test]
    [CancelAfter(5000)]
    public void RetainedFile_Device_RejectsPromptlyWithoutReading()
    {
        Stopwatch timer = Stopwatch.StartNew();

        Action action = () => RetainedReleaseAssetFile.Open("/dev/null", "test release asset").Dispose();

        action.Should().Throw<PackageSecurityException>();
        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task RetainedFile_PathReplacedAfterOpen_ReadsCapturedOrdinaryFile()
    {
        string path = Path.Combine(this.TempRoot, "asset");
        byte[] original = "captured release bytes"u8.ToArray();
        File.WriteAllBytes(path, original);
        using RetainedReleaseAssetFile retained = RetainedReleaseAssetFile.Open(path, "test release asset");
        File.Move(path, path + ".moved");
        File.WriteAllText(path, "replacement");

        byte[] read = await retained.ReadAllBytesAsync(1024, requireNonEmpty: true, CancellationToken.None);

        read.Should().Equal(original);
    }

    [Test]
    public async Task RetainedFile_UnlinkedAfterOpen_RejectsChangedLinkIdentity()
    {
        string path = Path.Combine(this.TempRoot, "asset");
        File.WriteAllText(path, "captured release bytes");
        using RetainedReleaseAssetFile retained = RetainedReleaseAssetFile.Open(path, "test release asset");
        File.Delete(path);
        File.WriteAllText(path, "replacement");

        Func<Task> action = () => retained.ReadAllBytesAsync(1024, requireNonEmpty: true, CancellationToken.None);

        await action.Should().ThrowAsync<PackageSecurityException>();
    }

    [Test]
    public async Task VerifyFilesAsync_SymlinkedParentSegment_Rejects()
    {
        Quartet quartet = this.CreateQuartet();
        string linkedRoot = Path.Combine(Path.GetDirectoryName(this.TempRoot)!, $"smapi-release-linked-{Guid.NewGuid():N}");
        Directory.CreateSymbolicLink(linkedRoot, this.TempRoot);
        try
        {
            Func<Task> action = () => new ReleasePackageVerifier().VerifyFilesAsync(
                Path.Combine(linkedRoot, Path.GetFileName(quartet.PackagePath)),
                Path.Combine(linkedRoot, ReleasePackageVerifier.ChecksumAssetName),
                Path.Combine(linkedRoot, ReleasePackageVerifier.BuildMetadataAssetName),
                this.Identity,
                ReleaseAssetPathSecurityTests.Commit
            );

            await action.Should().ThrowAsync<PackageSecurityException>();
        }
        finally
        {
            Directory.Delete(linkedRoot);
        }
    }

    private async Task VerifyReleaseForAssetAsync(Quartet quartet)
    {
        await using VerifiedReleasePackage package = await new ReleasePackageVerifier().VerifyFilesAsync(
            quartet.PackagePath,
            quartet.ChecksumPath,
            quartet.MetadataPath,
            this.Identity,
            ReleaseAssetPathSecurityTests.Commit
        );
    }

    private static async Task VerifyManifestForAssetAsync(VerifiedReleasePackage release, string manifestPath)
    {
        await using VerifiedInstallerPackage package = await new VerifiedInstallerPackageFactory().VerifyAsync(release, manifestPath);
    }

    private Quartet CreateQuartet(byte[]? manifestBytesOverride = null)
    {
        byte[] packageBytes = "synthetic installer package"u8.ToArray();
        string packageHash = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
        string workflow = $"{ForkReleaseIdentity.Repository}/.github/workflows/linux-alpha-release.yml@refs/tags/{this.Identity.Tag}";
        InstallationReleaseIdentity release = new(
            InstallationReleaseIdentity.ReviewedRepository,
            this.Identity.Tag,
            this.Identity.EmbeddedVersion,
            this.Identity.PackageAssetName,
            ReleaseAssetPathSecurityTests.Commit,
            ReleaseAssetPathSecurityTests.Tree,
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
        byte[] manifestBytes = manifestBytesOverride ?? System.Text.Encoding.UTF8.GetBytes(manifest.ToCanonicalJson());
        string manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        string manifestName = VerifiedInstallerPackageFactory.GetManifestAssetName(this.Identity);

        string packagePath = Path.Combine(this.TempRoot, this.Identity.PackageAssetName);
        string checksumPath = Path.Combine(this.TempRoot, ReleasePackageVerifier.ChecksumAssetName);
        string metadataPath = Path.Combine(this.TempRoot, ReleasePackageVerifier.BuildMetadataAssetName);
        string manifestPath = Path.Combine(this.TempRoot, manifestName);
        File.WriteAllBytes(packagePath, packageBytes);
        File.WriteAllText(checksumPath, $"{packageHash}  {this.Identity.PackageAssetName}\n{manifestHash}  {manifestName}\n");
        File.WriteAllBytes(manifestPath, manifestBytes);
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(new
        {
            schema_version = 1,
            release = new { version = this.Identity.EmbeddedVersion, tag = this.Identity.Tag },
            source = new { repository = ForkReleaseIdentity.RepositoryUrl, commit = ReleaseAssetPathSecurityTests.Commit, tree = ReleaseAssetPathSecurityTests.Tree },
            build = new { workflow, configuration = "Release", runtime_identifier = "linux-x64" },
            artifacts = new object[]
            {
                new { name = this.Identity.PackageAssetName, size_bytes = packageBytes.Length, sha256 = packageHash },
                new { name = manifestName, size_bytes = manifestBytes.Length, sha256 = manifestHash }
            }
        }));
        return new Quartet(packagePath, checksumPath, metadataPath, manifestPath, packageBytes, manifestBytes, packageHash);
    }

    private sealed record Quartet(
        string PackagePath,
        string ChecksumPath,
        string MetadataPath,
        string ManifestPath,
        byte[] PackageBytes,
        byte[] ManifestBytes,
        string PackageHash
    )
    {
        public string GetPath(string asset) => asset switch
        {
            "package" => this.PackagePath,
            "checksums" => this.ChecksumPath,
            "metadata" => this.MetadataPath,
            "manifest" => this.ManifestPath,
            _ => throw new AssertionException($"Unknown release asset '{asset}'.")
        };
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string existingPath, string newPath);

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string path, uint mode);
}
