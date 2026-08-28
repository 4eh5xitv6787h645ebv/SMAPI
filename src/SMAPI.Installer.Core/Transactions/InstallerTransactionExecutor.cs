using System.Runtime.ExceptionServices;
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
    internal const int MaximumTransactionStoreEntries = 32;
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
        return this.ApplyLockedCore(lease, payload, plan, cancellationToken, captureOutcome: false).Outcome.Result!;
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
        return this.ApplyLockedCore(lease, payload, plan, cancellationToken, captureOutcome: false).Outcome.Result!;
    }

    internal TransactionExecutionOutcome ApplyLockedWithOutcome(
        InstallerOperationLease lease,
        LinuxAnchoredFileSystem payload,
        TransactionPlan plan,
        GameRootIdentity expectedRoot,
        ulong expectedGeneration,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            LinuxPrivilegeGuard.AssertNotRoot();
            ArgumentNullException.ThrowIfNull(lease);
            ArgumentNullException.ThrowIfNull(payload);
            ArgumentNullException.ThrowIfNull(plan);
            ValidatePlan(plan);
            lease.AssertRootAndGeneration(expectedRoot, expectedGeneration);
            this.Progress.Report(new(TransactionStage.Recovering, 0, plan.Operations.Count));
            IReadOnlyList<TransactionResult> recovered = this.RecoverIncompleteTransactionsLocked(lease.Game, lease.Workspace, lease.CanonicalGameRoot);
            if (recovered.Count > 0)
            {
                lease.ReserveNextGeneration(expectedGeneration);
                return FailureOutcome(plan.TransactionId, TransactionOutcomeStatus.FailedBeforeMutation, TransactionErrorCode.PathChanged);
            }
            lease.AssertRootAndGeneration(expectedRoot, expectedGeneration);
            return this.ApplyLockedCore(lease, payload, plan, cancellationToken, captureOutcome: true).Outcome;
        }
        catch (OperationCanceledException)
        {
            return FailureOutcome(plan.TransactionId, TransactionOutcomeStatus.CancelledBeforeMutation, null, TransactionCancellationDisposition.ObservedBeforeMutation);
        }
        catch (Exception exception)
        {
            return FailureOutcome(plan.TransactionId, TransactionOutcomeStatus.FailedBeforeMutation, GetErrorCode(exception));
        }
    }

    private TransactionExecutionAttempt ApplyLockedCore(
        InstallerOperationLease lease,
        LinuxAnchoredFileSystem payload,
        TransactionPlan plan,
        CancellationToken cancellationToken,
        bool captureOutcome
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
        catch (SimulatedProcessTerminationException exception)
        {
            preparedEventsFile?.Dispose();
            preparedTransaction?.Dispose();
            if (captureOutcome)
                return new(FailureOutcome(plan.TransactionId, TransactionOutcomeStatus.InterruptedRecoveryRequired, TransactionErrorCode.IoFailure), exception);
            throw;
        }
        catch (Exception exception)
        {
            preparedEventsFile?.Dispose();
            preparedTransaction?.Dispose();
            try
            {
                this.RecoverIncompleteTransactionsLocked(game, workspace, canonicalGameRoot);
            }
            catch (Exception recoveryException)
            {
                if (captureOutcome)
                    return new(FailureOutcome(plan.TransactionId, TransactionOutcomeStatus.RollbackFailedRecoveryRequired, GetErrorCode(recoveryException)), recoveryException);
                throw;
            }
            if (captureOutcome)
                return new(FailureOutcome(plan.TransactionId, TransactionOutcomeStatus.FailedBeforeMutation, GetErrorCode(exception)), exception);
            ExceptionDispatchInfo.Capture(exception).Throw();
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
            lease.ReserveNextGeneration(lease.Generation);
            replay = TransactionJournalStore.Append(transaction, eventsFile, journal, replay, TransactionJournalEventKind.Applying);
            replay = this.ApplyMutations(game, transaction, plan, journal, replay, eventsFile);

            this.Progress.Report(new(TransactionStage.Verifying, plan.Operations.Count, plan.Operations.Count));
            VerifyResults(game, plan);
            this.Progress.Report(new(TransactionStage.Committing, plan.Operations.Count, plan.Operations.Count));
            replay = TransactionJournalStore.Append(transaction, eventsFile, journal, replay, TransactionJournalEventKind.Committed);
            committed = true;
        }
        catch (SimulatedProcessTerminationException exception)
        {
            if (captureOutcome)
            {
                replay = TransactionJournalStore.ReadEvents(transaction, journal);
                return new(CreateInterruptedOutcome(game, plan, journal, replay), exception);
            }
            throw;
        }
        catch (Exception exception)
        {
            eventsFile.Dispose();
            replay = TransactionJournalStore.ReadEvents(transaction, journal);
            IReadOnlyList<TransactionPathChange> changed = GetDurablyAppliedOrObservedOperationIndices(game, journal, replay)
                .OrderBy(index => index)
                .Select(index => new TransactionPathChange(plan.Operations[index].RelativePath, plan.Operations[index].Kind))
                .ToArray();
            try
            {
                this.RollBackJournal(game, transaction, journal);
                CleanupFinalTransaction(transaction);
            }
            catch (Exception rollbackException)
            {
                if (captureOutcome)
                {
                    IReadOnlyList<TransactionPathChange> rolledBack;
                    try
                    {
                        TransactionJournalReplay rollbackReplay = TransactionJournalStore.ReadEvents(transaction, journal);
                        rolledBack = GetRolledBackChanges(game, plan, journal, rollbackReplay, changed);
                    }
                    catch
                    {
                        rolledBack = Array.Empty<TransactionPathChange>();
                    }
                    TransactionExecutionOutcome failed = new(
                        plan.TransactionId,
                        TransactionOutcomeStatus.RollbackFailedRecoveryRequired,
                        null,
                        changed,
                        rolledBack,
                        TransactionCancellationDisposition.None,
                        GetErrorCode(rollbackException),
                        SafeMessage(GetErrorCode(rollbackException))
                    );
                    return new(failed, rollbackException);
                }
                throw;
            }
            if (captureOutcome)
            {
                bool cancelled = exception is OperationCanceledException;
                bool changedBeforeCancellation = changed.Count > 0;
                TransactionExecutionOutcome rolledBack = new(
                    plan.TransactionId,
                    cancelled
                        ? changedBeforeCancellation ? TransactionOutcomeStatus.CancelledAndRolledBack : TransactionOutcomeStatus.CancelledBeforeMutation
                        : TransactionOutcomeStatus.FailedAndRolledBack,
                    TransactionStatus.RolledBack,
                    changed,
                    changed,
                    cancelled
                        ? changedBeforeCancellation ? TransactionCancellationDisposition.ObservedAfterMutationAndRolledBack : TransactionCancellationDisposition.ObservedBeforeMutation
                        : TransactionCancellationDisposition.None,
                    cancelled ? null : GetErrorCode(exception),
                    cancelled
                        ? changedBeforeCancellation ? "Cancellation was observed after mutation began and every changed path was rolled back." : "The operation was cancelled before game-file mutation."
                        : SafeMessage(GetErrorCode(exception))
                );
                return new(rolledBack, exception);
            }
            ExceptionDispatchInfo.Capture(exception).Throw();
            throw;
        }

        if (!committed)
            throw new InstallerTransactionException(TransactionErrorCode.IoFailure, "The transaction ended without a durable terminal event.");
        Exception? cleanupWarning = null;
        try
        {
            this.FaultInjector.AfterDurableCommit(plan.TransactionId);
            CleanupFinalTransaction(transaction);
            this.TrimFinalTransactions(game, workspace, canonicalGameRoot);
        }
        catch (Exception exception)
        {
            cleanupWarning = exception;
        }
        this.Progress.Report(new(TransactionStage.Completed, plan.Operations.Count, plan.Operations.Count));
        IReadOnlyList<TransactionPathChange> committedChanges = plan.Operations
            .Select(operation => new TransactionPathChange(operation.RelativePath, operation.Kind))
            .ToArray();
        TransactionExecutionOutcome outcome = new(
            plan.TransactionId,
            cleanupWarning is null ? TransactionOutcomeStatus.Committed : TransactionOutcomeStatus.CommittedWithCleanupWarning,
            TransactionStatus.Committed,
            committedChanges,
            Array.Empty<TransactionPathChange>(),
            cancellationToken.IsCancellationRequested
                ? TransactionCancellationDisposition.RequestedAfterMutationStartedAndCommitted
                : TransactionCancellationDisposition.None,
            cleanupWarning is null ? null : GetErrorCode(cleanupWarning),
            cleanupWarning is null ? null : "The operation committed, but obsolete installer recovery data could not be cleaned up."
        );
        return new(outcome, cleanupWarning);
    }

    private static TransactionExecutionOutcome CreateInterruptedOutcome(
        LinuxAnchoredFileSystem game,
        TransactionPlan plan,
        TransactionJournal journal,
        TransactionJournalReplay replay
    )
    {
        IReadOnlyList<TransactionPathChange> changed = GetDurablyAppliedOrObservedOperationIndices(game, journal, replay)
            .OrderBy(index => index)
            .Select(index => new TransactionPathChange(plan.Operations[index].RelativePath, plan.Operations[index].Kind))
            .ToArray();
        return new(
            plan.TransactionId,
            TransactionOutcomeStatus.InterruptedRecoveryRequired,
            null,
            changed,
            Array.Empty<TransactionPathChange>(),
            TransactionCancellationDisposition.None,
            TransactionErrorCode.IoFailure,
            "The operation was interrupted and requires recovery before another installation operation."
        );
    }

    private static IReadOnlyList<TransactionPathChange> GetRolledBackChanges(
        LinuxAnchoredFileSystem game,
        TransactionPlan plan,
        TransactionJournal journal,
        TransactionJournalReplay replay,
        IReadOnlyList<TransactionPathChange> changed
    )
    {
        HashSet<string> changedPaths = changed.Select(item => item.RelativePath).ToHashSet(StringComparer.Ordinal);
        return GetDurablyRolledBackOrObservedOperationIndices(game, journal, replay)
            .OrderBy(index => index)
            .Select(index => new TransactionPathChange(plan.Operations[index].RelativePath, plan.Operations[index].Kind))
            .Where(item => changedPaths.Contains(item.RelativePath))
            .ToArray();
    }

    private static TransactionExecutionOutcome FailureOutcome(
        Guid transactionId,
        TransactionOutcomeStatus status,
        TransactionErrorCode? code,
        TransactionCancellationDisposition cancellation = TransactionCancellationDisposition.None
    )
        => new(transactionId, status, null, Array.Empty<TransactionPathChange>(), Array.Empty<TransactionPathChange>(), cancellation, code, SafeMessage(code));

    internal static TransactionErrorCode GetErrorCode(Exception exception)
        => exception switch
        {
            InstallerTransactionException transaction => transaction.Code,
            TransactionRecoveryAttemptException recovery when recovery.InnerException is not null => GetErrorCode(recovery.InnerException),
            AggregateException aggregate when aggregate.InnerExceptions.Count > 0 => GetErrorCode(aggregate.InnerExceptions[^1]),
            _ => TransactionErrorCode.IoFailure
        };

    internal static string? SafeMessage(TransactionErrorCode? code)
        => code switch
        {
            null => null,
            TransactionErrorCode.InvalidPlan => "The installer operation is invalid and must be inspected again.",
            TransactionErrorCode.UnsafePath => "An installer path is unsafe.",
            TransactionErrorCode.PathChanged or TransactionErrorCode.ExistingFileMismatch => "The game installation changed and must be inspected again.",
            TransactionErrorCode.PayloadMismatch => "The verified package content changed or failed validation.",
            TransactionErrorCode.ConcurrentOperation => "Another installer operation is active.",
            TransactionErrorCode.WorkspaceConflict => "The installer workspace needs attention before continuing.",
            TransactionErrorCode.RecoveryFailed => "Automatic recovery could not safely finish.",
            _ => "The installer operation failed because of an input/output error."
        };

    /// <summary>Recover every incomplete transaction under a game root.</summary>
    public IReadOnlyList<TransactionResult> RecoverIncompleteTransactions(string gameRoot)
    {
        LinuxPrivilegeGuard.AssertNotRoot();
        string canonicalGameRoot = TransactionPath.GetCanonicalRoot(gameRoot, nameof(gameRoot));
        using InstallerOperationLease lease = InstallerOperationLease.Acquire(canonicalGameRoot);
        LinuxAnchoredFileSystem game = lease.Game;
        LinuxAnchoredFileSystem workspace = lease.Workspace;
        ulong recoveryStartGeneration = lease.Generation;
        IReadOnlyList<TransactionResult> results;
        try
        {
            results = this.RecoverIncompleteTransactionsLocked(game, workspace, canonicalGameRoot);
        }
        catch (TransactionRecoveryAttemptException exception)
        {
            this.InvalidateFailedRecoveryGeneration(lease, recoveryStartGeneration, exception);
            ExceptionDispatchInfo.Capture(exception.InnerException ?? exception).Throw();
            throw;
        }
        catch (Exception exception)
        {
            TransactionRecoveryAttemptException recoveryFailure = new(Array.Empty<TransactionResult>(), exception);
            this.InvalidateFailedRecoveryGeneration(lease, recoveryStartGeneration, recoveryFailure);
            ExceptionDispatchInfo.Capture(exception).Throw();
            throw;
        }
        try
        {
            if (results.Count > 0)
                lease.ReserveNextGeneration(lease.Generation);
            this.TrimFinalTransactions(game, workspace, canonicalGameRoot);
            return results;
        }
        catch (Exception exception)
        {
            TransactionRecoveryAttemptException recoveryFailure = new(results, exception);
            this.InvalidateFailedRecoveryGeneration(lease, recoveryStartGeneration, recoveryFailure);
            ExceptionDispatchInfo.Capture(exception).Throw();
            throw;
        }
    }

    /// <summary>Recover under an already-held operation lease and invalidate its prior generation when needed.</summary>
    internal IReadOnlyList<TransactionResult> RecoverLocked(InstallerOperationLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ulong recoveryStartGeneration = lease.Generation;
        IReadOnlyList<TransactionResult> results;
        try
        {
            results = this.RecoverIncompleteTransactionsLocked(
                lease.Game,
                lease.Workspace,
                lease.CanonicalGameRoot
            );
        }
        catch (TransactionRecoveryAttemptException exception)
        {
            this.InvalidateFailedRecoveryGeneration(lease, recoveryStartGeneration, exception);
            throw;
        }
        catch (Exception exception)
        {
            TransactionRecoveryAttemptException recoveryFailure = new(Array.Empty<TransactionResult>(), exception);
            this.InvalidateFailedRecoveryGeneration(lease, recoveryStartGeneration, recoveryFailure);
            throw recoveryFailure;
        }
        try
        {
            if (results.Count > 0)
                lease.ReserveNextGeneration(lease.Generation);
            this.TrimFinalTransactions(lease.Game, lease.Workspace, lease.CanonicalGameRoot);
            return results;
        }
        catch (Exception exception)
        {
            TransactionRecoveryAttemptException recoveryFailure = new(results, exception);
            this.InvalidateFailedRecoveryGeneration(lease, recoveryStartGeneration, recoveryFailure);
            throw recoveryFailure;
        }
    }

    private void InvalidateFailedRecoveryGeneration(
        InstallerOperationLease lease,
        ulong recoveryStartGeneration,
        TransactionRecoveryAttemptException recoveryFailure
    )
    {
        if (lease.Generation != recoveryStartGeneration)
            return;
        try
        {
            lease.ReserveNextGeneration(recoveryStartGeneration);
        }
        catch (Exception generationFailure)
        {
            throw new TransactionRecoveryAttemptException(
                recoveryFailure.RecoveredTransactions,
                new AggregateException(recoveryFailure, generationFailure)
            );
        }
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
            TransactionFileOperation operation = plan.Operations[index];
            this.Progress.Report(new(GetMutationStage(operation), index, plan.Operations.Count));
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

            this.FaultInjector.AfterMutationBeforeAppliedEvent(plan.TransactionId, index);
            replay = TransactionJournalStore.Append(transaction, eventsFile, journal, replay, TransactionJournalEventKind.Applied, index);
            this.FaultInjector.AfterMutation(plan.TransactionId, index);
        }
        return replay;
    }

    private static TransactionStage GetMutationStage(TransactionFileOperation operation)
    {
        if (operation.RelativePath == "StardewValley" || operation.RelativePath == "StardewValley-original")
            return TransactionStage.UpdatingLauncher;
        if (operation.RelativePath == TransactionPlan.CoreRecoveryPointerRelativePath)
            return TransactionStage.PublishingRecovery;
        if (operation.RelativePath is TransactionPlan.CoreManifestRelativePath or TransactionPlan.CoreReceiptRelativePath)
            return TransactionStage.UpdatingInstallerState;
        return operation.Kind == TransactionOperationKind.RemoveFile
            ? TransactionStage.RemovingFiles
            : TransactionStage.WritingFiles;
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
        IReadOnlyList<string> names = EnumerateTransactionStore(transactions)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        foreach (string name in names)
            this.PreflightRecoveryStoreEntry(game, transactions, canonicalGameRoot, name);

        List<TransactionResult> results = new();
        try
        {
            foreach (string name in names)
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
                this.FaultInjector.BeforeRecoveringTransaction(transactionId);
                using LinuxAnchoredFileSystem transaction = transactions.OpenSubdirectory(name);
                TransactionJournal journal = TransactionJournalStore.ReadPlan(transaction, transactionId, canonicalGameRoot, game.Identity);
                TransactionJournalReplay replay = TransactionJournalStore.ReadEvents(transaction, journal);
                PreflightUnpublishedTransaction(transaction);
                if (replay.Status is TransactionJournalEventKind.Committed or TransactionJournalEventKind.RolledBack)
                {
                    CleanupFinalTransaction(transaction);
                    continue;
                }
                PreflightRollbackJournal(game, journal, replay);
                this.Progress.Report(new(TransactionStage.Recovering, 0, journal.Entries.Count));
                int changed = GetDurablyAppliedOrObservedOperationIndices(game, journal, replay).Count;
                this.RollBackJournal(game, transaction, journal);
                results.Add(new(transactionId, TransactionStatus.Recovered, changed));
                this.FaultInjector.AfterRecoveryRollbackBeforeCleanup(transactionId);
                CleanupFinalTransaction(transaction);
            }
            return results;
        }
        catch (TransactionRecoveryAttemptException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new TransactionRecoveryAttemptException(results, exception);
        }
    }

    private void PreflightRecoveryStoreEntry(
        LinuxAnchoredFileSystem game,
        LinuxAnchoredFileSystem transactions,
        string canonicalGameRoot,
        string name
    )
    {
        LinuxFileIdentity? identity = transactions.Stat(name);
        if (name.StartsWith("preparing-", StringComparison.Ordinal))
        {
            if (
                identity?.Kind != LinuxAnchoredEntryKind.Directory
                || !Guid.TryParseExact(name["preparing-".Length..], "N", out _)
            )
                throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "An unsafe preparation workspace requires manual inspection.");
            using LinuxAnchoredFileSystem preparation = transactions.OpenSubdirectory(name);
            PreflightUnpublishedTransaction(preparation);
            return;
        }
        if (identity?.Kind != LinuxAnchoredEntryKind.Directory || !Guid.TryParseExact(name, "N", out Guid transactionId))
            throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "An unsafe or unrecognized transaction workspace requires manual inspection.");
        using LinuxAnchoredFileSystem transaction = transactions.OpenSubdirectory(name);
        TransactionJournal journal = TransactionJournalStore.ReadPlan(transaction, transactionId, canonicalGameRoot, game.Identity);
        TransactionJournalReplay replay = TransactionJournalStore.ReadEvents(transaction, journal);
        PreflightUnpublishedTransaction(transaction);
        if (replay.Status is not (TransactionJournalEventKind.Committed or TransactionJournalEventKind.RolledBack))
            PreflightRollbackJournal(game, journal, replay);
    }

    private static void PreflightRollbackJournal(
        LinuxAnchoredFileSystem game,
        TransactionJournal journal,
        TransactionJournalReplay replay
    )
    {
        string transactionPrefix = $"{WorkspaceName}/{TransactionsName}/{journal.TransactionId:N}/";
        foreach (int index in replay.IntendedOperations.OrderByDescending(value => value))
        {
            if (replay.RolledBackOperations.Contains(index))
                continue;
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
                    ValidateFile(game, backupPath, entry.ExpectedExistingSha256!, TransactionErrorCode.RecoveryFailed);
                    if (current is not null)
                    {
                        if (entry.Kind != TransactionOperationKind.WriteFile || entry.ExpectedResultSha256 is null)
                            throw RecoveryError("An unexpected file blocks exact rollback.");
                        ValidateFile(game, entry.RelativePath, entry.ExpectedResultSha256, TransactionErrorCode.RecoveryFailed);
                    }
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
                    ValidateFile(game, entry.RelativePath, entry.ExpectedResultSha256, TransactionErrorCode.RecoveryFailed);
                }
            }
            foreach (string directory in entry.CreatedDirectories)
            {
                LinuxFileIdentity? directoryIdentity = game.Stat(directory);
                if (directoryIdentity is not null && directoryIdentity.Kind != LinuxAnchoredEntryKind.Directory)
                    throw RecoveryError("A transaction-created parent changed type during rollback.");
            }
        }
    }

    private static HashSet<int> GetDurablyAppliedOrObservedOperationIndices(
        LinuxAnchoredFileSystem game,
        TransactionJournal journal,
        TransactionJournalReplay replay
    )
    {
        HashSet<int> changed = new(replay.AppliedOperations);
        string transactionPrefix = $"{WorkspaceName}/{TransactionsName}/{journal.TransactionId:N}/";
        foreach (int index in replay.IntendedOperations)
        {
            if (changed.Contains(index))
                continue;
            TransactionJournalEntry entry = journal.Entries[index];
            if (entry.HadOriginal)
            {
                string backupPath = transactionPrefix + entry.BackupRelativePath;
                if (game.Stat(backupPath) is not null)
                {
                    ValidateFile(game, backupPath, entry.ExpectedExistingSha256!, TransactionErrorCode.RecoveryFailed);
                    changed.Add(index);
                }
            }
            else if (entry.Kind == TransactionOperationKind.WriteFile && entry.ExpectedResultSha256 is not null && game.Stat(entry.RelativePath) is not null)
            {
                ValidateFile(game, entry.RelativePath, entry.ExpectedResultSha256, TransactionErrorCode.RecoveryFailed);
                changed.Add(index);
            }
        }
        return changed;
    }

    private static HashSet<int> GetDurablyRolledBackOrObservedOperationIndices(
        LinuxAnchoredFileSystem game,
        TransactionJournal journal,
        TransactionJournalReplay replay
    )
    {
        HashSet<int> rolledBack = new(replay.RolledBackOperations);
        string transactionPrefix = $"{WorkspaceName}/{TransactionsName}/{journal.TransactionId:N}/";
        foreach (int index in replay.RollbackIntendedOperations)
        {
            if (rolledBack.Contains(index))
                continue;
            TransactionJournalEntry entry = journal.Entries[index];
            string backupPath = transactionPrefix + entry.BackupRelativePath;
            if (game.Stat(backupPath) is not null)
                continue;
            LinuxFileIdentity? current = game.Stat(entry.RelativePath);
            if (
                entry.HadOriginal
                    ? current is not null
                        && entry.ExpectedExistingSha256 is not null
                        && IsExactFile(game, entry.RelativePath, entry.ExpectedExistingSha256)
                    : current is null
            )
            {
                rolledBack.Add(index);
            }
        }
        return rolledBack;
    }

    private static bool IsExactFile(
        LinuxAnchoredFileSystem game,
        string relativePath,
        string expectedSha256
    )
    {
        try
        {
            ValidateFile(game, relativePath, expectedSha256, TransactionErrorCode.RecoveryFailed);
            return true;
        }
        catch (Exception exception) when (exception is InstallerTransactionException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void PreflightUnpublishedTransaction(LinuxAnchoredFileSystem preparation)
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
            using LinuxAnchoredFileSystem directory = preparation.OpenSubdirectory(directoryName);
            foreach (string fileName in directory.EnumerateEntryNames(maximumEntries: TransactionPlan.MaximumOperationCount))
            {
                if (fileName.Length != 8 || fileName.Any(character => character is < '0' or > '9'))
                    throw RecoveryError("An unpublished transaction contains an unknown payload entry.");
                LinuxFileIdentity fileIdentity = directory.Stat(fileName)
                    ?? throw RecoveryError("An unpublished transaction payload entry disappeared.");
                if (fileIdentity.Kind != LinuxAnchoredEntryKind.RegularFile)
                    throw RecoveryError("An unpublished transaction payload entry isn't a regular file.");
            }
        }
        foreach (string fileName in new[] { TransactionJournalStore.PlanFileName, TransactionJournalStore.EventsFileName })
        {
            LinuxFileIdentity? fileIdentity = preparation.Stat(fileName);
            if (fileIdentity is not null && fileIdentity.Kind != LinuxAnchoredEntryKind.RegularFile)
                throw RecoveryError("An unpublished transaction state file isn't a regular file.");
        }
    }

    private void RollBackJournal(LinuxAnchoredFileSystem game, LinuxAnchoredFileSystem transaction, TransactionJournal journal)
    {
        TransactionJournalReplay replay = TransactionJournalStore.ReadEvents(transaction, journal);
        if (replay.Status == TransactionJournalEventKind.RolledBack)
            return;
        if (replay.Status == TransactionJournalEventKind.Committed)
            throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "A committed transaction can't be rolled back by crash recovery.");
        using LinuxAnchoredFile eventsFile = TransactionJournalStore.OpenEventsForAppend(transaction, replay);
        if (replay.Status is not (TransactionJournalEventKind.RollingBack or TransactionJournalEventKind.RollbackIntent or TransactionJournalEventKind.RollbackApplied))
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
            if (!replay.RollbackIntendedOperations.Contains(index))
                replay = TransactionJournalStore.Append(transaction, eventsFile, journal, replay, TransactionJournalEventKind.RollbackIntent, index);
            bool rollbackMutated = false;

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
                        rollbackMutated = true;
                    }
                    game.RenameFileNoReplace(backupPath, entry.RelativePath, verifiedBackup);
                    rollbackMutated = true;
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
                    rollbackMutated = true;
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
                {
                    game.RemoveEmptyDirectory(directory, directoryIdentity);
                    rollbackMutated = true;
                }
            }
            if (rollbackMutated)
                this.FaultInjector.AfterRollbackMutationBeforeAppliedEvent(journal.TransactionId, index);
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
