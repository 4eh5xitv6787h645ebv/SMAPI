using System;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Framework;
using StardewModdingAPI.Framework.Events;
using StardewModdingAPI.Framework.Health;
using StardewModdingAPI.Framework.Performance;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for <see cref="ManagedEvent{TEventArgs}"/>.</summary>
[TestFixture]
internal class ManagedEventTests
{
    [Test(Description = "Assert that lazy event dispatch reuses each handler's callback wrapper.")]
    public void Raise_Lazy_ReusesCallback()
    {
        // arrange
        ManagedEvent<EventArgs> managedEvent = new("test event", new ModRegistry());
        Mock<IModMetadata> mod = new(MockBehavior.Strict);
        int invocationCount = 0;
        managedEvent.Add((sender, args) =>
        {
            sender.Should().BeNull();
            args.Should().BeSameAs(EventArgs.Empty);
            invocationCount++;
        }, mod.Object);

        Action<EventArgs>? firstCallback = null;
        Action<EventArgs>? secondCallback = null;

        // act
        var firstState = (Mod: mod.Object, Callback: firstCallback);
        managedEvent.Raise(ref firstState, static (ref (IModMetadata Mod, Action<EventArgs>? Callback) state, IModMetadata sourceMod, Action<EventArgs> callback) =>
        {
            sourceMod.Should().BeSameAs(state.Mod);
            state.Callback = callback;
            callback(EventArgs.Empty);
        });
        firstCallback = firstState.Callback;

        var secondState = (Mod: mod.Object, Callback: secondCallback);
        managedEvent.Raise(ref secondState, static (ref (IModMetadata Mod, Action<EventArgs>? Callback) state, IModMetadata sourceMod, Action<EventArgs> callback) =>
        {
            sourceMod.Should().BeSameAs(state.Mod);
            state.Callback = callback;
            callback(EventArgs.Empty);
        });
        secondCallback = secondState.Callback;

        // assert
        invocationCount.Should().Be(2);
        firstCallback.Should().BeSameAs(secondCallback);
        Context.HeuristicModsRunningCode.Should().BeEmpty();
    }

    [Test(Description = "Assert that warmed stateful event dispatch doesn't allocate per raise or handler.")]
    [Category("PerformanceRegression")]
    [NonParallelizable]
    public void Raise_Lazy_WarmedDispatchDoesNotAllocate()
    {
        ManagedEvent<EventArgs> managedEvent = new("test event", new ModRegistry());
        Mock<IModMetadata> mod = new(MockBehavior.Strict);
        EventHandler<EventArgs> handler = static (_, _) => { };
        const int handlerCount = 8;
        for (int i = 0; i < handlerCount; i++)
            managedEvent.Add(handler, mod.Object);
        ManagedEventInvoker<int, EventArgs> invoke = static (ref int count, IModMetadata _, Action<EventArgs> callback) =>
        {
            count++;
            callback(EventArgs.Empty);
        };

        int warmupInvocations = 0;
        for (int i = 0; i < 10_000; i++)
            managedEvent.Raise(ref warmupInvocations, invoke);

        const int iterations = 10_000;
        int invocations = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
            managedEvent.Raise(ref invocations, invoke);
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        invocations.Should().Be(iterations * handlerCount);
        Context.HeuristicModsRunningCode.Should().BeEmpty();
        allocatedBytes.Should().Be(0, "handlers and the stateful invoker should reuse their cached delegates");
    }

    [Test]
    public void Raise_Profiled_AttributesExclusiveTimeAndFailureToMod()
    {
        long timestamp = 0;
        ModPerformanceManager performance = new(timestampFrequency: 1000, getTimestamp: () => timestamp);
        performance.Start();

        Mock<IManifest> manifest = new();
        manifest.SetupGet(instance => instance.UniqueID).Returns("Example.Mod");
        Mock<IMonitor> monitor = new();
        Mock<IModMetadata> mod = new();
        mod.Setup(instance => instance.HasManifest()).Returns(true);
        mod.SetupGet(instance => instance.Manifest).Returns(manifest.Object);
        mod.SetupGet(instance => instance.DisplayName).Returns("Example Mod");
        mod.SetupGet(instance => instance.Monitor).Returns(monitor.Object);
        monitor.Setup(instance => instance.Log(It.IsAny<string>(), LogLevel.Error)).Callback(() => timestamp += 6);

        ManagedEvent<EventArgs> managedEvent = new("GameLoop.GameLaunched", new ModRegistry(), performance);
        managedEvent.Add((_, _) =>
        {
            timestamp += 4;
            throw new InvalidOperationException("test failure");
        }, mod.Object);

        managedEvent.Raise(EventArgs.Empty);

        ModPerformanceSnapshot snapshot = performance.GetSnapshot();
        snapshot.Handlers.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new
            {
                ModId = "Example.Mod",
                ModName = "Example Mod",
                EventName = "GameLoop.GameLaunched",
                CallCount = 1,
                // exclude the six milliseconds spent writing SMAPI's failure report after the callback threw
                TotalMilliseconds = 4d,
                MaximumMilliseconds = 4d,
                FailureCount = 1
            },
            options => options.ExcludingMissingMembers()
        );
        ModHealthCallbackPerformanceSnapshot healthCallback = snapshot.Health!.Callbacks.Should().ContainSingle().Subject;
        healthCallback.Phase.Should().Be(ModHealthExecutionPhase.Startup);
        healthCallback.Operation.Should().Be(ModHealthOperationKind.Event);
        monitor.Verify(instance => instance.Log(It.Is<string>(message => message.Contains("test failure")), LogLevel.Error), Times.Once);
        Context.HeuristicModsRunningCode.Should().BeEmpty();
    }

    [TestCase("Display.Rendering", ModHealthExecutionPhase.Draw)]
    [TestCase("GameLoop.GameLaunched", ModHealthExecutionPhase.Startup)]
    public void Raise_Profiled_NestedEventsInheritAmbientExecutionPhase(string outerEventName, ModHealthExecutionPhase expectedPhase)
    {
        ModPerformanceManager performance = new(timestampFrequency: 1000, getTimestamp: static () => 0, getGcCollectionCount: _ => 0);
        performance.Start();

        Mock<IManifest> manifest = new();
        manifest.SetupGet(instance => instance.UniqueID).Returns("Example.Mod");
        Mock<IModMetadata> mod = new();
        mod.Setup(instance => instance.HasManifest()).Returns(true);
        mod.SetupGet(instance => instance.Manifest).Returns(manifest.Object);
        mod.SetupGet(instance => instance.DisplayName).Returns("Example Mod");

        ManagedEvent<EventArgs> inner = new("Content.AssetRequested", new ModRegistry(), performance);
        inner.Add((_, _) => { }, mod.Object);
        ManagedEvent<EventArgs> outer = new(outerEventName, new ModRegistry(), performance);
        outer.Add((_, _) => inner.Raise(EventArgs.Empty), mod.Object);

        outer.Raise(EventArgs.Empty);

        performance.GetSnapshot().Health!.Callbacks
            .Single(callback => callback.EventName == "Content.AssetRequested")
            .Phase.Should().Be(expectedPhase);
        Context.HeuristicModsRunningCode.Should().BeEmpty();
    }

    [Test]
    public void Raise_Failure_RecordsStructuredHealthEvidenceWithoutExceptionMessage()
    {
        ModHealthLedger ledger = new();
        ModHealthRuntimeObserver observer = new(ledger);
        Mock<IManifest> manifest = new();
        manifest.SetupGet(instance => instance.UniqueID).Returns("Example.Mod");
        manifest.SetupGet(instance => instance.Name).Returns("Example Mod");
        Mock<IMonitor> monitor = new();
        Mock<IModMetadata> mod = new();
        mod.Setup(instance => instance.HasId()).Returns(true);
        mod.Setup(instance => instance.HasManifest()).Returns(true);
        mod.SetupGet(instance => instance.Manifest).Returns(manifest.Object);
        mod.SetupGet(instance => instance.Monitor).Returns(monitor.Object);

        ManagedEvent<EventArgs> managedEvent = new("GameLoop.GameLaunched", new ModRegistry(), healthObserver: observer);
        managedEvent.Add((_, _) => throw new InvalidOperationException("private save and path"), mod.Object);

        managedEvent.Raise(EventArgs.Empty);

        ModHealthCallbackFailureSnapshot failure = ledger.GetSnapshot().CallbackFailures.Should().ContainSingle().Subject;
        failure.ModId.Should().Be("Example.Mod");
        failure.ModName.Should().Be("Example Mod");
        failure.Phase.Should().Be(ModHealthExecutionPhase.Startup);
        failure.Operation.Should().Be(ModHealthOperationKind.Event);
        failure.ExceptionType.Should().Be(typeof(InvalidOperationException).FullName);
        failure.CallbackIdentity.Should().NotContain("private");
        Context.HeuristicModsRunningCode.Should().BeEmpty();
    }
}
