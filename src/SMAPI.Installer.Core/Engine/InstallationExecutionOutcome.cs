using System.Collections.ObjectModel;
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
public sealed record InstallationExecutionOutcome
{
    public InstallationAction Action { get; }
    public InstallationExecutionStatus Status { get; }
    public TransactionExecutionOutcome? Transaction { get; }
    public IReadOnlyList<TransactionResult> RecoveredTransactions { get; }
    public TransactionErrorCode? ErrorCode { get; }
    public string? SafeMessage { get; }
    public string? SanitizedLogPath { get; }
    public IReadOnlyList<TransactionPathChange> ChangedPaths { get; }
    public IReadOnlyList<TransactionPathChange> ManagedGamePathChanges { get; }
    public IReadOnlyList<TransactionPathChange> InternalStateChanges { get; }

    public InstallationExecutionOutcome(InstallationAction action, InstallationExecutionStatus status, TransactionExecutionOutcome? transaction, IReadOnlyList<TransactionResult> recoveredTransactions, TransactionErrorCode? errorCode, string? safeMessage, string? sanitizedLogPath = null)
    {
        ArgumentNullException.ThrowIfNull(recoveredTransactions);
        TransactionResult[] recovered = recoveredTransactions.ToArray();
        if (recovered.Any(item => item is null))
            throw new ArgumentException("Installation outcome recovery collections can't contain null entries.", nameof(recoveredTransactions));
        TransactionPathChange[] changed = transaction?.ChangedPaths.ToArray() ?? [];
        this.Action = action;
        this.Status = status;
        this.Transaction = transaction;
        this.RecoveredTransactions = new ReadOnlyCollection<TransactionResult>(recovered);
        this.ErrorCode = errorCode;
        this.SafeMessage = safeMessage;
        this.SanitizedLogPath = sanitizedLogPath;
        this.ChangedPaths = new ReadOnlyCollection<TransactionPathChange>(changed);
        this.ManagedGamePathChanges = new ReadOnlyCollection<TransactionPathChange>(changed.Where(change => !change.RelativePath.StartsWith(".smapi-installer/", StringComparison.Ordinal)).ToArray());
        this.InternalStateChanges = new ReadOnlyCollection<TransactionPathChange>(changed.Where(change => change.RelativePath.StartsWith(".smapi-installer/", StringComparison.Ordinal)).ToArray());
    }

    public bool RequiresFreshInspection => this.Status is not (InstallationExecutionStatus.Succeeded or InstallationExecutionStatus.SucceededWithCleanupWarning);
    public bool RequiresRecovery => this.Transaction?.RequiresRecovery ?? false;
}
