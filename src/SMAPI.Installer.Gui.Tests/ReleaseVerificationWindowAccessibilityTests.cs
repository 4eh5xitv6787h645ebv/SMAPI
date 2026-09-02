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
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Gui.Backend;
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
    public void ManualFallbackHelpNamesTheExactSamePackageCommandAndSafetyLimits()
    {
        ReleaseVerificationWindow window = CreateWindow([]);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        TextBlock help = window.FindControl<TextBlock>("ManualFallbackText")!;
        AutomationProperties.GetName(help).Should().Be("Manual terminal installation help");
        help.Text.Should().Contain("all six public release files");
        help.Text.Should().Contain("bash \"install on Linux.sh\"");
        help.Text.Should().Contain("normal desktop user");
        help.Text.Should().Contain("never use sudo");
        help.Text.Should().Contain("headless commands");
        help.Text.Should().Contain("rollback");
        help.Text.Should().Contain("last-resort manual extraction");

        window.Close();
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
        ReleaseVerificationWindow window = CreateWindow([], exposeLocalPackageAction: true);
        AssertRenderedLayout(window, deviceIndependentWidth);
        window.Close();
    }

    [AvaloniaTest]
    public void Narrow420DipLayoutRendersWithoutHorizontalPageScrollAtTwoHundredPercent()
    {
        ReleaseVerificationWindow window = CreateWindow([], exposeLocalPackageAction: true);
        ScrollViewer scroll = AssertRenderedLayout(window, 420);
        window.IsNarrowLayout.Should().BeTrue();
        window.FindControl<Grid>("PageGrid")!.Margin.Left.Should().Be(14);
        TextBlock heading = window.FindControl<TextBlock>("ManualFallbackHeading")!;
        heading.TextWrapping.Should().Be(TextWrapping.Wrap);
        heading.Bounds.Width.Should().BeLessThanOrEqualTo(scroll.Viewport.Width + 1);
        heading.Bounds.Height.Should().BeGreaterThan(24, "the long section heading must wrap at 420 DIP / 200% instead of clipping");

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

    [AvaloniaTest]
    public async Task TerminalErrorHasOneAssertiveExactSafeAnnouncementAndNoFalseLocationCue()
    {
        ReviewedReleaseCandidate candidate = ReleaseVerificationViewModelTests.Candidate();
        ReleaseVerificationViewModelTests.FakeReleaseService service = new([candidate]) { CompletePreparation = true };
        ReleaseVerificationViewModel viewModel = new(new ReleaseVerificationController(
            service,
            () => new ReleaseVerificationViewModelTests.FakeProtocolClient(
                false,
                candidate,
                terminalRejection: true
            )
        ));
        ReleaseVerificationWindow window = new(viewModel);
        window.Show();
        await WaitUntilAsync(() => viewModel.IsDownloadActionVisible);

        await viewModel.DownloadAndVerifyCommand.ExecuteAsync();
        await WaitUntilAsync(() => viewModel.IsErrorVisible);
        Border status = window.FindControl<Border>("StatusRegion")!;
        Border error = window.FindControl<Border>("ErrorRegion")!;
        AutomationPeer errorPeer = ControlAutomationPeer.CreatePeerForElement(error);

        ControlAutomationPeer.CreatePeerForElement(status).GetLiveSetting().Should().Be(AutomationLiveSetting.Off);
        errorPeer.GetLiveSetting().Should().Be(AutomationLiveSetting.Assertive);
        errorPeer.GetName().Should().Be($"{viewModel.Heading}. {viewModel.Message}");
        errorPeer.GetName().Should().Contain("Close and reopen the installer to start a new verification session");
        errorPeer.GetName().Should().Contain("game is unchanged");
        window.FindControl<TextBlock>("ErrorMessageText")!.Text.Should().NotContain("below");
        error.IsFocused.Should().BeTrue();

        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
    }

    [AvaloniaTest]
    [TestCase(FailureScenario.DownloadTimeout)]
    [TestCase(FailureScenario.PackageIntegrity)]
    [TestCase(FailureScenario.GitHubProvenance)]
    public async Task TypedFailureShowsFocusedEvidenceAndExactlyOneRecommendedWorkflowAction(
        FailureScenario scenario
    )
    {
        (ReleaseVerificationWindow window, ReleaseVerificationViewModel viewModel) = await ShowFailureAsync(scenario, 840);
        Border status = window.FindControl<Border>("StatusRegion")!;
        Border error = window.FindControl<Border>("ErrorRegion")!;
        Border evidence = window.FindControl<Border>("FailureEvidence")!;
        Button localPackage = window.FindControl<Button>("LocalPackageButton")!;
        Button retry = window.FindControl<Button>("RetryButton")!;
        Button exit = window.FindControl<Button>("ExitButton")!;

        ControlAutomationPeer.CreatePeerForElement(status).GetLiveSetting().Should().Be(AutomationLiveSetting.Off);
        ControlAutomationPeer.CreatePeerForElement(error).GetLiveSetting().Should().Be(AutomationLiveSetting.Assertive);
        AutomationProperties.GetName(error).Should().Be($"{viewModel.Heading}. {viewModel.Message}");
        error.IsFocused.Should().BeTrue("terminal evidence receives focus before its recommended action");
        evidence.IsVisible.Should().BeTrue();
        AutomationProperties.GetName(evidence).Should().Be("Observed release verification evidence");
        viewModel.FailureEvidence.Should().HaveCount(5);
        viewModel.FailureEvidence.Should().OnlyContain(row =>
            !string.IsNullOrWhiteSpace(row.Label)
            && !string.IsNullOrWhiteSpace(row.Value)
            && row.AccessibleName == $"{row.Label}: {row.Value}"
        );
        string allVisibleEvidence = string.Join("\n", viewModel.FailureEvidence.Select(row => row.AccessibleName));
        allVisibleEvidence.Should().Contain("Game files: Unchanged");
        allVisibleEvidence.Should().NotContain("/home/").And.NotContain("token=").And.NotContain("https://");

        if (scenario == FailureScenario.GitHubProvenance)
        {
            retry.IsVisible.Should().BeFalse();
            exit.IsVisible.Should().BeTrue();
            AutomationProperties.GetName(exit).Should().Be("Close installer and end this verification session");
            AutomationProperties.GetAccessKey(exit).Should().Be("Alt+X");
            viewModel.IsLocalPackageActionVisible.Should().BeFalse();
        }
        else
        {
            localPackage.IsVisible.Should().BeTrue("production always wires the secondary local-package route");
            localPackage.Classes.Should().NotContain("primary");
            AutomationProperties.GetName(localPackage).Should().Be("Use local release package folder");
            AutomationProperties.GetAccessKey(localPackage).Should().Be("Alt+L");
            retry.IsVisible.Should().BeTrue();
            retry.Classes.Should().Contain("primary", "retry is the one recommended workflow action");
            exit.IsVisible.Should().BeFalse();
            AutomationProperties.GetAccessKey(retry).Should().Be("Alt+T");
            AutomationProperties.GetName(retry).Should().Be(
                scenario == FailureScenario.DownloadTimeout
                    ? "Retry the release download and verification"
                    : "Download and verify the selected release again"
            );
            new[] { localPackage, retry, exit }
                .Where(button => button.IsVisible && button.Classes.Contains("primary"))
                .Should().ContainSingle().Which.Should().BeSameAs(retry);
        }

        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
    }

    [AvaloniaTest]
    [TestCase(ProtocolPrePlanErrorCode.PackageIntegrityRejected, 1.00)]
    [TestCase(ProtocolPrePlanErrorCode.PackageIntegrityRejected, 1.25)]
    [TestCase(ProtocolPrePlanErrorCode.PackageIntegrityRejected, 1.50)]
    [TestCase(ProtocolPrePlanErrorCode.PackageIntegrityRejected, 2.00)]
    [TestCase(ProtocolPrePlanErrorCode.PackageReleaseIdentityRejected, 1.00)]
    [TestCase(ProtocolPrePlanErrorCode.PackageReleaseIdentityRejected, 1.25)]
    [TestCase(ProtocolPrePlanErrorCode.PackageReleaseIdentityRejected, 1.50)]
    [TestCase(ProtocolPrePlanErrorCode.PackageReleaseIdentityRejected, 2.00)]
    public async Task LocalTypedFailureKeepsFocusedEvidenceAndFolderReselectionReachableAcrossDesktopScales(
        ProtocolPrePlanErrorCode rejection,
        double scale
    )
    {
        double width = 840 / scale;
        (ReleaseVerificationWindow window, ReleaseVerificationViewModel viewModel) = await ShowLocalFailureAsync(
            rejection,
            width
        );
        ScrollViewer scroll = window.FindControl<ScrollViewer>("PageScrollViewer")!;
        Border error = window.FindControl<Border>("ErrorRegion")!;
        Border evidence = window.FindControl<Border>("FailureEvidence")!;
        Button localPackage = window.FindControl<Button>("LocalPackageButton")!;
        Button retry = window.FindControl<Button>("RetryButton")!;
        Button exit = window.FindControl<Button>("ExitButton")!;

        ControlAutomationPeer.CreatePeerForElement(error).GetLiveSetting().Should().Be(AutomationLiveSetting.Assertive);
        error.IsFocused.Should().BeTrue("the local verification error and safe next step receive initial focus");
        evidence.IsVisible.Should().BeTrue();
        viewModel.FailureEvidence.Should().HaveCount(5);
        string visibleEvidence = string.Join('\n', viewModel.FailureEvidence.Select(row => row.AccessibleName));
        visibleEvidence.Should().Contain("Game files: Unchanged");
        visibleEvidence.Should().Contain("then choose the folder again");
        visibleEvidence.Should().NotContain("/home/").And.NotContain("token=").And.NotContain("https://");
        localPackage.IsVisible.Should().BeTrue();
        localPackage.Focusable.Should().BeTrue();
        localPackage.Bounds.Width.Should().BeGreaterThan(0);
        AutomationProperties.GetName(localPackage).Should().Be("Use local release package folder");
        AutomationProperties.GetAccessKey(localPackage).Should().Be("Alt+L");
        retry.IsVisible.Should().BeFalse();
        exit.IsVisible.Should().BeFalse();
        scroll.HorizontalScrollBarVisibility.Should().Be(ScrollBarVisibility.Disabled);
        scroll.Extent.Width.Should().BeLessThanOrEqualTo(scroll.Viewport.Width + 1);
        evidence.Bounds.Width.Should().BeLessThanOrEqualTo(scroll.Viewport.Width + 1);
        window.CaptureRenderedFrame().Should().NotBeNull();
        if (width <= 420)
        {
            window.IsNarrowLayout.Should().BeTrue();
            scroll.Extent.Height.Should().BeGreaterThan(scroll.Viewport.Height);
            scroll.Offset = new Avalonia.Vector(0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.CaptureRenderedFrame().Should().NotBeNull();
        }

        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
    }

    [AvaloniaTest]
    [TestCase(FailureScenario.DownloadTimeout, 1.00)]
    [TestCase(FailureScenario.DownloadTimeout, 1.25)]
    [TestCase(FailureScenario.DownloadTimeout, 1.50)]
    [TestCase(FailureScenario.DownloadTimeout, 2.00)]
    [TestCase(FailureScenario.PackageIntegrity, 1.00)]
    [TestCase(FailureScenario.PackageIntegrity, 1.25)]
    [TestCase(FailureScenario.PackageIntegrity, 1.50)]
    [TestCase(FailureScenario.PackageIntegrity, 2.00)]
    [TestCase(FailureScenario.GitHubProvenance, 1.00)]
    [TestCase(FailureScenario.GitHubProvenance, 1.25)]
    [TestCase(FailureScenario.GitHubProvenance, 1.50)]
    [TestCase(FailureScenario.GitHubProvenance, 2.00)]
    public async Task TypedFailureEvidenceRendersAtPhysical840AcrossDesktopScales(
        FailureScenario scenario,
        double scale
    )
    {
        double width = 840 / scale;
        (ReleaseVerificationWindow window, ReleaseVerificationViewModel viewModel) = await ShowFailureAsync(scenario, width);
        ScrollViewer scroll = window.FindControl<ScrollViewer>("PageScrollViewer")!;
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        scroll.HorizontalScrollBarVisibility.Should().Be(ScrollBarVisibility.Disabled);
        scroll.Extent.Width.Should().BeLessThanOrEqualTo(scroll.Viewport.Width + 1);
        window.FindControl<Border>("FailureEvidence")!.Bounds.Width.Should().BeLessThanOrEqualTo(scroll.Viewport.Width + 1);
        viewModel.FailureEvidence.Should().HaveCount(5);
        Button recommended = scenario == FailureScenario.GitHubProvenance
            ? window.FindControl<Button>("ExitButton")!
            : window.FindControl<Button>("RetryButton")!;
        recommended.IsVisible.Should().BeTrue();
        recommended.Focusable.Should().BeTrue();
        recommended.Bounds.Width.Should().BeGreaterThan(0);
        window.CaptureRenderedFrame().Should().NotBeNull();
        if (width <= 420)
        {
            window.IsNarrowLayout.Should().BeTrue();
            scroll.Extent.Height.Should().BeGreaterThan(scroll.Viewport.Height);
            scroll.Offset = new Avalonia.Vector(0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.CaptureRenderedFrame().Should().NotBeNull();
        }

        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
    }

    [AvaloniaTest]
    public async Task AccessKeysInvokeReadyAndBusyActionsAndTabTraversalTracksDynamicControls()
    {
        ReviewedReleaseCandidate candidate = ReleaseVerificationViewModelTests.Candidate();
        ReleaseVerificationViewModelTests.FakeReleaseService service = new([candidate]);
        ReleaseVerificationViewModel viewModel = new(new ReleaseVerificationController(
            service,
            () => new ReleaseVerificationViewModelTests.FakeProtocolClient(true, candidate)
        ));
        ReleaseVerificationWindow window = new(viewModel);
        window.LocalPackageFolderRequested += (_, _) => { };
        window.Show();
        await WaitUntilAsync(() => viewModel.IsDownloadActionVisible);
        ComboBox selector = window.FindControl<ComboBox>("ReleaseSelector")!;
        Button download = window.FindControl<Button>("DownloadButton")!;
        Button local = window.FindControl<Button>("LocalPackageButton")!;
        Button cancel = window.FindControl<Button>("CancelButton")!;
        Border status = window.FindControl<Border>("StatusRegion")!;
        Control[] actionControls =
        [
            selector,
            download,
            local,
            window.FindControl<Button>("RetryButton")!,
            window.FindControl<Button>("ExitButton")!,
            window.FindControl<Button>("ContinueButton")!,
            cancel,
            window.FindControl<InstallerDiagnosticsAccess>("DiagnosticsAccess")!.FindControl<Button>("OpenButton")!
        ];

        actionControls.Select(AutomationProperties.GetAccessKey)
            .Should().NotContainNulls()
            .And.OnlyHaveUniqueItems();
        selector.IsFocused.Should().BeTrue();
        Press(window, Key.Tab);
        download.IsFocused.Should().BeTrue("forward traversal follows the visible ready-state order");
        Press(window, Key.Tab);
        local.IsFocused.Should().BeTrue("the production local-package action follows the public download action");
        Press(window, Key.Tab, RawInputModifiers.Shift);
        download.IsFocused.Should().BeTrue("reverse traversal returns from local-package selection to download");
        PressAccessKey(window, PhysicalKey.E);
        selector.IsFocused.Should().BeTrue("Alt+E focuses the release selector in the Ready state");
        Press(window, Key.Tab);
        download.IsFocused.Should().BeTrue();
        Press(window, Key.Tab, RawInputModifiers.Shift);
        selector.IsFocused.Should().BeTrue("reverse traversal returns to the release selector");

        PressAccessKey(window, PhysicalKey.W);
        await service.PreparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() =>
            viewModel.CancelCommand.CanExecute(null)
            && viewModel.Heading == "Preparing the selected release…"
            && status.IsFocused
        );
        Press(window, Key.Tab);
        cancel.IsFocused.Should().BeTrue("disabled and hidden ready-state controls are skipped while busy");
        Press(window, Key.Tab, RawInputModifiers.Shift);
        status.IsFocused.Should().BeTrue("reverse traversal returns to the busy status region");

        PressAccessKey(window, PhysicalKey.C);
        await WaitUntilAsync(() =>
            viewModel.Heading == "Download cancelled"
            && !viewModel.CancelCommand.CanExecute(null)
            && viewModel.DurableState.Contains("nothing has been installed", StringComparison.Ordinal)
        );
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

    private static async Task<(ReleaseVerificationWindow Window, ReleaseVerificationViewModel ViewModel)> ShowFailureAsync(
        FailureScenario scenario,
        double width
    )
    {
        ReviewedReleaseCandidate candidate = ReleaseVerificationViewModelTests.Candidate();
        PackageSecurityFailureKind? preparationFailure = scenario == FailureScenario.DownloadTimeout
            ? PackageSecurityFailureKind.NetworkTimeout
            : null;
        ProtocolPrePlanErrorCode? rejection = scenario switch
        {
            FailureScenario.PackageIntegrity => ProtocolPrePlanErrorCode.PackageIntegrityRejected,
            FailureScenario.GitHubProvenance => ProtocolPrePlanErrorCode.PackageProvenanceRejected,
            _ => null
        };
        ImmediateReleaseService service = new(candidate, preparationFailure);
        ReleaseVerificationViewModel viewModel = new(new ReleaseVerificationController(
            service,
            () => new TypedFailureProtocolClient(rejection)
        ));
        ReleaseVerificationWindow window = new(viewModel)
        {
            Width = width,
            Height = 560
        };
        window.LocalPackageFolderRequested += (_, _) => { };
        window.ApplyResponsiveLayout(width);
        window.Show();
        await WaitUntilAsync(() => viewModel.IsDownloadActionVisible);
        await viewModel.DownloadAndVerifyCommand.ExecuteAsync();
        await WaitUntilAsync(() => viewModel.IsErrorVisible && window.FindControl<Border>("ErrorRegion")!.IsFocused);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return (window, viewModel);
    }

    private static async Task<(ReleaseVerificationWindow Window, ReleaseVerificationViewModel ViewModel)> ShowLocalFailureAsync(
        ProtocolPrePlanErrorCode rejection,
        double width
    )
    {
        ReviewedReleaseCandidate candidate = ReleaseVerificationViewModelTests.Candidate();
        ReleaseVerificationViewModel viewModel = new(new ReleaseVerificationController(
            new ImmediateReleaseService(candidate, failure: null),
            () => new TypedFailureProtocolClient(rejection),
            new ImmediateLocalReleaseService(candidate)
        ));
        ReleaseVerificationWindow window = new(viewModel)
        {
            Width = width,
            Height = 560
        };
        window.LocalPackageFolderRequested += (_, _) => { };
        window.ApplyResponsiveLayout(width);
        window.Show();
        await WaitUntilAsync(() => viewModel.IsDownloadActionVisible && viewModel.IsLocalPackageActionVisible);
        await viewModel.ApplyLocalPackageFolderAsync("/home/private-user/release?token=secret");
        await WaitUntilAsync(() => viewModel.IsErrorVisible && window.FindControl<Border>("ErrorRegion")!.IsFocused);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return (window, viewModel);
    }

    private static void Press(
        ReleaseVerificationWindow window,
        Key key,
        RawInputModifiers modifiers = RawInputModifiers.None
    )
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static void PressAccessKey(ReleaseVerificationWindow window, PhysicalKey physicalKey)
    {
        window.KeyPressQwerty(PhysicalKey.AltLeft, RawInputModifiers.None);
        window.KeyPressQwerty(physicalKey, RawInputModifiers.Alt);
        window.KeyReleaseQwerty(physicalKey, RawInputModifiers.Alt);
        window.KeyReleaseQwerty(PhysicalKey.AltLeft, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static ReleaseVerificationWindow CreateWindow(
        IReadOnlyList<ReviewedReleaseCandidate> catalog,
        bool exposeLocalPackageAction = false
    )
    {
        ReviewedReleaseCandidate fallback = catalog.FirstOrDefault() ?? ReleaseVerificationViewModelTests.Candidate();
        ReleaseVerificationViewModel viewModel = new(new ReleaseVerificationController(
            new ReleaseVerificationViewModelTests.FakeReleaseService(catalog),
            () => new ReleaseVerificationViewModelTests.FakeProtocolClient(false, fallback)
        ));
        ReleaseVerificationWindow window = new(viewModel);
        if (exposeLocalPackageAction)
            window.LocalPackageFolderRequested += (_, _) => { };
        return window;
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

    public enum FailureScenario
    {
        DownloadTimeout,
        PackageIntegrity,
        GitHubProvenance
    }

    private sealed class ImmediateReleaseService(
        ReviewedReleaseCandidate candidate,
        PackageSecurityFailureKind? failure
    ) : IReviewedReleaseService
    {
        public Task<IReadOnlyList<ReviewedReleaseCandidate>> LoadCatalogAsync(
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ReviewedReleaseCandidate>>([candidate]);
        }

        public Task<IPreparedReleasePackage> PrepareAsync(
            ReviewedReleaseCandidate selected,
            IProgress<ReviewedReleasePreparationProgress>? progress = null,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (failure is { } failureKind)
            {
                throw new PackageSecurityException(
                    failureKind,
                    "/home/private-user package https://example.invalid/?token=secret"
                );
            }
            progress?.Report(new(ReviewedReleasePreparationStage.ObservingTag, null, 0, 0, 0, 0));
            long total = selected.Assets.Sum(asset => asset.SizeBytes);
            long transferred = 0;
            for (int index = 0; index < selected.Assets.Length; index++)
            {
                transferred += selected.Assets[index].SizeBytes;
                progress?.Report(new(
                    ReviewedReleasePreparationStage.Downloading,
                    selected.Assets[index].Kind,
                    index + 1,
                    selected.Assets.Length,
                    transferred,
                    total
                ));
            }
            progress?.Report(new(ReviewedReleasePreparationStage.RefreshingTag, null, 0, 0, 0, 0));
            return Task.FromResult<IPreparedReleasePackage>(new ImmediatePreparedPackage(selected));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ImmediatePreparedPackage : IPreparedReleasePackage
    {
        public InstallerPackageOpenInput Package { get; }

        public ImmediatePreparedPackage(ReviewedReleaseCandidate candidate)
        {
            this.Package = new(
                candidate.Identity.Tag,
                new string('a', 40),
                "/proc/self/fd/1/package",
                "/proc/self/fd/1/checksums",
                "/proc/self/fd/1/metadata",
                "/proc/self/fd/1/manifest",
                "/proc/self/fd/1/bundle",
                "/proc/self/fd/1/bundle-checksum"
            );
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ImmediateLocalReleaseService(ReviewedReleaseCandidate candidate) : ILocalReleasePackageService
    {
        public Task<IPreparedReleasePackage> PrepareAsync(
            string selectedDirectory,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IPreparedReleasePackage>(new ImmediatePreparedPackage(candidate));
        }
    }

    private sealed class TypedFailureProtocolClient(ProtocolPrePlanErrorCode? rejection) : IInstallerProtocolClient
    {
        private readonly TaskCompletionSource<InstallerProtocolClientException> Fault = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<InstallerProtocolClientException> SessionFaulted => this.Fault.Task;

        public Task<HandshakeEvent> HandshakeAsync(
            string clientName,
            string clientVersion,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HandshakeEvent(
                ProtocolSessionId.Parse("11111111111111111111111111111111"),
                "1",
                ["verified-local-package"]
            ));
        }

        public Task<InstallerPackageOpenResult> OpenPackageAsync(
            InstallerPackageOpenInput package,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rejection is not { } code)
                throw new AssertionException("A preparation failure must not reach package opening.");
            InstallerPackageOpenResult result = new InstallerPackageOpenRejection(
                code,
                ProtocolNextAction.ReopenVerifiedPackage,
                "/home/private-user https://example.invalid/?token=secret",
                false
            );
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<ProtocolGameCandidate>> DiscoverGamesAsync(CancellationToken cancellationToken = default)
            => throw new AssertionException("Release verification must not discover a game.");

        public Task<ProtocolGameCandidate> ValidateGameAsync(string canonicalPath, CancellationToken cancellationToken = default)
            => throw new AssertionException("Release verification must not validate a game.");

        public Task<InstallerReadOnlyPlanResult> InspectPlanAsync(
            string canonicalGamePath,
            InstallerOperation operation,
            CancellationToken cancellationToken = default
        ) => throw new AssertionException("Release verification must not inspect a plan.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
