using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.Content;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for <see cref="AssetOperationGroup"/>.</summary>
[TestFixture]
internal class AssetOperationGroupTests
{
    [Test(Description = "Assert that operation groups are allocation-free value containers which retain their operation lists.")]
    public void Constructor_CreatesValueContainerWithLists()
    {
        List<AssetLoadOperation> loaders = [];
        List<AssetEditOperation> editors = [];

        AssetOperationGroup group = new(loaders, editors);
        AssetOperationGroup? optional = group;

        typeof(AssetOperationGroup).IsValueType.Should().BeTrue();
        optional?.LoadOperations.Should().BeSameAs(loaders);
        optional?.EditOperations.Should().BeSameAs(editors);
    }
}
