using System;
using System.Threading;

namespace StardewModdingAPI.Framework.Extensions;

/// <summary>Provides internal extensions for <see cref="ReaderWriterLockSlim"/>.</summary>
internal static class ReaderWriterLockSlimExtensions
{
    /// <param name="lock">The lock to extend.</param>
    extension(ReaderWriterLockSlim @lock)
    {
        /// <summary>Run code within a read lock.</summary>
        /// <param name="action">The action to perform.</param>
        public void InReadLock(Action action)
        {
            @lock.EnterReadLock();
            try
            {
                action();
            }
            finally
            {
                @lock.ExitReadLock();
            }
        }

        /// <summary>Run code with explicit state within a read lock.</summary>
        /// <typeparam name="TState">The state type.</typeparam>
        /// <param name="state">The state to pass to the action.</param>
        /// <param name="action">The action to perform.</param>
        public void InReadLock<TState>(TState state, Action<TState> action)
        {
            @lock.EnterReadLock();
            try
            {
                action(state);
            }
            finally
            {
                @lock.ExitReadLock();
            }
        }

        /// <summary>Run code within a read lock.</summary>
        /// <typeparam name="TReturn">The action's return value.</typeparam>
        /// <param name="action">The action to perform.</param>
        public TReturn InReadLock<TReturn>(Func<TReturn> action)
        {
            @lock.EnterReadLock();
            try
            {
                return action();
            }
            finally
            {
                @lock.ExitReadLock();
            }
        }

        /// <summary>Run code with explicit state within a read lock.</summary>
        /// <typeparam name="TState">The state type.</typeparam>
        /// <typeparam name="TReturn">The action's return value.</typeparam>
        /// <param name="state">The state to pass to the action.</param>
        /// <param name="action">The action to perform.</param>
        public TReturn InReadLock<TState, TReturn>(TState state, Func<TState, TReturn> action)
        {
            @lock.EnterReadLock();
            try
            {
                return action(state);
            }
            finally
            {
                @lock.ExitReadLock();
            }
        }

        /// <summary>Run code within a write lock.</summary>
        /// <param name="action">The action to perform.</param>
        public void InWriteLock(Action action)
        {
            @lock.EnterWriteLock();
            try
            {
                action();
            }
            finally
            {
                @lock.ExitWriteLock();
            }
        }

        /// <summary>Run code with explicit state within a write lock.</summary>
        /// <typeparam name="TState">The state type.</typeparam>
        /// <param name="state">The state to pass to the action.</param>
        /// <param name="action">The action to perform.</param>
        public void InWriteLock<TState>(TState state, Action<TState> action)
        {
            @lock.EnterWriteLock();
            try
            {
                action(state);
            }
            finally
            {
                @lock.ExitWriteLock();
            }
        }

        /// <summary>Run code within a write lock.</summary>
        /// <typeparam name="TReturn">The action's return value.</typeparam>
        /// <param name="action">The action to perform.</param>
        public TReturn InWriteLock<TReturn>(Func<TReturn> action)
        {
            @lock.EnterWriteLock();
            try
            {
                return action();
            }
            finally
            {
                @lock.ExitWriteLock();
            }
        }

        /// <summary>Run code with explicit state within a write lock.</summary>
        /// <typeparam name="TState">The state type.</typeparam>
        /// <typeparam name="TReturn">The action's return value.</typeparam>
        /// <param name="state">The state to pass to the action.</param>
        /// <param name="action">The action to perform.</param>
        public TReturn InWriteLock<TState, TReturn>(TState state, Func<TState, TReturn> action)
        {
            @lock.EnterWriteLock();
            try
            {
                return action(state);
            }
            finally
            {
                @lock.ExitWriteLock();
            }
        }
    }
}
