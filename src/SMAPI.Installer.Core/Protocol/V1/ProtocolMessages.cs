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
    RecoverInterruptedRequest,
    OpenPackageRequest,
    ListRecoveriesRequest,
    InspectPlanRequest,
    SelectPlanCandidatesRequest,
    ConfirmPlanRequest,
    ExecutePlanRequest,
    CancelPlanRequest,
    InspectPruneRequest,
    ConfirmPruneRequest,
    ExecutePruneRequest,
    CancelPruneRequest,
    HandshakeEvent,
    GameDiscoveryEvent,
    RecoveryProgressEvent,
    RecoveryCompletedEvent,
    RecoveryFailureEvent,
    PackageOpenedEvent,
    RecoveryCatalogEvent,
    PlanEvent,
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
    PrePlanErrorEvent
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

public enum ProtocolRecoveryResult
{
    NotNeeded,
    Succeeded,
    Pending,
    Failed
}

public enum InstallerRecoveryAction
{
    Resume,
    Retry,
    Rollback,
    InspectAgain
}

public abstract record ProtocolMessage
{
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

/// <summary>Recover bounded interrupted installer work before creating any fresh inspection.</summary>
public sealed record RecoverInterruptedRequest(ProtocolSessionId SessionId, string GamePath) : ProtocolRequest
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.RecoverInterruptedRequest;
}

/// <summary>Ask the backend to independently verify one complete local release asset set.</summary>
public sealed record OpenPackageRequest(
    ProtocolSessionId SessionId,
    string ReleaseTag,
    string ExpectedSourceCommit,
    string PackagePath,
    string ChecksumsPath,
    string BuildMetadataPath,
    string InstallManifestPath
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
    ProtocolGameRootIdentity GameRoot,
    bool NamedRootStillSelected,
    ulong PreviousOperationGeneration,
    ulong CurrentOperationGeneration,
    int RecoveredTransactionCount,
    int RecoveredPathCount,
    string Summary,
    string SafeNextStep,
    string? SanitizedLogPath
) : ProtocolEvent
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.RecoveryCompletedEvent;
}

public sealed record RecoveryFailureEvent(
    ProtocolSessionId SessionId,
    string ErrorCode,
    string Message,
    ProtocolRecoveryResult RecoveryResult,
    string SafeNextStep,
    string? SanitizedLogPath
) : ProtocolEvent
{
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
    bool IsUserCheckpoint
);

/// <summary>Host-side source metadata used to mint opaque recovery selections.</summary>
public sealed record ProtocolRecoveryGenerationSource(
    string GenerationId,
    InstallerOperation OriginOperation,
    bool IsCurrent,
    bool IsUserCheckpoint
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
    private readonly ProtocolPlanOperation[] OperationValues;
    private readonly ProtocolPlanConflict[] ConflictValues;
    private readonly ProtocolPlanCandidate[] CandidateValues;
    private readonly string[] WarningValues;

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
    public ProtocolPlanOperation[] Operations => this.OperationValues.ToArray();
    public ProtocolPlanConflict[] Conflicts => this.ConflictValues.ToArray();
    public ProtocolPlanCandidate[] Candidates => this.CandidateValues.ToArray();
    public string Summary { get; }
    public string[] Warnings => this.WarningValues.ToArray();
    public bool RequiresConfirmation { get; }

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
        ProtocolPlanOperation[] operations,
        ProtocolPlanConflict[] conflicts,
        ProtocolPlanCandidate[] candidates,
        string summary,
        string[] warnings,
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
        this.OperationValues = operations?.ToArray() ?? throw new ProtocolException("The protocol 'operations' collection can't be null.");
        this.ConflictValues = conflicts?.ToArray() ?? throw new ProtocolException("The protocol 'conflicts' collection can't be null.");
        this.CandidateValues = candidates?.ToArray() ?? throw new ProtocolException("The protocol 'candidates' collection can't be null.");
        this.Summary = summary;
        this.WarningValues = warnings?.ToArray() ?? throw new ProtocolException("The protocol 'warnings' collection can't be null.");
        this.RequiresConfirmation = requiresConfirmation;
    }

    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.PlanEvent;
}

public sealed record PrunePlanEvent : ProtocolEvent
{
    private readonly ProtocolRecoverySelectionId[] RetainedValues;
    private readonly ProtocolRecoverySelectionId[] RemovedValues;
    private readonly string[] CleanupGenerationValues;
    private readonly string[] WarningValues;

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
        this.RequiresConfirmation = requiresConfirmation;
    }

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
    string Summary,
    int FilesChanged,
    ProtocolRecoveryResult RecoveryResult,
    string SafeNextStep,
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
    string ErrorCode,
    string Message,
    string RollbackSummary,
    int FilesChanged,
    ProtocolRecoveryResult RecoveryResult,
    string SafeNextStep,
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
    string ErrorCode,
    string Message,
    InstallerRecoveryAction RecoveryAction,
    string RecoverySummary,
    int FilesChanged,
    ProtocolRecoveryResult RecoveryResult,
    string SafeNextStep,
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
    string Summary,
    string SafeStateSummary,
    int FilesChanged,
    ProtocolRecoveryResult RecoveryResult,
    string SafeNextStep,
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
    int LogicalRemovedGenerationCount,
    int PhysicalCleanupGenerationCount,
    string Summary,
    string SafeNextStep,
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
    string ErrorCode,
    string Message,
    int LogicalRemovedGenerationCount,
    int PhysicalCleanupGenerationCount,
    ProtocolRecoveryResult RecoveryResult,
    string SafeNextStep,
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
    string ErrorCode,
    string Message,
    InstallerRecoveryAction RecoveryAction,
    int LogicalRemovedGenerationCount,
    int PhysicalCleanupGenerationCount,
    ProtocolRecoveryResult RecoveryResult,
    string SafeNextStep,
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
    string Summary,
    string SafeStateSummary,
    int LogicalRemovedGenerationCount,
    int PhysicalCleanupGenerationCount,
    ProtocolRecoveryResult RecoveryResult,
    string SafeNextStep,
    string? SanitizedLogPath
) : ProtocolEvent
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.PruneCancelledEvent;
}

public sealed record PrePlanErrorEvent(
    ProtocolSessionId SessionId,
    string ErrorCode,
    string Message,
    string SafeNextStep,
    bool IsTerminal,
    string? SanitizedLogPath
) : ProtocolEvent
{
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.PrePlanErrorEvent;
}
