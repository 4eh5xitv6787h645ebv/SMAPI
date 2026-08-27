using System;
using System.Collections.Immutable;
using System.IO;
using StardewModdingAPI.Framework.Health.Presentation;
using StardewModdingAPI.Framework.Health.Viewer.Content;
using StardewModdingAPI.Framework.Health.Viewer.Layout;

namespace StardewModdingAPI.Framework.Health.Viewer.Game;

/// <summary>Pure, screen-local menu state tied to an exact viewer token and exact report request.</summary>
internal sealed class ModHealthViewerSession
{
    private readonly Func<ModHealthViewerActionKind, Guid, Guid, ModHealthViewerActionDisposition> EnqueueAction;
    private readonly Func<string, string>? TranslateContent;
    private readonly ModHealthViewerActionKind[] AvailableActions = new ModHealthViewerActionKind[ModHealthViewerLayout.MaximumActions];

    private ModHealthReport? MappedModel;

    public ModHealthViewerSession(Guid viewerInstanceId, Guid requestId, Func<ModHealthViewerActionKind, Guid, Guid, ModHealthViewerActionDisposition> enqueueAction, Func<string, string>? translateContent = null)
    {
        if (viewerInstanceId == Guid.Empty)
            throw new ArgumentException("A viewer instance ID is required.", nameof(viewerInstanceId));
        if (requestId == Guid.Empty)
            throw new ArgumentException("A report request ID is required.", nameof(requestId));
        this.ViewerInstanceId = viewerInstanceId;
        this.RequestId = requestId;
        this.EnqueueAction = enqueueAction ?? throw new ArgumentNullException(nameof(enqueueAction));
        this.TranslateContent = translateContent;
        this.RefreshAvailableActions(captureState: null);
    }

    public Guid ViewerInstanceId { get; }

    public Guid RequestId { get; private set; }

    public Guid? NewerRequestId { get; private set; }

    public ModHealthPreparedReportState PreparedState { get; private set; } = ModHealthPreparedReportState.Absent;

    public ModHealthViewerContentAdapter? Content { get; private set; }

    public string? TextPath { get; private set; }

    public string? JsonPath { get; private set; }

    public string? Error { get; private set; }

    public int AvailableActionCount { get; private set; }

    public ModHealthViewerActionDisposition? LastActionDisposition { get; private set; }

    /// <summary>Changes whenever the exact request or any displayed prepared/action state changes.</summary>
    public long ProjectionRevision { get; private set; }

    /// <summary>Switch explicitly to another exact request, clearing every projection from the old model.</summary>
    public void SwitchRequest(Guid requestId)
    {
        if (requestId == Guid.Empty)
            throw new ArgumentException("A report request ID is required.", nameof(requestId));
        if (this.RequestId == requestId)
            return;
        this.RequestId = requestId;
        this.PreparedState = ModHealthPreparedReportState.Absent;
        this.NewerRequestId = null;
        this.MappedModel = null;
        this.Content = null;
        this.TextPath = null;
        this.JsonPath = null;
        this.Error = null;
        this.LastActionDisposition = null;
        this.ProjectionRevision++;
        this.RefreshAvailableActions(captureState: null);
    }

    /// <summary>Apply only a snapshot for this session's exact request and map its immutable model at most once.</summary>
    public void ApplyPreparedSnapshot(ModHealthPreparedReportSnapshot snapshot, ModHealthReportPresentationMapper mapper)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(mapper);
        if (snapshot.RequestId is Guid snapshotRequestId && snapshotRequestId != this.RequestId)
            return;
        if (this.PreparedState == ModHealthPreparedReportState.Rejected && snapshot.State == ModHealthPreparedReportState.Absent)
        {
            // A rejected request is never accepted into the prepared-report store, so its exact-ID
            // poll returns Absent. Keep the explicit terminal result until the user refreshes or
            // switches request instead of silently downgrading it on the menu's first update.
            return;
        }

        ModHealthPreparedReportState oldState = this.PreparedState;
        Guid? oldNewerRequestId = this.NewerRequestId;
        string? oldTextPath = this.TextPath;
        string? oldJsonPath = this.JsonPath;
        string? oldError = this.Error;
        ModHealthViewerContentAdapter? oldContent = this.Content;

        this.PreparedState = snapshot.State;
        this.NewerRequestId = snapshot.NewerRequestId;
        this.TextPath = KeepSafeRelativePath(snapshot.TextPath);
        this.JsonPath = KeepSafeRelativePath(snapshot.JsonPath);
        this.Error = snapshot.Error;
        if (snapshot.Model is ModHealthReport model && !ReferenceEquals(model, this.MappedModel))
        {
            this.MappedModel = model;
            this.Content = new ModHealthViewerContentAdapter(mapper.Map(model), this.TranslateContent);
        }
        else if (snapshot.Model is null && snapshot.State is ModHealthPreparedReportState.Absent
            or ModHealthPreparedReportState.FailedBeforeModel
            or ModHealthPreparedReportState.Superseded
            or ModHealthPreparedReportState.Canceled
            or ModHealthPreparedReportState.Disposed
            or ModHealthPreparedReportState.Rejected)
        {
            this.MappedModel = null;
            this.Content = null;
        }
        if (oldState != this.PreparedState
            || oldNewerRequestId != this.NewerRequestId
            || oldTextPath != this.TextPath
            || oldJsonPath != this.JsonPath
            || oldError != this.Error
            || !ReferenceEquals(oldContent, this.Content))
        {
            this.ProjectionRevision++;
        }
        this.RefreshAvailableActions(captureState: null);
    }

    /// <summary>Rebuild the fixed action pool from report and capture state.</summary>
    public void RefreshAvailableActions(ModHealthCaptureState? captureState)
    {
        this.AvailableActionCount = 0;
        if (this.PreparedState is ModHealthPreparedReportState.WriteFailed or ModHealthPreparedReportState.FailedBeforeModel)
            this.AddAction(ModHealthViewerActionKind.RetrySave);
        if (this.PreparedState == ModHealthPreparedReportState.Superseded && this.NewerRequestId.HasValue)
            this.AddAction(ModHealthViewerActionKind.ViewNewer);

        if (captureState is not null)
        {
            if (captureState == ModHealthCaptureState.Inactive)
                this.AddAction(ModHealthViewerActionKind.StartCapture);
            else if (captureState == ModHealthCaptureState.Active)
            {
                this.AddAction(ModHealthViewerActionKind.AddMark);
                this.AddAction(ModHealthViewerActionKind.StopCapture);
            }
        }

        if (this.PreparedState != ModHealthPreparedReportState.Disposed)
            this.AddAction(ModHealthViewerActionKind.RefreshAndSaveSnapshot);
        this.AddAction(ModHealthViewerActionKind.Close);
    }

    public ModHealthViewerActionKind GetAvailableAction(int index)
    {
        if ((uint)index >= this.AvailableActionCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        return this.AvailableActions[index];
    }

    public ModHealthViewerActionDisposition QueueAction(ModHealthViewerActionKind kind)
    {
        ModHealthViewerActionDisposition disposition = this.EnqueueAction(kind, this.ViewerInstanceId, this.RequestId);
        if (this.LastActionDisposition != disposition)
        {
            this.LastActionDisposition = disposition;
            this.ProjectionRevision++;
        }
        return disposition;
    }

    /// <summary>Clear transient queue feedback once the next safe boundary has drained the queue.</summary>
    public void AcknowledgeActionQueueDrained()
    {
        if (this.LastActionDisposition is null or ModHealthViewerActionDisposition.RejectedFull)
            return;
        this.LastActionDisposition = null;
        this.ProjectionRevision++;
    }

    /// <summary>Clear a queue-full refusal only after the owned menu rendered it at least once.</summary>
    public void AcknowledgeActionFeedbackRendered()
    {
        if (this.LastActionDisposition != ModHealthViewerActionDisposition.RejectedFull)
            return;
        this.LastActionDisposition = null;
        this.ProjectionRevision++;
    }

    private void AddAction(ModHealthViewerActionKind kind)
    {
        if (this.AvailableActionCount < this.AvailableActions.Length)
            this.AvailableActions[this.AvailableActionCount++] = kind;
    }

    private static string? KeepSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.Length > 4096
            || Path.IsPathRooted(path)
            || path.Contains(':', StringComparison.Ordinal)
            || ContainsUnsafeDisplayCharacter(path))
        {
            return null;
        }
        string normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.StartsWith("~", StringComparison.Ordinal)
            || normalized == ".."
            || normalized.StartsWith("../", StringComparison.Ordinal)
            || normalized.EndsWith("/..", StringComparison.Ordinal)
            || normalized.Contains("/../", StringComparison.Ordinal))
        {
            return null;
        }
        return path;
    }

    private static bool ContainsUnsafeDisplayCharacter(string value)
    {
        foreach (char character in value)
        {
            if (char.IsControl(character)
                || character is '\u200E' or '\u200F'
                || character is >= '\u202A' and <= '\u202E'
                || character is >= '\u2066' and <= '\u2069')
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>Central translation keys for every piece of viewer chrome.</summary>
internal static class ModHealthViewerTranslationKeys
{
    public const string Title = "health-view.title";
    public const string Privacy = "health-view.privacy";
    public const string Preparing = "health-view.state.preparing";
    public const string Ready = "health-view.state.ready";
    public const string Saved = "health-view.state.saved";
    public const string WriteFailed = "health-view.state.write-failed";
    public const string FailedBeforeModel = "health-view.state.failed-before-model";
    public const string Retrying = "health-view.state.retrying";
    public const string Superseded = "health-view.state.superseded";
    public const string Canceled = "health-view.state.canceled";
    public const string Disposed = "health-view.state.disposed";
    public const string Rejected = "health-view.state.rejected";
    public const string Absent = "health-view.state.absent";
    public const string NotSaved = "health-view.not-saved";
    public const string Details = "health-view.details";
    public const string Request = "health-view.request";
    public const string TextArtifact = "health-view.artifact.text";
    public const string JsonArtifact = "health-view.artifact.json";
    public const string NewerRequest = "health-view.newer-request";
    public const string CloseGlyph = "health-view.close-glyph";
    public const string Opened = "health-view.notice.opened";
    public const string AlreadyOpen = "health-view.notice.already-open";
    public const string UnsafeState = "health-view.notice.unsafe-state";
    public const string MenuBusy = "health-view.notice.menu-busy";
    public const string OperationAccepted = "health-view.notice.operation-accepted";
    public const string OperationRefused = "health-view.notice.operation-refused";

    public static string Section(ModHealthViewerSection section) => $"health-view.section.{section.ToString().ToLowerInvariant()}";

    public static string Action(ModHealthViewerActionKind action) => action switch
    {
        ModHealthViewerActionKind.StartCapture => "health-view.action.start",
        ModHealthViewerActionKind.AddMark => "health-view.action.mark",
        ModHealthViewerActionKind.RefreshAndSaveSnapshot => "health-view.action.refresh-save",
        ModHealthViewerActionKind.StopCapture => "health-view.action.stop",
        ModHealthViewerActionKind.RetrySave => "health-view.action.retry",
        ModHealthViewerActionKind.ViewNewer => "health-view.action.view-newer",
        ModHealthViewerActionKind.Close => "health-view.action.close",
        _ => "health-view.action.unavailable"
    };

    public static string State(ModHealthPreparedReportState state) => state switch
    {
        ModHealthPreparedReportState.Preparing => Preparing,
        ModHealthPreparedReportState.ReadyBeforeWrite => Ready,
        ModHealthPreparedReportState.Saved => Saved,
        ModHealthPreparedReportState.WriteFailed => WriteFailed,
        ModHealthPreparedReportState.FailedBeforeModel => FailedBeforeModel,
        ModHealthPreparedReportState.Retrying => Retrying,
        ModHealthPreparedReportState.Superseded => Superseded,
        ModHealthPreparedReportState.Canceled => Canceled,
        ModHealthPreparedReportState.Disposed => Disposed,
        ModHealthPreparedReportState.Rejected => Rejected,
        _ => Absent
    };
}
