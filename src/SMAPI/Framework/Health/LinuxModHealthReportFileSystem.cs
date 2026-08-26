using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace StardewModdingAPI.Framework.Health;

/// <summary>A directory-handle-based Linux filesystem for private health reports.</summary>
internal sealed class LinuxModHealthReportFileSystem : IModHealthReportFileSystem
{
    private const int AtFdcwd = -100;
    private const int OReadOnly = 0;
    private const int OWriteOnly = 1;
    private const int OCreate = 0x40;
    private const int OExclusive = 0x80;
    private const int ODirectory = 0x10000;
    private const int ONonBlocking = 0x800;
    private const int ONoFollow = 0x20000;
    private const int OCloseOnExec = 0x80000;
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const int UserReadWrite = 0x180; // 0600
    private const int UserReadWriteExecute = 0x1C0; // 0700
    private const int AlreadyExists = 17;
    private const int NoEntry = 2;
    private const uint FileTypeMask = 0xF000;
    private const uint RegularFile = 0x8000;

    private readonly string EnumerationPath;
    private readonly SafeFileHandle DirectoryHandle;

    public string RelativeDirectory { get; }

    public LinuxModHealthReportFileSystem(string outputDirectory, string relativeDirectory = "ErrorLogs/HealthReports")
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Mod health reports use the Linux-private publisher only.");
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("An output directory is required.", nameof(outputDirectory));

        string fullPath = Path.GetFullPath(outputDirectory);
        string? parent = Path.GetDirectoryName(fullPath);
        string leaf = Path.GetFileName(fullPath);
        if (parent is null || leaf.Length == 0 || leaf is "." or "..")
            throw new ArgumentException("The output directory must have an existing parent.", nameof(outputDirectory));

        int parentFd = LinuxModHealthReportFileSystem.open(parent, LinuxModHealthReportFileSystem.OReadOnly | LinuxModHealthReportFileSystem.ODirectory | LinuxModHealthReportFileSystem.ONoFollow | LinuxModHealthReportFileSystem.OCloseOnExec);
        if (parentFd < 0)
            throw LinuxModHealthReportFileSystem.CreateException("open report parent");

        using SafeFileHandle parentHandle = new((IntPtr)parentFd, ownsHandle: true);
        int mkdirResult = LinuxModHealthReportFileSystem.mkdirat(parentFd, leaf, LinuxModHealthReportFileSystem.UserReadWriteExecute);
        int mkdirError = Marshal.GetLastWin32Error();
        if (mkdirResult != 0 && mkdirError != LinuxModHealthReportFileSystem.AlreadyExists)
            throw LinuxModHealthReportFileSystem.CreateException("create report directory", mkdirError);

        int directoryFd = LinuxModHealthReportFileSystem.openat(parentFd, leaf, LinuxModHealthReportFileSystem.OReadOnly | LinuxModHealthReportFileSystem.ODirectory | LinuxModHealthReportFileSystem.ONoFollow | LinuxModHealthReportFileSystem.OCloseOnExec, 0);
        if (directoryFd < 0)
            throw LinuxModHealthReportFileSystem.CreateException("open report directory");

        this.DirectoryHandle = new SafeFileHandle((IntPtr)directoryFd, ownsHandle: true);
        if (LinuxModHealthReportFileSystem.fchmod(directoryFd, LinuxModHealthReportFileSystem.UserReadWriteExecute) != 0)
        {
            this.DirectoryHandle.Dispose();
            throw LinuxModHealthReportFileSystem.CreateException("secure report directory permissions");
        }

        this.EnumerationPath = $"/proc/self/fd/{directoryFd}";
        this.RelativeDirectory = relativeDirectory.Replace('\\', '/').TrimEnd('/');
    }

    public void WritePrivateFile(string name, ReadOnlySpan<byte> contents)
    {
        LinuxModHealthReportFileSystem.ValidateName(name);
        int fd = LinuxModHealthReportFileSystem.openat(this.GetDirectoryFd(), name, LinuxModHealthReportFileSystem.OWriteOnly | LinuxModHealthReportFileSystem.OCreate | LinuxModHealthReportFileSystem.OExclusive | LinuxModHealthReportFileSystem.ONoFollow | LinuxModHealthReportFileSystem.OCloseOnExec, LinuxModHealthReportFileSystem.UserReadWrite);
        if (fd < 0)
            throw LinuxModHealthReportFileSystem.CreateException("create private report file");

        using SafeFileHandle handle = new((IntPtr)fd, ownsHandle: true);
        if (LinuxModHealthReportFileSystem.fchmod(fd, LinuxModHealthReportFileSystem.UserReadWrite) != 0)
            throw LinuxModHealthReportFileSystem.CreateException("secure report file permissions");

        using FileStream stream = new(handle, FileAccess.Write);
        stream.Write(contents);
        stream.Flush(flushToDisk: true);
    }

    public bool TryPublishNoReplace(string temporaryName, string finalName)
    {
        LinuxModHealthReportFileSystem.ValidateName(temporaryName);
        LinuxModHealthReportFileSystem.ValidateName(finalName);
        if (LinuxModHealthReportFileSystem.linkat(this.GetDirectoryFd(), temporaryName, this.GetDirectoryFd(), finalName, 0) == 0)
        {
            this.Delete(temporaryName);
            return true;
        }

        int error = Marshal.GetLastWin32Error();
        if (error == LinuxModHealthReportFileSystem.AlreadyExists)
            return false;
        throw LinuxModHealthReportFileSystem.CreateException("publish report file", error);
    }

    public bool Exists(string name)
    {
        LinuxModHealthReportFileSystem.ValidateName(name);
        int fd = this.OpenExistingRegular(name);
        if (fd >= 0)
        {
            LinuxModHealthReportFileSystem.close(fd);
            return true;
        }
        return false;
    }

    public void SyncDirectory()
    {
        if (LinuxModHealthReportFileSystem.fsync(this.GetDirectoryFd()) != 0)
            throw LinuxModHealthReportFileSystem.CreateException("sync report directory");
    }

    public DateTimeOffset GetLastWriteTimeUtc(string name)
    {
        LinuxModHealthReportFileSystem.ValidateName(name);
        int fd = this.OpenExistingRegular(name);
        if (fd < 0)
            throw new FileNotFoundException("The report artifact no longer exists.", name);
        try
        {
            return File.GetLastWriteTimeUtc($"/proc/self/fd/{fd}");
        }
        finally
        {
            LinuxModHealthReportFileSystem.close(fd);
        }
    }

    public IEnumerable<string> EnumerateNames()
    {
        return Directory.EnumerateFileSystemEntries(this.EnumerationPath)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!);
    }

    public void Delete(string name)
    {
        LinuxModHealthReportFileSystem.ValidateName(name);
        if (LinuxModHealthReportFileSystem.unlinkat(this.GetDirectoryFd(), name, 0) == 0)
            return;
        int error = Marshal.GetLastWin32Error();
        if (error == LinuxModHealthReportFileSystem.NoEntry)
            throw new FileNotFoundException("The report artifact no longer exists.", name);
        throw LinuxModHealthReportFileSystem.CreateException("delete report artifact", error);
    }

    public IDisposable? TryAcquireMaintenanceLock()
    {
        const string name = ".maintenance.lock";
        int fd = LinuxModHealthReportFileSystem.openat(this.GetDirectoryFd(), name, LinuxModHealthReportFileSystem.OReadOnly | LinuxModHealthReportFileSystem.OCreate | LinuxModHealthReportFileSystem.ONonBlocking | LinuxModHealthReportFileSystem.ONoFollow | LinuxModHealthReportFileSystem.OCloseOnExec, LinuxModHealthReportFileSystem.UserReadWrite);
        if (fd < 0)
            throw LinuxModHealthReportFileSystem.CreateException("open report maintenance lock");
        SafeFileHandle handle = new((IntPtr)fd, ownsHandle: true);
        try
        {
            LinuxModHealthReportFileSystem.RequireRegularFile(fd, "validate report maintenance lock");
        }
        catch
        {
            handle.Dispose();
            throw;
        }
        if (LinuxModHealthReportFileSystem.fchmod(fd, LinuxModHealthReportFileSystem.UserReadWrite) != 0)
        {
            handle.Dispose();
            throw LinuxModHealthReportFileSystem.CreateException("secure report maintenance lock");
        }
        if (LinuxModHealthReportFileSystem.flock(fd, LinuxModHealthReportFileSystem.LockExclusive | LinuxModHealthReportFileSystem.LockNonBlocking) == 0)
            return handle;

        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        return error is 11 or 35 ? null : throw LinuxModHealthReportFileSystem.CreateException("lock report maintenance", error);
    }

    public void Dispose()
    {
        this.DirectoryHandle.Dispose();
    }

    private int GetDirectoryFd()
    {
        if (this.DirectoryHandle.IsClosed || this.DirectoryHandle.IsInvalid)
            throw new ObjectDisposedException(nameof(LinuxModHealthReportFileSystem));
        return this.DirectoryHandle.DangerousGetHandle().ToInt32();
    }

    private int OpenExistingRegular(string name)
    {
        int fd = LinuxModHealthReportFileSystem.openat(this.GetDirectoryFd(), name, LinuxModHealthReportFileSystem.OReadOnly | LinuxModHealthReportFileSystem.ONonBlocking | LinuxModHealthReportFileSystem.ONoFollow | LinuxModHealthReportFileSystem.OCloseOnExec, 0);
        if (fd >= 0)
        {
            try
            {
                LinuxModHealthReportFileSystem.RequireRegularFile(fd, "validate report artifact");
                return fd;
            }
            catch
            {
                LinuxModHealthReportFileSystem.close(fd);
                throw;
            }
        }

        int error = Marshal.GetLastWin32Error();
        if (error == LinuxModHealthReportFileSystem.NoEntry)
            return -1;
        throw LinuxModHealthReportFileSystem.CreateException("inspect report artifact", error);
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || Path.GetFileName(name) != name || name.IndexOfAny(['/', '\\', '\0']) >= 0)
            throw new ArgumentException("A simple report filename is required.", nameof(name));
    }

    private static IOException CreateException(string operation, int? error = null)
    {
        int code = error ?? Marshal.GetLastWin32Error();
        return new IOException($"Unable to {operation} (Linux error {code}: {new Win32Exception(code).Message}).");
    }

    private static void RequireRegularFile(int fd, string operation)
    {
        if (LinuxModHealthReportFileSystem.fstat(fd, out LinuxStat stat) != 0)
            throw LinuxModHealthReportFileSystem.CreateException(operation);
        if ((stat.Mode & LinuxModHealthReportFileSystem.FileTypeMask) != LinuxModHealthReportFileSystem.RegularFile)
            throw new IOException($"Unable to {operation}: the entry is not a regular file.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStat
    {
        public ulong Device;
        public ulong Inode;
        public ulong LinkCount;
        public uint Mode;
        public uint UserId;
        public uint GroupId;
        public uint Padding;
        public ulong SpecialDevice;
        public long Size;
        public long BlockSize;
        public long Blocks;
        public long AccessSeconds;
        public long AccessNanoseconds;
        public long ModifySeconds;
        public long ModifyNanoseconds;
        public long ChangeSeconds;
        public long ChangeNanoseconds;
        public long Reserved0;
        public long Reserved1;
        public long Reserved2;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int openat(int directoryFd, string pathname, int flags, int mode = 0);

    [DllImport("libc", SetLastError = true)]
    private static extern int mkdirat(int directoryFd, string pathname, int mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int fchmod(int fd, int mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int fstat(int fd, out LinuxStat stat);

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int linkat(int oldDirectoryFd, string oldPath, int newDirectoryFd, string newPath, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int unlinkat(int directoryFd, string pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int flock(int fd, int operation);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}
