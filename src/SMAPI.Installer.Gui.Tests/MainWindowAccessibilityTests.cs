using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using FluentAssertions;

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
        window.MinWidth.Should().BeLessThanOrEqualTo(680);
        window.FindControl<ScrollViewer>("PageScrollViewer")!.HorizontalScrollBarVisibility
            .Should().Be(Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
    }
}
