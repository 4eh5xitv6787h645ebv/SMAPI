using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Protocol.V1;

namespace StardewModdingAPI.Installer.Gui.Backend;

/// <summary>A sanitized read-only result which never exposes transport authority, file identities, or paths.</summary>
internal abstract record InstallerReadOnlyPlanResult;

/// <summary>
/// A complete, digest-verified read-only description of a backend plan. These facts do not carry execution,
/// approval, or confirmation authority, even when no blocking conflict was observed.
/// </summary>
internal sealed record InstallerReadOnlyPlanSuccess(
    InstallerOperation Operation,
    ObservedInstallState ObservedState,
    InstallerPlanRelease? CurrentRelease,
    InstallerPlanRelease? TargetRelease,
    bool HasBlockingConflicts,
    IReadOnlyList<ProtocolPlanRisk> Risks,
    ProtocolRecommendedDefault RecommendedDefault,
    bool SeparateConfirmationRequired,
    IReadOnlyList<InstallerPlanOperationCount> OperationCounts,
    IReadOnlyList<InstallerPlanConflictCount> ConflictCounts,
    IReadOnlyList<InstallerPlanCandidateCount> CandidateCounts,
    int AdditionalNoticeCount
) : InstallerReadOnlyPlanResult;

/// <summary>A normal pre-plan domain rejection without backend text, private logs, paths, or opaque IDs.</summary>
internal sealed record InstallerReadOnlyPlanRejection(
    ProtocolPrePlanErrorCode ErrorCode,
    ProtocolNextAction NextAction,
    bool IsTerminal
) : InstallerReadOnlyPlanResult;

/// <summary>The minimum public release label needed to explain current and target semantics.</summary>
internal sealed record InstallerPlanRelease(string Tag, string EmbeddedVersion);

internal sealed record InstallerPlanOperationCount(PlanOperationKind Kind, int Count);

internal sealed record InstallerPlanConflictCount(PlanConflictCode Code, int Count);

internal sealed record InstallerPlanCandidateCount(
    FileReplacementCandidateReason Reason,
    FileReplacementCandidateDisposition Disposition,
    bool Selected,
    int Count
);
