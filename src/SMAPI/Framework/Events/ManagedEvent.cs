using System;
using System.Collections.Generic;
using System.Reflection;
using StardewModdingAPI.Events;
using StardewModdingAPI.Framework.Extensions;
using StardewModdingAPI.Framework.Health;
using StardewModdingAPI.Framework.Performance;
using StardewModdingAPI.Internal;
using StardewValley;

namespace StardewModdingAPI.Framework.Events;

/// <summary>Invoke one handler for a stateful managed-event raise.</summary>
/// <typeparam name="TState">The per-raise state type.</typeparam>
/// <typeparam name="TEventArgs">The event arguments type.</typeparam>
/// <param name="state">The per-raise state, which may be updated between handlers.</param>
/// <param name="sourceMod">The mod which registered the handler.</param>
/// <param name="callback">Invoke the registered handler with its event arguments.</param>
internal delegate void ManagedEventInvoker<TState, TEventArgs>(ref TState state, IModMetadata sourceMod, Action<TEventArgs> callback);

/// <summary>An event wrapper which intercepts and logs errors in handler code.</summary>
/// <typeparam name="TEventArgs">The event arguments type.</typeparam>
internal class ManagedEvent<TEventArgs> : IManagedEvent
{
    /*********
    ** Fields
    *********/
    /// <summary>The mod registry with which to identify mods.</summary>
    protected readonly ModRegistry ModRegistry;

    /// <summary>Collects mod-owned handler timing and failure diagnostics.</summary>
    private readonly ModPerformanceManager? PerformanceManager;

    /// <summary>Collects privacy-safe structured callback failures.</summary>
    private readonly ModHealthRuntimeObserver? HealthObserver;

    /// <summary>The underlying event handlers.</summary>
    private readonly List<ManagedEventHandler<TEventArgs>> Handlers = [];

    /// <summary>A cached snapshot of the <see cref="Handlers"/> sorted by event priority, or <c>null</c> to rebuild it next raise.</summary>
    private ManagedEventHandler<TEventArgs>[]? CachedHandlers = [];

    /// <summary>The total number of event handlers registered for this events, regardless of whether they're still registered.</summary>
    private int RegistrationIndex;

    /// <summary>Whether handlers were removed since the last raise.</summary>
    private bool HasRemovedHandlers;

    /// <summary>Whether any of the handlers have a custom priority.</summary>
    private bool HasPriorities;


    /*********
    ** Accessors
    *********/
    /// <inheritdoc />
    public string EventName { get; }

    /// <inheritdoc />
    public bool HasListeners { get; private set; }


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="eventName">A human-readable name for the event.</param>
    /// <param name="modRegistry">The mod registry with which to identify mods.</param>
    /// <param name="performanceManager">Collects mod-owned handler performance diagnostics, if enabled.</param>
    /// <param name="healthObserver">Collects privacy-safe structured callback failures, if enabled.</param>
    public ManagedEvent(string eventName, ModRegistry modRegistry, ModPerformanceManager? performanceManager = null, ModHealthRuntimeObserver? healthObserver = null)
    {
        this.EventName = eventName;
        this.ModRegistry = modRegistry;
        this.PerformanceManager = performanceManager;
        this.HealthObserver = healthObserver;
    }

    /// <summary>Add an event handler.</summary>
    /// <param name="handler">The event handler.</param>
    /// <param name="mod">The mod which added the event handler.</param>
    public void Add(EventHandler<TEventArgs> handler, IModMetadata mod)
    {
        lock (this.Handlers)
        {
            EventPriority priority = handler.Method.GetCustomAttribute<EventPriorityAttribute>()?.Priority ?? EventPriority.Normal;
            var managedHandler = new ManagedEventHandler<TEventArgs>(handler, this.RegistrationIndex++, priority, mod);

            this.Handlers.Add(managedHandler);
            this.CachedHandlers = null;
            this.HasListeners = true;
            this.HasPriorities |= priority != EventPriority.Normal;
        }
    }

    /// <summary>Remove an event handler.</summary>
    /// <param name="handler">The event handler.</param>
    public void Remove(EventHandler<TEventArgs> handler)
    {
        lock (this.Handlers)
        {
            // match C# events: if a handler is listed multiple times, remove the last one added
            for (int i = this.Handlers.Count - 1; i >= 0; i--)
            {
                if (this.Handlers[i].Handler != handler)
                    continue;

                this.Handlers.RemoveAt(i);
                this.CachedHandlers = null;
                this.HasListeners = this.Handlers.Count != 0;
                this.HasRemovedHandlers = true;
                break;
            }
        }
    }

    /// <summary>Raise the event and notify all handlers.</summary>
    /// <param name="args">The event arguments to pass.</param>
    public void Raise(TEventArgs args)
    {
        if (!this.HasListeners)
            return;

        if (this.PerformanceManager?.IsTracking is true)
        {
            this.RaiseProfiled(args);
            return;
        }

        // raise event
        foreach (ManagedEventHandler<TEventArgs> handler in this.GetHandlers())
        {
            Context.HeuristicModsRunningCode.Push(handler.SourceMod);

            try
            {
                handler.Handler(null, args);
            }
            catch (Exception ex)
            {
                this.LogError(handler, ex);
            }
            finally
            {
                Context.HeuristicModsRunningCode.TryPop(out _);
            }
        }
    }

    /// <summary>Raise the event and notify all handlers.</summary>
    /// <typeparam name="TState">The per-raise state type.</typeparam>
    /// <param name="state">The per-raise state, which may be updated between handlers.</param>
    /// <param name="invoke">Invoke an event handler. This receives the state, mod which registered the handler, and callback to invoke with the event arguments.</param>
    public void Raise<TState>(ref TState state, ManagedEventInvoker<TState, TEventArgs> invoke)
    {
        if (!this.HasListeners)
            return;

        if (this.PerformanceManager?.IsTracking is true)
        {
            this.RaiseProfiled(ref state, invoke);
            return;
        }

        // raise event
        foreach (ManagedEventHandler<TEventArgs> handler in this.GetHandlers())
        {
            Context.HeuristicModsRunningCode.Push(handler.SourceMod);

            try
            {
                invoke(ref state, handler.SourceMod, handler.Callback);
            }
            catch (Exception ex)
            {
                this.LogError(handler, ex);
            }
            finally
            {
                Context.HeuristicModsRunningCode.TryPop(out _);
            }
        }
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Raise the event with per-handler performance attribution.</summary>
    /// <param name="args">The event arguments to pass.</param>
    private void RaiseProfiled(TEventArgs args)
    {
        foreach (ManagedEventHandler<TEventArgs> handler in this.GetHandlers())
        {
            Context.HeuristicModsRunningCode.Push(handler.SourceMod);
            HandlerTimingToken timing = this.BeginPerformance(handler);
            bool failed = false;

            try
            {
                handler.Handler(null, args);
            }
            catch (Exception ex)
            {
                failed = true;
                this.LogError(handler, ex);
            }
            finally
            {
                this.PerformanceManager!.EndHandler(timing, failed);
                Context.HeuristicModsRunningCode.TryPop(out _);
            }
        }
    }

    /// <summary>Raise a stateful event with per-handler performance attribution.</summary>
    /// <typeparam name="TState">The per-raise state type.</typeparam>
    /// <param name="state">The per-raise state, which may be updated between handlers.</param>
    /// <param name="invoke">Invoke an event handler.</param>
    private void RaiseProfiled<TState>(ref TState state, ManagedEventInvoker<TState, TEventArgs> invoke)
    {
        foreach (ManagedEventHandler<TEventArgs> handler in this.GetHandlers())
        {
            Context.HeuristicModsRunningCode.Push(handler.SourceMod);
            HandlerTimingToken timing = this.BeginPerformance(handler);
            bool failed = false;

            try
            {
                invoke(ref state, handler.SourceMod, handler.Callback);
            }
            catch (Exception ex)
            {
                failed = true;
                this.LogError(handler, ex);
            }
            finally
            {
                this.PerformanceManager!.EndHandler(timing, failed);
                Context.HeuristicModsRunningCode.TryPop(out _);
            }
        }
    }

    /// <summary>Begin one profiled handler invocation.</summary>
    /// <param name="handler">The invoked handler.</param>
    /// <returns>The timing token to complete after invocation.</returns>
    private HandlerTimingToken BeginPerformance(ManagedEventHandler<TEventArgs> handler)
    {
        IModMetadata mod = handler.SourceMod;
        return this.PerformanceManager!.BeginHandler(
            mod,
            this.EventName,
            handler.HandlerName,
            this.GetExecutionPhase(),
            ModHealthOperationKind.Event,
            onBehalfOfModId: null
        );
    }

    /// <summary>Log an exception from an event handler.</summary>
    /// <param name="handler">The event handler instance.</param>
    /// <param name="ex">The exception that was raised.</param>
    private void LogError(ManagedEventHandler<TEventArgs> handler, Exception ex)
    {
        this.HealthObserver?.ObserveCallbackFailure(
            handler.SourceMod,
            this.GetExecutionPhase(),
            ModHealthOperationKind.Event,
            handler.HandlerName,
            ex
        );
        handler.SourceMod.LogAsMod($"This mod failed in the {this.EventName} event. Technical details: \n{ex.GetLogSummary()}", LogLevel.Error);
    }

    /// <summary>Classify this invocation without retaining event arguments or game state.</summary>
    private ModHealthExecutionPhase GetExecutionPhase()
    {
        if (this.EventName == "GameLoop.GameLaunched")
            return ModHealthExecutionPhase.Startup;
        if (this.EventName.StartsWith("Display.Rendering", StringComparison.Ordinal) || this.EventName.StartsWith("Display.Rendered", StringComparison.Ordinal))
            return ModHealthExecutionPhase.Draw;
        return Game1.IsOnMainThread()
            ? ModHealthExecutionPhase.Update
            : ModHealthExecutionPhase.Background;
    }

    /// <summary>Get cached copy of the sorted handlers to invoke.</summary>
    /// <remarks>This returns the handlers sorted by priority, and allows iterating the list even if a mod adds/removes handlers while handling it. This is debounced when requested to avoid repeatedly sorting when handlers are added/removed.</remarks>
    private ManagedEventHandler<TEventArgs>[] GetHandlers()
    {
        ManagedEventHandler<TEventArgs>[]? handlers = this.CachedHandlers;

        if (handlers == null)
        {
            lock (this.Handlers)
            {
                // recheck priorities
                if (this.HasRemovedHandlers)
                {
                    this.HasPriorities = false;
                    foreach (var handler in this.Handlers)
                    {
                        if (handler.Priority != EventPriority.Normal)
                        {
                            this.HasPriorities = true;
                            break;
                        }
                    }
                }

                // sort by priority if needed
                if (this.HasPriorities)
                    this.Handlers.Sort();

                // update cache
                this.CachedHandlers = handlers = this.Handlers.ToArray();
                this.HasRemovedHandlers = false;
            }
        }

        return handlers;
    }
}
