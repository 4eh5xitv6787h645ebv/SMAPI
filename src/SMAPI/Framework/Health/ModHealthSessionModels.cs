using System;
using System.Collections.Immutable;
using StardewModdingAPI.Framework.Performance;

namespace StardewModdingAPI.Framework.Health;

/// <summary>The owner of the single deep-timing capture.</summary>
internal enum ModHealthCaptureOwner
{
    None,
    Health,
    Performance
}

/// <summary>The lifecycle state of the single deep-timing capture.</summary>
internal enum ModHealthCaptureState
{
    Inactive,
    Active,
    StoppedRetained
}

/// <summary>How a capture was started.</summary>
internal enum ModHealthCaptureOrigin
{
    Manual,
    Configuration,
    HealthOnLaunch
}

/// <summary>The externally visible state of a report export.</summary>
internal enum ModHealthExportState
{
    None,
    Queued,
    Writing,
    Succeeded,
    Failed
}

/// <summary>The result of asking the bounded export queue to accept work.</summary>
internal enum ModHealthExportDisposition
{
    Queued,
    Pending,
    Coalesced,
    AlreadySucceeded,
    Retried,
    RejectedBusy,
    NoRetryableExport
}

/// <summary>A stable outcome category returned by the session coordinator.</summary>
internal enum ModHealthCoordinatorResultCode
{
    Started,
    Replaced,
    Stopped,
    Reset,
    Marked,
    ExportQueued,
    ExportPending,
    ExportAlreadySucceeded,
    ExportRetried,
    SettingsApplied,
    SettingsPending,
    Refused,
    NothingToRetry
}

/// <summary>An immutable source snapshot submitted to the report builder/export queue.</summary>
internal sealed record ModHealthExportRequest(
    Guid RequestId,
    DateTimeOffset RequestedUtc,
    ModHealthCaptureOwner Owner,
    ModHealthCaptureOrigin? Origin,
    ModHealthCompletionReason CompletionReason,
    ModPerformanceSnapshot? Performance,
    ModHealthLedgerSnapshot Ledger,
    ImmutableArray<ModHealthMark> Marks,
    double SlowUpdateThresholdMilliseconds,
    bool IsFinal,
    ModHealthEnvironmentSnapshot? Environment = null
);

/// <summary>The current result for one export request.</summary>
internal sealed record ModHealthExportStatus(
    ModHealthExportState State,
    Guid? RequestId = null,
    bool IsFinal = false,
    string? TextPath = null,
    string? JsonPath = null,
    string? Error = null,
    ModHealthCompletionSummary? Summary = null
)
{
    public static ModHealthExportStatus None { get; } = new(ModHealthExportState.None);
}

/// <summary>A typed queue response.</summary>
internal readonly record struct ModHealthExportQueueResult(ModHealthExportDisposition Disposition, ModHealthExportStatus Status);

/// <summary>A typed coordinator response suitable for command presentation.</summary>
internal sealed record ModHealthCoordinatorResult(ModHealthCoordinatorResultCode Code, string Message, bool IsError = false, ModHealthExportStatus? Export = null);

/// <summary>An immutable view of the coordinated diagnostic state.</summary>
internal sealed record ModHealthSessionStatus(
    ModHealthCaptureState CaptureState,
    ModHealthCaptureOwner Owner,
    ModHealthCaptureOrigin? Origin,
    ModPerformanceSnapshot? Performance,
    int MarkCount,
    long SessionWarningCount,
    long SessionErrorCount,
    long CaptureWarningCount,
    long CaptureErrorCount,
    long SlowUpdateCount,
    int RetainedSlowMomentCount,
    bool CapacityReached,
    ModHealthExportStatus Export,
    bool HasPendingConfiguration
);

/// <summary>The minimal allocation-free coordinator state needed to choose viewer actions.</summary>
internal readonly record struct ModHealthViewerActionState(ModHealthCaptureState CaptureState);

/// <summary>Persistent diagnostic settings which must be applied atomically by the session coordinator.</summary>
internal readonly record struct ModHealthDiagnosticSettings(
    bool EnableHealthOnLaunch,
    bool EnablePerformanceTracking,
    bool LogPerformanceTicks,
    double PerformanceTickThresholdMilliseconds
);
