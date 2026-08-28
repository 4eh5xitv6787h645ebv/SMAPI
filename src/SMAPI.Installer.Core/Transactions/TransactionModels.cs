using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("SMAPI.Installer.Core.Tests")]

namespace StardewModdingAPI.Installer.Core.Transactions;

/// <summary>The type of a transaction file operation.</summary>
public enum TransactionOperationKind
{
    /// <summary>Create or replace a regular file from the staged payload.</summary>
    WriteFile,

    /// <summary>Remove an existing regular file.</summary>
    RemoveFile
}

/// <summary>A single immutable file operation.</summary>
/// <param name="Kind">The operation kind.</param>
/// <param name="RelativePath">The normalized path relative to the canonical game directory.</param>
/// <param name="ExpectedExistingSha256">The expected current SHA-256, or <see langword="null"/> when the path must be absent.</param>
/// <param name="PayloadRelativePath">The normalized path relative to the payload root for a write.</param>
/// <param name="ExpectedResultSha256">The expected payload/result SHA-256 for a write.</param>
/// <param name="ResultUnixMode">The Unix permission bits (for example <c>0755</c>) to set on a written file, if any.</param>
internal sealed record TransactionFileOperation(
    TransactionOperationKind Kind,
    string RelativePath,
    string? ExpectedExistingSha256,
    string? PayloadRelativePath = null,
    string? ExpectedResultSha256 = null,
    int? ResultUnixMode = null
);

/// <summary>An immutable set of ordered operations.</summary>
internal sealed class TransactionPlan
{
    internal const string CoreReceiptRelativePath = ".smapi-installer/ownership/receipt.json";
    /// <summary>The maximum bounded operation count accepted by one transaction.</summary>
    public const int MaximumOperationCount = 20_000;
    /// <summary>The unique transaction ID.</summary>
    public Guid TransactionId { get; }

    /// <summary>The stable ordered operations.</summary>
    public IReadOnlyList<TransactionFileOperation> Operations { get; }

    /// <summary>Whether the core engine, rather than an external caller, appended the reserved receipt mutation.</summary>
    internal bool HasCoreAuthorizedReceiptMutation { get; }

    /// <summary>Construct an instance.</summary>
    /// <param name="transactionId">The unique transaction ID.</param>
    /// <param name="operations">The ordered operations.</param>
    public TransactionPlan(Guid transactionId, IEnumerable<TransactionFileOperation> operations)
        : this(transactionId, operations, hasCoreAuthorizedReceiptMutation: false)
    {
    }

    private TransactionPlan(Guid transactionId, IEnumerable<TransactionFileOperation> operations, bool hasCoreAuthorizedReceiptMutation)
    {
        if (transactionId == Guid.Empty)
            throw new ArgumentException("A transaction ID is required.", nameof(transactionId));

        TransactionFileOperation[] array = operations?.ToArray() ?? throw new ArgumentNullException(nameof(operations));
        if (array.Length == 0)
            throw new ArgumentException("A transaction must contain at least one operation.", nameof(operations));
        if (array.Length > TransactionPlan.MaximumOperationCount)
            throw new ArgumentException($"A transaction can't exceed {TransactionPlan.MaximumOperationCount} operations.", nameof(operations));

        this.TransactionId = transactionId;
        this.Operations = new ReadOnlyCollection<TransactionFileOperation>(array);
        this.HasCoreAuthorizedReceiptMutation = hasCoreAuthorizedReceiptMutation;
    }

    /// <summary>Create a plan whose one reserved receipt mutation was produced by the typed core engine.</summary>
    internal static TransactionPlan CreateWithCoreReceipt(
        Guid transactionId,
        IEnumerable<TransactionFileOperation> ordinaryOperations,
        TransactionFileOperation receiptOperation
    )
    {
        ArgumentNullException.ThrowIfNull(ordinaryOperations);
        ArgumentNullException.ThrowIfNull(receiptOperation);
        TransactionFileOperation[] ordinary = ordinaryOperations.ToArray();
        if (ordinary.Any(operation => operation.RelativePath == CoreReceiptRelativePath))
            throw new ArgumentException("The ordinary operation set can't contain the reserved receipt path.", nameof(ordinaryOperations));
        if (receiptOperation.RelativePath != CoreReceiptRelativePath)
            throw new ArgumentException("The authorized receipt operation must target the exact reserved receipt path.", nameof(receiptOperation));
        return new TransactionPlan(transactionId, ordinary.Append(receiptOperation), hasCoreAuthorizedReceiptMutation: true);
    }
}

/// <summary>The result of applying or recovering a transaction.</summary>
/// <param name="TransactionId">The transaction ID.</param>
/// <param name="Status">The final status.</param>
/// <param name="ChangedPathCount">The number of operations durably applied before commit or rollback.</param>
public sealed record TransactionResult(Guid TransactionId, TransactionStatus Status, int ChangedPathCount);

/// <summary>A final transaction status.</summary>
public enum TransactionStatus
{
    /// <summary>The transaction committed and post-verification passed.</summary>
    Committed,

    /// <summary>The transaction was restored to its original state.</summary>
    RolledBack,

    /// <summary>An interrupted transaction was discovered and restored.</summary>
    Recovered
}

/// <summary>Stable transaction error codes suitable for frontends and logs.</summary>
public enum TransactionErrorCode
{
    InvalidPlan,
    UnsafePath,
    PathChanged,
    ExistingFileMismatch,
    PayloadMismatch,
    ConcurrentOperation,
    WorkspaceConflict,
    RecoveryFailed,
    IoFailure
}

/// <summary>An expected, user-actionable transaction failure.</summary>
public sealed class InstallerTransactionException : Exception
{
    /// <summary>The stable error code.</summary>
    public TransactionErrorCode Code { get; }

    /// <summary>Construct an instance.</summary>
    public InstallerTransactionException(TransactionErrorCode code, string message)
        : base(message)
    {
        this.Code = code;
    }

    /// <summary>Construct an instance.</summary>
    public InstallerTransactionException(TransactionErrorCode code, string message, Exception innerException)
        : base(message, innerException)
    {
        this.Code = code;
    }
}

/// <summary>A progress stage emitted by the executor.</summary>
public enum TransactionStage
{
    AcquiringLock,
    Recovering,
    Staging,
    Revalidating,
    Applying,
    Verifying,
    Committing,
    RollingBack,
    Completed
}

/// <summary>A bounded transaction progress update.</summary>
public sealed record TransactionProgress(TransactionStage Stage, int CompletedOperations, int TotalOperations);

/// <summary>Receives transaction progress updates.</summary>
public interface ITransactionProgressSink
{
    /// <summary>Report a progress update.</summary>
    void Report(TransactionProgress progress);
}

/// <summary>Provides deterministic fault-injection boundaries for recovery testing.</summary>
public interface ITransactionFaultInjector
{
    /// <summary>Called at a durable transaction-setup boundary before any game-file mutation is possible.</summary>
    void AtSetupBoundary(Guid transactionId, TransactionSetupBoundary boundary) { }

    /// <summary>Called after an operation intent is durable and before its first mutation.</summary>
    void BeforeMutation(Guid transactionId, int operationIndex);

    /// <summary>Called after an operation mutation is durable.</summary>
    void AfterMutation(Guid transactionId, int operationIndex);
}

/// <summary>Durable transaction setup boundaries exposed for deterministic crash testing.</summary>
public enum TransactionSetupBoundary
{
    PreparationDirectoryCreated,
    PayloadDirectoriesCreated,
    ImmutablePlanCreated,
    CreationEventCreated,
    TransactionPublished
}

/// <summary>No-op transaction instrumentation.</summary>
public sealed class NullTransactionInstrumentation : ITransactionProgressSink, ITransactionFaultInjector
{
    /// <summary>A shared instance.</summary>
    public static NullTransactionInstrumentation Instance { get; } = new();

    private NullTransactionInstrumentation() { }

    /// <inheritdoc />
    public void Report(TransactionProgress progress) { }

    /// <inheritdoc />
    public void AtSetupBoundary(Guid transactionId, TransactionSetupBoundary boundary) { }

    /// <inheritdoc />
    public void BeforeMutation(Guid transactionId, int operationIndex) { }

    /// <inheritdoc />
    public void AfterMutation(Guid transactionId, int operationIndex) { }
}

/// <summary>A test-only signal which represents abrupt process termination and intentionally bypasses in-process rollback.</summary>
public sealed class SimulatedProcessTerminationException : Exception
{
    /// <summary>Construct an instance.</summary>
    public SimulatedProcessTerminationException(string message)
        : base(message) { }
}
