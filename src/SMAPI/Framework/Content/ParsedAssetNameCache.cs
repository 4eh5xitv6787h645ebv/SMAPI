using System;
using System.Collections.Concurrent;
using System.Threading;
using StardewValley;

namespace StardewModdingAPI.Framework.Content;

/// <summary>A bounded thread-safe cache of parsed immutable asset names.</summary>
internal sealed class ParsedAssetNameCache
{
    /*********
    ** Fields
    *********/
    /// <summary>Parse a locale suffix in an asset name.</summary>
    private readonly Func<string, LocalizedContentManager.LanguageCode?> ParseLocale;

    /// <summary>The maximum number of parsed names to retain.</summary>
    private readonly int Capacity;

    /// <summary>The current cache generation.</summary>
    private CacheState State = new();

    /// <summary>A reusable parser which treats suffixes as part of the base name.</summary>
    private static readonly Func<string, LocalizedContentManager.LanguageCode?> IgnoreLocale = static _ => null;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="parseLocale">Parse a locale suffix in an asset name.</param>
    /// <param name="capacity">The maximum number of parsed names to retain.</param>
    public ParsedAssetNameCache(Func<string, LocalizedContentManager.LanguageCode?> parseLocale, int capacity = 8192)
    {
        this.ParseLocale = parseLocale ?? throw new ArgumentNullException(nameof(parseLocale));
        this.Capacity = capacity > 0
            ? capacity
            : throw new ArgumentOutOfRangeException(nameof(capacity), "The cache capacity must be greater than zero.");
    }

    /// <summary>Parse an asset name, or reuse the cached immutable instance for the exact same input.</summary>
    /// <param name="rawName">The raw asset name to parse.</param>
    /// <param name="allowLocales">Whether to parse locale suffixes.</param>
    public AssetName GetOrAdd(string rawName, bool allowLocales)
    {
        CacheState state = Volatile.Read(ref this.State);
        CacheKey key = new(rawName, allowLocales);
        if (state.Values.TryGetValue(key, out AssetName? cached))
            return cached;

        AssetName parsed = AssetName.Parse(rawName, allowLocales ? this.ParseLocale : ParsedAssetNameCache.IgnoreLocale);
        AssetName result = state.Values.GetOrAdd(key, parsed);
        if (!ReferenceEquals(result, parsed))
            return result;

        state.InsertionOrder.Enqueue(key);
        if (Interlocked.Increment(ref state.Count) > this.Capacity)
            this.EvictOverflow(state);

        return result;
    }

    /// <summary>Clear cached names after the locale-code definitions change.</summary>
    public void Clear()
    {
        Volatile.Write(ref this.State, new CacheState());
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Evict the oldest entries until the cache is within its capacity.</summary>
    /// <param name="state">The cache generation to trim.</param>
    private void EvictOverflow(CacheState state)
    {
        lock (state.EvictionLock)
        {
            while (Volatile.Read(ref state.Count) > this.Capacity && state.InsertionOrder.TryDequeue(out CacheKey oldest))
            {
                if (state.Values.TryRemove(oldest, out _))
                    Interlocked.Decrement(ref state.Count);
            }
        }
    }

    /// <summary>One independently replaceable cache generation.</summary>
    private sealed class CacheState
    {
        /// <summary>The cached names.</summary>
        public readonly ConcurrentDictionary<CacheKey, AssetName> Values = new();

        /// <summary>The keys in insertion order for bounded eviction.</summary>
        public readonly ConcurrentQueue<CacheKey> InsertionOrder = new();

        /// <summary>A lock which ensures only one thread evicts entries at a time.</summary>
        public readonly object EvictionLock = new();

        /// <summary>The number of entries currently in <see cref="Values"/>.</summary>
        public int Count;
    }

    /// <summary>A parsed-name cache key.</summary>
    private readonly record struct CacheKey(string RawName, bool AllowLocales);
}
