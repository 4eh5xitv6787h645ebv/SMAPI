using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Protocol.V1;

namespace StardewModdingAPI.Installer.Gui.Backend;

/// <summary>
/// A sanitized result with no opaque transport authority, raw backend strings or file identities, or canonical
/// absolute game path. Candidate objects expose only normalized and escaped relative display paths and exact-reference
/// scoped approval capability.
/// </summary>
internal abstract record InstallerReadOnlyPlanResult;

/// <summary>
/// A complete, digest-verified read-only description of a backend plan. Candidate objects are exact scoped
/// approval capabilities. An executable result may carry one non-presentational exact-reference confirmation
/// capability for the bound backend layer; no result carries execution authority.
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
) : InstallerReadOnlyPlanResult
{
    /// <summary>
    /// A layer-local exact-reference capability for confirming this executable plan. Presentation code must not
    /// retain or expose it, and each ownership layer must replace it with a freshly minted reference.
    /// </summary>
    internal InstallerPlanConfirmation? Confirmation { get; init; }

    /// <summary>
    /// Sanitized, reference-identity approval capabilities for the exact candidates in this plan. These can only
    /// be presented or returned to the session which issued them; their opaque protocol authority remains private.
    /// </summary>
    public IReadOnlyList<InstallerReadOnlyPlanCandidate> Candidates { get; init; } = [];
}

/// <summary>
/// One opaque, property-free, exact-reference capability for confirming the current executable plan at one backend
/// ownership layer. It deliberately has no value equality and carries no transport identifier or digest.
/// </summary>
internal sealed class InstallerPlanConfirmation
{
    internal InstallerPlanConfirmation() { }
}

/// <summary>
/// One opaque exact-reference authority returned only after the process backend acknowledged confirmation of the
/// retained plan. It is held by the confirmed session owner for a later execution slice and carries no public data.
/// </summary>
internal sealed class InstallerConfirmedPlanAuthority
{
    internal InstallerConfirmedPlanAuthority() { }
}

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

/// <summary>
/// One sanitized file-replacement candidate issued by an exact inspected plan. The live exact object reference is
/// itself the scoped approval capability, so copied, reconstructed, stale, or foreign values have no authority.
/// </summary>
internal sealed class InstallerReadOnlyPlanCandidate
{
    public string DisplayPath { get; }
    public FileReplacementCandidateReason Reason { get; }
    public FileReplacementCandidateDisposition Disposition { get; }
    public bool BackendProvisionallyIncluded { get; }

    internal InstallerReadOnlyPlanCandidate(ProtocolPlanCandidate source)
    {
        ArgumentNullException.ThrowIfNull(source);
        this.DisplayPath = InstallerDisplayText.Escape(NormalizedRelativePath.Parse(source.Path).Value);
        this.Reason = source.Reason;
        this.Disposition = source.Disposition;
        this.BackendProvisionallyIncluded = source.Selected;
    }
}

/// <summary>Creates a bounded stable snapshot before any candidate-selection authority is inspected.</summary>
internal static class InstallerCandidateSelection
{
    /// <summary>A conservative lifetime bound across repeated plan generations in one authenticated session.</summary>
    public const int MaximumIssuedCandidatesPerSession = ProtocolJsonSerializer.MaxPlanCandidates * ProtocolJsonSerializer.MaxPlanCandidates;

    public static InstallerReadOnlyPlanCandidate[] Snapshot(
        IReadOnlyList<InstallerReadOnlyPlanCandidate> candidates,
        string parameterName
    )
    {
        ArgumentNullException.ThrowIfNull(candidates, parameterName);
        int count;
        try { count = candidates.Count; }
        catch
        {
            throw new ArgumentException("The candidate selection couldn't be read safely.", parameterName);
        }
        if (count is < 1 or > ProtocolJsonSerializer.MaxPlanCandidates)
            throw new ArgumentException("Candidate approval requires a bounded nonempty set of exact issued candidates.", parameterName);

        InstallerReadOnlyPlanCandidate[] result = new InstallerReadOnlyPlanCandidate[count];
        for (int index = 0; index < count; index++)
        {
            try { result[index] = candidates[index]; }
            catch
            {
                throw new ArgumentException("The candidate selection couldn't be read safely.", parameterName);
            }
            if (result[index] is null)
                throw new ArgumentException("The candidate selection contains an invalid capability.", parameterName);
        }
        return result;
    }
}
