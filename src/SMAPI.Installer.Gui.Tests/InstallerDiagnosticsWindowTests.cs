using System.Text;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using FluentAssertions;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Privacy;
using StardewModdingAPI.Installer.Gui.Diagnostics;
using StardewModdingAPI.Installer.Gui.Frontend;
using StardewModdingAPI.Installer.Gui.ViewModels;

namespace StardewModdingAPI.Installer.Gui.Tests;

[NonParallelizable]
internal sealed class InstallerDiagnosticsWindowTests
{
    private string? TemporaryDirectory;
    private InstallerDiagnosticSession? Session;

    [TearDown]
    public async Task TearDown()
    {
        if (this.Session is not null)
            await this.Session.DisposeAsync();
        this.Session = null;
        if (this.TemporaryDirectory is not null && Directory.Exists(this.TemporaryDirectory))
            Directory.Delete(this.TemporaryDirectory, recursive: true);
        this.TemporaryDirectory = null;
    }

    [AvaloniaTest]
    public void ViewerIsPrivateKeyboardReachableAndResponsiveAtSupportedWidths()
    {
        InstallerDiagnosticSession session = this.CreateSession();
        InstallerDiagnosticsWindow window = new(session, _ => Task.CompletedTask);
        Button copy = window.FindControl<Button>("CopyButton")!;
        Button close = window.FindControl<Button>("CloseButton")!;
        TextBox text = window.FindControl<TextBox>("DiagnosticText")!;
        Border privacy = window.FindControl<Border>("PrivacyRegion")!;
        Border health = window.FindControl<Border>("SnapshotHealthRegion")!;
        Border rawBoundary = window.FindControl<Border>("RawLogBoundaryRegion")!;

        window.Title.Should().Contain("Local diagnostics");
        text.IsReadOnly.Should().BeTrue();
        text.Text.Should().Contain("Review this text before sharing it.");
        text.Text.Should().Contain("Snapshot health:")
            .And.Contain("Display-window omissions:")
            .And.Contain("Private raw-log omissions:")
            .And.Contain("Coalesced intermediate events:");
        AutomationProperties.GetName(text).Should().Be("Sanitized installer diagnostic snapshot");
        AutomationProperties.GetAccessKey(copy).Should().Be("Alt+Y");
        AutomationProperties.GetAccessKey(close).Should().Be("Alt+X");
        AutomationProperties.GetHelpText(copy).Should().Contain("never reads clipboard contents");
        privacy.TabIndex.Should().Be(0);
        text.TabIndex.Should().Be(1);
        copy.TabIndex.Should().Be(2);
        close.TabIndex.Should().Be(3);
        AutomationProperties.GetName(privacy).Should().Contain("Review this snapshot before sharing");
        AutomationProperties.GetName(health).Should().Contain("bounded omission counts");
        AutomationProperties.GetName(rawBoundary).Should().Contain("one MiB per file")
            .And.Contain("five owned files")
            .And.Contain("never uploaded automatically")
            .And.NotContain("/home/");
        window.FindControl<TextBlock>("SnapshotHealthText")!.Text.Should().Be("complete within configured bounds");
        window.FindControl<TextBlock>("SnapshotCountText")!.Text.Should()
            .Contain("displayed entries")
            .And.Contain("omitted from the private raw log")
            .And.Contain("intermediate events coalesced");

        window.Show();
        Dispatcher.UIThread.RunJobs();
        privacy.IsFocused.Should().BeTrue("the private-sharing disclosure is the dialog's initial keyboard and screen-reader context");

        window.ApplyResponsiveLayout(420);
        window.IsNarrowLayout.Should().BeTrue();
        window.FindControl<Grid>("PageGrid")!.Margin.Left.Should().Be(14);
        window.ApplyResponsiveLayout(760);
        window.IsNarrowLayout.Should().BeFalse();
        window.Close();
    }

    [AvaloniaTest]
    public void VisibleRawLogBoundaryMatchesProductionDefaults()
    {
        InstallerLogOptions defaults = new("/tmp/smapi-policy-contract");
        defaults.MaximumFileBytes.Should().Be(1024 * 1024);
        defaults.MaximumFileCount.Should().Be(5);
        defaults.MaximumAggregateBytes.Should().Be(0, "zero selects the validated file-count times file-size aggregate default");

        InstallerDiagnosticsWindow window = new(this.CreateSession(), _ => Task.CompletedTask);
        string visible = ((StackPanel)window.FindControl<Border>("RawLogBoundaryRegion")!.Child!)
            .Children.OfType<TextBlock>().Last().Text!;
        visible.Should().Contain("1 MiB per file")
            .And.Contain("five installer-owned files")
            .And.Contain("5 MiB total")
            .And.Contain("rotate when the next session starts")
            .And.Contain("never uploaded automatically");
    }

    [AvaloniaTest]
    public void ViewerHealthAndCountsMatchTheExactRenderedCaptureAboveTheEntryCap()
    {
        InstallerDiagnosticSession session = this.CreateSession();
        for (int index = 0; index < 129; index++)
            session.EnsureReadyForMutation();

        InstallerDiagnosticsWindow window = new(session, _ => Task.CompletedTask);
        string text = window.FindControl<TextBox>("DiagnosticText")!.Text!;
        int renderedEntryLines = text.Split('\n').Count(line => line.StartsWith("1970-01-01T00:00:00.0000000+00:00 [", StringComparison.Ordinal));
        string counts = window.FindControl<TextBlock>("SnapshotCountText")!.Text!;

        renderedEntryLines.Should().Be(InstallerDiagnosticSession.MaximumSanitizedCopyEntries);
        counts.Should().StartWith($"{renderedEntryLines} displayed entries")
            .And.Contain("2 omitted from the display window");
        window.FindControl<TextBlock>("SnapshotHealthText")!.Text.Should().Be("bounded; some events were omitted or coalesced");
        text.Should().Contain($"Displayed entries in this copy: {renderedEntryLines}")
            .And.Contain("Display-window omissions: 2")
            .And.NotContain("Snapshot health: complete within configured bounds");
    }

    [AvaloniaTest]
    public async Task CopyWritesOneBoundedStableSnapshotWithoutReadingClipboard()
    {
        InstallerDiagnosticSession session = this.CreateSession();
        string? copied = null;
        int writes = 0;
        InstallerDiagnosticsWindow window = new(session, value =>
        {
            writes++;
            copied = value;
            return Task.CompletedTask;
        });
        string captured = window.FindControl<TextBox>("DiagnosticText")!.Text!;
        session.EnsureReadyForMutation();

        await window.CopyForTestingAsync();

        writes.Should().Be(1);
        copied.Should().Be(captured, "the viewer copies its exact stable opening snapshot");
        copied.Should().NotContain("diagnostics.mutation-ready");
        Encoding.UTF8.GetByteCount(copied!).Should().BeLessThanOrEqualTo(InstallerDiagnosticSession.MaximumSanitizedCopyBytes);
        window.FindControl<TextBlock>("CopyStatusText")!.Text.Should().Contain("copied once").And.Contain("Review");
    }

    [AvaloniaTest]
    public async Task ConcurrentCopyRequestsAreSingleFlightAndFailuresStayGeneric()
    {
        InstallerDiagnosticSession session = this.CreateSession();
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int writes = 0;
        InstallerDiagnosticsWindow window = new(session, _ =>
        {
            writes++;
            return completion.Task;
        });

        Task first = window.CopyForTestingAsync();
        Task second = window.CopyForTestingAsync();
        await second;
        writes.Should().Be(1);
        completion.SetResult();
        await first;

        const string canary = "PRIVATE-CLIPBOARD-FAILURE-/home/example/SyntheticSave";
        InstallerDiagnosticsWindow failing = new(session, _ => throw new IOException(canary));
        await failing.CopyForTestingAsync();
        failing.FindControl<TextBlock>("CopyStatusText")!.Text.Should()
            .Contain("could not be copied")
            .And.NotContain(canary);
    }

    [AvaloniaTest]
    public async Task NeverCompletingClipboardProviderTimesOutWithoutRetry()
    {
        InstallerDiagnosticSession session = this.CreateSession();
        int writes = 0;
        TaskCompletionSource pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        InstallerDiagnosticsWindow window = new(
            session,
            _ =>
            {
                writes++;
                return pending.Task;
            },
            TimeSpan.FromMilliseconds(20)
        );

        await window.CopyForTestingAsync();
        await window.CopyForTestingAsync();

        writes.Should().Be(1);
        window.FindControl<TextBlock>("CopyStatusText")!.Text.Should().Contain("has not confirmed completion").And.Contain("may still finish");
        window.FindControl<Button>("CopyButton")!.IsEnabled.Should().BeFalse();

        int reopenedWrites = 0;
        InstallerDiagnosticsWindow reopened = new(session, _ =>
        {
            reopenedWrites++;
            return Task.CompletedTask;
        });
        await reopened.CopyForTestingAsync();
        reopenedWrites.Should().Be(0, "clipboard authority is session-scoped across viewer close and reopen");
        reopened.FindControl<TextBlock>("CopyStatusText")!.Text.Should().Contain("still pending");

        pending.SetResult();
        await WaitUntilAsync(() => !session.IsClipboardWriteActive);
        InstallerDiagnosticsWindow afterSettlement = new(session, _ =>
        {
            reopenedWrites++;
            return Task.CompletedTask;
        });
        await afterSettlement.CopyForTestingAsync();
        reopenedWrites.Should().Be(1, "a fresh explicit copy is admitted only after the original provider settles");
    }

    [AvaloniaTest]
    public void ReusableAccessIsAbsentWithoutProductionDiagnosticsAndUsesUniqueShortcut()
    {
        InstallerDiagnosticsAccess demo = new();
        demo.Attach(null);
        demo.IsVisible.Should().BeFalse();

        InstallerDiagnosticsAccess production = new();
        production.Attach(this.CreateSession());
        production.IsVisible.Should().BeTrue();
        Button open = production.FindControl<Button>("OpenButton")!;
        AutomationProperties.GetAccessKey(open).Should().Be("Alt+D");
        AutomationProperties.GetName(open).Should().Be("View local diagnostic snapshot");
    }

    [AvaloniaTest]
    [TestCase(1.00)]
    [TestCase(1.25)]
    [TestCase(1.50)]
    [TestCase(2.00)]
    public void ExpandedViewerRendersWithoutHorizontalOverflowAcrossDesktopScales(double scale)
    {
        const double PhysicalViewportWidth = 1400;
        double deviceIndependentWidth = Math.Max(420, PhysicalViewportWidth / scale);
        InstallerDiagnosticsWindow window = new(this.CreateSession(), _ => Task.CompletedTask)
        {
            Width = deviceIndependentWidth,
            Height = 700
        };
        window.ApplyResponsiveLayout(deviceIndependentWidth);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        ScrollViewer scroll = window.FindControl<ScrollViewer>("PageScrollViewer")!;
        scroll.HorizontalScrollBarVisibility.Should().Be(ScrollBarVisibility.Disabled);
        scroll.Extent.Width.Should().BeLessThanOrEqualTo(scroll.Viewport.Width + 1);
        window.FindControl<Button>("CopyButton")!.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        window.FindControl<Button>("CopyButton")!.IsVisible.Should().BeTrue();
        window.FindControl<Button>("CloseButton")!.IsVisible.Should().BeTrue();
        window.CaptureRenderedFrame().Should().NotBeNull();

        window.Close();
    }

    [AvaloniaTest]
    public void Narrow420DipViewerAtTwoHundredPercentKeepsActionsReachableWithoutHorizontalOverflow()
    {
        InstallerDiagnosticsWindow window = new(this.CreateSession(), _ => Task.CompletedTask)
        {
            Width = 420,
            Height = 420
        };
        window.ApplyResponsiveLayout(420);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        ScrollViewer scroll = window.FindControl<ScrollViewer>("PageScrollViewer")!;
        Button copy = window.FindControl<Button>("CopyButton")!;
        Button close = window.FindControl<Button>("CloseButton")!;
        window.IsNarrowLayout.Should().BeTrue();
        scroll.HorizontalScrollBarVisibility.Should().Be(ScrollBarVisibility.Disabled);
        scroll.Extent.Width.Should().BeLessThanOrEqualTo(scroll.Viewport.Width + 1);
        foreach (Button action in new[] { copy, close })
        {
            action.BringIntoView();
            action.Focus(NavigationMethod.Tab);
            Dispatcher.UIThread.RunJobs();
            action.IsFocused.Should().BeTrue();
        }
        window.CaptureRenderedFrame().Should().NotBeNull();

        window.Close();
    }

    [AvaloniaTest]
    public async Task ProductionReleaseScreenOpensDiagnosticsByKeyboardAndRestoresFocus()
    {
        InstallerDiagnosticSession session = this.CreateSession();
        ReviewedReleaseCandidate candidate = ReleaseVerificationViewModelTests.Candidate();
        ReleaseVerificationViewModel viewModel = new(new ReleaseVerificationController(
            new ReleaseVerificationViewModelTests.FakeReleaseService([candidate]),
            () => new ReleaseVerificationViewModelTests.FakeProtocolClient(true, candidate)
        ));
        ReleaseVerificationWindow window = new(viewModel, session);
        window.Show();
        await WaitUntilAsync(() => viewModel.IsDownloadActionVisible);

        InstallerDiagnosticsAccess access = window.FindControl<InstallerDiagnosticsAccess>("DiagnosticsAccess")!;
        Button open = access.FindControl<Button>("OpenButton")!;
        open.IsVisible.Should().BeTrue();
        PressAccessKey(window, PhysicalKey.D);

        await WaitUntilAsync(() => access.ActiveWindowForTesting is { IsVisible: true });
        InstallerDiagnosticsWindow viewer = access.ActiveWindowForTesting!;
        viewer.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await WaitUntilAsync(() => access.ActiveWindowForTesting is null && open.IsFocused);

        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
    }

    private InstallerDiagnosticSession CreateSession()
    {
        if (this.Session is not null)
            return this.Session;
        this.TemporaryDirectory = Path.Combine(Path.GetTempPath(), $"smapi-gui-diagnostic-window-tests-{Guid.NewGuid():N}");
        Guid operationId = Guid.NewGuid();
        InstallerLog log = new(
            new(Path.Combine(this.TemporaryDirectory, "state")),
            operationId,
            DateTimeOffset.UnixEpoch
        );
        return this.Session = new(log, operationId, () => DateTimeOffset.UnixEpoch);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The expected diagnostics UI or clipboard state did not settle within the test bound.");
            await Task.Delay(10);
        }
    }

    private static void PressAccessKey(ReleaseVerificationWindow window, PhysicalKey physicalKey)
    {
        window.KeyPressQwerty(PhysicalKey.AltLeft, RawInputModifiers.None);
        window.KeyPressQwerty(physicalKey, RawInputModifiers.Alt);
        window.KeyReleaseQwerty(physicalKey, RawInputModifiers.Alt);
        window.KeyReleaseQwerty(PhysicalKey.AltLeft, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }
}
