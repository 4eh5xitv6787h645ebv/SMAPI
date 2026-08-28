using System;
using SMAPI.PerformanceBenchmarks.Framework;
using StardewModdingAPI.Toolkit.Utilities;

namespace SMAPI.PerformanceBenchmarks;

/// <summary>Measure already-canonical file and asset path normalization.</summary>
internal sealed class CanonicalPathScenario : IPerformanceScenario
{
    /// <summary>Canonical file paths which production code must return by reference.</summary>
    private string[] FilePaths = Array.Empty<string>();

    /// <summary>Canonical asset paths which production code must return by reference.</summary>
    private string[] AssetNames = Array.Empty<string>();

    /// <inheritdoc />
    public string Id => "path.canonical";

    /// <inheritdoc />
    public string Description => "Normalizes warmed canonical file and asset paths without copying them.";

    /// <inheritdoc />
    public void Setup()
    {
        this.FilePaths =
        [
            "/tmp/smapi/Mods/Example/assets/data.json",
            "/opt/games/Stardew Valley/Content/Maps/Farm.xnb",
            "Mods/Example/config.json",
            "Content/Characters/Dialogue/Abigail.xnb"
        ];
        this.AssetNames =
        [
            "Maps/Custom/Farmhouse",
            "Data/Objects",
            "Characters/Dialogue/Abigail",
            "LooseSprites/Cursors"
        ];
    }

    /// <inheritdoc />
    public ulong Execute(int operations)
    {
        ulong digest = ScenarioDigest.Offset;
        for (int index = 0; index < operations; index++)
        {
            string filePath = this.FilePaths[index & 3];
            string assetName = this.AssetNames[index & 3];
            string normalizedFilePath = PathUtilities.NormalizePath(filePath);
            string normalizedAssetName = PathUtilities.NormalizeAssetName(assetName);

            digest = ScenarioDigest.Add(digest, ReferenceEquals(filePath, normalizedFilePath) ? 1UL : 0UL);
            digest = ScenarioDigest.Add(digest, ReferenceEquals(assetName, normalizedAssetName) ? 1UL : 0UL);
            digest = ScenarioDigest.Add(digest, normalizedFilePath);
            digest = ScenarioDigest.Add(digest, normalizedAssetName);
        }
        return digest;
    }

    /// <inheritdoc />
    public void Cleanup()
    {
        this.FilePaths = Array.Empty<string>();
        this.AssetNames = Array.Empty<string>();
    }
}
