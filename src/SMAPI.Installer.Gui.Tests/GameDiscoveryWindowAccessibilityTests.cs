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
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Gui.Frontend;
using StardewModdingAPI.Installer.Gui.ViewModels;

[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]
[assembly: LevelOfParallelism(1)]

namespace StardewModdingAPI.Installer.Gui.Tests;

internal sealed partial class GameDiscoveryAccessibilityTests
{
    [AvaloniaTest]
    public async Task EmptyStateFocusesManualChoiceAndExposesOnePoliteStatusRegion()
    {
        GameDiscoveryControllerTests.FakeVerifiedSession session = new();
        GameDiscoveryWindow window = CreateWindow(session);
        window.FolderPickerRequested += (_, _) => { };
        window.Show();
        GameDiscoveryViewModel viewModel = (GameDiscoveryViewModel)window.DataContext!;
        await WaitUntilAsync(() => viewModel.IsEmptyVisible);

        Border status = window.FindControl<Border>("StatusRegion")!;
        Button browse = window.FindControl<Button>("BrowseButton")!;
        Button retry = window.FindControl<Button>("RetryButton")!;
        ProgressBar progress = window.FindControl<ProgressBar>("DiscoveryProgress")!;

        ControlAutomationPeer.CreatePeerForElement(status).GetLiveSetting().Should().Be(AutomationLiveSetting.Polite);
        AutomationProperties.GetName(status).Should().Be(viewModel.LiveAnnouncement);
        AutomationProperties.GetName(browse).Should().NotBeNullOrWhiteSpace();
        AutomationProperties.GetName(retry).Should().NotBeNullOrWhiteSpace();
        AutomationProperties.GetName(progress).Should().NotBeNullOrWhiteSpace();
        browse.IsFocused.Should().BeTrue();
        viewModel.DurableState.Should().Contain("no game files have been modified");

        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
        session.DisposeCalls.Should().Be(1);
    }

    [AvaloniaTest]
    public async Task RealAccessKeysDriveListBrowseContinueAndBusyCancellation()
    {
        ProtocolGameCandidate valid = GameDiscoveryControllerTests.Candidate("valid", LinuxGameFolderStatus.Valid);
        TaskCompletionSource? blockedStarted = null;
        GameDiscoveryControllerTests.FakeVerifiedSession session = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([valid])
        };
        GameDiscoveryWindow window = CreateWindow(session);
        GameDiscoveryViewModel viewModel = (GameDiscoveryViewModel)window.DataContext!;
        int pickerRequests = 0;
        window.FolderPickerRequested += (_, _) => pickerRequests++;
        ProtocolGameCandidate? continued = null;
        viewModel.ContinueRequested += (_, args) => continued = args.Candidate;
        window.Show();
        await WaitUntilAsync(() => viewModel.Candidates.Count == 1);

        ListBox list = window.FindControl<ListBox>("CandidateList")!;
        Button browse = window.FindControl<Button>("BrowseButton")!;
        Button cancel = window.FindControl<Button>("CancelButton")!;
        Control[] keyed =
        [
            list,
            browse,
            window.FindControl<Button>("RetryButton")!,
            window.FindControl<Button>("ContinueButton")!,
            cancel
        ];
        keyed.Select(AutomationProperties.GetAccessKey).Should().NotContainNulls().And.OnlyHaveUniqueItems();

        PressAccessKey(window, PhysicalKey.G);
        list.IsKeyboardFocusWithin.Should().BeTrue("Alt+G moves focus into the detected-game list");
        list.SelectedItem = viewModel.Candidates[0];
        Dispatcher.UIThread.RunJobs();
        await WaitUntilAsync(() => viewModel.IsContinueVisible);
        PressAccessKey(window, PhysicalKey.N);
        continued.Should().BeSameAs(valid);

        PressAccessKey(window, PhysicalKey.B);
        pickerRequests.Should().Be(1);
        blockedStarted = GameDiscoveryControllerTests.NewCompletion();
        session.Validation = async (_, cancellationToken) =>
        {
            blockedStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return valid;
        };
        Task validation = window.ApplyManualFolderAsync("/games/chosen");
        await blockedStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => cancel.IsVisible && cancel.IsEffectivelyEnabled);
        PressAccessKey(window, PhysicalKey.C);
        await validation;
        await WaitUntilAsync(() => viewModel.Heading == "Game-folder check cancelled");

        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
    }

    [AvaloniaTest]
    public async Task InvalidManualFolderHasExactAccessibleReasonAndSafeFocus()
    {
        ProtocolGameCandidate invalid = GameDiscoveryControllerTests.Candidate("manual", LinuxGameFolderStatus.MissingGameAssembly);
        GameDiscoveryControllerTests.FakeVerifiedSession session = new()
        {
            Validation = (_, _) => Task.FromResult(invalid)
        };
        GameDiscoveryWindow window = CreateWindow(session);
        window.FolderPickerRequested += (_, _) => { };
        window.Show();
        GameDiscoveryViewModel viewModel = (GameDiscoveryViewModel)window.DataContext!;
        await WaitUntilAsync(() => viewModel.IsEmptyVisible);

        await window.ApplyManualFolderAsync("/games/manual");
        await WaitUntilAsync(() => viewModel.IsProblemVisible);
        Border status = window.FindControl<Border>("StatusRegion")!;
        Border problem = window.FindControl<Border>("ProblemRegion")!;
        AutomationPeer problemPeer = ControlAutomationPeer.CreatePeerForElement(problem);

        ControlAutomationPeer.CreatePeerForElement(status).GetLiveSetting().Should().Be(AutomationLiveSetting.Off);
        problemPeer.GetLiveSetting().Should().Be(AutomationLiveSetting.Polite);
        problemPeer.GetName().Should().Be($"{viewModel.Heading}. {viewModel.Message}");
        problemPeer.GetName().Should().Contain("game assembly").And.Contain("not found");
        viewModel.IsContinueVisible.Should().BeFalse();
        window.FindControl<Button>("BrowseButton")!.IsFocused.Should().BeTrue();

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
        GameDiscoveryWindow window = CreateWindow(new());
        AssertRenderedLayout(window, PhysicalViewportWidth / scale);
        window.Close();
    }

    [AvaloniaTest]
    public void Narrow420DipLayoutAndPrimaryContrastRemainAccessible()
    {
        GameDiscoveryWindow window = CreateWindow(new());
        ScrollViewer scroll = AssertRenderedLayout(window, 420);
        Button browse = window.FindControl<Button>("BrowseButton")!;
        ISolidColorBrush background = browse.Background.Should().BeAssignableTo<ISolidColorBrush>().Subject;

        window.IsNarrowLayout.Should().BeTrue();
        window.FindControl<Grid>("PageGrid")!.Margin.Left.Should().Be(14);
        scroll.Extent.Width.Should().BeLessThanOrEqualTo(scroll.Viewport.Width + 1);
        Contrast(background.Color, Colors.White).Should().BeGreaterThanOrEqualTo(4.5);
        AutomationProperties.GetHeadingLevel(window.FindControl<TextBlock>("StatusHeading")!).Should().Be(2);
        window.Close();
    }

    private static GameDiscoveryWindow CreateWindow(GameDiscoveryControllerTests.FakeVerifiedSession session)
    {
        return new(new GameDiscoveryViewModel(new GameDiscoveryController(session)));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The expected game-discovery window state was not reached.");
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void PressAccessKey(GameDiscoveryWindow window, PhysicalKey physicalKey)
    {
        window.KeyPressQwerty(PhysicalKey.AltLeft, RawInputModifiers.None);
        window.KeyPressQwerty(physicalKey, RawInputModifiers.Alt);
        window.KeyReleaseQwerty(physicalKey, RawInputModifiers.Alt);
        window.KeyReleaseQwerty(PhysicalKey.AltLeft, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static ScrollViewer AssertRenderedLayout(GameDiscoveryWindow window, double width)
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
