using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Transactions;

internal sealed class TransactionJournal
{
    public int SchemaVersion { get; init; } = 2;
    public Guid TransactionId { get; init; }
    public long CreatedUtcTicks { get; init; }
    public string CanonicalGameRoot { get; init; } = null!;
    public ulong GameRootInode { get; init; }
    public uint GameRootDeviceMajor { get; init; }
    public uint GameRootDeviceMinor { get; init; }
    public bool HasCoreAuthorizedReceiptMutation { get; init; }
    public List<TransactionJournalEntry> Entries { get; init; } = new();
}

internal sealed class TransactionJournalEntry
{
    public int Index { get; init; }
    public TransactionOperationKind Kind { get; init; }
    public string RelativePath { get; init; } = null!;
    public bool HadOriginal { get; init; }
    public string? ExpectedExistingSha256 { get; init; }
    public string? ExpectedResultSha256 { get; init; }
    public int? ResultUnixMode { get; init; }
    public string BackupRelativePath { get; init; } = null!;
    public string? StagedRelativePath { get; init; }
    public List<string> CreatedDirectories { get; init; } = new();
}

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum TransactionJournalEventKind
{
    Created,
    Prepared,
    Applying,
    Intent,
    Applied,
    RollingBack,
    RollbackApplied,
    Committed,
    RolledBack
}

internal sealed class TransactionJournalEvent
{
    public int SchemaVersion { get; init; } = 1;
    public int Sequence { get; init; }
    public TransactionJournalEventKind Kind { get; init; }
    public int? OperationIndex { get; init; }
    public string PlanSha256 { get; init; } = null!;
    public string? PreviousEventSha256 { get; init; }
    public string EventSha256 { get; init; } = null!;
}

internal sealed class TransactionJournalReplay
{
    public IReadOnlyList<TransactionJournalEvent> Events { get; }
    public TransactionJournalEventKind? Status => this.Events.Count == 0 ? null : this.Events[^1].Kind;
    public string? LastEventSha256 => this.Events.Count == 0 ? null : this.Events[^1].EventSha256;
    public int ValidByteLength { get; }
    public HashSet<int> IntendedOperations { get; }
    public HashSet<int> AppliedOperations { get; }
    public HashSet<int> RolledBackOperations { get; }

    public TransactionJournalReplay(
        IReadOnlyList<TransactionJournalEvent> events,
        int validByteLength,
        HashSet<int> intendedOperations,
        HashSet<int> appliedOperations,
        HashSet<int> rolledBackOperations
    )
    {
        this.Events = events;
        this.ValidByteLength = validByteLength;
        this.IntendedOperations = intendedOperations;
        this.AppliedOperations = appliedOperations;
        this.RolledBackOperations = rolledBackOperations;
    }
}

internal static class TransactionJournalStore
{
    public const string PlanFileName = "journal.json";
    public const string EventsFileName = "events.jsonl";
    // This covers the worst-case intent/applied/rollback history for the public 20,000-operation cap while
    // remaining an explicit allocation and disk bound.
    public const int MaximumJournalBytes = 64 * 1024 * 1024;
    private const int MaximumEntries = TransactionPlan.MaximumOperationCount;
    private static readonly JsonSerializerOptions PlanSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
    };
    private static readonly JsonSerializerOptions EventSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
    };

    public static string Create(LinuxAnchoredFileSystem transaction, TransactionJournal journal)
    {
        ValidateJournal(journal, journal.TransactionId, journal.CanonicalGameRoot, null);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(journal, PlanSerializerOptions);
        if (bytes.Length is <= 0 or > MaximumJournalBytes)
            throw new InstallerTransactionException(TransactionErrorCode.InvalidPlan, "The immutable transaction plan exceeds its durable size limit.");
        using (LinuxAnchoredFile plan = transaction.CreateNewFile(PlanFileName, 0x180))
            transaction.AppendAndFsync(plan, PlanFileName, bytes, 0, MaximumJournalBytes);
        using (transaction.CreateNewFile(EventsFileName, 0x180)) { }
        transaction.FsyncDirectory();
        return Hash(bytes);
    }

    public static TransactionJournal ReadPlan(
        LinuxAnchoredFileSystem transaction,
        Guid expectedTransactionId,
        string expectedGameRoot,
        LinuxFileIdentity expectedGameRootIdentity
    )
    {
        byte[] bytes;
        try
        {
            using LinuxAnchoredFile plan = transaction.OpenRegularFileForRead(PlanFileName);
            bytes = transaction.ReadAllBytes(plan, MaximumJournalBytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "The immutable recovery plan is missing or unsafe.", exception);
        }
        if (bytes.Length == 0)
            throw RecoveryError("The immutable recovery plan is empty.");
        AssertPlanShape(bytes);
        TransactionJournal? journal;
        try
        {
            journal = JsonSerializer.Deserialize<TransactionJournal>(bytes, PlanSerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "The immutable recovery plan isn't valid JSON.", exception);
        }
        if (journal is null || !bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(journal, PlanSerializerOptions)))
            throw RecoveryError("The immutable recovery plan isn't canonical.");
        ValidateJournal(journal, expectedTransactionId, expectedGameRoot, expectedGameRootIdentity);
        return journal;
    }

    public static string GetPlanSha256(TransactionJournal journal)
    {
        return Hash(JsonSerializer.SerializeToUtf8Bytes(journal, PlanSerializerOptions));
    }

    public static TransactionJournalReplay ReadEvents(LinuxAnchoredFileSystem transaction, TransactionJournal journal)
    {
        byte[] bytes;
        try
        {
            using LinuxAnchoredFile events = transaction.OpenRegularFileForRead(EventsFileName);
            bytes = transaction.ReadAllBytes(events, MaximumJournalBytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "The recovery event log is missing or unsafe.", exception);
        }

        string planDigest = GetPlanSha256(journal);
        List<TransactionJournalEvent> eventsList = new();
        int start = 0;
        int validLength = 0;
        while (start < bytes.Length)
        {
            int newline = Array.IndexOf(bytes, (byte)'\n', start);
            if (newline < 0)
                break;
            if (newline == start)
                throw RecoveryError("The recovery event log contains an empty record.");
            eventsList.Add(ReadCanonicalEvent(bytes.AsSpan(start, newline - start)));
            validLength = newline + 1;
            start = newline + 1;
        }
        ValidateEventSequence(eventsList, planDigest, journal.Entries.Count, out HashSet<int> intended, out HashSet<int> applied, out HashSet<int> rolledBack);
        return new TransactionJournalReplay(eventsList, validLength, intended, applied, rolledBack);
    }

    public static TransactionJournalReplay Append(
        LinuxAnchoredFileSystem transaction,
        LinuxAnchoredFile eventsFile,
        TransactionJournal journal,
        TransactionJournalReplay replay,
        TransactionJournalEventKind kind,
        int? operationIndex = null
    )
    {
        string planDigest = GetPlanSha256(journal);
        TransactionJournalEvent unsigned = new()
        {
            Sequence = replay.Events.Count,
            Kind = kind,
            OperationIndex = operationIndex,
            PlanSha256 = planDigest,
            PreviousEventSha256 = replay.LastEventSha256,
            EventSha256 = string.Empty
        };
        TransactionJournalEvent completed = new()
        {
            Sequence = unsigned.Sequence,
            Kind = unsigned.Kind,
            OperationIndex = unsigned.OperationIndex,
            PlanSha256 = unsigned.PlanSha256,
            PreviousEventSha256 = unsigned.PreviousEventSha256,
            EventSha256 = ComputeEventDigest(unsigned)
        };
        byte[] record = JsonSerializer.SerializeToUtf8Bytes(completed, EventSerializerOptions);
        byte[] line = new byte[record.Length + 1];
        record.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        transaction.AppendAndFsync(eventsFile, EventsFileName, line, replay.ValidByteLength, MaximumJournalBytes);
        List<TransactionJournalEvent> all = replay.Events.Append(completed).ToList();
        ValidateEventSequence(all, planDigest, journal.Entries.Count, out HashSet<int> intended, out HashSet<int> applied, out HashSet<int> rolledBack);
        return new TransactionJournalReplay(all, checked(replay.ValidByteLength + line.Length), intended, applied, rolledBack);
    }

    public static LinuxAnchoredFile OpenEventsForAppend(LinuxAnchoredFileSystem transaction, TransactionJournalReplay replay)
    {
        LinuxAnchoredFile file = transaction.OpenRegularFileForReadWrite(EventsFileName);
        if (file.Identity.Size < replay.ValidByteLength)
        {
            file.Dispose();
            throw RecoveryError("The recovery event log became shorter before reuse.");
        }
        if (file.Identity.Size != replay.ValidByteLength)
            transaction.TruncateAndFsync(file, EventsFileName, replay.ValidByteLength);
        return file;
    }

    private static void ValidateJournal(TransactionJournal journal, Guid expectedId, string expectedRoot, LinuxFileIdentity? expectedIdentity)
    {
        if (
            journal.SchemaVersion != 2
            || journal.TransactionId == Guid.Empty
            || journal.TransactionId != expectedId
            || journal.CreatedUtcTicks < DateTime.UnixEpoch.Ticks
            || !string.Equals(journal.CanonicalGameRoot, expectedRoot, StringComparison.Ordinal)
            || journal.Entries.Count is <= 0 or > MaximumEntries
            || (expectedIdentity is not null && (
                journal.GameRootInode != expectedIdentity.Inode
                || journal.GameRootDeviceMajor != expectedIdentity.DeviceMajor
                || journal.GameRootDeviceMinor != expectedIdentity.DeviceMinor
            ))
        )
            throw RecoveryError("The immutable recovery plan identity is invalid.");

        HashSet<string> exact = new(StringComparer.Ordinal);
        HashSet<string> insensitive = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> allCreatedDirectories = new(StringComparer.Ordinal);
        for (int index = 0; index < journal.Entries.Count; index++)
        {
            TransactionJournalEntry entry = journal.Entries[index];
            string path = NormalizeRecoveryPath(entry.RelativePath, "Recovery path");
            if (
                entry.Index != index
                || !Enum.IsDefined(typeof(TransactionOperationKind), entry.Kind)
                || !IsAllowedDestination(path, journal.HasCoreAuthorizedReceiptMutation, index == journal.Entries.Count - 1)
                || !exact.Add(path)
                || !insensitive.Add(path)
                || entry.HadOriginal != (entry.ExpectedExistingSha256 is not null)
                || !string.Equals(entry.BackupRelativePath, $"backups/{index:D8}", StringComparison.Ordinal)
                || (entry.Kind == TransactionOperationKind.WriteFile) != (entry.StagedRelativePath is not null)
                || (entry.StagedRelativePath is not null && !string.Equals(entry.StagedRelativePath, $"staged/{index:D8}", StringComparison.Ordinal))
            )
                throw RecoveryError("The immutable recovery plan contains an invalid entry.");
            AssertDigest(entry.ExpectedExistingSha256, allowNull: true);
            AssertDigest(entry.ExpectedResultSha256, allowNull: entry.Kind == TransactionOperationKind.RemoveFile);
            if (entry.Kind == TransactionOperationKind.RemoveFile && (entry.ExpectedResultSha256 is not null || entry.ResultUnixMode is not null))
                throw RecoveryError("A recovery remove entry contains write-only fields.");
            if (entry.ResultUnixMode is < 0 or > 0x1ff)
                throw RecoveryError("A recovery write entry contains an invalid mode.");
            HashSet<string> directories = new(StringComparer.Ordinal);
            foreach (string directory in entry.CreatedDirectories)
            {
                string normalized = NormalizeRecoveryPath(directory, "Recovery-created directory");
                if (!path.StartsWith(normalized + "/", StringComparison.Ordinal) || !directories.Add(normalized) || !allCreatedDirectories.Add(normalized))
                    throw RecoveryError("A recovery-created directory isn't a unique parent of its destination.");
            }
        }
        if (journal.HasCoreAuthorizedReceiptMutation != journal.Entries.Any(entry => entry.RelativePath == TransactionPlan.CoreReceiptRelativePath))
            throw RecoveryError("The immutable recovery plan's receipt authorization is inconsistent.");
    }

    private static bool IsAllowedDestination(string path, bool receiptAuthorized, bool isLast)
    {
        return OwnedNamespacePolicy.IsAllowedTransactionDestination(NormalizedRelativePath.Parse(path))
            || (path == TransactionPlan.CoreReceiptRelativePath && receiptAuthorized && isLast);
    }

    private static string NormalizeRecoveryPath(string path, string name)
    {
        try
        {
            string normalized = TransactionPath.NormalizeRelativePath(path, name);
            NormalizedRelativePath.Parse(normalized);
            return normalized;
        }
        catch (ArgumentException exception)
        {
            throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, $"{name} isn't a bounded canonical path.", exception);
        }
    }

    private static void ValidateEventSequence(
        IReadOnlyList<TransactionJournalEvent> events,
        string planDigest,
        int operationCount,
        out HashSet<int> intended,
        out HashSet<int> applied,
        out HashSet<int> rolledBack
    )
    {
        intended = new();
        applied = new();
        rolledBack = new();
        string? previous = null;
        bool prepared = false;
        bool applying = false;
        bool rollingBack = false;
        bool final = false;
        int nextIntent = 0;
        int? pendingIntent = null;
        int nextRollback = -1;
        for (int sequence = 0; sequence < events.Count; sequence++)
        {
            TransactionJournalEvent item = events[sequence];
            if (
                item.SchemaVersion != 1
                || item.Sequence != sequence
                || item.PlanSha256 != planDigest
                || item.PreviousEventSha256 != previous
                || item.EventSha256 != ComputeEventDigest(item)
                || final
            )
                throw RecoveryError("The recovery event chain is invalid.");
            bool valid = item.Kind switch
            {
                TransactionJournalEventKind.Created => sequence == 0 && item.OperationIndex is null,
                TransactionJournalEventKind.Prepared => sequence > 0 && !prepared && !applying && !rollingBack && item.OperationIndex is null,
                TransactionJournalEventKind.Applying => prepared && !applying && !rollingBack && item.OperationIndex is null,
                TransactionJournalEventKind.Intent => applying && !rollingBack && pendingIntent is null && item.OperationIndex == nextIntent,
                TransactionJournalEventKind.Applied => applying && !rollingBack && item.OperationIndex == pendingIntent,
                TransactionJournalEventKind.RollingBack => sequence > 0 && !rollingBack && item.OperationIndex is null,
                TransactionJournalEventKind.RollbackApplied => rollingBack && item.OperationIndex == nextRollback,
                TransactionJournalEventKind.Committed => applying && !rollingBack && pendingIntent is null && nextIntent == operationCount && item.OperationIndex is null,
                TransactionJournalEventKind.RolledBack => rollingBack && nextRollback < 0 && item.OperationIndex is null,
                _ => false
            };
            if (!valid)
                throw RecoveryError("The recovery event sequence contains an invalid transition.");
            switch (item.Kind)
            {
                case TransactionJournalEventKind.Prepared:
                    prepared = true;
                    break;
                case TransactionJournalEventKind.Applying:
                    applying = true;
                    break;
                case TransactionJournalEventKind.Intent:
                    pendingIntent = item.OperationIndex;
                    intended.Add(item.OperationIndex!.Value);
                    nextRollback = item.OperationIndex.Value;
                    break;
                case TransactionJournalEventKind.Applied:
                    applied.Add(item.OperationIndex!.Value);
                    pendingIntent = null;
                    nextIntent++;
                    break;
                case TransactionJournalEventKind.RollingBack:
                    rollingBack = true;
                    nextRollback = intended.Count == 0 ? -1 : intended.Max();
                    break;
                case TransactionJournalEventKind.RollbackApplied:
                    rolledBack.Add(item.OperationIndex!.Value);
                    nextRollback--;
                    break;
                case TransactionJournalEventKind.Committed:
                case TransactionJournalEventKind.RolledBack:
                    final = true;
                    break;
            }
            previous = item.EventSha256;
        }
        if (events.Count > 0 && events[0].Kind != TransactionJournalEventKind.Created)
            throw RecoveryError("The recovery event log has no creation record.");
    }

    private static TransactionJournalEvent ReadCanonicalEvent(ReadOnlySpan<byte> line)
    {
        byte[] bytes = line.ToArray();
        AssertEventShape(bytes);
        TransactionJournalEvent? item;
        try
        {
            item = JsonSerializer.Deserialize<TransactionJournalEvent>(bytes, EventSerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "The recovery event log contains invalid JSON.", exception);
        }
        if (item is null || !bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(item, EventSerializerOptions)))
            throw RecoveryError("The recovery event log isn't canonical.");
        return item;
    }

    private static string ComputeEventDigest(TransactionJournalEvent item)
    {
        byte[] unsigned = JsonSerializer.SerializeToUtf8Bytes(new
        {
            item.SchemaVersion,
            item.Sequence,
            item.Kind,
            item.OperationIndex,
            item.PlanSha256,
            item.PreviousEventSha256
        }, EventSerializerOptions);
        return Hash(unsigned);
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void AssertDigest(string? value, bool allowNull)
    {
        if (value is null && allowNull)
            return;
        if (value is null || value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw RecoveryError("The recovery plan contains an invalid digest.");
    }

    private static void AssertPlanShape(byte[] bytes)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16 });
            AssertExactProperties(document.RootElement, "schemaVersion", "transactionId", "createdUtcTicks", "canonicalGameRoot", "gameRootInode", "gameRootDeviceMajor", "gameRootDeviceMinor", "hasCoreAuthorizedReceiptMutation", "entries");
            JsonElement entries = document.RootElement.GetProperty("entries");
            if (entries.ValueKind != JsonValueKind.Array)
                throw new JsonException();
            foreach (JsonElement entry in entries.EnumerateArray())
                AssertExactProperties(entry, "index", "kind", "relativePath", "hadOriginal", "expectedExistingSha256", "expectedResultSha256", "resultUnixMode", "backupRelativePath", "stagedRelativePath", "createdDirectories");
        }
        catch (JsonException exception)
        {
            throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "The immutable recovery plan isn't strict JSON.", exception);
        }
    }

    private static void AssertEventShape(byte[] bytes)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 4 });
            AssertExactProperties(document.RootElement, "schemaVersion", "sequence", "kind", "operationIndex", "planSha256", "previousEventSha256", "eventSha256");
        }
        catch (JsonException exception)
        {
            throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "A recovery event isn't strict JSON.", exception);
        }
    }

    private static void AssertExactProperties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new JsonException();
        HashSet<string> remaining = new(expected, StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!remaining.Remove(property.Name))
                throw new JsonException();
        }
        if (remaining.Count != 0)
            throw new JsonException();
    }

    private static InstallerTransactionException RecoveryError(string message) => new(TransactionErrorCode.RecoveryFailed, message);
}
