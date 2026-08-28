using System.Text.RegularExpressions;

namespace StardewModdingAPI.Installer.Core.Ownership;

/// <summary>The complete immutable identity bound into a package manifest and installation receipt.</summary>
public sealed class InstallationReleaseIdentity : IEquatable<InstallationReleaseIdentity>
{
    /// <summary>The reviewed release repository.</summary>
    public const string ReviewedRepository = "https://github.com/4eh5xitv6787h645ebv/SMAPI";

    private static readonly Regex TagPattern = new(
        @"\Afork-4eh5xitv6787h645ebv-linux-v(?<version>(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*))-alpha\.(?<alpha>[1-9][0-9]*)\z",
        RegexOptions.CultureInvariant
    );

    private static readonly Regex GitObjectPattern = new(@"\A[0-9a-f]{40}\z", RegexOptions.CultureInvariant);

    /// <summary>The source repository.</summary>
    public string Repository { get; }

    /// <summary>The immutable fork release tag.</summary>
    public string Tag { get; }

    /// <summary>The embedded assembly/package version.</summary>
    public string EmbeddedVersion { get; }

    /// <summary>The exact package asset name.</summary>
    public string PackageAssetName { get; }

    /// <summary>The exact source commit.</summary>
    public string SourceCommit { get; }

    /// <summary>The exact source tree.</summary>
    public string SourceTree { get; }

    /// <summary>The verified package digest.</summary>
    public Sha256Digest PackageSha256 { get; }

    /// <summary>Construct and validate a complete release identity.</summary>
    public InstallationReleaseIdentity(
        string repository,
        string tag,
        string embeddedVersion,
        string packageAssetName,
        string sourceCommit,
        string sourceTree,
        Sha256Digest packageSha256
    )
    {
        ArgumentNullException.ThrowIfNull(packageSha256);
        if (!string.Equals(repository, InstallationReleaseIdentity.ReviewedRepository, StringComparison.Ordinal))
            throw new ArgumentException("The release repository isn't the reviewed SMAPI fork.", nameof(repository));

        if (tag == null)
            throw new ArgumentNullException(nameof(tag));
        Match match = InstallationReleaseIdentity.TagPattern.Match(tag);
        if (!match.Success || !int.TryParse(match.Groups["alpha"].Value, out int alpha))
            throw new ArgumentException("The release tag isn't a canonical Linux fork alpha tag.", nameof(tag));

        string expectedEmbeddedVersion = $"{match.Groups["version"].Value}-unofficial.4eh5xitv6787h645ebv.linux.alpha.{alpha}";
        string expectedPackageName = $"SMAPI-{expectedEmbeddedVersion}-linux-x64-installer.zip";
        if (!string.Equals(embeddedVersion, expectedEmbeddedVersion, StringComparison.Ordinal))
            throw new ArgumentException("The embedded version doesn't match the release tag.", nameof(embeddedVersion));
        if (!string.Equals(packageAssetName, expectedPackageName, StringComparison.Ordinal))
            throw new ArgumentException("The package name doesn't match the release tag.", nameof(packageAssetName));
        if (sourceCommit == null || !InstallationReleaseIdentity.GitObjectPattern.IsMatch(sourceCommit))
            throw new ArgumentException("The source commit must be a full lowercase Git object ID.", nameof(sourceCommit));
        if (sourceTree == null || !InstallationReleaseIdentity.GitObjectPattern.IsMatch(sourceTree))
            throw new ArgumentException("The source tree must be a full lowercase Git object ID.", nameof(sourceTree));

        this.Repository = repository;
        this.Tag = tag;
        this.EmbeddedVersion = embeddedVersion;
        this.PackageAssetName = packageAssetName;
        this.SourceCommit = sourceCommit;
        this.SourceTree = sourceTree;
        this.PackageSha256 = packageSha256;
    }

    /// <inheritdoc />
    public bool Equals(InstallationReleaseIdentity? other)
    {
        return other != null
            && this.Repository == other.Repository
            && this.Tag == other.Tag
            && this.EmbeddedVersion == other.EmbeddedVersion
            && this.PackageAssetName == other.PackageAssetName
            && this.SourceCommit == other.SourceCommit
            && this.SourceTree == other.SourceTree
            && this.PackageSha256 == other.PackageSha256;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is InstallationReleaseIdentity other && this.Equals(other);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine(this.Repository, this.Tag, this.EmbeddedVersion, this.PackageAssetName, this.SourceCommit, this.SourceTree, this.PackageSha256);
    }
}
