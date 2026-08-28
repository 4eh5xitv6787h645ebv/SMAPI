using System;
using System.Collections.Generic;
using SMAPI.PerformanceBenchmarks.Framework;
using StardewModdingAPI.Framework.Content;

namespace SMAPI.PerformanceBenchmarks;

/// <summary>Measure allocation-free enumeration of cached content entries.</summary>
internal sealed class ContentCacheEnumerationScenario : IPerformanceScenario
{
    /// <summary>The cache under test.</summary>
    private ContentCache? Cache;

    /// <summary>The mutable backing dictionary, retained for deterministic cleanup.</summary>
    private Dictionary<string, object>? Values;

    /// <inheritdoc />
    public string Id => "content.cache-enumeration";

    /// <inheritdoc />
    public string Description => "Enumerates a warmed content cache without iterator or key-lookup allocations.";

    /// <inheritdoc />
    public void Setup()
    {
        this.Values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < 16; index++)
            this.Values.Add($"Maps/Synthetic/Asset{index:D2}", index + 1);
        this.Cache = new ContentCache(this.Values);
    }

    /// <inheritdoc />
    public ulong Execute(int operations)
    {
        ContentCache cache = this.Cache!;
        ulong digest = ScenarioDigest.Offset;
        for (int index = 0; index < operations; index++)
        {
            int count = 0;
            ulong entriesDigest = 0;
            foreach ((string key, object rawValue) in cache.GetEntries())
            {
                int value = (int)rawValue;
                ulong entryDigest = ScenarioDigest.Add(ScenarioDigest.Offset, key);
                entryDigest = ScenarioDigest.Add(entryDigest, (ulong)value);
                entriesDigest += entryDigest;
                count++;
            }

            digest = ScenarioDigest.Add(digest, entriesDigest);
            digest = ScenarioDigest.Add(digest, (ulong)count);
            digest = ScenarioDigest.Add(digest, (ulong)cache.Count);
        }
        return digest;
    }

    /// <inheritdoc />
    public void Cleanup()
    {
        this.Values?.Clear();
        this.Values = null;
        this.Cache = null;
    }
}
