using FluentAssertions;

namespace StardewModdingAPI.Installer.Gui.Tests;

public sealed class ProgramTests
{
    [Test]
    public void RootRefusalPrecedesDesktopInitializationAndArgumentHandling()
    {
        bool started = false;
        using StringWriter diagnostics = new();

        int exit = Program.Run(["--invalid"], true, 0, _ => { started = true; return 0; }, diagnostics);

        exit.Should().Be(2);
        started.Should().BeFalse();
        diagnostics.ToString().Should().Be("The SMAPI graphical installer must not be run as root or with sudo. Run it as your normal desktop user instead." + Environment.NewLine);
    }

    [TestCase(false, null)]
    [TestCase(true, 1000u)]
    public void NormalNoArgumentLaunchSelectsProduction(bool isLinux, uint? effectiveUserId)
    {
        GuiLaunchMode? started = null;
        using StringWriter diagnostics = new();

        int exit = Program.Run([], isLinux, effectiveUserId, mode => { started = mode; return 17; }, diagnostics);

        exit.Should().Be(17);
        started.Should().Be(GuiLaunchMode.Production);
        diagnostics.ToString().Should().BeEmpty();
    }

    [Test]
    public void ExactDemoLaunchPreservesDemoMode()
    {
        GuiLaunchMode? started = null;
        using StringWriter diagnostics = new();

        int exit = Program.Run(["--demo"], true, 1000, mode => { started = mode; return 0; }, diagnostics);

        exit.Should().Be(0);
        started.Should().Be(GuiLaunchMode.Demo);
        diagnostics.ToString().Should().BeEmpty();
    }

    [Test]
    public void ProductionCompositionStartsOnlyProductionMode()
    {
        GuiLaunchMode? started = null;
        using StringWriter diagnostics = new();

        int exit = Program.StartSelectedMode(GuiLaunchMode.Production, mode => { started = mode; return 19; }, diagnostics);

        exit.Should().Be(19);
        started.Should().Be(GuiLaunchMode.Production);
        diagnostics.ToString().Should().BeEmpty();
    }

    [Test]
    public void DemoCompositionRemainsAvailableOnlyForDemoMode()
    {
        using StringWriter diagnostics = new();

        GuiLaunchMode? started = null;
        int exit = Program.StartSelectedMode(GuiLaunchMode.Demo, mode => { started = mode; return 23; }, diagnostics);

        exit.Should().Be(23);
        started.Should().Be(GuiLaunchMode.Demo);
        diagnostics.ToString().Should().BeEmpty();
    }

    [Test]
    public void InvalidCompositionModeFailsBeforeDesktopInitialization()
    {
        bool started = false;
        using StringWriter diagnostics = new();

        int exit = Program.StartSelectedMode((GuiLaunchMode)999, _ => { started = true; return 0; }, diagnostics);

        exit.Should().Be(2);
        started.Should().BeFalse();
        diagnostics.ToString().Should().Contain("launch mode is invalid");
    }
}
