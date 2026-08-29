using Avalonia;
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
using StardewModdingAPI.Installer.Gui.Frontend;
using StardewModdingAPI.Installer.Gui.ViewModels;

namespace StardewModdingAPI.Installer.Gui.Tests;

public sealed class MainWindowAccessibilityTests
{
    [AvaloniaTest]
    public void ImportantControlsHaveNamesAndLogicalKeyboardOrder()
    {
        MainWindow window = new();

        Control[] controls =
        [
            window.FindControl<ComboBox>("FolderSelector")!,
            window.FindControl<ComboBox>("ReleaseSelector")!,
            window.FindControl<ComboBox>("OperationSelector")!,
            window.FindControl<Button>("PreviewButton")!,
            window.FindControl<Button>("ResetButton")!,
            window.FindControl<Button>("ExecuteButton")!,
            window.FindControl<ListBox>("LogList")!
        ];

        controls.Should().OnlyContain(control => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)));
        controls.Select(control => control.TabIndex).Should().BeInAscendingOrder();
        controls.Take(5).Should().OnlyContain(control => control.Focusable);
        controls.Select(AutomationProperties.GetAccessKey)
            .Where(value => !string.IsNullOrEmpty(value))
            .Should().OnlyHaveUniqueItems();
    }

    [AvaloniaTest]
    public void DemoSafetyAndDisabledExecutionAreVisibleToAssistiveTechnology()
    {
        MainWindow window = new();
        Border banner = window.FindControl<Border>("SafetyBanner")!;
        Button execute = window.FindControl<Button>("ExecuteButton")!;

        AutomationProperties.GetName(banner).Should().Contain("Safe demo");
        AutomationProperties.GetHelpText(execute).Should().Contain("backend is not connected");
        execute.IsEnabled.Should().BeFalse();
        window.MinWidth.Should().BeLessThanOrEqualTo(420);
        window.FindControl<ScrollViewer>("PageScrollViewer")!.HorizontalScrollBarVisibility
            .Should().Be(ScrollBarVisibility.Disabled);
        window.FindControl<ListBox>("LogList")!.Classes.Should().Contain("focus-ring");
    }

    [AvaloniaTest]
    public void InitialFocusAndForwardAndReverseTabTraversalAreDeterministic()
    {
        MainWindow window = new();
        ComboBox folder = window.FindControl<ComboBox>("FolderSelector")!;
        ComboBox release = window.FindControl<ComboBox>("ReleaseSelector")!;
        ComboBox operation = window.FindControl<ComboBox>("OperationSelector")!;

        window.Show();
        Dispatcher.UIThread.RunJobs();
        folder.IsFocused.Should().BeTrue("the synthetic folder is the explicit initial logical focus");

        Press(window, Key.Tab);
        release.IsFocused.Should().BeTrue();
        Press(window, Key.Tab);
        operation.IsFocused.Should().BeTrue();
        Press(window, Key.Tab, RawInputModifiers.Shift);
        release.IsFocused.Should().BeTrue();
    }

    [AvaloniaTest]
    public void UniqueAccessKeysInvokeTheirRealTargetsAndCommands()
    {
        MainWindow window = new();
        MainWindowViewModel viewModel = (MainWindowViewModel)window.DataContext!;
        ComboBox folder = window.FindControl<ComboBox>("FolderSelector")!;
        ComboBox release = window.FindControl<ComboBox>("ReleaseSelector")!;
        ComboBox operation = window.FindControl<ComboBox>("OperationSelector")!;

        window.Show();
        Dispatcher.UIThread.RunJobs();

        PressAccessKey(window, PhysicalKey.R);
        release.IsFocused.Should().BeTrue();
        PressAccessKey(window, PhysicalKey.O);
        operation.IsFocused.Should().BeTrue();
        PressAccessKey(window, PhysicalKey.G);
        folder.IsFocused.Should().BeTrue();

        PressAccessKey(window, PhysicalKey.P);
        viewModel.PreviewHeading.Should().StartWith("Synthetic");
        viewModel.SelectedRelease = viewModel.Releases[1];
        PressAccessKey(window, PhysicalKey.C);
        viewModel.SelectedRelease.Should().Be(viewModel.Releases[0]);
        viewModel.LogEntries.Should().ContainSingle(entry => entry.Contains("Selections reset", StringComparison.Ordinal));
    }

    [AvaloniaTest]
    public void PreviewPublishesOneConcisePoliteAutomationAnnouncement()
    {
        MainWindow window = new();
        MainWindowViewModel viewModel = (MainWindowViewModel)window.DataContext!;
        Border region = window.FindControl<Border>("PreviewStatusRegion")!;
        TextBlock state = window.FindControl<TextBlock>("StateLabel")!;

        window.Show();
        viewModel.PreviewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(region);
        peer.GetLiveSetting().Should().Be(AutomationLiveSetting.Polite);
        peer.GetName().Should().Be(viewModel.PreviewAnnouncement);
        peer.GetName().Should().Contain("No backend action ran");
        peer.GetName().Length.Should().BeLessThanOrEqualTo(DemoText.MaxDisplayLength);
        AutomationProperties.GetLiveSetting(state).Should().Be(AutomationLiveSetting.Off, "the durable-state text must not duplicate the concise result announcement");
    }

    [AvaloniaTest]
    [TestCase(1.00)]
    [TestCase(1.25)]
    [TestCase(1.50)]
    [TestCase(2.00)]
    public void LayoutArrangesAndRendersWithoutHorizontalPageScrollAtDesktopScale(double scale)
    {
        const double PhysicalViewportWidth = 1400;
        double deviceIndependentWidth = PhysicalViewportWidth / scale;
        AssertRenderedLayout(deviceIndependentWidth);
    }

    [AvaloniaTest]
    public void NarrowViewportStacksCardsAndRendersWithoutHorizontalPageScroll()
    {
        MainWindow window = AssertRenderedLayout(420);
        Grid layout = window.FindControl<Grid>("SelectionReviewGrid")!;
        Border review = window.FindControl<Border>("ReviewCard")!;
        Grid header = window.FindControl<Grid>("HeaderGrid")!;
        TextBlock title = window.FindControl<TextBlock>("HeaderTitle")!;
        Border badge = window.FindControl<Border>("HeaderBadge")!;

        window.IsNarrowLayout.Should().BeTrue();
        Grid.GetColumn(review).Should().Be(0);
        Grid.GetRow(review).Should().Be(1);
        layout.ColumnDefinitions.Should().ContainSingle();
        Grid.GetColumn(badge).Should().Be(0);
        Grid.GetRow(badge).Should().Be(1);
        title.Bounds.Width.Should().BeLessThanOrEqualTo(header.Bounds.Width);
        title.TextLayout!.TextLines.Should().OnlyContain(line => !line.HasOverflowed && !line.HasCollapsed, "the full title must remain visible without clipping");
    }

    [AvaloniaTest]
    public void PrimaryActionHasAtLeastFourPointFiveToOneWhiteContrastAndHeadingsAreExposed()
    {
        MainWindow window = new();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Button preview = window.FindControl<Button>("PreviewButton")!;
        ISolidColorBrush brush = preview.Background.Should().BeAssignableTo<ISolidColorBrush>().Subject;

        Contrast(brush.Color, Colors.White).Should().BeGreaterThanOrEqualTo(4.5);
        AutomationProperties.GetHeadingLevel(window.FindControl<TextBlock>("PreviewHeading")!).Should().Be(3);
    }

    private static MainWindow AssertRenderedLayout(double width)
    {
        MainWindow window = new()
        {
            Width = width,
            Height = 760
        };
        window.Show();
        window.ApplyResponsiveLayout(width);
        Dispatcher.UIThread.RunJobs();

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame().Should().NotBeNull();
        ScrollViewer scroll = window.FindControl<ScrollViewer>("PageScrollViewer")!;
        scroll.HorizontalScrollBarVisibility.Should().Be(ScrollBarVisibility.Disabled);
        scroll.Extent.Width.Should().BeLessThanOrEqualTo(scroll.Viewport.Width + 1);
        return window;
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

    private static void Press(MainWindow window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static void PressAccessKey(MainWindow window, PhysicalKey physicalKey)
    {
        window.KeyPressQwerty(PhysicalKey.AltLeft, RawInputModifiers.None);
        window.KeyPressQwerty(physicalKey, RawInputModifiers.Alt);
        window.KeyReleaseQwerty(physicalKey, RawInputModifiers.Alt);
        window.KeyReleaseQwerty(PhysicalKey.AltLeft, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }
}
