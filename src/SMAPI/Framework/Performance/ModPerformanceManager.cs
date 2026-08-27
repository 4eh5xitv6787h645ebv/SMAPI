using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using StardewModdingAPI.Framework.Health;

namespace StardewModdingAPI.Framework.Performance;

/// <summary>Collects bounded performance and error diagnostics for mod-owned execution observed by SMAPI.</summary>
internal sealed class ModPerformanceManager
{
    private const double DefaultSlowUpdateThresholdMilliseconds = ModHealthReportLimits.SlowUpdateMilliseconds;
    private static readonly double[] HealthHistogramThresholds = [16.667, 33.333, 50, 100, 250, 500, 1000];

    /// <summary>The mutually exclusive owned update domain active on the main update thread.</summary>
    internal enum UpdateTimingDomain
    {
        Unowned,
        Game,
        Smapi
    }

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

    /// <summary>The number of callback observations omitted from per-update contributor attribution after its identity cap was reached.</summary>
    private long OmittedTickContributorObservations;

    /// <summary>The total number of tracked contributor identities omitted from generated slow-update top lists.</summary>
    private long OmittedSlowTickContributorIdentities;

    /// <summary>The number of completed slow episodes omitted from the ranked bounded list.</summary>
    private long OmittedSlowEpisodes;

    /// <summary>The number of invalid update durations omitted from the histogram.</summary>
    private long InvalidHistogramUpdates;

    /// <summary>The UTC time when the current sample began.</summary>
    private DateTime SampleStartedUtc;

    /// <summary>The timestamp when the current sample began.</summary>
    private long SampleStartTimestamp;

    /// <summary>The frozen timestamp when the current sample stopped.</summary>
    private long SampleEndTimestamp;

    /// <summary>Whether <see cref="SampleEndTimestamp"/> contains a frozen stop boundary.</summary>
    private bool HasSampleEndTimestamp;

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

    /// <summary>The number of callback observations omitted from the current update's contributor attribution after its identity cap was reached.</summary>
    private long CurrentTickOmittedContributorObservations;

    /// <summary>The instrumented handler time recorded during the current update tick.</summary>
    private long CurrentTickInstrumentedTimestampTicks;

    /// <summary>The base game update time recorded during the current update tick.</summary>
    private long CurrentTickGameUpdateTimestampTicks;

    /// <summary>The instrumented handler time recorded while a base game update was executing during the current update tick.</summary>
    private long CurrentTickInstrumentedDuringGameUpdateTicks;

    /// <summary>The separately measured SMAPI update time recorded during the current update tick.</summary>
    private long CurrentTickSmapiUpdateTimestampTicks;

    /// <summary>The instrumented handler time recorded while a SMAPI update scope was executing.</summary>
    private long CurrentTickInstrumentedDuringSmapiUpdateTicks;

    /// <summary>The mutually exclusive update timing domain currently open.</summary>
    private UpdateTimingDomain CurrentTickUpdateDomain;

    /// <summary>The timestamp when the current owned update domain began.</summary>
    private long CurrentUpdateDomainStartTimestamp;

    /// <summary>Whether at least one valid SMAPI update scope began during the current tick.</summary>
    private bool CurrentTickObservedSmapiUpdateScope;

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

    /// <summary>The total separately measured SMAPI update time in this sample.</summary>
    private long TotalSmapiUpdateTimestampTicks;

    /// <summary>The total instrumented handler time measured within SMAPI update scopes in this sample.</summary>
    private long TotalInstrumentedDuringSmapiUpdateTicks;

    /// <summary>The number of completed ticks for which separately measured SMAPI timing was unavailable.</summary>
    private long UnavailableSmapiUpdateTimingTicks;

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
            this.SlowUpdateThresholdMilliseconds = DefaultSlowUpdateThresholdMilliseconds;
            Volatile.Write(ref this.TrackingEnabled, 1);
        }
    }

    /// <summary>Stop performance sampling while retaining the current results.</summary>
    public void Stop()
    {
        lock (this.SyncRoot)
        {
            bool wasTracking = Interlocked.Exchange(ref this.TrackingEnabled, 0) != 0;
            if (wasTracking && !this.HasSampleEndTimestamp)
            {
                this.SampleEndTimestamp = Math.Max(this.SampleStartTimestamp, this.GetTimestamp());
                this.HasSampleEndTimestamp = true;
                for (int generation = 0; generation < this.SampleEndGcCollections.Length; generation++)
                    this.SampleEndGcCollections[generation] = this.GetGcCollectionCount(generation);
                this.HasSampleEndGcCollections = true;
            }
            this.CloseOpenSlowEpisode();
            this.IsTickOpen = false;
            this.CurrentTickUpdateDomain = UpdateTimingDomain.Unowned;
            this.CurrentUpdateDomainStartTimestamp = 0;
            this.CurrentTickObservedSmapiUpdateScope = false;
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
            this.CurrentTickOmittedContributorObservations = 0;
            this.CurrentTickMods.Clear();
            this.CurrentTickInstrumentedTimestampTicks = 0;
            this.CurrentTickGameUpdateTimestampTicks = 0;
            this.CurrentTickInstrumentedDuringGameUpdateTicks = 0;
            this.CurrentTickSmapiUpdateTimestampTicks = 0;
            this.CurrentTickInstrumentedDuringSmapiUpdateTicks = 0;
            this.CurrentTickUpdateDomain = UpdateTimingDomain.Unowned;
            this.CurrentUpdateDomainStartTimestamp = 0;
            this.CurrentTickObservedSmapiUpdateScope = false;
            this.CurrentTickTimingPartitionIsInvalid = startTimestamp < this.SampleStartTimestamp;
            for (int generation = 0; generation < this.TickStartGcCollections.Length; generation++)
                this.TickStartGcCollections[generation] = this.GetGcCollectionCount(generation);
        }
    }

    /// <summary>Begin measuring a base game update within the current update tick.</summary>
    public void BeginGameUpdate()
    {
        this.BeginUpdateDomain(UpdateTimingDomain.Game);
    }

    /// <summary>Finish measuring a base game update within the current update tick.</summary>
    public void EndGameUpdate()
    {
        this.EndUpdateDomain(UpdateTimingDomain.Game);
    }

    /// <summary>Begin measuring a SMAPI-owned update scope within the current update tick.</summary>
    public void BeginSmapiUpdate()
    {
        this.BeginUpdateDomain(UpdateTimingDomain.Smapi);
    }

    /// <summary>Finish measuring a SMAPI-owned update scope within the current update tick.</summary>
    public void EndSmapiUpdate()
    {
        this.EndUpdateDomain(UpdateTimingDomain.Smapi);
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

            if (Environment.CurrentManagedThreadId != this.CurrentTickThreadId || this.CurrentTickUpdateDomain != UpdateTimingDomain.Unowned)
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
                    this.CurrentTickInstrumentedDuringGameUpdateTicks,
                    this.CurrentTickSmapiUpdateTimestampTicks,
                    this.CurrentTickInstrumentedDuringSmapiUpdateTicks
                );
            bool smapiUpdateTimingAvailable = timingPartitionIsValid && this.CurrentTickObservedSmapiUpdateScope;

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
                GcCollectionDataIsValid: gcCollectionDataIsValid,
                SmapiUpdateMilliseconds: this.ToMilliseconds(this.CurrentTickSmapiUpdateTimestampTicks),
                InstrumentedDuringSmapiUpdateMilliseconds: this.ToMilliseconds(this.CurrentTickInstrumentedDuringSmapiUpdateTicks),
                SmapiUpdateTimingAvailable: smapiUpdateTimingAvailable
            );

            long captureSequence = this.NextHealthTickSequence++;
            bool hasValidDuration = totalTimestampTicks >= 0;
            bool isSlowUpdate = hasValidDuration && this.ToMilliseconds(totalTimestampTicks) >= this.SlowUpdateThresholdMilliseconds;
            ModHealthTickContributorSnapshot[] contributors = isSlowUpdate
                ? this.GetTopCurrentTickContributors()
                : [];
            long omittedContributorIdentities = isSlowUpdate
                ? Math.Max(0, this.CurrentTickMods.Count - contributors.Length)
                : 0;
            long omittedContributorObservations = this.CurrentTickOmittedContributorObservations;
            if (isSlowUpdate)
            {
                this.SlowUpdateCount++;
                this.OmittedSlowTickContributorIdentities += omittedContributorIdentities;
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
                Gen0Collections: gcCollections[0],
                Gen1Collections: gcCollections[1],
                Gen2Collections: gcCollections[2],
                GcCollectionDataIsValid: gcCollectionDataIsValid,
                Contributors: contributors,
                OmittedContributorIdentities: omittedContributorIdentities,
                OmittedContributorObservations: omittedContributorObservations,
                SmapiUpdateMilliseconds: this.ToMilliseconds(this.CurrentTickSmapiUpdateTimestampTicks),
                InstrumentedDuringSmapiUpdateMilliseconds: this.ToMilliseconds(this.CurrentTickInstrumentedDuringSmapiUpdateTicks),
                SmapiUpdateTimingAvailable: smapiUpdateTimingAvailable
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
            this.TotalSmapiUpdateTimestampTicks += this.CurrentTickSmapiUpdateTimestampTicks;
            this.TotalInstrumentedDuringSmapiUpdateTicks += this.CurrentTickInstrumentedDuringSmapiUpdateTicks;
            for (int generation = 0; generation < gcCollections.Length; generation++)
                this.TotalGcCollections[generation] += gcCollections[generation];
            if (!timingPartitionIsValid)
                this.InvalidTimingPartitionTicks++;
            if (!smapiUpdateTimingAvailable)
                this.UnavailableSmapiUpdateTimingTicks++;
            if (!gcCollectionDataIsValid)
                this.InvalidGcCollectionTicks++;

            this.AddTick(sample);
            this.CompletedTicks++;
            this.IsTickOpen = false;
            this.CurrentTickUpdateDomain = UpdateTimingDomain.Unowned;
            this.CurrentUpdateDomainStartTimestamp = 0;
            this.CurrentTickObservedSmapiUpdateScope = false;
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
        while (handlers.Count > 0)
        {
            ActiveHandler stale = handlers[^1];
            if (!ReferenceEquals(stale.Manager, this) || stale.Generation == generation)
                break;
            handlers.RemoveAt(handlers.Count - 1);
        }

        modId = ModPerformanceManager.SanitizeIdentity(modId, "unknown-mod", ModHealthReportLimits.MaxIdentityLength);
        modName = ModPerformanceManager.SanitizeIdentity(modName, modId, ModHealthReportLimits.MaxIdentityLength);
        eventName = ModPerformanceManager.SanitizeIdentity(eventName, "unknown-event", ModHealthReportLimits.MaxCallbackNameLength);
        handlerName = ModPerformanceManager.SanitizeIdentity(handlerName, "unknown-callback", ModHealthReportLimits.MaxCallbackNameLength);
        onBehalfOfModId = ModPerformanceManager.SanitizeOptionalIdentity(onBehalfOfModId);
        int depth = handlers.Count;
        UpdateTimingDomain startDomain = this.GetUpdateDomainForCurrentThread();
        handlers.Add(new ActiveHandler(this, generation, modId, modName, eventName, handlerName, phase, operation, onBehalfOfModId, this.GetTimestamp(), 0, startDomain));
        return new HandlerTimingToken(this, generation, depth, startDomain);
    }

    /// <summary>Get the phase of the innermost active invocation for this collector on the current thread, if any.</summary>
    public ModHealthExecutionPhase? GetActiveExecutionPhase()
    {
        if (!this.IsTracking)
            return null;

        List<ActiveHandler>? handlers = ModPerformanceManager.ActiveHandlers;
        if (handlers is null)
            return null;

        long generation = Volatile.Read(ref this.SampleGeneration);
        for (int i = handlers.Count - 1; i >= 0; i--)
        {
            ActiveHandler handler = handlers[i];
            if (ReferenceEquals(handler.Manager, this) && handler.Generation == generation)
                return handler.Phase;
        }

        return null;
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
        if (!ReferenceEquals(handler.Manager, this) || handler.Generation != token.Generation || handler.StartDomain != token.StartDomain)
        {
            if (ReferenceEquals(handler.Manager, this) && handler.Generation != Volatile.Read(ref this.SampleGeneration))
                handlers.RemoveAt(index);
            return;
        }
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

        this.RecordHandler(handler.ModId, handler.ModName, handler.EventName, handler.HandlerName, handler.Phase, handler.Operation, handler.OnBehalfOfModId, exclusiveTimestampTicks, failed, handler.Generation, handler.StartDomain);
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
        this.RecordHandler(modId, modName, eventName, handlerName, ModHealthExecutionPhase.Unscoped, ModPerformanceManager.GetOperationKind(eventName), onBehalfOfModId: null, elapsedTimestampTicks, failed, requiredGeneration: null, startDomain: this.GetUpdateDomainForCurrentThread());
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
        this.RecordHandler(modId, modName, eventName, handlerName, phase, operation, onBehalfOfModId, elapsedTimestampTicks, failed, requiredGeneration: null, startDomain: this.GetUpdateDomainForCurrentThread());
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
    /// <param name="startDomain">The owned update domain in which the invocation began.</param>
    private void RecordHandler(string modId, string modName, string eventName, string handlerName, ModHealthExecutionPhase phase, ModHealthOperationKind operation, string? onBehalfOfModId, long elapsedTimestampTicks, bool failed, long? requiredGeneration, UpdateTimingDomain startDomain)
    {
        if (!this.IsTracking)
            return;

        elapsedTimestampTicks = Math.Max(0, elapsedTimestampTicks);
        modId = ModPerformanceManager.SanitizeIdentity(modId, "unknown-mod", ModHealthReportLimits.MaxIdentityLength);
        modName = ModPerformanceManager.SanitizeIdentity(modName, modId, ModHealthReportLimits.MaxIdentityLength);
        eventName = ModPerformanceManager.SanitizeIdentity(eventName, "unknown-event", ModHealthReportLimits.MaxCallbackNameLength);
        handlerName = ModPerformanceManager.SanitizeIdentity(handlerName, "unknown-callback", ModHealthReportLimits.MaxCallbackNameLength);
        onBehalfOfModId = ModPerformanceManager.SanitizeOptionalIdentity(onBehalfOfModId);
        HandlerIdentity identity = new(modId, modName, eventName, handlerName, phase, operation, onBehalfOfModId);
        int currentThreadId = Environment.CurrentManagedThreadId;

        lock (this.SyncRoot)
        {
            if (!this.IsTracking || (requiredGeneration.HasValue && requiredGeneration.Value != this.SampleGeneration))
                return;

            if (this.IsTickOpen && currentThreadId == this.CurrentTickThreadId)
            {
                UpdateTimingDomain endDomain = this.CurrentTickUpdateDomain;
                if (startDomain != endDomain)
                    this.CurrentTickTimingPartitionIsInvalid = true;
                if (this.CurrentTickMods.TryGetValue(modId, out TickModCounter tickCounter))
                {
                    tickCounter.TimestampTicks += elapsedTimestampTicks;
                    this.CurrentTickMods[modId] = tickCounter;
                }
                else if (this.CurrentTickMods.Count < this.TickContributorIdentityCapacity)
                    this.CurrentTickMods[modId] = new TickModCounter(modName, elapsedTimestampTicks);
                else
                {
                    this.CurrentTickOmittedContributorObservations++;
                    this.OmittedTickContributorObservations++;
                }
                this.CurrentTickInstrumentedTimestampTicks += elapsedTimestampTicks;
                if (startDomain == endDomain && endDomain == UpdateTimingDomain.Game)
                    this.CurrentTickInstrumentedDuringGameUpdateTicks += elapsedTimestampTicks;
                else if (startDomain == endDomain && endDomain == UpdateTimingDomain.Smapi)
                    this.CurrentTickInstrumentedDuringSmapiUpdateTicks += elapsedTimestampTicks;
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
        if (!isWarning && !isError)
            return;

        modId = ModPerformanceManager.SanitizeIdentity(modId, "unknown-mod", ModHealthReportLimits.MaxIdentityLength);
        modName = ModPerformanceManager.SanitizeIdentity(modName, modId, ModHealthReportLimits.MaxIdentityLength);
        if (modId is "SMAPI" or "game")
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
        RawPerformanceSnapshot raw;
        lock (this.SyncRoot)
            raw = this.CaptureRawSnapshot();

        HandlerPerformanceSnapshot[] handlers = this.GetLegacyHandlerSnapshots(raw.Handlers);
        ModLogSnapshot[] logs = new ModLogSnapshot[raw.Logs.Length];
        for (int i = 0; i < logs.Length; i++)
        {
            RawModLogSnapshot log = raw.Logs[i];
            logs[i] = new ModLogSnapshot(log.ModId, log.ModName, log.WarningCount, log.ErrorCount);
        }

        return new ModPerformanceSnapshot(
            IsTracking: raw.IsTracking,
            StartedUtc: raw.StartedUtc,
            Elapsed: TimeSpan.FromMilliseconds(this.ToMilliseconds(raw.ElapsedTimestampTicks)),
            CompletedTickCount: raw.CompletedTicks,
            Handlers: Array.AsReadOnly(handlers),
            ModLogs: Array.AsReadOnly(logs),
            RecentTicks: Array.AsReadOnly(raw.Ticks),
            OmittedHandlerInvocations: raw.OmittedHandlerInvocations,
            LogIndividualTicks: raw.LogIndividualTicks,
            TickLogThresholdMilliseconds: raw.TickLogThresholdMilliseconds,
            TickTotalMilliseconds: this.ToMilliseconds(raw.TotalTickTimestampTicks),
            GameUpdateMilliseconds: this.ToMilliseconds(raw.TotalGameUpdateTimestampTicks),
            TickInstrumentedMilliseconds: this.ToMilliseconds(raw.TotalTickInstrumentedTimestampTicks),
            InstrumentedDuringGameUpdateMilliseconds: this.ToMilliseconds(raw.TotalInstrumentedDuringGameUpdateTicks),
            Gen0Collections: raw.TotalGcCollections[0],
            Gen1Collections: raw.TotalGcCollections[1],
            Gen2Collections: raw.TotalGcCollections[2],
            TimingPartitionIsValid:
                raw.InvalidTimingPartitionTicks == 0
                && ModPerformanceManager.IsValidTimingPartition(
                    raw.TotalTickTimestampTicks,
                    raw.TotalGameUpdateTimestampTicks,
                    raw.TotalTickInstrumentedTimestampTicks,
                    raw.TotalInstrumentedDuringGameUpdateTicks,
                    raw.TotalSmapiUpdateTimestampTicks,
                    raw.TotalInstrumentedDuringSmapiUpdateTicks
                ),
            InvalidTimingPartitionTickCount: raw.InvalidTimingPartitionTicks,
            GcCollectionDataIsValid: raw.InvalidGcCollectionTicks == 0,
            InvalidGcCollectionTickCount: raw.InvalidGcCollectionTicks,
            CaptureGen0Collections: raw.CaptureGcCollections[0],
            CaptureGen1Collections: raw.CaptureGcCollections[1],
            CaptureGen2Collections: raw.CaptureGcCollections[2],
            CaptureGcCollectionDataIsValid: raw.CaptureGcCollectionDataIsValid,
            Health: this.GetHealthSnapshot(raw),
            SmapiUpdateMilliseconds: this.ToMilliseconds(raw.TotalSmapiUpdateTimestampTicks),
            InstrumentedDuringSmapiUpdateMilliseconds: this.ToMilliseconds(raw.TotalInstrumentedDuringSmapiUpdateTicks),
            SmapiUpdateTimingAvailable:
                raw.CompletedTicks > 0
                && raw.UnavailableSmapiUpdateTimingTicks == 0
                && raw.InvalidTimingPartitionTicks == 0
                && ModPerformanceManager.IsValidTimingPartition(
                    raw.TotalTickTimestampTicks,
                    raw.TotalGameUpdateTimestampTicks,
                    raw.TotalTickInstrumentedTimestampTicks,
                    raw.TotalInstrumentedDuringGameUpdateTicks,
                    raw.TotalSmapiUpdateTimestampTicks,
                    raw.TotalInstrumentedDuringSmapiUpdateTicks
                )
        );
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
    /// <summary>Copy the collector state needed to build a snapshot. The caller must hold <see cref="SyncRoot"/>.</summary>
    private RawPerformanceSnapshot CaptureRawSnapshot()
    {
        RawHandlerSnapshot[] handlers = new RawHandlerSnapshot[this.HandlerCounters.Count];
        int handlerIndex = 0;
        foreach ((HandlerIdentity identity, HandlerCounter counter) in this.HandlerCounters)
        {
            handlers[handlerIndex++] = new RawHandlerSnapshot(
                identity,
                counter.CallCount,
                counter.TotalTimestampTicks,
                counter.MaximumTimestampTicks,
                counter.FailureCount
            );
        }

        RawModLogSnapshot[] logs = new RawModLogSnapshot[this.ModLogs.Count];
        int logIndex = 0;
        foreach (MutableModLogSummary log in this.ModLogs.Values)
            logs[logIndex++] = new RawModLogSnapshot(log.ModId, log.ModName, log.WarningCount, log.ErrorCount);

        TickPerformanceSnapshot[] ticks = new TickPerformanceSnapshot[this.TickHistoryCount];
        for (int i = 0; i < ticks.Length; i++)
            ticks[i] = this.TickHistory[(this.TickHistoryStart + i) % this.TickHistory.Length];

        ModHealthUpdatePerformanceSnapshot[] recent = new ModHealthUpdatePerformanceSnapshot[this.HealthTickHistoryCount];
        for (int i = 0; i < recent.Length; i++)
            recent[i] = ModPerformanceManager.FreezeHealthUpdate(this.HealthTickHistory[(this.HealthTickHistoryStart + i) % this.HealthTickHistory.Length]);

        ModHealthUpdatePerformanceSnapshot[] worst = new ModHealthUpdatePerformanceSnapshot[this.WorstHealthTickCount];
        for (int i = 0; i < worst.Length; i++)
            worst[i] = ModPerformanceManager.FreezeHealthUpdate(this.WorstHealthTicks[i]);

        ModHealthSlowEpisodeSnapshot[] closedEpisodes = new ModHealthSlowEpisodeSnapshot[this.SlowEpisodeCount];
        Array.Copy(this.SlowEpisodes, closedEpisodes, closedEpisodes.Length);

        long[] captureGcCollections = new long[3];
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

        long endTimestamp = this.HasSampleEndTimestamp
            ? this.SampleEndTimestamp
            : this.GetTimestamp();
        return new RawPerformanceSnapshot
        {
            IsTracking = this.IsTracking,
            StartedUtc = this.SampleStartedUtc,
            ElapsedTimestampTicks = Math.Max(0, endTimestamp - this.SampleStartTimestamp),
            CompletedTicks = this.CompletedTicks,
            Handlers = handlers,
            Logs = logs,
            Ticks = ticks,
            OmittedHandlerInvocations = this.OmittedHandlerInvocations,
            LogIndividualTicks = this.LogIndividualTicks,
            TickLogThresholdMilliseconds = this.TickLogThresholdMilliseconds,
            TotalTickTimestampTicks = this.TotalTickTimestampTicks,
            TotalGameUpdateTimestampTicks = this.TotalGameUpdateTimestampTicks,
            TotalTickInstrumentedTimestampTicks = this.TotalTickInstrumentedTimestampTicks,
            TotalInstrumentedDuringGameUpdateTicks = this.TotalInstrumentedDuringGameUpdateTicks,
            TotalSmapiUpdateTimestampTicks = this.TotalSmapiUpdateTimestampTicks,
            TotalInstrumentedDuringSmapiUpdateTicks = this.TotalInstrumentedDuringSmapiUpdateTicks,
            UnavailableSmapiUpdateTimingTicks = this.UnavailableSmapiUpdateTimingTicks,
            TotalGcCollections = (long[])this.TotalGcCollections.Clone(),
            InvalidTimingPartitionTicks = this.InvalidTimingPartitionTicks,
            InvalidGcCollectionTicks = this.InvalidGcCollectionTicks,
            CaptureGcCollections = captureGcCollections,
            CaptureGcCollectionDataIsValid = captureGcCollectionDataIsValid,
            RecentHealthTicks = recent,
            WorstHealthTicks = worst,
            ClosedSlowEpisodes = closedEpisodes,
            OpenSlowEpisode = this.HasOpenSlowEpisode ? this.OpenSlowEpisode.ToSnapshot() : null,
            HistogramBuckets = (long[])this.HealthHistogramBuckets.Clone(),
            ThresholdCounts = (long[])this.HealthThresholdCounts.Clone(),
            HistogramCount = this.HealthHistogramCount,
            HistogramTotalTimestampTicks = this.HealthHistogramTotalTimestampTicks,
            HistogramMinimumTimestampTicks = this.HealthHistogramMinimumTimestampTicks,
            HistogramMaximumTimestampTicks = this.HealthHistogramMaximumTimestampTicks,
            HistogramUnderflowCount = this.HealthHistogramUnderflowCount,
            HistogramOverflowCount = this.HealthHistogramOverflowCount,
            SlowUpdateThresholdMilliseconds = this.SlowUpdateThresholdMilliseconds,
            SlowUpdateCount = this.SlowUpdateCount,
            OmittedSlowEpisodes = this.OmittedSlowEpisodes,
            OmittedTickContributorObservations = this.OmittedTickContributorObservations,
            OmittedSlowTickContributorIdentities = this.OmittedSlowTickContributorIdentities,
            InvalidHistogramUpdates = this.InvalidHistogramUpdates
        };
    }

    /// <summary>Build the legacy handler view by reaggregating health dimensions into the original callback identity.</summary>
    private HandlerPerformanceSnapshot[] GetLegacyHandlerSnapshots(RawHandlerSnapshot[] rawHandlers)
    {
        Dictionary<LegacyHandlerIdentity, LegacyHandlerCounter> grouped = [];
        foreach (RawHandlerSnapshot raw in rawHandlers)
        {
            LegacyHandlerIdentity identity = new(raw.Identity.ModId, raw.Identity.ModName, raw.Identity.EventName, raw.Identity.HandlerName);
            if (!grouped.TryGetValue(identity, out LegacyHandlerCounter counter))
                counter = default;
            counter.CallCount += raw.CallCount;
            counter.TotalTimestampTicks += raw.TotalTimestampTicks;
            counter.MaximumTimestampTicks = Math.Max(counter.MaximumTimestampTicks, raw.MaximumTimestampTicks);
            counter.FailureCount += raw.FailureCount;
            grouped[identity] = counter;
        }

        HandlerPerformanceSnapshot[] handlers = new HandlerPerformanceSnapshot[grouped.Count];
        int index = 0;
        foreach ((LegacyHandlerIdentity identity, LegacyHandlerCounter counter) in grouped)
        {
            handlers[index++] = new HandlerPerformanceSnapshot(
                identity.ModId,
                identity.ModName,
                identity.EventName,
                identity.HandlerName,
                counter.CallCount,
                this.ToMilliseconds(counter.TotalTimestampTicks),
                this.ToMilliseconds(counter.MaximumTimestampTicks),
                counter.FailureCount
            );
        }
        return handlers;
    }

    /// <summary>Build the immutable bounded health-oriented timing snapshot outside the collector lock.</summary>
    private ModHealthPerformanceSnapshot GetHealthSnapshot(RawPerformanceSnapshot raw)
    {
        ModHealthCallbackPerformanceSnapshot[] callbacks = new ModHealthCallbackPerformanceSnapshot[raw.Handlers.Length];
        for (int i = 0; i < callbacks.Length; i++)
        {
            RawHandlerSnapshot handler = raw.Handlers[i];
            HandlerIdentity identity = handler.Identity;
            callbacks[i] = new ModHealthCallbackPerformanceSnapshot(
                identity.ModId,
                identity.ModName,
                identity.Phase,
                identity.Operation,
                identity.EventName,
                identity.HandlerName,
                identity.OnBehalfOfModId,
                handler.CallCount,
                this.ToMilliseconds(handler.TotalTimestampTicks),
                this.ToMilliseconds(handler.MaximumTimestampTicks),
                handler.FailureCount
            );
        }
        Array.Sort(callbacks, ModHealthCallbackPerformanceSnapshotComparer.Instance);

        ModHealthSlowEpisodeSnapshot[] episodes = ModPerformanceManager.GetRankedEpisodesIncludingOpen(
            raw.ClosedSlowEpisodes,
            raw.OpenSlowEpisode,
            this.SlowEpisodes.Length,
            out bool openEpisodeOmitted
        );

        ModHealthTimingThresholdSnapshot[] thresholds = new ModHealthTimingThresholdSnapshot[HealthHistogramThresholds.Length];
        for (int i = 0; i < thresholds.Length; i++)
            thresholds[i] = new ModHealthTimingThresholdSnapshot(HealthHistogramThresholds[i], raw.ThresholdCounts[i]);

        ModHealthTimingHistogramSnapshot histogram = new(
            Buckets: Array.AsReadOnly(raw.HistogramBuckets),
            Count: raw.HistogramCount,
            SumMilliseconds: this.ToMilliseconds(raw.HistogramTotalTimestampTicks),
            MinimumMilliseconds: raw.HistogramCount > 0 ? this.ToMilliseconds(raw.HistogramMinimumTimestampTicks) : null,
            MaximumMilliseconds: raw.HistogramCount > 0 ? this.ToMilliseconds(raw.HistogramMaximumTimestampTicks) : null,
            UnderflowCount: raw.HistogramUnderflowCount,
            OverflowCount: raw.HistogramOverflowCount,
            Thresholds: Array.AsReadOnly(thresholds),
            P50Milliseconds: this.GetHealthHistogramPercentile(raw, 0.50),
            P95Milliseconds: this.GetHealthHistogramPercentile(raw, 0.95),
            P99Milliseconds: this.GetHealthHistogramPercentile(raw, 0.99),
            MaximumRelativeBucketError: Math.Pow(2, 1d / ModHealthTimingHistogramSnapshot.SubBucketsPerPowerOfTwo) - 1
        );

        return new ModHealthPerformanceSnapshot(
            SlowUpdateThresholdMilliseconds: raw.SlowUpdateThresholdMilliseconds,
            SlowUpdateCount: raw.SlowUpdateCount,
            Callbacks: Array.AsReadOnly(callbacks),
            RecentUpdates: Array.AsReadOnly(raw.RecentHealthTicks),
            WorstUpdates: Array.AsReadOnly(raw.WorstHealthTicks),
            Episodes: Array.AsReadOnly(episodes),
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
                RecentUpdates: Math.Max(0, raw.CompletedTicks - raw.RecentHealthTicks.Length),
                WorstUpdates: Math.Max(0, raw.HistogramCount - raw.WorstHealthTicks.Length),
                SlowEpisodes: raw.OmittedSlowEpisodes + (openEpisodeOmitted ? 1 : 0),
                ContributorObservations: raw.OmittedTickContributorObservations,
                SlowUpdateContributorIdentities: raw.OmittedSlowTickContributorIdentities,
                CallbackInvocations: raw.OmittedHandlerInvocations,
                InvalidHistogramUpdates: raw.InvalidHistogramUpdates
            )
        );
    }

    /// <summary>Deep-copy one retained update so its nested contributor list can't mutate collector state.</summary>
    private static ModHealthUpdatePerformanceSnapshot FreezeHealthUpdate(ModHealthUpdatePerformanceSnapshot update)
    {
        ModHealthTickContributorSnapshot[] contributors = new ModHealthTickContributorSnapshot[update.Contributors.Count];
        for (int i = 0; i < contributors.Length; i++)
            contributors[i] = update.Contributors[i];
        return update with { Contributors = Array.AsReadOnly(contributors) };
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
    private double? GetHealthHistogramPercentile(RawPerformanceSnapshot raw, double percentile)
    {
        if (raw.HistogramCount == 0)
            return null;

        long rank = (long)Math.Ceiling(percentile * raw.HistogramCount);
        long cumulative = raw.HistogramUnderflowCount;
        if (rank <= cumulative)
            return ModHealthTimingHistogramSnapshot.MinimumBucketMilliseconds;

        for (int i = 0; i < raw.HistogramBuckets.Length; i++)
        {
            cumulative += raw.HistogramBuckets[i];
            if (rank <= cumulative)
            {
                return ModHealthTimingHistogramSnapshot.MinimumBucketMilliseconds
                    * Math.Pow(2, (i + 1d) / ModHealthTimingHistogramSnapshot.SubBucketsPerPowerOfTwo);
            }
        }

        return this.ToMilliseconds(raw.HistogramMaximumTimestampTicks);
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
    private static ModHealthSlowEpisodeSnapshot[] GetRankedEpisodesIncludingOpen(ModHealthSlowEpisodeSnapshot[] closed, ModHealthSlowEpisodeSnapshot? open, int capacity, out bool openEpisodeOmitted)
    {
        openEpisodeOmitted = false;
        if (open is null)
            return closed;

        int insertAt = 0;
        while (insertAt < closed.Length && ModPerformanceManager.CompareEpisodes(closed[insertAt], open.Value) <= 0)
            insertAt++;
        if (insertAt >= capacity)
        {
            openEpisodeOmitted = true;
            return closed;
        }

        int count = Math.Min(closed.Length + 1, capacity);
        ModHealthSlowEpisodeSnapshot[] result = new ModHealthSlowEpisodeSnapshot[count];
        if (insertAt > 0)
            Array.Copy(closed, 0, result, 0, insertAt);
        result[insertAt] = open.Value;
        int after = count - insertAt - 1;
        if (after > 0)
            Array.Copy(closed, insertAt, result, insertAt + 1, after);
        if (closed.Length >= capacity)
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

    /// <summary>Bound and structurally sanitize an identity before retaining it in collector state.</summary>
    private static string SanitizeIdentity(string? value, string fallback, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        bool safe = value.Length <= maximumLength;
        bool previousWasSpace = false;
        for (int i = 0; safe && i < value.Length; i++)
        {
            char character = value[i];
            bool isSpace = character == ' ';
            bool isWhitespace = char.IsWhiteSpace(character);
            safe =
                !char.IsControl(character)
                && !char.IsSurrogate(character)
                && character is not '/' and not '\\'
                && (!isWhitespace || (isSpace && i > 0 && i < value.Length - 1 && !previousWasSpace));
            previousWasSpace = isSpace;
        }
        if (safe)
            return value;

        if (value.Length > maximumLength)
            value = value.Substring(0, maximumLength);
        string sanitized = ModHealthTextSanitizer.SanitizeIdentity(value, maximumLength);
        return sanitized.Length > 0 ? sanitized : fallback;
    }

    /// <summary>Bound and structurally sanitize an optional identity before retaining it in collector state.</summary>
    private static string? SanitizeOptionalIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return ModPerformanceManager.SanitizeIdentity(value, "unknown", ModHealthReportLimits.MaxIdentityLength);
    }

    /// <summary>Begin one mutually exclusive owned update timing domain.</summary>
    private void BeginUpdateDomain(UpdateTimingDomain domain)
    {
        if (!this.IsTracking)
            return;

        lock (this.SyncRoot)
        {
            if (!this.IsTracking || !this.IsTickOpen)
                return;
            if (Environment.CurrentManagedThreadId != this.CurrentTickThreadId || this.CurrentTickUpdateDomain != UpdateTimingDomain.Unowned)
            {
                this.CurrentTickTimingPartitionIsInvalid = true;
                return;
            }

            this.CurrentTickUpdateDomain = domain;
            this.CurrentUpdateDomainStartTimestamp = this.GetTimestamp();
            if (domain == UpdateTimingDomain.Smapi)
                this.CurrentTickObservedSmapiUpdateScope = true;
        }
    }

    /// <summary>Finish one mutually exclusive owned update timing domain.</summary>
    private void EndUpdateDomain(UpdateTimingDomain domain)
    {
        if (!this.IsTracking)
            return;

        lock (this.SyncRoot)
        {
            if (!this.IsTickOpen)
                return;
            if (Environment.CurrentManagedThreadId != this.CurrentTickThreadId || this.CurrentTickUpdateDomain != domain)
            {
                this.CurrentTickTimingPartitionIsInvalid = true;
                return;
            }

            long elapsedTimestampTicks = this.GetTimestamp() - this.CurrentUpdateDomainStartTimestamp;
            if (domain == UpdateTimingDomain.Game)
                this.CurrentTickGameUpdateTimestampTicks += elapsedTimestampTicks;
            else
                this.CurrentTickSmapiUpdateTimestampTicks += elapsedTimestampTicks;

            this.CurrentTickUpdateDomain = UpdateTimingDomain.Unowned;
            this.CurrentUpdateDomainStartTimestamp = 0;
        }
    }

    /// <summary>Get the owned update domain active for the current thread.</summary>
    private UpdateTimingDomain GetUpdateDomainForCurrentThread()
    {
        if (!this.IsTracking)
            return UpdateTimingDomain.Unowned;

        lock (this.SyncRoot)
        {
            return this.IsTracking && this.IsTickOpen && Environment.CurrentManagedThreadId == this.CurrentTickThreadId
                ? this.CurrentTickUpdateDomain
                : UpdateTimingDomain.Unowned;
        }
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
        this.OmittedTickContributorObservations = 0;
        this.OmittedSlowTickContributorIdentities = 0;
        this.OmittedSlowEpisodes = 0;
        this.InvalidHistogramUpdates = 0;
        this.SampleStartedUtc = DateTime.UtcNow;
        this.SampleStartTimestamp = timestamp;
        this.SampleEndTimestamp = 0;
        this.HasSampleEndTimestamp = false;
        for (int generation = 0; generation < this.SampleStartGcCollections.Length; generation++)
            this.SampleStartGcCollections[generation] = this.GetGcCollectionCount(generation);
        this.HasSampleEndGcCollections = false;
        this.IsTickOpen = false;
        this.CurrentTickThreadId = 0;
        this.CurrentTickErrors = 0;
        this.CurrentTickWarnings = 0;
        this.CurrentTickCallbackFailures = 0;
        this.CurrentTickContext = default;
        this.CurrentTickOmittedContributorObservations = 0;
        this.CurrentTickInstrumentedTimestampTicks = 0;
        this.CurrentTickGameUpdateTimestampTicks = 0;
        this.CurrentTickInstrumentedDuringGameUpdateTicks = 0;
        this.CurrentTickSmapiUpdateTimestampTicks = 0;
        this.CurrentTickInstrumentedDuringSmapiUpdateTicks = 0;
        this.CurrentTickUpdateDomain = UpdateTimingDomain.Unowned;
        this.CurrentUpdateDomainStartTimestamp = 0;
        this.CurrentTickObservedSmapiUpdateScope = false;
        this.CurrentTickTimingPartitionIsInvalid = false;
        this.TotalTickTimestampTicks = 0;
        this.TotalGameUpdateTimestampTicks = 0;
        this.TotalTickInstrumentedTimestampTicks = 0;
        this.TotalInstrumentedDuringGameUpdateTicks = 0;
        this.TotalSmapiUpdateTimestampTicks = 0;
        this.TotalInstrumentedDuringSmapiUpdateTicks = 0;
        this.UnavailableSmapiUpdateTimingTicks = 0;
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
    private static bool IsValidTimingPartition(long totalTicks, long gameUpdateTicks, long instrumentedTicks, long instrumentedDuringGameUpdateTicks, long smapiUpdateTicks, long instrumentedDuringSmapiUpdateTicks)
    {
        return
            totalTicks >= 0
            && gameUpdateTicks >= 0
            && instrumentedTicks >= 0
            && instrumentedDuringGameUpdateTicks >= 0
            && smapiUpdateTicks >= 0
            && instrumentedDuringSmapiUpdateTicks >= 0
            && instrumentedDuringGameUpdateTicks <= gameUpdateTicks
            && instrumentedDuringGameUpdateTicks <= instrumentedTicks
            && instrumentedDuringSmapiUpdateTicks <= smapiUpdateTicks
            && instrumentedDuringSmapiUpdateTicks <= instrumentedTicks - instrumentedDuringGameUpdateTicks
            && gameUpdateTicks <= totalTicks
            && smapiUpdateTicks <= totalTicks - gameUpdateTicks
            && instrumentedTicks - instrumentedDuringGameUpdateTicks - instrumentedDuringSmapiUpdateTicks <= totalTicks - gameUpdateTicks - smapiUpdateTicks;
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

    /// <summary>The original performance-command callback identity, excluding health-only dimensions.</summary>
    private readonly record struct LegacyHandlerIdentity(string ModId, string ModName, string EventName, string HandlerName);

    /// <summary>A copied handler counter which is safe to project outside the collector lock.</summary>
    private readonly record struct RawHandlerSnapshot(HandlerIdentity Identity, long CallCount, long TotalTimestampTicks, long MaximumTimestampTicks, long FailureCount);

    /// <summary>A copied log counter which is safe to project outside the collector lock.</summary>
    private readonly record struct RawModLogSnapshot(string ModId, string ModName, long WarningCount, long ErrorCount);

    /// <summary>A temporary aggregate for the legacy handler view.</summary>
    private struct LegacyHandlerCounter
    {
        public long CallCount;
        public long TotalTimestampTicks;
        public long MaximumTimestampTicks;
        public long FailureCount;
    }

    /// <summary>A bounded point-in-time copy of collector state.</summary>
    private sealed class RawPerformanceSnapshot
    {
        public bool IsTracking;
        public DateTime StartedUtc;
        public long ElapsedTimestampTicks;
        public long CompletedTicks;
        public RawHandlerSnapshot[] Handlers = [];
        public RawModLogSnapshot[] Logs = [];
        public TickPerformanceSnapshot[] Ticks = [];
        public long OmittedHandlerInvocations;
        public bool LogIndividualTicks;
        public double TickLogThresholdMilliseconds;
        public long TotalTickTimestampTicks;
        public long TotalGameUpdateTimestampTicks;
        public long TotalTickInstrumentedTimestampTicks;
        public long TotalInstrumentedDuringGameUpdateTicks;
        public long TotalSmapiUpdateTimestampTicks;
        public long TotalInstrumentedDuringSmapiUpdateTicks;
        public long UnavailableSmapiUpdateTimingTicks;
        public long[] TotalGcCollections = [];
        public long InvalidTimingPartitionTicks;
        public long InvalidGcCollectionTicks;
        public long[] CaptureGcCollections = [];
        public bool CaptureGcCollectionDataIsValid;
        public ModHealthUpdatePerformanceSnapshot[] RecentHealthTicks = [];
        public ModHealthUpdatePerformanceSnapshot[] WorstHealthTicks = [];
        public ModHealthSlowEpisodeSnapshot[] ClosedSlowEpisodes = [];
        public ModHealthSlowEpisodeSnapshot? OpenSlowEpisode;
        public long[] HistogramBuckets = [];
        public long[] ThresholdCounts = [];
        public long HistogramCount;
        public long HistogramTotalTimestampTicks;
        public long HistogramMinimumTimestampTicks;
        public long HistogramMaximumTimestampTicks;
        public long HistogramUnderflowCount;
        public long HistogramOverflowCount;
        public double SlowUpdateThresholdMilliseconds;
        public long SlowUpdateCount;
        public long OmittedSlowEpisodes;
        public long OmittedTickContributorObservations;
        public long OmittedSlowTickContributorIdentities;
        public long InvalidHistogramUpdates;
    }

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
    private struct ActiveHandler(ModPerformanceManager manager, long generation, string modId, string modName, string eventName, string handlerName, ModHealthExecutionPhase phase, ModHealthOperationKind operation, string? onBehalfOfModId, long startTimestamp, long nestedTimestampTicks, UpdateTimingDomain startDomain)
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
        public UpdateTimingDomain StartDomain { get; } = startDomain;
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

    /// <summary>The owned update timing domain in which the invocation began.</summary>
    internal ModPerformanceManager.UpdateTimingDomain StartDomain { get; }

    /// <summary>Construct an instance.</summary>
    public HandlerTimingToken(ModPerformanceManager manager, long generation, int depth, ModPerformanceManager.UpdateTimingDomain startDomain)
    {
        this.Manager = manager;
        this.Generation = generation;
        this.Depth = depth;
        this.StartDomain = startDomain;
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
    ModHealthPerformanceSnapshot? Health = null,
    double SmapiUpdateMilliseconds = 0,
    double InstrumentedDuringSmapiUpdateMilliseconds = 0,
    bool SmapiUpdateTimingAvailable = false
)
{
    /// <summary>Base game update time in completed ticks, excluding instrumented mod callbacks which ran within it.</summary>
    public double GameUpdateExclusiveMilliseconds => this.GameUpdateMilliseconds - this.InstrumentedDuringGameUpdateMilliseconds;

    /// <summary>Separately measured SMAPI update time in completed ticks, excluding observed callbacks which ran within it.</summary>
    public double SmapiUpdateExclusiveMilliseconds => this.SmapiUpdateMilliseconds - this.InstrumentedDuringSmapiUpdateMilliseconds;

    /// <summary>Time outside the measured game, SMAPI update, and observed callback boundaries.</summary>
    public double ResidualMilliseconds =>
        this.TickTotalMilliseconds
        - this.GameUpdateMilliseconds
        - this.SmapiUpdateMilliseconds
        - (this.TickInstrumentedMilliseconds - this.InstrumentedDuringGameUpdateMilliseconds - this.InstrumentedDuringSmapiUpdateMilliseconds);

    /// <summary>Legacy residual outside the base game update and observed callbacks. This isn't an owned SMAPI attribution.</summary>
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
    bool GcCollectionDataIsValid = true,
    double SmapiUpdateMilliseconds = 0,
    double InstrumentedDuringSmapiUpdateMilliseconds = 0,
    bool SmapiUpdateTimingAvailable = false
)
{
    /// <summary>Time in the update which wasn't observed inside a SMAPI-managed mod event handler.</summary>
    public double UnattributedMilliseconds => this.TotalMilliseconds - this.InstrumentedModMilliseconds;

    /// <summary>Base game update time excluding instrumented mod callbacks which ran within it. This can include Harmony patches and other unobserved work invoked by the game.</summary>
    public double GameUpdateExclusiveMilliseconds => this.GameUpdateMilliseconds - this.InstrumentedDuringGameUpdateMilliseconds;

    /// <summary>Separately measured SMAPI update time excluding observed callbacks which ran within it.</summary>
    public double SmapiUpdateExclusiveMilliseconds => this.SmapiUpdateMilliseconds - this.InstrumentedDuringSmapiUpdateMilliseconds;

    /// <summary>Time outside the measured game, SMAPI update, and observed callback boundaries.</summary>
    public double ResidualMilliseconds =>
        this.TotalMilliseconds
        - this.GameUpdateMilliseconds
        - this.SmapiUpdateMilliseconds
        - (this.InstrumentedModMilliseconds - this.InstrumentedDuringGameUpdateMilliseconds - this.InstrumentedDuringSmapiUpdateMilliseconds);

    /// <summary>Legacy residual outside the base game update and observed callbacks. This isn't an owned SMAPI attribution.</summary>
    public double OutsideGameUpdateMilliseconds => this.TotalMilliseconds - this.GameUpdateMilliseconds - (this.InstrumentedModMilliseconds - this.InstrumentedDuringGameUpdateMilliseconds);
}
