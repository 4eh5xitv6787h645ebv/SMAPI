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
public sealed record RecoveryPruneOutcome(
    RecoveryPruneOutcomeStatus Status,
    IReadOnlyList<Guid> LogicallyRemovedGenerationIds,
    IReadOnlyList<Guid> PhysicallyCleanedGenerationIds,
    IReadOnlyList<Guid> PendingCleanupGenerationIds,
    bool AuxiliaryCleanupPending,
    TransactionErrorCode? ErrorCode,
    string? SafeMessage
)
{
    public bool LogicalStatePublished => this.LogicallyRemovedGenerationIds.Count > 0;
    public bool RequiresCleanup => this.PendingCleanupGenerationIds.Count > 0 || this.AuxiliaryCleanupPending;
}

internal sealed record RecoveryPruneAttempt(RecoveryPruneOutcome Outcome, Exception? Failure);
