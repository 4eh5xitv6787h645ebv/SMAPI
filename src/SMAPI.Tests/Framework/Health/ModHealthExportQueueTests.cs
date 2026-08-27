using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.Health;

namespace SMAPI.Tests.Framework.Health;

[TestFixture]
internal sealed class ModHealthExportQueueTests
{
    [Test]
    public async Task Worker_PublishesPreparingReadyAndSavedStatesAtExactBoundaries()
    {
        ManualResetEventSlim buildStarted = new(false);
        ManualResetEventSlim releaseBuild = new(false);
        BlockingPublisher publisher = new();
        ModHealthReportPayload payload = new ModHealthReportPayloadFactory().Create(ModHealthReportFixtureFactory.CreateCanonical());
        using ModHealthExportQueue queue = new(
            (_, _) =>
            {
                buildStarted.Set();
                releaseBuild.Wait();
                return payload;
            },
            publisher
        );
        ModHealthExportRequest request = CreateRequest(isFinal: true);

        queue.Enqueue(request);
        buildStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        queue.GetPreparedReport(request.RequestId).Should().Match<ModHealthPreparedReportSnapshot>(snapshot => snapshot.State == ModHealthPreparedReportState.Preparing && snapshot.Model == null);

        releaseBuild.Set();
        publisher.Started.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        queue.GetPreparedReport(request.RequestId).Should().Match<ModHealthPreparedReportSnapshot>(snapshot => snapshot.State == ModHealthPreparedReportState.ReadyBeforeWrite && ReferenceEquals(snapshot.Model, payload.Model));

        publisher.Release.Set();
        (await queue.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        queue.GetPreparedReport(request.RequestId).Should().Match<ModHealthPreparedReportSnapshot>(snapshot =>
            snapshot.State == ModHealthPreparedReportState.Saved
            && ReferenceEquals(snapshot.Model, payload.Model)
            && snapshot.TextPath == "ErrorLogs/HealthReports/report.txt"
            && snapshot.JsonPath == "ErrorLogs/HealthReports/report.json"
        );
    }

    [Test]
    public async Task Worker_DistinguishesBuildFailureFromWriteFailureAndRetainsWrittenModel()
    {
        ModHealthExportRequest buildFailure = CreateRequest(isFinal: false);
        using (ModHealthExportQueue queue = new((_, _) => throw new InvalidOperationException("build"), new DisposablePublisher()))
        {
            queue.Enqueue(buildFailure);
            (await queue.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
            queue.GetPreparedReport(buildFailure.RequestId).Should().Match<ModHealthPreparedReportSnapshot>(snapshot => snapshot.State == ModHealthPreparedReportState.FailedBeforeModel && snapshot.Model == null);
        }

        FailingOncePublisher publisher = new();
        ModHealthReportPayload payload = new ModHealthReportPayloadFactory().Create(ModHealthReportFixtureFactory.CreateCanonical());
        using ModHealthExportQueue failedWriteQueue = new((_, _) => payload, publisher);
        ModHealthExportRequest writeFailure = CreateRequest(isFinal: true);
        failedWriteQueue.Enqueue(writeFailure);
        (await failedWriteQueue.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();

        failedWriteQueue.GetPreparedReport(writeFailure.RequestId).Should().Match<ModHealthPreparedReportSnapshot>(snapshot =>
            snapshot.State == ModHealthPreparedReportState.WriteFailed && ReferenceEquals(snapshot.Model, payload.Model)
        );
    }

    [Test]
    public async Task Retry_IsExactRequestKeyedAndExposesRetryingWithRetainedModel()
    {
        int builds = 0;
        ManualResetEventSlim retryBuildStarted = new(false);
        ManualResetEventSlim releaseRetryBuild = new(false);
        FailingOncePublisher publisher = new();
        ModHealthReportPayload payload = new ModHealthReportPayloadFactory().Create(ModHealthReportFixtureFactory.CreateCanonical());
        using ModHealthExportQueue queue = new(
            (_, _) =>
            {
                if (Interlocked.Increment(ref builds) == 2)
                {
                    retryBuildStarted.Set();
                    releaseRetryBuild.Wait();
                }
                return payload;
            },
            publisher
        );
        ModHealthExportRequest request = CreateRequest(isFinal: true);
        queue.Enqueue(request);
        (await queue.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();

        queue.Retry(Guid.NewGuid()).Disposition.Should().Be(ModHealthExportDisposition.NoRetryableExport);
        queue.Retry(request.RequestId).Disposition.Should().Be(ModHealthExportDisposition.Retried);
        retryBuildStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        queue.GetPreparedReport(request.RequestId).Should().Match<ModHealthPreparedReportSnapshot>(snapshot =>
            snapshot.State == ModHealthPreparedReportState.Retrying && snapshot.RequestId == request.RequestId && ReferenceEquals(snapshot.Model, payload.Model)
        );

        releaseRetryBuild.Set();
        (await queue.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        queue.GetPreparedReport(request.RequestId).State.Should().Be(ModHealthPreparedReportState.Saved);
    }

    [Test]
    public async Task DiscardRetryable_OnlyDiscardsMatchingUnsavedModel()
    {
        FailingOncePublisher publisher = new();
        ModHealthReportPayload payload = new ModHealthReportPayloadFactory().Create(ModHealthReportFixtureFactory.CreateCanonical());
        using ModHealthExportQueue queue = new((_, _) => payload, publisher);
        ModHealthExportRequest request = CreateRequest(isFinal: true);
        queue.Enqueue(request);
        (await queue.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();

        queue.DiscardRetryable(Guid.NewGuid());
        queue.GetPreparedReport(request.RequestId).Model.Should().BeSameAs(payload.Model);
        queue.DiscardRetryable(request.RequestId);

        queue.GetPreparedReport(request.RequestId).State.Should().Be(ModHealthPreparedReportState.Absent);
        queue.Retry(request.RequestId).Disposition.Should().Be(ModHealthExportDisposition.NoRetryableExport);
    }

    [Test]
    public async Task Enqueue_AllowsOneWriterAndFinalReplacesPendingInterim()
    {
        BlockingPublisher publisher = new();
        using ModHealthExportQueue queue = CreateQueue(publisher);
        ModHealthExportRequest writing = CreateRequest(isFinal: false);
        ModHealthExportRequest interim = CreateRequest(isFinal: false);
        ModHealthExportRequest final = CreateRequest(isFinal: true);

        queue.Enqueue(writing).Disposition.Should().Be(ModHealthExportDisposition.Queued);
        publisher.Started.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        queue.Enqueue(interim).Disposition.Should().Be(ModHealthExportDisposition.Pending);
        queue.Enqueue(final).Disposition.Should().Be(ModHealthExportDisposition.Coalesced);
        queue.GetStatus(interim.RequestId).State.Should().Be(ModHealthExportState.Failed);
        queue.GetPreparedReport(interim.RequestId).Should().Match<ModHealthPreparedReportSnapshot>(snapshot => snapshot.State == ModHealthPreparedReportState.Superseded && snapshot.NewerRequestId == final.RequestId);
        queue.GetPreparedReport().Should().Match<ModHealthPreparedReportSnapshot>(snapshot => snapshot.State == ModHealthPreparedReportState.Preparing && snapshot.RequestId == final.RequestId);

        publisher.Release.Set();
        (await queue.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        publisher.RequestIds.Should().Equal(writing.RequestId, final.RequestId);
        queue.GetStatus(final.RequestId).State.Should().Be(ModHealthExportState.Succeeded);
    }

    [Test]
    public async Task Completion_PreservesPendingAsLatestAndCompletedRequestAsPrevious()
    {
        TwoWritePublisher publisher = new();
        ManualResetEventSlim callbackStarted = new(false);
        ManualResetEventSlim releaseCallback = new(false);
        ModHealthExportRequest first = CreateRequest(isFinal: false);
        ModHealthExportRequest second = CreateRequest(isFinal: true);
        using ModHealthExportQueue queue = new(
            (_, _) => new ModHealthReportPayloadFactory().Create(ModHealthReportFixtureFactory.CreateCanonical()),
            publisher,
            onCompleted: status =>
            {
                if (status.RequestId == first.RequestId)
                {
                    callbackStarted.Set();
                    releaseCallback.Wait();
                }
            }
        );

        queue.Enqueue(first);
        publisher.FirstStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        queue.Enqueue(second).Disposition.Should().Be(ModHealthExportDisposition.Pending);
        publisher.ReleaseFirst.Set();
        callbackStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

        queue.GetStatus().Should().BeEquivalentTo(new ModHealthExportStatus(ModHealthExportState.Queued, second.RequestId, IsFinal: true));
        queue.GetStatus(first.RequestId).State.Should().Be(ModHealthExportState.Succeeded);
        queue.GetPreparedReport().Should().Match<ModHealthPreparedReportSnapshot>(snapshot => snapshot.RequestId == second.RequestId && snapshot.State == ModHealthPreparedReportState.Preparing);

        releaseCallback.Set();
        publisher.SecondStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        publisher.ReleaseSecond.Set();
        (await queue.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
    }

    [Test]
    public async Task Retry_ReusesExactFrozenRequestAfterFailure()
    {
        FailingOncePublisher publisher = new();
        ModHealthReportBuilder builder = new();
        ModHealthEnvironmentSnapshot environment = CreateEnvironment();
        using ModHealthExportQueue queue = new(
            (candidate, isRetry) => builder.BuildPayload(candidate, environment, writeRetry: isRetry),
            publisher
        );
        ModHealthExportRequest request = CreateRequest(isFinal: true);

        queue.Enqueue(request);
        (await queue.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        queue.GetStatus(request.RequestId).State.Should().Be(ModHealthExportState.Failed);
        queue.Retry().Disposition.Should().Be(ModHealthExportDisposition.Retried);
        (await queue.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();

        ModHealthExportRequest[] attempts = publisher.Requests.ToArray();
        attempts.Should().HaveCount(2);
        ReferenceEquals(attempts[0], attempts[1]).Should().BeTrue();
        publisher.WriteRetryValues.Should().Equal(false, true);
        queue.GetStatus(request.RequestId).State.Should().Be(ModHealthExportState.Succeeded);
        queue.Retry().Disposition.Should().Be(ModHealthExportDisposition.NoRetryableExport);
    }

    [Test]
    public async Task Enqueue_FinalReplacingPendingRetryLeavesFailedRequestRetryable()
    {
        FailingThenBlockingPublisher publisher = new();
        using ModHealthExportQueue queue = CreateQueue(publisher);
        ModHealthExportRequest failedInterim = CreateRequest(isFinal: false);
        ModHealthExportRequest writingInterim = CreateRequest(isFinal: false);
        ModHealthExportRequest final = CreateRequest(isFinal: true);

        queue.Enqueue(failedInterim);
        (await queue.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        queue.Enqueue(writingInterim);
        publisher.SecondAttemptStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        queue.Retry().Disposition.Should().Be(ModHealthExportDisposition.Pending);

        queue.Enqueue(final).Disposition.Should().Be(ModHealthExportDisposition.Coalesced);
        queue.Retry().Disposition.Should().Be(ModHealthExportDisposition.RejectedBusy);
        publisher.ReleaseSecondAttempt.Set();
        (await queue.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();

        queue.Retry().Disposition.Should().Be(ModHealthExportDisposition.Retried);
        (await queue.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        ModHealthExportRequest[] attempts = publisher.Requests.ToArray();
        attempts.Select(candidate => candidate.RequestId).Should().Equal(failedInterim.RequestId, writingInterim.RequestId, final.RequestId, failedInterim.RequestId);
        ReferenceEquals(attempts[0], attempts[3]).Should().BeTrue();
        queue.GetPreparedReport().Should().Match<ModHealthPreparedReportSnapshot>(snapshot =>
            snapshot.RequestId == final.RequestId && snapshot.State == ModHealthPreparedReportState.Saved && snapshot.Model != null
        );
        queue.GetPreparedReport(failedInterim.RequestId).Should().Match<ModHealthPreparedReportSnapshot>(snapshot =>
            snapshot.State == ModHealthPreparedReportState.Superseded && snapshot.NewerRequestId == final.RequestId && snapshot.Model == null
        );
    }

    [Test]
    public async Task DrainAsync_TimesOutWithoutCancellingWriter()
    {
        BlockingPublisher publisher = new();
        using ModHealthExportQueue queue = CreateQueue(publisher);
        queue.Enqueue(CreateRequest(isFinal: false));
        publisher.Started.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

        (await queue.DrainAsync(TimeSpan.FromMilliseconds(20))).Should().BeFalse();
        publisher.Release.Set();
        (await queue.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
    }

    [Test]
    public void Dispose_CancelsAWriterWhichObservesCancellation()
    {
        CancellationPublisher publisher = new();
        ModHealthExportQueue queue = CreateQueue(publisher);
        ModHealthExportRequest request = CreateRequest(isFinal: true);
        queue.Enqueue(request);
        publisher.Started.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

        FluentActions.Invoking(queue.Dispose).Should().NotThrow();
        publisher.Cancelled.Should().BeTrue();
        queue.GetPreparedReport(requestId: null).State.Should().Be(ModHealthPreparedReportState.Disposed);
        queue.GetPreparedReport(request.RequestId).State.Should().Be(ModHealthPreparedReportState.Canceled);
    }

    [Test]
    public void Dispose_BlocksLateModelPublicationAfterUncooperativeBuild()
    {
        ManualResetEventSlim buildStarted = new(false);
        ManualResetEventSlim releaseBuild = new(false);
        DisposablePublisher publisher = new();
        ModHealthReportPayload payload = new ModHealthReportPayloadFactory().Create(ModHealthReportFixtureFactory.CreateCanonical());
        ModHealthExportQueue queue = new(
            (_, _) =>
            {
                buildStarted.Set();
                releaseBuild.Wait();
                return payload;
            },
            publisher,
            shutdownTimeout: TimeSpan.Zero
        );
        ModHealthExportRequest request = CreateRequest(isFinal: true);
        queue.Enqueue(request);
        buildStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

        queue.Dispose();
        queue.GetPreparedReport().State.Should().Be(ModHealthPreparedReportState.Disposed);
        queue.GetPreparedReport(request.RequestId).State.Should().Be(ModHealthPreparedReportState.Canceled);
        releaseBuild.Set();

        SpinWait.SpinUntil(() => publisher.IsDisposed, TimeSpan.FromSeconds(5)).Should().BeTrue();
        publisher.PublishCalls.Should().Be(0);
        queue.GetPreparedReport(request.RequestId).Should().Match<ModHealthPreparedReportSnapshot>(snapshot => snapshot.State == ModHealthPreparedReportState.Canceled && snapshot.Model == null);
    }

    [Test]
    public void Dispose_RepeatedWriterExitCannotRaceShutdownSignalCleanup()
    {
        for (int i = 0; i < 32; i++)
        {
            CancellationPublisher publisher = new();
            ModHealthExportQueue queue = CreateQueue(publisher);
            queue.Enqueue(CreateRequest(isFinal: true));
            publisher.Started.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

            FluentActions.Invoking(queue.Dispose).Should().NotThrow();
            publisher.Cancelled.Should().BeTrue();
        }
    }

    [Test]
    public async Task CompletionCallback_ReceivesCommittedRelativePaths()
    {
        DisposablePublisher publisher = new();
        ConcurrentQueue<ModHealthExportStatus> completed = new();
        ModHealthReportPayload payload = new ModHealthReportPayloadFactory().Create(ModHealthReportFixtureFactory.CreateCanonical());
        using ModHealthExportQueue queue = new((_, _) => payload, publisher, onCompleted: completed.Enqueue);

        queue.Enqueue(CreateRequest(isFinal: true));
        (await queue.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();

        completed.Should().ContainSingle();
        ModHealthCompletionSummary expectedSummary = ModHealthCompletionSummary.FromReport(payload.Model);
        completed.Single().Should().BeEquivalentTo(new ModHealthExportStatus(
            ModHealthExportState.Succeeded,
            completed.Single().RequestId,
            IsFinal: true,
            "ErrorLogs/HealthReports/report.txt",
            "ErrorLogs/HealthReports/report.json",
            Summary: expectedSummary
        ));
    }

    [Test]
    public void Dispose_DisposesPublisherAfterWorkerStops()
    {
        DisposablePublisher publisher = new();
        ModHealthExportQueue queue = CreateQueue(publisher);

        queue.Dispose();

        publisher.IsDisposed.Should().BeTrue();
    }

    [Test]
    public void Dispose_WhenJoinTimesOutWorkerEventuallyOwnsResourceCleanup()
    {
        UncooperativePublisher publisher = new();
        ModHealthReportPayload payload = new ModHealthReportPayloadFactory().Create(ModHealthReportFixtureFactory.CreateCanonical());
        ModHealthExportQueue queue = new((_, _) => payload, publisher, shutdownTimeout: TimeSpan.Zero);
        queue.Enqueue(CreateRequest(isFinal: true));
        publisher.Started.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

        queue.Dispose();
        publisher.IsDisposed.Should().BeFalse();
        publisher.Release.Set();

        publisher.Disposed.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        publisher.IsDisposed.Should().BeTrue();
    }

    [Test]
    public void Dispose_SuppressesCompletionWhichHasNotClaimedCallbackBoundary()
    {
        UncooperativePublisher publisher = new();
        ConcurrentQueue<ModHealthExportStatus> completed = new();
        ModHealthReportPayload payload = new ModHealthReportPayloadFactory().Create(ModHealthReportFixtureFactory.CreateCanonical());
        ModHealthExportQueue queue = new((_, _) => payload, publisher, shutdownTimeout: TimeSpan.Zero, onCompleted: completed.Enqueue);
        queue.Enqueue(CreateRequest(isFinal: true));
        publisher.Started.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

        queue.Dispose();
        publisher.Release.Set();

        publisher.Disposed.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        completed.Should().BeEmpty();
    }

    [Test]
    public async Task Dispose_WaitsForCompletionWhichAlreadyClaimedCallbackBoundary()
    {
        DisposablePublisher publisher = new();
        ManualResetEventSlim callbackStarted = new(false);
        ManualResetEventSlim releaseCallback = new(false);
        ModHealthReportPayload payload = new ModHealthReportPayloadFactory().Create(ModHealthReportFixtureFactory.CreateCanonical());
        ModHealthExportQueue queue = new(
            (_, _) => payload,
            publisher,
            shutdownTimeout: TimeSpan.FromSeconds(5),
            onCompleted: _ =>
            {
                callbackStarted.Set();
                releaseCallback.Wait();
            }
        );
        queue.Enqueue(CreateRequest(isFinal: true));
        callbackStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

        Task dispose = Task.Run(queue.Dispose);
        await Task.Delay(20);
        dispose.IsCompleted.Should().BeFalse();
        releaseCallback.Set();

        await dispose;
        publisher.IsDisposed.Should().BeTrue();
    }

    private static ModHealthExportQueue CreateQueue(IModHealthReportPublisher publisher)
    {
        ModHealthReportPayload payload = new ModHealthReportPayloadFactory().Create(ModHealthReportFixtureFactory.CreateCanonical());
        return new((_, _) => payload, publisher);
    }

    private static ModHealthEnvironmentSnapshot CreateEnvironment()
    {
        return new(
            "4.3.0",
            null,
            "1.6.15",
            ".NET 10.0.0",
            "x64",
            64,
            "linux",
            "6.0.0",
            "wayland",
            "en-US",
            8,
            "single-player",
            1,
            StartupObserved: true,
            LifecycleTimingObserved: false
        );
    }

    private static ModHealthExportRequest CreateRequest(bool isFinal)
    {
        return new(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 8, 26, 12, 34, 56, TimeSpan.Zero),
            ModHealthCaptureOwner.Health,
            ModHealthCaptureOrigin.Manual,
            isFinal ? ModHealthCompletionReason.UserStop : ModHealthCompletionReason.InterimReport,
            null,
            new ModHealthLedger().GetSnapshot(),
            ImmutableArray<ModHealthMark>.Empty,
            33.333,
            isFinal
        );
    }

    private sealed class BlockingPublisher : IModHealthReportPublisher
    {
        public ManualResetEventSlim Started { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);
        public ConcurrentQueue<Guid> RequestIds { get; } = new();

        public ModHealthPublishedReport Publish(ModHealthExportRequest request, ModHealthReportPayload payload, CancellationToken cancellationToken)
        {
            this.RequestIds.Enqueue(request.RequestId);
            this.Started.Set();
            this.Release.Wait(cancellationToken);
            return new("ErrorLogs/HealthReports/report.txt", "ErrorLogs/HealthReports/report.json");
        }
    }

    private sealed class FailingOncePublisher : IModHealthReportPublisher
    {
        public ConcurrentQueue<ModHealthExportRequest> Requests { get; } = new();
        public ConcurrentQueue<bool> WriteRetryValues { get; } = new();

        public ModHealthPublishedReport Publish(ModHealthExportRequest request, ModHealthReportPayload payload, CancellationToken cancellationToken)
        {
            this.Requests.Enqueue(request);
            this.WriteRetryValues.Enqueue(payload.Model.Header.WriteRetry);
            if (this.Requests.Count == 1)
                throw new InvalidOperationException("injected");
            return new("ErrorLogs/HealthReports/report.txt", "ErrorLogs/HealthReports/report.json");
        }
    }

    private sealed class TwoWritePublisher : IModHealthReportPublisher
    {
        private int Attempts;
        public ManualResetEventSlim FirstStarted { get; } = new(false);
        public ManualResetEventSlim ReleaseFirst { get; } = new(false);
        public ManualResetEventSlim SecondStarted { get; } = new(false);
        public ManualResetEventSlim ReleaseSecond { get; } = new(false);

        public ModHealthPublishedReport Publish(ModHealthExportRequest request, ModHealthReportPayload payload, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref this.Attempts) == 1)
            {
                this.FirstStarted.Set();
                this.ReleaseFirst.Wait(cancellationToken);
            }
            else
            {
                this.SecondStarted.Set();
                this.ReleaseSecond.Wait(cancellationToken);
            }
            return new("ErrorLogs/HealthReports/report.txt", "ErrorLogs/HealthReports/report.json");
        }
    }

    private sealed class FailingThenBlockingPublisher : IModHealthReportPublisher
    {
        public ConcurrentQueue<ModHealthExportRequest> Requests { get; } = new();
        public ManualResetEventSlim SecondAttemptStarted { get; } = new(false);
        public ManualResetEventSlim ReleaseSecondAttempt { get; } = new(false);

        public ModHealthPublishedReport Publish(ModHealthExportRequest request, ModHealthReportPayload payload, CancellationToken cancellationToken)
        {
            this.Requests.Enqueue(request);
            int attempt = this.Requests.Count;
            if (attempt == 1)
                throw new InvalidOperationException("injected");
            if (attempt == 2)
            {
                this.SecondAttemptStarted.Set();
                this.ReleaseSecondAttempt.Wait(cancellationToken);
            }
            return new("ErrorLogs/HealthReports/report.txt", "ErrorLogs/HealthReports/report.json");
        }
    }

    private sealed class CancellationPublisher : IModHealthReportPublisher
    {
        public ManualResetEventSlim Started { get; } = new(false);
        public bool Cancelled { get; private set; }

        public ModHealthPublishedReport Publish(ModHealthExportRequest request, ModHealthReportPayload payload, CancellationToken cancellationToken)
        {
            this.Started.Set();
            try
            {
                cancellationToken.WaitHandle.WaitOne();
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                this.Cancelled = true;
                throw;
            }
            throw new InvalidOperationException("Cancellation was not observed.");
        }
    }

    private sealed class DisposablePublisher : IModHealthReportPublisher, IDisposable
    {
        private int DisposedValue;
        private int PublishCallCount;

        public bool IsDisposed => Volatile.Read(ref this.DisposedValue) != 0;
        public int PublishCalls => Volatile.Read(ref this.PublishCallCount);

        public ModHealthPublishedReport Publish(ModHealthExportRequest request, ModHealthReportPayload payload, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref this.PublishCallCount);
            return new("ErrorLogs/HealthReports/report.txt", "ErrorLogs/HealthReports/report.json");
        }

        public void Dispose()
        {
            Volatile.Write(ref this.DisposedValue, 1);
        }
    }

    private sealed class UncooperativePublisher : IModHealthReportPublisher, IDisposable
    {
        public ManualResetEventSlim Started { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);
        public ManualResetEventSlim Disposed { get; } = new(false);
        public bool IsDisposed { get; private set; }

        public ModHealthPublishedReport Publish(ModHealthExportRequest request, ModHealthReportPayload payload, CancellationToken cancellationToken)
        {
            this.Started.Set();
            this.Release.Wait();
            return new("ErrorLogs/HealthReports/report.txt", "ErrorLogs/HealthReports/report.json");
        }

        public void Dispose()
        {
            this.IsDisposed = true;
            this.Disposed.Set();
        }
    }
}
