using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using FluentAssertions;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Gui.Frontend;
using StardewModdingAPI.Installer.Gui.ViewModels;

namespace StardewModdingAPI.Installer.Gui.Tests;

internal sealed class ThemeAccessibilityTests
{
    [Test]
    public void ThemePolicyFollowsThePlatformUnlessTheExactHighContrastOverrideIsSet()
    {
        App.ResolveRequestedThemeVariant(null).Should().BeSameAs(ThemeVariant.Default);
        App.ResolveRequestedThemeVariant("").Should().BeSameAs(ThemeVariant.Default);
        App.ResolveRequestedThemeVariant("0").Should().BeSameAs(ThemeVariant.Default);
        App.ResolveRequestedThemeVariant("true").Should().BeSameAs(ThemeVariant.Default);
        App.ResolveRequestedThemeVariant(" 1 ").Should().BeSameAs(ThemeVariant.Default);
        App.ResolveRequestedThemeVariant("1").Should().BeSameAs(App.HighContrastTheme);
    }

    [AvaloniaTest]
    public async Task LightDarkAndHighContrastKeepFocusErrorsAndManualHelpReadable()
    {
        ThemeVariant[] themes = [ThemeVariant.Light, ThemeVariant.Dark, App.HighContrastTheme];

        foreach (ThemeVariant theme in themes)
        {
            ReviewedReleaseCandidate candidate = ReleaseVerificationViewModelTests.Candidate();
            ReleaseVerificationViewModelTests.FakeReleaseService service = new([candidate])
            {
                CompletePreparation = true
            };
            ReleaseVerificationViewModel viewModel = new(new ReleaseVerificationController(
                service,
                () => new ReleaseVerificationViewModelTests.FakeProtocolClient(
                    false,
                    candidate,
                    terminalRejection: true
                )
            ));
            ReleaseVerificationWindow window = new(viewModel)
            {
                RequestedThemeVariant = theme
            };
            window.Show();
            await WaitUntilAsync(() => viewModel.IsDownloadActionVisible);

            window.ActualThemeVariant.Should().BeSameAs(theme);
            ComboBox selector = window.FindControl<ComboBox>("ReleaseSelector")!;
            selector.IsFocused.Should().BeTrue();
            Color focus = Solid(selector.BorderBrush, "visible keyboard focus ring");
            Color card = Solid(window.FindControl<Border>("ManualFallbackCard")!.Background, "manual-help card");
            Color foreground = Solid(window.Foreground, "window foreground");
            Contrast(focus, card).Should().BeGreaterThanOrEqualTo(3, $"keyboard focus must remain distinguishable in {theme}");
            Contrast(foreground, card).Should().BeGreaterThanOrEqualTo(4.5, $"manual help must remain readable in {theme}");

            TextBlock manualHelp = window.FindControl<TextBlock>("ManualFallbackText")!;
            manualHelp.IsEffectivelyVisible.Should().BeTrue();
            AutomationProperties.GetName(manualHelp).Should().Be("Manual terminal installation help");
            Solid(manualHelp.Foreground, "manual-help foreground").Should().Be(foreground);

            await viewModel.DownloadAndVerifyCommand.ExecuteAsync();
            await WaitUntilAsync(() => viewModel.IsErrorVisible);

            Border error = window.FindControl<Border>("ErrorRegion")!;
            Color errorBackground = Solid(error.Background, "error background");
            Color errorForeground = Solid(window.FindControl<TextBlock>("ErrorMessageText")!.Foreground, "error foreground");
            Color semanticErrorBorder = ResourceColor(window, "InstallerErrorBorderBrush");
            error.IsEffectivelyVisible.Should().BeTrue();
            Contrast(errorForeground, errorBackground).Should().BeGreaterThanOrEqualTo(4.5, $"error text must remain readable in {theme}");
            Contrast(semanticErrorBorder, errorBackground).Should().BeGreaterThanOrEqualTo(3, $"the error boundary must remain visible in {theme}");

            window.Close();
            await WaitUntilAsync(() => !window.IsVisible);
        }
    }

    [AvaloniaTest]
    public async Task AShownWindowRespondsToLightAndDarkThemeChanges()
    {
        ReleaseVerificationWindow window = CreateWindow();
        window.RequestedThemeVariant = ThemeVariant.Light;
        window.Show();
        await WaitUntilAsync(() => ((ReleaseVerificationViewModel)window.DataContext!).IsDownloadActionVisible);
        Color lightBackground = Solid(window.Background, "light window background");

        window.RequestedThemeVariant = ThemeVariant.Dark;
        Dispatcher.UIThread.RunJobs();
        Color darkBackground = Solid(window.Background, "dark window background");

        window.ActualThemeVariant.Should().BeSameAs(ThemeVariant.Dark);
        darkBackground.Should().NotBe(lightBackground);

        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
    }

    private static ReleaseVerificationWindow CreateWindow()
    {
        ReviewedReleaseCandidate candidate = ReleaseVerificationViewModelTests.Candidate();
        return new ReleaseVerificationWindow(new ReleaseVerificationViewModel(new ReleaseVerificationController(
            new ReleaseVerificationViewModelTests.FakeReleaseService([candidate]),
            () => new ReleaseVerificationViewModelTests.FakeProtocolClient(false, candidate)
        )));
    }

    private static Color ResourceColor(Control control, string key)
    {
        control.TryFindResource(key, control.ActualThemeVariant, out object? value).Should().BeTrue();
        return Solid(value as IBrush, key);
    }

    private static Color Solid(IBrush? brush, string description)
    {
        return brush.Should().BeAssignableTo<ISolidColorBrush>(description).Subject.Color;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The expected themed GUI state was not reached.");
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static double Contrast(Color first, Color second)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte channel)
            {
                double value = channel / 255d;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }

            return (0.2126 * Linear(color.R)) + (0.7152 * Linear(color.G)) + (0.0722 * Linear(color.B));
        }

        double brighter = Math.Max(Luminance(first), Luminance(second));
        double darker = Math.Min(Luminance(first), Luminance(second));
        return (brighter + 0.05) / (darker + 0.05);
    }
}
