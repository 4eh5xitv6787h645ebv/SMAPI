using FluentAssertions;
using StardewModdingAPI.Installer.Gui.Frontend;
using StardewModdingAPI.Installer.Gui.ViewModels;

namespace StardewModdingAPI.Installer.Gui.Tests;

public sealed class MainWindowViewModelTests
{
    [Test]
    public void StartsDisconnectedAndCannotExecute()
    {
        MainWindowViewModel viewModel = new(new DemoInstallerFrontendSession());

        viewModel.IsBackendConnected.Should().BeFalse();
        viewModel.ExecuteCommand.CanExecute(null).Should().BeFalse();
        viewModel.StateLabel.Should().Contain("Unchanged");
        viewModel.LogEntries.Should().Contain(p => p.Contains("backend is disconnected", StringComparison.Ordinal));
    }

    [Test]
    public void RejectsAProductionSessionInThisDemoOnlyBuild()
    {
        Action construct = () => _ = new MainWindowViewModel(new NonDemoSession());

        construct.Should().Throw<ArgumentException>()
            .WithMessage("*only accepts a safe demo session*");
    }

    [Test]
    public void PreviewUpdatesOnlySyntheticStateAndLog()
    {
        MainWindowViewModel viewModel = new(new DemoInstallerFrontendSession());

        viewModel.PreviewCommand.Execute(null);

        viewModel.PreviewHeading.Should().StartWith("Synthetic");
        viewModel.StateLabel.Should().Be("Unchanged — backend disconnected");
        viewModel.LogEntries.Should().Contain(p => p.StartsWith("SAFE", StringComparison.Ordinal));
    }

    [Test]
    public void ChangingASelectionClearsAStalePreview()
    {
        MainWindowViewModel viewModel = new(new DemoInstallerFrontendSession());
        viewModel.PreviewCommand.Execute(null);

        viewModel.SelectedRelease = viewModel.Releases[1];

        viewModel.PreviewHeading.Should().Be("Settings changed — preview again");
        viewModel.StateLabel.Should().Contain("Unchanged");
    }

    [TestCase()]
    [TestCase("--demo")]
    public void LaunchPolicyAllowsOnlyExplicitDemoCompatibleArguments(params string[] args)
    {
        DemoLaunchPolicy.TryValidate(args, out string? error).Should().BeTrue();
        error.Should().BeNull();
    }

    [TestCase("--install")]
    [TestCase("--demo", "/real/game")]
    public void LaunchPolicyRejectsArgumentsThatCouldSuggestProductionUse(params string[] args)
    {
        DemoLaunchPolicy.TryValidate(args, out string? error).Should().BeFalse();
        error.Should().Contain("safe demo mode");
    }

    private sealed class NonDemoSession : IInstallerFrontendSession
    {
        public bool IsDemoMode => false;

        public IReadOnlyList<FolderChoice> Folders => [];

        public IReadOnlyList<ReleaseChoice> Releases => [];

        public IReadOnlyList<OperationChoice> Operations => [];

        public FrontendPreview CreatePreview(FolderChoice folder, ReleaseChoice release, OperationChoice operation)
        {
            throw new InvalidOperationException("A non-demo session must never be used by this shell.");
        }
    }
}
