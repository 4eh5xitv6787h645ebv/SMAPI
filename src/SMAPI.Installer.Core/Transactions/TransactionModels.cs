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
    int? ResultUnixMode = null,
    int? ExpectedExistingUnixMode = null
);

/// <summary>A non-mutating full-identity precondition revalidated after staging and before any mutation.</summary>
internal sealed record TransactionFilePrecondition(
    string RelativePath,
    string ExpectedSha256,
    int ExpectedUnixMode
);

/// <summary>An immutable set of ordered operations.</summary>
internal sealed class TransactionPlan
{
    internal const string CoreReceiptRelativePath = ".smapi-installer/ownership/receipt.json";
    internal const string CoreManifestRelativePath = ".smapi-installer/ownership/manifest.json";
    internal const string CoreRecoveryPointerRelativePath = ".smapi-installer/recovery/current.json";
    /// <summary>The maximum bounded operation count accepted by one transaction.</summary>
    public const int MaximumOperationCount = 20_000;
    /// <summary>The unique transaction ID.</summary>
    public Guid TransactionId { get; }

    /// <summary>The stable ordered operations.</summary>
    public IReadOnlyList<TransactionFileOperation> Operations { get; }
    internal IReadOnlyList<TransactionFilePrecondition> Preconditions { get; }

    /// <summary>Whether the core engine authorized an exact receipt mutation inside a complete core-state transition.</summary>
    internal bool HasCoreAuthorizedReceiptMutation => this.CoreAuthorization?.HasReceiptMutation ?? false;
    internal CoreReservedMutationAuthorization? CoreAuthorization { get; }

    /// <summary>Construct an instance.</summary>
    /// <param name="transactionId">The unique transaction ID.</param>
    /// <param name="operations">The ordered operations.</param>
    public TransactionPlan(Guid transactionId, IEnumerable<TransactionFileOperation> operations)
        : this(transactionId, operations, Array.Empty<TransactionFilePrecondition>(), coreAuthorization: null)
    {
    }

    private TransactionPlan(
        Guid transactionId,
        IEnumerable<TransactionFileOperation> operations,
        IEnumerable<TransactionFilePrecondition> preconditions,
        CoreReservedMutationAuthorization? coreAuthorization
    )
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
        TransactionFilePrecondition[] required = preconditions?.ToArray() ?? throw new ArgumentNullException(nameof(preconditions));
        if (required.Length > TransactionPlan.MaximumOperationCount)
            throw new ArgumentException($"A transaction can't exceed {TransactionPlan.MaximumOperationCount} preconditions.", nameof(preconditions));
        this.Preconditions = new ReadOnlyCollection<TransactionFilePrecondition>(required);
        this.CoreAuthorization = coreAuthorization;
    }

    /// <summary>Create the sole fully authorized engine plan, with recovery published first and its pointer last.</summary>
    internal static TransactionPlan CreateWithCoreState(
        Guid transactionId,
        IEnumerable<TransactionFileOperation> recoveryOperations,
        IEnumerable<TransactionFileOperation> ordinaryOperations,
        TransactionFileOperation? manifestOperation,
        TransactionFileOperation? receiptOperation,
        TransactionFileOperation pointerOperation,
        IEnumerable<TransactionFilePrecondition>? preconditions = null
    )
    {
        ArgumentNullException.ThrowIfNull(recoveryOperations);
        ArgumentNullException.ThrowIfNull(ordinaryOperations);
        ArgumentNullException.ThrowIfNull(pointerOperation);
        TransactionFileOperation[] recovery = recoveryOperations.ToArray();
        TransactionFileOperation[] ordinary = ordinaryOperations.ToArray();
        string generationPrefix = $".smapi-installer/recovery/generations/{transactionId:N}/";
        if (recovery.Length == 0 || recovery.Any(operation => !operation.RelativePath.StartsWith(generationPrefix, StringComparison.Ordinal)))
            throw new ArgumentException("Core recovery operations must populate only their exact transaction generation.", nameof(recoveryOperations));
        if (ordinary.Any(operation => operation.RelativePath.StartsWith(".smapi-installer/", StringComparison.Ordinal)))
            throw new ArgumentException("Ordinary operations can't target reserved installer state.", nameof(ordinaryOperations));
        if (manifestOperation is not null && manifestOperation.RelativePath != CoreManifestRelativePath)
            throw new ArgumentException("The core manifest operation targets an unexpected path.", nameof(manifestOperation));
        if (receiptOperation is not null && receiptOperation.RelativePath != CoreReceiptRelativePath)
            throw new ArgumentException("The core receipt operation targets an unexpected path.", nameof(receiptOperation));
        if ((manifestOperation is null) != (receiptOperation is null))
            throw new ArgumentException("The core manifest and receipt must mutate as one ownership tuple.", nameof(manifestOperation));
        if (manifestOperation is not null && manifestOperation.Kind != receiptOperation!.Kind)
            throw new ArgumentException("The core manifest and receipt must use the same mutation kind.", nameof(manifestOperation));
        if (pointerOperation.RelativePath != CoreRecoveryPointerRelativePath || pointerOperation.Kind != TransactionOperationKind.WriteFile)
            throw new ArgumentException("The core recovery pointer must be one exact write operation.", nameof(pointerOperation));

        string[] relativeRecoveryPaths = recovery.Select(operation => operation.RelativePath[generationPrefix.Length..]).ToArray();
        if (relativeRecoveryPaths[0] != "snapshot.json")
            throw new ArgumentException("The core recovery generation must publish its canonical snapshot first.", nameof(recoveryOperations));
        string[] contentNames = relativeRecoveryPaths
            .Where(path => path.StartsWith("files/", StringComparison.Ordinal))
            .Select(path => path["files/".Length..])
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (contentNames.Where((name, index) => name != index.ToString("D8")).Any())
            throw new ArgumentException("Core recovery content indices aren't canonical and contiguous.", nameof(recoveryOperations));
        HashSet<string> fixedNames = new(StringComparer.Ordinal)
        {
            "snapshot.json",
            "previous-receipt.json",
            "previous-manifest.json",
            "previous-pointer.json"
        };
        if (relativeRecoveryPaths.Any(path => !fixedNames.Contains(path) && !path.StartsWith("files/", StringComparison.Ordinal)))
            throw new ArgumentException("The core recovery generation contains an unknown path.", nameof(recoveryOperations));
        string[] expectedOrder = new[] { "snapshot.json", "previous-receipt.json", "previous-manifest.json", "previous-pointer.json" }
            .Where(relativeRecoveryPaths.Contains)
            .Concat(contentNames.Select(name => $"files/{name}"))
            .ToArray();
        if (!relativeRecoveryPaths.SequenceEqual(expectedOrder, StringComparer.Ordinal))
            throw new ArgumentException("Core recovery generation paths aren't in canonical publication order.", nameof(recoveryOperations));
        if (
            relativeRecoveryPaths.Distinct(StringComparer.Ordinal).Count() != relativeRecoveryPaths.Length
            || recovery.Any(operation =>
                operation.Kind != TransactionOperationKind.WriteFile
                || operation.ExpectedExistingSha256 is not null
                || operation.ResultUnixMode != 0x180
            )
        )
        {
            throw new ArgumentException("Core recovery generation paths must be unique absent-file writes with private 0600 modes.", nameof(recoveryOperations));
        }
        if (relativeRecoveryPaths.Contains("previous-receipt.json") != relativeRecoveryPaths.Contains("previous-manifest.json"))
            throw new ArgumentException("Core recovery generations must retain the prior receipt and manifest as one tuple.", nameof(recoveryOperations));
        if (manifestOperation?.Kind == TransactionOperationKind.WriteFile && manifestOperation.ResultUnixMode != 0x180)
            throw new ArgumentException("The core manifest write must use private 0600 permissions.", nameof(manifestOperation));
        if (receiptOperation?.Kind == TransactionOperationKind.WriteFile && receiptOperation.ResultUnixMode != 0x180)
            throw new ArgumentException("The core receipt write must use private 0600 permissions.", nameof(receiptOperation));
        if (pointerOperation.ResultUnixMode != 0x180)
            throw new ArgumentException("The core recovery pointer write must use private 0600 permissions.", nameof(pointerOperation));

        CoreReservedMutationAuthorization authorization = new(
            transactionId,
            recovery.Length,
            contentNames.Length,
            manifestOperation is not null,
            receiptOperation is not null
        );
        IEnumerable<TransactionFileOperation> ordered = recovery
            .Concat(ordinary)
            .Concat(manifestOperation is null ? [] : [manifestOperation])
            .Concat(receiptOperation is null ? [] : [receiptOperation])
            .Append(pointerOperation);
        return new TransactionPlan(
            transactionId,
            ordered,
            preconditions ?? Array.Empty<TransactionFilePrecondition>(),
            coreAuthorization: authorization
        );
    }
}

internal sealed record CoreReservedMutationAuthorization(
    Guid GenerationId,
    int RecoveryOperationCount,
    int RecoveryContentCount,
    bool HasManifestMutation,
    bool HasReceiptMutation
);

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
    Completed,
    Inspecting,
    VerifyingRecovery,
    PreparingRecovery,
    PreparingPayload,
    WritingFiles,
    RemovingFiles,
    UpdatingLauncher,
    UpdatingInstallerState,
    PublishingRecovery,
    CleaningRecovery
}

/// <summary>A bounded transaction progress update. A <see langword="null"/> total denotes indeterminate pre-mutation work.</summary>
public sealed record TransactionProgress(TransactionStage Stage, int CompletedOperations, int? TotalOperations)
{
    /// <summary>Whether an ordinary cancellation request can still stop this operation before mutation.</summary>
    public bool CanCancel => this.Stage is
        TransactionStage.Staging
        or TransactionStage.Revalidating
        or TransactionStage.Inspecting
        or TransactionStage.VerifyingRecovery
        or TransactionStage.PreparingRecovery
        or TransactionStage.PreparingPayload;
}

/// <summary>Receives transaction progress updates.</summary>
public interface ITransactionProgressSink
{
    /// <summary>Report a progress update.</summary>
    /// <remarks>
    /// Implementations must return promptly and must not perform transaction work. Exceptions are isolated by the
    /// installer and never change commit, rollback, or recovery outcomes.
    /// </remarks>
    void Report(TransactionProgress progress);
}

/// <summary>Prevents an untrusted progress observer from affecting installer correctness.</summary>
internal sealed class NonThrowingTransactionProgressSink : ITransactionProgressSink
{
    private readonly ITransactionProgressSink Inner;

    public NonThrowingTransactionProgressSink(ITransactionProgressSink? inner)
    {
        this.Inner = inner ?? NullTransactionInstrumentation.Instance;
    }

    public void Report(TransactionProgress progress)
    {
        try
        {
            this.Inner.Report(progress);
        }
        catch (Exception)
        {
            // Progress is advisory. Observer failures must never alter durable installer state.
        }
    }
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

    /// <summary>Called after filesystem mutation but before its applied journal event is appended.</summary>
    void AfterMutationBeforeAppliedEvent(Guid transactionId, int operationIndex) { }

    /// <summary>Called after the committed event is durable but before best-effort final-store cleanup.</summary>
    void AfterDurableCommit(Guid transactionId) { }
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

    /// <inheritdoc />
    public void AfterMutationBeforeAppliedEvent(Guid transactionId, int operationIndex) { }
}

/// <summary>A test-only signal which represents abrupt process termination and intentionally bypasses in-process rollback.</summary>
public sealed class SimulatedProcessTerminationException : Exception
{
    /// <summary>Construct an instance.</summary>
    public SimulatedProcessTerminationException(string message)
        : base(message) { }
}
