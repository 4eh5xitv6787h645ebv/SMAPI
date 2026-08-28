using System.Text.Json;
using System.Text.Json.Serialization;

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
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static TransactionJournal Read(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length > 1024 * 1024)
            throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "The recovery journal exceeds its size limit.");
        TransactionJournal? journal = JsonSerializer.Deserialize<TransactionJournal>(bytes, SerializerOptions);
        if (journal is null || journal.SchemaVersion != 1 || journal.TransactionId == Guid.Empty)
            throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "The recovery journal is invalid.");
        return journal;
    }

    public static void WriteDurable(string path, TransactionJournal journal)
    {
        string temporaryPath = path + ".new";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(journal, SerializerOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (FileStream stream = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporaryPath, path, overwrite: true);
        TransactionDurability.FlushDirectory(Path.GetDirectoryName(path)!);
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
