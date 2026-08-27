using System;
using StardewModdingAPI.Framework.Health.Presentation;
using StardewModdingAPI.Framework.Health.Viewer.Content;

namespace StardewModdingAPI.Framework.Health.Viewer.Game;

/// <summary>The game-state boundary used to attach and remove one screen's health viewer safely.</summary>
internal interface IModHealthViewerHost
{
    bool CanOpen(out string refusalTranslationKey);

    bool TryOpen(ModHealthViewerSession session, ModHealthViewerController controller, Func<string, string> translate, out string refusalTranslationKey);

    bool Owns(Guid viewerInstanceId);

    void CloseOwned(Guid viewerInstanceId);

    void Release(Guid viewerInstanceId);
}

/// <summary>The narrow coordinator boundary consumed by the game-thread viewer controller.</summary>
internal interface IModHealthViewerCoordinator
{
    ModHealthViewPreparation PrepareHealthView(bool forceRefresh);

    ModHealthPreparedReportSnapshot GetPreparedHealthReport(Guid requestId);

    ModHealthViewerActionState GetViewerActionState();

    ModHealthCoordinatorResult StartHealth();

    ModHealthCoordinatorResult Mark();

    ModHealthCoordinatorResult StopHealth();

    ModHealthCoordinatorResult RetryHealthExport(Guid requestId);
}

/// <summary>Adapts the existing atomic health coordinator without adding another source of report state.</summary>
internal sealed class ModHealthViewerCoordinatorAdapter : IModHealthViewerCoordinator
{
    private readonly ModHealthSessionCoordinator Coordinator;

    public ModHealthViewerCoordinatorAdapter(ModHealthSessionCoordinator coordinator)
    {
        this.Coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    public ModHealthViewPreparation PrepareHealthView(bool forceRefresh) => this.Coordinator.PrepareHealthView(forceRefresh);

    public ModHealthPreparedReportSnapshot GetPreparedHealthReport(Guid requestId) => this.Coordinator.GetPreparedHealthReport(requestId);

    public ModHealthViewerActionState GetViewerActionState() => this.Coordinator.GetViewerActionState();

    public ModHealthCoordinatorResult StartHealth() => this.Coordinator.StartHealth();

    public ModHealthCoordinatorResult Mark() => this.Coordinator.Mark();

    public ModHealthCoordinatorResult StopHealth() => this.Coordinator.StopHealth();

    public ModHealthCoordinatorResult RetryHealthExport(Guid requestId) => this.Coordinator.RetryHealthExport(requestId);
}

/// <summary>
/// Owns one screen's bounded viewer action queue and applies it only at the caller's safe game-thread boundary.
/// The closed fast path checks one integer-backed property and performs no allocation.
/// </summary>
internal sealed class ModHealthViewerController
{
    private readonly IModHealthViewerCoordinator Coordinator;
    private readonly IModHealthViewerHost Host;
    private readonly Func<string, string> Translate;
    private readonly Action<string>? Notify;
    private readonly Action? RequestDrain;
    private readonly ModHealthReportPresentationMapper Mapper = new();
    private readonly ModHealthViewerActionQueue Actions = new();

    private ModHealthViewerSession? Session;
    private Guid PendingOpenViewerId;

    /// <summary>Construct a controller backed by the real game menu host.</summary>
    public ModHealthViewerController(ModHealthSessionCoordinator coordinator, Func<string, string> translate, Action<string>? notify = null, Action? requestDrain = null)
        : this(new ModHealthViewerCoordinatorAdapter(coordinator), new StardewModHealthViewerHost(), translate, notify, requestDrain)
    {
    }

    /// <summary>Construct a controller with pure seams for safety and ownership tests.</summary>
    internal ModHealthViewerController(IModHealthViewerCoordinator coordinator, IModHealthViewerHost host, Func<string, string> translate, Action<string>? notify = null, Action? requestDrain = null)
    {
        this.Coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.Host = host ?? throw new ArgumentNullException(nameof(host));
        this.Translate = translate ?? throw new ArgumentNullException(nameof(translate));
        this.Notify = notify;
        this.RequestDrain = requestDrain;
    }

    /// <summary>Whether this screen has actions waiting for the safe pre-update drain.</summary>
    public bool HasPendingActions => this.Actions.HasPendingActions;

    /// <summary>The latest translation key describing an open refusal or applied operation.</summary>
    public string? LastNoticeTranslationKey { get; private set; }

    /// <summary>Queue an explicit command request to open the viewer. This does not inspect or mutate game state.</summary>
    public ModHealthViewerActionDisposition QueueOpen()
    {
        if (this.Session is not null && this.Host.Owns(this.Session.ViewerInstanceId))
            return ModHealthViewerActionDisposition.Coalesced;

        if (this.PendingOpenViewerId == Guid.Empty)
            this.PendingOpenViewerId = Guid.NewGuid();
        ModHealthViewerActionDisposition disposition = this.Enqueue(new(ModHealthViewerActionKind.Open, this.PendingOpenViewerId));
        if (disposition == ModHealthViewerActionDisposition.RejectedFull)
            this.PendingOpenViewerId = Guid.Empty;
        return disposition;
    }

    /// <summary>Drain queued work at SCore's safe pre-base-update boundary.</summary>
    public void DrainPendingActions()
    {
        if (!this.Actions.HasPendingActions)
            return;

        while (this.Actions.TryDequeue(out ModHealthViewerAction action))
        {
            if (action.Kind == ModHealthViewerActionKind.Open)
                this.Open(action.ViewerInstanceId);
            else
                this.ApplyOwnedAction(action);
        }
        this.Session?.AcknowledgeActionQueueDrained();
    }

    /// <summary>Poll the exact prepared model during an owned menu update without blocking or reading disk/live diagnostic sources.</summary>
    public void UpdateOwnedViewer(Guid viewerInstanceId)
    {
        ModHealthViewerSession? session = this.Session;
        if (session is null || session.ViewerInstanceId != viewerInstanceId)
            return;
        if (!this.Host.Owns(viewerInstanceId))
        {
            this.HandleViewerClosed(viewerInstanceId);
            return;
        }

        ModHealthPreparedReportSnapshot prepared = this.Coordinator.GetPreparedHealthReport(session.RequestId);
        session.ApplyPreparedSnapshot(prepared, this.Mapper);
        this.RefreshAvailableActions(session);
    }

    /// <summary>Release references when the game closes or replaces the open menu outside the viewer action queue.</summary>
    public void HandleViewerClosed(Guid viewerInstanceId)
    {
        if (this.Session?.ViewerInstanceId != viewerInstanceId)
            return;
        this.Host.Release(viewerInstanceId);
        this.Session = null;
    }

    /// <summary>Queue an action from the menu for the next safe drain.</summary>
    public ModHealthViewerActionDisposition QueueOwnedAction(ModHealthViewerActionKind kind, Guid viewerInstanceId, Guid expectedRequestId)
    {
        if (kind == ModHealthViewerActionKind.Open)
            return ModHealthViewerActionDisposition.RejectedFull;
        return this.Enqueue(new(kind, viewerInstanceId, expectedRequestId));
    }

    private void Open(Guid viewerInstanceId)
    {
        if (this.PendingOpenViewerId == viewerInstanceId)
            this.PendingOpenViewerId = Guid.Empty;

        if (this.Session is not null && this.Host.Owns(this.Session.ViewerInstanceId))
        {
            this.SetNotice(ModHealthViewerTranslationKeys.AlreadyOpen);
            return;
        }
        if (!this.Host.CanOpen(out string refusalKey))
        {
            this.SetNotice(refusalKey);
            return;
        }

        // Safety is checked before PrepareHealthView so an unsafe menu never queues report work.
        ModHealthViewPreparation prepared = this.Coordinator.PrepareHealthView(forceRefresh: false);
        ModHealthViewerSession session = new(viewerInstanceId, prepared.RequestId, this.QueueOwnedAction, this.Translate);
        session.ApplyPreparedSnapshot(prepared.PreparedReport, this.Mapper);
        this.RefreshAvailableActions(session);
        if (!this.Host.TryOpen(session, this, this.Translate, out refusalKey))
        {
            this.SetNotice(refusalKey);
            return;
        }

        this.Session = session;
        if (prepared.Operation.IsError)
            this.SetOperationNotice(prepared.Operation);
        else
            this.SetNotice(ModHealthViewerTranslationKeys.Opened);
    }

    private void ApplyOwnedAction(ModHealthViewerAction action)
    {
        ModHealthViewerSession? session = this.Session;
        if (session is null
            || action.ViewerInstanceId != session.ViewerInstanceId
            || !this.Host.Owns(session.ViewerInstanceId)
            || (action.ExpectedRequestId is Guid expectedId && expectedId != session.RequestId))
        {
            return;
        }

        switch (action.Kind)
        {
            case ModHealthViewerActionKind.StartCapture:
                this.ApplyAndRefresh(session, this.Coordinator.StartHealth());
                break;

            case ModHealthViewerActionKind.AddMark:
                this.SetOperationNotice(this.Coordinator.Mark());
                this.RefreshAvailableActions(session);
                break;

            case ModHealthViewerActionKind.RefreshAndSaveSnapshot:
                this.SwitchRequest(session, this.Coordinator.PrepareHealthView(forceRefresh: true));
                break;

            case ModHealthViewerActionKind.StopCapture:
                this.ApplyAndRefresh(session, this.Coordinator.StopHealth());
                break;

            case ModHealthViewerActionKind.RetrySave:
                this.SetOperationNotice(this.Coordinator.RetryHealthExport(session.RequestId));
                session.ApplyPreparedSnapshot(this.Coordinator.GetPreparedHealthReport(session.RequestId), this.Mapper);
                this.RefreshAvailableActions(session);
                break;

            case ModHealthViewerActionKind.ViewNewer:
                if (session.NewerRequestId is Guid newerRequestId)
                {
                    session.SwitchRequest(newerRequestId);
                    session.ApplyPreparedSnapshot(this.Coordinator.GetPreparedHealthReport(newerRequestId), this.Mapper);
                    this.RefreshAvailableActions(session);
                }
                break;

            case ModHealthViewerActionKind.Close:
                this.Host.CloseOwned(session.ViewerInstanceId);
                if (!this.Host.Owns(session.ViewerInstanceId))
                    this.HandleViewerClosed(session.ViewerInstanceId);
                break;
        }
    }

    private void ApplyAndRefresh(ModHealthViewerSession session, ModHealthCoordinatorResult operation)
    {
        this.SetOperationNotice(operation);
        if (!operation.IsError)
            this.SwitchRequest(session, this.Coordinator.PrepareHealthView(forceRefresh: true));
        else
            this.RefreshAvailableActions(session);
    }

    private void SwitchRequest(ModHealthViewerSession session, ModHealthViewPreparation preparation)
    {
        this.SetOperationNotice(preparation.Operation);
        session.SwitchRequest(preparation.RequestId);
        session.ApplyPreparedSnapshot(preparation.PreparedReport, this.Mapper);
        this.RefreshAvailableActions(session);
    }

    private void RefreshAvailableActions(ModHealthViewerSession session)
    {
        session.RefreshAvailableActions(this.Coordinator.GetViewerActionState().CaptureState);
    }

    private ModHealthViewerActionDisposition Enqueue(ModHealthViewerAction action)
    {
        bool wasEmpty = !this.Actions.HasPendingActions;
        ModHealthViewerActionDisposition disposition = this.Actions.Enqueue(action);
        if (wasEmpty && this.Actions.HasPendingActions)
            this.RequestDrain?.Invoke();
        return disposition;
    }

    private void SetOperationNotice(ModHealthCoordinatorResult operation)
    {
        this.SetNotice(operation.IsError
            ? ModHealthViewerTranslationKeys.OperationRefused
            : ModHealthViewerTranslationKeys.OperationAccepted);
    }

    private void SetNotice(string translationKey)
    {
        this.LastNoticeTranslationKey = translationKey;
        this.Notify?.Invoke(this.Translate(translationKey));
    }
}
