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
        using ModHealthExportQueue queue = CreateQueue(publisher);
        ModHealthExportRequest request = CreateRequest(isFinal: true);

        queue.Enqueue(request);
        (await queue.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        queue.GetStatus(request.RequestId).State.Should().Be(ModHealthExportState.Failed);
        queue.Retry().Disposition.Should().Be(ModHealthExportDisposition.Retried);
        (await queue.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();

        ModHealthExportRequest[] attempts = publisher.Requests.ToArray();
        attempts.Should().HaveCount(2);
        ReferenceEquals(attempts[0], attempts[1]).Should().BeTrue();
        queue.GetStatus(request.RequestId).State.Should().Be(ModHealthExportState.Succeeded);
        queue.Retry().Disposition.Should().Be(ModHealthExportDisposition.NoRetryableExport);
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
        using ModHealthExportQueue queue = new(_ => payload, publisher, onCompleted: completed.Enqueue);

        queue.Enqueue(CreateRequest(isFinal: true));
        (await queue.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();

        completed.Should().ContainSingle();
        completed.Single().Should().Be(new ModHealthExportStatus(
            ModHealthExportState.Succeeded,
            completed.Single().RequestId,
            IsFinal: true,
            "ErrorLogs/HealthReports/report.txt",
            "ErrorLogs/HealthReports/report.json"
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

    private static ModHealthExportQueue CreateQueue(IModHealthReportPublisher publisher)
    {
        ModHealthReportPayload payload = new ModHealthReportPayloadFactory().Create(ModHealthReportFixtureFactory.CreateCanonical());
        return new(_ => payload, publisher);
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

        public ModHealthPublishedReport Publish(ModHealthExportRequest request, ModHealthReportPayload payload, CancellationToken cancellationToken)
        {
            this.Requests.Enqueue(request);
            if (this.Requests.Count == 1)
                throw new InvalidOperationException("injected");
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
}
