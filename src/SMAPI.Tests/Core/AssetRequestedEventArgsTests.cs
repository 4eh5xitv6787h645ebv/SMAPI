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
    [Test(Description = "Assert that an asset request with no operations doesn't allocate empty operation lists.")]
    public void Constructor_NoOperationsAvoidsListAllocations()
    {
        AssetInfo info = new(null, AssetName.Parse("Data/Test", _ => null), typeof(object), static path => path);
        static IModMetadata? GetOnBehalfOf(IModMetadata source, string? id, string verb) => null;
        AssetRequestedEventArgs? last = null;
        for (int i = 0; i < 1_000; i++)
            last = new AssetRequestedEventArgs(info, GetOnBehalfOf);

        const int iterations = 10_000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
            last = new AssetRequestedEventArgs(info, GetOnBehalfOf);
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(last);

        last!.LoadOperations.Should().BeNull();
        last.EditOperations.Should().BeNull();
        (allocatedBytes / iterations).Should().BeLessThan(80, "only the event-args instance itself should be allocated");
    }

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
        args.EditOperations.Should().BeNull();
        AssetLoadOperation operation = args.LoadOperations![0];
        operation.Should().BeOfType<DelegateAssetLoadOperation>();
        operation.Mod.Should().BeSameAs(mod.Object);
        operation.OnBehalfOf.Should().BeNull();
        operation.Priority.Should().Be(AssetLoadPriority.High);
        operation.GetData().Should().BeSameAs(expected);
    }

    [Test(Description = "Assert that registering only an editor doesn't allocate an empty loader list.")]
    public void Edit_OnlyCreatesEditorList()
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
        Action<IAssetData> edit = _ => { };

        args.Edit(edit);

        args.LoadOperations.Should().BeNull();
        args.EditOperations.Should().ContainSingle();
        args.EditOperations![0].ApplyEdit.Should().BeSameAs(edit);
    }
}
