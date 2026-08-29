using StardewModdingAPI.Installer.Core.Protocol.V1;

namespace StardewModdingAPI.Installer.Gui.Backend;

/// <summary>The deliberately narrow backend surface available to the release-verification UI slice.</summary>
internal interface IInstallerProtocolClient : IAsyncDisposable
{
    /// <summary>Completes with a generic fault if the live backend session later violates its transport contract.</summary>
    Task<InstallerProtocolClientException> SessionFaulted { get; }

    /// <summary>Start and authenticate a fresh protocol session.</summary>
    Task<HandshakeEvent> HandshakeAsync(string clientName, string clientVersion, CancellationToken cancellationToken = default);

    /// <summary>Ask the backend to independently verify and open one complete local package set.</summary>
    Task<InstallerPackageOpenResult> OpenPackageAsync(InstallerPackageOpenInput package, CancellationToken cancellationToken = default);
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
