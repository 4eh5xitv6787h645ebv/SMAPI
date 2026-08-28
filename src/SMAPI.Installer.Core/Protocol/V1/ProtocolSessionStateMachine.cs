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
    /// <summary>The random identifier assigned to this process session.</summary>
    public ProtocolSessionId SessionId { get; } = ProtocolSessionId.CreateRandom();

    /// <summary>The current lifecycle state.</summary>
    public ProtocolSessionState State { get; private set; } = ProtocolSessionState.AwaitingHandshake;

    /// <summary>The current immutable plan ID, or <c>null</c> before inspection.</summary>
    public ProtocolPlanId? CurrentPlanId { get; private set; }

    /// <summary>The operation bound to the current immutable plan, or <c>null</c> before inspection.</summary>
    public InstallerOperation? CurrentOperation { get; private set; }

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
        ObservedInstallState observedState,
        string summary,
        string[] warnings
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        if (this.State is not (ProtocolSessionState.Ready or ProtocolSessionState.PlanIssued or ProtocolSessionState.PlanConfirmed))
            throw new ProtocolException($"A plan can't be issued while the session is in state '{this.State}'.");
        this.RequireSession(request.SessionId);
        ProtocolJsonSerializer.SerializeLine(request);

        ProtocolPlanId planId = ProtocolPlanId.CreateRandom();
        PlanEvent response = new(
            this.SessionId,
            planId,
            request.Operation,
            request.GamePath,
            observedState,
            summary,
            warnings,
            RequiresConfirmation: true
        );
        ProtocolJsonSerializer.SerializeLine(response);

        this.CurrentPlanId = planId;
        this.CurrentOperation = request.Operation;
        this.LastProgressSequence = -1;
        this.State = ProtocolSessionState.PlanIssued;
        return response;
    }

    /// <summary>Bind user confirmation to the exact current plan.</summary>
    public void ConfirmPlan(ConfirmPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        this.RequireState(ProtocolSessionState.PlanIssued);
        this.RequireCurrentIds(request.SessionId, request.PlanId);
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
        this.RequireCurrentIds(request.SessionId, request.PlanId);
        ProtocolJsonSerializer.SerializeLine(request);
        this.State = ProtocolSessionState.Executing;
    }

    /// <summary>Request cancellation of the exact current plan at a safe execution boundary.</summary>
    public void RequestCancellation(CancelPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (this.State is not (ProtocolSessionState.PlanIssued or ProtocolSessionState.PlanConfirmed or ProtocolSessionState.Executing))
            throw new ProtocolException($"Cancellation can't be requested while the session is in state '{this.State}'.");
        this.RequireCurrentIds(request.SessionId, request.PlanId);
        ProtocolJsonSerializer.SerializeLine(request);
        this.State = ProtocolSessionState.CancellationRequested;
    }

    /// <summary>Validate and record a monotonically increasing progress event.</summary>
    public void RecordProgress(ProgressEvent progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (this.State is not (ProtocolSessionState.Executing or ProtocolSessionState.CancellationRequested))
            throw new ProtocolException($"Progress can't be recorded while the session is in state '{this.State}'.");
        this.RequireCurrentIds(progress.SessionId, progress.PlanId);
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
        this.CompleteTerminal(result, result.SessionId, result.PlanId);
    }

    /// <summary>Complete after a failed transaction was rolled back.</summary>
    public void Complete(RolledBackFailureEvent result)
    {
        this.CompleteTerminal(result, result.SessionId, result.PlanId);
    }

    /// <summary>Complete with durable recovery information after an interruption.</summary>
    public void Complete(RecoverableInterruptionEvent result)
    {
        this.CompleteTerminal(result, result.SessionId, result.PlanId);
    }

    private void CompleteTerminal(ProtocolEvent result, ProtocolSessionId sessionId, ProtocolPlanId planId)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (this.State is not (ProtocolSessionState.Executing or ProtocolSessionState.CancellationRequested))
            throw new ProtocolException($"A terminal event can't be recorded while the session is in state '{this.State}'.");
        this.RequireCurrentIds(sessionId, planId);
        ProtocolJsonSerializer.SerializeLine(result);
        this.State = ProtocolSessionState.Completed;
    }

    private void RequireCurrentIds(ProtocolSessionId sessionId, ProtocolPlanId planId)
    {
        this.RequireSession(sessionId);
        if (this.CurrentPlanId is not ProtocolPlanId current || planId != current)
            throw new ProtocolException("The request or event doesn't match the current plan ID; it may be stale.");
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
