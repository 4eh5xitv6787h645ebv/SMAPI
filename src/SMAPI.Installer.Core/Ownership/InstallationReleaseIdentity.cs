using System.Text.RegularExpressions;

namespace StardewModdingAPI.Installer.Core.Ownership;

/// <summary>The complete immutable identity of an exact reviewed tagged release package.</summary>
public sealed class InstallationReleaseIdentity : InstallationPackageIdentity, IEquatable<InstallationReleaseIdentity>
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

    /// <summary>The exact source commit.</summary>
    public string SourceCommit { get; }

    /// <summary>The exact source tree.</summary>
    public string SourceTree { get; }

    /// <summary>The exact GitHub Actions workflow reference which built the package.</summary>
    public string BuildWorkflow { get; }

    /// <summary>The recorded build configuration.</summary>
    public string BuildConfiguration { get; }

    /// <summary>The recorded runtime identifier.</summary>
    public string RuntimeIdentifier { get; }

    /// <summary>Construct and validate a complete release identity.</summary>
    internal InstallationReleaseIdentity(
        string repository,
        string tag,
        string embeddedVersion,
        string packageAssetName,
        string sourceCommit,
        string sourceTree,
        Sha256Digest packageSha256,
        long packageSizeBytes,
        string buildWorkflow,
        string buildConfiguration,
        string runtimeIdentifier
    )
        : base(
            InstallationPackageOrigin.TaggedRelease,
            embeddedVersion,
            packageAssetName,
            packageSha256,
            packageSizeBytes
        )
    {
        if (!string.Equals(repository, InstallationReleaseIdentity.ReviewedRepository, StringComparison.Ordinal))
            throw new ArgumentException("The release repository isn't the reviewed SMAPI fork.", nameof(repository));

        ArgumentNullException.ThrowIfNull(tag);
        if (tag.Length > 160)
            throw new ArgumentException("The release tag isn't a bounded canonical Linux fork alpha tag.", nameof(tag));
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
        string expectedWorkflow = $"4eh5xitv6787h645ebv/SMAPI/.github/workflows/linux-alpha-release.yml@refs/tags/{tag}";
        if (!string.Equals(buildWorkflow, expectedWorkflow, StringComparison.Ordinal))
            throw new ArgumentException("The build workflow doesn't match the exact reviewed release tag.", nameof(buildWorkflow));
        if (!string.Equals(buildConfiguration, "Release", StringComparison.Ordinal))
            throw new ArgumentException("The build configuration must be Release.", nameof(buildConfiguration));
        if (!string.Equals(runtimeIdentifier, "linux-x64", StringComparison.Ordinal))
            throw new ArgumentException("The runtime identifier must be linux-x64.", nameof(runtimeIdentifier));

        this.Repository = repository;
        this.Tag = tag;
        this.SourceCommit = sourceCommit;
        this.SourceTree = sourceTree;
        this.BuildWorkflow = buildWorkflow;
        this.BuildConfiguration = buildConfiguration;
        this.RuntimeIdentifier = runtimeIdentifier;
    }

    /// <inheritdoc />
    public bool Equals(InstallationReleaseIdentity? other)
    {
        return base.Equals(other);
    }

    private protected override bool EqualsOrigin(InstallationPackageIdentity other)
    {
        InstallationReleaseIdentity release = (InstallationReleaseIdentity)other;
        return this.Repository == release.Repository
            && this.Tag == release.Tag
            && this.SourceCommit == release.SourceCommit
            && this.SourceTree == release.SourceTree
            && this.BuildWorkflow == release.BuildWorkflow
            && this.BuildConfiguration == release.BuildConfiguration
            && this.RuntimeIdentifier == release.RuntimeIdentifier;
    }

    private protected override void AddOriginHash(ref HashCode hash)
    {
        hash.Add(this.Repository, StringComparer.Ordinal);
        hash.Add(this.Tag, StringComparer.Ordinal);
        hash.Add(this.SourceCommit, StringComparer.Ordinal);
        hash.Add(this.SourceTree, StringComparer.Ordinal);
        hash.Add(this.BuildWorkflow, StringComparer.Ordinal);
        hash.Add(this.BuildConfiguration, StringComparer.Ordinal);
        hash.Add(this.RuntimeIdentifier, StringComparer.Ordinal);
    }
}
