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
using StardewModdingAPI.Installer.Core.Transactions;

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

    [Test]
    public async Task PublicInspectAsync_PreCancelledDoesNotMutateAndLeavesPackageUsable()
    {
        Dictionary<string, (byte[] Bytes, int Mode)> files = new(StringComparer.Ordinal)
        {
            ["unix-launcher.sh"] = ("#!/bin/sh\nexec smapi\n"u8.ToArray(), 493),
            ["smapi-internal/a.dll"] = ("verified assembly"u8.ToArray(), 420)
        };
        VerifiedInstallerPackage authority = await this.CreateAuthorityAsync(files);
        await using VerifiedPackageContent content = await new VerifiedPackageContentFactory().ExtractAsync(authority);
        string game = Path.Combine(this.TempRoot, "cancelled-inspection-game");
        Directory.CreateDirectory(game);
        string launcher = Path.Combine(game, "StardewValley");
        await File.WriteAllTextAsync(launcher, "vanilla launcher");
        File.SetUnixFileMode(launcher, (UnixFileMode)493);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Func<Task> cancelled = () => new LinuxInstallerEngine().InspectAsync(
            game,
            InstallationAction.Install,
            content,
            cancellationToken: cancellation.Token
        );

        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        Directory.Exists(Path.Combine(game, ".smapi-installer")).Should().BeFalse();
        using InspectedInstallationState retry = await new LinuxInstallerEngine().InspectAsync(game, InstallationAction.Install, content);
        retry.Plan.CanExecute.Should().BeTrue();
    }

    [Test]
    public void RepairCandidateApprovalHasNoCallerMintedAuthoritySurface()
    {
        typeof(ModifiedFileReplacementCandidate).GetConstructors().Should().BeEmpty();
        typeof(LinuxInstallerEngine).GetMethod("InspectRepairAsync").Should().BeNull();
        typeof(ModifiedFileReplacementApproval).IsNotPublic.Should().BeTrue();
    }

    [Test]
    public async Task PublicInspection_DisposeDoesNotTakePackageOwnership()
    {
        Dictionary<string, (byte[] Bytes, int Mode)> files = new(StringComparer.Ordinal)
        {
            ["unix-launcher.sh"] = ("#!/bin/sh\nexec smapi\n"u8.ToArray(), 493),
            ["smapi-internal/a.dll"] = ("verified assembly"u8.ToArray(), 420)
        };
        VerifiedInstallerPackage authority = await this.CreateAuthorityAsync(files);
        await using VerifiedPackageContent content = await new VerifiedPackageContentFactory().ExtractAsync(authority);
        string game = Path.Combine(this.TempRoot, "borrowed-package-game");
        Directory.CreateDirectory(game);
        await File.WriteAllTextAsync(Path.Combine(game, "StardewValley"), "vanilla launcher");
        File.SetUnixFileMode(Path.Combine(game, "StardewValley"), (UnixFileMode)493);
        LinuxInstallerEngine engine = new();
        InspectedInstallationState first = await engine.InspectAsync(game, InstallationAction.Install, content);

        first.Dispose();
        using InspectedInstallationState second = await engine.InspectAsync(game, InstallationAction.Install, content);

        second.Plan.CanExecute.Should().BeTrue();
    }

    [Test]
    public async Task PublicExecution_DisposedBorrowedPackageRejectsBeforeMutation()
    {
        Dictionary<string, (byte[] Bytes, int Mode)> files = new(StringComparer.Ordinal)
        {
            ["unix-launcher.sh"] = ("#!/bin/sh\nexec smapi\n"u8.ToArray(), 493),
            ["smapi-internal/a.dll"] = ("verified assembly"u8.ToArray(), 420)
        };
        VerifiedInstallerPackage authority = await this.CreateAuthorityAsync(files);
        VerifiedPackageContent content = await new VerifiedPackageContentFactory().ExtractAsync(authority);
        string game = Path.Combine(this.TempRoot, "disposed-package-game");
        Directory.CreateDirectory(game);
        string launcher = Path.Combine(game, "StardewValley");
        await File.WriteAllTextAsync(launcher, "vanilla launcher");
        File.SetUnixFileMode(launcher, (UnixFileMode)493);
        LinuxInstallerEngine engine = new();
        using InspectedInstallationState inspection = await engine.InspectAsync(game, InstallationAction.Install, content);
        await content.DisposeAsync();

        Func<Task> execute = () => engine.ExecuteAsync(inspection, inspection.ConfirmationDigest);

        await execute.Should().ThrowAsync<ObjectDisposedException>();
        Directory.Exists(Path.Combine(game, ".smapi-installer")).Should().BeFalse();
        (await File.ReadAllTextAsync(launcher)).Should().Be("vanilla launcher");
    }

    [Test]
    public async Task PublicExecution_PreCancelledLeavesInspectionAndBorrowedPackageReusable()
    {
        Dictionary<string, (byte[] Bytes, int Mode)> files = new(StringComparer.Ordinal)
        {
            ["unix-launcher.sh"] = ("#!/bin/sh\nexec smapi\n"u8.ToArray(), 493),
            ["smapi-internal/a.dll"] = ("verified assembly"u8.ToArray(), 420)
        };
        VerifiedInstallerPackage authority = await this.CreateAuthorityAsync(files);
        await using VerifiedPackageContent content = await new VerifiedPackageContentFactory().ExtractAsync(authority);
        string game = Path.Combine(this.TempRoot, "cancelled-execution-game");
        Directory.CreateDirectory(game);
        string launcher = Path.Combine(game, "StardewValley");
        await File.WriteAllTextAsync(launcher, "vanilla launcher");
        File.SetUnixFileMode(launcher, (UnixFileMode)493);
        LinuxInstallerEngine engine = new();
        using InspectedInstallationState inspection = await engine.InspectAsync(game, InstallationAction.Install, content);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Func<Task> cancelled = () => engine.ExecuteAsync(inspection, inspection.ConfirmationDigest, cancellation.Token);

        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        Directory.Exists(Path.Combine(game, ".smapi-installer")).Should().BeFalse();
        (await engine.ExecuteAsync(inspection, inspection.ConfirmationDigest)).Status.Should().Be(TransactionStatus.Committed);
    }

    [Test]
    public async Task PublicFacade_VerifiedInstallAndApprovedRepair_UsesExactPackageBytes()
    {
        Dictionary<string, (byte[] Bytes, int Mode)> files = new(StringComparer.Ordinal)
        {
            ["unix-launcher.sh"] = ("#!/bin/sh\nexec smapi\n"u8.ToArray(), 493),
            ["smapi-internal/a.dll"] = ("verified assembly"u8.ToArray(), 420)
        };
        VerifiedInstallerPackage authority = await this.CreateAuthorityAsync(files);
        await using VerifiedPackageContent content = await new VerifiedPackageContentFactory().ExtractAsync(authority);
        string game = Path.Combine(this.TempRoot, "facade-game");
        Directory.CreateDirectory(game);
        string launcher = Path.Combine(game, "StardewValley");
        await File.WriteAllTextAsync(launcher, "vanilla launcher");
        File.SetUnixFileMode(launcher, (UnixFileMode)493);
        LinuxInstallerEngine engine = new();
        using (InspectedInstallationState install = await engine.InspectAsync(game, InstallationAction.Install, content))
            (await engine.ExecuteAsync(install, install.ConfirmationDigest)).Status.Should().Be(TransactionStatus.Committed);
        string assembly = Path.Combine(game, "smapi-internal", "a.dll");
        await File.WriteAllTextAsync(assembly, "locally modified");
        File.SetUnixFileMode(assembly, (UnixFileMode)420);
        using InspectedInstallationState blocked = await engine.InspectAsync(game, InstallationAction.Repair, content);
        ModifiedFileReplacementCandidate candidate = blocked.ModifiedFileReplacementCandidates.Should().ContainSingle().Subject;
        candidate.Path.Value.Should().Be("smapi-internal/a.dll");
        candidate.ObservedSha256.Should().Be(Sha256Digest.Hash("locally modified"u8));
        candidate.ObservedSizeBytes.Should().Be("locally modified"u8.Length);
        candidate.ObservedUnixMode.Should().Be(420);
        candidate.ObservedFileType.Should().Be(RecoveryFileType.RegularFile);

        using InspectedInstallationState repair = await engine.ApproveRepairAsync(blocked, [candidate]);
        (await engine.ExecuteAsync(repair, repair.ConfirmationDigest)).Status.Should().Be(TransactionStatus.Committed);

        (await File.ReadAllTextAsync(assembly)).Should().Be("verified assembly");
        File.GetUnixFileMode(assembly).Should().Be((UnixFileMode)420);
        using InspectedInstallationState reusable = await engine.InspectAsync(game, InstallationAction.Repair, content);
        reusable.Plan.CanExecute.Should().BeTrue();
    }

    [Test]
    public async Task ApproveRepair_DisposeDuringCandidateEnumerationRejects()
    {
        Dictionary<string, (byte[] Bytes, int Mode)> files = new(StringComparer.Ordinal)
        {
            ["unix-launcher.sh"] = ("#!/bin/sh\nexec smapi\n"u8.ToArray(), 493),
            ["smapi-internal/a.dll"] = ("verified assembly"u8.ToArray(), 420)
        };
        VerifiedInstallerPackage authority = await this.CreateAuthorityAsync(files);
        await using VerifiedPackageContent content = await new VerifiedPackageContentFactory().ExtractAsync(authority);
        string game = Path.Combine(this.TempRoot, "dispose-during-approval-game");
        Directory.CreateDirectory(game);
        await File.WriteAllTextAsync(Path.Combine(game, "StardewValley"), "vanilla launcher");
        File.SetUnixFileMode(Path.Combine(game, "StardewValley"), (UnixFileMode)493);
        LinuxInstallerEngine engine = new();
        using (InspectedInstallationState install = await engine.InspectAsync(game, InstallationAction.Install, content))
            await engine.ExecuteAsync(install, install.ConfirmationDigest);
        await File.WriteAllTextAsync(Path.Combine(game, "smapi-internal/a.dll"), "modified");
        InspectedInstallationState blocked = await engine.InspectAsync(game, InstallationAction.Repair, content);
        ModifiedFileReplacementCandidate candidate = blocked.ModifiedFileReplacementCandidates.Single();

        IEnumerable<ModifiedFileReplacementCandidate> DisposeThenYield()
        {
            blocked.Dispose();
            yield return candidate;
        }
        Func<Task> approve = () => engine.ApproveRepairAsync(blocked, DisposeThenYield());

        await approve.Should().ThrowAsync<ObjectDisposedException>();
        (await File.ReadAllTextAsync(Path.Combine(game, "smapi-internal/a.dll"))).Should().Be("modified");
    }

    [Test]
    public async Task ApproveRepair_UnboundedCandidateEnumerableRejectsAtIssuedCount()
    {
        Dictionary<string, (byte[] Bytes, int Mode)> files = new(StringComparer.Ordinal)
        {
            ["unix-launcher.sh"] = ("#!/bin/sh\nexec smapi\n"u8.ToArray(), 493),
            ["smapi-internal/a.dll"] = ("verified assembly"u8.ToArray(), 420)
        };
        VerifiedInstallerPackage authority = await this.CreateAuthorityAsync(files);
        await using VerifiedPackageContent content = await new VerifiedPackageContentFactory().ExtractAsync(authority);
        string game = Path.Combine(this.TempRoot, "bounded-approval-game");
        Directory.CreateDirectory(game);
        await File.WriteAllTextAsync(Path.Combine(game, "StardewValley"), "vanilla launcher");
        File.SetUnixFileMode(Path.Combine(game, "StardewValley"), (UnixFileMode)493);
        LinuxInstallerEngine engine = new();
        using (InspectedInstallationState install = await engine.InspectAsync(game, InstallationAction.Install, content))
            await engine.ExecuteAsync(install, install.ConfirmationDigest);
        await File.WriteAllTextAsync(Path.Combine(game, "smapi-internal/a.dll"), "modified");
        using InspectedInstallationState blocked = await engine.InspectAsync(game, InstallationAction.Repair, content);
        ModifiedFileReplacementCandidate candidate = blocked.ModifiedFileReplacementCandidates.Single();

        IEnumerable<ModifiedFileReplacementCandidate> RepeatForever()
        {
            while (true)
                yield return candidate;
        }
        Func<Task> approve = () => engine.ApproveRepairAsync(blocked, RepeatForever());

        await approve.Should().ThrowAsync<ArgumentException>().WithMessage("*bounded issued set*");
        (await File.ReadAllTextAsync(Path.Combine(game, "smapi-internal/a.dll"))).Should().Be("modified");
    }

    [Test]
    public async Task PublicExecution_StaleFailureDoesNotDisposeBorrowedPackage()
    {
        Dictionary<string, (byte[] Bytes, int Mode)> files = new(StringComparer.Ordinal)
        {
            ["unix-launcher.sh"] = ("#!/bin/sh\nexec smapi\n"u8.ToArray(), 493),
            ["smapi-internal/a.dll"] = ("verified assembly"u8.ToArray(), 420)
        };
        VerifiedInstallerPackage authority = await this.CreateAuthorityAsync(files);
        await using VerifiedPackageContent content = await new VerifiedPackageContentFactory().ExtractAsync(authority);
        string game = Path.Combine(this.TempRoot, "failed-borrow-game");
        Directory.CreateDirectory(game);
        string launcher = Path.Combine(game, "StardewValley");
        await File.WriteAllTextAsync(launcher, "vanilla launcher");
        File.SetUnixFileMode(launcher, (UnixFileMode)493);
        LinuxInstallerEngine engine = new();
        using InspectedInstallationState stale = await engine.InspectAsync(game, InstallationAction.Install, content);
        await File.WriteAllTextAsync(launcher, "changed after inspection");

        Func<Task> execute = () => engine.ExecuteAsync(stale, stale.ConfirmationDigest);

        await execute.Should().ThrowAsync<Exception>();
        string retryGame = Path.Combine(this.TempRoot, "failed-borrow-retry-game");
        Directory.CreateDirectory(retryGame);
        string retryLauncher = Path.Combine(retryGame, "StardewValley");
        await File.WriteAllTextAsync(retryLauncher, "vanilla launcher");
        File.SetUnixFileMode(retryLauncher, (UnixFileMode)493);
        using InspectedInstallationState retry = await engine.InspectAsync(retryGame, InstallationAction.Install, content);
        retry.Plan.CanExecute.Should().BeTrue();
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
