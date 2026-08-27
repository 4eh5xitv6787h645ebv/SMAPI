using System;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.Health;
using StardewModdingAPI.Framework.Health.Viewer;
using StardewModdingAPI.Framework.Health.Viewer.Game;

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
        controller.UpdateOwnedViewer(host.Session!.ViewerInstanceId);

        GetActions(host.Session).Should().ContainInOrder(ModHealthViewerActionKind.AddMark, ModHealthViewerActionKind.StopCapture, ModHealthViewerActionKind.RefreshAndSaveSnapshot, ModHealthViewerActionKind.Close);
        GetActions(host.Session).Should().NotContain(ModHealthViewerActionKind.StartCapture);
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
        public Guid? LastRetriedId { get; private set; }
        public ModHealthCaptureState CaptureState { get; set; } = ModHealthCaptureState.Inactive;

        public ModHealthViewPreparation PrepareHealthView(bool forceRefresh)
        {
            this.PrepareCalls++;
            ModHealthPreparedReportSnapshot snapshot = new(ModHealthPreparedReportState.Preparing, this.InitialRequestId);
            this.Snapshots[this.InitialRequestId] = snapshot;
            return new(this.InitialRequestId, Success(), snapshot);
        }

        public ModHealthPreparedReportSnapshot GetPreparedHealthReport(Guid requestId)
        {
            this.GetPreparedCalls++;
            return this.Snapshots.TryGetValue(requestId, out ModHealthPreparedReportSnapshot? snapshot)
                ? snapshot
                : new(ModHealthPreparedReportState.Absent, requestId);
        }

        public ModHealthSessionStatus GetStatus() => new(
            this.CaptureState,
            ModHealthCaptureOwner.None,
            null,
            null,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            false,
            ModHealthExportStatus.None,
            false
        );

        public ModHealthCoordinatorResult StartHealth() => Success();

        public ModHealthCoordinatorResult Mark() => Success();

        public ModHealthCoordinatorResult StopHealth() => Success();

        public ModHealthCoordinatorResult RetryHealthExport(Guid requestId)
        {
            this.RetryCalls++;
            this.LastRetriedId = requestId;
            return Success();
        }

        private static ModHealthCoordinatorResult Success() => new(ModHealthCoordinatorResultCode.ExportQueued, "ok");
    }
}
