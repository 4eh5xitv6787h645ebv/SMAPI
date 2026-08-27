using System;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.Health;
using StardewModdingAPI.Framework.Health.Viewer;
using StardewModdingAPI.Framework.Health.Viewer.Game;
using StardewModdingAPI.Framework.Performance;

namespace SMAPI.Tests.Framework.Health.Viewer.Game;

[TestFixture]
internal sealed class ModHealthViewerControllerTests
{
    [Test]
    public void QueueOpen_DoesNotTouchCoordinatorUntilSafeDrain()
    {
        FakeCoordinator coordinator = new();
        FakeHost host = new();
        ModHealthViewerController controller = new(coordinator, host, key => key);

        controller.QueueOpen().Should().Be(ModHealthViewerActionDisposition.Queued);

        coordinator.PrepareCalls.Should().Be(0);
        host.OpenCalls.Should().Be(0);
        controller.HasPendingActions.Should().BeTrue();

        controller.DrainPendingActions();

        coordinator.PrepareCalls.Should().Be(1);
        host.OpenCalls.Should().Be(1);
        host.Session.Should().NotBeNull();
        host.Session!.RequestId.Should().Be(coordinator.InitialRequestId);
    }

    [Test]
    public void Open_RefusesUnsafeHostBeforeQueueingReportWork()
    {
        FakeCoordinator coordinator = new();
        FakeHost host = new() { CanAttach = false, RefusalKey = ModHealthViewerTranslationKeys.UnsafeState };
        string? notice = null;
        ModHealthViewerController controller = new(coordinator, host, key => key, message => notice = message);

        controller.QueueOpen();
        controller.DrainPendingActions();

        coordinator.PrepareCalls.Should().Be(0);
        host.OpenCalls.Should().Be(0);
        controller.LastNoticeTranslationKey.Should().Be(ModHealthViewerTranslationKeys.UnsafeState);
        notice.Should().Be(ModHealthViewerTranslationKeys.UnsafeState);
    }

    [Test]
    public void Open_RejectedPreparationKeepsExplicitStateAndRefusalNotice()
    {
        FakeCoordinator coordinator = new() { RejectPreparation = true };
        FakeHost host = new();
        ModHealthViewerController controller = new(coordinator, host, key => key);

        controller.QueueOpen();
        controller.DrainPendingActions();

        host.Session!.PreparedState.Should().Be(ModHealthPreparedReportState.Rejected);
        controller.LastNoticeTranslationKey.Should().Be(ModHealthViewerTranslationKeys.OperationRefused);
        host.IsOwned.Should().BeTrue("the exact rejected request remains visible instead of being silently substituted");
    }

    [Test]
    public void CloseWhilePreparing_ReleasesOnlyExactOwnedViewer()
    {
        FakeCoordinator coordinator = new();
        FakeHost host = new();
        ModHealthViewerController controller = Open(coordinator, host);
        Guid viewerId = host.Session!.ViewerInstanceId;
        Guid requestId = host.Session.RequestId;

        controller.QueueOwnedAction(ModHealthViewerActionKind.Close, Guid.NewGuid(), requestId);
        controller.QueueOwnedAction(ModHealthViewerActionKind.Close, viewerId, requestId);
        controller.DrainPendingActions();

        host.CloseCalls.Should().Be(1);
        host.IsOwned.Should().BeFalse();
        coordinator.GetPreparedCalls.Should().Be(0);
    }

    [Test]
    public void Actions_RequireExactTokenAndRequestId()
    {
        FakeCoordinator coordinator = new();
        FakeHost host = new();
        ModHealthViewerController controller = Open(coordinator, host);
        Guid viewerId = host.Session!.ViewerInstanceId;
        Guid requestId = host.Session.RequestId;

        controller.QueueOwnedAction(ModHealthViewerActionKind.RetrySave, Guid.NewGuid(), requestId);
        controller.QueueOwnedAction(ModHealthViewerActionKind.RetrySave, viewerId, Guid.NewGuid());
        controller.DrainPendingActions();

        coordinator.RetryCalls.Should().Be(0);

        controller.QueueOwnedAction(ModHealthViewerActionKind.RetrySave, viewerId, requestId);
        controller.DrainPendingActions();
        coordinator.RetryCalls.Should().Be(1);
        coordinator.LastRetriedId.Should().Be(requestId);
    }

    [Test]
    public void SupersededViewer_ChangesOnlyThroughExplicitViewNewerAction()
    {
        FakeCoordinator coordinator = new();
        FakeHost host = new();
        ModHealthViewerController controller = Open(coordinator, host);
        ModHealthViewerSession session = host.Session!;
        Guid oldId = session.RequestId;
        Guid newerId = Guid.NewGuid();
        coordinator.Snapshots[oldId] = new(ModHealthPreparedReportState.Superseded, oldId, NewerRequestId: newerId);
        coordinator.Snapshots[newerId] = new(ModHealthPreparedReportState.Preparing, newerId);

        controller.UpdateOwnedViewer(session.ViewerInstanceId);

        session.RequestId.Should().Be(oldId);
        session.NewerRequestId.Should().Be(newerId);
        session.PreparedState.Should().Be(ModHealthPreparedReportState.Superseded);

        controller.QueueOwnedAction(ModHealthViewerActionKind.ViewNewer, session.ViewerInstanceId, oldId);
        controller.DrainPendingActions();

        session.RequestId.Should().Be(newerId);
        session.PreparedState.Should().Be(ModHealthPreparedReportState.Preparing);
    }

    [Test]
    public void Controllers_AreScreenLocalAndUseDistinctOwnershipTokens()
    {
        FakeHost firstHost = new();
        FakeHost secondHost = new();
        ModHealthViewerController first = Open(new FakeCoordinator(), firstHost);
        ModHealthViewerController second = Open(new FakeCoordinator(), secondHost);

        firstHost.Session!.ViewerInstanceId.Should().NotBe(secondHost.Session!.ViewerInstanceId);

        first.QueueOwnedAction(ModHealthViewerActionKind.Close, secondHost.Session.ViewerInstanceId, firstHost.Session.RequestId);
        first.DrainPendingActions();
        firstHost.IsOwned.Should().BeTrue();
        secondHost.IsOwned.Should().BeTrue();
    }

    [Test]
    public void OwnershipLoss_ReleasesHostAndControllerReferencesWithoutClosedPolling()
    {
        FakeCoordinator coordinator = new();
        FakeHost host = new();
        ModHealthViewerController controller = Open(coordinator, host);
        Guid viewerId = host.Session!.ViewerInstanceId;

        host.ForceOwnershipLoss();
        controller.HandleViewerClosed(viewerId);

        host.ReleaseCalls.Should().Be(1);
        host.Session.Should().BeNull();
        controller.QueueOpen().Should().Be(ModHealthViewerActionDisposition.Queued);
    }

    [Test]
    public void OpenAndPoll_RefreshCaptureContextActions()
    {
        FakeCoordinator coordinator = new() { CaptureState = ModHealthCaptureState.Inactive };
        FakeHost host = new();
        ModHealthViewerController controller = Open(coordinator, host);

        GetActions(host.Session!).Should().ContainInOrder(ModHealthViewerActionKind.StartCapture, ModHealthViewerActionKind.RefreshAndSaveSnapshot, ModHealthViewerActionKind.Close);

        coordinator.CaptureState = ModHealthCaptureState.Active;
        int actionStateCallsBeforePoll = coordinator.GetViewerActionStateCalls;
        controller.UpdateOwnedViewer(host.Session!.ViewerInstanceId);

        GetActions(host.Session).Should().ContainInOrder(ModHealthViewerActionKind.AddMark, ModHealthViewerActionKind.StopCapture, ModHealthViewerActionKind.RefreshAndSaveSnapshot, ModHealthViewerActionKind.Close);
        GetActions(host.Session).Should().NotContain(ModHealthViewerActionKind.StartCapture);
        coordinator.GetViewerActionStateCalls.Should().Be(actionStateCallsBeforePoll + 1);
    }

    [Test]
    public void ClosedFastPath_DoesNotAllocateOrCallDependencies()
    {
        FakeCoordinator coordinator = new();
        FakeHost host = new();
        ModHealthViewerController controller = new(coordinator, host, key => key);
        controller.DrainPendingActions();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            if (controller.HasPendingActions)
                controller.DrainPendingActions();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().Be(0);
        coordinator.PrepareCalls.Should().Be(0);
        host.OpenCalls.Should().Be(0);
    }

    [Test]
    public void PendingDrain_IsScheduledOncePerNonemptyQueueAndAgainAfterClose()
    {
        int drainRequests = 0;
        FakeCoordinator coordinator = new();
        FakeHost host = new();
        ModHealthViewerController controller = new(coordinator, host, key => key, requestDrain: () => drainRequests++);

        controller.QueueOpen().Should().Be(ModHealthViewerActionDisposition.Queued);
        controller.QueueOpen().Should().Be(ModHealthViewerActionDisposition.Coalesced);
        drainRequests.Should().Be(1);
        controller.DrainPendingActions();

        ModHealthViewerSession session = host.Session!;
        session.QueueAction(ModHealthViewerActionKind.AddMark).Should().Be(ModHealthViewerActionDisposition.Queued);
        session.QueueAction(ModHealthViewerActionKind.AddMark).Should().Be(ModHealthViewerActionDisposition.Queued);
        drainRequests.Should().Be(2, "only the empty-to-nonempty transition schedules the existing safe command queue");
        controller.DrainPendingActions();

        session.QueueAction(ModHealthViewerActionKind.Close).Should().Be(ModHealthViewerActionDisposition.Queued);
        drainRequests.Should().Be(3);
        controller.DrainPendingActions();
        controller.QueueOpen().Should().Be(ModHealthViewerActionDisposition.Queued);
        drainRequests.Should().Be(4, "a closed controller schedules nothing until another explicit open request");
    }

    [Test]
    public void QueueFullFeedback_ClearsAfterTheSafeDrainAppliesQueuedActions()
    {
        FakeCoordinator coordinator = new();
        FakeHost host = new();
        ModHealthViewerController controller = Open(coordinator, host);
        ModHealthViewerSession session = host.Session!;

        for (int index = 0; index < ModHealthViewerActionQueue.Capacity; index++)
            session.QueueAction(ModHealthViewerActionKind.AddMark).Should().Be(ModHealthViewerActionDisposition.Queued);
        session.QueueAction(ModHealthViewerActionKind.AddMark).Should().Be(ModHealthViewerActionDisposition.RejectedFull);
        session.LastActionDisposition.Should().Be(ModHealthViewerActionDisposition.RejectedFull);

        controller.DrainPendingActions();

        coordinator.MarkCalls.Should().Be(ModHealthViewerActionQueue.Capacity);
        session.LastActionDisposition.Should().BeNull();
    }

    [Test]
    public void OpenViewerPolling_UsesLightweightStateWithoutFreezingDiagnosticsOrAllocating()
    {
        int performanceTimestampReads = 0;
        int ledgerSnapshots = 0;
        ModPerformanceManager manager = new(timestampFrequency: 1000, getTimestamp: () =>
        {
            performanceTimestampReads++;
            return 0;
        }, getGcCollectionCount: _ => 0);
        ModHealthLedger ledger = new(timestampFrequency: 1000, getTimestamp: () => 0, onSnapshotLocksReleased: () => ledgerSnapshots++);
        PollExportQueue queue = new();
        ModHealthSessionCoordinator coordinator = new(
            manager,
            ledger,
            queue,
            getEnvironment: () => new ModHealthEnvironmentSnapshot("4.5.2", "abcdef0", "1.6.15", ".NET 10", "x64", 64, "Linux", "6.0", "wayland", "en", 8, "single-player", 1, false, false)
        );
        FakeHost host = new();
        ModHealthViewerController controller = new(new ModHealthViewerCoordinatorAdapter(coordinator), host, static key => key);
        controller.QueueOpen();
        controller.DrainPendingActions();
        Guid viewerId = host.Session!.ViewerInstanceId;

        controller.UpdateOwnedViewer(viewerId);
        int performanceReadsBefore = performanceTimestampReads;
        int ledgerSnapshotsBefore = ledgerSnapshots;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
            controller.UpdateOwnedViewer(viewerId);

        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
        performanceTimestampReads.Should().Be(performanceReadsBefore, "menu polling must not freeze performance diagnostics");
        ledgerSnapshots.Should().Be(ledgerSnapshotsBefore, "menu polling must not freeze the session ledger");
    }

    private static ModHealthViewerController Open(FakeCoordinator coordinator, FakeHost host)
    {
        ModHealthViewerController controller = new(coordinator, host, key => key);
        controller.QueueOpen();
        controller.DrainPendingActions();
        return controller;
    }

    private static ModHealthViewerActionKind[] GetActions(ModHealthViewerSession session)
    {
        ModHealthViewerActionKind[] actions = new ModHealthViewerActionKind[session.AvailableActionCount];
        for (int index = 0; index < actions.Length; index++)
            actions[index] = session.GetAvailableAction(index);
        return actions;
    }

    private sealed class FakeHost : IModHealthViewerHost
    {
        public bool CanAttach { get; init; } = true;
        public string RefusalKey { get; init; } = ModHealthViewerTranslationKeys.MenuBusy;
        public bool IsOwned { get; private set; }
        public int OpenCalls { get; private set; }
        public int CloseCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public ModHealthViewerSession? Session { get; private set; }

        public bool CanOpen(out string refusalTranslationKey)
        {
            refusalTranslationKey = this.CanAttach ? string.Empty : this.RefusalKey;
            return this.CanAttach;
        }

        public bool TryOpen(ModHealthViewerSession session, ModHealthViewerController controller, Func<string, string> translate, out string refusalTranslationKey)
        {
            this.OpenCalls++;
            this.Session = session;
            this.IsOwned = this.CanAttach;
            refusalTranslationKey = this.CanAttach ? string.Empty : this.RefusalKey;
            return this.CanAttach;
        }

        public bool Owns(Guid viewerInstanceId) => this.IsOwned && this.Session?.ViewerInstanceId == viewerInstanceId;

        public void CloseOwned(Guid viewerInstanceId)
        {
            if (!this.Owns(viewerInstanceId))
                return;
            this.CloseCalls++;
            this.IsOwned = false;
        }

        public void Release(Guid viewerInstanceId)
        {
            if (this.Session?.ViewerInstanceId != viewerInstanceId)
                return;
            this.ReleaseCalls++;
            this.Session = null;
            this.IsOwned = false;
        }

        public void ForceOwnershipLoss()
        {
            this.IsOwned = false;
        }
    }

    private sealed class FakeCoordinator : IModHealthViewerCoordinator
    {
        public Guid InitialRequestId { get; } = Guid.NewGuid();
        public System.Collections.Generic.Dictionary<Guid, ModHealthPreparedReportSnapshot> Snapshots { get; } = new();
        public int PrepareCalls { get; private set; }
        public int GetPreparedCalls { get; private set; }
        public int RetryCalls { get; private set; }
        public int MarkCalls { get; private set; }
        public int GetViewerActionStateCalls { get; private set; }
        public Guid? LastRetriedId { get; private set; }
        public ModHealthCaptureState CaptureState { get; set; } = ModHealthCaptureState.Inactive;
        public bool RejectPreparation { get; init; }

        public ModHealthViewPreparation PrepareHealthView(bool forceRefresh)
        {
            this.PrepareCalls++;
            ModHealthPreparedReportSnapshot snapshot = new(
                this.RejectPreparation ? ModHealthPreparedReportState.Rejected : ModHealthPreparedReportState.Preparing,
                this.InitialRequestId
            );
            this.Snapshots[this.InitialRequestId] = snapshot;
            ModHealthCoordinatorResult operation = this.RejectPreparation
                ? new(ModHealthCoordinatorResultCode.Refused, "busy", IsError: true)
                : Success();
            return new(this.InitialRequestId, operation, snapshot);
        }

        public ModHealthPreparedReportSnapshot GetPreparedHealthReport(Guid requestId)
        {
            this.GetPreparedCalls++;
            return this.Snapshots.TryGetValue(requestId, out ModHealthPreparedReportSnapshot? snapshot)
                ? snapshot
                : new(ModHealthPreparedReportState.Absent, requestId);
        }

        public ModHealthViewerActionState GetViewerActionState()
        {
            this.GetViewerActionStateCalls++;
            return new(this.CaptureState);
        }

        public ModHealthCoordinatorResult StartHealth() => Success();

        public ModHealthCoordinatorResult Mark()
        {
            this.MarkCalls++;
            return Success();
        }

        public ModHealthCoordinatorResult StopHealth() => Success();

        public ModHealthCoordinatorResult RetryHealthExport(Guid requestId)
        {
            this.RetryCalls++;
            this.LastRetriedId = requestId;
            return Success();
        }

        private static ModHealthCoordinatorResult Success() => new(ModHealthCoordinatorResultCode.ExportQueued, "ok");
    }

    private sealed class PollExportQueue : IModHealthExportQueue
    {
        private ModHealthPreparedReportSnapshot Prepared = ModHealthPreparedReportSnapshot.Absent;

        public ModHealthExportQueueResult Enqueue(ModHealthExportRequest request)
        {
            this.Prepared = new(ModHealthPreparedReportState.Preparing, request.RequestId, request.IsFinal);
            return new(ModHealthExportDisposition.Queued, new(ModHealthExportState.Queued, request.RequestId, request.IsFinal));
        }

        public ModHealthExportQueueResult Retry(Guid? requestId = null) => new(ModHealthExportDisposition.NoRetryableExport, ModHealthExportStatus.None);

        public void DiscardRetryable(Guid? requestId = null) { }

        public ModHealthExportStatus GetStatus(Guid? requestId = null) => this.Prepared.RequestId is Guid id && (requestId is null || requestId == id)
            ? new(ModHealthExportState.Queued, id, this.Prepared.IsFinal)
            : ModHealthExportStatus.None;

        public ModHealthPreparedReportSnapshot GetPreparedReport(Guid? requestId = null) => this.Prepared.RequestId is Guid id && (requestId is null || requestId == id)
            ? this.Prepared
            : ModHealthPreparedReportSnapshot.Absent;
    }
}
