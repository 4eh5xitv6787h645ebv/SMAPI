using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Gui.Backend;
using StardewModdingAPI.Installer.Gui.Frontend;

namespace StardewModdingAPI.Installer.Gui.Tests;

[Platform("Linux")]
[SupportedOSPlatform("linux")]
internal sealed class LocalReleasePackageServiceTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private string TempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        this.TempRoot = Path.Combine(Path.GetTempPath(), $"smapi-gui-local-release-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.TempRoot);
        File.SetUnixFileMode(
            this.TempRoot,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        );
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(this.TempRoot))
            Directory.Delete(this.TempRoot, recursive: true);
    }

    [Test]
    public async Task PrepareAsync_RealCoreImportRetainsPrivateExactSixUntilPreparedOwnerIsDisposed()
    {
        ForkReleaseIdentity identity = ForkReleaseIdentity.Parse(
            "fork-4eh5xitv6787h645ebv-linux-v4.5.4-alpha.2"
        );
        string selectedDirectory = this.CreateAssetDirectory(identity);
        Dictionary<string, byte[]> expected = Directory.EnumerateFiles(selectedDirectory)
            .ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes, StringComparer.Ordinal);
        LocalReleasePackageService service = new();

        IPreparedReleasePackage prepared = await service.PrepareAsync(selectedDirectory);
        InstallerPackageOpenInput package = prepared.Package;
        string[] retainedPaths = GetAssetPaths(package);

        package.ReleaseTag.Should().Be(identity.Tag);
        package.ExpectedSourceCommit.Should().Be(Commit);
        package.ProcWorkspaceIdentity.Should().NotBeNull();
        retainedPaths.Should().HaveCount(6)
            .And.OnlyContain(path => path.StartsWith($"/proc/{Environment.ProcessId}/fd/", StringComparison.Ordinal));
        retainedPaths.Select(Path.GetDirectoryName).Distinct(StringComparer.Ordinal).Should().ContainSingle();
        retainedPaths.Select(Path.GetFileName).Should().BeEquivalentTo(expected.Keys);
        foreach (string path in retainedPaths)
            File.ReadAllBytes(path).Should().Equal(expected[Path.GetFileName(path)]);
        typeof(InstallerPackageOpenInput).GetProperties()
            .Where(property => property.PropertyType == typeof(string))
            .Select(property => (string)property.GetValue(package)!)
            .Should().NotContain(value => value.Contains(this.TempRoot, StringComparison.Ordinal));

        string retainedDirectory = ResolveProcTarget(Path.GetDirectoryName(retainedPaths[0])!);
        Directory.Delete(selectedDirectory, recursive: true);
        retainedPaths.Should().OnlyContain(path => File.Exists(path));

        ValueTask first = prepared.DisposeAsync();
        ValueTask second = prepared.DisposeAsync();
        await Task.WhenAll(first.AsTask(), second.AsTask());

        retainedPaths.Should().OnlyContain(path => !File.Exists(path));
        Directory.Exists(retainedDirectory).Should().BeFalse();
        FluentActions.Invoking(() => _ = prepared.Package).Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public async Task PrepareAsync_PreCancelledRequestDoesNotReturnPreparedAuthority()
    {
        ForkReleaseIdentity identity = ForkReleaseIdentity.Parse(
            "fork-4eh5xitv6787h645ebv-linux-v4.5.4-alpha.2"
        );
        string selectedDirectory = this.CreateAssetDirectory(identity);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        LocalReleasePackageService service = new();

        Func<Task> prepare = async () => await service.PrepareAsync(selectedDirectory, cancellation.Token);

        await prepare.Should().ThrowAsync<OperationCanceledException>();
        Directory.EnumerateFiles(selectedDirectory).Should().HaveCount(6);
    }

    private string CreateAssetDirectory(ForkReleaseIdentity identity)
    {
        string source = Path.Combine(this.TempRoot, $"assets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        File.SetUnixFileMode(
            source,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        );
        foreach (ReviewedReleaseAssetKind kind in Enum.GetValues<ReviewedReleaseAssetKind>())
        {
            string content = kind == ReviewedReleaseAssetKind.BuildMetadata
                ? $"{{\"source\":{{\"commit\":\"{Commit}\"}}}}"
                : $"local-{kind}";
            string path = Path.Combine(source, ReviewedGitHubReleaseUris.GetAssetName(identity, kind));
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return source;
    }

    private static string[] GetAssetPaths(InstallerPackageOpenInput package)
    {
        return
        [
            package.PackagePath,
            package.InstallManifestPath,
            package.ChecksumsPath,
            package.BuildMetadataPath,
            package.AttestationBundlePath,
            package.AttestationBundleChecksumPath
        ];
    }

    private static string ResolveProcTarget(string procPath)
    {
        return Directory.ResolveLinkTarget(procPath, returnFinalTarget: false)?.FullName
            ?? throw new IOException("The retained workspace descriptor link couldn't be resolved for testing.");
    }
}
