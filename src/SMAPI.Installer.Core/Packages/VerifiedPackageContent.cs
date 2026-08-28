using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Packages;

internal interface IVerifiedPackageContentAuthority
{
    PackageManifest Manifest { get; }
    Sha256Digest ManifestSha256 { get; }
    LinuxAnchoredFile OpenFile(PackageManifestEntry expected);
    void AssertUsable();
}

/// <summary>An opaque, descriptor-anchored extraction of every file in a verified install manifest.</summary>
public sealed class VerifiedPackageContent : IDisposable, IAsyncDisposable, IVerifiedPackageContentAuthority
{
    private readonly string StagingRoot;
    private bool Disposed;

    /// <summary>The exact release represented by these bytes.</summary>
    public InstallationReleaseIdentity Release => this.Authority.Release;

    /// <summary>The exact manifest digest represented by these bytes.</summary>
    public Sha256Digest ManifestSha256 => this.Authority.ManifestSha256;

    internal VerifiedInstallerPackage Authority { get; }
    internal LinuxAnchoredFileSystem Payload { get; }
    PackageManifest IVerifiedPackageContentAuthority.Manifest => this.Authority.Manifest;

    internal VerifiedPackageContent(
        VerifiedInstallerPackage authority,
        string stagingRoot,
        LinuxAnchoredFileSystem payload
    )
    {
        this.Authority = authority;
        this.StagingRoot = stagingRoot;
        this.Payload = payload;
    }

    LinuxAnchoredFile IVerifiedPackageContentAuthority.OpenFile(PackageManifestEntry expected)
    {
        if (this.Disposed)
            throw new ObjectDisposedException(nameof(VerifiedPackageContent));
        LinuxAnchoredFile file = this.Payload.OpenRegularFileForRead(expected.Path.Value);
        try
        {
            if (
                file.Identity.Size != expected.SizeBytes
                || file.Identity.UnixMode != expected.UnixMode
                || this.Payload.ComputeSha256(file) != expected.Sha256.Value
            )
            {
                throw new PackageSecurityException("A retained verified payload file changed before use.");
            }
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    void IVerifiedPackageContentAuthority.AssertUsable()
    {
        if (this.Disposed)
            throw new ObjectDisposedException(nameof(VerifiedPackageContent));
        this.Authority.AssertUsable();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (this.Disposed)
            return;
        this.Disposed = true;
        this.Payload.Dispose();
        this.Authority.Dispose();
        PrivatePackageStaging.TryDeleteDirectory(this.StagingRoot);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (this.Disposed)
            return;
        this.Disposed = true;
        this.Payload.Dispose();
        await this.Authority.DisposeAsync().ConfigureAwait(false);
        PrivatePackageStaging.TryDeleteDirectory(this.StagingRoot);
    }
}

/// <summary>Extracts the exact nested Linux payload described by verified companion metadata.</summary>
public sealed class VerifiedPackageContentFactory
{
    /// <summary>
    /// Extract, authenticate, and anchor the package payload. On success ownership of
    /// <paramref name="authority"/> transfers to the returned handle.
    /// </summary>
    public async Task<VerifiedPackageContent> ExtractAsync(
        VerifiedInstallerPackage authority,
        ZipPackageLimits? limits = null,
        CancellationToken cancellationToken = default
    )
    {
        LinuxPrivilegeGuard.AssertNotRoot();
        ArgumentNullException.ThrowIfNull(authority);
        authority.AssertUsable();
        limits ??= ZipPackageLimits.Default;

        string stagingRoot = PrivatePackageStaging.CreateDirectory();
        string outerDestination = Path.Combine(stagingRoot, "outer");
        string payloadDestination = Path.Combine(stagingRoot, "payload");
        LinuxAnchoredFileSystem? payload = null;
        try
        {
            string outerRoot = $"SMAPI {authority.Release.EmbeddedVersion} Linux installer";
            await new BoundedZipPackage().InspectAndExtractAsync(
                authority.Package,
                outerRoot,
                outerDestination,
                limits,
                cancellationToken
            ).ConfigureAwait(false);

            string nestedArchive = Path.Combine(outerDestination, outerRoot, "internal", "linux", "install.dat");
            await ManifestPayloadExtractor.ExtractAsync(
                nestedArchive,
                payloadDestination,
                authority.Manifest,
                limits,
                cancellationToken
            ).ConfigureAwait(false);
            PrivatePackageStaging.TryDeleteDirectory(outerDestination);

            payload = new LinuxAnchoredFileSystem(payloadDestination);
            foreach (PackageManifestEntry entry in authority.Manifest.Entries)
            {
                using LinuxAnchoredFile verified = payload.OpenRegularFileForRead(entry.Path.Value);
                if (
                    verified.Identity.Size != entry.SizeBytes
                    || verified.Identity.UnixMode != entry.UnixMode
                    || payload.ComputeSha256(verified) != entry.Sha256.Value
                )
                {
                    throw new PackageSecurityException("The anchored installer payload doesn't match its verified manifest.");
                }
            }

            VerifiedPackageContent result = new(authority, stagingRoot, payload);
            payload = null;
            stagingRoot = "";
            return result;
        }
        finally
        {
            payload?.Dispose();
            if (stagingRoot.Length > 0)
                PrivatePackageStaging.TryDeleteDirectory(stagingRoot);
        }
    }
}

internal static class ManifestPayloadExtractor
{
    private const int UnixTypeMask = 0xF000;
    private const int UnixRegularFile = 0x8000;
    private const int UnixDirectory = 0x4000;
    private static readonly Regex WindowsDrivePattern = new(@"\A[A-Za-z]:", RegexOptions.CultureInvariant);

    public static async Task ExtractAsync(
        string archivePath,
        string destinationPath,
        PackageManifest manifest,
        ZipPackageLimits limits,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(manifest);
        string fullDestination = Path.GetFullPath(destinationPath);
        if (File.Exists(fullDestination) || Directory.Exists(fullDestination))
            throw new PackageSecurityException("The verified payload destination must not already exist.");

        Dictionary<string, PackageManifestEntry> expected = manifest.Entries.ToDictionary(
            entry => entry.Path.Value == "StardewValley" ? "unix-launcher.sh" : entry.Path.Value,
            StringComparer.Ordinal
        );
        HashSet<string> expectedInsensitive = new(expected.Keys, StringComparer.OrdinalIgnoreCase);
        if (expectedInsensitive.Count != expected.Count)
            throw new PackageSecurityException("The verified manifest has ambiguous source mappings.");

        try
        {
            await using FileStream stream = new(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan
            );
            if (!stream.CanSeek || stream.Length <= 0 || stream.Length > limits.MaxArchiveBytes)
                throw new PackageSecurityException("The nested installer payload has an invalid or excessive size.");
            using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: Encoding.UTF8);
            if (archive.Entries.Count == 0 || archive.Entries.Count > limits.MaxEntries)
                throw new PackageSecurityException("The nested installer payload has an invalid entry count.");

            HashSet<string> observed = new(StringComparer.Ordinal);
            HashSet<string> observedInsensitive = new(StringComparer.OrdinalIgnoreCase);
            long totalExpanded = 0;
            foreach (ZipArchiveEntry archiveEntry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool isDirectory = archiveEntry.FullName.EndsWith("/", StringComparison.Ordinal);
                string sourcePath = GetCanonicalPath(archiveEntry.FullName, limits.MaxDepth);
                AssertOrdinaryEntry(archiveEntry, isDirectory);
                if (!observed.Add(sourcePath) || !observedInsensitive.Add(sourcePath))
                    throw new PackageSecurityException("The nested installer payload contains duplicate or case-colliding paths.");

                totalExpanded = checked(totalExpanded + archiveEntry.Length);
                if (archiveEntry.Length < 0 || archiveEntry.Length > limits.MaxEntryExpandedBytes || totalExpanded > limits.MaxTotalExpandedBytes)
                    throw new PackageSecurityException("The nested installer payload exceeds its expanded-size limits.");
                if (archiveEntry.Length > 0)
                {
                    if (archiveEntry.CompressedLength <= 0 || (double)archiveEntry.Length / archiveEntry.CompressedLength > limits.MaxCompressionRatio)
                        throw new PackageSecurityException("The nested installer payload exceeds its compression-ratio limit.");
                }

                if (isDirectory)
                {
                    if (!expected.Keys.Any(path => path.StartsWith(sourcePath + "/", StringComparison.Ordinal)))
                        throw new PackageSecurityException("The nested installer payload contains an unexpected directory.");
                    continue;
                }

                if (!expected.TryGetValue(sourcePath, out PackageManifestEntry? manifestEntry))
                    throw new PackageSecurityException("The nested installer payload contains a file absent from the verified manifest.");
                if (archiveEntry.Length != manifestEntry.SizeBytes)
                    throw new PackageSecurityException("A nested payload file doesn't match its verified size.");
                int unixMode = (archiveEntry.ExternalAttributes >> 16) & 0x1ff;
                if (unixMode != manifestEntry.UnixMode)
                    throw new PackageSecurityException("A nested payload file doesn't match its verified Unix mode.");

                string targetPath = Path.Combine(fullDestination, manifestEntry.Path.Value.Replace('/', Path.DirectorySeparatorChar));
                string parent = Path.GetDirectoryName(targetPath)!;
                Directory.CreateDirectory(parent);
                for (string? current = parent; current != null && current.StartsWith(fullDestination, StringComparison.Ordinal); current = Path.GetDirectoryName(current))
                    PrivatePackageStaging.SetDirectoryMode(current);

                await using Stream input = archiveEntry.Open();
                await using FileStream output = new(
                    targetPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan
                );
                PrivatePackageStaging.SetFileMode(targetPath);
                using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                long written = 0;
                try
                {
                    while (true)
                    {
                        int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                            break;
                        written = checked(written + read);
                        if (written > manifestEntry.SizeBytes)
                            throw new PackageSecurityException("A nested payload file exceeded its verified size.");
                        hasher.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
                if (written != manifestEntry.SizeBytes || Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant() != manifestEntry.Sha256.Value)
                    throw new PackageSecurityException("A nested payload file doesn't match its verified digest.");
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                PrivatePackageStaging.SetFileMode(targetPath, manifestEntry.UnixMode);
            }

            if (!expected.Keys.All(observed.Contains))
                throw new PackageSecurityException("The nested installer payload is missing a verified manifest file.");
        }
        catch (InvalidDataException ex)
        {
            throw new PackageSecurityException("The nested installer payload isn't a valid ZIP archive.", ex);
        }
        catch
        {
            PrivatePackageStaging.TryDeleteDirectory(fullDestination);
            throw;
        }
    }

    private static string GetCanonicalPath(string rawPath, int maxDepth)
    {
        if (
            string.IsNullOrEmpty(rawPath)
            || rawPath.IndexOf('\0') >= 0
            || rawPath.Contains('\\')
            || rawPath.StartsWith("/", StringComparison.Ordinal)
            || WindowsDrivePattern.IsMatch(rawPath)
            || !rawPath.IsNormalized(NormalizationForm.FormC)
        )
        {
            throw new PackageSecurityException("The nested installer payload contains an unsafe path.");
        }
        string canonical = rawPath.EndsWith("/", StringComparison.Ordinal) ? rawPath[..^1] : rawPath;
        string[] segments = canonical.Split('/');
        if (
            segments.Length == 0
            || segments.Length > maxDepth
            || Encoding.UTF8.GetByteCount(canonical) > 4096
            || segments.Any(segment =>
                segment.Length == 0
                || segment is "." or ".."
                || Encoding.UTF8.GetByteCount(segment) > 255
                || segment.Contains(':')
                || segment.Any(char.IsControl)
                || segment.EndsWith(" ", StringComparison.Ordinal)
                || segment.EndsWith(".", StringComparison.Ordinal)
            )
        )
        {
            throw new PackageSecurityException("The nested installer payload contains an ambiguous path.");
        }
        return canonical;
    }

    private static void AssertOrdinaryEntry(ZipArchiveEntry entry, bool isDirectory)
    {
        uint attributes = unchecked((uint)entry.ExternalAttributes);
        int unixType = (int)(attributes >> 16) & UnixTypeMask;
        bool dosDirectory = (attributes & 0x10) != 0;
        if (unixType is not 0 and not UnixRegularFile and not UnixDirectory)
            throw new PackageSecurityException("The nested installer payload contains a link or special entry.");
        if (isDirectory && (unixType == UnixRegularFile || entry.Length != 0))
            throw new PackageSecurityException("A nested payload directory has invalid file metadata.");
        if (!isDirectory && (unixType == UnixDirectory || dosDirectory || entry.Name.Length == 0))
            throw new PackageSecurityException("A nested payload file has invalid directory metadata.");
    }
}
