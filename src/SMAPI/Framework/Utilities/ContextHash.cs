using System;
using System.Collections.Generic;

namespace StardewModdingAPI.Framework.Utilities;

/// <summary>A <see cref="HashSet{T}"/> wrapper meant for tracking recursive contexts.</summary>
/// <typeparam name="T">The key type.</typeparam>
internal class ContextHash<T> : HashSet<T>
{
    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    public ContextHash() { }

    /// <summary>Construct an instance.</summary>
    /// <param name="comparer">The <see cref="IEqualityComparer{T}"/> implementation to use when comparing values in the set, or <c>null</c> to use the default comparer for the set type.</param>
    public ContextHash(IEqualityComparer<T> comparer)
        : base(comparer) { }

    /// <summary>Add a key while an action is in progress, and remove it when it completes.</summary>
    /// <param name="key">The key to add.</param>
    /// <param name="action">The action to perform.</param>
    /// <exception cref="InvalidOperationException">The specified key is already added.</exception>
    public void Track(T key, Action action)
    {
        if (!this.Add(key))
            throw new InvalidOperationException($"Can't track context for key {key} because it's already added.");
        try
        {
            action();
        }
        finally
        {
            this.Remove(key);
        }
    }

    /// <summary>Add a key while an action is in progress, and remove it when it completes.</summary>
    /// <typeparam name="TResult">The value type returned by the method.</typeparam>
    /// <param name="key">The key to add.</param>
    /// <param name="action">The action to perform.</param>
    public TResult Track<TResult>(T key, Func<TResult> action)
    {
        if (!this.Add(key))
            throw new InvalidOperationException($"Can't track context for key {key} because it's already added.");
        try
        {
            return action();
        }
        finally
        {
            this.Remove(key);
        }
    }

    /// <summary>Add a key while an action is in progress, and remove it when it completes.</summary>
    /// <typeparam name="TState">The state type to pass to the action.</typeparam>
    /// <typeparam name="TResult">The value type returned by the method.</typeparam>
    /// <param name="key">The key to add.</param>
    /// <param name="state">The state to pass to the action.</param>
    /// <param name="action">The action to perform.</param>
    public TResult Track<TState, TResult>(T key, TState state, Func<TState, TResult> action)
    {
        if (!this.Add(key))
            throw new InvalidOperationException($"Can't track context for key {key} because it's already added.");
        try
        {
            return action(state);
        }
        finally
        {
            this.Remove(key);
        }
    }
}
