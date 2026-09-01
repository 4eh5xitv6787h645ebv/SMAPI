using StardewModdingAPI.Installer.Core.Protocol.V1;

namespace StardewModdingAPI.Installer.Gui.Backend;

/// <summary>
/// A sanitized recovery-history lookup result. It contains no path, filesystem identity, transport identifier,
/// digest, package identity, raw backend text, log path, or protocol authority.
/// </summary>
internal abstract record InstallerRecoveryCatalogResult;

/// <summary>A bounded newest-first recovery catalog whose point objects are exact-reference selection capabilities.</summary>
internal sealed record InstallerRecoveryCatalogSuccess(
    IReadOnlyList<InstallerRecoveryPoint> RecoveryPoints
) : InstallerRecoveryCatalogResult;

/// <summary>A normal nonterminal result reporting that bounded inspection observed no committed recovery history.</summary>
internal sealed record InstallerNoRecoveryHistory : InstallerRecoveryCatalogResult;

/// <summary>A reachable sanitized lookup rejection with no backend message or private log path.</summary>
internal sealed record InstallerRecoveryCatalogRejection(
    ProtocolPrePlanErrorCode ErrorCode,
    ProtocolNextAction NextAction,
    bool IsTerminal
) : InstallerRecoveryCatalogResult;

/// <summary>
/// One sanitized newest-first recovery point. Its object identity is a scoped local selection capability; none of
/// the private catalog, generation, selection, root, or digest binding is stored on this object.
/// </summary>
internal sealed class InstallerRecoveryPoint
{
    public int Ordinal { get; }
    public bool IsCurrent { get; }
    public bool IsUserCheckpoint { get; }
    public InstallerOperation OriginOperation { get; }
    public InstallerRecoveryRestoreTarget RestoreTarget { get; }

    internal InstallerRecoveryPoint(
        int ordinal,
        bool isCurrent,
        bool isUserCheckpoint,
        InstallerOperation originOperation,
        InstallerRecoveryRestoreTarget restoreTarget
    )
    {
        if (ordinal is < 1 or > ProtocolJsonSerializer.MaxRecoveryGenerations)
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        this.Ordinal = ordinal;
        this.IsCurrent = isCurrent;
        this.IsUserCheckpoint = isUserCheckpoint;
        this.OriginOperation = originOperation;
        this.RestoreTarget = restoreTarget ?? throw new ArgumentNullException(nameof(restoreTarget));
    }
}

/// <summary>The mutually exclusive sanitized state restored by one recovery point.</summary>
internal abstract record InstallerRecoveryRestoreTarget;

/// <summary>The only two release labels permitted outside the private protocol binding.</summary>
internal sealed record InstallerRecoveryReleaseTarget(
    string Tag,
    string EmbeddedVersion
) : InstallerRecoveryRestoreTarget;

/// <summary>An explicit restore-to-uninstalled-state marker with no additional data.</summary>
internal sealed record InstallerRecoveryUninstalledTarget : InstallerRecoveryRestoreTarget;
