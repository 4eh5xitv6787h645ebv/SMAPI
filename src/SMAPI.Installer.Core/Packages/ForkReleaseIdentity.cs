using System.Text.RegularExpressions;

namespace StardewModdingAPI.Installer.Core.Packages;

/// <summary>The validated immutable identity of a Linux fork alpha release.</summary>
public sealed record ForkReleaseIdentity
{
    /// <summary>The only repository authorized to publish packages accepted by this installer.</summary>
    public const string Repository = "4eh5xitv6787h645ebv/SMAPI";

    /// <summary>The canonical HTTPS repository URL recorded in build metadata.</summary>
    public const string RepositoryUrl = "https://github.com/4eh5xitv6787h645ebv/SMAPI";

    private const string ForkMarker = "4eh5xitv6787h645ebv";

    private static readonly Regex TagPattern = new(
        @"\Afork-4eh5xitv6787h645ebv-linux-v(?<version>(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*))-alpha\.(?<alpha>[1-9][0-9]*)\z",
        RegexOptions.CultureInvariant
    );

    /// <summary>The release tag.</summary>
    public string Tag { get; }

    /// <summary>The numeric version component.</summary>
    public string Version { get; }

    /// <summary>The positive alpha sequence.</summary>
    public int AlphaSequence { get; }

    /// <summary>The exact embedded fork version.</summary>
    public string EmbeddedVersion { get; }

    /// <summary>The only accepted package asset name.</summary>
    public string PackageAssetName { get; }

    private ForkReleaseIdentity(string tag, string version, int alphaSequence)
    {
        this.Tag = tag;
        this.Version = version;
        this.AlphaSequence = alphaSequence;
        this.EmbeddedVersion = $"{version}-unofficial.{ForkReleaseIdentity.ForkMarker}.linux.alpha.{alphaSequence}";
        this.PackageAssetName = $"SMAPI-{this.EmbeddedVersion}-linux-x64-installer.zip";
    }

    /// <summary>Parse and validate a fork-specific Linux alpha tag.</summary>
    /// <param name="tag">The exact release tag.</param>
    /// <returns>The validated identity.</returns>
    /// <exception cref="PackageSecurityException">The tag isn't an exact fork Linux alpha identity.</exception>
    public static ForkReleaseIdentity Parse(string tag)
    {
        string candidate = tag ?? "";
        if (candidate.Length > 160)
            throw new PackageSecurityException("The selected release tag is too long to be a valid fork identity.");
        Match match = ForkReleaseIdentity.TagPattern.Match(candidate);
        if (!match.Success || !int.TryParse(match.Groups["alpha"].Value, out int alphaSequence))
        {
            throw new PackageSecurityException(
                "The selected release tag isn't a valid SMAPI Linux fork alpha identity."
            );
        }

        return new ForkReleaseIdentity(candidate, match.Groups["version"].Value, alphaSequence);
    }

    /// <summary>Require the supplied embedded version and asset name to match this identity.</summary>
    /// <param name="embeddedVersion">The embedded release version.</param>
    /// <param name="assetName">The package asset filename.</param>
    public void AssertMatches(string embeddedVersion, string assetName)
    {
        if (!string.Equals(embeddedVersion, this.EmbeddedVersion, StringComparison.Ordinal))
            throw new PackageSecurityException("The package version doesn't match its release tag.");
        if (!string.Equals(assetName, this.PackageAssetName, StringComparison.Ordinal))
            throw new PackageSecurityException("The package filename doesn't match its release identity.");
    }
}
