using System;
using System.Threading;
using System.Threading.Tasks;

namespace StardewModdingAPI.Framework.Health;

/// <summary>A bounded single-consumer export queue for frozen mod health requests.</summary>
internal sealed class ModHealthExportQueue : IModHealthExportQueue, IDisposable
{
    private readonly object SyncRoot = new();
    private readonly AutoResetEvent WorkAvailable = new(initialState: false);
    private readonly CancellationTokenSource Cancellation = new();
    private readonly Func<ModHealthExportRequest, bool, ModHealthReportPayload> BuildPayload;
    private readonly IModHealthReportPublisher Publisher;
    private readonly ModHealthPreparedReportStore PreparedReports;
    private readonly Action<ModHealthExportStatus>? OnCompleted;
    private readonly Thread Worker;
    private readonly TimeSpan ShutdownTimeout;

    private ModHealthExportRequest? WritingRequest;
    private ModHealthExportRequest? PendingRequest;
    private bool PendingIsRetry;
    private ModHealthExportRequest? RetryableRequest;
    private bool RetryScheduled;
    private ModHealthExportStatus LatestStatus = ModHealthExportStatus.None;
    private ModHealthExportStatus? PreviousStatus;
    private TaskCompletionSource<bool> Drained = ModHealthExportQueue.CreateCompletionSource(completed: true);
    private bool DisposeRequested;
    private bool CompletionCallbackInProgress;

    public ModHealthExportQueue(Func<ModHealthExportRequest, bool, ModHealthReportPayload> buildPayload, IModHealthReportPublisher publisher, string workerName = "SMAPI mod health report writer", TimeSpan? shutdownTimeout = null, Action<ModHealthExportStatus>? onCompleted = null, ModHealthPreparedReportStore? preparedReports = null)
    {
        this.BuildPayload = buildPayload ?? throw new ArgumentNullException(nameof(buildPayload));
        this.Publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        this.PreparedReports = preparedReports ?? new ModHealthPreparedReportStore();
        this.OnCompleted = onCompleted;
        this.ShutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(2);
        if (this.ShutdownTimeout < TimeSpan.Zero && this.ShutdownTimeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(shutdownTimeout));
        this.Worker = new Thread(this.Run)
        {
            IsBackground = true,
            Name = workerName
        };
        this.Worker.Start();
    }

    /// <inheritdoc />
    public ModHealthExportQueueResult Enqueue(ModHealthExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (this.SyncRoot)
        {
            this.ThrowIfDisposed();
            ModHealthExportStatus? existing = this.FindStatus(request.RequestId);
            if (existing?.State == ModHealthExportState.Succeeded)
                return new(ModHealthExportDisposition.AlreadySucceeded, existing);
            if (this.WritingRequest?.RequestId == request.RequestId || this.PendingRequest?.RequestId == request.RequestId)
                return new(ModHealthExportDisposition.Coalesced, existing!);

            if (this.PendingRequest is null)
            {
                bool queuedDirectly = this.WritingRequest is null;
                this.AcceptPending(request);
                return new(queuedDirectly ? ModHealthExportDisposition.Queued : ModHealthExportDisposition.Pending, this.LatestStatus);
            }

            if (request.IsFinal && !this.PendingRequest.IsFinal)
            {
                if (this.PendingIsRetry && this.RetryableRequest?.RequestId == this.PendingRequest.RequestId)
                {
                    this.RetryScheduled = false;
                    this.PreparedReports.RetryDeferred(this.PendingRequest.RequestId, this.PendingRequest.IsFinal);
                }
                else
                    this.PreparedReports.Superseded(this.PendingRequest.RequestId, this.PendingRequest.IsFinal, request.RequestId);
                this.PreviousStatus = new(ModHealthExportState.Failed, this.PendingRequest.RequestId, this.PendingRequest.IsFinal, Error: "Superseded by the final report.");
                this.PendingRequest = request;
                this.PendingIsRetry = false;
                this.LatestStatus = CreateQueuedStatus(request);
                this.PreparedReports.Begin(request, isRetry: false);
                return new(ModHealthExportDisposition.Coalesced, this.LatestStatus);
            }

            return new(ModHealthExportDisposition.RejectedBusy, this.LatestStatus);
        }
    }

    /// <inheritdoc />
    public ModHealthExportQueueResult Retry(Guid? requestId = null)
    {
        lock (this.SyncRoot)
        {
            this.ThrowIfDisposed();
            if (this.RetryableRequest is null || (requestId.HasValue && this.RetryableRequest.RequestId != requestId.Value))
                return new(ModHealthExportDisposition.NoRetryableExport, ModHealthExportStatus.None);
            if (this.RetryScheduled)
                return new(ModHealthExportDisposition.Coalesced, this.FindStatus(this.RetryableRequest.RequestId) ?? CreateQueuedStatus(this.RetryableRequest));
            if (this.PendingRequest is not null)
                return new(ModHealthExportDisposition.RejectedBusy, this.LatestStatus);

            bool queuedDirectly = this.WritingRequest is null;
            this.RetryScheduled = true;
            this.AcceptPending(this.RetryableRequest, isRetry: true);
            return new(queuedDirectly ? ModHealthExportDisposition.Retried : ModHealthExportDisposition.Pending, this.LatestStatus);
        }
    }

    /// <inheritdoc />
    public void DiscardRetryable(Guid? requestId = null)
    {
        lock (this.SyncRoot)
        {
            if (this.RetryableRequest is null || (requestId.HasValue && this.RetryableRequest.RequestId != requestId.Value))
                return;
            if (this.RetryScheduled)
                throw new InvalidOperationException("The retryable export is already queued or writing.");
            this.PreparedReports.DiscardRetryable(this.RetryableRequest.RequestId);
            this.RetryableRequest = null;
        }
    }

    /// <inheritdoc />
    public ModHealthExportStatus GetStatus(Guid? requestId = null)
    {
        lock (this.SyncRoot)
            return requestId is null ? this.LatestStatus : this.FindStatus(requestId.Value) ?? ModHealthExportStatus.None;
    }

    /// <inheritdoc />
    public ModHealthPreparedReportSnapshot GetPreparedReport(Guid? requestId = null)
    {
        lock (this.SyncRoot)
            return this.PreparedReports.Get(requestId);
    }

    /// <summary>Wait until the current write and pending slot are empty.</summary>
    public async Task<bool> DrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        Task drained;
        lock (this.SyncRoot)
            drained = this.Drained.Task;
        if (drained.IsCompleted)
            return true;

        using CancellationTokenSource timeoutSource = timeout == Timeout.InfiniteTimeSpan
            ? new CancellationTokenSource()
            : new CancellationTokenSource(timeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token, cancellationToken);
        Task cancelled = Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
        Task completed = await Task.WhenAny(drained, cancelled).ConfigureAwait(false);
        if (completed == drained)
        {
            linked.Cancel();
            await drained.ConfigureAwait(false);
            return true;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    /// <summary>Cancel queued/writing work and stop the dedicated consumer.</summary>
    public void Dispose()
    {
        lock (this.SyncRoot)
        {
            if (this.DisposeRequested)
                return;
            this.DisposeRequested = true;
            this.Cancellation.Cancel();
            if (this.PendingRequest is not null)
            {
                this.PreviousStatus = new(ModHealthExportState.Failed, this.PendingRequest.RequestId, this.PendingRequest.IsFinal, Error: "Report export was cancelled during shutdown.");
                this.PreparedReports.Canceled(this.PendingRequest.RequestId, this.PendingRequest.IsFinal, "Report export was canceled during shutdown.");
                this.PendingRequest = null;
                this.PendingIsRetry = false;
            }
            if (this.WritingRequest is not null)
                this.PreparedReports.Canceled(this.WritingRequest.RequestId, this.WritingRequest.IsFinal, "Report export was canceled during shutdown.");
            this.PreparedReports.Dispose();
            this.CompleteDrainIfIdle();
            // Signal before releasing the state lock. The worker must acquire this same lock to
            // observe DisposeRequested and exit, so it can't dispose the event before this Set.
            this.WorkAvailable.Set();
        }
        this.Worker.Join(this.ShutdownTimeout);
    }

    private void Run()
    {
        try
        {
            this.RunCore();
        }
        finally
        {
            try
            {
                (this.Publisher as IDisposable)?.Dispose();
            }
            catch
            {
                // Resource cleanup must not terminate the process from the background worker.
            }
            this.WorkAvailable.Dispose();
            this.Cancellation.Dispose();
        }
    }

    private void RunCore()
    {
        while (true)
        {
            ModHealthExportRequest? request;
            bool isRetry = false;
            lock (this.SyncRoot)
            {
                if (this.DisposeRequested && this.PendingRequest is null)
                    return;
                request = this.PendingRequest;
                if (request is not null)
                {
                    this.PendingRequest = null;
                    isRetry = this.PendingIsRetry;
                    this.PendingIsRetry = false;
                    this.WritingRequest = request;
                    this.LatestStatus = new(ModHealthExportState.Writing, request.RequestId, request.IsFinal);
                }
            }

            if (request is null)
            {
                this.WorkAvailable.WaitOne();
                continue;
            }

            ModHealthExportStatus result;
            ModHealthReportPayload? payload = null;
            try
            {
                this.Cancellation.Token.ThrowIfCancellationRequested();
                payload = this.BuildPayload(request, isRetry);
                if (!this.PreparedReports.PublishReady(request.RequestId, request.IsFinal, payload.Model))
                {
                    this.Cancellation.Token.ThrowIfCancellationRequested();
                    throw new ObjectDisposedException(nameof(ModHealthPreparedReportStore));
                }
                this.Cancellation.Token.ThrowIfCancellationRequested();
                ModHealthPublishedReport published = this.Publisher.Publish(request, payload, this.Cancellation.Token);
                result = new(
                    ModHealthExportState.Succeeded,
                    request.RequestId,
                    request.IsFinal,
                    published.TextPath,
                    published.JsonPath,
                    Summary: published.Summary ?? ModHealthCompletionSummary.FromReport(payload.Model)
                );
            }
            catch (OperationCanceledException) when (this.Cancellation.IsCancellationRequested)
            {
                result = new(ModHealthExportState.Failed, request.RequestId, request.IsFinal, Error: "Report export was cancelled during shutdown.");
            }
            catch (Exception ex)
            {
                result = new(ModHealthExportState.Failed, request.RequestId, request.IsFinal, Error: $"Report export failed ({ex.GetType().Name}).");
            }

            lock (this.SyncRoot)
            {
                this.WritingRequest = null;
                if (this.PendingRequest is not null)
                {
                    this.PreviousStatus = result;
                    this.LatestStatus = CreateQueuedStatus(this.PendingRequest);
                }
                else
                    this.LatestStatus = result;
                if (result.State == ModHealthExportState.Failed && !this.DisposeRequested)
                {
                    this.RetryableRequest = request;
                    this.RetryScheduled = false;
                }
                else if (this.RetryableRequest?.RequestId == request.RequestId)
                {
                    this.RetryableRequest = null;
                    this.RetryScheduled = false;
                }

                if (this.DisposeRequested || this.Cancellation.IsCancellationRequested)
                    this.PreparedReports.Canceled(request.RequestId, request.IsFinal, "Report export was canceled during shutdown.");
                else if (result.State == ModHealthExportState.Succeeded)
                    this.PreparedReports.Saved(request.RequestId, request.IsFinal, result.TextPath, result.JsonPath);
                else if (payload is null)
                    this.PreparedReports.FailedBeforeModel(request.RequestId, request.IsFinal, result.Error ?? "Report preparation failed.");
                else
                    this.PreparedReports.WriteFailed(request.RequestId, request.IsFinal, result.Error ?? "Report write failed.");
            }

            this.NotifyCompleted(result);

            lock (this.SyncRoot)
                this.CompleteDrainIfIdle();
        }
    }

    private void AcceptPending(ModHealthExportRequest request, bool isRetry = false)
    {
        if (this.WritingRequest is null && this.PendingRequest is null && this.Drained.Task.IsCompleted)
            this.Drained = ModHealthExportQueue.CreateCompletionSource(completed: false);
        this.PendingRequest = request;
        this.PendingIsRetry = isRetry;
        this.LatestStatus = CreateQueuedStatus(request);
        this.PreparedReports.Begin(request, isRetry);
        this.WorkAvailable.Set();
    }

    private ModHealthExportStatus? FindStatus(Guid requestId)
    {
        if (this.WritingRequest?.RequestId == requestId)
            return new(ModHealthExportState.Writing, requestId, this.WritingRequest.IsFinal);
        if (this.PendingRequest?.RequestId == requestId)
            return CreateQueuedStatus(this.PendingRequest);
        if (this.LatestStatus.RequestId == requestId)
            return this.LatestStatus;
        if (this.PreviousStatus?.RequestId == requestId)
            return this.PreviousStatus;
        if (this.RetryableRequest?.RequestId == requestId)
            return new(ModHealthExportState.Failed, requestId, this.RetryableRequest.IsFinal, Error: "Report export failed and can be retried.");
        return null;
    }

    private void CompleteDrainIfIdle()
    {
        if (this.WritingRequest is null && this.PendingRequest is null && !this.CompletionCallbackInProgress)
            this.Drained.TrySetResult(true);
    }

    /// <summary>Notify the consumer unless shutdown claimed the callback boundary first.</summary>
    private void NotifyCompleted(ModHealthExportStatus result)
    {
        if (this.OnCompleted is null)
            return;

        lock (this.SyncRoot)
        {
            if (this.DisposeRequested)
                return;
            this.CompletionCallbackInProgress = true;
        }

        try
        {
            this.OnCompleted(result);
        }
        catch
        {
            // Completion notifications are informational and must never stop the writer.
        }
        finally
        {
            lock (this.SyncRoot)
                this.CompletionCallbackInProgress = false;
        }
    }

    private void ThrowIfDisposed()
    {
        if (this.DisposeRequested)
            throw new ObjectDisposedException(nameof(ModHealthExportQueue));
    }

    private static ModHealthExportStatus CreateQueuedStatus(ModHealthExportRequest request)
    {
        return new(ModHealthExportState.Queued, request.RequestId, request.IsFinal);
    }

    private static TaskCompletionSource<bool> CreateCompletionSource(bool completed)
    {
        TaskCompletionSource<bool> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed)
            source.SetResult(true);
        return source;
    }
}
