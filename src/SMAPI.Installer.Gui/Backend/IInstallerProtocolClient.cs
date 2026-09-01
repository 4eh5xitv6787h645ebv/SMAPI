using StardewModdingAPI.Installer.Core.Protocol.V1;

namespace StardewModdingAPI.Installer.Gui.Backend;

/// <summary>The deliberately narrow backend surface retained within the authenticated GUI workflow.</summary>
internal interface IInstallerProtocolClient : IAsyncDisposable
{
    /// <summary>Completes with a generic fault if the live backend session later violates its transport contract.</summary>
    Task<InstallerProtocolClientException> SessionFaulted { get; }

    /// <summary>Start and authenticate a fresh protocol session.</summary>
    Task<HandshakeEvent> HandshakeAsync(string clientName, string clientVersion, CancellationToken cancellationToken = default);

    /// <summary>Ask the backend to independently verify and open one complete local package set.</summary>
    Task<InstallerPackageOpenResult> OpenPackageAsync(InstallerPackageOpenInput package, CancellationToken cancellationToken = default);

    /// <summary>Ask the authenticated backend session for its bounded Linux game-folder candidates.</summary>
    Task<IReadOnlyList<ProtocolGameCandidate>> DiscoverGamesAsync(CancellationToken cancellationToken = default);

    /// <summary>Ask the authenticated backend session to validate one exact canonical Linux game-folder path.</summary>
    Task<ProtocolGameCandidate> ValidateGameAsync(string canonicalPath, CancellationToken cancellationToken = default);

    /// <summary>Inspect one non-rollback operation and return a sanitized projection whose candidate references carry only scoped approval authority.</summary>
    Task<InstallerReadOnlyPlanResult> InspectPlanAsync(string canonicalGamePath, InstallerOperation operation, CancellationToken cancellationToken = default);

    /// <summary>Reinspect the retained exact plan after approving an additive set of its issued candidates.</summary>
    Task<InstallerReadOnlyPlanResult> ApprovePlanCandidatesAsync(IReadOnlyList<InstallerReadOnlyPlanCandidate> candidates, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This restricted client doesn't support candidate approval.");
}

/// <summary>
/// The only backend authority available after release verification. It retains exactly one verified package session,
/// exposes only game discovery and validation until later workflow slices add restricted operations, and owns cleanup.
/// </summary>
internal interface IVerifiedInstallerSession : IAsyncDisposable
{
    ProtocolReleaseIdentity Release { get; }
    Task<InstallerProtocolClientException> SessionFaulted { get; }
    Task<IReadOnlyList<ProtocolGameCandidate>> DiscoverGamesAsync(CancellationToken cancellationToken = default);
    Task<ProtocolGameCandidate> ValidateGameAsync(string canonicalPath, CancellationToken cancellationToken = default);
}

/// <summary>The six downloaded paths and reviewed release identity needed for package verification.</summary>
internal sealed record InstallerPackageOpenInput(
    string ReleaseTag,
    string ExpectedSourceCommit,
    string PackagePath,
    string ChecksumsPath,
    string BuildMetadataPath,
    string InstallManifestPath,
    string AttestationBundlePath,
    string AttestationBundleChecksumPath,
    ProtocolProcWorkspaceIdentity? ProcWorkspaceIdentity = null
);

/// <summary>A sanitized, typed outcome from local package verification.</summary>
internal abstract record InstallerPackageOpenResult;

internal sealed record InstallerPackageOpenSuccess(ProtocolReleaseIdentity Release) : InstallerPackageOpenResult;

/// <summary>A normal backend domain rejection with no private log path or raw exception detail.</summary>
internal sealed record InstallerPackageOpenRejection(
    ProtocolPrePlanErrorCode ErrorCode,
    ProtocolNextAction NextAction,
    string Message,
    bool IsTerminal
) : InstallerPackageOpenResult;

internal sealed class InstallerProtocolClientException : Exception
{
    public InstallerProtocolClientException(string message) : base(message) { }
}
