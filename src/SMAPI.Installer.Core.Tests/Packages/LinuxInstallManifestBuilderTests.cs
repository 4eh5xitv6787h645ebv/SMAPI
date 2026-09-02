using System.IO.Compression;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Tests.Packages;

[TestFixture]
public sealed class LinuxInstallManifestBuilderTests
{
    private const string Commit = "1111111111111111111111111111111111111111";
    private const string Tree = "2222222222222222222222222222222222222222";
    private string TempRoot = null!;
    private ForkReleaseIdentity Identity = null!;
    private string Workflow = null!;

    [SetUp]
    public void SetUp()
    {
        this.TempRoot = Path.Combine(Path.GetTempPath(), $"smapi-manifest-builder-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.TempRoot);
        this.Identity = ForkReleaseIdentity.Parse("fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2");
        this.Workflow = $"{ForkReleaseIdentity.Repository}/.github/workflows/linux-alpha-release.yml@refs/tags/{this.Identity.Tag}";
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(this.TempRoot))
            Directory.Delete(this.TempRoot, recursive: true);
    }

    [Test]
    public async Task BuildAsync_ClassifiesPayloadAndEmitsCanonicalDeterministicSchemaFourManifest()
    {
        string package = this.CreatePackage(extraLinuxSupportFiles: true);
        LinuxInstallManifestBuilder builder = new();

        LinuxInstallManifestBuildResult first = await builder.BuildAsync(
            package,
            this.Identity,
            LinuxInstallManifestBuilderTests.Commit,
            LinuxInstallManifestBuilderTests.Tree,
            this.Workflow
        );
        LinuxInstallManifestBuildResult second = await builder.BuildAsync(
            package,
            this.Identity,
            LinuxInstallManifestBuilderTests.Commit,
            LinuxInstallManifestBuilderTests.Tree,
            this.Workflow
        );

        first.GetCanonicalBytes().Should().Equal(second.GetCanonicalBytes());
        first.GetCanonicalBytes().Take(3).Should().NotEqual(new byte[] { 0xef, 0xbb, 0xbf });
        Encoding.UTF8.GetString(first.GetCanonicalBytes()).Should().NotContain("\r").And.NotContain("\n");
        first.Manifest.SchemaVersion.Should().Be(4);
        first.Manifest.ReleaseAuthorityPolicy.Should().Be(TaggedReleaseAuthorityPolicy.Create(first.Manifest.Release));
        first.Manifest.ReleaseAuthorityPolicy!.Repository.Should().NotContain("://");
        first.Manifest.Release.BuildWorkflow.Should().Be(this.Workflow);
        (string, OwnedEntryKind)[] entries = first.Manifest.Entries.Select(entry => (entry.Path.Value, entry.Kind)).ToArray();
        entries.Should().Contain(("StardewValley", OwnedEntryKind.Launcher));
        entries.Should().Contain(("StardewModdingAPI", OwnedEntryKind.RuntimeFile));
        entries.Should().Contain(("StardewModdingAPI.dll", OwnedEntryKind.RuntimeFile));
        entries.Should().Contain(("smapi-internal/config.json", OwnedEntryKind.InternalFile));
        entries.Should().Contain(("Mods/ConsoleCommands/ConsoleCommands.dll", OwnedEntryKind.BundledModFile));
        entries.Should().Contain(("Mods/SaveBackup/SaveBackup.dll", OwnedEntryKind.BundledModFile));
        first.Manifest.Entries.Single(entry => entry.Kind == OwnedEntryKind.Launcher).UnixMode.Should().Be(493).And.Match(mode => (mode & 0x49) != 0);
        GeneratedFileRecipe generated = first.Manifest.GeneratedFiles.Should().ContainSingle().Subject;
        generated.Path.Value.Should().Be("StardewModdingAPI-net6.deps.json");
        generated.Recipe.Should().Be("copy_game_deps_v1");
        generated.SourcePath.Value.Should().Be("Stardew Valley.deps.json");
    }

    [Test]
    public async Task InspectAsync_AppliesProductionStructureChecksWithoutReturningReleaseAuthority()
    {
        string package = this.CreatePackage(extraLinuxSupportFiles: true);

        LinuxPackageStructuralInspection inspection = await new LinuxPackageStructuralInspector().InspectAsync(
            package,
            this.Identity
        );

        inspection.PayloadFileCount.Should().Be(6);
        inspection.PayloadExpandedBytes.Should().BeGreaterThan(0);
        inspection.GetType().GetProperties().Select(property => property.Name).Should().Equal(
            nameof(LinuxPackageStructuralInspection.PayloadFileCount),
            nameof(LinuxPackageStructuralInspection.PayloadExpandedBytes)
        );
        inspection.GetType().GetProperties().Select(property => property.PropertyType)
            .Should().NotContain(typeof(PackageManifest)).And.NotContain(typeof(InstallationReleaseIdentity));
    }

    [Test]
    public async Task InspectAsync_RejectsTheSameCorruptNestedProductionLayoutWithoutAuthorityInputs()
    {
        string package = this.CreatePackage(corruptInstallDat: true);

        Func<Task> action = () => new LinuxPackageStructuralInspector().InspectAsync(package, this.Identity);

        await action.Should().ThrowAsync<PackageSecurityException>();
    }

    [TestCase(RequiredFileMutation.MissingGraphicalLauncher)]
    [TestCase(RequiredFileMutation.NonExecutableGraphicalLauncher)]
    [TestCase(RequiredFileMutation.EmptyGraphicalLauncher)]
    [TestCase(RequiredFileMutation.LinkedGraphicalLauncher)]
    [TestCase(RequiredFileMutation.SpecialGraphicalLauncher)]
    [TestCase(RequiredFileMutation.WrongCaseGraphicalLauncher)]
    [TestCase(RequiredFileMutation.MissingGraphicalInstaller)]
    [TestCase(RequiredFileMutation.NonExecutableGraphicalInstaller)]
    [TestCase(RequiredFileMutation.EmptyGraphicalInstaller)]
    [TestCase(RequiredFileMutation.LinkedGraphicalInstaller)]
    [TestCase(RequiredFileMutation.SpecialGraphicalInstaller)]
    [TestCase(RequiredFileMutation.WrongCaseGraphicalInstaller)]
    [TestCase(RequiredFileMutation.ExtraOuterRootFile)]
    public async Task InspectAndBuild_RequireExactOrdinaryExecutableGraphicalPackageFiles(RequiredFileMutation mutation)
    {
        string package = this.CreatePackage(requiredFileMutation: mutation);

        Func<Task> inspect = () => new LinuxPackageStructuralInspector().InspectAsync(package, this.Identity);
        Func<Task> build = () => this.BuildAsync(package);

        await inspect.Should().ThrowAsync<PackageSecurityException>();
        await build.Should().ThrowAsync<PackageSecurityException>();
    }

    [TestCase("StardewModdingAPI-net6.deps.json", 420, "unexpected")]
    [TestCase("unexpected.dll", 420, "unexpected")]
    [TestCase("smapi-internal/privileged.dll", 2541, "setuid")]
    [TestCase("smapi-internal/setgid.dll", 1508, "setgid")]
    [TestCase("smapi-internal/sticky.dll", 1005, "sticky")]
    public async Task BuildAsync_RejectsGeneratedUnexpectedAndPrivilegedPackageEntries(string path, int mode, string message)
    {
        string package = this.CreatePackage((path, "bad", 0x8000, mode));

        Func<Task> action = () => new LinuxInstallManifestBuilder().BuildAsync(
            package,
            this.Identity,
            LinuxInstallManifestBuilderTests.Commit,
            LinuxInstallManifestBuilderTests.Tree,
            this.Workflow
        );

        await action.Should().ThrowAsync<PackageSecurityException>().WithMessage($"*{message}*");
    }

    [TestCase("../smapi-internal/traversal.dll")]
    [TestCase("/smapi-internal/absolute.dll")]
    [TestCase("smapi-internal\\backslash.dll")]
    [TestCase("smapi-internal/\u0001control.dll")]
    [TestCase("smapi-internal/e\u0301.dll")]
    [TestCase("smapi-internal/trailing ")]
    [TestCase("smapi-internal/trailing.")]
    public async Task BuildAsync_RejectsUnsafeOrAmbiguousNestedPaths(string path)
    {
        string package = this.CreatePackage((path, "bad", 0x8000, 420));

        Func<Task> action = () => this.BuildAsync(package);

        await action.Should().ThrowAsync<PackageSecurityException>();
    }

    [Test]
    public async Task BuildAsync_RejectsEmbeddedNulPath()
    {
        string package = this.CreatePackage(
            ("smapi-internal/nulx.dll", "bad", 0x8000, 420),
            mutateNested: bytes => ReplaceAll(bytes, Encoding.UTF8.GetBytes("nulx.dll"), [.. "nul"u8, 0, .. ".dll"u8])
        );

        Func<Task> action = () => this.BuildAsync(package);

        await action.Should().ThrowAsync<PackageSecurityException>();
    }

    [TestCase("smapi-internal/CONFIG.json", "duplicate or case-colliding")]
    [TestCase("smapi-internal", "both a directory and file")]
    [TestCase("smapi-internal/empty/", "empty directory", 0x4000)]
    public async Task BuildAsync_RejectsCasePrefixCollisionsAndEmptyDirectories(string path, string message, int type = 0x8000)
    {
        string package = this.CreatePackage((path, "", type, 493));

        Func<Task> action = () => this.BuildAsync(package);

        await action.Should().ThrowAsync<PackageSecurityException>().WithMessage($"*{message}*");
    }

    [Test]
    public async Task BuildAsync_RejectsLinkEntriesAndUnexpectedOuterLayout()
    {
        string linkPackage = this.CreatePackage(("smapi-internal/link", "target", 0xA000, 420));
        string socketPackage = this.CreatePackage(("smapi-internal/socket", "socket", 0xC000, 420), suffix: "socket");
        string extraOuterPackage = this.CreatePackage(extraOuterFile: true, suffix: "extra");

        Func<Task> link = () => this.BuildAsync(linkPackage);
        Func<Task> socket = () => this.BuildAsync(socketPackage);
        Func<Task> outer = () => this.BuildAsync(extraOuterPackage);

        await link.Should().ThrowAsync<PackageSecurityException>().WithMessage("*link or special*");
        await socket.Should().ThrowAsync<PackageSecurityException>().WithMessage("*link or special*");
        await outer.Should().ThrowAsync<PackageSecurityException>().WithMessage("*exact Linux-only outer layout*");
    }

    [Test]
    public async Task BuildAsync_RequiresExecutableLauncher()
    {
        string package = this.CreatePackage(launcherMode: 420);

        Func<Task> action = () => this.BuildAsync(package);

        await action.Should().ThrowAsync<PackageSecurityException>().WithMessage("*launcher*executable*");
    }

    [TestCase(false, true, false)]
    [TestCase(true, false, false)]
    [TestCase(false, false, true)]
    public async Task BuildAsync_RejectsMissingDuplicateOrCorruptNestedArchive(bool omit, bool duplicate, bool corrupt)
    {
        string package = this.CreatePackage(omitInstallDat: omit, duplicateInstallDat: duplicate, corruptInstallDat: corrupt);

        Func<Task> action = () => this.BuildAsync(package);

        await action.Should().ThrowAsync<PackageSecurityException>();
    }

    [Test]
    public async Task BuildAsync_EnforcesRepresentativeArchiveCountSizeAndCompressionBounds()
    {
        string normal = this.CreatePackage();
        string compressed = this.CreatePackage(compressionBomb: true, suffix: "compressed");
        ZipPackageLimits countLimit = new(16 * 1024 * 1024, 5, 16, 8 * 1024 * 1024, 16 * 1024 * 1024, 200);
        ZipPackageLimits sizeLimit = new(new FileInfo(normal).Length - 1, 1024, 16, 8 * 1024 * 1024, 16 * 1024 * 1024, 200);
        ZipPackageLimits ratioLimit = new(16 * 1024 * 1024, 1024, 16, 8 * 1024 * 1024, 16 * 1024 * 1024, 2);

        Func<Task> count = () => this.BuildAsync(normal, countLimit);
        Func<Task> size = () => this.BuildAsync(normal, sizeLimit);
        Func<Task> ratio = () => this.BuildAsync(compressed, ratioLimit);

        await count.Should().ThrowAsync<PackageSecurityException>();
        await size.Should().ThrowAsync<PackageSecurityException>();
        await ratio.Should().ThrowAsync<PackageSecurityException>().WithMessage("*compression-ratio*");
    }

    [Test]
    public async Task BuildAsync_CancellationCleansPrivateStaging()
    {
        string package = this.CreatePackage();
        HashSet<string> before = Directory.GetDirectories(Path.GetTempPath(), "smapi-installer-verified-*").ToHashSet(StringComparer.Ordinal);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Func<Task> action = () => this.BuildAsync(package, cancellationToken: cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        Directory.GetDirectories(Path.GetTempPath(), "smapi-installer-verified-*").Should().OnlyContain(path => before.Contains(path));
    }

    [Test]
    public async Task BuildAsync_RejectsUnexpectedLinuxSupportSubdirectory()
    {
        string package = this.CreatePackage(extraLinuxSupportDirectory: true);

        Func<Task> action = () => this.BuildAsync(package);

        await action.Should().ThrowAsync<PackageSecurityException>().WithMessage("*missing its Linux installer or nested payload*");
    }

    [Test]
    public async Task BuildAsync_RejectsNonTagWorkflowWithoutProducingAuthority()
    {
        string package = this.CreatePackage();

        Func<Task> action = () => new LinuxInstallManifestBuilder().BuildAsync(
            package,
            this.Identity,
            LinuxInstallManifestBuilderTests.Commit,
            LinuxInstallManifestBuilderTests.Tree,
            $"{ForkReleaseIdentity.Repository}/.github/workflows/linux-alpha-release.yml@refs/heads/develop"
        );

        await action.Should().ThrowAsync<ArgumentException>().WithMessage("*exact reviewed release tag*");
    }

    private Task<LinuxInstallManifestBuildResult> BuildAsync(
        string package,
        ZipPackageLimits? limits = null,
        CancellationToken cancellationToken = default
    )
    {
        return new LinuxInstallManifestBuilder().BuildAsync(
            package,
            this.Identity,
            LinuxInstallManifestBuilderTests.Commit,
            LinuxInstallManifestBuilderTests.Tree,
            this.Workflow,
            limits,
            cancellationToken
        );
    }

    private string CreatePackage(
        (string Path, string Contents, int Type, int Mode)? additionalNested = null,
        bool extraOuterFile = false,
        bool extraLinuxSupportFiles = false,
        bool extraLinuxSupportDirectory = false,
        int launcherMode = 493,
        bool omitInstallDat = false,
        bool duplicateInstallDat = false,
        bool corruptInstallDat = false,
        bool compressionBomb = false,
        Func<byte[], byte[]>? mutateNested = null,
        string suffix = "",
        RequiredFileMutation requiredFileMutation = RequiredFileMutation.None
    )
    {
        string directory = suffix.Length == 0 ? this.TempRoot : Path.Combine(this.TempRoot, suffix);
        Directory.CreateDirectory(directory);
        string package = Path.Combine(directory, this.Identity.PackageAssetName);
        byte[] nested = CreateNestedArchive(additionalNested, launcherMode, compressionBomb);
        if (mutateNested != null)
            nested = mutateNested(nested);
        if (corruptInstallDat)
            nested = "not a zip"u8.ToArray();
        string root = $"SMAPI {this.Identity.EmbeddedVersion} Linux installer";
        using FileStream stream = File.Create(package);
        using ZipArchive archive = new(stream, ZipArchiveMode.Create);
        AddFile(archive, $"{root}/README.txt", "README", 420);
        AddFile(archive, $"{root}/install on Linux.sh", "#!/bin/sh", 493);
        if (requiredFileMutation != RequiredFileMutation.MissingGraphicalLauncher)
        {
            string graphicalLauncherPath = requiredFileMutation == RequiredFileMutation.WrongCaseGraphicalLauncher
                ? $"{root}/Install on Linux (graphical).sh"
                : $"{root}/install on Linux (graphical).sh";
            string graphicalLauncherContents = requiredFileMutation == RequiredFileMutation.EmptyGraphicalLauncher ? "" : "#!/bin/sh";
            int graphicalLauncherMode = requiredFileMutation == RequiredFileMutation.NonExecutableGraphicalLauncher ? 420 : 493;
            int graphicalLauncherType = requiredFileMutation switch
            {
                RequiredFileMutation.LinkedGraphicalLauncher => 0xA000,
                RequiredFileMutation.SpecialGraphicalLauncher => 0xC000,
                _ => 0x8000
            };
            AddFile(archive, graphicalLauncherPath, graphicalLauncherContents, graphicalLauncherMode, graphicalLauncherType);
        }
        AddFile(archive, $"{root}/internal/linux/SMAPI.Installer", "installer", 493);
        if (requiredFileMutation != RequiredFileMutation.MissingGraphicalInstaller)
        {
            string graphicalInstallerPath = requiredFileMutation == RequiredFileMutation.WrongCaseGraphicalInstaller
                ? $"{root}/internal/linux/smapi.installer.gui"
                : $"{root}/internal/linux/SMAPI.Installer.Gui";
            string graphicalInstallerContents = requiredFileMutation == RequiredFileMutation.EmptyGraphicalInstaller ? "" : "graphical installer";
            int graphicalInstallerMode = requiredFileMutation == RequiredFileMutation.NonExecutableGraphicalInstaller ? 420 : 493;
            int graphicalInstallerType = requiredFileMutation switch
            {
                RequiredFileMutation.LinkedGraphicalInstaller => 0xA000,
                RequiredFileMutation.SpecialGraphicalInstaller => 0xC000,
                _ => 0x8000
            };
            AddFile(archive, graphicalInstallerPath, graphicalInstallerContents, graphicalInstallerMode, graphicalInstallerType);
        }
        if (!omitInstallDat)
            AddFile(archive, $"{root}/internal/linux/install.dat", nested, 420);
        if (duplicateInstallDat)
            AddFile(archive, $"{root}/internal/linux/install.dat", nested, 420);
        if (extraLinuxSupportFiles)
        {
            AddFile(archive, $"{root}/internal/linux/SMAPI.Installer.dll", "managed support", 420);
            AddFile(archive, $"{root}/internal/linux/libhostfxr.so", "native support", 493);
        }
        if (extraLinuxSupportDirectory)
            AddFile(archive, $"{root}/internal/linux/support/runtime.dll", "nested support", 420);
        if (extraOuterFile || requiredFileMutation == RequiredFileMutation.ExtraOuterRootFile)
            AddFile(archive, $"{root}/unexpected.txt", "unexpected", 420);
        return package;
    }

    private static byte[] CreateNestedArchive(
        (string Path, string Contents, int Type, int Mode)? additional,
        int launcherMode,
        bool compressionBomb
    )
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddFile(archive, "unix-launcher.sh", "launcher", launcherMode);
            AddFile(archive, "StardewModdingAPI", "runtime", 493);
            AddFile(archive, "StardewModdingAPI.dll", "assembly", 420);
            AddFile(archive, "smapi-internal/config.json", "{}", 420);
            AddFile(archive, "Mods/ConsoleCommands/ConsoleCommands.dll", "console", 420);
            AddFile(archive, "Mods/SaveBackup/SaveBackup.dll", "backup", 420);
            if (additional is { } value)
                AddFile(archive, value.Path, value.Contents, value.Mode, value.Type);
            if (compressionBomb)
                AddFile(archive, "smapi-internal/compressed.bin", new byte[1024 * 1024], 420, compressionLevel: CompressionLevel.SmallestSize);
        }
        return stream.ToArray();
    }

    private static void AddFile(ZipArchive archive, string path, string contents, int mode, int type = 0x8000)
    {
        AddFile(archive, path, Encoding.UTF8.GetBytes(contents), mode, type);
    }

    private static void AddFile(
        ZipArchive archive,
        string path,
        byte[] contents,
        int mode,
        int type = 0x8000,
        CompressionLevel compressionLevel = CompressionLevel.NoCompression
    )
    {
        ZipArchiveEntry entry = archive.CreateEntry(path, compressionLevel);
        entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        entry.ExternalAttributes = (type | mode) << 16;
        using Stream output = entry.Open();
        output.Write(contents);
    }

    private static byte[] ReplaceAll(byte[] source, byte[] expected, byte[] replacement)
    {
        replacement.Length.Should().Be(expected.Length);
        byte[] result = source.ToArray();
        int replacements = 0;
        for (int index = 0; index <= result.Length - expected.Length; index++)
        {
            if (!result.AsSpan(index, expected.Length).SequenceEqual(expected))
                continue;
            replacement.CopyTo(result, index);
            replacements++;
        }
        replacements.Should().BeGreaterThanOrEqualTo(2);
        return result;
    }

    public enum RequiredFileMutation
    {
        None,
        MissingGraphicalLauncher,
        NonExecutableGraphicalLauncher,
        EmptyGraphicalLauncher,
        LinkedGraphicalLauncher,
        SpecialGraphicalLauncher,
        WrongCaseGraphicalLauncher,
        MissingGraphicalInstaller,
        NonExecutableGraphicalInstaller,
        EmptyGraphicalInstaller,
        LinkedGraphicalInstaller,
        SpecialGraphicalInstaller,
        WrongCaseGraphicalInstaller,
        ExtraOuterRootFile
    }
}
