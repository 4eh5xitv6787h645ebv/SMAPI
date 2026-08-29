using System.Text.RegularExpressions;
using StardewModdingAPI.Installer.Core.Ownership.Persistence;

namespace StardewModdingAPI.Installer.Core.Packages;

/// <summary>The six exact public assets required to open one reviewed Linux tagged release.</summary>
public enum ReviewedReleaseAssetKind
{
    InstallerPackage,
    InstallManifest,
    Checksums,
    BuildMetadata,
    AttestationBundle,
    AttestationBundleChecksum
}

/// <summary>Canonical names, URI builders, and download bounds for the reviewed release assets.</summary>
public static class ReviewedGitHubReleaseUris
{
    /// <summary>The maximum accepted release-catalog response size.</summary>
    public const int MaximumCatalogBytes = 2 * 1024 * 1024;

    /// <summary>The maximum accepted Git-reference or annotated-tag response size.</summary>
    public const int MaximumTagDocumentBytes = 128 * 1024;

    /// <summary>The maximum number of release objects accepted from one catalog response.</summary>
    public const int MaximumCatalogReleases = 20;

    /// <summary>The maximum number of uploaded assets accepted on one release object.</summary>
    public const int MaximumAssetsPerRelease = 16;

    private static readonly Regex GitObjectPattern = new(@"\A[0-9a-f]{40}\z", RegexOptions.CultureInvariant);

    /// <summary>Get the only accepted unauthenticated catalog endpoint.</summary>
    public static Uri GetCatalogUri()
    {
        return new Uri(
            $"https://api.github.com/repos/{ForkReleaseIdentity.Repository}/releases?per_page={MaximumCatalogReleases}&page=1"
        );
    }

    /// <summary>Get the only accepted Git-reference endpoint for a selected release.</summary>
    public static Uri GetTagReferenceUri(ForkReleaseIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new Uri(
            $"https://api.github.com/repos/{ForkReleaseIdentity.Repository}/git/ref/tags/{identity.Tag}"
        );
    }

    /// <summary>Get the canonical object URL returned for the selected Git reference.</summary>
    internal static Uri GetTagReferenceObjectUri(ForkReleaseIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new Uri(
            $"https://api.github.com/repos/{ForkReleaseIdentity.Repository}/git/refs/tags/{identity.Tag}"
        );
    }

    /// <summary>Get the only accepted annotated-tag-object endpoint for a selected reference.</summary>
    public static Uri GetTagObjectUri(string tagObjectSha)
    {
        AssertGitObject(tagObjectSha, "tag object");
        return new Uri(
            $"https://api.github.com/repos/{ForkReleaseIdentity.Repository}/git/tags/{tagObjectSha}"
        );
    }

    /// <summary>Get the canonical API URL which an annotated tag must use for its commit target.</summary>
    internal static Uri GetCommitObjectUri(string commit)
    {
        AssertGitObject(commit, "source commit");
        return new Uri(
            $"https://api.github.com/repos/{ForkReleaseIdentity.Repository}/git/commits/{commit}"
        );
    }

    /// <summary>Get the exact filename for one required public release asset.</summary>
    public static string GetAssetName(ForkReleaseIdentity identity, ReviewedReleaseAssetKind kind)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return kind switch
        {
            ReviewedReleaseAssetKind.InstallerPackage => identity.PackageAssetName,
            ReviewedReleaseAssetKind.InstallManifest => VerifiedInstallerPackageFactory.GetManifestAssetName(identity),
            ReviewedReleaseAssetKind.Checksums => ReleasePackageVerifier.ChecksumAssetName,
            ReviewedReleaseAssetKind.BuildMetadata => ReleasePackageVerifier.BuildMetadataAssetName,
            ReviewedReleaseAssetKind.AttestationBundle => VerifiedGitHubAttestationBundleFactory.GetBundleAssetName(identity),
            ReviewedReleaseAssetKind.AttestationBundleChecksum => VerifiedGitHubAttestationBundleFactory.GetChecksumAssetName(identity),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    /// <summary>Get the exact initial download URI for one required public release asset.</summary>
    public static Uri GetAssetUri(ForkReleaseIdentity identity, ReviewedReleaseAssetKind kind)
    {
        ArgumentNullException.ThrowIfNull(identity);
        string name = GetAssetName(identity, kind);
        return new Uri(
            $"https://github.com/{ForkReleaseIdentity.Repository}/releases/download/{identity.Tag}/{name}"
        );
    }

    /// <summary>Get the maximum accepted byte length for one required public release asset.</summary>
    public static long GetMaximumAssetBytes(ReviewedReleaseAssetKind kind)
    {
        return kind switch
        {
            ReviewedReleaseAssetKind.InstallerPackage => PackageVerificationLimits.Default.MaxPackageBytes,
            ReviewedReleaseAssetKind.InstallManifest => OwnershipPersistenceLimits.Default.MaxDocumentBytes,
            ReviewedReleaseAssetKind.Checksums => PackageVerificationLimits.Default.MaxChecksumBytes,
            ReviewedReleaseAssetKind.BuildMetadata => PackageVerificationLimits.Default.MaxMetadataBytes,
            ReviewedReleaseAssetKind.AttestationBundle => VerifiedGitHubAttestationBundleFactory.MaximumBundleBytes,
            ReviewedReleaseAssetKind.AttestationBundleChecksum => VerifiedGitHubAttestationBundleFactory.MaximumChecksumBytes,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    /// <summary>Get the maximum total bytes for the complete six-asset set before private verification staging.</summary>
    public static long GetMaximumAssetSetBytes()
    {
        return Enum.GetValues<ReviewedReleaseAssetKind>().Sum(GetMaximumAssetBytes);
    }

    internal static void AssertGitObject(string value, string description)
    {
        if (value is null || !GitObjectPattern.IsMatch(value))
            throw new PackageSecurityException($"The reviewed GitHub {description} isn't a full lowercase Git object ID.");
    }
}

/// <summary>Accepts only the exact release-catalog and annotated-tag API requests built by this assembly.</summary>
public sealed class ReviewedGitHubReleaseApiPolicy : IDownloadUriPolicy
{
    /// <inheritdoc />
    public void AssertAllowed(Uri uri, bool isInitial)
    {
        AssertCredentialFreeGitHubApiUri(uri);
        if (!isInitial)
            throw new PackageSecurityException("The reviewed GitHub release API request must not redirect.");

        string absolute = uri.OriginalString;
        if (string.Equals(absolute, ReviewedGitHubReleaseUris.GetCatalogUri().AbsoluteUri, StringComparison.Ordinal))
            return;

        string escapedPath = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        string referencePrefix = $"repos/{ForkReleaseIdentity.Repository}/git/ref/tags/";
        if (escapedPath.StartsWith(referencePrefix, StringComparison.Ordinal))
        {
            if (uri.Query.Length != 0)
                throw new PackageSecurityException("The reviewed Git-reference request contains an unexpected query.");
            string rawTag = escapedPath[referencePrefix.Length..];
            ForkReleaseIdentity identity = ForkReleaseIdentity.Parse(rawTag);
            if (!string.Equals(uri.OriginalString, ReviewedGitHubReleaseUris.GetTagReferenceUri(identity).AbsoluteUri, StringComparison.Ordinal))
                throw new PackageSecurityException("The reviewed Git-reference URI isn't canonically encoded.");
            return;
        }

        string objectPrefix = $"repos/{ForkReleaseIdentity.Repository}/git/tags/";
        if (escapedPath.StartsWith(objectPrefix, StringComparison.Ordinal))
        {
            if (uri.Query.Length != 0)
                throw new PackageSecurityException("The reviewed annotated-tag request contains an unexpected query.");
            string rawObject = escapedPath[objectPrefix.Length..];
            ReviewedGitHubReleaseUris.AssertGitObject(rawObject, "tag object");
            if (!string.Equals(uri.OriginalString, ReviewedGitHubReleaseUris.GetTagObjectUri(rawObject).AbsoluteUri, StringComparison.Ordinal))
                throw new PackageSecurityException("The reviewed annotated-tag URI isn't canonically encoded.");
            return;
        }

        throw new PackageSecurityException("The release API URI isn't one of the exact reviewed SMAPI fork routes.");
    }

    private static void AssertCredentialFreeGitHubApiUri(Uri uri)
    {
        if (
            uri is null
            || !uri.IsAbsoluteUri
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.Port != 443
            || !string.Equals(uri.DnsSafeHost, "api.github.com", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)
        )
        {
            throw new PackageSecurityException("The release API URI isn't an approved credential-free GitHub HTTPS address.");
        }
    }
}

/// <summary>Accepts canonical initial downloads for the six reviewed assets and only GitHub's release-asset redirects.</summary>
public sealed class ReviewedGitHubReleaseAssetPolicy : IDownloadUriPolicy
{
    private static readonly HashSet<string> RedirectHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "release-assets.githubusercontent.com",
        "objects.githubusercontent.com"
    };

    /// <inheritdoc />
    public void AssertAllowed(Uri uri, bool isInitial)
    {
        if (
            uri is null
            || !uri.IsAbsoluteUri
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.Port != 443
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)
        )
        {
            throw new PackageSecurityException("The release asset URI isn't an approved credential-free HTTPS address.");
        }

        if (!isInitial)
        {
            if (!RedirectHosts.Contains(uri.DnsSafeHost))
                throw new PackageSecurityException("The release asset redirected to an unapproved host.");
            return;
        }

        if (!string.Equals(uri.DnsSafeHost, "github.com", StringComparison.OrdinalIgnoreCase) || uri.Query.Length != 0)
            throw new PackageSecurityException("The initial release asset URI isn't an exact reviewed GitHub download.");

        string escapedPath = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        string prefix = $"{ForkReleaseIdentity.Repository}/releases/download/";
        if (!escapedPath.StartsWith(prefix, StringComparison.Ordinal))
            throw new PackageSecurityException("The initial release asset URI isn't scoped to the reviewed SMAPI fork.");

        string remainder = escapedPath[prefix.Length..];
        int separator = remainder.IndexOf('/');
        if (separator <= 0 || separator == remainder.Length - 1 || remainder.IndexOf('/', separator + 1) >= 0)
            throw new PackageSecurityException("The initial release asset URI doesn't have one exact tag and filename.");

        string rawTag = remainder[..separator];
        string rawName = remainder[(separator + 1)..];
        ForkReleaseIdentity identity = ForkReleaseIdentity.Parse(rawTag);
        ReviewedReleaseAssetKind[] matches = Enum.GetValues<ReviewedReleaseAssetKind>()
            .Where(kind => string.Equals(GetCanonicalEscapedName(identity, kind), rawName, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
            throw new PackageSecurityException("The initial release asset filename isn't one of the six exact reviewed assets.");
        if (!string.Equals(uri.OriginalString, ReviewedGitHubReleaseUris.GetAssetUri(identity, matches[0]).AbsoluteUri, StringComparison.Ordinal))
            throw new PackageSecurityException("The initial release asset URI isn't canonically encoded.");
    }

    private static string GetCanonicalEscapedName(ForkReleaseIdentity identity, ReviewedReleaseAssetKind kind)
    {
        return ReviewedGitHubReleaseUris.GetAssetUri(identity, kind)
            .GetComponents(UriComponents.Path, UriFormat.UriEscaped)
            .Split('/')[^1];
    }
}
