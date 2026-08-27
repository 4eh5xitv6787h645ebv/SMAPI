using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.Health.Viewer.Game;

namespace SMAPI.Tests.Framework.Health.Viewer.Game;

[TestFixture]
internal sealed class ModHealthViewerHostPolicyTests
{
    [Test]
    public void CanOpen_AllowsOnlyUnownedRoot()
    {
        ModHealthViewerHostPolicy.CanOpen(new(false, ModHealthViewerRootMenuKind.None), out string refusal).Should().BeTrue();
        refusal.Should().BeEmpty();
    }

    [TestCase(ModHealthViewerRootMenuKind.Other)]
    public void CanOpen_NeverReplacesAnotherMenu(ModHealthViewerRootMenuKind menuKind)
    {
        ModHealthViewerHostPolicy.CanOpen(new(false, menuKind), out string refusal).Should().BeFalse();
        refusal.Should().Be(ModHealthViewerTranslationKeys.MenuBusy);
    }

    [TestCase(ModHealthViewerRootMenuKind.None)]
    [TestCase(ModHealthViewerRootMenuKind.Other)]
    public void CanOpen_RefusesSavingLoadingMinigameOrOtherUnsafeTransition(ModHealthViewerRootMenuKind menuKind)
    {
        ModHealthViewerHostPolicy.CanOpen(new(true, menuKind), out string refusal).Should().BeFalse();
        refusal.Should().Be(ModHealthViewerTranslationKeys.UnsafeState);
    }

    [TestCase(true, false, false)]
    [TestCase(false, true, false)]
    [TestCase(false, false, true)]
    [TestCase(true, true, true)]
    public void CanOpen_RefusesLocationFadeAndWarpTransitions(bool location, bool fade, bool warp)
    {
        ModHealthViewerHostPolicy.CanOpen(new(false, ModHealthViewerRootMenuKind.None, location, fade, warp), out string refusal).Should().BeFalse();
        refusal.Should().Be(ModHealthViewerTranslationKeys.UnsafeState);
    }
}
