using System.Threading.Channels;
using StardewModdingAPI.Installer.Core.Protocol.V1;

namespace StardewModdingAPI.Installer.Gui.Backend;

internal sealed partial class ProcessInstallerProtocolClient
{
    private RetainedPrunePlanBinding? CurrentPrunePlanBinding;
    private RetainedConfirmedPruneBinding? CurrentConfirmedPruneBinding;
    private ActivePruneRoute? ActivePrune;
    private ActivePruneRoute? SettlingPrune;
    private int PruneAdmitted;

    internal Action? BeforePrunePlanBindingCommitForTesting { get; set; }
    internal Action? BeforePruneConfirmationAuthorityCommitForTesting { get; set; }
    internal Action? BeforePruneExecutionWriteForTesting { get; set; }
    internal Func<Task>? BeforePruneExecuteWrittenCommitForTesting { get; set; }
    internal Action? PruneTerminalRoutedForTesting { get; set; }
    internal Func<Task>? BeforePruneSettlementForTesting { get; set; }
    internal Func<Task>? BeforePrunePostCancellationDeadlineForTesting { get; set; }
    internal int PruneProgressCapacityForTesting { get; set; } = MaximumPruneProgressEvents;
    internal long PruneProgressByteCapacityForTesting { get; set; } = MaximumPruneProgressUtf8Bytes;
    internal TimeSpan PruneHardTimeoutForTesting { get; set; } = TimeSpan.FromMinutes(30);
    internal TimeSpan PruneIdleTimeoutForTesting { get; set; } = TimeSpan.FromMinutes(5);
    internal TimeSpan PruneCancellationAcknowledgementTimeoutForTesting { get; set; } = TimeSpan.FromSeconds(30);
    internal TimeSpan PrunePostCancellationTimeoutForTesting { get; set; } = TimeSpan.FromMinutes(30);

    public async Task<InstallerRecoveryPrunePlanResult> InspectRecoveryPruneAsync(
        string canonicalGamePath,
        InstallerRecoveryPoint oldestPointToKeep,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(canonicalGamePath);
        ArgumentNullException.ThrowIfNull(oldestPointToKeep);
        await this.CommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.AssertUsable();
            Volatile.Write(ref this.RecoveryEligibilityLost, 1);
            ProtocolSessionId session;
            RetainedRecoveryCatalogBinding catalog;
            ProtocolRecoveryGeneration selected;
            lock (this.ResponseLock)
            {
                session = this.SessionId ?? throw new InstallerProtocolClientException("The installer backend handshake hasn't completed.");
                if (this.CurrentConfirmedPlanBinding is not null || this.CurrentConfirmedPruneBinding is not null)
                    throw new InvalidOperationException("The confirmed backend session can no longer inspect recovery cleanup.");
                catalog = this.CurrentRecoveryCatalogBinding
                    ?? throw new InvalidOperationException("A current recovery catalog is required before inspecting recovery cleanup.");
                if (!string.Equals(canonicalGamePath, catalog.CanonicalGamePath, StringComparison.Ordinal))
                    throw new ArgumentException("The cleanup game path must match the exact current recovery catalog.", nameof(canonicalGamePath));
                if (!catalog.Points.TryGetValue(oldestPointToKeep, out selected!))
                    throw new ArgumentException("The recovery point must be an exact current capability issued by this client.", nameof(oldestPointToKeep));

                this.CurrentRecoveryCatalogBinding = null;
                this.CurrentPlanBinding = null;
                this.CurrentPrunePlanBinding = null;
            }

            using CancellationTokenSource aggregateTimeout = new(this.OperationTimeout);
            using CancellationTokenSource aggregate = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, aggregateTimeout.Token);
            try
            {
                ProtocolEvent response = await this.ExchangeAsync<ProtocolEvent>(
                    new InspectPruneRequest(session, catalog.CatalogId, oldestPointToKeep.Ordinal),
                    aggregate.Token
                ).ConfigureAwait(false);
                if (response is PrePlanRejectedEvent rejected && rejected.SessionId == session)
                {
                    if (
                        !IsReachablePruneInspectionRejection(rejected)
                        || rejected.ErrorCode == ProtocolPrePlanErrorCode.NothingToPrune && oldestPointToKeep.Ordinal != catalog.Points.Count
                    )
                        return await this.FailProtocolAsync<InstallerRecoveryPrunePlanResult>().ConfigureAwait(false);
                    InstallerRecoveryPrunePlanRejection result = new(rejected.ErrorCode, rejected.NextAction, rejected.IsTerminal);
                    if (rejected.IsTerminal)
                        await this.CleanupAsync(allowCleanExit: false).ConfigureAwait(false);
                    return result;
                }
                if (response is not PrunePlanEvent plan || !ValidatePrunePlan(plan, session, catalog, oldestPointToKeep.Ordinal))
                    return await this.FailProtocolAsync<InstallerRecoveryPrunePlanResult>().ConfigureAwait(false);

                InstallerRecoveryPruneConfirmation confirmation = new();
                InstallerRecoveryPrunePlanSuccess projected = new(
                    plan.RetainNewest,
                    plan.RetainedSelectionIds.Length,
                    plan.RemovedSelectionIds.Length,
                    plan.CleanupGenerationIds.Length,
                    plan.AuxiliaryCleanupPlanned,
                    plan.Warnings.Length,
                    Array.AsReadOnly(plan.Risks),
                    plan.RecommendedDefault,
                    plan.RequiresConfirmation
                ) { Confirmation = confirmation };
                aggregate.Token.ThrowIfCancellationRequested();
                if (this.SessionFault.Task.IsCompletedSuccessfully)
                    throw await this.SessionFault.Task.ConfigureAwait(false);
                this.BeforePrunePlanBindingCommitForTesting?.Invoke();
                aggregate.Token.ThrowIfCancellationRequested();
                if (this.SessionFault.Task.IsCompletedSuccessfully)
                    throw await this.SessionFault.Task.ConfigureAwait(false);
                bool retained;
                lock (this.ResponseLock)
                {
                    retained = !this.SessionFaultRaised
                        && Volatile.Read(ref this.CleanupStarted) == 0
                        && !cancellationToken.IsCancellationRequested
                        && this.CurrentPlanBinding is null
                        && this.CurrentConfirmedPlanBinding is null
                        && this.CurrentPrunePlanBinding is null;
                    if (retained)
                        this.CurrentPrunePlanBinding = new(plan.GameRoot, plan.PrunePlanId, plan.PruneDigest, plan.RemovedSelectionIds.Length, plan.CleanupGenerationIds.Length, plan.AuxiliaryCleanupPlanned, confirmation);
                }
                if (!retained)
                    return await this.FailProtocolAsync<InstallerRecoveryPrunePlanResult>().ConfigureAwait(false);
                return projected;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await this.CleanupAsync(allowCleanExit: false).ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException) when (aggregateTimeout.IsCancellationRequested)
            {
                await this.CleanupAsync(allowCleanExit: false).ConfigureAwait(false);
                throw new InstallerProtocolClientException(this.CleanupConfirmed
                    ? "The installer backend recovery-cleanup inspection exceeded its bounded deadline and was stopped."
                    : "The installer backend recovery-cleanup inspection exceeded its bounded deadline, and termination could not be confirmed.");
            }
        }
        finally
        {
            this.CommandGate.Release();
        }
    }

    public async Task<InstallerConfirmedRecoveryPruneAuthority> ConfirmRecoveryPruneAsync(
        InstallerRecoveryPruneConfirmation confirmation,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        await this.CommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.AssertUsable();
            RetainedPrunePlanBinding binding;
            lock (this.ResponseLock)
            {
                binding = this.CurrentPrunePlanBinding
                    ?? throw new InvalidOperationException("A current recovery-cleanup plan is required before confirmation.");
                if (!ReferenceEquals(binding.Confirmation, confirmation))
                    throw new ArgumentException("The confirmation must be the exact current capability issued by this cleanup plan.", nameof(confirmation));
                if (this.CurrentConfirmedPlanBinding is not null || this.CurrentConfirmedPruneBinding is not null)
                    throw new InvalidOperationException("A plan was already confirmed in this backend session.");
                this.CurrentPrunePlanBinding = null;
            }

            ProtocolSessionId session = this.SessionId ?? throw new InstallerProtocolClientException("The installer backend handshake hasn't completed.");
            try
            {
                CommandAcknowledgedEvent acknowledged = await this.ExchangeAsync<CommandAcknowledgedEvent>(
                    new ConfirmPruneRequest(session, binding.PlanId, binding.PlanDigest),
                    cancellationToken
                ).ConfigureAwait(false);
                if (
                    acknowledged.SessionId != session
                    || acknowledged.Acknowledgement != ProtocolAcknowledgementKind.PrunePlanConfirmed
                    || acknowledged.PlanId is not null
                    || acknowledged.PrunePlanId != binding.PlanId
                )
                    return await this.FailProtocolAsync<InstallerConfirmedRecoveryPruneAuthority>().ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                if (this.SessionFault.Task.IsCompletedSuccessfully)
                    throw await this.SessionFault.Task.ConfigureAwait(false);
                this.BeforePruneConfirmationAuthorityCommitForTesting?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                if (this.SessionFault.Task.IsCompletedSuccessfully)
                    throw await this.SessionFault.Task.ConfigureAwait(false);
                InstallerConfirmedRecoveryPruneAuthority authority = new();
                bool committed;
                lock (this.ResponseLock)
                {
                    committed = !this.SessionFaultRaised
                        && Volatile.Read(ref this.CleanupStarted) == 0
                        && !cancellationToken.IsCancellationRequested
                        && this.CurrentPlanBinding is null
                        && this.CurrentConfirmedPlanBinding is null
                        && this.CurrentConfirmedPruneBinding is null;
                    if (committed)
                        this.CurrentConfirmedPruneBinding = new(binding, authority);
                }
                if (!committed)
                    return await this.FailProtocolAsync<InstallerConfirmedRecoveryPruneAuthority>().ConfigureAwait(false);
                return authority;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await this.CleanupAsync(allowCleanExit: false).ConfigureAwait(false);
                throw;
            }
            catch (InstallerProtocolClientException)
            {
                await this.CleanupAsync(allowCleanExit: false).ConfigureAwait(false);
                throw;
            }
            catch
            {
                await this.CleanupAsync(allowCleanExit: false).ConfigureAwait(false);
                throw new InstallerProtocolClientException(this.CleanupConfirmed
                    ? "The installer backend recovery-cleanup confirmation stopped safely."
                    : "The installer backend recovery-cleanup confirmation stopped, and termination could not be confirmed.");
            }
        }
        finally
        {
            this.CommandGate.Release();
        }
    }

    public async Task<InstallerRecoveryPruneOperation> ExecuteRecoveryPruneAsync(
        InstallerConfirmedRecoveryPruneAuthority authority,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(authority);
        await this.CommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.AssertUsable();
            ActivePruneRoute route;
            ExecutePruneRequest request;
            lock (this.ResponseLock)
            {
                RetainedConfirmedPruneBinding binding = this.CurrentConfirmedPruneBinding
                    ?? throw new InvalidOperationException("A current confirmed recovery-cleanup plan is required before execution.");
                if (!ReferenceEquals(binding.Authority, authority))
                    throw new ArgumentException("Execution requires the exact current confirmed cleanup authority.", nameof(authority));
                if (
                    Volatile.Read(ref this.DisposeStarted) != 0
                    || Volatile.Read(ref this.CleanupStarted) != 0
                    || this.SessionFaultRaised
                )
                    throw new ObjectDisposedException(nameof(ProcessInstallerProtocolClient));
                cancellationToken.ThrowIfCancellationRequested();
                if (
                    this.PendingResponse is not null
                    || this.ActiveExecution is not null
                    || this.SettlingExecution is not null
                    || this.ActiveRecovery is not null
                    || this.SettlingRecovery is not null
                    || this.ActivePrune is not null
                    || this.SettlingPrune is not null
                )
                    throw new InvalidOperationException("The installer backend already has an active or settling command.");
                if (
                    this.PruneProgressCapacityForTesting is < 1 or > MaximumPruneProgressEvents
                    || this.PruneProgressByteCapacityForTesting is < ProtocolJsonSerializer.MaxLineBytes or > MaximumPruneProgressUtf8Bytes
                )
                    throw new InvalidOperationException("The recovery-cleanup progress bounds are invalid.");
                ProtocolSessionId session = this.SessionId ?? throw new InstallerProtocolClientException("The installer backend handshake hasn't completed.");
                request = new(session, binding.Plan.PlanId, binding.Plan.PlanDigest);
                route = new(session, binding, request.CommandId, this.PruneProgressCapacityForTesting, this.PruneProgressByteCapacityForTesting);
                this.CurrentConfirmedPruneBinding = null;
                this.CurrentRecoveryCatalogBinding = null;
                this.CurrentPlanBinding = null;
                this.CurrentConfirmedPlanBinding = null;
                this.ActivePrune = route;
                Volatile.Write(ref this.PruneAdmitted, 1);
            }

            Task<InstallerRecoveryPruneResult> completion = this.CompletePruneAsync(route);
            InstallerRecoveryPruneOperation operation = new(route.Progress.Reader, completion, () => this.RequestPruneCancellationAsync(route));
            route.AttachCallerCancellation(cancellationToken, () => ObserveAbandoned(this.RequestPruneCancellationAsync(route)));
            try
            {
                this.BeforePruneExecutionWriteForTesting?.Invoke();
                await this.WritePruneExecutionRequestAsync(request).ConfigureAwait(false);
                if (this.BeforePruneExecuteWrittenCommitForTesting is { } beforeCommit)
                    await beforeCommit().ConfigureAwait(false);
                route.MarkExecuteWritten();
            }
            catch
            {
                route.MarkExecuteWriteFailed();
                await this.TryCleanupAfterPruneAsync(route, allowCleanExit: false).ConfigureAwait(false);
            }
            return operation;
        }
        finally
        {
            this.CommandGate.Release();
        }
    }

    private async Task WritePruneExecutionRequestAsync(ExecutePruneRequest request)
    {
        string line;
        try { line = ProtocolJsonSerializer.SerializeLine(request); }
        catch { throw new InstallerProtocolClientException("The installer backend recovery-cleanup request was rejected safely."); }
        await this.EnsureStartedAsync().ConfigureAwait(false);
        using CancellationTokenSource timeout = new(this.OperationTimeout);
        using CancellationTokenSource write = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, this.Lifetime.Token);
        byte[] bytes = StrictUtf8.GetBytes(line + "\n");
        await this.AwaitTransportAsync(this.ProcessInput!.WriteAsync(bytes, write.Token).AsTask(), write.Token).ConfigureAwait(false);
        await this.AwaitTransportAsync(this.ProcessInput.FlushAsync(write.Token), write.Token).ConfigureAwait(false);
    }

    private Task RequestPruneCancellationAsync(ActivePruneRoute route)
    {
        TaskCompletionSource? publication;
        lock (this.ResponseLock)
        {
            if (route.CancellationTask is not null)
                return route.CancellationTask;
            if (!ReferenceEquals(this.ActivePrune, route) || route.Terminal.Task.IsCompleted)
                return Task.CompletedTask;
            publication = new(TaskCreationOptions.RunContinuationsAsynchronously);
            route.CancellationTask = publication.Task;
        }
        _ = this.PublishPruneCancellationAsync(route, publication);
        return publication.Task;
    }

    private async Task PublishPruneCancellationAsync(ActivePruneRoute route, TaskCompletionSource publication)
    {
        try { await this.SendPruneCancellationAsync(route).ConfigureAwait(false); publication.TrySetResult(); }
        catch (Exception error) { publication.TrySetException(error); }
    }

    private async Task SendPruneCancellationAsync(ActivePruneRoute route)
    {
        if (!await route.ExecuteWritten.Task.ConfigureAwait(false))
        {
            route.MarkSettlementUnconfirmed();
            throw new InstallerProtocolClientException("The installer backend could not confirm the recovery-cleanup cancellation request.");
        }
        using CancellationTokenSource timeout = new(this.PruneCancellationAcknowledgementTimeoutForTesting);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, this.Lifetime.Token);
        try
        {
            CancelPruneRequest request;
            Task<CommandAcknowledgedEvent> acknowledgement;
            lock (this.ResponseLock)
            {
                if (!ReferenceEquals(this.ActivePrune, route) || route.Terminal.Task.IsCompleted)
                    return;
                request = new(route.SessionId, route.Binding.Plan.PlanId, route.Binding.Plan.PlanDigest);
                acknowledgement = route.InstallCancellationLane(request.CommandId);
                route.MarkCancellationRequested();
            }
            await this.WritePruneCancellationRequestAsync(request, cancellation.Token).ConfigureAwait(false);
            await this.AwaitTransportAsync(acknowledgement, cancellation.Token).ConfigureAwait(false);
        }
        catch
        {
            route.MarkSettlementUnconfirmed();
            await this.TryCleanupAfterPruneAsync(route, allowCleanExit: false).ConfigureAwait(false);
            throw new InstallerProtocolClientException(this.CleanupConfirmed
                ? "The installer backend could not confirm the recovery-cleanup cancellation request and was stopped."
                : "The installer backend could not confirm the recovery-cleanup cancellation request, and termination could not be confirmed.");
        }
    }

    private async Task WritePruneCancellationRequestAsync(CancelPruneRequest request, CancellationToken cancellationToken)
    {
        string line;
        try { line = ProtocolJsonSerializer.SerializeLine(request); }
        catch { throw new InstallerProtocolClientException("The installer backend recovery-cleanup cancellation request was rejected safely."); }
        byte[] bytes = StrictUtf8.GetBytes(line + "\n");
        await this.AwaitTransportAsync(this.ProcessInput!.WriteAsync(bytes, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);
        await this.AwaitTransportAsync(this.ProcessInput.FlushAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private async Task<InstallerRecoveryPruneResult> CompletePruneAsync(ActivePruneRoute route)
    {
        using CancellationTokenSource hard = new(this.PruneHardTimeoutForTesting);
        Task hardDeadline = Task.Delay(Timeout.InfiniteTimeSpan, hard.Token);
        Task postCancellationDeadline = Task.Delay(Timeout.InfiniteTimeSpan);
        Task cancellationRequested = route.CancellationRequested.Task;
        try
        {
            while (true)
            {
                using CancellationTokenSource idle = new(this.PruneIdleTimeoutForTesting);
                Task idleDeadline = Task.Delay(Timeout.InfiniteTimeSpan, idle.Token);
                Task<bool> activity = route.Activity.Reader.WaitToReadAsync().AsTask();
                Task completed = await Task.WhenAny(route.Terminal.Task, activity, cancellationRequested, hardDeadline, idleDeadline, postCancellationDeadline).ConfigureAwait(false);
                if (ReferenceEquals(completed, route.Terminal.Task)) break;
                if (ReferenceEquals(completed, activity)) { while (route.Activity.Reader.TryRead(out _)) { } continue; }
                if (ReferenceEquals(completed, cancellationRequested))
                {
                    if (this.BeforePrunePostCancellationDeadlineForTesting is { } beforeDeadline)
                        await beforeDeadline().ConfigureAwait(false);
                    postCancellationDeadline = Task.Delay(this.PrunePostCancellationTimeoutForTesting);
                    cancellationRequested = Task.Delay(Timeout.InfiniteTimeSpan);
                    continue;
                }
                return await this.CompletePruneUnknownAsync(route).ConfigureAwait(false);
            }

            InstallerRecoveryPruneTerminalResult result = ProjectPruneTerminal(route, await route.Terminal.Task.ConfigureAwait(false));
            if (this.BeforePruneSettlementForTesting is { } beforeSettlement)
                await beforeSettlement().ConfigureAwait(false);
            bool acknowledgementConfirmed = true;
            if (route.CancellationTask is { } cancellation)
            {
                try { await cancellation.ConfigureAwait(false); }
                catch { acknowledgementConfirmed = false; route.MarkSettlementUnconfirmed(); }
            }
            route.CompleteProgress();
            bool cleanupConfirmed = await this.TryCleanupAfterPruneAsync(route, allowCleanExit: true).ConfigureAwait(false);
            return result with
            {
                BackendSettlement = acknowledgementConfirmed && cleanupConfirmed && !route.SettlementUnconfirmed
                    ? InstallerBackendSettlement.ConfirmedClosed
                    : InstallerBackendSettlement.Unconfirmed
            };
        }
        catch
        {
            return await this.CompletePruneUnknownAsync(route).ConfigureAwait(false);
        }
        finally
        {
            route.DisposeCallerCancellation();
        }
    }

    private async Task<InstallerRecoveryPruneResult> CompletePruneUnknownAsync(ActivePruneRoute route)
    {
        route.CompleteProgress();
        lock (this.ResponseLock) if (ReferenceEquals(this.ActivePrune, route)) this.ActivePrune = null;
        await this.TryCleanupAfterPruneAsync(route, allowCleanExit: false).ConfigureAwait(false);
        return new InstallerRecoveryPruneStateUnknownResult();
    }

    private async Task<bool> TryCleanupAfterPruneAsync(ActivePruneRoute route, bool allowCleanExit)
    {
        try { await this.CleanupAsync(allowCleanExit).ConfigureAwait(false); return this.CleanupConfirmed; }
        catch { route.MarkSettlementUnconfirmed(); Volatile.Write(ref this.CleanupUnconfirmed, 1); return false; }
    }

    private static InstallerRecoveryPruneTerminalResult ProjectPruneTerminal(ActivePruneRoute route, ProtocolEvent terminal)
    {
        ProtocolPruneOutcome outcome;
        ProtocolTerminalState state;
        ProtocolPruneSummary summary;
        switch (terminal)
        {
            case PruneSuccessEvent value: (outcome, state, summary) = (value.Outcome, value.TerminalState, value.PruneSummary); break;
            case PruneFailureEvent value: (outcome, state, summary) = (value.Outcome, value.TerminalState, value.PruneSummary); break;
            case PruneInterruptionEvent value: (outcome, state, summary) = (value.Outcome, value.TerminalState, value.PruneSummary); break;
            case PruneCancelledEvent value when route.CancellationRequested.Task.IsCompletedSuccessfully: (outcome, state, summary) = (value.Outcome, value.TerminalState, value.PruneSummary); break;
            default: throw new InstallerProtocolClientException("The installer backend returned an invalid recovery-cleanup terminal and was stopped.");
        }
        if (outcome == ProtocolPruneOutcome.UnexpectedCoreFailure)
            return new(outcome, state.DurableState, state.ErrorCode, state.RecoveryDisposition, state.NextAction, new(null, null, null, null), InstallerBackendSettlement.Unconfirmed);

        int logical = summary.LogicallyRemovedGenerationCount!.Value;
        int cleaned = summary.PhysicallyCleanedGenerationCount!.Value;
        int pending = summary.PendingCleanupGenerationCount!.Value;
        bool auxiliaryPending = summary.AuxiliaryCleanupPending!.Value;
        int removed = route.Binding.Plan.RemovedCount;
        int cleanup = route.Binding.Plan.CleanupCount;
        bool logicalPublished = logical > 0;
        if (
            logical is not 0 && logical != removed
            || cleaned > cleanup
            || pending > cleanup
            || cleaned > cleanup - pending
            || !logicalPublished && removed > 0 && cleaned > 0
            || auxiliaryPending && !route.Binding.Plan.AuxiliaryCleanupPlanned && (removed == 0 || logicalPublished)
        )
            throw new InstallerProtocolClientException("The installer backend returned impossible recovery-cleanup counters and was stopped.");

        int expectedAccounted = logicalPublished ? cleanup : cleanup - removed;
        bool requiresCompleteAccounting = outcome is
            ProtocolPruneOutcome.Interrupted
            or ProtocolPruneOutcome.CancelledWithCleanupPending
            or ProtocolPruneOutcome.FailedWithCleanupPending
            || outcome is ProtocolPruneOutcome.FailedBeforePublication or ProtocolPruneOutcome.CancelledBeforePublication
                && (pending > 0 || auxiliaryPending);
        if (requiresCompleteAccounting && cleaned + pending != expectedAccounted)
            throw new InstallerProtocolClientException("The installer backend returned incomplete recovery-cleanup accounting and was stopped.");
        if (
            outcome is ProtocolPruneOutcome.Succeeded or ProtocolPruneOutcome.CancelledAfterApply or ProtocolPruneOutcome.FailedAfterApply
            && (logical != removed || cleaned != cleanup || pending != 0 || auxiliaryPending)
        )
            throw new InstallerProtocolClientException("The installer backend returned incomplete after-apply recovery-cleanup counters and was stopped.");
        return new(outcome, state.DurableState, state.ErrorCode, state.RecoveryDisposition, state.NextAction, new(summary.LogicallyRemovedGenerationCount, summary.PhysicallyCleanedGenerationCount, summary.PendingCleanupGenerationCount, summary.AuxiliaryCleanupPending), InstallerBackendSettlement.Unconfirmed);
    }

    private static bool ValidatePrunePlan(PrunePlanEvent plan, ProtocolSessionId session, RetainedRecoveryCatalogBinding catalog, int retainNewest)
    {
        ProtocolRecoveryGeneration[] ordered = catalog.Points.OrderBy(pair => pair.Key.Ordinal).Select(pair => pair.Value).ToArray();
        ProtocolRecoverySelectionId[] retained = ordered.Take(retainNewest).Select(value => value.SelectionId).ToArray();
        ProtocolRecoveryGeneration[] removedGenerations = ordered.Skip(retainNewest).ToArray();
        ProtocolRecoverySelectionId[] removed = removedGenerations.Select(value => value.SelectionId).ToArray();
        string[] removedGenerationIds = removedGenerations.Select(value => value.GenerationId).ToArray();
        HashSet<string> retainedGenerationIds = ordered.Take(retainNewest).Select(value => value.GenerationId).ToHashSet(StringComparer.Ordinal);
        HashSet<string> cleanup = plan.CleanupGenerationIds.ToHashSet(StringComparer.Ordinal);
        return plan.SessionId == session
            && plan.CatalogId == catalog.CatalogId
            && plan.GameRoot.CanonicalPath == catalog.CanonicalGamePath
            && plan.GameRoot.DeviceMajor == catalog.GameRoot.DeviceMajor
            && plan.GameRoot.DeviceMinor == catalog.GameRoot.DeviceMinor
            && plan.GameRoot.Inode == catalog.GameRoot.Inode
            && plan.GameRoot.OperationGeneration == catalog.GameRoot.OperationGeneration
            && plan.HeadSha256 == catalog.HeadSha256
            && plan.RetainNewest == retainNewest
            && plan.RetainedSelectionIds.SequenceEqual(retained)
            && plan.RemovedSelectionIds.SequenceEqual(removed)
            && plan.CleanupGenerationIds.Take(removedGenerationIds.Length).SequenceEqual(removedGenerationIds)
            && plan.CleanupGenerationIds.Skip(removedGenerationIds.Length).All(value => !retainedGenerationIds.Contains(value))
            && cleanup.Count == plan.CleanupGenerationIds.Length
            && plan.Risks.SequenceEqual([ProtocolPlanRisk.RecoveryPrune])
            && plan.RecommendedDefault == ProtocolRecommendedDefault.Cancel
            && plan.RequiresConfirmation;
    }

    private static bool IsReachablePruneInspectionRejection(PrePlanRejectedEvent rejection) => rejection switch
    {
        { ErrorCode: ProtocolPrePlanErrorCode.NothingToPrune, NextAction: ProtocolNextAction.ListRecoveries, IsTerminal: false } => true,
        { ErrorCode: ProtocolPrePlanErrorCode.RequestCancelled, NextAction: ProtocolNextAction.RetryRequest, IsTerminal: false } => true,
        { ErrorCode: ProtocolPrePlanErrorCode.InvalidGameFolder, NextAction: ProtocolNextAction.SelectGameFolder, IsTerminal: false } => true,
        { ErrorCode: ProtocolPrePlanErrorCode.InspectionFailed, NextAction: ProtocolNextAction.InspectAgain, IsTerminal: false } => true,
        { ErrorCode: ProtocolPrePlanErrorCode.PermissionDenied, NextAction: ProtocolNextAction.ReviewFilesystem, IsTerminal: false } => true,
        { ErrorCode: ProtocolPrePlanErrorCode.UnexpectedFailure, NextAction: ProtocolNextAction.StartNewSession or ProtocolNextAction.ViewPrivateLog, IsTerminal: true } => true,
        _ => false
    };

    private sealed record RetainedPrunePlanBinding(ProtocolGameRootIdentity GameRoot, ProtocolPrunePlanId PlanId, ProtocolPlanDigest PlanDigest, int RemovedCount, int CleanupCount, bool AuxiliaryCleanupPlanned, InstallerRecoveryPruneConfirmation Confirmation);
    private sealed record RetainedConfirmedPruneBinding(RetainedPrunePlanBinding Plan, InstallerConfirmedRecoveryPruneAuthority Authority);

    private sealed class ActivePruneRoute
    {
        private long LastSequence = -1;
        private int ProgressEventCount;
        private long ProgressUtf8Bytes;
        private CancellationTokenRegistration CallerCancellation;
        private int SettlementUnconfirmedValue;
        private ProtocolCommandId? CancellationCommandId;
        private TaskCompletionSource<CommandAcknowledgedEvent>? CancellationAcknowledgement;
        public ProtocolSessionId SessionId { get; }
        public RetainedConfirmedPruneBinding Binding { get; }
        public ProtocolCommandId CommandId { get; }
        public int MaximumProgressEvents { get; }
        public long MaximumProgressUtf8Bytes { get; }
        public Channel<InstallerRecoveryPruneProgress> Progress { get; } = Channel.CreateBounded<InstallerRecoveryPruneProgress>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false });
        public Channel<bool> Activity { get; } = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false });
        public TaskCompletionSource<ProtocolEvent> Terminal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ExecuteWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? CancellationTask { get; set; }
        public bool SettlementUnconfirmed => Volatile.Read(ref this.SettlementUnconfirmedValue) != 0;
        public bool HasPendingCancellation => this.CancellationAcknowledgement is not null && !this.CancellationAcknowledgement.Task.IsCompleted;
        public ActivePruneRoute(ProtocolSessionId sessionId, RetainedConfirmedPruneBinding binding, ProtocolCommandId commandId, int maximumProgressEvents, long maximumProgressUtf8Bytes) { this.SessionId = sessionId; this.Binding = binding; this.CommandId = commandId; this.MaximumProgressEvents = maximumProgressEvents; this.MaximumProgressUtf8Bytes = maximumProgressUtf8Bytes; }
        public void AttachCallerCancellation(CancellationToken token, Action request) { if (token.CanBeCanceled) this.CallerCancellation = token.Register(request); }
        public void DisposeCallerCancellation() => this.CallerCancellation.Dispose();
        public void MarkExecuteWritten() => this.ExecuteWritten.TrySetResult(true);
        public void MarkExecuteWriteFailed() => this.ExecuteWritten.TrySetResult(false);
        public void MarkCancellationRequested() => this.CancellationRequested.TrySetResult();
        public void SignalActivity() => this.Activity.Writer.TryWrite(true);
        public void MarkSettlementUnconfirmed() => Volatile.Write(ref this.SettlementUnconfirmedValue, 1);
        public Task<CommandAcknowledgedEvent> InstallCancellationLane(ProtocolCommandId commandId) { if (this.CancellationCommandId is not null) throw new InstallerProtocolClientException("The recovery cleanup already has a cancellation command."); this.CancellationCommandId = commandId; this.CancellationAcknowledgement = new(TaskCreationOptions.RunContinuationsAsynchronously); return this.CancellationAcknowledgement.Task; }
        public bool IsCancellationCommand(ProtocolCommandId commandId) => this.CancellationCommandId == commandId;
        public bool TryAcceptCancellationAcknowledgement(CommandAcknowledgedEvent value, int utf8Bytes) { if (this.CancellationAcknowledgement is null || value.CommandId != this.CancellationCommandId || value.SessionId != this.SessionId || value.Acknowledgement != ProtocolAcknowledgementKind.PruneCancellationRequested || value.PlanId is not null || value.PrunePlanId != this.Binding.Plan.PlanId || !this.TryCountFrameBytes(utf8Bytes)) return false; this.SignalActivity(); return this.CancellationAcknowledgement.TrySetResult(value); }
        public bool TryAcceptProgress(PruneProgressEvent value, int utf8Bytes)
        {
            if (value.SessionId != this.SessionId || value.PrunePlanId != this.Binding.Plan.PlanId || value.PruneDigest != this.Binding.Plan.PlanDigest || value.CommandId != this.CommandId || value.Sequence <= this.LastSequence || value.CompletedUnits is < 0 or > MaximumPruneProgressUnits || value.TotalUnits is < 0 or > MaximumPruneProgressUnits || value.TotalUnits is { } total && value.CompletedUnits > total || utf8Bytes < 1) return false;
            try { this.LastSequence = value.Sequence; this.ProgressEventCount = checked(this.ProgressEventCount + 1); if (!this.TryCountFrameBytes(utf8Bytes)) return false; }
            catch { return false; }
            if (this.ProgressEventCount > this.MaximumProgressEvents) return false;
            this.Progress.Writer.TryWrite(new(value.Stage, checked((int)value.CompletedUnits), value.TotalUnits is { } bounded ? checked((int)bounded) : null)); this.SignalActivity(); return true;
        }
        public bool TryCountFrameBytes(int utf8Bytes) { if (utf8Bytes < 1) return false; try { this.ProgressUtf8Bytes = checked(this.ProgressUtf8Bytes + utf8Bytes); } catch { return false; } return this.ProgressUtf8Bytes <= this.MaximumProgressUtf8Bytes; }
        public bool IsExactTerminal(ProtocolEvent value) => value switch { PruneSuccessEvent terminal => this.IsExact(terminal.SessionId, terminal.PrunePlanId, terminal.PruneDigest, terminal.CommandId), PruneFailureEvent terminal => this.IsExact(terminal.SessionId, terminal.PrunePlanId, terminal.PruneDigest, terminal.CommandId), PruneInterruptionEvent terminal => this.IsExact(terminal.SessionId, terminal.PrunePlanId, terminal.PruneDigest, terminal.CommandId), PruneCancelledEvent terminal => this.IsExact(terminal.SessionId, terminal.PrunePlanId, terminal.PruneDigest, terminal.CommandId), _ => false };
        private bool IsExact(ProtocolSessionId session, ProtocolPrunePlanId plan, ProtocolPlanDigest digest, ProtocolCommandId command) => session == this.SessionId && plan == this.Binding.Plan.PlanId && digest == this.Binding.Plan.PlanDigest && command == this.CommandId;
        public void CompleteTerminal(ProtocolEvent terminal) { this.Progress.Writer.TryComplete(); this.Activity.Writer.TryComplete(); this.Terminal.TrySetResult(terminal); }
        public void Fail(Exception error) { this.MarkExecuteWriteFailed(); this.Progress.Writer.TryComplete(); this.Activity.Writer.TryComplete(); this.Terminal.TrySetException(error); this.CancellationAcknowledgement?.TrySetException(error); }
        public void CompleteProgress() { this.Progress.Writer.TryComplete(); this.Activity.Writer.TryComplete(); }
    }
}
