using System.Runtime.InteropServices;
using System.Security.Cryptography;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Transactions;

/// <summary>Strict path and regular-file checks for transaction operations.</summary>
internal static class TransactionPath
{
    private const int AtFdcwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxBasicStats = 0x7ff;
    private const ushort FileTypeMask = 0xf000;
    private const ushort FileTypeDirectory = 0x4000;
    private const ushort FileTypeRegular = 0x8000;

    /// <summary>Get and validate a canonical root.</summary>
    public static string GetCanonicalRoot(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A root path is required.", parameterName);

        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        DirectoryInfo root = new(fullPath);
        if (!root.Exists)
            throw new DirectoryNotFoundException($"The selected root does not exist: {fullPath}");

        if (root.LinkTarget is not null)
        {
            FileSystemInfo? resolved = root.ResolveLinkTarget(returnFinalTarget: true);
            if (resolved is null || !resolved.Exists)
                throw new InstallerTransactionException(TransactionErrorCode.UnsafePath, "The selected root is a broken symbolic link.");
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved.FullName));
        }

        PathEntry entry = Inspect(fullPath);
        if (entry.Kind != PathEntryKind.Directory)
            throw new InstallerTransactionException(TransactionErrorCode.UnsafePath, "The selected root is not a regular directory.");
        return fullPath;
    }

    /// <summary>Normalize a portable relative path and reject aliases.</summary>
    public static string NormalizeRelativePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InstallerTransactionException(TransactionErrorCode.InvalidPlan, $"{parameterName} is empty.");
        if (Path.IsPathRooted(path) || path.StartsWith('/') || path.StartsWith('\\'))
            throw new InstallerTransactionException(TransactionErrorCode.InvalidPlan, $"{parameterName} must be relative.");
        if (path.Contains('\\'))
            throw new InstallerTransactionException(TransactionErrorCode.InvalidPlan, $"{parameterName} must use forward slashes.");

        string[] segments = path.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.IndexOf('\0') >= 0))
            throw new InstallerTransactionException(TransactionErrorCode.InvalidPlan, $"{parameterName} contains an invalid segment.");

        string normalized = string.Join('/', segments);
        if (!string.Equals(normalized, path, StringComparison.Ordinal))
            throw new InstallerTransactionException(TransactionErrorCode.InvalidPlan, $"{parameterName} is not normalized.");
        return normalized;
    }

    /// <summary>Resolve a validated relative path under a canonical root.</summary>
    public static string ResolveUnderRoot(string canonicalRoot, string relativePath)
    {
        string normalized = NormalizeRelativePath(relativePath, "Relative path");
        string resolved = Path.GetFullPath(normalized.Replace('/', Path.DirectorySeparatorChar), canonicalRoot);
        string prefix = canonicalRoot + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.Ordinal))
            throw new InstallerTransactionException(TransactionErrorCode.UnsafePath, "A transaction path escaped its root.");
        return resolved;
    }

    /// <summary>Ensure every existing parent is a real directory immediately before use.</summary>
    public static void AssertSafeParents(string canonicalRoot, string destinationPath)
    {
        string? current = Path.GetDirectoryName(destinationPath);
        Stack<string> parents = new();
        while (current is not null && !string.Equals(current, canonicalRoot, StringComparison.Ordinal))
        {
            parents.Push(current);
            current = Path.GetDirectoryName(current);
        }
        if (current is null)
            throw new InstallerTransactionException(TransactionErrorCode.UnsafePath, "A transaction path escaped its canonical root.");

        while (parents.TryPop(out string? parent))
        {
            PathEntry entry = Inspect(parent);
            if (entry.Kind == PathEntryKind.Missing)
                continue;
            if (entry.Kind != PathEntryKind.Directory)
                throw new InstallerTransactionException(TransactionErrorCode.UnsafePath, "A transaction parent is not a regular directory.");
        }
    }

    /// <summary>Inspect an existing path without following its leaf.</summary>
    public static PathEntry Inspect(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            FileSystemInfo missingProbe = new FileInfo(path);
            if (missingProbe.LinkTarget is null)
                return PathEntry.Missing;
        }

        if (OperatingSystem.IsLinux())
        {
            int result = statx(AtFdcwd, path, AtSymlinkNoFollow, StatxBasicStats, out Statx data);
            if (result != 0)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 2)
                    return PathEntry.Missing;
                throw new LinuxNativeIOException("Couldn't inspect a transaction path", error);
            }

            PathEntryKind kind = (data.Mode & FileTypeMask) switch
            {
                FileTypeRegular => PathEntryKind.RegularFile,
                FileTypeDirectory => PathEntryKind.Directory,
                _ => PathEntryKind.UnsafeSpecial
            };
            return new PathEntry(kind, data.Inode, data.DeviceMajor, data.DeviceMinor, data.LinkCount, data.Mode & 0x1ff);
        }

        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            return new PathEntry(PathEntryKind.UnsafeSpecial, 0, 0, 0, 0, 0);
        return new PathEntry(
            (attributes & FileAttributes.Directory) != 0 ? PathEntryKind.Directory : PathEntryKind.RegularFile,
            0,
            0,
            0,
            1,
            0
        );
    }

    /// <summary>Require a safe single-link regular file.</summary>
    public static PathEntry RequireRegularFile(string path, TransactionErrorCode code, string message)
    {
        PathEntry entry = Inspect(path);
        if (entry.Kind != PathEntryKind.RegularFile || entry.LinkCount != 1)
            throw new InstallerTransactionException(code, message);
        return entry;
    }

    /// <summary>Compute a lowercase SHA-256 for a regular file.</summary>
    public static string ComputeSha256(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        using SHA256 algorithm = SHA256.Create();
        return Convert.ToHexString(algorithm.ComputeHash(stream)).ToLowerInvariant();
    }

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int statx(int directoryFileDescriptor, string path, int flags, uint mask, out Statx buffer);

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
}

internal enum PathEntryKind
{
    Missing,
    RegularFile,
    Directory,
    UnsafeSpecial
}

internal sealed record PathEntry(PathEntryKind Kind, ulong Inode, uint DeviceMajor, uint DeviceMinor, uint LinkCount, int UnixMode)
{
    public static PathEntry Missing { get; } = new(PathEntryKind.Missing, 0, 0, 0, 0, 0);
}
