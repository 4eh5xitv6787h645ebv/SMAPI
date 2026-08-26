using System;
using System.Threading;

namespace StardewModdingAPI.Framework.Health;

/// <summary>Accepts allocation-free log counters from registered monitors.</summary>
internal interface IModHealthLogSink
{
    /// <summary>Register a safe source identity once and get its high-volume counter.</summary>
    IModHealthLogCounter RegisterLogSource(string? modId, string? modName, ModHealthLogSourceCategory sourceCategory);

    /// <summary>Exclude logs produced while the health reporter is generating or writing output.</summary>
    IDisposable SuppressReporterLogs();
}

/// <summary>A pre-registered, allocation-free log counter.</summary>
internal interface IModHealthLogCounter
{
    /// <summary>Record safe numeric metadata for one log message.</summary>
    void Record(LogLevel level, int messageLength, int managedThreadId, ModHealthLogObservationCategory observationCategory);
}

/// <summary>Whether a log is normal session evidence or reporter output which must be suppressed.</summary>
internal enum ModHealthLogObservationCategory
{
    Normal,
    Reporter
}

/// <summary>Tracks reporter-log suppression across asynchronous continuations.</summary>
internal static class ModHealthReporterLogScope
{
    private static readonly AsyncLocal<int> Depth = new();

    public static bool IsActive => Depth.Value > 0;

    public static IDisposable Enter()
    {
        Depth.Value++;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private bool IsDisposed;

        public void Dispose()
        {
            if (this.IsDisposed)
                return;
            this.IsDisposed = true;
            Depth.Value = Math.Max(0, Depth.Value - 1);
        }
    }
}
