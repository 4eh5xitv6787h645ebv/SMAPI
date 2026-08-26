using System;
using System.Collections.Generic;
using StardewModdingAPI.Framework.Health;

namespace StardewModdingAPI.Framework.Performance;

/// <summary>A coarse game phase sampled once at the beginning of an update tick.</summary>
internal enum ModHealthTickPhase
{
    Unknown,
    LoadingSaving,
    Title,
    Cutscene,
    Menu,
    Gameplay
}

/// <summary>Safe game-thread context captured at the beginning of an update tick.</summary>
internal readonly record struct ModHealthTickContext(ModHealthTickPhase Phase, bool IsFocused, int ScreenId);

/// <summary>The bounded health-oriented timing data associated with a performance snapshot.</summary>
internal sealed record ModHealthPerformanceSnapshot(
    double SlowUpdateThresholdMilliseconds,
    long SlowUpdateCount,
    IReadOnlyList<ModHealthCallbackPerformanceSnapshot> Callbacks,
    IReadOnlyList<ModHealthUpdatePerformanceSnapshot> RecentUpdates,
    IReadOnlyList<ModHealthUpdatePerformanceSnapshot> WorstUpdates,
    IReadOnlyList<ModHealthSlowEpisodeSnapshot> Episodes,
    ModHealthTimingHistogramSnapshot Histogram,
    ModHealthTimingCapacities Capacities,
    ModHealthTimingOmissions Omissions
);

/// <summary>One callback aggregate with orthogonal execution and operation dimensions.</summary>
internal readonly record struct ModHealthCallbackPerformanceSnapshot(
    string ModId,
    string ModName,
    ModHealthExecutionPhase Phase,
    ModHealthOperationKind Operation,
    string EventName,
    string CallbackName,
    string? OnBehalfOfModId,
    long CallCount,
    double TotalMilliseconds,
    double MaximumMilliseconds,
    long FailureCount
);

/// <summary>One retained update tick for health diagnostics.</summary>
internal readonly record struct ModHealthUpdatePerformanceSnapshot(
    long CaptureSequence,
    uint Tick,
    double OffsetMilliseconds,
    double TotalMilliseconds,
    double GameUpdateMilliseconds,
    double InstrumentedModMilliseconds,
    double InstrumentedDuringGameUpdateMilliseconds,
    bool TimingPartitionIsValid,
    ModHealthTickContext Context,
    int WarningCount,
    int ErrorCount,
    int CallbackFailureCount,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    bool GcCollectionDataIsValid,
    IReadOnlyList<ModHealthTickContributorSnapshot> Contributors,
    long OmittedContributorIdentities,
    long OmittedContributorObservations
)
{
    /// <summary>Base game update time excluding observed callbacks which ran within it.</summary>
    public double GameUpdateExclusiveMilliseconds => this.GameUpdateMilliseconds - this.InstrumentedDuringGameUpdateMilliseconds;

    /// <summary>Residual time outside the measured game update and observed callback boundaries.</summary>
    public double ResidualMilliseconds => this.TotalMilliseconds - this.GameUpdateMilliseconds - (this.InstrumentedModMilliseconds - this.InstrumentedDuringGameUpdateMilliseconds);
}

/// <summary>One observed mod contributor retained for a slow update.</summary>
internal readonly record struct ModHealthTickContributorSnapshot(string ModId, string ModName, double Milliseconds);

/// <summary>One clustered slow-update episode.</summary>
internal readonly record struct ModHealthSlowEpisodeSnapshot(
    long FirstCaptureSequence,
    long LastCaptureSequence,
    uint FirstTick,
    uint LastTick,
    int QualifyingUpdateCount,
    double MaximumMilliseconds,
    double SummedQualifyingMilliseconds,
    uint RepresentativeTick
);

/// <summary>A fixed logarithmic update-duration histogram.</summary>
internal sealed record ModHealthTimingHistogramSnapshot(
    IReadOnlyList<long> Buckets,
    long Count,
    double SumMilliseconds,
    double? MinimumMilliseconds,
    double? MaximumMilliseconds,
    long UnderflowCount,
    long OverflowCount,
    IReadOnlyList<ModHealthTimingThresholdSnapshot> Thresholds,
    double? P50Milliseconds,
    double? P95Milliseconds,
    double? P99Milliseconds,
    double MaximumRelativeBucketError
)
{
    public const int BucketCount = 256;
    public const double MinimumBucketMilliseconds = 0.125;
    public const double MaximumBucketMilliseconds = 8192;
    public const int SubBucketsPerPowerOfTwo = 16;
}

/// <summary>An exact count at or above one duration threshold.</summary>
internal readonly record struct ModHealthTimingThresholdSnapshot(double Milliseconds, long Count);

/// <summary>The configured health timing capacities.</summary>
internal readonly record struct ModHealthTimingCapacities(
    int RecentUpdates,
    int WorstUpdates,
    int SlowEpisodes,
    int ContributorsPerSlowUpdate,
    int ContributorIdentitiesPerUpdate,
    int CallbackIdentities
);

/// <summary>Data omitted by bounded health timing collections.</summary>
internal readonly record struct ModHealthTimingOmissions(
    long RecentUpdates,
    long WorstUpdates,
    long SlowEpisodes,
    long ContributorObservations,
    long SlowUpdateContributorIdentities,
    long CallbackInvocations,
    long InvalidHistogramUpdates
);
