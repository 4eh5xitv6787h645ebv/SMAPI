using System;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.Utilities;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for <see cref="ContextHash{T}"/>.</summary>
[TestFixture]
internal class ContextHashTests
{
    [Test(Description = "Assert that a configured comparer detects equivalent recursive context keys.")]
    public void Track_WithComparer_RejectsEquivalentNestedKey()
    {
        ContextHash<string> contexts = new(StringComparer.OrdinalIgnoreCase);

        FluentActions.Invoking(() => contexts.Track("Data/Asset", () => contexts.Track("data/asset", static () => { })))
            .Should().Throw<InvalidOperationException>();

        contexts.Should().BeEmpty();
    }

    [Test(Description = "Assert that stateful tracking passes state, returns the result, and removes the context afterward.")]
    public void Track_WithState_ReturnsResultAndCleansUp()
    {
        ContextHash<string> contexts = [];

        int result = contexts.Track(
            "asset",
            (Contexts: contexts, Value: 41),
            static state =>
            {
                state.Contexts.Should().Contain("asset");
                return state.Value + 1;
            }
        );

        result.Should().Be(42);
        contexts.Should().BeEmpty();
    }

    [Test(Description = "Assert that stateful tracking removes the context when the callback throws.")]
    public void Track_WithState_CleansUpAfterError()
    {
        ContextHash<string> contexts = [];

        FluentActions.Invoking(() => contexts.Track<int, int>(
            "asset",
            0,
            static _ => throw new InvalidOperationException("test")
        )).Should().Throw<InvalidOperationException>();

        contexts.Should().BeEmpty();
    }
}
