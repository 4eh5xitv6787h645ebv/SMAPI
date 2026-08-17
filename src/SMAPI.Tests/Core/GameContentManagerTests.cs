using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Framework.ContentManagers;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for <see cref="GameContentManager"/>.</summary>
[TestFixture]
internal class GameContentManagerTests
{
    [Test(Description = "Assert that concrete editor dispatch creates a working open delegate once per asset type.")]
    public void GetApplyEditorsDelegate_CachesWorkingOpenDelegate()
    {
        MethodInfo getDelegate = typeof(GameContentManager).GetMethod("GetApplyEditorsDelegate", BindingFlags.NonPublic | BindingFlags.Static)!;
        Type assetType = typeof(Dictionary<string, string>);

        Delegate first = (Delegate)getDelegate.Invoke(null, [assetType])!;
        Delegate second = (Delegate)getDelegate.Invoke(null, [assetType])!;

        first.Should().BeSameAs(second);

        GameContentManager manager = (GameContentManager)RuntimeHelpers.GetUninitializedObject(typeof(GameContentManager));
        GC.SuppressFinalize(manager);
        IAssetData asset = Mock.Of<IAssetData>();
        first.DynamicInvoke(manager, null, asset, null).Should().BeSameAs(asset);
    }
}
