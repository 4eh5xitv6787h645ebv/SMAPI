using System;
using System.Collections.Generic;

namespace StardewModdingAPI.Framework.Extensions;

/// <summary>Provides internal extensions for <see cref="IMonitor"/>.</summary>
internal static class MonitorExtensions
{
    /// <param name="monitor">The monitor to extend.</param>
    extension(IMonitor monitor)
    {
        /// <summary>Log a message for the player or developer the first time it occurs.</summary>
        /// <param name="hash">The hash of logged messages.</param>
        /// <param name="message">The message to log.</param>
        /// <param name="level">The log severity level.</param>
        public void LogOnce(HashSet<string> hash, string message, LogLevel level = LogLevel.Trace)
        {
            if (hash.Add(message))
                monitor.Log(message, level);
        }

        /// <summary>Log a message whose text can be created on the log-writer thread when supported.</summary>
        /// <typeparam name="TState">The type of state passed to the message factory.</typeparam>
        /// <param name="state">The immutable or privately owned state from which to create the message.</param>
        /// <param name="getMessage">Create the message text.</param>
        /// <param name="level">The message severity.</param>
        public void LogDeferred<TState>(TState state, Func<TState, string> getMessage, LogLevel level = LogLevel.Trace)
        {
            if (monitor is Monitor implementation)
                implementation.LogDeferred(state, getMessage, level);
            else
                monitor.Log(getMessage(state), level);
        }
    }
}
