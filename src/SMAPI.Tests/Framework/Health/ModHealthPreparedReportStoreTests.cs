using System;
using System.Collections.Immutable;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.Health;

namespace SMAPI.Tests.Framework.Health;

[TestFixture]
internal sealed class ModHealthPreparedReportStoreTests
{
    [Test]
    public void Lifecycle_PublishesReadyModelBeforeSaving()
    {
        using ModHealthPreparedReportStore store = new();
        ModHealthExportRequest request = CreateRequest(isFinal: true);
        ModHealthReport model = ModHealthReportFixtureFactory.CreateCanonical();

        store.Begin(request, isRetry: false);
        store.Get(request.RequestId).Should().BeEquivalentTo(new ModHealthPreparedReportSnapshot(ModHealthPreparedReportState.Preparing, request.RequestId, IsFinal: true));

        store.PublishReady(request.RequestId, request.IsFinal, model).Should().BeTrue();
        ModHealthPreparedReportSnapshot ready = store.Get(request.RequestId);
        ready.State.Should().Be(ModHealthPreparedReportState.ReadyBeforeWrite);
        ready.Model.Should().BeSameAs(model);

        store.Saved(request.RequestId, request.IsFinal, "ErrorLogs/HealthReports/report.txt", "ErrorLogs/HealthReports/report.json");
        ModHealthPreparedReportSnapshot saved = store.Get(request.RequestId);
        saved.State.Should().Be(ModHealthPreparedReportState.Saved);
        saved.Model.Should().BeSameAs(model);
        saved.TextPath.Should().Be("ErrorLogs/HealthReports/report.txt");
        saved.JsonPath.Should().Be("ErrorLogs/HealthReports/report.json");
    }

    [Test]
    public void WriteFailure_RetainsOnlyMatchingPreparedModelUntilDiscarded()
    {
        using ModHealthPreparedReportStore store = new();
        ModHealthExportRequest request = CreateRequest(isFinal: false);
        ModHealthReport model = ModHealthReportFixtureFactory.CreateCanonical();
        store.Begin(request, isRetry: false);
        store.PublishReady(request.RequestId, request.IsFinal, model);

        store.WriteFailed(request.RequestId, request.IsFinal, "Report export failed (IOException).");

        ModHealthPreparedReportSnapshot failed = store.Get(request.RequestId);
        failed.State.Should().Be(ModHealthPreparedReportState.WriteFailed);
        failed.Model.Should().BeSameAs(model);

        store.Begin(request, isRetry: true);
        store.FailedBeforeModel(request.RequestId, request.IsFinal, "Report export failed (InvalidOperationException).");
        store.Get(request.RequestId).Should().Match<ModHealthPreparedReportSnapshot>(snapshot =>
            snapshot.State == ModHealthPreparedReportState.WriteFailed && ReferenceEquals(snapshot.Model, model)
        );

        store.DiscardRetryable(Guid.NewGuid());
        store.Get(request.RequestId).Model.Should().BeSameAs(model);

        store.DiscardRetryable(request.RequestId);
        store.Get(request.RequestId).State.Should().Be(ModHealthPreparedReportState.Absent);
        store.Get().State.Should().Be(ModHealthPreparedReportState.Absent);
    }

    [Test]
    public void FailureBeforeModel_AndRetryKeepExactRequestId()
    {
        using ModHealthPreparedReportStore store = new();
        ModHealthExportRequest request = CreateRequest(isFinal: true);
        store.Begin(request, isRetry: false);
        store.FailedBeforeModel(request.RequestId, request.IsFinal, "Report export failed (InvalidOperationException).");

        ModHealthPreparedReportSnapshot failed = store.Get(request.RequestId);
        failed.State.Should().Be(ModHealthPreparedReportState.FailedBeforeModel);
        failed.Model.Should().BeNull();

        store.Begin(request, isRetry: true);
        ModHealthPreparedReportSnapshot retrying = store.Get();
        retrying.State.Should().Be(ModHealthPreparedReportState.Retrying);
        retrying.RequestId.Should().Be(request.RequestId);
    }

    [Test]
    public void Supersession_RetainsExactTombstoneAndNewerRequestId()
    {
        using ModHealthPreparedReportStore store = new();
        ModHealthExportRequest interim = CreateRequest(isFinal: false);
        ModHealthExportRequest final = CreateRequest(isFinal: true);
        store.Begin(interim, isRetry: false);

        store.Superseded(interim.RequestId, interim.IsFinal, final.RequestId);
        store.Begin(final, isRetry: false);

        ModHealthPreparedReportSnapshot old = store.Get(interim.RequestId);
        old.State.Should().Be(ModHealthPreparedReportState.Superseded);
        old.NewerRequestId.Should().Be(final.RequestId);
        store.Get().Should().Match<ModHealthPreparedReportSnapshot>(snapshot => snapshot.State == ModHealthPreparedReportState.Preparing && snapshot.RequestId == final.RequestId);
        store.Get(Guid.NewGuid()).State.Should().Be(ModHealthPreparedReportState.Absent);
    }

    [Test]
    public void PublishReady_ReplacesTheOnlyRetainedModel()
    {
        using ModHealthPreparedReportStore store = new();
        ModHealthExportRequest first = CreateRequest(isFinal: false);
        ModHealthExportRequest second = CreateRequest(isFinal: true);
        ModHealthReport firstModel = ModHealthReportFixtureFactory.CreateCanonical() with { Header = ModHealthReportFixtureFactory.CreateCanonical().Header with { ReportId = "first" } };
        ModHealthReport secondModel = ModHealthReportFixtureFactory.CreateCanonical() with { Header = ModHealthReportFixtureFactory.CreateCanonical().Header with { ReportId = "second" } };
        store.Begin(first, isRetry: false);
        store.PublishReady(first.RequestId, first.IsFinal, firstModel);

        store.Begin(second, isRetry: false);
        store.PublishReady(second.RequestId, second.IsFinal, secondModel);

        store.Get(first.RequestId).Should().Match<ModHealthPreparedReportSnapshot>(snapshot => snapshot.State == ModHealthPreparedReportState.Superseded && snapshot.Model == null && snapshot.NewerRequestId == second.RequestId);
        store.Get(second.RequestId).Model.Should().BeSameAs(secondModel);
    }

    [Test]
    public void RetryOfOlderRequest_DoesNotReverseNewerReportRelationship()
    {
        using ModHealthPreparedReportStore store = new();
        ModHealthExportRequest older = CreateRequest(isFinal: false);
        ModHealthExportRequest newer = CreateRequest(isFinal: true);
        ModHealthReport olderModel = ModHealthReportFixtureFactory.CreateCanonical();
        ModHealthReport newerModel = ModHealthReportFixtureFactory.CreateCanonical();
        store.Begin(older, isRetry: false);
        store.PublishReady(older.RequestId, older.IsFinal, olderModel);
        store.WriteFailed(older.RequestId, older.IsFinal, "failed");
        store.Begin(newer, isRetry: false);
        store.PublishReady(newer.RequestId, newer.IsFinal, newerModel);
        store.Saved(newer.RequestId, newer.IsFinal, "newer.txt", "newer.json");

        store.Begin(older, isRetry: true);
        store.PublishReady(older.RequestId, older.IsFinal, olderModel).Should().BeTrue();
        store.Saved(older.RequestId, older.IsFinal, "older.txt", "older.json");

        store.Get().Should().Match<ModHealthPreparedReportSnapshot>(snapshot =>
            snapshot.RequestId == newer.RequestId && snapshot.State == ModHealthPreparedReportState.Saved && ReferenceEquals(snapshot.Model, newerModel)
        );
        store.Get(older.RequestId).Should().Match<ModHealthPreparedReportSnapshot>(snapshot =>
            snapshot.State == ModHealthPreparedReportState.Superseded
            && snapshot.Model == null
            && snapshot.NewerRequestId == newer.RequestId
            && snapshot.TextPath == "older.txt"
            && snapshot.JsonPath == "older.json"
        );
    }

    [TestCase(true)]
    [TestCase(false)]
    public void FailedRetryOfOlderRequest_PreservesNewerReportRelationship(bool failsBeforeModel)
    {
        using ModHealthPreparedReportStore store = new();
        ModHealthExportRequest older = CreateRequest(isFinal: false);
        ModHealthExportRequest newer = CreateRequest(isFinal: true);
        ModHealthReport model = ModHealthReportFixtureFactory.CreateCanonical();
        store.Begin(older, isRetry: false);
        store.PublishReady(older.RequestId, older.IsFinal, model);
        store.WriteFailed(older.RequestId, older.IsFinal, "initial failure");
        store.Begin(newer, isRetry: false);
        store.PublishReady(newer.RequestId, newer.IsFinal, model);
        store.Saved(newer.RequestId, newer.IsFinal, "newer.txt", "newer.json");
        store.Begin(older, isRetry: true);

        if (failsBeforeModel)
            store.FailedBeforeModel(older.RequestId, older.IsFinal, "retry build failed");
        else
        {
            store.PublishReady(older.RequestId, older.IsFinal, model);
            store.WriteFailed(older.RequestId, older.IsFinal, "retry write failed");
        }

        store.Get(older.RequestId).Should().Match<ModHealthPreparedReportSnapshot>(snapshot =>
            snapshot.State == ModHealthPreparedReportState.Superseded
            && snapshot.NewerRequestId == newer.RequestId
            && snapshot.Model == null
            && snapshot.Error == (failsBeforeModel ? "retry build failed" : "retry write failed")
        );
        store.Get(newer.RequestId).Model.Should().BeSameAs(model);
    }

    [Test]
    public void FailedRetryOfOldRequest_RelinksToRetainedModelAfterIntermediateTombstoneIsTrimmed()
    {
        using ModHealthPreparedReportStore store = new();
        ModHealthExportRequest older = CreateRequest(isFinal: false);
        ModHealthReport model = ModHealthReportFixtureFactory.CreateCanonical();
        store.Begin(older, isRetry: false);
        store.PublishReady(older.RequestId, older.IsFinal, model);
        store.WriteFailed(older.RequestId, older.IsFinal, "initial failure");

        ModHealthExportRequest newest = null!;
        for (int i = 0; i < 10; i++)
        {
            newest = CreateRequest(isFinal: true);
            store.Begin(newest, isRetry: false);
            store.PublishReady(newest.RequestId, newest.IsFinal, model);
            store.Saved(newest.RequestId, newest.IsFinal, $"newer-{i}.txt", $"newer-{i}.json");
        }

        store.Begin(older, isRetry: true);
        store.FailedBeforeModel(older.RequestId, older.IsFinal, "retry build failed");

        store.Get(older.RequestId).Should().Match<ModHealthPreparedReportSnapshot>(snapshot =>
            snapshot.State == ModHealthPreparedReportState.Superseded
            && snapshot.NewerRequestId == newest.RequestId
            && snapshot.Error == "retry build failed"
        );
        store.Get(newest.RequestId).Model.Should().BeSameAs(model);
    }

    [Test]
    public void Dispose_CancelsActiveRequestAndRejectsLatePublication()
    {
        ModHealthPreparedReportStore store = new();
        ModHealthExportRequest request = CreateRequest(isFinal: true);
        store.Begin(request, isRetry: false);

        store.Dispose();

        store.Get().State.Should().Be(ModHealthPreparedReportState.Disposed);
        store.Get(request.RequestId).State.Should().Be(ModHealthPreparedReportState.Canceled);
        store.PublishReady(request.RequestId, request.IsFinal, ModHealthReportFixtureFactory.CreateCanonical()).Should().BeFalse();
        store.Get(request.RequestId).Model.Should().BeNull();

        Guid lateRequestId = Guid.NewGuid();
        store.Canceled(lateRequestId, isFinal: false, "late");
        store.Get(lateRequestId).State.Should().Be(ModHealthPreparedReportState.Disposed);
    }

    [Test]
    public void ConcurrentReads_AlwaysAssociateModelWithExactRequest()
    {
        using ModHealthPreparedReportStore store = new();
        ModHealthExportRequest request = CreateRequest(isFinal: true);
        ModHealthReport model = ModHealthReportFixtureFactory.CreateCanonical();
        store.Begin(request, isRetry: false);

        Parallel.For(0, 1000, iteration =>
        {
            if (iteration == 500)
                store.PublishReady(request.RequestId, request.IsFinal, model);
            ModHealthPreparedReportSnapshot snapshot = store.Get(request.RequestId);
            snapshot.RequestId.Should().Be(request.RequestId);
            if (snapshot.Model is not null)
                snapshot.Model.Should().BeSameAs(model);
        });
    }

    private static ModHealthExportRequest CreateRequest(bool isFinal)
    {
        return new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
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
}
