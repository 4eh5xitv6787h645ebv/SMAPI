using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Tests.Packages;

[Platform("Linux")]
[SupportedOSPlatform("linux")]
internal sealed class LocalReleaseAssetImporterTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private string TempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        this.TempRoot = Path.Combine(Path.GetTempPath(), $"smapi-local-release-{Guid.NewGuid():N}");
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
    public void PublicApi_AcceptsOnlyDirectoryAndReturnsNonforgeablePathlessLease()
    {
        MethodInfo method = typeof(LocalReleaseAssetImporter).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Should().ContainSingle().Subject;
        method.Name.Should().Be(nameof(LocalReleaseAssetImporter.ImportDirectoryAsync));
        method.GetParameters().Select(parameter => parameter.ParameterType).Should().Equal(typeof(string), typeof(CancellationToken));
        typeof(LocalReleaseAssetLease).GetConstructors(BindingFlags.Public | BindingFlags.Instance).Should().BeEmpty();
        typeof(LocalReleaseAssetLease).IsSealed.Should().BeTrue();
        typeof(LocalReleaseAssetLease).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Should().Equal(nameof(LocalReleaseAssetLease.ReleaseTag));
        typeof(LocalReleaseAssetLease).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(methodInfo => !methodInfo.IsSpecialName)
            .Select(methodInfo => methodInfo.Name)
            .Should().Equal(nameof(LocalReleaseAssetLease.Bind), nameof(LocalReleaseAssetLease.DisposeAsync));
        typeof(LocalReleaseProtocolAssetPaths).GetConstructors(BindingFlags.Public | BindingFlags.Instance).Should().BeEmpty();
        typeof(LocalReleaseProtocolAssetPaths).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Should().OnlyContain(property => property.SetMethod == null);
        typeof(LocalReleaseProtocolAssetPaths).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Should().Contain(nameof(LocalReleaseProtocolAssetPaths.UntrustedSourceCommitHint));
        typeof(LocalReleaseProtocolAssetPaths).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Should().NotContain(name => name.Contains("Selected", StringComparison.Ordinal) || name.Contains("Directory", StringComparison.Ordinal));
    }

    [Test]
    public async Task ImportDirectoryAsync_SnapshotsExactSixToOnePrivateProcProjection()
    {
        ForkReleaseIdentity identity = Identity();
        string source = this.CreateAssetDirectory(identity);
        Dictionary<string, byte[]> expected = Directory.EnumerateFiles(source)
            .ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes, StringComparer.Ordinal);

        LocalReleaseAssetLease lease = await LocalReleaseAssetImporter.ImportDirectoryAsync(source);
        LocalReleaseProtocolAssetPaths paths = lease.Bind();
        string[] assetPaths = AssetPaths(paths);

        paths.ReleaseTag.Should().Be(identity.Tag);
        paths.UntrustedSourceCommitHint.Should().Be(Commit);
        assetPaths.Should().HaveCount(6)
            .And.OnlyContain(path => path.StartsWith($"/proc/{Environment.ProcessId}/fd/", StringComparison.Ordinal));
        assetPaths.Select(Path.GetDirectoryName).Distinct(StringComparer.Ordinal).Should().ContainSingle();
        assetPaths.Select(Path.GetFileName).Should().BeEquivalentTo(expected.Keys);
        foreach (string path in assetPaths)
        {
            File.ReadAllBytes(path).Should().Equal(expected[Path.GetFileName(path)]);
            File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        typeof(LocalReleaseProtocolAssetPaths).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(string))
            .Select(property => (string)property.GetValue(paths)!)
            .Should().NotContain(value => value.Contains(this.TempRoot, StringComparison.Ordinal));

        string retainedDirectory = ResolveProcTarget(Path.GetDirectoryName(assetPaths[0])!);
        Directory.Delete(source, recursive: true);
        assetPaths.Should().OnlyContain(path => File.Exists(path));
        ValueTask first = lease.DisposeAsync();
        ValueTask second = lease.DisposeAsync();
        await Task.WhenAll(first.AsTask(), second.AsTask());
        assetPaths.Should().OnlyContain(path => !File.Exists(path));
        Directory.Exists(retainedDirectory).Should().BeFalse();
        FluentActions.Invoking(lease.Bind).Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public async Task ImportDirectoryAsync_SourceMutationAfterSnapshotDoesNotChangePrivateBytes()
    {
        ForkReleaseIdentity identity = Identity();
        string source = this.CreateAssetDirectory(identity);
        string packageName = identity.PackageAssetName;
        byte[] original = File.ReadAllBytes(Path.Combine(source, packageName));

        await using LocalReleaseAssetLease lease = await LocalReleaseAssetImporter.ImportDirectoryAsync(source);
        LocalReleaseProtocolAssetPaths paths = lease.Bind();
        File.WriteAllText(Path.Combine(source, packageName), "changed after import");

        File.ReadAllBytes(paths.InstallerPackagePath).Should().Equal(original);
    }

    [Test]
    public async Task ImportDirectoryAsync_AcceptsAbsoluteFolderPathWithTrailingSeparator()
    {
        string source = this.CreateAssetDirectory(Identity()) + Path.DirectorySeparatorChar;

        await using LocalReleaseAssetLease lease = await LocalReleaseAssetImporter.ImportDirectoryAsync(source);

        lease.Bind().UntrustedSourceCommitHint.Should().Be(Commit);
    }

    [TestCase("relative/assets")]
    [TestCase("/tmp/control\npath")]
    public async Task ImportDirectoryAsync_RejectsRelativeOrControlCharacterPathsBeforeWork(string path)
    {
        Func<Task> action = () => LocalReleaseAssetImporter.ImportDirectoryAsync(path);

        await action.Should().ThrowAsync<ArgumentException>();
    }

    [TestCase("missing")]
    [TestCase("extra")]
    [TestCase("wrong-case")]
    public async Task ImportDirectoryAsync_RejectsAnythingExceptExactCanonicalSix(string scenario)
    {
        ForkReleaseIdentity identity = Identity();
        string source = this.CreateAssetDirectory(identity);
        string manifest = ReviewedGitHubReleaseUris.GetAssetName(identity, ReviewedReleaseAssetKind.InstallManifest);
        switch (scenario)
        {
            case "missing":
                File.Delete(Path.Combine(source, manifest));
                break;
            case "extra":
                this.WriteSafeFile(Path.Combine(source, "extra"), "extra");
                break;
            case "wrong-case":
                File.Move(Path.Combine(source, manifest), Path.Combine(source, manifest.ToUpperInvariant()));
                break;
        }

        Func<Task> action = () => LocalReleaseAssetImporter.ImportDirectoryAsync(source);

        await action.Should().ThrowAsync<PackageSecurityException>();
    }

    [TestCase("")]
    [TestCase("{}")]
    [TestCase("{\"source\":{\"commit\":\"ABCDEF0123456789abcdef0123456789abcdef01\"}}")]
    [TestCase("{\"source\":{\"commit\":\"0123456789abcdef0123456789abcdef01234567\",\"commit\":\"1123456789abcdef0123456789abcdef01234567\"}}")]
    public async Task ImportDirectoryAsync_RejectsMalformedOrNoncanonicalMetadataHint(string metadata)
    {
        ForkReleaseIdentity identity = Identity();
        string source = this.CreateAssetDirectory(identity);
        this.WriteSafeFile(
            Path.Combine(source, ReviewedGitHubReleaseUris.GetAssetName(identity, ReviewedReleaseAssetKind.BuildMetadata)),
            metadata
        );

        Func<Task> action = () => LocalReleaseAssetImporter.ImportDirectoryAsync(source);

        await action.Should().ThrowAsync<PackageSecurityException>();
    }

    [Test]
    public async Task ImportDirectoryAsync_SymlinkAssetIsRejectedWithoutReadingTarget()
    {
        ForkReleaseIdentity identity = Identity();
        string source = this.CreateAssetDirectory(identity);
        string name = ReviewedGitHubReleaseUris.GetAssetName(identity, ReviewedReleaseAssetKind.Checksums);
        string target = Path.Combine(this.TempRoot, "outside-secret");
        File.WriteAllText(target, "do not read");
        File.Delete(Path.Combine(source, name));
        File.CreateSymbolicLink(Path.Combine(source, name), target);

        Func<Task> action = () => LocalReleaseAssetImporter.ImportDirectoryAsync(source);

        await action.Should().ThrowAsync<PackageSecurityException>();
        File.ReadAllText(target).Should().Be("do not read");
    }

    [Test]
    public void ImportDirectory_SourceReplacementAtFinalFenceIsRejectedAndReplacementPreserved()
    {
        ForkReleaseIdentity identity = Identity();
        string source = this.CreateAssetDirectory(identity);
        string moved = source + "-moved";
        string marker = Path.Combine(source, "replacement");

        Action action = () => LocalReleaseAssetImporter.ImportDirectory(
            source,
            geteuid(),
            checkpoint: (stage, _) =>
            {
                if (stage != LocalReleaseImportCheckpoint.BeforeFinalFence)
                    return;
                Directory.Move(source, moved);
                Directory.CreateDirectory(source);
                File.WriteAllText(marker, "keep");
            }
        );

        action.Should().Throw<PackageSecurityException>();
        File.ReadAllText(marker).Should().Be("keep");
    }

    [Test]
    public void ImportDirectory_SourceAssetReplacementIsRejectedPreservedAndCleansWorkspace()
    {
        ForkReleaseIdentity identity = Identity();
        string source = this.CreateAssetDirectory(identity);
        string assetName = ReviewedGitHubReleaseUris.GetAssetName(identity, ReviewedReleaseAssetKind.Checksums);
        string assetPath = Path.Combine(source, assetName);
        string? namedWorkspace = null;

        Action action = () => LocalReleaseAssetImporter.ImportDirectory(
            source,
            geteuid(),
            workspaceFactory: () =>
            {
                PrivateReleaseAssetWorkspace workspace = PrivateReleaseAssetWorkspace.Create();
                namedWorkspace = ResolveProcTarget(workspace.ProcPath);
                return workspace;
            },
            checkpoint: (stage, _) =>
            {
                if (stage != LocalReleaseImportCheckpoint.BeforeFinalFence)
                    return;
                File.Delete(assetPath);
                this.WriteSafeFile(assetPath, "replacement stays");
            }
        );

        action.Should().Throw<PackageSecurityException>();
        File.ReadAllText(assetPath).Should().Be("replacement stays");
        Directory.Exists(namedWorkspace).Should().BeFalse();
    }

    [Test]
    public void ImportDirectory_InPlaceSourceAssetMutationAtFinalFenceIsRejectedPreservedAndCleansWorkspace()
    {
        ForkReleaseIdentity identity = Identity();
        string source = this.CreateAssetDirectory(identity);
        string assetName = ReviewedGitHubReleaseUris.GetAssetName(identity, ReviewedReleaseAssetKind.Checksums);
        string assetPath = Path.Combine(source, assetName);
        LinuxFileIdentity initialRoot;
        using (LinuxAnchoredFileSystem sourceAuthority = new(source))
            initialRoot = sourceAuthority.GetCurrentRootIdentity();
        string? namedWorkspace = null;

        Action action = () => LocalReleaseAssetImporter.ImportDirectory(
            source,
            geteuid(),
            workspaceFactory: () =>
            {
                PrivateReleaseAssetWorkspace workspace = PrivateReleaseAssetWorkspace.Create();
                namedWorkspace = ResolveProcTarget(workspace.ProcPath);
                return workspace;
            },
            checkpoint: (stage, _) =>
            {
                if (stage != LocalReleaseImportCheckpoint.BeforeFinalFence)
                    return;
                File.SetUnixFileMode(assetPath, UnixFileMode.UserRead);
                using LinuxAnchoredFileSystem sourceAuthority = new(source);
                sourceAuthority.GetCurrentRootIdentity().Should().Be(
                    initialRoot,
                    "an in-place leaf mutation must reach the per-file final fence"
                );
            }
        );

        action.Should().Throw<PackageSecurityException>();
        File.GetUnixFileMode(assetPath).Should().Be(UnixFileMode.UserRead);
        Directory.Exists(namedWorkspace).Should().BeFalse();
    }

    [Test]
    public void ImportDirectory_CancellationAfterFirstCopyCleansExactPrivateWorkspace()
    {
        ForkReleaseIdentity identity = Identity();
        string source = this.CreateAssetDirectory(identity);
        using CancellationTokenSource cancellation = new();
        string? namedWorkspace = null;

        Action action = () => LocalReleaseAssetImporter.ImportDirectory(
            source,
            geteuid(),
            cancellation.Token,
            workspaceFactory: () =>
            {
                PrivateReleaseAssetWorkspace workspace = PrivateReleaseAssetWorkspace.Create();
                namedWorkspace = ResolveProcTarget(workspace.ProcPath);
                return workspace;
            },
            checkpoint: (stage, _) =>
            {
                if (stage == LocalReleaseImportCheckpoint.AssetCopied)
                    cancellation.Cancel();
            }
        );

        action.Should().Throw<OperationCanceledException>();
        Directory.Exists(namedWorkspace).Should().BeFalse();
    }

    [Test]
    public void ImportDirectory_CancellationImmediatelyBeforeLeaseTransferCleansExactPrivateWorkspace()
    {
        ForkReleaseIdentity identity = Identity();
        string source = this.CreateAssetDirectory(identity);
        using CancellationTokenSource cancellation = new();
        string? namedWorkspace = null;

        Action action = () => LocalReleaseAssetImporter.ImportDirectory(
            source,
            geteuid(),
            cancellation.Token,
            workspaceFactory: () =>
            {
                PrivateReleaseAssetWorkspace workspace = PrivateReleaseAssetWorkspace.Create();
                namedWorkspace = ResolveProcTarget(workspace.ProcPath);
                return workspace;
            },
            checkpoint: (stage, _) =>
            {
                if (stage == LocalReleaseImportCheckpoint.BeforeLeaseTransfer)
                    cancellation.Cancel();
            }
        );

        action.Should().Throw<OperationCanceledException>();
        Directory.Exists(namedWorkspace).Should().BeFalse();
    }

    [Test]
    public void SourcePermissionValidatorsRejectWrongOwnerWritableExecutableAndSpecialModes()
    {
        uint owner = geteuid();
        LinuxFileIdentity safeDirectory = FileIdentity(LinuxAnchoredEntryKind.Directory, 0x1c0, owner, size: 0);
        LinuxFileIdentity safeFile = FileIdentity(LinuxAnchoredEntryKind.RegularFile, 0x180, owner, size: 1);

        FluentActions.Invoking(() => LocalReleaseAssetImporter.AssertSourceDirectory(safeDirectory, owner)).Should().NotThrow();
        FluentActions.Invoking(() => LocalReleaseAssetImporter.AssertSourceAsset(safeFile, owner, 1)).Should().NotThrow();
        FluentActions.Invoking(() => LocalReleaseAssetImporter.AssertSourceDirectory(safeDirectory with { OwnerUserId = owner + 1 }, owner))
            .Should().Throw<PackageSecurityException>();
        FluentActions.Invoking(() => LocalReleaseAssetImporter.AssertSourceDirectory(safeDirectory with { UnixMode = 0x1d0 }, owner))
            .Should().Throw<PackageSecurityException>();
        FluentActions.Invoking(() => LocalReleaseAssetImporter.AssertSourceAsset(safeFile with { UnixMode = 0x1c0 }, owner, 1))
            .Should().Throw<PackageSecurityException>();
        FluentActions.Invoking(() => LocalReleaseAssetImporter.AssertSourceAsset(safeFile with { SpecialModeBits = 0x800 }, owner, 1))
            .Should().Throw<PackageSecurityException>();
        FluentActions.Invoking(() => LocalReleaseAssetImporter.AssertSourceAsset(safeFile with { LinkCount = 2 }, owner, 1))
            .Should().Throw<PackageSecurityException>();
        FluentActions.Invoking(() => LocalReleaseAssetImporter.AssertSourceAsset(safeFile with { Size = 2 }, owner, 1))
            .Should().Throw<PackageSecurityException>();
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
            this.WriteSafeFile(Path.Combine(source, ReviewedGitHubReleaseUris.GetAssetName(identity, kind)), content);
        }
        return source;
    }

    private void WriteSafeFile(string path, string content)
    {
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static ForkReleaseIdentity Identity()
    {
        return ForkReleaseIdentity.Parse("fork-4eh5xitv6787h645ebv-linux-v4.5.4-alpha.2");
    }

    private static string[] AssetPaths(LocalReleaseProtocolAssetPaths paths)
    {
        return
        [
            paths.InstallerPackagePath,
            paths.InstallManifestPath,
            paths.ChecksumsPath,
            paths.BuildMetadataPath,
            paths.AttestationBundlePath,
            paths.AttestationBundleChecksumPath
        ];
    }

    private static LinuxFileIdentity FileIdentity(LinuxAnchoredEntryKind kind, int mode, uint owner, long size)
    {
        return new LinuxFileIdentity(kind, 1, 1, 1, 1, size, mode, 1, 1, 1, 1)
        {
            OwnerUserId = owner,
            SpecialModeBits = 0
        };
    }

    private static string ResolveProcTarget(string procPath)
    {
        return Directory.ResolveLinkTarget(procPath, returnFinalTarget: false)?.FullName
            ?? throw new IOException("The retained workspace descriptor link couldn't be resolved for testing.");
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint geteuid();
}
