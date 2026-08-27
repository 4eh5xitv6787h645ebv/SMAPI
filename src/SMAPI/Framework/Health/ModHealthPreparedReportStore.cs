using System;
using System.Collections.Generic;
using System.Linq;

namespace StardewModdingAPI.Framework.Health;

/// <summary>The presentation-safe lifecycle state for one exact report request.</summary>
internal enum ModHealthPreparedReportState
{
    Absent,
    Preparing,
    ReadyBeforeWrite,
    Saved,
    WriteFailed,
    FailedBeforeModel,
    Retrying,
    Superseded,
    Canceled,
    Disposed,
    Rejected
}

/// <summary>An immutable exact-request view of the single prepared report model.</summary>
internal sealed record ModHealthPreparedReportSnapshot(
    ModHealthPreparedReportState State,
    Guid? RequestId = null,
    bool IsFinal = false,
    ModHealthReport? Model = null,
    string? TextPath = null,
    string? JsonPath = null,
    string? Error = null,
    Guid? NewerRequestId = null
)
{
    public static ModHealthPreparedReportSnapshot Absent { get; } = new(ModHealthPreparedReportState.Absent);
}

/// <summary>Retains at most one immutable prepared report model plus a small bounded set of exact request tombstones.</summary>
internal sealed class ModHealthPreparedReportStore : IDisposable
{
    private const int MaximumRetainedRequestStates = 8;

    private readonly object SyncRoot = new();
    private readonly Dictionary<Guid, RequestState> States = new();

    private Guid? PreparedRequestId;
    private ModHealthReport? PreparedModel;
    private Guid? RetryableRequestId;
    private long NextSequence;
    private bool IsDisposed;

    /// <summary>Record an accepted request before its model is built.</summary>
    public void Begin(ModHealthExportRequest request, bool isRetry)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (this.SyncRoot)
        {
            if (this.IsDisposed)
                return;

            bool hasModel = isRetry && this.PreparedRequestId == request.RequestId && this.PreparedModel is not null;
            Guid? newerRequestId = null;
            if (isRetry && this.States.TryGetValue(request.RequestId, out RequestState? existing))
            {
                newerRequestId = existing.NewerRequestId;
                if (this.PreparedRequestId is Guid preparedId
                    && preparedId != request.RequestId
                    && this.States.TryGetValue(preparedId, out RequestState? prepared)
                    && prepared.Sequence > existing.Sequence)
                {
                    newerRequestId = preparedId;
                }
            }
            this.SetState(request.RequestId, request.IsFinal, isRetry ? ModHealthPreparedReportState.Retrying : ModHealthPreparedReportState.Preparing, hasModel, newerRequestId: newerRequestId, advanceSequence: !isRetry);
        }
    }

    /// <summary>Publish a fully built, sanitized, analyzed, and pruned model before filesystem publication begins.</summary>
    /// <returns>Whether the model was accepted. A disposed store rejects late worker publication.</returns>
    public bool PublishReady(Guid requestId, bool isFinal, ModHealthReport model)
    {
        ArgumentNullException.ThrowIfNull(model);
        lock (this.SyncRoot)
        {
            if (this.IsDisposed)
                return false;

            if (this.PreparedRequestId is Guid previousId && previousId != requestId)
            {
                RequestState previous = this.GetOrCreateState(previousId, isFinal: false);
                RequestState incoming = this.GetOrCreateState(requestId, isFinal);
                if (previous.Sequence > incoming.Sequence)
                {
                    // An exact retry keeps its original acceptance order. It may still publish
                    // its frozen artifacts, but it must not make an older report the semantic
                    // successor of the genuinely newer model retained for viewing.
                    this.SetState(requestId, isFinal, ModHealthPreparedReportState.Superseded, newerRequestId: previousId);
                    return true;
                }
                this.States[previousId] = previous with
                {
                    State = ModHealthPreparedReportState.Superseded,
                    ModelAvailable = false,
                    NewerRequestId = requestId,
                    Sequence = previous.Sequence
                };
            }

            this.PreparedRequestId = requestId;
            this.PreparedModel = model;
            this.SetState(requestId, isFinal, ModHealthPreparedReportState.ReadyBeforeWrite, modelAvailable: true);
            return true;
        }
    }

    /// <summary>Record successful publication with stable relative artifact paths.</summary>
    public void Saved(Guid requestId, bool isFinal, string? textPath, string? jsonPath)
    {
        lock (this.SyncRoot)
        {
            if (this.IsDisposed)
                return;
            if (this.States.TryGetValue(requestId, out RequestState? existing) && existing.State == ModHealthPreparedReportState.Superseded && existing.NewerRequestId is not null)
            {
                this.States[requestId] = existing with { TextPath = textPath, JsonPath = jsonPath };
                if (this.RetryableRequestId == requestId)
                    this.RetryableRequestId = null;
                return;
            }
            bool hasModel = this.PreparedRequestId == requestId && this.PreparedModel is not null;
            this.SetState(requestId, isFinal, ModHealthPreparedReportState.Saved, hasModel, textPath, jsonPath);
            if (this.RetryableRequestId == requestId)
                this.RetryableRequestId = null;
        }
    }

    /// <summary>Record a failure after model creation, retaining the exact prepared model when still current.</summary>
    public void WriteFailed(Guid requestId, bool isFinal, string error)
    {
        lock (this.SyncRoot)
        {
            if (this.IsDisposed)
                return;
            bool hasModel = this.PreparedRequestId == requestId && this.PreparedModel is not null;
            if (!hasModel && this.TryKeepSupersededFailure(requestId, isFinal, error))
                return;
            this.SetState(requestId, isFinal, hasModel ? ModHealthPreparedReportState.WriteFailed : ModHealthPreparedReportState.FailedBeforeModel, hasModel, error: error);
            this.RetryableRequestId = requestId;
        }
    }

    /// <summary>Record a failure before any model was created.</summary>
    public void FailedBeforeModel(Guid requestId, bool isFinal, string error)
    {
        lock (this.SyncRoot)
        {
            if (this.IsDisposed)
                return;
            // A retry can fail while rebuilding an already prepared model. Keep that exact
            // model visible and retryable instead of misrepresenting the request as model-less.
            bool hasModel = this.PreparedRequestId == requestId && this.PreparedModel is not null;
            if (!hasModel && this.TryKeepSupersededFailure(requestId, isFinal, error))
                return;
            this.SetState(requestId, isFinal, hasModel ? ModHealthPreparedReportState.WriteFailed : ModHealthPreparedReportState.FailedBeforeModel, hasModel, error: error);
            this.RetryableRequestId = requestId;
        }
    }

    /// <summary>Record that a pending request was replaced by a newer final request.</summary>
    public void Superseded(Guid requestId, bool isFinal, Guid newerRequestId)
    {
        lock (this.SyncRoot)
        {
            if (this.IsDisposed)
                return;
            this.SetState(requestId, isFinal, ModHealthPreparedReportState.Superseded, newerRequestId: newerRequestId);
        }
    }

    /// <summary>Restore a failed request's visible state when only its scheduled retry was superseded.</summary>
    public void RetryDeferred(Guid requestId, bool isFinal)
    {
        lock (this.SyncRoot)
        {
            if (this.IsDisposed)
                return;
            bool hasModel = this.PreparedRequestId == requestId && this.PreparedModel is not null;
            this.SetState(requestId, isFinal, hasModel ? ModHealthPreparedReportState.WriteFailed : ModHealthPreparedReportState.FailedBeforeModel, hasModel, error: "Report export failed and can be retried.");
            this.RetryableRequestId = requestId;
        }
    }

    /// <summary>Record cancellation without retaining a potentially stale model.</summary>
    public void Canceled(Guid requestId, bool isFinal, string error)
    {
        lock (this.SyncRoot)
        {
            if (this.IsDisposed)
                return;
            if (this.PreparedRequestId == requestId)
                this.ClearPreparedModel();
            this.SetState(requestId, isFinal, ModHealthPreparedReportState.Canceled, error: error);
        }
    }

    /// <summary>Discard the exact failed request retained for retry, without affecting another prepared model.</summary>
    public void DiscardRetryable(Guid requestId)
    {
        lock (this.SyncRoot)
        {
            if (this.RetryableRequestId != requestId)
                return;
            this.RetryableRequestId = null;
            this.States.Remove(requestId);
            if (this.PreparedRequestId == requestId)
                this.ClearPreparedModel();
        }
    }

    /// <summary>Get the latest request state, or one exact request without substituting another model.</summary>
    public ModHealthPreparedReportSnapshot Get(Guid? requestId = null)
    {
        lock (this.SyncRoot)
        {
            if (this.IsDisposed)
            {
                if (requestId is Guid canceledId && this.States.TryGetValue(canceledId, out RequestState? canceled) && canceled.State == ModHealthPreparedReportState.Canceled)
                    return this.CreateSnapshot(canceled);
                return new(ModHealthPreparedReportState.Disposed, requestId);
            }

            RequestState? state;
            if (requestId is Guid exactId)
                this.States.TryGetValue(exactId, out state);
            else
                state = this.States.Values.OrderByDescending(candidate => candidate.Sequence).FirstOrDefault();
            return state is null ? requestId is null ? ModHealthPreparedReportSnapshot.Absent : new(ModHealthPreparedReportState.Absent, requestId) : this.CreateSnapshot(state);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (this.SyncRoot)
        {
            if (this.IsDisposed)
                return;
            foreach ((Guid requestId, RequestState state) in this.States.ToArray())
            {
                if (state.State is ModHealthPreparedReportState.Preparing or ModHealthPreparedReportState.ReadyBeforeWrite or ModHealthPreparedReportState.Retrying)
                    this.States[requestId] = state with { State = ModHealthPreparedReportState.Canceled, ModelAvailable = false, Error = "Report export was canceled during shutdown." };
            }
            this.ClearPreparedModel();
            this.RetryableRequestId = null;
            this.IsDisposed = true;
        }
    }

    private ModHealthPreparedReportSnapshot CreateSnapshot(RequestState state)
    {
        ModHealthReport? model = state.ModelAvailable && this.PreparedRequestId == state.RequestId
            ? this.PreparedModel
            : null;
        return new(state.State, state.RequestId, state.IsFinal, model, state.TextPath, state.JsonPath, state.Error, state.NewerRequestId);
    }

    private RequestState GetOrCreateState(Guid requestId, bool isFinal)
    {
        return this.States.TryGetValue(requestId, out RequestState? state)
            ? state
            : new RequestState(requestId, isFinal, ModHealthPreparedReportState.Absent, false, null, null, null, null, this.NextSequence++);
    }

    private void SetState(Guid requestId, bool isFinal, ModHealthPreparedReportState state, bool modelAvailable = false, string? textPath = null, string? jsonPath = null, string? error = null, Guid? newerRequestId = null, bool advanceSequence = false)
    {
        long sequence = advanceSequence || !this.States.TryGetValue(requestId, out RequestState? existing)
            ? this.NextSequence++
            : existing.Sequence;
        this.States[requestId] = new(requestId, isFinal, state, modelAvailable, textPath, jsonPath, error, newerRequestId, sequence);
        this.TrimStates();
    }

    private void TrimStates()
    {
        while (this.States.Count > MaximumRetainedRequestStates)
        {
            Guid? removable = this.States.Values
                .Where(state => state.RequestId != this.PreparedRequestId && state.RequestId != this.RetryableRequestId)
                .Where(state => state.State is not (ModHealthPreparedReportState.Preparing or ModHealthPreparedReportState.ReadyBeforeWrite or ModHealthPreparedReportState.Retrying))
                .OrderBy(state => state.Sequence)
                .Select(state => (Guid?)state.RequestId)
                .FirstOrDefault();
            if (removable is not Guid requestId)
                return;
            this.States.Remove(requestId);
        }
    }

    private bool TryKeepSupersededFailure(Guid requestId, bool isFinal, string error)
    {
        if (!this.States.TryGetValue(requestId, out RequestState? existing) || existing.NewerRequestId is not Guid newerRequestId)
            return false;
        this.SetState(requestId, isFinal, ModHealthPreparedReportState.Superseded, error: error, newerRequestId: newerRequestId);
        this.RetryableRequestId = requestId;
        return true;
    }

    private void ClearPreparedModel()
    {
        this.PreparedRequestId = null;
        this.PreparedModel = null;
    }

    private sealed record RequestState(
        Guid RequestId,
        bool IsFinal,
        ModHealthPreparedReportState State,
        bool ModelAvailable,
        string? TextPath,
        string? JsonPath,
        string? Error,
        Guid? NewerRequestId,
        long Sequence
    );
}
