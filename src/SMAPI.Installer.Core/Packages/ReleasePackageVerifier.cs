using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Packages;

/// <summary>One release artifact identity cross-verified between checksums and build metadata.</summary>
internal sealed record VerifiedReleaseArtifactIdentity(string Name, long SizeBytes, string Sha256);

/// <summary>Bounds for cross-artifact release verification.</summary>
public sealed record PackageVerificationLimits
{
    /// <summary>Default verification bounds.</summary>
    public static PackageVerificationLimits Default { get; } = new(
        maxPackageBytes: 512L * 1024 * 1024,
        maxChecksumBytes: 64 * 1024,
        maxMetadataBytes: 256 * 1024
    );

    /// <summary>The maximum installer ZIP size.</summary>
    public long MaxPackageBytes { get; }

    /// <summary>The maximum UTF-8 checksum document size.</summary>
    public int MaxChecksumBytes { get; }

    /// <summary>The maximum UTF-8 build metadata document size.</summary>
    public int MaxMetadataBytes { get; }

    /// <summary>Construct an instance.</summary>
    public PackageVerificationLimits(long maxPackageBytes, int maxChecksumBytes, int maxMetadataBytes)
    {
        if (maxPackageBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPackageBytes));
        if (maxChecksumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxChecksumBytes));
        if (maxMetadataBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxMetadataBytes));

        this.MaxPackageBytes = maxPackageBytes;
        this.MaxChecksumBytes = maxChecksumBytes;
        this.MaxMetadataBytes = maxMetadataBytes;
    }
}

/// <summary>
/// A verified release package retained in private staging. Callers can inspect its immutable identity, but only
/// package code can consume the retained read handle.
/// </summary>
public sealed class VerifiedReleasePackage : IDisposable, IAsyncDisposable
{
    private readonly FileStream Stream;
    private readonly string StagingDirectory;
    private readonly string StagingPath;
    private readonly SemaphoreSlim UseLock = new(1, 1);
    private readonly IReadOnlyDictionary<string, VerifiedReleaseArtifactIdentity> Artifacts;
    private bool Disposed;

    /// <summary>The exact fork release identity.</summary>
    public ForkReleaseIdentity Identity { get; }

    /// <summary>The verified package SHA-256.</summary>
    public string Sha256 { get; }

    /// <summary>The verified package byte length.</summary>
    public long SizeBytes { get; }

    /// <summary>The exact reviewed source commit.</summary>
    public string SourceCommit { get; }

    /// <summary>The exact reviewed source tree.</summary>
    public string SourceTree { get; }

    /// <summary>The authoritative ownership identity derived from the cross-verified release artifacts.</summary>
    public InstallationReleaseIdentity InstallationIdentity { get; }

    internal VerifiedReleasePackage(
        ForkReleaseIdentity identity,
        string sha256,
        long sizeBytes,
        string sourceCommit,
        string sourceTree,
        string buildWorkflow,
        string buildConfiguration,
        string runtimeIdentifier,
        IEnumerable<VerifiedReleaseArtifactIdentity> artifacts,
        string stagingDirectory,
        string stagingPath,
        FileStream stream
    )
    {
        this.Identity = identity;
        this.Sha256 = sha256;
        this.SizeBytes = sizeBytes;
        this.SourceCommit = sourceCommit;
        this.SourceTree = sourceTree;
        this.Artifacts = artifacts.ToDictionary(artifact => artifact.Name, StringComparer.Ordinal);
        this.InstallationIdentity = new InstallationReleaseIdentity(
            ForkReleaseIdentity.RepositoryUrl,
            identity.Tag,
            identity.EmbeddedVersion,
            identity.PackageAssetName,
            sourceCommit,
            sourceTree,
            Sha256Digest.Parse(sha256),
            sizeBytes,
            buildWorkflow,
            buildConfiguration,
            runtimeIdentifier
        );
        this.StagingDirectory = stagingDirectory;
        this.StagingPath = stagingPath;
        this.Stream = stream;
    }

    internal VerifiedReleaseArtifactIdentity GetArtifact(string exactName)
    {
        if (this.Disposed)
            throw new ObjectDisposedException(nameof(VerifiedReleasePackage));
        if (!this.Artifacts.TryGetValue(exactName, out VerifiedReleaseArtifactIdentity? artifact))
            throw new PackageSecurityException("The verified release metadata doesn't contain the required companion artifact.");
        return artifact;
    }

    internal async Task<T> UseVerifiedStreamAsync<T>(
        Func<Stream, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(action);
        await this.UseLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.Disposed)
                throw new ObjectDisposedException(nameof(VerifiedReleasePackage));
            if (!this.Stream.CanRead || !this.Stream.CanSeek || this.Stream.Length != this.SizeBytes)
                throw new PackageSecurityException("The private verified package handle changed before use.");

            this.Stream.Position = 0;
            using SHA256 hasher = SHA256.Create();
            byte[] hash = await hasher.ComputeHashAsync(this.Stream, cancellationToken).ConfigureAwait(false);
            string actualHash = Convert.ToHexString(hash).ToLowerInvariant();
            if (this.Stream.Position != this.SizeBytes || !string.Equals(actualHash, this.Sha256, StringComparison.Ordinal))
                throw new PackageSecurityException("The private staged package no longer matches its verified identity.");

            this.Stream.Position = 0;
            return await action(this.Stream, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!this.Disposed && this.Stream.CanSeek)
                this.Stream.Position = 0;
            this.UseLock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        this.UseLock.Wait();
        try
        {
            if (this.Disposed)
                return;
            this.Disposed = true;
            this.Stream.Dispose();
            PrivatePackageStaging.TryDeleteFile(this.StagingPath);
            PrivatePackageStaging.TryDeleteDirectory(this.StagingDirectory);
        }
        finally
        {
            this.UseLock.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        this.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Verifies that package bytes, SHA256SUMS, metadata, and release identity all agree.</summary>
public sealed class ReleasePackageVerifier
{
    private const int MetadataMaxDepth = 8;

    private static readonly Regex ChecksumLinePattern = new(
        @"\A(?<hash>[0-9a-fA-F]{64}) [ *](?<name>[^\r\n]+)\z",
        RegexOptions.CultureInvariant
    );

    private static readonly Regex CommitPattern = new(@"\A[0-9a-f]{40}\z", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Pattern = new(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant);

    /// <summary>Verify a release into private staging and retain the exact verified read handle.</summary>
    public async Task<VerifiedReleasePackage> VerifyAsync(
        string packagePath,
        string checksumDocument,
        string metadataDocument,
        ForkReleaseIdentity identity,
        string expectedSourceCommit,
        PackageVerificationLimits? limits = null,
        CancellationToken cancellationToken = default
    )
    {
        LinuxPrivilegeGuard.AssertNotRoot();
        if (string.IsNullOrEmpty(packagePath))
            throw new ArgumentException("The package path is required.", nameof(packagePath));
        ArgumentNullException.ThrowIfNull(checksumDocument);
        ArgumentNullException.ThrowIfNull(metadataDocument);
        ArgumentNullException.ThrowIfNull(identity);
        if (expectedSourceCommit == null || !ReleasePackageVerifier.CommitPattern.IsMatch(expectedSourceCommit))
            throw new ArgumentException("The expected source commit must be a full lowercase Git commit.", nameof(expectedSourceCommit));
        limits ??= PackageVerificationLimits.Default;

        this.AssertTextBound(checksumDocument, limits.MaxChecksumBytes, "checksum document");
        this.AssertTextBound(metadataDocument, limits.MaxMetadataBytes, "build metadata");
        IReadOnlyDictionary<string, string> checksumHashes = this.ParseChecksumDocument(checksumDocument);
        ReleaseBuildMetadata metadata = this.ParseMetadata(metadataDocument, identity.PackageAssetName);
        this.AssertArtifactSetsAgree(checksumHashes, metadata.Artifacts);
        string checksumHash = checksumHashes[identity.PackageAssetName];

        string fullPackagePath = Path.GetFullPath(packagePath);
        if (!string.Equals(Path.GetFileName(fullPackagePath), identity.PackageAssetName, StringComparison.Ordinal))
            throw new PackageSecurityException("The selected installer filename doesn't match its release identity.");

        string? stagingDirectory = null;
        string? stagingPath = null;
        try
        {
            stagingDirectory = PrivatePackageStaging.CreateDirectory();
            stagingPath = Path.Combine(stagingDirectory, "verified-package.zip");

            long size;
            string packageHash;
            await using (FileStream source = new(
                fullPackagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan
            ))
            {
                size = source.Length;
                if (size <= 0 || size > limits.MaxPackageBytes)
                    throw new PackageSecurityException("The selected installer package has an invalid or excessive size.");

                await using FileStream output = new(
                    stagingPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 128 * 1024,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan
                );
                PrivatePackageStaging.SetFileMode(stagingPath);

                using SHA256 hasher = SHA256.Create();
                byte[] buffer = new byte[128 * 1024];
                long copied = 0;
                while (true)
                {
                    int read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    copied = checked(copied + read);
                    if (copied > limits.MaxPackageBytes || copied > size)
                        throw new PackageSecurityException("The installer package changed or exceeded its size limit while staging.");
                    hasher.TransformBlock(buffer, 0, read, null, 0);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                if (copied != size || source.Length != size)
                    throw new PackageSecurityException("The installer package changed while it was being staged.");
                packageHash = Convert.ToHexString(hasher.Hash!).ToLowerInvariant();
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            this.AssertMetadata(metadata, identity, size, expectedSourceCommit);
            if (!string.Equals(packageHash, checksumHash, StringComparison.OrdinalIgnoreCase))
                throw new PackageSecurityException("The installer package doesn't match SHA256SUMS.");
            if (!string.Equals(packageHash, metadata.Artifact.Sha256, StringComparison.Ordinal))
                throw new PackageSecurityException("The installer package doesn't match build-metadata.json.");

            FileStream verifiedStream = new(
                stagingPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan
            );
            try
            {
                if (verifiedStream.Length != size)
                    throw new PackageSecurityException("The private staged package changed before its verified handle was retained.");
                VerifiedReleasePackage result = new(
                    identity,
                    packageHash,
                    size,
                    metadata.Source.Commit,
                    metadata.Source.Tree,
                    metadata.Build.Workflow,
                    metadata.Build.Configuration,
                    metadata.Build.RuntimeIdentifier,
                    metadata.Artifacts.Select(artifact => new VerifiedReleaseArtifactIdentity(artifact.Name, artifact.SizeBytes, artifact.Sha256)),
                    stagingDirectory,
                    stagingPath,
                    verifiedStream
                );
                stagingDirectory = null;
                stagingPath = null;
                return result;
            }
            catch
            {
                verifiedStream.Dispose();
                throw;
            }
        }
        catch (FileNotFoundException ex)
        {
            throw new PackageSecurityException("The selected installer package doesn't exist.", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new PackageSecurityException("The selected installer package directory doesn't exist.", ex);
        }
        finally
        {
            if (stagingPath != null)
                PrivatePackageStaging.TryDeleteFile(stagingPath);
            if (stagingDirectory != null)
                PrivatePackageStaging.TryDeleteDirectory(stagingDirectory);
        }
    }

    private void AssertTextBound(string text, int maxBytes, string description)
    {
        if (Encoding.UTF8.GetByteCount(text) > maxBytes)
            throw new PackageSecurityException($"The {description} exceeds its configured size limit.");
    }

    private IReadOnlyDictionary<string, string> ParseChecksumDocument(string document)
    {
        string[] lines = document.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        Dictionary<string, string> entries = new(StringComparer.Ordinal);
        HashSet<string> caseInsensitiveNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in lines)
        {
            if (rawLine.Length == 0)
                continue;

            Match match = ReleasePackageVerifier.ChecksumLinePattern.Match(rawLine);
            if (!match.Success)
                throw new PackageSecurityException("SHA256SUMS contains an invalid entry.");

            string name = match.Groups["name"].Value;
            if (!caseInsensitiveNames.Add(name) || !entries.TryAdd(name, match.Groups["hash"].Value.ToLowerInvariant()))
                throw new PackageSecurityException("SHA256SUMS contains duplicate or case-colliding release assets.");
            if (entries.Count > 32)
                throw new PackageSecurityException("SHA256SUMS contains too many release assets.");
        }

        if (entries.Count == 0)
            throw new PackageSecurityException("SHA256SUMS must contain at least one release asset.");
        return entries;
    }

    private ReleaseBuildMetadata ParseMetadata(string document, string expectedAssetName)
    {
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(
                document,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = ReleasePackageVerifier.MetadataMaxDepth
                }
            );
            JsonElement root = parsed.RootElement;
            HashSet<string> topProperties = AssertObject(
                root,
                "root",
                ["schema_version", "release", "source", "build"],
                ["artifact", "artifacts", "reproducibility"]
            );
            bool hasArtifact = topProperties.Contains("artifact");
            bool hasArtifacts = topProperties.Contains("artifacts");
            if (hasArtifact == hasArtifacts)
                throw new PackageSecurityException("build-metadata.json must contain exactly one of 'artifact' or 'artifacts'.");

            int schemaVersion = RequireInt32(root, "schema_version", "root");
            JsonElement releaseElement = root.GetProperty("release");
            AssertObject(releaseElement, "release", ["version", "tag"], []);
            ReleaseSection release = new(
                RequireString(releaseElement, "version", "release"),
                RequireString(releaseElement, "tag", "release")
            );

            JsonElement sourceElement = root.GetProperty("source");
            AssertObject(sourceElement, "source", ["repository", "commit", "tree"], []);
            SourceSection source = new(
                RequireString(sourceElement, "repository", "source"),
                RequireString(sourceElement, "commit", "source"),
                RequireString(sourceElement, "tree", "source")
            );

            JsonElement buildElement = root.GetProperty("build");
            HashSet<string> buildProperties = AssertObject(
                buildElement,
                "build",
                ["workflow", "configuration", "runtime_identifier"],
                ["run", "runner_image", "runner_arch", "reference_assemblies_commit", "timestamp_utc", "dotnet_info"]
            );
            foreach (string optional in buildProperties.Except(["workflow", "configuration", "runtime_identifier"], StringComparer.Ordinal))
                RequireString(buildElement, optional, "build");
            BuildSection build = new(
                RequireString(buildElement, "workflow", "build"),
                RequireString(buildElement, "configuration", "build"),
                RequireString(buildElement, "runtime_identifier", "build")
            );

            if (topProperties.Contains("reproducibility"))
                RequireString(root, "reproducibility", "root");

            List<ArtifactSection> artifacts = new();
            if (hasArtifact)
                artifacts.Add(ParseArtifact(root.GetProperty("artifact"), "artifact"));
            else
            {
                JsonElement array = root.GetProperty("artifacts");
                if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 32)
                    throw new PackageSecurityException("build-metadata.json 'artifacts' must be a bounded non-empty array.");
                int index = 0;
                foreach (JsonElement artifact in array.EnumerateArray())
                    artifacts.Add(ParseArtifact(artifact, $"artifacts[{index++}]"));
            }

            if (artifacts.Select(artifact => artifact.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != artifacts.Count)
                throw new PackageSecurityException("build-metadata.json contains duplicate or case-colliding artifact names.");
            ArtifactSection[] matching = artifacts
                .Where(artifact => string.Equals(artifact.Name, expectedAssetName, StringComparison.Ordinal))
                .ToArray();
            if (matching.Length != 1)
                throw new PackageSecurityException("build-metadata.json doesn't identify exactly one selected installer package.");

            return new ReleaseBuildMetadata(schemaVersion, release, source, build, matching[0], artifacts);
        }
        catch (PackageSecurityException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new PackageSecurityException("build-metadata.json isn't valid strict bounded JSON.", ex);
        }
    }

    private void AssertMetadata(
        ReleaseBuildMetadata metadata,
        ForkReleaseIdentity identity,
        long packageSize,
        string expectedSourceCommit
    )
    {
        if (metadata.SchemaVersion != 1)
            throw new PackageSecurityException("build-metadata.json has an unsupported schema version.");

        identity.AssertMatches(metadata.Release.Version, metadata.Artifact.Name);
        if (!string.Equals(metadata.Release.Tag, identity.Tag, StringComparison.Ordinal))
            throw new PackageSecurityException("The metadata release tag doesn't match the selected release.");
        if (!string.Equals(metadata.Source.Repository, ForkReleaseIdentity.RepositoryUrl, StringComparison.Ordinal))
            throw new PackageSecurityException("The metadata source repository isn't the reviewed SMAPI fork.");
        if (!ReleasePackageVerifier.CommitPattern.IsMatch(metadata.Source.Commit))
            throw new PackageSecurityException("The metadata source commit isn't a full lowercase Git commit.");
        if (!ReleasePackageVerifier.CommitPattern.IsMatch(metadata.Source.Tree))
            throw new PackageSecurityException("The metadata source tree isn't a full lowercase Git tree.");
        if (!string.Equals(metadata.Source.Commit, expectedSourceCommit, StringComparison.Ordinal))
            throw new PackageSecurityException("The metadata source commit doesn't match the selected release target.");
        if (!string.Equals(metadata.Build.Configuration, "Release", StringComparison.Ordinal))
            throw new PackageSecurityException("The installer package wasn't recorded as a Release build.");
        if (!string.Equals(metadata.Build.RuntimeIdentifier, "linux-x64", StringComparison.Ordinal))
            throw new PackageSecurityException("The installer package wasn't recorded for Linux x86_64.");
        if (!string.Equals(
            metadata.Build.Workflow,
            $"{ForkReleaseIdentity.Repository}/.github/workflows/linux-alpha-release.yml@refs/tags/{identity.Tag}",
            StringComparison.Ordinal
        ))
        {
            throw new PackageSecurityException("The metadata workflow isn't the reviewed Linux release workflow.");
        }
        if (metadata.Artifact.SizeBytes != packageSize)
            throw new PackageSecurityException("The installer package size doesn't match build-metadata.json.");
        if (!ReleasePackageVerifier.Sha256Pattern.IsMatch(metadata.Artifact.Sha256))
            throw new PackageSecurityException("The metadata package SHA-256 isn't canonical lowercase hexadecimal.");
    }

    private void AssertArtifactSetsAgree(
        IReadOnlyDictionary<string, string> checksumHashes,
        IReadOnlyList<ArtifactSection> metadataArtifacts
    )
    {
        if (checksumHashes.Count != metadataArtifacts.Count)
            throw new PackageSecurityException("SHA256SUMS and build-metadata.json don't name the same release artifacts.");

        foreach (ArtifactSection artifact in metadataArtifacts)
        {
            if (
                !checksumHashes.TryGetValue(artifact.Name, out string? checksum)
                || !string.Equals(checksum, artifact.Sha256, StringComparison.Ordinal)
            )
            {
                throw new PackageSecurityException("SHA256SUMS and build-metadata.json disagree about a release artifact.");
            }
        }
    }

    private static HashSet<string> AssertObject(
        JsonElement element,
        string description,
        IReadOnlyCollection<string> required,
        IReadOnlyCollection<string> optional
    )
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new PackageSecurityException($"build-metadata.json '{description}' must be an object.");

        HashSet<string> allowed = new(required.Concat(optional), StringComparer.Ordinal);
        HashSet<string> actual = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!actual.Add(property.Name))
                throw new PackageSecurityException($"build-metadata.json '{description}' contains duplicate property '{property.Name}'.");
            if (!allowed.Contains(property.Name))
                throw new PackageSecurityException($"build-metadata.json '{description}' contains unknown property '{property.Name}'.");
        }

        string? missing = required.FirstOrDefault(property => !actual.Contains(property));
        if (missing != null)
            throw new PackageSecurityException($"build-metadata.json '{description}' is missing required property '{missing}'.");
        return actual;
    }

    private static ArtifactSection ParseArtifact(JsonElement element, string description)
    {
        AssertObject(element, description, ["name", "size_bytes", "sha256"], []);
        string name = RequireString(element, "name", description);
        if (
            name.Length > 255
            || name.Contains('/')
            || name.Contains('\\')
            || name.Contains(':')
            || name.Any(char.IsControl)
            || name.EndsWith(" ", StringComparison.Ordinal)
            || name.EndsWith(".", StringComparison.Ordinal)
        )
        {
            throw new PackageSecurityException($"build-metadata.json '{description}' contains an unsafe artifact name.");
        }
        JsonElement sizeElement = element.GetProperty("size_bytes");
        if (sizeElement.ValueKind != JsonValueKind.Number || !sizeElement.TryGetInt64(out long size) || size <= 0)
            throw new PackageSecurityException($"build-metadata.json '{description}.size_bytes' must be a positive integer.");
        string sha256 = RequireString(element, "sha256", description);
        if (!ReleasePackageVerifier.Sha256Pattern.IsMatch(sha256))
            throw new PackageSecurityException($"build-metadata.json '{description}.sha256' isn't canonical lowercase hexadecimal.");
        return new ArtifactSection(name, size, sha256);
    }

    private static string RequireString(JsonElement parent, string name, string description)
    {
        JsonElement value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String)
            throw new PackageSecurityException($"build-metadata.json '{description}.{name}' must be a string.");
        string text = value.GetString()!;
        if (string.IsNullOrWhiteSpace(text))
            throw new PackageSecurityException($"build-metadata.json '{description}.{name}' can't be empty.");
        return text;
    }

    private static int RequireInt32(JsonElement parent, string name, string description)
    {
        JsonElement value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
            throw new PackageSecurityException($"build-metadata.json '{description}.{name}' must be an integer.");
        return result;
    }

    private sealed record ReleaseBuildMetadata(
        int SchemaVersion,
        ReleaseSection Release,
        SourceSection Source,
        BuildSection Build,
        ArtifactSection Artifact,
        IReadOnlyList<ArtifactSection> Artifacts
    );

    private sealed record ReleaseSection(string Version, string Tag);
    private sealed record SourceSection(string Repository, string Commit, string Tree);
    private sealed record BuildSection(string Workflow, string Configuration, string RuntimeIdentifier);
    private sealed record ArtifactSection(string Name, long SizeBytes, string Sha256);
}

internal static class PrivatePackageStaging
{
    private const int ErrorExists = 17;

    public static string CreateDirectory()
    {
        for (int attempt = 0; attempt < 32; attempt++)
        {
            string path = Path.Combine(Path.GetTempPath(), $"smapi-installer-verified-{Guid.NewGuid():N}");
            if (OperatingSystem.IsLinux())
            {
                if (mkdir(path, Convert.ToUInt32("700", 8)) != 0)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == PrivatePackageStaging.ErrorExists)
                        continue;
                    throw new IOException($"Couldn't create private verified-package staging (errno {error}).");
                }
            }
            else
            {
                if (Directory.Exists(path) || File.Exists(path))
                    continue;
                Directory.CreateDirectory(path);
            }
            SetDirectoryMode(path);
            return path;
        }
        throw new IOException("Couldn't create private verified-package staging.");
    }

    public static void SetDirectoryMode(string path)
    {
        SetMode(path, Convert.ToInt32("700", 8));
    }

    public static void SetFileMode(string path)
    {
        SetMode(path, Convert.ToInt32("600", 8));
    }

    public static void SetFileMode(string path, int unixMode)
    {
        if (unixMode is < 0 or > 511)
            throw new ArgumentOutOfRangeException(nameof(unixMode));
        SetMode(path, unixMode);
    }

    public static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort for private temporary staging only.
        }
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort for private temporary staging only.
        }
    }

    private static void SetMode(string path, int mode)
    {
        if (OperatingSystem.IsLinux() && chmod(path, (uint)mode) != 0)
            throw new IOException("Couldn't set private verified-package staging permissions.");
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int chmod(string path, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int mkdir(string path, uint mode);
}
