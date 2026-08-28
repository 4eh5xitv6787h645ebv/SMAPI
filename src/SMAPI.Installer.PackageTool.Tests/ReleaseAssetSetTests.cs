using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.PackageTool.Tests;

[TestFixture]
public sealed class ReleaseAssetSetTests
{
    private string TempRoot = null!;
    private ForkReleaseIdentity Identity = null!;
    private ReleaseAssetSetInputs Inputs = null!;

    [SetUp]
    public void SetUp()
    {
        this.TempRoot = Path.Combine(Path.GetTempPath(), $"smapi-package-tool-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.TempRoot);
        this.Identity = ForkReleaseIdentity.Parse("fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2");
        this.Inputs = new ReleaseAssetSetInputs(
            this.Identity,
            new string('1', 40),
            new string('2', 40),
            $"{ForkReleaseIdentity.Repository}/.github/workflows/linux-alpha-release.yml@refs/tags/{this.Identity.Tag}",
            "https://github.com/4eh5xitv6787h645ebv/SMAPI/actions/runs/12/attempts/1",
            "ubuntu24-20260824.1",
            "X64",
            new string('3', 40),
            "2026-08-29T01:02:03Z",
            ".NET SDK synthetic\nVersion: 10.0.108"
        );
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(this.TempRoot))
            Directory.Delete(this.TempRoot, recursive: true);
    }

    [Test]
    public async Task CreateAsync_EmitsDeterministicQuartetAndPassesFullAuthorityChain()
    {
        string package = this.CreatePackage();
        string first = Path.Combine(this.TempRoot, "first");
        string second = Path.Combine(this.TempRoot, "second");
        ReleaseAssetSet tool = new();

        await tool.CreateAsync(package, first, this.Inputs);
        await tool.CreateAsync(package, second, this.Inputs);

        Directory.GetFiles(first).Select(Path.GetFileName).Should().BeEquivalentTo(
            this.Identity.PackageAssetName,
            VerifiedInstallerPackageFactory.GetManifestAssetName(this.Identity),
            "SHA256SUMS",
            "build-metadata.json"
        );
        foreach (string name in Directory.GetFiles(first).Select(Path.GetFileName).Select(name => name!))
            File.ReadAllBytes(Path.Combine(first, name)).Should().Equal(File.ReadAllBytes(Path.Combine(second, name)));
        string manifestName = VerifiedInstallerPackageFactory.GetManifestAssetName(this.Identity);
        string[] checksumLines = File.ReadAllLines(Path.Combine(first, "SHA256SUMS"));
        checksumLines.Should().HaveCount(2);
        checksumLines[0].Should().EndWith($"  {manifestName}");
        checksumLines[1].Should().EndWith($"  {this.Identity.PackageAssetName}");
        using JsonDocument metadata = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(first, "build-metadata.json")));
        metadata.RootElement.TryGetProperty("artifact", out _).Should().BeFalse();
        metadata.RootElement.GetProperty("artifacts").EnumerateArray().Select(value => value.GetProperty("name").GetString())
            .Should().Equal(manifestName, this.Identity.PackageAssetName);
        await tool.VerifyReleaseAsync(first, this.GetVerificationInputs());
    }

    [Test]
    public async Task VerifyReleaseAsync_RejectsTamperedMetadata()
    {
        string package = this.CreatePackage();
        string output = Path.Combine(this.TempRoot, "tamper");
        ReleaseAssetSet tool = new();
        await tool.CreateAsync(package, output, this.Inputs);
        string metadata = Path.Combine(output, "build-metadata.json");
        byte[] original = File.ReadAllBytes(metadata);
        original[^1] ^= 1;
        File.WriteAllBytes(metadata, original);

        Func<Task> action = () => tool.VerifyReleaseAsync(output, this.GetVerificationInputs());

        await action.Should().ThrowAsync<PackageSecurityException>().WithMessage("*strict bounded release metadata*");
    }

    [Test]
    public async Task CreateAsync_RefusesCandidateWorkflowAndLeavesNoQuartet()
    {
        string package = this.CreatePackage();
        string output = Path.Combine(this.TempRoot, "candidate");
        ReleaseAssetSetInputs candidate = this.Inputs with
        {
            Workflow = $"{ForkReleaseIdentity.Repository}/.github/workflows/linux-alpha-release.yml@refs/pull/177/merge"
        };

        Func<Task> action = () => new ReleaseAssetSet().CreateAsync(package, output, candidate);

        await action.Should().ThrowAsync<ArgumentException>().WithMessage("*exact reviewed tag workflow*");
        Directory.Exists(output).Should().BeFalse();
    }

    [Test]
    public async Task CliCreate_ExactTagPushContext_CreatesQuartet()
    {
        string package = this.CreatePackage();
        string output = Path.Combine(this.TempRoot, "cli-tag");

        int result = await Program.RunAsync(
            this.GetCreateArguments(package, output),
            this.GetTagPushContext().GetValueOrDefault
        );

        result.Should().Be(0);
        Directory.GetFiles(output).Should().HaveCount(4);
        await new ReleaseAssetSet().VerifyReleaseAsync(output, this.GetVerificationInputs());
    }

    [TestCase("GITHUB_EVENT_NAME", "pull_request")]
    [TestCase("GITHUB_EVENT_NAME", "workflow_dispatch")]
    [TestCase("GITHUB_REF_TYPE", "branch")]
    [TestCase("GITHUB_REF", "refs/tags/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.3")]
    [TestCase("GITHUB_WORKFLOW_REF", "4eh5xitv6787h645ebv/SMAPI/.github/workflows/linux-alpha-release.yml@refs/heads/develop")]
    [TestCase("GITHUB_REPOSITORY", "attacker/SMAPI")]
    [TestCase("GITHUB_SHA", "9999999999999999999999999999999999999999")]
    public async Task CliCreate_NonTagOrMismatchedGitHubContext_RefusesWithoutQuartet(string variable, string value)
    {
        string package = this.CreatePackage();
        string output = Path.Combine(this.TempRoot, $"cli-refusal-{variable}-{value.GetHashCode():x}");
        Dictionary<string, string> context = this.GetTagPushContext();
        context[variable] = value;

        int result = await Program.RunAsync(
            this.GetCreateArguments(package, output),
            context.GetValueOrDefault
        );

        result.Should().Be(1);
        Directory.Exists(output).Should().BeFalse();
    }

    [Test]
    public async Task VerifyReleaseAsync_RejectsPackageTamperingBeforeAuthority()
    {
        string package = this.CreatePackage();
        string output = Path.Combine(this.TempRoot, "package-tamper");
        ReleaseAssetSet tool = new();
        await tool.CreateAsync(package, output, this.Inputs);
        await File.AppendAllTextAsync(Path.Combine(output, this.Identity.PackageAssetName), "tampered", Encoding.UTF8);

        Func<Task> action = () => tool.VerifyReleaseAsync(output, this.GetVerificationInputs());

        await action.Should().ThrowAsync<PackageSecurityException>();
    }

    [TestCase("manifest")]
    [TestCase("checksums")]
    [TestCase("metadata-profile")]
    [TestCase("metadata-artifact-order")]
    [TestCase("extra-asset")]
    public async Task VerifyReleaseAsync_RejectsQuartetTamperProfileOrderAndExtraAsset(string tamper)
    {
        string package = this.CreatePackage();
        string output = Path.Combine(this.TempRoot, $"quartet-{tamper}");
        ReleaseAssetSet tool = new();
        await tool.CreateAsync(package, output, this.Inputs);
        string manifestName = VerifiedInstallerPackageFactory.GetManifestAssetName(this.Identity);

        switch (tamper)
        {
            case "manifest":
                await File.AppendAllTextAsync(Path.Combine(output, manifestName), " ", Encoding.UTF8);
                break;
            case "checksums":
                string[] checksumLines = await File.ReadAllLinesAsync(Path.Combine(output, "SHA256SUMS"));
                await File.WriteAllLinesAsync(Path.Combine(output, "SHA256SUMS"), checksumLines.Reverse());
                break;
            case "metadata-profile":
                string metadataPath = Path.Combine(output, "build-metadata.json");
                string metadata = await File.ReadAllTextAsync(metadataPath);
                await File.WriteAllTextAsync(
                    metadataPath,
                    metadata.Replace(this.Inputs.SourceTree, new string('8', 40), StringComparison.Ordinal)
                );
                break;
            case "metadata-artifact-order":
                string orderedMetadataPath = Path.Combine(output, "build-metadata.json");
                JsonNode root = JsonNode.Parse(await File.ReadAllTextAsync(orderedMetadataPath))!;
                JsonArray artifacts = root["artifacts"]!.AsArray();
                JsonNode first = artifacts[0]!.DeepClone();
                JsonNode second = artifacts[1]!.DeepClone();
                artifacts.Clear();
                artifacts.Add(second);
                artifacts.Add(first);
                await File.WriteAllTextAsync(orderedMetadataPath, root.ToJsonString());
                break;
            case "extra-asset":
                await File.WriteAllTextAsync(Path.Combine(output, "unexpected.txt"), "unexpected");
                break;
            default:
                throw new AssertionException($"Unknown tamper case '{tamper}'.");
        }

        Func<Task> action = () => tool.VerifyReleaseAsync(output, this.GetVerificationInputs());

        await action.Should().ThrowAsync<PackageSecurityException>();
    }

    [Test]
    public async Task VerifyReleaseAsync_DoesNotAuthenticateInformativeBuildInputs()
    {
        string package = this.CreatePackage();
        string output = Path.Combine(this.TempRoot, "informative");
        ReleaseAssetSet tool = new();
        await tool.CreateAsync(package, output, this.Inputs);
        string metadataPath = Path.Combine(output, "build-metadata.json");
        string metadata = await File.ReadAllTextAsync(metadataPath);
        metadata = metadata.Replace("/runs/12/attempts/1", "/runs/34/attempts/2", StringComparison.Ordinal);
        metadata = metadata.Replace("ubuntu24-20260824.1", "ubuntu24-downloaded", StringComparison.Ordinal);
        metadata = metadata.Replace(new string('3', 40), new string('4', 40), StringComparison.Ordinal);
        metadata = metadata.Replace("2026-08-29T01:02:03Z", "2026-08-30T04:05:06Z", StringComparison.Ordinal);
        await File.WriteAllTextAsync(metadataPath, metadata);

        await tool.VerifyReleaseAsync(output, this.GetVerificationInputs());
    }

    [Test]
    public async Task CreateAndVerify_RejectSymlinkHardlinkDirectoryAndFifoWithoutBlocking()
    {
        string package = this.CreatePackage();
        ReleaseAssetSet tool = new();
        string symlinkDirectory = Path.Combine(this.TempRoot, "symlink-source");
        Directory.CreateDirectory(symlinkDirectory);
        string symlink = Path.Combine(symlinkDirectory, this.Identity.PackageAssetName);
        File.CreateSymbolicLink(symlink, package);
        Func<Task> linkedCreate = () => tool.CreateAsync(symlink, Path.Combine(this.TempRoot, "symlink-output"), this.Inputs);
        await linkedCreate.Should().ThrowAsync<PackageSecurityException>().WithMessage("*single-link regular file*");

        string hardlinkDirectory = Path.Combine(this.TempRoot, "hardlink-source");
        Directory.CreateDirectory(hardlinkDirectory);
        string hardlink = Path.Combine(hardlinkDirectory, this.Identity.PackageAssetName);
        link(package, hardlink).Should().Be(0);
        Func<Task> hardlinkedCreate = () => tool.CreateAsync(hardlink, Path.Combine(this.TempRoot, "hardlink-output"), this.Inputs);
        await hardlinkedCreate.Should().ThrowAsync<PackageSecurityException>().WithMessage("*single-link regular file*");
        File.Delete(hardlink);

        string directoryOutput = Path.Combine(this.TempRoot, "directory-output");
        await tool.CreateAsync(this.CreatePackage(), directoryOutput, this.Inputs);
        string directoryChecksum = Path.Combine(directoryOutput, "SHA256SUMS");
        File.Delete(directoryChecksum);
        Directory.CreateDirectory(directoryChecksum);
        Func<Task> directoryVerify = () => tool.VerifyReleaseAsync(directoryOutput, this.GetVerificationInputs());
        await directoryVerify.Should().ThrowAsync<PackageSecurityException>();

        string fifoOutput = Path.Combine(this.TempRoot, "fifo-output");
        await tool.CreateAsync(this.CreatePackage(), fifoOutput, this.Inputs);
        string fifoChecksum = Path.Combine(fifoOutput, "SHA256SUMS");
        File.Delete(fifoChecksum);
        mkfifo(fifoChecksum, Convert.ToUInt32("600", 8)).Should().Be(0);
        Func<Task> fifoVerify = () => tool.VerifyReleaseAsync(fifoOutput, this.GetVerificationInputs());
        await fifoVerify.Should().ThrowAsync<PackageSecurityException>();
    }

    private string CreatePackage()
    {
        string package = Path.Combine(this.TempRoot, this.Identity.PackageAssetName);
        byte[] nested = CreateNestedArchive();
        string root = $"SMAPI {this.Identity.EmbeddedVersion} Linux installer";
        using FileStream stream = File.Create(package);
        using ZipArchive archive = new(stream, ZipArchiveMode.Create);
        AddFile(archive, $"{root}/README.txt", "README", 420);
        AddFile(archive, $"{root}/install on Linux.sh", "#!/bin/sh", 493);
        AddFile(archive, $"{root}/internal/linux/SMAPI.Installer", "installer", 493);
        AddFile(archive, $"{root}/internal/linux/install.dat", nested, 420);
        return package;
    }

    private ReleaseVerificationInputs GetVerificationInputs()
    {
        return new ReleaseVerificationInputs(this.Identity, this.Inputs.SourceCommit, this.Inputs.SourceTree);
    }

    private string[] GetCreateArguments(string package, string output)
    {
        string dotnetInfo = Path.Combine(this.TempRoot, "dotnet-info.txt");
        File.WriteAllText(dotnetInfo, this.Inputs.DotNetInfo);
        return
        [
            "create",
            "--asset-directory", output,
            "--tag", this.Identity.Tag,
            "--source-commit", this.Inputs.SourceCommit,
            "--source-tree", this.Inputs.SourceTree,
            "--package", package,
            "--workflow-ref", this.Inputs.Workflow,
            "--workflow-run", this.Inputs.WorkflowRun,
            "--runner-image", this.Inputs.RunnerImage,
            "--runner-arch", this.Inputs.RunnerArchitecture,
            "--reference-assemblies-commit", this.Inputs.ReferenceAssembliesCommit,
            "--timestamp-utc", this.Inputs.TimestampUtc,
            "--dotnet-info-file", dotnetInfo
        ];
    }

    private Dictionary<string, string> GetTagPushContext()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GITHUB_EVENT_NAME"] = "push",
            ["GITHUB_REF_TYPE"] = "tag",
            ["GITHUB_REF"] = $"refs/tags/{this.Identity.Tag}",
            ["GITHUB_WORKFLOW_REF"] = this.Inputs.Workflow,
            ["GITHUB_REPOSITORY"] = ForkReleaseIdentity.Repository,
            ["GITHUB_SHA"] = this.Inputs.SourceCommit
        };
    }

    private static byte[] CreateNestedArchive()
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddFile(archive, "unix-launcher.sh", "launcher", 493);
            AddFile(archive, "StardewModdingAPI", "runtime", 493);
            AddFile(archive, "StardewModdingAPI.dll", "assembly", 420);
            AddFile(archive, "StardewModdingAPI.deps.json", "{}", 420);
            AddFile(archive, "smapi-internal/config.json", "{}", 420);
            AddFile(archive, "Mods/ConsoleCommands/ConsoleCommands.dll", "console", 420);
            AddFile(archive, "Mods/SaveBackup/SaveBackup.dll", "backup", 420);
        }
        return stream.ToArray();
    }

    private static void AddFile(ZipArchive archive, string path, string contents, int mode)
    {
        AddFile(archive, path, Encoding.UTF8.GetBytes(contents), mode);
    }

    private static void AddFile(ZipArchive archive, string path, byte[] contents, int mode)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        entry.ExternalAttributes = (0x8000 | mode) << 16;
        using Stream output = entry.Open();
        output.Write(contents);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string path, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string oldPath, string newPath);
}
