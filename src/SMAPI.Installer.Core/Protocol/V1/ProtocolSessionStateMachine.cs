namespace StardewModdingAPI.Installer.Core.Protocol.V1;

public enum ProtocolSessionState
{
    AwaitingHandshake,
    Ready,
    PlanIssued,
    PlanConfirmed,
    Executing,
    CancellationRequested,
    PrunePlanIssued,
    PrunePlanConfirmed,
    Pruning,
    Completed
}

/// <summary>Validates ordering and owns every opaque authority exposed during one backend process.</summary>
public sealed class ProtocolSessionStateMachine
{
    private readonly Dictionary<ProtocolPackageId, ProtocolReleaseIdentity> Packages = [];
    private readonly Dictionary<ProtocolRecoveryCatalogId, RecoveryCatalogEvent> Catalogs = [];
    private readonly Dictionary<ProtocolRecoverySelectionId, RecoverySelection> Recoveries = [];
    private PlanEvent? CurrentPlan;
    private PrunePlanEvent? CurrentPrunePlan;
    private bool ExecutionStartedForCurrentPlan;

    public ProtocolSessionId SessionId { get; } = ProtocolSessionId.CreateRandom();
    public ProtocolSessionState State { get; private set; } = ProtocolSessionState.AwaitingHandshake;
    public ProtocolPlanId? CurrentPlanId => this.CurrentPlan?.PlanId;
    public InstallerOperation? CurrentOperation => this.CurrentPlan?.Operation;
    public ProtocolPlanDigest? CurrentPlanDigest => this.CurrentPlan?.PlanDigest;
    public bool CurrentPlanCanExecute { get; private set; }
    public long LastProgressSequence { get; private set; } = -1;

    public HandshakeEvent AcceptHandshake(HandshakeRequest request, string serverVersion, params string[] capabilities)
    {
        ArgumentNullException.ThrowIfNull(request); this.RequireState(ProtocolSessionState.AwaitingHandshake);
        ProtocolJsonSerializer.SerializeLine(request);
        HandshakeEvent response = new(this.SessionId, serverVersion, capabilities ?? []);
        ProtocolJsonSerializer.SerializeLine(response); this.State = ProtocolSessionState.Ready; return response;
    }

    public GameDiscoveryEvent RecordDiscovery(DiscoverGamesRequest request, ProtocolGameCandidate[] candidates)
    {
        this.RequireReadyLookup(request.SessionId); ProtocolJsonSerializer.SerializeLine(request);
        GameDiscoveryEvent result = new(this.SessionId, candidates?.ToArray() ?? throw new ProtocolException("Candidates can't be null."));
        ProtocolJsonSerializer.SerializeLine(result); return result;
    }

    public PackageOpenedEvent OpenPackage(OpenPackageRequest request, ProtocolReleaseIdentity verifiedRelease)
    {
        this.RequireReadyLookup(request.SessionId); ProtocolJsonSerializer.SerializeLine(request);
        if (this.Packages.Count >= ProtocolJsonSerializer.MaxPackages) throw new ProtocolException("The session package registry is full.");
        if (verifiedRelease.Tag != request.ReleaseTag || verifiedRelease.SourceCommit != request.ExpectedSourceCommit)
            throw new ProtocolException("The verified package release identity doesn't match the requested tag and source commit.");
        ProtocolPackageId id; do id = ProtocolPackageId.CreateRandom(); while (this.Packages.ContainsKey(id));
        PackageOpenedEvent result = new(this.SessionId, id, verifiedRelease); ProtocolJsonSerializer.SerializeLine(result);
        this.Packages.Add(id, verifiedRelease); return result;
    }

    public RecoveryCatalogEvent RecordRecoveryCatalog(
        ListRecoveriesRequest request,
        ProtocolGameRootIdentity gameRoot,
        string headSha256,
        ProtocolRecoveryGenerationSource[] generations
    )
    {
        this.RequireReadyLookup(request.SessionId); ProtocolJsonSerializer.SerializeLine(request);
        if (request.GamePath != gameRoot.CanonicalPath) throw new ProtocolException("The recovery catalog game root doesn't match the requested path.");
        ProtocolRecoveryGenerationSource[] sources = generations?.ToArray() ?? throw new ProtocolException("Recovery generations can't be null.");
        if (sources.Length > ProtocolJsonSerializer.MaxRecoveryGenerations) throw new ProtocolException("The recovery catalog is too large.");
        foreach (ProtocolRecoveryCatalogId old in this.Catalogs.Where(p => p.Value.GameRoot.CanonicalPath == gameRoot.CanonicalPath).Select(p => p.Key).ToArray())
        {
            foreach (ProtocolRecoverySelectionId selection in this.Catalogs[old].Generations.Select(p => p.SelectionId)) this.Recoveries.Remove(selection);
            this.Catalogs.Remove(old);
        }
        if (this.Catalogs.Count >= ProtocolJsonSerializer.MaxRecoveryCatalogs)
            throw new ProtocolException("The session recovery-catalog registry is full.");
        ProtocolRecoveryCatalogId catalogId; do catalogId = ProtocolRecoveryCatalogId.CreateRandom(); while (this.Catalogs.ContainsKey(catalogId));
        HashSet<string> sourceIds = new(StringComparer.Ordinal); HashSet<ProtocolRecoverySelectionId> minted = [];
        ProtocolRecoveryGeneration[] entries = sources.Select(source =>
        {
            if (!sourceIds.Add(source.GenerationId)) throw new ProtocolException("Recovery generation IDs must be unique.");
            ProtocolRecoverySelectionId selection; do selection = ProtocolRecoverySelectionId.CreateRandom(); while (this.Recoveries.ContainsKey(selection) || !minted.Add(selection));
            return new ProtocolRecoveryGeneration(selection, source.GenerationId, source.OriginOperation, source.IsCurrent, source.IsUserCheckpoint);
        }).ToArray();
        RecoveryCatalogEvent result = new(this.SessionId, catalogId, gameRoot, headSha256, entries); ProtocolJsonSerializer.SerializeLine(result);
        this.Catalogs.Add(catalogId, result);
        foreach (ProtocolRecoveryGeneration entry in entries) this.Recoveries.Add(entry.SelectionId, new(catalogId, gameRoot.CanonicalPath, entry));
        return result;
    }

    public PlanEvent IssuePlan(
        InspectPlanRequest request,
        ProtocolPlanDigest executionBindingDigest,
        ProtocolGameRootIdentity gameRoot,
        ProtocolReleaseIdentity? currentRelease,
        ObservedInstallState observedState,
        ProtocolPlanOperation[] operations,
        ProtocolPlanConflict[] conflicts,
        ProtocolPlanCandidateSource[] candidates,
        string summary,
        string[] warnings
    )
    {
        this.RequireCanIssuePlan(); this.RequireSession(request.SessionId); ProtocolJsonSerializer.SerializeLine(request);
        ProtocolReleaseIdentity? targetRelease = null;
        if (request.PackageId is { } packageId)
        {
            if (!this.Packages.TryGetValue(packageId, out targetRelease)) throw new ProtocolException("The package ID is unknown or stale.");
        }
        if (request.RecoverySelectionId is { } recoveryId)
        {
            if (!this.Recoveries.TryGetValue(recoveryId, out RecoverySelection? recovery)) throw new ProtocolException("The recovery selection ID is unknown or stale.");
            if (recovery.GamePath != gameRoot.CanonicalPath || request.GamePath != recovery.GamePath) throw new ProtocolException("The recovery selection belongs to a different game root.");
        }
        if (request.GamePath != gameRoot.CanonicalPath) throw new ProtocolException("The inspected game root doesn't match the requested path.");
        ProtocolPlanCandidate[] minted = this.MintCandidates(candidates);
        return this.SetCurrentPlan(request.Operation, request.PackageId, request.RecoverySelectionId, executionBindingDigest, gameRoot, currentRelease, targetRelease, observedState, operations, conflicts, minted, summary, warnings);
    }

    /// <summary>Apply an exact candidate selection and issue a newly-bound plan, invalidating the old ID and digest.</summary>
    public PlanEvent SelectCandidates(
        SelectPlanCandidatesRequest request,
        ProtocolPlanDigest executionBindingDigest,
        ProtocolPlanOperation[] operations,
        ProtocolPlanConflict[] conflicts,
        string summary,
        string[] warnings
    )
    {
        ArgumentNullException.ThrowIfNull(request); this.RequireState(ProtocolSessionState.PlanIssued); this.RequireCurrentBinding(request.SessionId, request.PlanId, request.PlanDigest); ProtocolJsonSerializer.SerializeLine(request);
        PlanEvent old = this.CurrentPlan!; HashSet<ProtocolCandidateId> known = old.Candidates.Select(p => p.CandidateId).ToHashSet();
        foreach (ProtocolCandidateId selected in request.SelectedCandidateIds) if (!known.Contains(selected)) throw new ProtocolException("The candidate ID is unknown or stale.");
        HashSet<ProtocolCandidateId> selectedSet = request.SelectedCandidateIds.ToHashSet();
        ProtocolPlanCandidate[] candidates = old.Candidates.Select(p => p with { Selected = selectedSet.Contains(p.CandidateId) }).ToArray();
        return this.SetCurrentPlan(old.Operation, old.PackageId, old.RecoverySelectionId, executionBindingDigest, old.GameRoot, old.CurrentRelease, old.TargetRelease, old.ObservedState, operations, conflicts, candidates, summary, warnings);
    }

    public PrunePlanEvent IssuePrunePlan(InspectPruneRequest request, ProtocolPlanDigest executionBindingDigest, string summary, string[] warnings)
    {
        this.RequireCanIssuePlan(); this.RequireSession(request.SessionId); ProtocolJsonSerializer.SerializeLine(request);
        if (!this.Catalogs.TryGetValue(request.CatalogId, out RecoveryCatalogEvent? catalog)) throw new ProtocolException("The recovery catalog ID is unknown or stale.");
        ProtocolRecoverySelectionId[] all = catalog.Generations.Select(p => p.SelectionId).ToArray();
        int retainCount = Math.Min(request.RetainNewest, all.Length);
        ProtocolRecoverySelectionId[] retained = all.Take(retainCount).ToArray(); ProtocolRecoverySelectionId[] removed = all.Skip(retainCount).ToArray();
        ProtocolPrunePlanId id = ProtocolPrunePlanId.CreateRandom();
        ProtocolPlanDigest digest = ProtocolPlanDigest.ComputePrune(executionBindingDigest, request.CatalogId, catalog.GameRoot, catalog.HeadSha256, request.RetainNewest, retained, removed, summary, warnings, true);
        PrunePlanEvent result = new(this.SessionId, id, digest, executionBindingDigest, request.CatalogId, catalog.GameRoot, catalog.HeadSha256, request.RetainNewest, retained, removed, summary, warnings, true);
        ProtocolJsonSerializer.SerializeLine(result); this.InvalidateCurrentPlan(); this.CurrentPrunePlan = result; this.State = ProtocolSessionState.PrunePlanIssued; return result;
    }

    public void ConfirmPlan(ConfirmPlanRequest request)
    {
        this.RequireState(ProtocolSessionState.PlanIssued); if (!this.CurrentPlanCanExecute) throw new ProtocolException("A plan with unresolved conflicts can't be confirmed."); this.RequireCurrentBinding(request.SessionId, request.PlanId, request.PlanDigest); ProtocolJsonSerializer.SerializeLine(request); this.State = ProtocolSessionState.PlanConfirmed;
    }
    public void BeginExecution(ExecutePlanRequest request)
    {
        if (this.State == ProtocolSessionState.PlanIssued) throw new ProtocolException("The current plan must be confirmed before execution."); this.RequireState(ProtocolSessionState.PlanConfirmed); this.RequireCurrentBinding(request.SessionId, request.PlanId, request.PlanDigest); ProtocolJsonSerializer.SerializeLine(request); this.State = ProtocolSessionState.Executing; this.ExecutionStartedForCurrentPlan = true;
    }
    public void ConfirmPrune(ConfirmPruneRequest request) { this.RequireState(ProtocolSessionState.PrunePlanIssued); this.RequireCurrentPruneBinding(request.SessionId, request.PrunePlanId, request.PruneDigest); ProtocolJsonSerializer.SerializeLine(request); this.State = ProtocolSessionState.PrunePlanConfirmed; }
    public void BeginPrune(ExecutePruneRequest request) { if (this.State == ProtocolSessionState.PrunePlanIssued) throw new ProtocolException("The prune plan must be confirmed before execution."); this.RequireState(ProtocolSessionState.PrunePlanConfirmed); this.RequireCurrentPruneBinding(request.SessionId, request.PrunePlanId, request.PruneDigest); ProtocolJsonSerializer.SerializeLine(request); this.State = ProtocolSessionState.Pruning; }

    public void RequestCancellation(CancelPlanRequest request)
    {
        if (this.State is not (ProtocolSessionState.PlanIssued or ProtocolSessionState.PlanConfirmed or ProtocolSessionState.Executing)) throw new ProtocolException($"Cancellation can't be requested while the session is in state '{this.State}'."); this.RequireCurrentBinding(request.SessionId, request.PlanId, request.PlanDigest); ProtocolJsonSerializer.SerializeLine(request); this.State = ProtocolSessionState.CancellationRequested;
    }
    public void RecordProgress(ProgressEvent progress)
    {
        if (this.State is not (ProtocolSessionState.Executing or ProtocolSessionState.CancellationRequested)) throw new ProtocolException($"Progress can't be recorded while the session is in state '{this.State}'."); if (!this.ExecutionStartedForCurrentPlan) throw new ProtocolException("Progress can't be recorded for a plan cancelled before execution began."); this.RequireCurrentBinding(progress.SessionId, progress.PlanId, progress.PlanDigest); ProtocolJsonSerializer.SerializeLine(progress); if (progress.Sequence <= this.LastProgressSequence) throw new ProtocolException("Progress sequence values must increase monotonically."); this.LastProgressSequence = progress.Sequence;
    }

    public void Complete(SuccessEvent result) { if (result.Operation != this.CurrentOperation) throw new ProtocolException("The success event operation doesn't match the current plan."); this.CompleteTerminal(result, result.SessionId, result.PlanId, result.PlanDigest, false); }
    public void Complete(RolledBackFailureEvent result) => this.CompleteTerminal(result, result.SessionId, result.PlanId, result.PlanDigest, true);
    public void Complete(RecoverableInterruptionEvent result) => this.CompleteTerminal(result, result.SessionId, result.PlanId, result.PlanDigest, true);
    public void Complete(CancelledEvent result) { this.RequireState(ProtocolSessionState.CancellationRequested); this.RequireCurrentBinding(result.SessionId, result.PlanId, result.PlanDigest); ProtocolJsonSerializer.SerializeLine(result); this.State = ProtocolSessionState.Completed; }
    public void Complete(PruneSuccessEvent result) { this.RequireState(ProtocolSessionState.Pruning); this.RequireCurrentPruneBinding(result.SessionId, result.PrunePlanId, result.PruneDigest); if (result.RemovedGenerationCount != this.CurrentPrunePlan!.RemovedSelectionIds.Length) throw new ProtocolException("The prune result count doesn't match the confirmed plan."); ProtocolJsonSerializer.SerializeLine(result); this.State = ProtocolSessionState.Completed; }

    /// <summary>Record a bounded error before execution; terminal errors end this one-process session.</summary>
    public void RecordPrePlanError(PrePlanErrorEvent error)
    {
        ArgumentNullException.ThrowIfNull(error); if (this.State is ProtocolSessionState.AwaitingHandshake or ProtocolSessionState.Executing or ProtocolSessionState.CancellationRequested or ProtocolSessionState.Pruning or ProtocolSessionState.Completed) throw new ProtocolException($"A pre-plan error can't be recorded while the session is in state '{this.State}'."); this.RequireSession(error.SessionId); ProtocolJsonSerializer.SerializeLine(error); if (error.IsTerminal) this.State = ProtocolSessionState.Completed;
    }

    private ProtocolPlanCandidate[] MintCandidates(ProtocolPlanCandidateSource[] sources)
    {
        if (sources is null || sources.Length > ProtocolJsonSerializer.MaxPlanCandidates) throw new ProtocolException("The candidate collection is missing or too large.");
        HashSet<ProtocolCandidateId> ids = [];
        return sources.Select(source => { ProtocolCandidateId id; do id = ProtocolCandidateId.CreateRandom(); while (!ids.Add(id)); return new ProtocolPlanCandidate(id, source.Kind, source.Path, source.ObservedSha256, source.ObservedSizeBytes, source.ObservedUnixMode, source.ProposedResultSha256, source.Selected, source.Evidence); }).ToArray();
    }

    private PlanEvent SetCurrentPlan(InstallerOperation operation, ProtocolPackageId? packageId, ProtocolRecoverySelectionId? recoveryId, ProtocolPlanDigest executionDigest, ProtocolGameRootIdentity root, ProtocolReleaseIdentity? current, ProtocolReleaseIdentity? target, ObservedInstallState state, ProtocolPlanOperation[] operations, ProtocolPlanConflict[] conflicts, ProtocolPlanCandidate[] candidates, string summary, string[] warnings)
    {
        ProtocolPlanOperation[] ops = operations?.ToArray() ?? throw new ProtocolException("Operations can't be null."); ProtocolPlanConflict[] blocks = conflicts?.ToArray() ?? throw new ProtocolException("Conflicts can't be null."); string[] notes = warnings?.ToArray() ?? throw new ProtocolException("Warnings can't be null.");
        ProtocolPlanId id = ProtocolPlanId.CreateRandom(); ProtocolPlanDigest digest = ProtocolPlanDigest.Compute(executionDigest, operation, packageId, recoveryId, root, current, target, state, ops, blocks, candidates, summary, notes, true);
        PlanEvent result = new(this.SessionId, id, digest, executionDigest, operation, packageId, recoveryId, root, current, target, state, ops, blocks, candidates, summary, notes, true); ProtocolJsonSerializer.SerializeLine(result);
        this.CurrentPrunePlan = null; this.CurrentPlan = result; this.CurrentPlanCanExecute = blocks.Length == 0; this.ExecutionStartedForCurrentPlan = false; this.LastProgressSequence = -1; this.State = ProtocolSessionState.PlanIssued; return result;
    }

    private void CompleteTerminal(ProtocolEvent result, ProtocolSessionId session, ProtocolPlanId plan, ProtocolPlanDigest digest, bool allowCancellation)
    {
        if (this.State != ProtocolSessionState.Executing && !(allowCancellation && this.State == ProtocolSessionState.CancellationRequested)) throw new ProtocolException($"A terminal event can't be recorded while the session is in state '{this.State}'."); this.RequireCurrentBinding(session, plan, digest); ProtocolJsonSerializer.SerializeLine(result); this.State = ProtocolSessionState.Completed;
    }
    private void RequireCanIssuePlan() { if (this.State is not (ProtocolSessionState.Ready or ProtocolSessionState.PlanIssued or ProtocolSessionState.PlanConfirmed or ProtocolSessionState.PrunePlanIssued or ProtocolSessionState.PrunePlanConfirmed)) throw new ProtocolException($"A plan can't be issued while the session is in state '{this.State}'."); }
    private void RequireReadyLookup(ProtocolSessionId session) { this.RequireState(ProtocolSessionState.Ready); this.RequireSession(session); }
    private void RequireCurrentBinding(ProtocolSessionId session, ProtocolPlanId plan, ProtocolPlanDigest digest) { this.RequireSession(session); if (this.CurrentPlan?.PlanId != plan) throw new ProtocolException("The request or event doesn't match the current plan ID; it may be stale."); if (this.CurrentPlan.PlanDigest != digest) throw new ProtocolException("The request or event doesn't match the current execution-plan digest; it may be stale or altered."); }
    private void RequireCurrentPruneBinding(ProtocolSessionId session, ProtocolPrunePlanId plan, ProtocolPlanDigest digest) { this.RequireSession(session); if (this.CurrentPrunePlan?.PrunePlanId != plan) throw new ProtocolException("The request or event doesn't match the current prune plan ID; it may be stale."); if (this.CurrentPrunePlan.PruneDigest != digest) throw new ProtocolException("The request or event doesn't match the current prune digest; it may be stale or altered."); }
    private void RequireSession(ProtocolSessionId session) { if (session != this.SessionId) throw new ProtocolException("The request or event doesn't match this process session ID."); }
    private void RequireState(ProtocolSessionState expected) { if (this.State != expected) throw new ProtocolException($"Expected protocol state '{expected}', but the session is in state '{this.State}'."); }
    private void InvalidateCurrentPlan() { this.CurrentPlan = null; this.CurrentPlanCanExecute = false; this.LastProgressSequence = -1; }
    private sealed record RecoverySelection(ProtocolRecoveryCatalogId CatalogId, string GamePath, ProtocolRecoveryGeneration Generation);
}
