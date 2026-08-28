using System;
using SMAPI.PerformanceBenchmarks.Framework;
using StardewModdingAPI.Events;
using StardewModdingAPI.Framework.Events;

namespace SMAPI.PerformanceBenchmarks;

/// <summary>Measure the cached callback objects used by managed-event dispatch.</summary>
internal sealed class EventDispatchScenario : IPerformanceScenario
{
    private ManagedEventHandler<EventArgs>[] Handlers = Array.Empty<ManagedEventHandler<EventArgs>>();
    private int Invocations;

    /// <inheritdoc />
    public string Id => "event.cached-dispatch";

    /// <inheritdoc />
    public string Description => "Invokes eight warmed managed-event callback wrappers without recreating delegates.";

    /// <inheritdoc />
    public void Setup()
    {
        EventHandler<EventArgs> handler = this.Handle;
        this.Handlers = new ManagedEventHandler<EventArgs>[8];
        for (int index = 0; index < this.Handlers.Length; index++)
            this.Handlers[index] = new ManagedEventHandler<EventArgs>(handler, index, EventPriority.Normal, sourceMod: null!);
    }

    /// <inheritdoc />
    public ulong Execute(int operations)
    {
        this.Invocations = 0;
        for (int operation = 0; operation < operations; operation++)
        {
            foreach (ManagedEventHandler<EventArgs> handler in this.Handlers)
                handler.Callback(EventArgs.Empty);
        }

        ulong digest = ScenarioDigest.Add(ScenarioDigest.Offset, (ulong)this.Invocations);
        digest = ScenarioDigest.Add(digest, (ulong)this.Handlers.Length);
        return digest;
    }

    /// <inheritdoc />
    public void Cleanup()
    {
        this.Handlers = Array.Empty<ManagedEventHandler<EventArgs>>();
        this.Invocations = 0;
    }

    private void Handle(object? sender, EventArgs args)
    {
        if (sender is not null || !ReferenceEquals(args, EventArgs.Empty))
            throw new InvalidOperationException("Unexpected synthetic event arguments.");
        this.Invocations++;
    }
}
