using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace StardewModdingAPI.Installer.Core.Packages;

/// <summary>A retained, pathname-swap-resistant private directory for the GitHub attestation process.</summary>
internal sealed class GitHubAttestationPrivateDirectory : IAsyncDisposable
{
    private const int OpenReadOnly = 0;
    private const int OpenNonBlocking = 0x800;
    private const int OpenDirectoryFlag = 0x10000;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const int AtSymlinkNoFollow = 0x100;
    private const int AtRemoveDirectory = 0x200;
    private const uint StatxBasicStats = 0x7ff;
    private const ushort FileTypeMask = 0xf000;
    private const ushort FileTypeDirectory = 0x4000;
    private const ushort FileTypeSymbolicLink = 0xa000;
    private const int ErrorNoEntry = 2;
    private const int ErrorExists = 17;
    private const int MaximumCleanupDepth = 32;
    private const int MaximumCleanupEntries = 16_384;
    private const int DirectoryBufferBytes = 64 * 1024;
    private const int DirectoryEntryHeaderBytes = 19;
    private const int MaximumLinkTargetBytes = 4096;
    private const long SystemCallGetDirectoryEntries64 = 217;
    private const int PrivateDirectoryMode = 0x1c0; // 0700
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    private SafeFileHandle? ParentHandle;
    private SafeFileHandle? DirectoryHandle;
    private readonly string EntryName;
    private readonly DirectoryIdentity Identity;
    private BundleBridgeIdentity? BundleBridge;
    private int CleanupStarted;

    /// <summary>The retained directory path, resolved through this process's open descriptor.</summary>
    public string ProcPath { get; }

    private GitHubAttestationPrivateDirectory(
        SafeFileHandle parentHandle,
        SafeFileHandle directoryHandle,
        string entryName,
        DirectoryIdentity identity
    )
    {
        this.ParentHandle = parentHandle;
        this.DirectoryHandle = directoryHandle;
        this.EntryName = entryName;
        this.Identity = identity;
        this.ProcPath = $"/proc/{Environment.ProcessId}/fd/{checked((int)directoryHandle.DangerousGetHandle())}";
    }

    /// <summary>Create and retain a fresh mode-0700 directory below the configured temporary root.</summary>
    /// <param name="afterCreatedForTesting">An internal deterministic race seam invoked after the directory is retained.</param>
    public static GitHubAttestationPrivateDirectory Create(Action<string>? afterCreatedForTesting = null)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");

        SafeFileHandle? parent = null;
        SafeFileHandle? directory = null;
        GitHubAttestationPrivateDirectory? authority = null;
        string? entryName = null;
        try
        {
            string temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            parent = OpenAbsoluteDirectoryNoFollow(temporaryRoot);
            for (int attempt = 0; attempt < 32; attempt++)
            {
                entryName = $"smapi-attestation-private-{Guid.NewGuid():N}";
                if (mkdirat(parent, entryName, PrivateDirectoryMode) == 0)
                    break;
                if (Marshal.GetLastWin32Error() != ErrorExists)
                    ThrowNativeFailure();
                entryName = null;
            }
            if (entryName is null)
                throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");

            directory = OpenAtDirectory(parent, entryName);
            if (fchmod(directory, PrivateDirectoryMode) != 0)
                ThrowNativeFailure();
            DirectoryIdentity identity = GetHandleIdentity(directory);
            AssertPrivateDirectory(identity);

            authority = new GitHubAttestationPrivateDirectory(parent, directory, entryName, identity);
            parent = null;
            directory = null;

            string createdPath = Path.Combine(temporaryRoot, entryName);
            afterCreatedForTesting?.Invoke(createdPath);
            DirectoryIdentity? namedIdentity = GetNamedIdentity(authority.ParentHandle!, entryName, allowMissing: false);
            if (namedIdentity is null || !identity.IsSameObject(namedIdentity.Value))
                throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");
            AssertPrivateDirectory(GetHandleIdentity(authority.DirectoryHandle!));
            return authority;
        }
        catch (PackageSecurityException)
        {
            authority?.CleanupAndDispose();
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or DecoderFallbackException
                or IOException
                or NotSupportedException
                or PathTooLongException
                or UnauthorizedAccessException
        )
        {
            authority?.CleanupAndDispose();
            throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");
        }
        catch
        {
            authority?.CleanupAndDispose();
            throw;
        }
        finally
        {
            directory?.Dispose();
            parent?.Dispose();
        }
    }

    /// <summary>Create and retain one fixed extension-bearing symlink to an immutable bundle descriptor.</summary>
    public string CreateBundleBridge(
        string entryName,
        string retainedBundlePath,
        Action<string>? afterCreatedForTesting = null
    )
    {
        SafeFileHandle directory = this.DirectoryHandle
            ?? throw new ObjectDisposedException(nameof(GitHubAttestationPrivateDirectory));
        if (this.BundleBridge is not null || entryName.Length == 0 || entryName.Contains('/') || entryName.Any(char.IsControl))
            throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");

        AssertPrivateDirectory(GetHandleIdentity(directory));
        if (symlinkat(retainedBundlePath, directory, entryName) != 0)
            ThrowNativeFailure();
        DirectoryIdentity identity = GetNamedIdentity(directory, entryName, allowMissing: false)
            ?? throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");
        AssertPrivateBundleBridge(identity);
        if (!string.Equals(ReadLinkAt(directory, entryName), retainedBundlePath, StringComparison.Ordinal))
            throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");

        string bridgePath = Path.Combine(this.ProcPath, entryName);
        afterCreatedForTesting?.Invoke(bridgePath);
        AssertBundleBridge(directory, entryName, retainedBundlePath, identity);
        this.BundleBridge = new BundleBridgeIdentity(entryName, retainedBundlePath, identity);
        return bridgePath;
    }

    /// <summary>Revalidate the exact retained bridge entry immediately before process start.</summary>
    public void AssertBundleBridge(string retainedBundlePath)
    {
        SafeFileHandle directory = this.DirectoryHandle
            ?? throw new ObjectDisposedException(nameof(GitHubAttestationPrivateDirectory));
        BundleBridgeIdentity bridge = this.BundleBridge
            ?? throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");
        if (!string.Equals(bridge.RetainedBundlePath, retainedBundlePath, StringComparison.Ordinal))
            throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");

        AssertPrivateDirectory(GetHandleIdentity(directory));
        AssertBundleBridge(directory, bridge.EntryName, bridge.RetainedBundlePath, bridge.Identity);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.CleanupStarted, 1) != 0)
            return;

        Task cleanup = Task.Run(this.CleanupAndDispose);
        if (await Task.WhenAny(cleanup, Task.Delay(CleanupTimeout)).ConfigureAwait(false) == cleanup)
            await cleanup.ConfigureAwait(false);
        else
            ObserveEventually(cleanup);
    }

    private void CleanupAndDispose()
    {
        SafeFileHandle? parent = Interlocked.Exchange(ref this.ParentHandle, null);
        SafeFileHandle? directory = Interlocked.Exchange(ref this.DirectoryHandle, null);
        if (parent is null || directory is null)
        {
            directory?.Dispose();
            parent?.Dispose();
            return;
        }

        try
        {
            int entryCount = 0;
            Stopwatch deadline = Stopwatch.StartNew();
            CleanupDirectory(directory, depth: 0, ref entryCount, deadline);

            DirectoryIdentity? named = GetNamedIdentity(parent, this.EntryName, allowMissing: true);
            if (named is not null && this.Identity.IsSameObject(named.Value))
                _ = unlinkat(parent, this.EntryName, AtRemoveDirectory);
        }
        catch
        {
            // Best effort applies only through retained directory authority. Never fall back to recursive pathname deletion.
        }
        finally
        {
            directory.Dispose();
            parent.Dispose();
        }
    }

    private static void CleanupDirectory(
        SafeFileHandle directory,
        int depth,
        ref int entryCount,
        Stopwatch deadline
    )
    {
        if (depth > MaximumCleanupDepth || deadline.Elapsed >= CleanupTimeout)
            return;

        using SafeFileHandle enumeration = OpenAtDirectory(directory, ".");
        byte[] buffer = new byte[DirectoryBufferBytes];
        while (deadline.Elapsed < CleanupTimeout)
        {
            long read;
            read = syscall_getdents64(
                SystemCallGetDirectoryEntries64,
                enumeration,
                buffer,
                (uint)buffer.Length
            );
            if (read == 0)
                return;
            if (read < 0 || read > buffer.Length)
                return;

            int offset = 0;
            while (offset < read && deadline.Elapsed < CleanupTimeout)
            {
                if (read - offset < DirectoryEntryHeaderBytes)
                    return;
                ushort recordLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 16, 2));
                if (recordLength < DirectoryEntryHeaderBytes || recordLength > read - offset)
                    return;
                ReadOnlySpan<byte> nameBytes = buffer.AsSpan(offset + DirectoryEntryHeaderBytes, recordLength - DirectoryEntryHeaderBytes);
                int terminator = nameBytes.IndexOf((byte)0);
                if (terminator < 0)
                    return;
                string name;
                try
                {
                    name = StrictUtf8.GetString(nameBytes[..terminator]);
                }
                catch (DecoderFallbackException)
                {
                    return;
                }
                offset += recordLength;
                if (name is "." or "..")
                    continue;
                if (++entryCount > MaximumCleanupEntries || name.Length == 0 || name.Contains('/'))
                    return;

                DirectoryIdentity? named = GetNamedIdentity(directory, name, allowMissing: true);
                if (named is null)
                    continue;
                if (named.Value.IsDirectory)
                {
                    SafeFileHandle? child = null;
                    try
                    {
                        child = OpenAtDirectory(directory, name);
                        DirectoryIdentity opened = GetHandleIdentity(child);
                        if (!opened.IsSameObject(named.Value))
                            continue;
                        CleanupDirectory(child, depth + 1, ref entryCount, deadline);
                        DirectoryIdentity? after = GetNamedIdentity(directory, name, allowMissing: true);
                        if (after is not null && opened.IsSameObject(after.Value))
                            _ = unlinkat(directory, name, AtRemoveDirectory);
                    }
                    catch
                    {
                        // Leave an entry which can't be removed safely through its retained parent.
                    }
                    finally
                    {
                        child?.Dispose();
                    }
                }
                else
                    _ = unlinkat(directory, name, 0);
            }
        }
    }

    private static SafeFileHandle OpenAbsoluteDirectoryNoFollow(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");

        SafeFileHandle current = OpenDirectory("/");
        try
        {
            foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment is "." or ".." || segment.Any(char.IsControl))
                    throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");
                SafeFileHandle next = OpenAtDirectory(current, segment);
                current.Dispose();
                current = next;
            }
            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        int descriptor = open(
            path,
            OpenReadOnly | OpenNonBlocking | OpenDirectoryFlag | OpenNoFollow | OpenCloseOnExec
        );
        if (descriptor < 0)
            ThrowNativeFailure();
        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    private static SafeFileHandle OpenAtDirectory(SafeFileHandle parent, string name)
    {
        int descriptor = openat(
            parent,
            name,
            OpenReadOnly | OpenNonBlocking | OpenDirectoryFlag | OpenNoFollow | OpenCloseOnExec
        );
        if (descriptor < 0)
            ThrowNativeFailure();
        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    private static DirectoryIdentity GetHandleIdentity(SafeFileHandle handle)
    {
        if (statx(handle, "", 0x1000 | AtSymlinkNoFollow, StatxBasicStats, out Statx data) != 0)
            ThrowNativeFailure();
        return DirectoryIdentity.From(data);
    }

    private static DirectoryIdentity? GetNamedIdentity(SafeFileHandle parent, string name, bool allowMissing)
    {
        if (statx(parent, name, AtSymlinkNoFollow, StatxBasicStats, out Statx data) == 0)
            return DirectoryIdentity.From(data);
        if (allowMissing && Marshal.GetLastWin32Error() == ErrorNoEntry)
            return null;
        ThrowNativeFailure();
        return null;
    }

    private static void AssertPrivateDirectory(DirectoryIdentity identity)
    {
        if (!identity.IsDirectory || identity.UserId != geteuid() || (identity.Mode & 0xfff) != PrivateDirectoryMode)
            throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");
    }

    private static void AssertBundleBridge(
        SafeFileHandle directory,
        string entryName,
        string retainedBundlePath,
        DirectoryIdentity expectedIdentity
    )
    {
        DirectoryIdentity current = GetNamedIdentity(directory, entryName, allowMissing: false)
            ?? throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");
        AssertPrivateBundleBridge(current);
        if (
            !expectedIdentity.IsSameNode(current)
            || !string.Equals(ReadLinkAt(directory, entryName), retainedBundlePath, StringComparison.Ordinal)
        )
        {
            throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");
        }
    }

    private static void AssertPrivateBundleBridge(DirectoryIdentity identity)
    {
        if (!identity.IsSymbolicLink || identity.UserId != geteuid() || identity.LinkCount != 1)
            throw new PackageSecurityException("The GitHub attestation verifier couldn't be started safely.");
    }

    private static string ReadLinkAt(SafeFileHandle directory, string entryName)
    {
        byte[] buffer = new byte[MaximumLinkTargetBytes];
        long length = readlinkat(directory, entryName, buffer, (ulong)buffer.Length);
        if (length is <= 0 or >= MaximumLinkTargetBytes)
            ThrowNativeFailure();
        return StrictUtf8.GetString(buffer, 0, checked((int)length));
    }

    private static void ThrowNativeFailure()
    {
        throw new IOException("A retained private attestation directory operation failed.");
    }

    private static void ObserveEventually(Task task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int openat(SafeFileHandle directory, string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int mkdirat(SafeFileHandle directory, string path, int mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int fchmod(SafeFileHandle handle, int mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int unlinkat(SafeFileHandle directory, string path, int flags);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int symlinkat(string target, SafeFileHandle directory, string linkPath);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern long readlinkat(SafeFileHandle directory, string path, [Out] byte[] buffer, ulong bufferSize);

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    [DllImport("libc", SetLastError = true, EntryPoint = "statx")]
    private static extern int statx(
        SafeFileHandle directory,
        string path,
        int flags,
        uint mask,
        out Statx data
    );

    [DllImport("libc", SetLastError = true, EntryPoint = "syscall")]
    private static extern long syscall_getdents64(
        long systemCallNumber,
        SafeFileHandle directory,
        [Out] byte[] buffer,
        uint count
    );

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
        public StatxTimestamp AccessTime;
        public StatxTimestamp BirthTime;
        public StatxTimestamp ChangeTime;
        public StatxTimestamp ModificationTime;
        public uint RootDeviceMajor;
        public uint RootDeviceMinor;
        public uint DeviceMajor;
        public uint DeviceMinor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StatxTimestamp
    {
        public long Seconds;
        public uint Nanoseconds;
        public int Reserved;
    }

    private readonly record struct DirectoryIdentity(
        ulong Inode,
        uint DeviceMajor,
        uint DeviceMinor,
        uint LinkCount,
        uint UserId,
        ushort Mode
    )
    {
        public bool IsDirectory => (this.Mode & FileTypeMask) == FileTypeDirectory;
        public bool IsSymbolicLink => (this.Mode & FileTypeMask) == FileTypeSymbolicLink;

        public bool IsSameNode(DirectoryIdentity other)
        {
            return this.Inode == other.Inode
                && this.DeviceMajor == other.DeviceMajor
                && this.DeviceMinor == other.DeviceMinor;
        }

        public bool IsSameObject(DirectoryIdentity other)
        {
            return this.IsSameNode(other)
                && this.IsDirectory
                && other.IsDirectory;
        }

        public static DirectoryIdentity From(Statx data)
        {
            return new DirectoryIdentity(
                data.Inode,
                data.DeviceMajor,
                data.DeviceMinor,
                data.LinkCount,
                data.UserId,
                data.Mode
            );
        }
    }

    private readonly record struct BundleBridgeIdentity(
        string EntryName,
        string RetainedBundlePath,
        DirectoryIdentity Identity
    );
}
