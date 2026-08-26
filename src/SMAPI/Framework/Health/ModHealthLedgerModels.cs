using System;
using System.Collections.Generic;

namespace StardewModdingAPI.Framework.Health;

/// <summary>How much of the process lifetime is covered by a mod health ledger.</summary>
internal enum ModHealthLedgerCompleteness
{
    /// <summary>The ledger began during managed SMAPI core initialization. Launcher, native, and earlier managed failures aren't observed.</summary>
    ManagedCoreInitialization
}

/// <summary>The broad source which emitted a log message.</summary>
internal enum ModHealthLogSourceCategory
{
    Mod,
    Smapi,
    Game,
    Reporter
}

/// <summary>The kind of discovered mod entry.</summary>
internal enum ModHealthLedgerModKind
{
    Unknown,
    CodeMod,
    ContentPack
}

/// <summary>The current load state for a discovered mod entry.</summary>
internal enum ModHealthLedgerModStatus
{
    Discovered,
    Loaded,
    Skipped,
    Ignored,
    Invalid,
    Failed
}

/// <summary>A structured reason why a mod wasn't loaded.</summary>
internal enum ModHealthModFailureReason
{
    None,
    DisabledByConvention,
    Duplicate,
    EmptyFolder,
    Incompatible,
    InvalidManifest,
    LoadFailed,
    Malicious,
    MissingDependencies,
    Obsolete,
    XnbMod,
    Unknown
}

/// <summary>The state of the already-requested update check for a mod.</summary>
internal enum ModHealthUpdateStatus
{
    Unknown,
    Pending,
    UpToDate,
    UpdateAvailable,
    Disabled,
    Suppressed,
    Unavailable
}

/// <summary>An opaque key assigned to one discovered mod entry.</summary>
internal readonly record struct ModHealthModKey(long Value);

/// <summary>Safe mod fields presented to the ledger. The ledger sanitizes and copies every field before retaining it.</summary>
internal readonly record struct ModHealthModObservation(
    bool HasValidManifest,
    string? UniqueId,
    string? DisplayName,
    string? Version,
    ModHealthLedgerModKind Kind,
    string? ParentId,
    IReadOnlyList<string>? DependencyIds,
    ModHealthLedgerModStatus Status,
    ModHealthModFailureReason FailureReason,
    ulong WarningFlags,
    ModHealthUpdateStatus UpdateStatus,
    string? SuggestedUpdateVersion
);

/// <summary>Safe metadata for one emitted message. Message text is deliberately not accepted.</summary>
internal readonly record struct ModHealthLogObservation(
    string? ModId,
    string? ModName,
    ModHealthLogSourceCategory SourceCategory,
    LogLevel Level,
    int MessageLength,
    int ManagedThreadId
);

/// <summary>Safe structured metadata for a callback failure. Exception messages and stack traces are deliberately not accepted.</summary>
internal readonly record struct ModHealthCallbackFailureObservation(
    string? ModId,
    string? ModName,
    ModHealthExecutionPhase Phase,
    ModHealthOperationKind Operation,
    string? CallbackIdentity,
    string? ExceptionType,
    string? OnBehalfOfModId,
    int ManagedThreadId
);

/// <summary>Message and approximate character counts by severity.</summary>
internal sealed record ModHealthSeverityCountsSnapshot(
    long TraceMessages,
    long TraceCharacters,
    long DebugMessages,
    long DebugCharacters,
    long InfoMessages,
    long InfoCharacters,
    long WarningMessages,
    long WarningCharacters,
    long ErrorMessages,
    long ErrorCharacters,
    long AlertMessages,
    long AlertCharacters
)
{
    /// <summary>Get the count for one severity.</summary>
    public long GetMessages(LogLevel level)
    {
        return level switch
        {
            LogLevel.Trace => this.TraceMessages,
            LogLevel.Debug => this.DebugMessages,
            LogLevel.Info => this.InfoMessages,
            LogLevel.Warn => this.WarningMessages,
            LogLevel.Error => this.ErrorMessages,
            LogLevel.Alert => this.AlertMessages,
            _ => 0
        };
    }

    /// <summary>Get the approximate character count for one severity.</summary>
    public long GetCharacters(LogLevel level)
    {
        return level switch
        {
            LogLevel.Trace => this.TraceCharacters,
            LogLevel.Debug => this.DebugCharacters,
            LogLevel.Info => this.InfoCharacters,
            LogLevel.Warn => this.WarningCharacters,
            LogLevel.Error => this.ErrorCharacters,
            LogLevel.Alert => this.AlertCharacters,
            _ => 0
        };
    }
}

/// <summary>A retained discovered-mod record.</summary>
internal sealed record ModHealthModSnapshot(
    ModHealthModKey Key,
    string UniqueId,
    string DisplayName,
    string? Version,
    ModHealthLedgerModKind Kind,
    string? ParentId,
    IReadOnlyList<string> DependencyIds,
    ModHealthLedgerModStatus Status,
    ModHealthModFailureReason FailureReason,
    ulong WarningFlags,
    ModHealthUpdateStatus UpdateStatus,
    string? SuggestedUpdateVersion,
    bool UsesGeneratedInvalidIdentity
);

/// <summary>Log-volume evidence for one safe source identity.</summary>
internal sealed record ModHealthLogSourceSnapshot(
    string ModId,
    string ModName,
    ModHealthLogSourceCategory SourceCategory,
    ModHealthSeverityCountsSnapshot SinceLedgerStart,
    ModHealthSeverityCountsSnapshot DuringCapture,
    long FirstSequence,
    long LastSequence,
    TimeSpan FirstOffset,
    TimeSpan LastOffset,
    int LastManagedThreadId,
    long PeakMessagesPerSecond,
    long PeakCharactersPerSecond
);

/// <summary>Aggregate evidence for one structured callback-failure identity.</summary>
internal sealed record ModHealthCallbackFailureSnapshot(
    string ModId,
    string ModName,
    ModHealthExecutionPhase Phase,
    ModHealthOperationKind Operation,
    string CallbackIdentity,
    string ExceptionType,
    string? OnBehalfOfModId,
    long SinceLedgerStartCount,
    long DuringCaptureCount,
    long FirstSequence,
    long LastSequence,
    TimeSpan FirstOffset,
    TimeSpan LastOffset,
    int LastManagedThreadId
);

/// <summary>Explicit ledger collection limits.</summary>
internal sealed record ModHealthLedgerCapacities(int ModInventory, int LogIdentities, int CallbackFailureSignatures, int DependenciesPerMod);

/// <summary>Counts for evidence omitted because a bounded collection was full.</summary>
internal sealed record ModHealthLedgerOmissions(long ModInventoryRecords, long LogIdentityObservations, long CallbackFailureObservations, long DependencyIds);

/// <summary>An immutable snapshot of the bounded session health ledger.</summary>
internal sealed record ModHealthLedgerSnapshot(
    DateTime StartedUtc,
    ModHealthLedgerCompleteness Completeness,
    long CutoffSequence,
    long? CaptureBaselineSequence,
    long TotalDiscoveredMods,
    IReadOnlyDictionary<ModHealthLedgerModStatus, long> ModStatusTotals,
    IReadOnlyList<ModHealthModSnapshot> Mods,
    ModHealthSeverityCountsSnapshot LogTotalsSinceLedgerStart,
    ModHealthSeverityCountsSnapshot LogTotalsDuringCapture,
    IReadOnlyList<ModHealthLogSourceSnapshot> LogSources,
    long CallbackFailuresSinceLedgerStart,
    long CallbackFailuresDuringCapture,
    IReadOnlyList<ModHealthCallbackFailureSnapshot> CallbackFailures,
    ModHealthLedgerCapacities Capacities,
    ModHealthLedgerOmissions Omissions
);

/// <summary>An immutable counter baseline used to calculate evidence observed during a timed capture.</summary>
internal sealed class ModHealthLedgerBaseline
{
    private readonly long[] TotalLogCounts;
    private readonly Dictionary<ModHealthLogIdentity, long[]> LogCounts;
    private readonly Dictionary<ModHealthFailureIdentity, long> FailureCounts;

    internal long Sequence { get; }
    internal long TotalFailureCount { get; }

    internal ModHealthLedgerBaseline(long sequence, long[] totalLogCounts, Dictionary<ModHealthLogIdentity, long[]> logCounts, long totalFailureCount, Dictionary<ModHealthFailureIdentity, long> failureCounts)
    {
        this.Sequence = sequence;
        this.TotalLogCounts = totalLogCounts;
        this.LogCounts = logCounts;
        this.TotalFailureCount = totalFailureCount;
        this.FailureCounts = failureCounts;
    }

    /// <summary>Get a baseline total log counter.</summary>
    internal long GetTotalLogCount(int index)
    {
        return index >= 0 && index < this.TotalLogCounts.Length ? this.TotalLogCounts[index] : 0;
    }

    /// <summary>Get a baseline log counter for one retained source identity.</summary>
    internal long GetLogCount(ModHealthLogIdentity identity, int index)
    {
        return this.LogCounts.TryGetValue(identity, out long[]? counts) && index >= 0 && index < counts.Length
            ? counts[index]
            : 0;
    }

    /// <summary>Get a baseline callback-failure counter for one retained signature.</summary>
    internal long GetFailureCount(ModHealthFailureIdentity identity)
    {
        return this.FailureCounts.TryGetValue(identity, out long count) ? count : 0;
    }
}

/// <summary>A normalized log source identity.</summary>
internal readonly record struct ModHealthLogIdentity(string ModId, string ModName, ModHealthLogSourceCategory SourceCategory);

/// <summary>A normalized structured callback-failure identity.</summary>
internal readonly record struct ModHealthFailureIdentity(
    string ModId,
    string ModName,
    ModHealthExecutionPhase Phase,
    ModHealthOperationKind Operation,
    string CallbackIdentity,
    string ExceptionType,
    string? OnBehalfOfModId
);
