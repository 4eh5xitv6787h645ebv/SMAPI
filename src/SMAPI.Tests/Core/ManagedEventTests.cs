using System;
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
