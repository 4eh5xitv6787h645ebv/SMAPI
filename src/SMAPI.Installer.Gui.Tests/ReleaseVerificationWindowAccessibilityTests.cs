using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAssertions;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Gui.Frontend;
using StardewModdingAPI.Installer.Gui.ViewModels;

namespace StardewModdingAPI.Installer.Gui.Tests;

internal sealed class ReleaseVerificationWindowAccessibilityTests
{
    [AvaloniaTest]
    public async Task EmptyStateHasNamedControlsPoliteStatusAndKeyboardRetryFocus()
    {
        ReleaseVerificationViewModel viewModel = new(new ReleaseVerificationController(
            new ReleaseVerificationViewModelTests.FakeReleaseService([]),
            () => new ReleaseVerificationViewModelTests.FakeProtocolClient(
                false,
                ReleaseVerificationViewModelTests.Candidate()
            )
        ));
        ReleaseVerificationWindow window = new(viewModel);
        window.Show();
        await WaitUntilAsync(() => viewModel.IsEmptyVisible);

        Border status = window.FindControl<Border>("StatusRegion")!;
        Button retry = window.FindControl<Button>("RetryButton")!;
        ComboBox selector = window.FindControl<ComboBox>("ReleaseSelector")!;
        ProgressBar progress = window.FindControl<ProgressBar>("ReleaseProgress")!;
        AutomationPeer statusPeer = ControlAutomationPeer.CreatePeerForElement(status);

        statusPeer.GetLiveSetting().Should().Be(AutomationLiveSetting.Polite);
        AutomationProperties.GetName(status).Should().Be(viewModel.LiveAnnouncement);
        AutomationProperties.GetName(retry).Should().NotBeNullOrWhiteSpace();
        AutomationProperties.GetName(selector).Should().NotBeNullOrWhiteSpace();
        AutomationProperties.GetName(progress).Should().NotBeNullOrWhiteSpace();
        retry.IsFocused.Should().BeTrue();
        retry.TabIndex.Should().BeLessThan(window.FindControl<Button>("CancelButton")!.TabIndex);
        viewModel.IsVerifiedVisible.Should().BeFalse();

        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
    }

    [AvaloniaTest]
    [TestCase(1.00)]
    [TestCase(1.25)]
    [TestCase(1.50)]
    [TestCase(2.00)]
    public void DesktopScaleVariantsRenderWithoutHorizontalPageScroll(double scale)
    {
        const double PhysicalViewportWidth = 1400;
        double deviceIndependentWidth = PhysicalViewportWidth / scale;
        ReleaseVerificationWindow window = CreateWindow([]);
        AssertRenderedLayout(window, deviceIndependentWidth);
        window.Close();
    }

    [AvaloniaTest]
    public void Narrow420DipLayoutRendersWithoutHorizontalPageScrollAtTwoHundredPercent()
    {
        ReleaseVerificationWindow window = CreateWindow([]);
        ScrollViewer scroll = AssertRenderedLayout(window, 420);
        window.IsNarrowLayout.Should().BeTrue();
        window.FindControl<Grid>("PageGrid")!.Margin.Left.Should().Be(14);

        window.Close();
    }

    [AvaloniaTest]
    public async Task PrimaryActionHasAccessibleContrastAndProductionHeadingsAreExposed()
    {
        ReviewedReleaseCandidate candidate = ReleaseVerificationViewModelTests.Candidate();
        ReleaseVerificationWindow window = CreateWindow([candidate]);
        window.Show();
        ReleaseVerificationViewModel viewModel = (ReleaseVerificationViewModel)window.DataContext!;
        await WaitUntilAsync(() => viewModel.IsDownloadActionVisible);
        Button download = window.FindControl<Button>("DownloadButton")!;
        ISolidColorBrush background = download.Background.Should().BeAssignableTo<ISolidColorBrush>().Subject;

        Contrast(background.Color, Colors.White).Should().BeGreaterThanOrEqualTo(4.5);
        AutomationProperties.GetHeadingLevel(window.FindControl<TextBlock>("StatusHeading")!)
            .Should().Be(2);
        download.IsFocused.Should().BeFalse("the non-destructive release selector receives initial ready-state focus");
        window.FindControl<ComboBox>("ReleaseSelector")!.IsFocused.Should().BeTrue();

        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
    }

    [AvaloniaTest]
    public async Task EscapeCancelsOnlyAnActiveCancellableReleaseAction()
    {
        ReviewedReleaseCandidate candidate = ReleaseVerificationViewModelTests.Candidate();
        ReleaseVerificationViewModelTests.FakeReleaseService service = new([candidate]);
        ReleaseVerificationViewModel viewModel = new(new ReleaseVerificationController(
            service,
            () => new ReleaseVerificationViewModelTests.FakeProtocolClient(true, candidate)
        ));
        ReleaseVerificationWindow window = new(viewModel);
        window.Show();
        await WaitUntilAsync(() => viewModel.IsDownloadActionVisible);
        viewModel.DownloadAndVerifyCommand.Execute(null);
        await service.PreparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => viewModel.CancelCommand.CanExecute(null));

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        await WaitUntilAsync(() => viewModel.Heading == "Download cancelled");

        viewModel.DurableState.Should().Contain("nothing has been installed");
        viewModel.RetryCommand.CanExecute(null).Should().BeTrue();
        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The expected production GUI state was not reached.");
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static ReleaseVerificationWindow CreateWindow(IReadOnlyList<ReviewedReleaseCandidate> catalog)
    {
        ReviewedReleaseCandidate fallback = catalog.FirstOrDefault() ?? ReleaseVerificationViewModelTests.Candidate();
        ReleaseVerificationViewModel viewModel = new(new ReleaseVerificationController(
            new ReleaseVerificationViewModelTests.FakeReleaseService(catalog),
            () => new ReleaseVerificationViewModelTests.FakeProtocolClient(false, fallback)
        ));
        return new ReleaseVerificationWindow(viewModel);
    }

    private static ScrollViewer AssertRenderedLayout(ReleaseVerificationWindow window, double width)
    {
        window.Width = width;
        window.Height = 700;
        window.ApplyResponsiveLayout(width);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        ScrollViewer scroll = window.FindControl<ScrollViewer>("PageScrollViewer")!;
        scroll.HorizontalScrollBarVisibility.Should().Be(ScrollBarVisibility.Disabled);
        scroll.Extent.Width.Should().BeLessThanOrEqualTo(scroll.Viewport.Width + 1);
        window.CaptureRenderedFrame().Should().NotBeNull();
        return scroll;
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
