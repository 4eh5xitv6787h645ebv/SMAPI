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
    public async Task BuildAsync_ClassifiesPayloadAndEmitsCanonicalDeterministicSchemaThreeManifest()
    {
        string package = this.CreatePackage();
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
        first.Manifest.SchemaVersion.Should().Be(3);
        first.Manifest.Release.BuildWorkflow.Should().Be(this.Workflow);
        (string, OwnedEntryKind)[] entries = first.Manifest.Entries.Select(entry => (entry.Path.Value, entry.Kind)).ToArray();
        entries.Should().Contain(("StardewValley", OwnedEntryKind.Launcher));
        entries.Should().Contain(("StardewModdingAPI", OwnedEntryKind.RuntimeFile));
        entries.Should().Contain(("StardewModdingAPI.dll", OwnedEntryKind.RuntimeFile));
        entries.Should().Contain(("smapi-internal/config.json", OwnedEntryKind.InternalFile));
        entries.Should().Contain(("Mods/ConsoleCommands/ConsoleCommands.dll", OwnedEntryKind.BundledModFile));
        entries.Should().Contain(("Mods/SaveBackup/SaveBackup.dll", OwnedEntryKind.BundledModFile));
        GeneratedFileRecipe generated = first.Manifest.GeneratedFiles.Should().ContainSingle().Subject;
        generated.Path.Value.Should().Be("StardewModdingAPI-net6.deps.json");
        generated.Recipe.Should().Be("copy_game_deps_v1");
        generated.SourcePath.Value.Should().Be("Stardew Valley.deps.json");
    }

    [TestCase("StardewModdingAPI-net6.deps.json", 420, "unexpected")]
    [TestCase("unexpected.dll", 420, "unexpected")]
    [TestCase("smapi-internal/privileged.dll", 2541, "setuid")]
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

    [Test]
    public async Task BuildAsync_RejectsLinkEntriesAndUnexpectedOuterLayout()
    {
        string linkPackage = this.CreatePackage(("smapi-internal/link", "target", 0xA000, 420));
        string extraOuterPackage = this.CreatePackage(extraOuterFile: true, suffix: "extra");

        Func<Task> link = () => this.BuildAsync(linkPackage);
        Func<Task> outer = () => this.BuildAsync(extraOuterPackage);

        await link.Should().ThrowAsync<PackageSecurityException>().WithMessage("*link or special*");
        await outer.Should().ThrowAsync<PackageSecurityException>().WithMessage("*exact Linux-only outer layout*");
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

    private Task<LinuxInstallManifestBuildResult> BuildAsync(string package)
    {
        return new LinuxInstallManifestBuilder().BuildAsync(
            package,
            this.Identity,
            LinuxInstallManifestBuilderTests.Commit,
            LinuxInstallManifestBuilderTests.Tree,
            this.Workflow
        );
    }

    private string CreatePackage(
        (string Path, string Contents, int Type, int Mode)? additionalNested = null,
        bool extraOuterFile = false,
        string suffix = ""
    )
    {
        string directory = suffix.Length == 0 ? this.TempRoot : Path.Combine(this.TempRoot, suffix);
        Directory.CreateDirectory(directory);
        string package = Path.Combine(directory, this.Identity.PackageAssetName);
        byte[] nested = CreateNestedArchive(additionalNested);
        string root = $"SMAPI {this.Identity.EmbeddedVersion} Linux installer";
        using FileStream stream = File.Create(package);
        using ZipArchive archive = new(stream, ZipArchiveMode.Create);
        AddFile(archive, $"{root}/README.txt", "README", 420);
        AddFile(archive, $"{root}/install on Linux.sh", "#!/bin/sh", 493);
        AddFile(archive, $"{root}/internal/linux/SMAPI.Installer", "installer", 493);
        AddFile(archive, $"{root}/internal/linux/install.dat", nested, 420);
        if (extraOuterFile)
            AddFile(archive, $"{root}/unexpected.txt", "unexpected", 420);
        return package;
    }

    private static byte[] CreateNestedArchive((string Path, string Contents, int Type, int Mode)? additional)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddFile(archive, "unix-launcher.sh", "launcher", 493);
            AddFile(archive, "StardewModdingAPI", "runtime", 493);
            AddFile(archive, "StardewModdingAPI.dll", "assembly", 420);
            AddFile(archive, "smapi-internal/config.json", "{}", 420);
            AddFile(archive, "Mods/ConsoleCommands/ConsoleCommands.dll", "console", 420);
            AddFile(archive, "Mods/SaveBackup/SaveBackup.dll", "backup", 420);
            if (additional is { } value)
                AddFile(archive, value.Path, value.Contents, value.Mode, value.Type);
        }
        return stream.ToArray();
    }

    private static void AddFile(ZipArchive archive, string path, string contents, int mode, int type = 0x8000)
    {
        AddFile(archive, path, Encoding.UTF8.GetBytes(contents), mode, type);
    }

    private static void AddFile(ZipArchive archive, string path, byte[] contents, int mode, int type = 0x8000)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        entry.ExternalAttributes = (type | mode) << 16;
        using Stream output = entry.Open();
        output.Write(contents);
    }
}
