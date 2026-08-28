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
    private Func<string, object, bool>? Predicate;
    private int Visits;
    private int ValueTotal;
    private ulong KeyDigest;

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
        this.Predicate = this.ObserveEntry;
    }

    /// <inheritdoc />
    public ulong Execute(int operations)
    {
        ulong digest = ScenarioDigest.Offset;
        for (int index = 0; index < operations; index++)
        {
            this.Visits = 0;
            this.ValueTotal = 0;
            this.KeyDigest = ScenarioDigest.Offset;
            int removed = this.Cache!.Remove(this.Predicate!, dispose: false).Count();
            digest = ScenarioDigest.Add(digest, (ulong)removed);
            digest = ScenarioDigest.Add(digest, (ulong)this.Cache.Count);
            digest = ScenarioDigest.Add(digest, (ulong)this.Visits);
            digest = ScenarioDigest.Add(digest, (ulong)this.ValueTotal);
            digest = ScenarioDigest.Add(digest, this.KeyDigest);
        }
        return digest;
    }

    /// <inheritdoc />
    public void Cleanup()
    {
        this.Cache = null;
        this.Predicate = null;
        this.Visits = 0;
        this.ValueTotal = 0;
        this.KeyDigest = 0;
    }

    /// <summary>Observe each production predicate invocation without invalidating the entry.</summary>
    private bool ObserveEntry(string key, object value)
    {
        this.Visits++;
        this.ValueTotal += (int)value;
        this.KeyDigest = ScenarioDigest.Add(this.KeyDigest, key);
        return false;
    }
}
