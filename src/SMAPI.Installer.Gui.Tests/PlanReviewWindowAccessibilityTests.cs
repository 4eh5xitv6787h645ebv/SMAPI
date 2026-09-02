using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Gui.Backend;
using StardewModdingAPI.Installer.Gui.Frontend;
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
        Border relationship = window.FindControl<Border>("ReleaseRelationshipRegion")!;

        await WaitUntilAsync(() => operations.IsKeyboardFocusWithin);
        operations.SelectedItem.Should().BeNull();
        inspect.IsEffectivelyEnabled.Should().BeFalse();
        operations.Items.Count.Should().Be(5);
        AutomationProperties.GetAccessKey(operations).Should().Be("Alt+O");
        ControlAutomationPeer.CreatePeerForElement(status).GetLiveSetting().Should().Be(AutomationLiveSetting.Polite);
        AutomationPeer gamePeer = ControlAutomationPeer.CreatePeerForElement(game);
        gamePeer.GetName().Should().Be("Selected game: Stardew Valley").And.NotContain(privatePath);
        ControlAutomationPeer.CreatePeerForElement(gameContext).GetName().Should().Be("Bound selected game: Stardew Valley").And.NotContain(privatePath);
        ControlAutomationPeer.CreatePeerForElement(boundary).GetName().Should()
            .Contain("Review first").And.Contain("separate final Run screen").And.Contain("changes no files");
        relationship.IsEffectivelyVisible.Should().BeFalse();
        viewModel.LiveAnnouncement.Should().NotContain(privatePath);

        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
        session.DisposeCalls.Should().Be(1);
    }

    [AvaloniaTest]
    public async Task RecoveryCardStartsExplicitUnselectedAndReadOnlyWithoutAutomaticBackendWork()
    {
        RecoveryWindowSession session = new(RecoveryPoints(2));
        PlanReviewViewModel viewModel = new(new PlanReviewController(session));
        PlanReviewWindow window = new(viewModel);
        window.Show();
        Border region = window.FindControl<Border>("RecoveryReviewRegion")!;
        Border status = window.FindControl<Border>("RecoveryStatusRegion")!;
        ListBox list = window.FindControl<ListBox>("RecoveryList")!;
        Button load = window.FindControl<Button>("LoadRecoveriesButton")!;
        Button inspect = window.FindControl<Button>("InspectRollbackButton")!;

        await WaitUntilAsync(() => window.FindControl<ListBox>("OperationList")!.IsKeyboardFocusWithin);

        region.IsVisible.Should().BeTrue();
        ControlAutomationPeer.CreatePeerForElement(region).GetName().Should()
            .Contain("Loading, selecting, and inspecting a rollback changes no files")
            .And.Contain("separate final Run screen");
        ControlAutomationPeer.CreatePeerForElement(status).GetLiveSetting().Should().Be(AutomationLiveSetting.Off);
        AutomationProperties.GetAccessKey(load).Should().Be("Alt+H");
        AutomationProperties.GetAccessKey(inspect).Should().Be("Alt+B");
        AutomationProperties.GetAccessKey(list).Should().Be("Alt+P");
        viewModel.RecoveryChoices.Should().BeEmpty();
        viewModel.SelectedRecoveryChoice.Should().BeNull();
        load.IsVisible.Should().BeTrue();
        inspect.IsVisible.Should().BeFalse();
        session.ListCalls.Should().Be(0, "opening the window must not list local recovery history");
        session.RollbackInspectionCalls.Should().Be(0, "opening the window must not inspect a rollback");

        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
    }

    [AvaloniaTest]
    public async Task RecoveryAccessKeysListWithoutDefaultSelectionThenInspectOnlyTheExplicitChoice()
    {
        BoundInstallerRecoveryPoint[] points = RecoveryPoints(3);
        RecoveryWindowSession session = new(points);
        PlanReviewViewModel viewModel = new(new PlanReviewController(session));
        PlanReviewWindow window = new(viewModel);
        window.Show();
        await WaitUntilAsync(() => window.FindControl<ListBox>("OperationList")!.IsKeyboardFocusWithin);

        PressAccessKey(window, PhysicalKey.H);
        await WaitUntilAsync(() => viewModel.RecoveryChoices.Count == points.Length);
        ListBox list = window.FindControl<ListBox>("RecoveryList")!;
        Button inspect = window.FindControl<Button>("InspectRollbackButton")!;

        list.IsVisible.Should().BeTrue();
        list.IsKeyboardFocusWithin.Should().BeTrue();
        list.SelectedItem.Should().BeNull("listing never chooses a destructive target");
        viewModel.SelectedRecoveryChoice.Should().BeNull();
        inspect.IsVisible.Should().BeTrue();
        inspect.IsEffectivelyEnabled.Should().BeFalse("an explicit recovery choice is required");
        session.ListCalls.Should().Be(1);
        session.RollbackInspectionCalls.Should().Be(0);

        list.SelectedItem = viewModel.RecoveryChoices[1];
        await WaitUntilAsync(() => inspect.IsVisible && inspect.IsEffectivelyEnabled);
        await WaitUntilAsync(() => list.ContainerFromIndex(1) is not null);
        string itemName = ControlAutomationPeer.CreatePeerForElement(list.ContainerFromIndex(1)!).GetName();
        itemName.Should().Be(viewModel.RecoveryChoices[1].AccessibleName).And.Contain("Recovery point 2");

        Border recoveryStatus = window.FindControl<Border>("RecoveryStatusRegion")!;
        Button load = window.FindControl<Button>("LoadRecoveriesButton")!;
        recoveryStatus.TabIndex.Should().BeLessThan(list.TabIndex);
        list.TabIndex.Should().BeLessThan(load.TabIndex);
        load.TabIndex.Should().BeLessThan(inspect.TabIndex);
        recoveryStatus.Focus();
        Press(window, Key.Tab);
        list.IsKeyboardFocusWithin.Should().BeTrue("forward traversal follows the visual recovery-card order");
        Press(window, Key.Tab);
        load.IsFocused.Should().BeTrue();
        Press(window, Key.Tab);
        inspect.IsFocused.Should().BeTrue();
        Press(window, Key.Tab, RawInputModifiers.Shift);
        load.IsFocused.Should().BeTrue();
        Press(window, Key.Tab, RawInputModifiers.Shift);
        list.IsKeyboardFocusWithin.Should().BeTrue("reverse traversal returns through the same visual order");

        PressAccessKey(window, PhysicalKey.B);
        await WaitUntilAsync(() => session.RollbackInspectionCalls == 1 && viewModel.IsResultVisible);

        session.InspectedRecoveryPoints.Should().Equal(points[1]);
        session.ConfirmCalls.Should().Be(0);
        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
    }

    [AvaloniaTest]
    public async Task RecoveryStatesExposeExactlyOneActiveVisibleLiveRegion()
    {
        BoundInstallerRecoveryPoint[] points = RecoveryPoints(1);
        TaskCompletionSource<BoundInstallerRecoveryCatalogResult> catalog = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecoveryWindowSession session = new(points)
        {
            RecoveryCatalog = _ => catalog.Task
        };
        PlanReviewViewModel viewModel = new(new PlanReviewController(session));
        PlanReviewWindow window = new(viewModel);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Control[] liveRegions =
        [
            window.FindControl<Border>("StatusRegion")!,
            window.FindControl<Border>("RecoveryStatusRegion")!,
            window.FindControl<Border>("ResultSummaryRegion")!
        ];
        int ActiveLiveRegionCount() => liveRegions.Count(control =>
            control.IsEffectivelyVisible
            && ControlAutomationPeer.CreatePeerForElement(control).GetLiveSetting() != AutomationLiveSetting.Off
        );

        ActiveLiveRegionCount().Should().Be(1, "the initial recovery prompt is announced once");
        Task loading = viewModel.LoadRecoveriesCommand.ExecuteAsync();
        await WaitUntilAsync(() => viewModel.IsRecoveryBusy);
        ActiveLiveRegionCount().Should().Be(1, "listing progress is announced once");
        catalog.SetResult(new BoundInstallerRecoveryCatalogSuccess(points));
        await loading;
        Dispatcher.UIThread.RunJobs();
        viewModel.SelectedRecoveryChoice = viewModel.RecoveryChoices.Single();
        Dispatcher.UIThread.RunJobs();
        ActiveLiveRegionCount().Should().Be(1, "selection guidance is announced once");

        await viewModel.InspectRollbackCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();

        viewModel.IsResultVisible.Should().BeTrue();
        ActiveLiveRegionCount().Should().Be(1, "the rollback result is announced only by its result region");
        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
    }

    [AvaloniaTest]
    public async Task ShownRecoveryListClearsSelectionBeforeRefreshOrInspectionReplacesItsItems()
    {
        RecoveryWindowSession session = new(RecoveryPoints(2));
        PlanReviewViewModel viewModel = new(new PlanReviewController(session));
        PlanReviewWindow window = new(viewModel);
        window.Show();
        await viewModel.LoadRecoveriesCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();
        ListBox list = window.FindControl<ListBox>("RecoveryList")!;
        list.SelectedItem = viewModel.RecoveryChoices[1];
        await WaitUntilAsync(() => viewModel.SelectedRecoveryChoice is not null);
        List<bool> selectionWasClearWhenItemsChanged = [];
        viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(PlanReviewViewModel.RecoveryChoices))
                selectionWasClearWhenItemsChanged.Add(viewModel.SelectedRecoveryChoice is null);
        };

        await viewModel.LoadRecoveriesCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();

        selectionWasClearWhenItemsChanged.Should().NotBeEmpty().And.OnlyContain(value => value);
        list.SelectedItem.Should().BeNull();
        viewModel.SelectedRecoveryChoice.Should().BeNull();
        session.ListCalls.Should().Be(2);
        session.RollbackInspectionCalls.Should().Be(0);

        selectionWasClearWhenItemsChanged.Clear();
        list.SelectedItem = viewModel.RecoveryChoices[0];
        await WaitUntilAsync(() => viewModel.SelectedRecoveryChoice is not null);
        await viewModel.InspectRollbackCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();

        selectionWasClearWhenItemsChanged.Should().NotBeEmpty().And.OnlyContain(value => value);
        viewModel.RecoveryChoices.Should().BeEmpty();
        viewModel.SelectedRecoveryChoice.Should().BeNull();
        session.ListCalls.Should().Be(2);
        session.RollbackInspectionCalls.Should().Be(1);
        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
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
        Border relationship = window.FindControl<Border>("ReleaseRelationshipRegion")!;
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
        peer.GetName().Should().Contain("preview only").And.Contain("no file action ran");
        peer.GetName().Should().NotContain(session.Game.DisplayPath);
        relationship.IsEffectivelyVisible.Should().BeTrue();
        relationship.Focusable.Should().BeFalse();
        ControlAutomationPeer.CreatePeerForElement(relationship).GetLiveSetting().Should().Be(AutomationLiveSetting.Off);
        TextBlock heading = window.FindControl<TextBlock>("ReleaseRelationshipHeading")!;
        TextBlock detail = window.FindControl<TextBlock>("ReleaseRelationshipDetail")!;
        ControlAutomationPeer.CreatePeerForElement(heading).GetName().Should().Be("Observed version relationship");
        ControlAutomationPeer.CreatePeerForElement(detail).GetName().Should()
            .StartWith("Fork Linux alpha (experimental).")
            .And.Contain("Version comparison only")
            .And.Contain("does not establish identical package bytes or acquisition source");
        AutomationProperties.GetHeadingLevel(heading).Should().Be(3);

        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
    }

    [AvaloniaTest]
    public async Task AvailableOrdinaryPlansExposeCurrentUpgradeAndTargetlessLabelsToAutomation()
    {
        ProtocolReleaseIdentity verified = GameDiscoveryControllerTests.Release();
        InstallerReadOnlyPlanSuccess upgrade = CreatePlan(InstallerOperation.Update) with
        {
            CurrentRelease = new(
                "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.9",
                "4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.9"
            ),
            TargetRelease = new(verified.Tag, verified.EmbeddedVersion)
        };
        InstallerReadOnlyPlanSuccess downgrade = CreatePlan(InstallerOperation.Update) with
        {
            CurrentRelease = new(
                "fork-4eh5xitv6787h645ebv-linux-v4.5.5-alpha.1",
                "4.5.5-unofficial.4eh5xitv6787h645ebv.linux.alpha.1"
            ),
            TargetRelease = new(verified.Tag, verified.EmbeddedVersion),
            Risks = [ProtocolPlanRisk.Downgrade]
        };
        (InstallerOperation Operation, InstallerReadOnlyPlanSuccess Plan, string Expected)[] cases =
        [
            (InstallerOperation.Update, CreatePlan(InstallerOperation.Update), "Same version as receipt"),
            (InstallerOperation.Update, upgrade, "Upgrade"),
            (InstallerOperation.Update, downgrade, "Downgrade"),
            (InstallerOperation.Uninstall, CreatePlan(InstallerOperation.Uninstall), "No target release is present")
        ];

        foreach ((InstallerOperation operation, InstallerReadOnlyPlanSuccess plan, string expected) in cases)
        {
            FakePlanSession session = new()
            {
                Inspection = (_, _) => Task.FromResult<InstallerReadOnlyPlanResult>(plan)
            };
            PlanReviewWindow window = CreateWindow(session);
            window.Show();
            PlanReviewViewModel viewModel = (PlanReviewViewModel)window.DataContext!;
            viewModel.SelectedOperation = Choice(viewModel, operation);
            await viewModel.InspectCommand.ExecuteAsync();
            await WaitUntilAsync(() => viewModel.IsResultVisible);

            Border relationship = window.FindControl<Border>("ReleaseRelationshipRegion")!;
            TextBlock heading = window.FindControl<TextBlock>("ReleaseRelationshipHeading")!;
            TextBlock detail = window.FindControl<TextBlock>("ReleaseRelationshipDetail")!;
            relationship.IsEffectivelyVisible.Should().BeTrue();
            ControlAutomationPeer.CreatePeerForElement(heading).GetName().Should().Be("Observed version relationship");
            ControlAutomationPeer.CreatePeerForElement(detail).GetName().Should()
                .Contain(expected)
                .And.Contain("confirm")
                .And.Contain("run this plan");

            window.Close();
            await WaitUntilAsync(() => !window.IsVisible);
        }
    }

    [AvaloniaTest]
    public async Task AvailableRollbackPlanExposesValidatedDowngradeLabelToAutomation()
    {
        BoundInstallerRecoveryPoint point = new(
            1,
            true,
            false,
            InstallerOperation.Update,
            new BoundInstallerRecoveryReleaseTarget(
                "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.9",
                "4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.9"
            )
        );
        RecoveryWindowSession session = new([point]);
        PlanReviewViewModel viewModel = new(new PlanReviewController(session));
        PlanReviewWindow window = new(viewModel);
        window.Show();
        await viewModel.LoadRecoveriesCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();
        viewModel.SelectedRecoveryChoice = viewModel.RecoveryChoices.Single();
        await viewModel.InspectRollbackCommand.ExecuteAsync();
        await WaitUntilAsync(() => viewModel.IsResultVisible);

        Border relationship = window.FindControl<Border>("ReleaseRelationshipRegion")!;
        TextBlock relationshipHeading = window.FindControl<TextBlock>("ReleaseRelationshipHeading")!;
        TextBlock relationshipDetail = window.FindControl<TextBlock>("ReleaseRelationshipDetail")!;
        relationship.IsEffectivelyVisible.Should().BeTrue();
        ControlAutomationPeer.CreatePeerForElement(relationshipHeading).GetName().Should().Be("Observed version relationship");
        ControlAutomationPeer.CreatePeerForElement(relationshipDetail).GetName().Should()
            .Contain("Downgrade")
            .And.Contain("Fork Linux alpha (experimental)")
            .And.Contain("does not confirm");
        AutomationProperties.GetHeadingLevel(relationshipHeading).Should().Be(3);

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
        const double PhysicalViewportWidth = 840;
        InstallerReadOnlyPlanSuccess maximal = CreateMaximalPlan();
        FakePlanSession session = new() { Inspection = (_, _) => Task.FromResult<InstallerReadOnlyPlanResult>(maximal) };
        PlanReviewViewModel viewModel = CreateViewModel(session);
        viewModel.SelectedOperation = Choice(viewModel, InstallerOperation.Repair);
        await viewModel.InspectCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();
        PlanReviewWindow window = new(viewModel);

        ScrollViewer scroll = AssertRenderedLayout(window, PhysicalViewportWidth / scale);

        scroll.Extent.Width.Should().BeLessThanOrEqualTo(scroll.Viewport.Width + 1);
        TextBlock candidateHeading = window.FindControl<TextBlock>("CandidateSummaryHeading")!;
        candidateHeading.TextWrapping.Should().Be(TextWrapping.Wrap);
        candidateHeading.Bounds.Width.Should().BeLessThanOrEqualTo(scroll.Viewport.Width + 1);
        viewModel.PathFactRows.Should().HaveCount(ProcessInstallerProtocolClient.MaximumVisiblePlanPathFacts);
        viewModel.PathFactSummary.Should().Contain("8 additional detail(s)");
        viewModel.PathFactRows.Should().Contain(row => row.DisplayPath.Contains("\\u202E", StringComparison.Ordinal));
        StackPanel managedPaths = window.FindControl<StackPanel>("ReceiptOwnedPathRegion")!;
        Border safety = window.FindControl<Border>("PlanSafetyRegion")!;
        ControlAutomationPeer.CreatePeerForElement(safety).GetName().Should().Be("Plan safety and recovery");
        ControlAutomationPeer.CreatePeerForElement(managedPaths).GetName().Should().Be("Managed file details");
        string[] safetyPeerNames = safety.GetVisualDescendants().OfType<Grid>()
            .Select(ControlAutomationPeer.CreatePeerForElement)
            .Where(peer => peer is not null && !string.IsNullOrEmpty(peer.GetName()))
            .Select(peer => peer!.GetName())
            .ToArray();
        safetyPeerNames.Should().Equal(viewModel.SafetyRows.Select(row => row.AccessibleName));
        string[] pathPeerNames = managedPaths.GetVisualDescendants().OfType<Border>()
            .Select(ControlAutomationPeer.CreatePeerForElement)
            .Where(peer => peer is not null && !string.IsNullOrEmpty(peer.GetName()))
            .Select(peer => peer!.GetName())
            .ToArray();
        pathPeerNames.Should().Equal(viewModel.PathFactRows.Select(row => row.AccessibleName));
        safetyPeerNames.Concat(pathPeerNames).Should().OnlyContain(name =>
            !name.Contains("/games/", StringComparison.Ordinal)
            && !name.Contains("digest", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("backend", StringComparison.OrdinalIgnoreCase)
            && !name.Contains(new string('a', 32), StringComparison.Ordinal)
        );
        TextBlock longPath = managedPaths.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Text?.Contains("\\u202E", StringComparison.Ordinal) == true);
        longPath.TextWrapping.Should().Be(TextWrapping.Wrap);
        longPath.Bounds.Width.Should().BeLessThanOrEqualTo(scroll.Viewport.Width + 1);
        longPath.Bounds.Height.Should().BeGreaterThan(24, "the maximal escaped managed path must wrap to multiple readable lines");
        scroll.Extent.Height.Should().BeGreaterThan(scroll.Viewport.Height);
        scroll.Offset = new Avalonia.Vector(0, scroll.Extent.Height);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        scroll.Offset.Y.Should().BeGreaterThan(0);
        Button finalVisibleAction = window.FindControl<Button>("StartFreshInspectionButton")!;
        finalVisibleAction.IsEffectivelyVisible.Should().BeTrue();
        finalVisibleAction.Bounds.Height.Should().BeGreaterThan(0);
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
                window.FindControl<Button>("ExitButton")!,
                window.FindControl<Button>("LoadRecoveriesButton")!,
                window.FindControl<Button>("InspectRollbackButton")!
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
            "Exit installer",
            "Load or refresh local recovery history",
            "Inspect selected rollback plan"
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
            window.FindControl<Button>("ExitButton")!,
            window.FindControl<Button>("LoadRecoveriesButton")!,
            window.FindControl<ListBox>("RecoveryList")!,
            window.FindControl<Button>("InspectRollbackButton")!
        ];
        keyed.Select(AutomationProperties.GetAccessKey).Should().NotContainNulls().And.OnlyHaveUniqueItems();

        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
    }

    [AvaloniaTest]
    [TestCase(420d)]
    [TestCase(620d)]
    [TestCase(980d)]
    public async Task MaximumRecoveryListIsVirtualizedReadableAndHasNoHorizontalOverflow(double width)
    {
        RecoveryWindowSession session = new(RecoveryPoints(ProtocolJsonSerializer.MaxRecoveryGenerations));
        PlanReviewViewModel viewModel = new(new PlanReviewController(session));
        await viewModel.LoadRecoveriesCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();
        PlanReviewWindow window = new(viewModel);

        ScrollViewer scroll = AssertRenderedLayout(window, width);
        ListBox list = window.FindControl<ListBox>("RecoveryList")!;

        list.Items.Count.Should().Be(ProtocolJsonSerializer.MaxRecoveryGenerations);
        list.SelectedItem.Should().BeNull();
        list.ItemsPanelRoot.Should().BeOfType<VirtualizingStackPanel>();
        int realized = Enumerable.Range(0, ProtocolJsonSerializer.MaxRecoveryGenerations)
            .Count(index => list.ContainerFromIndex(index) is not null);
        realized.Should().BeLessThan(ProtocolJsonSerializer.MaxRecoveryGenerations);
        list.Bounds.Width.Should().BeLessThanOrEqualTo(scroll.Viewport.Width);
        scroll.Extent.Width.Should().BeLessThanOrEqualTo(scroll.Viewport.Width + 1);
        string firstName = ControlAutomationPeer.CreatePeerForElement(list.ContainerFromIndex(0)!).GetName();
        firstName.Should().Be(viewModel.RecoveryChoices[0].AccessibleName)
            .And.Contain("Current recovery point")
            .And.NotContain("/games/");

        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
    }

    [AvaloniaTest]
    public async Task CandidateListUsesRealUncheckedCheckboxesAndOneSpaceTogglesExactlyOnce()
    {
        InstallerReadOnlyPlanCandidate first = CandidateCapability("mods/first.dll", true);
        InstallerReadOnlyPlanCandidate second = CandidateCapability("mods/second.dll", false);
        FakePlanSession session = new()
        {
            Inspection = (operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CandidatePlan(operation, [first, second]))
        };
        PlanReviewWindow window = CreateWindow(session);
        window.Show();
        PlanReviewViewModel viewModel = (PlanReviewViewModel)window.DataContext!;
        await WaitUntilAsync(() => window.FindControl<ListBox>("OperationList")!.IsKeyboardFocusWithin);
        window.FindControl<ListBox>("OperationList")!.SelectedItem = Choice(viewModel, InstallerOperation.Install);
        PressAccessKey(window, PhysicalKey.I);
        await WaitUntilAsync(() => viewModel.IsCandidateReviewVisible);
        ListBox list = window.FindControl<ListBox>("CandidateList")!;

        PressAccessKey(window, PhysicalKey.F);
        await WaitUntilAsync(() => list.IsKeyboardFocusWithin);
        list.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        CheckBox[] checks = list.GetVisualDescendants().OfType<CheckBox>().ToArray();
        checks.Should().HaveCount(2).And.OnlyContain(check => check.IsChecked == false);
        checks[0].Focus();
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        viewModel.CandidateChoices.Select(choice => choice.IsSelected).Should().Equal(true, false);
        checks[0].IsChecked.Should().BeTrue("one Space press must toggle exactly once");
        viewModel.CandidateSelectionAnnouncement.Should().Be("1 of 2 files selected.");
        ControlAutomationPeer.CreatePeerForElement(checks[0]).GetName().Should().Contain("mods/first.dll").And.Contain("not your approval");

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        viewModel.CandidateChoices.Should().OnlyContain(choice => !choice.IsSelected);
        window.FindControl<Border>("CandidateSelectionStatusRegion")!.IsFocused.Should().BeTrue();

        list.SelectedIndex = 1;
        Control secondContainer = list.ContainerFromIndex(1)!;
        secondContainer.Focus();
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        viewModel.CandidateChoices.Select(choice => choice.IsSelected).Should().Equal(false, true);
        checks[1].IsChecked.Should().BeTrue("one Space press on the list item must toggle exactly once");
        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
    }

    [AvaloniaTest]
    public async Task CandidateAccessKeysApplyClearAndStartFreshWithoutConfirmationOrExecution()
    {
        InstallerReadOnlyPlanCandidate candidate = CandidateCapability("mods/approval.dll", false);
        int inspections = 0;
        FakePlanSession session = new()
        {
            Inspection = (operation, _) =>
            {
                inspections++;
                return Task.FromResult<InstallerReadOnlyPlanResult>(CandidatePlan(operation, [candidate]));
            },
            Approval = (_, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CandidatePlan(InstallerOperation.Install, []))
        };
        PlanReviewWindow window = CreateWindow(session);
        window.Show();
        PlanReviewViewModel viewModel = (PlanReviewViewModel)window.DataContext!;
        await WaitUntilAsync(() => window.FindControl<ListBox>("OperationList")!.IsKeyboardFocusWithin);
        window.FindControl<ListBox>("OperationList")!.SelectedItem = Choice(viewModel, InstallerOperation.Install);
        PressAccessKey(window, PhysicalKey.I);
        await WaitUntilAsync(() => viewModel.IsCandidateReviewVisible);
        ListBox list = window.FindControl<ListBox>("CandidateList")!;
        Button apply = window.FindControl<Button>("ApplyCandidatesButton")!;
        Button clear = window.FindControl<Button>("ClearCandidatesButton")!;
        Button fresh = window.FindControl<Button>("StartFreshInspectionButton")!;

        Control[] keyed =
        [
            window.FindControl<ListBox>("OperationList")!,
            window.FindControl<Button>("InspectButton")!,
            window.FindControl<Button>("RetryButton")!,
            window.FindControl<Button>("CancelButton")!,
            window.FindControl<Button>("ExitButton")!,
            list,
            apply,
            clear,
            fresh
        ];
        keyed.Select(AutomationProperties.GetAccessKey).Should().Equal("Alt+O", "Alt+I", "Alt+T", "Alt+C", "Alt+E", "Alt+F", "Alt+A", "Alt+L", "Alt+R");
        keyed.Select(AutomationProperties.GetAccessKey).Should().OnlyHaveUniqueItems();
        apply.IsEffectivelyEnabled.Should().BeFalse();
        string?[] buttonContent = window.GetVisualDescendants().OfType<Button>().Select(button => button.Content?.ToString()).ToArray();
        buttonContent.Any(text => text != null && text.Contains("Select all", StringComparison.OrdinalIgnoreCase)).Should().BeFalse();

        viewModel.CandidateChoices.Single().IsSelected = true;
        await WaitUntilAsync(() => clear.IsEffectivelyEnabled);
        PressAccessKey(window, PhysicalKey.L);
        viewModel.CandidateChoices.Single().IsSelected.Should().BeFalse();
        session.ApprovedCandidates.Should().BeEmpty();

        viewModel.CandidateChoices.Single().IsSelected = true;
        await WaitUntilAsync(() => apply.IsEffectivelyEnabled);
        PressAccessKey(window, PhysicalKey.A);
        await WaitUntilAsync(() => viewModel.CandidateChoices.Count == 0 && fresh.IsEffectivelyEnabled);
        session.ApprovedCandidates.Should().ContainSingle();
        Border countStatus = window.FindControl<Border>("CandidateSelectionStatusRegion")!;
        string cumulativeCount = ControlAutomationPeer.CreatePeerForElement(countStatus).GetName();
        cumulativeCount.Should().Be("1 approval already applied and fixed in this preview; 0 of 0 remaining files selected.");
        cumulativeCount.Should().NotContain("approval.dll");
        window.FindControl<TextBlock>("CandidateCapacityDetail")!.IsVisible.Should().BeFalse("one applied approval does not fill the bounded history");
        AutomationProperties.GetHelpText(apply).Should().Contain("additive approvals").And.Contain("does not change files, confirm, or execute");
        AutomationProperties.GetHelpText(fresh).Should().Contain("Revokes the current preview").And.Contain("does not undo");

        PressAccessKey(window, PhysicalKey.R);
        await WaitUntilAsync(() => inspections == 2 && viewModel.CandidateChoices.Count == 1);
        viewModel.CandidateChoices.Single().IsSelected.Should().BeFalse();
        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
    }

    [AvaloniaTest]
    [TestCase(420d)]
    [TestCase(620d)]
    [TestCase(980d)]
    public async Task CandidateRegionHasNoHorizontalOverflowAtResponsiveDesktopWidths(double width)
    {
        string hostileLongPath = $"mods/{string.Join('/', Enumerable.Range(0, 15).Select(index => $"{index:D2}{new string('x', 198)}"))}/tail\u202E.dll";
        InstallerReadOnlyPlanCandidate candidate = CandidateCapability(hostileLongPath, true);
        FakePlanSession session = new()
        {
            Inspection = (operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CandidatePlan(operation, [candidate]))
        };
        PlanReviewViewModel viewModel = CreateViewModel(session);
        viewModel.SelectedOperation = Choice(viewModel, InstallerOperation.Install);
        await viewModel.InspectCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();
        PlanReviewWindow window = new(viewModel);

        ScrollViewer scroll = AssertRenderedLayout(window, width);

        viewModel.CandidateChoices.Single().DisplayPath.Should().Contain("\\u202E").And.NotContain("\u202E");
        scroll.Extent.Width.Should().BeLessThanOrEqualTo(scroll.Viewport.Width + 1);
        window.FindControl<ListBox>("CandidateList")!.Bounds.Width.Should().BeLessThanOrEqualTo(scroll.Viewport.Width);
        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
    }

    [AvaloniaTest]
    public async Task MaximumCandidateListIsVirtualizedAndLiveRegionContainsCountsOnly()
    {
        InstallerReadOnlyPlanCandidate[] candidates = Enumerable.Range(0, ProtocolJsonSerializer.MaxPlanCandidates)
            .Select(index => CandidateCapability($"mods/private-{index:D3}.dll", index % 2 == 0))
            .ToArray();
        FakePlanSession session = new()
        {
            Inspection = (operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CandidatePlan(operation, candidates))
        };
        PlanReviewViewModel viewModel = CreateViewModel(session);
        viewModel.SelectedOperation = Choice(viewModel, InstallerOperation.Install);
        await viewModel.InspectCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();
        PlanReviewWindow window = new(viewModel);
        AssertRenderedLayout(window, 620);
        ListBox list = window.FindControl<ListBox>("CandidateList")!;
        Border live = window.FindControl<Border>("CandidateSelectionStatusRegion")!;

        list.Items.Count.Should().Be(ProtocolJsonSerializer.MaxPlanCandidates);
        list.ItemsPanelRoot.Should().BeOfType<VirtualizingStackPanel>();
        int realized = Enumerable.Range(0, ProtocolJsonSerializer.MaxPlanCandidates).Count(index => list.ContainerFromIndex(index) is not null);
        realized.Should().BeLessThan(ProtocolJsonSerializer.MaxPlanCandidates);
        AutomationPeer livePeer = ControlAutomationPeer.CreatePeerForElement(live);
        livePeer.GetLiveSetting().Should().Be(AutomationLiveSetting.Polite);
        livePeer.GetName().Should().Be($"0 of {ProtocolJsonSerializer.MaxPlanCandidates} files selected.");
        livePeer.GetName().Should().NotContain("private-000.dll");
        list.ScrollIntoView(ProtocolJsonSerializer.MaxPlanCandidates - 1);
        Dispatcher.UIThread.RunJobs();
        list.ContainerFromIndex(ProtocolJsonSerializer.MaxPlanCandidates - 1).Should().NotBeNull();
        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
    }

    [AvaloniaTest]
    public async Task PartialApprovalCapacityKeepsApplyUsableAndCapacityWarningHiddenAtTwo()
    {
        InstallerReadOnlyPlanCandidate first = CandidateCapability("mods/first-private.dll", false);
        InstallerReadOnlyPlanCandidate second = CandidateCapability("mods/second-private.dll", false);
        InstallerReadOnlyPlanCandidate remaining = CandidateCapability("mods/remaining-private.dll", false);
        FakePlanSession session = new()
        {
            Inspection = (operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CandidatePlan(operation, [first, second, remaining])),
            Approval = (_, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CandidatePlan(InstallerOperation.Install, [remaining]))
        };
        PlanReviewViewModel viewModel = CreateViewModel(session);
        viewModel.SelectedOperation = Choice(viewModel, InstallerOperation.Install);
        await viewModel.InspectCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();
        viewModel.CandidateChoices[0].IsSelected = true;
        viewModel.CandidateChoices[1].IsSelected = true;
        await viewModel.ApplyCandidatesCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();
        viewModel.CandidateChoices.Single().IsSelected = true;
        PlanReviewWindow window = new(viewModel);

        AssertRenderedLayout(window, 620);
        TextBlock capacity = window.FindControl<TextBlock>("CandidateCapacityDetail")!;
        Button apply = window.FindControl<Button>("ApplyCandidatesButton")!;
        Button clear = window.FindControl<Button>("ClearCandidatesButton")!;
        Border countStatus = window.FindControl<Border>("CandidateSelectionStatusRegion")!;

        capacity.IsVisible.Should().BeFalse();
        viewModel.CandidateCapacityDetail.Should().BeEmpty();
        apply.IsEffectivelyEnabled.Should().BeTrue();
        clear.IsEffectivelyEnabled.Should().BeTrue();
        string liveName = ControlAutomationPeer.CreatePeerForElement(countStatus).GetName();
        liveName.Should().Be("2 approvals already applied and fixed in this preview; 1 of 1 remaining files selected.");
        liveName.Should().NotContain("remaining-private.dll").And.NotContain("history is full");
        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
    }

    [AvaloniaTest]
    public async Task SelectionBeyondOneRemainingSlotExplainsDisabledApplyThenClearsWhenReduced()
    {
        InstallerReadOnlyPlanCandidate[] initial = Enumerable.Range(0, ProtocolJsonSerializer.MaxPlanCandidates - 1)
            .Select(index => CandidateCapability($"mods/applied-{index:D3}.dll", false))
            .ToArray();
        InstallerReadOnlyPlanCandidate firstRemaining = CandidateCapability("mods/first-remaining-private.dll", false);
        InstallerReadOnlyPlanCandidate secondRemaining = CandidateCapability("mods/second-remaining-private.dll", false);
        FakePlanSession session = new()
        {
            Inspection = (operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CandidatePlan(operation, initial)),
            Approval = (_, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CandidatePlan(InstallerOperation.Install, [firstRemaining, secondRemaining]))
        };
        PlanReviewViewModel viewModel = CreateViewModel(session);
        viewModel.SelectedOperation = Choice(viewModel, InstallerOperation.Install);
        await viewModel.InspectCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();
        foreach (PlanReviewCandidateChoice choice in viewModel.CandidateChoices)
            choice.IsSelected = true;
        await viewModel.ApplyCandidatesCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();
        foreach (PlanReviewCandidateChoice choice in viewModel.CandidateChoices)
            choice.IsSelected = true;
        PlanReviewWindow window = new(viewModel);

        AssertRenderedLayout(window, 620);
        TextBlock capacity = window.FindControl<TextBlock>("CandidateCapacityDetail")!;
        Button apply = window.FindControl<Button>("ApplyCandidatesButton")!;
        Button clear = window.FindControl<Button>("ClearCandidatesButton")!;
        Border countStatus = window.FindControl<Border>("CandidateSelectionStatusRegion")!;

        capacity.IsVisible.Should().BeTrue();
        string capacityName = ControlAutomationPeer.CreatePeerForElement(capacity).GetName();
        capacityName.Should().Be(viewModel.CandidateCapacityDetail)
            .And.Contain("Only 1 more approval fits")
            .And.Contain("2 files are selected")
            .And.Contain("Uncheck files or Clear local choices")
            .And.NotContain("remaining-private.dll");
        apply.IsEffectivelyEnabled.Should().BeFalse();
        clear.IsEffectivelyEnabled.Should().BeTrue();
        string liveName = ControlAutomationPeer.CreatePeerForElement(countStatus).GetName();
        liveName.Should().Be($"{ProtocolJsonSerializer.MaxPlanCandidates - 1} approvals already applied and fixed in this preview; 2 of 2 remaining files selected.");
        liveName.Should().NotContain("remaining-private.dll").And.NotContain("Only 1");

        viewModel.CandidateChoices[1].IsSelected = false;
        Dispatcher.UIThread.RunJobs();

        capacity.IsVisible.Should().BeFalse();
        viewModel.CandidateCapacityDetail.Should().BeEmpty();
        apply.IsEffectivelyEnabled.Should().BeTrue();
        clear.IsEffectivelyEnabled.Should().BeTrue();
        ControlAutomationPeer.CreatePeerForElement(countStatus).GetName().Should().Be($"{ProtocolJsonSerializer.MaxPlanCandidates - 1} approvals already applied and fixed in this preview; 1 of 2 remaining files selected.");
        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
    }

    [AvaloniaTest]
    public async Task FullApprovalCapacityExplainsDisabledApplyAndUsableLocalClearToVisualAndAutomationUsers()
    {
        InstallerReadOnlyPlanCandidate[] maximum = Enumerable.Range(0, ProtocolJsonSerializer.MaxPlanCandidates)
            .Select(index => CandidateCapability($"mods/capacity-{index:D3}.dll", false))
            .ToArray();
        InstallerReadOnlyPlanCandidate remaining = CandidateCapability("mods/remaining-private.dll", false);
        FakePlanSession session = new()
        {
            Inspection = (operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CandidatePlan(operation, maximum)),
            Approval = (_, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CandidatePlan(InstallerOperation.Install, [remaining]))
        };
        PlanReviewViewModel viewModel = CreateViewModel(session);
        viewModel.SelectedOperation = Choice(viewModel, InstallerOperation.Install);
        await viewModel.InspectCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();
        foreach (PlanReviewCandidateChoice choice in viewModel.CandidateChoices)
            choice.IsSelected = true;
        await viewModel.ApplyCandidatesCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();
        viewModel.CandidateChoices.Single().IsSelected = true;
        PlanReviewWindow window = new(viewModel);

        AssertRenderedLayout(window, 620);
        TextBlock capacity = window.FindControl<TextBlock>("CandidateCapacityDetail")!;
        Button apply = window.FindControl<Button>("ApplyCandidatesButton")!;
        Button clear = window.FindControl<Button>("ClearCandidatesButton")!;
        Border countStatus = window.FindControl<Border>("CandidateSelectionStatusRegion")!;

        capacity.IsVisible.Should().BeTrue();
        string capacityName = ControlAutomationPeer.CreatePeerForElement(capacity).GetName();
        capacityName.Should().Be(viewModel.CandidateCapacityDetail)
            .And.Contain("approval history is full")
            .And.Contain("Clear local choices")
            .And.Contain("Start a fresh inspection")
            .And.NotContain("remaining-private.dll");
        apply.IsEffectivelyEnabled.Should().BeFalse();
        clear.IsEffectivelyEnabled.Should().BeTrue();
        string liveName = ControlAutomationPeer.CreatePeerForElement(countStatus).GetName();
        liveName.Should().Be($"{ProtocolJsonSerializer.MaxPlanCandidates} approvals already applied and fixed in this preview; 1 of 1 remaining files selected.");
        liveName.Should().NotContain("remaining-private.dll").And.NotContain("history is full");
        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
    }

    private static PlanReviewWindow CreateWindow(FakePlanSession session)
        => new(CreateViewModel(session));

    private static InstallerReadOnlyPlanSuccess CreateMaximalPlan()
    {
        InstallerReadOnlyPlanCandidate[] candidates =
        [
            CandidateCapability("mods/unknown-one.dll", true, FileReplacementCandidateReason.UnknownCollision),
            CandidateCapability("mods/unknown-two.dll", true, FileReplacementCandidateReason.UnknownCollision),
            CandidateCapability("mods/modified.dll", false)
        ];
        return CandidatePlan(InstallerOperation.Repair, candidates) with
        {
            HasBlockingConflicts = true,
            Confirmation = null,
            Risks = [ProtocolPlanRisk.ModifiedOrUnknownFileApproval],
            OperationCounts = Enum.GetValues<PlanOperationKind>().Select(kind => new InstallerPlanOperationCount(kind, 20)).ToArray(),
            RecoveryCapacity = new(ProtocolJsonSerializer.MaxRecoveryGenerations, ProtocolJsonSerializer.MaxRecoveryGenerations),
            PathFacts = MaximalPathFacts(),
            AdditionalPathFactCount = 8,
            ConflictCounts = Enum.GetValues<PlanConflictCode>().Select(code => new InstallerPlanConflictCount(code, 1)).ToArray(),
            AdditionalNoticeCount = 256
        };
    }

    private static InstallerPlanPathFact[] MaximalPathFacts()
    {
        List<InstallerPlanPathFact> facts =
        [
            new("StardewValley", InstallerPlanPathFactKind.ApprovedModifiedInstalledLauncher, PlanOperationKind.Restore)
        ];
        facts.AddRange(Enumerable.Range(1, 10).Select(index => new InstallerPlanPathFact(
            index == 10
                ? $"smapi-internal/{new string('x', 220)}\\u202E.dll"
                : $"smapi-internal/{index:D2}-owned.dll",
            InstallerPlanPathFactKind.ApprovedModifiedReceiptOwned,
            PlanOperationKind.Replace
        )));
        facts.Add(new("smapi-internal/00-missing.dll", InstallerPlanPathFactKind.MissingReceiptOwned, PlanOperationKind.Create));
        return facts.ToArray();
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

    private static BoundInstallerRecoveryPoint[] RecoveryPoints(int count)
    {
        ProtocolReleaseIdentity release = GameDiscoveryControllerTests.Release();
        return Enumerable.Range(1, count)
            .Select(ordinal => new BoundInstallerRecoveryPoint(
                ordinal,
                ordinal == 1,
                false,
                InstallerOperation.Update,
                new BoundInstallerRecoveryReleaseTarget(release.Tag, release.EmbeddedVersion)
            ))
            .ToArray();
    }

    private static InstallerReadOnlyPlanSuccess RecoveryRollbackPlan(BoundInstallerRecoveryPoint point)
    {
        ProtocolReleaseIdentity release = GameDiscoveryControllerTests.Release();
        InstallerPlanRelease current = new(release.Tag, release.EmbeddedVersion);
        InstallerPlanRelease? target = point.RestoreTarget switch
        {
            BoundInstallerRecoveryReleaseTarget value => new(value.Tag, value.EmbeddedVersion),
            BoundInstallerRecoveryUninstalledTarget => null,
            _ => throw new AssertionException("Unsupported synthetic recovery target.")
        };
        bool isDowngrade = target is not null
            && ForkReleaseIdentity.Compare(
                ForkReleaseIdentity.Parse(target.Tag),
                ForkReleaseIdentity.Parse(current.Tag)
            ) < 0;
        return new(
            InstallerOperation.Rollback,
            ObservedInstallState.KnownUnmodified,
            current,
            target,
            false,
            isDowngrade ? [ProtocolPlanRisk.Rollback, ProtocolPlanRisk.Downgrade] : [ProtocolPlanRisk.Rollback],
            ProtocolRecommendedDefault.Cancel,
            true,
            [new InstallerPlanOperationCount(PlanOperationKind.Restore, 1)],
            [],
            [],
            0
        )
        {
            Confirmation = new InstallerPlanConfirmation(),
            Candidates = []
        };
    }

    private sealed class RecoveryWindowSession : IPlanInspectionSession
    {
        private readonly IReadOnlyList<BoundInstallerRecoveryPoint> Points;
        private readonly List<BoundInstallerRecoveryPoint> Inspected = [];
        private int listCount;
        private int rollbackInspectionCount;
        private int confirmCount;

        public RecoveryWindowSession(IReadOnlyList<BoundInstallerRecoveryPoint> points)
        {
            this.Points = points;
        }

        public ProtocolReleaseIdentity Release { get; } = GameDiscoveryControllerTests.Release();
        public VerifiedGamePresentation Game { get; } = new("/games/Stardew Valley", "Stardew Valley");
        public TaskCompletionSource<InstallerProtocolClientException> Fault { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<InstallerProtocolClientException> SessionFaulted => this.Fault.Task;
        public Func<CancellationToken, Task<BoundInstallerRecoveryCatalogResult>>? RecoveryCatalog { get; init; }
        public int ListCalls => Volatile.Read(ref this.listCount);
        public int RollbackInspectionCalls => Volatile.Read(ref this.rollbackInspectionCount);
        public int ConfirmCalls => Volatile.Read(ref this.confirmCount);
        public BoundInstallerRecoveryPoint[] InspectedRecoveryPoints
        {
            get
            {
                lock (this.Inspected)
                    return this.Inspected.ToArray();
            }
        }

        public Task<BoundInstallerRecoveryCatalogResult> ListRecoveriesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref this.listCount);
            if (this.RecoveryCatalog is not null)
                return this.RecoveryCatalog(cancellationToken);
            return Task.FromResult<BoundInstallerRecoveryCatalogResult>(
                new BoundInstallerRecoveryCatalogSuccess(this.Points)
            );
        }

        public Task<InstallerReadOnlyPlanResult> InspectRollbackAsync(
            BoundInstallerRecoveryPoint point,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref this.rollbackInspectionCount);
            lock (this.Inspected)
                this.Inspected.Add(point);
            return Task.FromResult<InstallerReadOnlyPlanResult>(RecoveryRollbackPlan(point));
        }

        public Task<InstallerReadOnlyPlanResult> InspectPlanAsync(
            InstallerOperation operation,
            CancellationToken cancellationToken = default
        ) => throw new AssertionException("Ordinary plan inspection wasn't expected in this recovery-window test.");

        public Task<IConfirmedInstallerSession> ConfirmPlanAsync(
            InstallerPlanConfirmation confirmation,
            CancellationToken cancellationToken = default
        )
        {
            Interlocked.Increment(ref this.confirmCount);
            throw new AssertionException("Plan confirmation wasn't expected in this recovery-window test.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static void PressAccessKey(PlanReviewWindow window, PhysicalKey physicalKey)
    {
        window.KeyPressQwerty(PhysicalKey.AltLeft, RawInputModifiers.None);
        window.KeyPressQwerty(physicalKey, RawInputModifiers.Alt);
        window.KeyReleaseQwerty(physicalKey, RawInputModifiers.Alt);
        window.KeyReleaseQwerty(PhysicalKey.AltLeft, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Press(
        PlanReviewWindow window,
        Key key,
        RawInputModifiers modifiers = RawInputModifiers.None
    )
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static void PressClosingAccessKey(PlanReviewWindow window, PhysicalKey physicalKey)
    {
        window.KeyPressQwerty(PhysicalKey.AltLeft, RawInputModifiers.None);
        window.KeyPressQwerty(physicalKey, RawInputModifiers.Alt);
    }
}
