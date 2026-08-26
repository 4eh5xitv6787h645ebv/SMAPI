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
    [property: JsonProperty("mods", Order = 7, Required = Required.Always)] ImmutableArray<ModHealthMod> Mods,
    [property: JsonProperty("logs", Order = 8, Required = Required.Always)] ImmutableArray<ModHealthLogSummary> Logs,
    [property: JsonProperty("capacities", Order = 9, Required = Required.Always)] ImmutableArray<ModHealthCapacity> Capacities,
    [property: JsonProperty("omissions", Order = 10, Required = Required.Always)] ImmutableArray<ModHealthOmission> Omissions,
    [property: JsonProperty("privacy", Order = 11, Required = Required.Always)] ModHealthPrivacy Privacy,
    [property: JsonProperty("limitations", Order = 12, Required = Required.Always)] ImmutableArray<string> Limitations
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
    [property: JsonProperty("linuxDistribution", Order = 5)] string? LinuxDistribution,
    [property: JsonProperty("kernel", Order = 6)] string? Kernel,
    [property: JsonProperty("sessionType", Order = 7, Required = Required.Always)] string SessionType,
    [property: JsonProperty("locale", Order = 8, Required = Required.Always)] string Locale,
    [property: JsonProperty("logicalProcessorCount", Order = 9, Required = Required.Always)] int LogicalProcessorCount,
    [property: JsonProperty("multiplayerRole", Order = 10, Required = Required.Always)] string MultiplayerRole,
    [property: JsonProperty("splitScreenCount", Order = 11, Required = Required.Always)] int SplitScreenCount
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
    [property: JsonProperty("callback", Order = 4, Required = Required.Always)] string Callback,
    [property: JsonProperty("onBehalfOfModId", Order = 5)] string? OnBehalfOfModId,
    [property: JsonProperty("callCount", Order = 6, Required = Required.Always)] long CallCount,
    [property: JsonProperty("totalMilliseconds", Order = 7, Required = Required.Always)] double TotalMilliseconds,
    [property: JsonProperty("maximumMilliseconds", Order = 8, Required = Required.Always)] double MaximumMilliseconds,
    [property: JsonProperty("failureCount", Order = 9, Required = Required.Always)] long FailureCount
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
    [property: JsonProperty("contributors", Order = 14, Required = Required.Always)] ImmutableArray<ModHealthContributor> Contributors,
    [property: JsonProperty("nearbyMark", Order = 15)] int? NearbyMark
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
    [property: JsonProperty("dependencies", Order = 7, Required = Required.Always)] ImmutableArray<string> Dependencies,
    [property: JsonProperty("suggestedUpdateVersion", Order = 8)] string? SuggestedUpdateVersion,
    [property: JsonProperty("sessionWarningCount", Order = 9, Required = Required.Always)] long SessionWarningCount,
    [property: JsonProperty("sessionErrorCount", Order = 10, Required = Required.Always)] long SessionErrorCount,
    [property: JsonProperty("captureErrorCount", Order = 11, Required = Required.Always)] long CaptureErrorCount,
    [property: JsonProperty("callbackFailureCount", Order = 12, Required = Required.Always)] long CallbackFailureCount,
    [property: JsonProperty("peakMessagesPerSecond", Order = 13, Required = Required.Always)] long PeakMessagesPerSecond,
    [property: JsonProperty("peakCharactersPerSecond", Order = 14, Required = Required.Always)] long PeakCharactersPerSecond
);

[JsonObject(MemberSerialization.OptIn)]
internal sealed record ModHealthLogSummary(
    [property: JsonProperty("source", Order = 0, Required = Required.Always)] string Source,
    [property: JsonProperty("traceCount", Order = 1, Required = Required.Always)] long TraceCount,
    [property: JsonProperty("debugCount", Order = 2, Required = Required.Always)] long DebugCount,
    [property: JsonProperty("infoCount", Order = 3, Required = Required.Always)] long InfoCount,
    [property: JsonProperty("warningCount", Order = 4, Required = Required.Always)] long WarningCount,
    [property: JsonProperty("errorCount", Order = 5, Required = Required.Always)] long ErrorCount,
    [property: JsonProperty("characterCount", Order = 6, Required = Required.Always)] long CharacterCount,
    [property: JsonProperty("firstOffsetMilliseconds", Order = 7)] double? FirstOffsetMilliseconds,
    [property: JsonProperty("lastOffsetMilliseconds", Order = 8)] double? LastOffsetMilliseconds
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
    [EnumMember(Value = "event")]
    Event,
    [EnumMember(Value = "content-load")]
    ContentLoad,
    [EnumMember(Value = "content-edit")]
    ContentEdit,
    [EnumMember(Value = "console")]
    Console,
    [EnumMember(Value = "entry")]
    Entry,
    [EnumMember(Value = "get-api")]
    GetApi,
    [EnumMember(Value = "other")]
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
