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
    private readonly Func<ModHealthExportRequest, ModHealthReportPayload> BuildPayload;
    private readonly IModHealthReportPublisher Publisher;
    private readonly Action<ModHealthExportStatus>? OnCompleted;
    private readonly Thread Worker;
    private readonly TimeSpan ShutdownTimeout;

    private ModHealthExportRequest? WritingRequest;
    private ModHealthExportRequest? PendingRequest;
    private ModHealthExportRequest? RetryableRequest;
    private bool RetryScheduled;
    private ModHealthExportStatus LatestStatus = ModHealthExportStatus.None;
    private ModHealthExportStatus? PreviousStatus;
    private TaskCompletionSource<bool> Drained = ModHealthExportQueue.CreateCompletionSource(completed: true);
    private bool DisposeRequested;

    public ModHealthExportQueue(Func<ModHealthExportRequest, ModHealthReportPayload> buildPayload, IModHealthReportPublisher publisher, string workerName = "SMAPI mod health report writer", TimeSpan? shutdownTimeout = null, Action<ModHealthExportStatus>? onCompleted = null)
    {
        this.BuildPayload = buildPayload ?? throw new ArgumentNullException(nameof(buildPayload));
        this.Publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
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
                this.PreviousStatus = new(ModHealthExportState.Failed, this.PendingRequest.RequestId, this.PendingRequest.IsFinal, Error: "Superseded by the final report.");
                this.PendingRequest = request;
                this.LatestStatus = CreateQueuedStatus(request);
                return new(ModHealthExportDisposition.Coalesced, this.LatestStatus);
            }

            return new(ModHealthExportDisposition.RejectedBusy, this.LatestStatus);
        }
    }

    /// <inheritdoc />
    public ModHealthExportQueueResult Retry()
    {
        lock (this.SyncRoot)
        {
            this.ThrowIfDisposed();
            if (this.RetryableRequest is null)
                return new(ModHealthExportDisposition.NoRetryableExport, ModHealthExportStatus.None);
            if (this.RetryScheduled)
                return new(ModHealthExportDisposition.Coalesced, this.FindStatus(this.RetryableRequest.RequestId) ?? CreateQueuedStatus(this.RetryableRequest));
            if (this.PendingRequest is not null)
                return new(ModHealthExportDisposition.RejectedBusy, this.LatestStatus);

            bool queuedDirectly = this.WritingRequest is null;
            this.RetryScheduled = true;
            this.AcceptPending(this.RetryableRequest);
            return new(queuedDirectly ? ModHealthExportDisposition.Retried : ModHealthExportDisposition.Pending, this.LatestStatus);
        }
    }

    /// <inheritdoc />
    public void DiscardRetryable()
    {
        lock (this.SyncRoot)
        {
            if (this.RetryScheduled)
                throw new InvalidOperationException("The retryable export is already queued or writing.");
            this.RetryableRequest = null;
        }
    }

    /// <inheritdoc />
    public ModHealthExportStatus GetStatus(Guid? requestId = null)
    {
        lock (this.SyncRoot)
            return requestId is null ? this.LatestStatus : this.FindStatus(requestId.Value) ?? ModHealthExportStatus.None;
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
                this.PendingRequest = null;
            }
            this.CompleteDrainIfIdle();
        }
        this.WorkAvailable.Set();
        if (this.Worker.Join(this.ShutdownTimeout))
        {
            (this.Publisher as IDisposable)?.Dispose();
            this.WorkAvailable.Dispose();
            this.Cancellation.Dispose();
        }
    }

    private void Run()
    {
        while (true)
        {
            ModHealthExportRequest? request;
            bool notify;
            lock (this.SyncRoot)
            {
                if (this.DisposeRequested && this.PendingRequest is null)
                    return;
                request = this.PendingRequest;
                if (request is not null)
                {
                    this.PendingRequest = null;
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
            try
            {
                this.Cancellation.Token.ThrowIfCancellationRequested();
                ModHealthReportPayload payload = this.BuildPayload(request);
                this.Cancellation.Token.ThrowIfCancellationRequested();
                ModHealthPublishedReport published = this.Publisher.Publish(request, payload, this.Cancellation.Token);
                result = new(ModHealthExportState.Succeeded, request.RequestId, request.IsFinal, published.TextPath, published.JsonPath);
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
                this.PreviousStatus = this.LatestStatus;
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
                notify = !this.DisposeRequested;
            }

            if (notify && this.OnCompleted is not null)
            {
                try
                {
                    this.OnCompleted(result);
                }
                catch
                {
                    // Completion notifications are informational and must never stop the writer.
                }
            }

            lock (this.SyncRoot)
                this.CompleteDrainIfIdle();
        }
    }

    private void AcceptPending(ModHealthExportRequest request)
    {
        if (this.WritingRequest is null && this.PendingRequest is null && this.Drained.Task.IsCompleted)
            this.Drained = ModHealthExportQueue.CreateCompletionSource(completed: false);
        this.PendingRequest = request;
        this.LatestStatus = CreateQueuedStatus(request);
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
        if (this.WritingRequest is null && this.PendingRequest is null)
            this.Drained.TrySetResult(true);
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
