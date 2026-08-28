using System.Text;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Recovery;
using StardewModdingAPI.Installer.Core.Transactions;

namespace StardewModdingAPI.Installer.Core.Protocol.V1;

/// <summary>
/// Owns one protocol session and atomically translates opaque protocol requests into Linux installer core calls.
/// Frontends never receive or maintain package, recovery, inspection, candidate, or prune authorities.
/// </summary>
public sealed class LinuxInstallerProtocolService : IDisposable, IAsyncDisposable
{
    private static readonly string[] Capabilities =
    [
        "linux-game-discovery",
        "verified-local-package",
        "install-update-repair-uninstall-backup-rollback",
        "candidate-approval",
        "recovery-pruning",
        "interrupted-operation-recovery",
        "exact-core-progress",
        "cancellation"
    ];

    private readonly object SessionLock = new();
    private readonly object OutboundLock = new();
    private readonly SemaphoreSlim CommandGate = new(1, 1);
    private readonly ProtocolSessionStateMachine Session = new();
    private readonly ILinuxInstallerProtocolEngine Engine;
    private readonly ILinuxInstallerProtocolDiscovery Discovery;
    private readonly ILinuxInstallerProtocolPackageOpener PackageOpener;
    private readonly Action<ProtocolEvent>? EventSink;
    private readonly Action? TerminalCompletionStarting;
    private readonly Action? DisposalPublished;
    private readonly string ServerVersion;
    private readonly string? SanitizedLogPath;
    private CancellationTokenSource? ActiveCancellation;
    private Task<ProtocolEvent>? ActiveOperation;
    private Task? DisposalTask;
    private bool Closing;
    private bool Disposed;

    /// <summary>Create a production service using the bounded Linux discovery, package verification, and installer engine.</summary>
    public LinuxInstallerProtocolService(string serverVersion, string? sanitizedLogPath = null, Action<ProtocolEvent>? eventSink = null)
        : this(
            serverVersion,
            progress => new LinuxInstallerProtocolEngine(new LinuxInstallerEngine(progress)),
            new LinuxInstallerProtocolDiscovery(new LinuxGameDiscovery()),
            new LinuxInstallerProtocolPackageOpener(),
            eventSink,
            sanitizedLogPath
        )
    {
    }

    internal LinuxInstallerProtocolService(
        string serverVersion,
        Func<ITransactionProgressSink, ILinuxInstallerProtocolEngine> engineFactory,
        ILinuxInstallerProtocolDiscovery discovery,
        ILinuxInstallerProtocolPackageOpener packageOpener,
        Action<ProtocolEvent>? eventSink = null,
        string? sanitizedLogPath = null,
        Action? terminalCompletionStarting = null,
        Action? disposalPublished = null
    )
    {
        if (string.IsNullOrWhiteSpace(serverVersion)) throw new ArgumentException("A bounded server version is required.", nameof(serverVersion));
        ArgumentNullException.ThrowIfNull(engineFactory);
        this.ServerVersion = serverVersion;
        this.Discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        this.PackageOpener = packageOpener ?? throw new ArgumentNullException(nameof(packageOpener));
        this.EventSink = eventSink;
        this.TerminalCompletionStarting = terminalCompletionStarting;
        this.DisposalPublished = disposalPublished;
        this.SanitizedLogPath = sanitizedLogPath;
        this.Engine = engineFactory(new CallbackProgressSink(this.RecordProgress)) ?? throw new ArgumentException("The engine factory returned null.", nameof(engineFactory));
    }

    /// <summary>The session-local identifier which every post-handshake request must carry.</summary>
    public ProtocolSessionId SessionId => this.Session.SessionId;

    /// <summary>The current strictly ordered protocol lifecycle state.</summary>
    public ProtocolSessionState State => this.Session.State;

    /// <summary>Handle one already-deserialized request. A null result means the request only changed protocol state.</summary>
    public async Task<ProtocolEvent?> HandleAsync(ProtocolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        this.AssertAcceptingRequests();
        await this.CommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool releaseGate = true;
        try
        {
            this.AssertAcceptingRequests();
            ProtocolJsonSerializer.SerializeLine(request);
            switch (request)
            {
                case HandshakeRequest value:
                    return this.Emit(this.WithSession(() => this.Session.AcceptHandshake(value, this.ServerVersion, Capabilities)));
                case DiscoverGamesRequest value:
                    return await this.DiscoverAsync(value, cancellationToken).ConfigureAwait(false);
                case RecoverInterruptedRequest value:
                    Task<ProtocolEvent> recovery = this.StartRecovery(value, cancellationToken);
                    releaseGate = false; this.CommandGate.Release();
                    return await recovery.ConfigureAwait(false);
                case OpenPackageRequest value:
                    return await this.OpenPackageAsync(value, cancellationToken).ConfigureAwait(false);
                case ListRecoveriesRequest value:
                    return await this.ListRecoveriesAsync(value, cancellationToken).ConfigureAwait(false);
                case InspectPlanRequest value:
                    return await this.InspectAsync(value, cancellationToken).ConfigureAwait(false);
                case SelectPlanCandidatesRequest value:
                    return await this.ApproveCandidatesAsync(value, cancellationToken).ConfigureAwait(false);
                case ConfirmPlanRequest value:
                    this.WithSession(() => this.Session.ConfirmPlan(value)); return null;
                case ExecutePlanRequest value:
                    Task<ProtocolEvent> execution = this.StartExecution(value, cancellationToken);
                    releaseGate = false; this.CommandGate.Release();
                    return await execution.ConfigureAwait(false);
                case CancelPlanRequest value:
                    return this.CancelPlan(value);
                case InspectPruneRequest value:
                    return await this.InspectPruneAsync(value, cancellationToken).ConfigureAwait(false);
                case ConfirmPruneRequest value:
                    this.WithSession(() => this.Session.ConfirmPrune(value)); return null;
                case ExecutePruneRequest value:
                    Task<ProtocolEvent> pruning = this.StartPrune(value, cancellationToken);
                    releaseGate = false; this.CommandGate.Release();
                    return await pruning.ConfigureAwait(false);
                case CancelPruneRequest value:
                    return this.CancelPrune(value);
                default:
                    throw new ProtocolException("The request isn't supported by this protocol service.");
            }
        }
        finally
        {
            if (releaseGate) this.CommandGate.Release();
        }
    }

    private async Task<GameDiscoveryEvent> DiscoverAsync(DiscoverGamesRequest request, CancellationToken cancellationToken)
    {
        this.WithSession(() => this.Session.ValidateReadyRequest(request.SessionId));
        IReadOnlyList<LinuxGameFolderCandidate> discovered = await this.Discovery.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        ProtocolGameCandidate[] candidates = discovered.Select(candidate => new ProtocolGameCandidate(candidate.CanonicalPath, candidate.Status, GetGameDisplayName(candidate))).ToArray();
        return this.Emit(this.WithSession(() => this.Session.RecordDiscovery(request, candidates)));
    }

    private async Task<PackageOpenedEvent> OpenPackageAsync(OpenPackageRequest request, CancellationToken cancellationToken)
    {
        this.WithSession(() => this.Session.ValidateReadyRequest(request.SessionId));
        ProtocolPackageRegistration registration = await this.PackageOpener.OpenAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            PackageOpenedEvent result = this.WithSession(() => this.Session.RegisterPackageAuthority(request, registration.Release, registration.Authority, registration.Owner));
            registration.TransferOwnership();
            return this.Emit(result);
        }
        finally
        {
            registration.Dispose();
        }
    }

    private Task<ProtocolEvent> StartRecovery(RecoverInterruptedRequest request, CancellationToken outerCancellation)
    {
        CancellationTokenSource active = CancellationTokenSource.CreateLinkedTokenSource(outerCancellation);
        TaskCompletionSource<ProtocolEvent> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (this.SessionLock)
        {
            this.AssertNoActiveExecution();
            try { this.Session.BeginInterruptedRecovery(request); }
            catch { active.Dispose(); throw; }
            this.ActiveCancellation = active;
            this.ActiveOperation = completion.Task;
        }
        _ = this.CompleteTrackedOperationAsync(() => this.RecoverInterruptedAsync(request, active), completion);
        return completion.Task;
    }

    private Task<ProtocolEvent> StartExecution(ExecutePlanRequest request, CancellationToken outerCancellation)
    {
        CancellationTokenSource active = CancellationTokenSource.CreateLinkedTokenSource(outerCancellation);
        TaskCompletionSource<ProtocolEvent> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        InspectedInstallationState inspection;
        lock (this.SessionLock)
        {
            this.AssertNoActiveExecution();
            try { inspection = this.Session.BeginExecution(request); }
            catch { active.Dispose(); throw; }
            this.ActiveCancellation = active;
            this.ActiveOperation = completion.Task;
        }
        _ = this.CompleteTrackedOperationAsync(() => this.ExecuteAsync(request, inspection, active, outerCancellation), completion);
        return completion.Task;
    }

    private Task<ProtocolEvent> StartPrune(ExecutePruneRequest request, CancellationToken outerCancellation)
    {
        CancellationTokenSource active = CancellationTokenSource.CreateLinkedTokenSource(outerCancellation);
        TaskCompletionSource<ProtocolEvent> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecoveryPrunePlan plan;
        lock (this.SessionLock)
        {
            this.AssertNoActiveExecution();
            try { plan = this.Session.BeginPrune(request); }
            catch { active.Dispose(); throw; }
            this.ActiveCancellation = active;
            this.ActiveOperation = completion.Task;
        }
        _ = this.CompleteTrackedOperationAsync(() => this.ExecutePruneAsync(request, plan, active, outerCancellation), completion);
        return completion.Task;
    }

    private async Task CompleteTrackedOperationAsync(Func<Task<ProtocolEvent>> start, TaskCompletionSource<ProtocolEvent> completion)
    {
        try
        {
            await Task.Yield();
            ProtocolEvent result = await start().ConfigureAwait(false);
            lock (this.SessionLock) { if (ReferenceEquals(this.ActiveOperation, completion.Task)) this.ActiveOperation = null; }
            completion.TrySetResult(result);
        }
        catch (Exception exception)
        {
            lock (this.SessionLock) { if (ReferenceEquals(this.ActiveOperation, completion.Task)) this.ActiveOperation = null; }
            completion.TrySetException(exception);
        }
    }

    private async Task<ProtocolEvent> RecoverInterruptedAsync(RecoverInterruptedRequest request, CancellationTokenSource active)
    {
        try
        {
            InterruptedOperationRecoveryResult result = await this.Engine.RecoverInterruptedOperationAsync(request.GamePath, active.Token).ConfigureAwait(false);
            RecoveryCompletedEvent terminal = new(this.SessionId, new(result.GameRoot.CanonicalPath, result.GameRoot.DeviceMajor, result.GameRoot.DeviceMinor, result.GameRoot.Inode, result.CurrentOperationGeneration), result.NamedRootStillSelected, result.PreviousOperationGeneration, result.CurrentOperationGeneration, result.RecoveredTransactions.Count, result.RecoveredTransactions.Sum(transaction => transaction.ChangedPathCount), result.RecoveredAny ? "Interrupted installer work was recovered to a durable safe state." : "No interrupted transaction required rollback; the operation generation was refreshed.", "Discard every prior plan and inspect again.", this.SanitizedLogPath);
            lock (this.OutboundLock) this.WithSession(() => this.Session.CompleteInterruptedRecovery(request, terminal));
            return this.Emit(terminal);
        }
        catch (Exception exception)
        {
            bool cancelled = exception is OperationCanceledException && active.IsCancellationRequested;
            InterruptedOperationRecoveryException? partial = exception as InterruptedOperationRecoveryException;
            string code = cancelled ? "RecoveryCancelled" : partial?.ErrorCode.ToString() ?? "RecoveryFailed";
            string message = cancelled ? "Interrupted-operation recovery was cancelled before recovery began." : partial?.SafeMessage ?? "Interrupted-operation recovery stopped without a completed result.";
            ProtocolInterruptedRecoveryFailureDetails? details = partial is null ? null : new(
                partial.GameRoot.CanonicalPath,
                partial.GameRoot.DeviceMajor,
                partial.GameRoot.DeviceMinor,
                partial.GameRoot.Inode,
                partial.PreviousOperationGeneration,
                partial.CurrentOperationGeneration,
                partial.OperationGenerationAdvanced,
                partial.NamedRootStillSelected,
                partial.NamedRootSelectionChanged,
                partial.RequiresRecovery,
                partial.RequiresFreshInspection,
                partial.RecoveredTransactions.Select(transaction => new ProtocolRecoveredTransactionResult(transaction.TransactionId.ToString("N"), transaction.ChangedPathCount)).ToArray(),
                partial.RecoveredTransactions.Count,
                partial.RecoveredTransactions.Sum(transaction => transaction.ChangedPathCount)
            );
            RecoveryFailureEvent terminal = new(this.SessionId, code, message, cancelled ? ProtocolRecoveryResult.NotNeeded : ProtocolRecoveryResult.Pending, "Retry interrupted-operation recovery before inspecting or changing the installation.", this.SanitizedLogPath, details);
            lock (this.OutboundLock) this.WithSession(() => this.Session.FailInterruptedRecovery(terminal));
            return this.Emit(terminal);
        }
        finally
        {
            lock (this.SessionLock) { if (ReferenceEquals(this.ActiveCancellation, active)) this.ActiveCancellation = null; }
            active.Dispose();
        }
    }

    private async Task<RecoveryCatalogEvent> ListRecoveriesAsync(ListRecoveriesRequest request, CancellationToken cancellationToken)
    {
        this.WithSession(() => this.Session.ValidateReadyRequest(request.SessionId));
        RecoveryHistory history = await this.Engine.ListRecoveriesAsync(request.GamePath, cancellationToken).ConfigureAwait(false);
        List<ICommittedRecoveryContentAuthority> handles = new(history.Generations.Count);
        try
        {
            foreach (RecoveryGenerationInfo generation in history.Generations)
                handles.Add(await this.Engine.OpenRecoveryAsync(request.GamePath, generation.GenerationId, cancellationToken).ConfigureAwait(false));
            RecoveryCatalogEvent result = this.WithSession(() => this.Session.RecordRecoveryCatalogAuthorities(request, history, handles.ToArray()));
            handles.Clear();
            return this.Emit(result);
        }
        finally
        {
            foreach (IDisposable handle in handles.OfType<IDisposable>()) handle.Dispose();
        }
    }

    private async Task<PlanEvent> InspectAsync(InspectPlanRequest request, CancellationToken cancellationToken)
    {
        (IVerifiedPackageContentAuthority? package, ICommittedRecoveryContentAuthority? recovery) = this.WithSession(() => this.Session.ResolveInspectionAuthorities(request));
        InspectedInstallationState inspection = await this.Engine.InspectAsync(request.GamePath, (InstallationAction)(int)request.Operation, package, recovery, cancellationToken).ConfigureAwait(false);
        try
        {
            PlanEvent result = this.WithSession(() => this.Session.IssuePlan(request, inspection));
            inspection = null!;
            return this.Emit(result);
        }
        finally
        {
            inspection?.Dispose();
        }
    }

    private async Task<PlanEvent> ApproveCandidatesAsync(SelectPlanCandidatesRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyList<ModifiedFileReplacementCandidate> selected = this.WithSession(() => this.Session.ResolveCandidateSelection(request));
        InspectedInstallationState source = this.WithSession(() => this.Session.ResolveCurrentInspection(request.SessionId, request.PlanId, request.PlanDigest));
        InspectedInstallationState replacement = await this.Engine.ApproveFileReplacementsAsync(source, selected, cancellationToken).ConfigureAwait(false);
        try
        {
            PlanEvent result = this.WithSession(() => this.Session.IssueCandidatePlan(request, replacement));
            replacement = null!;
            return this.Emit(result);
        }
        finally
        {
            replacement?.Dispose();
        }
    }

    private async Task<PrunePlanEvent> InspectPruneAsync(InspectPruneRequest request, CancellationToken cancellationToken)
    {
        RecoveryCatalogEvent catalog = this.WithSession(() => this.Session.ResolveRecoveryCatalog(request.SessionId, request.CatalogId));
        RecoveryPrunePlan plan = await this.Engine.InspectRecoveryPruneAsync(catalog.GameRoot.CanonicalPath, request.RetainNewest, cancellationToken).ConfigureAwait(false);
        return this.Emit(this.WithSession(() => this.Session.IssuePrunePlan(request, plan)));
    }

    private async Task<ProtocolEvent> ExecuteAsync(ExecutePlanRequest request, InspectedInstallationState inspection, CancellationTokenSource active, CancellationToken outerCancellation)
    {
        try
        {
            using CancellationTokenRegistration outerRegistration = outerCancellation.Register(() => this.RequestOuterCancellation(request));
            InstallationExecutionOutcome outcome = await this.Engine.ExecuteAsync(inspection, Sha256Digest.Parse(request.PlanDigest.Value), this.SanitizedLogPath, active.Token).ConfigureAwait(false);
            ProtocolEvent terminal = this.CreateExecutionTerminal(request, outcome);
            this.CompleteExecutionTerminal(terminal); return this.Emit(terminal);
        }
        catch (Exception)
        {
            RecoverableInterruptionEvent terminal = new(request.SessionId, request.PlanId, request.PlanDigest, "UnexpectedCoreFailure", "The installer core stopped without returning a typed terminal outcome.", InstallerRecoveryAction.Resume, "Treat the installation as requiring conservative interrupted-operation recovery.", 0, ProtocolRecoveryResult.Pending, "Run interrupted-operation recovery, then inspect again.", this.SanitizedLogPath);
            lock (this.OutboundLock) this.WithSession(() => this.Session.Complete(terminal));
            return this.Emit(terminal);
        }
        finally
        {
            lock (this.SessionLock) { if (ReferenceEquals(this.ActiveCancellation, active)) this.ActiveCancellation = null; }
            active.Dispose();
        }
    }

    private async Task<ProtocolEvent> ExecutePruneAsync(ExecutePruneRequest request, RecoveryPrunePlan plan, CancellationTokenSource active, CancellationToken outerCancellation)
    {
        try
        {
            using CancellationTokenRegistration outerRegistration = outerCancellation.Register(() => this.RequestOuterPruneCancellation(request));
            RecoveryPruneOutcome outcome = await this.Engine.ExecuteRecoveryPruneAsync(plan, Sha256Digest.Parse(request.PruneDigest.Value), active.Token).ConfigureAwait(false);
            ProtocolEvent terminal = this.CreatePruneTerminal(request, outcome);
            this.CompletePruneTerminal(terminal); return this.Emit(terminal);
        }
        catch (Exception)
        {
            PruneInterruptionEvent terminal = new(request.SessionId, request.PrunePlanId, request.PruneDigest, "UnexpectedCoreFailure", "Recovery pruning stopped without returning a typed terminal outcome.", InstallerRecoveryAction.Retry, 0, 0, ProtocolRecoveryResult.Pending, "List recoveries to observe durable state, then inspect pruning again.", null);
            lock (this.OutboundLock) this.WithSession(() => this.Session.Complete(terminal));
            return this.Emit(terminal);
        }
        finally
        {
            lock (this.SessionLock) { if (ReferenceEquals(this.ActiveCancellation, active)) this.ActiveCancellation = null; }
            active.Dispose();
        }
    }

    private ProtocolEvent? CancelPlan(CancelPlanRequest request)
    {
        ProtocolSessionState before;
        lock (this.SessionLock) { this.AssertUsable(); before = this.Session.State; this.Session.RequestCancellation(request); this.ActiveCancellation?.Cancel(); }
        if (before == ProtocolSessionState.Executing) return null;
        CancelledEvent terminal = new(request.SessionId, request.PlanId, request.PlanDigest, "The confirmed plan was cancelled before execution began.", "No files were changed.", 0, ProtocolRecoveryResult.NotNeeded, "Inspect again when ready.", null);
        this.WithSession(() => this.Session.Complete(terminal)); return this.Emit(terminal);
    }

    private ProtocolEvent? CancelPrune(CancelPruneRequest request)
    {
        ProtocolSessionState before;
        lock (this.SessionLock) { this.AssertUsable(); before = this.Session.State; this.Session.RequestPruneCancellation(request); this.ActiveCancellation?.Cancel(); }
        if (before == ProtocolSessionState.Pruning) return null;
        PruneCancelledEvent terminal = new(request.SessionId, request.PrunePlanId, request.PruneDigest, "The prune plan was cancelled before execution began.", "No recovery state was changed.", 0, 0, ProtocolRecoveryResult.NotNeeded, "List recoveries again when ready.", null);
        this.WithSession(() => this.Session.Complete(terminal)); return this.Emit(terminal);
    }

    private ProtocolEvent CreateExecutionTerminal(ExecutePlanRequest request, InstallationExecutionOutcome outcome)
    {
        int changed = outcome.ManagedGamePathChanges.Count;
        int rolledBack = outcome.Transaction?.RolledBackPaths.Count(change => !change.RelativePath.StartsWith(".smapi-installer/", StringComparison.Ordinal)) ?? 0;
        int recoveredTransactions = outcome.RecoveredTransactions.Count;
        int recoveredPaths = outcome.RecoveredTransactions.Sum(transaction => transaction.ChangedPathCount);
        string code = outcome.ErrorCode?.ToString() ?? outcome.Status.ToString();
        string message = outcome.SafeMessage ?? "The installer core returned a bounded terminal outcome.";
        return outcome.Status switch
        {
            InstallationExecutionStatus.Succeeded => new SuccessEvent(request.SessionId, request.PlanId, request.PlanDigest, (InstallerOperation)(int)outcome.Action, message, changed, ProtocolRecoveryResult.NotNeeded, "Close the installer or inspect again.", outcome.SanitizedLogPath),
            InstallationExecutionStatus.SucceededWithCleanupWarning => new SuccessEvent(request.SessionId, request.PlanId, request.PlanDigest, (InstallerOperation)(int)outcome.Action, message, changed, ProtocolRecoveryResult.Pending, "The installation committed; retry recovery cleanup from a fresh inspection.", outcome.SanitizedLogPath),
            InstallationExecutionStatus.FailedBeforeMutation => new RolledBackFailureEvent(request.SessionId, request.PlanId, request.PlanDigest, code, message, "No managed-file mutation began, so rollback was not needed.", 0, ProtocolRecoveryResult.NotNeeded, "Inspect again before retrying.", outcome.SanitizedLogPath),
            InstallationExecutionStatus.CancelledBeforeMutation => new CancelledEvent(request.SessionId, request.PlanId, request.PlanDigest, message, "Cancellation was observed before managed-file mutation.", 0, ProtocolRecoveryResult.NotNeeded, "Inspect again when ready.", outcome.SanitizedLogPath),
            InstallationExecutionStatus.CancelledAndRolledBack => new CancelledEvent(request.SessionId, request.PlanId, request.PlanDigest, message, $"Core restored {rolledBack} of {changed} observed managed-file change(s).", changed, ProtocolRecoveryResult.Succeeded, "Inspect again before retrying.", outcome.SanitizedLogPath),
            InstallationExecutionStatus.FailedAndRolledBack => new RolledBackFailureEvent(request.SessionId, request.PlanId, request.PlanDigest, code, message, $"Core restored {rolledBack} of {changed} observed managed-file change(s).", changed, ProtocolRecoveryResult.Succeeded, "Inspect again before retrying.", outcome.SanitizedLogPath),
            InstallationExecutionStatus.InterruptedRecoveryRequired => new RecoverableInterruptionEvent(request.SessionId, request.PlanId, request.PlanDigest, code, message, InstallerRecoveryAction.Resume, $"Core restored {rolledBack} of {changed} observed managed-file change(s); interrupted-operation recovery is still required.", changed, ProtocolRecoveryResult.Pending, "Run interrupted-operation recovery, then inspect again.", outcome.SanitizedLogPath),
            InstallationExecutionStatus.AutomaticRecoveryCompletedFreshInspectionRequired => new RolledBackFailureEvent(request.SessionId, request.PlanId, request.PlanDigest, code, message, $"Core recovered {recoveredTransactions} prior interrupted transaction(s) covering {recoveredPaths} installer-owned path operation(s) before this operation began.", 0, ProtocolRecoveryResult.Succeeded, "Inspect and confirm again against the recovered state.", outcome.SanitizedLogPath),
            _ => throw new ProtocolException("The core returned an unknown installation execution status.")
        };
    }

    private ProtocolEvent CreatePruneTerminal(ExecutePruneRequest request, RecoveryPruneOutcome outcome)
    {
        int logical = outcome.LogicallyRemovedGenerationIds.Count;
        int cleaned = outcome.PhysicallyCleanedGenerationIds.Count;
        string code = outcome.ErrorCode?.ToString() ?? outcome.Status.ToString();
        string message = outcome.SafeMessage ?? "The recovery core returned a bounded terminal outcome.";
        ProtocolRecoveryResult recovery = outcome.RequiresCleanup ? ProtocolRecoveryResult.Pending : ProtocolRecoveryResult.NotNeeded;
        string pending = $"{outcome.PendingCleanupGenerationIds.Count} physical generation cleanup operation(s) remain"
            + (outcome.AuxiliaryCleanupPending ? ", and authenticated auxiliary recovery metadata cleanup remains" : "")
            + ".";
        return outcome.Status switch
        {
            RecoveryPruneOutcomeStatus.Succeeded => new PruneSuccessEvent(request.SessionId, request.PrunePlanId, request.PruneDigest, logical, cleaned, message, "Close the installer or list recoveries again.", null),
            RecoveryPruneOutcomeStatus.FailedBeforePublication => new PruneFailureEvent(request.SessionId, request.PrunePlanId, request.PruneDigest, code, recovery == ProtocolRecoveryResult.Pending ? $"{message} {pending}" : message, logical, cleaned, recovery, "List recoveries and inspect pruning again.", null),
            RecoveryPruneOutcomeStatus.CancelledBeforePublication => new PruneCancelledEvent(request.SessionId, request.PrunePlanId, request.PruneDigest, message, recovery == ProtocolRecoveryResult.Pending ? $"Cancellation was observed before logical retention publication; {pending}" : "Cancellation was observed before logical retention publication.", logical, cleaned, recovery, "List recoveries again when ready.", null),
            RecoveryPruneOutcomeStatus.Interrupted => new PruneInterruptionEvent(request.SessionId, request.PrunePlanId, request.PruneDigest, code, $"{message} {pending}", InstallerRecoveryAction.Retry, logical, cleaned, recovery, "List recoveries to observe pending cleanup, then retry.", null),
            RecoveryPruneOutcomeStatus.CancelledWithCleanupPending => new PruneCancelledEvent(request.SessionId, request.PrunePlanId, request.PruneDigest, message, logical > 0 ? $"Logical retention was published; {pending}" : $"No logical generations were removed; {pending}", logical, cleaned, ProtocolRecoveryResult.Pending, "List recoveries, then retry cleanup.", null),
            RecoveryPruneOutcomeStatus.FailedWithCleanupPending => new PruneFailureEvent(request.SessionId, request.PrunePlanId, request.PruneDigest, code, $"{message} {pending}", logical, cleaned, ProtocolRecoveryResult.Pending, "List recoveries, then retry cleanup.", null),
            _ => throw new ProtocolException("The core returned an unknown recovery-prune status.")
        };
    }

    private void CompleteExecutionTerminal(ProtocolEvent terminal)
    {
        this.TerminalCompletionStarting?.Invoke();
        lock (this.OutboundLock) this.WithSession(() =>
        {
            switch (terminal)
            {
                case SuccessEvent value: this.Session.Complete(value); break;
                case RolledBackFailureEvent value: this.Session.Complete(value); break;
                case RecoverableInterruptionEvent value: this.Session.Complete(value); break;
                case CancelledEvent value: this.Session.Complete(value); break;
                default: throw new ProtocolException("The core outcome didn't map to an installation terminal event.");
            }
        });
    }

    private void CompletePruneTerminal(ProtocolEvent terminal)
    {
        this.TerminalCompletionStarting?.Invoke();
        lock (this.OutboundLock) this.WithSession(() =>
        {
            switch (terminal)
            {
                case PruneSuccessEvent value: this.Session.Complete(value); break;
                case PruneFailureEvent value: this.Session.Complete(value); break;
                case PruneInterruptionEvent value: this.Session.Complete(value); break;
                case PruneCancelledEvent value: this.Session.Complete(value); break;
                default: throw new ProtocolException("The core outcome didn't map to a prune terminal event.");
            }
        });
    }

    private void RecordProgress(TransactionProgress progress)
    {
        ProtocolEvent? result = null;
        lock (this.OutboundLock)
        {
            lock (this.SessionLock)
            {
                if (this.Disposed) return;
                long sequence = this.Session.LastProgressSequence + 1;
                string message = $"Core transaction stage: {progress.Stage}.";
                if (this.Session.State == ProtocolSessionState.Recovering)
                {
                    RecoveryProgressEvent value = new(this.SessionId, sequence, progress.Stage, progress.CompletedOperations, progress.TotalOperations, message);
                    this.Session.RecordRecoveryProgress(value); result = value;
                }
                else if (this.Session.State is ProtocolSessionState.Executing or ProtocolSessionState.CancellationRequested && this.Session.CurrentPlanId is { } plan && this.Session.CurrentPlanDigest is { } digest)
                {
                    ProgressEvent value = new(this.SessionId, plan, digest, sequence, progress.Stage, progress.CompletedOperations, progress.TotalOperations, message);
                    this.Session.RecordProgress(value); result = value;
                }
                else if (this.Session.State is ProtocolSessionState.Pruning or ProtocolSessionState.PruneCancellationRequested && this.Session.CurrentPrunePlanId is { } prune && this.Session.CurrentPruneDigest is { } pruneDigest)
                {
                    PruneProgressEvent value = new(this.SessionId, prune, pruneDigest, sequence, progress.Stage, progress.CompletedOperations, progress.TotalOperations, message);
                    this.Session.RecordPruneProgress(value); result = value;
                }
            }
            if (result is not null) this.DispatchProgress(result);
        }
    }

    private void RequestOuterCancellation(ExecutePlanRequest request)
    {
        lock (this.SessionLock)
        {
            if (this.Disposed || this.Session.State != ProtocolSessionState.Executing) return;
            this.Session.RequestInternalCancellation(request.SessionId, request.PlanId, request.PlanDigest);
        }
    }

    private void RequestOuterPruneCancellation(ExecutePruneRequest request)
    {
        lock (this.SessionLock)
        {
            if (this.Disposed || this.Session.State != ProtocolSessionState.Pruning) return;
            this.Session.RequestInternalPruneCancellation(request.SessionId, request.PrunePlanId, request.PruneDigest);
        }
    }

    private T WithSession<T>(Func<T> action) { lock (this.SessionLock) { this.AssertUsable(); return action(); } }
    private void WithSession(Action action) { lock (this.SessionLock) { this.AssertUsable(); action(); } }
    private T Emit<T>(T value) where T : ProtocolEvent => value;
    private void DispatchProgress(ProtocolEvent value) { try { this.EventSink?.Invoke(value); } catch { } }
    private void AssertNoActiveExecution() { if (this.ActiveCancellation is not null) throw new ProtocolException("Another execution is already active in this session."); }
    private void AssertUsable() { if (this.Disposed) throw new ObjectDisposedException(nameof(LinuxInstallerProtocolService)); }
    private void AssertAcceptingRequests() { this.AssertUsable(); if (this.Closing) throw new ObjectDisposedException(nameof(LinuxInstallerProtocolService), "The service is finishing an active operation before disposal."); }
    private static string GetGameDisplayName(LinuxGameFolderCandidate candidate) => candidate.GameVersion is null ? $"Stardew Valley ({candidate.Status})" : $"Stardew Valley {candidate.GameVersion} ({candidate.Status})";

    /// <inheritdoc />
    public void Dispose() => this.GetOrStartDisposal().GetAwaiter().GetResult();

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await this.GetOrStartDisposal().ConfigureAwait(false);

    private Task GetOrStartDisposal()
    {
        TaskCompletionSource completion;
        lock (this.SessionLock)
        {
            if (this.Disposed) return Task.CompletedTask;
            if (this.DisposalTask is not null) return this.DisposalTask;
            this.Closing = true;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            this.DisposalTask = completion.Task;
        }
        this.DisposalPublished?.Invoke();
        _ = this.DisposeCoreAsync(completion);
        return completion.Task;
    }

    private async Task DisposeCoreAsync(TaskCompletionSource completion)
    {
        bool gateHeld = false;
        try
        {
            this.RequestDisposalCancellation();
            await this.CommandGate.WaitAsync().ConfigureAwait(false); gateHeld = true;
            this.RequestDisposalCancellation();
            Task<ProtocolEvent>? active;
            lock (this.SessionLock) active = this.ActiveOperation;
            if (active is not null)
            {
                try { await active.ConfigureAwait(false); }
                catch { }
            }
            lock (this.SessionLock)
            {
                if (!this.Disposed)
                {
                    this.Disposed = true; this.ActiveCancellation?.Dispose(); this.ActiveCancellation = null; this.Session.Dispose();
                }
            }
            completion.TrySetResult();
        }
        catch (Exception exception) { completion.TrySetException(exception); }
        finally { if (gateHeld) this.CommandGate.Release(); }
    }

    private void RequestDisposalCancellation()
    {
        lock (this.SessionLock)
        {
            if (this.Disposed) return;
            if (this.Session.State == ProtocolSessionState.Executing && this.Session.CurrentPlanId is { } plan && this.Session.CurrentPlanDigest is { } digest)
                this.Session.RequestInternalCancellation(this.SessionId, plan, digest);
            else if (this.Session.State == ProtocolSessionState.Pruning && this.Session.CurrentPrunePlanId is { } prune && this.Session.CurrentPruneDigest is { } pruneDigest)
                this.Session.RequestInternalPruneCancellation(this.SessionId, prune, pruneDigest);
            this.ActiveCancellation?.Cancel();
        }
    }

    private sealed class CallbackProgressSink(Action<TransactionProgress> callback) : ITransactionProgressSink
    {
        public void Report(TransactionProgress progress) => callback(progress);
    }
}

internal interface ILinuxInstallerProtocolEngine
{
    Task<InspectedInstallationState> InspectAsync(string gameRoot, InstallationAction action, IVerifiedPackageContentAuthority? package, ICommittedRecoveryContentAuthority? recovery, CancellationToken cancellationToken);
    Task<InspectedInstallationState> ApproveFileReplacementsAsync(InspectedInstallationState source, IEnumerable<ModifiedFileReplacementCandidate> selected, CancellationToken cancellationToken);
    Task<InterruptedOperationRecoveryResult> RecoverInterruptedOperationAsync(string gameRoot, CancellationToken cancellationToken);
    Task<InstallationExecutionOutcome> ExecuteAsync(InspectedInstallationState inspection, Sha256Digest confirmedDigest, string? sanitizedLogPath, CancellationToken cancellationToken);
    Task<RecoveryHistory> ListRecoveriesAsync(string gameRoot, CancellationToken cancellationToken);
    Task<ICommittedRecoveryContentAuthority> OpenRecoveryAsync(string gameRoot, Guid generationId, CancellationToken cancellationToken);
    Task<RecoveryPrunePlan> InspectRecoveryPruneAsync(string gameRoot, int retainNewest, CancellationToken cancellationToken);
    Task<RecoveryPruneOutcome> ExecuteRecoveryPruneAsync(RecoveryPrunePlan plan, Sha256Digest confirmedDigest, CancellationToken cancellationToken);
}

internal sealed class LinuxInstallerProtocolEngine(LinuxInstallerEngine engine) : ILinuxInstallerProtocolEngine
{
    public Task<InspectedInstallationState> InspectAsync(string gameRoot, InstallationAction action, IVerifiedPackageContentAuthority? package, ICommittedRecoveryContentAuthority? recovery, CancellationToken cancellationToken)
        => engine.InspectAsync(gameRoot, action, package as VerifiedPackageContent, recovery as CommittedRecoveryHandle, cancellationToken);
    public Task<InspectedInstallationState> ApproveFileReplacementsAsync(InspectedInstallationState source, IEnumerable<ModifiedFileReplacementCandidate> selected, CancellationToken cancellationToken) => engine.ApproveFileReplacementsAsync(source, selected, cancellationToken);
    public Task<InterruptedOperationRecoveryResult> RecoverInterruptedOperationAsync(string gameRoot, CancellationToken cancellationToken) => engine.RecoverInterruptedOperationAsync(gameRoot, cancellationToken);
    public Task<InstallationExecutionOutcome> ExecuteAsync(InspectedInstallationState inspection, Sha256Digest confirmedDigest, string? sanitizedLogPath, CancellationToken cancellationToken) => engine.ExecuteWithOutcomeAsync(inspection, confirmedDigest, sanitizedLogPath, cancellationToken);
    public Task<RecoveryHistory> ListRecoveriesAsync(string gameRoot, CancellationToken cancellationToken) => engine.ListRecoveriesAsync(gameRoot, cancellationToken);
    public async Task<ICommittedRecoveryContentAuthority> OpenRecoveryAsync(string gameRoot, Guid generationId, CancellationToken cancellationToken) => await engine.OpenRecoveryAsync(gameRoot, generationId, cancellationToken).ConfigureAwait(false);
    public Task<RecoveryPrunePlan> InspectRecoveryPruneAsync(string gameRoot, int retainNewest, CancellationToken cancellationToken) => engine.InspectRecoveryPruneAsync(gameRoot, retainNewest, cancellationToken);
    public Task<RecoveryPruneOutcome> ExecuteRecoveryPruneAsync(RecoveryPrunePlan plan, Sha256Digest confirmedDigest, CancellationToken cancellationToken) => engine.ExecuteRecoveryPruneWithOutcomeAsync(plan, confirmedDigest, cancellationToken);
}

internal interface ILinuxInstallerProtocolDiscovery
{
    Task<IReadOnlyList<LinuxGameFolderCandidate>> DiscoverAsync(CancellationToken cancellationToken);
}

internal sealed class LinuxInstallerProtocolDiscovery(LinuxGameDiscovery discovery) : ILinuxInstallerProtocolDiscovery
{
    public Task<IReadOnlyList<LinuxGameFolderCandidate>> DiscoverAsync(CancellationToken cancellationToken) => discovery.DiscoverAsync(cancellationToken: cancellationToken);
}

internal interface ILinuxInstallerProtocolPackageOpener
{
    Task<ProtocolPackageRegistration> OpenAsync(OpenPackageRequest request, CancellationToken cancellationToken);
}

internal sealed class ProtocolPackageRegistration : IDisposable
{
    private bool Transferred;
    public InstallationReleaseIdentity Release { get; }
    public IVerifiedPackageContentAuthority Authority { get; }
    public IDisposable Owner { get; }
    public ProtocolPackageRegistration(InstallationReleaseIdentity release, IVerifiedPackageContentAuthority authority, IDisposable owner) { this.Release = release; this.Authority = authority; this.Owner = owner; }
    public void TransferOwnership() => this.Transferred = true;
    public void Dispose() { if (!this.Transferred) this.Owner.Dispose(); }
}

internal sealed class LinuxInstallerProtocolPackageOpener : ILinuxInstallerProtocolPackageOpener
{
    public async Task<ProtocolPackageRegistration> OpenAsync(OpenPackageRequest request, CancellationToken cancellationToken)
    {
        ForkReleaseIdentity identity = ForkReleaseIdentity.Parse(request.ReleaseTag);
        string checksums = await ReadBoundedTextAsync(request.ChecksumsPath, PackageVerificationLimits.Default.MaxChecksumBytes, cancellationToken).ConfigureAwait(false);
        string metadata = await ReadBoundedTextAsync(request.BuildMetadataPath, PackageVerificationLimits.Default.MaxMetadataBytes, cancellationToken).ConfigureAwait(false);
        VerifiedReleasePackage? release = await new ReleasePackageVerifier().VerifyAsync(request.PackagePath, checksums, metadata, identity, request.ExpectedSourceCommit, cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            VerifiedInstallerPackage? installer = await new VerifiedInstallerPackageFactory().VerifyAsync(release, request.InstallManifestPath, cancellationToken: cancellationToken).ConfigureAwait(false);
            release = null;
            try
            {
                VerifiedPackageContent content = await new VerifiedPackageContentFactory().ExtractAsync(installer, cancellationToken: cancellationToken).ConfigureAwait(false);
                installer = null;
                return new(content.Release, content, content);
            }
            finally { installer?.Dispose(); }
        }
        finally { release?.Dispose(); }
    }

    private static async Task<string> ReadBoundedTextAsync(string path, int maxBytes, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length < 0 || stream.Length > maxBytes) throw new PackageSecurityException("A selected release metadata document exceeds its configured bound.");
        byte[] bytes = new byte[checked((int)stream.Length)]; int total = 0;
        while (total < bytes.Length) { int read = await stream.ReadAsync(bytes.AsMemory(total), cancellationToken).ConfigureAwait(false); if (read == 0) throw new PackageSecurityException("A selected release metadata document ended early."); total += read; }
        if (await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0 || stream.Length != bytes.Length) throw new PackageSecurityException("A selected release metadata document changed while it was read.");
        return new UTF8Encoding(false, true).GetString(bytes);
    }
}
