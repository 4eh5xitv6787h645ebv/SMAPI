using System.Text;
using System.Text.Json;

namespace StardewModdingAPI.Installer.Core.Packages;

/// <summary>One exact required asset advertised by a compatible reviewed release.</summary>
public sealed record ReviewedReleaseAsset(
    ReviewedReleaseAssetKind Kind,
    string Name,
    long SizeBytes,
    Uri DownloadUri,
    long MaximumBytes
);

/// <summary>A compatible fork release derived only from bounded GitHub catalog fields and local labels.</summary>
public sealed class ReviewedReleaseCandidate
{
    private readonly ReviewedReleaseAsset[] AssetValues;

    /// <summary>The exact fork release identity.</summary>
    public ForkReleaseIdentity Identity { get; }

    /// <summary>A deterministic local label which contains no remote release text.</summary>
    public string DisplayLabel { get; }

    /// <summary>The six exact required release assets in stable kind order.</summary>
    public ReviewedReleaseAsset[] Assets => this.AssetValues.ToArray();

    internal ReviewedReleaseCandidate(ForkReleaseIdentity identity, ReviewedReleaseAsset[] assets)
    {
        this.Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        this.AssetValues = assets?.ToArray() ?? throw new ArgumentNullException(nameof(assets));
        if (this.AssetValues.Length != Enum.GetValues<ReviewedReleaseAssetKind>().Length)
            throw new ArgumentException("A reviewed release candidate requires exactly six assets.", nameof(assets));
        this.DisplayLabel = $"SMAPI {identity.Version} — fork Linux alpha {identity.AlphaSequence} (experimental)";
    }

    /// <summary>Get one exact required asset.</summary>
    public ReviewedReleaseAsset GetAsset(ReviewedReleaseAssetKind kind)
    {
        return this.AssetValues.Single(asset => asset.Kind == kind);
    }
}

/// <summary>Parses the bounded unauthenticated GitHub release page into deterministic compatible choices.</summary>
public static class ReviewedGitHubReleaseCatalog
{
    /// <summary>
    /// Parse one exact catalog response. Invalid fork tags and releases without the complete six-asset contract are
    /// filtered out; malformed, duplicate, or ambiguous compatible release data fails closed.
    /// </summary>
    public static IReadOnlyList<ReviewedReleaseCandidate> Parse(ReadOnlyMemory<byte> document)
    {
        using JsonDocument parsed = ReviewedGitHubJson.Parse(
            document,
            ReviewedGitHubReleaseUris.MaximumCatalogBytes,
            "release catalog"
        );
        JsonElement root = parsed.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
            throw new PackageSecurityException("The GitHub release catalog isn't a JSON array.");
        if (root.GetArrayLength() > ReviewedGitHubReleaseUris.MaximumCatalogReleases)
            throw new PackageSecurityException("The GitHub release catalog contains too many releases.");

        HashSet<string> validTags = new(StringComparer.Ordinal);
        List<ReviewedReleaseCandidate> candidates = [];
        foreach (JsonElement release in root.EnumerateArray())
        {
            ReviewedGitHubJson.RequireObject(release, "release");
            string tag = ReviewedGitHubJson.RequireString(release, "tag_name", "release", 160);
            bool draft = ReviewedGitHubJson.RequireBoolean(release, "draft", "release");
            bool prerelease = ReviewedGitHubJson.RequireBoolean(release, "prerelease", "release");
            JsonElement assetsElement = ReviewedGitHubJson.RequireProperty(release, "assets", "release");
            if (assetsElement.ValueKind != JsonValueKind.Array)
                throw new PackageSecurityException("A GitHub release 'assets' value isn't an array.");
            if (assetsElement.GetArrayLength() > ReviewedGitHubReleaseUris.MaximumAssetsPerRelease)
                throw new PackageSecurityException("A GitHub release contains too many uploaded assets.");

            ForkReleaseIdentity? identity = TryParseIdentity(tag);
            if (identity is null || draft || !prerelease)
                continue;
            if (!validTags.Add(identity.Tag))
                throw new PackageSecurityException("The GitHub release catalog contains a duplicate compatible release tag.");

            List<CatalogAsset> advertised = [];
            HashSet<string> exactNames = new(StringComparer.Ordinal);
            HashSet<string> insensitiveNames = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement asset in assetsElement.EnumerateArray())
            {
                ReviewedGitHubJson.RequireObject(asset, "release asset");
                string name = ReviewedGitHubJson.RequireString(asset, "name", "release asset", 256);
                long size = ReviewedGitHubJson.RequireInt64(asset, "size", "release asset");
                string state = ReviewedGitHubJson.RequireString(asset, "state", "release asset", 32);
                string rawUri = ReviewedGitHubJson.RequireString(
                    asset,
                    "browser_download_url",
                    "release asset",
                    2048
                );
                if (!exactNames.Add(name) || !insensitiveNames.Add(name))
                    throw new PackageSecurityException("A compatible release contains duplicate or case-colliding asset names.");
                if (!Uri.TryCreate(rawUri, UriKind.Absolute, out Uri? uri))
                    throw new PackageSecurityException("A compatible release contains an invalid asset download URI.");
                advertised.Add(new(name, size, state, uri));
            }

            ReviewedReleaseAssetKind[] kinds = Enum.GetValues<ReviewedReleaseAssetKind>();
            if (
                advertised.Count != kinds.Length
                || kinds.Any(kind => !advertised.Any(asset => string.Equals(
                    asset.Name,
                    ReviewedGitHubReleaseUris.GetAssetName(identity, kind),
                    StringComparison.Ordinal
                )))
            )
            {
                continue;
            }

            ReviewedReleaseAsset[] assets = kinds.Select(kind =>
            {
                string expectedName = ReviewedGitHubReleaseUris.GetAssetName(identity, kind);
                CatalogAsset asset = advertised.Single(value => string.Equals(value.Name, expectedName, StringComparison.Ordinal));
                long maximum = ReviewedGitHubReleaseUris.GetMaximumAssetBytes(kind);
                if (!string.Equals(asset.State, "uploaded", StringComparison.Ordinal))
                    throw new PackageSecurityException("A required release asset isn't in GitHub's uploaded state.");
                if (asset.SizeBytes <= 0 || asset.SizeBytes > maximum)
                    throw new PackageSecurityException("A required release asset has an invalid or excessive advertised size.");
                Uri expectedUri = ReviewedGitHubReleaseUris.GetAssetUri(identity, kind);
                if (!string.Equals(asset.DownloadUri.OriginalString, expectedUri.AbsoluteUri, StringComparison.Ordinal))
                    throw new PackageSecurityException("A required release asset has an unexpected repository, tag, filename, or URI encoding.");
                new ReviewedGitHubReleaseAssetPolicy().AssertAllowed(asset.DownloadUri, isInitial: true);
                return new ReviewedReleaseAsset(kind, expectedName, asset.SizeBytes, expectedUri, maximum);
            }).ToArray();
            candidates.Add(new(identity, assets));
        }

        candidates.Sort(ReviewedReleaseCandidateComparer.Instance);
        return candidates.AsReadOnly();
    }

    private static ForkReleaseIdentity? TryParseIdentity(string tag)
    {
        try
        {
            return ForkReleaseIdentity.Parse(tag);
        }
        catch (PackageSecurityException)
        {
            return null;
        }
    }

    private sealed record CatalogAsset(string Name, long SizeBytes, string State, Uri DownloadUri);

    private sealed class ReviewedReleaseCandidateComparer : IComparer<ReviewedReleaseCandidate>
    {
        public static ReviewedReleaseCandidateComparer Instance { get; } = new();

        public int Compare(ReviewedReleaseCandidate? left, ReviewedReleaseCandidate? right)
        {
            if (ReferenceEquals(left, right))
                return 0;
            if (left is null)
                return 1;
            if (right is null)
                return -1;

            string[] leftVersion = left.Identity.Version.Split('.');
            string[] rightVersion = right.Identity.Version.Split('.');
            for (int index = 0; index < 3; index++)
            {
                int numeric = CompareCanonicalNumberDescending(leftVersion[index], rightVersion[index]);
                if (numeric != 0)
                    return numeric;
            }
            int alpha = right.Identity.AlphaSequence.CompareTo(left.Identity.AlphaSequence);
            return alpha != 0 ? alpha : StringComparer.Ordinal.Compare(left.Identity.Tag, right.Identity.Tag);
        }

        private static int CompareCanonicalNumberDescending(string left, string right)
        {
            int length = right.Length.CompareTo(left.Length);
            return length != 0 ? length : StringComparer.Ordinal.Compare(right, left);
        }
    }
}

/// <summary>The exact annotated tag object selected by one reviewed Git reference.</summary>
public sealed record ReviewedGitHubTagReference(string ReleaseTag, string TagObjectSha);

/// <summary>The independently resolved source commit for one reviewed annotated release tag.</summary>
public sealed record ReviewedGitHubResolvedTag(string ReleaseTag, string TagObjectSha, string SourceCommit);

/// <summary>Strictly resolves GitHub's annotated-tag API documents without consulting release build metadata.</summary>
public static class ReviewedGitHubTagResolver
{
    /// <summary>Require an exact tag reference whose target is an annotated tag object, never a lightweight tag.</summary>
    public static ReviewedGitHubTagReference ParseReference(
        ReadOnlyMemory<byte> document,
        ForkReleaseIdentity identity
    )
    {
        ArgumentNullException.ThrowIfNull(identity);
        using JsonDocument parsed = ReviewedGitHubJson.Parse(
            document,
            ReviewedGitHubReleaseUris.MaximumTagDocumentBytes,
            "Git-reference response"
        );
        JsonElement root = parsed.RootElement;
        ReviewedGitHubJson.RequireObject(root, "Git-reference response");
        string reference = ReviewedGitHubJson.RequireString(root, "ref", "Git-reference response", 256);
        string rootUrl = ReviewedGitHubJson.RequireString(root, "url", "Git-reference response", 2048);
        if (!string.Equals(reference, $"refs/tags/{identity.Tag}", StringComparison.Ordinal))
            throw new PackageSecurityException("The Git-reference response names a different release tag.");
        AssertExactUri(rootUrl, ReviewedGitHubReleaseUris.GetTagReferenceObjectUri(identity), "Git-reference response");

        JsonElement target = ReviewedGitHubJson.RequireProperty(root, "object", "Git-reference response");
        ReviewedGitHubJson.RequireObject(target, "Git-reference target");
        string type = ReviewedGitHubJson.RequireString(target, "type", "Git-reference target", 32);
        if (!string.Equals(type, "tag", StringComparison.Ordinal))
            throw new PackageSecurityException("The selected release uses a lightweight or unsupported Git tag.");
        string tagObject = ReviewedGitHubJson.RequireString(target, "sha", "Git-reference target", 40);
        ReviewedGitHubReleaseUris.AssertGitObject(tagObject, "tag object");
        string targetUrl = ReviewedGitHubJson.RequireString(target, "url", "Git-reference target", 2048);
        AssertExactUri(targetUrl, ReviewedGitHubReleaseUris.GetTagObjectUri(tagObject), "Git-reference target");
        return new(identity.Tag, tagObject);
    }

    /// <summary>Require the exact annotated tag object to target one full lowercase source commit.</summary>
    public static ReviewedGitHubResolvedTag ParseAnnotatedTag(
        ReadOnlyMemory<byte> document,
        ForkReleaseIdentity identity,
        ReviewedGitHubTagReference reference
    )
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(reference);
        if (!string.Equals(reference.ReleaseTag, identity.Tag, StringComparison.Ordinal))
            throw new PackageSecurityException("The retained Git-reference selection doesn't match the release tag.");
        ReviewedGitHubReleaseUris.AssertGitObject(reference.TagObjectSha, "tag object");

        using JsonDocument parsed = ReviewedGitHubJson.Parse(
            document,
            ReviewedGitHubReleaseUris.MaximumTagDocumentBytes,
            "annotated-tag response"
        );
        JsonElement root = parsed.RootElement;
        ReviewedGitHubJson.RequireObject(root, "annotated-tag response");
        string tagObject = ReviewedGitHubJson.RequireString(root, "sha", "annotated-tag response", 40);
        string tag = ReviewedGitHubJson.RequireString(root, "tag", "annotated-tag response", 160);
        string rootUrl = ReviewedGitHubJson.RequireString(root, "url", "annotated-tag response", 2048);
        if (!string.Equals(tagObject, reference.TagObjectSha, StringComparison.Ordinal))
            throw new PackageSecurityException("The annotated-tag response doesn't match the selected tag object.");
        if (!string.Equals(tag, identity.Tag, StringComparison.Ordinal))
            throw new PackageSecurityException("The annotated-tag response names a different release tag.");
        AssertExactUri(rootUrl, ReviewedGitHubReleaseUris.GetTagObjectUri(reference.TagObjectSha), "annotated-tag response");

        JsonElement target = ReviewedGitHubJson.RequireProperty(root, "object", "annotated-tag response");
        ReviewedGitHubJson.RequireObject(target, "annotated-tag target");
        string type = ReviewedGitHubJson.RequireString(target, "type", "annotated-tag target", 32);
        if (!string.Equals(type, "commit", StringComparison.Ordinal))
            throw new PackageSecurityException("The annotated release tag doesn't target a Git commit.");
        string commit = ReviewedGitHubJson.RequireString(target, "sha", "annotated-tag target", 40);
        ReviewedGitHubReleaseUris.AssertGitObject(commit, "source commit");
        string targetUrl = ReviewedGitHubJson.RequireString(target, "url", "annotated-tag target", 2048);
        AssertExactUri(targetUrl, ReviewedGitHubReleaseUris.GetCommitObjectUri(commit), "annotated-tag target");
        return new(identity.Tag, reference.TagObjectSha, commit);
    }

    /// <summary>Require a fresh post-download reference observation to select the same exact annotated tag object.</summary>
    public static void AssertReferenceUnchanged(
        ReviewedGitHubTagReference selected,
        ReviewedGitHubTagReference refreshed
    )
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(refreshed);
        ReviewedGitHubReleaseUris.AssertGitObject(selected.TagObjectSha, "selected tag object");
        ReviewedGitHubReleaseUris.AssertGitObject(refreshed.TagObjectSha, "refreshed tag object");
        if (
            !string.Equals(selected.ReleaseTag, refreshed.ReleaseTag, StringComparison.Ordinal)
            || !string.Equals(selected.TagObjectSha, refreshed.TagObjectSha, StringComparison.Ordinal)
        )
        {
            throw new PackageSecurityException("The selected release tag moved while its assets were acquired.");
        }
    }

    private static void AssertExactUri(string raw, Uri expected, string description)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out _) || !string.Equals(raw, expected.AbsoluteUri, StringComparison.Ordinal))
            throw new PackageSecurityException($"The {description} contains an unexpected repository path or URI encoding.");
    }
}

internal static class ReviewedGitHubJson
{
    private const int MaximumDepth = 16;

    public static JsonDocument Parse(ReadOnlyMemory<byte> document, int maximumBytes, string description)
    {
        if (document.Length == 0 || document.Length > maximumBytes)
            throw new PackageSecurityException($"The {description} is empty or exceeds its configured size limit.");
        try
        {
            byte[] snapshot = document.ToArray();
            JsonDocument parsed = JsonDocument.Parse(
                snapshot,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaximumDepth
                }
            );
            AssertNoDuplicateProperties(parsed.RootElement, description);
            return parsed;
        }
        catch (PackageSecurityException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new PackageSecurityException($"The {description} isn't valid strict bounded JSON.", ex);
        }
    }

    public static void RequireObject(JsonElement value, string description)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new PackageSecurityException($"The {description} isn't a JSON object.");
    }

    public static JsonElement RequireProperty(JsonElement value, string name, string description)
    {
        RequireObject(value, description);
        if (!value.TryGetProperty(name, out JsonElement result))
            throw new PackageSecurityException($"The {description} is missing required property '{name}'.");
        return result;
    }

    public static string RequireString(JsonElement value, string name, string description, int maximumCharacters)
    {
        JsonElement property = RequireProperty(value, name, description);
        if (property.ValueKind != JsonValueKind.String)
            throw new PackageSecurityException($"The {description} property '{name}' isn't a string.");
        string result = property.GetString()!;
        if (
            result.Length == 0
            || result.Length > maximumCharacters
            || Encoding.UTF8.GetByteCount(result) > maximumCharacters * 4
            || result.Any(character => char.IsControl(character) || char.IsSurrogate(character))
        )
        {
            throw new PackageSecurityException($"The {description} property '{name}' is empty, excessive, or unsafe.");
        }
        return result;
    }

    public static bool RequireBoolean(JsonElement value, string name, string description)
    {
        JsonElement property = RequireProperty(value, name, description);
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new PackageSecurityException($"The {description} property '{name}' isn't a Boolean.")
        };
    }

    public static long RequireInt64(JsonElement value, string name, string description)
    {
        JsonElement property = RequireProperty(value, name, description);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out long result))
            throw new PackageSecurityException($"The {description} property '{name}' isn't a bounded integer.");
        return result;
    }

    private static void AssertNoDuplicateProperties(JsonElement value, string description)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new PackageSecurityException($"The {description} contains duplicate JSON properties.");
                AssertNoDuplicateProperties(property.Value, description);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
                AssertNoDuplicateProperties(item, description);
        }
    }
}
