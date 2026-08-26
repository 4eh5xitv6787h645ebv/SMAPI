using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace StardewModdingAPI.Framework.Health;

/// <summary>Collects bounded, message-free session evidence for a mod health report.</summary>
internal sealed class ModHealthLedger
{
    /*********
    ** Constants
    *********/
    private const int SeverityCount = 6;
    private const int IdentityMaxLength = 256;
    private const int CallbackMaxLength = 1024;


    /*********
    ** Fields
    *********/
    private readonly object SyncRoot = new();
    private readonly Func<long> GetTimestamp;
    private readonly long TimestampFrequency;
    private readonly long StartedTimestamp;
    private readonly int ModCapacity;
    private readonly int LogIdentityCapacity;
    private readonly int FailureCapacity;
    private readonly int DependencyCapacity;
    private readonly Dictionary<long, MutableModRecord> Mods = [];
    private readonly LinkedList<long>[] ModKeysByPriority = [new(), new(), new(), new()];
    private readonly Dictionary<ModHealthLogIdentity, MutableLogRecord> Logs = new(ModHealthLogIdentityComparer.Instance);
    private readonly Dictionary<ModHealthFailureIdentity, MutableFailureRecord> Failures = new(ModHealthFailureIdentityComparer.Instance);
    private readonly long[] TotalLogCounts = new long[SeverityCount * 2];
    private readonly long[] ModStatusTotals = new long[Enum.GetValues<ModHealthLedgerModStatus>().Length];
    private long Sequence;
    private long NextModKey;
    private long TotalDiscoveredMods;
    private long TotalFailureCount;
    private long LogIdentityObservationsOmitted;
    private long FailureObservationsOmitted;
    private long DependenciesOmitted;


    /*********
    ** Accessors
    *********/
    /// <summary>The UTC instant when managed ledger collection began.</summary>
    public DateTime StartedUtc { get; }

    /// <summary>The known completeness boundary for this ledger.</summary>
    public ModHealthLedgerCompleteness Completeness { get; }

    /// <summary>The configured bounded collection capacities.</summary>
    public ModHealthLedgerCapacities Capacities { get; }


    /*********
    ** Public methods
    *********/
    /// <summary>Construct a new session ledger.</summary>
    public ModHealthLedger(
        int modCapacity = 4096,
        int logIdentityCapacity = 4096,
        int failureCapacity = 1024,
        int dependencyCapacity = 256,
        DateTime? startedUtc = null,
        long? timestampFrequency = null,
        Func<long>? getTimestamp = null
    )
    {
        if (modCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(modCapacity));
        if (logIdentityCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(logIdentityCapacity));
        if (failureCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(failureCapacity));
        if (dependencyCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(dependencyCapacity));

        this.ModCapacity = modCapacity;
        this.LogIdentityCapacity = logIdentityCapacity;
        this.FailureCapacity = failureCapacity;
        this.DependencyCapacity = dependencyCapacity;
        this.StartedUtc = (startedUtc ?? DateTime.UtcNow).ToUniversalTime();
        this.Completeness = ModHealthLedgerCompleteness.ManagedCoreInitialization;
        this.TimestampFrequency = timestampFrequency ?? Stopwatch.Frequency;
        this.GetTimestamp = getTimestamp ?? Stopwatch.GetTimestamp;
        if (this.TimestampFrequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(timestampFrequency));
        this.StartedTimestamp = this.GetTimestamp();
        this.Capacities = new ModHealthLedgerCapacities(modCapacity, logIdentityCapacity, failureCapacity, dependencyCapacity);
    }

    /// <summary>Register one discovered mod entry and return its opaque ledger key.</summary>
    public ModHealthModKey RegisterMod(ModHealthModObservation observation)
    {
        lock (this.SyncRoot)
        {
            this.AdvanceSequence();
            long rawKey = this.NextModKey == long.MaxValue ? long.MaxValue : ++this.NextModKey;
            ModHealthModKey key = new(rawKey);
            this.TotalDiscoveredMods = SaturatingIncrement(this.TotalDiscoveredMods);
            this.ModStatusTotals[(int)observation.Status] = SaturatingIncrement(this.ModStatusTotals[(int)observation.Status]);

            MutableModRecord record = this.CreateModRecord(key, observation);
            this.TryRetainMod(record);
            return key;
        }
    }

    /// <summary>Record a changed status or update state for a previously discovered mod.</summary>
    /// <remarks>The caller supplies the previous status so aggregate counts remain bounded even if the entry itself wasn't retained.</remarks>
    public void UpdateMod(ModHealthModKey key, ModHealthLedgerModStatus previousStatus, ModHealthModObservation observation)
    {
        lock (this.SyncRoot)
        {
            this.AdvanceSequence();
            if (previousStatus != observation.Status)
            {
                int previousIndex = (int)previousStatus;
                if (this.ModStatusTotals[previousIndex] > 0)
                    this.ModStatusTotals[previousIndex]--;
                this.ModStatusTotals[(int)observation.Status] = SaturatingIncrement(this.ModStatusTotals[(int)observation.Status]);
            }

            MutableModRecord replacement = this.CreateModRecord(key, observation);
            if (this.Mods.TryGetValue(key.Value, out MutableModRecord? existing))
            {
                this.RemoveRetainedMod(existing);
                this.TryRetainMod(replacement);
            }
            else
                this.TryRetainMod(replacement);
        }
    }

    /// <summary>Record one log event without accepting its message text.</summary>
    /// <returns>The monotonic ledger sequence for the observation, or the current sequence when reporter output was excluded.</returns>
    public long ObserveLog(in ModHealthLogObservation observation)
    {
        if (observation.SourceCategory == ModHealthLogSourceCategory.Reporter)
        {
            lock (this.SyncRoot)
                return this.Sequence;
        }

        lock (this.SyncRoot)
        {
            long sequence = this.AdvanceSequence();
            long timestamp = this.GetTimestamp();
            int severity = GetSeverityIndex(observation.Level);
            int countIndex = severity * 2;
            long characterCount = Math.Max(0, observation.MessageLength);
            this.TotalLogCounts[countIndex] = SaturatingIncrement(this.TotalLogCounts[countIndex]);
            this.TotalLogCounts[countIndex + 1] = SaturatingAdd(this.TotalLogCounts[countIndex + 1], characterCount);

            ModHealthLogIdentity identity = CreateLogIdentity(observation);
            if (!this.Logs.TryGetValue(identity, out MutableLogRecord? record))
            {
                if (this.Logs.Count >= this.LogIdentityCapacity)
                {
                    this.LogIdentityObservationsOmitted = SaturatingIncrement(this.LogIdentityObservationsOmitted);
                    return sequence;
                }

                this.Logs[identity] = record = new MutableLogRecord(identity, sequence, timestamp);
            }

            record.Counts[countIndex] = SaturatingIncrement(record.Counts[countIndex]);
            record.Counts[countIndex + 1] = SaturatingAdd(record.Counts[countIndex + 1], characterCount);
            record.LastSequence = sequence;
            record.LastTimestamp = timestamp;
            record.LastManagedThreadId = observation.ManagedThreadId;
            return sequence;
        }
    }

    /// <summary>Record one structured callback failure without accepting an exception message or stack trace.</summary>
    public long ObserveCallbackFailure(in ModHealthCallbackFailureObservation observation)
    {
        lock (this.SyncRoot)
        {
            long sequence = this.AdvanceSequence();
            long timestamp = this.GetTimestamp();
            this.TotalFailureCount = SaturatingIncrement(this.TotalFailureCount);
            ModHealthFailureIdentity identity = CreateFailureIdentity(observation);

            if (!this.Failures.TryGetValue(identity, out MutableFailureRecord? record))
            {
                if (this.Failures.Count >= this.FailureCapacity)
                {
                    this.FailureObservationsOmitted = SaturatingIncrement(this.FailureObservationsOmitted);
                    return sequence;
                }

                this.Failures[identity] = record = new MutableFailureRecord(identity, sequence, timestamp);
            }

            record.Count = SaturatingIncrement(record.Count);
            record.LastSequence = sequence;
            record.LastTimestamp = timestamp;
            record.LastManagedThreadId = observation.ManagedThreadId;
            return sequence;
        }
    }

    /// <summary>Freeze counter values used to calculate evidence observed during a timed capture.</summary>
    public ModHealthLedgerBaseline CreateCaptureBaseline()
    {
        lock (this.SyncRoot)
        {
            Dictionary<ModHealthLogIdentity, long[]> logs = new(this.Logs.Count, ModHealthLogIdentityComparer.Instance);
            foreach ((ModHealthLogIdentity identity, MutableLogRecord record) in this.Logs)
                logs[identity] = (long[])record.Counts.Clone();

            Dictionary<ModHealthFailureIdentity, long> failures = new(this.Failures.Count, ModHealthFailureIdentityComparer.Instance);
            foreach ((ModHealthFailureIdentity identity, MutableFailureRecord record) in this.Failures)
                failures[identity] = record.Count;

            return new ModHealthLedgerBaseline(this.Sequence, (long[])this.TotalLogCounts.Clone(), logs, this.TotalFailureCount, failures);
        }
    }

    /// <summary>Create an immutable, deterministically ordered snapshot at a precise completed-observation cutoff.</summary>
    public ModHealthLedgerSnapshot GetSnapshot(ModHealthLedgerBaseline? captureBaseline = null)
    {
        lock (this.SyncRoot)
        {
            ModHealthModSnapshot[] mods = this.Mods.Values
                .OrderBy(record => GetModPriority(record.Status))
                .ThenBy(record => record.UniqueId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(record => record.UniqueId, StringComparer.Ordinal)
                .ThenBy(record => record.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(record => record.DisplayName, StringComparer.Ordinal)
                .ThenBy(record => record.Key.Value)
                .Select(record => record.ToSnapshot())
                .ToArray();

            ModHealthLogSourceSnapshot[] logs = this.Logs.Values
                .OrderBy(record => record.Identity.SourceCategory)
                .ThenBy(record => record.Identity.ModId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(record => record.Identity.ModId, StringComparer.Ordinal)
                .ThenBy(record => record.Identity.ModName, StringComparer.Ordinal)
                .Select(record =>
                {
                    long[] during = SubtractCounts(record.Counts, captureBaseline, record.Identity);
                    return new ModHealthLogSourceSnapshot(
                        record.Identity.ModId,
                        record.Identity.ModName,
                        record.Identity.SourceCategory,
                        CreateSeveritySnapshot(record.Counts),
                        CreateSeveritySnapshot(during),
                        record.FirstSequence,
                        record.LastSequence,
                        this.ToOffset(record.FirstTimestamp),
                        this.ToOffset(record.LastTimestamp),
                        record.LastManagedThreadId
                    );
                })
                .ToArray();

            ModHealthCallbackFailureSnapshot[] failures = this.Failures.Values
                .OrderBy(record => record.Identity.ModId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(record => record.Identity.ModId, StringComparer.Ordinal)
                .ThenBy(record => record.Identity.Operation)
                .ThenBy(record => record.Identity.CallbackIdentity, StringComparer.Ordinal)
                .ThenBy(record => record.Identity.ExceptionType, StringComparer.Ordinal)
                .Select(record =>
                {
                    long baseline = captureBaseline?.GetFailureCount(record.Identity) ?? 0;
                    return new ModHealthCallbackFailureSnapshot(
                        record.Identity.ModId,
                        record.Identity.ModName,
                        record.Identity.Phase,
                        record.Identity.Operation,
                        record.Identity.CallbackIdentity,
                        record.Identity.ExceptionType,
                        record.Identity.OnBehalfOfModId,
                        record.Count,
                        SaturatingSubtract(record.Count, baseline),
                        record.FirstSequence,
                        record.LastSequence,
                        this.ToOffset(record.FirstTimestamp),
                        this.ToOffset(record.LastTimestamp),
                        record.LastManagedThreadId
                    );
                })
                .ToArray();

            var statusTotals = new Dictionary<ModHealthLedgerModStatus, long>();
            foreach (ModHealthLedgerModStatus status in Enum.GetValues<ModHealthLedgerModStatus>())
                statusTotals[status] = this.ModStatusTotals[(int)status];

            long[] totalDuring = SubtractCounts(this.TotalLogCounts, captureBaseline);
            return new ModHealthLedgerSnapshot(
                this.StartedUtc,
                this.Completeness,
                this.Sequence,
                captureBaseline?.Sequence,
                this.TotalDiscoveredMods,
                new ReadOnlyDictionary<ModHealthLedgerModStatus, long>(statusTotals),
                Array.AsReadOnly(mods),
                CreateSeveritySnapshot(this.TotalLogCounts),
                CreateSeveritySnapshot(totalDuring),
                Array.AsReadOnly(logs),
                this.TotalFailureCount,
                SaturatingSubtract(this.TotalFailureCount, captureBaseline?.TotalFailureCount ?? 0),
                Array.AsReadOnly(failures),
                this.Capacities,
                new ModHealthLedgerOmissions(
                    SaturatingSubtract(this.TotalDiscoveredMods, this.Mods.Count),
                    this.LogIdentityObservationsOmitted,
                    this.FailureObservationsOmitted,
                    this.DependenciesOmitted
                )
            );
        }
    }


    /*********
    ** Private methods
    *********/
    private long AdvanceSequence()
    {
        return this.Sequence = SaturatingIncrement(this.Sequence);
    }

    private MutableModRecord CreateModRecord(ModHealthModKey key, ModHealthModObservation observation)
    {
        bool generated = !observation.HasValidManifest;
        string uniqueId = generated
            ? $"invalid-mod-{key.Value:0000}"
            : SanitizeIdentity(observation.UniqueId, "unknown-mod");
        string displayName = generated
            ? $"Invalid mod #{key.Value}"
            : SanitizeIdentity(observation.DisplayName, uniqueId);
        string? version = generated ? null : SanitizeOptionalIdentity(observation.Version);
        string? parentId = generated ? null : SanitizeOptionalIdentity(observation.ParentId);
        string? suggestedVersion = SanitizeOptionalIdentity(observation.SuggestedUpdateVersion);

        List<string> dependencies = [];
        if (!generated && observation.DependencyIds != null)
        {
            foreach (string dependency in observation.DependencyIds)
            {
                if (dependencies.Count >= this.DependencyCapacity)
                {
                    this.DependenciesOmitted = SaturatingIncrement(this.DependenciesOmitted);
                    continue;
                }

                dependencies.Add(SanitizeIdentity(dependency, "unknown-dependency"));
            }
            dependencies.Sort(StringComparer.OrdinalIgnoreCase);
        }

        return new MutableModRecord(
            key,
            uniqueId,
            displayName,
            version,
            generated ? ModHealthLedgerModKind.Unknown : observation.Kind,
            parentId,
            dependencies.ToArray(),
            observation.Status,
            observation.FailureReason,
            observation.WarningFlags,
            observation.UpdateStatus,
            suggestedVersion,
            generated
        );
    }

    private bool TryRetainMod(MutableModRecord record)
    {
        if (this.Mods.Count >= this.ModCapacity)
        {
            int incomingPriority = GetModPriority(record.Status);
            MutableModRecord? victim = null;
            for (int priority = this.ModKeysByPriority.Length - 1; priority > incomingPriority; priority--)
            {
                LinkedListNode<long>? node = this.ModKeysByPriority[priority].First;
                if (node != null && this.Mods.TryGetValue(node.Value, out victim))
                    break;
            }

            if (victim == null)
                return false;
            this.RemoveRetainedMod(victim);
        }

        int recordPriority = GetModPriority(record.Status);
        record.PriorityNode = this.ModKeysByPriority[recordPriority].AddLast(record.Key.Value);
        this.Mods[record.Key.Value] = record;
        return true;
    }

    private void RemoveRetainedMod(MutableModRecord record)
    {
        if (record.PriorityNode != null)
            this.ModKeysByPriority[GetModPriority(record.Status)].Remove(record.PriorityNode);
        this.Mods.Remove(record.Key.Value);
    }

    private TimeSpan ToOffset(long timestamp)
    {
        long elapsed = Math.Max(0, timestamp - this.StartedTimestamp);
        return TimeSpan.FromSeconds(elapsed / (double)this.TimestampFrequency);
    }

    private static ModHealthLogIdentity CreateLogIdentity(in ModHealthLogObservation observation)
    {
        return observation.SourceCategory switch
        {
            ModHealthLogSourceCategory.Smapi => new ModHealthLogIdentity("SMAPI", "SMAPI", observation.SourceCategory),
            ModHealthLogSourceCategory.Game => new ModHealthLogIdentity("game", "game", observation.SourceCategory),
            _ => new ModHealthLogIdentity(
                SanitizeIdentity(observation.ModId, "unknown-mod"),
                SanitizeIdentity(observation.ModName, SanitizeIdentity(observation.ModId, "unknown-mod")),
                observation.SourceCategory
            )
        };
    }

    private static ModHealthFailureIdentity CreateFailureIdentity(in ModHealthCallbackFailureObservation observation)
    {
        string modId = SanitizeIdentity(observation.ModId, "unknown-mod");
        return new ModHealthFailureIdentity(
            modId,
            SanitizeIdentity(observation.ModName, modId),
            observation.Phase,
            observation.Operation,
            SanitizeIdentity(observation.CallbackIdentity, "unknown-callback", CallbackMaxLength),
            SanitizeIdentity(observation.ExceptionType, "unknown-exception", CallbackMaxLength),
            SanitizeOptionalIdentity(observation.OnBehalfOfModId)
        );
    }

    private static int GetSeverityIndex(LogLevel level)
    {
        return level switch
        {
            LogLevel.Trace => 0,
            LogLevel.Debug => 1,
            LogLevel.Info => 2,
            LogLevel.Warn => 3,
            LogLevel.Error => 4,
            LogLevel.Alert => 5,
            _ => 0
        };
    }

    private static int GetModPriority(ModHealthLedgerModStatus status)
    {
        return status switch
        {
            ModHealthLedgerModStatus.Loaded or ModHealthLedgerModStatus.Failed or ModHealthLedgerModStatus.Invalid => 0,
            ModHealthLedgerModStatus.Skipped => 1,
            ModHealthLedgerModStatus.Ignored => 2,
            _ => 3
        };
    }

    private static ModHealthSeverityCountsSnapshot CreateSeveritySnapshot(long[] counts)
    {
        return new ModHealthSeverityCountsSnapshot(
            counts[0], counts[1],
            counts[2], counts[3],
            counts[4], counts[5],
            counts[6], counts[7],
            counts[8], counts[9],
            counts[10], counts[11]
        );
    }

    private static long[] SubtractCounts(long[] current, ModHealthLedgerBaseline? baseline, ModHealthLogIdentity? identity = null)
    {
        long[] result = new long[current.Length];
        for (int i = 0; i < result.Length; i++)
        {
            long baselineValue = baseline == null
                ? 0
                : identity.HasValue
                    ? baseline.GetLogCount(identity.Value, i)
                    : baseline.GetTotalLogCount(i);
            result[i] = SaturatingSubtract(current[i], baselineValue);
        }
        return result;
    }

    private static string SanitizeIdentity(string? value, string fallback, int maxLength = IdentityMaxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        value = value.Trim();
        if (LooksLikeAbsolutePath(value))
            return "<redacted-path>";

        char[]? buffer = null;
        int length = Math.Min(value.Length, maxLength);
        for (int i = 0; i < length; i++)
        {
            char character = value[i];
            bool replace = char.IsControl(character) || character == '\u001b';
            if (!replace)
                continue;

            buffer ??= value.Substring(0, length).ToCharArray();
            buffer[i] = ' ';
        }

        string sanitized = buffer != null ? new string(buffer) : value.Substring(0, length);
        sanitized = sanitized.Trim();
        return sanitized.Length > 0 ? sanitized : fallback;
    }

    private static string? SanitizeOptionalIdentity(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : SanitizeIdentity(value, "unknown");
    }

    private static bool LooksLikeAbsolutePath(string value)
    {
        return
            Path.IsPathRooted(value)
            || value.StartsWith("~/", StringComparison.Ordinal)
            || value.StartsWith("~\\", StringComparison.Ordinal)
            || value.StartsWith("\\\\", StringComparison.Ordinal)
            || (value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/'));
    }

    private static long SaturatingIncrement(long value)
    {
        return value == long.MaxValue ? long.MaxValue : value + 1;
    }

    private static long SaturatingAdd(long value, long amount)
    {
        if (amount <= 0)
            return value;
        return value > long.MaxValue - amount ? long.MaxValue : value + amount;
    }

    private static long SaturatingSubtract(long value, long baseline)
    {
        return value >= baseline ? value - baseline : 0;
    }


    /*********
    ** Private models
    *********/
    private sealed class MutableModRecord
    {
        public ModHealthModKey Key { get; }
        public string UniqueId { get; }
        public string DisplayName { get; }
        public string? Version { get; }
        public ModHealthLedgerModKind Kind { get; }
        public string? ParentId { get; }
        public string[] DependencyIds { get; }
        public ModHealthLedgerModStatus Status { get; }
        public ModHealthModFailureReason FailureReason { get; }
        public ulong WarningFlags { get; }
        public ModHealthUpdateStatus UpdateStatus { get; }
        public string? SuggestedUpdateVersion { get; }
        public bool UsesGeneratedInvalidIdentity { get; }
        public LinkedListNode<long>? PriorityNode { get; set; }

        public MutableModRecord(ModHealthModKey key, string uniqueId, string displayName, string? version, ModHealthLedgerModKind kind, string? parentId, string[] dependencyIds, ModHealthLedgerModStatus status, ModHealthModFailureReason failureReason, ulong warningFlags, ModHealthUpdateStatus updateStatus, string? suggestedUpdateVersion, bool usesGeneratedInvalidIdentity)
        {
            this.Key = key;
            this.UniqueId = uniqueId;
            this.DisplayName = displayName;
            this.Version = version;
            this.Kind = kind;
            this.ParentId = parentId;
            this.DependencyIds = dependencyIds;
            this.Status = status;
            this.FailureReason = failureReason;
            this.WarningFlags = warningFlags;
            this.UpdateStatus = updateStatus;
            this.SuggestedUpdateVersion = suggestedUpdateVersion;
            this.UsesGeneratedInvalidIdentity = usesGeneratedInvalidIdentity;
        }

        public ModHealthModSnapshot ToSnapshot()
        {
            return new ModHealthModSnapshot(
                this.Key,
                this.UniqueId,
                this.DisplayName,
                this.Version,
                this.Kind,
                this.ParentId,
                Array.AsReadOnly((string[])this.DependencyIds.Clone()),
                this.Status,
                this.FailureReason,
                this.WarningFlags,
                this.UpdateStatus,
                this.SuggestedUpdateVersion,
                this.UsesGeneratedInvalidIdentity
            );
        }
    }

    private sealed class MutableLogRecord(ModHealthLogIdentity identity, long sequence, long timestamp)
    {
        public ModHealthLogIdentity Identity { get; } = identity;
        public long[] Counts { get; } = new long[SeverityCount * 2];
        public long FirstSequence { get; } = sequence;
        public long LastSequence { get; set; } = sequence;
        public long FirstTimestamp { get; } = timestamp;
        public long LastTimestamp { get; set; } = timestamp;
        public int LastManagedThreadId { get; set; }
    }

    private sealed class MutableFailureRecord(ModHealthFailureIdentity identity, long sequence, long timestamp)
    {
        public ModHealthFailureIdentity Identity { get; } = identity;
        public long Count { get; set; }
        public long FirstSequence { get; } = sequence;
        public long LastSequence { get; set; } = sequence;
        public long FirstTimestamp { get; } = timestamp;
        public long LastTimestamp { get; set; } = timestamp;
        public int LastManagedThreadId { get; set; }
    }

    private sealed class ModHealthLogIdentityComparer : IEqualityComparer<ModHealthLogIdentity>
    {
        public static ModHealthLogIdentityComparer Instance { get; } = new();

        public bool Equals(ModHealthLogIdentity x, ModHealthLogIdentity y)
        {
            return x.SourceCategory == y.SourceCategory
                && StringComparer.OrdinalIgnoreCase.Equals(x.ModId, y.ModId);
        }

        public int GetHashCode(ModHealthLogIdentity obj)
        {
            return HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ModId), obj.SourceCategory);
        }
    }

    private sealed class ModHealthFailureIdentityComparer : IEqualityComparer<ModHealthFailureIdentity>
    {
        public static ModHealthFailureIdentityComparer Instance { get; } = new();

        public bool Equals(ModHealthFailureIdentity x, ModHealthFailureIdentity y)
        {
            return
                StringComparer.OrdinalIgnoreCase.Equals(x.ModId, y.ModId)
                && x.Phase == y.Phase
                && x.Operation == y.Operation
                && StringComparer.Ordinal.Equals(x.CallbackIdentity, y.CallbackIdentity)
                && StringComparer.Ordinal.Equals(x.ExceptionType, y.ExceptionType)
                && StringComparer.OrdinalIgnoreCase.Equals(x.OnBehalfOfModId, y.OnBehalfOfModId);
        }

        public int GetHashCode(ModHealthFailureIdentity obj)
        {
            return HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ModId),
                obj.Phase,
                obj.Operation,
                StringComparer.Ordinal.GetHashCode(obj.CallbackIdentity),
                StringComparer.Ordinal.GetHashCode(obj.ExceptionType),
                obj.OnBehalfOfModId != null ? StringComparer.OrdinalIgnoreCase.GetHashCode(obj.OnBehalfOfModId) : 0
            );
        }
    }
}
