using System.Globalization;
using System.Text;
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

    /// <summary>Reinspect the current plan with an additive set of exact backend-issued file candidates.</summary>
    Task<InstallerReadOnlyPlanResult> ApprovePlanCandidatesAsync(IReadOnlyList<InstallerReadOnlyPlanCandidate> candidates, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This restricted session doesn't support candidate approval.");
}

/// <summary>Bounded, non-authoritative display data for an exact valid game-folder selection.</summary>
internal sealed class VerifiedGamePresentation
{
    private const int MaximumSourceTextLength = 4096;

    public string DisplayPath { get; }
    public string DisplayName { get; }

    internal VerifiedGamePresentation(string canonicalPath, string displayName)
    {
        AssertCanonicalLinuxPath(canonicalPath);
        AssertBoundedText(displayName, nameof(displayName));
        this.DisplayPath = InstallerDisplayText.Escape(canonicalPath);
        this.DisplayName = InstallerDisplayText.Escape(displayName);
    }

    private static void AssertCanonicalLinuxPath(string value)
    {
        AssertBoundedText(value, nameof(value));
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

    private static void AssertBoundedText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumSourceTextLength)
            throw new ArgumentException("The selected game presentation is empty or too long.", parameterName);
    }

}

/// <summary>Escapes untrusted-but-bounded backend presentation text without losing visible path identity.</summary>
internal static class InstallerDisplayText
{
    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        StringBuilder? escaped = null;
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            int scalarLength = 1;
            bool invalidSurrogate = char.IsSurrogate(current);
            UnicodeCategory category;
            if (char.IsHighSurrogate(current) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                scalarLength = 2;
                invalidSurrogate = false;
                category = CharUnicodeInfo.GetUnicodeCategory(value, index);
            }
            else if (invalidSurrogate)
                category = UnicodeCategory.Surrogate;
            else
                category = char.GetUnicodeCategory(current);

            bool mustEscape = invalidSurrogate
                || category is UnicodeCategory.Control
                    or UnicodeCategory.Format
                    or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator;
            if (mustEscape)
            {
                escaped ??= new StringBuilder(value.Length + 8).Append(value, 0, index);
                AppendEscapedCodeUnit(escaped, current);
                if (scalarLength == 2)
                    AppendEscapedCodeUnit(escaped, value[++index]);
            }
            else if (escaped is not null)
            {
                escaped.Append(current);
                if (scalarLength == 2)
                    escaped.Append(value[++index]);
            }
            else if (scalarLength == 2)
                index++;
        }
        return escaped?.ToString() ?? value;
    }

    private static void AppendEscapedCodeUnit(StringBuilder target, char value)
    {
        target.Append("\\u").Append(((int)value).ToString("X4", CultureInfo.InvariantCulture));
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
