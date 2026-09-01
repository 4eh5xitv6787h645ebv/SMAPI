using StardewModdingAPI.Installer.Core.Protocol.V1;

namespace StardewModdingAPI.Installer.Gui.Backend;

/// <summary>A capability-reduced owner for one exact backend-validated game and verified release session.</summary>
internal interface IPlanInspectionSession : IAsyncDisposable
{
    /// <summary>The exact release whose package authority remains live in the backend session.</summary>
    ProtocolReleaseIdentity Release { get; }

    /// <summary>Sanitized presentation for the exact valid game selected before this session was bound.</summary>
    VerifiedGamePresentation Game { get; }

    /// <summary>Completes with a generic fault if the live backend session later violates its transport contract.</summary>
    Task<InstallerProtocolClientException> SessionFaulted { get; }

    /// <summary>Inspect one supported operation for only the game fixed by this bound session.</summary>
    Task<InstallerReadOnlyPlanResult> InspectPlanAsync(InstallerOperation operation, CancellationToken cancellationToken = default);
}

/// <summary>Non-authoritative display data for an exact valid game-folder selection.</summary>
internal sealed class VerifiedGamePresentation
{
    public string CanonicalPath { get; }
    public string DisplayName { get; }

    internal VerifiedGamePresentation(string canonicalPath, string displayName)
    {
        AssertCanonicalLinuxPath(canonicalPath);
        AssertSafeText(displayName, nameof(displayName));
        this.CanonicalPath = canonicalPath;
        this.DisplayName = displayName;
    }

    private static void AssertCanonicalLinuxPath(string value)
    {
        AssertSafeText(value, nameof(value));
        if (
            value[0] != '/'
            || value.IndexOf('\\') >= 0
            || value.Length > 1 && value[^1] == '/'
            || value.Split('/').Skip(1).Any(segment => segment.Length == 0 || segment is "." or "..")
        )
        {
            throw new ArgumentException("The selected game folder must be a canonical absolute Linux path.", nameof(value));
        }
    }

    private static void AssertSafeText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl))
            throw new ArgumentException("The selected game presentation is empty, too long, or contains control characters.", parameterName);
    }
}

/// <summary>Internal implementation seam for the one-time discovery-to-plan capability transition.</summary>
internal interface IVerifiedInstallerSessionBinder
{
    IPlanInspectionSession BindToGame(ProtocolGameCandidate candidate);
}

/// <summary>Restricts a verified discovery session to one exact valid game without exposing its backend authority.</summary>
internal static class VerifiedInstallerSessionExtensions
{
    public static IPlanInspectionSession BindToGame(this IVerifiedInstallerSession session, ProtocolGameCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(candidate);
        return session is IVerifiedInstallerSessionBinder binder
            ? binder.BindToGame(candidate)
            : throw new InvalidOperationException("The verified installer session doesn't support a safe game-binding handoff.");
    }
}
