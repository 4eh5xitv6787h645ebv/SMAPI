using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Ownership.Persistence;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Packages;

/// <summary>The deterministic schema-3 manifest generated from one finalized Linux installer package.</summary>
public sealed class LinuxInstallManifestBuildResult
{
    private readonly byte[] Bytes;

    /// <summary>The validated manifest model.</summary>
    public PackageManifest Manifest { get; }

    /// <summary>The canonical manifest digest.</summary>
    public Sha256Digest ManifestSha256 { get; }

    internal LinuxInstallManifestBuildResult(PackageManifest manifest, byte[] bytes)
    {
        this.Manifest = manifest;
        this.Bytes = bytes.ToArray();
        this.ManifestSha256 = Sha256Digest.Hash(bytes);
    }

    /// <summary>Get a defensive copy of the canonical UTF-8 bytes.</summary>
    public byte[] GetCanonicalBytes() => this.Bytes.ToArray();
}

/// <summary>The non-authoritative result of structurally inspecting a finalized Linux installer package.</summary>
/// <remarks>
/// This intentionally contains no package digest, release authority, or manifest entries. Passing structural inspection
/// does not qualify a candidate as a release asset.
/// </remarks>
public sealed class LinuxPackageStructuralInspection
{
    /// <summary>The number of ordinary files in the nested installation payload.</summary>
    public int PayloadFileCount { get; }

    /// <summary>The total expanded byte count of the nested installation payload.</summary>
    public long PayloadExpandedBytes { get; }

    internal LinuxPackageStructuralInspection(int payloadFileCount, long payloadExpandedBytes)
    {
        this.PayloadFileCount = payloadFileCount;
        this.PayloadExpandedBytes = payloadExpandedBytes;
    }
}

/// <summary>Applies production package structure checks without creating release authority or artifacts.</summary>
public sealed class LinuxPackageStructuralInspector
{
    /// <summary>Inspect an exact prepared Linux ZIP as a non-authoritative candidate.</summary>
    public async Task<LinuxPackageStructuralInspection> InspectAsync(
        string packagePath,
        ForkReleaseIdentity identity,
        ZipPackageLimits? limits = null,
        CancellationToken cancellationToken = default
    )
    {
        LinuxInstallManifestBuilder.InspectedLinuxPackage package = await LinuxInstallManifestBuilder.InspectPackageAsync(
            packagePath,
            identity,
            limits,
            cancellationToken
        ).ConfigureAwait(false);
        return new LinuxPackageStructuralInspection(package.Entries.Count, package.PayloadExpandedBytes);
    }
}

/// <summary>Builds the release ownership manifest from the exact finalized outer and nested ZIP bytes.</summary>
public sealed class LinuxInstallManifestBuilder
{
    private const int UnixTypeMask = 0xF000;
    private const int UnixRegularFile = 0x8000;
    private const int UnixDirectory = 0x4000;
    private const int UnixSpecialModeMask = 0xE00;
    private const int UnixExecutableModeMask = 0x49;
    private static readonly Regex WindowsDrivePattern = new(@"\A[A-Za-z]:", RegexOptions.CultureInvariant);

    /// <summary>Build a canonical manifest from one finalized Linux installer ZIP.</summary>
    public async Task<LinuxInstallManifestBuildResult> BuildAsync(
        string packagePath,
        ForkReleaseIdentity identity,
        string sourceCommit,
        string sourceTree,
        string buildWorkflow,
        ZipPackageLimits? limits = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(identity);
        InspectedLinuxPackage inspected = await LinuxInstallManifestBuilder.InspectPackageAsync(
            packagePath,
            identity,
            limits,
            cancellationToken
        ).ConfigureAwait(false);
        InstallationReleaseIdentity release = new(
            ForkReleaseIdentity.RepositoryUrl,
            identity.Tag,
            identity.EmbeddedVersion,
            identity.PackageAssetName,
            sourceCommit,
            sourceTree,
            inspected.PackageSha256,
            inspected.PackageSizeBytes,
            buildWorkflow,
            "Release",
            "linux-x64"
        );
        PackageManifest manifest = new(
            release,
            inspected.Entries,
            [GeneratedFileRecipe.CreateCopyGameDepsTemplate()]
        );
        byte[] bytes = Encoding.UTF8.GetBytes(manifest.ToCanonicalJson());
        PackageManifest roundTrip = CanonicalOwnershipDocuments.ParseManifest(bytes);
        if (!roundTrip.Release.Equals(release) || roundTrip.SchemaVersion != PackageManifest.CurrentSchemaVersion)
            throw new PackageSecurityException("The generated install manifest failed its canonical round trip.");
        return new LinuxInstallManifestBuildResult(manifest, bytes);
    }

    internal static async Task<InspectedLinuxPackage> InspectPackageAsync(
        string packagePath,
        ForkReleaseIdentity identity,
        ZipPackageLimits? limits,
        CancellationToken cancellationToken
    )
    {
        LinuxPrivilegeGuard.AssertNotRoot();
        if (string.IsNullOrWhiteSpace(packagePath))
            throw new ArgumentException("The finalized package path is required.", nameof(packagePath));
        ArgumentNullException.ThrowIfNull(identity);
        limits ??= ZipPackageLimits.Default;

        string fullPackagePath = Path.GetFullPath(packagePath);
        if (!string.Equals(Path.GetFileName(fullPackagePath), identity.PackageAssetName, StringComparison.Ordinal))
            throw new PackageSecurityException("The finalized package filename doesn't match its release identity.");
        string stagingRoot = PrivatePackageStaging.CreateDirectory();
        try
        {
            string stagedPackage = Path.Combine(stagingRoot, "package.zip");
            (Sha256Digest packageSha256, long packageSize) = await CopyAndHashPackageAsync(
                fullPackagePath,
                stagedPackage,
                limits.MaxArchiveBytes,
                cancellationToken
            ).ConfigureAwait(false);

            string outerRootName = $"SMAPI {identity.EmbeddedVersion} Linux installer";
            string outerDestination = Path.Combine(stagingRoot, "outer");
            await new BoundedZipPackage().InspectAndExtractAsync(
                stagedPackage,
                outerRootName,
                outerDestination,
                limits,
                cancellationToken
            ).ConfigureAwait(false);

            string nestedArchive = AssertExpectedOuterLayout(Path.Combine(outerDestination, outerRootName));
            InspectedNestedPayload payload = await InspectNestedPayloadAsync(nestedArchive, limits, cancellationToken).ConfigureAwait(false);
            return new InspectedLinuxPackage(packageSha256, packageSize, payload.Entries, payload.ExpandedBytes);
        }
        catch (FileNotFoundException ex)
        {
            throw new PackageSecurityException("The finalized Linux installer package is incomplete.", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new PackageSecurityException("The finalized Linux installer package layout is incomplete.", ex);
        }
        catch (InvalidDataException ex)
        {
            throw new PackageSecurityException("The finalized Linux installer contains an invalid ZIP payload.", ex);
        }
        catch (IOException ex)
        {
            throw new PackageSecurityException("The finalized Linux installer isn't a stable single-link regular package.", ex);
        }
        finally
        {
            PrivatePackageStaging.TryDeleteDirectory(stagingRoot);
        }
    }

    private static async Task<(Sha256Digest Sha256, long Size)> CopyAndHashPackageAsync(
        string sourcePath,
        string destinationPath,
        long maximumBytes,
        CancellationToken cancellationToken
    )
    {
        using LinuxAnchoredFileSystem sourceDirectory = new(Path.GetDirectoryName(sourcePath)!);
        using LinuxAnchoredFile source = sourceDirectory.OpenRegularFileForRead(Path.GetFileName(sourcePath));
        long expectedSize = source.Identity.Size;
        if (expectedSize <= 0 || expectedSize > maximumBytes)
            throw new PackageSecurityException("The finalized package has an invalid or excessive size.");

        await using FileStream destination = new(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        PrivatePackageStaging.SetFileMode(destinationPath);
        string sha256 = await sourceDirectory.CopyAndHashAsync(source, destination, maximumBytes, cancellationToken).ConfigureAwait(false);
        if (destination.Length != expectedSize)
            throw new PackageSecurityException("The finalized package changed while it was staged.");
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
        return (
            Sha256Digest.Parse(sha256),
            expectedSize
        );
    }

    private static string AssertExpectedOuterLayout(string outerRoot)
    {
        Dictionary<string, FileSystemInfo> rootEntries = new DirectoryInfo(outerRoot)
            .EnumerateFileSystemInfos()
            .ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        string[] expectedRootEntries = ["README.txt", "install on Linux.sh", "internal"];
        if (
            rootEntries.Count != expectedRootEntries.Length
            || expectedRootEntries.Any(name => !rootEntries.ContainsKey(name))
            || rootEntries["README.txt"] is not FileInfo
            || rootEntries["install on Linux.sh"] is not FileInfo
            || rootEntries["internal"] is not DirectoryInfo internalDirectory
        )
        {
            throw new PackageSecurityException("The finalized package doesn't have the exact Linux-only outer layout.");
        }

        FileSystemInfo[] internalEntries = internalDirectory.EnumerateFileSystemInfos().ToArray();
        if (internalEntries.Length != 1 || internalEntries[0] is not DirectoryInfo linuxDirectory || internalEntries[0].Name != "linux")
            throw new PackageSecurityException("The finalized package contains an unexpected platform payload.");

        string nestedArchive = Path.Combine(linuxDirectory.FullName, "install.dat");
        string installer = Path.Combine(linuxDirectory.FullName, "SMAPI.Installer");
        FileSystemInfo[] linuxEntries = linuxDirectory.EnumerateFileSystemInfos().ToArray();
        if (
            linuxEntries.Length < 2
            || linuxEntries.Any(entry => entry is not FileInfo)
            || linuxEntries.Count(entry => entry.Name == "install.dat") != 1
            || linuxEntries.Count(entry => entry.Name == "SMAPI.Installer") != 1
            || new FileInfo(nestedArchive).Length <= 0
            || new FileInfo(installer).Length <= 0
        )
        {
            throw new PackageSecurityException("The finalized package is missing its Linux installer or nested payload.");
        }
        return nestedArchive;
    }

    private static async Task<InspectedNestedPayload> InspectNestedPayloadAsync(
        string archivePath,
        ZipPackageLimits limits,
        CancellationToken cancellationToken
    )
    {
        await using FileStream stream = new(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        if (!stream.CanSeek || stream.Length <= 0 || stream.Length > limits.MaxArchiveBytes)
            throw new PackageSecurityException("The nested Linux install payload has an invalid or excessive size.");

        using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: Encoding.UTF8);
        if (archive.Entries.Count == 0 || archive.Entries.Count > limits.MaxEntries)
            throw new PackageSecurityException("The nested Linux install payload has an invalid entry count.");

        HashSet<string> exactPaths = new(StringComparer.Ordinal);
        HashSet<string> caseInsensitivePaths = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> observedPrefixCasing = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
        List<string> explicitDirectories = new();
        List<PackageManifestEntry> manifestEntries = new();
        long totalExpanded = 0;
        long actualTotal = 0;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
            string path = GetCanonicalNestedPath(entry.FullName, limits.MaxDepth);
            AssertOrdinaryNestedEntry(entry, isDirectory);
            if (!exactPaths.Add(path) || !caseInsensitivePaths.Add(path))
                throw new PackageSecurityException("The nested Linux install payload contains duplicate or case-colliding paths.");
            AssertNoPathCollisions(path, isDirectory, observedPrefixCasing, files, directories);
            AssertEntryBounds(entry, isDirectory, limits, ref totalExpanded);

            if (isDirectory)
            {
                explicitDirectories.Add(path);
                continue;
            }
            if (!OwnedNamespacePolicy.TryMapPackageSource(path, out NormalizedRelativePath? destination, out OwnedEntryKind kind))
                throw new PackageSecurityException($"The nested Linux install payload contains unexpected file '{path}'.");

            (Sha256Digest sha256, long actualLength) = await HashEntryAsync(
                entry,
                limits.MaxEntryExpandedBytes,
                cancellationToken
            ).ConfigureAwait(false);
            actualTotal = checked(actualTotal + actualLength);
            if (actualTotal > limits.MaxTotalExpandedBytes || actualLength != entry.Length)
                throw new PackageSecurityException("The nested Linux install payload changed while it was read.");
            int unixMode = (entry.ExternalAttributes >> 16) & 0x1ff;
            if (path == "unix-launcher.sh" && (unixMode & LinuxInstallManifestBuilder.UnixExecutableModeMask) == 0)
                throw new PackageSecurityException("The nested Linux launcher must have at least one executable permission bit.");
            manifestEntries.Add(new PackageManifestEntry(destination!, sha256, actualLength, unixMode, kind));
        }

        if (actualTotal != totalExpanded)
            throw new PackageSecurityException("The nested Linux install payload size didn't match its inspected size.");
        if (explicitDirectories.Any(directory => !exactPaths.Any(path => path.StartsWith(directory + "/", StringComparison.Ordinal))))
            throw new PackageSecurityException("The nested Linux install payload contains an unexpected empty directory.");

        if (manifestEntries.Count(entry => entry.Kind == OwnedEntryKind.Launcher && entry.Path.Value == "StardewValley") != 1)
            throw new PackageSecurityException("The nested Linux install payload must contain exactly one mapped launcher.");
        try
        {
            OwnershipCollectionValidation.AssertDistinctFilePaths(manifestEntries.Select(entry => entry.Path), nameof(manifestEntries));
        }
        catch (ArgumentException ex)
        {
            throw new PackageSecurityException("The nested Linux install payload doesn't map to a canonical owned-file layout.", ex);
        }
        return new InspectedNestedPayload(manifestEntries, actualTotal);
    }

    private static string GetCanonicalNestedPath(string rawPath, int maxDepth)
    {
        if (
            string.IsNullOrEmpty(rawPath)
            || rawPath.IndexOf('\0') >= 0
            || rawPath.Contains('\\')
            || rawPath.StartsWith("/", StringComparison.Ordinal)
            || LinuxInstallManifestBuilder.WindowsDrivePattern.IsMatch(rawPath)
            || !rawPath.IsNormalized(NormalizationForm.FormC)
        )
        {
            throw new PackageSecurityException("The nested Linux install payload contains an unsafe path.");
        }
        string path = rawPath.EndsWith("/", StringComparison.Ordinal) ? rawPath[..^1] : rawPath;
        string[] segments = path.Split('/');
        if (
            segments.Length == 0
            || segments.Length > maxDepth
            || Encoding.UTF8.GetByteCount(path) > NormalizedRelativePath.MaxPathBytes
            || segments.Any(segment =>
                segment.Length == 0
                || segment is "." or ".."
                || Encoding.UTF8.GetByteCount(segment) > NormalizedRelativePath.MaxSegmentBytes
                || segment.Contains(':')
                || segment.Any(char.IsControl)
                || segment.EndsWith(" ", StringComparison.Ordinal)
                || segment.EndsWith(".", StringComparison.Ordinal)
            )
        )
        {
            throw new PackageSecurityException("The nested Linux install payload contains an ambiguous path.");
        }
        return path;
    }

    private static void AssertOrdinaryNestedEntry(ZipArchiveEntry entry, bool isDirectory)
    {
        uint attributes = unchecked((uint)entry.ExternalAttributes);
        int unixType = (int)(attributes >> 16) & LinuxInstallManifestBuilder.UnixTypeMask;
        int unixSpecialMode = (int)(attributes >> 16) & LinuxInstallManifestBuilder.UnixSpecialModeMask;
        bool dosDirectory = (attributes & 0x10) != 0;
        if (unixType is not LinuxInstallManifestBuilder.UnixRegularFile and not LinuxInstallManifestBuilder.UnixDirectory)
            throw new PackageSecurityException("The nested Linux install payload contains a link or special entry.");
        if (unixSpecialMode != 0)
            throw new PackageSecurityException("The nested Linux install payload contains setuid, setgid, or sticky permissions.");
        if (isDirectory && (unixType != LinuxInstallManifestBuilder.UnixDirectory || entry.Length != 0))
            throw new PackageSecurityException("A nested Linux payload directory has invalid metadata.");
        if (!isDirectory && (unixType != LinuxInstallManifestBuilder.UnixRegularFile || dosDirectory || entry.Name.Length == 0))
            throw new PackageSecurityException("A nested Linux payload file has invalid metadata.");
    }

    private static void AssertEntryBounds(
        ZipArchiveEntry entry,
        bool isDirectory,
        ZipPackageLimits limits,
        ref long totalExpanded
    )
    {
        if (entry.Length < 0 || entry.CompressedLength < 0 || entry.Length > limits.MaxEntryExpandedBytes)
            throw new PackageSecurityException("A nested Linux payload entry has an invalid or excessive size.");
        if (isDirectory && (entry.Length != 0 || entry.CompressedLength != 0))
            throw new PackageSecurityException("A nested Linux payload directory contains data.");
        totalExpanded = checked(totalExpanded + entry.Length);
        if (totalExpanded > limits.MaxTotalExpandedBytes)
            throw new PackageSecurityException("The nested Linux install payload exceeds its expanded-size limit.");
        if (entry.Length > 0 && (entry.CompressedLength == 0 || (double)entry.Length / entry.CompressedLength > limits.MaxCompressionRatio))
            throw new PackageSecurityException("A nested Linux payload entry exceeds its compression-ratio limit.");
    }

    private static void AssertNoPathCollisions(
        string path,
        bool isDirectory,
        IDictionary<string, string> observedPrefixCasing,
        ISet<string> filePaths,
        ISet<string> directoryPaths
    )
    {
        string[] segments = path.Split('/');
        string prefix = "";
        for (int index = 0; index < segments.Length; index++)
        {
            prefix = index == 0 ? segments[index] : $"{prefix}/{segments[index]}";
            if (observedPrefixCasing.TryGetValue(prefix, out string? observed) && observed != prefix)
                throw new PackageSecurityException("The nested Linux install payload contains case-colliding path segments.");
            observedPrefixCasing[prefix] = prefix;
            if (index < segments.Length - 1)
            {
                if (filePaths.Contains(prefix))
                    throw new PackageSecurityException("A nested Linux payload file is also a parent directory.");
                directoryPaths.Add(prefix);
            }
        }
        if (isDirectory)
        {
            if (filePaths.Contains(path))
                throw new PackageSecurityException("A nested Linux payload path is both a file and directory.");
            directoryPaths.Add(path);
        }
        else
        {
            if (directoryPaths.Contains(path))
                throw new PackageSecurityException("A nested Linux payload path is both a directory and file.");
            filePaths.Add(path);
        }
    }

    private static async Task<(Sha256Digest Sha256, long Length)> HashEntryAsync(
        ZipArchiveEntry entry,
        long maximumBytes,
        CancellationToken cancellationToken
    )
    {
        await using Stream input = entry.Open();
        using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long length = 0;
        try
        {
            while (true)
            {
                int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                length = checked(length + read);
                if (length > entry.Length || length > maximumBytes)
                    throw new PackageSecurityException("A nested Linux payload file exceeded its declared size.");
                hasher.AppendData(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return (
            Sha256Digest.Parse(Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant()),
            length
        );
    }

    internal sealed record InspectedLinuxPackage(
        Sha256Digest PackageSha256,
        long PackageSizeBytes,
        IReadOnlyList<PackageManifestEntry> Entries,
        long PayloadExpandedBytes
    );

    private sealed record InspectedNestedPayload(IReadOnlyList<PackageManifestEntry> Entries, long ExpandedBytes);
}
