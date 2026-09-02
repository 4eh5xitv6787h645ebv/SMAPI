using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Recovery;

namespace StardewModdingAPI.Installer.Core.Protocol.V1;

public enum ProtocolSessionState
{
    AwaitingHandshake,
    Ready,
    Recovering,
    RecoveryRequired,
    PlanIssued,
    PlanConfirmed,
    Executing,
    CancellationRequested,
    PrunePlanIssued,
    PrunePlanConfirmed,
    Pruning,
    PruneCancellationRequested,
    Completed
}

/// <summary>Owns exact core authorities and validates the lifecycle of one backend process.</summary>
public sealed class ProtocolSessionStateMachine : IDisposable
{
    private readonly Dictionary<ProtocolPackageId, PackageAuthority> Packages = [];
    private readonly Dictionary<ProtocolRecoveryCatalogId, RecoveryCatalogAuthority> Catalogs = [];
    private readonly Dictionary<ProtocolRecoverySelectionId, RecoverySelectionAuthority> Recoveries = [];
    private readonly Dictionary<ProtocolCandidateId, ModifiedFileReplacementCandidate> Candidates = [];
    private readonly HashSet<ProtocolCommandId> AcceptedCommands = [];
    private PlanEvent? CurrentPlan;
    private InspectedInstallationState? CurrentInspection;
    private PrunePlanEvent? CurrentPrunePlan;
    private RecoveryPrunePlan? CurrentCorePrunePlan;
    private ProtocolPlanOperation[] CurrentOperations = [];
    private ProtocolPlanConflict[] CurrentConflicts = [];
    private ProtocolPlanCandidate[] CurrentCandidates = [];
    private string[] CurrentWarnings = [];
    private ProtocolCommandId? ActiveCommandId;
    private bool ExecutionStartedForCurrentPlan;
    private bool PruneStarted;
    private bool Disposed;

    public ProtocolSessionId SessionId { get; } = ProtocolSessionId.CreateRandom();
    public ProtocolSessionState State { get; private set; } = ProtocolSessionState.AwaitingHandshake;
    public ProtocolPlanId? CurrentPlanId => this.CurrentPlan?.PlanId;
    public InstallerOperation? CurrentOperation => this.CurrentPlan?.Operation;
    public ProtocolPlanDigest? CurrentPlanDigest => this.CurrentPlan?.PlanDigest;
    public ProtocolPrunePlanId? CurrentPrunePlanId => this.CurrentPrunePlan?.PrunePlanId;
    public ProtocolPlanDigest? CurrentPruneDigest => this.CurrentPrunePlan?.PruneDigest;
    internal ProtocolCommandId? ActiveCommand => this.ActiveCommandId;
    public bool CurrentPlanCanExecute { get; private set; }
    public long LastProgressSequence { get; private set; } = -1;

    public HandshakeEvent AcceptHandshake(HandshakeRequest request, string serverVersion, params string[] capabilities)
    {
        this.AssertUsable(); ArgumentNullException.ThrowIfNull(request); this.RequireState(ProtocolSessionState.AwaitingHandshake);
        string[] snapshot = capabilities?.ToArray() ?? [];
        ProtocolJsonSerializer.SerializeLine(request);
        HandshakeEvent response = new(this.SessionId, serverVersion, snapshot) { CommandId = request.CommandId };
        ProtocolJsonSerializer.SerializeLine(response); this.State = ProtocolSessionState.Ready; return response;
    }

    /// <summary>Reserve one never-reusable command ID before any request work begins.</summary>
    internal void AcceptCommand(ProtocolRequest request)
    {
        this.AssertUsable(); ArgumentNullException.ThrowIfNull(request); ProtocolJsonSerializer.SerializeLine(request);
        if (this.AcceptedCommands.Count >= 65_536) throw new ProtocolException("The bounded session command registry is full.");
        if (!this.AcceptedCommands.Add(request.CommandId)) throw new ProtocolException("A command ID can't be reused within one protocol session.");
    }

    public GameDiscoveryEvent RecordDiscovery(DiscoverGamesRequest request, ProtocolGameCandidate[] candidates)
    {
        this.AssertUsable(); this.RequireReadyLookup(request.SessionId); ProtocolJsonSerializer.SerializeLine(request);
        ProtocolGameCandidate[] snapshot = candidates?.ToArray() ?? throw new ProtocolException("Candidates can't be null.");
        GameDiscoveryEvent result = new(this.SessionId, snapshot) { CommandId = request.CommandId }; ProtocolJsonSerializer.SerializeLine(result);
        return new GameDiscoveryEvent(result.SessionId, result.Candidates.ToArray()) { CommandId = request.CommandId };
    }

    public GameValidationEvent RecordGameValidation(ValidateGameRequest request, ProtocolGameCandidate candidate)
    {
        this.AssertUsable(); this.RequireReadyLookup(request.SessionId); ArgumentNullException.ThrowIfNull(candidate); ProtocolJsonSerializer.SerializeLine(request);
        GameValidationEvent result = new(this.SessionId, candidate) { CommandId = request.CommandId }; ProtocolJsonSerializer.SerializeLine(result);
        return result;
    }

    /// <summary>Validate a ready-state lookup before an orchestration service performs external bounded work.</summary>
    internal void ValidateReadyRequest(ProtocolSessionId sessionId) { this.AssertUsable(); this.RequireReadyLookup(sessionId); }

    /// <summary>Register a verified package authority. Ownership transfers to this session on success.</summary>
    public PackageOpenedEvent OpenPackage(OpenPackageRequest request, VerifiedPackageContent verifiedPackage)
    {
        this.AssertUsable(); this.RequireReadyLookup(request.SessionId); ArgumentNullException.ThrowIfNull(verifiedPackage); ProtocolJsonSerializer.SerializeLine(request);
        IVerifiedPackageContentAuthority authority = verifiedPackage;
        authority.AssertUsable(); InstallationReleaseIdentity release = verifiedPackage.Release;
        return this.RegisterPackageAuthority(request, release, verifiedPackage, verifiedPackage);
    }

    internal PackageOpenedEvent RegisterPackageAuthority(OpenPackageRequest request, InstallationReleaseIdentity release, IVerifiedPackageContentAuthority authority, IDisposable? owner = null)
    {
        if (this.Packages.Count >= ProtocolJsonSerializer.MaxPackages) throw new ProtocolException("The session package registry is full.");
        authority.AssertUsable();
        if (release.Tag != request.ReleaseTag || release.SourceCommit != request.ExpectedSourceCommit)
            throw new ProtocolException("The verified package authority doesn't match the requested tag and source commit.");
        ProtocolPackageId id; do id = ProtocolPackageId.CreateRandom(); while (this.Packages.ContainsKey(id));
        PackageOpenedEvent result = new(this.SessionId, id, ToProtocol(release)!) { CommandId = request.CommandId }; ProtocolJsonSerializer.SerializeLine(result);
        this.Packages.Add(id, new(authority.AuthorityIdentity, authority, release, owner)); return result;
    }

    /// <summary>Register a core-authenticated catalog and its live committed recovery authorities.</summary>
    public RecoveryCatalogEvent RecordRecoveryCatalog(ListRecoveriesRequest request, RecoveryHistory history, CommittedRecoveryHandle[] handles)
        => this.RecordRecoveryCatalogAuthorities(request, history, handles.Cast<ICommittedRecoveryContentAuthority>().ToArray());

    /// <summary>Record a normal no-history lookup without minting any empty or synthetic recovery authority.</summary>
    internal NoRecoveryHistoryEvent RecordNoRecoveryHistory(ListRecoveriesRequest request, GameRootIdentity observedGameRoot)
    {
        this.AssertUsable(); this.RequireReadyLookup(request.SessionId); ArgumentNullException.ThrowIfNull(observedGameRoot); ProtocolJsonSerializer.SerializeLine(request);
        if (request.GamePath != observedGameRoot.CanonicalPath) throw new ProtocolException("The absent recovery history game root doesn't match the requested path.");
        foreach (ProtocolRecoveryCatalogId old in this.Catalogs
            .Where(pair => pair.Value.GameRoot.CanonicalPath == observedGameRoot.CanonicalPath)
            .Select(pair => pair.Key)
            .ToArray())
        {
            this.RemoveCatalog(old);
        }
        NoRecoveryHistoryEvent result = new(this.SessionId) { CommandId = request.CommandId }; ProtocolJsonSerializer.SerializeLine(result); return result;
    }

    internal RecoveryCatalogEvent RecordRecoveryCatalogAuthorities(ListRecoveriesRequest request, RecoveryHistory history, ICommittedRecoveryContentAuthority[] handles)
    {
        this.AssertUsable(); this.RequireReadyLookup(request.SessionId); ArgumentNullException.ThrowIfNull(history); ProtocolJsonSerializer.SerializeLine(request);
        ICommittedRecoveryContentAuthority[] authoritySnapshot = handles?.ToArray() ?? throw new ProtocolException("Recovery handles can't be null.");
        if (history.Generations.Count is <= 0 or > ProtocolJsonSerializer.MaxRecoveryGenerations || authoritySnapshot.Length != history.Generations.Count)
            throw new ProtocolException("The authenticated recovery catalog and authority count are inconsistent.");
        Dictionary<Guid, ICommittedRecoveryContentAuthority> byId = authoritySnapshot.ToDictionary(handle => handle.GenerationId);
        ICommittedRecoveryContentAuthority first = authoritySnapshot[0]; first.AssertUsable();
        GameRootIdentity root = first.GameRoot;
        if (request.GamePath != root.CanonicalPath) throw new ProtocolException("The recovery catalog game root doesn't match the requested path.");
        foreach (RecoveryGenerationInfo info in history.Generations)
        {
            if (!byId.TryGetValue(info.GenerationId, out ICommittedRecoveryContentAuthority? handle)) throw new ProtocolException("The recovery catalog is missing its exact generation authority.");
            ICommittedRecoveryContentAuthority authority = handle; authority.AssertUsable();
            if (authority.GameRoot != root || authority.AuthorizedHeadPointerSha256 != history.HeadConfirmationDigest || authority.OriginAction != info.Action || !Equals(handle.RestoreRelease, info.RestoreRelease))
                throw new ProtocolException("A recovery handle doesn't match the exact authenticated catalog root, head, generation, or release.");
        }
        foreach (ProtocolRecoveryCatalogId old in this.Catalogs.Where(pair => pair.Value.GameRoot == root).Select(pair => pair.Key).ToArray()) this.RemoveCatalog(old);
        if (this.Catalogs.Count >= ProtocolJsonSerializer.MaxRecoveryCatalogs) throw new ProtocolException("The session recovery-catalog registry is full.");
        ProtocolRecoveryCatalogId catalogId; do catalogId = ProtocolRecoveryCatalogId.CreateRandom(); while (this.Catalogs.ContainsKey(catalogId));
        HashSet<ProtocolRecoverySelectionId> minted = [];
        ProtocolRecoveryGeneration[] generations = history.Generations.Select(info =>
        {
            ProtocolRecoverySelectionId selection; do selection = ProtocolRecoverySelectionId.CreateRandom(); while (this.Recoveries.ContainsKey(selection) || !minted.Add(selection));
            return new ProtocolRecoveryGeneration(selection, info.GenerationId.ToString("N"), ToProtocol(info.Action), info.IsCurrent, info.IsUserCheckpoint, ToProtocol(info.RestoreRelease), info.RestoreRelease is null);
        }).ToArray();
        RecoveryCatalogEvent result = new(this.SessionId, catalogId, ToProtocol(root, 0), history.HeadConfirmationDigest.Value, generations) { CommandId = request.CommandId }; ProtocolJsonSerializer.SerializeLine(result);
        RecoveryCatalogAuthority catalog = new(result, root, history, authoritySnapshot); this.Catalogs.Add(catalogId, catalog);
        for (int index = 0; index < generations.Length; index++) this.Recoveries.Add(generations[index].SelectionId, new(catalog, generations[index], byId[history.Generations[index].GenerationId]));
        return result;
    }

    /// <summary>Issue presentation data derived only from an exact core inspection. Ownership transfers on success.</summary>
    public PlanEvent IssuePlan(InspectPlanRequest request, InspectedInstallationState inspection)
    {
        this.AssertUsable(); this.RequireCanIssuePlan(); this.RequireSession(request.SessionId); ArgumentNullException.ThrowIfNull(inspection); ProtocolJsonSerializer.SerializeLine(request); inspection.AssertUsable();
        InstallerOperation operation = ToProtocol(inspection.Action);
        if (request.Operation != operation || request.GamePath != inspection.GameRoot.CanonicalPath) throw new ProtocolException("The core inspection doesn't match the requested action and game path.");
        ProtocolPackageId? packageId = this.ResolvePackageAuthority(request.PackageId, inspection.TargetPackageContent, inspection.TargetPackageAuthorityIdentity, operation);
        ProtocolRecoveryAuthority? recovery = this.ResolveRecoveryAuthority(request.RecoverySelectionId, inspection.RollbackContent, inspection.GameRoot, inspection.OperationGeneration, operation);
        return this.SetCurrentPlan(request.CommandId, inspection, packageId, recovery);
    }

    /// <summary>Resolve selected opaque IDs back to the exact core candidates needed by the core approval API.</summary>
    public IReadOnlyList<ModifiedFileReplacementCandidate> ResolveCandidateSelection(SelectPlanCandidatesRequest request)
    {
        this.AssertUsable(); this.RequireState(ProtocolSessionState.PlanIssued); this.RequireCurrentBinding(request.SessionId, request.PlanId, request.PlanDigest); ProtocolJsonSerializer.SerializeLine(request);
        return request.SelectedCandidateIds.Select(id => this.Candidates.TryGetValue(id, out ModifiedFileReplacementCandidate? candidate) ? candidate : throw new ProtocolException("The candidate ID is unknown or stale.")).ToArray();
    }

    /// <summary>Resolve opaque request IDs to borrowed core authorities without exposing them outside the protocol assembly.</summary>
    internal (IVerifiedPackageContentAuthority? Package, ICommittedRecoveryContentAuthority? Recovery) ResolveInspectionAuthorities(InspectPlanRequest request)
    {
        this.AssertUsable(); this.RequireCanIssuePlan(); this.RequireSession(request.SessionId); ProtocolJsonSerializer.SerializeLine(request);
        IVerifiedPackageContentAuthority? package = null;
        ICommittedRecoveryContentAuthority? recovery = null;
        if (request.Operation is InstallerOperation.Install or InstallerOperation.Update or InstallerOperation.Repair)
        {
            if (request.PackageId is not { } packageId || !this.Packages.TryGetValue(packageId, out PackageAuthority? registered))
                throw new ProtocolException("The package ID is unknown, stale, or missing for this operation.");
            registered.Authority.AssertUsable(); package = registered.Authority;
            if (request.RecoverySelectionId is not null) throw new ProtocolException("This operation must not carry recovery authority.");
        }
        else if (request.Operation == InstallerOperation.Rollback)
        {
            if (request.PackageId is not null) throw new ProtocolException("Rollback must not carry package authority.");
            if (request.RecoverySelectionId is not { } recoveryId || !this.Recoveries.TryGetValue(recoveryId, out RecoverySelectionAuthority? registered))
                throw new ProtocolException("The recovery selection ID is unknown, stale, or missing for rollback.");
            registered.Handle.AssertUsable(); recovery = registered.Handle;
        }
        else if (request.PackageId is not null || request.RecoverySelectionId is not null)
            throw new ProtocolException("This operation must not carry package or recovery authority.");
        return (package, recovery);
    }

    /// <summary>Resolve one opaque catalog ID to its immutable display identity for a core prune inspection.</summary>
    internal RecoveryCatalogEvent ResolveRecoveryCatalog(ProtocolSessionId sessionId, ProtocolRecoveryCatalogId catalogId)
    {
        this.AssertUsable(); this.RequireCanIssuePlan(); this.RequireSession(sessionId);
        if (!this.Catalogs.TryGetValue(catalogId, out RecoveryCatalogAuthority? catalog))
            throw new ProtocolException("The recovery catalog ID is unknown or stale.");
        return catalog.Event;
    }

    /// <summary>Begin explicit bounded journal recovery and invalidate every authority tied to the selected game root.</summary>
    internal void BeginInterruptedRecovery(RecoverInterruptedRequest request)
    {
        this.AssertUsable();
        if (this.State != ProtocolSessionState.RecoveryRequired) this.RequireCanIssuePlan();
        this.RequireSession(request.SessionId); ProtocolJsonSerializer.SerializeLine(request);
        this.InvalidateCurrentPlan(); this.CurrentPrunePlan = null; this.CurrentCorePrunePlan = null;
        foreach (ProtocolRecoveryCatalogId catalog in this.Catalogs.Keys.ToArray()) this.RemoveCatalog(catalog);
        this.LastProgressSequence = -1; this.ActiveCommandId = request.CommandId; this.State = ProtocolSessionState.Recovering;
    }

    internal void RecordRecoveryProgress(RecoveryProgressEvent progress)
    {
        this.AssertUsable(); this.RequireState(ProtocolSessionState.Recovering); this.RequireSession(progress.SessionId); this.RequireActiveCommand(progress.CommandId); this.RequireNextSequence(progress.Sequence); ProtocolJsonSerializer.SerializeLine(progress); this.LastProgressSequence = progress.Sequence;
    }

    internal void CompleteInterruptedRecovery(RecoverInterruptedRequest request, RecoveryCompletedEvent result)
    {
        this.AssertUsable(); this.RequireState(ProtocolSessionState.Recovering); this.RequireSession(request.SessionId); this.RequireSession(result.SessionId); this.RequireActiveCommand(result.CommandId);
        ProtocolJsonSerializer.SerializeLine(result); this.LastProgressSequence = -1; this.ActiveCommandId = null; this.State = ProtocolSessionState.Ready;
    }

    internal void FailInterruptedRecovery(RecoveryFailureEvent result)
    {
        this.AssertUsable(); this.RequireState(ProtocolSessionState.Recovering); this.RequireSession(result.SessionId); this.RequireActiveCommand(result.CommandId); ProtocolJsonSerializer.SerializeLine(result); this.LastProgressSequence = -1; this.ActiveCommandId = null; this.State = result.TerminalState.RecoveryDisposition == ProtocolRecoveryDisposition.NotRequired ? ProtocolSessionState.Ready : ProtocolSessionState.RecoveryRequired;
    }

    /// <summary>Borrow the current inspection for the exact current plan while an orchestration command is serialized.</summary>
    internal InspectedInstallationState ResolveCurrentInspection(ProtocolSessionId sessionId, ProtocolPlanId planId, ProtocolPlanDigest planDigest)
    {
        this.AssertUsable(); this.RequireCurrentBinding(sessionId, planId, planDigest);
        InspectedInstallationState inspection = this.CurrentInspection ?? throw new ProtocolException("The current inspection is unavailable.");
        inspection.AssertUsable(); return inspection;
    }

    /// <summary>Issue the replacement core inspection after additive exact candidate approval; the old plan becomes stale.</summary>
    public PlanEvent IssueCandidatePlan(SelectPlanCandidatesRequest request, InspectedInstallationState replacement)
    {
        IReadOnlyList<ModifiedFileReplacementCandidate> selected = this.ResolveCandidateSelection(request); ArgumentNullException.ThrowIfNull(replacement); replacement.AssertUsable();
        InspectedInstallationState currentInspection = this.CurrentInspection ?? throw new ProtocolException("The current inspection is unavailable.");
        if (replacement.Action != currentInspection.Action || replacement.Action is not (InstallationAction.Install or InstallationAction.Update or InstallationAction.Repair or InstallationAction.Uninstall) || replacement.GameRoot != currentInspection.GameRoot || replacement.OperationGeneration != currentInspection.OperationGeneration || !ReferenceEquals(replacement.TargetPackageAuthorityIdentity, currentInspection.TargetPackageAuthorityIdentity) || !ReferenceEquals(replacement.RollbackContent, currentInspection.RollbackContent))
            throw new ProtocolException("The replacement inspection doesn't preserve the exact current action, root, package, and recovery authorities.");
        HashSet<(string Path, string Sha256, int Mode)> expected = currentInspection.ModifiedFileReplacementApprovals
            .Select(approval => (approval.Path.Value, approval.ObservedSha256.Value, approval.ObservedUnixMode))
            .Concat(selected.Select(candidate => (candidate.Path.Value, candidate.ObservedSha256.Value, candidate.ObservedUnixMode)))
            .ToHashSet();
        HashSet<(string Path, string Sha256, int Mode)> actual = replacement.ModifiedFileReplacementApprovals.Select(approval => (approval.Path.Value, approval.ObservedSha256.Value, approval.ObservedUnixMode)).ToHashSet();
        if (!actual.SetEquals(expected)) throw new ProtocolException("The replacement inspection doesn't contain the exact selected candidate authorities.");
        return this.SetCurrentPlan(request.CommandId, replacement, this.CurrentPlan!.PackageId, this.CurrentPlan.RecoveryAuthority);
    }

    /// <summary>Issue a prune presentation only from an exact core prune plan and stored authenticated catalog.</summary>
    public PrunePlanEvent IssuePrunePlan(InspectPruneRequest request, RecoveryPrunePlan corePlan)
    {
        this.AssertUsable(); this.RequireCanIssuePlan(); this.RequireSession(request.SessionId); ArgumentNullException.ThrowIfNull(corePlan); ProtocolJsonSerializer.SerializeLine(request);
        RecoveryCatalogAuthority catalog = this.RequireExactCorePrunePlan(request, corePlan);
        if (
            corePlan.RemovedGenerationIds.Count == 0
                && corePlan.CleanupGenerationIds.Count == 0
                && !corePlan.HasAuxiliaryCleanup
        )
            throw new ProtocolException("The core prune plan is a no-op or doesn't match the exact stored catalog order.");
        Dictionary<Guid, ProtocolRecoverySelectionId> selections = catalog.Event.Generations.ToDictionary(item => Guid.ParseExact(item.GenerationId, "N"), item => item.SelectionId);
        ProtocolRecoverySelectionId[] retained = corePlan.RetainedGenerationIds.Select(id => selections[id]).ToArray();
        ProtocolRecoverySelectionId[] removed = corePlan.RemovedGenerationIds.Select(id => selections[id]).ToArray();
        string[] cleanup = corePlan.CleanupGenerationIds.Select(generationId => generationId.ToString("N")).ToArray();
        string summary = $"Logically remove {removed.Length} authenticated recovery generation(s), retain {retained.Length}, clean up {cleanup.Length} physical generation director{(cleanup.Length == 1 ? "y" : "ies")}, and {(corePlan.HasAuxiliaryCleanup ? "apply" : "skip")} auxiliary recovery-state cleanup."; string[] warnings = [];
        ProtocolPlanDigest execution = ProtocolPlanDigest.Parse(corePlan.ConfirmationDigest.Value); ProtocolPrunePlanId id = ProtocolPrunePlanId.CreateRandom();
        ProtocolGameRootIdentity pruneRoot = ToProtocol(corePlan.GameRoot, corePlan.OperationGeneration);
        ProtocolPlanDigest digest = ProtocolPlanDigest.ComputePrune(execution, request.CatalogId, pruneRoot, catalog.Event.HeadSha256, request.RetainNewest, retained, removed, cleanup, corePlan.HasAuxiliaryCleanup, summary, warnings, true);
        PrunePlanEvent result = new(this.SessionId, id, digest, execution, request.CatalogId, pruneRoot, catalog.Event.HeadSha256, request.RetainNewest, retained, removed, cleanup, corePlan.HasAuxiliaryCleanup, summary, warnings, [ProtocolPlanRisk.RecoveryPrune], ProtocolRecommendedDefault.Cancel, true) { CommandId = request.CommandId }; ProtocolJsonSerializer.SerializeLine(result);
        this.InvalidateCurrentPlan(); this.CurrentPrunePlan = result; this.CurrentCorePrunePlan = corePlan; this.LastProgressSequence = -1; this.PruneStarted = false; this.State = ProtocolSessionState.PrunePlanIssued; return result;
    }

    /// <summary>Record a benign exact observation that the authenticated catalog currently has no cleanup work.</summary>
    public PrePlanRejectedEvent RecordNoPruneWork(InspectPruneRequest request, RecoveryPrunePlan corePlan)
    {
        this.AssertUsable(); this.RequireCanIssuePlan(); this.RequireSession(request.SessionId); ArgumentNullException.ThrowIfNull(corePlan); ProtocolJsonSerializer.SerializeLine(request);
        this.RequireExactCorePrunePlan(request, corePlan);
        if (corePlan.RemovedGenerationIds.Count != 0 || corePlan.CleanupGenerationIds.Count != 0 || corePlan.HasAuxiliaryCleanup)
            throw new ProtocolException("A no-work prune result can't hide authenticated logical, physical, or auxiliary cleanup.");
        this.InvalidateCurrentPlan(); this.CurrentPrunePlan = null; this.CurrentCorePrunePlan = null; this.State = ProtocolSessionState.Ready;
        PrePlanRejectedEvent result = new(this.SessionId, ProtocolPrePlanErrorCode.NothingToPrune, "No authenticated recovery-history cleanup is currently required.", ProtocolNextAction.ListRecoveries, false, null) { CommandId = request.CommandId };
        this.RecordPrePlanRejection(result); return result;
    }

    /// <summary>Return one deterministic bounded page from the exact current digest-bound plan.</summary>
    public PlanPageEvent GetPlanPage(GetPlanPageRequest request)
    {
        this.AssertUsable();
        if (this.State is not (ProtocolSessionState.PlanIssued or ProtocolSessionState.PlanConfirmed)) throw new ProtocolException($"Plan pages can't be read while the session is in state '{this.State}'.");
        this.RequireCurrentBinding(request.SessionId, request.PlanId, request.PlanDigest); ProtocolJsonSerializer.SerializeLine(request);
        int total = request.PageKind switch
        {
            ProtocolPlanPageKind.Operations => this.CurrentOperations.Length,
            ProtocolPlanPageKind.Conflicts => this.CurrentConflicts.Length,
            ProtocolPlanPageKind.Candidates => this.CurrentCandidates.Length,
            ProtocolPlanPageKind.Warnings => this.CurrentWarnings.Length,
            _ => throw new ProtocolException("The requested plan page kind isn't defined.")
        };
        if (request.Offset < 0 || request.Offset >= total) throw new ProtocolException("The requested plan page offset is outside the exact collection.");

        PlanPageEvent? accepted = null;
        int maximum = Math.Min(128, total - request.Offset);
        for (int count = 1; count <= maximum; count++)
        {
            int? next = request.Offset + count < total ? request.Offset + count : null;
            PlanPageEvent candidate = new(
                this.SessionId,
                request.PlanId,
                request.PlanDigest,
                request.PageKind,
                request.Offset,
                total,
                next,
                request.PageKind == ProtocolPlanPageKind.Operations ? this.CurrentOperations.Skip(request.Offset).Take(count).ToArray() : [],
                request.PageKind == ProtocolPlanPageKind.Conflicts ? this.CurrentConflicts.Skip(request.Offset).Take(count).ToArray() : [],
                request.PageKind == ProtocolPlanPageKind.Candidates ? this.CurrentCandidates.Skip(request.Offset).Take(count).ToArray() : [],
                request.PageKind == ProtocolPlanPageKind.Warnings ? this.CurrentWarnings.Skip(request.Offset).Take(count).ToArray() : []
            )
            {
                CommandId = request.CommandId
            };
            try
            {
                string line = ProtocolJsonSerializer.SerializeLine(candidate);
                if (System.Text.Encoding.UTF8.GetByteCount(line) > ProtocolJsonSerializer.MaxLineBytes - 1024) break;
                accepted = candidate;
            }
            catch (ProtocolException) when (accepted is not null)
            {
                break;
            }
        }
        return accepted ?? throw new ProtocolException("One plan page item exceeds the bounded protocol line size.");
    }

    public CommandAcknowledgedEvent ConfirmPlan(ConfirmPlanRequest request) { this.AssertUsable(); this.RequireState(ProtocolSessionState.PlanIssued); if (!this.CurrentPlanCanExecute) throw new ProtocolException("A plan with unresolved conflicts can't be confirmed."); this.RequireCurrentBinding(request.SessionId, request.PlanId, request.PlanDigest); ProtocolJsonSerializer.SerializeLine(request); this.State = ProtocolSessionState.PlanConfirmed; CommandAcknowledgedEvent result = new(this.SessionId, ProtocolAcknowledgementKind.PlanConfirmed, request.PlanId, null) { CommandId = request.CommandId }; ProtocolJsonSerializer.SerializeLine(result); return result; }
    public InspectedInstallationState BeginExecution(ExecutePlanRequest request) { this.AssertUsable(); if (this.State == ProtocolSessionState.PlanIssued) throw new ProtocolException("The current plan must be confirmed before execution."); this.RequireState(ProtocolSessionState.PlanConfirmed); this.RequireCurrentBinding(request.SessionId, request.PlanId, request.PlanDigest); ProtocolJsonSerializer.SerializeLine(request); this.CurrentInspection!.AssertUsable(); this.ActiveCommandId = request.CommandId; this.State = ProtocolSessionState.Executing; this.ExecutionStartedForCurrentPlan = true; return this.CurrentInspection; }
    public CommandAcknowledgedEvent ConfirmPrune(ConfirmPruneRequest request) { this.AssertUsable(); this.RequireState(ProtocolSessionState.PrunePlanIssued); this.RequireCurrentPruneBinding(request.SessionId, request.PrunePlanId, request.PruneDigest); ProtocolJsonSerializer.SerializeLine(request); this.State = ProtocolSessionState.PrunePlanConfirmed; CommandAcknowledgedEvent result = new(this.SessionId, ProtocolAcknowledgementKind.PrunePlanConfirmed, null, request.PrunePlanId) { CommandId = request.CommandId }; ProtocolJsonSerializer.SerializeLine(result); return result; }
    public RecoveryPrunePlan BeginPrune(ExecutePruneRequest request) { this.AssertUsable(); if (this.State == ProtocolSessionState.PrunePlanIssued) throw new ProtocolException("The prune plan must be confirmed before execution."); this.RequireState(ProtocolSessionState.PrunePlanConfirmed); this.RequireCurrentPruneBinding(request.SessionId, request.PrunePlanId, request.PruneDigest); ProtocolJsonSerializer.SerializeLine(request); this.ActiveCommandId = request.CommandId; this.State = ProtocolSessionState.Pruning; this.PruneStarted = true; return this.CurrentCorePrunePlan!; }

    public CommandAcknowledgedEvent RequestCancellation(CancelPlanRequest request) { this.AssertUsable(); if (this.State is not (ProtocolSessionState.PlanIssued or ProtocolSessionState.PlanConfirmed or ProtocolSessionState.Executing)) throw new ProtocolException($"Cancellation can't be requested while the session is in state '{this.State}'."); this.RequireCurrentBinding(request.SessionId, request.PlanId, request.PlanDigest); ProtocolJsonSerializer.SerializeLine(request); bool active = this.State == ProtocolSessionState.Executing; this.State = active ? ProtocolSessionState.CancellationRequested : ProtocolSessionState.Completed; CommandAcknowledgedEvent result = new(this.SessionId, active ? ProtocolAcknowledgementKind.PlanCancellationRequested : ProtocolAcknowledgementKind.PlanCancelledBeforeExecution, request.PlanId, null) { CommandId = request.CommandId }; ProtocolJsonSerializer.SerializeLine(result); return result; }
    public CommandAcknowledgedEvent RequestPruneCancellation(CancelPruneRequest request) { this.AssertUsable(); if (this.State is not (ProtocolSessionState.PrunePlanIssued or ProtocolSessionState.PrunePlanConfirmed or ProtocolSessionState.Pruning)) throw new ProtocolException($"Prune cancellation can't be requested while the session is in state '{this.State}'."); this.RequireCurrentPruneBinding(request.SessionId, request.PrunePlanId, request.PruneDigest); ProtocolJsonSerializer.SerializeLine(request); bool active = this.State == ProtocolSessionState.Pruning; this.State = active ? ProtocolSessionState.PruneCancellationRequested : ProtocolSessionState.Completed; CommandAcknowledgedEvent result = new(this.SessionId, active ? ProtocolAcknowledgementKind.PruneCancellationRequested : ProtocolAcknowledgementKind.PruneCancelledBeforeExecution, null, request.PrunePlanId) { CommandId = request.CommandId }; ProtocolJsonSerializer.SerializeLine(result); return result; }
    internal void RequestInternalCancellation(ProtocolSessionId sessionId, ProtocolPlanId planId, ProtocolPlanDigest planDigest) { this.AssertUsable(); this.RequireState(ProtocolSessionState.Executing); this.RequireCurrentBinding(sessionId, planId, planDigest); this.State = ProtocolSessionState.CancellationRequested; }
    internal void RequestInternalPruneCancellation(ProtocolSessionId sessionId, ProtocolPrunePlanId planId, ProtocolPlanDigest planDigest) { this.AssertUsable(); this.RequireState(ProtocolSessionState.Pruning); this.RequireCurrentPruneBinding(sessionId, planId, planDigest); this.State = ProtocolSessionState.PruneCancellationRequested; }
    public void RecordProgress(ProgressEvent progress) { this.AssertUsable(); if (this.State is not (ProtocolSessionState.Executing or ProtocolSessionState.CancellationRequested)) throw new ProtocolException($"Progress can't be recorded while the session is in state '{this.State}'."); if (!this.ExecutionStartedForCurrentPlan) throw new ProtocolException("Progress can't be recorded for a plan cancelled before execution began."); this.RequireCurrentBinding(progress.SessionId, progress.PlanId, progress.PlanDigest); this.RequireActiveCommand(progress.CommandId); this.RequireNextSequence(progress.Sequence); ProtocolJsonSerializer.SerializeLine(progress); this.LastProgressSequence = progress.Sequence; }
    public void RecordPruneProgress(PruneProgressEvent progress) { this.AssertUsable(); if (this.State is not (ProtocolSessionState.Pruning or ProtocolSessionState.PruneCancellationRequested)) throw new ProtocolException($"Prune progress can't be recorded while the session is in state '{this.State}'."); if (!this.PruneStarted) throw new ProtocolException("Prune progress can't be recorded for a plan cancelled before execution began."); this.RequireCurrentPruneBinding(progress.SessionId, progress.PrunePlanId, progress.PruneDigest); this.RequireActiveCommand(progress.CommandId); this.RequireNextSequence(progress.Sequence); ProtocolJsonSerializer.SerializeLine(progress); this.LastProgressSequence = progress.Sequence; }

    public void Complete(SuccessEvent result) { if (result.Operation != this.CurrentOperation) throw new ProtocolException("The success event operation doesn't match the current plan."); this.CompleteTerminal(result, result.SessionId, result.PlanId, result.PlanDigest, true); }
    public void Complete(RolledBackFailureEvent result) => this.CompleteTerminal(result, result.SessionId, result.PlanId, result.PlanDigest, true);
    public void Complete(RecoverableInterruptionEvent result) => this.CompleteTerminal(result, result.SessionId, result.PlanId, result.PlanDigest, true);
    public void Complete(CancelledEvent result) { this.AssertUsable(); this.RequireState(ProtocolSessionState.CancellationRequested); this.RequireCurrentBinding(result.SessionId, result.PlanId, result.PlanDigest); this.RequireActiveCommand(result.CommandId); if (!this.ExecutionStartedForCurrentPlan && result.Outcome != ProtocolExecutionOutcome.CancelledBeforeMutation) throw new ProtocolException("A pre-execution cancellation can't report rollback or mutation."); this.RequireExecutionSummary(result.ExecutionSummary); ProtocolJsonSerializer.SerializeLine(result); this.State = ProtocolSessionState.Completed; }
    public void Complete(PruneSuccessEvent result) { this.RequirePruneTerminalState(true); this.RequireCurrentPruneBinding(result.SessionId, result.PrunePlanId, result.PruneDigest); this.RequireActiveCommand(result.CommandId); this.RequireExactAppliedPruneCounts(result.PruneSummary); if (result.PruneSummary.PendingCleanupGenerationCount != 0 || result.PruneSummary.AuxiliaryCleanupPending != false) throw new ProtocolException("A successful prune can't report pending cleanup."); ProtocolJsonSerializer.SerializeLine(result); this.State = ProtocolSessionState.Completed; }
    public void Complete(PruneFailureEvent result) { this.RequirePruneTerminalState(true); this.RequireCurrentPruneBinding(result.SessionId, result.PrunePlanId, result.PruneDigest); this.RequireActiveCommand(result.CommandId); this.RequirePruneCounts(result.PruneSummary.LogicallyRemovedGenerationCount, result.PruneSummary.PhysicallyCleanedGenerationCount); if (result.Outcome == ProtocolPruneOutcome.FailedBeforePublication && (result.PruneSummary.LogicallyRemovedGenerationCount != 0 || result.PruneSummary.PhysicallyCleanedGenerationCount != 0)) throw new ProtocolException("A prune which failed before publication can't report applied work."); if (result.Outcome == ProtocolPruneOutcome.FailedAfterApply) this.RequireExactAppliedPruneCounts(result.PruneSummary); ProtocolJsonSerializer.SerializeLine(result); this.State = ProtocolSessionState.Completed; }
    public void Complete(PruneInterruptionEvent result) { this.RequirePruneTerminalState(true); this.RequireCurrentPruneBinding(result.SessionId, result.PrunePlanId, result.PruneDigest); this.RequireActiveCommand(result.CommandId); this.RequirePruneCounts(result.PruneSummary.LogicallyRemovedGenerationCount, result.PruneSummary.PhysicallyCleanedGenerationCount); ProtocolJsonSerializer.SerializeLine(result); this.State = ProtocolSessionState.Completed; }
    public void Complete(PruneCancelledEvent result) { this.AssertUsable(); this.RequireState(ProtocolSessionState.PruneCancellationRequested); this.RequireCurrentPruneBinding(result.SessionId, result.PrunePlanId, result.PruneDigest); this.RequireActiveCommand(result.CommandId); this.RequirePruneCounts(result.PruneSummary.LogicallyRemovedGenerationCount, result.PruneSummary.PhysicallyCleanedGenerationCount); if (result.Outcome == ProtocolPruneOutcome.CancelledBeforePublication && (result.PruneSummary.LogicallyRemovedGenerationCount != 0 || result.PruneSummary.PhysicallyCleanedGenerationCount != 0)) throw new ProtocolException("A prune cancelled before publication can't report applied work."); if (result.Outcome == ProtocolPruneOutcome.CancelledAfterApply) this.RequireExactAppliedPruneCounts(result.PruneSummary); if (!this.PruneStarted && (result.Outcome != ProtocolPruneOutcome.CancelledBeforePublication || result.PruneSummary.LogicallyRemovedGenerationCount != 0 || result.PruneSummary.PhysicallyCleanedGenerationCount != 0 || result.PruneSummary.PendingCleanupGenerationCount != 0 || result.PruneSummary.AuxiliaryCleanupPending != false || result.TerminalState.RecoveryDisposition != ProtocolRecoveryDisposition.NotRequired)) throw new ProtocolException("A pre-execution prune cancellation can't report publication, cleanup, or recovery work."); ProtocolJsonSerializer.SerializeLine(result); this.State = ProtocolSessionState.Completed; }

    public void RecordPrePlanRejection(PrePlanRejectedEvent error) { this.AssertUsable(); if (this.State is ProtocolSessionState.AwaitingHandshake or ProtocolSessionState.Recovering or ProtocolSessionState.RecoveryRequired or ProtocolSessionState.Executing or ProtocolSessionState.CancellationRequested or ProtocolSessionState.Pruning or ProtocolSessionState.PruneCancellationRequested or ProtocolSessionState.Completed) throw new ProtocolException($"A pre-plan rejection can't be recorded while the session is in state '{this.State}'."); this.RequireSession(error.SessionId); ProtocolJsonSerializer.SerializeLine(error); if (error.IsTerminal) this.State = ProtocolSessionState.Completed; }

    public void Dispose()
    {
        if (this.Disposed) return; this.Disposed = true;
        this.CurrentInspection?.Dispose();
        foreach (IDisposable owner in this.Packages.Values.Select(value => value.Owner).OfType<IDisposable>().Distinct()) owner.Dispose();
        foreach (IDisposable recovery in this.Recoveries.Values.Select(value => value.Handle).OfType<IDisposable>().Distinct()) recovery.Dispose();
        this.Packages.Clear(); this.Catalogs.Clear(); this.Recoveries.Clear(); this.Candidates.Clear();
        this.AcceptedCommands.Clear();
    }

    private PlanEvent SetCurrentPlan(ProtocolCommandId commandId, InspectedInstallationState inspection, ProtocolPackageId? packageId, ProtocolRecoveryAuthority? recovery)
    {
        if (inspection.RecoveryCapacity != inspection.Plan.RecoveryCapacity)
            throw new ProtocolException("The inspected recovery capacity doesn't match the exact plan.");
        bool capacityReached = inspection.Plan.Conflicts.Any(conflict => conflict.Code == PlanConflictCode.RecoveryCapacityReached);
        if (capacityReached != !inspection.RecoveryCapacity.CanCreateGeneration)
            throw new ProtocolException("The plan recovery-capacity conflict doesn't match the exact observed capacity.");
        InstallerOperation operation = ToProtocol(inspection.Action); ProtocolGameRootIdentity root = ToProtocol(inspection.GameRoot, inspection.OperationGeneration);
        ProtocolPlanOperation[] operations = inspection.Plan.Operations.Select(item => new ProtocolPlanOperation(item.Kind, item.Path.Value, item.ExpectedCurrentSha256?.Value, item.ResultSha256?.Value)).ToArray();
        ProtocolPlanConflict[] conflicts = inspection.Plan.Conflicts.Select(item => new ProtocolPlanConflict(item.Code, item.Path?.Value)).ToArray();
        HashSet<string> approved = inspection.ModifiedFileReplacementApprovals.Select(item => item.Path.Value).ToHashSet(StringComparer.Ordinal); Dictionary<ProtocolCandidateId, ModifiedFileReplacementCandidate> candidateAuthorities = [];
        ProtocolPlanCandidate[] candidates = inspection.ModifiedFileReplacementCandidates.Select(candidate =>
        {
            ProtocolCandidateId id; do id = ProtocolCandidateId.CreateRandom(); while (candidateAuthorities.ContainsKey(id)); candidateAuthorities.Add(id, candidate);
            return new ProtocolPlanCandidate(id, candidate.Reason, candidate.Disposition, candidate.Path.Value, candidate.ObservedSha256.Value, candidate.ObservedSizeBytes, candidate.ObservedUnixMode, candidate.ProposedResultSha256?.Value, approved.Contains(candidate.Path.Value), "Core observed this exact file identity and minted the displayed reason, disposition, and proposed result.");
        }).ToArray();
        string summary = inspection.Plan.CanExecute ? $"{operation} is ready for confirmation." : $"{operation} is blocked by {conflicts.Length} observed conflict(s).";
        string[] warnings = conflicts.Select(conflict => conflict.Path is null ? $"{conflict.Code}." : $"{conflict.Code}: {conflict.Path}.").ToArray();
        ProtocolPlanDigest execution = ProtocolPlanDigest.Parse(inspection.ConfirmationDigest.Value); ProtocolPlanId id = ProtocolPlanId.CreateRandom();
        RecoveryCapacityState capacity = inspection.RecoveryCapacity;
        ProtocolPlanDigest digest = ProtocolPlanDigest.Compute(execution, operation, packageId, recovery, root, ToProtocol(inspection.CurrentRelease), ToProtocol(inspection.ExpectedResultRelease), ToProtocol(inspection.ObservedState), capacity.UsedGenerationCount, capacity.MaximumGenerationCount, operations, conflicts, candidates, summary, warnings, true);
        ProtocolPlanRisk[] risks = GetPlanRisks(operation, ToProtocol(inspection.CurrentRelease), ToProtocol(inspection.ExpectedResultRelease), candidates);
        PlanEvent result = new PlanEvent(this.SessionId, id, digest, execution, operation, packageId, recovery, root, ToProtocol(inspection.CurrentRelease), ToProtocol(inspection.ExpectedResultRelease), ToProtocol(inspection.ObservedState), capacity.UsedGenerationCount, capacity.MaximumGenerationCount, operations.Length, conflicts.Length, candidates.Length, warnings.Length, inspection.Plan.CanExecute, risks, ProtocolRecommendedDefault.Cancel, summary, true) { CommandId = commandId }.AttachPageData(operations, conflicts, candidates, warnings); ProtocolJsonSerializer.SerializeLine(result);
        this.CurrentInspection?.Dispose(); this.CurrentInspection = inspection; this.CurrentPlan = result; this.CurrentOperations = operations; this.CurrentConflicts = conflicts; this.CurrentCandidates = candidates; this.CurrentWarnings = warnings; this.Candidates.Clear(); foreach ((ProtocolCandidateId candidateId, ModifiedFileReplacementCandidate candidate) in candidateAuthorities) this.Candidates.Add(candidateId, candidate); this.CurrentPlanCanExecute = inspection.Plan.CanExecute; this.CurrentPrunePlan = null; this.CurrentCorePrunePlan = null; this.ExecutionStartedForCurrentPlan = false; this.LastProgressSequence = -1; this.State = ProtocolSessionState.PlanIssued; return result;
    }

    private ProtocolPackageId? ResolvePackageAuthority(ProtocolPackageId? requested, IVerifiedPackageContentAuthority? inspected, object? authorityIdentity, InstallerOperation operation)
    {
        if (operation is InstallerOperation.Install or InstallerOperation.Update or InstallerOperation.Repair)
        {
            if (requested is not { } id || inspected is null || authorityIdentity is null || !this.Packages.TryGetValue(id, out PackageAuthority? package) || !ReferenceEquals(package.Identity, authorityIdentity)) throw new ProtocolException("The package ID is unknown, stale, or doesn't own the inspection's exact verified content authority.");
            inspected.AssertUsable(); return id;
        }
        if (requested is not null || inspected is not null) throw new ProtocolException("This operation must not carry package authority."); return null;
    }

    private ProtocolRecoveryAuthority? ResolveRecoveryAuthority(ProtocolRecoverySelectionId? requested, ICommittedRecoveryContentAuthority? inspected, GameRootIdentity root, ulong operationGeneration, InstallerOperation operation)
    {
        if (operation != InstallerOperation.Rollback) { if (requested is not null || inspected is not null) throw new ProtocolException("This operation must not carry recovery authority."); return null; }
        if (requested is not { } id || inspected is null || !this.Recoveries.TryGetValue(id, out RecoverySelectionAuthority? selection) || !ReferenceEquals(selection.Handle, inspected)) throw new ProtocolException("The recovery selection is unknown, stale, or doesn't own the inspection's exact committed handle.");
        inspected.AssertUsable(); RecoveryCatalogEvent catalog = selection.Catalog.Event;
        if (selection.Catalog.GameRoot != root || inspected.GameRoot != root || inspected.AuthorizedHeadPointerSha256.Value != catalog.HeadSha256 || inspected.GenerationId.ToString("N") != selection.Generation.GenerationId)
            throw new ProtocolException("The rollback inspection doesn't match the selected catalog root, head, and generation authority.");
        return new ProtocolRecoveryAuthority(catalog.CatalogId, id, ToProtocol(root, operationGeneration), catalog.HeadSha256, selection.Generation);
    }

    private void RemoveCatalog(ProtocolRecoveryCatalogId id) { RecoveryCatalogAuthority catalog = this.Catalogs[id]; foreach (ProtocolRecoverySelectionId selection in catalog.Event.Generations.Select(item => item.SelectionId)) { if (this.Recoveries.Remove(selection, out RecoverySelectionAuthority? authority) && authority.Handle is IDisposable disposable) disposable.Dispose(); } this.Catalogs.Remove(id); }
    private void CompleteTerminal(ProtocolEvent result, ProtocolSessionId session, ProtocolPlanId plan, ProtocolPlanDigest digest, bool allowCancellation) { this.AssertUsable(); if (this.State != ProtocolSessionState.Executing && !(allowCancellation && this.State == ProtocolSessionState.CancellationRequested && this.ExecutionStartedForCurrentPlan)) throw new ProtocolException($"A terminal event can't be recorded while the session is in state '{this.State}'."); this.RequireCurrentBinding(session, plan, digest); this.RequireActiveCommand(result.CommandId); this.RequireExecutionSummary(result switch { SuccessEvent value => value.ExecutionSummary, RolledBackFailureEvent value => value.ExecutionSummary, RecoverableInterruptionEvent value => value.ExecutionSummary, _ => throw new ProtocolException("The execution terminal family is invalid.") }); ProtocolJsonSerializer.SerializeLine(result); this.State = ProtocolSessionState.Completed; }
    private void RequireExecutionSummary(ProtocolExecutionSummary summary) { if (summary.ManagedFileChangeCount is { } managed && managed > this.CurrentPlan!.OperationCount) throw new ProtocolException("The execution terminal managed-file count exceeds the confirmed plan operation count."); }
    private void RequirePruneTerminalState(bool allowCancellation) { this.AssertUsable(); if (this.State != ProtocolSessionState.Pruning && !(allowCancellation && this.State == ProtocolSessionState.PruneCancellationRequested)) throw new ProtocolException($"A prune terminal event can't be recorded while the session is in state '{this.State}'."); }
    private void RequireNextSequence(long sequence) { if (sequence <= this.LastProgressSequence) throw new ProtocolException("Progress sequence values must increase monotonically."); }
    private void RequireActiveCommand(ProtocolCommandId commandId) { if (this.ActiveCommandId != commandId) throw new ProtocolException("The progress or terminal doesn't match the active long-operation command ID."); }
    private void RequireExactAppliedPruneCounts(ProtocolPruneSummary summary) { if (summary.LogicallyRemovedGenerationCount != this.CurrentPrunePlan!.RemovedSelectionIds.Length || summary.PhysicallyCleanedGenerationCount != this.CurrentPrunePlan.CleanupGenerationIds.Length) throw new ProtocolException("The after-apply prune logical-removal and physical-cleanup counts must exactly match the confirmed plan."); }
    private void RequirePruneCounts(int? logicalRemoved, int? physicalCleanup) { if (logicalRemoved is null && physicalCleanup is null) return; if (logicalRemoved is null || physicalCleanup is null) throw new ProtocolException("A prune terminal event can't mix known and unknown counts."); int expectedLogical = this.CurrentPrunePlan!.RemovedSelectionIds.Length; if (logicalRemoved is not 0 && logicalRemoved != expectedLogical) throw new ProtocolException("A prune terminal event must report either zero or the exact confirmed logical removals."); if (physicalCleanup > this.CurrentPrunePlan.CleanupGenerationIds.Length) throw new ProtocolException("A prune terminal event can't report more physical cleanup than the confirmed plan."); }
    private RecoveryCatalogAuthority RequireExactCorePrunePlan(InspectPruneRequest request, RecoveryPrunePlan corePlan) { if (!this.Catalogs.TryGetValue(request.CatalogId, out RecoveryCatalogAuthority? catalog)) throw new ProtocolException("The recovery catalog ID is unknown or stale."); if (request.RetainNewest != corePlan.RetainNewest || corePlan.RetainNewest is < 1 or > ProtocolJsonSerializer.MaxRecoveryGenerations || corePlan.GameRoot != catalog.GameRoot || corePlan.HeadPointerSha256.Value != catalog.Event.HeadSha256) throw new ProtocolException("The core prune plan doesn't match the exact stored catalog root, head, and retention request."); Guid[] catalogIds = catalog.History.Generations.Select(info => info.GenerationId).ToArray(); if (!corePlan.OrderedCatalogGenerationIds.SequenceEqual(catalogIds)) throw new ProtocolException("The core prune plan doesn't match the exact stored catalog order."); return catalog; }
    private void RequireCanIssuePlan() { if (this.State is not (ProtocolSessionState.Ready or ProtocolSessionState.PlanIssued or ProtocolSessionState.PlanConfirmed or ProtocolSessionState.PrunePlanIssued or ProtocolSessionState.PrunePlanConfirmed)) throw new ProtocolException($"A plan can't be issued while the session is in state '{this.State}'."); }
    private void RequireReadyLookup(ProtocolSessionId session) { this.RequireState(ProtocolSessionState.Ready); this.RequireSession(session); }
    private void RequireCurrentBinding(ProtocolSessionId session, ProtocolPlanId plan, ProtocolPlanDigest digest) { this.RequireSession(session); if (this.CurrentPlan?.PlanId != plan) throw new ProtocolException("The request or event doesn't match the current plan ID; it may be stale."); if (this.CurrentPlan.PlanDigest != digest) throw new ProtocolException("The request or event doesn't match the current execution-plan digest; it may be stale or altered."); }
    private void RequireCurrentPruneBinding(ProtocolSessionId session, ProtocolPrunePlanId plan, ProtocolPlanDigest digest) { this.RequireSession(session); if (this.CurrentPrunePlan?.PrunePlanId != plan) throw new ProtocolException("The request or event doesn't match the current prune plan ID; it may be stale."); if (this.CurrentPrunePlan.PruneDigest != digest) throw new ProtocolException("The request or event doesn't match the current prune digest; it may be stale or altered."); }
    private void RequireSession(ProtocolSessionId session) { if (session != this.SessionId) throw new ProtocolException("The request or event doesn't match this process session ID."); }
    private void RequireState(ProtocolSessionState expected) { if (this.State != expected) throw new ProtocolException($"Expected protocol state '{expected}', but the session is in state '{this.State}'."); }
    private void InvalidateCurrentPlan() { this.CurrentInspection?.Dispose(); this.CurrentInspection = null; this.CurrentPlan = null; this.CurrentOperations = []; this.CurrentConflicts = []; this.CurrentCandidates = []; this.CurrentWarnings = []; this.Candidates.Clear(); this.CurrentPlanCanExecute = false; this.ActiveCommandId = null; this.LastProgressSequence = -1; }
    private void AssertUsable() { if (this.Disposed) throw new ObjectDisposedException(nameof(ProtocolSessionStateMachine)); }

    private static InstallerOperation ToProtocol(InstallationAction action) => (InstallerOperation)(int)action;
    private static ProtocolGameRootIdentity ToProtocol(GameRootIdentity root, ulong operationGeneration) => new(root.CanonicalPath, root.DeviceMajor, root.DeviceMinor, root.Inode, operationGeneration);
    private static ObservedInstallState ToProtocol(ObservedInstallationState state) => (ObservedInstallState)(int)state;
    private static ProtocolReleaseIdentity? ToProtocol(InstallationReleaseIdentity? release) => release is null ? null : new(release.Repository, release.Tag, release.EmbeddedVersion, release.PackageAssetName, release.SourceCommit, release.SourceTree, release.PackageSha256.Value, release.PackageSizeBytes, release.BuildWorkflow, release.BuildConfiguration, release.RuntimeIdentifier);

    private static ProtocolPlanRisk[] GetPlanRisks(InstallerOperation operation, ProtocolReleaseIdentity? current, ProtocolReleaseIdentity? target, ProtocolPlanCandidate[] candidates)
    {
        List<ProtocolPlanRisk> risks = [];
        if (operation == InstallerOperation.Uninstall) risks.Add(ProtocolPlanRisk.Uninstall);
        if (operation == InstallerOperation.Rollback) risks.Add(ProtocolPlanRisk.Rollback);
        if (
            current is not null
            && target is not null
            && ForkReleaseIdentity.Compare(ForkReleaseIdentity.Parse(target.Tag), ForkReleaseIdentity.Parse(current.Tag)) < 0
        )
            risks.Add(ProtocolPlanRisk.Downgrade);
        if (candidates.Length > 0) risks.Add(ProtocolPlanRisk.ModifiedOrUnknownFileApproval);
        return risks.ToArray();
    }

    private sealed record PackageAuthority(object Identity, IVerifiedPackageContentAuthority Authority, InstallationReleaseIdentity Release, IDisposable? Owner);
    private sealed record RecoveryCatalogAuthority(RecoveryCatalogEvent Event, GameRootIdentity GameRoot, RecoveryHistory History, ICommittedRecoveryContentAuthority[] Handles);
    private sealed record RecoverySelectionAuthority(RecoveryCatalogAuthority Catalog, ProtocolRecoveryGeneration Generation, ICommittedRecoveryContentAuthority Handle);
}
