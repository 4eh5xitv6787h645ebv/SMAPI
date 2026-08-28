using System;
using System.Collections.Generic;
using System.Linq;
using SMAPI.PerformanceBenchmarks.Framework;
using StardewModdingAPI.Framework.Content;

namespace SMAPI.PerformanceBenchmarks;

/// <summary>Measure a warmed content-invalidation scan with no matching entries.</summary>
internal sealed class ContentInvalidationScenario : IPerformanceScenario
{
    private ContentCache? Cache;

    /// <inheritdoc />
    public string Id => "content.invalidation-scan";

    /// <inheritdoc />
    public string Description => "Scans a synthetic content cache through the production invalidation predicate path.";

    /// <inheritdoc />
    public void Setup()
    {
        Dictionary<string, object> values = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < 32; index++)
            values.Add($"Maps/Synthetic/Asset{index:D2}", index);
        this.Cache = new ContentCache(values);
    }

    /// <inheritdoc />
    public ulong Execute(int operations)
    {
        ulong digest = ScenarioDigest.Offset;
        for (int index = 0; index < operations; index++)
        {
            int removed = this.Cache!.Remove(static (key, value) => false, dispose: false).Count();
            digest = ScenarioDigest.Add(digest, (ulong)removed);
            digest = ScenarioDigest.Add(digest, (ulong)this.Cache.Count);
        }
        return digest;
    }

    /// <inheritdoc />
    public void Cleanup()
    {
        this.Cache = null;
    }
}
