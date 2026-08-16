using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for <see cref="PerScreen{T}"/>.</summary>
[TestFixture]
[NonParallelizable]
internal class PerScreenTests
{
    [Test(Description = "Assert that the most recently accessed screen state remains stable and reset clears it.")]
    public void GetValueForScreen_CachesAndResetsState()
    {
        int createdStates = 0;
        PerScreen<object> values = new(() => new State(++createdStates));

        object first = values.GetValueForScreen(42);
        values.GetValueForScreen(42).Should().BeSameAs(first);
        createdStates.Should().Be(1);

        values.ResetAllScreens();
        values.GetValueForScreen(42).Should().NotBeSameAs(first);
        createdStates.Should().Be(2);
    }

    [Test(Description = "Assert that removing a split-screen invalidates its cached state.")]
    public void GetValueForScreen_RemovesDeadCachedState()
    {
        int previousLastRemovedScreenId = Context.LastRemovedScreenId;
        int[] previousActiveScreenIds = Context.ActiveScreenIds.ToArray();
        try
        {
            Context.ActiveScreenIds.Clear();
            Context.ActiveScreenIds.Add(42);
            Context.LastRemovedScreenId = 100_000;

            int createdStates = 0;
            PerScreen<object> values = new(() => new State(++createdStates));
            object first = values.GetValueForScreen(42);

            Context.ActiveScreenIds.Remove(42);
            Context.LastRemovedScreenId = 42;

            values.GetValueForScreen(42).Should().NotBeSameAs(first);
            createdStates.Should().Be(2);
        }
        finally
        {
            Context.ActiveScreenIds.Clear();
            foreach (int screenId in previousActiveScreenIds)
                Context.ActiveScreenIds.Add(screenId);
            Context.LastRemovedScreenId = previousLastRemovedScreenId;
        }
    }

    /// <summary>A distinguishable screen state.</summary>
    /// <param name="Id">The creation sequence.</param>
    private sealed record State(int Id);
}
