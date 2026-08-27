using System;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.Health;
using StardewModdingAPI.Framework.Health.Presentation;
using StardewModdingAPI.Framework.Health.Viewer;
using StardewModdingAPI.Framework.Health.Viewer.Game;
using StardewModdingAPI.Framework.Health.Viewer.Layout;

namespace SMAPI.Tests.Framework.Health.Viewer.Game;

[TestFixture]
internal sealed class ModHealthViewerSessionTests
{
    [TestCase(ModHealthPreparedReportState.Absent)]
    [TestCase(ModHealthPreparedReportState.Preparing)]
    [TestCase(ModHealthPreparedReportState.ReadyBeforeWrite)]
    [TestCase(ModHealthPreparedReportState.Saved)]
    [TestCase(ModHealthPreparedReportState.WriteFailed)]
    [TestCase(ModHealthPreparedReportState.FailedBeforeModel)]
    [TestCase(ModHealthPreparedReportState.Retrying)]
    [TestCase(ModHealthPreparedReportState.Superseded)]
    [TestCase(ModHealthPreparedReportState.Canceled)]
    [TestCase(ModHealthPreparedReportState.Disposed)]
    [TestCase(ModHealthPreparedReportState.Rejected)]
    public void ApplyPreparedSnapshot_RepresentsEveryPreparedState(ModHealthPreparedReportState state)
    {
        Guid requestId = Guid.NewGuid();
        Guid newerId = Guid.NewGuid();
        ModHealthViewerSession session = Create(requestId);

        session.ApplyPreparedSnapshot(new(state, requestId, Error: "safe error", NewerRequestId: state == ModHealthPreparedReportState.Superseded ? newerId : null), new ModHealthReportPresentationMapper());

        session.PreparedState.Should().Be(state);
        session.Error.Should().Be("safe error");
        session.NewerRequestId.Should().Be(state == ModHealthPreparedReportState.Superseded ? newerId : null);
        session.AvailableActionCount.Should().BeInRange(1, 3);
    }

    [Test]
    public void ApplyPreparedSnapshot_IgnoresDifferentExactRequest()
    {
        Guid requestId = Guid.NewGuid();
        ModHealthViewerSession session = Create(requestId);

        session.ApplyPreparedSnapshot(new(ModHealthPreparedReportState.WriteFailed, Guid.NewGuid(), Error: "wrong"), new ModHealthReportPresentationMapper());

        session.PreparedState.Should().Be(ModHealthPreparedReportState.Absent);
        session.Error.Should().BeNull();
    }

    [Test]
    public void ApplyPreparedSnapshot_MapsSameExactImmutableModelOnlyOnceAcrossReadyAndSaved()
    {
        Guid requestId = Guid.NewGuid();
        ModHealthReport model = ModHealthReportFixtureFactory.CreateCanonical();
        ModHealthViewerSession session = Create(requestId);
        ModHealthReportPresentationMapper mapper = new();

        session.ApplyPreparedSnapshot(new(ModHealthPreparedReportState.ReadyBeforeWrite, requestId, Model: model), mapper);
        object content = session.Content!;
        session.ApplyPreparedSnapshot(new(ModHealthPreparedReportState.Saved, requestId, Model: model, TextPath: "reports/health.txt", JsonPath: "reports/health.json"), mapper);

        session.Content.Should().BeSameAs(content);
        session.PreparedState.Should().Be(ModHealthPreparedReportState.Saved);
    }

    [Test]
    public void ProjectionRevision_ChangesForNewExactRequestOrEqualSizedReplacementContent()
    {
        Guid requestId = Guid.NewGuid();
        ModHealthReport model = ModHealthReportFixtureFactory.CreateCanonical();
        ModHealthViewerSession session = Create(requestId);
        ModHealthReportPresentationMapper mapper = new();

        session.ApplyPreparedSnapshot(new(ModHealthPreparedReportState.ReadyBeforeWrite, requestId, Model: model), mapper);
        long firstProjection = session.ProjectionRevision;
        object firstContent = session.Content!;

        ModHealthReport equalSizedReplacement = model with { Header = model.Header with { ReportId = "replacement" } };
        session.ApplyPreparedSnapshot(new(ModHealthPreparedReportState.Saved, requestId, Model: equalSizedReplacement), mapper);
        session.ProjectionRevision.Should().BeGreaterThan(firstProjection);
        session.Content.Should().NotBeSameAs(firstContent);

        long replacementProjection = session.ProjectionRevision;
        session.SwitchRequest(Guid.NewGuid());
        session.ProjectionRevision.Should().BeGreaterThan(replacementProjection);
        session.Content.Should().BeNull();
    }

    [Test]
    public void QueueAction_CapturesScreenTokenAndCurrentExactRequest()
    {
        Guid viewerId = Guid.NewGuid();
        Guid requestId = Guid.NewGuid();
        ModHealthViewerActionKind? kind = null;
        Guid actualViewer = Guid.Empty;
        Guid actualRequest = Guid.Empty;
        ModHealthViewerSession session = new(viewerId, requestId, (queuedKind, queuedViewer, queuedRequest) =>
        {
            kind = queuedKind;
            actualViewer = queuedViewer;
            actualRequest = queuedRequest;
            return ModHealthViewerActionDisposition.Queued;
        });

        session.QueueAction(ModHealthViewerActionKind.RefreshAndSaveSnapshot).Should().Be(ModHealthViewerActionDisposition.Queued);

        kind.Should().Be(ModHealthViewerActionKind.RefreshAndSaveSnapshot);
        actualViewer.Should().Be(viewerId);
        actualRequest.Should().Be(requestId);
    }

    [Test]
    public void SwitchRequest_ClearsOldPathsErrorAndSuccessor()
    {
        Guid oldId = Guid.NewGuid();
        ModHealthViewerSession session = Create(oldId);
        session.ApplyPreparedSnapshot(new(ModHealthPreparedReportState.Superseded, oldId, TextPath: "reports/old.txt", JsonPath: "reports/old.json", Error: "old", NewerRequestId: Guid.NewGuid()), new ModHealthReportPresentationMapper());

        Guid newId = Guid.NewGuid();
        session.SwitchRequest(newId);

        session.RequestId.Should().Be(newId);
        session.PreparedState.Should().Be(ModHealthPreparedReportState.Absent);
        session.NewerRequestId.Should().BeNull();
        session.TextPath.Should().BeNull();
        session.JsonPath.Should().BeNull();
        session.Error.Should().BeNull();
        session.Content.Should().BeNull();
    }

    [Test]
    public void ApplyPreparedSnapshot_ShowsOnlyStableRelativeArtifactPaths()
    {
        Guid requestId = Guid.NewGuid();
        ModHealthViewerSession session = Create(requestId);

        session.ApplyPreparedSnapshot(new(ModHealthPreparedReportState.Saved, requestId, TextPath: "\\\\server\\private\\report.txt", JsonPath: "reports/../secret.json"), new ModHealthReportPresentationMapper());

        session.TextPath.Should().BeNull();
        session.JsonPath.Should().BeNull();

        session.ApplyPreparedSnapshot(new(ModHealthPreparedReportState.Saved, requestId, TextPath: "reports/health.txt", JsonPath: "reports/health.json"), new ModHealthReportPresentationMapper());
        session.TextPath.Should().Be("reports/health.txt");
        session.JsonPath.Should().Be("reports/health.json");
    }

    [TestCase("/home/user/private/report.txt")]
    [TestCase("C:\\Users\\private\\report.txt")]
    [TestCase("\\\\server\\private\\report.txt")]
    [TestCase("..\\private\\report.txt")]
    [TestCase("reports\\..\\private\\report.txt")]
    [TestCase("reports/\u202esecret.txt")]
    [TestCase("reports/secret\n.txt")]
    public void ApplyPreparedSnapshot_RejectsUnsafeOrMisleadingArtifactPaths(string path)
    {
        Guid requestId = Guid.NewGuid();
        ModHealthViewerSession session = Create(requestId);

        session.ApplyPreparedSnapshot(new(ModHealthPreparedReportState.Saved, requestId, TextPath: path, JsonPath: path), new ModHealthReportPresentationMapper());

        session.TextPath.Should().BeNull();
        session.JsonPath.Should().BeNull();
    }

    [Test]
    public void ApplyPreparedSnapshot_PreservesCompleteBoundedRelativeArtifactPath()
    {
        Guid requestId = Guid.NewGuid();
        string path = $"HealthReports/{new string('a', 4000)}.json";
        ModHealthViewerSession session = Create(requestId);

        session.ApplyPreparedSnapshot(new(ModHealthPreparedReportState.Saved, requestId, TextPath: path), new ModHealthReportPresentationMapper());

        session.TextPath.Should().Be(path);
    }

    [Test]
    public void ApplyPreparedSnapshot_AdvancesDisplayRevisionWhenOnlyExactStatusOrArtifactChanges()
    {
        Guid requestId = Guid.NewGuid();
        ModHealthViewerSession session = Create(requestId);
        long initial = session.ProjectionRevision;

        session.ApplyPreparedSnapshot(new(ModHealthPreparedReportState.Preparing, requestId), new ModHealthReportPresentationMapper());
        long preparing = session.ProjectionRevision;
        session.ApplyPreparedSnapshot(new(ModHealthPreparedReportState.Saved, requestId, TextPath: "reports/report.txt"), new ModHealthReportPresentationMapper());

        preparing.Should().BeGreaterThan(initial);
        session.ProjectionRevision.Should().BeGreaterThan(preparing);
    }

    [Test]
    public void QueueAction_RetainsRejectedFullDispositionForVisibleMenuNotice()
    {
        ModHealthViewerSession session = new(Guid.NewGuid(), Guid.NewGuid(), (_, _, _) => ModHealthViewerActionDisposition.RejectedFull);

        session.QueueAction(ModHealthViewerActionKind.AddMark).Should().Be(ModHealthViewerActionDisposition.RejectedFull);

        session.LastActionDisposition.Should().Be(ModHealthViewerActionDisposition.RejectedFull);
        session.ProjectionRevision.Should().BePositive();
    }

    [Test]
    public void ApplyPreparedSnapshot_WiresMenuTranslatorIntoCanonicalContent()
    {
        Guid requestId = Guid.NewGuid();
        ModHealthViewerSession session = new(
            Guid.NewGuid(),
            requestId,
            (_, _, _) => ModHealthViewerActionDisposition.Queued,
            key => key == "health-view.content.summary.report.title" ? "Bericht {0}" : key
        );

        session.ApplyPreparedSnapshot(new(ModHealthPreparedReportState.Saved, requestId, Model: ModHealthReportFixtureFactory.CreateCanonical()), new ModHealthReportPresentationMapper());

        session.Content!.GetPage(ModHealthViewerSection.Overview, 0, 1)[0].Title.Should().StartWith("Bericht ");
    }

    private static ModHealthViewerSession Create(Guid requestId)
    {
        return new(Guid.NewGuid(), requestId, (_, _, _) => ModHealthViewerActionDisposition.Queued);
    }
}
