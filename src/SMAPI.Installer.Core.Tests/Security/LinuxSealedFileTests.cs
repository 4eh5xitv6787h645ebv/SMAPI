using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Microsoft.Win32.SafeHandles;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Tests.Security;

[TestFixture]
[Platform("Linux")]
public sealed class LinuxSealedFileTests
{
    [Test]
    public void LeaseForExternalRead_SealedBytesRemainReadableAfterOwnerDispose()
    {
        byte[] expected = Encoding.UTF8.GetBytes("exact immutable release bytes");
        SafeFileHandle handle = LinuxSealedFile.CreateAnonymous("smapi-installer-lease-test");
        RandomAccess.Write(handle, expected, 0);
        LinuxSealedFile.SealImmutable(handle);

        using LinuxSealedFileLease lease = LinuxSealedFile.LeaseForExternalRead(handle);
        string procPath = lease.ProcPath;
        handle.Dispose();

        File.ReadAllBytes(procPath).Should().Equal(expected);
        File.Exists(procPath).Should().BeTrue();

        lease.Dispose();
        File.Exists(procPath).Should().BeFalse();
    }

    [Test]
    [CancelAfter(5000)]
    public async Task LeaseForExternalRead_ChildProcessReadsExactBytesAfterOwnerDispose()
    {
        byte[] expected = Encoding.UTF8.GetBytes("exact child-process release bytes");
        SafeFileHandle handle = LinuxSealedFile.CreateAnonymous("smapi-installer-child-read-test");
        RandomAccess.Write(handle, expected, 0);
        LinuxSealedFile.SealImmutable(handle);
        using LinuxSealedFileLease lease = LinuxSealedFile.LeaseForExternalRead(handle);
        handle.Dispose();

        ProcessStartInfo start = new("cat")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(lease.ProcPath);
        using Process process = Process.Start(start) ?? throw new AssertionException("The child reader process didn't start.");
        using MemoryStream output = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(4));
        Task copy = process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await copy;
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }

        process.ExitCode.Should().Be(0, await error);
        output.ToArray().Should().Equal(expected);
    }

    [Test]
    public void LeaseForExternalRead_RetainedDescriptorCannotBeReusedBeforeLeaseDispose()
    {
        SafeFileHandle owner = LinuxSealedFile.CreateAnonymous("smapi-installer-fd-lifetime-test");
        RandomAccess.Write(owner, new byte[] { 0x41 }, 0);
        LinuxSealedFile.SealImmutable(owner);
        using LinuxSealedFileLease lease = LinuxSealedFile.LeaseForExternalRead(owner);
        int retainedDescriptor = int.Parse(Path.GetFileName(lease.ProcPath), System.Globalization.CultureInfo.InvariantCulture);
        owner.Dispose();

        using SafeFileHandle next = LinuxSealedFile.CreateAnonymous("smapi-installer-fd-nonreuse-test");

        checked((int)next.DangerousGetHandle()).Should().NotBe(retainedDescriptor);
        File.ReadAllBytes(lease.ProcPath).Should().Equal(0x41);
    }

    [Test]
    public void LeaseForExternalRead_UnsealedFileFailsClosed()
    {
        using SafeFileHandle handle = LinuxSealedFile.CreateAnonymous("smapi-installer-unsealed-test");
        RandomAccess.Write(handle, new byte[] { 1 }, 0);

        Action action = () => LinuxSealedFile.LeaseForExternalRead(handle);

        action.Should().Throw<PackageSecurityException>().WithMessage("*every required immutable seal*");
    }

    [Test]
    public void Duplicate_SealedWriteAliasCannotMutateOrResizeBytes()
    {
        byte[] expected = Encoding.UTF8.GetBytes("sealed");
        using SafeFileHandle handle = LinuxSealedFile.CreateAnonymous("smapi-installer-duplicate-test");
        using SafeFileHandle alias = LinuxSealedFile.Duplicate(handle);
        RandomAccess.Write(handle, expected, 0);

        LinuxSealedFile.SealImmutable(handle);

        Action write = () => RandomAccess.Write(alias, new byte[] { 0x41 }, 0);
        Exception error = write.Should().Throw<Exception>().Which;
        (error is IOException or UnauthorizedAccessException).Should().BeTrue();
        using LinuxSealedFileLease lease = LinuxSealedFile.LeaseForExternalRead(alias);
        File.ReadAllBytes(lease.ProcPath).Should().Equal(expected);
    }
}
