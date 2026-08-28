using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Transactions;

namespace StardewModdingAPI.Installer.Core.Engine;

/// <summary>The host-facing status of one exact inspected installation operation.</summary>
public enum InstallationExecutionStatus
{
    Succeeded,
    SucceededWithCleanupWarning,
    FailedBeforeMutation,
    CancelledBeforeMutation,
    CancelledAndRolledBack,
    FailedAndRolledBack,
    InterruptedRecoveryRequired,
    AutomaticRecoveryCompletedFreshInspectionRequired
}

/// <summary>A complete structured execution result which never requires an exception message to explain durable state.</summary>
public sealed record InstallationExecutionOutcome(
    InstallationAction Action,
    InstallationExecutionStatus Status,
    TransactionExecutionOutcome? Transaction,
    IReadOnlyList<TransactionResult> RecoveredTransactions,
    TransactionErrorCode? ErrorCode,
    string? SafeMessage,
    string? SanitizedLogPath = null
)
{
    public IReadOnlyList<TransactionPathChange> ChangedPaths => this.Transaction?.ChangedPaths ?? Array.Empty<TransactionPathChange>();
    public IReadOnlyList<TransactionPathChange> ManagedGamePathChanges => this.ChangedPaths
        .Where(change => !change.RelativePath.StartsWith(".smapi-installer/", StringComparison.Ordinal))
        .ToArray();
    public IReadOnlyList<TransactionPathChange> InternalStateChanges => this.ChangedPaths
        .Where(change => change.RelativePath.StartsWith(".smapi-installer/", StringComparison.Ordinal))
        .ToArray();
    public bool RequiresFreshInspection => this.Status is not (InstallationExecutionStatus.Succeeded or InstallationExecutionStatus.SucceededWithCleanupWarning);
    public bool RequiresRecovery => this.Transaction?.RequiresRecovery ?? false;
}
