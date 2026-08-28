namespace StardewModdingAPI.Installer.Core.Packages;

/// <summary>Restricts release downloads to the reviewed GitHub repository and asset redirect hosts.</summary>
public sealed class ReviewedGitHubDownloadPolicy : IDownloadUriPolicy
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
            uri == null
            || !uri.IsAbsoluteUri
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.Port != 443
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)
        )
        {
            throw new PackageSecurityException("The release download URI isn't an approved credential-free HTTPS address.");
        }

        string host = uri.DnsSafeHost;
        string escapedPath = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        bool isReleaseAsset = host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && escapedPath.StartsWith(
                $"{ForkReleaseIdentity.Repository}/releases/download/",
                StringComparison.Ordinal
            );
        string releaseApiPrefix = $"repos/{ForkReleaseIdentity.Repository}/releases";
        bool isReleaseApi = host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
            && (
                escapedPath.Equals(releaseApiPrefix, StringComparison.Ordinal)
                || escapedPath.StartsWith(releaseApiPrefix + "/", StringComparison.Ordinal)
            );
        bool isScopedGitHubUri = isReleaseAsset || isReleaseApi;
        if (isInitial)
        {
            if (!isScopedGitHubUri)
                throw new PackageSecurityException("The release download isn't scoped to the reviewed SMAPI fork repository.");
            return;
        }

        bool isApprovedRedirect = ReviewedGitHubDownloadPolicy.RedirectHosts.Contains(host)
            || isScopedGitHubUri;
        if (!isApprovedRedirect)
            throw new PackageSecurityException("The release download redirected to an unapproved host.");
    }
}
