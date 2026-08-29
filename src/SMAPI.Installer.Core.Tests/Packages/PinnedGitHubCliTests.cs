using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Win32.SafeHandles;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Tests.Packages;

[TestFixture]
[Platform("Linux")]
[NonParallelizable]
[SupportedOSPlatform("linux")]
public sealed class PinnedGitHubCliTests
{
    private const string MemfdName = "memfd:smapi-installer-pinned-gh";
    private string TempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        this.TempRoot = Path.Combine(Path.GetTempPath(), $"smapi-pinned-gh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.TempRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(this.TempRoot))
            Directory.Delete(this.TempRoot, recursive: true);
    }

    [Test]
    public async Task OfficialIdentity_IsExactAndProductionFactoryCannotAcceptATestOverride()
    {
        PinnedGitHubCli.OfficialVersion.Should().Be("2.92.0");
        PinnedGitHubCli.OfficialArchiveSha256.Should().Be("b57848131bdf0c229cd35e1f2a51aa718199858b2e728410b37e89a428943ec4");
        PinnedGitHubCli.OfficialBinarySizeBytes.Should().Be(39_805_090);
        PinnedGitHubCli.OfficialBinarySha256.Should().Be("b58e487e37c00c114aa07f14987ce12f5e5abf12b9da8a38937b65ef218f6772");

        (string path, byte[] bytes, _) = this.CreateScript();
        Func<Task> productionOpen = () => PinnedGitHubCli.OpenAsync(path);

        await productionOpen.Should().ThrowAsync<PackageSecurityException>().WithMessage("*pinned byte length*");
        bytes.Should().NotBeEmpty();
    }

    [Test]
    [CancelAfter(5000)]
    public async Task OpenForTesting_ExecutesExactSealedScriptAfterSourceReplacementAndDeletion()
    {
        (string path, byte[] bytes, PinnedGitHubCliTestIdentity identity) = this.CreateScript();
        using PinnedGitHubCli executable = await PinnedGitHubCli.OpenForTestingAsync(path, identity);
        string original = path + ".original";
        File.Move(path, original);
        File.WriteAllBytes(path, "replacement"u8.ToArray());
        File.Delete(path);
        using LinuxSealedFileLease lease = executable.LeaseForExecution();

        File.ReadAllBytes(lease.ProcPath).Should().Equal(bytes);
        File.GetUnixFileMode(lease.ProcPath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserExecute);
        Action overwrite = () => File.WriteAllBytes(lease.ProcPath, "changed"u8.ToArray());
        Exception writeError = overwrite.Should().Throw<Exception>().Which;
        (writeError is IOException or UnauthorizedAccessException).Should().BeTrue();

        ProcessStartInfo start = new(lease.ProcPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("fixture-argument");
        using Process process = Process.Start(start) ?? throw new AssertionException("The pinned CLI fixture didn't start.");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(4));
        string output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
        string error = await process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);

        process.ExitCode.Should().Be(0, error);
        output.Should().Be("pinned-cli-ok:fixture-argument");
        File.ReadAllBytes(lease.ProcPath).Should().Equal(bytes);
    }

    [Test]
    public async Task LeaseForExecution_SurvivesAuthorityDisposalAndPinsDescriptorUntilLeaseDisposal()
    {
        (string path, byte[] bytes, PinnedGitHubCliTestIdentity identity) = this.CreateScript();
        using PinnedGitHubCli executable = await PinnedGitHubCli.OpenForTestingAsync(path, identity);
        using LinuxSealedFileLease lease = executable.LeaseForExecution();
        string procPath = lease.ProcPath;
        int retainedDescriptor = int.Parse(Path.GetFileName(procPath), System.Globalization.CultureInfo.InvariantCulture);

        executable.Dispose();
        using SafeFileHandle next = LinuxSealedFile.CreateAnonymous("smapi-installer-pinned-gh-nonreuse-test");

        checked((int)next.DangerousGetHandle()).Should().NotBe(retainedDescriptor);
        File.ReadAllBytes(procPath).Should().Equal(bytes);
        Action reuse = () => executable.LeaseForExecution().Dispose();
        reuse.Should().Throw<ObjectDisposedException>();
        lease.Dispose();
        File.Exists(procPath).Should().BeFalse();
    }

    [TestCase("wrong-size")]
    [TestCase("wrong-digest")]
    public async Task OpenForTesting_WrongPinnedIdentityFailsClosedWithoutPublishedDescriptor(string kind)
    {
        (string path, byte[] bytes, PinnedGitHubCliTestIdentity valid) = this.CreateScript();
        PinnedGitHubCliTestIdentity invalid = kind == "wrong-size"
            ? new PinnedGitHubCliTestIdentity(valid.SizeBytes + 1, valid.Sha256)
            : new PinnedGitHubCliTestIdentity(valid.SizeBytes, new string('0', 64));
        int before = CountPinnedDescriptors();

        Func<Task> open = () => PinnedGitHubCli.OpenForTestingAsync(path, invalid);

        await open.Should().ThrowAsync<PackageSecurityException>();
        CountPinnedDescriptors().Should().Be(before);
        File.ReadAllBytes(path).Should().Equal(bytes);
    }

    [Test]
    public async Task OpenForTesting_CancellationAfterDescriptorCreationClosesDescriptor()
    {
        (string path, _, PinnedGitHubCliTestIdentity identity) = this.CreateScript();
        int before = CountPinnedDescriptors();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Func<Task> open = () => PinnedGitHubCli.OpenForTestingAsync(path, identity, cancellation.Token);

        await open.Should().ThrowAsync<OperationCanceledException>();
        CountPinnedDescriptors().Should().Be(before);
    }

    [Test]
    public async Task OpenForTesting_EnforcedNoExecDoesNotFallbackAndPublishesNoDescriptor()
    {
        (string path, _, PinnedGitHubCliTestIdentity identity) = this.CreateScript();
        List<uint> requestedFlags = [];
        int before = CountPinnedDescriptors();

        Func<Task> open = () => PinnedGitHubCli.OpenForTestingAsync(
            path,
            identity,
            createExecutableOverride: flags =>
            {
                requestedFlags.Add(flags);
                throw new LinuxNativeIOException("credential-secret-never-disclose", 1);
            }
        );

        Exception error = (await open.Should().ThrowAsync<PackageSecurityException>()).Which;
        requestedFlags.Should().Equal(0x13u);
        CountPinnedDescriptors().Should().Be(before);
        error.ToString().Should().NotContain("credential-secret-never-disclose");
    }

    [Test]
    public async Task OpenForTesting_ModeFailureClosesExactExecutableDescriptor()
    {
        (string path, _, PinnedGitHubCliTestIdentity identity) = this.CreateScript();
        uint? requestedMode = null;
        int before = CountPinnedDescriptors();

        Func<Task> open = () => PinnedGitHubCli.OpenForTestingAsync(
            path,
            identity,
            changeModeOverride: (_, mode) =>
            {
                requestedMode = mode;
                return 13;
            }
        );

        await open.Should().ThrowAsync<PackageSecurityException>().WithMessage("*restrict*pinned GitHub CLI executable mode*");
        requestedMode.Should().Be(0x140u);
        CountPinnedDescriptors().Should().Be(before);
    }

    [TestCase("symlink")]
    [TestCase("hardlink")]
    [TestCase("fifo")]
    [TestCase("socket")]
    [CancelAfter(5000)]
    public async Task OpenForTesting_NonRegularLinkedOrBlockingSourceFailsClosed(string kind)
    {
        string caseDirectory = Path.Combine(this.TempRoot, kind);
        Directory.CreateDirectory(caseDirectory);
        string path = Path.Combine(caseDirectory, PinnedGitHubCli.ExecutableFilename);
        string target = Path.Combine(caseDirectory, "target");
        File.WriteAllText(target, "target");
        IDisposable? specialOwner = null;
        try
        {
            switch (kind)
            {
                case "symlink":
                    File.CreateSymbolicLink(path, target);
                    break;
                case "hardlink":
                    link(target, path).Should().Be(0, $"link failed with errno {Marshal.GetLastWin32Error()}");
                    break;
                case "fifo":
                    mkfifo(path, Convert.ToUInt32("600", 8)).Should().Be(0, $"mkfifo failed with errno {Marshal.GetLastWin32Error()}");
                    break;
                case "socket":
                    Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    socket.Bind(new UnixDomainSocketEndPoint(path));
                    specialOwner = socket;
                    break;
                default:
                    throw new AssertionException("Unknown unsafe-source test case.");
            }
            byte[] fixture = "target"u8.ToArray();
            PinnedGitHubCliTestIdentity identity = IdentityFor(fixture);

            Func<Task> open = () => PinnedGitHubCli.OpenForTestingAsync(path, identity);

            await open.Should().ThrowAsync<PackageSecurityException>().WithMessage("*single-link regular file*");
        }
        finally
        {
            specialOwner?.Dispose();
        }
    }

    [Test]
    public async Task OpenForTesting_RejectsWrongFilenameAndDoesNotDiscloseCallerPath()
    {
        string secretDirectory = Path.Combine(this.TempRoot, "credential-secret-never-disclose");
        Directory.CreateDirectory(secretDirectory);
        string target = Path.Combine(secretDirectory, "target");
        File.WriteAllText(target, "target");
        string unsafePath = Path.Combine(secretDirectory, PinnedGitHubCli.ExecutableFilename);
        File.CreateSymbolicLink(unsafePath, target);
        PinnedGitHubCliTestIdentity identity = IdentityFor("target"u8.ToArray());

        Func<Task> unsafeOpen = () => PinnedGitHubCli.OpenForTestingAsync(unsafePath, identity);
        Exception unsafeError = (await unsafeOpen.Should().ThrowAsync<PackageSecurityException>()).Which;

        unsafeError.ToString().Should().NotContain("credential-secret-never-disclose");

        string wrongName = Path.Combine(this.TempRoot, "github-cli");
        File.WriteAllText(wrongName, "target");
        Func<Task> wrongNameOpen = () => PinnedGitHubCli.OpenForTestingAsync(wrongName, identity);
        await wrongNameOpen.Should().ThrowAsync<PackageSecurityException>().WithMessage("*exactly 'gh'*");
    }

    private (string Path, byte[] Bytes, PinnedGitHubCliTestIdentity Identity) CreateScript()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("#!/bin/sh\nprintf 'pinned-cli-ok:%s' \"$1\"\n");
        string path = Path.Combine(this.TempRoot, PinnedGitHubCli.ExecutableFilename);
        File.WriteAllBytes(path, bytes);
        return (path, bytes, IdentityFor(bytes));
    }

    private static PinnedGitHubCliTestIdentity IdentityFor(byte[] bytes)
    {
        return new PinnedGitHubCliTestIdentity(
            bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
        );
    }

    private static int CountPinnedDescriptors()
    {
        int count = 0;
        foreach (string path in Directory.EnumerateFiles($"/proc/{Environment.ProcessId}/fd"))
        {
            try
            {
                if (new FileInfo(path).LinkTarget?.Contains(PinnedGitHubCliTests.MemfdName, StringComparison.Ordinal) == true)
                    count++;
            }
            catch (IOException)
            {
                // Another runtime descriptor can close during enumeration.
            }
        }
        return count;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string existingPath, string newPath);

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string path, uint mode);
}
