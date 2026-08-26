using System;
using System.Collections.Immutable;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace StardewModdingAPI.Framework.Health;

/// <summary>An immutable, implementation-independent schema-v1 mod health report.</summary>
[JsonObject(MemberSerialization.OptIn)]
internal sealed record ModHealthReport(
    [property: JsonProperty("schemaVersion", Order = 0, Required = Required.Always)] int SchemaVersion,
    [property: JsonProperty("header", Order = 1, Required = Required.Always)] ModHealthReportHeader Header,
    [property: JsonProperty("completeness", Order = 2, Required = Required.Always)] ModHealthCompleteness Completeness,
    [property: JsonProperty("environment", Order = 3, Required = Required.Always)] ModHealthEnvironment Environment,
    [property: JsonProperty("capture", Order = 4, Required = Required.Always)] ModHealthCapture Capture,
    [property: JsonProperty("findings", Order = 5, Required = Required.Always)] ImmutableArray<ModHealthFinding> Findings,
    [property: JsonProperty("performance", Order = 6, Required = Required.Always)] ModHealthPerformance Performance,
    [property: JsonProperty("modInventory", Order = 7, Required = Required.Always)] ModHealthModInventorySummary ModInventory,
    [property: JsonProperty("mods", Order = 8, Required = Required.Always)] ImmutableArray<ModHealthMod> Mods,
    [property: JsonProperty("logTotals", Order = 9, Required = Required.Always)] ModHealthLogTotals LogTotals,
    [property: JsonProperty("logs", Order = 10, Required = Required.Always)] ImmutableArray<ModHealthLogSummary> Logs,
    [property: JsonProperty("callbackFailureTotals", Order = 11, Required = Required.Always)] ModHealthCallbackFailureTotals CallbackFailureTotals,
    [property: JsonProperty("callbackFailures", Order = 12, Required = Required.Always)] ImmutableArray<ModHealthCallbackFailure> CallbackFailures,
    [property: JsonProperty("capacities", Order = 13, Required = Required.Always)] ImmutableArray<ModHealthCapacity> Capacities,
    [property: JsonProperty("omissions", Order = 14, Required = Required.Always)] ImmutableArray<ModHealthOmission> Omissions,
    [property: JsonProperty("privacy", Order = 15, Required = Required.Always)] ModHealthPrivacy Privacy,
    [property: JsonProperty("limitations", Order = 16, Required = Required.Always)] ImmutableArray<string> Limitations
);

[JsonObject(MemberSerialization.OptIn)]
internal sealed record ModHealthReportHeader(
    [property: JsonProperty("reportId", Order = 0, Required = Required.Always)] string ReportId,
    [property: JsonProperty("generatedUtc", Order = 1, Required = Required.Always)] DateTimeOffset GeneratedUtc,
    [property: JsonProperty("isTruncated", Order = 2, Required = Required.Always)] bool IsTruncated,
    [property: JsonProperty("isMinimalFallback", Order = 3, Required = Required.Always)] bool IsMinimalFallback,
    [property: JsonProperty("writeRetry", Order = 4, Required = Required.Always)] bool WriteRetry
);

[JsonObject(MemberSerialization.OptIn)]
internal sealed record ModHealthCompleteness(
    [property: JsonProperty("ledgerStartedUtc", Order = 0, Required = Required.Always)] DateTimeOffset LedgerStartedUtc,
    [property: JsonProperty("boundary", Order = 1, Required = Required.Always)] string Boundary,
    [property: JsonProperty("startupObserved", Order = 2, Required = Required.Always)] bool StartupObserved,
    [property: JsonProperty("lifecycleTimingObserved", Order = 3, Required = Required.Always)] bool LifecycleTimingObserved
);

[JsonObject(MemberSerialization.OptIn)]
internal sealed record ModHealthEnvironment(
    [property: JsonProperty("smapiVersion", Order = 0, Required = Required.Always)] string SmapiVersion,
    [property: JsonProperty("smapiCommit", Order = 1)] string? SmapiCommit,
    [property: JsonProperty("gameVersion", Order = 2, Required = Required.Always)] string GameVersion,
    [property: JsonProperty("runtimeVersion", Order = 3, Required = Required.Always)] string RuntimeVersion,
    [property: JsonProperty("processArchitecture", Order = 4, Required = Required.Always)] string ProcessArchitecture,
    [property: JsonProperty("processBitness", Order = 5, Required = Required.Always)] int ProcessBitness,
    [property: JsonProperty("linuxDistribution", Order = 6)] string? LinuxDistribution,
    [property: JsonProperty("kernel", Order = 7)] string? Kernel,
    [property: JsonProperty("sessionType", Order = 8, Required = Required.Always)] string SessionType,
    [property: JsonProperty("locale", Order = 9, Required = Required.Always)] string Locale,
    [property: JsonProperty("logicalProcessorCount", Order = 10, Required = Required.Always)] int LogicalProcessorCount,
    [property: JsonProperty("multiplayerRole", Order = 11, Required = Required.Always)] string MultiplayerRole,
    [property: JsonProperty("splitScreenCount", Order = 12, Required = Required.Always)] int SplitScreenCount
);

[JsonObject(MemberSerialization.OptIn)]
internal sealed record ModHealthCapture(
    [property: JsonProperty("mode", Order = 0, Required = Required.Always)] ModHealthCaptureMode Mode,
    [property: JsonProperty("completionReason", Order = 1, Required = Required.Always)] ModHealthCompletionReason CompletionReason,
    [property: JsonProperty("startedUtc", Order = 2)] DateTimeOffset? StartedUtc,
    [property: JsonProperty("endedUtc", Order = 3)] DateTimeOffset? EndedUtc,
    [property: JsonProperty("durationMilliseconds", Order = 4, Required = Required.Always)] double DurationMilliseconds,
    [property: JsonProperty("completedUpdateCount", Order = 5, Required = Required.Always)] long CompletedUpdateCount,
    [property: JsonProperty("slowUpdateThresholdMilliseconds", Order = 6, Required = Required.Always)] double SlowUpdateThresholdMilliseconds,
    [property: JsonProperty("isShortSample", Order = 7, Required = Required.Always)] bool IsShortSample,
    [property: JsonProperty("timingValid", Order = 8, Required = Required.Always)] bool TimingValid,
    [property: JsonProperty("marks", Order = 9, Required = Required.Always)] ImmutableArray<ModHealthMark> Marks
);

[JsonObject(MemberSerialization.OptIn)]
internal readonly record struct ModHealthMark(
    [property: JsonProperty("number", Order = 0, Required = Required.Always)] int Number,
    [property: JsonProperty("updateTick", Order = 1, Required = Required.Always)] uint UpdateTick,
    [property: JsonProperty("offsetMilliseconds", Order = 2, Required = Required.Always)] double OffsetMilliseconds
);

[JsonObject(MemberSerialization.OptIn)]
internal sealed record ModHealthPerformance(
    [property: JsonProperty("histogram", Order = 0, Required = Required.Always)] ModHealthHistogram Histogram,
    [property: JsonProperty("totalObservedModMilliseconds", Order = 1, Required = Required.Always)] double TotalObservedModMilliseconds,
    [property: JsonProperty("totalBaseGameExclusiveMilliseconds", Order = 2, Required = Required.Always)] double TotalBaseGameExclusiveMilliseconds,
    [property: JsonProperty("totalSmapiOtherMilliseconds", Order = 3, Required = Required.Always)] double TotalSmapiOtherMilliseconds,
    [property: JsonProperty("totalResidualMilliseconds", Order = 4, Required = Required.Always)] double TotalResidualMilliseconds,
    [property: JsonProperty("slowUpdateCount", Order = 5, Required = Required.Always)] long SlowUpdateCount,
    [property: JsonProperty("callbacks", Order = 6, Required = Required.Always)] ImmutableArray<ModHealthCallback> Callbacks,
    [property: JsonProperty("worstUpdates", Order = 7, Required = Required.Always)] ImmutableArray<ModHealthUpdate> WorstUpdates,
    [property: JsonProperty("recentUpdates", Order = 8, Required = Required.Always)] ImmutableArray<ModHealthUpdate> RecentUpdates,
    [property: JsonProperty("episodes", Order = 9, Required = Required.Always)] ImmutableArray<ModHealthEpisode> Episodes,
    [property: JsonProperty("gen0Collections", Order = 10, Required = Required.Always)] long Gen0Collections,
    [property: JsonProperty("gen1Collections", Order = 11, Required = Required.Always)] long Gen1Collections,
    [property: JsonProperty("gen2Collections", Order = 12, Required = Required.Always)] long Gen2Collections
);

[JsonObject(MemberSerialization.OptIn)]
internal sealed record ModHealthHistogram(
    [property: JsonProperty("count", Order = 0, Required = Required.Always)] long Count,
    [property: JsonProperty("sumMilliseconds", Order = 1, Required = Required.Always)] double SumMilliseconds,
    [property: JsonProperty("minimumMilliseconds", Order = 2)] double? MinimumMilliseconds,
    [property: JsonProperty("maximumMilliseconds", Order = 3)] double? MaximumMilliseconds,
    [property: JsonProperty("p50Milliseconds", Order = 4)] double? P50Milliseconds,
    [property: JsonProperty("p95Milliseconds", Order = 5)] double? P95Milliseconds,
    [property: JsonProperty("p99Milliseconds", Order = 6)] double? P99Milliseconds,
    [property: JsonProperty("percentilesApproximate", Order = 7, Required = Required.Always)] bool PercentilesApproximate,
    [property: JsonProperty("maximumRelativeBucketError", Order = 8, Required = Required.Always)] double MaximumRelativeBucketError,
    [property: JsonProperty("underflowCount", Order = 9, Required = Required.Always)] long UnderflowCount,
    [property: JsonProperty("overflowCount", Order = 10, Required = Required.Always)] long OverflowCount,
    [property: JsonProperty("thresholds", Order = 11, Required = Required.Always)] ImmutableArray<ModHealthThresholdCount> Thresholds
);

[JsonObject(MemberSerialization.OptIn)]
internal readonly record struct ModHealthThresholdCount(
    [property: JsonProperty("milliseconds", Order = 0, Required = Required.Always)] double Milliseconds,
    [property: JsonProperty("count", Order = 1, Required = Required.Always)] long Count
);

[JsonObject(MemberSerialization.OptIn)]
internal sealed record ModHealthCallback(
    [property: JsonProperty("modId", Order = 0, Required = Required.Always)] string ModId,
    [property: JsonProperty("modName", Order = 1, Required = Required.Always)] string ModName,
    [property: JsonProperty("phase", Order = 2, Required = Required.Always)] ModHealthExecutionPhase Phase,
    [property: JsonProperty("operation", Order = 3, Required = Required.Always)] ModHealthOperationKind Operation,
    [property: JsonProperty("event", Order = 4, Required = Required.Always)] string Event,
    [property: JsonProperty("callback", Order = 5, Required = Required.Always)] string Callback,
    [property: JsonProperty("onBehalfOfModId", Order = 6)] string? OnBehalfOfModId,
    [property: JsonProperty("callCount", Order = 7, Required = Required.Always)] long CallCount,
    [property: JsonProperty("totalMilliseconds", Order = 8, Required = Required.Always)] double TotalMilliseconds,
    [property: JsonProperty("maximumMilliseconds", Order = 9, Required = Required.Always)] double MaximumMilliseconds,
    [property: JsonProperty("failureCount", Order = 10, Required = Required.Always)] long FailureCount
);

[JsonObject(MemberSerialization.OptIn)]
internal sealed record ModHealthUpdate(
    [property: JsonProperty("updateTick", Order = 0, Required = Required.Always)] uint UpdateTick,
    [property: JsonProperty("offsetMilliseconds", Order = 1, Required = Required.Always)] double OffsetMilliseconds,
    [property: JsonProperty("totalMilliseconds", Order = 2, Required = Required.Always)] double TotalMilliseconds,
    [property: JsonProperty("baseGameExclusiveMilliseconds", Order = 3, Required = Required.Always)] double BaseGameExclusiveMilliseconds,
    [property: JsonProperty("observedModMilliseconds", Order = 4, Required = Required.Always)] double ObservedModMilliseconds,
    [property: JsonProperty("smapiOtherMilliseconds", Order = 5, Required = Required.Always)] double SmapiOtherMilliseconds,
    [property: JsonProperty("residualMilliseconds", Order = 6, Required = Required.Always)] double ResidualMilliseconds,
    [property: JsonProperty("timingValid", Order = 7, Required = Required.Always)] bool TimingValid,
    [property: JsonProperty("phase", Order = 8, Required = Required.Always)] string Phase,
    [property: JsonProperty("focused", Order = 9, Required = Required.Always)] bool Focused,
    [property: JsonProperty("screen", Order = 10, Required = Required.Always)] int Screen,
    [property: JsonProperty("warningCount", Order = 11, Required = Required.Always)] int WarningCount,
    [property: JsonProperty("errorCount", Order = 12, Required = Required.Always)] int ErrorCount,
    [property: JsonProperty("callbackFailureCount", Order = 13, Required = Required.Always)] int CallbackFailureCount,
    [property: JsonProperty("gen0Collections", Order = 14, Required = Required.Always)] int Gen0Collections,
    [property: JsonProperty("gen1Collections", Order = 15, Required = Required.Always)] int Gen1Collections,
    [property: JsonProperty("gen2Collections", Order = 16, Required = Required.Always)] int Gen2Collections,
    [property: JsonProperty("gcCollectionDataValid", Order = 17, Required = Required.Always)] bool GcCollectionDataValid,
    [property: JsonProperty("contributors", Order = 18, Required = Required.Always)] ImmutableArray<ModHealthContributor> Contributors,
    [property: JsonProperty("nearbyMark", Order = 19)] int? NearbyMark
);

[JsonObject(MemberSerialization.OptIn)]
internal readonly record struct ModHealthContributor(
    [property: JsonProperty("modId", Order = 0, Required = Required.Always)] string ModId,
    [property: JsonProperty("milliseconds", Order = 1, Required = Required.Always)] double Milliseconds
);

[JsonObject(MemberSerialization.OptIn)]
internal sealed record ModHealthEpisode(
    [property: JsonProperty("firstUpdateTick", Order = 0, Required = Required.Always)] uint FirstUpdateTick,
    [property: JsonProperty("lastUpdateTick", Order = 1, Required = Required.Always)] uint LastUpdateTick,
    [property: JsonProperty("qualifyingUpdateCount", Order = 2, Required = Required.Always)] int QualifyingUpdateCount,
    [property: JsonProperty("maximumMilliseconds", Order = 3, Required = Required.Always)] double MaximumMilliseconds,
    [property: JsonProperty("summedQualifyingMilliseconds", Order = 4, Required = Required.Always)] double SummedQualifyingMilliseconds,
    [property: JsonProperty("representativeUpdateTick", Order = 5, Required = Required.Always)] uint RepresentativeUpdateTick,
    [property: JsonProperty("nearbyMark", Order = 6)] int? NearbyMark
);

[JsonObject(MemberSerialization.OptIn)]
internal sealed record ModHealthMod(
    [property: JsonProperty("id", Order = 0, Required = Required.Always)] string Id,
    [property: JsonProperty("name", Order = 1, Required = Required.Always)] string Name,
    [property: JsonProperty("version", Order = 2, Required = Required.Always)] string Version,
    [property: JsonProperty("kind", Order = 3, Required = Required.Always)] ModHealthModKind Kind,
    [property: JsonProperty("parentId", Order = 4)] string? ParentId,
    [property: JsonProperty("status", Order = 5, Required = Required.Always)] ModHealthModStatus Status,
    [property: JsonProperty("failureCategory", Order = 6)] string? FailureCategory,
    [property: JsonProperty("warningFlags", Order = 7, Required = Required.Always)] ImmutableArray<string> WarningFlags,
    [property: JsonProperty("dependencies", Order = 8, Required = Required.Always)] ImmutableArray<string> Dependencies,
    [property: JsonProperty("updateStatus", Order = 9, Required = Required.Always)] ModHealthReportUpdateStatus UpdateStatus,
    [property: JsonProperty("suggestedUpdateVersion", Order = 10)] string? SuggestedUpdateVersion,
    [property: JsonProperty("sessionWarningCount", Order = 11, Required = Required.Always)] long SessionWarningCount,
    [property: JsonProperty("sessionErrorCount", Order = 12, Required = Required.Always)] long SessionErrorCount,
    [property: JsonProperty("captureErrorCount", Order = 13, Required = Required.Always)] long CaptureErrorCount,
    [property: JsonProperty("callbackFailureCount", Order = 14, Required = Required.Always)] long CallbackFailureCount,
    [property: JsonProperty("observedCallbackMilliseconds", Order = 15, Required = Required.Always)] double ObservedCallbackMilliseconds,
    [property: JsonProperty("observedCallbackPeakMilliseconds", Order = 16, Required = Required.Always)] double ObservedCallbackPeakMilliseconds,
    [property: JsonProperty("observedCallbackCount", Order = 17, Required = Required.Always)] long ObservedCallbackCount,
    [property: JsonProperty("observedCallbackFailureCount", Order = 18, Required = Required.Always)] long ObservedCallbackFailureCount,
    [property: JsonProperty("slowUpdateParticipationCount", Order = 19, Required = Required.Always)] long SlowUpdateParticipationCount,
    [property: JsonProperty("instrumentedTimeShare", Order = 20, Required = Required.Always)] double InstrumentedTimeShare,
    [property: JsonProperty("peakMessagesPerSecond", Order = 21, Required = Required.Always)] long PeakMessagesPerSecond,
    [property: JsonProperty("peakCharactersPerSecond", Order = 22, Required = Required.Always)] long PeakCharactersPerSecond
);

[JsonObject(MemberSerialization.OptIn)]
internal sealed record ModHealthModInventorySummary(
    [property: JsonProperty("totalDiscovered", Order = 0, Required = Required.Always)] long TotalDiscovered,
    [property: JsonProperty("discovered", Order = 1, Required = Required.Always)] long Discovered,
    [property: JsonProperty("loaded", Order = 2, Required = Required.Always)] long Loaded,
    [property: JsonProperty("skipped", Order = 3, Required = Required.Always)] long Skipped,
    [property: JsonProperty("ignored", Order = 4, Required = Required.Always)] long Ignored,
    [property: JsonProperty("invalid", Order = 5, Required = Required.Always)] long Invalid,
    [property: JsonProperty("failed", Order = 6, Required = Required.Always)] long Failed,
    [property: JsonProperty("retained", Order = 7, Required = Required.Always)] int Retained
);

[JsonObject(MemberSerialization.OptIn)]
internal sealed record ModHealthLogSummary(
    [property: JsonProperty("source", Order = 0, Required = Required.Always)] string Source,
    [property: JsonProperty("sourceCategory", Order = 1, Required = Required.Always)] ModHealthReportLogSourceCategory SourceCategory,
    [property: JsonProperty("sinceLedgerStart", Order = 2, Required = Required.Always)] ModHealthLogSeveritySummary SinceLedgerStart,
    [property: JsonProperty("duringCapture", Order = 3, Required = Required.Always)] ModHealthLogSeveritySummary DuringCapture,
    [property: JsonProperty("peakMessagesPerSecond", Order = 4, Required = Required.Always)] long PeakMessagesPerSecond,
    [property: JsonProperty("peakCharactersPerSecond", Order = 5, Required = Required.Always)] long PeakCharactersPerSecond,
    [property: JsonProperty("firstOffsetMilliseconds", Order = 6)] double? FirstOffsetMilliseconds,
    [property: JsonProperty("lastOffsetMilliseconds", Order = 7)] double? LastOffsetMilliseconds
);

[JsonObject(MemberSerialization.OptIn)]
internal sealed record ModHealthLogSeveritySummary(
    [property: JsonProperty("traceMessages", Order = 0, Required = Required.Always)] long TraceMessages,
    [property: JsonProperty("traceCharacters", Order = 1, Required = Required.Always)] long TraceCharacters,
    [property: JsonProperty("debugMessages", Order = 2, Required = Required.Always)] long DebugMessages,
    [property: JsonProperty("debugCharacters", Order = 3, Required = Required.Always)] long DebugCharacters,
    [property: JsonProperty("infoMessages", Order = 4, Required = Required.Always)] long InfoMessages,
    [property: JsonProperty("infoCharacters", Order = 5, Required = Required.Always)] long InfoCharacters,
    [property: JsonProperty("warningMessages", Order = 6, Required = Required.Always)] long WarningMessages,
    [property: JsonProperty("warningCharacters", Order = 7, Required = Required.Always)] long WarningCharacters,
    [property: JsonProperty("errorMessages", Order = 8, Required = Required.Always)] long ErrorMessages,
    [property: JsonProperty("errorCharacters", Order = 9, Required = Required.Always)] long ErrorCharacters,
    [property: JsonProperty("alertMessages", Order = 10, Required = Required.Always)] long AlertMessages,
    [property: JsonProperty("alertCharacters", Order = 11, Required = Required.Always)] long AlertCharacters
)
{
    [JsonIgnore]
    public long TotalMessages => Sum(this.TraceMessages, this.DebugMessages, this.InfoMessages, this.WarningMessages, this.ErrorMessages, this.AlertMessages);

    [JsonIgnore]
    public long TotalCharacters => Sum(this.TraceCharacters, this.DebugCharacters, this.InfoCharacters, this.WarningCharacters, this.ErrorCharacters, this.AlertCharacters);

    private static long Sum(params long[] values)
    {
        long total = 0;
        foreach (long value in values)
        {
            if (value > 0 && total > long.MaxValue - value)
                return long.MaxValue;
            total += Math.Max(0, value);
        }
        return total;
    }
}

[JsonObject(MemberSerialization.OptIn)]
internal sealed record ModHealthLogTotals(
    [property: JsonProperty("sinceLedgerStart", Order = 0, Required = Required.Always)] ModHealthLogSeveritySummary SinceLedgerStart,
    [property: JsonProperty("duringCapture", Order = 1, Required = Required.Always)] ModHealthLogSeveritySummary DuringCapture
);

[JsonObject(MemberSerialization.OptIn)]
internal sealed record ModHealthCallbackFailure(
    [property: JsonProperty("modId", Order = 0, Required = Required.Always)] string ModId,
    [property: JsonProperty("modName", Order = 1, Required = Required.Always)] string ModName,
    [property: JsonProperty("phase", Order = 2, Required = Required.Always)] ModHealthExecutionPhase Phase,
    [property: JsonProperty("operation", Order = 3, Required = Required.Always)] ModHealthOperationKind Operation,
    [property: JsonProperty("callback", Order = 4, Required = Required.Always)] string Callback,
    [property: JsonProperty("exceptionType", Order = 5, Required = Required.Always)] string ExceptionType,
    [property: JsonProperty("onBehalfOfModId", Order = 6)] string? OnBehalfOfModId,
    [property: JsonProperty("sessionCount", Order = 7, Required = Required.Always)] long SessionCount,
    [property: JsonProperty("captureCount", Order = 8, Required = Required.Always)] long CaptureCount,
    [property: JsonProperty("firstOffsetMilliseconds", Order = 9, Required = Required.Always)] double FirstOffsetMilliseconds,
    [property: JsonProperty("lastOffsetMilliseconds", Order = 10, Required = Required.Always)] double LastOffsetMilliseconds
);

[JsonObject(MemberSerialization.OptIn)]
internal sealed record ModHealthCallbackFailureTotals(
    [property: JsonProperty("sinceLedgerStart", Order = 0, Required = Required.Always)] long SinceLedgerStart,
    [property: JsonProperty("duringCapture", Order = 1, Required = Required.Always)] long DuringCapture
);

[JsonObject(MemberSerialization.OptIn)]
internal sealed record ModHealthFinding(
    [property: JsonProperty("ruleId", Order = 0, Required = Required.Always)] string RuleId,
    [property: JsonProperty("severity", Order = 1, Required = Required.Always)] ModHealthFindingSeverity Severity,
    [property: JsonProperty("confidence", Order = 2, Required = Required.Always)] ModHealthFindingConfidence Confidence,
    [property: JsonProperty("modId", Order = 3)] string? ModId,
    [property: JsonProperty("summary", Order = 4, Required = Required.Always)] string Summary,
    [property: JsonProperty("evidence", Order = 5, Required = Required.Always)] string Evidence,
    [property: JsonProperty("suggestedAction", Order = 6, Required = Required.Always)] string SuggestedAction,
    [property: JsonProperty("limitation", Order = 7, Required = Required.Always)] string Limitation
);

[JsonObject(MemberSerialization.OptIn)]
internal readonly record struct ModHealthCapacity(
    [property: JsonProperty("name", Order = 0, Required = Required.Always)] string Name,
    [property: JsonProperty("limit", Order = 1, Required = Required.Always)] long Limit,
    [property: JsonProperty("reached", Order = 2, Required = Required.Always)] bool Reached
);

[JsonObject(MemberSerialization.OptIn)]
internal readonly record struct ModHealthOmission(
    [property: JsonProperty("section", Order = 0, Required = Required.Always)] string Section,
    [property: JsonProperty("count", Order = 1, Required = Required.Always)] long Count
);

[JsonObject(MemberSerialization.OptIn)]
internal sealed record ModHealthPrivacy(
    [property: JsonProperty("inspectBeforeSharing", Order = 0, Required = Required.Always)] bool InspectBeforeSharing,
    [property: JsonProperty("automaticUpload", Order = 1, Required = Required.Always)] bool AutomaticUpload,
    [property: JsonProperty("includedIdentityFields", Order = 2, Required = Required.Always)] ImmutableArray<string> IncludedIdentityFields,
    [property: JsonProperty("excludedSources", Order = 3, Required = Required.Always)] ImmutableArray<string> ExcludedSources
);

[JsonConverter(typeof(StringEnumConverter))]
internal enum ModHealthCaptureMode
{
    [EnumMember(Value = "ledger-only")]
    LedgerOnly,
    [EnumMember(Value = "health")]
    Health,
    [EnumMember(Value = "performance")]
    Performance
}

[JsonConverter(typeof(StringEnumConverter))]
internal enum ModHealthCompletionReason
{
    [EnumMember(Value = "not-stopped")]
    NotStopped,
    [EnumMember(Value = "user-stop")]
    UserStop,
    [EnumMember(Value = "performance-stop")]
    PerformanceStop,
    [EnumMember(Value = "normal-shutdown")]
    NormalShutdown,
    [EnumMember(Value = "interim-report")]
    InterimReport
}

[JsonConverter(typeof(StringEnumConverter))]
internal enum ModHealthExecutionPhase
{
    [EnumMember(Value = "startup")]
    Startup,
    [EnumMember(Value = "update")]
    Update,
    [EnumMember(Value = "draw")]
    Draw,
    [EnumMember(Value = "background")]
    Background,
    [EnumMember(Value = "unscoped")]
    Unscoped
}

[JsonConverter(typeof(StringEnumConverter))]
internal enum ModHealthOperationKind
{
    [EnumMember(Value = ModHealthReportLabels.Event)]
    Event,
    [EnumMember(Value = ModHealthReportLabels.ContentLoad)]
    ContentLoad,
    [EnumMember(Value = ModHealthReportLabels.ContentEdit)]
    ContentEdit,
    [EnumMember(Value = ModHealthReportLabels.Console)]
    Console,
    [EnumMember(Value = ModHealthReportLabels.Entry)]
    Entry,
    [EnumMember(Value = ModHealthReportLabels.GetApi)]
    GetApi,
    [EnumMember(Value = ModHealthReportLabels.Other)]
    Other
}

[JsonConverter(typeof(StringEnumConverter))]
internal enum ModHealthModKind
{
    [EnumMember(Value = "code-mod")]
    CodeMod,
    [EnumMember(Value = "content-pack")]
    ContentPack,
    [EnumMember(Value = "invalid")]
    Invalid
}

[JsonConverter(typeof(StringEnumConverter))]
internal enum ModHealthModStatus
{
    [EnumMember(Value = "discovered")]
    Discovered,
    [EnumMember(Value = "loaded")]
    Loaded,
    [EnumMember(Value = "skipped")]
    Skipped,
    [EnumMember(Value = "ignored")]
    Ignored,
    [EnumMember(Value = "invalid")]
    Invalid,
    [EnumMember(Value = "failed")]
    Failed
}

[JsonConverter(typeof(StringEnumConverter))]
internal enum ModHealthReportUpdateStatus
{
    [EnumMember(Value = "unknown")]
    Unknown,
    [EnumMember(Value = "pending")]
    Pending,
    [EnumMember(Value = "up-to-date")]
    UpToDate,
    [EnumMember(Value = "update-available")]
    UpdateAvailable,
    [EnumMember(Value = "disabled")]
    Disabled,
    [EnumMember(Value = "suppressed")]
    Suppressed,
    [EnumMember(Value = "unavailable")]
    Unavailable
}

[JsonConverter(typeof(StringEnumConverter))]
internal enum ModHealthReportLogSourceCategory
{
    [EnumMember(Value = "mod")]
    Mod,
    [EnumMember(Value = "smapi")]
    Smapi,
    [EnumMember(Value = "game")]
    Game
}

[JsonConverter(typeof(StringEnumConverter))]
internal enum ModHealthFindingSeverity
{
    [EnumMember(Value = "action-needed")]
    ActionNeeded,
    [EnumMember(Value = "performance")]
    Performance,
    [EnumMember(Value = "check")]
    Check,
    [EnumMember(Value = "info")]
    Info
}

[JsonConverter(typeof(StringEnumConverter))]
internal enum ModHealthFindingConfidence
{
    [EnumMember(Value = "factual")]
    Factual,
    [EnumMember(Value = "likely")]
    Likely,
    [EnumMember(Value = "possible")]
    Possible,
    [EnumMember(Value = "limited")]
    Limited
}
