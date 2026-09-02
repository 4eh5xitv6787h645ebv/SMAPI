using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Tests.Packages;

[TestFixture]
[Platform("Linux")]
[NonParallelizable]
internal sealed class VerifiedGitHubAttestationBundleTests
{
    private const string Tag = "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1";
    private const string Commit = "1111111111111111111111111111111111111111";
    private const string Tree = "2222222222222222222222222222222222222222";
    private string TempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        this.TempRoot = Path.Combine(Path.GetTempPath(), $"b{Guid.NewGuid():N}"[..10]);
        Directory.CreateDirectory(this.TempRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(this.TempRoot))
            Directory.Delete(this.TempRoot, recursive: true);
    }

    [Test]
    public async Task VerifyAsync_ExactFilesPublishImmutableAuthorityUnaffectedBySourceReplacement()
    {
        using VerifiedInstallerPackage package = await this.CreateVerifiedInstallerPackageAsync();
        byte[] bytes = "local attestation evidence"u8.ToArray();
        (string bundlePath, string checksumPath) = this.WriteBundleFiles(package, bytes);
        using VerifiedGitHubAttestationBundle authority = await new VerifiedGitHubAttestationBundleFactory().VerifyAsync(
            package,
            bundlePath,
            checksumPath
        );
        string moved = bundlePath + ".original";
        File.Move(bundlePath, moved);
        File.WriteAllText(bundlePath, "replacement");
        File.Delete(bundlePath);
        using LinuxSealedFileLease lease = authority.LeaseForExternalRead();

        File.ReadAllBytes(lease.ProcPath).Should().Equal(bytes);
        Action overwrite = () => File.WriteAllBytes(lease.ProcPath, "changed"u8.ToArray());
        Action resize = () =>
        {
            using FileStream stream = new(lease.ProcPath, FileMode.Open, FileAccess.ReadWrite);
            stream.SetLength(bytes.Length + 1);
        };
        overwrite.Should().Throw<Exception>();
        resize.Should().Throw<Exception>();
        File.ReadAllBytes(lease.ProcPath).Should().Equal(bytes);
        authority.Release.Should().Be(package.Release);
        authority.AssetName.Should().Be(VerifiedGitHubAttestationBundleFactory.GetBundleAssetName(package.Release));
        authority.Sha256.Should().Be(Sha256Digest.Hash(bytes));
        authority.SizeBytes.Should().Be(bytes.LongLength);
        authority.Dispose();
        File.ReadAllBytes(lease.ProcPath).Should().Equal(bytes);
        Action reuse = () => authority.LeaseForExternalRead().Dispose();
        reuse.Should().Throw<ObjectDisposedException>();
        lease.Dispose();
        File.Exists(lease.ProcPath).Should().BeFalse();
    }

    [TestCase("wrong-bundle-filename")]
    [TestCase("wrong-checksum-filename")]
    [TestCase("wrong-checksum")]
    [TestCase("noncanonical-checksum")]
    [TestCase("oversize-bundle")]
    [TestCase("oversize-checksum")]
    [TestCase("invalid-bundle-utf8")]
    [TestCase("invalid-checksum-utf8")]
    [CancelAfter(5000)]
    public async Task VerifyAsync_RejectsMalformedMismatchedOrOversizedInputs(string kind)
    {
        using VerifiedInstallerPackage package = await this.CreateVerifiedInstallerPackageAsync();
        byte[] bytes = "local attestation evidence"u8.ToArray();
        (string bundlePath, string checksumPath) = this.WriteBundleFiles(package, bytes);
        switch (kind)
        {
            case "wrong-bundle-filename":
                string wrongBundle = Path.Combine(Path.GetDirectoryName(bundlePath)!, "bundle.jsonl");
                File.Move(bundlePath, wrongBundle);
                bundlePath = wrongBundle;
                break;
            case "wrong-checksum-filename":
                string wrongChecksum = Path.Combine(Path.GetDirectoryName(checksumPath)!, "bundle.sha256");
                File.Move(checksumPath, wrongChecksum);
                checksumPath = wrongChecksum;
                break;
            case "wrong-checksum":
                string text = File.ReadAllText(checksumPath);
                File.WriteAllText(checksumPath, $"{(text[0] == '0' ? '1' : '0')}{text[1..]}");
                break;
            case "noncanonical-checksum":
                File.AppendAllText(checksumPath, "\n");
                break;
            case "oversize-bundle":
                bytes = new byte[VerifiedGitHubAttestationBundleFactory.MaximumBundleBytes + 1];
                File.WriteAllBytes(bundlePath, bytes);
                WriteBundleChecksum(bundlePath, checksumPath, bytes);
                break;
            case "oversize-checksum":
                File.WriteAllText(checksumPath, new string('x', VerifiedGitHubAttestationBundleFactory.MaximumChecksumBytes + 1));
                break;
            case "invalid-bundle-utf8":
                bytes = [0xff, 0xfe, 0xfd];
                File.WriteAllBytes(bundlePath, bytes);
                WriteBundleChecksum(bundlePath, checksumPath, bytes);
                break;
            case "invalid-checksum-utf8":
                File.WriteAllBytes(checksumPath, [0xff, 0xfe, 0xfd]);
                break;
        }

        Func<Task> verify = () => new VerifiedGitHubAttestationBundleFactory().VerifyAsync(package, bundlePath, checksumPath);

        PackageSecurityException exception = (await verify.Should().ThrowAsync<PackageSecurityException>()).Which;
        exception.FailureKind.Should().Be(PackageSecurityFailureKind.ProvenanceRejected);
    }

    [TestCase("bundle", "symlink")]
    [TestCase("bundle", "hardlink")]
    [TestCase("bundle", "fifo")]
    [TestCase("bundle", "socket")]
    [TestCase("checksum", "symlink")]
    [TestCase("checksum", "hardlink")]
    [TestCase("checksum", "fifo")]
    [TestCase("checksum", "socket")]
    [CancelAfter(5000)]
    public async Task VerifyAsync_RejectsLinkedOrBlockingInputsPromptly(string asset, string kind)
    {
        using VerifiedInstallerPackage package = await this.CreateVerifiedInstallerPackageAsync();
        (string bundlePath, string checksumPath) = this.WriteBundleFiles(package, "local evidence"u8.ToArray());
        string selected = asset == "bundle" ? bundlePath : checksumPath;
        string target = selected + ".target";
        File.Move(selected, target);
        Socket? socket = null;
        string previousDirectory = Environment.CurrentDirectory;
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
                    mkfifo(selected, 384).Should().Be(0, $"mkfifo(2) failed with errno {Marshal.GetLastWin32Error()}");
                    break;
                case "socket":
                    Environment.CurrentDirectory = Path.GetDirectoryName(selected)!;
                    socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    socket.Bind(new UnixDomainSocketEndPoint(Path.GetFileName(selected)));
                    break;
            }

            Func<Task> verify = () => new VerifiedGitHubAttestationBundleFactory().VerifyAsync(package, bundlePath, checksumPath);
            PackageSecurityException exception = (await verify.Should().ThrowAsync<PackageSecurityException>()).Which;
            exception.FailureKind.Should().Be(PackageSecurityFailureKind.Unclassified);
        }
        finally
        {
            socket?.Dispose();
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    [Test]
    public async Task VerifyAsync_CancellationPublishesNoDescriptor()
    {
        using VerifiedInstallerPackage package = await this.CreateVerifiedInstallerPackageAsync();
        (string bundlePath, string checksumPath) = this.WriteBundleFiles(package, "local evidence"u8.ToArray());
        HashSet<string> before = FindBundleDescriptors();
        using CancellationTokenSource cancellation = new();
        VerifiedGitHubAttestationBundleFactory factory = new(_ => cancellation.Cancel());

        Func<Task> verify = () => factory.VerifyAsync(
            package,
            bundlePath,
            checksumPath,
            cancellation.Token
        );

        await verify.Should().ThrowAsync<OperationCanceledException>();
        FindBundleDescriptors().Should().BeEquivalentTo(before);
    }

    [Test]
    public async Task VerifyAsync_LocalRetainedAuthorityFailureRemainsUnclassifiedAndPublishesNoDescriptor()
    {
        using VerifiedInstallerPackage package = await this.CreateVerifiedInstallerPackageAsync();
        (string bundlePath, string checksumPath) = this.WriteBundleFiles(package, "local evidence"u8.ToArray());
        HashSet<string> before = FindBundleDescriptors();
        VerifiedGitHubAttestationBundleFactory factory = new(
            _ => throw new PackageSecurityException("Synthetic retained-authority failure.")
        );

        Func<Task> verify = () => factory.VerifyAsync(package, bundlePath, checksumPath);

        PackageSecurityException exception = (await verify.Should().ThrowAsync<PackageSecurityException>()).Which;
        exception.FailureKind.Should().Be(PackageSecurityFailureKind.Unclassified);
        FindBundleDescriptors().Should().BeEquivalentTo(before);
    }

    [Test]
    public async Task VerifyAsync_FailureDoesNotExposeBundleBytesOrCallerPaths()
    {
        using VerifiedInstallerPackage package = await this.CreateVerifiedInstallerPackageAsync();
        byte[] bytes = "private synthetic bundle marker"u8.ToArray();
        (string bundlePath, string checksumPath) = this.WriteBundleFiles(package, bytes);
        string checksum = File.ReadAllText(checksumPath);
        File.WriteAllText(checksumPath, $"{(checksum[0] == '0' ? '1' : '0')}{checksum[1..]}");

        Func<Task> verify = () => new VerifiedGitHubAttestationBundleFactory().VerifyAsync(package, bundlePath, checksumPath);

        Exception exception = (await verify.Should().ThrowAsync<PackageSecurityException>()).Which;
        exception.ToString().Should().NotContain(Encoding.UTF8.GetString(bytes));
        exception.ToString().Should().NotContain(bundlePath);
        exception.ToString().Should().NotContain(checksumPath);
    }

    private async Task<VerifiedInstallerPackage> CreateVerifiedInstallerPackageAsync()
    {
        ForkReleaseIdentity fork = ForkReleaseIdentity.Parse(Tag);
        byte[] packageBytes = "synthetic installer package"u8.ToArray();
        string packageHash = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
        string workflow = $"{ForkReleaseIdentity.Repository}/.github/workflows/linux-alpha-release.yml@refs/tags/{Tag}";
        InstallationReleaseIdentity releaseIdentity = new(
            InstallationReleaseIdentity.ReviewedRepository,
            Tag,
            fork.EmbeddedVersion,
            fork.PackageAssetName,
            Commit,
            Tree,
            Sha256Digest.Parse(packageHash),
            packageBytes.LongLength,
            workflow,
            "Release",
            "linux-x64"
        );
        PackageManifest manifest = new(
            releaseIdentity,
            [new PackageManifestEntry(NormalizedRelativePath.Parse("StardewValley"), Sha256Digest.Parse(new string('d', 64)), 42, 493, OwnedEntryKind.Launcher)]
        );
        byte[] manifestBytes = Encoding.UTF8.GetBytes(manifest.ToCanonicalJson());
        string manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        string manifestName = VerifiedInstallerPackageFactory.GetManifestAssetName(fork);
        string packagePath = Path.Combine(this.TempRoot, fork.PackageAssetName);
        string manifestPath = Path.Combine(this.TempRoot, manifestName);
        File.WriteAllBytes(packagePath, packageBytes);
        File.WriteAllBytes(manifestPath, manifestBytes);
        string checksums = $"{packageHash}  {fork.PackageAssetName}\n{manifestHash}  {manifestName}\n";
        string metadata = JsonSerializer.Serialize(new
        {
            schema_version = 1,
            release = new { version = fork.EmbeddedVersion, tag = fork.Tag },
            source = new { repository = ForkReleaseIdentity.RepositoryUrl, commit = Commit, tree = Tree },
            build = new { workflow, configuration = "Release", runtime_identifier = "linux-x64" },
            artifacts = new object[]
            {
                new { name = fork.PackageAssetName, size_bytes = packageBytes.LongLength, sha256 = packageHash },
                new { name = manifestName, size_bytes = manifestBytes.LongLength, sha256 = manifestHash }
            }
        });
        VerifiedReleasePackage? release = await new ReleasePackageVerifier().VerifyAsync(packagePath, checksums, metadata, fork, Commit);
        try
        {
            VerifiedInstallerPackage result = await new VerifiedInstallerPackageFactory().VerifyAsync(release, manifestPath);
            release = null;
            return result;
        }
        finally
        {
            if (release is not null)
                await release.DisposeAsync();
        }
    }

    private (string BundlePath, string ChecksumPath) WriteBundleFiles(VerifiedInstallerPackage package, byte[] bytes)
    {
        string directory = Path.Combine(this.TempRoot, $"e{Guid.NewGuid():N}"[..10]);
        Directory.CreateDirectory(directory);
        string bundlePath = Path.Combine(directory, VerifiedGitHubAttestationBundleFactory.GetBundleAssetName(package.Release));
        string checksumPath = Path.Combine(directory, VerifiedGitHubAttestationBundleFactory.GetChecksumAssetName(package.Release));
        File.WriteAllBytes(bundlePath, bytes);
        WriteBundleChecksum(bundlePath, checksumPath, bytes);
        return (bundlePath, checksumPath);
    }

    private static void WriteBundleChecksum(string bundlePath, string checksumPath, byte[] bytes)
    {
        string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        File.WriteAllText(checksumPath, $"{sha256}  {Path.GetFileName(bundlePath)}\n", new UTF8Encoding(false));
    }

    private static HashSet<string> FindBundleDescriptors()
    {
        HashSet<string> paths = new(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles($"/proc/{Environment.ProcessId}/fd"))
        {
            try
            {
                if (new FileInfo(path).LinkTarget?.Contains("memfd:smapi-installer-attestation-bundle", StringComparison.Ordinal) == true)
                    paths.Add(path);
            }
            catch (IOException)
            {
                // An unrelated runtime descriptor can close while procfs is enumerated.
            }
        }
        return paths;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string existingPath, string newPath);

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string path, uint mode);
}
