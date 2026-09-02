using System.Threading.Channels;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Core.Transactions;

namespace StardewModdingAPI.Installer.Gui.Backend;

/// <summary>A sanitized result from inspecting one exact authenticated recovery-prune boundary.</summary>
internal abstract record InstallerRecoveryPrunePlanResult;

/// <summary>
/// Bounded local facts about an exact recovery-prune plan. This deliberately excludes backend prose, filesystem
/// identities, catalog/selection/generation IDs, digests, and log paths.
/// </summary>
internal sealed record InstallerRecoveryPrunePlanSuccess(
    int RetainNewest,
    int RetainedCount,
    int RemovedCount,
    int CleanupGenerationCount,
    bool AuxiliaryCleanupPlanned,
    int WarningCount,
    IReadOnlyList<ProtocolPlanRisk> Risks,
    ProtocolRecommendedDefault RecommendedDefault,
    bool RequiresConfirmation
) : InstallerRecoveryPrunePlanResult
{
    internal InstallerRecoveryPruneConfirmation? Confirmation { get; init; }
}

/// <summary>A reachable pre-prune rejection with no raw backend message or private log path.</summary>
internal sealed record InstallerRecoveryPrunePlanRejection(
    ProtocolPrePlanErrorCode ErrorCode,
    ProtocolNextAction NextAction,
    bool IsTerminal
) : InstallerRecoveryPrunePlanResult;

/// <summary>A property-free exact-reference capability for confirming the current prune plan.</summary>
internal sealed class InstallerRecoveryPruneConfirmation
{
    internal InstallerRecoveryPruneConfirmation() { }
}

/// <summary>A property-free exact-reference authority returned after exact prune confirmation.</summary>
internal sealed class InstallerConfirmedRecoveryPruneAuthority
{
    internal InstallerConfirmedRecoveryPruneAuthority() { }
}

/// <summary>A bounded typed prune update with no backend text, path, identifier, or digest.</summary>
internal sealed record InstallerRecoveryPruneProgress(
    TransactionStage Stage,
    int CompletedUnits,
    int? TotalUnits
);

/// <summary>A sanitized recovery-prune outcome.</summary>
internal abstract record InstallerRecoveryPruneResult;

/// <summary>Bounded aggregate facts from one exact prune terminal.</summary>
internal sealed record InstallerRecoveryPruneSummary(
    int? LogicallyRemovedGenerationCount,
    int? PhysicallyCleanedGenerationCount,
    int? PendingCleanupGenerationCount,
    bool? AuxiliaryCleanupPending
);

/// <summary>A fully validated terminal from the exact admitted prune command.</summary>
internal sealed record InstallerRecoveryPruneTerminalResult(
    ProtocolPruneOutcome Outcome,
    ProtocolDurableState DurableState,
    ProtocolTerminalErrorCode? ErrorCode,
    ProtocolRecoveryDisposition RecoveryDisposition,
    ProtocolNextAction NextAction,
    InstallerRecoveryPruneSummary Summary,
    InstallerBackendSettlement BackendSettlement
) : InstallerRecoveryPruneResult;

internal enum InstallerRecoveryPruneUncertaintyReason
{
    BackendStateCouldNotBeConfirmed
}

/// <summary>
/// A conservative result used when transport was lost after pruning may have begun. A fresh authenticated catalog
/// must be loaded; this never claims that recovery history is unchanged or that the exact prune can be retried.
/// </summary>
internal sealed record InstallerRecoveryPruneStateUnknownResult : InstallerRecoveryPruneResult
{
    public InstallerRecoveryPruneUncertaintyReason Reason => InstallerRecoveryPruneUncertaintyReason.BackendStateCouldNotBeConfirmed;
    public ProtocolDurableState DurableState => ProtocolDurableState.Unknown;
    public ProtocolTerminalErrorCode? ErrorCode => null;
    public ProtocolRecoveryDisposition RecoveryDisposition => ProtocolRecoveryDisposition.StateRefreshRequired;
    public ProtocolNextAction NextAction => ProtocolNextAction.ListRecoveries;
}

/// <summary>One admitted, bounded, cancellable recovery-prune operation.</summary>
internal sealed class InstallerRecoveryPruneOperation
{
    private readonly Func<Task> RequestCancellationCore;

    public ChannelReader<InstallerRecoveryPruneProgress> Progress { get; }
    public Task<InstallerRecoveryPruneResult> Completion { get; }

    internal InstallerRecoveryPruneOperation(
        ChannelReader<InstallerRecoveryPruneProgress> progress,
        Task<InstallerRecoveryPruneResult> completion,
        Func<Task> requestCancellation
    )
    {
        this.Progress = progress ?? throw new ArgumentNullException(nameof(progress));
        this.Completion = completion ?? throw new ArgumentNullException(nameof(completion));
        this.RequestCancellationCore = requestCancellation ?? throw new ArgumentNullException(nameof(requestCancellation));
    }

    public Task RequestCancellationAsync() => this.RequestCancellationCore();
}
