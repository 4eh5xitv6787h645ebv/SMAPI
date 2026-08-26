using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using StardewModdingAPI.Framework.Health;

namespace StardewModdingAPI.Framework.Performance;

/// <summary>Collects bounded performance and error diagnostics for mod-owned execution observed by SMAPI.</summary>
internal sealed class ModPerformanceManager
{
    private const double DefaultSlowUpdateThresholdMilliseconds = 33.333;
    private static readonly double[] HealthHistogramThresholds = [16.667, 33.333, 50, 100, 250, 500, 1000];

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

    /// <summary>The retained health-oriented recent update history.</summary>
    private readonly ModHealthUpdatePerformanceSnapshot[] HealthTickHistory;

    /// <summary>The worst update ticks retained across the whole capture.</summary>
    private readonly ModHealthUpdatePerformanceSnapshot[] WorstHealthTicks;

    /// <summary>The highest-ranked completed slow-update episodes.</summary>
    private readonly ModHealthSlowEpisodeSnapshot[] SlowEpisodes;

    /// <summary>The maximum number of contributors retained for a slow update.</summary>
    private readonly int SlowTickContributorCapacity;

    /// <summary>The maximum number of distinct mod contributors tracked in one update.</summary>
    private readonly int TickContributorIdentityCapacity;

    /// <summary>The fixed logarithmic update-duration histogram buckets.</summary>
    private readonly long[] HealthHistogramBuckets = new long[ModHealthTimingHistogramSnapshot.BucketCount];

    /// <summary>Exact counts at the configured update-duration thresholds.</summary>
    private readonly long[] HealthThresholdCounts = new long[HealthHistogramThresholds.Length];

    /// <summary>The timestamp frequency used for elapsed-time conversion.</summary>
    private readonly long TimestampFrequency;

    /// <summary>Get a high-resolution timestamp.</summary>
    private readonly Func<long> GetTimestamp;

    /// <summary>Get the number of garbage collections for a generation.</summary>
    private readonly Func<int, int> GetGcCollectionCount;

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

    /// <summary>The first retained health tick-history index.</summary>
    private int HealthTickHistoryStart;

    /// <summary>The number of retained health tick-history entries.</summary>
    private int HealthTickHistoryCount;

    /// <summary>The number of retained whole-capture worst updates.</summary>
    private int WorstHealthTickCount;

    /// <summary>The number of retained completed slow-update episodes.</summary>
    private int SlowEpisodeCount;

    /// <summary>The number of completed update ticks in this sample, including those outside the retained history.</summary>
    private long CompletedTicks;

    /// <summary>The capture-relative sequence assigned to the next completed update.</summary>
    private long NextHealthTickSequence;

    /// <summary>The number of distinct handlers omitted after reaching <see cref="HandlerCapacity"/>.</summary>
    private long OmittedHandlerInvocations;

    /// <summary>The number of update contributors omitted after the per-update identity cap was reached.</summary>
    private long OmittedTickContributorIdentities;

    /// <summary>The total number of contributors omitted from retained slow-update top lists.</summary>
    private long OmittedRetainedSlowTickContributors;

    /// <summary>The number of completed slow episodes omitted from the ranked bounded list.</summary>
    private long OmittedSlowEpisodes;

    /// <summary>The number of invalid update durations omitted from the histogram.</summary>
    private long InvalidHistogramUpdates;

    /// <summary>The UTC time when the current sample began.</summary>
    private DateTime SampleStartedUtc;

    /// <summary>The timestamp when the current sample began.</summary>
    private long SampleStartTimestamp;

    /// <summary>A monotonically increasing identity for the current sample.</summary>
    private long SampleGeneration;

    /// <summary>The garbage collection counts per generation when the current sample began.</summary>
    private readonly int[] SampleStartGcCollections = new int[3];

    /// <summary>The frozen garbage collection counts per generation when the current sample stopped.</summary>
    private readonly int[] SampleEndGcCollections = new int[3];

    /// <summary>Whether <see cref="SampleEndGcCollections"/> contains a frozen stop boundary.</summary>
    private bool HasSampleEndGcCollections;

    /// <summary>Whether an update tick is currently being measured.</summary>
    private bool IsTickOpen;

    /// <summary>The current update tick number.</summary>
    private uint CurrentTick;

    /// <summary>The timestamp when the current update tick began.</summary>
    private long CurrentTickStartTimestamp;

    /// <summary>The managed thread which owns the current update tick.</summary>
    private int CurrentTickThreadId;

    /// <summary>The number of error messages emitted by mods during the current update tick.</summary>
    private int CurrentTickErrors;

    /// <summary>The number of warning messages emitted on the owning thread during the current update tick.</summary>
    private int CurrentTickWarnings;

    /// <summary>The number of failed callbacks completed on the owning thread during the current update tick.</summary>
    private int CurrentTickCallbackFailures;

    /// <summary>The safe coarse context sampled at the beginning of the current update tick.</summary>
    private ModHealthTickContext CurrentTickContext;

    /// <summary>The number of contributor identities omitted from the current update.</summary>
    private long CurrentTickOmittedContributorIdentities;

    /// <summary>The instrumented handler time recorded during the current update tick.</summary>
    private long CurrentTickInstrumentedTimestampTicks;

    /// <summary>The base game update time recorded during the current update tick.</summary>
    private long CurrentTickGameUpdateTimestampTicks;

    /// <summary>The instrumented handler time recorded while a base game update was executing during the current update tick.</summary>
    private long CurrentTickInstrumentedDuringGameUpdateTicks;

    /// <summary>Whether a base game update is currently being measured.</summary>
    private bool IsGameUpdateOpen;

    /// <summary>The timestamp when the current base game update began.</summary>
    private long GameUpdateStartTimestamp;

    /// <summary>Whether the current tick's timing partition is invalid.</summary>
    private bool CurrentTickTimingPartitionIsInvalid;

    /// <summary>The garbage collection counts per generation when the current update tick began.</summary>
    private readonly int[] TickStartGcCollections = new int[3];

    /// <summary>The total measured update-tick time in this sample.</summary>
    private long TotalTickTimestampTicks;

    /// <summary>The total base game update time measured within ticks in this sample.</summary>
    private long TotalGameUpdateTimestampTicks;

    /// <summary>The total instrumented handler time measured within ticks in this sample.</summary>
    private long TotalTickInstrumentedTimestampTicks;

    /// <summary>The total instrumented handler time measured within base game updates in this sample.</summary>
    private long TotalInstrumentedDuringGameUpdateTicks;

    /// <summary>The total garbage collections per generation observed within ticks in this sample.</summary>
    private readonly long[] TotalGcCollections = new long[3];

    /// <summary>The number of completed ticks whose timing partition was invalid.</summary>
    private long InvalidTimingPartitionTicks;

    /// <summary>The number of completed ticks whose garbage collection delta was invalid.</summary>
    private long InvalidGcCollectionTicks;

    /// <summary>The slow-update threshold used for health retention and episode clustering.</summary>
    private double SlowUpdateThresholdMilliseconds = DefaultSlowUpdateThresholdMilliseconds;

    /// <summary>The number of completed updates at or above the slow-update threshold.</summary>
    private long SlowUpdateCount;

    /// <summary>Whether a slow-update episode is currently open.</summary>
    private bool HasOpenSlowEpisode;

    /// <summary>The currently open slow-update episode.</summary>
    private MutableSlowEpisode OpenSlowEpisode;

    /// <summary>The number of consecutive below-threshold updates following the open episode.</summary>
    private int OpenEpisodeTrailingFastUpdates;

    /// <summary>The number of valid durations added to the histogram.</summary>
    private long HealthHistogramCount;

    /// <summary>The exact sum of valid update durations in timestamp ticks.</summary>
    private long HealthHistogramTotalTimestampTicks;

    /// <summary>The exact minimum valid update duration in timestamp ticks.</summary>
    private long HealthHistogramMinimumTimestampTicks;

    /// <summary>The exact maximum valid update duration in timestamp ticks.</summary>
    private long HealthHistogramMaximumTimestampTicks;

    /// <summary>The number of valid durations below the histogram range.</summary>
    private long HealthHistogramUnderflowCount;

    /// <summary>The number of valid durations above the histogram range.</summary>
    private long HealthHistogramOverflowCount;

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
    /// <param name="getGcCollectionCount">Get the number of garbage collections for a generation.</param>
    /// <param name="worstTickCapacity">The maximum number of whole-capture worst updates to retain.</param>
    /// <param name="slowEpisodeCapacity">The maximum number of ranked slow-update episodes to retain.</param>
    /// <param name="slowTickContributorCapacity">The maximum number of contributors retained for one slow update.</param>
    /// <param name="tickContributorIdentityCapacity">The maximum number of distinct contributors tracked in one update.</param>
    public ModPerformanceManager(int tickHistoryCapacity = 600, int handlerCapacity = 8192, long? timestampFrequency = null, Func<long>? getTimestamp = null, Func<int, int>? getGcCollectionCount = null, int worstTickCapacity = 100, int slowEpisodeCapacity = 50, int slowTickContributorCapacity = 5, int tickContributorIdentityCapacity = 4096)
    {
        if (tickHistoryCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(tickHistoryCapacity));
        if (handlerCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(handlerCapacity));
        if (worstTickCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(worstTickCapacity));
        if (slowEpisodeCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(slowEpisodeCapacity));
        if (slowTickContributorCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(slowTickContributorCapacity));
        if (tickContributorIdentityCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(tickContributorIdentityCapacity));

        this.TickHistory = new TickPerformanceSnapshot[tickHistoryCapacity];
        this.HealthTickHistory = new ModHealthUpdatePerformanceSnapshot[tickHistoryCapacity];
        this.WorstHealthTicks = new ModHealthUpdatePerformanceSnapshot[worstTickCapacity];
        this.SlowEpisodes = new ModHealthSlowEpisodeSnapshot[slowEpisodeCapacity];
        this.HandlerCapacity = handlerCapacity;
        this.SlowTickContributorCapacity = slowTickContributorCapacity;
        this.TickContributorIdentityCapacity = tickContributorIdentityCapacity;
        this.TimestampFrequency = timestampFrequency ?? Stopwatch.Frequency;
        this.GetTimestamp = getTimestamp ?? Stopwatch.GetTimestamp;
        this.GetGcCollectionCount = getGcCollectionCount ?? GC.CollectionCount;
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
            this.SlowUpdateThresholdMilliseconds = tickLogThresholdMilliseconds > 0
                ? tickLogThresholdMilliseconds
                : DefaultSlowUpdateThresholdMilliseconds;
            Volatile.Write(ref this.TrackingEnabled, 1);
        }
    }

    /// <summary>Stop performance sampling while retaining the current results.</summary>
    public void Stop()
    {
        bool wasTracking = Interlocked.Exchange(ref this.TrackingEnabled, 0) != 0;
        lock (this.SyncRoot)
        {
            this.CloseOpenSlowEpisode();
            if (wasTracking && !this.HasSampleEndGcCollections)
            {
                for (int generation = 0; generation < this.SampleEndGcCollections.Length; generation++)
                    this.SampleEndGcCollections[generation] = this.GetGcCollectionCount(generation);
                this.HasSampleEndGcCollections = true;
            }
            this.IsTickOpen = false;
            this.IsGameUpdateOpen = false;
            this.CurrentTickThreadId = 0;
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
        this.BeginTick(tick, startTimestamp, default);
    }

    /// <summary>Begin measuring an outer game update tick with safe coarse context.</summary>
    public void BeginTick(uint tick, long startTimestamp, ModHealthTickContext context)
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
            this.CurrentTickThreadId = Environment.CurrentManagedThreadId;
            this.CurrentTickErrors = 0;
            this.CurrentTickWarnings = 0;
            this.CurrentTickCallbackFailures = 0;
            this.CurrentTickContext = context with { ScreenId = Math.Max(0, context.ScreenId) };
            this.CurrentTickOmittedContributorIdentities = 0;
            this.CurrentTickMods.Clear();
            this.CurrentTickInstrumentedTimestampTicks = 0;
            this.CurrentTickGameUpdateTimestampTicks = 0;
            this.CurrentTickInstrumentedDuringGameUpdateTicks = 0;
            this.CurrentTickTimingPartitionIsInvalid = startTimestamp < this.SampleStartTimestamp;
            this.IsGameUpdateOpen = false;
            for (int generation = 0; generation < this.TickStartGcCollections.Length; generation++)
                this.TickStartGcCollections[generation] = this.GetGcCollectionCount(generation);
        }
    }

    /// <summary>Begin measuring a base game update within the current update tick.</summary>
    public void BeginGameUpdate()
    {
        if (!this.IsTracking)
            return;

        lock (this.SyncRoot)
        {
            if (!this.IsTracking || !this.IsTickOpen)
                return;
            if (Environment.CurrentManagedThreadId != this.CurrentTickThreadId || this.IsGameUpdateOpen)
            {
                this.CurrentTickTimingPartitionIsInvalid = true;
                return;
            }

            this.IsGameUpdateOpen = true;
            this.GameUpdateStartTimestamp = this.GetTimestamp();
        }
    }

    /// <summary>Finish measuring a base game update within the current update tick.</summary>
    public void EndGameUpdate()
    {
        lock (this.SyncRoot)
        {
            if (!this.IsTickOpen)
                return;
            if (Environment.CurrentManagedThreadId != this.CurrentTickThreadId || !this.IsGameUpdateOpen)
            {
                this.CurrentTickTimingPartitionIsInvalid = true;
                return;
            }

            this.IsGameUpdateOpen = false;
            this.CurrentTickGameUpdateTimestampTicks += this.GetTimestamp() - this.GameUpdateStartTimestamp;
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

            if (Environment.CurrentManagedThreadId != this.CurrentTickThreadId || this.IsGameUpdateOpen)
                this.CurrentTickTimingPartitionIsInvalid = true;

            long totalTimestampTicks = endTimestamp - this.CurrentTickStartTimestamp;
            long instrumentedTimestampTicks = this.CurrentTickInstrumentedTimestampTicks;
            string? slowestModId = null;
            string? slowestModName = null;
            long slowestModTimestampTicks = 0;

            foreach ((string modId, TickModCounter counter) in this.CurrentTickMods)
            {
                if (counter.TimestampTicks > slowestModTimestampTicks)
                {
                    slowestModId = modId;
                    slowestModName = counter.DisplayName;
                    slowestModTimestampTicks = counter.TimestampTicks;
                }
            }

            Span<int> gcCollections = stackalloc int[3];
            bool gcCollectionDataIsValid = true;
            for (int generation = 0; generation < gcCollections.Length; generation++)
            {
                long delta = (long)this.GetGcCollectionCount(generation) - this.TickStartGcCollections[generation];
                if (delta is < 0 or > int.MaxValue)
                    gcCollectionDataIsValid = false;
                gcCollections[generation] = delta is >= int.MinValue and <= int.MaxValue
                    ? (int)delta
                    : 0;
            }

            bool timingPartitionIsValid =
                !this.CurrentTickTimingPartitionIsInvalid
                && ModPerformanceManager.IsValidTimingPartition(
                    totalTimestampTicks,
                    this.CurrentTickGameUpdateTimestampTicks,
                    instrumentedTimestampTicks,
                    this.CurrentTickInstrumentedDuringGameUpdateTicks
                );

            TickPerformanceSnapshot sample = new(
                Tick: this.CurrentTick,
                TotalMilliseconds: this.ToMilliseconds(totalTimestampTicks),
                InstrumentedModMilliseconds: this.ToMilliseconds(instrumentedTimestampTicks),
                SlowestModId: slowestModId,
                SlowestModName: slowestModName,
                SlowestModMilliseconds: this.ToMilliseconds(slowestModTimestampTicks),
                ErrorCount: this.CurrentTickErrors,
                GameUpdateMilliseconds: this.ToMilliseconds(this.CurrentTickGameUpdateTimestampTicks),
                InstrumentedDuringGameUpdateMilliseconds: this.ToMilliseconds(this.CurrentTickInstrumentedDuringGameUpdateTicks),
                Gen0Collections: gcCollections[0],
                Gen1Collections: gcCollections[1],
                Gen2Collections: gcCollections[2],
                TimingPartitionIsValid: timingPartitionIsValid,
                GcCollectionDataIsValid: gcCollectionDataIsValid
            );

            long captureSequence = this.NextHealthTickSequence++;
            bool hasValidDuration = totalTimestampTicks >= 0;
            bool isSlowUpdate = hasValidDuration && this.ToMilliseconds(totalTimestampTicks) >= this.SlowUpdateThresholdMilliseconds;
            ModHealthTickContributorSnapshot[] contributors = isSlowUpdate
                ? this.GetTopCurrentTickContributors()
                : [];
            long omittedContributors = isSlowUpdate
                ? this.CurrentTickOmittedContributorIdentities + Math.Max(0, this.CurrentTickMods.Count - contributors.Length)
                : 0;
            if (isSlowUpdate)
            {
                this.SlowUpdateCount++;
                this.OmittedRetainedSlowTickContributors += omittedContributors;
            }

            ModHealthUpdatePerformanceSnapshot healthSample = new(
                CaptureSequence: captureSequence,
                Tick: this.CurrentTick,
                OffsetMilliseconds: this.ToMilliseconds(this.CurrentTickStartTimestamp - this.SampleStartTimestamp),
                TotalMilliseconds: this.ToMilliseconds(totalTimestampTicks),
                GameUpdateMilliseconds: this.ToMilliseconds(this.CurrentTickGameUpdateTimestampTicks),
                InstrumentedModMilliseconds: this.ToMilliseconds(instrumentedTimestampTicks),
                InstrumentedDuringGameUpdateMilliseconds: this.ToMilliseconds(this.CurrentTickInstrumentedDuringGameUpdateTicks),
                TimingPartitionIsValid: timingPartitionIsValid,
                Context: this.CurrentTickContext,
                WarningCount: this.CurrentTickWarnings,
                ErrorCount: this.CurrentTickErrors,
                CallbackFailureCount: this.CurrentTickCallbackFailures,
                Contributors: contributors,
                OmittedContributors: omittedContributors
            );

            if (hasValidDuration)
            {
                this.AddHealthHistogramValue(totalTimestampTicks);
                this.AddWorstHealthTick(healthSample);
                this.UpdateSlowEpisode(healthSample, isSlowUpdate);
            }
            else
                this.InvalidHistogramUpdates++;
            this.AddHealthTick(healthSample);

            this.TotalTickTimestampTicks += totalTimestampTicks;
            this.TotalGameUpdateTimestampTicks += this.CurrentTickGameUpdateTimestampTicks;
            this.TotalTickInstrumentedTimestampTicks += instrumentedTimestampTicks;
            this.TotalInstrumentedDuringGameUpdateTicks += this.CurrentTickInstrumentedDuringGameUpdateTicks;
            for (int generation = 0; generation < gcCollections.Length; generation++)
                this.TotalGcCollections[generation] += gcCollections[generation];
            if (!timingPartitionIsValid)
                this.InvalidTimingPartitionTicks++;
            if (!gcCollectionDataIsValid)
                this.InvalidGcCollectionTicks++;

            this.AddTick(sample);
            this.CompletedTicks++;
            this.IsTickOpen = false;
            this.IsGameUpdateOpen = false;
            this.CurrentTickThreadId = 0;
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
        return this.BeginHandler(modId, modName, eventName, handlerName, ModHealthExecutionPhase.Unscoped, ModPerformanceManager.GetOperationKind(eventName), onBehalfOfModId: null);
    }

    /// <summary>Begin timing one mod-owned operation with explicit health-report dimensions.</summary>
    public HandlerTimingToken BeginHandler(string modId, string modName, string eventName, string handlerName, ModHealthExecutionPhase phase, ModHealthOperationKind operation, string? onBehalfOfModId)
    {
        if (!this.IsTracking)
            return default;

        long generation = Volatile.Read(ref this.SampleGeneration);
        List<ActiveHandler> handlers = ModPerformanceManager.ActiveHandlers ??= new List<ActiveHandler>(8);
        int depth = handlers.Count;
        handlers.Add(new ActiveHandler(this, generation, modId, modName, eventName, handlerName, phase, operation, onBehalfOfModId, this.GetTimestamp(), 0));
        return new HandlerTimingToken(this, generation, depth);
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

    /// <summary>Begin timing one mod-owned operation with explicit health-report dimensions.</summary>
    public HandlerTimingToken BeginHandler(IModMetadata mod, string operationName, string handlerName, ModHealthExecutionPhase phase, ModHealthOperationKind operation, string? onBehalfOfModId)
    {
        string modId = mod.HasManifest()
            ? mod.Manifest.UniqueID
            : mod.DisplayName;

        return this.BeginHandler(modId, mod.DisplayName, operationName, handlerName, phase, operation, onBehalfOfModId);
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
        if (handler.Generation != token.Generation)
            return;
        handlers.RemoveAt(index);

        long elapsedTimestampTicks = Math.Max(0, this.GetTimestamp() - handler.StartTimestamp);
        long exclusiveTimestampTicks = Math.Max(0, elapsedTimestampTicks - handler.NestedTimestampTicks);

        if (handlers.Count > 0)
        {
            int parentIndex = handlers.Count - 1;
            ActiveHandler parent = handlers[parentIndex];
            if (ReferenceEquals(parent.Manager, this) && parent.Generation == handler.Generation)
            {
                parent.NestedTimestampTicks += elapsedTimestampTicks;
                handlers[parentIndex] = parent;
            }
        }

        this.RecordHandler(handler.ModId, handler.ModName, handler.EventName, handler.HandlerName, handler.Phase, handler.Operation, handler.OnBehalfOfModId, exclusiveTimestampTicks, failed, handler.Generation);
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
        this.RecordHandler(modId, modName, eventName, handlerName, ModHealthExecutionPhase.Unscoped, ModPerformanceManager.GetOperationKind(eventName), onBehalfOfModId: null, elapsedTimestampTicks, failed, requiredGeneration: null);
    }

    /// <summary>Record one invocation with explicit health-report dimensions.</summary>
    /// <param name="modId">The mod's unique ID.</param>
    /// <param name="modName">The mod's display name.</param>
    /// <param name="eventName">The managed event name.</param>
    /// <param name="handlerName">The registered handler method name.</param>
    /// <param name="phase">The execution phase.</param>
    /// <param name="operation">The operation kind.</param>
    /// <param name="onBehalfOfModId">The content-pack identity on whose behalf the callback ran, if known.</param>
    /// <param name="elapsedTimestampTicks">The elapsed timestamp ticks.</param>
    /// <param name="failed">Whether the handler threw an exception.</param>
    public void RecordHandler(string modId, string modName, string eventName, string handlerName, ModHealthExecutionPhase phase, ModHealthOperationKind operation, string? onBehalfOfModId, long elapsedTimestampTicks, bool failed)
    {
        this.RecordHandler(modId, modName, eventName, handlerName, phase, operation, onBehalfOfModId, elapsedTimestampTicks, failed, requiredGeneration: null);
    }

    /// <summary>Record one invocation of a mod-owned SMAPI event handler.</summary>
    /// <param name="modId">The mod's unique ID.</param>
    /// <param name="modName">The mod's display name.</param>
    /// <param name="eventName">The managed event name.</param>
    /// <param name="handlerName">The registered handler method name.</param>
    /// <param name="phase">The execution phase.</param>
    /// <param name="operation">The operation kind.</param>
    /// <param name="onBehalfOfModId">The content-pack identity on whose behalf the callback ran, if known.</param>
    /// <param name="elapsedTimestampTicks">The elapsed timestamp ticks.</param>
    /// <param name="failed">Whether the handler threw an exception.</param>
    /// <param name="requiredGeneration">The sample generation which must still be active, if any.</param>
    private void RecordHandler(string modId, string modName, string eventName, string handlerName, ModHealthExecutionPhase phase, ModHealthOperationKind operation, string? onBehalfOfModId, long elapsedTimestampTicks, bool failed, long? requiredGeneration)
    {
        if (!this.IsTracking)
            return;

        elapsedTimestampTicks = Math.Max(0, elapsedTimestampTicks);
        HandlerIdentity identity = new(modId, modName, eventName, handlerName, phase, operation, onBehalfOfModId);
        int currentThreadId = Environment.CurrentManagedThreadId;

        lock (this.SyncRoot)
        {
            if (!this.IsTracking || (requiredGeneration.HasValue && requiredGeneration.Value != this.SampleGeneration))
                return;

            if (this.IsTickOpen && currentThreadId == this.CurrentTickThreadId)
            {
                if (this.CurrentTickMods.TryGetValue(modId, out TickModCounter tickCounter))
                {
                    tickCounter.TimestampTicks += elapsedTimestampTicks;
                    this.CurrentTickMods[modId] = tickCounter;
                }
                else if (this.CurrentTickMods.Count < this.TickContributorIdentityCapacity)
                    this.CurrentTickMods[modId] = new TickModCounter(modName, elapsedTimestampTicks);
                else
                {
                    this.CurrentTickOmittedContributorIdentities++;
                    this.OmittedTickContributorIdentities++;
                }
                this.CurrentTickInstrumentedTimestampTicks += elapsedTimestampTicks;
                if (this.IsGameUpdateOpen)
                    this.CurrentTickInstrumentedDuringGameUpdateTicks += elapsedTimestampTicks;
                if (failed)
                    this.CurrentTickCallbackFailures++;
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
            {
                summary.WarningCount++;
                if (this.IsTickOpen && Environment.CurrentManagedThreadId == this.CurrentTickThreadId)
                    this.CurrentTickWarnings++;
            }
            if (isError)
            {
                summary.ErrorCount++;
                if (this.IsTickOpen && Environment.CurrentManagedThreadId == this.CurrentTickThreadId)
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
            Span<long> captureGcCollections = stackalloc long[3];
            bool captureGcCollectionDataIsValid = true;
            for (int generation = 0; generation < captureGcCollections.Length; generation++)
            {
                int currentCount = this.HasSampleEndGcCollections
                    ? this.SampleEndGcCollections[generation]
                    : this.GetGcCollectionCount(generation);
                captureGcCollections[generation] = (long)currentCount - this.SampleStartGcCollections[generation];
                if (captureGcCollections[generation] < 0)
                    captureGcCollectionDataIsValid = false;
            }

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
                TickLogThresholdMilliseconds: this.TickLogThresholdMilliseconds,
                TickTotalMilliseconds: this.ToMilliseconds(this.TotalTickTimestampTicks),
                GameUpdateMilliseconds: this.ToMilliseconds(this.TotalGameUpdateTimestampTicks),
                TickInstrumentedMilliseconds: this.ToMilliseconds(this.TotalTickInstrumentedTimestampTicks),
                InstrumentedDuringGameUpdateMilliseconds: this.ToMilliseconds(this.TotalInstrumentedDuringGameUpdateTicks),
                Gen0Collections: this.TotalGcCollections[0],
                Gen1Collections: this.TotalGcCollections[1],
                Gen2Collections: this.TotalGcCollections[2],
                TimingPartitionIsValid:
                    this.InvalidTimingPartitionTicks == 0
                    && ModPerformanceManager.IsValidTimingPartition(
                        this.TotalTickTimestampTicks,
                        this.TotalGameUpdateTimestampTicks,
                        this.TotalTickInstrumentedTimestampTicks,
                        this.TotalInstrumentedDuringGameUpdateTicks
                    ),
                InvalidTimingPartitionTickCount: this.InvalidTimingPartitionTicks,
                GcCollectionDataIsValid: this.InvalidGcCollectionTicks == 0,
                InvalidGcCollectionTickCount: this.InvalidGcCollectionTicks,
                CaptureGen0Collections: captureGcCollections[0],
                CaptureGen1Collections: captureGcCollections[1],
                CaptureGen2Collections: captureGcCollections[2],
                CaptureGcCollectionDataIsValid: captureGcCollectionDataIsValid,
                Health: this.GetHealthSnapshot()
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
    /// <summary>Build the immutable bounded health-oriented timing snapshot. The caller must hold <see cref="SyncRoot"/>.</summary>
    private ModHealthPerformanceSnapshot GetHealthSnapshot()
    {
        ModHealthCallbackPerformanceSnapshot[] callbacks = new ModHealthCallbackPerformanceSnapshot[this.HandlerCounters.Count];
        int callbackIndex = 0;
        foreach ((HandlerIdentity identity, HandlerCounter counter) in this.HandlerCounters)
        {
            callbacks[callbackIndex++] = new ModHealthCallbackPerformanceSnapshot(
                identity.ModId,
                identity.ModName,
                identity.Phase,
                identity.Operation,
                identity.EventName,
                identity.HandlerName,
                identity.OnBehalfOfModId,
                counter.CallCount,
                this.ToMilliseconds(counter.TotalTimestampTicks),
                this.ToMilliseconds(counter.MaximumTimestampTicks),
                counter.FailureCount
            );
        }
        Array.Sort(callbacks, ModHealthCallbackPerformanceSnapshotComparer.Instance);

        ModHealthUpdatePerformanceSnapshot[] recent = new ModHealthUpdatePerformanceSnapshot[this.HealthTickHistoryCount];
        for (int i = 0; i < recent.Length; i++)
            recent[i] = this.HealthTickHistory[(this.HealthTickHistoryStart + i) % this.HealthTickHistory.Length];

        ModHealthUpdatePerformanceSnapshot[] worst = new ModHealthUpdatePerformanceSnapshot[this.WorstHealthTickCount];
        Array.Copy(this.WorstHealthTicks, worst, worst.Length);

        ModHealthSlowEpisodeSnapshot[] episodes = this.GetRankedEpisodesIncludingOpen(out bool openEpisodeOmitted);

        long[] histogramBuckets = (long[])this.HealthHistogramBuckets.Clone();
        ModHealthTimingThresholdSnapshot[] thresholds = new ModHealthTimingThresholdSnapshot[HealthHistogramThresholds.Length];
        for (int i = 0; i < thresholds.Length; i++)
            thresholds[i] = new ModHealthTimingThresholdSnapshot(HealthHistogramThresholds[i], this.HealthThresholdCounts[i]);

        ModHealthTimingHistogramSnapshot histogram = new(
            Buckets: histogramBuckets,
            Count: this.HealthHistogramCount,
            SumMilliseconds: this.ToMilliseconds(this.HealthHistogramTotalTimestampTicks),
            MinimumMilliseconds: this.HealthHistogramCount > 0 ? this.ToMilliseconds(this.HealthHistogramMinimumTimestampTicks) : null,
            MaximumMilliseconds: this.HealthHistogramCount > 0 ? this.ToMilliseconds(this.HealthHistogramMaximumTimestampTicks) : null,
            UnderflowCount: this.HealthHistogramUnderflowCount,
            OverflowCount: this.HealthHistogramOverflowCount,
            Thresholds: thresholds,
            P50Milliseconds: this.GetHealthHistogramPercentile(0.50),
            P95Milliseconds: this.GetHealthHistogramPercentile(0.95),
            P99Milliseconds: this.GetHealthHistogramPercentile(0.99),
            MaximumRelativeBucketError: Math.Pow(2, 1d / ModHealthTimingHistogramSnapshot.SubBucketsPerPowerOfTwo) - 1
        );

        return new ModHealthPerformanceSnapshot(
            SlowUpdateThresholdMilliseconds: this.SlowUpdateThresholdMilliseconds,
            SlowUpdateCount: this.SlowUpdateCount,
            Callbacks: callbacks,
            RecentUpdates: recent,
            WorstUpdates: worst,
            Episodes: episodes,
            Histogram: histogram,
            Capacities: new ModHealthTimingCapacities(
                RecentUpdates: this.HealthTickHistory.Length,
                WorstUpdates: this.WorstHealthTicks.Length,
                SlowEpisodes: this.SlowEpisodes.Length,
                ContributorsPerSlowUpdate: this.SlowTickContributorCapacity,
                ContributorIdentitiesPerUpdate: this.TickContributorIdentityCapacity,
                CallbackIdentities: this.HandlerCapacity
            ),
            Omissions: new ModHealthTimingOmissions(
                RecentUpdates: Math.Max(0, this.CompletedTicks - this.HealthTickHistoryCount),
                WorstUpdates: Math.Max(0, this.HealthHistogramCount - this.WorstHealthTickCount),
                SlowEpisodes: this.OmittedSlowEpisodes + (openEpisodeOmitted ? 1 : 0),
                ContributorIdentities: this.OmittedTickContributorIdentities,
                ContributorsFromRetainedSlowUpdates: this.OmittedRetainedSlowTickContributors,
                CallbackInvocations: this.OmittedHandlerInvocations,
                InvalidHistogramUpdates: this.InvalidHistogramUpdates
            )
        );
    }

    /// <summary>Add a valid update duration to the fixed histogram.</summary>
    private void AddHealthHistogramValue(long timestampTicks)
    {
        double milliseconds = this.ToMilliseconds(timestampTicks);
        this.HealthHistogramCount++;
        this.HealthHistogramTotalTimestampTicks += timestampTicks;
        if (this.HealthHistogramCount == 1)
            this.HealthHistogramMinimumTimestampTicks = this.HealthHistogramMaximumTimestampTicks = timestampTicks;
        else
        {
            this.HealthHistogramMinimumTimestampTicks = Math.Min(this.HealthHistogramMinimumTimestampTicks, timestampTicks);
            this.HealthHistogramMaximumTimestampTicks = Math.Max(this.HealthHistogramMaximumTimestampTicks, timestampTicks);
        }

        for (int i = 0; i < HealthHistogramThresholds.Length; i++)
        {
            if (milliseconds >= HealthHistogramThresholds[i])
                this.HealthThresholdCounts[i]++;
        }

        if (milliseconds < ModHealthTimingHistogramSnapshot.MinimumBucketMilliseconds)
        {
            this.HealthHistogramUnderflowCount++;
            return;
        }
        if (milliseconds > ModHealthTimingHistogramSnapshot.MaximumBucketMilliseconds)
        {
            this.HealthHistogramOverflowCount++;
            return;
        }

        int bucket = milliseconds == ModHealthTimingHistogramSnapshot.MaximumBucketMilliseconds
            ? ModHealthTimingHistogramSnapshot.BucketCount - 1
            : (int)Math.Floor(
                Math.Log2(milliseconds / ModHealthTimingHistogramSnapshot.MinimumBucketMilliseconds)
                * ModHealthTimingHistogramSnapshot.SubBucketsPerPowerOfTwo
            );
        this.HealthHistogramBuckets[Math.Clamp(bucket, 0, ModHealthTimingHistogramSnapshot.BucketCount - 1)]++;
    }

    /// <summary>Get an approximate percentile as the selected logarithmic bucket's upper bound.</summary>
    private double? GetHealthHistogramPercentile(double percentile)
    {
        if (this.HealthHistogramCount == 0)
            return null;

        long rank = (long)Math.Ceiling(percentile * this.HealthHistogramCount);
        long cumulative = this.HealthHistogramUnderflowCount;
        if (rank <= cumulative)
            return ModHealthTimingHistogramSnapshot.MinimumBucketMilliseconds;

        for (int i = 0; i < this.HealthHistogramBuckets.Length; i++)
        {
            cumulative += this.HealthHistogramBuckets[i];
            if (rank <= cumulative)
            {
                return ModHealthTimingHistogramSnapshot.MinimumBucketMilliseconds
                    * Math.Pow(2, (i + 1d) / ModHealthTimingHistogramSnapshot.SubBucketsPerPowerOfTwo);
            }
        }

        return this.ToMilliseconds(this.HealthHistogramMaximumTimestampTicks);
    }

    /// <summary>Retain one update in the recent health ring.</summary>
    private void AddHealthTick(ModHealthUpdatePerformanceSnapshot sample)
    {
        int index;
        if (this.HealthTickHistoryCount < this.HealthTickHistory.Length)
        {
            index = (this.HealthTickHistoryStart + this.HealthTickHistoryCount) % this.HealthTickHistory.Length;
            this.HealthTickHistoryCount++;
        }
        else
        {
            index = this.HealthTickHistoryStart;
            this.HealthTickHistoryStart = (this.HealthTickHistoryStart + 1) % this.HealthTickHistory.Length;
        }
        this.HealthTickHistory[index] = sample;
    }

    /// <summary>Retain one update in the independently ranked whole-capture worst list.</summary>
    private void AddWorstHealthTick(ModHealthUpdatePerformanceSnapshot sample)
    {
        int insertAt = 0;
        while (insertAt < this.WorstHealthTickCount && ModPerformanceManager.CompareWorstTicks(this.WorstHealthTicks[insertAt], sample) <= 0)
            insertAt++;
        if (insertAt >= this.WorstHealthTicks.Length)
            return;

        int oldCount = this.WorstHealthTickCount;
        if (this.WorstHealthTickCount < this.WorstHealthTicks.Length)
            this.WorstHealthTickCount++;
        int shiftCount = Math.Min(oldCount, this.WorstHealthTicks.Length - 1) - insertAt;
        if (shiftCount > 0)
            Array.Copy(this.WorstHealthTicks, insertAt, this.WorstHealthTicks, insertAt + 1, shiftCount);
        this.WorstHealthTicks[insertAt] = sample;
    }

    /// <summary>Update the streaming slow-episode state for one completed update.</summary>
    private void UpdateSlowEpisode(ModHealthUpdatePerformanceSnapshot sample, bool isSlowUpdate)
    {
        if (isSlowUpdate)
        {
            if (!this.HasOpenSlowEpisode)
            {
                this.HasOpenSlowEpisode = true;
                this.OpenSlowEpisode = new MutableSlowEpisode(sample);
            }
            else
                this.OpenSlowEpisode.Add(sample);
            this.OpenEpisodeTrailingFastUpdates = 0;
            return;
        }

        if (this.HasOpenSlowEpisode && ++this.OpenEpisodeTrailingFastUpdates >= 2)
            this.CloseOpenSlowEpisode();
    }

    /// <summary>Close and rank the current slow episode, if any.</summary>
    private void CloseOpenSlowEpisode()
    {
        if (!this.HasOpenSlowEpisode)
            return;

        this.AddSlowEpisode(this.OpenSlowEpisode.ToSnapshot());
        this.HasOpenSlowEpisode = false;
        this.OpenSlowEpisode = default;
        this.OpenEpisodeTrailingFastUpdates = 0;
    }

    /// <summary>Add a completed episode to the bounded deterministic ranking.</summary>
    private void AddSlowEpisode(ModHealthSlowEpisodeSnapshot episode)
    {
        int insertAt = 0;
        while (insertAt < this.SlowEpisodeCount && ModPerformanceManager.CompareEpisodes(this.SlowEpisodes[insertAt], episode) <= 0)
            insertAt++;

        if (this.SlowEpisodeCount >= this.SlowEpisodes.Length)
            this.OmittedSlowEpisodes++;
        if (insertAt >= this.SlowEpisodes.Length)
            return;

        int oldCount = this.SlowEpisodeCount;
        if (this.SlowEpisodeCount < this.SlowEpisodes.Length)
            this.SlowEpisodeCount++;
        int shiftCount = Math.Min(oldCount, this.SlowEpisodes.Length - 1) - insertAt;
        if (shiftCount > 0)
            Array.Copy(this.SlowEpisodes, insertAt, this.SlowEpisodes, insertAt + 1, shiftCount);
        this.SlowEpisodes[insertAt] = episode;
    }

    /// <summary>Copy ranked completed episodes and project the open episode without mutating the collector.</summary>
    private ModHealthSlowEpisodeSnapshot[] GetRankedEpisodesIncludingOpen(out bool openEpisodeOmitted)
    {
        openEpisodeOmitted = false;
        if (!this.HasOpenSlowEpisode)
        {
            ModHealthSlowEpisodeSnapshot[] closed = new ModHealthSlowEpisodeSnapshot[this.SlowEpisodeCount];
            Array.Copy(this.SlowEpisodes, closed, closed.Length);
            return closed;
        }

        ModHealthSlowEpisodeSnapshot open = this.OpenSlowEpisode.ToSnapshot();
        int insertAt = 0;
        while (insertAt < this.SlowEpisodeCount && ModPerformanceManager.CompareEpisodes(this.SlowEpisodes[insertAt], open) <= 0)
            insertAt++;
        if (insertAt >= this.SlowEpisodes.Length)
        {
            openEpisodeOmitted = true;
            ModHealthSlowEpisodeSnapshot[] unchanged = new ModHealthSlowEpisodeSnapshot[this.SlowEpisodeCount];
            Array.Copy(this.SlowEpisodes, unchanged, unchanged.Length);
            return unchanged;
        }

        int count = Math.Min(this.SlowEpisodeCount + 1, this.SlowEpisodes.Length);
        ModHealthSlowEpisodeSnapshot[] result = new ModHealthSlowEpisodeSnapshot[count];
        if (insertAt > 0)
            Array.Copy(this.SlowEpisodes, 0, result, 0, insertAt);
        result[insertAt] = open;
        int after = count - insertAt - 1;
        if (after > 0)
            Array.Copy(this.SlowEpisodes, insertAt, result, insertAt + 1, after);
        if (this.SlowEpisodeCount >= this.SlowEpisodes.Length)
            openEpisodeOmitted = true;
        return result;
    }

    /// <summary>Get the deterministic top contributors for the current slow update.</summary>
    private ModHealthTickContributorSnapshot[] GetTopCurrentTickContributors()
    {
        int capacity = Math.Min(this.SlowTickContributorCapacity, this.CurrentTickMods.Count);
        if (capacity == 0)
            return [];

        ModHealthTickContributorSnapshot[] result = new ModHealthTickContributorSnapshot[capacity];
        int count = 0;
        foreach ((string modId, TickModCounter counter) in this.CurrentTickMods)
        {
            ModHealthTickContributorSnapshot candidate = new(modId, counter.DisplayName, this.ToMilliseconds(counter.TimestampTicks));
            int insertAt = 0;
            while (insertAt < count && ModPerformanceManager.CompareContributors(result[insertAt], candidate) <= 0)
                insertAt++;
            if (insertAt >= capacity)
                continue;

            int shiftCount = Math.Min(count, capacity - 1) - insertAt;
            if (shiftCount > 0)
                Array.Copy(result, insertAt, result, insertAt + 1, shiftCount);
            result[insertAt] = candidate;
            if (count < capacity)
                count++;
        }
        return result;
    }

    /// <summary>Compare worst updates in desired ranking order.</summary>
    private static int CompareWorstTicks(ModHealthUpdatePerformanceSnapshot left, ModHealthUpdatePerformanceSnapshot right)
    {
        int compare = right.TotalMilliseconds.CompareTo(left.TotalMilliseconds);
        return compare != 0 ? compare : left.CaptureSequence.CompareTo(right.CaptureSequence);
    }

    /// <summary>Compare slow episodes in desired ranking order.</summary>
    private static int CompareEpisodes(ModHealthSlowEpisodeSnapshot left, ModHealthSlowEpisodeSnapshot right)
    {
        int compare = right.MaximumMilliseconds.CompareTo(left.MaximumMilliseconds);
        if (compare != 0)
            return compare;
        compare = right.SummedQualifyingMilliseconds.CompareTo(left.SummedQualifyingMilliseconds);
        return compare != 0 ? compare : left.FirstCaptureSequence.CompareTo(right.FirstCaptureSequence);
    }

    /// <summary>Compare contributors in desired ranking order.</summary>
    private static int CompareContributors(ModHealthTickContributorSnapshot left, ModHealthTickContributorSnapshot right)
    {
        int compare = right.Milliseconds.CompareTo(left.Milliseconds);
        if (compare != 0)
            return compare;
        compare = StringComparer.OrdinalIgnoreCase.Compare(left.ModId, right.ModId);
        return compare != 0 ? compare : StringComparer.Ordinal.Compare(left.ModId, right.ModId);
    }

    /// <summary>Infer an operation kind for legacy call sites which don't provide one explicitly.</summary>
    private static ModHealthOperationKind GetOperationKind(string eventName)
    {
        if (eventName.StartsWith("Content.Load", StringComparison.Ordinal))
            return ModHealthOperationKind.ContentLoad;
        if (eventName.StartsWith("Content.Edit", StringComparison.Ordinal))
            return ModHealthOperationKind.ContentEdit;
        if (eventName.StartsWith("ConsoleCommand.", StringComparison.Ordinal))
            return ModHealthOperationKind.Console;
        if (eventName.StartsWith("ModLifecycle.Entry", StringComparison.Ordinal))
            return ModHealthOperationKind.Entry;
        if (eventName.StartsWith("ModLifecycle.GetApi", StringComparison.Ordinal))
            return ModHealthOperationKind.GetApi;
        return ModHealthOperationKind.Event;
    }

    /// <summary>Reset all mutable sample data. The caller must hold <see cref="SyncRoot"/> unless constructing the instance.</summary>
    /// <param name="timestamp">The current timestamp.</param>
    private void ResetCore(long timestamp)
    {
        Interlocked.Increment(ref this.SampleGeneration);
        this.HandlerCounters.Clear();
        this.ModLogs.Clear();
        this.CurrentTickMods.Clear();
        Array.Clear(this.TickHistory);
        Array.Clear(this.HealthTickHistory);
        Array.Clear(this.WorstHealthTicks);
        Array.Clear(this.SlowEpisodes);
        Array.Clear(this.HealthHistogramBuckets);
        Array.Clear(this.HealthThresholdCounts);
        this.TickHistoryStart = 0;
        this.TickHistoryCount = 0;
        this.HealthTickHistoryStart = 0;
        this.HealthTickHistoryCount = 0;
        this.WorstHealthTickCount = 0;
        this.SlowEpisodeCount = 0;
        this.CompletedTicks = 0;
        this.NextHealthTickSequence = 0;
        this.OmittedHandlerInvocations = 0;
        this.OmittedTickContributorIdentities = 0;
        this.OmittedRetainedSlowTickContributors = 0;
        this.OmittedSlowEpisodes = 0;
        this.InvalidHistogramUpdates = 0;
        this.SampleStartedUtc = DateTime.UtcNow;
        this.SampleStartTimestamp = timestamp;
        for (int generation = 0; generation < this.SampleStartGcCollections.Length; generation++)
            this.SampleStartGcCollections[generation] = this.GetGcCollectionCount(generation);
        this.HasSampleEndGcCollections = false;
        this.IsTickOpen = false;
        this.CurrentTickThreadId = 0;
        this.CurrentTickErrors = 0;
        this.CurrentTickWarnings = 0;
        this.CurrentTickCallbackFailures = 0;
        this.CurrentTickContext = default;
        this.CurrentTickOmittedContributorIdentities = 0;
        this.CurrentTickInstrumentedTimestampTicks = 0;
        this.CurrentTickGameUpdateTimestampTicks = 0;
        this.CurrentTickInstrumentedDuringGameUpdateTicks = 0;
        this.CurrentTickTimingPartitionIsInvalid = false;
        this.IsGameUpdateOpen = false;
        this.TotalTickTimestampTicks = 0;
        this.TotalGameUpdateTimestampTicks = 0;
        this.TotalTickInstrumentedTimestampTicks = 0;
        this.TotalInstrumentedDuringGameUpdateTicks = 0;
        Array.Clear(this.TotalGcCollections);
        this.InvalidTimingPartitionTicks = 0;
        this.InvalidGcCollectionTicks = 0;
        this.SlowUpdateCount = 0;
        this.HasOpenSlowEpisode = false;
        this.OpenSlowEpisode = default;
        this.OpenEpisodeTrailingFastUpdates = 0;
        this.HealthHistogramCount = 0;
        this.HealthHistogramTotalTimestampTicks = 0;
        this.HealthHistogramMinimumTimestampTicks = 0;
        this.HealthHistogramMaximumTimestampTicks = 0;
        this.HealthHistogramUnderflowCount = 0;
        this.HealthHistogramOverflowCount = 0;
    }

    /// <summary>Get whether raw timing values form a valid non-overlapping tick partition.</summary>
    private static bool IsValidTimingPartition(long totalTicks, long gameUpdateTicks, long instrumentedTicks, long instrumentedDuringGameUpdateTicks)
    {
        return
            totalTicks >= 0
            && gameUpdateTicks >= 0
            && instrumentedTicks >= 0
            && instrumentedDuringGameUpdateTicks >= 0
            && instrumentedDuringGameUpdateTicks <= gameUpdateTicks
            && instrumentedDuringGameUpdateTicks <= instrumentedTicks
            && gameUpdateTicks <= totalTicks
            && instrumentedTicks - instrumentedDuringGameUpdateTicks <= totalTicks - gameUpdateTicks;
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
    private readonly record struct HandlerIdentity(
        string ModId,
        string ModName,
        string EventName,
        string HandlerName,
        ModHealthExecutionPhase Phase,
        ModHealthOperationKind Operation,
        string? OnBehalfOfModId
    );

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
    private struct ActiveHandler(ModPerformanceManager manager, long generation, string modId, string modName, string eventName, string handlerName, ModHealthExecutionPhase phase, ModHealthOperationKind operation, string? onBehalfOfModId, long startTimestamp, long nestedTimestampTicks)
    {
        public ModPerformanceManager Manager { get; } = manager;
        public long Generation { get; } = generation;
        public string ModId { get; } = modId;
        public string ModName { get; } = modName;
        public string EventName { get; } = eventName;
        public string HandlerName { get; } = handlerName;
        public ModHealthExecutionPhase Phase { get; } = phase;
        public ModHealthOperationKind Operation { get; } = operation;
        public string? OnBehalfOfModId { get; } = onBehalfOfModId;
        public long StartTimestamp { get; } = startTimestamp;
        public long NestedTimestampTicks = nestedTimestampTicks;
    }

    /// <summary>One mutable streaming slow-update episode.</summary>
    private struct MutableSlowEpisode
    {
        public long FirstCaptureSequence;
        public long LastCaptureSequence;
        public uint FirstTick;
        public uint LastTick;
        public int QualifyingUpdateCount;
        public double MaximumMilliseconds;
        public double SummedQualifyingMilliseconds;
        public uint RepresentativeTick;

        public MutableSlowEpisode(ModHealthUpdatePerformanceSnapshot first)
        {
            this.FirstCaptureSequence = first.CaptureSequence;
            this.LastCaptureSequence = first.CaptureSequence;
            this.FirstTick = first.Tick;
            this.LastTick = first.Tick;
            this.QualifyingUpdateCount = 1;
            this.MaximumMilliseconds = first.TotalMilliseconds;
            this.SummedQualifyingMilliseconds = first.TotalMilliseconds;
            this.RepresentativeTick = first.Tick;
        }

        public void Add(ModHealthUpdatePerformanceSnapshot sample)
        {
            this.LastCaptureSequence = sample.CaptureSequence;
            this.LastTick = sample.Tick;
            this.QualifyingUpdateCount++;
            this.SummedQualifyingMilliseconds += sample.TotalMilliseconds;
            if (sample.TotalMilliseconds > this.MaximumMilliseconds)
            {
                this.MaximumMilliseconds = sample.TotalMilliseconds;
                this.RepresentativeTick = sample.Tick;
            }
        }

        public ModHealthSlowEpisodeSnapshot ToSnapshot()
        {
            return new ModHealthSlowEpisodeSnapshot(
                this.FirstCaptureSequence,
                this.LastCaptureSequence,
                this.FirstTick,
                this.LastTick,
                this.QualifyingUpdateCount,
                this.MaximumMilliseconds,
                this.SummedQualifyingMilliseconds,
                this.RepresentativeTick
            );
        }
    }

    /// <summary>Deterministic callback snapshot ordering.</summary>
    private sealed class ModHealthCallbackPerformanceSnapshotComparer : IComparer<ModHealthCallbackPerformanceSnapshot>
    {
        public static ModHealthCallbackPerformanceSnapshotComparer Instance { get; } = new();

        public int Compare(ModHealthCallbackPerformanceSnapshot left, ModHealthCallbackPerformanceSnapshot right)
        {
            int compare = StringComparer.OrdinalIgnoreCase.Compare(left.ModId, right.ModId);
            if (compare != 0)
                return compare;
            compare = StringComparer.Ordinal.Compare(left.ModId, right.ModId);
            if (compare != 0)
                return compare;
            compare = left.Phase.CompareTo(right.Phase);
            if (compare != 0)
                return compare;
            compare = left.Operation.CompareTo(right.Operation);
            if (compare != 0)
                return compare;
            compare = StringComparer.Ordinal.Compare(left.OnBehalfOfModId, right.OnBehalfOfModId);
            if (compare != 0)
                return compare;
            compare = StringComparer.Ordinal.Compare(left.EventName, right.EventName);
            return compare != 0 ? compare : StringComparer.Ordinal.Compare(left.CallbackName, right.CallbackName);
        }
    }
}

/// <summary>An opaque token for one active profiled handler invocation.</summary>
internal readonly struct HandlerTimingToken
{
    /// <summary>The manager which created this token.</summary>
    private readonly ModPerformanceManager? Manager;

    /// <summary>The sample generation in which the invocation began.</summary>
    internal long Generation { get; }

    /// <summary>The handler's zero-based nesting depth.</summary>
    internal int Depth { get; }

    /// <summary>Construct an instance.</summary>
    public HandlerTimingToken(ModPerformanceManager manager, long generation, int depth)
    {
        this.Manager = manager;
        this.Generation = generation;
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
    double TickLogThresholdMilliseconds,
    double TickTotalMilliseconds = 0,
    double GameUpdateMilliseconds = 0,
    double TickInstrumentedMilliseconds = 0,
    double InstrumentedDuringGameUpdateMilliseconds = 0,
    long Gen0Collections = 0,
    long Gen1Collections = 0,
    long Gen2Collections = 0,
    bool TimingPartitionIsValid = true,
    long InvalidTimingPartitionTickCount = 0,
    bool GcCollectionDataIsValid = true,
    long InvalidGcCollectionTickCount = 0,
    long CaptureGen0Collections = 0,
    long CaptureGen1Collections = 0,
    long CaptureGen2Collections = 0,
    bool CaptureGcCollectionDataIsValid = true,
    ModHealthPerformanceSnapshot? Health = null
)
{
    /// <summary>Base game update time in completed ticks, excluding instrumented mod callbacks which ran within it.</summary>
    public double GameUpdateExclusiveMilliseconds => this.GameUpdateMilliseconds - this.InstrumentedDuringGameUpdateMilliseconds;

    /// <summary>Tick time outside both the base game update and instrumented mod callbacks, such as SMAPI's own dispatch and per-tick framework work.</summary>
    public double OutsideGameUpdateMilliseconds => this.TickTotalMilliseconds - this.GameUpdateMilliseconds - (this.TickInstrumentedMilliseconds - this.InstrumentedDuringGameUpdateMilliseconds);
}

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
    int ErrorCount,
    double GameUpdateMilliseconds = 0,
    double InstrumentedDuringGameUpdateMilliseconds = 0,
    int Gen0Collections = 0,
    int Gen1Collections = 0,
    int Gen2Collections = 0,
    bool TimingPartitionIsValid = true,
    bool GcCollectionDataIsValid = true
)
{
    /// <summary>Time in the update which wasn't observed inside a SMAPI-managed mod event handler.</summary>
    public double UnattributedMilliseconds => this.TotalMilliseconds - this.InstrumentedModMilliseconds;

    /// <summary>Base game update time excluding instrumented mod callbacks which ran within it. This can include Harmony patches and other unobserved work invoked by the game.</summary>
    public double GameUpdateExclusiveMilliseconds => this.GameUpdateMilliseconds - this.InstrumentedDuringGameUpdateMilliseconds;

    /// <summary>Tick time outside both the base game update and instrumented mod callbacks, such as SMAPI's own dispatch and per-tick framework work.</summary>
    public double OutsideGameUpdateMilliseconds => this.TotalMilliseconds - this.GameUpdateMilliseconds - (this.InstrumentedModMilliseconds - this.InstrumentedDuringGameUpdateMilliseconds);
}
