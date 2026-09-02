using System.Text.Json;

namespace StardewModdingAPI.Installer.Core.Packages;

/// <summary>Reads the untrusted source-commit hint needed to bind local release assets for backend verification.</summary>
/// <remarks>
/// This parser does not authenticate metadata or a package. It only extracts one canonical bounded value which the
/// existing direct-child package opener must independently verify against checksums, metadata, and attestation.
/// </remarks>
internal static class ReleaseMetadataIdentityHintParser
{
    public static string ParseSourceCommit(ReadOnlyMemory<byte> document)
    {
        using JsonDocument parsed = ReviewedGitHubJson.Parse(
            document,
            PackageVerificationLimits.Default.MaxMetadataBytes,
            "local build metadata"
        );
        JsonElement source = ReviewedGitHubJson.RequireProperty(parsed.RootElement, "source", "local build metadata");
        ReviewedGitHubJson.RequireObject(source, "local build metadata source");
        string commit = ReviewedGitHubJson.RequireString(source, "commit", "local build metadata source", 40);
        ReviewedGitHubReleaseUris.AssertGitObject(commit, "local metadata source commit");
        return commit;
    }
}
