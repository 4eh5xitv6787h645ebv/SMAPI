using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Framework.Content;
using StardewValley;

namespace SMAPI.Tests.Core;

/// <summary>Unit tests for <see cref="AssetReadyEventArgs"/>.</summary>
[TestFixture]
internal class AssetReadyEventArgsTests
{
    [Test(Description = "Assert that the locale-stripped name is created lazily and reused.")]
    public void NameWithoutLocale_IsCached()
    {
        IAssetName localized = new AssetName("Data/Test", "fr-FR", LocalizedContentManager.LanguageCode.fr);
        AssetReadyEventArgs args = new(localized);

        IAssetName first = args.NameWithoutLocale;

        first.Name.Should().Be("Data/Test");
        first.LocaleCode.Should().BeNull();
        args.NameWithoutLocale.Should().BeSameAs(first);
    }
}
