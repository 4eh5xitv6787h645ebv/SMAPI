using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using StardewModdingAPI.Installer.Core.Ownership;

namespace StardewModdingAPI.Installer.Core.Security;

/// <summary>The supported entry types below an anchored Linux directory.</summary>
public enum LinuxAnchoredEntryKind
{
    RegularFile,
    Directory
}

/// <summary>An exact observed Linux filesystem identity.</summary>
public sealed record LinuxFileIdentity(
    LinuxAnchoredEntryKind Kind,
    ulong Inode,
    uint DeviceMajor,
    uint DeviceMinor,
    uint LinkCount,
    long Size,
    int UnixMode,
    long ModificationSeconds,
    uint ModificationNanoseconds,
    long ChangeSeconds,
    uint ChangeNanoseconds
)
{
    /// <summary>Whether two observations refer to the same filesystem object.</summary>
    public bool IsSameObject(LinuxFileIdentity other)
    {
        return other != null
            && this.Kind == other.Kind
            && this.Inode == other.Inode
            && this.DeviceMajor == other.DeviceMajor
            && this.DeviceMinor == other.DeviceMinor;
    }
}

/// <summary>An owned safe handle opened through a <see cref="LinuxAnchoredFileSystem"/>.</summary>
public sealed class LinuxAnchoredFile : IDisposable
{
    internal SafeFileHandle Handle { get; }

    /// <summary>The identity captured when the handle was opened.</summary>
    public LinuxFileIdentity Identity { get; }

    internal LinuxAnchoredFile(SafeFileHandle handle, LinuxFileIdentity identity)
    {
        this.Handle = handle;
        this.Identity = identity;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        this.Handle.Dispose();
    }

    internal void AssertOpen()
    {
        if (this.Handle.IsClosed || this.Handle.IsInvalid)
            throw new ObjectDisposedException(nameof(LinuxAnchoredFile));
    }
}

/// <summary>
/// Performs Linux file operations relative to an open real-directory handle. Every traversed segment is opened with
/// no-follow semantics, so later replacement of the selected root path can't redirect operations.
/// </summary>
public sealed class LinuxAnchoredFileSystem : IDisposable
{
    private const int OpenReadOnly = 0;
    private const int OpenReadWrite = 2;
    private const int OpenNonBlocking = 0x800;
    private const int OpenCreate = 0x40;
    private const int OpenExclusive = 0x80;
    private const int OpenDirectory = 0x10000;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const int DuplicateCloseOnExec = 1030;
    private const int AtSymlinkNoFollow = 0x100;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxBasicStats = 0x7ff;
    private const uint RenameNoReplace = 1;
    private const uint RenameExchange = 2;
    private const ushort FileTypeMask = 0xf000;
    private const ushort FileTypeDirectory = 0x4000;
    private const ushort FileTypeRegular = 0x8000;
    private const int ErrorNoEntry = 2;
    private const int ErrorExists = 17;
    private const int ErrorNotDirectory = 20;
    private const int ErrorNotSupported = 38;
    private const int ErrorSymbolicLinkLoop = 40;
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const int AtRemoveDirectory = 0x200;

    private readonly SafeFileHandle RootHandle;
    private readonly LinuxFileIdentity RootIdentity;
    private bool Disposed;

    /// <summary>The captured identity of this anchored root directory.</summary>
    public LinuxFileIdentity Identity => this.RootIdentity;

    /// <summary>Observe the current metadata for the captured root handle after verifying its stable object identity.</summary>
    public LinuxFileIdentity GetCurrentRootIdentity()
    {
        this.AssertUsable();
        return GetHandleIdentity(this.RootHandle, requireSingleLinkRegularFile: false);
    }

    /// <summary>Open and anchor a real Linux directory. A symbolic-link root is rejected.</summary>
    public LinuxAnchoredFileSystem(string rootPath)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Anchored filesystem operations require Linux.");
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("An anchored root path is required.", nameof(rootPath));

        string fullPath = Path.GetFullPath(rootPath);
        try
        {
            this.RootHandle = OpenAbsoluteDirectoryNoFollow(fullPath);
        }
        catch (IOException ex)
        {
            throw new IOException("The anchored root isn't a real accessible directory.", ex);
        }
        try
        {
            this.RootIdentity = GetHandleIdentity(this.RootHandle, requireSingleLinkRegularFile: false);
            if (this.RootIdentity.Kind != LinuxAnchoredEntryKind.Directory)
                throw new IOException("The anchored root isn't a directory.");
        }
        catch
        {
            this.RootHandle.Dispose();
            throw;
        }
    }

    private LinuxAnchoredFileSystem(SafeFileHandle rootHandle)
    {
        this.RootHandle = rootHandle;
        try
        {
            this.RootIdentity = GetHandleIdentity(this.RootHandle, requireSingleLinkRegularFile: false);
            if (this.RootIdentity.Kind != LinuxAnchoredEntryKind.Directory)
                throw new IOException("The anchored root isn't a directory.");
        }
        catch
        {
            this.RootHandle.Dispose();
            throw;
        }
    }

    /// <summary>Open a real subdirectory as a new anchored filesystem without resolving it again by absolute path.</summary>
    public LinuxAnchoredFileSystem OpenSubdirectory(string relativePath)
    {
        this.AssertUsable();
        return new LinuxAnchoredFileSystem(this.OpenDirectoryPath(relativePath));
    }

    /// <summary>Get a safe regular-file handle without following any path segment.</summary>
    public LinuxAnchoredFile OpenRegularFileForRead(string relativePath)
    {
        this.AssertUsable();
        using ParentAndLeaf parent = this.OpenParent(relativePath);
        SafeFileHandle handle = OpenAt(
            parent.Parent,
            parent.Leaf,
            OpenReadOnly | OpenNonBlocking | OpenNoFollow | OpenCloseOnExec,
            0,
            "Couldn't open an anchored regular file"
        );
        try
        {
            LinuxFileIdentity identity = GetHandleIdentity(handle, requireSingleLinkRegularFile: true);
            if (identity.Kind != LinuxAnchoredEntryKind.RegularFile)
                throw new IOException("The anchored entry isn't a regular file.");
            return new LinuxAnchoredFile(handle, identity);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Open a stable single-link regular file for descriptor-relative reads and writes.</summary>
    public LinuxAnchoredFile OpenRegularFileForReadWrite(string relativePath)
    {
        this.AssertUsable();
        using ParentAndLeaf parent = this.OpenParent(relativePath);
        SafeFileHandle handle = OpenAt(
            parent.Parent,
            parent.Leaf,
            OpenReadWrite | OpenNonBlocking | OpenNoFollow | OpenCloseOnExec,
            0,
            "Couldn't open an anchored regular file for writing"
        );
        try
        {
            LinuxFileIdentity identity = GetHandleIdentity(handle, requireSingleLinkRegularFile: true);
            if (identity.Kind != LinuxAnchoredEntryKind.RegularFile)
                throw new IOException("The anchored entry isn't a regular file.");
            return new LinuxAnchoredFile(handle, identity);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Create or open a private regular lock file and acquire a nonblocking process-wide exclusive lock.</summary>
    public LinuxAnchoredFile AcquireExclusiveFileLock(string relativePath, int unixMode)
    {
        this.AssertUsable();
        ValidateUnixMode(unixMode);
        using ParentAndLeaf parent = this.OpenParent(relativePath);
        SafeFileHandle handle = OpenAt(
            parent.Parent,
            parent.Leaf,
            OpenReadWrite | OpenCreate | OpenNonBlocking | OpenNoFollow | OpenCloseOnExec,
            (uint)unixMode,
            "Couldn't create or open the anchored lock file"
        );
        try
        {
            LinuxFileIdentity identity = GetHandleIdentity(handle, requireSingleLinkRegularFile: true);
            if (identity.Kind != LinuxAnchoredEntryKind.RegularFile)
                throw new IOException("The anchored lock entry isn't a regular file.");
            LinuxFileIdentity? named = GetIdentityAt(parent.Parent, parent.Leaf, allowMissing: false, requireSingleLinkRegularFile: true);
            if (named != identity)
                throw new IOException("The anchored lock-file identity changed while it was opened.");
            SetHandleMode(handle, unixMode);
            if (flock(handle, LockExclusive | LockNonBlocking) != 0)
                throw new LinuxNativeIOException("Couldn't acquire the anchored exclusive lock", Marshal.GetLastWin32Error());
            Fsync(handle);
            Fsync(parent.Parent);
            return new LinuxAnchoredFile(handle, GetHandleIdentity(handle, requireSingleLinkRegularFile: true));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Create a new empty single-link regular file without replacing an existing entry.</summary>
    public LinuxAnchoredFile CreateNewFile(string relativePath, int unixMode)
    {
        this.AssertUsable();
        ValidateUnixMode(unixMode);
        using ParentAndLeaf parent = this.OpenParent(relativePath);
        SafeFileHandle handle = OpenAt(
            parent.Parent,
            parent.Leaf,
            OpenReadWrite | OpenCreate | OpenExclusive | OpenNonBlocking | OpenNoFollow | OpenCloseOnExec,
            (uint)unixMode,
            "Couldn't create an anchored file without replacement"
        );
        try
        {
            SetHandleMode(handle, unixMode);
            LinuxFileIdentity identity = GetHandleIdentity(handle, requireSingleLinkRegularFile: true);
            if (identity.Kind != LinuxAnchoredEntryKind.RegularFile)
                throw new IOException("The newly created anchored entry isn't a regular file.");
            Fsync(parent.Parent);
            return new LinuxAnchoredFile(handle, identity);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Copy an already-open regular file to a new anchored path and durably verify the bytes.</summary>
    public LinuxFileIdentity CopyFile(
        LinuxAnchoredFile source,
        string destinationRelativePath,
        int unixMode,
        CancellationToken cancellationToken = default
    )
    {
        this.AssertUsable();
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        source.AssertOpen();
        LinuxFileIdentity sourceBefore = GetHandleIdentity(source.Handle, requireSingleLinkRegularFile: true);
        if (sourceBefore != source.Identity)
            throw new IOException("The source identity changed after it was opened.");

        using LinuxAnchoredFile destination = this.CreateNewFile(destinationRelativePath, unixMode);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            long offset = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = RandomAccess.Read(source.Handle, buffer, offset);
                if (count == 0)
                    break;
                RandomAccess.Write(destination.Handle, buffer.AsSpan(0, count), offset);
                offset = checked(offset + count);
            }
            Fsync(destination.Handle);

            LinuxFileIdentity sourceAfter = GetHandleIdentity(source.Handle, requireSingleLinkRegularFile: true);
            LinuxFileIdentity destinationAfter = GetHandleIdentity(destination.Handle, requireSingleLinkRegularFile: true);
            if (sourceAfter != sourceBefore)
                throw new IOException("The source identity changed while it was copied.");
            if (
                destinationAfter.Size != sourceAfter.Size
                || this.ComputeSha256(source, cancellationToken) != this.ComputeSha256(destination, cancellationToken)
            )
                throw new IOException("The anchored copy failed byte-for-byte verification.");
            return destinationAfter;
        }
        catch
        {
            try
            {
                LinuxFileIdentity current = GetHandleIdentity(destination.Handle, requireSingleLinkRegularFile: true);
                this.UnlinkFile(destinationRelativePath, current);
            }
            catch
            {
                // The original copy error is more useful; a caller can inspect the private staging root.
            }
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Compute SHA-256 from an already-open stable single-link regular-file handle.</summary>
    public string ComputeSha256(LinuxAnchoredFile file, CancellationToken cancellationToken = default)
    {
        this.AssertUsable();
        ArgumentNullException.ThrowIfNull(file);
        cancellationToken.ThrowIfCancellationRequested();
        file.AssertOpen();
        LinuxFileIdentity before = GetHandleIdentity(file.Handle, requireSingleLinkRegularFile: true);
        if (before != file.Identity && !before.IsSameObject(file.Identity))
            throw new IOException("The open file handle no longer refers to its captured object.");

        using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            long offset = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = RandomAccess.Read(file.Handle, buffer, offset);
                if (count == 0)
                    break;
                hasher.AppendData(buffer, 0, count);
                offset = checked(offset + count);
            }
            LinuxFileIdentity after = GetHandleIdentity(file.Handle, requireSingleLinkRegularFile: true);
            if (after != before)
                throw new IOException("The file identity changed while it was hashed.");
            return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Read a bounded stable regular file through its already-open handle.</summary>
    public byte[] ReadAllBytes(
        LinuxAnchoredFile file,
        int maximumBytes,
        CancellationToken cancellationToken = default
    )
    {
        this.AssertUsable();
        ArgumentNullException.ThrowIfNull(file);
        if (maximumBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        cancellationToken.ThrowIfCancellationRequested();
        file.AssertOpen();

        LinuxFileIdentity before = GetHandleIdentity(file.Handle, requireSingleLinkRegularFile: true);
        if (!before.IsSameObject(file.Identity) || before.Size > maximumBytes)
            throw new IOException("The open file is too large or no longer refers to its captured object.");
        byte[] result = new byte[(int)before.Size];
        int offset = 0;
        while (offset < result.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = RandomAccess.Read(
                file.Handle,
                result.AsSpan(offset, Math.Min(128 * 1024, result.Length - offset)),
                offset
            );
            if (count == 0)
                throw new EndOfStreamException("The anchored file became shorter while it was read.");
            offset += count;
        }

        LinuxFileIdentity after = GetHandleIdentity(file.Handle, requireSingleLinkRegularFile: true);
        if (after != before)
            throw new IOException("The anchored file identity changed while it was read.");
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>Copy and SHA-256 hash a bounded stable regular file through its already-open handle.</summary>
    public async Task<string> CopyAndHashAsync(
        LinuxAnchoredFile file,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken = default
    )
    {
        this.AssertUsable();
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(destination);
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (!destination.CanWrite || !destination.CanSeek || destination.Position != 0 || destination.Length != 0)
            throw new ArgumentException("The copy destination must be writable, seekable, empty, and positioned at zero.", nameof(destination));
        cancellationToken.ThrowIfCancellationRequested();
        file.AssertOpen();

        LinuxFileIdentity before = GetHandleIdentity(file.Handle, requireSingleLinkRegularFile: true);
        if (!before.IsSameObject(file.Identity) || before.Size <= 0 || before.Size > maximumBytes)
            throw new IOException("The open file is empty, too large, or no longer refers to its captured object.");
        using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            long offset = 0;
            while (offset < before.Size)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = RandomAccess.Read(
                    file.Handle,
                    buffer.AsSpan(0, (int)Math.Min(buffer.Length, before.Size - offset)),
                    offset
                );
                if (count == 0)
                    throw new EndOfStreamException("The anchored file became shorter while it was copied.");
                hasher.AppendData(buffer, 0, count);
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                offset = checked(offset + count);
            }
            LinuxFileIdentity after = GetHandleIdentity(file.Handle, requireSingleLinkRegularFile: true);
            if (after != before)
                throw new IOException("The anchored file identity changed while it was copied.");
            return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Append one bounded record through a captured file handle and durably verify its anchored name and length.</summary>
    public long AppendAndFsync(
        LinuxAnchoredFile file,
        string relativePath,
        ReadOnlySpan<byte> content,
        long expectedLength,
        long maximumLength
    )
    {
        this.AssertUsable();
        ArgumentNullException.ThrowIfNull(file);
        if (expectedLength < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedLength));
        if (maximumLength < expectedLength)
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
        file.AssertOpen();

        long resultingLength = checked(expectedLength + content.Length);
        if (resultingLength > maximumLength)
            throw new IOException("The anchored write would exceed its configured byte bound.");

        using ParentAndLeaf parent = this.OpenParent(relativePath);
        LinuxFileIdentity before = GetHandleIdentity(file.Handle, requireSingleLinkRegularFile: true);
        LinuxFileIdentity? namedBefore = GetIdentityAt(parent.Parent, parent.Leaf, allowMissing: false, requireSingleLinkRegularFile: true);
        if (!before.IsSameObject(file.Identity) || before.Size != expectedLength || namedBefore != before)
            throw new IOException("The anchored write target identity or expected length changed before mutation.");

        RandomAccess.Write(file.Handle, content, expectedLength);
        Fsync(file.Handle);

        LinuxFileIdentity after = GetHandleIdentity(file.Handle, requireSingleLinkRegularFile: true);
        LinuxFileIdentity? namedAfter = GetIdentityAt(parent.Parent, parent.Leaf, allowMissing: false, requireSingleLinkRegularFile: true);
        if (!after.IsSameObject(before) || after.Size != resultingLength || namedAfter != after)
            throw new IOException("The anchored write target identity changed during mutation.");
        return resultingLength;
    }

    /// <summary>Durably truncate a named regular file through a verified open handle.</summary>
    public LinuxFileIdentity TruncateAndFsync(LinuxAnchoredFile file, string relativePath, long length)
    {
        this.AssertUsable();
        ArgumentNullException.ThrowIfNull(file);
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));
        file.AssertOpen();

        using ParentAndLeaf parent = this.OpenParent(relativePath);
        LinuxFileIdentity before = GetHandleIdentity(file.Handle, requireSingleLinkRegularFile: true);
        LinuxFileIdentity? namedBefore = GetIdentityAt(parent.Parent, parent.Leaf, allowMissing: false, requireSingleLinkRegularFile: true);
        if (!before.IsSameObject(file.Identity) || namedBefore != before || length > before.Size)
            throw new IOException("The anchored truncate target identity or requested length is invalid.");
        if (ftruncate(file.Handle, length) != 0)
            throw new LinuxNativeIOException("Couldn't truncate an anchored file", Marshal.GetLastWin32Error());
        Fsync(file.Handle);
        LinuxFileIdentity after = GetHandleIdentity(file.Handle, requireSingleLinkRegularFile: true);
        LinuxFileIdentity? namedAfter = GetIdentityAt(parent.Parent, parent.Leaf, allowMissing: false, requireSingleLinkRegularFile: true);
        if (!after.IsSameObject(before) || after.Size != length || namedAfter != after)
            throw new IOException("The anchored truncate target identity changed during mutation.");
        return after;
    }

    /// <summary>Get an entry identity without following links, or <see langword="null"/> when absent.</summary>
    /// <remarks>Symbolic links, hardlinked regular files, and special entries are rejected rather than returned.</remarks>
    public LinuxFileIdentity? Stat(string relativePath)
    {
        this.AssertUsable();
        try
        {
            using ParentAndLeaf parent = this.OpenParent(relativePath);
            return GetIdentityAt(parent.Parent, parent.Leaf, allowMissing: true, requireSingleLinkRegularFile: true);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Create and durably flush any missing real-directory segments.</summary>
    public LinuxFileIdentity EnsureDirectory(string relativePath, int unixMode)
    {
        return this.EnsureDirectory(relativePath, unixMode, out _);
    }

    /// <summary>Create and durably flush missing real-directory segments and report whether the final segment was created.</summary>
    public LinuxFileIdentity EnsureDirectory(string relativePath, int unixMode, out bool created)
    {
        this.AssertUsable();
        ValidateUnixMode(unixMode);
        string[] segments = GetSegments(relativePath);
        created = false;
        SafeFileHandle current = Duplicate(this.RootHandle);
        try
        {
            for (int index = 0; index < segments.Length; index++)
            {
                string segment = segments[index];
                SafeFileHandle next;
                try
                {
                    next = OpenDirectoryAt(current, segment);
                }
                catch (FileNotFoundException)
                {
                    bool createdSegment = mkdirat(current, segment, (uint)unixMode) == 0;
                    if (!createdSegment)
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != ErrorExists)
                            throw CreatePathException("Couldn't create an anchored directory", error);
                    }
                    if (createdSegment && index == segments.Length - 1)
                        created = true;
                    LinuxFileIdentity? createdIdentity = createdSegment
                        ? GetIdentityAt(current, segment, allowMissing: false, requireSingleLinkRegularFile: false)
                        : null;
                    next = OpenDirectoryAt(current, segment);
                    LinuxFileIdentity openedIdentity = GetHandleIdentity(next, requireSingleLinkRegularFile: false);
                    if (createdIdentity != null && openedIdentity != createdIdentity)
                    {
                        next.Dispose();
                        throw new IOException("An anchored directory identity changed while it was created.");
                    }
                    SetHandleMode(next, unixMode);
                    Fsync(current);
                }

                current.Dispose();
                current = next;
            }

            LinuxFileIdentity identity = GetHandleIdentity(current, requireSingleLinkRegularFile: false);
            if (identity.Kind != LinuxAnchoredEntryKind.Directory)
                throw new IOException("An anchored directory path resolved to a non-directory entry.");
            SetHandleMode(current, unixMode);
            Fsync(current);
            return GetHandleIdentity(current, requireSingleLinkRegularFile: false);
        }
        finally
        {
            current.Dispose();
        }
    }

    /// <summary>Enumerate the immediate entry names of a real anchored directory.</summary>
    public IReadOnlyList<string> EnumerateEntryNames(string? relativePath = null, int maximumEntries = int.MaxValue)
    {
        this.AssertUsable();
        if (maximumEntries < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        using SafeFileHandle directory = relativePath is null ? Duplicate(this.RootHandle) : this.OpenDirectoryPath(relativePath);
        SafeFileHandle duplicate = Duplicate(directory);
        IntPtr stream = fdopendir(duplicate.DangerousGetHandle().ToInt32());
        if (stream == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            duplicate.Dispose();
            throw new LinuxNativeIOException("Couldn't enumerate an anchored directory", error);
        }

        duplicate.SetHandleAsInvalid(); // fdopendir owns the descriptor from here.
        List<string> names = new();
        try
        {
            while (true)
            {
                Marshal.WriteInt32(__errno_location(), 0);
                IntPtr entry = readdir(stream);
                if (entry == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != 0)
                        throw new LinuxNativeIOException("Couldn't enumerate an anchored directory", error);
                    break;
                }

                ushort recordLength = unchecked((ushort)Marshal.ReadInt16(entry, 16));
                const int nameOffset = 19;
                if (recordLength <= nameOffset)
                    throw new IOException("The anchored directory returned an invalid entry record.");
                int maximumNameBytes = recordLength - nameOffset;
                byte[] bytes = new byte[maximumNameBytes];
                Marshal.Copy(IntPtr.Add(entry, nameOffset), bytes, 0, bytes.Length);
                int length = Array.IndexOf(bytes, (byte)0);
                if (length < 0)
                    throw new IOException("The anchored directory returned an unterminated entry name.");
                string name = new UTF8Encoding(false, true).GetString(bytes, 0, length);
                if (name is "." or "..")
                    continue;
                if (name.Length == 0 || name.Contains('/') || name.Contains('\0'))
                    throw new IOException("The anchored directory returned an unsafe entry name.");
                names.Add(name);
                if (names.Count > maximumEntries)
                    throw new IOException("The anchored directory exceeds its bounded entry limit.");
            }
        }
        finally
        {
            if (closedir(stream) != 0)
                throw new LinuxNativeIOException("Couldn't close an anchored directory enumeration", Marshal.GetLastWin32Error());
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>Rename a known regular file without replacing a destination.</summary>
    public LinuxFileIdentity RenameFileNoReplace(string sourceRelativePath, string destinationRelativePath, LinuxFileIdentity expectedSource)
    {
        this.AssertUsable();
        ArgumentNullException.ThrowIfNull(expectedSource);
        if (string.Equals(sourceRelativePath, destinationRelativePath, StringComparison.Ordinal))
            throw new ArgumentException("Rename source and destination must differ.", nameof(destinationRelativePath));

        using ParentAndLeaf source = this.OpenParent(sourceRelativePath);
        using ParentAndLeaf destination = this.OpenParent(destinationRelativePath);
        using SafeFileHandle sourceHandle = OpenAt(
            source.Parent,
            source.Leaf,
            OpenReadOnly | OpenNonBlocking | OpenNoFollow | OpenCloseOnExec,
            0,
            "Couldn't open the anchored rename source"
        );
        LinuxFileIdentity immediatelyBefore = GetHandleIdentity(sourceHandle, requireSingleLinkRegularFile: true);
        if (immediatelyBefore != expectedSource || GetIdentityAt(source.Parent, source.Leaf, false, true) != expectedSource)
            throw new IOException("The rename source identity changed before mutation.");
        if (GetIdentityAt(destination.Parent, destination.Leaf, true, true) != null)
            throw new IOException("The no-replace rename destination already exists.");

        RenameAtNoReplace(source.Parent, source.Leaf, destination.Parent, destination.Leaf);
        Fsync(source.Parent);
        Fsync(destination.Parent);

        LinuxFileIdentity? result = GetIdentityAt(destination.Parent, destination.Leaf, false, true);
        if (result == null || !result.IsSameObject(expectedSource))
        {
            TryRollbackUnexpectedRename(destination.Parent, destination.Leaf, source.Parent, source.Leaf);
            throw new IOException("The rename source identity changed during mutation.");
        }
        return result;
    }

    /// <summary>
    /// Atomically publish a known private regular file, replacing either the exact expected destination or no destination.
    /// The operation never follows the destination leaf and durably flushes both parent directories.
    /// </summary>
    public LinuxFileIdentity ReplaceFileAtomically(
        string sourceRelativePath,
        string destinationRelativePath,
        LinuxFileIdentity expectedSource,
        LinuxFileIdentity? expectedDestination
    )
    {
        this.AssertUsable();
        ArgumentNullException.ThrowIfNull(expectedSource);
        if (expectedSource.Kind != LinuxAnchoredEntryKind.RegularFile || expectedSource.LinkCount != 1)
            throw new ArgumentException("The expected replacement source isn't a single-link regular file.", nameof(expectedSource));
        if (expectedDestination != null && (expectedDestination.Kind != LinuxAnchoredEntryKind.RegularFile || expectedDestination.LinkCount != 1))
            throw new ArgumentException("The expected replacement destination isn't a single-link regular file.", nameof(expectedDestination));
        if (string.Equals(sourceRelativePath, destinationRelativePath, StringComparison.Ordinal))
            throw new ArgumentException("Replacement source and destination must differ.", nameof(destinationRelativePath));

        using ParentAndLeaf source = this.OpenParent(sourceRelativePath);
        using ParentAndLeaf destination = this.OpenParent(destinationRelativePath);
        using SafeFileHandle sourceHandle = OpenAt(
            source.Parent,
            source.Leaf,
            OpenReadOnly | OpenNonBlocking | OpenNoFollow | OpenCloseOnExec,
            0,
            "Couldn't open the anchored replacement source"
        );
        LinuxFileIdentity sourceBefore = GetHandleIdentity(sourceHandle, requireSingleLinkRegularFile: true);
        LinuxFileIdentity? destinationBefore = GetIdentityAt(destination.Parent, destination.Leaf, allowMissing: true, requireSingleLinkRegularFile: true);
        if (sourceBefore != expectedSource || GetIdentityAt(source.Parent, source.Leaf, false, true) != expectedSource)
            throw new IOException("The replacement source identity changed before mutation.");
        if (destinationBefore != expectedDestination)
            throw new IOException("The replacement destination identity changed before mutation.");

        if (expectedDestination == null)
            RenameAtNoReplace(source.Parent, source.Leaf, destination.Parent, destination.Leaf);
        else
            RenameAtExchange(source.Parent, source.Leaf, destination.Parent, destination.Leaf);
        Fsync(source.Parent);
        Fsync(destination.Parent);

        LinuxFileIdentity? result = GetIdentityAt(destination.Parent, destination.Leaf, allowMissing: false, requireSingleLinkRegularFile: true);
        LinuxFileIdentity? displaced = expectedDestination == null
            ? null
            : GetIdentityAt(source.Parent, source.Leaf, allowMissing: false, requireSingleLinkRegularFile: true);
        if (
            result == null
            || !result.IsSameObject(expectedSource)
            || (expectedDestination != null && (displaced == null || !MatchesAfterRename(displaced, expectedDestination)))
        )
        {
            if (expectedDestination != null)
                TryRollbackUnexpectedExchange(source.Parent, source.Leaf, destination.Parent, destination.Leaf);
            throw new IOException("The replacement result identity changed during mutation.");
        }
        if (displaced != null)
            this.UnlinkFile(sourceRelativePath, displaced);
        return result;
    }

    private static bool MatchesAfterRename(LinuxFileIdentity observed, LinuxFileIdentity expected)
    {
        // Linux changes ctime when a name is exchanged. All stable object and content-relevant metadata must still match.
        return observed.IsSameObject(expected)
            && observed.Kind == expected.Kind
            && observed.LinkCount == expected.LinkCount
            && observed.Size == expected.Size
            && observed.UnixMode == expected.UnixMode
            && observed.ModificationSeconds == expected.ModificationSeconds
            && observed.ModificationNanoseconds == expected.ModificationNanoseconds;
    }

    /// <summary>Rename a known real directory without replacing a destination.</summary>
    public LinuxFileIdentity RenameDirectoryNoReplace(string sourceRelativePath, string destinationRelativePath, LinuxFileIdentity expectedSource)
    {
        this.AssertUsable();
        ArgumentNullException.ThrowIfNull(expectedSource);
        if (expectedSource.Kind != LinuxAnchoredEntryKind.Directory)
            throw new ArgumentException("The expected rename source isn't a directory.", nameof(expectedSource));
        if (string.Equals(sourceRelativePath, destinationRelativePath, StringComparison.Ordinal))
            throw new ArgumentException("Rename source and destination must differ.", nameof(destinationRelativePath));

        using ParentAndLeaf source = this.OpenParent(sourceRelativePath);
        using ParentAndLeaf destination = this.OpenParent(destinationRelativePath);
        using SafeFileHandle sourceHandle = OpenDirectoryAt(source.Parent, source.Leaf);
        LinuxFileIdentity immediatelyBefore = GetHandleIdentity(sourceHandle, requireSingleLinkRegularFile: false);
        if (immediatelyBefore != expectedSource || GetIdentityAt(source.Parent, source.Leaf, false, false) != expectedSource)
            throw new IOException("The directory rename source identity changed before mutation.");
        if (GetIdentityAt(destination.Parent, destination.Leaf, true, false) != null)
            throw new IOException("The no-replace directory rename destination already exists.");

        RenameAtNoReplace(source.Parent, source.Leaf, destination.Parent, destination.Leaf);
        Fsync(source.Parent);
        Fsync(destination.Parent);
        LinuxFileIdentity? result = GetIdentityAt(destination.Parent, destination.Leaf, false, false);
        if (result is null || !result.IsSameObject(expectedSource))
        {
            TryRollbackUnexpectedRename(destination.Parent, destination.Leaf, source.Parent, source.Leaf);
            throw new IOException("The directory rename source identity changed during mutation.");
        }
        return result;
    }

    /// <summary>Unlink a regular file only if its exact identity still matches.</summary>
    public void UnlinkFile(string relativePath, LinuxFileIdentity expectedIdentity)
    {
        this.AssertUsable();
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        using ParentAndLeaf parent = this.OpenParent(relativePath);
        using SafeFileHandle handle = OpenAt(
            parent.Parent,
            parent.Leaf,
            OpenReadOnly | OpenNonBlocking | OpenNoFollow | OpenCloseOnExec,
            0,
            "Couldn't open the anchored unlink target"
        );
        LinuxFileIdentity observed = GetHandleIdentity(handle, requireSingleLinkRegularFile: true);
        if (observed != expectedIdentity || GetIdentityAt(parent.Parent, parent.Leaf, false, true) != expectedIdentity)
            throw new IOException("The unlink target identity changed before mutation.");
        if (unlinkat(parent.Parent, parent.Leaf, 0) != 0)
            throw CreatePathException("Couldn't unlink the anchored regular file", Marshal.GetLastWin32Error());
        Fsync(parent.Parent);

        LinuxFileIdentity after = GetHandleIdentity(handle, requireSingleLinkRegularFile: false);
        if (after.LinkCount != 0)
            throw new IOException("The unlink target identity changed during mutation.");
    }

    /// <summary>Remove an empty real directory only if its exact anchored identity still matches.</summary>
    public void RemoveEmptyDirectory(string relativePath, LinuxFileIdentity expectedIdentity)
    {
        this.AssertUsable();
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        if (expectedIdentity.Kind != LinuxAnchoredEntryKind.Directory)
            throw new ArgumentException("The expected identity isn't a directory.", nameof(expectedIdentity));
        using ParentAndLeaf parent = this.OpenParent(relativePath);
        using SafeFileHandle handle = OpenDirectoryAt(parent.Parent, parent.Leaf);
        LinuxFileIdentity observed = GetHandleIdentity(handle, requireSingleLinkRegularFile: false);
        if (observed != expectedIdentity || GetIdentityAt(parent.Parent, parent.Leaf, false, false) != expectedIdentity)
            throw new IOException("The directory identity changed before removal.");
        using (LinuxAnchoredFileSystem directory = new(Duplicate(handle)))
        {
            if (directory.EnumerateEntryNames().Count != 0)
                throw new IOException("The anchored directory isn't empty.");
        }
        if (unlinkat(parent.Parent, parent.Leaf, AtRemoveDirectory) != 0)
            throw CreatePathException("Couldn't remove the anchored directory", Marshal.GetLastWin32Error());
        Fsync(parent.Parent);
    }

    /// <summary>Set exact permission bits on a known regular file and return its updated identity.</summary>
    public LinuxFileIdentity ChmodFile(string relativePath, LinuxFileIdentity expectedIdentity, int unixMode)
    {
        this.AssertUsable();
        ValidateUnixMode(unixMode);
        using LinuxAnchoredFile file = this.OpenRegularFileForRead(relativePath);
        if (file.Identity != expectedIdentity)
            throw new IOException("The chmod target identity changed before mutation.");
        SetHandleMode(file.Handle, unixMode);
        Fsync(file.Handle);
        LinuxFileIdentity result = GetHandleIdentity(file.Handle, requireSingleLinkRegularFile: true);
        if ((result.UnixMode & 0x1ff) != unixMode || !result.IsSameObject(expectedIdentity))
            throw new IOException("The chmod result failed identity or mode verification.");
        return result;
    }

    /// <summary>Durably flush the anchored root or one of its real subdirectories.</summary>
    public void FsyncDirectory(string? relativePath = null)
    {
        this.AssertUsable();
        using SafeFileHandle directory = relativePath == null ? Duplicate(this.RootHandle) : this.OpenDirectoryPath(relativePath);
        Fsync(directory);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (this.Disposed)
            return;
        this.RootHandle.Dispose();
        this.Disposed = true;
    }

    private ParentAndLeaf OpenParent(string relativePath)
    {
        string[] segments = GetSegments(relativePath);
        SafeFileHandle current = Duplicate(this.RootHandle);
        try
        {
            for (int index = 0; index < segments.Length - 1; index++)
            {
                SafeFileHandle next = OpenDirectoryAt(current, segments[index]);
                current.Dispose();
                current = next;
            }
            return new ParentAndLeaf(current, segments[^1]);
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private SafeFileHandle OpenDirectoryPath(string relativePath)
    {
        string[] segments = GetSegments(relativePath);
        SafeFileHandle current = Duplicate(this.RootHandle);
        try
        {
            foreach (string segment in segments)
            {
                SafeFileHandle next = OpenDirectoryAt(current, segment);
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

    private static SafeFileHandle OpenDirectoryAt(SafeFileHandle parent, string segment)
    {
        SafeFileHandle handle = OpenAt(
            parent,
            segment,
            OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec,
            0,
            "Couldn't traverse an anchored directory segment"
        );
        try
        {
            LinuxFileIdentity identity = GetHandleIdentity(handle, requireSingleLinkRegularFile: false);
            if (identity.Kind != LinuxAnchoredEntryKind.Directory)
                throw new IOException("An anchored parent segment isn't a directory.");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenAbsoluteDirectoryNoFollow(string absolutePath)
    {
        if (!Path.IsPathFullyQualified(absolutePath) || absolutePath[0] != '/')
            throw new ArgumentException("An anchored Linux root must resolve to an absolute path.", nameof(absolutePath));

        int rootDescriptor = open("/", OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec, 0);
        if (rootDescriptor < 0)
            throw CreatePathException("Couldn't anchor the filesystem root", Marshal.GetLastWin32Error());
        SafeFileHandle current = new((IntPtr)rootDescriptor, ownsHandle: true);
        try
        {
            foreach (string segment in absolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                SafeFileHandle next = OpenDirectoryAt(current, segment);
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

    private static SafeFileHandle OpenAt(SafeFileHandle parent, string name, int flags, uint mode, string message)
    {
        int descriptor = openat(parent, name, flags, mode);
        if (descriptor < 0)
            throw CreatePathException(message, Marshal.GetLastWin32Error());
        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    private static SafeFileHandle Duplicate(SafeFileHandle handle)
    {
        int descriptor = fcntl(handle, DuplicateCloseOnExec, 0);
        if (descriptor < 0)
            throw new LinuxNativeIOException("Couldn't duplicate an anchored directory descriptor", Marshal.GetLastWin32Error());
        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    private static LinuxFileIdentity? GetIdentityAt(
        SafeFileHandle parent,
        string name,
        bool allowMissing,
        bool requireSingleLinkRegularFile
    )
    {
        int result = statx(parent, name, AtSymlinkNoFollow, StatxBasicStats, out Statx data);
        if (result != 0)
        {
            int error = Marshal.GetLastWin32Error();
            if (allowMissing && error == ErrorNoEntry)
                return null;
            if (error == ErrorNotSupported)
                throw new PlatformNotSupportedException("Linux statx is required for fail-closed anchored filesystem identity checks.");
            throw CreatePathException("Couldn't inspect an anchored entry", error);
        }
        return ConvertIdentity(data, requireSingleLinkRegularFile);
    }

    private static LinuxFileIdentity GetHandleIdentity(SafeFileHandle handle, bool requireSingleLinkRegularFile)
    {
        int result = statx(handle, "", AtEmptyPath | AtSymlinkNoFollow, StatxBasicStats, out Statx data);
        if (result != 0)
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorNotSupported)
                throw new PlatformNotSupportedException("Linux statx is required for fail-closed anchored filesystem identity checks.");
            throw new LinuxNativeIOException("Couldn't inspect an anchored handle", error);
        }
        return ConvertIdentity(data, requireSingleLinkRegularFile);
    }

    private static LinuxFileIdentity ConvertIdentity(Statx data, bool requireSingleLinkRegularFile)
    {
        LinuxAnchoredEntryKind kind = (data.Mode & FileTypeMask) switch
        {
            FileTypeRegular => LinuxAnchoredEntryKind.RegularFile,
            FileTypeDirectory => LinuxAnchoredEntryKind.Directory,
            _ => throw new IOException("An anchored path is a symbolic link or unsupported special file.")
        };
        if (requireSingleLinkRegularFile && kind == LinuxAnchoredEntryKind.RegularFile && data.LinkCount != 1)
            throw new IOException("An anchored regular file has multiple hard links.");
        if (data.Size > long.MaxValue)
            throw new IOException("An anchored file is too large to address safely.");
        return new LinuxFileIdentity(
            kind,
            data.Inode,
            data.DeviceMajor,
            data.DeviceMinor,
            data.LinkCount,
            (long)data.Size,
            data.Mode & 0x1ff,
            data.ModificationTime.Seconds,
            data.ModificationTime.Nanoseconds,
            data.ChangeTime.Seconds,
            data.ChangeTime.Nanoseconds
        );
    }

    private static string[] GetSegments(string relativePath)
    {
        return NormalizedRelativePath.Parse(relativePath).Value.Split('/');
    }

    private static void RenameAtNoReplace(SafeFileHandle sourceParent, string source, SafeFileHandle destinationParent, string destination)
    {
        try
        {
            if (renameat2(sourceParent, source, destinationParent, destination, RenameNoReplace) == 0)
                return;
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorNotSupported)
                throw new PlatformNotSupportedException("Linux renameat2(RENAME_NOREPLACE) is required for fail-closed anchored renames.");
            throw CreatePathException("Couldn't rename an anchored file without replacement", error);
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new PlatformNotSupportedException("Linux renameat2(RENAME_NOREPLACE) is required for fail-closed anchored renames.", ex);
        }
    }

    private static void RenameAtExchange(SafeFileHandle sourceParent, string source, SafeFileHandle destinationParent, string destination)
    {
        try
        {
            if (renameat2(sourceParent, source, destinationParent, destination, RenameExchange) == 0)
                return;
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorNotSupported)
                throw new PlatformNotSupportedException("Linux renameat2(RENAME_EXCHANGE) is required for fail-closed atomic replacements.");
            throw CreatePathException("Couldn't exchange anchored files for atomic replacement", error);
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new PlatformNotSupportedException("Linux renameat2(RENAME_EXCHANGE) is required for fail-closed atomic replacements.", ex);
        }
    }

    private static void TryRollbackUnexpectedRename(SafeFileHandle sourceParent, string source, SafeFileHandle destinationParent, string destination)
    {
        try
        {
            RenameAtNoReplace(sourceParent, source, destinationParent, destination);
            Fsync(sourceParent);
            Fsync(destinationParent);
        }
        catch
        {
            // The caller receives a fail-closed identity error and must inspect both anchored paths.
        }
    }

    private static void TryRollbackUnexpectedExchange(SafeFileHandle sourceParent, string source, SafeFileHandle destinationParent, string destination)
    {
        try
        {
            RenameAtExchange(sourceParent, source, destinationParent, destination);
            Fsync(sourceParent);
            Fsync(destinationParent);
        }
        catch
        {
            // The caller receives a fail-closed identity error and must inspect both anchored paths.
        }
    }

    private static void SetHandleMode(SafeFileHandle handle, int mode)
    {
        ValidateUnixMode(mode);
        if (fchmod(handle, (uint)mode) != 0)
            throw new LinuxNativeIOException("Couldn't set anchored file permissions", Marshal.GetLastWin32Error());
    }

    private static void ValidateUnixMode(int mode)
    {
        if (mode is < 0 or > 0x1ff)
            throw new ArgumentOutOfRangeException(nameof(mode), "Only ordinary 0000-0777 permission bits are accepted.");
    }

    private static void Fsync(SafeFileHandle handle)
    {
        if (fsync(handle) != 0)
            throw new LinuxNativeIOException("Couldn't durably flush an anchored handle", Marshal.GetLastWin32Error());
    }

    private void AssertUsable()
    {
        if (this.Disposed || this.RootHandle.IsClosed || this.RootHandle.IsInvalid)
            throw new ObjectDisposedException(nameof(LinuxAnchoredFileSystem));
        LinuxFileIdentity current = GetHandleIdentity(this.RootHandle, requireSingleLinkRegularFile: false);
        if (current.Kind != LinuxAnchoredEntryKind.Directory || !current.IsSameObject(this.RootIdentity))
            throw new IOException("The anchored root identity changed unexpectedly.");
    }

    private static Exception CreatePathException(string message, int error)
    {
        return error switch
        {
            ErrorNoEntry => new FileNotFoundException($"{message} (errno {error})."),
            ErrorExists => new LinuxNativeIOException($"{message}: the destination already exists", error),
            ErrorNotDirectory or ErrorSymbolicLinkLoop => new LinuxNativeIOException($"{message}: a path segment is a symbolic link or non-directory", error),
            ErrorNotSupported => new PlatformNotSupportedException($"{message}: the required Linux syscall isn't supported."),
            _ => new LinuxNativeIOException(message, error)
        };
    }

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int open(string path, int flags, uint mode);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int openat(SafeFileHandle directory, string path, int flags, uint mode);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int mkdirat(SafeFileHandle directory, string path, uint mode);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int unlinkat(SafeFileHandle directory, string path, int flags);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int renameat2(SafeFileHandle sourceDirectory, string source, SafeFileHandle destinationDirectory, string destination, uint flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(SafeFileHandle descriptor, int command, int argument);

    [DllImport("libc", SetLastError = true)]
    private static extern int fchmod(SafeFileHandle descriptor, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int flock(SafeFileHandle descriptor, int operation);

    [DllImport("libc", SetLastError = true)]
    private static extern int ftruncate(SafeFileHandle descriptor, long length);

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(SafeFileHandle descriptor);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int statx(SafeFileHandle directory, string path, int flags, uint mask, out Statx data);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr fdopendir(int descriptor);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr readdir(IntPtr directory);

    [DllImport("libc", SetLastError = true)]
    private static extern int closedir(IntPtr directory);

    [DllImport("libc")]
    private static extern IntPtr __errno_location();

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
        public uint DeviceIdMajor;
        public uint DeviceIdMinor;
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

    private sealed class ParentAndLeaf : IDisposable
    {
        public SafeFileHandle Parent { get; }
        public string Leaf { get; }

        public ParentAndLeaf(SafeFileHandle parent, string leaf)
        {
            this.Parent = parent;
            this.Leaf = leaf;
        }

        public void Dispose()
        {
            this.Parent.Dispose();
        }
    }
}
