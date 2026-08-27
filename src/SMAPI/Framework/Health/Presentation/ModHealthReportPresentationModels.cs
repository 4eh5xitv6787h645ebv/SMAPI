using System;
using System.Collections.Immutable;

namespace StardewModdingAPI.Framework.Health.Presentation;

/// <summary>Whether a diagnostic value is a measurement or a deliberately nonnumeric state.</summary>
internal enum ModHealthEvidenceState
{
    Measured,
    Unavailable,
    Invalid,
    NotApplicable
}

/// <summary>A millisecond value which keeps measured zero distinct from unavailable or invalid evidence.</summary>
internal readonly record struct ModHealthMeasuredMilliseconds(ModHealthEvidenceState State, double Value)
{
    public static ModHealthMeasuredMilliseconds Measured(double value) => new(ModHealthEvidenceState.Measured, value);
    public static ModHealthMeasuredMilliseconds Unavailable() => new(ModHealthEvidenceState.Unavailable, 0);
    public static ModHealthMeasuredMilliseconds Invalid() => new(ModHealthEvidenceState.Invalid, 0);
    public static ModHealthMeasuredMilliseconds NotApplicable() => new(ModHealthEvidenceState.NotApplicable, 0);
}

/// <summary>A ratio which is hidden when the aggregate timing partition can't support percentages.</summary>
internal readonly record struct ModHealthMeasuredRatio(ModHealthEvidenceState State, double Value)
{
    public static ModHealthMeasuredRatio Measured(double value) => new(ModHealthEvidenceState.Measured, value);
    public static ModHealthMeasuredRatio Invalid() => new(ModHealthEvidenceState.Invalid, 0);
    public static ModHealthMeasuredRatio NotApplicable() => new(ModHealthEvidenceState.NotApplicable, 0);
}

internal sealed record ModHealthReportPresentation(
    int SchemaVersion,
    ModHealthOverviewPresentation Overview,
    ModHealthFindingsPresentation Findings,
    ModHealthCapturePresentation Capture,
    ModHealthAttentionPresentation Attention,
    ModHealthPerformancePresentation Performance,
    ModHealthErrorsPresentation Errors,
    ModHealthInventoryPresentation Inventory,
    ModHealthContextPresentation Context
);

internal sealed record ModHealthOverviewPresentation(
    ModHealthReportHeader Header,
    ModHealthPrivacy Privacy,
    ImmutableArray<string> PrivacyNotices
);

internal sealed record ModHealthFindingsPresentation(
    ImmutableArray<ModHealthFinding> Rows,
    ImmutableArray<string> SuggestedActions
);

internal sealed record ModHealthCapturePresentation(
    ModHealthCapture Details,
    bool IsTruncated,
    bool IsMinimalFallback,
    bool WriteRetry,
    ImmutableArray<ModHealthOmission> PositiveOmissions
);

internal sealed record ModHealthAttentionPresentation(
    ModHealthVirtualRowSource<ModHealthMod, ModHealthModPresentation> Mods
);

internal sealed record ModHealthPerformancePresentation(
    ModHealthHistogram Histogram,
    ModHealthMeasuredMilliseconds ObservedCallbacks,
    ModHealthMeasuredMilliseconds BaseGameExclusive,
    ModHealthMeasuredMilliseconds SmapiUpdateDispatch,
    ModHealthMeasuredMilliseconds Residual,
    bool CanShowTimingPercentages,
    long SlowUpdateCount,
    ModHealthGcPresentation Gc,
    ImmutableArray<string> AttributionCaveats,
    ModHealthVirtualRowSource<ModHealthMod, ModHealthModPresentation> ObservedMods,
    ModHealthVirtualRowSource<ModHealthCallback, ModHealthCallback> Callbacks,
    ModHealthVirtualRowSource<ModHealthEpisode, ModHealthEpisode> Episodes,
    ModHealthVirtualRowSource<ModHealthUpdate, ModHealthUpdatePresentation> WorstUpdates,
    ModHealthVirtualRowSource<ModHealthUpdate, ModHealthUpdatePresentation> RecentUpdates
);

internal sealed record ModHealthGcPresentation(ModHealthEvidenceState State, long Gen0Collections, long Gen1Collections, long Gen2Collections);

internal sealed record ModHealthErrorsPresentation(
    ModHealthLogTotals LogTotals,
    ModHealthCallbackFailureTotals CallbackFailureTotals,
    ModHealthVirtualRowSource<ModHealthLogSummary, ModHealthLogSummary> Logs,
    ModHealthVirtualRowSource<ModHealthCallbackFailure, ModHealthCallbackFailure> CallbackFailures
);

internal sealed record ModHealthInventoryPresentation(
    ModHealthModInventorySummary Summary,
    ModHealthVirtualRowSource<ModHealthMod, ModHealthModPresentation> Mods
);

internal sealed record ModHealthContextPresentation(
    ModHealthEnvironment Environment,
    ModHealthCompleteness Completeness,
    ImmutableArray<ModHealthCapacity> Capacities,
    ImmutableArray<ModHealthOmission> Omissions,
    ImmutableArray<string> Limitations
);

/// <summary>All sanitized schema-v1 mod fields with percentage availability made explicit.</summary>
internal sealed record ModHealthModPresentation(
    string Id,
    string Name,
    string Version,
    ModHealthModKind Kind,
    string? ParentId,
    ModHealthModStatus Status,
    string? FailureCategory,
    ImmutableArray<string> WarningFlags,
    ImmutableArray<string> Dependencies,
    ModHealthReportUpdateStatus UpdateStatus,
    string? SuggestedUpdateVersion,
    long SessionWarningCount,
    long SessionErrorCount,
    long CaptureErrorCount,
    long CallbackFailureCount,
    double ObservedCallbackMilliseconds,
    double ObservedCallbackPeakMilliseconds,
    long ObservedCallbackCount,
    long ObservedCallbackFailureCount,
    long SlowUpdateParticipationCount,
    ModHealthMeasuredRatio InstrumentedTimeShare,
    long PeakMessagesPerSecond,
    long PeakCharactersPerSecond
);

/// <summary>One update row whose partition and GC display states cannot be mistaken for measured zero.</summary>
internal sealed record ModHealthUpdatePresentation(
    uint UpdateTick,
    double OffsetMilliseconds,
    double TotalMilliseconds,
    ModHealthMeasuredMilliseconds BaseGameExclusive,
    ModHealthMeasuredMilliseconds ObservedCallbacks,
    ModHealthMeasuredMilliseconds SmapiUpdateDispatch,
    ModHealthMeasuredMilliseconds Residual,
    bool TimingValid,
    string Phase,
    bool Focused,
    int Screen,
    int WarningCount,
    int ErrorCount,
    int CallbackFailureCount,
    ModHealthGcPresentation Gc,
    ImmutableArray<ModHealthContributor> Contributors,
    int? NearbyMark
);
