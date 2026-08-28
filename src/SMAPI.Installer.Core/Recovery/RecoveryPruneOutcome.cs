using System.Collections.ObjectModel;
using StardewModdingAPI.Installer.Core.Transactions;

namespace StardewModdingAPI.Installer.Core.Recovery;

/// <summary>The bounded result of applying an authenticated recovery-retention decision.</summary>
public enum RecoveryPruneOutcomeStatus
{
    Succeeded,
    FailedBeforePublication,
    CancelledBeforePublication,
    Interrupted,
    CancelledWithCleanupPending,
    FailedWithCleanupPending
}

/// <summary>A truthful result captured at recovery-retention publication and physical-cleanup boundaries.</summary>
public sealed record RecoveryPruneOutcome
{
    public RecoveryPruneOutcomeStatus Status { get; }
    public IReadOnlyList<Guid> LogicallyRemovedGenerationIds { get; }
    public IReadOnlyList<Guid> PhysicallyCleanedGenerationIds { get; }
    public IReadOnlyList<Guid> PendingCleanupGenerationIds { get; }
    public bool AuxiliaryCleanupPending { get; }
    public TransactionErrorCode? ErrorCode { get; }
    public string? SafeMessage { get; }

    public RecoveryPruneOutcome(RecoveryPruneOutcomeStatus status, IReadOnlyList<Guid> logicallyRemovedGenerationIds, IReadOnlyList<Guid> physicallyCleanedGenerationIds, IReadOnlyList<Guid> pendingCleanupGenerationIds, bool auxiliaryCleanupPending, TransactionErrorCode? errorCode, string? safeMessage)
    {
        ArgumentNullException.ThrowIfNull(logicallyRemovedGenerationIds);
        ArgumentNullException.ThrowIfNull(physicallyCleanedGenerationIds);
        ArgumentNullException.ThrowIfNull(pendingCleanupGenerationIds);
        this.Status = status;
        this.LogicallyRemovedGenerationIds = new ReadOnlyCollection<Guid>(logicallyRemovedGenerationIds.ToArray());
        this.PhysicallyCleanedGenerationIds = new ReadOnlyCollection<Guid>(physicallyCleanedGenerationIds.ToArray());
        this.PendingCleanupGenerationIds = new ReadOnlyCollection<Guid>(pendingCleanupGenerationIds.ToArray());
        this.AuxiliaryCleanupPending = auxiliaryCleanupPending;
        this.ErrorCode = errorCode;
        this.SafeMessage = safeMessage;
    }

    public bool LogicalStatePublished => this.LogicallyRemovedGenerationIds.Count > 0;
    public bool RequiresCleanup => this.PendingCleanupGenerationIds.Count > 0 || this.AuxiliaryCleanupPending;
}

internal sealed record RecoveryPruneAttempt(RecoveryPruneOutcome Outcome, Exception? Failure);
