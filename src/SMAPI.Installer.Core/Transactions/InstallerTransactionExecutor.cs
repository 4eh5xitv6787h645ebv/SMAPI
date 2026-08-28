namespace StardewModdingAPI.Installer.Core.Transactions;

/// <summary>Applies immutable file plans with a durable journal and exact rollback.</summary>
public sealed class InstallerTransactionExecutor
{
    private const string WorkspaceName = ".smapi-installer";
    private const string WorkspaceMarkerContents = "smapi-installer-state-v1\n";
    private readonly ITransactionProgressSink Progress;
    private readonly ITransactionFaultInjector FaultInjector;

    /// <summary>Construct an instance.</summary>
    public InstallerTransactionExecutor(ITransactionProgressSink? progress = null, ITransactionFaultInjector? faultInjector = null)
    {
        this.Progress = progress ?? NullTransactionInstrumentation.Instance;
        this.FaultInjector = faultInjector ?? NullTransactionInstrumentation.Instance;
    }

    /// <summary>Apply an immutable transaction plan.</summary>
    /// <remarks>Cancellation is honored through staging and final revalidation. Once mutation begins, the executor finishes commit or rollback.</remarks>
    public TransactionResult Apply(string gameRoot, string payloadRoot, TransactionPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string canonicalGameRoot = TransactionPath.GetCanonicalRoot(gameRoot, nameof(gameRoot));
        string canonicalPayloadRoot = TransactionPath.GetCanonicalRoot(payloadRoot, nameof(payloadRoot));
        ValidatePlan(plan);

        this.Progress.Report(new(TransactionStage.AcquiringLock, 0, plan.Operations.Count));
        string workspace = EnsureWorkspace(canonicalGameRoot);
        using FileStream operationLock = AcquireLock(workspace);

        this.Progress.Report(new(TransactionStage.Recovering, 0, plan.Operations.Count));
        this.RecoverIncompleteTransactionsLocked(canonicalGameRoot, workspace);

        string transactionDirectory = Path.Combine(workspace, "transactions", plan.TransactionId.ToString("N"));
        if (Directory.Exists(transactionDirectory))
            throw new InstallerTransactionException(TransactionErrorCode.WorkspaceConflict, "A transaction with this ID already exists.");

        Directory.CreateDirectory(Path.Combine(transactionDirectory, "staged"));
        Directory.CreateDirectory(Path.Combine(transactionDirectory, "backups"));
        SetPrivateDirectoryModes(transactionDirectory);
        string journalPath = Path.Combine(transactionDirectory, "journal.json");
        TransactionJournal journal = new()
        {
            TransactionId = plan.TransactionId,
            CanonicalGameRoot = canonicalGameRoot
        };
        TransactionJournalStore.WriteDurable(journalPath, journal);

        try
        {
            this.StagePayload(canonicalPayloadRoot, transactionDirectory, plan, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            this.RevalidateAll(canonicalGameRoot, plan);
            cancellationToken.ThrowIfCancellationRequested();

            journal.Status = TransactionJournalStatus.Applying;
            TransactionJournalStore.WriteDurable(journalPath, journal);
            this.ApplyMutations(canonicalGameRoot, transactionDirectory, plan, journal, journalPath);

            this.Progress.Report(new(TransactionStage.Verifying, plan.Operations.Count, plan.Operations.Count));
            VerifyResults(canonicalGameRoot, plan);
            journal.Status = TransactionJournalStatus.Committed;
            this.Progress.Report(new(TransactionStage.Committing, plan.Operations.Count, plan.Operations.Count));
            TransactionJournalStore.WriteDurable(journalPath, journal);
            this.Progress.Report(new(TransactionStage.Completed, plan.Operations.Count, plan.Operations.Count));
            return new(plan.TransactionId, TransactionStatus.Committed, plan.Operations.Count);
        }
        catch (SimulatedProcessTerminationException)
        {
            throw;
        }
        catch
        {
            this.RollBackJournal(canonicalGameRoot, transactionDirectory, journal, journalPath);
            throw;
        }
    }

    /// <summary>Recover every incomplete transaction under a game root.</summary>
    public IReadOnlyList<TransactionResult> RecoverIncompleteTransactions(string gameRoot)
    {
        string canonicalGameRoot = TransactionPath.GetCanonicalRoot(gameRoot, nameof(gameRoot));
        string workspace = EnsureWorkspace(canonicalGameRoot);
        using FileStream operationLock = AcquireLock(workspace);
        return this.RecoverIncompleteTransactionsLocked(canonicalGameRoot, workspace);
    }

    private static void ValidatePlan(TransactionPlan plan)
    {
        HashSet<string> destinations = new(StringComparer.Ordinal);
        HashSet<string> caseInsensitiveDestinations = new(StringComparer.OrdinalIgnoreCase);
        foreach (TransactionFileOperation operation in plan.Operations)
        {
            string relativePath = TransactionPath.NormalizeRelativePath(operation.RelativePath, nameof(operation.RelativePath));
            if (!destinations.Add(relativePath) || !caseInsensitiveDestinations.Add(relativePath))
                throw new InstallerTransactionException(TransactionErrorCode.InvalidPlan, "A transaction contains duplicate or case-colliding destinations.");
            ValidateSha256(operation.ExpectedExistingSha256, allowNull: true, nameof(operation.ExpectedExistingSha256));

            if (operation.Kind == TransactionOperationKind.WriteFile)
            {
                if (operation.PayloadRelativePath is null || operation.ExpectedResultSha256 is null)
                    throw new InstallerTransactionException(TransactionErrorCode.InvalidPlan, "A write operation requires a payload path and result digest.");
                TransactionPath.NormalizeRelativePath(operation.PayloadRelativePath, nameof(operation.PayloadRelativePath));
                ValidateSha256(operation.ExpectedResultSha256, allowNull: false, nameof(operation.ExpectedResultSha256));
            }
            else if (operation.PayloadRelativePath is not null || operation.ExpectedResultSha256 is not null || operation.ResultUnixMode is not null)
                throw new InstallerTransactionException(TransactionErrorCode.InvalidPlan, "A remove operation can't include payload fields.");
        }
    }

    private static void ValidateSha256(string? value, bool allowNull, string name)
    {
        if (value is null && allowNull)
            return;
        if (value is null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)) || !string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal))
            throw new InstallerTransactionException(TransactionErrorCode.InvalidPlan, $"{name} must be a lowercase SHA-256 digest.");
    }

    private void StagePayload(string payloadRoot, string transactionDirectory, TransactionPlan plan, CancellationToken cancellationToken)
    {
        for (int index = 0; index < plan.Operations.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.Progress.Report(new(TransactionStage.Staging, index, plan.Operations.Count));
            TransactionFileOperation operation = plan.Operations[index];
            if (operation.Kind != TransactionOperationKind.WriteFile)
                continue;

            string source = TransactionPath.ResolveUnderRoot(payloadRoot, operation.PayloadRelativePath!);
            TransactionPath.AssertSafeParents(payloadRoot, source);
            TransactionPath.RequireRegularFile(source, TransactionErrorCode.PayloadMismatch, "A payload entry isn't a single-link regular file.");
            string actualHash = TransactionPath.ComputeSha256(source);
            if (!string.Equals(actualHash, operation.ExpectedResultSha256, StringComparison.Ordinal))
                throw new InstallerTransactionException(TransactionErrorCode.PayloadMismatch, "A payload entry doesn't match its expected digest.");

            string stagedPath = Path.Combine(transactionDirectory, "staged", index.ToString("D8"));
            using (FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan))
            using (FileStream output = new(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.SequentialScan))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            if (operation.ResultUnixMode is { } mode)
                TransactionPermissions.SetMode(stagedPath, mode);
            if (!string.Equals(TransactionPath.ComputeSha256(stagedPath), operation.ExpectedResultSha256, StringComparison.Ordinal))
                throw new InstallerTransactionException(TransactionErrorCode.PayloadMismatch, "A staged payload entry failed verification.");
        }
        TransactionDurability.FlushDirectory(Path.Combine(transactionDirectory, "staged"));
    }

    private void RevalidateAll(string gameRoot, TransactionPlan plan)
    {
        for (int index = 0; index < plan.Operations.Count; index++)
        {
            this.Progress.Report(new(TransactionStage.Revalidating, index, plan.Operations.Count));
            TransactionFileOperation operation = plan.Operations[index];
            string destination = TransactionPath.ResolveUnderRoot(gameRoot, operation.RelativePath);
            TransactionPath.AssertSafeParents(gameRoot, destination);
            ValidateExisting(destination, operation.ExpectedExistingSha256);
        }
    }

    private void ApplyMutations(string gameRoot, string transactionDirectory, TransactionPlan plan, TransactionJournal journal, string journalPath)
    {
        for (int index = 0; index < plan.Operations.Count; index++)
        {
            this.Progress.Report(new(TransactionStage.Applying, index, plan.Operations.Count));
            TransactionFileOperation operation = plan.Operations[index];
            string destination = TransactionPath.ResolveUnderRoot(gameRoot, operation.RelativePath);
            TransactionPath.AssertSafeParents(gameRoot, destination);
            PathEntry existing = ValidateExisting(destination, operation.ExpectedExistingSha256);

            string backupRelativePath = $"backups/{index:D8}";
            TransactionJournalEntry entry = new()
            {
                Index = index,
                Kind = operation.Kind,
                RelativePath = operation.RelativePath,
                HadOriginal = existing.Kind != PathEntryKind.Missing,
                ExpectedExistingSha256 = operation.ExpectedExistingSha256,
                ExpectedResultSha256 = operation.ExpectedResultSha256,
                BackupRelativePath = backupRelativePath
            };
            journal.Entries.Add(entry);
            TransactionJournalStore.WriteDurable(journalPath, journal);
            this.FaultInjector.BeforeMutation(plan.TransactionId, index);

            PathEntry immediatelyBeforeMutation = ValidateExisting(destination, operation.ExpectedExistingSha256);
            if (immediatelyBeforeMutation != existing)
                throw new InstallerTransactionException(TransactionErrorCode.PathChanged, "A destination's filesystem identity changed immediately before mutation.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            TransactionPath.AssertSafeParents(gameRoot, destination);
            if (entry.HadOriginal)
            {
                string backupPath = TransactionPath.ResolveUnderRoot(transactionDirectory, backupRelativePath);
                File.Move(destination, backupPath);
                TransactionDurability.FlushDirectory(Path.GetDirectoryName(destination)!);
                TransactionDurability.FlushDirectory(Path.GetDirectoryName(backupPath)!);
            }

            if (operation.Kind == TransactionOperationKind.WriteFile)
            {
                string stagedPath = Path.Combine(transactionDirectory, "staged", index.ToString("D8"));
                string temporaryDestination = destination + $".smapi-tmp-{plan.TransactionId:N}";
                if (TransactionPath.Inspect(temporaryDestination).Kind != PathEntryKind.Missing)
                    throw new InstallerTransactionException(TransactionErrorCode.PathChanged, "A transaction temporary path unexpectedly exists.");
                File.Move(stagedPath, temporaryDestination);
                File.Move(temporaryDestination, destination);
                TransactionDurability.FlushDirectory(Path.GetDirectoryName(destination)!);
            }

            entry.MutationApplied = true;
            TransactionJournalStore.WriteDurable(journalPath, journal);
            this.FaultInjector.AfterMutation(plan.TransactionId, index);
        }
    }

    private static PathEntry ValidateExisting(string destination, string? expectedHash)
    {
        PathEntry entry = TransactionPath.Inspect(destination);
        if (expectedHash is null)
        {
            if (entry.Kind != PathEntryKind.Missing)
                throw new InstallerTransactionException(TransactionErrorCode.ExistingFileMismatch, "A destination expected to be absent now exists.");
            return entry;
        }

        if (entry.Kind != PathEntryKind.RegularFile || entry.LinkCount != 1)
            throw new InstallerTransactionException(TransactionErrorCode.ExistingFileMismatch, "An existing destination isn't a single-link regular file.");
        string actualHash = TransactionPath.ComputeSha256(destination);
        if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
            throw new InstallerTransactionException(TransactionErrorCode.ExistingFileMismatch, "An existing destination changed after planning.");
        return entry;
    }

    private static void VerifyResults(string gameRoot, TransactionPlan plan)
    {
        foreach (TransactionFileOperation operation in plan.Operations)
        {
            string destination = TransactionPath.ResolveUnderRoot(gameRoot, operation.RelativePath);
            TransactionPath.AssertSafeParents(gameRoot, destination);
            PathEntry entry = TransactionPath.Inspect(destination);
            if (operation.Kind == TransactionOperationKind.RemoveFile)
            {
                if (entry.Kind != PathEntryKind.Missing)
                    throw new InstallerTransactionException(TransactionErrorCode.PathChanged, "A removed destination reappeared before commit.");
            }
            else
            {
                if (entry.Kind != PathEntryKind.RegularFile || entry.LinkCount != 1 || !string.Equals(TransactionPath.ComputeSha256(destination), operation.ExpectedResultSha256, StringComparison.Ordinal))
                    throw new InstallerTransactionException(TransactionErrorCode.PathChanged, "A written destination failed post-verification.");
            }
        }
    }

    private IReadOnlyList<TransactionResult> RecoverIncompleteTransactionsLocked(string gameRoot, string workspace)
    {
        string transactionsRoot = Path.Combine(workspace, "transactions");
        if (!Directory.Exists(transactionsRoot))
            return Array.Empty<TransactionResult>();

        List<TransactionResult> results = new();
        foreach (string directory in Directory.EnumerateDirectories(transactionsRoot).OrderBy(path => path, StringComparer.Ordinal))
        {
            string journalPath = Path.Combine(directory, "journal.json");
            if (!File.Exists(journalPath))
                throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "An unrecognized transaction workspace requires manual inspection.");
            TransactionJournal journal = TransactionJournalStore.Read(journalPath);
            if (!string.Equals(journal.CanonicalGameRoot, gameRoot, StringComparison.Ordinal))
                throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "A recovery journal belongs to a different game root.");
            if (journal.Status is TransactionJournalStatus.Committed or TransactionJournalStatus.RolledBack)
                continue;

            this.Progress.Report(new(TransactionStage.Recovering, 0, journal.Entries.Count));
            int changed = journal.Entries.Count(entry => entry.MutationApplied || File.Exists(Path.Combine(directory, entry.BackupRelativePath.Replace('/', Path.DirectorySeparatorChar))));
            this.RollBackJournal(gameRoot, directory, journal, journalPath);
            results.Add(new(journal.TransactionId, TransactionStatus.Recovered, changed));
        }
        return results;
    }

    private void RollBackJournal(string gameRoot, string transactionDirectory, TransactionJournal journal, string journalPath)
    {
        journal.Status = TransactionJournalStatus.RollingBack;
        TransactionJournalStore.WriteDurable(journalPath, journal);
        for (int index = journal.Entries.Count - 1; index >= 0; index--)
        {
            this.Progress.Report(new(TransactionStage.RollingBack, journal.Entries.Count - index - 1, journal.Entries.Count));
            TransactionJournalEntry entry = journal.Entries[index];
            string destination = TransactionPath.ResolveUnderRoot(gameRoot, entry.RelativePath);
            TransactionPath.AssertSafeParents(gameRoot, destination);
            string backupPath = TransactionPath.ResolveUnderRoot(transactionDirectory, entry.BackupRelativePath);

            PathEntry current = TransactionPath.Inspect(destination);
            PathEntry backup = TransactionPath.Inspect(backupPath);
            if (entry.HadOriginal && backup.Kind == PathEntryKind.Missing)
            {
                if (current.Kind != PathEntryKind.RegularFile || current.LinkCount != 1 || entry.ExpectedExistingSha256 is null || !string.Equals(TransactionPath.ComputeSha256(destination), entry.ExpectedExistingSha256, StringComparison.Ordinal))
                    throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "A transaction stopped before backing up its original file, but that file no longer matches the journal.");
                continue;
            }

            if (current.Kind != PathEntryKind.Missing)
            {
                if (current.Kind != PathEntryKind.RegularFile || current.LinkCount != 1)
                    throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "Recovery refused an unsafe changed destination.");
                File.Delete(destination);
                TransactionDurability.FlushDirectory(Path.GetDirectoryName(destination)!);
            }

            if (entry.HadOriginal)
            {
                if (backup.Kind != PathEntryKind.RegularFile || backup.LinkCount != 1)
                    throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "A required transaction backup is missing or unsafe.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(backupPath, destination);
                TransactionDurability.FlushDirectory(Path.GetDirectoryName(destination)!);
                TransactionDurability.FlushDirectory(Path.GetDirectoryName(backupPath)!);
            }
        }
        journal.Status = TransactionJournalStatus.RolledBack;
        TransactionJournalStore.WriteDurable(journalPath, journal);
    }

    private static string EnsureWorkspace(string gameRoot)
    {
        string workspace = Path.Combine(gameRoot, WorkspaceName);
        string marker = Path.Combine(workspace, "state-version");
        PathEntry existing = TransactionPath.Inspect(workspace);
        if (existing.Kind == PathEntryKind.Missing)
        {
            Directory.CreateDirectory(workspace);
            SetPrivateDirectoryModes(workspace);
            using FileStream markerStream = new(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using StreamWriter writer = new(markerStream, leaveOpen: true);
            writer.Write(WorkspaceMarkerContents);
            writer.Flush();
            markerStream.Flush(flushToDisk: true);
            TransactionPermissions.SetMode(marker, Convert.ToInt32("600", 8));
            TransactionDurability.FlushDirectory(workspace);
        }
        else
        {
            if (existing.Kind != PathEntryKind.Directory || !File.Exists(marker) || File.ReadAllText(marker) != WorkspaceMarkerContents)
                throw new InstallerTransactionException(TransactionErrorCode.WorkspaceConflict, "An unknown .smapi-installer entry blocks safe installation.");
            TransactionPath.RequireRegularFile(marker, TransactionErrorCode.WorkspaceConflict, "The installer workspace marker is unsafe.");
        }
        return workspace;
    }

    private static FileStream AcquireLock(string workspace)
    {
        string lockPath = Path.Combine(workspace, "operation.lock");
        try
        {
            FileStream stream = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            TransactionPermissions.SetMode(lockPath, Convert.ToInt32("600", 8));
            return stream;
        }
        catch (IOException exception)
        {
            throw new InstallerTransactionException(TransactionErrorCode.ConcurrentOperation, "Another installer operation is already using this game directory.", exception);
        }
    }

    private static void SetPrivateDirectoryModes(string root)
    {
        TransactionPermissions.SetMode(root, Convert.ToInt32("700", 8));
        foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            TransactionPermissions.SetMode(directory, Convert.ToInt32("700", 8));
    }
}

internal static class TransactionPermissions
{
    public static void SetMode(string path, int mode)
    {
        if (!OperatingSystem.IsLinux())
            return;
        if (mode is < 0 or > 0x1ff)
            throw new InstallerTransactionException(TransactionErrorCode.InvalidPlan, "A Unix mode is outside the supported permission bits.");
        if (chmod(path, (uint)mode) != 0)
            throw new IOException("Couldn't set private transaction permissions.");
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int chmod(string path, uint mode);
}
