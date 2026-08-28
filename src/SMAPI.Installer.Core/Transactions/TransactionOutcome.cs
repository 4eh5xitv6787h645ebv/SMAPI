namespace StardewModdingAPI.Installer.Core.Transactions;

/// <summary>The durable outcome of one transaction attempt.</summary>
public enum TransactionOutcomeStatus
{
    Committed,
    CommittedWithCleanupWarning,
    FailedBeforeMutation,
    CancelledBeforeMutation,
    CancelledAndRolledBack,
    FailedAndRolledBack,
    InterruptedRecoveryRequired,
    RollbackFailedRecoveryRequired
}

/// <summary>How cancellation affected a transaction attempt.</summary>
public enum TransactionCancellationDisposition
{
    None,
    ObservedBeforeMutation,
    ObservedAfterMutationAndRolledBack,
    RequestedAfterMutationStartedAndCommitted
}

/// <summary>One exact installer-owned path which was durably mutated during an attempt.</summary>
public sealed record TransactionPathChange(string RelativePath, TransactionOperationKind Kind);

/// <summary>A complete bounded result captured at the durable transaction boundary.</summary>
public sealed record TransactionExecutionOutcome(
    Guid TransactionId,
    TransactionOutcomeStatus Status,
    TransactionStatus? DurableStatus,
    IReadOnlyList<TransactionPathChange> ChangedPaths,
    IReadOnlyList<TransactionPathChange> RolledBackPaths,
    TransactionCancellationDisposition Cancellation,
    TransactionErrorCode? ErrorCode,
    string? SafeMessage
)
{
    /// <summary>Whether crash recovery must run before a new installation operation.</summary>
    public bool RequiresRecovery => this.Status is
        TransactionOutcomeStatus.InterruptedRecoveryRequired
        or TransactionOutcomeStatus.RollbackFailedRecoveryRequired;

    /// <summary>The legacy successful result, if this attempt committed.</summary>
    public TransactionResult? Result => this.Status is TransactionOutcomeStatus.Committed or TransactionOutcomeStatus.CommittedWithCleanupWarning
        ? new(this.TransactionId, TransactionStatus.Committed, this.ChangedPaths.Count)
        : null;
}

internal sealed record TransactionExecutionAttempt(TransactionExecutionOutcome Outcome, Exception? Failure);
