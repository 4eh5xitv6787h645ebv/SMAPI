using System.Text.Json.Serialization;

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
    RecoverableInterruptionEvent
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
    string TargetPackageVersion
) : ProtocolRequest
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.InspectPlanRequest;
}

/// <summary>Confirm the exact currently issued plan.</summary>
public sealed record ConfirmPlanRequest(ProtocolSessionId SessionId, ProtocolPlanId PlanId) : ProtocolRequest
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.ConfirmPlanRequest;
}

/// <summary>Execute the exact confirmed plan.</summary>
public sealed record ExecutePlanRequest(ProtocolSessionId SessionId, ProtocolPlanId PlanId) : ProtocolRequest
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.ExecutePlanRequest;
}

/// <summary>Request cancellation of the exact current plan at a safe boundary.</summary>
public sealed record CancelPlanRequest(ProtocolSessionId SessionId, ProtocolPlanId PlanId) : ProtocolRequest
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

/// <summary>The immutable inspected operation plan presented for confirmation.</summary>
public sealed record PlanEvent(
    ProtocolSessionId SessionId,
    ProtocolPlanId PlanId,
    InstallerOperation Operation,
    string GamePath,
    ObservedInstallState ObservedState,
    string Summary,
    string[] Warnings,
    bool RequiresConfirmation
) : ProtocolEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProtocolMessageKind Kind => ProtocolMessageKind.PlanEvent;
}

/// <summary>Bounded progress for an executing plan.</summary>
public sealed record ProgressEvent(
    ProtocolSessionId SessionId,
    ProtocolPlanId PlanId,
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
