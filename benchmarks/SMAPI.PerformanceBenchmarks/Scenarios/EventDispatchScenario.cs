using System;
using SMAPI.PerformanceBenchmarks.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Framework;
using StardewModdingAPI.Framework.Events;

namespace SMAPI.PerformanceBenchmarks;

/// <summary>Measure the cached callback objects used by managed-event dispatch.</summary>
internal sealed class EventDispatchScenario : IPerformanceScenario
{
    private static readonly ManagedEventInvoker<int, EventArgs> InvokeHandler = static (ref int invocations, IModMetadata _, Action<EventArgs> callback) =>
    {
        invocations++;
        callback(EventArgs.Empty);
    };

    private ManagedEvent<EventArgs>? Event;
    private int Invocations;

    /// <inheritdoc />
    public string Id => "event.cached-dispatch";

    /// <inheritdoc />
    public string Description => "Raises a warmed production managed event with eight cached handlers.";

    /// <inheritdoc />
    public void Setup()
    {
        EventHandler<EventArgs> handler = this.Handle;
        this.Event = new ManagedEvent<EventArgs>("Synthetic.PerformanceGate", new ModRegistry());
        for (int index = 0; index < 8; index++)
            this.Event.Add(handler, mod: null!);

        int warmupInvocations = 0;
        this.Event.Raise(ref warmupInvocations, EventDispatchScenario.InvokeHandler);
    }

    /// <inheritdoc />
    public ulong Execute(int operations)
    {
        this.Invocations = 0;
        for (int operation = 0; operation < operations; operation++)
            this.Event!.Raise(ref this.Invocations, EventDispatchScenario.InvokeHandler);

        if (Context.HeuristicModsRunningCode.Count != 0)
            throw new InvalidOperationException("Managed-event context was not cleared after dispatch.");

        ulong digest = ScenarioDigest.Add(ScenarioDigest.Offset, (ulong)this.Invocations);
        digest = ScenarioDigest.Add(digest, this.Event!.HasListeners ? 1UL : 0UL);
        return digest;
    }

    /// <inheritdoc />
    public void Cleanup()
    {
        this.Event = null;
        this.Invocations = 0;
    }

    private void Handle(object? sender, EventArgs args)
    {
        if (sender is not null || !ReferenceEquals(args, EventArgs.Empty))
            throw new InvalidOperationException("Unexpected synthetic event arguments.");
        this.Invocations++;
    }
}
