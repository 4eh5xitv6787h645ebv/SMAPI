namespace StardewModdingAPI.Installer.Core.Protocol.V1;

/// <summary>The validated lifecycle of one version 1 one-shot backend session.</summary>
public enum ProtocolSessionState
{
    AwaitingHandshake,
    Ready,
    PlanIssued,
    PlanConfirmed,
    Executing,
    CancellationRequested,
    Completed
}

/// <summary>Validates request ordering and binds confirmation and execution to exact random IDs.</summary>
public sealed class ProtocolSessionStateMachine
{
    private bool ExecutionStartedForCurrentPlan;

    /// <summary>The random identifier assigned to this process session.</summary>
    public ProtocolSessionId SessionId { get; } = ProtocolSessionId.CreateRandom();

    /// <summary>The current lifecycle state.</summary>
    public ProtocolSessionState State { get; private set; } = ProtocolSessionState.AwaitingHandshake;

    /// <summary>The current immutable plan ID, or <c>null</c> before inspection.</summary>
    public ProtocolPlanId? CurrentPlanId { get; private set; }

    /// <summary>The operation bound to the current immutable plan, or <c>null</c> before inspection.</summary>
    public InstallerOperation? CurrentOperation { get; private set; }

    /// <summary>The canonical execution-plan digest bound to the current plan, or <c>null</c> before inspection.</summary>
    public ProtocolPlanDigest? CurrentPlanDigest { get; private set; }

    /// <summary>Whether the current structured plan is free of blocking conflicts.</summary>
    public bool CurrentPlanCanExecute { get; private set; }

    /// <summary>The last accepted progress sequence, or <c>-1</c> before progress.</summary>
    public long LastProgressSequence { get; private set; } = -1;

    /// <summary>Accept the one initial handshake and return the server handshake event.</summary>
    public HandshakeEvent AcceptHandshake(HandshakeRequest request, string serverVersion, params string[] capabilities)
    {
        ArgumentNullException.ThrowIfNull(request);
        this.RequireState(ProtocolSessionState.AwaitingHandshake);

        HandshakeEvent response = new(this.SessionId, serverVersion, capabilities ?? []);
        ProtocolJsonSerializer.SerializeLine(request);
        ProtocolJsonSerializer.SerializeLine(response);
        this.State = ProtocolSessionState.Ready;
        return response;
    }

    /// <summary>Issue a new random plan, invalidating any earlier unexecuted plan.</summary>
    public PlanEvent IssuePlan(
        InspectPlanRequest request,
        ProtocolPlanDigest executionBindingDigest,
        ProtocolGameRootIdentity gameRoot,
        ProtocolReleaseIdentity? currentRelease,
        ProtocolReleaseIdentity? targetRelease,
        ObservedInstallState observedState,
        ProtocolPlanOperation[] operations,
        ProtocolPlanConflict[] conflicts,
        string summary,
        string[] warnings
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        if (this.State is not (ProtocolSessionState.Ready or ProtocolSessionState.PlanIssued or ProtocolSessionState.PlanConfirmed))
            throw new ProtocolException($"A plan can't be issued while the session is in state '{this.State}'.");
        this.RequireSession(request.SessionId);
        ProtocolJsonSerializer.SerializeLine(request);
        if (
            request.TargetPackageVersion is not null
            && (
                targetRelease is null
                || (
                    request.TargetPackageVersion != targetRelease.Tag
                    && request.TargetPackageVersion != targetRelease.EmbeddedVersion
                )
            )
        )
        {
            throw new ProtocolException("The inspected target package version doesn't match the exact target release identity.");
        }

        ProtocolPlanOperation[] operationSnapshot = operations?.ToArray()
            ?? throw new ProtocolException("The protocol 'operations' collection can't be null.");
        ProtocolPlanConflict[] conflictSnapshot = conflicts?.ToArray()
            ?? throw new ProtocolException("The protocol 'conflicts' collection can't be null.");
        string[] warningSnapshot = warnings?.ToArray()
            ?? throw new ProtocolException("The protocol 'warnings' collection can't be null.");

        ProtocolPlanId planId = ProtocolPlanId.CreateRandom();
        ProtocolPlanDigest planDigest = ProtocolPlanDigest.Compute(
            executionBindingDigest,
            request.Operation,
            gameRoot,
            currentRelease,
            targetRelease,
            observedState,
            operationSnapshot,
            conflictSnapshot
        );
        PlanEvent response = new(
            this.SessionId,
            planId,
            planDigest,
            executionBindingDigest,
            request.Operation,
            gameRoot,
            currentRelease,
            targetRelease,
            observedState,
            operationSnapshot,
            conflictSnapshot,
            summary,
            warningSnapshot,
            requiresConfirmation: true
        );
        ProtocolJsonSerializer.SerializeLine(response);

        this.CurrentPlanId = planId;
        this.CurrentPlanDigest = planDigest;
        this.CurrentOperation = request.Operation;
        this.CurrentPlanCanExecute = conflictSnapshot.Length == 0;
        this.ExecutionStartedForCurrentPlan = false;
        this.LastProgressSequence = -1;
        this.State = ProtocolSessionState.PlanIssued;
        return response;
    }

    /// <summary>Bind user confirmation to the exact current plan.</summary>
    public void ConfirmPlan(ConfirmPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        this.RequireState(ProtocolSessionState.PlanIssued);
        if (!this.CurrentPlanCanExecute)
            throw new ProtocolException("A plan with unresolved conflicts can't be confirmed.");
        this.RequireCurrentBinding(request.SessionId, request.PlanId, request.PlanDigest);
        ProtocolJsonSerializer.SerializeLine(request);
        this.State = ProtocolSessionState.PlanConfirmed;
    }

    /// <summary>Begin executing the exact plan which was confirmed.</summary>
    public void BeginExecution(ExecutePlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (this.State == ProtocolSessionState.PlanIssued)
            throw new ProtocolException("The current plan must be confirmed before execution.");
        this.RequireState(ProtocolSessionState.PlanConfirmed);
        this.RequireCurrentBinding(request.SessionId, request.PlanId, request.PlanDigest);
        ProtocolJsonSerializer.SerializeLine(request);
        this.State = ProtocolSessionState.Executing;
        this.ExecutionStartedForCurrentPlan = true;
    }

    /// <summary>Request cancellation of the exact current plan at a safe execution boundary.</summary>
    public void RequestCancellation(CancelPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (this.State is not (ProtocolSessionState.PlanIssued or ProtocolSessionState.PlanConfirmed or ProtocolSessionState.Executing))
            throw new ProtocolException($"Cancellation can't be requested while the session is in state '{this.State}'.");
        this.RequireCurrentBinding(request.SessionId, request.PlanId, request.PlanDigest);
        ProtocolJsonSerializer.SerializeLine(request);
        this.State = ProtocolSessionState.CancellationRequested;
    }

    /// <summary>Validate and record a monotonically increasing progress event.</summary>
    public void RecordProgress(ProgressEvent progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (this.State is not (ProtocolSessionState.Executing or ProtocolSessionState.CancellationRequested))
            throw new ProtocolException($"Progress can't be recorded while the session is in state '{this.State}'.");
        if (!this.ExecutionStartedForCurrentPlan)
            throw new ProtocolException("Progress can't be recorded for a plan cancelled before execution began.");
        this.RequireCurrentBinding(progress.SessionId, progress.PlanId, progress.PlanDigest);
        ProtocolJsonSerializer.SerializeLine(progress);
        if (progress.Sequence <= this.LastProgressSequence)
            throw new ProtocolException("Progress sequence values must increase monotonically.");
        this.LastProgressSequence = progress.Sequence;
    }

    /// <summary>Complete successfully after execution.</summary>
    public void Complete(SuccessEvent result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Operation != this.CurrentOperation)
            throw new ProtocolException("The success event operation doesn't match the current plan.");
        this.CompleteTerminal(result, result.SessionId, result.PlanId, result.PlanDigest, allowAfterCancellation: false);
    }

    /// <summary>Complete after a failed transaction was rolled back.</summary>
    public void Complete(RolledBackFailureEvent result)
    {
        ArgumentNullException.ThrowIfNull(result);
        this.CompleteTerminal(result, result.SessionId, result.PlanId, result.PlanDigest, allowAfterCancellation: true);
    }

    /// <summary>Complete with durable recovery information after an interruption.</summary>
    public void Complete(RecoverableInterruptionEvent result)
    {
        ArgumentNullException.ThrowIfNull(result);
        this.CompleteTerminal(result, result.SessionId, result.PlanId, result.PlanDigest, allowAfterCancellation: true);
    }

    /// <summary>Complete an accepted cancellation after the backend reached a verified safe state.</summary>
    public void Complete(CancelledEvent result)
    {
        ArgumentNullException.ThrowIfNull(result);
        this.RequireState(ProtocolSessionState.CancellationRequested);
        this.RequireCurrentBinding(result.SessionId, result.PlanId, result.PlanDigest);
        ProtocolJsonSerializer.SerializeLine(result);
        this.State = ProtocolSessionState.Completed;
    }

    private void CompleteTerminal(
        ProtocolEvent result,
        ProtocolSessionId sessionId,
        ProtocolPlanId planId,
        ProtocolPlanDigest planDigest,
        bool allowAfterCancellation
    )
    {
        ArgumentNullException.ThrowIfNull(result);
        if (this.State != ProtocolSessionState.Executing && !(allowAfterCancellation && this.State == ProtocolSessionState.CancellationRequested))
            throw new ProtocolException($"A terminal event can't be recorded while the session is in state '{this.State}'.");
        this.RequireCurrentBinding(sessionId, planId, planDigest);
        ProtocolJsonSerializer.SerializeLine(result);
        this.State = ProtocolSessionState.Completed;
    }

    private void RequireCurrentBinding(
        ProtocolSessionId sessionId,
        ProtocolPlanId planId,
        ProtocolPlanDigest planDigest
    )
    {
        this.RequireSession(sessionId);
        if (this.CurrentPlanId is not ProtocolPlanId current || planId != current)
            throw new ProtocolException("The request or event doesn't match the current plan ID; it may be stale.");
        if (this.CurrentPlanDigest is null || planDigest != this.CurrentPlanDigest)
            throw new ProtocolException("The request or event doesn't match the current execution-plan digest; it may be stale or altered.");
    }

    private void RequireSession(ProtocolSessionId sessionId)
    {
        if (sessionId != this.SessionId)
            throw new ProtocolException("The request or event doesn't match this process session ID.");
    }

    private void RequireState(ProtocolSessionState expected)
    {
        if (this.State != expected)
            throw new ProtocolException($"Expected protocol state '{expected}', but the session is in state '{this.State}'.");
    }
}
