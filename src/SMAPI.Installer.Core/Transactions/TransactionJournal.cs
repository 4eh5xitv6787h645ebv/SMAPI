using System.Text.Json;
using System.Text.Json.Serialization;
using StardewModdingAPI.Installer.Core.Ownership;

namespace StardewModdingAPI.Installer.Core.Transactions;

internal sealed class TransactionJournal
{
    public int SchemaVersion { get; init; } = 1;
    public Guid TransactionId { get; init; }
    public string CanonicalGameRoot { get; init; } = null!;
    public TransactionJournalStatus Status { get; set; } = TransactionJournalStatus.Staging;
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
    public string BackupRelativePath { get; init; } = null!;
    public List<string> CreatedDirectories { get; init; } = new();
    public bool MutationApplied { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum TransactionJournalStatus
{
    Staging,
    Applying,
    Committed,
    RollingBack,
    RolledBack
}

internal static class TransactionJournalStore
{
    private const int MaximumJournalBytes = 16 * 1024 * 1024;
    private const int MaximumEntries = 20_000;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
    };

    public static TransactionJournal Read(string path, Guid expectedTransactionId, string expectedGameRoot, string expectedTransactionDirectory)
    {
        FileInfo info = new(path);
        if (!info.Exists || info.Length is <= 0 or > MaximumJournalBytes)
            throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "The recovery journal exceeds its size limit.");
        byte[] bytes = new byte[checked((int)info.Length)];
        using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0)
                    throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "The recovery journal ended unexpectedly.");
                offset += read;
            }
            if (stream.ReadByte() != -1 || stream.Length != info.Length)
                throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "The recovery journal changed while it was read.");
        }

        AssertExactJsonShape(bytes);
        TransactionJournal? journal = JsonSerializer.Deserialize<TransactionJournal>(bytes, SerializerOptions);
        if (
            journal is null
            || journal.SchemaVersion != 1
            || journal.TransactionId != expectedTransactionId
            || !string.Equals(journal.CanonicalGameRoot, expectedGameRoot, StringComparison.Ordinal)
            || !string.Equals(Path.GetFileName(expectedTransactionDirectory), expectedTransactionId.ToString("N"), StringComparison.Ordinal)
            || journal.Entries.Count > MaximumEntries
            || !Enum.IsDefined(typeof(TransactionJournalStatus), journal.Status)
        )
            throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "The recovery journal is invalid.");
        if (!bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(journal, SerializerOptions)))
            throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "The recovery journal isn't in canonical serialized form.");

        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < journal.Entries.Count; index++)
        {
            TransactionJournalEntry entry = journal.Entries[index];
            if (entry.Index != index || !Enum.IsDefined(typeof(TransactionOperationKind), entry.Kind))
                throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "The recovery journal entry order is invalid.");
            string pathValue = TransactionPath.NormalizeRelativePath(entry.RelativePath, "Recovery path");
            if (!paths.Add(pathValue))
                throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "The recovery journal contains duplicate destinations.");
            if (!OwnedNamespacePolicy.IsAllowedTransactionDestination(NormalizedRelativePath.Parse(pathValue)))
                throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "The recovery journal contains a destination outside the compiled allowlist.");
            if (!string.Equals(entry.BackupRelativePath, $"backups/{index:D8}", StringComparison.Ordinal))
                throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "The recovery journal contains a noncanonical backup path.");
            if (entry.HadOriginal != (entry.ExpectedExistingSha256 is not null))
                throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "The recovery journal's original-file state is inconsistent.");
            AssertDigest(entry.ExpectedExistingSha256, allowNull: true);
            AssertDigest(entry.ExpectedResultSha256, allowNull: entry.Kind == TransactionOperationKind.RemoveFile);
            if (entry.Kind == TransactionOperationKind.RemoveFile && entry.ExpectedResultSha256 is not null)
                throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "A recovery remove entry contains a result digest.");
            if (entry.Kind == TransactionOperationKind.RemoveFile && !entry.HadOriginal)
                throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "A recovery remove entry has no expected original.");

            HashSet<string> created = new(StringComparer.Ordinal);
            foreach (string directory in entry.CreatedDirectories)
            {
                string normalized = TransactionPath.NormalizeRelativePath(directory, "Created directory");
                if (!entry.RelativePath.StartsWith(normalized + "/", StringComparison.Ordinal) || !created.Add(normalized))
                    throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "A journaled created directory isn't a unique parent of its destination.");
            }
        }
        return journal;
    }

    public static void WriteDurable(string path, TransactionJournal journal)
    {
        string temporaryPath = path + ".new";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(journal, SerializerOptions);
        if (bytes.Length > MaximumJournalBytes)
            throw new InstallerTransactionException(TransactionErrorCode.InvalidPlan, "The transaction journal exceeds its durable size limit.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (FileStream stream = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporaryPath, path, overwrite: true);
        TransactionDurability.FlushDirectory(Path.GetDirectoryName(path)!);
    }

    private static void AssertDigest(string? value, bool allowNull)
    {
        if (value is null && allowNull)
            return;
        if (value is null || value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "The recovery journal contains an invalid digest.");
    }

    private static void AssertExactJsonShape(byte[] bytes)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            AssertExactProperties(document.RootElement, "schemaVersion", "transactionId", "canonicalGameRoot", "status", "entries");
            if (document.RootElement.GetProperty("entries").ValueKind != JsonValueKind.Array)
                throw new JsonException();
            foreach (JsonElement entry in document.RootElement.GetProperty("entries").EnumerateArray())
                AssertExactProperties(entry, "index", "kind", "relativePath", "hadOriginal", "expectedExistingSha256", "expectedResultSha256", "backupRelativePath", "createdDirectories", "mutationApplied");
        }
        catch (JsonException exception)
        {
            throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "The recovery journal isn't strict canonical JSON.", exception);
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
}

internal static class TransactionDurability
{
    private const int OpenReadOnly = 0;
    private const int OpenDirectory = 0x10000;
    private const int OpenCloseOnExec = 0x80000;

    public static void FlushDirectory(string path)
    {
        if (!OperatingSystem.IsLinux())
            return;

        int descriptor = open(path, OpenReadOnly | OpenDirectory | OpenCloseOnExec);
        if (descriptor < 0)
            throw new IOException("Couldn't open a transaction directory for durability.");
        try
        {
            if (fsync(descriptor) != 0)
                throw new IOException("Couldn't flush a transaction directory.");
        }
        finally
        {
            close(descriptor);
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int fsync(int descriptor);

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int close(int descriptor);
}
