using System.Collections.ObjectModel;

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
public sealed record TransactionExecutionOutcome
{
    public Guid TransactionId { get; }
    public TransactionOutcomeStatus Status { get; }
    public TransactionStatus? DurableStatus { get; }
    public IReadOnlyList<TransactionPathChange> ChangedPaths { get; }
    public IReadOnlyList<TransactionPathChange> RolledBackPaths { get; }
    public TransactionCancellationDisposition Cancellation { get; }
    public TransactionErrorCode? ErrorCode { get; }
    public string? SafeMessage { get; }

    public TransactionExecutionOutcome(Guid transactionId, TransactionOutcomeStatus status, TransactionStatus? durableStatus, IReadOnlyList<TransactionPathChange> changedPaths, IReadOnlyList<TransactionPathChange> rolledBackPaths, TransactionCancellationDisposition cancellation, TransactionErrorCode? errorCode, string? safeMessage)
    {
        ArgumentNullException.ThrowIfNull(changedPaths);
        ArgumentNullException.ThrowIfNull(rolledBackPaths);
        TransactionPathChange[] changed = changedPaths.ToArray();
        TransactionPathChange[] rolledBack = rolledBackPaths.ToArray();
        if (changed.Any(item => item is null) || rolledBack.Any(item => item is null))
            throw new ArgumentException("Transaction outcome path collections can't contain null entries.");
        this.TransactionId = transactionId;
        this.Status = status;
        this.DurableStatus = durableStatus;
        this.ChangedPaths = new ReadOnlyCollection<TransactionPathChange>(changed);
        this.RolledBackPaths = new ReadOnlyCollection<TransactionPathChange>(rolledBack);
        this.Cancellation = cancellation;
        this.ErrorCode = errorCode;
        this.SafeMessage = safeMessage;
    }

    public bool RequiresRecovery => this.Status is TransactionOutcomeStatus.InterruptedRecoveryRequired or TransactionOutcomeStatus.RollbackFailedRecoveryRequired;
    public TransactionResult? Result => this.Status is TransactionOutcomeStatus.Committed or TransactionOutcomeStatus.CommittedWithCleanupWarning
        ? new(this.TransactionId, TransactionStatus.Committed, this.ChangedPaths.Count)
        : null;
}

internal sealed record TransactionExecutionAttempt(TransactionExecutionOutcome Outcome, Exception? Failure);
