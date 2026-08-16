using System;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Framework;
using StardewModdingAPI.Framework.Events;

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
}
