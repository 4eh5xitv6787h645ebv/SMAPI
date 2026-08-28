using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Tests.Packages;

[TestFixture]
[SupportedOSPlatform("linux")]
public sealed class VerifiedPackageContentTests
{
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Tree = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private string TempRoot = null!;
    private ForkReleaseIdentity Identity = null!;

    [SetUp]
    public void SetUp()
    {
        this.TempRoot = Path.Combine(Path.GetTempPath(), $"smapi-content-tests-{Guid.NewGuid():N}");
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
    public async Task ExtractAsync_ExactNestedPayload_ReturnsAnchoredContentAndMapsLauncher()
    {
        Dictionary<string, (byte[] Bytes, int Mode)> files = new(StringComparer.Ordinal)
        {
            ["unix-launcher.sh"] = ("#!/bin/sh\nexec smapi\n"u8.ToArray(), 493),
            ["smapi-internal/a.dll"] = ("verified assembly"u8.ToArray(), 420)
        };
        VerifiedInstallerPackage authority = await this.CreateAuthorityAsync(files);
        await using VerifiedPackageContent content = await new VerifiedPackageContentFactory().ExtractAsync(authority);

        content.Release.Should().Be(authority.Release);
        using LinuxAnchoredFile launcher = content.Payload.OpenRegularFileForRead("StardewValley");
        using LinuxAnchoredFile assembly = content.Payload.OpenRegularFileForRead("smapi-internal/a.dll");
        launcher.Identity.UnixMode.Should().Be(493);
        assembly.Identity.UnixMode.Should().Be(420);
        content.Payload.ReadAllBytes(launcher, 1024).Should().Equal(files["unix-launcher.sh"].Bytes);
        content.Payload.ReadAllBytes(assembly, 1024).Should().Equal(files["smapi-internal/a.dll"].Bytes);
    }

    [Test]
    public async Task ExtractAsync_ExtraNestedFile_RejectsAndKeepsAuthorityOwnedByCaller()
    {
        Dictionary<string, (byte[] Bytes, int Mode)> files = new(StringComparer.Ordinal)
        {
            ["unix-launcher.sh"] = ("launcher"u8.ToArray(), 493),
            ["unexpected.txt"] = ("unexpected"u8.ToArray(), 420)
        };
        VerifiedInstallerPackage authority = await this.CreateAuthorityAsync(
            files,
            manifestSourcePaths: ["unix-launcher.sh"]
        );

        Func<Task> action = () => new VerifiedPackageContentFactory().ExtractAsync(authority);

        await action.Should().ThrowAsync<PackageSecurityException>().WithMessage("*absent from the verified manifest*");
        authority.AssertUsable();
        await authority.DisposeAsync();
    }

    [Test]
    public async Task ExtractAsync_ModeOrDigestMismatch_Rejects()
    {
        Dictionary<string, (byte[] Bytes, int Mode)> files = new(StringComparer.Ordinal)
        {
            ["unix-launcher.sh"] = ("launcher"u8.ToArray(), 420)
        };
        VerifiedInstallerPackage authority = await this.CreateAuthorityAsync(
            files,
            manifestModeOverrides: new Dictionary<string, int>(StringComparer.Ordinal) { ["unix-launcher.sh"] = 493 }
        );

        Func<Task> action = () => new VerifiedPackageContentFactory().ExtractAsync(authority);

        await action.Should().ThrowAsync<PackageSecurityException>().WithMessage("*Unix mode*");
        await authority.DisposeAsync();
    }

    [Test]
    public async Task PublicInspectAsync_WithVerifiedContent_IsReadOnlyForUntouchedGame()
    {
        Dictionary<string, (byte[] Bytes, int Mode)> files = new(StringComparer.Ordinal)
        {
            ["unix-launcher.sh"] = ("#!/bin/sh\nexec smapi\n"u8.ToArray(), 493),
            ["smapi-internal/a.dll"] = ("verified assembly"u8.ToArray(), 420)
        };
        VerifiedInstallerPackage authority = await this.CreateAuthorityAsync(files);
        await using VerifiedPackageContent content = await new VerifiedPackageContentFactory().ExtractAsync(authority);
        string game = Path.Combine(this.TempRoot, "game");
        Directory.CreateDirectory(game);
        string launcher = Path.Combine(game, "StardewValley");
        await File.WriteAllTextAsync(launcher, "vanilla launcher");
        File.SetUnixFileMode(launcher, (UnixFileMode)493);

        using InspectedInstallationState inspection = await new LinuxInstallerEngine().InspectAsync(
            game,
            InstallationAction.Install,
            content
        );

        inspection.Plan.CanExecute.Should().BeTrue();
        Directory.Exists(Path.Combine(game, ".smapi-installer")).Should().BeFalse();
        Directory.EnumerateFileSystemEntries(game).Should().Equal(launcher);
        (await File.ReadAllTextAsync(launcher)).Should().Be("vanilla launcher");
        File.GetUnixFileMode(launcher).Should().Be((UnixFileMode)493);
    }

    private async Task<VerifiedInstallerPackage> CreateAuthorityAsync(
        IReadOnlyDictionary<string, (byte[] Bytes, int Mode)> nestedFiles,
        IReadOnlyCollection<string>? manifestSourcePaths = null,
        IReadOnlyDictionary<string, int>? manifestModeOverrides = null
    )
    {
        byte[] nested = CreateZip(nestedFiles);
        string outerRoot = $"SMAPI {this.Identity.EmbeddedVersion} Linux installer";
        byte[] outer = CreateOuterZip(outerRoot, nested);
        string packageHash = Hash(outer);
        string packagePath = Path.Combine(this.TempRoot, this.Identity.PackageAssetName);
        File.WriteAllBytes(packagePath, outer);

        InstallationReleaseIdentity release = new(
            InstallationReleaseIdentity.ReviewedRepository,
            this.Identity.Tag,
            this.Identity.EmbeddedVersion,
            this.Identity.PackageAssetName,
            Commit,
            Tree,
            Sha256Digest.Parse(packageHash),
            outer.Length,
            $"{ForkReleaseIdentity.Repository}/.github/workflows/linux-alpha-release.yml@refs/tags/{this.Identity.Tag}",
            "Release",
            "linux-x64"
        );
        HashSet<string> included = new(manifestSourcePaths ?? nestedFiles.Keys, StringComparer.Ordinal);
        PackageManifest manifest = new(
            release,
            nestedFiles
                .Where(pair => included.Contains(pair.Key))
                .Select(pair =>
                {
                    string destination = pair.Key == "unix-launcher.sh" ? "StardewValley" : pair.Key;
                    int mode = manifestModeOverrides?.GetValueOrDefault(pair.Key) ?? pair.Value.Mode;
                    OwnedEntryKind kind = destination == "StardewValley"
                        ? OwnedEntryKind.Launcher
                        : OwnedEntryKind.InternalFile;
                    return new PackageManifestEntry(
                        NormalizedRelativePath.Parse(destination),
                        Sha256Digest.Parse(Hash(pair.Value.Bytes)),
                        pair.Value.Bytes.Length,
                        mode,
                        kind
                    );
                })
        );
        byte[] manifestBytes = Encoding.UTF8.GetBytes(manifest.ToCanonicalJson());
        string manifestName = VerifiedInstallerPackageFactory.GetManifestAssetName(this.Identity);
        string manifestPath = Path.Combine(this.TempRoot, manifestName);
        File.WriteAllBytes(manifestPath, manifestBytes);
        string manifestHash = Hash(manifestBytes);
        string checksums = $"{packageHash}  {this.Identity.PackageAssetName}\n{manifestHash}  {manifestName}\n";
        string metadata = this.CreateMetadata(
            packageHash,
            outer.Length,
            manifestName,
            manifestBytes.Length,
            manifestHash
        );
        VerifiedReleasePackage package = await new ReleasePackageVerifier().VerifyAsync(
            packagePath,
            checksums,
            metadata,
            this.Identity,
            Commit
        );
        return await new VerifiedInstallerPackageFactory().VerifyAsync(package, manifestPath);
    }

    private string CreateMetadata(
        string packageHash,
        long packageSize,
        string manifestName,
        long manifestSize,
        string manifestHash
    )
    {
        return JsonSerializer.Serialize(new
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
                new { name = this.Identity.PackageAssetName, size_bytes = packageSize, sha256 = packageHash },
                new { name = manifestName, size_bytes = manifestSize, sha256 = manifestHash }
            }
        });
    }

    private static byte[] CreateZip(IReadOnlyDictionary<string, (byte[] Bytes, int Mode)> files)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string path, (byte[] bytes, int mode)) in files)
            {
                ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Optimal);
                entry.ExternalAttributes = unchecked((int)((uint)(0x8000 | mode) << 16));
                using Stream output = entry.Open();
                output.Write(bytes);
            }
        }
        return stream.ToArray();
    }

    private static byte[] CreateOuterZip(string rootName, byte[] nested)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string directory in new[] { rootName, $"{rootName}/internal", $"{rootName}/internal/linux" })
            {
                ZipArchiveEntry entry = archive.CreateEntry(directory + "/", CompressionLevel.NoCompression);
                entry.ExternalAttributes = unchecked((int)((uint)(0x4000 | 493) << 16));
            }
            ZipArchiveEntry install = archive.CreateEntry($"{rootName}/internal/linux/install.dat", CompressionLevel.NoCompression);
            install.ExternalAttributes = unchecked((int)((uint)(0x8000 | 420) << 16));
            using Stream output = install.Open();
            output.Write(nested);
        }
        return stream.ToArray();
    }

    private static string Hash(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
