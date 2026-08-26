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

        publisher.Release.Set();
        (await queue.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        publisher.RequestIds.Should().Equal(writing.RequestId, final.RequestId);
        queue.GetStatus(final.RequestId).State.Should().Be(ModHealthExportState.Succeeded);
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
        queue.Enqueue(CreateRequest(isFinal: true));
        publisher.Started.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

        FluentActions.Invoking(queue.Dispose).Should().NotThrow();
        publisher.Cancelled.Should().BeTrue();
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
        public bool IsDisposed { get; private set; }

        public ModHealthPublishedReport Publish(ModHealthExportRequest request, ModHealthReportPayload payload, CancellationToken cancellationToken)
        {
            return new("ErrorLogs/HealthReports/report.txt", "ErrorLogs/HealthReports/report.json");
        }

        public void Dispose()
        {
            this.IsDisposed = true;
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
