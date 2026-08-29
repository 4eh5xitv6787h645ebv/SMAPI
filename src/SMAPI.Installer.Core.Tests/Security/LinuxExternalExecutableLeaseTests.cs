using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Tests.Security;

[Platform("Linux")]
[SupportedOSPlatform("linux")]
internal sealed class LinuxExternalExecutableLeaseTests
{
    private string Root = null!;

    [SetUp]
    public void SetUp()
    {
        this.Root = Path.Combine(Path.GetTempPath(), $"smapi-executable-lease-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.Root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(this.Root))
            Directory.Delete(this.Root, true);
    }

    [Test]
    public void RetainsExactUnlinkedInodeUntilIdempotentDisposal()
    {
        string path = this.CreateExecutable("SMAPI.Installer", "#!/bin/sh\nprintf original");
        LinuxExternalExecutableLease lease = LinuxExternalExecutableLease.Open(path);
        string procPath = lease.ProcPath;
        LinuxFileIdentity identity = lease.Identity;

        File.Delete(path);
        File.WriteAllText(path, "replacement");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        using Process process = Process.Start(new ProcessStartInfo(procPath) { RedirectStandardOutput = true, UseShellExecute = false })!;
        process.StandardOutput.ReadToEnd().Should().Be("original");
        process.WaitForExit();
        process.ExitCode.Should().Be(0);
        lease.Identity.Should().Be(identity);
        lease.Dispose();
        lease.Dispose();
        File.Exists(procPath).Should().BeFalse();
    }

    [Test]
    public void RejectsMissingEmptyDirectorySymlinkHardlinkAndFifo()
    {
        string path = Path.Combine(this.Root, "SMAPI.Installer");
        FluentActions.Invoking(() => LinuxExternalExecutableLease.Open(path)).Should().Throw<Exception>();

        File.WriteAllText(path, "");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        FluentActions.Invoking(() => LinuxExternalExecutableLease.Open(path)).Should().Throw<IOException>();
        File.Delete(path);

        Directory.CreateDirectory(path);
        FluentActions.Invoking(() => LinuxExternalExecutableLease.Open(path)).Should().Throw<IOException>();
        Directory.Delete(path);

        string target = this.CreateExecutable("target", "target");
        File.CreateSymbolicLink(path, target);
        FluentActions.Invoking(() => LinuxExternalExecutableLease.Open(path)).Should().Throw<IOException>();
        File.Delete(path);

        link(target, path).Should().Be(0);
        FluentActions.Invoking(() => LinuxExternalExecutableLease.Open(path)).Should().Throw<IOException>();
        File.Delete(path);

        mkfifo(path, 0x1c0).Should().Be(0);
        FluentActions.Invoking(() => LinuxExternalExecutableLease.Open(path)).Should().Throw<IOException>();

        FluentActions.Invoking(() => LinuxExternalExecutableLease.Open("/dev/null")).Should().Throw<IOException>();
    }

    [TestCase(0x120, TestName = "RejectsNoOwnerExecute")]
    [TestCase(0x108, TestName = "RejectsGroupOnlyExecute")]
    [TestCase(0x101, TestName = "RejectsOtherOnlyExecute")]
    [TestCase(0x1d0, TestName = "RejectsGroupWritable")]
    [TestCase(0x1c2, TestName = "RejectsOtherWritable")]
    [TestCase(0x9c0, TestName = "RejectsSetUserId")]
    [TestCase(0x5c0, TestName = "RejectsSetGroupId")]
    [TestCase(0x3c0, TestName = "RejectsStickyBit")]
    public void RejectsUnsafeModes(int mode)
    {
        string path = this.CreateExecutable("SMAPI.Installer", "backend");
        File.SetUnixFileMode(path, (UnixFileMode)mode);

        FluentActions.Invoking(() => LinuxExternalExecutableLease.Open(path)).Should().Throw<IOException>();
    }

    [Test]
    public void RejectsOversizedSparseExecutable()
    {
        string path = this.CreateExecutable("SMAPI.Installer", "backend");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using (FileStream stream = new(path, FileMode.Open, FileAccess.Write, FileShare.None))
            stream.SetLength((64L * 1024 * 1024) + 1);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        FluentActions.Invoking(() => LinuxExternalExecutableLease.Open(path)).Should().Throw<IOException>();
    }

    [Test]
    public void RejectsSymlinkedParentTraversal()
    {
        string real = Path.Combine(this.Root, "real");
        Directory.CreateDirectory(real);
        string executable = Path.Combine(real, "SMAPI.Installer");
        File.WriteAllText(executable, "backend");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        string linked = Path.Combine(this.Root, "linked");
        Directory.CreateSymbolicLink(linked, real);

        FluentActions.Invoking(() => LinuxExternalExecutableLease.Open(Path.Combine(linked, "SMAPI.Installer"))).Should().Throw<IOException>();
    }

    [Test]
    public void RejectsRelativeAndNonOwnerAuthority()
    {
        FluentActions.Invoking(() => LinuxExternalExecutableLease.Open("SMAPI.Installer")).Should().Throw<ArgumentException>();
        string path = this.CreateExecutable("SMAPI.Installer", "backend");
        using LinuxExternalExecutableLease lease = LinuxExternalExecutableLease.Open(path);

        FluentActions.Invoking(() => LinuxExternalExecutableLease.ValidateIdentity(lease.Identity, 123, 0x140, 456))
            .Should().Throw<IOException>();
    }

    private string CreateExecutable(string name, string content)
    {
        string path = Path.Combine(this.Root, name);
        File.WriteAllText(path, content);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return path;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string oldPath, string newPath);

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string path, uint mode);
}
