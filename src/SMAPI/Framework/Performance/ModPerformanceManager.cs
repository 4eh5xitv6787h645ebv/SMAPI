using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace StardewModdingAPI.Framework.Performance;

/// <summary>Collects bounded performance and error diagnostics for mod-owned execution observed by SMAPI.</summary>
internal sealed class ModPerformanceManager
{
    /// <summary>The nested profiled handlers on the current thread.</summary>
    [ThreadStatic]
    private static List<ActiveHandler>? ActiveHandlers;

    /*********
    ** Fields
    *********/
    /// <summary>The lock which synchronizes mutable diagnostic state.</summary>
    private readonly object SyncRoot = new();

    /// <summary>The maximum number of distinct handler counters to retain.</summary>
    private readonly int HandlerCapacity;

    /// <summary>The retained tick history.</summary>
    private readonly TickPerformanceSnapshot[] TickHistory;

    /// <summary>The timestamp frequency used for elapsed-time conversion.</summary>
    private readonly long TimestampFrequency;

    /// <summary>Get a high-resolution timestamp.</summary>
    private readonly Func<long> GetTimestamp;

    /// <summary>Performance counters by mod, event, and handler.</summary>
    private readonly Dictionary<HandlerIdentity, HandlerCounter> HandlerCounters = [];

    /// <summary>Warning and error counters by mod.</summary>
    private readonly Dictionary<string, MutableModLogSummary> ModLogs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Instrumented handler time by mod during the current update tick.</summary>
    private readonly Dictionary<string, TickModCounter> CurrentTickMods = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether performance sampling is enabled, stored as an integer for lock-free hot-path reads.</summary>
    private int TrackingEnabled;

    /// <summary>The first retained tick-history index.</summary>
    private int TickHistoryStart;

    /// <summary>The number of retained tick-history entries.</summary>
    private int TickHistoryCount;

    /// <summary>The number of completed update ticks in this sample, including those outside the retained history.</summary>
    private long CompletedTicks;

    /// <summary>The number of distinct handlers omitted after reaching <see cref="HandlerCapacity"/>.</summary>
    private long OmittedHandlerInvocations;

    /// <summary>The UTC time when the current sample began.</summary>
    private DateTime SampleStartedUtc;

    /// <summary>The timestamp when the current sample began.</summary>
    private long SampleStartTimestamp;

    /// <summary>Whether an update tick is currently being measured.</summary>
    private bool IsTickOpen;

    /// <summary>The current update tick number.</summary>
    private uint CurrentTick;

    /// <summary>The timestamp when the current update tick began.</summary>
    private long CurrentTickStartTimestamp;

    /// <summary>The number of error messages emitted by mods during the current update tick.</summary>
    private int CurrentTickErrors;

    /// <summary>Whether completed ticks should be logged individually.</summary>
    private bool LogIndividualTicks;

    /// <summary>The minimum elapsed milliseconds for an individual tick to be logged.</summary>
    private double TickLogThresholdMilliseconds;


    /*********
    ** Accessors
    *********/
    /// <summary>Whether performance sampling is currently enabled.</summary>
    public bool IsTracking => Volatile.Read(ref this.TrackingEnabled) != 0;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="tickHistoryCapacity">The maximum number of recent update ticks to retain.</param>
    /// <param name="handlerCapacity">The maximum number of distinct handler counters to retain.</param>
    /// <param name="timestampFrequency">The timestamp frequency used for elapsed-time conversion.</param>
    /// <param name="getTimestamp">Get a high-resolution timestamp.</param>
    public ModPerformanceManager(int tickHistoryCapacity = 600, int handlerCapacity = 8192, long? timestampFrequency = null, Func<long>? getTimestamp = null)
    {
        if (tickHistoryCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(tickHistoryCapacity));
        if (handlerCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(handlerCapacity));

        this.TickHistory = new TickPerformanceSnapshot[tickHistoryCapacity];
        this.HandlerCapacity = handlerCapacity;
        this.TimestampFrequency = timestampFrequency ?? Stopwatch.Frequency;
        this.GetTimestamp = getTimestamp ?? Stopwatch.GetTimestamp;
        if (this.TimestampFrequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(timestampFrequency));

        this.ResetCore(this.GetTimestamp());
    }

    /// <summary>Start a new performance sample.</summary>
    /// <param name="logIndividualTicks">Whether to log completed ticks individually.</param>
    /// <param name="tickLogThresholdMilliseconds">The minimum tick duration to log, or zero to log every tick.</param>
    public void Start(bool logIndividualTicks = false, double tickLogThresholdMilliseconds = 0)
    {
        if (!double.IsFinite(tickLogThresholdMilliseconds) || tickLogThresholdMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(tickLogThresholdMilliseconds));

        lock (this.SyncRoot)
        {
            this.ResetCore(this.GetTimestamp());
            this.LogIndividualTicks = logIndividualTicks;
            this.TickLogThresholdMilliseconds = tickLogThresholdMilliseconds;
            Volatile.Write(ref this.TrackingEnabled, 1);
        }
    }

    /// <summary>Stop performance sampling while retaining the current results.</summary>
    public void Stop()
    {
        Volatile.Write(ref this.TrackingEnabled, 0);
        lock (this.SyncRoot)
        {
            this.IsTickOpen = false;
            this.CurrentTickMods.Clear();
        }
    }

    /// <summary>Clear all performance, warning, and error diagnostics while retaining the current tracking configuration.</summary>
    public void Reset()
    {
        lock (this.SyncRoot)
            this.ResetCore(this.GetTimestamp());
    }

    /// <summary>Configure individual tick logging without resetting the current sample.</summary>
    /// <param name="enabled">Whether individual tick logging should be enabled.</param>
    /// <param name="thresholdMilliseconds">The minimum tick duration to log, or zero to log every tick.</param>
    public void ConfigureTickLogging(bool enabled, double thresholdMilliseconds)
    {
        if (!double.IsFinite(thresholdMilliseconds) || thresholdMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(thresholdMilliseconds));

        lock (this.SyncRoot)
        {
            this.LogIndividualTicks = enabled;
            this.TickLogThresholdMilliseconds = thresholdMilliseconds;
        }
    }

    /// <summary>Apply persistent diagnostic settings.</summary>
    /// <param name="enabled">Whether performance sampling should be active.</param>
    /// <param name="logIndividualTicks">Whether qualifying ticks should be logged individually.</param>
    /// <param name="tickLogThresholdMilliseconds">The minimum tick duration to log, or zero to log every tick.</param>
    public void ApplySettings(bool enabled, bool logIndividualTicks, double tickLogThresholdMilliseconds)
    {
        if (enabled && !this.IsTracking)
            this.Start(logIndividualTicks, tickLogThresholdMilliseconds);
        else
        {
            this.ConfigureTickLogging(logIndividualTicks, tickLogThresholdMilliseconds);
            if (!enabled && this.IsTracking)
                this.Stop();
        }
    }

    /// <summary>Begin measuring an outer game update tick.</summary>
    /// <param name="tick">The update tick number.</param>
    /// <param name="startTimestamp">The timestamp at which the update began.</param>
    public void BeginTick(uint tick, long startTimestamp)
    {
        if (!this.IsTracking)
            return;

        lock (this.SyncRoot)
        {
            if (!this.IsTracking)
                return;

            this.IsTickOpen = true;
            this.CurrentTick = tick;
            this.CurrentTickStartTimestamp = startTimestamp;
            this.CurrentTickErrors = 0;
            this.CurrentTickMods.Clear();
        }
    }

    /// <summary>Finish measuring the current outer game update tick.</summary>
    /// <param name="endTimestamp">The timestamp at which the update ended.</param>
    /// <returns>The completed tick if it should be logged individually, otherwise <c>null</c>.</returns>
    public TickPerformanceSnapshot? CompleteTick(long endTimestamp)
    {
        if (!this.IsTracking)
            return null;

        lock (this.SyncRoot)
        {
            if (!this.IsTracking || !this.IsTickOpen)
                return null;

            long totalTimestampTicks = Math.Max(0, endTimestamp - this.CurrentTickStartTimestamp);
            long instrumentedTimestampTicks = 0;
            string? slowestModId = null;
            string? slowestModName = null;
            long slowestModTimestampTicks = 0;

            foreach ((string modId, TickModCounter counter) in this.CurrentTickMods)
            {
                instrumentedTimestampTicks += counter.TimestampTicks;
                if (counter.TimestampTicks > slowestModTimestampTicks)
                {
                    slowestModId = modId;
                    slowestModName = counter.DisplayName;
                    slowestModTimestampTicks = counter.TimestampTicks;
                }
            }

            TickPerformanceSnapshot sample = new(
                Tick: this.CurrentTick,
                TotalMilliseconds: this.ToMilliseconds(totalTimestampTicks),
                InstrumentedModMilliseconds: this.ToMilliseconds(instrumentedTimestampTicks),
                SlowestModId: slowestModId,
                SlowestModName: slowestModName,
                SlowestModMilliseconds: this.ToMilliseconds(slowestModTimestampTicks),
                ErrorCount: this.CurrentTickErrors
            );

            this.AddTick(sample);
            this.CompletedTicks++;
            this.IsTickOpen = false;
            this.CurrentTickMods.Clear();

            return this.LogIndividualTicks && sample.TotalMilliseconds >= this.TickLogThresholdMilliseconds
                ? sample
                : null;
        }
    }

    /// <summary>Begin timing one mod-owned event handler.</summary>
    /// <param name="modId">The mod's unique ID.</param>
    /// <param name="modName">The mod's display name.</param>
    /// <param name="eventName">The managed event name.</param>
    /// <param name="handlerName">The registered handler method name.</param>
    /// <returns>A token used to finish the matching invocation.</returns>
    public HandlerTimingToken BeginHandler(string modId, string modName, string eventName, string handlerName)
    {
        if (!this.IsTracking)
            return default;

        List<ActiveHandler> handlers = ModPerformanceManager.ActiveHandlers ??= new List<ActiveHandler>(8);
        int depth = handlers.Count;
        handlers.Add(new ActiveHandler(this, modId, modName, eventName, handlerName, this.GetTimestamp(), 0));
        return new HandlerTimingToken(this, depth);
    }

    /// <summary>Begin timing one mod-owned operation.</summary>
    /// <param name="mod">The mod which owns the operation.</param>
    /// <param name="operationName">The event or operation name.</param>
    /// <param name="handlerName">The callback or method name.</param>
    public HandlerTimingToken BeginHandler(IModMetadata mod, string operationName, string handlerName)
    {
        string modId = mod.HasManifest()
            ? mod.Manifest.UniqueID
            : mod.DisplayName;

        return this.BeginHandler(modId, mod.DisplayName, operationName, handlerName);
    }

    /// <summary>Finish timing a mod-owned event handler.</summary>
    /// <param name="token">The token returned by <see cref="BeginHandler(string, string, string, string)"/>.</param>
    /// <param name="failed">Whether the handler threw an exception.</param>
    public void EndHandler(HandlerTimingToken token, bool failed)
    {
        if (!token.IsFor(this))
            return;

        List<ActiveHandler>? handlers = ModPerformanceManager.ActiveHandlers;
        if (handlers is null || handlers.Count != token.Depth + 1)
            return;

        int index = handlers.Count - 1;
        ActiveHandler handler = handlers[index];
        handlers.RemoveAt(index);

        long elapsedTimestampTicks = Math.Max(0, this.GetTimestamp() - handler.StartTimestamp);
        long exclusiveTimestampTicks = Math.Max(0, elapsedTimestampTicks - handler.NestedTimestampTicks);

        if (handlers.Count > 0)
        {
            int parentIndex = handlers.Count - 1;
            ActiveHandler parent = handlers[parentIndex];
            if (ReferenceEquals(parent.Manager, this))
            {
                parent.NestedTimestampTicks += elapsedTimestampTicks;
                handlers[parentIndex] = parent;
            }
        }

        this.RecordHandler(handler.ModId, handler.ModName, handler.EventName, handler.HandlerName, exclusiveTimestampTicks, failed);
    }

    /// <summary>Record one invocation of a mod-owned SMAPI event handler.</summary>
    /// <param name="modId">The mod's unique ID.</param>
    /// <param name="modName">The mod's display name.</param>
    /// <param name="eventName">The managed event name.</param>
    /// <param name="handlerName">The registered handler method name.</param>
    /// <param name="elapsedTimestampTicks">The elapsed timestamp ticks.</param>
    /// <param name="failed">Whether the handler threw an exception.</param>
    public void RecordHandler(string modId, string modName, string eventName, string handlerName, long elapsedTimestampTicks, bool failed)
    {
        if (!this.IsTracking)
            return;

        elapsedTimestampTicks = Math.Max(0, elapsedTimestampTicks);
        HandlerIdentity identity = new(modId, modName, eventName, handlerName);

        lock (this.SyncRoot)
        {
            if (!this.IsTracking)
                return;

            if (this.IsTickOpen)
            {
                if (!this.CurrentTickMods.TryGetValue(modId, out TickModCounter tickCounter))
                    tickCounter = new TickModCounter(modName, 0);

                tickCounter.TimestampTicks += elapsedTimestampTicks;
                this.CurrentTickMods[modId] = tickCounter;
            }

            if (!this.HandlerCounters.TryGetValue(identity, out HandlerCounter? counter))
            {
                if (this.HandlerCounters.Count >= this.HandlerCapacity)
                {
                    this.OmittedHandlerInvocations++;
                    return;
                }

                this.HandlerCounters[identity] = counter = new HandlerCounter();
            }

            counter.Add(elapsedTimestampTicks, failed);
        }
    }

    /// <summary>Record a warning or error emitted through a mod's monitor.</summary>
    /// <param name="modId">The monitor's mod ID.</param>
    /// <param name="modName">The monitor's display name.</param>
    /// <param name="level">The logged severity.</param>
    public void RecordLog(string modId, string modName, LogLevel level)
    {
        bool isWarning = level is LogLevel.Warn or LogLevel.Alert;
        bool isError = level is LogLevel.Error;
        if ((!isWarning && !isError) || modId is "SMAPI" or "game")
            return;

        lock (this.SyncRoot)
        {
            if (!this.ModLogs.TryGetValue(modId, out MutableModLogSummary? summary))
                this.ModLogs[modId] = summary = new MutableModLogSummary(modId, modName);

            if (isWarning)
                summary.WarningCount++;
            if (isError)
            {
                summary.ErrorCount++;
                if (this.IsTickOpen)
                    this.CurrentTickErrors++;
            }
        }
    }

    /// <summary>Get an immutable snapshot of the current diagnostics.</summary>
    public ModPerformanceSnapshot GetSnapshot()
    {
        lock (this.SyncRoot)
        {
            HandlerPerformanceSnapshot[] handlers = this.HandlerCounters
                .Select(pair => new HandlerPerformanceSnapshot(
                    ModId: pair.Key.ModId,
                    ModName: pair.Key.ModName,
                    EventName: pair.Key.EventName,
                    HandlerName: pair.Key.HandlerName,
                    CallCount: pair.Value.CallCount,
                    TotalMilliseconds: this.ToMilliseconds(pair.Value.TotalTimestampTicks),
                    MaximumMilliseconds: this.ToMilliseconds(pair.Value.MaximumTimestampTicks),
                    FailureCount: pair.Value.FailureCount
                ))
                .ToArray();

            ModLogSnapshot[] logs = this.ModLogs.Values
                .Select(summary => new ModLogSnapshot(summary.ModId, summary.ModName, summary.WarningCount, summary.ErrorCount))
                .ToArray();

            TickPerformanceSnapshot[] ticks = new TickPerformanceSnapshot[this.TickHistoryCount];
            for (int i = 0; i < ticks.Length; i++)
                ticks[i] = this.TickHistory[(this.TickHistoryStart + i) % this.TickHistory.Length];

            long now = this.GetTimestamp();
            return new ModPerformanceSnapshot(
                IsTracking: this.IsTracking,
                StartedUtc: this.SampleStartedUtc,
                Elapsed: TimeSpan.FromMilliseconds(this.ToMilliseconds(Math.Max(0, now - this.SampleStartTimestamp))),
                CompletedTickCount: this.CompletedTicks,
                Handlers: handlers,
                ModLogs: logs,
                RecentTicks: ticks,
                OmittedHandlerInvocations: this.OmittedHandlerInvocations,
                LogIndividualTicks: this.LogIndividualTicks,
                TickLogThresholdMilliseconds: this.TickLogThresholdMilliseconds
            );
        }
    }

    /// <summary>Convert timestamp ticks to milliseconds.</summary>
    /// <param name="timestampTicks">The timestamp ticks.</param>
    internal double ToMilliseconds(long timestampTicks)
    {
        return timestampTicks * 1000d / this.TimestampFrequency;
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Reset all mutable sample data. The caller must hold <see cref="SyncRoot"/> unless constructing the instance.</summary>
    /// <param name="timestamp">The current timestamp.</param>
    private void ResetCore(long timestamp)
    {
        this.HandlerCounters.Clear();
        this.ModLogs.Clear();
        this.CurrentTickMods.Clear();
        Array.Clear(this.TickHistory);
        this.TickHistoryStart = 0;
        this.TickHistoryCount = 0;
        this.CompletedTicks = 0;
        this.OmittedHandlerInvocations = 0;
        this.SampleStartedUtc = DateTime.UtcNow;
        this.SampleStartTimestamp = timestamp;
        this.IsTickOpen = false;
        this.CurrentTickErrors = 0;
    }

    /// <summary>Add a tick to the bounded circular history.</summary>
    /// <param name="sample">The tick sample.</param>
    private void AddTick(TickPerformanceSnapshot sample)
    {
        int index;
        if (this.TickHistoryCount < this.TickHistory.Length)
        {
            index = (this.TickHistoryStart + this.TickHistoryCount) % this.TickHistory.Length;
            this.TickHistoryCount++;
        }
        else
        {
            index = this.TickHistoryStart;
            this.TickHistoryStart = (this.TickHistoryStart + 1) % this.TickHistory.Length;
        }

        this.TickHistory[index] = sample;
    }


    /*********
    ** Private models
    *********/
    /// <summary>A distinct mod-owned event handler.</summary>
    private readonly record struct HandlerIdentity(string ModId, string ModName, string EventName, string HandlerName);

    /// <summary>Mutable aggregate statistics for one handler.</summary>
    private sealed class HandlerCounter
    {
        public long CallCount;
        public long TotalTimestampTicks;
        public long MaximumTimestampTicks;
        public long FailureCount;

        public void Add(long elapsedTimestampTicks, bool failed)
        {
            this.CallCount++;
            this.TotalTimestampTicks += elapsedTimestampTicks;
            this.MaximumTimestampTicks = Math.Max(this.MaximumTimestampTicks, elapsedTimestampTicks);
            if (failed)
                this.FailureCount++;
        }
    }

    /// <summary>Mutable aggregate warning/error statistics for one mod.</summary>
    private sealed class MutableModLogSummary(string modId, string modName)
    {
        public string ModId { get; } = modId;
        public string ModName { get; } = modName;
        public long WarningCount;
        public long ErrorCount;
    }

    /// <summary>Mutable time attributed to one mod during a tick.</summary>
    private struct TickModCounter(string displayName, long timestampTicks)
    {
        public string DisplayName { get; } = displayName;
        public long TimestampTicks = timestampTicks;
    }

    /// <summary>One active nested handler invocation.</summary>
    private struct ActiveHandler(ModPerformanceManager manager, string modId, string modName, string eventName, string handlerName, long startTimestamp, long nestedTimestampTicks)
    {
        public ModPerformanceManager Manager { get; } = manager;
        public string ModId { get; } = modId;
        public string ModName { get; } = modName;
        public string EventName { get; } = eventName;
        public string HandlerName { get; } = handlerName;
        public long StartTimestamp { get; } = startTimestamp;
        public long NestedTimestampTicks = nestedTimestampTicks;
    }
}

/// <summary>An opaque token for one active profiled handler invocation.</summary>
internal readonly struct HandlerTimingToken
{
    /// <summary>The manager which created this token.</summary>
    private readonly ModPerformanceManager? Manager;

    /// <summary>The handler's zero-based nesting depth.</summary>
    internal int Depth { get; }

    /// <summary>Construct an instance.</summary>
    public HandlerTimingToken(ModPerformanceManager manager, int depth)
    {
        this.Manager = manager;
        this.Depth = depth;
    }

    /// <summary>Get whether this token belongs to the given manager.</summary>
    public bool IsFor(ModPerformanceManager manager)
    {
        return ReferenceEquals(this.Manager, manager);
    }
}

/// <summary>An immutable performance diagnostic snapshot.</summary>
internal sealed record ModPerformanceSnapshot(
    bool IsTracking,
    DateTime StartedUtc,
    TimeSpan Elapsed,
    long CompletedTickCount,
    IReadOnlyList<HandlerPerformanceSnapshot> Handlers,
    IReadOnlyList<ModLogSnapshot> ModLogs,
    IReadOnlyList<TickPerformanceSnapshot> RecentTicks,
    long OmittedHandlerInvocations,
    bool LogIndividualTicks,
    double TickLogThresholdMilliseconds
);

/// <summary>Aggregate timing for one mod-owned event handler.</summary>
internal readonly record struct HandlerPerformanceSnapshot(
    string ModId,
    string ModName,
    string EventName,
    string HandlerName,
    long CallCount,
    double TotalMilliseconds,
    double MaximumMilliseconds,
    long FailureCount
)
{
    /// <summary>The average duration per invocation.</summary>
    public double AverageMilliseconds => this.CallCount > 0 ? this.TotalMilliseconds / this.CallCount : 0;
}

/// <summary>Aggregate warning/error log counts for one mod.</summary>
internal readonly record struct ModLogSnapshot(string ModId, string ModName, long WarningCount, long ErrorCount);

/// <summary>Aggregate timings for one outer game update tick.</summary>
internal readonly record struct TickPerformanceSnapshot(
    uint Tick,
    double TotalMilliseconds,
    double InstrumentedModMilliseconds,
    string? SlowestModId,
    string? SlowestModName,
    double SlowestModMilliseconds,
    int ErrorCount
)
{
    /// <summary>Time in the update which wasn't observed inside a SMAPI-managed mod event handler.</summary>
    public double UnattributedMilliseconds => Math.Max(0, this.TotalMilliseconds - this.InstrumentedModMilliseconds);
}
