using System.Buffers;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Packages;

/// <summary>Safety bounds for installer ZIP inspection and extraction.</summary>
public sealed record ZipPackageLimits
{
    /// <summary>Default Linux installer package bounds.</summary>
    public static ZipPackageLimits Default { get; } = new(
        maxArchiveBytes: 512L * 1024 * 1024,
        maxEntries: 20_000,
        maxDepth: 32,
        maxEntryExpandedBytes: 768L * 1024 * 1024,
        maxTotalExpandedBytes: 2L * 1024 * 1024 * 1024,
        maxCompressionRatio: 200
    );

    public long MaxArchiveBytes { get; }
    public int MaxEntries { get; }
    public int MaxDepth { get; }
    public long MaxEntryExpandedBytes { get; }
    public long MaxTotalExpandedBytes { get; }
    public double MaxCompressionRatio { get; }

    /// <summary>Construct an instance.</summary>
    public ZipPackageLimits(
        long maxArchiveBytes,
        int maxEntries,
        int maxDepth,
        long maxEntryExpandedBytes,
        long maxTotalExpandedBytes,
        double maxCompressionRatio
    )
    {
        if (maxArchiveBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxArchiveBytes));
        if (maxEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        if (maxDepth <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDepth));
        if (maxEntryExpandedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntryExpandedBytes));
        if (maxTotalExpandedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxTotalExpandedBytes));
        if (!double.IsFinite(maxCompressionRatio) || maxCompressionRatio < 1)
            throw new ArgumentOutOfRangeException(nameof(maxCompressionRatio));

        this.MaxArchiveBytes = maxArchiveBytes;
        this.MaxEntries = maxEntries;
        this.MaxDepth = maxDepth;
        this.MaxEntryExpandedBytes = maxEntryExpandedBytes;
        this.MaxTotalExpandedBytes = maxTotalExpandedBytes;
        this.MaxCompressionRatio = maxCompressionRatio;
    }
}

/// <summary>A bounded inspection summary.</summary>
public sealed record ZipPackageInspection(int EntryCount, long TotalExpandedBytes, string ExpectedRoot);

/// <summary>Inspects and extracts ZIP packages without accepting ambiguous or special filesystem entries.</summary>
public sealed class BoundedZipPackage
{
    private static readonly Regex WindowsDrivePattern = new(@"\A[A-Za-z]:", RegexOptions.CultureInvariant);

    private const int UnixTypeMask = 0xF000;
    private const int UnixRegularFile = 0x8000;
    private const int UnixDirectory = 0x4000;

    /// <summary>Inspect an archive without extracting it.</summary>
    public ZipPackageInspection Inspect(string archivePath, string expectedRoot, ZipPackageLimits? limits = null)
    {
        LinuxPrivilegeGuard.AssertNotRoot();
        try
        {
            limits ??= ZipPackageLimits.Default;
            using FileStream stream = this.OpenArchive(archivePath, limits);
            using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: false, entryNameEncoding: Encoding.UTF8);
            return this.ValidateEntries(archive, expectedRoot, limits).Inspection;
        }
        catch (InvalidDataException ex)
        {
            throw new PackageSecurityException("The installer package isn't a structurally valid ZIP archive.", ex);
        }
    }

    /// <summary>
    /// Inspect and extract an archive through the same open handle. The destination must not exist and is removed
    /// on every failure or cancellation.
    /// </summary>
    public async Task<ZipPackageInspection> InspectAndExtractAsync(
        string archivePath,
        string expectedRoot,
        string destinationPath,
        ZipPackageLimits? limits = null,
        CancellationToken cancellationToken = default
    )
    {
        LinuxPrivilegeGuard.AssertNotRoot();
        try
        {
            return await this.InspectAndExtractCoreAsync(
                archivePath,
                expectedRoot,
                destinationPath,
                limits,
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            throw new PackageSecurityException("The installer package failed ZIP integrity validation.", ex);
        }
    }

    /// <summary>
    /// Revalidate and extract the exact private handle retained by release verification. Replacing the original
    /// download path after verification can't change the bytes consumed here.
    /// </summary>
    public async Task<ZipPackageInspection> InspectAndExtractAsync(
        VerifiedReleasePackage verifiedPackage,
        string expectedRoot,
        string destinationPath,
        ZipPackageLimits? limits = null,
        CancellationToken cancellationToken = default
    )
    {
        LinuxPrivilegeGuard.AssertNotRoot();
        ArgumentNullException.ThrowIfNull(verifiedPackage);
        try
        {
            return await verifiedPackage.UseVerifiedStreamAsync(
                (stream, token) => this.InspectAndExtractCoreAsync(
                    stream,
                    expectedRoot,
                    destinationPath,
                    limits,
                    token
                ),
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            throw new PackageSecurityException("The verified installer package failed ZIP integrity validation.", ex);
        }
    }

    private async Task<ZipPackageInspection> InspectAndExtractCoreAsync(
        string archivePath,
        string expectedRoot,
        string destinationPath,
        ZipPackageLimits? limits,
        CancellationToken cancellationToken
    )
    {
        limits ??= ZipPackageLimits.Default;
        using FileStream stream = this.OpenArchive(archivePath, limits);
        return await this.InspectAndExtractCoreAsync(
            stream,
            expectedRoot,
            destinationPath,
            limits,
            cancellationToken
        ).ConfigureAwait(false);
    }

    private async Task<ZipPackageInspection> InspectAndExtractCoreAsync(
        Stream stream,
        string expectedRoot,
        string destinationPath,
        ZipPackageLimits? limits,
        CancellationToken cancellationToken
    )
    {
        limits ??= ZipPackageLimits.Default;
        if (!stream.CanRead || !stream.CanSeek || stream.Length <= 0 || stream.Length > limits.MaxArchiveBytes)
            throw new PackageSecurityException("The selected package archive handle has an invalid or excessive size.");
        stream.Position = 0;
        string fullDestinationPath = Path.GetFullPath(destinationPath);
        if (File.Exists(fullDestinationPath) || Directory.Exists(fullDestinationPath))
            throw new PackageSecurityException("The package extraction destination must not already exist.");

        using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: Encoding.UTF8);
        ValidatedArchive validated = this.ValidateEntries(archive, expectedRoot, limits);

        Directory.CreateDirectory(fullDestinationPath);
        PrivatePackageStaging.SetDirectoryMode(fullDestinationPath);
        try
        {
            string destinationPrefix = fullDestinationPath.EndsWith(Path.DirectorySeparatorChar)
                ? fullDestinationPath
                : fullDestinationPath + Path.DirectorySeparatorChar;
            long actualTotalBytes = 0;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                foreach (ValidatedEntry validatedEntry in validated.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string relativePath = validatedEntry.CanonicalPath.Replace('/', Path.DirectorySeparatorChar);
                    string targetPath = Path.GetFullPath(Path.Combine(fullDestinationPath, relativePath));
                    if (!targetPath.StartsWith(destinationPrefix, StringComparison.Ordinal))
                        throw new PackageSecurityException("A package entry escaped the extraction destination.");

                    if (validatedEntry.IsDirectory)
                    {
                        Directory.CreateDirectory(targetPath);
                        PrivatePackageStaging.SetDirectoryMode(targetPath);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    PrivatePackageStaging.SetDirectoryMode(Path.GetDirectoryName(targetPath)!);
                    await using Stream input = validatedEntry.Entry.Open();
                    await using FileStream output = new(
                        targetPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 64 * 1024,
                        options: FileOptions.Asynchronous | FileOptions.SequentialScan
                    );
                    PrivatePackageStaging.SetFileMode(targetPath);

                    long actualEntryBytes = 0;
                    while (true)
                    {
                        int bytesRead = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                        if (bytesRead == 0)
                            break;

                        actualEntryBytes = checked(actualEntryBytes + bytesRead);
                        actualTotalBytes = checked(actualTotalBytes + bytesRead);
                        if (actualEntryBytes > limits.MaxEntryExpandedBytes || actualEntryBytes > validatedEntry.Entry.Length)
                            throw new PackageSecurityException("A package entry exceeded its declared or configured expanded size.");
                        if (actualTotalBytes > limits.MaxTotalExpandedBytes)
                            throw new PackageSecurityException("The package exceeded its configured total expanded size.");
                        await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                    }

                    if (actualEntryBytes != validatedEntry.Entry.Length)
                        throw new PackageSecurityException("A package entry didn't match its declared expanded size.");
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (actualTotalBytes != validated.Inspection.TotalExpandedBytes)
                throw new PackageSecurityException("The extracted package size didn't match its inspected size.");
            return validated.Inspection;
        }
        catch
        {
            BoundedZipPackage.TryDeleteDirectory(fullDestinationPath);
            throw;
        }
    }

    private FileStream OpenArchive(string archivePath, ZipPackageLimits limits)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
            throw new ArgumentException("The archive path is required.", nameof(archivePath));

        FileInfo archiveFile = new(archivePath);
        if (!archiveFile.Exists)
            throw new PackageSecurityException("The selected package archive doesn't exist.");
        if (archiveFile.Length <= 0 || archiveFile.Length > limits.MaxArchiveBytes)
            throw new PackageSecurityException("The selected package archive has an invalid or excessive size.");

        return new FileStream(
            archiveFile.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan
        );
    }

    private ValidatedArchive ValidateEntries(ZipArchive archive, string expectedRoot, ZipPackageLimits limits)
    {
        if (
            string.IsNullOrWhiteSpace(expectedRoot)
            || this.IsUnsafeSegment(expectedRoot)
            || !expectedRoot.IsNormalized(NormalizationForm.FormC)
        )
        {
            throw new ArgumentException("The expected root must be one literal directory name.", nameof(expectedRoot));
        }
        if (archive.Entries.Count == 0)
            throw new PackageSecurityException("The installer package archive is empty.");
        if (archive.Entries.Count > limits.MaxEntries)
            throw new PackageSecurityException("The installer package contains too many entries.");

        HashSet<string> exactPaths = new(StringComparer.Ordinal);
        HashSet<string> caseInsensitivePaths = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> observedPrefixCasing = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> filePaths = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> directoryPaths = new(StringComparer.OrdinalIgnoreCase);
        List<ValidatedEntry> entries = new(archive.Entries.Count);
        long totalExpandedBytes = 0;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string canonicalPath = this.GetCanonicalPath(entry.FullName, expectedRoot, limits.MaxDepth);
            bool isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
            this.AssertOrdinaryEntry(entry, isDirectory);

            if (!exactPaths.Add(canonicalPath))
                throw new PackageSecurityException("The installer package contains a duplicate entry path.");
            if (!caseInsensitivePaths.Add(canonicalPath))
                throw new PackageSecurityException("The installer package contains case-colliding entry paths.");
            this.AssertNoPathTypeOrPrefixCollisions(
                canonicalPath,
                isDirectory,
                observedPrefixCasing,
                filePaths,
                directoryPaths
            );

            if (entry.Length < 0 || entry.CompressedLength < 0 || entry.Length > limits.MaxEntryExpandedBytes)
                throw new PackageSecurityException("An installer package entry has an invalid or excessive expanded size.");
            if (isDirectory && (entry.Length != 0 || entry.CompressedLength != 0))
                throw new PackageSecurityException("An installer package directory contains an unexpected data payload.");
            totalExpandedBytes = checked(totalExpandedBytes + entry.Length);
            if (totalExpandedBytes > limits.MaxTotalExpandedBytes)
                throw new PackageSecurityException("The installer package exceeds its total expanded size limit.");

            if (entry.Length > 0)
            {
                if (entry.CompressedLength == 0)
                    throw new PackageSecurityException("An installer package entry has an impossible compression ratio.");
                double ratio = (double)entry.Length / entry.CompressedLength;
                if (ratio > limits.MaxCompressionRatio)
                    throw new PackageSecurityException("An installer package entry exceeds the compression-ratio limit.");
            }

            entries.Add(new ValidatedEntry(entry, canonicalPath, isDirectory));
        }

        return new ValidatedArchive(
            entries,
            new ZipPackageInspection(entries.Count, totalExpandedBytes, expectedRoot)
        );
    }

    private string GetCanonicalPath(string rawPath, string expectedRoot, int maxDepth)
    {
        if (
            string.IsNullOrEmpty(rawPath)
            || rawPath.IndexOf('\0') >= 0
            || rawPath.Contains('\\')
            || rawPath.StartsWith("/", StringComparison.Ordinal)
            || rawPath.StartsWith("//", StringComparison.Ordinal)
            || BoundedZipPackage.WindowsDrivePattern.IsMatch(rawPath)
            || !rawPath.IsNormalized(NormalizationForm.FormC)
        )
        {
            throw new PackageSecurityException("The installer package contains an unsafe or ambiguous entry path.");
        }

        string canonicalPath = rawPath.EndsWith("/", StringComparison.Ordinal)
            ? rawPath[..^1]
            : rawPath;
        string[] segments = canonicalPath.Split('/');
        if (
            segments.Length == 0
            || segments.Length > maxDepth
            || Encoding.UTF8.GetByteCount(canonicalPath) > 4096
            || segments.Any(this.IsUnsafeSegment)
        )
        {
            throw new PackageSecurityException("The installer package contains a traversing or excessively deep entry path.");
        }
        if (!string.Equals(segments[0], expectedRoot, StringComparison.Ordinal))
            throw new PackageSecurityException("The installer package contains an unexpected top-level directory.");
        if (segments.Length == 1 && !rawPath.EndsWith("/", StringComparison.Ordinal))
            throw new PackageSecurityException("The installer package top-level entry isn't a directory.");

        return canonicalPath;
    }

    private bool IsUnsafeSegment(string segment)
    {
        return segment.Length == 0
            || segment is "." or ".."
            || Encoding.UTF8.GetByteCount(segment) > 255
            || segment.Contains('/')
            || segment.Contains('\\')
            || segment.Contains(':')
            || segment.Any(char.IsControl)
            || segment.EndsWith(" ", StringComparison.Ordinal)
            || segment.EndsWith(".", StringComparison.Ordinal);
    }

    private void AssertNoPathTypeOrPrefixCollisions(
        string canonicalPath,
        bool isDirectory,
        IDictionary<string, string> observedPrefixCasing,
        ISet<string> filePaths,
        ISet<string> directoryPaths
    )
    {
        string[] segments = canonicalPath.Split('/');
        string prefix = "";
        for (int index = 0; index < segments.Length; index++)
        {
            prefix = index == 0 ? segments[index] : $"{prefix}/{segments[index]}";
            if (
                observedPrefixCasing.TryGetValue(prefix, out string? observedPrefix)
                && !string.Equals(prefix, observedPrefix, StringComparison.Ordinal)
            )
            {
                throw new PackageSecurityException("The installer package contains case-colliding path segments.");
            }
            observedPrefixCasing[prefix] = prefix;

            bool isFullPath = index == segments.Length - 1;
            if (!isFullPath)
            {
                if (filePaths.Contains(prefix))
                    throw new PackageSecurityException("An installer package file is also used as a parent directory.");
                directoryPaths.Add(prefix);
            }
        }

        if (isDirectory)
        {
            if (filePaths.Contains(canonicalPath))
                throw new PackageSecurityException("An installer package path is both a file and a directory.");
            directoryPaths.Add(canonicalPath);
        }
        else
        {
            if (directoryPaths.Contains(canonicalPath))
                throw new PackageSecurityException("An installer package path is both a directory and a file.");
            filePaths.Add(canonicalPath);
        }
    }

    private void AssertOrdinaryEntry(ZipArchiveEntry entry, bool isDirectory)
    {
        uint attributes = unchecked((uint)entry.ExternalAttributes);
        int unixType = (int)(attributes >> 16) & BoundedZipPackage.UnixTypeMask;
        bool dosDirectory = (attributes & 0x10) != 0;

        if (unixType is not 0 and not BoundedZipPackage.UnixRegularFile and not BoundedZipPackage.UnixDirectory)
            throw new PackageSecurityException("The installer package contains a link, device, socket, or FIFO entry.");
        if (isDirectory && unixType == BoundedZipPackage.UnixRegularFile)
            throw new PackageSecurityException("A package directory is marked as a regular file.");
        if (!isDirectory && (unixType == BoundedZipPackage.UnixDirectory || dosDirectory || entry.Name.Length == 0))
            throw new PackageSecurityException("A package file is marked as a directory.");
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; the validation or extraction error remains the primary failure.
        }
    }

    private sealed record ValidatedEntry(ZipArchiveEntry Entry, string CanonicalPath, bool IsDirectory);

    private sealed record ValidatedArchive(IReadOnlyList<ValidatedEntry> Entries, ZipPackageInspection Inspection);
}
