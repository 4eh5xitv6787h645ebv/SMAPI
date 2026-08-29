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
/// <remarks>
/// <para>
/// Each value returned by <see cref="HandleAsync"/> is the sole response to that command. The optional progress
/// callback receives only unsolicited <see cref="ProgressEvent"/>, <see cref="PruneProgressEvent"/>, or
/// <see cref="RecoveryProgressEvent"/> values; command responses and terminal events are never duplicated through it.
/// </para>
/// <para>
/// Progress callbacks run synchronously on the core worker which reported progress. Calls are serialized and never
/// overlap, but callers must not assume a UI thread or synchronization context. The callback must return promptly and
/// should post an immutable event to the frontend's own queue. Callback exceptions are swallowed so frontend
/// observation failures can't alter an installer transaction.
/// </para>
/// <para>
/// The callback isn't a reentrant command surface. It must not synchronously call or synchronously wait on
/// <see cref="HandleAsync"/>, <see cref="Dispose"/>, or <see cref="DisposeAsync"/>. Queue those operations to run
/// after the callback returns.
/// </para>
/// </remarks>
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
    /// <param name="serverVersion">The bounded backend version displayed during the handshake.</param>
    /// <param name="sanitizedLogPath">An optional absolute path to a sanitized local log which terminal responses may present.</param>
    /// <param name="eventSink">
    /// An optional synchronous, serialized sink for unsolicited progress events only. It runs on a core worker thread,
    /// its exceptions are swallowed, and it must not synchronously reenter this service. Command and terminal responses
    /// are returned only by <see cref="HandleAsync"/>.
    /// </param>
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

    /// <summary>Handle one already-deserialized request and return its sole command response.</summary>
    /// <param name="request">The strictly validated request for this service's session.</param>
    /// <param name="cancellationToken">Cancellation for command admission and the bounded core operation.</param>
    /// <returns>
    /// The command's sole correlated response or terminal event. This response is never also sent to the progress callback.
    /// </returns>
    /// <remarks>
    /// Unsolicited progress is delivered only through the callback described by the constructor. Do not synchronously
    /// call or wait on this method from that callback; queue follow-up commands after the callback returns.
    /// </remarks>
    public async Task<ProtocolEvent> HandleAsync(ProtocolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        this.AssertAcceptingRequests();
        await this.CommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool releaseGate = true;
        bool longOperationStarted = false;
        try
        {
            this.AssertAcceptingRequests();
            ProtocolJsonSerializer.SerializeLine(request);
            this.WithSession(() => this.Session.AcceptCommand(request));
            switch (request)
            {
                case HandshakeRequest value:
                    return this.Emit(this.WithSession(() => this.Session.AcceptHandshake(value, this.ServerVersion, Capabilities)));
                case DiscoverGamesRequest value:
                    return await this.DiscoverAsync(value, cancellationToken).ConfigureAwait(false);
                case RecoverInterruptedRequest value:
                    Task<ProtocolEvent> recovery = this.StartRecovery(value, cancellationToken);
                    longOperationStarted = true;
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
                case GetPlanPageRequest value:
                    return this.Emit(this.WithSession(() => this.Session.GetPlanPage(value)));
                case ConfirmPlanRequest value:
                    return this.Emit(this.WithSession(() => this.Session.ConfirmPlan(value)));
                case ExecutePlanRequest value:
                    Task<ProtocolEvent> execution = this.StartExecution(value, cancellationToken);
                    longOperationStarted = true;
                    releaseGate = false; this.CommandGate.Release();
                    return await execution.ConfigureAwait(false);
                case CancelPlanRequest value:
                    return this.CancelPlan(value);
                case InspectPruneRequest value:
                    return await this.InspectPruneAsync(value, cancellationToken).ConfigureAwait(false);
                case ConfirmPruneRequest value:
                    return this.Emit(this.WithSession(() => this.Session.ConfirmPrune(value)));
                case ExecutePruneRequest value:
                    Task<ProtocolEvent> pruning = this.StartPrune(value, cancellationToken);
                    longOperationStarted = true;
                    releaseGate = false; this.CommandGate.Release();
                    return await pruning.ConfigureAwait(false);
                case CancelPruneRequest value:
                    return this.CancelPrune(value);
                default:
                    throw new ProtocolException("The request isn't supported by this protocol service.");
            }
        }
        catch (Exception exception) when (!longOperationStarted && exception is not ProtocolException and not ObjectDisposedException)
        {
            return this.CreatePrePlanRejection(request, exception);
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
            ProtocolInterruptedRecoveryAttempt attempt = new(new(result.GameRoot.CanonicalPath, result.GameRoot.DeviceMajor, result.GameRoot.DeviceMinor, result.GameRoot.Inode, result.CurrentOperationGeneration), result.PreviousOperationGeneration, result.CurrentOperationGeneration, result.NamedRootStillSelected, result.RecoveredTransactions.Select(transaction => new ProtocolRecoveredTransactionResult(transaction.TransactionId.ToString("N"), transaction.ChangedPathCount)).ToArray());
            RecoveryCompletedEvent terminal = new(this.SessionId, ProtocolInterruptedRecoveryOutcome.RecoveryCompleted, new(ProtocolDurableState.RecoveryCompleted, null, ProtocolRecoveryDisposition.Completed, result.NamedRootStillSelected ? ProtocolNextAction.InspectAgain : ProtocolNextAction.SelectGameFolder), attempt, result.RecoveredAny ? "Interrupted installer work was recovered to a durable safe state." : "No interrupted transaction required rollback; the operation generation was refreshed.", this.SanitizedLogPath) { CommandId = request.CommandId };
            lock (this.OutboundLock) this.WithSession(() => this.Session.CompleteInterruptedRecovery(request, terminal));
            return this.Emit(terminal);
        }
        catch (Exception exception)
        {
            bool cancelled = exception is OperationCanceledException cancellation
                && active.IsCancellationRequested
                && cancellation.CancellationToken == active.Token;
            InterruptedOperationRecoveryException? partial = exception as InterruptedOperationRecoveryException;
            string message = cancelled ? "Interrupted-operation recovery was cancelled before recovery began." : partial?.SafeMessage ?? "Interrupted-operation recovery stopped without a completed result.";
            ProtocolInterruptedRecoveryAttempt? attempt = partial is null ? null : new(
                new(partial.GameRoot.CanonicalPath, partial.GameRoot.DeviceMajor, partial.GameRoot.DeviceMinor, partial.GameRoot.Inode, partial.CurrentOperationGeneration ?? partial.PreviousOperationGeneration),
                partial.PreviousOperationGeneration,
                partial.CurrentOperationGeneration,
                partial.NamedRootStillSelected,
                partial.RecoveredTransactions.Select(transaction => new ProtocolRecoveredTransactionResult(transaction.TransactionId.ToString("N"), transaction.ChangedPathCount)).ToArray()
            );
            ProtocolInterruptedRecoveryOutcome outcome = cancelled ? ProtocolInterruptedRecoveryOutcome.CancelledBeforeRecovery : partial is not null ? ProtocolInterruptedRecoveryOutcome.PartialFailure : ProtocolInterruptedRecoveryOutcome.UnexpectedFailure;
            ProtocolTerminalState state = outcome switch
            {
                ProtocolInterruptedRecoveryOutcome.CancelledBeforeRecovery => new(ProtocolDurableState.Unchanged, null, ProtocolRecoveryDisposition.InterruptedRecoveryRequired, ProtocolNextAction.RecoverInterrupted),
                ProtocolInterruptedRecoveryOutcome.PartialFailure => new(ProtocolDurableState.RecoveryRequired, MapError(partial!.ErrorCode), ProtocolRecoveryDisposition.InterruptedRecoveryRequired, ProtocolNextAction.RecoverInterrupted),
                ProtocolInterruptedRecoveryOutcome.UnexpectedFailure => new(ProtocolDurableState.Unknown, ProtocolTerminalErrorCode.UnexpectedCoreFailure, ProtocolRecoveryDisposition.InterruptedRecoveryRequired, ProtocolNextAction.RecoverInterrupted),
                _ => throw new ProtocolException("The interrupted-recovery exception didn't map to a typed outcome.")
            };
            RecoveryFailureEvent terminal = new(this.SessionId, outcome, state, message, this.SanitizedLogPath, attempt) { CommandId = request.CommandId };
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
        catch (Exception exception) when (exception is not ProtocolException)
        {
            RecoverableInterruptionEvent terminal = new(request.SessionId, request.PlanId, request.PlanDigest, ProtocolExecutionOutcome.UnexpectedCoreFailure, new(ProtocolDurableState.Unknown, ProtocolTerminalErrorCode.UnexpectedCoreFailure, ProtocolRecoveryDisposition.InterruptedRecoveryRequired, ProtocolNextAction.RecoverInterrupted), new(null, null, null, null, null, null), "The installer core stopped without returning a typed terminal outcome.", "Treat the installation as requiring conservative interrupted-operation recovery.", this.SanitizedLogPath) { CommandId = request.CommandId };
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
        catch (Exception exception) when (exception is not ProtocolException)
        {
            PruneInterruptionEvent terminal = new(request.SessionId, request.PrunePlanId, request.PruneDigest, ProtocolPruneOutcome.UnexpectedCoreFailure, new(ProtocolDurableState.Unknown, ProtocolTerminalErrorCode.UnexpectedCoreFailure, ProtocolRecoveryDisposition.StateRefreshRequired, ProtocolNextAction.ListRecoveries), new(null, null, null, null), "Recovery pruning stopped without returning a typed terminal outcome.", this.SanitizedLogPath) { CommandId = request.CommandId };
            lock (this.OutboundLock) this.WithSession(() => this.Session.Complete(terminal));
            return this.Emit(terminal);
        }
        finally
        {
            lock (this.SessionLock) { if (ReferenceEquals(this.ActiveCancellation, active)) this.ActiveCancellation = null; }
            active.Dispose();
        }
    }

    private ProtocolEvent CancelPlan(CancelPlanRequest request)
    {
        CommandAcknowledgedEvent acknowledgement;
        lock (this.SessionLock) { this.AssertUsable(); acknowledgement = this.Session.RequestCancellation(request); this.ActiveCancellation?.Cancel(); }
        return this.Emit(acknowledgement);
    }

    private ProtocolEvent CancelPrune(CancelPruneRequest request)
    {
        CommandAcknowledgedEvent acknowledgement;
        lock (this.SessionLock) { this.AssertUsable(); acknowledgement = this.Session.RequestPruneCancellation(request); this.ActiveCancellation?.Cancel(); }
        return this.Emit(acknowledgement);
    }

    private ProtocolEvent CreateExecutionTerminal(ExecutePlanRequest request, InstallationExecutionOutcome outcome)
    {
        int changed = outcome.ManagedGamePathChanges.Count;
        int rolledBack = outcome.Transaction?.RolledBackPaths.Count(change => !change.RelativePath.StartsWith(".smapi-installer/", StringComparison.Ordinal)) ?? 0;
        int internalChanged = outcome.InternalStateChanges.Count;
        int internalRolledBack = outcome.Transaction?.RolledBackPaths.Count(change => change.RelativePath.StartsWith(".smapi-installer/", StringComparison.Ordinal)) ?? 0;
        int recoveredTransactions = outcome.RecoveredTransactions.Count;
        int recoveredPaths = outcome.RecoveredTransactions.Sum(transaction => transaction.ChangedPathCount);
        string message = outcome.SafeMessage ?? "The installer core returned a bounded terminal outcome.";
        ProtocolExecutionSummary summary = new(changed, rolledBack, internalChanged, internalRolledBack, recoveredTransactions, recoveredPaths);
        return ((ProtocolEvent)(outcome.Status switch
        {
            InstallationExecutionStatus.Succeeded => new SuccessEvent(request.SessionId, request.PlanId, request.PlanDigest, (InstallerOperation)(int)outcome.Action, ProtocolExecutionOutcome.Succeeded, TerminalState(ProtocolDurableState.Committed, RequireNoError(outcome.ErrorCode), ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.InspectAgain), summary, message, outcome.SanitizedLogPath),
            InstallationExecutionStatus.SucceededWithCleanupWarning => new SuccessEvent(request.SessionId, request.PlanId, request.PlanDigest, (InstallerOperation)(int)outcome.Action, ProtocolExecutionOutcome.SucceededWithCleanupWarning, TerminalState(ProtocolDurableState.Committed, RequireError(outcome.ErrorCode), ProtocolRecoveryDisposition.CleanupPending, ProtocolNextAction.InspectAgain), summary, message, outcome.SanitizedLogPath),
            InstallationExecutionStatus.FailedBeforeMutation => new RolledBackFailureEvent(request.SessionId, request.PlanId, request.PlanDigest, ProtocolExecutionOutcome.FailedBeforeMutation, TerminalState(ProtocolDurableState.Unchanged, RequireError(outcome.ErrorCode), ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.InspectAgain), summary, message, "No managed-file mutation began, so rollback was not needed.", outcome.SanitizedLogPath),
            InstallationExecutionStatus.CancelledBeforeMutation => new CancelledEvent(request.SessionId, request.PlanId, request.PlanDigest, ProtocolExecutionOutcome.CancelledBeforeMutation, TerminalState(ProtocolDurableState.Unchanged, RequireNoError(outcome.ErrorCode), ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.InspectAgain), summary, message, outcome.SanitizedLogPath),
            InstallationExecutionStatus.CancelledAndRolledBack => new CancelledEvent(request.SessionId, request.PlanId, request.PlanDigest, ProtocolExecutionOutcome.CancelledAndRolledBack, TerminalState(ProtocolDurableState.RolledBack, RequireNoError(outcome.ErrorCode), ProtocolRecoveryDisposition.Completed, ProtocolNextAction.InspectAgain), summary, $"{message} Core restored {rolledBack} of {changed} observed managed-file change(s).", outcome.SanitizedLogPath),
            InstallationExecutionStatus.FailedAndRolledBack => new RolledBackFailureEvent(request.SessionId, request.PlanId, request.PlanDigest, ProtocolExecutionOutcome.FailedAndRolledBack, TerminalState(ProtocolDurableState.RolledBack, RequireError(outcome.ErrorCode), ProtocolRecoveryDisposition.Completed, ProtocolNextAction.InspectAgain), summary, message, $"Core restored {rolledBack} of {changed} observed managed-file change(s).", outcome.SanitizedLogPath),
            InstallationExecutionStatus.InterruptedRecoveryRequired => new RecoverableInterruptionEvent(request.SessionId, request.PlanId, request.PlanDigest, ProtocolExecutionOutcome.InterruptedRecoveryRequired, TerminalState(ProtocolDurableState.RecoveryRequired, RequireError(outcome.ErrorCode), ProtocolRecoveryDisposition.InterruptedRecoveryRequired, ProtocolNextAction.RecoverInterrupted), summary, message, $"Core restored {rolledBack} of {changed} observed managed-file change(s); interrupted-operation recovery is still required.", outcome.SanitizedLogPath),
            InstallationExecutionStatus.AutomaticRecoveryCompletedFreshInspectionRequired => new RolledBackFailureEvent(request.SessionId, request.PlanId, request.PlanDigest, ProtocolExecutionOutcome.AutomaticRecoveryCompletedFreshInspectionRequired, TerminalState(ProtocolDurableState.RecoveryCompleted, RequireExactError(outcome.ErrorCode, TransactionErrorCode.PathChanged), ProtocolRecoveryDisposition.Completed, ProtocolNextAction.InspectAgain), summary, message, $"Core recovered {recoveredTransactions} prior interrupted transaction(s) covering {recoveredPaths} installer-owned path operation(s) before this operation began.", outcome.SanitizedLogPath),
            _ => throw new ProtocolException("The core returned an unknown installation execution status.")
        })) with
        {
            CommandId = request.CommandId
        };
    }

    private ProtocolEvent CreatePruneTerminal(ExecutePruneRequest request, RecoveryPruneOutcome outcome)
    {
        int logical = outcome.LogicallyRemovedGenerationIds.Count;
        int cleaned = outcome.PhysicallyCleanedGenerationIds.Count;
        string message = outcome.SafeMessage ?? "The recovery core returned a bounded terminal outcome.";
        ProtocolPruneSummary summary = new(logical, cleaned, outcome.PendingCleanupGenerationIds.Count, outcome.AuxiliaryCleanupPending);
        ProtocolRecoveryDisposition recovery = outcome.RequiresCleanup ? ProtocolRecoveryDisposition.CleanupPending : ProtocolRecoveryDisposition.NotRequired;
        string pending = $"{outcome.PendingCleanupGenerationIds.Count} physical generation cleanup operation(s) remain"
            + (outcome.AuxiliaryCleanupPending ? ", and authenticated auxiliary recovery metadata cleanup remains" : "")
            + ".";
        return ((ProtocolEvent)(outcome.Status switch
        {
            RecoveryPruneOutcomeStatus.Succeeded => new PruneSuccessEvent(request.SessionId, request.PrunePlanId, request.PruneDigest, ProtocolPruneOutcome.Succeeded, TerminalState(ProtocolDurableState.PruneApplied, RequireNoError(outcome.ErrorCode), ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.ListRecoveries), summary, message, this.SanitizedLogPath),
            RecoveryPruneOutcomeStatus.FailedBeforePublication => new PruneFailureEvent(request.SessionId, request.PrunePlanId, request.PruneDigest, ProtocolPruneOutcome.FailedBeforePublication, TerminalState(ProtocolDurableState.Unchanged, RequireError(outcome.ErrorCode), recovery, ProtocolNextAction.ListRecoveries), summary, outcome.RequiresCleanup ? $"{message} {pending}" : message, this.SanitizedLogPath),
            RecoveryPruneOutcomeStatus.CancelledBeforePublication => new PruneCancelledEvent(request.SessionId, request.PrunePlanId, request.PruneDigest, ProtocolPruneOutcome.CancelledBeforePublication, TerminalState(ProtocolDurableState.Unchanged, RequireNoError(outcome.ErrorCode), recovery, ProtocolNextAction.ListRecoveries), summary, outcome.RequiresCleanup ? $"{message} Cancellation was observed before logical retention publication; {pending}" : $"{message} Cancellation was observed before logical retention publication.", this.SanitizedLogPath),
            RecoveryPruneOutcomeStatus.Interrupted => new PruneInterruptionEvent(request.SessionId, request.PrunePlanId, request.PruneDigest, ProtocolPruneOutcome.Interrupted, TerminalState(logical > 0 || cleaned > 0 ? ProtocolDurableState.PruneApplied : ProtocolDurableState.Unchanged, RequireError(outcome.ErrorCode), outcome.RequiresCleanup ? ProtocolRecoveryDisposition.CleanupPending : ProtocolRecoveryDisposition.StateRefreshRequired, ProtocolNextAction.ListRecoveries), summary, outcome.RequiresCleanup ? $"{message} {pending}" : message, this.SanitizedLogPath),
            RecoveryPruneOutcomeStatus.CancelledWithCleanupPending => new PruneCancelledEvent(request.SessionId, request.PrunePlanId, request.PruneDigest, ProtocolPruneOutcome.CancelledWithCleanupPending, TerminalState(logical > 0 || cleaned > 0 ? ProtocolDurableState.PruneApplied : ProtocolDurableState.Unchanged, RequireNoError(outcome.ErrorCode), ProtocolRecoveryDisposition.CleanupPending, ProtocolNextAction.ListRecoveries), summary, logical > 0 ? $"{message} Logical retention was published; {pending}" : $"{message} No logical generations were removed; {pending}", this.SanitizedLogPath),
            RecoveryPruneOutcomeStatus.FailedWithCleanupPending => new PruneFailureEvent(request.SessionId, request.PrunePlanId, request.PruneDigest, ProtocolPruneOutcome.FailedWithCleanupPending, TerminalState(logical > 0 || cleaned > 0 ? ProtocolDurableState.PruneApplied : ProtocolDurableState.Unchanged, RequireError(outcome.ErrorCode), ProtocolRecoveryDisposition.CleanupPending, ProtocolNextAction.ListRecoveries), summary, $"{message} {pending}", this.SanitizedLogPath),
            _ => throw new ProtocolException("The core returned an unknown recovery-prune status.")
        })) with
        {
            CommandId = request.CommandId
        };
    }

    private static ProtocolTerminalState TerminalState(ProtocolDurableState durableState, ProtocolTerminalErrorCode? error, ProtocolRecoveryDisposition recoveryDisposition, ProtocolNextAction nextAction) => new(durableState, error, recoveryDisposition, nextAction);
    private static ProtocolTerminalErrorCode MapError(TransactionErrorCode error) => error switch
    {
        TransactionErrorCode.InvalidPlan => ProtocolTerminalErrorCode.InvalidPlan,
        TransactionErrorCode.UnsafePath => ProtocolTerminalErrorCode.UnsafePath,
        TransactionErrorCode.PathChanged => ProtocolTerminalErrorCode.PathChanged,
        TransactionErrorCode.ExistingFileMismatch => ProtocolTerminalErrorCode.ExistingFileMismatch,
        TransactionErrorCode.PayloadMismatch => ProtocolTerminalErrorCode.PayloadMismatch,
        TransactionErrorCode.ConcurrentOperation => ProtocolTerminalErrorCode.ConcurrentOperation,
        TransactionErrorCode.WorkspaceConflict => ProtocolTerminalErrorCode.WorkspaceConflict,
        TransactionErrorCode.RecoveryFailed => ProtocolTerminalErrorCode.RecoveryFailed,
        TransactionErrorCode.DiskFull => ProtocolTerminalErrorCode.DiskFull,
        TransactionErrorCode.ReadOnlyFileSystem => ProtocolTerminalErrorCode.ReadOnlyFileSystem,
        TransactionErrorCode.PermissionDenied => ProtocolTerminalErrorCode.PermissionDenied,
        TransactionErrorCode.CrossDeviceBoundary => ProtocolTerminalErrorCode.CrossDeviceBoundary,
        TransactionErrorCode.IoFailure => ProtocolTerminalErrorCode.IoFailure,
        _ => throw new ProtocolException("The core returned an unknown transaction error code.")
    };
    private static ProtocolTerminalErrorCode RequireError(TransactionErrorCode? error) => error is { } value ? MapError(value) : throw new ProtocolException("The typed terminal outcome requires an exact core error code.");
    private static ProtocolTerminalErrorCode RequireExactError(TransactionErrorCode? error, TransactionErrorCode expected) => error == expected ? MapError(expected) : throw new ProtocolException($"The typed terminal outcome requires exact core error '{expected}'.");
    private static ProtocolTerminalErrorCode? RequireNoError(TransactionErrorCode? error) => error is null ? null : throw new ProtocolException("A successful or cancelled terminal outcome can't carry an error code.");

    private PrePlanRejectedEvent CreatePrePlanRejection(ProtocolRequest request, Exception exception)
    {
        (ProtocolPrePlanErrorCode code, ProtocolNextAction action, string message, bool terminal) = exception switch
        {
            OperationCanceledException => (ProtocolPrePlanErrorCode.RequestCancelled, ProtocolNextAction.RetryRequest, "The requested read-only installer operation was cancelled.", false),
            LinuxGameFolderException => (ProtocolPrePlanErrorCode.InvalidGameFolder, ProtocolNextAction.SelectGameFolder, "The selected game folder isn't a safe supported installation.", false),
            PackageSecurityException => (ProtocolPrePlanErrorCode.PackageRejected, ProtocolNextAction.ReopenVerifiedPackage, "The selected release asset set failed strict package verification.", false),
            UnauthorizedAccessException => (ProtocolPrePlanErrorCode.PermissionDenied, ProtocolNextAction.ReviewFilesystem, "The installer couldn't read a required path with the current user's permissions.", false),
            IOException when request is ListRecoveriesRequest => (ProtocolPrePlanErrorCode.RecoveryUnavailable, ProtocolNextAction.ListRecoveries, "The authenticated recovery catalog couldn't be read safely.", false),
            IOException when request is InspectPlanRequest or InspectPruneRequest => (ProtocolPrePlanErrorCode.InspectionFailed, ProtocolNextAction.InspectAgain, "The selected installation couldn't be inspected safely.", false),
            IOException when request is SelectPlanCandidatesRequest => (ProtocolPrePlanErrorCode.CandidateApprovalFailed, ProtocolNextAction.InspectAgain, "The exact file candidates changed or couldn't be revalidated.", false),
            IOException => (ProtocolPrePlanErrorCode.InputOutputFailure, ProtocolNextAction.RetryRequest, "The requested read-only installer operation failed because of an input/output error.", false),
            _ => (ProtocolPrePlanErrorCode.UnexpectedFailure, this.SanitizedLogPath is null ? ProtocolNextAction.StartNewSession : ProtocolNextAction.ViewPrivateLog, "The installer backend stopped the requested operation without exposing private exception details.", true)
        };
        PrePlanRejectedEvent result = new(this.SessionId, code, message, action, terminal, this.SanitizedLogPath) { CommandId = request.CommandId };
        this.WithSession(() => this.Session.RecordPrePlanRejection(result));
        return this.Emit(result);
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
                    ProtocolCommandId commandId = this.Session.ActiveCommand ?? throw new ProtocolException("Progress has no active command binding.");
                    RecoveryProgressEvent value = new(this.SessionId, sequence, progress.Stage, progress.CompletedOperations, progress.TotalOperations, message) { CommandId = commandId };
                    this.Session.RecordRecoveryProgress(value); result = value;
                }
                else if (this.Session.State is ProtocolSessionState.Executing or ProtocolSessionState.CancellationRequested && this.Session.CurrentPlanId is { } plan && this.Session.CurrentPlanDigest is { } digest)
                {
                    ProtocolCommandId commandId = this.Session.ActiveCommand ?? throw new ProtocolException("Progress has no active command binding.");
                    ProgressEvent value = new(this.SessionId, plan, digest, sequence, progress.Stage, progress.CompletedOperations, progress.TotalOperations, message) { CommandId = commandId };
                    this.Session.RecordProgress(value); result = value;
                }
                else if (this.Session.State is ProtocolSessionState.Pruning or ProtocolSessionState.PruneCancellationRequested && this.Session.CurrentPrunePlanId is { } prune && this.Session.CurrentPruneDigest is { } pruneDigest)
                {
                    ProtocolCommandId commandId = this.Session.ActiveCommand ?? throw new ProtocolException("Progress has no active command binding.");
                    PruneProgressEvent value = new(this.SessionId, prune, pruneDigest, sequence, progress.Stage, progress.CompletedOperations, progress.TotalOperations, message) { CommandId = commandId };
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
        VerifiedPackageContent content = await new LinuxTaggedReleasePackageOpener().OpenAsync(
            new LinuxTaggedReleaseAssetSet(
                request.ReleaseTag,
                request.ExpectedSourceCommit,
                request.PackagePath,
                request.ChecksumsPath,
                request.BuildMetadataPath,
                request.InstallManifestPath,
                request.AttestationBundlePath,
                request.AttestationBundleChecksumPath,
                request.GitHubCliPath
            ),
            cancellationToken
        ).ConfigureAwait(false);
        return new(content.Release, content, content);
    }

}
