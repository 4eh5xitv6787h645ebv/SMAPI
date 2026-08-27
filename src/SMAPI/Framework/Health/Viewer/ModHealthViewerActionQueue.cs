using System;

namespace StardewModdingAPI.Framework.Health.Viewer;

/// <summary>A typed game-thread action requested by the health viewer or its console command.</summary>
internal enum ModHealthViewerActionKind
{
    Open,
    StartCapture,
    AddMark,
    SaveSnapshot,
    StopCapture,
    RetrySave,
    ResetConfirmed,
    ViewNewer,
    Close
}

/// <summary>One screen-local viewer action tied to an exact menu ownership token and report request where applicable.</summary>
internal readonly record struct ModHealthViewerAction(ModHealthViewerActionKind Kind, Guid OwnershipToken, Guid? ExpectedRequestId = null);

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

    /// <summary>Queue an action, coalescing exact duplicates and giving close priority.</summary>
    public bool TryEnqueue(ModHealthViewerAction action)
    {
        if (action.Kind == ModHealthViewerActionKind.Close)
        {
            this.Clear();
            this.Actions[0] = action;
            this.ReadIndex = 0;
            this.Count = 1;
            return true;
        }

        for (int offset = 0; offset < this.Count; offset++)
        {
            int index = (this.ReadIndex + offset) % Capacity;
            if (this.Actions[index] == action)
                return true;
        }

        if (this.Count >= Capacity)
            return false;
        int writeIndex = (this.ReadIndex + this.Count) % Capacity;
        this.Actions[writeIndex] = action;
        this.Count++;
        return true;
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
}
