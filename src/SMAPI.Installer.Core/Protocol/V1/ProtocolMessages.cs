using System.Text.Json.Serialization;
using StardewModdingAPI.Installer.Core.Planning;

namespace StardewModdingAPI.Installer.Core.Protocol.V1;

/// <summary>The exact message variants in the version 1 protocol.</summary>
public enum ProtocolMessageKind
{
    HandshakeRequest,
    InspectPlanRequest,
    ConfirmPlanRequest,
    ExecutePlanRequest,
    CancelPlanRequest,
    HandshakeEvent,
    PlanEvent,
    ProgressEvent,
    SuccessEvent,
    RolledBackFailureEvent,
    RecoverableInterruptionEvent,
    CancelledEvent
}

/// <summary>An installer operation exposed by the machine protocol.</summary>
public enum InstallerOperation
{
    Install,
    Update,
    Repair,
    Uninstall,
    Backup,
    Rollback
}

/// <summary>The observed state of the selected game installation.</summary>
public enum ObservedInstallState
{
    NotInstalled,
    KnownUnmodified,
    KnownModified,
    LegacyOrOfficial,
    Unknown
}

/// <summary>A bounded high-level execution stage.</summary>
public enum InstallerProgressStage
{
    Revalidating,
    BackingUp,
    RemovingManagedFiles,
    CopyingManagedFiles,
    UpdatingLauncher,
    VerifyingInstallation,
    RollingBack,
    Finalizing
}

/// <summary>The safe next action after a recoverable interruption.</summary>
public enum InstallerRecoveryAction
{
    Resume,
    Retry,
    Rollback,
    InspectAgain
}

/// <summary>The base type for all version 1 messages.</summary>
public abstract record ProtocolMessage
{
    /// <summary>The discriminator written into the JSON envelope.</summary>
    [JsonIgnore]
    public abstract ProtocolMessageKind Kind { get; }
}

/// <summary>The base type for client-to-backend requests.</summary>
public abstract record ProtocolRequest : ProtocolMessage;

/// <summary>The base type for backend-to-client events.</summary>
public abstract record ProtocolEvent : ProtocolMessage;

/// <summary>Start a version 1 session.</summary>
public sealed record HandshakeRequest(string ClientName, string ClientVersion) : ProtocolRequest
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.HandshakeRequest;
}

/// <summary>Inspect a game path and create an immutable operation plan.</summary>
public sealed record InspectPlanRequest(
    ProtocolSessionId SessionId,
    string GamePath,
    InstallerOperation Operation,
    string? TargetPackageVersion
) : ProtocolRequest
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.InspectPlanRequest;
}

/// <summary>Confirm the exact currently issued plan.</summary>
public sealed record ConfirmPlanRequest(
    ProtocolSessionId SessionId,
    ProtocolPlanId PlanId,
    ProtocolPlanDigest PlanDigest
) : ProtocolRequest
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.ConfirmPlanRequest;
}

/// <summary>Execute the exact confirmed plan.</summary>
public sealed record ExecutePlanRequest(
    ProtocolSessionId SessionId,
    ProtocolPlanId PlanId,
    ProtocolPlanDigest PlanDigest
) : ProtocolRequest
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.ExecutePlanRequest;
}

/// <summary>Request cancellation of the exact current plan at a safe boundary.</summary>
public sealed record CancelPlanRequest(
    ProtocolSessionId SessionId,
    ProtocolPlanId PlanId,
    ProtocolPlanDigest PlanDigest
) : ProtocolRequest
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.CancelPlanRequest;
}

/// <summary>Accept a handshake and assign the random backend session ID.</summary>
public sealed record HandshakeEvent(
    ProtocolSessionId SessionId,
    string ServerVersion,
    string[] Capabilities
) : ProtocolEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.HandshakeEvent;
}

/// <summary>The exact reviewed release identity shown by the frontend for a plan.</summary>
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

/// <summary>The canonical selected Linux game-root identity observed during inspection.</summary>
public sealed record ProtocolGameRootIdentity(
    string CanonicalPath,
    uint DeviceMajor,
    uint DeviceMinor,
    ulong Inode,
    ulong OperationGeneration
);

/// <summary>One structured, deterministically ordered execution-plan operation.</summary>
public sealed record ProtocolPlanOperation(
    PlanOperationKind Kind,
    string Path,
    string? ExpectedCurrentSha256,
    string? ResultSha256
);

/// <summary>One structured, deterministically ordered reason a plan can't execute.</summary>
public sealed record ProtocolPlanConflict(PlanConflictCode Code, string? Path);

/// <summary>The immutable inspected operation plan presented for confirmation.</summary>
public sealed record PlanEvent : ProtocolEvent
{
    private readonly ProtocolPlanOperation[] OperationValues;
    private readonly ProtocolPlanConflict[] ConflictValues;
    private readonly string[] WarningValues;

    public ProtocolSessionId SessionId { get; }
    public ProtocolPlanId PlanId { get; }
    public ProtocolPlanDigest PlanDigest { get; }
    public ProtocolPlanDigest ExecutionBindingDigest { get; }
    public InstallerOperation Operation { get; }
    public ProtocolGameRootIdentity GameRoot { get; }
    public ProtocolReleaseIdentity? CurrentRelease { get; }
    public ProtocolReleaseIdentity? TargetRelease { get; }
    public ObservedInstallState ObservedState { get; }
    public ProtocolPlanOperation[] Operations => this.OperationValues.ToArray();
    public ProtocolPlanConflict[] Conflicts => this.ConflictValues.ToArray();
    public string Summary { get; }
    public string[] Warnings => this.WarningValues.ToArray();
    public bool RequiresConfirmation { get; }

    /// <summary>Construct an immutable plan-event snapshot.</summary>
    [JsonConstructor]
    public PlanEvent(
        ProtocolSessionId sessionId,
        ProtocolPlanId planId,
        ProtocolPlanDigest planDigest,
        ProtocolPlanDigest executionBindingDigest,
        InstallerOperation operation,
        ProtocolGameRootIdentity gameRoot,
        ProtocolReleaseIdentity? currentRelease,
        ProtocolReleaseIdentity? targetRelease,
        ObservedInstallState observedState,
        ProtocolPlanOperation[] operations,
        ProtocolPlanConflict[] conflicts,
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
        this.GameRoot = gameRoot;
        this.CurrentRelease = currentRelease;
        this.TargetRelease = targetRelease;
        this.ObservedState = observedState;
        this.OperationValues = operations?.ToArray() ?? throw new ProtocolException("The protocol 'operations' collection can't be null.");
        this.ConflictValues = conflicts?.ToArray() ?? throw new ProtocolException("The protocol 'conflicts' collection can't be null.");
        this.Summary = summary;
        this.WarningValues = warnings?.ToArray() ?? throw new ProtocolException("The protocol 'warnings' collection can't be null.");
        this.RequiresConfirmation = requiresConfirmation;
    }

    /// <inheritdoc />
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.PlanEvent;
}

/// <summary>Bounded progress for an executing plan.</summary>
public sealed record ProgressEvent(
    ProtocolSessionId SessionId,
    ProtocolPlanId PlanId,
    ProtocolPlanDigest PlanDigest,
    long Sequence,
    InstallerProgressStage Stage,
    long CompletedUnits,
    long? TotalUnits,
    string Message
) : ProtocolEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.ProgressEvent;
}

/// <summary>The plan completed and passed its final verification.</summary>
public sealed record SuccessEvent(
    ProtocolSessionId SessionId,
    ProtocolPlanId PlanId,
    ProtocolPlanDigest PlanDigest,
    InstallerOperation Operation,
    string Summary
) : ProtocolEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.SuccessEvent;
}

/// <summary>The plan failed and its transaction was successfully rolled back.</summary>
public sealed record RolledBackFailureEvent(
    ProtocolSessionId SessionId,
    ProtocolPlanId PlanId,
    ProtocolPlanDigest PlanDigest,
    string ErrorCode,
    string Message,
    string RollbackSummary
) : ProtocolEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.RolledBackFailureEvent;
}

/// <summary>The process stopped with durable state from which the user can safely recover.</summary>
public sealed record RecoverableInterruptionEvent(
    ProtocolSessionId SessionId,
    ProtocolPlanId PlanId,
    ProtocolPlanDigest PlanDigest,
    string ErrorCode,
    string Message,
    InstallerRecoveryAction RecoveryAction,
    string RecoverySummary
) : ProtocolEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.RecoverableInterruptionEvent;
}

/// <summary>Cancellation completed at a safe boundary and left no incomplete transaction.</summary>
public sealed record CancelledEvent(
    ProtocolSessionId SessionId,
    ProtocolPlanId PlanId,
    ProtocolPlanDigest PlanDigest,
    string Summary,
    string SafeStateSummary
) : ProtocolEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.CancelledEvent;
}
