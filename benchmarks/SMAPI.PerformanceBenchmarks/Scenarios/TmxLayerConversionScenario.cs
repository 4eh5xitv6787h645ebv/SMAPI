using System;
using System.Collections.Generic;
using SMAPI.PerformanceBenchmarks.Framework;
using StardewModdingAPI.Framework.Content;

namespace SMAPI.PerformanceBenchmarks;

/// <summary>Measure the game-independent production cores used while converting each TMX layer.</summary>
internal sealed class TmxLayerConversionScenario : IPerformanceScenario
{
    private static readonly Func<uint, uint> GetRawGid = static rawGid => rawGid;
    private static readonly DecodedTmxTileConsumer<ConversionState> ConsumeTile = TmxLayerConversionScenario.Consume;

    private uint[] RawGids = [];
    private OptimizedTmxTileIndex<int, int[]>? Index;

    /// <inheritdoc />
    public string Id => "tmx.layer-conversion";

    /// <inheritdoc />
    public string Description => "Traverses a dense synthetic layer and resolves transforms, sheets, and animations through production conversion cores.";

    /// <inheritdoc />
    public void Setup()
    {
        const int tileCount = 64 * 64;
        this.RawGids = new uint[tileCount];
        uint[] transforms =
        [
            0,
            0x80000000u,
            0x40000000u,
            0xC0000000u,
            0x20000000u,
            0xA0000000u,
            0x60000000u,
            0xE0000000u
        ];
        for (int index = 0; index < this.RawGids.Length; index++)
        {
            if (index % 11 != 0)
                this.RawGids[index] = (uint)(index % 128 + 1) | transforms[index & 7];
        }

        Dictionary<int, int[]> secondSheetAnimations = new()
        {
            [2] = [2, 3, 4]
        };
        this.Index = new OptimizedTmxTileIndex<int, int[]>(
        [
            new TmxTileRange<int, int[]>(1, 64, 10, Animations: null),
            new TmxTileRange<int, int[]>(65, 128, 20, secondSheetAnimations)
        ]);
    }

    /// <inheritdoc />
    public ulong Execute(int operations)
    {
        ulong digest = ScenarioDigest.Offset;
        for (int operation = 0; operation < operations; operation++)
        {
            ConversionState state = new(this.Index!);
            OptimizedTmxLayerConversion.ForEachPopulated(
                this.RawGids,
                64,
                0,
                0,
                TmxLayerConversionScenario.GetRawGid,
                ref state,
                TmxLayerConversionScenario.ConsumeTile
            );
            digest = ScenarioDigest.Add(digest, state.Digest);
            digest = ScenarioDigest.Add(digest, (ulong)state.Visited);
        }
        return digest;
    }

    /// <inheritdoc />
    public void Cleanup()
    {
        this.RawGids = [];
        this.Index = null;
    }

    /// <summary>Record every visited cell and resolved tile property.</summary>
    private static void Consume(ref ConversionState state, int x, int y, DecodedTmxTileTransform transform)
    {
        if (!state.Index.TryResolve(transform.Gid, out ResolvedTmxTile<int, int[]> resolved))
            throw new InvalidOperationException($"Synthetic gid {transform.Gid} wasn't indexed.");

        state.Visited++;
        state.Digest = ScenarioDigest.Add(state.Digest, (ulong)x);
        state.Digest = ScenarioDigest.Add(state.Digest, (ulong)y);
        state.Digest = ScenarioDigest.Add(state.Digest, transform.Gid);
        state.Digest = ScenarioDigest.Add(state.Digest, unchecked((uint)transform.Rotation));
        state.Digest = ScenarioDigest.Add(state.Digest, (uint)transform.Flip);
        state.Digest = ScenarioDigest.Add(state.Digest, (ulong)resolved.TileSheet);
        state.Digest = ScenarioDigest.Add(state.Digest, (ulong)resolved.TileIndex);
        state.Digest = ScenarioDigest.Add(state.Digest, resolved.IsAnimated ? 1UL : 0UL);
        if (resolved.Animation is not null)
        {
            foreach (int frame in resolved.Animation)
                state.Digest = ScenarioDigest.Add(state.Digest, (ulong)frame);
        }
    }

    /// <summary>Mutable state passed through one synthetic layer conversion.</summary>
    private struct ConversionState
    {
        public OptimizedTmxTileIndex<int, int[]> Index { get; }
        public ulong Digest;
        public int Visited;

        public ConversionState(OptimizedTmxTileIndex<int, int[]> index)
        {
            this.Index = index;
            this.Digest = ScenarioDigest.Offset;
            this.Visited = 0;
        }
    }
}
