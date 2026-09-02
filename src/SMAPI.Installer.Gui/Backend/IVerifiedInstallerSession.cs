using System.Globalization;
using System.Text;
using StardewModdingAPI.Installer.Core.Ownership;
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

    /// <summary>
    /// List the bound game's recovery history without exposing its canonical path or the process client's exact
    /// recovery-point capabilities.
    /// </summary>
    Task<BoundInstallerRecoveryCatalogResult> ListRecoveriesAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This restricted session doesn't support recovery-history listing.");

    /// <summary>Consume one exact current bound-session recovery point to inspect a rollback plan.</summary>
    Task<InstallerReadOnlyPlanResult> InspectRollbackAsync(
        BoundInstallerRecoveryPoint point,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException("This restricted session doesn't support rollback inspection.");

    /// <summary>
    /// Consume one exact current bound-session recovery point as the oldest retained cleanup boundary and inspect a
    /// destructive recovery-prune plan. Inspection alone performs no filesystem mutation.
    /// </summary>
    Task<BoundInstallerRecoveryPrunePlanResult> InspectRecoveryPruneAsync(
        BoundInstallerRecoveryPoint oldestPointToKeep,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException("This restricted session doesn't support recovery-cleanup inspection.");

    /// <summary>Reinspect the current plan with an additive set of exact backend-issued file candidates.</summary>
    Task<InstallerReadOnlyPlanResult> ApprovePlanCandidatesAsync(IReadOnlyList<InstallerReadOnlyPlanCandidate> candidates, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This restricted session doesn't support candidate approval.");

    /// <summary>
    /// Consume this layer's exact current confirmation capability and transfer backend cleanup ownership to a sealed
    /// confirmed-plan session. Confirmation alone performs no filesystem mutation.
    /// </summary>
    Task<IConfirmedInstallerSession> ConfirmPlanAsync(InstallerPlanConfirmation confirmation, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This restricted session doesn't support plan confirmation.");

    /// <summary>
    /// Consume this layer's exact current recovery-prune confirmation capability and transfer backend cleanup
    /// ownership to a sealed confirmed-prune session. Confirmation alone performs no filesystem mutation.
    /// </summary>
    Task<IConfirmedRecoveryPruneSession> ConfirmRecoveryPruneAsync(
        BoundInstallerRecoveryPruneConfirmation confirmation,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException("This restricted session doesn't support recovery-cleanup confirmation.");
}

/// <summary>
/// A capability-reduced recovery-history result. It contains no canonical path, filesystem identity, transport
/// identifier, digest, package identity, raw backend text, log path, or process-client authority.
/// </summary>
internal abstract record BoundInstallerRecoveryCatalogResult;

/// <summary>A bounded newest-first catalog whose points are exact bound-session reference capabilities.</summary>
internal sealed record BoundInstallerRecoveryCatalogSuccess(
    IReadOnlyList<BoundInstallerRecoveryPoint> RecoveryPoints
) : BoundInstallerRecoveryCatalogResult;

/// <summary>A normal bounded observation that the selected game has no committed recovery history.</summary>
internal sealed record BoundInstallerNoRecoveryHistory : BoundInstallerRecoveryCatalogResult;

/// <summary>A sanitized recovery lookup rejection with no backend message or private log path.</summary>
internal sealed record BoundInstallerRecoveryCatalogRejection(
    ProtocolPrePlanErrorCode ErrorCode,
    ProtocolNextAction NextAction,
    bool IsTerminal
) : BoundInstallerRecoveryCatalogResult;

/// <summary>A capability-reduced result from inspecting one exact bound-session recovery-prune boundary.</summary>
internal abstract record BoundInstallerRecoveryPrunePlanResult;

/// <summary>
/// Bounded recovery-prune facts with a session-reminted confirmation capability. This excludes canonical paths,
/// filesystem identity, transport/catalog/selection/generation IDs, digests, raw backend text, and private logs.
/// </summary>
internal sealed record BoundInstallerRecoveryPrunePlanSuccess(
    int RetainNewest,
    int RetainedCount,
    int RemovedCount,
    int CleanupGenerationCount,
    bool AuxiliaryCleanupPlanned,
    int WarningCount,
    IReadOnlyList<ProtocolPlanRisk> Risks,
    ProtocolRecommendedDefault RecommendedDefault,
    bool RequiresConfirmation
) : BoundInstallerRecoveryPrunePlanResult
{
    internal BoundInstallerRecoveryPruneConfirmation? Confirmation { get; init; }
}

/// <summary>A reachable sanitized rejection. Every rejection requires a fresh recovery catalog.</summary>
internal sealed record BoundInstallerRecoveryPrunePlanRejection(
    ProtocolPrePlanErrorCode ErrorCode,
    ProtocolNextAction NextAction,
    bool IsTerminal
) : BoundInstallerRecoveryPrunePlanResult;

/// <summary>A property-free exact-reference capability for confirming the current bound recovery-prune plan.</summary>
internal sealed class BoundInstallerRecoveryPruneConfirmation
{
    internal BoundInstallerRecoveryPruneConfirmation() { }
}

/// <summary>
/// One sanitized recovery point reminted by the bound session. Its object identity is the only selection capability;
/// it contains no process-client point or protocol authority.
/// </summary>
internal sealed class BoundInstallerRecoveryPoint
{
    public int Ordinal { get; }
    public bool IsCurrent { get; }
    public bool IsUserCheckpoint { get; }
    public InstallerOperation OriginOperation { get; }
    public BoundInstallerRecoveryRestoreTarget RestoreTarget { get; }

    internal BoundInstallerRecoveryPoint(
        int ordinal,
        bool isCurrent,
        bool isUserCheckpoint,
        InstallerOperation originOperation,
        BoundInstallerRecoveryRestoreTarget restoreTarget
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

/// <summary>The mutually exclusive sanitized state restored by one bound recovery point.</summary>
internal abstract record BoundInstallerRecoveryRestoreTarget;

internal sealed record BoundInstallerRecoveryReleaseTarget(
    string Tag,
    string EmbeddedVersion
) : BoundInstallerRecoveryRestoreTarget;

internal sealed record BoundInstallerRecoveryUninstalledTarget : BoundInstallerRecoveryRestoreTarget;

/// <summary>
/// The sealed capability-reduced owner produced after exact plan confirmation. It can consume its exact confirmed
/// plan once; all progress, cancellation, and terminal data remain bounded and sanitized by the protocol client.
/// </summary>
internal interface IConfirmedInstallerSession : IAsyncDisposable
{
    ProtocolReleaseIdentity Release { get; }
    VerifiedGamePresentation Game { get; }
    Task<InstallerProtocolClientException> SessionFaulted { get; }

    /// <summary>Consume the exact confirmed plan and admit its one execution.</summary>
    Task<InstallerExecutionOperation> ExecuteAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// After the exact execution terminal requires interrupted recovery (or local post-admission uncertainty),
    /// transfer a sealed owner which can explicitly start fresh authenticated recovery attempts.
    /// </summary>
    Task<InstallerPostExecutionRecoveryOwner> TakePostExecutionRecoveryOwnerAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This confirmed session doesn't support post-execution recovery ownership.");
}

/// <summary>
/// A sealed capability-reduced owner for one exact confirmed recovery-prune plan. Execution is explicit and one-shot;
/// progress and terminal data remain bounded and sanitized by the process client.
/// </summary>
internal interface IConfirmedRecoveryPruneSession : IAsyncDisposable
{
    ProtocolReleaseIdentity Release { get; }
    VerifiedGamePresentation Game { get; }
    Task<InstallerProtocolClientException> SessionFaulted { get; }

    Task<InstallerRecoveryPruneOperation> ExecuteAsync(CancellationToken cancellationToken = default);
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

    /// <summary>Whether a display value is the exact escape projection of one canonical managed relative path.</summary>
    public static bool IsEscapedCanonicalRelativePath(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        StringBuilder decoded = new(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (current != '\\')
            {
                decoded.Append(current);
                continue;
            }
            if (index + 5 >= value.Length
                || value[index + 1] != 'u'
                || !int.TryParse(value.AsSpan(index + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int codeUnit))
            {
                return false;
            }
            decoded.Append((char)codeUnit);
            index += 5;
        }
        string raw = decoded.ToString();
        try
        {
            NormalizedRelativePath parsed = NormalizedRelativePath.Parse(raw);
            return string.Equals(Escape(parsed.Value), value, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
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
