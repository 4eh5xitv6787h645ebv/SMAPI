using System.Text;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Transactions;

/// <summary>Applies immutable Linux file plans through anchored descriptors with durable exact rollback.</summary>
internal sealed class InstallerTransactionExecutor
{
    internal const string WorkspaceName = ".smapi-installer";
    private const string TransactionsName = "transactions";
    internal const string WorkspaceMarkerName = "state-version";
    internal const string WorkspaceMarkerContents = "smapi-installer-state-v2\n";
    private const int MaximumRetainedFinalTransactions = 16;
    private const int MaximumTransactionStoreEntries = 32;
    private readonly ITransactionProgressSink Progress;
    private readonly ITransactionFaultInjector FaultInjector;

    /// <summary>Construct an instance.</summary>
    public InstallerTransactionExecutor(ITransactionProgressSink? progress = null, ITransactionFaultInjector? faultInjector = null)
    {
        this.Progress = new NonThrowingTransactionProgressSink(progress);
        this.FaultInjector = faultInjector ?? NullTransactionInstrumentation.Instance;
    }

    /// <summary>Apply an immutable transaction plan.</summary>
    /// <remarks>Cancellation is honored through staging and final revalidation. Once mutation begins, the executor finishes commit or rollback.</remarks>
    public TransactionResult Apply(string gameRoot, string payloadRoot, TransactionPlan plan, CancellationToken cancellationToken = default)
    {
        LinuxPrivilegeGuard.AssertNotRoot();
        ArgumentNullException.ThrowIfNull(plan);
        string canonicalGameRoot = TransactionPath.GetCanonicalRoot(gameRoot, nameof(gameRoot));
        string canonicalPayloadRoot = TransactionPath.GetCanonicalRoot(payloadRoot, nameof(payloadRoot));
        ValidatePlan(plan);

        using LinuxAnchoredFileSystem payload = new(canonicalPayloadRoot);
        this.Progress.Report(new(TransactionStage.AcquiringLock, 0, plan.Operations.Count));
        using InstallerOperationLease lease = InstallerOperationLease.Acquire(canonicalGameRoot);
        LinuxAnchoredFileSystem game = lease.Game;
        LinuxAnchoredFileSystem workspace = lease.Workspace;

        this.Progress.Report(new(TransactionStage.Recovering, 0, plan.Operations.Count));
        this.RecoverIncompleteTransactionsLocked(game, workspace, canonicalGameRoot);
        lease.ReserveNextGeneration(lease.Generation);
        return this.ApplyLockedCore(lease, payload, plan, cancellationToken);
    }

    /// <summary>Apply through the same exclusive root lease which was revalidated against user confirmation.</summary>
    internal TransactionResult ApplyLocked(
        InstallerOperationLease lease,
        LinuxAnchoredFileSystem payload,
        TransactionPlan plan,
        GameRootIdentity expectedRoot,
        ulong expectedGeneration,
        CancellationToken cancellationToken = default
    )
    {
        LinuxPrivilegeGuard.AssertNotRoot();
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(plan);
        ValidatePlan(plan);
        lease.AssertRootAndGeneration(expectedRoot, expectedGeneration);
        this.Progress.Report(new(TransactionStage.Recovering, 0, plan.Operations.Count));
        IReadOnlyList<TransactionResult> recovered = this.RecoverIncompleteTransactionsLocked(
            lease.Game,
            lease.Workspace,
            lease.CanonicalGameRoot
        );
        if (recovered.Count > 0)
        {
            lease.ReserveNextGeneration(expectedGeneration);
            throw new InstallerTransactionException(
                TransactionErrorCode.PathChanged,
                "Crash recovery invalidated the confirmed installer operation generation. Inspect and confirm the new plan."
            );
        }
        lease.AssertRootAndGeneration(expectedRoot, expectedGeneration);
        lease.ReserveNextGeneration(expectedGeneration);
        return this.ApplyLockedCore(lease, payload, plan, cancellationToken);
    }

    private TransactionResult ApplyLockedCore(
        InstallerOperationLease lease,
        LinuxAnchoredFileSystem payload,
        TransactionPlan plan,
        CancellationToken cancellationToken
    )
    {
        LinuxAnchoredFileSystem game = lease.Game;
        LinuxAnchoredFileSystem workspace = lease.Workspace;
        string canonicalGameRoot = lease.CanonicalGameRoot;

        TransactionJournal journal = BuildJournal(game, canonicalGameRoot, plan);
        string transactionName = plan.TransactionId.ToString("N");
        string preparationName = $"preparing-{transactionName}";
        string transactionRelativePath = $"{WorkspaceName}/{TransactionsName}/{transactionName}";
        string preparationRelativePath = $"{WorkspaceName}/{TransactionsName}/{preparationName}";
        if (game.Stat(transactionRelativePath) is not null || game.Stat(preparationRelativePath) is not null)
            throw new InstallerTransactionException(TransactionErrorCode.WorkspaceConflict, "A transaction with this ID already exists.");

        game.EnsureDirectory(preparationRelativePath, 0x1c0, out bool transactionCreated);
        if (!transactionCreated)
            throw new InstallerTransactionException(TransactionErrorCode.WorkspaceConflict, "The transaction directory appeared concurrently.");
        LinuxAnchoredFileSystem? preparedTransaction = null;
        LinuxAnchoredFile? preparedEventsFile = null;
        TransactionJournalReplay replay;
        try
        {
            this.FaultInjector.AtSetupBoundary(plan.TransactionId, TransactionSetupBoundary.PreparationDirectoryCreated);
            preparedTransaction = game.OpenSubdirectory(preparationRelativePath);
            preparedTransaction.EnsureDirectory("staged", 0x1c0);
            preparedTransaction.EnsureDirectory("backups", 0x1c0);
            this.FaultInjector.AtSetupBoundary(plan.TransactionId, TransactionSetupBoundary.PayloadDirectoriesCreated);
            TransactionJournalStore.Create(preparedTransaction, journal);
            this.FaultInjector.AtSetupBoundary(plan.TransactionId, TransactionSetupBoundary.ImmutablePlanCreated);
            replay = TransactionJournalStore.ReadEvents(preparedTransaction, journal);
            preparedEventsFile = TransactionJournalStore.OpenEventsForAppend(preparedTransaction, replay);
            replay = TransactionJournalStore.Append(preparedTransaction, preparedEventsFile, journal, replay, TransactionJournalEventKind.Created);
            this.FaultInjector.AtSetupBoundary(plan.TransactionId, TransactionSetupBoundary.CreationEventCreated);
            LinuxFileIdentity preparationIdentity = game.Stat(preparationRelativePath)
                ?? throw new InstallerTransactionException(TransactionErrorCode.WorkspaceConflict, "The prepared transaction disappeared before publication.");
            game.RenameDirectoryNoReplace(preparationRelativePath, transactionRelativePath, preparationIdentity);
            this.FaultInjector.AtSetupBoundary(plan.TransactionId, TransactionSetupBoundary.TransactionPublished);
        }
        catch (SimulatedProcessTerminationException)
        {
            preparedEventsFile?.Dispose();
            preparedTransaction?.Dispose();
            throw;
        }
        catch
        {
            preparedEventsFile?.Dispose();
            preparedTransaction?.Dispose();
            this.RecoverIncompleteTransactionsLocked(game, workspace, canonicalGameRoot);
            throw;
        }
        using LinuxAnchoredFileSystem transaction = preparedTransaction;
        using LinuxAnchoredFile eventsFile = preparedEventsFile;

        bool committed = false;
        try
        {
            this.StagePayload(payload, transaction, plan, cancellationToken);
            replay = TransactionJournalStore.Append(transaction, eventsFile, journal, replay, TransactionJournalEventKind.Prepared);
            cancellationToken.ThrowIfCancellationRequested();
            this.RevalidateAll(game, plan, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            replay = TransactionJournalStore.Append(transaction, eventsFile, journal, replay, TransactionJournalEventKind.Applying);
            replay = this.ApplyMutations(game, transaction, plan, journal, replay, eventsFile);

            this.Progress.Report(new(TransactionStage.Verifying, plan.Operations.Count, plan.Operations.Count));
            VerifyResults(game, plan);
            this.Progress.Report(new(TransactionStage.Committing, plan.Operations.Count, plan.Operations.Count));
            replay = TransactionJournalStore.Append(transaction, eventsFile, journal, replay, TransactionJournalEventKind.Committed);
            committed = true;
        }
        catch (SimulatedProcessTerminationException)
        {
            throw;
        }
        catch
        {
            eventsFile.Dispose();
            this.RollBackJournal(game, transaction, journal);
            CleanupFinalTransaction(transaction);
            throw;
        }

        if (!committed)
            throw new InstallerTransactionException(TransactionErrorCode.IoFailure, "The transaction ended without a durable terminal event.");
        CleanupFinalTransaction(transaction);
        this.TrimFinalTransactions(game, workspace, canonicalGameRoot);
        this.Progress.Report(new(TransactionStage.Completed, plan.Operations.Count, plan.Operations.Count));
        return new(plan.TransactionId, TransactionStatus.Committed, plan.Operations.Count);
    }

    /// <summary>Recover every incomplete transaction under a game root.</summary>
    public IReadOnlyList<TransactionResult> RecoverIncompleteTransactions(string gameRoot)
    {
        LinuxPrivilegeGuard.AssertNotRoot();
        string canonicalGameRoot = TransactionPath.GetCanonicalRoot(gameRoot, nameof(gameRoot));
        using InstallerOperationLease lease = InstallerOperationLease.Acquire(canonicalGameRoot);
        LinuxAnchoredFileSystem game = lease.Game;
        LinuxAnchoredFileSystem workspace = lease.Workspace;
        IReadOnlyList<TransactionResult> results = this.RecoverIncompleteTransactionsLocked(game, workspace, canonicalGameRoot);
        if (results.Count > 0)
            lease.ReserveNextGeneration(lease.Generation);
        this.TrimFinalTransactions(game, workspace, canonicalGameRoot);
        return results;
    }

    /// <summary>Recover under an already-held operation lease and invalidate its prior generation when needed.</summary>
    internal IReadOnlyList<TransactionResult> RecoverLocked(InstallerOperationLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        IReadOnlyList<TransactionResult> results = this.RecoverIncompleteTransactionsLocked(
            lease.Game,
            lease.Workspace,
            lease.CanonicalGameRoot
        );
        if (results.Count > 0)
            lease.ReserveNextGeneration(lease.Generation);
        this.TrimFinalTransactions(lease.Game, lease.Workspace, lease.CanonicalGameRoot);
        return results;
    }

    private static void ValidatePlan(TransactionPlan plan)
    {
        HashSet<string> destinations = new(StringComparer.Ordinal);
        HashSet<string> caseInsensitiveDestinations = new(StringComparer.OrdinalIgnoreCase);
        for (int operationIndex = 0; operationIndex < plan.Operations.Count; operationIndex++)
        {
            TransactionFileOperation operation = plan.Operations[operationIndex];
            if (!Enum.IsDefined(typeof(TransactionOperationKind), operation.Kind))
                throw new InstallerTransactionException(TransactionErrorCode.InvalidPlan, "A transaction contains an unknown operation kind.");
            string relativePath = TransactionPath.NormalizeRelativePath(operation.RelativePath, nameof(operation.RelativePath));
            bool allowed;
            try
            {
                allowed = OwnedNamespacePolicy.IsAllowedTransactionDestination(NormalizedRelativePath.Parse(relativePath))
                    || IsAuthorizedCoreStatePath(plan, operationIndex, relativePath);
            }
            catch (ArgumentException exception)
            {
                throw new InstallerTransactionException(TransactionErrorCode.InvalidPlan, "A destination path isn't canonical or exceeds its bound.", exception);
            }
            if (!allowed)
                throw new InstallerTransactionException(TransactionErrorCode.InvalidPlan, "A transaction destination isn't in the compiled installer-owned allowlist.");
            if (!destinations.Add(relativePath) || !caseInsensitiveDestinations.Add(relativePath))
                throw new InstallerTransactionException(TransactionErrorCode.InvalidPlan, "A transaction contains duplicate or case-colliding destinations.");
            ValidateSha256(operation.ExpectedExistingSha256, allowNull: true, nameof(operation.ExpectedExistingSha256));
            if (operation.ExpectedExistingUnixMode is < 0 or > 0x1ff)
                throw new InstallerTransactionException(TransactionErrorCode.InvalidPlan, "An expected Unix mode is outside the supported permission bits.");
            if (operation.ExpectedExistingSha256 is null && operation.ExpectedExistingUnixMode is not null)
                throw new InstallerTransactionException(TransactionErrorCode.InvalidPlan, "An absent destination can't have an expected Unix mode.");

            if (operation.Kind == TransactionOperationKind.WriteFile)
            {
                if (operation.PayloadRelativePath is null || operation.ExpectedResultSha256 is null)
                    throw new InstallerTransactionException(TransactionErrorCode.InvalidPlan, "A write operation requires a payload path and result digest.");
                string payloadPath = TransactionPath.NormalizeRelativePath(operation.PayloadRelativePath, nameof(operation.PayloadRelativePath));
                try
                {
                    NormalizedRelativePath.Parse(payloadPath);
                }
                catch (ArgumentException exception)
                {
                    throw new InstallerTransactionException(TransactionErrorCode.InvalidPlan, "A payload path isn't canonical or exceeds its bound.", exception);
                }
                ValidateSha256(operation.ExpectedResultSha256, allowNull: false, nameof(operation.ExpectedResultSha256));
                if (operation.ResultUnixMode is < 0 or > 0x1ff)
                    throw new InstallerTransactionException(TransactionErrorCode.InvalidPlan, "A Unix mode is outside the supported permission bits.");
            }
            else if (operation.PayloadRelativePath is not null || operation.ExpectedResultSha256 is not null || operation.ResultUnixMode is not null)
                throw new InstallerTransactionException(TransactionErrorCode.InvalidPlan, "A remove operation can't include payload fields.");
        }
        HashSet<string> preconditions = new(StringComparer.Ordinal);
        foreach (TransactionFilePrecondition precondition in plan.Preconditions)
        {
            string path = TransactionPath.NormalizeRelativePath(precondition.RelativePath, nameof(precondition.RelativePath));
            if (
                !preconditions.Add(path)
                || destinations.Contains(path)
                || precondition.ExpectedUnixMode is < 0 or > 0x1ff
            )
                throw new InstallerTransactionException(TransactionErrorCode.InvalidPlan, "A transaction precondition isn't unique and safe.");
            ValidateSha256(precondition.ExpectedSha256, allowNull: false, nameof(precondition.ExpectedSha256));
        }
    }

    private static bool IsAuthorizedCoreStatePath(TransactionPlan plan, int operationIndex, string relativePath)
    {
        CoreReservedMutationAuthorization? authorization = plan.CoreAuthorization;
        if (authorization is null)
            return false;

        if (authorization.GenerationId != plan.TransactionId)
            return false;
        if (operationIndex < authorization.RecoveryOperationCount)
        {
            string prefix = $".smapi-installer/recovery/generations/{plan.TransactionId:N}/";
            return relativePath.StartsWith(prefix, StringComparison.Ordinal);
        }
        if (operationIndex == plan.Operations.Count - 1)
            return relativePath == TransactionPlan.CoreRecoveryPointerRelativePath;

        int receiptIndex = plan.Operations.Count - 2;
        if (authorization.HasReceiptMutation && operationIndex == receiptIndex)
            return relativePath == TransactionPlan.CoreReceiptRelativePath;
        int manifestIndex = receiptIndex - (authorization.HasReceiptMutation ? 1 : 0);
        return authorization.HasManifestMutation
            && operationIndex == manifestIndex
            && relativePath == TransactionPlan.CoreManifestRelativePath;
    }

    private static void ValidateSha256(string? value, bool allowNull, string name)
    {
        if (value is null && allowNull)
            return;
        if (value is null || value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InstallerTransactionException(TransactionErrorCode.InvalidPlan, $"{name} must be a lowercase SHA-256 digest.");
    }

    private static TransactionJournal BuildJournal(LinuxAnchoredFileSystem game, string canonicalGameRoot, TransactionPlan plan)
    {
        HashSet<string> assignedCreatedDirectories = new(StringComparer.Ordinal);
        TransactionJournal journal = new()
        {
            TransactionId = plan.TransactionId,
            CreatedUtcTicks = DateTime.UtcNow.Ticks,
            CanonicalGameRoot = canonicalGameRoot,
            GameRootInode = game.Identity.Inode,
            GameRootDeviceMajor = game.Identity.DeviceMajor,
            GameRootDeviceMinor = game.Identity.DeviceMinor,
            HasCoreAuthorizedReceiptMutation = plan.HasCoreAuthorizedReceiptMutation,
            CoreGenerationId = plan.CoreAuthorization?.GenerationId,
            CoreRecoveryOperationCount = plan.CoreAuthorization?.RecoveryOperationCount ?? 0,
            CoreRecoveryContentCount = plan.CoreAuthorization?.RecoveryContentCount ?? 0,
            HasCoreAuthorizedManifestMutation = plan.CoreAuthorization?.HasManifestMutation ?? false,
            HasCoreAuthorizedRecoveryPointerMutation = plan.CoreAuthorization is not null
        };
        for (int index = 0; index < plan.Operations.Count; index++)
        {
            TransactionFileOperation operation = plan.Operations[index];
            IReadOnlyList<string> missingParents = GetMissingParentDirectories(game, operation.RelativePath);
            ValidateExisting(
                game,
                operation.RelativePath,
                operation.ExpectedExistingSha256,
                operation.ExpectedExistingUnixMode,
                TransactionErrorCode.ExistingFileMismatch
            );
            journal.Entries.Add(new TransactionJournalEntry
            {
                Index = index,
                Kind = operation.Kind,
                RelativePath = operation.RelativePath,
                HadOriginal = operation.ExpectedExistingSha256 is not null,
                ExpectedExistingSha256 = operation.ExpectedExistingSha256,
                ExpectedResultSha256 = operation.ExpectedResultSha256,
                ResultUnixMode = operation.ResultUnixMode,
                BackupRelativePath = $"backups/{index:D8}",
                StagedRelativePath = operation.Kind == TransactionOperationKind.WriteFile ? $"staged/{index:D8}" : null,
                CreatedDirectories = missingParents
                    .Where(assignedCreatedDirectories.Add)
                    .ToList()
            });
        }
        return journal;
    }

    private void StagePayload(LinuxAnchoredFileSystem payload, LinuxAnchoredFileSystem transaction, TransactionPlan plan, CancellationToken cancellationToken)
    {
        for (int index = 0; index < plan.Operations.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.Progress.Report(new(TransactionStage.Staging, index, plan.Operations.Count));
            TransactionFileOperation operation = plan.Operations[index];
            if (operation.Kind != TransactionOperationKind.WriteFile)
                continue;
            try
            {
                using LinuxAnchoredFile source = payload.OpenRegularFileForRead(operation.PayloadRelativePath!);
                string actualHash = payload.ComputeSha256(source, cancellationToken);
                if (!string.Equals(actualHash, operation.ExpectedResultSha256, StringComparison.Ordinal))
                    throw new InstallerTransactionException(TransactionErrorCode.PayloadMismatch, "A payload entry doesn't match its expected digest.");
                int mode = operation.ResultUnixMode ?? source.Identity.UnixMode;
                LinuxFileIdentity staged = transaction.CopyFile(source, $"staged/{index:D8}", mode, cancellationToken);
                using LinuxAnchoredFile stagedFile = transaction.OpenRegularFileForRead($"staged/{index:D8}");
                if (
                    staged != stagedFile.Identity
                    || transaction.ComputeSha256(stagedFile, cancellationToken) != operation.ExpectedResultSha256
                )
                    throw new InstallerTransactionException(TransactionErrorCode.PayloadMismatch, "A staged payload entry failed verification.");
            }
            catch (InstallerTransactionException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InstallerTransactionException(TransactionErrorCode.PayloadMismatch, "A payload entry isn't a stable single-link regular file.", exception);
            }
        }
        transaction.FsyncDirectory("staged");
    }

    private void RevalidateAll(
        LinuxAnchoredFileSystem game,
        TransactionPlan plan,
        CancellationToken cancellationToken
    )
    {
        RevalidatePreconditions(game, plan.Preconditions, cancellationToken);
        for (int index = 0; index < plan.Operations.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.Progress.Report(new(TransactionStage.Revalidating, index, plan.Operations.Count));
            TransactionFileOperation operation = plan.Operations[index];
            ValidateExisting(
                game,
                operation.RelativePath,
                operation.ExpectedExistingSha256,
                operation.ExpectedExistingUnixMode,
                TransactionErrorCode.ExistingFileMismatch,
                cancellationToken
            );
        }
    }

    private static void RevalidatePreconditions(
        LinuxAnchoredFileSystem game,
        IReadOnlyList<TransactionFilePrecondition> preconditions,
        CancellationToken cancellationToken = default
    )
    {
        foreach (TransactionFilePrecondition precondition in preconditions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateExisting(
                game,
                precondition.RelativePath,
                precondition.ExpectedSha256,
                precondition.ExpectedUnixMode,
                TransactionErrorCode.ExistingFileMismatch,
                cancellationToken
            );
        }
    }

    private TransactionJournalReplay ApplyMutations(
        LinuxAnchoredFileSystem game,
        LinuxAnchoredFileSystem transaction,
        TransactionPlan plan,
        TransactionJournal journal,
        TransactionJournalReplay replay,
        LinuxAnchoredFile eventsFile
    )
    {
        string transactionPrefix = $"{WorkspaceName}/{TransactionsName}/{plan.TransactionId:N}/";
        for (int index = 0; index < plan.Operations.Count; index++)
        {
            this.Progress.Report(new(TransactionStage.Applying, index, plan.Operations.Count));
            TransactionFileOperation operation = plan.Operations[index];
            TransactionJournalEntry entry = journal.Entries[index];
            LinuxFileIdentity? existing = ValidateExisting(
                game,
                operation.RelativePath,
                operation.ExpectedExistingSha256,
                operation.ExpectedExistingUnixMode,
                TransactionErrorCode.PathChanged
            );
            LinuxFileIdentity? staged = operation.Kind == TransactionOperationKind.WriteFile
                ? ValidateFile(transaction, entry.StagedRelativePath!, operation.ExpectedResultSha256!, TransactionErrorCode.PayloadMismatch)
                : null;

            replay = TransactionJournalStore.Append(transaction, eventsFile, journal, replay, TransactionJournalEventKind.Intent, index);
            this.FaultInjector.BeforeMutation(plan.TransactionId, index);
            if (index == 0)
                RevalidatePreconditions(game, plan.Preconditions);
            LinuxFileIdentity? immediatelyBefore = ValidateExisting(
                game,
                operation.RelativePath,
                operation.ExpectedExistingSha256,
                operation.ExpectedExistingUnixMode,
                TransactionErrorCode.PathChanged
            );
            if (existing != immediatelyBefore)
                throw new InstallerTransactionException(TransactionErrorCode.PathChanged, "A destination identity changed immediately before mutation.");

            foreach (string directory in entry.CreatedDirectories)
            {
                if (game.Stat(directory) is not null)
                    throw new InstallerTransactionException(TransactionErrorCode.PathChanged, "A planned destination directory appeared before mutation.");
                game.EnsureDirectory(directory, 0x1c0, out bool created);
                if (!created)
                    throw new InstallerTransactionException(TransactionErrorCode.PathChanged, "A planned destination directory wasn't exclusively created.");
            }
            if (existing is not null)
                game.RenameFileNoReplace(operation.RelativePath, transactionPrefix + entry.BackupRelativePath, existing);
            if (operation.Kind == TransactionOperationKind.WriteFile)
                game.RenameFileNoReplace(transactionPrefix + entry.StagedRelativePath, operation.RelativePath, staged!);

            replay = TransactionJournalStore.Append(transaction, eventsFile, journal, replay, TransactionJournalEventKind.Applied, index);
            this.FaultInjector.AfterMutation(plan.TransactionId, index);
        }
        return replay;
    }

    private static LinuxFileIdentity? ValidateExisting(
        LinuxAnchoredFileSystem fileSystem,
        string relativePath,
        string? expectedHash,
        int? expectedUnixMode,
        TransactionErrorCode code,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            LinuxFileIdentity? identity = fileSystem.Stat(relativePath);
            if (expectedHash is null)
            {
                if (expectedUnixMode is not null)
                    throw new InstallerTransactionException(code, "An absent destination can't have an expected Unix mode.");
                if (identity is not null)
                    throw new InstallerTransactionException(code, "A destination expected to be absent now exists.");
                return null;
            }
            if (identity is null || identity.Kind != LinuxAnchoredEntryKind.RegularFile || identity.LinkCount != 1)
                throw new InstallerTransactionException(code, "An existing destination isn't a single-link regular file.");
            using LinuxAnchoredFile opened = fileSystem.OpenRegularFileForRead(relativePath);
            if (
                opened.Identity != identity
                || (expectedUnixMode is not null && identity.UnixMode != expectedUnixMode)
                || fileSystem.ComputeSha256(opened, cancellationToken) != expectedHash
            )
                throw new InstallerTransactionException(code, "An existing destination changed after planning.");
            return identity;
        }
        catch (InstallerTransactionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InstallerTransactionException(code, "A destination path is unsafe or changed during inspection.", exception);
        }
    }

    private static LinuxFileIdentity ValidateFile(LinuxAnchoredFileSystem fileSystem, string path, string digest, TransactionErrorCode code)
    {
        try
        {
            using LinuxAnchoredFile opened = fileSystem.OpenRegularFileForRead(path);
            if (fileSystem.ComputeSha256(opened) != digest)
                throw new InstallerTransactionException(code, "A transaction file failed digest verification.");
            return opened.Identity;
        }
        catch (InstallerTransactionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InstallerTransactionException(code, "A transaction file is missing or unsafe.", exception);
        }
    }

    private static void VerifyResults(LinuxAnchoredFileSystem game, TransactionPlan plan)
    {
        foreach (TransactionFileOperation operation in plan.Operations)
        {
            if (operation.Kind == TransactionOperationKind.RemoveFile)
            {
                if (game.Stat(operation.RelativePath) is not null)
                    throw new InstallerTransactionException(TransactionErrorCode.PathChanged, "A removed destination reappeared before commit.");
                continue;
            }
            LinuxFileIdentity identity = ValidateFile(game, operation.RelativePath, operation.ExpectedResultSha256!, TransactionErrorCode.PathChanged);
            if (operation.ResultUnixMode is { } expectedMode && identity.UnixMode != expectedMode)
                throw new InstallerTransactionException(TransactionErrorCode.PathChanged, "A written destination has incorrect permissions.");
        }
        RevalidatePreconditions(game, plan.Preconditions);
    }

    private IReadOnlyList<TransactionResult> RecoverIncompleteTransactionsLocked(
        LinuxAnchoredFileSystem game,
        LinuxAnchoredFileSystem workspace,
        string canonicalGameRoot
    )
    {
        using LinuxAnchoredFileSystem transactions = workspace.OpenSubdirectory(TransactionsName);
        List<TransactionResult> results = new();
        foreach (string name in EnumerateTransactionStore(transactions))
        {
            LinuxFileIdentity? identity = transactions.Stat(name);
            if (name.StartsWith("preparing-", StringComparison.Ordinal))
            {
                if (
                    identity?.Kind != LinuxAnchoredEntryKind.Directory
                    || !Guid.TryParseExact(name["preparing-".Length..], "N", out _)
                )
                    throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "An unsafe preparation workspace requires manual inspection.");
                CleanupUnpublishedTransaction(transactions, name, identity);
                continue;
            }
            if (identity?.Kind != LinuxAnchoredEntryKind.Directory || !Guid.TryParseExact(name, "N", out Guid transactionId))
                throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "An unsafe or unrecognized transaction workspace requires manual inspection.");
            using LinuxAnchoredFileSystem transaction = transactions.OpenSubdirectory(name);
            TransactionJournal journal = TransactionJournalStore.ReadPlan(transaction, transactionId, canonicalGameRoot, game.Identity);
            TransactionJournalReplay replay = TransactionJournalStore.ReadEvents(transaction, journal);
            if (replay.Status is TransactionJournalEventKind.Committed or TransactionJournalEventKind.RolledBack)
            {
                CleanupFinalTransaction(transaction);
                continue;
            }
            this.Progress.Report(new(TransactionStage.Recovering, 0, journal.Entries.Count));
            int changed = replay.IntendedOperations.Count;
            this.RollBackJournal(game, transaction, journal);
            CleanupFinalTransaction(transaction);
            results.Add(new(transactionId, TransactionStatus.Recovered, changed));
        }
        return results;
    }

    private void RollBackJournal(LinuxAnchoredFileSystem game, LinuxAnchoredFileSystem transaction, TransactionJournal journal)
    {
        TransactionJournalReplay replay = TransactionJournalStore.ReadEvents(transaction, journal);
        if (replay.Status == TransactionJournalEventKind.RolledBack)
            return;
        if (replay.Status == TransactionJournalEventKind.Committed)
            throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "A committed transaction can't be rolled back by crash recovery.");
        using LinuxAnchoredFile eventsFile = TransactionJournalStore.OpenEventsForAppend(transaction, replay);
        if (replay.Status != TransactionJournalEventKind.RollingBack && replay.Status != TransactionJournalEventKind.RollbackApplied)
            replay = TransactionJournalStore.Append(transaction, eventsFile, journal, replay, TransactionJournalEventKind.RollingBack);

        string transactionPrefix = $"{WorkspaceName}/{TransactionsName}/{journal.TransactionId:N}/";
        foreach (int index in replay.IntendedOperations.OrderByDescending(value => value))
        {
            if (replay.RolledBackOperations.Contains(index))
                continue;
            this.Progress.Report(new(TransactionStage.RollingBack, replay.RolledBackOperations.Count, replay.IntendedOperations.Count));
            TransactionJournalEntry entry = journal.Entries[index];
            string backupPath = transactionPrefix + entry.BackupRelativePath;
            LinuxFileIdentity? current = game.Stat(entry.RelativePath);
            LinuxFileIdentity? backup = game.Stat(backupPath);

            if (entry.HadOriginal)
            {
                if (backup is null)
                {
                    if (current is null || entry.ExpectedExistingSha256 is null)
                        throw RecoveryError("A required original and its backup are both missing.");
                    ValidateFile(game, entry.RelativePath, entry.ExpectedExistingSha256, TransactionErrorCode.RecoveryFailed);
                }
                else
                {
                    LinuxFileIdentity verifiedBackup = ValidateFile(game, backupPath, entry.ExpectedExistingSha256!, TransactionErrorCode.RecoveryFailed);
                    if (current is not null)
                    {
                        if (entry.Kind != TransactionOperationKind.WriteFile || entry.ExpectedResultSha256 is null)
                            throw RecoveryError("An unexpected file blocks exact rollback.");
                        LinuxFileIdentity verifiedResult = ValidateFile(game, entry.RelativePath, entry.ExpectedResultSha256, TransactionErrorCode.RecoveryFailed);
                        game.UnlinkFile(entry.RelativePath, verifiedResult);
                    }
                    game.RenameFileNoReplace(backupPath, entry.RelativePath, verifiedBackup);
                }
            }
            else
            {
                if (backup is not null)
                    throw RecoveryError("A transaction backup exists for a destination which was originally absent.");
                if (current is not null)
                {
                    if (entry.Kind != TransactionOperationKind.WriteFile || entry.ExpectedResultSha256 is null)
                        throw RecoveryError("An unexpected result blocks exact rollback.");
                    LinuxFileIdentity verifiedResult = ValidateFile(game, entry.RelativePath, entry.ExpectedResultSha256, TransactionErrorCode.RecoveryFailed);
                    game.UnlinkFile(entry.RelativePath, verifiedResult);
                }
            }

            foreach (string directory in entry.CreatedDirectories.AsEnumerable().Reverse())
            {
                LinuxFileIdentity? directoryIdentity = game.Stat(directory);
                if (directoryIdentity is null)
                    continue;
                if (directoryIdentity.Kind != LinuxAnchoredEntryKind.Directory)
                    throw RecoveryError("A transaction-created parent changed type during rollback.");
                using LinuxAnchoredFileSystem opened = game.OpenSubdirectory(directory);
                if (opened.EnumerateEntryNames().Count == 0)
                    game.RemoveEmptyDirectory(directory, directoryIdentity);
            }
            replay = TransactionJournalStore.Append(transaction, eventsFile, journal, replay, TransactionJournalEventKind.RollbackApplied, index);
        }
        TransactionJournalStore.Append(transaction, eventsFile, journal, replay, TransactionJournalEventKind.RolledBack);
    }

    internal static LinuxAnchoredFileSystem EnsureWorkspace(LinuxAnchoredFileSystem game)
    {
        try
        {
            LinuxFileIdentity? workspaceIdentity = game.Stat(WorkspaceName);
            if (workspaceIdentity is null)
            {
                game.EnsureDirectory(WorkspaceName, 0x1c0, out bool created);
                if (!created)
                    throw new IOException("The workspace appeared concurrently.");
                using LinuxAnchoredFileSystem createdWorkspace = game.OpenSubdirectory(WorkspaceName);
                byte[] markerBytes = Encoding.UTF8.GetBytes(WorkspaceMarkerContents);
                using LinuxAnchoredFile marker = createdWorkspace.CreateNewFile(WorkspaceMarkerName, 0x180);
                createdWorkspace.AppendAndFsync(marker, WorkspaceMarkerName, markerBytes, 0, markerBytes.Length);
            }
            else if (workspaceIdentity.Kind != LinuxAnchoredEntryKind.Directory)
                throw new IOException("The reserved workspace isn't a directory.");

            LinuxAnchoredFileSystem workspace = game.OpenSubdirectory(WorkspaceName);
            try
            {
                using LinuxAnchoredFile marker = workspace.OpenRegularFileForRead(WorkspaceMarkerName);
                byte[] expected = Encoding.UTF8.GetBytes(WorkspaceMarkerContents);
                if (!workspace.ReadAllBytes(marker, expected.Length).AsSpan().SequenceEqual(expected))
                    throw new IOException("The workspace marker is unknown.");
                workspace.EnsureDirectory(TransactionsName, 0x1c0);
                return workspace;
            }
            catch
            {
                workspace.Dispose();
                throw;
            }
        }
        catch (InstallerTransactionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InstallerTransactionException(TransactionErrorCode.WorkspaceConflict, "An unknown or unsafe .smapi-installer entry blocks safe installation.", exception);
        }
    }

    internal static LinuxAnchoredFile AcquireLock(LinuxAnchoredFileSystem workspace)
    {
        try
        {
            return workspace.AcquireExclusiveFileLock("operation.lock", 0x180);
        }
        catch (IOException exception)
        {
            throw new InstallerTransactionException(TransactionErrorCode.ConcurrentOperation, "Another installer operation is already using this game directory.", exception);
        }
    }

    private static IReadOnlyList<string> GetMissingParentDirectories(LinuxAnchoredFileSystem game, string destination)
    {
        string[] segments = destination.Split('/');
        List<string> missing = new();
        for (int count = 1; count < segments.Length; count++)
        {
            string parent = string.Join('/', segments.Take(count));
            LinuxFileIdentity? identity;
            try
            {
                identity = game.Stat(parent);
            }
            catch (IOException exception)
            {
                throw new InstallerTransactionException(TransactionErrorCode.UnsafePath, "A destination parent is a link or unsupported entry.", exception);
            }
            if (identity is null)
                missing.Add(parent);
            else if (identity.Kind != LinuxAnchoredEntryKind.Directory)
                throw new InstallerTransactionException(TransactionErrorCode.UnsafePath, "A destination parent isn't a real directory.");
        }
        return missing;
    }

    private static void CleanupFinalTransaction(LinuxAnchoredFileSystem transaction)
    {
        foreach (string directoryName in new[] { "staged", "backups" })
        {
            LinuxFileIdentity? directoryIdentity = transaction.Stat(directoryName);
            if (directoryIdentity is null)
                continue;
            if (directoryIdentity.Kind != LinuxAnchoredEntryKind.Directory)
                throw RecoveryError("A finalized transaction payload directory changed type.");
            using LinuxAnchoredFileSystem directory = transaction.OpenSubdirectory(directoryName);
            foreach (string name in directory.EnumerateEntryNames(maximumEntries: TransactionPlan.MaximumOperationCount))
            {
                if (name.Length != 8 || name.Any(character => character is < '0' or > '9'))
                    throw RecoveryError("A finalized transaction contains an unknown payload entry.");
                LinuxFileIdentity identity = directory.Stat(name)
                    ?? throw RecoveryError("A finalized transaction payload entry disappeared.");
                if (identity.Kind != LinuxAnchoredEntryKind.RegularFile)
                    throw RecoveryError("A finalized transaction contains a non-file payload entry.");
                directory.UnlinkFile(name, identity);
            }
            directory.Dispose();
            LinuxFileIdentity emptiedIdentity = transaction.Stat(directoryName)
                ?? throw RecoveryError("A finalized transaction payload directory disappeared before cleanup.");
            transaction.RemoveEmptyDirectory(directoryName, emptiedIdentity);
        }
        HashSet<string> expected = new(StringComparer.Ordinal) { TransactionJournalStore.PlanFileName, TransactionJournalStore.EventsFileName };
        if (transaction.EnumerateEntryNames(maximumEntries: expected.Count).Any(name => !expected.Contains(name)))
            throw RecoveryError("A finalized transaction contains unknown state and was preserved for inspection.");
    }

    private static void CleanupUnpublishedTransaction(
        LinuxAnchoredFileSystem transactions,
        string name,
        LinuxFileIdentity originalIdentity
    )
    {
        using (LinuxAnchoredFileSystem preparation = transactions.OpenSubdirectory(name))
        {
            HashSet<string> known = new(StringComparer.Ordinal)
            {
                "staged",
                "backups",
                TransactionJournalStore.PlanFileName,
                TransactionJournalStore.EventsFileName
            };
            IReadOnlyList<string> entries = preparation.EnumerateEntryNames(maximumEntries: known.Count);
            if (entries.Any(entry => !known.Contains(entry)))
                throw RecoveryError("An unpublished transaction contains unknown state and was preserved.");
            foreach (string directoryName in new[] { "staged", "backups" })
            {
                LinuxFileIdentity? directoryIdentity = preparation.Stat(directoryName);
                if (directoryIdentity is null)
                    continue;
                if (directoryIdentity.Kind != LinuxAnchoredEntryKind.Directory)
                    throw RecoveryError("An unpublished transaction payload directory is unsafe.");
                using (LinuxAnchoredFileSystem directory = preparation.OpenSubdirectory(directoryName))
                {
                    foreach (string fileName in directory.EnumerateEntryNames(maximumEntries: TransactionPlan.MaximumOperationCount))
                    {
                        if (fileName.Length != 8 || fileName.Any(character => character is < '0' or > '9'))
                            throw RecoveryError("An unpublished transaction contains an unknown payload entry.");
                        LinuxFileIdentity fileIdentity = directory.Stat(fileName)
                            ?? throw RecoveryError("An unpublished transaction payload entry disappeared.");
                        if (fileIdentity.Kind != LinuxAnchoredEntryKind.RegularFile)
                            throw RecoveryError("An unpublished transaction payload entry isn't a regular file.");
                        directory.UnlinkFile(fileName, fileIdentity);
                    }
                }
                LinuxFileIdentity emptyDirectory = preparation.Stat(directoryName)
                    ?? throw RecoveryError("An unpublished transaction directory disappeared during cleanup.");
                preparation.RemoveEmptyDirectory(directoryName, emptyDirectory);
            }
            foreach (string fileName in new[] { TransactionJournalStore.PlanFileName, TransactionJournalStore.EventsFileName })
            {
                LinuxFileIdentity? fileIdentity = preparation.Stat(fileName);
                if (fileIdentity is null)
                    continue;
                if (fileIdentity.Kind != LinuxAnchoredEntryKind.RegularFile)
                    throw RecoveryError("An unpublished transaction state file isn't a regular file.");
                preparation.UnlinkFile(fileName, fileIdentity);
            }
            if (preparation.EnumerateEntryNames(maximumEntries: 1).Count != 0)
                throw RecoveryError("An unpublished transaction contains unknown residual state.");
        }
        LinuxFileIdentity currentIdentity = transactions.Stat(name)
            ?? throw RecoveryError("An unpublished transaction disappeared before directory cleanup.");
        if (!currentIdentity.IsSameObject(originalIdentity))
            throw RecoveryError("An unpublished transaction identity changed during cleanup.");
        transactions.RemoveEmptyDirectory(name, currentIdentity);
    }

    private void TrimFinalTransactions(
        LinuxAnchoredFileSystem game,
        LinuxAnchoredFileSystem workspace,
        string canonicalGameRoot
    )
    {
        using LinuxAnchoredFileSystem transactions = workspace.OpenSubdirectory(TransactionsName);
        List<(string Name, long CreatedUtcTicks)> finals = new();
        foreach (string name in EnumerateTransactionStore(transactions))
        {
            LinuxFileIdentity? identity = transactions.Stat(name);
            if (identity?.Kind != LinuxAnchoredEntryKind.Directory || !Guid.TryParseExact(name, "N", out Guid id))
                throw RecoveryError("An unknown transaction entry prevents bounded retention cleanup.");
            using LinuxAnchoredFileSystem transaction = transactions.OpenSubdirectory(name);
            TransactionJournal journal = TransactionJournalStore.ReadPlan(transaction, id, canonicalGameRoot, game.Identity);
            TransactionJournalReplay replay = TransactionJournalStore.ReadEvents(transaction, journal);
            if (replay.Status is not (TransactionJournalEventKind.Committed or TransactionJournalEventKind.RolledBack))
                throw RecoveryError("An incomplete transaction remains after recovery.");
            CleanupFinalTransaction(transaction);
            if (transactions.Stat(name) is null)
                throw RecoveryError("A retained transaction disappeared during cleanup.");
            finals.Add((name, journal.CreatedUtcTicks));
        }

        foreach ((string name, _) in finals
            .OrderBy(item => item.CreatedUtcTicks)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .Take(Math.Max(0, finals.Count - MaximumRetainedFinalTransactions)))
        {
            using (LinuxAnchoredFileSystem transaction = transactions.OpenSubdirectory(name))
            {
                foreach (string fileName in new[] { TransactionJournalStore.PlanFileName, TransactionJournalStore.EventsFileName })
                {
                    LinuxFileIdentity fileIdentity = transaction.Stat(fileName)
                        ?? throw RecoveryError("A retained transaction record disappeared during cleanup.");
                    transaction.UnlinkFile(fileName, fileIdentity);
                }
                if (transaction.EnumerateEntryNames().Count != 0)
                    throw RecoveryError("A retained transaction contains unknown state and wasn't removed.");
            }
            LinuxFileIdentity emptyIdentity = transactions.Stat(name)
                ?? throw RecoveryError("A retained transaction disappeared before directory removal.");
            transactions.RemoveEmptyDirectory(name, emptyIdentity);
        }
    }

    private static IReadOnlyList<string> EnumerateTransactionStore(LinuxAnchoredFileSystem transactions)
    {
        try
        {
            return transactions.EnumerateEntryNames(maximumEntries: MaximumTransactionStoreEntries);
        }
        catch (IOException exception)
        {
            throw new InstallerTransactionException(
                TransactionErrorCode.RecoveryFailed,
                "The transaction store exceeds its bounded entry limit.",
                exception
            );
        }
    }

    private static InstallerTransactionException RecoveryError(string message) => new(TransactionErrorCode.RecoveryFailed, message);
}
