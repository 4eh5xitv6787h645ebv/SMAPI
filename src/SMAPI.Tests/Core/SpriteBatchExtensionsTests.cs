using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Xna.Framework.Graphics;
using NUnit.Framework;
using StardewModdingAPI.Framework.Extensions;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for <see cref="SpriteBatchExtensions"/>.</summary>
[TestFixture]
internal class SpriteBatchExtensionsTests
{
    [Test(Description = "Assert that the typed sprite-batch state accessor reads the private begin flag.")]
    public void IsOpen_ReadsPrivateState()
    {
        SpriteBatch spriteBatch = (SpriteBatch)RuntimeHelpers.GetUninitializedObject(typeof(SpriteBatch));
        FieldInfo beginCalled = typeof(SpriteBatch).GetField("_beginCalled", BindingFlags.Instance | BindingFlags.NonPublic)!;

        spriteBatch.IsOpen().Should().BeFalse();

        beginCalled.SetValue(spriteBatch, true);
        spriteBatch.IsOpen().Should().BeTrue();
    }
}
