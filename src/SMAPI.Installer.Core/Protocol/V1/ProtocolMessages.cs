using System.Text.Json.Serialization;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Transactions;

namespace StardewModdingAPI.Installer.Core.Protocol.V1;

/// <summary>The exact message variants in the unshipped version 1 protocol.</summary>
public enum ProtocolMessageKind
{
    HandshakeRequest,
    DiscoverGamesRequest,
    ValidateGameRequest,
    RecoverInterruptedRequest,
    OpenPackageRequest,
    ListRecoveriesRequest,
    InspectPlanRequest,
    SelectPlanCandidatesRequest,
    GetPlanPageRequest,
    ConfirmPlanRequest,
    ExecutePlanRequest,
    CancelPlanRequest,
    InspectPruneRequest,
    ConfirmPruneRequest,
    ExecutePruneRequest,
    CancelPruneRequest,
    CommandAcknowledgedEvent,
    HandshakeEvent,
    GameDiscoveryEvent,
    GameValidationEvent,
    RecoveryProgressEvent,
    RecoveryCompletedEvent,
    RecoveryFailureEvent,
    PackageOpenedEvent,
    RecoveryCatalogEvent,
    NoRecoveryHistoryEvent,
    PlanEvent,
    PlanPageEvent,
    PrunePlanEvent,
    ProgressEvent,
    PruneProgressEvent,
    SuccessEvent,
    RolledBackFailureEvent,
    RecoverableInterruptionEvent,
    CancelledEvent,
    PruneSuccessEvent,
    PruneFailureEvent,
    PruneInterruptionEvent,
    PruneCancelledEvent,
    PrePlanRejectedEvent
}

public enum InstallerOperation
{
    Install,
    Update,
    Repair,
    Uninstall,
    Backup,
    Rollback
}

public enum ObservedInstallState
{
    NotInstalled,
    KnownUnmodified,
    KnownModified,
    LegacyOrOfficial,
    Unknown
}

public enum ProtocolDurableState
{
    Committed,
    Unchanged,
    RolledBack,
    RecoveryRequired,
    RecoveryCompleted,
    Unknown,
    PruneApplied
}

public enum ProtocolRecoveryDisposition
{
    NotRequired,
    CleanupPending,
    Completed,
    InterruptedRecoveryRequired,
    StateRefreshRequired
}

/// <summary>Every exact transaction error the core can report, plus a conservative protocol-only fallback.</summary>
public enum ProtocolTerminalErrorCode
{
    InvalidPlan,
    UnsafePath,
    PathChanged,
    ExistingFileMismatch,
    PayloadMismatch,
    ConcurrentOperation,
    WorkspaceConflict,
    RecoveryFailed,
    DiskFull,
    ReadOnlyFileSystem,
    PermissionDenied,
    CrossDeviceBoundary,
    IoFailure,
    UnexpectedCoreFailure
}

public enum ProtocolExecutionOutcome
{
    Succeeded,
    SucceededWithCleanupWarning,
    FailedBeforeMutation,
    CancelledBeforeMutation,
    CancelledAndRolledBack,
    FailedAndRolledBack,
    InterruptedRecoveryRequired,
    AutomaticRecoveryCompletedFreshInspectionRequired,
    UnexpectedCoreFailure
}

public enum ProtocolPruneOutcome
{
    Succeeded,
    FailedBeforePublication,
    CancelledBeforePublication,
    Interrupted,
    CancelledWithCleanupPending,
    FailedWithCleanupPending,
    UnexpectedCoreFailure
}

public enum ProtocolInterruptedRecoveryOutcome
{
    RecoveryCompleted,
    CancelledBeforeRecovery,
    PartialFailure,
    UnexpectedFailure
}

public sealed record ProtocolTerminalState(
    ProtocolDurableState DurableState,
    ProtocolTerminalErrorCode? ErrorCode,
    ProtocolRecoveryDisposition RecoveryDisposition,
    ProtocolNextAction NextAction
);

public sealed record ProtocolExecutionSummary(
    int? ManagedFileChangeCount,
    int? RolledBackManagedFileCount,
    int? InternalStateChangeCount,
    int? RolledBackInternalStateCount,
    int? RecoveredTransactionCount,
    int? RecoveredPathCount
);

public sealed record ProtocolPruneSummary(
    int? LogicallyRemovedGenerationCount,
    int? PhysicallyCleanedGenerationCount,
    int? PendingCleanupGenerationCount,
    bool? AuxiliaryCleanupPending
);

/// <summary>A closed user action which is safe after a rejected pre-plan command.</summary>
public enum ProtocolNextAction
{
    RetryRequest,
    SelectGameFolder,
    ReopenVerifiedPackage,
    InspectAgain,
    ListRecoveries,
    RecoverInterrupted,
    StartNewSession,
    ReviewFilesystem,
    ViewPrivateLog
}

/// <summary>A stable class of failure before a mutating operation began.</summary>
public enum ProtocolPrePlanErrorCode
{
    RequestCancelled,
    InvalidGameFolder,
    PackageRejected,
    RecoveryUnavailable,
    InspectionFailed,
    CandidateApprovalFailed,
    PermissionDenied,
    InputOutputFailure,
    UnexpectedFailure
}

public enum ProtocolAcknowledgementKind
{
    PlanConfirmed,
    PlanCancellationRequested,
    PlanCancelledBeforeExecution,
    PrunePlanConfirmed,
    PruneCancellationRequested,
    PruneCancelledBeforeExecution
}

public enum ProtocolPlanPageKind
{
    Operations,
    Conflicts,
    Candidates,
    Warnings
}

public enum ProtocolPlanRisk
{
    Uninstall,
    Rollback,
    Downgrade,
    ModifiedOrUnknownFileApproval,
    RecoveryPrune
}

public enum ProtocolRecommendedDefault
{
    Cancel
}

public abstract record ProtocolMessage
{
    /// <summary>The canonical command which owns this request, sole response, progress, or terminal.</summary>
    [JsonPropertyOrder(-100)]
    public ProtocolCommandId CommandId { get; init; } = ProtocolCommandId.CreateRandom();

    [JsonIgnore]
    public abstract ProtocolMessageKind Kind { get; }
}

public abstract record ProtocolRequest : ProtocolMessage;

public abstract record ProtocolEvent : ProtocolMessage;

public sealed record HandshakeRequest(string ClientName, string ClientVersion) : ProtocolRequest
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.HandshakeRequest;
}

public sealed record DiscoverGamesRequest(ProtocolSessionId SessionId) : ProtocolRequest
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.DiscoverGamesRequest;
}

/// <summary>Ask the backend to validate one manually selected Linux game folder without changing it.</summary>
public sealed record ValidateGameRequest(ProtocolSessionId SessionId, string GamePath) : ProtocolRequest
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.ValidateGameRequest;
}

/// <summary>Recover bounded interrupted installer work before creating any fresh inspection.</summary>
public sealed record RecoverInterruptedRequest(ProtocolSessionId SessionId, string GamePath) : ProtocolRequest
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.RecoverInterruptedRequest;
}

/// <summary>The asserted kernel identity of a nonforgeable retained parent-process workspace.</summary>
public sealed record ProtocolProcWorkspaceIdentity(
    uint DeviceMajor,
    uint DeviceMinor,
    ulong Inode,
    long ChangeSeconds,
    uint ChangeNanoseconds
);

/// <summary>Ask the backend to independently verify one complete local release asset set.</summary>
public sealed record OpenPackageRequest(
    ProtocolSessionId SessionId,
    string ReleaseTag,
    string ExpectedSourceCommit,
    string PackagePath,
    string ChecksumsPath,
    string BuildMetadataPath,
    string InstallManifestPath,
    string AttestationBundlePath,
    string AttestationBundleChecksumPath,
    ProtocolProcWorkspaceIdentity? ProcWorkspaceIdentity = null
) : ProtocolRequest
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.OpenPackageRequest;
}

public sealed record ListRecoveriesRequest(ProtocolSessionId SessionId, string GamePath) : ProtocolRequest
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.ListRecoveriesRequest;
}

/// <summary>Inspect a game path using only session-local package and recovery selections.</summary>
public sealed record InspectPlanRequest(
    ProtocolSessionId SessionId,
    string GamePath,
    InstallerOperation Operation,
    ProtocolPackageId? PackageId,
    ProtocolRecoverySelectionId? RecoverySelectionId
) : ProtocolRequest
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.InspectPlanRequest;
}

public sealed record SelectPlanCandidatesRequest(
    ProtocolSessionId SessionId,
    ProtocolPlanId PlanId,
    ProtocolPlanDigest PlanDigest,
    ProtocolCandidateId[] SelectedCandidateIds
) : ProtocolRequest
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.SelectPlanCandidatesRequest;
}

public sealed record GetPlanPageRequest(
    ProtocolSessionId SessionId,
    ProtocolPlanId PlanId,
    ProtocolPlanDigest PlanDigest,
    ProtocolPlanPageKind PageKind,
    int Offset
) : ProtocolRequest
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.GetPlanPageRequest;
}

public sealed record ConfirmPlanRequest(ProtocolSessionId SessionId, ProtocolPlanId PlanId, ProtocolPlanDigest PlanDigest) : ProtocolRequest
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.ConfirmPlanRequest;
}

public sealed record ExecutePlanRequest(ProtocolSessionId SessionId, ProtocolPlanId PlanId, ProtocolPlanDigest PlanDigest) : ProtocolRequest
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.ExecutePlanRequest;
}

public sealed record CancelPlanRequest(ProtocolSessionId SessionId, ProtocolPlanId PlanId, ProtocolPlanDigest PlanDigest) : ProtocolRequest
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.CancelPlanRequest;
}

public sealed record InspectPruneRequest(ProtocolSessionId SessionId, ProtocolRecoveryCatalogId CatalogId, int RetainNewest) : ProtocolRequest
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.InspectPruneRequest;
}

public sealed record ConfirmPruneRequest(ProtocolSessionId SessionId, ProtocolPrunePlanId PrunePlanId, ProtocolPlanDigest PruneDigest) : ProtocolRequest
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.ConfirmPruneRequest;
}

public sealed record ExecutePruneRequest(ProtocolSessionId SessionId, ProtocolPrunePlanId PrunePlanId, ProtocolPlanDigest PruneDigest) : ProtocolRequest
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.ExecutePruneRequest;
}

public sealed record CancelPruneRequest(ProtocolSessionId SessionId, ProtocolPrunePlanId PrunePlanId, ProtocolPlanDigest PruneDigest) : ProtocolRequest
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.CancelPruneRequest;
}

public sealed record CommandAcknowledgedEvent(
    ProtocolSessionId SessionId,
    ProtocolAcknowledgementKind Acknowledgement,
    ProtocolPlanId? PlanId,
    ProtocolPrunePlanId? PrunePlanId
) : ProtocolEvent
{
    [JsonIgnore] public bool RequiresRecovery => false;
    [JsonIgnore] public bool RequiresFreshInspection => true;
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.CommandAcknowledgedEvent;
}

public sealed record HandshakeEvent : ProtocolEvent
{
    private readonly string[] CapabilityValues;
    public ProtocolSessionId SessionId { get; }
    public string ServerVersion { get; }
    public string[] Capabilities => this.CapabilityValues.ToArray();

    [JsonConstructor]
    public HandshakeEvent(ProtocolSessionId sessionId, string serverVersion, string[] capabilities)
    {
        this.SessionId = sessionId;
        this.ServerVersion = serverVersion;
        this.CapabilityValues = capabilities?.ToArray() ?? throw new ProtocolException("The protocol 'capabilities' collection can't be null.");
    }

    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.HandshakeEvent;
}

public sealed record ProtocolGameCandidate(string CanonicalPath, LinuxGameFolderStatus State, string DisplayName);

public sealed record GameDiscoveryEvent : ProtocolEvent
{
    private readonly ProtocolGameCandidate[] CandidateValues;
    public ProtocolSessionId SessionId { get; }
    public ProtocolGameCandidate[] Candidates => this.CandidateValues.ToArray();

    [JsonConstructor]
    public GameDiscoveryEvent(ProtocolSessionId sessionId, ProtocolGameCandidate[] candidates)
    {
        this.SessionId = sessionId;
        this.CandidateValues = candidates?.ToArray() ?? throw new ProtocolException("The protocol 'candidates' collection can't be null.");
    }

    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.GameDiscoveryEvent;
}

/// <summary>The exact bounded validation result for one manually selected Linux game folder.</summary>
public sealed record GameValidationEvent(ProtocolSessionId SessionId, ProtocolGameCandidate Candidate) : ProtocolEvent
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.GameValidationEvent;
}

public sealed record RecoveryProgressEvent(
    ProtocolSessionId SessionId,
    long Sequence,
    TransactionStage Stage,
    int CompletedUnits,
    int? TotalUnits,
    string Message
) : ProtocolEvent
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.RecoveryProgressEvent;
}

public sealed record RecoveryCompletedEvent(
    ProtocolSessionId SessionId,
    ProtocolInterruptedRecoveryOutcome Outcome,
    ProtocolTerminalState TerminalState,
    ProtocolInterruptedRecoveryAttempt Attempt,
    string Summary,
    string? SanitizedLogPath
) : ProtocolEvent
{
    [JsonIgnore] public bool RequiresRecovery => false;
    [JsonIgnore] public bool RequiresFreshInspection => true;
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.RecoveryCompletedEvent;
}

public sealed record ProtocolRecoveredTransactionResult(string TransactionId, int ChangedPathCount);

/// <summary>Exact bounded progress preserved when interrupted-operation recovery only partially completes.</summary>
public sealed record ProtocolInterruptedRecoveryAttempt
{
    private readonly ProtocolRecoveredTransactionResult[] RecoveredTransactionValues;
    public ProtocolGameRootIdentity GameRoot { get; }
    public ulong PreviousOperationGeneration { get; }
    public ulong? CurrentOperationGeneration { get; }
    public bool? NamedRootStillSelected { get; }
    public ProtocolRecoveredTransactionResult[] RecoveredTransactions => this.RecoveredTransactionValues.ToArray();
    [JsonIgnore] public bool? OperationGenerationAdvanced => this.CurrentOperationGeneration is { } current ? current > this.PreviousOperationGeneration : null;
    [JsonIgnore] public bool? NamedRootSelectionChanged => this.NamedRootStillSelected is { } selected ? !selected : null;
    [JsonIgnore] public int RecoveredTransactionCount => this.RecoveredTransactionValues.Length;
    [JsonIgnore] public int RecoveredPathCount => this.RecoveredTransactionValues.Sum(value => value.ChangedPathCount);

    [JsonConstructor]
    public ProtocolInterruptedRecoveryAttempt(ProtocolGameRootIdentity gameRoot, ulong previousOperationGeneration, ulong? currentOperationGeneration, bool? namedRootStillSelected, ProtocolRecoveredTransactionResult[] recoveredTransactions)
    {
        this.GameRoot = gameRoot;
        this.PreviousOperationGeneration = previousOperationGeneration;
        this.CurrentOperationGeneration = currentOperationGeneration;
        this.NamedRootStillSelected = namedRootStillSelected;
        this.RecoveredTransactionValues = recoveredTransactions?.ToArray() ?? throw new ProtocolException("Interrupted-recovery transaction results can't be null.");
    }
}

public sealed record RecoveryFailureEvent(
    ProtocolSessionId SessionId,
    ProtocolInterruptedRecoveryOutcome Outcome,
    ProtocolTerminalState TerminalState,
    string Message,
    string? SanitizedLogPath,
    ProtocolInterruptedRecoveryAttempt? Attempt = null
) : ProtocolEvent
{
    [JsonIgnore] public bool RequiresRecovery => this.TerminalState.RecoveryDisposition == ProtocolRecoveryDisposition.InterruptedRecoveryRequired;
    [JsonIgnore] public bool RequiresFreshInspection => true;
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.RecoveryFailureEvent;
}

public sealed record ProtocolReleaseIdentity(
    string Repository,
    string Tag,
    string EmbeddedVersion,
    string PackageAssetName,
    string SourceCommit,
    string SourceTree,
    string PackageSha256,
    long PackageSizeBytes,
    string BuildWorkflow,
    string BuildConfiguration,
    string RuntimeIdentifier
);

public sealed record PackageOpenedEvent(ProtocolSessionId SessionId, ProtocolPackageId PackageId, ProtocolReleaseIdentity Release) : ProtocolEvent
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.PackageOpenedEvent;
}

public sealed record ProtocolGameRootIdentity(
    string CanonicalPath,
    uint DeviceMajor,
    uint DeviceMinor,
    ulong Inode,
    ulong OperationGeneration
);

public sealed record ProtocolRecoveryGeneration(
    ProtocolRecoverySelectionId SelectionId,
    string GenerationId,
    InstallerOperation OriginOperation,
    bool IsCurrent,
    bool IsUserCheckpoint,
    ProtocolReleaseIdentity? RestoreRelease,
    bool RestoresUninstalledState
)
{
    internal ProtocolRecoveryGeneration(ProtocolRecoverySelectionId selectionId, string generationId, InstallerOperation originOperation, bool isCurrent, bool isUserCheckpoint)
        : this(selectionId, generationId, originOperation, isCurrent, isUserCheckpoint, null, true) { }
}

/// <summary>Host-side source metadata used to mint opaque recovery selections.</summary>
public sealed record ProtocolRecoveryGenerationSource(
    string GenerationId,
    InstallerOperation OriginOperation,
    bool IsCurrent,
    bool IsUserCheckpoint,
    ProtocolReleaseIdentity? RestoreRelease,
    bool RestoresUninstalledState
);

/// <summary>The exact authenticated catalog, game root, head, and generation selected for rollback.</summary>
public sealed record ProtocolRecoveryAuthority(
    ProtocolRecoveryCatalogId CatalogId,
    ProtocolRecoverySelectionId SelectionId,
    ProtocolGameRootIdentity GameRoot,
    string HeadSha256,
    ProtocolRecoveryGeneration Generation
);

public sealed record RecoveryCatalogEvent : ProtocolEvent
{
    private readonly ProtocolRecoveryGeneration[] GenerationValues;

    public ProtocolSessionId SessionId { get; }
    public ProtocolRecoveryCatalogId CatalogId { get; }
    public ProtocolGameRootIdentity GameRoot { get; }
    public string HeadSha256 { get; }
    public ProtocolRecoveryGeneration[] Generations => this.GenerationValues.ToArray();

    [JsonConstructor]
    public RecoveryCatalogEvent(
        ProtocolSessionId sessionId,
        ProtocolRecoveryCatalogId catalogId,
        ProtocolGameRootIdentity gameRoot,
        string headSha256,
        ProtocolRecoveryGeneration[] generations
    )
    {
        this.SessionId = sessionId;
        this.CatalogId = catalogId;
        this.GameRoot = gameRoot;
        this.HeadSha256 = headSha256;
        this.GenerationValues = generations?.ToArray()
            ?? throw new ProtocolException("The protocol 'generations' collection can't be null.");
    }

    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.RecoveryCatalogEvent;
}

/// <summary>
/// A correlated nonterminal result reporting that bounded anchored inspection observed no committed recovery pointer.
/// This deliberately carries no empty catalog, game-root identity, digest, or selection authority.
/// </summary>
public sealed record NoRecoveryHistoryEvent(ProtocolSessionId SessionId) : ProtocolEvent
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.NoRecoveryHistoryEvent;
}

public sealed record ProtocolPlanOperation(PlanOperationKind Kind, string Path, string? ExpectedCurrentSha256, string? ResultSha256);

public sealed record ProtocolPlanConflict(PlanConflictCode Code, string? Path);

public sealed record ProtocolPlanCandidate(
    ProtocolCandidateId CandidateId,
    FileReplacementCandidateReason Reason,
    FileReplacementCandidateDisposition Disposition,
    string Path,
    string ObservedSha256,
    long ObservedSizeBytes,
    int ObservedUnixMode,
    string? ProposedResultSha256,
    bool Selected,
    string Evidence
);

/// <summary>Core-observed candidate data from which the protocol mints an opaque selection ID.</summary>
public sealed record ProtocolPlanCandidateSource(
    FileReplacementCandidateReason Reason,
    FileReplacementCandidateDisposition Disposition,
    string Path,
    string ObservedSha256,
    long ObservedSizeBytes,
    int ObservedUnixMode,
    string? ProposedResultSha256,
    bool Selected,
    string Evidence
);

public sealed record PlanEvent : ProtocolEvent
{
    private readonly ProtocolPlanRisk[] RiskValues;
    private ProtocolPlanOperation[] LegacyOperationValues = [];
    private ProtocolPlanConflict[] LegacyConflictValues = [];
    private ProtocolPlanCandidate[] LegacyCandidateValues = [];
    private string[] LegacyWarningValues = [];

    public ProtocolSessionId SessionId { get; }
    public ProtocolPlanId PlanId { get; }
    public ProtocolPlanDigest PlanDigest { get; }
    public ProtocolPlanDigest ExecutionBindingDigest { get; }
    public InstallerOperation Operation { get; }
    public ProtocolPackageId? PackageId { get; }
    public ProtocolRecoveryAuthority? RecoveryAuthority { get; }
    public ProtocolGameRootIdentity GameRoot { get; }
    public ProtocolReleaseIdentity? CurrentRelease { get; }
    public ProtocolReleaseIdentity? TargetRelease { get; }
    public ObservedInstallState ObservedState { get; }
    public int OperationCount { get; }
    public int ConflictCount { get; }
    public int CandidateCount { get; }
    public int WarningCount { get; }
    public bool CanExecute { get; }
    public ProtocolPlanRisk[] Risks => this.RiskValues.ToArray();
    public ProtocolRecommendedDefault RecommendedDefault { get; }
    public string Summary { get; }
    public bool RequiresConfirmation { get; }
    [JsonIgnore] internal ProtocolPlanOperation[] Operations => this.LegacyOperationValues.ToArray();
    [JsonIgnore] internal ProtocolPlanConflict[] Conflicts => this.LegacyConflictValues.ToArray();
    [JsonIgnore] internal ProtocolPlanCandidate[] Candidates => this.LegacyCandidateValues.ToArray();
    [JsonIgnore] internal string[] Warnings => this.LegacyWarningValues.ToArray();

    [JsonConstructor]
    public PlanEvent(
        ProtocolSessionId sessionId,
        ProtocolPlanId planId,
        ProtocolPlanDigest planDigest,
        ProtocolPlanDigest executionBindingDigest,
        InstallerOperation operation,
        ProtocolPackageId? packageId,
        ProtocolRecoveryAuthority? recoveryAuthority,
        ProtocolGameRootIdentity gameRoot,
        ProtocolReleaseIdentity? currentRelease,
        ProtocolReleaseIdentity? targetRelease,
        ObservedInstallState observedState,
        int operationCount,
        int conflictCount,
        int candidateCount,
        int warningCount,
        bool canExecute,
        ProtocolPlanRisk[] risks,
        ProtocolRecommendedDefault recommendedDefault,
        string summary,
        bool requiresConfirmation
    )
    {
        this.SessionId = sessionId;
        this.PlanId = planId;
        this.PlanDigest = planDigest;
        this.ExecutionBindingDigest = executionBindingDigest;
        this.Operation = operation;
        this.PackageId = packageId;
        this.RecoveryAuthority = recoveryAuthority;
        this.GameRoot = gameRoot;
        this.CurrentRelease = currentRelease;
        this.TargetRelease = targetRelease;
        this.ObservedState = observedState;
        this.OperationCount = operationCount;
        this.ConflictCount = conflictCount;
        this.CandidateCount = candidateCount;
        this.WarningCount = warningCount;
        this.CanExecute = canExecute;
        this.RiskValues = risks?.ToArray() ?? throw new ProtocolException("The protocol 'risks' collection can't be null.");
        this.RecommendedDefault = recommendedDefault;
        this.Summary = summary;
        this.RequiresConfirmation = requiresConfirmation;
    }

    internal PlanEvent(ProtocolSessionId sessionId, ProtocolPlanId planId, ProtocolPlanDigest planDigest, ProtocolPlanDigest executionBindingDigest, InstallerOperation operation, ProtocolPackageId? packageId, ProtocolRecoveryAuthority? recoveryAuthority, ProtocolGameRootIdentity gameRoot, ProtocolReleaseIdentity? currentRelease, ProtocolReleaseIdentity? targetRelease, ObservedInstallState observedState, ProtocolPlanOperation[] operations, ProtocolPlanConflict[] conflicts, ProtocolPlanCandidate[] candidates, string summary, string[] warnings, bool requiresConfirmation)
        : this(sessionId, planId, planDigest, executionBindingDigest, operation, packageId, recoveryAuthority, gameRoot, currentRelease, targetRelease, observedState, operations.Length, conflicts.Length, candidates.Length, warnings.Length, conflicts.Length == 0, GetCompatibilityRisks(operation, candidates), ProtocolRecommendedDefault.Cancel, summary, requiresConfirmation)
    {
        this.LegacyOperationValues = operations.ToArray();
        this.LegacyConflictValues = conflicts.ToArray();
        this.LegacyCandidateValues = candidates.ToArray();
        this.LegacyWarningValues = warnings.ToArray();
    }

    private static ProtocolPlanRisk[] GetCompatibilityRisks(InstallerOperation operation, ProtocolPlanCandidate[] candidates)
    {
        List<ProtocolPlanRisk> risks = [];
        if (operation == InstallerOperation.Uninstall) risks.Add(ProtocolPlanRisk.Uninstall);
        if (operation == InstallerOperation.Rollback) risks.Add(ProtocolPlanRisk.Rollback);
        if (candidates.Length > 0) risks.Add(ProtocolPlanRisk.ModifiedOrUnknownFileApproval);
        return risks.ToArray();
    }

    internal PlanEvent AttachPageData(ProtocolPlanOperation[] operations, ProtocolPlanConflict[] conflicts, ProtocolPlanCandidate[] candidates, string[] warnings)
    {
        this.LegacyOperationValues = operations.ToArray(); this.LegacyConflictValues = conflicts.ToArray(); this.LegacyCandidateValues = candidates.ToArray(); this.LegacyWarningValues = warnings.ToArray(); return this;
    }

    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.PlanEvent;
}

/// <summary>One bounded deterministic page of an exact digest-bound plan presentation.</summary>
public sealed record PlanPageEvent : ProtocolEvent
{
    private readonly ProtocolPlanOperation[] OperationValues;
    private readonly ProtocolPlanConflict[] ConflictValues;
    private readonly ProtocolPlanCandidate[] CandidateValues;
    private readonly string[] WarningValues;

    public ProtocolSessionId SessionId { get; }
    public ProtocolPlanId PlanId { get; }
    public ProtocolPlanDigest PlanDigest { get; }
    public ProtocolPlanPageKind PageKind { get; }
    public int Offset { get; }
    public int TotalCount { get; }
    public int? NextOffset { get; }
    public ProtocolPlanOperation[] Operations => this.OperationValues.ToArray();
    public ProtocolPlanConflict[] Conflicts => this.ConflictValues.ToArray();
    public ProtocolPlanCandidate[] Candidates => this.CandidateValues.ToArray();
    public string[] Warnings => this.WarningValues.ToArray();

    [JsonConstructor]
    public PlanPageEvent(ProtocolSessionId sessionId, ProtocolPlanId planId, ProtocolPlanDigest planDigest, ProtocolPlanPageKind pageKind, int offset, int totalCount, int? nextOffset, ProtocolPlanOperation[] operations, ProtocolPlanConflict[] conflicts, ProtocolPlanCandidate[] candidates, string[] warnings)
    {
        this.SessionId = sessionId;
        this.PlanId = planId;
        this.PlanDigest = planDigest;
        this.PageKind = pageKind;
        this.Offset = offset;
        this.TotalCount = totalCount;
        this.NextOffset = nextOffset;
        this.OperationValues = operations?.ToArray() ?? throw new ProtocolException("The protocol 'operations' page can't be null.");
        this.ConflictValues = conflicts?.ToArray() ?? throw new ProtocolException("The protocol 'conflicts' page can't be null.");
        this.CandidateValues = candidates?.ToArray() ?? throw new ProtocolException("The protocol 'candidates' page can't be null.");
        this.WarningValues = warnings?.ToArray() ?? throw new ProtocolException("The protocol 'warnings' page can't be null.");
    }

    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.PlanPageEvent;
}

public sealed record PrunePlanEvent : ProtocolEvent
{
    private readonly ProtocolRecoverySelectionId[] RetainedValues;
    private readonly ProtocolRecoverySelectionId[] RemovedValues;
    private readonly string[] CleanupGenerationValues;
    private readonly string[] WarningValues;
    private readonly ProtocolPlanRisk[] RiskValues;

    public ProtocolSessionId SessionId { get; }
    public ProtocolPrunePlanId PrunePlanId { get; }
    public ProtocolPlanDigest PruneDigest { get; }
    public ProtocolPlanDigest ExecutionBindingDigest { get; }
    public ProtocolRecoveryCatalogId CatalogId { get; }
    public ProtocolGameRootIdentity GameRoot { get; }
    public string HeadSha256 { get; }
    public int RetainNewest { get; }
    public ProtocolRecoverySelectionId[] RetainedSelectionIds => this.RetainedValues.ToArray();
    public ProtocolRecoverySelectionId[] RemovedSelectionIds => this.RemovedValues.ToArray();
    public string[] CleanupGenerationIds => this.CleanupGenerationValues.ToArray();
    public string Summary { get; }
    public string[] Warnings => this.WarningValues.ToArray();
    public ProtocolPlanRisk[] Risks => this.RiskValues.ToArray();
    public ProtocolRecommendedDefault RecommendedDefault { get; }
    public bool RequiresConfirmation { get; }

    [JsonConstructor]
    public PrunePlanEvent(
        ProtocolSessionId sessionId,
        ProtocolPrunePlanId prunePlanId,
        ProtocolPlanDigest pruneDigest,
        ProtocolPlanDigest executionBindingDigest,
        ProtocolRecoveryCatalogId catalogId,
        ProtocolGameRootIdentity gameRoot,
        string headSha256,
        int retainNewest,
        ProtocolRecoverySelectionId[] retainedSelectionIds,
        ProtocolRecoverySelectionId[] removedSelectionIds,
        string[] cleanupGenerationIds,
        string summary,
        string[] warnings,
        ProtocolPlanRisk[] risks,
        ProtocolRecommendedDefault recommendedDefault,
        bool requiresConfirmation
    )
    {
        this.SessionId = sessionId;
        this.PrunePlanId = prunePlanId;
        this.PruneDigest = pruneDigest;
        this.ExecutionBindingDigest = executionBindingDigest;
        this.CatalogId = catalogId;
        this.GameRoot = gameRoot;
        this.HeadSha256 = headSha256;
        this.RetainNewest = retainNewest;
        this.RetainedValues = retainedSelectionIds?.ToArray()
            ?? throw new ProtocolException("The protocol 'retainedSelectionIds' collection can't be null.");
        this.RemovedValues = removedSelectionIds?.ToArray()
            ?? throw new ProtocolException("The protocol 'removedSelectionIds' collection can't be null.");
        this.CleanupGenerationValues = cleanupGenerationIds?.ToArray()
            ?? throw new ProtocolException("The protocol 'cleanupGenerationIds' collection can't be null.");
        this.Summary = summary;
        this.WarningValues = warnings?.ToArray() ?? throw new ProtocolException("The protocol 'warnings' collection can't be null.");
        this.RiskValues = risks?.ToArray() ?? throw new ProtocolException("The protocol 'risks' collection can't be null.");
        this.RecommendedDefault = recommendedDefault;
        this.RequiresConfirmation = requiresConfirmation;
    }

    internal PrunePlanEvent(ProtocolSessionId sessionId, ProtocolPrunePlanId prunePlanId, ProtocolPlanDigest pruneDigest, ProtocolPlanDigest executionBindingDigest, ProtocolRecoveryCatalogId catalogId, ProtocolGameRootIdentity gameRoot, string headSha256, int retainNewest, ProtocolRecoverySelectionId[] retainedSelectionIds, ProtocolRecoverySelectionId[] removedSelectionIds, string[] cleanupGenerationIds, string summary, string[] warnings, bool requiresConfirmation)
        : this(sessionId, prunePlanId, pruneDigest, executionBindingDigest, catalogId, gameRoot, headSha256, retainNewest, retainedSelectionIds, removedSelectionIds, cleanupGenerationIds, summary, warnings, [ProtocolPlanRisk.RecoveryPrune], ProtocolRecommendedDefault.Cancel, requiresConfirmation) { }

    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.PrunePlanEvent;
}

public sealed record ProgressEvent(
    ProtocolSessionId SessionId,
    ProtocolPlanId PlanId,
    ProtocolPlanDigest PlanDigest,
    long Sequence,
    TransactionStage Stage,
    long CompletedUnits,
    long? TotalUnits,
    string Message
) : ProtocolEvent
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.ProgressEvent;
}

public sealed record PruneProgressEvent(
    ProtocolSessionId SessionId,
    ProtocolPrunePlanId PrunePlanId,
    ProtocolPlanDigest PruneDigest,
    long Sequence,
    TransactionStage Stage,
    long CompletedUnits,
    long? TotalUnits,
    string Message
) : ProtocolEvent
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.PruneProgressEvent;
}

public sealed record SuccessEvent(
    ProtocolSessionId SessionId,
    ProtocolPlanId PlanId,
    ProtocolPlanDigest PlanDigest,
    InstallerOperation Operation,
    ProtocolExecutionOutcome Outcome,
    ProtocolTerminalState TerminalState,
    ProtocolExecutionSummary ExecutionSummary,
    string Summary,
    string? SanitizedLogPath
) : ProtocolEvent
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.SuccessEvent;
}

public sealed record RolledBackFailureEvent(
    ProtocolSessionId SessionId,
    ProtocolPlanId PlanId,
    ProtocolPlanDigest PlanDigest,
    ProtocolExecutionOutcome Outcome,
    ProtocolTerminalState TerminalState,
    ProtocolExecutionSummary ExecutionSummary,
    string Message,
    string Summary,
    string? SanitizedLogPath
) : ProtocolEvent
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.RolledBackFailureEvent;
}

public sealed record RecoverableInterruptionEvent(
    ProtocolSessionId SessionId,
    ProtocolPlanId PlanId,
    ProtocolPlanDigest PlanDigest,
    ProtocolExecutionOutcome Outcome,
    ProtocolTerminalState TerminalState,
    ProtocolExecutionSummary ExecutionSummary,
    string Message,
    string Summary,
    string? SanitizedLogPath
) : ProtocolEvent
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.RecoverableInterruptionEvent;
}

public sealed record CancelledEvent(
    ProtocolSessionId SessionId,
    ProtocolPlanId PlanId,
    ProtocolPlanDigest PlanDigest,
    ProtocolExecutionOutcome Outcome,
    ProtocolTerminalState TerminalState,
    ProtocolExecutionSummary ExecutionSummary,
    string Summary,
    string? SanitizedLogPath
) : ProtocolEvent
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.CancelledEvent;
}

public sealed record PruneSuccessEvent(
    ProtocolSessionId SessionId,
    ProtocolPrunePlanId PrunePlanId,
    ProtocolPlanDigest PruneDigest,
    ProtocolPruneOutcome Outcome,
    ProtocolTerminalState TerminalState,
    ProtocolPruneSummary PruneSummary,
    string Summary,
    string? SanitizedLogPath
) : ProtocolEvent
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.PruneSuccessEvent;
}

public sealed record PruneFailureEvent(
    ProtocolSessionId SessionId,
    ProtocolPrunePlanId PrunePlanId,
    ProtocolPlanDigest PruneDigest,
    ProtocolPruneOutcome Outcome,
    ProtocolTerminalState TerminalState,
    ProtocolPruneSummary PruneSummary,
    string Message,
    string? SanitizedLogPath
) : ProtocolEvent
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.PruneFailureEvent;
}

public sealed record PruneInterruptionEvent(
    ProtocolSessionId SessionId,
    ProtocolPrunePlanId PrunePlanId,
    ProtocolPlanDigest PruneDigest,
    ProtocolPruneOutcome Outcome,
    ProtocolTerminalState TerminalState,
    ProtocolPruneSummary PruneSummary,
    string Message,
    string? SanitizedLogPath
) : ProtocolEvent
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.PruneInterruptionEvent;
}

public sealed record PruneCancelledEvent(
    ProtocolSessionId SessionId,
    ProtocolPrunePlanId PrunePlanId,
    ProtocolPlanDigest PruneDigest,
    ProtocolPruneOutcome Outcome,
    ProtocolTerminalState TerminalState,
    ProtocolPruneSummary PruneSummary,
    string Summary,
    string? SanitizedLogPath
) : ProtocolEvent
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.PruneCancelledEvent;
}

public sealed record PrePlanRejectedEvent(
    ProtocolSessionId SessionId,
    ProtocolPrePlanErrorCode ErrorCode,
    string Message,
    ProtocolNextAction NextAction,
    bool IsTerminal,
    string? SanitizedLogPath
) : ProtocolEvent
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.PrePlanRejectedEvent;
}
