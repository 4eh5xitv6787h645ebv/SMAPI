using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using Microsoft.Win32.SafeHandles;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Tests.Security;

[TestFixture]
[Platform("Linux")]
[SupportedOSPlatform("linux")]
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
    public void CreateExecutableAnonymous_RequestsExplicitExecutableFlagsWithoutChangingGenericCreation()
    {
        List<uint> requestedFlags = [];
        List<uint> requestedDataFlags = [];

        using SafeFileHandle executable = LinuxSealedFile.CreateExecutableAnonymous(
            "smapi-installer-executable-flags-test",
            flags =>
            {
                requestedFlags.Add(flags);
                return LinuxSealedFile.CreateAnonymous("smapi-installer-executable-flags-fixture");
            }
        );
        using SafeFileHandle data = LinuxSealedFile.CreateAnonymous(
            "smapi-installer-generic-data-test",
            createWithFlagsOverride: flags =>
            {
                requestedDataFlags.Add(flags);
                int descriptor = memfd_create("smapi-installer-generic-data-fixture", flags);
                descriptor.Should().BeGreaterThanOrEqualTo(0, $"memfd_create failed with errno {Marshal.GetLastWin32Error()}");
                return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
            }
        );

        requestedFlags.Should().Equal(0x13u);
        requestedDataFlags.Should().Equal(0x0bu);
        executable.IsInvalid.Should().BeFalse();
        data.IsInvalid.Should().BeFalse();
        fchmod(data, Convert.ToUInt32("500", 8)).Should().Be(-1, "MFD_NOEXEC_SEAL data authority must reject execute bits");
    }

    [Test]
    public void CreateAnonymous_OldKernelEinvalFallsBackToExactLegacyDataFlags()
    {
        List<uint> requestedFlags = [];
        using SafeFileHandle data = LinuxSealedFile.CreateAnonymous(
            "smapi-installer-data-fallback-test",
            createWithFlagsOverride: flags =>
            {
                requestedFlags.Add(flags);
                if (requestedFlags.Count == 1)
                    throw new LinuxNativeIOException("synthetic old-kernel data flag rejection", 22);
                int descriptor = memfd_create("smapi-installer-data-fallback-fixture", flags);
                descriptor.Should().BeGreaterThanOrEqualTo(0, $"memfd_create failed with errno {Marshal.GetLastWin32Error()}");
                return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
            }
        );
        byte[] expected = "legacy data authority"u8.ToArray();
        RandomAccess.Write(data, expected, 0);

        LinuxSealedFile.SealImmutable(data);

        requestedFlags.Should().Equal(0x0bu, 0x03u);
        using LinuxSealedFileLease lease = LinuxSealedFile.LeaseForExternalRead(data);
        File.ReadAllBytes(lease.ProcPath).Should().Equal(expected);
    }

    [Test]
    [CancelAfter(5000)]
    public async Task CreateExecutableAnonymous_OldKernelEinvalFallsBackAndExactSealedScriptExecutes()
    {
        List<uint> requestedFlags = [];
        using SafeFileHandle executable = LinuxSealedFile.CreateExecutableAnonymous(
            "smapi-installer-executable-fallback-test",
            flags =>
            {
                requestedFlags.Add(flags);
                if (requestedFlags.Count == 1)
                    throw new LinuxNativeIOException("synthetic old-kernel flag rejection", 22);
                int descriptor = memfd_create("smapi-installer-executable-fallback-fixture", flags);
                descriptor.Should().BeGreaterThanOrEqualTo(0, $"memfd_create failed with errno {Marshal.GetLastWin32Error()}");
                return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
            }
        );
        byte[] script = Encoding.UTF8.GetBytes("#!/bin/sh\nprintf 'sealed-fallback-ok'\n");
        RandomAccess.Write(executable, script, 0);
        fchmod(executable, Convert.ToUInt32("500", 8)).Should().Be(0, $"fchmod failed with errno {Marshal.GetLastWin32Error()}");
        LinuxSealedFile.SealImmutable(executable);
        using LinuxSealedFileLease lease = LinuxSealedFile.LeaseForExternalRead(executable);
        ProcessStartInfo start = new(lease.ProcPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using Process process = Process.Start(start) ?? throw new AssertionException("The fallback executable didn't start.");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(4));
        string output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
        string error = await process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }

        requestedFlags.Should().Equal(0x13u, 0x03u);
        process.ExitCode.Should().Be(0, error);
        output.Should().Be("sealed-fallback-ok");
        File.ReadAllBytes(lease.ProcPath).Should().Equal(script);
    }

    [Test]
    public void CreateExecutableAnonymous_EnforcedNoExecFailsClosedWithoutLegacyFallbackOrPrivateError()
    {
        List<uint> requestedFlags = [];

        Action create = () => LinuxSealedFile.CreateExecutableAnonymous(
            "smapi-installer-executable-noexec-test",
            flags =>
            {
                requestedFlags.Add(flags);
                throw new LinuxNativeIOException("credential-secret-never-disclose", 1);
            }
        ).Dispose();

        Exception error = create.Should().Throw<PackageSecurityException>().Which;
        requestedFlags.Should().Equal(0x13u);
        error.ToString().Should().NotContain("credential-secret-never-disclose");
        error.Message.Should().Contain("couldn't create executable");
    }

    [Test]
    public void SealExecutableImmutable_SupportedKernelPreventsExecuteModeChanges()
    {
        using SafeFileHandle executable = LinuxSealedFile.CreateExecutableAnonymous(
            "smapi-installer-executable-mode-seal-test"
        );
        RandomAccess.Write(executable, "sealed-executable"u8.ToArray(), 0);
        fchmod(executable, Convert.ToUInt32("500", 8)).Should().Be(0, $"fchmod failed with errno {Marshal.GetLastWin32Error()}");

        bool executeModeSealed = LinuxSealedFile.SealExecutableImmutable(executable);

        if (!executeModeSealed)
            Assert.Ignore("This kernel predates F_SEAL_EXEC; the deterministic fallback is covered separately.");
        fchmod(executable, Convert.ToUInt32("400", 8)).Should().Be(-1, "F_SEAL_EXEC must prevent execute-bit changes");
        using LinuxSealedFileLease lease = LinuxSealedFile.LeaseForExternalRead(executable);
        File.GetUnixFileMode(lease.ProcPath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserExecute);
    }

    [Test]
    public void SealExecutableImmutable_OldKernelEinvalFallsBackToExactLegacySealSet()
    {
        using SafeFileHandle executable = LinuxSealedFile.CreateExecutableAnonymous(
            "smapi-installer-executable-seal-fallback-test"
        );
        byte[] expected = "legacy executable seals"u8.ToArray();
        RandomAccess.Write(executable, expected, 0);
        fchmod(executable, Convert.ToUInt32("500", 8)).Should().Be(0, $"fchmod failed with errno {Marshal.GetLastWin32Error()}");
        List<int> requestedSeals = [];

        bool executeModeSealed = LinuxSealedFile.SealExecutableImmutable(
            executable,
            seals =>
            {
                requestedSeals.Add(seals);
                if (requestedSeals.Count == 1)
                    return 22;
                return fcntl(executable, 1033, seals) == 0 ? 0 : Marshal.GetLastWin32Error();
            }
        );

        executeModeSealed.Should().BeFalse();
        requestedSeals.Should().Equal(0x2f, 0x0f);
        using LinuxSealedFileLease lease = LinuxSealedFile.LeaseForExternalRead(executable);
        File.ReadAllBytes(lease.ProcPath).Should().Equal(expected);
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

    [DllImport("libc", SetLastError = true)]
    private static extern int fchmod(SafeFileHandle descriptor, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(SafeFileHandle descriptor, int command, int argument);

    [DllImport("libc", SetLastError = true)]
    private static extern int memfd_create(string name, uint flags);
}
