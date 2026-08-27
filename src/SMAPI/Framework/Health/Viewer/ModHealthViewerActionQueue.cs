using System;

namespace StardewModdingAPI.Framework.Health.Viewer;

/// <summary>A typed game-thread action requested by the health viewer or its console command.</summary>
internal enum ModHealthViewerActionKind
{
    Open,
    StartCapture,
    AddMark,
    RefreshAndSaveSnapshot,
    StopCapture,
    RetrySave,
    ResetConfirmed,
    ViewNewer,
    Close
}

/// <summary>The result of adding an action to the bounded queue.</summary>
internal enum ModHealthViewerActionDisposition
{
    Queued,
    Coalesced,
    RejectedFull
}

/// <summary>One screen-local viewer action tied to an exact viewer instance and report request where applicable.</summary>
/// <remarks>The controller creates the viewer instance ID before it queues <see cref="ModHealthViewerActionKind.Open"/>, so every action has exact ownership even before the menu exists.</remarks>
internal readonly record struct ModHealthViewerAction(ModHealthViewerActionKind Kind, Guid ViewerInstanceId, Guid? ExpectedRequestId = null);

/// <summary>A small bounded FIFO for actions drained at the next safe game-thread update boundary.</summary>
internal sealed class ModHealthViewerActionQueue
{
    internal const int Capacity = 8;

    private readonly ModHealthViewerAction[] Actions = new ModHealthViewerAction[Capacity];
    private int ReadIndex;
    private int Count;

    /// <summary>Whether an action is waiting.</summary>
    public bool HasPendingActions => this.Count > 0;

    /// <summary>The number of queued actions.</summary>
    public int PendingCount => this.Count;

    /// <summary>Queue an action, coalescing only idempotent duplicates and giving close priority within the same viewer instance.</summary>
    public ModHealthViewerActionDisposition Enqueue(ModHealthViewerAction action)
    {
        if (action.Kind == ModHealthViewerActionKind.Close)
        {
            if (this.Contains(action))
                return ModHealthViewerActionDisposition.Coalesced;
            this.RemoveForViewer(action.ViewerInstanceId);
        }
        else if (IsIdempotent(action.Kind) && this.Contains(action))
            return ModHealthViewerActionDisposition.Coalesced;

        if (this.Count >= Capacity)
            return ModHealthViewerActionDisposition.RejectedFull;
        int writeIndex = (this.ReadIndex + this.Count) % Capacity;
        this.Actions[writeIndex] = action;
        this.Count++;
        return ModHealthViewerActionDisposition.Queued;
    }

    private bool Contains(ModHealthViewerAction action)
    {
        for (int offset = 0; offset < this.Count; offset++)
        {
            int index = (this.ReadIndex + offset) % Capacity;
            if (this.Actions[index] == action)
                return true;
        }
        return false;
    }

    /// <summary>Take the next action.</summary>
    public bool TryDequeue(out ModHealthViewerAction action)
    {
        if (this.Count == 0)
        {
            action = default;
            return false;
        }

        action = this.Actions[this.ReadIndex];
        this.Actions[this.ReadIndex] = default;
        this.ReadIndex = (this.ReadIndex + 1) % Capacity;
        this.Count--;
        if (this.Count == 0)
            this.ReadIndex = 0;
        return true;
    }

    /// <summary>Discard all pending actions.</summary>
    public void Clear()
    {
        Array.Clear(this.Actions);
        this.ReadIndex = 0;
        this.Count = 0;
    }

    private void RemoveForViewer(Guid viewerInstanceId)
    {
        int originalCount = this.Count;
        int retainedCount = 0;
        for (int offset = 0; offset < originalCount; offset++)
        {
            int read = (this.ReadIndex + offset) % Capacity;
            ModHealthViewerAction action = this.Actions[read];
            if (action.ViewerInstanceId == viewerInstanceId)
                continue;
            int write = (this.ReadIndex + retainedCount) % Capacity;
            this.Actions[write] = action;
            retainedCount++;
        }
        for (int offset = retainedCount; offset < originalCount; offset++)
            this.Actions[(this.ReadIndex + offset) % Capacity] = default;
        this.Count = retainedCount;
        if (this.Count == 0)
            this.ReadIndex = 0;
    }

    private static bool IsIdempotent(ModHealthViewerActionKind kind)
    {
        return kind is ModHealthViewerActionKind.Open
            or ModHealthViewerActionKind.RefreshAndSaveSnapshot
            or ModHealthViewerActionKind.RetrySave
            or ModHealthViewerActionKind.ViewNewer
            or ModHealthViewerActionKind.Close;
    }
}
