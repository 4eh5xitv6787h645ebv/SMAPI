using SMAPI.PerformanceBenchmarks.Framework;
using StardewModdingAPI.Framework.Content;

namespace SMAPI.PerformanceBenchmarks;

/// <summary>Measure the Tiled transform decoder used for every populated TMX cell.</summary>
internal sealed class TmxTileTransformScenario : IPerformanceScenario
{
    private uint[] RawGids = [];

    /// <inheritdoc />
    public string Id => "tmx.tile-transform";

    /// <inheritdoc />
    public string Description => "Decodes every Tiled flip combination through the production TMX conversion helper.";

    /// <inheritdoc />
    public void Setup()
    {
        this.RawGids =
        [
            1u,
            0x80000001u,
            0x40000001u,
            0xC0000001u,
            0x20000001u,
            0xA0000001u,
            0x60000001u,
            0xE0000001u
        ];
    }

    /// <inheritdoc />
    public ulong Execute(int operations)
    {
        ulong digest = ScenarioDigest.Offset;
        for (int index = 0; index < operations; index++)
        {
            DecodedTmxTileTransform transform = OptimizedTmxTileTransform.Decode(this.RawGids[index & 7]);
            digest = ScenarioDigest.Add(digest, transform.Gid);
            digest = ScenarioDigest.Add(digest, unchecked((uint)transform.Rotation));
            digest = ScenarioDigest.Add(digest, (uint)transform.Flip);
        }
        return digest;
    }

    /// <inheritdoc />
    public void Cleanup()
    {
        this.RawGids = [];
    }
}
