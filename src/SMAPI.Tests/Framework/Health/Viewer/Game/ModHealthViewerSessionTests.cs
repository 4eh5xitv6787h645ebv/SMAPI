using System;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.Health;
using StardewModdingAPI.Framework.Health.Presentation;
using StardewModdingAPI.Framework.Health.Viewer;
using StardewModdingAPI.Framework.Health.Viewer.Game;

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

    private static ModHealthViewerSession Create(Guid requestId)
    {
        return new(Guid.NewGuid(), requestId, (_, _, _) => ModHealthViewerActionDisposition.Queued);
    }
}
