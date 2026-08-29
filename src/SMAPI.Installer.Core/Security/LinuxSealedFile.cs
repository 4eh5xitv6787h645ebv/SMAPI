using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using StardewModdingAPI.Installer.Core.Packages;

namespace StardewModdingAPI.Installer.Core.Security;

/// <summary>Creates and retains kernel-immutable anonymous files for Linux security boundaries.</summary>
internal static class LinuxSealedFile
{
    private const uint MemfdCloseOnExec = 0x0001;
    private const uint MemfdAllowSealing = 0x0002;
    private const int DuplicateCloseOnExec = 1030;
    private const int AddSeals = 1033;
    private const int GetSeals = 1034;
    private const int SealSeal = 0x0001;
    private const int SealShrink = 0x0002;
    private const int SealGrow = 0x0004;
    private const int SealWrite = 0x0008;
    private const int RequiredSeals = SealSeal | SealShrink | SealGrow | SealWrite;
    private const int ErrorFunctionNotImplemented = 38;

    /// <summary>Create an anonymous file which can be made kernel-immutable after its bytes are written.</summary>
    public static SafeFileHandle CreateAnonymous(string privateName, Func<SafeFileHandle>? createOverride = null)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Anonymous sealed files are only supported on Linux.");
        if (string.IsNullOrWhiteSpace(privateName) || privateName.Length > 128 || privateName.Any(char.IsControl))
            throw new ArgumentException("A short private anonymous-file name is required.", nameof(privateName));

        if (createOverride is not null)
        {
            SafeFileHandle overridden;
            try
            {
                overridden = createOverride()
                    ?? throw new PackageSecurityException("The Linux anonymous-file test seam returned no descriptor.");
            }
            catch (EntryPointNotFoundException ex)
            {
                throw Unsupported(ex);
            }
            if (overridden.IsInvalid || overridden.IsClosed)
            {
                overridden.Dispose();
                throw new PackageSecurityException("The Linux anonymous-file test seam returned an invalid descriptor.");
            }
            return overridden;
        }

        int descriptor;
        try
        {
            descriptor = memfd_create(privateName, LinuxSealedFile.MemfdCloseOnExec | LinuxSealedFile.MemfdAllowSealing);
        }
        catch (EntryPointNotFoundException ex)
        {
            throw Unsupported(ex);
        }
        if (descriptor < 0)
        {
            int error = Marshal.GetLastWin32Error();
            if (error == LinuxSealedFile.ErrorFunctionNotImplemented)
                throw Unsupported(new LinuxNativeIOException("memfd_create failed", error));
            throw new PackageSecurityException(
                "Linux couldn't create anonymous sealed-file staging.",
                new LinuxNativeIOException("memfd_create failed", error)
            );
        }
        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    /// <summary>Duplicate a descriptor with close-on-exec retained.</summary>
    public static SafeFileHandle Duplicate(SafeFileHandle source)
    {
        AssertOpen(source);
        int descriptor = fcntl(source, LinuxSealedFile.DuplicateCloseOnExec, 0);
        if (descriptor < 0)
        {
            throw new PackageSecurityException(
                "Linux couldn't duplicate anonymous sealed-file authority.",
                new LinuxNativeIOException("fcntl(F_DUPFD_CLOEXEC) failed", Marshal.GetLastWin32Error())
            );
        }
        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    /// <summary>Make an anonymous file immutable and verify every required kernel seal.</summary>
    public static void SealImmutable(SafeFileHandle handle)
    {
        AssertOpen(handle);
        if (fcntl(handle, LinuxSealedFile.AddSeals, LinuxSealedFile.RequiredSeals) < 0)
        {
            throw new PackageSecurityException(
                "Linux couldn't seal anonymous file staging.",
                new LinuxNativeIOException("fcntl(F_ADD_SEALS) failed", Marshal.GetLastWin32Error())
            );
        }
        AssertImmutable(handle);
    }

    /// <summary>
    /// Retain an immutable descriptor while an external process reopens it through this process's procfs entry.
    /// The returned authority remains valid if the original <see cref="SafeFileHandle"/> is disposed.
    /// </summary>
    public static LinuxSealedFileLease LeaseForExternalRead(SafeFileHandle handle)
    {
        AssertOpen(handle);
        AssertImmutable(handle);

        bool addedReference = false;
        try
        {
            handle.DangerousAddRef(ref addedReference);
            int descriptor = checked((int)handle.DangerousGetHandle());
            if (descriptor < 0)
                throw new PackageSecurityException("The immutable anonymous-file descriptor is invalid.");
            return new LinuxSealedFileLease(handle, descriptor);
        }
        catch
        {
            if (addedReference)
                handle.DangerousRelease();
            throw;
        }
    }

    private static void AssertImmutable(SafeFileHandle handle)
    {
        int actual = fcntl(handle, LinuxSealedFile.GetSeals, 0);
        if (actual < 0)
        {
            throw new PackageSecurityException(
                "Linux couldn't verify anonymous file staging seals.",
                new LinuxNativeIOException("fcntl(F_GET_SEALS) failed", Marshal.GetLastWin32Error())
            );
        }
        if ((actual & LinuxSealedFile.RequiredSeals) != LinuxSealedFile.RequiredSeals)
            throw new PackageSecurityException("Linux anonymous file staging doesn't have every required immutable seal.");
    }

    private static void AssertOpen(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsInvalid || handle.IsClosed)
            throw new ObjectDisposedException(nameof(handle));
    }

    private static PackageSecurityException Unsupported(Exception inner)
    {
        return new PackageSecurityException(
            "This Linux runtime doesn't provide the required anonymous sealed-package staging support.",
            inner
        );
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int memfd_create(string name, uint flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(SafeFileHandle descriptor, int command, int argument);
}

/// <summary>One retained immutable descriptor addressable only through the owning process's procfs table.</summary>
internal sealed class LinuxSealedFileLease : IDisposable
{
    private SafeFileHandle? Owner;

    /// <summary>The procfs path an external process can open while this lease is retained.</summary>
    public string ProcPath { get; }

    internal LinuxSealedFileLease(SafeFileHandle owner, int descriptor)
    {
        this.Owner = owner;
        this.ProcPath = $"/proc/{Environment.ProcessId}/fd/{descriptor}";
    }

    /// <inheritdoc />
    public void Dispose()
    {
        SafeFileHandle? owner = Interlocked.Exchange(ref this.Owner, null);
        owner?.DangerousRelease();
    }
}
