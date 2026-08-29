using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace StardewModdingAPI.Installer.Core.Security;

/// <summary>Retains one owner-controlled Linux executable for exact descriptor-bound external launch.</summary>
public sealed class LinuxExternalExecutableLease : IDisposable
{
    private const int AtEmptyPath = 0x1000;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxBasicStats = 0x7ff;
    private const int GroupOrOtherWrite = 0x12;
    private const int SpecialModeBits = 0xe00;
    private const int OwnerExecute = 0x40;
    private const long MaximumExecutableBytes = 64L * 1024 * 1024;
    private readonly LinuxAnchoredFileSystem Directory;
    private readonly LinuxAnchoredFile Executable;
    private bool Disposed;

    private LinuxExternalExecutableLease(LinuxAnchoredFileSystem directory, LinuxAnchoredFile executable, string procPath)
    {
        this.Directory = directory;
        this.Executable = executable;
        this.ProcPath = procPath;
        this.Identity = executable.Identity;
    }

    /// <summary>The parent-owned descriptor path which must be used directly as ProcessStartInfo.FileName.</summary>
    public string ProcPath { get; }

    /// <summary>The exact regular-file identity retained by this lease.</summary>
    public LinuxFileIdentity Identity { get; }

    /// <summary>Open an absolute executable with full no-follow directory traversal and retain its exact inode.</summary>
    public static LinuxExternalExecutableLease Open(string executablePath)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("External executable leases require Linux.");
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath))
            throw new ArgumentException("An absolute Linux executable path is required.", nameof(executablePath));
        string fullPath = Path.GetFullPath(executablePath);
        string? directoryPath = Path.GetDirectoryName(fullPath);
        string fileName = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(directoryPath) || string.IsNullOrEmpty(fileName) || fileName is "." or ".." || fileName.IndexOfAny(['/', '\\']) >= 0)
            throw new ArgumentException("The Linux executable path must end in one safe filename.", nameof(executablePath));

        LinuxAnchoredFileSystem? directory = null;
        LinuxAnchoredFile? executable = null;
        try
        {
            directory = new LinuxAnchoredFileSystem(directoryPath);
            executable = directory.OpenRegularFileForRead(fileName);
            LinuxFileIdentity identity = executable.Identity;
            if (statx(executable.Handle, "", AtEmptyPath | AtSymlinkNoFollow, StatxBasicStats, out Statx status) != 0)
                throw new IOException("The external executable isn't owned by the current effective user.");
            ValidateIdentity(identity, status.UserId, status.Mode, geteuid());
            int descriptor = checked((int)executable.Handle.DangerousGetHandle());
            string procPath = $"/proc/{Environment.ProcessId}/fd/{descriptor}";
            LinuxExternalExecutableLease result = new(directory, executable, procPath);
            directory = null;
            executable = null;
            return result;
        }
        finally
        {
            executable?.Dispose();
            directory?.Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (this.Disposed)
            return;
        this.Disposed = true;
        this.Executable.Dispose();
        this.Directory.Dispose();
    }

    internal static void ValidateIdentity(LinuxFileIdentity identity, uint ownerUserId, int fullUnixMode, uint effectiveUserId)
    {
        if (
            identity.Kind != LinuxAnchoredEntryKind.RegularFile
            || identity.LinkCount != 1
            || identity.Size is <= 0 or > MaximumExecutableBytes
            || ownerUserId != effectiveUserId
            || (fullUnixMode & OwnerExecute) == 0
            || (fullUnixMode & (GroupOrOtherWrite | SpecialModeBits)) != 0
        )
        {
            throw new IOException("The external executable isn't a bounded owner-controlled single-link regular file.");
        }
    }

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int statx(SafeFileHandle directory, string path, int flags, uint mask, out Statx data);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint geteuid();

    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private struct Statx
    {
        public uint Mask;
        public uint BlockSize;
        public ulong Attributes;
        public uint LinkCount;
        public uint UserId;
        public uint GroupId;
        public ushort Mode;
        public ushort Spare0;
        public ulong Inode;
        public ulong Size;
        public ulong Blocks;
        public ulong AttributesMask;
    }
}
