using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Win32.SafeHandles;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Tests.Packages;

[Platform("Linux")]
[SupportedOSPlatform("linux")]
internal sealed class LinuxParentProcAssetSourceTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private readonly ForkReleaseIdentity Identity = ForkReleaseIdentity.Parse("fork-4eh5xitv6787h645ebv-linux-v4.5.4-alpha.2");

    [Test]
    public void Capture_OpensExactSixPrivateFilesAndSurvivesNamedWorkspaceReplacement()
    {
        string root = CreateWorkspace(this.Identity);
        try
        {
            using LinuxAnchoredFileSystem workspace = new(root);
            using LinuxParentProcFdAuthority authority = CreateSelfAuthority();
            LinuxTaggedReleaseAssetSet assets = CreateAssets(workspace.ProcPath, this.Identity);
            using LinuxParentProcAssetSource source = authority.Capture(assets, this.Identity, WorkspaceIdentity(workspace), CancellationToken.None);

            string moved = root + ".moved";
            Directory.Move(root, moved);
            Directory.CreateDirectory(root);
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            using RetainedReleaseAssetFile package = source.Open(this.Identity.PackageAssetName, "installer package");
            package.ReadAllBytesAsync(1024, true, CancellationToken.None).GetAwaiter().GetResult()
                .Should().Equal([1, 2, 3]);
            Directory.Delete(root);
            Directory.Delete(moved, recursive: true);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            if (Directory.Exists(root + ".moved"))
                Directory.Delete(root + ".moved", recursive: true);
        }
    }

    [TestCase("/proc/{pid}/fd/{fd}/{leaf}/")]
    [TestCase("/proc/{pid}/fd/{fd}//{leaf}")]
    [TestCase("/proc/0/fd/{fd}/{leaf}")]
    [TestCase("/proc/01/fd/{fd}/{leaf}")]
    [TestCase("/proc/+1/fd/{fd}/{leaf}")]
    [TestCase("/proc/{pid}/fd/01/{leaf}")]
    [TestCase("/proc/{pid}/fd/-1/{leaf}")]
    [TestCase("/proc/{pid}/FD/{fd}/{leaf}")]
    [TestCase("/proc/{pid}/fd/{fd}/./{leaf}")]
    [TestCase("/proc/{pid}/fd/{fd}/../{leaf}")]
    [TestCase("/proc/{pid}/fd/{fd}\\{leaf}")]
    [TestCase("/proc/{pid}/fd/{fd}/{leaf}.wrong")]
    [TestCase("/proc/2147483648/fd/{fd}/{leaf}")]
    [TestCase("/proc/{pid}/fd/2147483648/{leaf}")]
    public void Capture_RejectsNonCanonicalProcGrammar(string template)
    {
        string root = CreateWorkspace(this.Identity);
        try
        {
            using LinuxAnchoredFileSystem workspace = new(root);
            using LinuxParentProcFdAuthority authority = CreateSelfAuthority();
            LinuxTaggedReleaseAssetSet valid = CreateAssets(workspace.ProcPath, this.Identity);
            string descriptor = workspace.ProcPath.Split('/')[4];
            string invalid = template
                .Replace("{pid}", Environment.ProcessId.ToString(), StringComparison.Ordinal)
                .Replace("{fd}", descriptor, StringComparison.Ordinal)
                .Replace("{leaf}", this.Identity.PackageAssetName, StringComparison.Ordinal);

            Action capture = () => authority.Capture(valid with { PackagePath = invalid }, this.Identity, WorkspaceIdentity(workspace), CancellationToken.None);

            capture.Should().Throw<PackageSecurityException>().Which.Message.Should().NotContain(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Capture_RejectsWrongLeafCrossDescriptorAndWrongParent()
    {
        string root = CreateWorkspace(this.Identity);
        try
        {
            using LinuxAnchoredFileSystem workspace = new(root);
            using LinuxAnchoredFileSystem second = new(root);
            using LinuxParentProcFdAuthority authority = CreateSelfAuthority();
            LinuxTaggedReleaseAssetSet valid = CreateAssets(workspace.ProcPath, this.Identity);
            string wrongLeaf = $"{workspace.ProcPath}/{ReleasePackageVerifier.ChecksumAssetName}";
            string crossDescriptor = $"{second.ProcPath}/{ReleasePackageVerifier.ChecksumAssetName}";
            string wrongParent = valid.PackagePath.Replace($"/proc/{Environment.ProcessId}/", $"/proc/{Environment.ProcessId + 1}/", StringComparison.Ordinal);

            FluentActions.Invoking(() => authority.Capture(valid with { PackagePath = wrongLeaf }, this.Identity, WorkspaceIdentity(workspace), CancellationToken.None))
                .Should().Throw<PackageSecurityException>();
            FluentActions.Invoking(() => authority.Capture(valid with { ChecksumsPath = crossDescriptor }, this.Identity, WorkspaceIdentity(workspace), CancellationToken.None))
                .Should().Throw<PackageSecurityException>();
            FluentActions.Invoking(() => authority.Capture(valid with { PackagePath = wrongParent }, this.Identity, WorkspaceIdentity(workspace), CancellationToken.None))
                .Should().Throw<PackageSecurityException>();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Capture_RejectsExtraMissingAndUnsafeModes()
    {
        string root = CreateWorkspace(this.Identity);
        try
        {
            using LinuxAnchoredFileSystem workspace = new(root);
            using LinuxParentProcFdAuthority authority = CreateSelfAuthority();
            LinuxTaggedReleaseAssetSet assets = CreateAssets(workspace.ProcPath, this.Identity);

            File.WriteAllBytes(Path.Combine(root, "extra"), [1]);
            File.SetUnixFileMode(Path.Combine(root, "extra"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            FluentActions.Invoking(() => authority.Capture(assets, this.Identity, WorkspaceIdentity(workspace), CancellationToken.None))
                .Should().Throw<PackageSecurityException>();
            File.Delete(Path.Combine(root, "extra"));

            File.SetUnixFileMode(assets.PackagePath.Replace(workspace.ProcPath, root, StringComparison.Ordinal), UnixFileMode.UserRead);
            FluentActions.Invoking(() => authority.Capture(assets, this.Identity, WorkspaceIdentity(workspace), CancellationToken.None))
                .Should().Throw<PackageSecurityException>();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Constructor_CapturesOneStableParentWithoutReanchoring()
    {
        int calls = 0;
        using LinuxParentProcFdAuthority authority = new(
            () => { calls++; return Environment.ProcessId; },
            path => new LinuxAnchoredFileSystem(path)
        );

        calls.Should().Be(2);
        authority.ParentProcessId.Should().Be(Environment.ProcessId);
    }

    [Test]
    public void Constructor_RejectsReparentDuringCapture()
    {
        int calls = 0;
        Action construct = () => _ = new LinuxParentProcFdAuthority(
            () => ++calls == 1 ? Environment.ProcessId : Environment.ProcessId + 1,
            path => new LinuxAnchoredFileSystem(path)
        );

        construct.Should().Throw<PackageSecurityException>();
    }

    [TestCase("kind")]
    [TestCase("owner")]
    [TestCase("mode")]
    [TestCase("special")]
    [TestCase("links")]
    public void Constructor_RejectsWrongParentProcDirectoryIdentity(string mismatch)
    {
        Action construct = () => _ = new LinuxParentProcFdAuthority(
            () => Environment.ProcessId,
            path => new LinuxAnchoredFileSystem(path),
            () => GetEffectiveUserId(),
            directory =>
            {
                LinuxFileIdentity identity = directory.GetCurrentRootIdentity();
                return mismatch switch
                {
                    "kind" => identity with { Kind = LinuxAnchoredEntryKind.RegularFile },
                    "owner" => identity with { OwnerUserId = checked(identity.OwnerUserId + 1) },
                    "mode" => identity with { UnixMode = 0x1c0 },
                    "special" => identity with { SpecialModeBits = 0x800 },
                    "links" => identity with { LinkCount = 3 },
                    _ => throw new AssertionException("Unknown mismatch.")
                };
            }
        );

        construct.Should().Throw<PackageSecurityException>();
    }

    [Test]
    public void Constructor_RejectsRootBeforeOpeningProcAuthority()
    {
        bool opened = false;
        Action construct = () => _ = new LinuxParentProcFdAuthority(
            () => Environment.ProcessId,
            path => { opened = true; return new LinuxAnchoredFileSystem(path); },
            () => 0
        );

        construct.Should().Throw<PrivilegedInstallerException>();
        opened.Should().BeFalse();
    }

    [Test]
    public void Capture_RejectsParentChangeAtStartAndFinalFence()
    {
        string root = CreateWorkspace(this.Identity);
        try
        {
            using LinuxAnchoredFileSystem workspace = new(root);
            int observedParent = Environment.ProcessId;
            using LinuxParentProcFdAuthority startAuthority = new(
                () => observedParent,
                path => new LinuxAnchoredFileSystem(path)
            );
            observedParent++;
            FluentActions.Invoking(() => startAuthority.Capture(
                CreateAssets(workspace.ProcPath, this.Identity),
                this.Identity,
                WorkspaceIdentity(workspace),
                CancellationToken.None
            )).Should().Throw<PackageSecurityException>();

            observedParent = Environment.ProcessId;
            using LinuxParentProcFdAuthority finalAuthority = new(
                () => observedParent,
                path => new LinuxAnchoredFileSystem(path),
                faults: new LinuxParentProcAssetSourceFaults(BeforeFinalFence: () => observedParent++)
            );
            FluentActions.Invoking(() => finalAuthority.Capture(
                CreateAssets(workspace.ProcPath, this.Identity),
                this.Identity,
                WorkspaceIdentity(workspace),
                CancellationToken.None
            )).Should().Throw<PackageSecurityException>();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Capture_RejectsPreCaptureDescriptorReuseByWorkspaceIdentity()
    {
        string originalRoot = CreateWorkspace(this.Identity);
        string replacementRoot = CreateWorkspace(this.Identity);
        try
        {
            LinuxAnchoredFileSystem original = new(originalRoot);
            using LinuxAnchoredFileSystem replacement = new(replacementRoot);
            string procRoot = original.ProcPath;
            int originalDescriptor = int.Parse(procRoot.Split('/')[4]);
            int replacementDescriptor = int.Parse(replacement.ProcPath.Split('/')[4]);
            ProtocolProcWorkspaceIdentity expected = WorkspaceIdentity(original);
            original.Dispose();
            int reusedDescriptor = dup2(replacementDescriptor, originalDescriptor);
            reusedDescriptor.Should().Be(originalDescriptor);
            using SafeFileHandle reused = new((IntPtr)reusedDescriptor, ownsHandle: true);
            using LinuxParentProcFdAuthority authority = CreateSelfAuthority();

            FluentActions.Invoking(() => authority.Capture(
                CreateAssets(procRoot, this.Identity),
                this.Identity,
                expected,
                CancellationToken.None
            )).Should().Throw<PackageSecurityException>();
        }
        finally
        {
            Directory.Delete(originalRoot, recursive: true);
            Directory.Delete(replacementRoot, recursive: true);
        }
    }

    [Test]
    public void Capture_RejectsLeafReplacementAtFinalFenceAndClosesHandles()
    {
        string root = CreateWorkspace(this.Identity);
        int before = CountFileDescriptors();
        try
        {
            using LinuxAnchoredFileSystem workspace = new(root);
            string package = Path.Combine(root, this.Identity.PackageAssetName);
            using LinuxParentProcFdAuthority authority = new(
                () => Environment.ProcessId,
                path => new LinuxAnchoredFileSystem(path),
                faults: new LinuxParentProcAssetSourceFaults(BeforeFinalFence: () =>
                {
                    File.Delete(package);
                    File.WriteAllBytes(package, [1, 2, 3]);
                    File.SetUnixFileMode(package, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                })
            );

            FluentActions.Invoking(() => authority.Capture(
                CreateAssets(workspace.ProcPath, this.Identity),
                this.Identity,
                WorkspaceIdentity(workspace),
                CancellationToken.None
            )).Should().Throw<PackageSecurityException>();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
        CountFileDescriptors().Should().Be(before);
    }

    [Test]
    public void Capture_RejectsChangedWorkspaceCtimeEvenWhenIdentityModeAndExactNamesAreRestored()
    {
        string root = CreateWorkspace(this.Identity);
        try
        {
            using LinuxAnchoredFileSystem workspace = new(root);
            ProtocolProcWorkspaceIdentity expected = WorkspaceIdentity(workspace);
            bool ctimeChanged = false;
            using LinuxParentProcFdAuthority authority = new(
                () => Environment.ProcessId,
                path => new LinuxAnchoredFileSystem(path),
                faults: new LinuxParentProcAssetSourceFaults(BeforeFinalFence: () =>
                {
                    string transient = Path.Combine(root, "transient");
                    File.WriteAllBytes(transient, [1]);
                    File.Delete(transient);
                    LinuxFileIdentity changed = workspace.GetCurrentRootIdentity();
                    ctimeChanged = changed.ChangeSeconds != expected.ChangeSeconds
                        || changed.ChangeNanoseconds != expected.ChangeNanoseconds;
                })
            );

            FluentActions.Invoking(() => authority.Capture(
                CreateAssets(workspace.ProcPath, this.Identity),
                this.Identity,
                expected,
                CancellationToken.None
            )).Should().Throw<PackageSecurityException>();
            ctimeChanged.Should().BeTrue();
            workspace.EnumerateEntryNames(maximumEntries: 7).Should().HaveCount(6);
            workspace.GetCurrentRootIdentity().UnixMode.Should().Be(0x1c0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void RetainedSource_TransfersEachLogicalAssetOnlyOnce()
    {
        string root = CreateWorkspace(this.Identity);
        try
        {
            using LinuxAnchoredFileSystem workspace = new(root);
            using LinuxParentProcFdAuthority authority = CreateSelfAuthority();
            using LinuxParentProcAssetSource source = authority.Capture(
                CreateAssets(workspace.ProcPath, this.Identity),
                this.Identity,
                WorkspaceIdentity(workspace),
                CancellationToken.None
            );
            using RetainedReleaseAssetFile package = source.Open(this.Identity.PackageAssetName, "installer package");
            FluentActions.Invoking(() => source.Open(this.Identity.PackageAssetName, "installer package"))
                .Should().Throw<PackageSecurityException>();
            FluentActions.Invoking(() => source.Open("unknown", "unknown"))
                .Should().Throw<PackageSecurityException>();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Capture_CancellationAfterLeafDisposesPartialHandles()
    {
        string root = CreateWorkspace(this.Identity);
        try
        {
            using LinuxAnchoredFileSystem workspace = new(root);
            void CancelledCapture()
            {
                using CancellationTokenSource cancellation = new();
                using LinuxParentProcFdAuthority authority = new(
                    () => Environment.ProcessId,
                    path => new LinuxAnchoredFileSystem(path),
                    faults: new LinuxParentProcAssetSourceFaults(index => { if (index == 2) cancellation.Cancel(); })
                );
                Action capture = () => authority.Capture(CreateAssets(workspace.ProcPath, this.Identity), this.Identity, WorkspaceIdentity(workspace), cancellation.Token);
                capture.Should().Throw<OperationCanceledException>();
            }

            CancelledCapture(); // warm one-time runtime and assertion-library handles before measuring.
            int before = CountFileDescriptors();
            for (int index = 0; index < 20; index++)
                CancelledCapture();
            CountFileDescriptors().Should().Be(before);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static LinuxParentProcFdAuthority CreateSelfAuthority()
        => new(() => Environment.ProcessId, path => new LinuxAnchoredFileSystem(path));

    private static LinuxTaggedReleaseAssetSet CreateAssets(string procRoot, ForkReleaseIdentity identity)
        => new(
            identity.Tag,
            Commit,
            $"{procRoot}/{identity.PackageAssetName}",
            $"{procRoot}/{ReleasePackageVerifier.ChecksumAssetName}",
            $"{procRoot}/{ReleasePackageVerifier.BuildMetadataAssetName}",
            $"{procRoot}/{VerifiedInstallerPackageFactory.GetManifestAssetName(identity)}",
            $"{procRoot}/{VerifiedGitHubAttestationBundleFactory.GetBundleAssetName(identity)}",
            $"{procRoot}/{VerifiedGitHubAttestationBundleFactory.GetChecksumAssetName(identity)}",
            "/tmp/gh"
        );

    private static string CreateWorkspace(ForkReleaseIdentity identity)
    {
        string root = Path.Combine(Path.GetTempPath(), $"smapi-proc-source-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string[] names =
        [
            identity.PackageAssetName,
            ReleasePackageVerifier.ChecksumAssetName,
            ReleasePackageVerifier.BuildMetadataAssetName,
            VerifiedInstallerPackageFactory.GetManifestAssetName(identity),
            VerifiedGitHubAttestationBundleFactory.GetBundleAssetName(identity),
            VerifiedGitHubAttestationBundleFactory.GetChecksumAssetName(identity)
        ];
        foreach (string name in names)
        {
            string path = Path.Combine(root, name);
            File.WriteAllBytes(path, [1, 2, 3]);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return root;
    }

    private static int CountFileDescriptors() => Directory.EnumerateFileSystemEntries($"/proc/{Environment.ProcessId}/fd").Count();

    private static ProtocolProcWorkspaceIdentity WorkspaceIdentity(LinuxAnchoredFileSystem workspace)
    {
        LinuxFileIdentity identity = workspace.GetCurrentRootIdentity();
        return new ProtocolProcWorkspaceIdentity(
            identity.DeviceMajor,
            identity.DeviceMinor,
            identity.Inode,
            identity.ChangeSeconds,
            identity.ChangeNanoseconds
        );
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", SetLastError = true)]
    private static extern int dup2(int oldDescriptor, int newDescriptor);
}
