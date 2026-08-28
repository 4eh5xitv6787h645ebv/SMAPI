using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace StardewModdingAPI.Installer.Core.Packages;

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

/// <summary>The immutable values established by package, checksum, and build metadata agreement.</summary>
public sealed record VerifiedReleasePackage(
    ForkReleaseIdentity Identity,
    string PackagePath,
    string Sha256,
    long SizeBytes,
    string SourceCommit,
    string SourceTree
);

/// <summary>Verifies that the package bytes, SHA256SUMS, metadata, and release identity all agree.</summary>
public sealed class ReleasePackageVerifier
{
    private static readonly Regex ChecksumLinePattern = new(
        @"\A(?<hash>[0-9a-fA-F]{64}) [ *](?<name>[^\r\n]+)\z",
        RegexOptions.CultureInvariant
    );

    private static readonly Regex CommitPattern = new(@"\A[0-9a-f]{40}\z", RegexOptions.CultureInvariant);

    /// <summary>Verify the release artifacts without loading the installer package into memory.</summary>
    public async Task<VerifiedReleasePackage> VerifyAsync(
        string packagePath,
        string checksumDocument,
        string metadataDocument,
        ForkReleaseIdentity identity,
        string? expectedSourceCommit = null,
        PackageVerificationLimits? limits = null,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrEmpty(packagePath))
            throw new ArgumentException("The package path is required.", nameof(packagePath));
        ArgumentNullException.ThrowIfNull(checksumDocument);
        ArgumentNullException.ThrowIfNull(metadataDocument);
        ArgumentNullException.ThrowIfNull(identity);
        limits ??= PackageVerificationLimits.Default;

        this.AssertTextBound(checksumDocument, limits.MaxChecksumBytes, "checksum document");
        this.AssertTextBound(metadataDocument, limits.MaxMetadataBytes, "build metadata");

        FileInfo packageFile = new(packagePath);
        if (!packageFile.Exists)
            throw new PackageSecurityException("The selected installer package doesn't exist.");
        if (packageFile.Length <= 0 || packageFile.Length > limits.MaxPackageBytes)
            throw new PackageSecurityException("The selected installer package has an invalid or excessive size.");
        if (!string.Equals(packageFile.Name, identity.PackageAssetName, StringComparison.Ordinal))
            throw new PackageSecurityException("The selected installer filename doesn't match its release identity.");

        string checksumHash = this.ParseChecksumDocument(checksumDocument, identity.PackageAssetName);
        ReleaseBuildMetadata metadata = this.ParseMetadata(metadataDocument);
        this.AssertMetadata(metadata, identity, packageFile.Length, expectedSourceCommit);

        string packageHash;
        await using (FileStream stream = new(
            packageFile.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan
        ))
        {
            if (stream.Length != packageFile.Length)
                throw new PackageSecurityException("The installer package changed before verification began.");
            using SHA256 hasher = SHA256.Create();
            byte[] hash = await hasher.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
            if (stream.Length != packageFile.Length || stream.Position != packageFile.Length)
                throw new PackageSecurityException("The installer package changed while it was being verified.");
            packageHash = Convert.ToHexString(hash).ToLowerInvariant();
        }

        if (!string.Equals(packageHash, checksumHash, StringComparison.OrdinalIgnoreCase))
            throw new PackageSecurityException("The installer package doesn't match SHA256SUMS.");
        if (!string.Equals(packageHash, metadata.Artifact!.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new PackageSecurityException("The installer package doesn't match build-metadata.json.");

        return new VerifiedReleasePackage(
            identity,
            packageFile.FullName,
            packageHash,
            packageFile.Length,
            metadata.Source!.Commit!,
            metadata.Source.Tree!
        );
    }

    private void AssertTextBound(string text, int maxBytes, string description)
    {
        if (Encoding.UTF8.GetByteCount(text) > maxBytes)
            throw new PackageSecurityException($"The {description} exceeds its configured size limit.");
    }

    private string ParseChecksumDocument(string document, string expectedAssetName)
    {
        string[] lines = document.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string? expectedHash = null;
        int entryCount = 0;
        foreach (string rawLine in lines)
        {
            if (rawLine.Length == 0)
                continue;

            Match match = ReleasePackageVerifier.ChecksumLinePattern.Match(rawLine);
            if (!match.Success)
                throw new PackageSecurityException("SHA256SUMS contains an invalid entry.");

            entryCount++;
            string name = match.Groups["name"].Value;
            if (!string.Equals(name, expectedAssetName, StringComparison.Ordinal))
                throw new PackageSecurityException("SHA256SUMS names an unexpected release asset.");
            if (expectedHash != null)
                throw new PackageSecurityException("SHA256SUMS contains a duplicate installer entry.");
            expectedHash = match.Groups["hash"].Value.ToLowerInvariant();
        }

        if (entryCount != 1 || expectedHash == null)
            throw new PackageSecurityException("SHA256SUMS must contain exactly one installer package entry.");
        return expectedHash;
    }

    private ReleaseBuildMetadata ParseMetadata(string document)
    {
        try
        {
            ReleaseBuildMetadata? metadata = JsonSerializer.Deserialize<ReleaseBuildMetadata>(
                document,
                new JsonSerializerOptions
                {
                    AllowTrailingCommas = false,
                    ReadCommentHandling = JsonCommentHandling.Disallow,
                    PropertyNameCaseInsensitive = false
                }
            );
            return metadata ?? throw new PackageSecurityException("build-metadata.json is empty.");
        }
        catch (PackageSecurityException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new PackageSecurityException("build-metadata.json isn't valid strict JSON.", ex);
        }
    }

    private void AssertMetadata(
        ReleaseBuildMetadata metadata,
        ForkReleaseIdentity identity,
        long packageSize,
        string? expectedSourceCommit
    )
    {
        if (metadata.SchemaVersion != 1)
            throw new PackageSecurityException("build-metadata.json has an unsupported schema version.");
        if (metadata.Release == null || metadata.Source == null || metadata.Build == null || metadata.Artifact == null)
            throw new PackageSecurityException("build-metadata.json is missing required identity sections.");

        identity.AssertMatches(metadata.Release.Version ?? "", metadata.Artifact.Name ?? "");
        if (!string.Equals(metadata.Release.Tag, identity.Tag, StringComparison.Ordinal))
            throw new PackageSecurityException("The metadata release tag doesn't match the selected release.");
        if (!string.Equals(metadata.Source.Repository, ForkReleaseIdentity.RepositoryUrl, StringComparison.Ordinal))
            throw new PackageSecurityException("The metadata source repository isn't the reviewed SMAPI fork.");
        if (!ReleasePackageVerifier.CommitPattern.IsMatch(metadata.Source.Commit ?? ""))
            throw new PackageSecurityException("The metadata source commit isn't a full lowercase Git commit.");
        if (!ReleasePackageVerifier.CommitPattern.IsMatch(metadata.Source.Tree ?? ""))
            throw new PackageSecurityException("The metadata source tree isn't a full lowercase Git tree.");
        if (
            expectedSourceCommit != null
            && !string.Equals(metadata.Source.Commit, expectedSourceCommit, StringComparison.Ordinal)
        )
        {
            throw new PackageSecurityException("The metadata source commit doesn't match the selected release target.");
        }
        if (!string.Equals(metadata.Build.Configuration, "Release", StringComparison.Ordinal))
            throw new PackageSecurityException("The installer package wasn't recorded as a Release build.");
        if (!string.Equals(metadata.Build.RuntimeIdentifier, "linux-x64", StringComparison.Ordinal))
            throw new PackageSecurityException("The installer package wasn't recorded for Linux x86_64.");
        if (
            metadata.Build.Workflow == null
            || !metadata.Build.Workflow.StartsWith(
                $"{ForkReleaseIdentity.Repository}/.github/workflows/linux-alpha-release.yml@",
                StringComparison.Ordinal
            )
        )
        {
            throw new PackageSecurityException("The metadata workflow isn't the reviewed Linux release workflow.");
        }
        if (metadata.Artifact.SizeBytes != packageSize)
            throw new PackageSecurityException("The installer package size doesn't match build-metadata.json.");
        if (!Regex.IsMatch(metadata.Artifact.Sha256 ?? "", @"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant))
            throw new PackageSecurityException("The metadata package SHA-256 isn't canonical lowercase hexadecimal.");
    }

    private sealed class ReleaseBuildMetadata
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("release")]
        public ReleaseSection? Release { get; set; }

        [JsonPropertyName("source")]
        public SourceSection? Source { get; set; }

        [JsonPropertyName("build")]
        public BuildSection? Build { get; set; }

        [JsonPropertyName("artifact")]
        public ArtifactSection? Artifact { get; set; }
    }

    private sealed class ReleaseSection
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("tag")]
        public string? Tag { get; set; }
    }

    private sealed class SourceSection
    {
        [JsonPropertyName("repository")]
        public string? Repository { get; set; }

        [JsonPropertyName("commit")]
        public string? Commit { get; set; }

        [JsonPropertyName("tree")]
        public string? Tree { get; set; }
    }

    private sealed class BuildSection
    {
        [JsonPropertyName("workflow")]
        public string? Workflow { get; set; }

        [JsonPropertyName("configuration")]
        public string? Configuration { get; set; }

        [JsonPropertyName("runtime_identifier")]
        public string? RuntimeIdentifier { get; set; }
    }

    private sealed class ArtifactSection
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("size_bytes")]
        public long SizeBytes { get; set; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; set; }
    }
}
