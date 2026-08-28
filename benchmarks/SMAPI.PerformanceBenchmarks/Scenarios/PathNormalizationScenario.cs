using System;
using SMAPI.PerformanceBenchmarks.Framework;
using StardewModdingAPI.Toolkit.Utilities;

namespace SMAPI.PerformanceBenchmarks;

/// <summary>Measure deterministic normalization of noncanonical Linux file paths.</summary>
internal sealed class PathNormalizationScenario : IPerformanceScenario
{
    /// <summary>Paths which require trimming, separator replacement, or separator collapsing.</summary>
    private string[] Paths = Array.Empty<string>();

    /// <inheritdoc />
    public string Id => "path.normalize";

    /// <inheritdoc />
    public string Description => "Normalizes synthetic mixed-separator Linux paths into canonical strings.";

    /// <inheritdoc />
    public void Setup()
    {
        this.Paths =
        [
            "  Mods//Example\\assets///data.json  ",
            "/tmp///Stardew Valley//Mods/",
            "Mods\\Pack\\config.json",
            "///",
            "folder//nested///",
            " already/mostly/canonical "
        ];
    }

    /// <inheritdoc />
    public ulong Execute(int operations)
    {
        ulong digest = ScenarioDigest.Offset;
        for (int index = 0; index < operations; index++)
        {
            string normalized = PathUtilities.NormalizePath(this.Paths[index % this.Paths.Length]);
            digest = ScenarioDigest.Add(digest, normalized);
        }
        return digest;
    }

    /// <inheritdoc />
    public void Cleanup()
    {
        this.Paths = Array.Empty<string>();
    }
}
