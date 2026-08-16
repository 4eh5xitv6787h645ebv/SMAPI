using System;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Framework;
using StardewModdingAPI.Framework.Content;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for <see cref="AssetRequestedEventArgs"/>.</summary>
[TestFixture]
internal class AssetRequestedEventArgsTests
{
    [Test(Description = "Assert that a delegate-backed asset loader retains its metadata and invokes the original callback.")]
    public void LoadFrom_StoresAndInvokesDelegate()
    {
        Mock<IModMetadata> mod = new(MockBehavior.Strict);
        AssetInfo info = new(
            locale: null,
            assetName: AssetName.Parse("Data/Test", _ => null),
            type: typeof(object),
            getNormalizedPath: static path => path
        );
        AssetRequestedEventArgs args = new(info, (source, id, verb) => null);
        args.SetMod(mod.Object);
        object expected = new();
        Func<object> load = () => expected;

        args.LoadFrom(load, AssetLoadPriority.High);

        args.LoadOperations.Should().ContainSingle();
        AssetLoadOperation operation = args.LoadOperations[0];
        operation.Should().BeOfType<DelegateAssetLoadOperation>();
        operation.Mod.Should().BeSameAs(mod.Object);
        operation.OnBehalfOf.Should().BeNull();
        operation.Priority.Should().Be(AssetLoadPriority.High);
        operation.GetData().Should().BeSameAs(expected);
    }
}
