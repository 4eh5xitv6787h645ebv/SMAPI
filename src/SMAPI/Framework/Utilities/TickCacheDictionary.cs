using System;
using System.Collections.Generic;

namespace StardewModdingAPI.Framework.Utilities;

/// <summary>An in-memory dictionary cache that stores data for the duration of a game update tick.</summary>
/// <typeparam name="TKey">The dictionary key type.</typeparam>
/// <typeparam name="TValue">The dictionary value type.</typeparam>
internal class TickCacheDictionary<TKey, TValue>
    where TKey : notnull
{
    /*********
    ** Fields
    *********/
    /// <summary>The last game tick for which data was cached.</summary>
    private uint? LastGameTick;

    /// <summary>The underlying cached data.</summary>
    private readonly Dictionary<TKey, TValue> Cache = new();


    /*********
    ** Public methods
    *********/
    /// <summary>Get a value from the cache, fetching it first if it's not cached yet.</summary>
    /// <param name="cacheKey">The unique key for the cached value.</param>
    /// <param name="get">Get the latest data if it's not in the cache yet.</param>
    public TValue GetOrSet(TKey cacheKey, Func<TValue> get)
    {
        return this.GetOrSet(cacheKey, get, static callback => callback());
    }

    /// <summary>Get a value from the cache, fetching it first if it's not cached yet.</summary>
    /// <typeparam name="TState">The factory state type.</typeparam>
    /// <param name="cacheKey">The unique key for the cached value.</param>
    /// <param name="state">The state to pass to the value factory.</param>
    /// <param name="get">Get the latest data from the state if it isn't cached yet.</param>
    public TValue GetOrSet<TState>(TKey cacheKey, TState state, Func<TState, TValue> get)
    {
        // clear cache on new tick
        if (SCore.ProcessTicksElapsed != this.LastGameTick)
        {
            this.Cache.Clear();
            this.LastGameTick = SCore.ProcessTicksElapsed;
        }

        // fetch value
        if (!this.Cache.TryGetValue(cacheKey, out TValue? cached))
            this.Cache[cacheKey] = cached = get(state);
        return cached;
    }

    /// <summary>Remove all cached values.</summary>
    public void Clear()
    {
        this.Cache.Clear();
        this.LastGameTick = null;
    }

    /// <summary>Remove an entry from the cache.</summary>
    /// <param name="cacheKey">The unique key for the cached value.</param>
    /// <returns>Returns whether the key was present in the dictionary.</returns>
    public bool Remove(TKey cacheKey)
    {
        return this.Cache.Remove(cacheKey);
    }

    /// <summary>Remove entries whose keys match a predicate.</summary>
    /// <typeparam name="TState">The predicate state type.</typeparam>
    /// <param name="state">The state to pass to the predicate.</param>
    /// <param name="shouldRemove">Get whether an entry should be removed.</param>
    /// <returns>Returns the number of entries removed.</returns>
    public int RemoveWhere<TState>(TState state, Func<TKey, TState, bool> shouldRemove)
    {
        int removed = 0;
        foreach (TKey key in this.Cache.Keys)
        {
            if (shouldRemove(key, state) && this.Cache.Remove(key))
                removed++;
        }

        return removed;
    }
}

/// <summary>An in-memory dictionary cache that stores data for the duration of a game update tick.</summary>
/// <typeparam name="TKey">The dictionary key type.</typeparam>
internal class TickCacheDictionary<TKey> : TickCacheDictionary<TKey, object>
    where TKey : notnull
{
    /*********
    ** Public methods
    *********/
    /// <summary>Get a value from the cache, fetching it first if it's not cached yet.</summary>
    /// <param name="cacheKey">The unique key for the cached value.</param>
    /// <param name="get">Get the latest data if it's not in the cache yet.</param>
    public TValue GetOrSet<TValue>(TKey cacheKey, Func<TValue> get)
    {
        return this.GetOrSet(cacheKey, get, static callback => callback());
    }

    /// <summary>Get a value from the cache, fetching it first if it's not cached yet.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <typeparam name="TState">The factory state type.</typeparam>
    /// <param name="cacheKey">The unique key for the cached value.</param>
    /// <param name="state">The state to pass to the value factory.</param>
    /// <param name="get">Get the latest data from the state if it isn't cached yet.</param>
    public TValue GetOrSet<TValue, TState>(TKey cacheKey, TState state, Func<TState, TValue> get)
    {
        object? value = base.GetOrSet(
            cacheKey,
            (State: state, Get: get),
            static factory => factory.Get(factory.State)!
        );

        try
        {
            return (TValue)value;
        }
        catch (Exception ex)
        {
            throw new InvalidCastException($"Can't cast value of the '{cacheKey}' cache entry from {value?.GetType().FullName ?? "null"} to {typeof(TValue).FullName}.", ex);
        }
    }
}
