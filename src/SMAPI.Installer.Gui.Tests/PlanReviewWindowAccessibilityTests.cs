using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using FluentAssertions;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Gui.Backend;
using StardewModdingAPI.Installer.Gui.ViewModels;

namespace StardewModdingAPI.Installer.Gui.Tests;

internal sealed partial class PlanReviewPresentationTests
{
    [AvaloniaTest]
    public async Task InitialWindowFocusesAnUnselectedOperationListAndKeepsPrivatePathOutOfAutomation()
    {
        const string privatePath = "/home/private-user/secret-game";
        FakePlanSession session = new(privatePath);
        PlanReviewWindow window = CreateWindow(session);
        window.Show();
        PlanReviewViewModel viewModel = (PlanReviewViewModel)window.DataContext!;
        ListBox operations = window.FindControl<ListBox>("OperationList")!;
        Button inspect = window.FindControl<Button>("InspectButton")!;
        Border status = window.FindControl<Border>("StatusRegion")!;
        TextBlock game = window.FindControl<TextBlock>("GameDetail")!;
        Border gameContext = window.FindControl<Border>("GameContextRegion")!;
        Border boundary = window.FindControl<Border>("InspectionBoundary")!;

        await WaitUntilAsync(() => operations.IsKeyboardFocusWithin);
        operations.SelectedItem.Should().BeNull();
        inspect.IsEffectivelyEnabled.Should().BeFalse();
        operations.Items.Count.Should().Be(5);
        AutomationProperties.GetAccessKey(operations).Should().Be("Alt+O");
        ControlAutomationPeer.CreatePeerForElement(status).GetLiveSetting().Should().Be(AutomationLiveSetting.Polite);
        AutomationPeer gamePeer = ControlAutomationPeer.CreatePeerForElement(game);
        gamePeer.IsControlElement().Should().BeFalse();
        gamePeer.IsContentElement().Should().BeFalse();
        ControlAutomationPeer.CreatePeerForElement(gameContext).GetName().Should().Be("Bound game folder").And.NotContain(privatePath);
        ControlAutomationPeer.CreatePeerForElement(boundary).GetName().Should()
            .Contain("Inspection only").And.Contain("cannot approve, confirm, or run");
        viewModel.LiveAnnouncement.Should().NotContain(privatePath);

        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
        session.DisposeCalls.Should().Be(1);
    }

    [AvaloniaTest]
    public async Task RealOperationAndInspectAccessKeysProduceAFocusedPolitePreview()
    {
        FakePlanSession session = new();
        PlanReviewWindow window = CreateWindow(session);
        window.Show();
        PlanReviewViewModel viewModel = (PlanReviewViewModel)window.DataContext!;
        ListBox operations = window.FindControl<ListBox>("OperationList")!;
        Border result = window.FindControl<Border>("ResultSummaryRegion")!;
        await WaitUntilAsync(() => operations.IsKeyboardFocusWithin);

        window.FindControl<Border>("StatusRegion")!.Focus();
        Dispatcher.UIThread.RunJobs();
        PressAccessKey(window, PhysicalKey.O);
        operations.IsKeyboardFocusWithin.Should().BeTrue();
        operations.SelectedItem = viewModel.OperationChoices.Single(choice => choice.Operation == InstallerOperation.Install);
        await WaitUntilAsync(() => window.FindControl<Button>("InspectButton")!.IsEffectivelyEnabled);
        PressAccessKey(window, PhysicalKey.I);
        await WaitUntilAsync(() => viewModel.IsResultVisible && result.IsFocused);

        session.InspectedOperations.Should().Equal(InstallerOperation.Install);
        AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(result);
        peer.GetLiveSetting().Should().Be(AutomationLiveSetting.Polite);
        peer.GetName().Should().Contain("preview only").And.Contain("no action ran");
        peer.GetName().Should().NotContain(session.Game.DisplayPath);

        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
    }

    [AvaloniaTest]
    public async Task RealRetryAccessKeyRepeatsOnlyAReadOnlyInspection()
    {
        int attempt = 0;
        FakePlanSession session = new()
        {
            Inspection = (operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(
                Interlocked.Increment(ref attempt) == 1
                    ? new InstallerReadOnlyPlanRejection(
                        ProtocolPrePlanErrorCode.InspectionFailed,
                        ProtocolNextAction.InspectAgain,
                        false
                    )
                    : CreatePlan(operation)
            )
        };
        PlanReviewWindow window = CreateWindow(session);
        window.Show();
        PlanReviewViewModel viewModel = (PlanReviewViewModel)window.DataContext!;
        await WaitUntilAsync(() => window.FindControl<ListBox>("OperationList")!.IsKeyboardFocusWithin);
        window.FindControl<ListBox>("OperationList")!.SelectedItem = Choice(viewModel, InstallerOperation.Install);
        await WaitUntilAsync(() => window.FindControl<Button>("InspectButton")!.IsEffectivelyEnabled);
        PressAccessKey(window, PhysicalKey.I);
        await WaitUntilAsync(() => viewModel.IsRetryVisible);

        Button retry = window.FindControl<Button>("RetryButton")!;
        AutomationProperties.GetAccessKey(retry).Should().Be("Alt+T");
        PressAccessKey(window, PhysicalKey.T);
        await WaitUntilAsync(() => viewModel.IsResultVisible);

        session.InspectedOperations.Should().Equal(InstallerOperation.Install, InstallerOperation.Install);
        viewModel.DurableState.Should().Contain("no installer action has run");
        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task AltCAndEscapeCancelAnActiveInspectionAndFocusTerminalExit(bool escape)
    {
        TaskCompletionSource started = NewCompletion();
        FakePlanSession session = new()
        {
            Inspection = async (operation, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return CreatePlan(operation);
            }
        };
        PlanReviewWindow window = CreateWindow(session);
        window.Show();
        PlanReviewViewModel viewModel = (PlanReviewViewModel)window.DataContext!;
        await WaitUntilAsync(() => window.FindControl<ListBox>("OperationList")!.IsKeyboardFocusWithin);
        window.FindControl<ListBox>("OperationList")!.SelectedItem = Choice(viewModel, InstallerOperation.Backup);
        await WaitUntilAsync(() => window.FindControl<Button>("InspectButton")!.IsEffectivelyEnabled);
        PressAccessKey(window, PhysicalKey.I);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => viewModel.IsCancelVisible);

        if (escape)
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        else
            PressAccessKey(window, PhysicalKey.C);
        await WaitUntilAsync(() => viewModel.IsExitVisible && window.FindControl<Button>("ExitButton")!.IsFocused);

        viewModel.Heading.Should().Contain("cancelled").And.Contain("session closed");
        session.DisposeCalls.Should().Be(1);
        viewModel.IsRetryVisible.Should().BeFalse();
        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
    }

    [AvaloniaTest]
    public async Task TerminalErrorUsesAssertiveRegionAndAltEClosesOnlyAfterCleanup()
    {
        FakePlanSession session = new()
        {
            Inspection = (_, _) => Task.FromResult<InstallerReadOnlyPlanResult>(new InstallerReadOnlyPlanRejection(
                ProtocolPrePlanErrorCode.InvalidGameFolder,
                ProtocolNextAction.SelectGameFolder,
                false
            ))
        };
        PlanReviewWindow window = CreateWindow(session);
        window.Show();
        PlanReviewViewModel viewModel = (PlanReviewViewModel)window.DataContext!;
        await WaitUntilAsync(() => window.FindControl<ListBox>("OperationList")!.IsKeyboardFocusWithin);
        window.FindControl<ListBox>("OperationList")!.SelectedItem = Choice(viewModel, InstallerOperation.Install);
        await WaitUntilAsync(() => window.FindControl<Button>("InspectButton")!.IsEffectivelyEnabled);
        PressAccessKey(window, PhysicalKey.I);
        Border error = window.FindControl<Border>("ErrorRegion")!;
        await WaitUntilAsync(() => viewModel.IsExitVisible && error.IsFocused);

        AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(error);
        peer.GetLiveSetting().Should().Be(AutomationLiveSetting.Assertive);
        peer.GetName().Should().Contain("choose and validate a game folder again").And.Contain("No installer action ran");
        session.DisposeCalls.Should().Be(1, "exit cannot become available until cleanup completed");
        Button exit = window.FindControl<Button>("ExitButton")!;
        AutomationProperties.GetAccessKey(exit).Should().Be("Alt+E");

        PressClosingAccessKey(window, PhysicalKey.E);
        await WaitUntilAsync(() => !window.IsVisible);
        session.DisposeCalls.Should().Be(1);
    }

    [AvaloniaTest]
    public async Task ClosingWindowDuringInspectionCancelsAwaitsAndDisposesExactlyOnce()
    {
        TaskCompletionSource started = NewCompletion();
        FakePlanSession session = new()
        {
            Inspection = async (operation, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return CreatePlan(operation);
            }
        };
        PlanReviewWindow window = CreateWindow(session);
        window.Show();
        PlanReviewViewModel viewModel = (PlanReviewViewModel)window.DataContext!;
        await WaitUntilAsync(() => window.FindControl<ListBox>("OperationList")!.IsKeyboardFocusWithin);
        window.FindControl<ListBox>("OperationList")!.SelectedItem = Choice(viewModel, InstallerOperation.Repair);
        await WaitUntilAsync(() => window.FindControl<Button>("InspectButton")!.IsEffectivelyEnabled);
        PressAccessKey(window, PhysicalKey.I);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);

        session.DisposeCalls.Should().Be(1);
        session.InspectedOperations.Should().Equal(InstallerOperation.Repair);
    }

    [AvaloniaTest]
    [TestCase(1.00)]
    [TestCase(1.25)]
    [TestCase(1.50)]
    [TestCase(2.00)]
    public async Task MaximalPlanRendersAcrossDesktopScaleWithoutHorizontalPageScroll(double scale)
    {
        const double PhysicalViewportWidth = 1400;
        InstallerReadOnlyPlanSuccess maximal = CreateMaximalPlan();
        FakePlanSession session = new() { Inspection = (_, _) => Task.FromResult<InstallerReadOnlyPlanResult>(maximal) };
        PlanReviewViewModel viewModel = CreateViewModel(session);
        viewModel.SelectedOperation = Choice(viewModel, InstallerOperation.Install);
        await viewModel.InspectCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();
        PlanReviewWindow window = new(viewModel);

        ScrollViewer scroll = AssertRenderedLayout(window, PhysicalViewportWidth / scale);

        scroll.Extent.Width.Should().BeLessThanOrEqualTo(scroll.Viewport.Width + 1);
        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
    }

    [AvaloniaTest]
    public async Task Narrow420DipLayoutHasNoHorizontalOverflowAndOnlyInspectionControls()
    {
        FakePlanSession session = new();
        PlanReviewWindow window = CreateWindow(session);
        ScrollViewer scroll = AssertRenderedLayout(window, 420);
        string[] buttonNames = new[]
            {
                window.FindControl<Button>("InspectButton")!,
                window.FindControl<Button>("RetryButton")!,
                window.FindControl<Button>("CancelButton")!,
                window.FindControl<Button>("ExitButton")!
            }
            .Select(AutomationProperties.GetName)
            .ToArray()!;

        window.IsNarrowLayout.Should().BeTrue();
        window.FindControl<Grid>("PageGrid")!.Margin.Left.Should().Be(14);
        scroll.Extent.Width.Should().BeLessThanOrEqualTo(scroll.Viewport.Width + 1);
        buttonNames.Should().Equal(
            "Inspect selected plan",
            "Try read-only inspection again",
            "Cancel active plan inspection",
            "Exit installer"
        );
        buttonNames.Should().NotContain(name =>
            name.Contains("approve", StringComparison.OrdinalIgnoreCase)
            || name.Contains("confirm", StringComparison.OrdinalIgnoreCase)
            || name.Contains("execute", StringComparison.OrdinalIgnoreCase)
        );
        Control[] keyed =
        [
            window.FindControl<ListBox>("OperationList")!,
            window.FindControl<Button>("InspectButton")!,
            window.FindControl<Button>("RetryButton")!,
            window.FindControl<Button>("CancelButton")!,
            window.FindControl<Button>("ExitButton")!
        ];
        keyed.Select(AutomationProperties.GetAccessKey).Should().NotContainNulls().And.OnlyHaveUniqueItems();

        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
    }

    private static PlanReviewWindow CreateWindow(FakePlanSession session)
        => new(CreateViewModel(session));

    private static InstallerReadOnlyPlanSuccess CreateMaximalPlan()
    {
        return CreatePlan(InstallerOperation.Install) with
        {
            HasBlockingConflicts = true,
            Risks = [ProtocolPlanRisk.ModifiedOrUnknownFileApproval],
            OperationCounts = Enum.GetValues<PlanOperationKind>().Select(kind => new InstallerPlanOperationCount(kind, 1)).ToArray(),
            ConflictCounts = Enum.GetValues<PlanConflictCode>().Select(code => new InstallerPlanConflictCount(code, 1)).ToArray(),
            CandidateCounts =
            [
                new(
                    FileReplacementCandidateReason.UnknownCollision,
                    FileReplacementCandidateDisposition.Replace,
                    true,
                    2
                ),
                new(
                    FileReplacementCandidateReason.ModifiedReceiptOwned,
                    FileReplacementCandidateDisposition.Replace,
                    false,
                    1
                )
            ],
            AdditionalNoticeCount = 256
        };
    }

    private static ScrollViewer AssertRenderedLayout(PlanReviewWindow window, double width)
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
        window.CaptureRenderedFrame().Should().NotBeNull();
        return scroll;
    }

    private static void PressAccessKey(PlanReviewWindow window, PhysicalKey physicalKey)
    {
        window.KeyPressQwerty(PhysicalKey.AltLeft, RawInputModifiers.None);
        window.KeyPressQwerty(physicalKey, RawInputModifiers.Alt);
        window.KeyReleaseQwerty(physicalKey, RawInputModifiers.Alt);
        window.KeyReleaseQwerty(PhysicalKey.AltLeft, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static void PressClosingAccessKey(PlanReviewWindow window, PhysicalKey physicalKey)
    {
        window.KeyPressQwerty(PhysicalKey.AltLeft, RawInputModifiers.None);
        window.KeyPressQwerty(physicalKey, RawInputModifiers.Alt);
    }
}
