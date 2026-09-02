using System.Threading.Channels;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using FluentAssertions;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Gui.Backend;
using StardewModdingAPI.Installer.Gui.ViewModels;

namespace StardewModdingAPI.Installer.Gui.Tests;

internal sealed class ProductionInstallerWorkflowTests
{
    [AvaloniaTest]
    public async Task VerifiedContinueTransfersOneSessionAndActivatesDiscoveryBeforeReleaseCleanup()
    {
        TrackingClient client = new();
        GameDiscoveryWindow? activated = null;
        ProductionInstallerWorkflow workflow = CreateWorkflow(client, next => activated = next.Should().BeOfType<GameDiscoveryWindow>().Subject);
        ReleaseVerificationWindow releaseWindow = workflow.CreateInitialWindow();
        ReleaseVerificationViewModel release = (ReleaseVerificationViewModel)releaseWindow.DataContext!;
        await VerifyAsync(release);

        release.ContinueCommand.Execute(null);
        await WaitUntilAsync(() => activated is not null);

        release.IsContinueVisible.Should().BeFalse("the one-time transition handler is detached after activation");
        client.DisposeCalls.Should().Be(0, "the discovery window now owns the live backend client");
        await releaseWindow.DisposeAsync();
        client.DisposeCalls.Should().Be(0, "release cleanup must not reclaim transferred authority");
        await activated!.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [AvaloniaTest]
    public async Task DiscoveryConstructionFailureClosesTransferredAuthorityAndShowsSafeError()
    {
        TrackingClient client = new() { ThrowOnDispose = true };
        ProductionInstallerWorkflow workflow = CreateWorkflow(
            client,
            _ => throw new AssertionException("A failed construction must not activate a window."),
            _ => throw new InvalidOperationException("synthetic private construction detail")
        );
        ReleaseVerificationWindow releaseWindow = workflow.CreateInitialWindow();
        ReleaseVerificationViewModel release = (ReleaseVerificationViewModel)releaseWindow.DataContext!;
        await VerifyAsync(release);

        release.ContinueCommand.Execute(null);
        await WaitUntilAsync(() => release.IsErrorVisible && client.DisposeCalls == 1);

        release.Heading.Should().Be("The game-folder step could not open");
        release.Message.Should().NotContain("synthetic").And.Contain("no game files were changed");
        release.IsContinueVisible.Should().BeFalse();
        release.IsVerifiedVisible.Should().BeFalse();
        await releaseWindow.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [AvaloniaTest]
    public async Task PartialDiscoveryActivationClosesVisibleWindowAndTransferredAuthority()
    {
        TrackingClient client = new();
        GameDiscoveryWindow? partiallyActivated = null;
        ProductionInstallerWorkflow workflow = CreateWorkflow(
            client,
            next =>
            {
                partiallyActivated = next.Should().BeOfType<GameDiscoveryWindow>().Subject;
                partiallyActivated.Show();
                throw new InvalidOperationException("synthetic private discovery activation failure");
            }
        );
        ReleaseVerificationWindow releaseWindow = workflow.CreateInitialWindow();
        ReleaseVerificationViewModel release = (ReleaseVerificationViewModel)releaseWindow.DataContext!;
        await VerifyAsync(release);

        release.ContinueCommand.Execute(null);
        await WaitUntilAsync(() => release.IsErrorVisible && client.DisposeCalls == 1);

        partiallyActivated.Should().NotBeNull();
        partiallyActivated!.IsVisible.Should().BeFalse();
        release.Message.Should().NotContain("synthetic").And.Contain("no game files were changed");
        await releaseWindow.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [AvaloniaTest]
    public async Task ProductionPickerSelectionUsesBackendValidationAndFailureIsSanitized()
    {
        TrackingClient client = new();
        int pickerCalls = 0;
        GameDiscoveryWindow? activated = null;
        ProductionInstallerWorkflow workflow = CreateWorkflow(
            client,
            next => activated = (GameDiscoveryWindow)next,
            pickFolder: _ =>
            {
                pickerCalls++;
                return pickerCalls == 1
                    ? Task.FromResult<string?>("/games/Stardew Valley")
                    : throw new InvalidOperationException("private portal failure");
            }
        );
        ReleaseVerificationWindow releaseWindow = workflow.CreateInitialWindow();
        ReleaseVerificationViewModel release = (ReleaseVerificationViewModel)releaseWindow.DataContext!;
        await VerifyAsync(release);
        release.ContinueCommand.Execute(null);
        await WaitUntilAsync(() => activated is not null);
        GameDiscoveryViewModel discovery = (GameDiscoveryViewModel)activated!.DataContext!;

        discovery.BrowseCommand.Execute(null);
        await WaitUntilAsync(() => client.ValidatedPath is not null);
        client.ValidatedPath.Should().Be("/games/Stardew Valley");
        discovery.Heading.Should().Be("Selected game folder is valid");

        discovery.BrowseCommand.Execute(null);
        await WaitUntilAsync(() => discovery.Heading == "The desktop folder picker could not open");
        discovery.Message.Should().NotContain("private portal").And.Contain("no game files were changed");
        discovery.BrowseCommand.CanExecute(null).Should().BeTrue();

        await releaseWindow.DisposeAsync();
        await activated.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [AvaloniaTest]
    public async Task SessionFaultWhilePickerIsOpenIsNotMisreportedAsPickerFailure()
    {
        TrackingClient client = new();
        TaskCompletionSource<string?> picker = new(TaskCreationOptions.RunContinuationsAsynchronously);
        GameDiscoveryWindow? activated = null;
        ProductionInstallerWorkflow workflow = CreateWorkflow(
            client,
            next => activated = (GameDiscoveryWindow)next,
            pickFolder: _ => picker.Task
        );
        ReleaseVerificationWindow releaseWindow = workflow.CreateInitialWindow();
        ReleaseVerificationViewModel release = (ReleaseVerificationViewModel)releaseWindow.DataContext!;
        await VerifyAsync(release);
        release.ContinueCommand.Execute(null);
        await WaitUntilAsync(() => activated is not null);
        GameDiscoveryViewModel discovery = (GameDiscoveryViewModel)activated!.DataContext!;

        discovery.BrowseCommand.Execute(null);
        client.Fail();
        await WaitUntilAsync(() => discovery.Heading == "The verified installer session closed");
        picker.TrySetResult("/games/selected-after-fault");
        await WaitUntilAsync(() => discovery.Heading == "The selected folder could not be checked");

        discovery.Message.Should().Contain("verified installer session").And.NotContain("folder picker");
        discovery.BrowseCommand.CanExecute(null).Should().BeFalse();
        client.ValidatedPath.Should().BeNull();
        await releaseWindow.DisposeAsync();
        await activated.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [AvaloniaTest]
    public async Task ValidGameTransfersOneBoundSessionIntoReadOnlyPlanWindow()
    {
        ProtocolGameCandidate game = new("/games/Stardew Valley", LinuxGameFolderStatus.Valid, "Stardew Valley test installation");
        TrackingClient client = new() { DiscoveredGames = [game] };
        GameDiscoveryWindow? discoveryWindow = null;
        PlanReviewWindow? planWindow = null;
        ProductionInstallerWorkflow workflow = CreateWorkflow(
            client,
            next =>
            {
                if (next is GameDiscoveryWindow discovery)
                    discoveryWindow = discovery;
                else
                    planWindow = next.Should().BeOfType<PlanReviewWindow>().Subject;
            }
        );
        ReleaseVerificationWindow releaseWindow = workflow.CreateInitialWindow();
        ReleaseVerificationViewModel release = (ReleaseVerificationViewModel)releaseWindow.DataContext!;
        await VerifyAsync(release);
        release.ContinueCommand.Execute(null);
        await WaitUntilAsync(() => discoveryWindow is not null);
        GameDiscoveryViewModel discovery = (GameDiscoveryViewModel)discoveryWindow!.DataContext!;
        await discovery.StartAsync();
        discovery.IsContinueVisible.Should().BeTrue();

        discovery.ContinueCommand.Execute(null);
        discovery.ContinueCommand.Execute(null);
        await WaitUntilAsync(() => planWindow is not null);
        PlanReviewViewModel plan = (PlanReviewViewModel)planWindow!.DataContext!;

        discovery.IsContinueVisible.Should().BeFalse();
        plan.OperationChoices.Should().HaveCount(5).And.NotContain(choice => choice.Operation == InstallerOperation.Rollback);
        plan.SelectedOperation.Should().BeNull();
        client.DisposeCalls.Should().Be(0);
        await discoveryWindow.DisposeAsync();
        client.DisposeCalls.Should().Be(0, "the plan window owns the transferred backend session");
        await planWindow.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
        await releaseWindow.DisposeAsync();
    }

    [AvaloniaTest]
    public async Task ValidGameTransfersOneBoundSessionIntoRecoveryHistoryWithoutExecution()
    {
        ProtocolGameCandidate game = new("/games/Stardew Valley", LinuxGameFolderStatus.Valid, "Stardew Valley test installation");
        TrackingClient client = new() { DiscoveredGames = [game], EnableRollback = true };
        GameDiscoveryWindow? discoveryWindow = null;
        PlanReviewWindow? planWindow = null;
        RecoveryPruneWindow? recoveryWindow = null;
        ProductionInstallerWorkflow workflow = CreateWorkflow(
            client,
            next =>
            {
                if (next is GameDiscoveryWindow discovery)
                    discoveryWindow = discovery;
                else if (next is PlanReviewWindow plan)
                    planWindow = plan;
                else
                    recoveryWindow = next.Should().BeOfType<RecoveryPruneWindow>().Subject;
            }
        );
        ReleaseVerificationWindow releaseWindow = workflow.CreateInitialWindow();
        ReleaseVerificationViewModel release = (ReleaseVerificationViewModel)releaseWindow.DataContext!;
        await VerifyAsync(release);
        release.ContinueCommand.Execute(null);
        await WaitUntilAsync(() => discoveryWindow is not null);
        GameDiscoveryViewModel discovery = (GameDiscoveryViewModel)discoveryWindow!.DataContext!;
        await discovery.StartAsync();

        discovery.ManageRecoveriesCommand.Execute(null);
        discovery.ContinueCommand.Execute(null);
        await WaitUntilAsync(() => recoveryWindow is not null);

        planWindow.Should().BeNull("plan and recovery routes share one selected-game transfer gate");
        client.ExecuteCalls.Should().Be(0);
        discovery.IsContinueVisible.Should().BeFalse();
        discovery.IsRecoveryCleanupVisible.Should().BeFalse();
        await discoveryWindow.DisposeAsync();
        client.DisposeCalls.Should().Be(0, "the recovery window owns the transferred backend session");
        await recoveryWindow!.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
        await releaseWindow.DisposeAsync();
    }

    [AvaloniaTest]
    public async Task RecoveryWindowConstructionFailureClosesTransferredAuthorityAndShowsSafeError()
    {
        ProtocolGameCandidate game = new("/games/Stardew Valley", LinuxGameFolderStatus.Valid, "Stardew Valley test installation");
        TrackingClient client = new() { DiscoveredGames = [game] };
        GameDiscoveryWindow? discoveryWindow = null;
        ProductionInstallerWorkflow workflow = CreateWorkflow(
            client,
            next => discoveryWindow = next.Should().BeOfType<GameDiscoveryWindow>().Subject,
            recoveryPruneWindowFactory: _ => throw new InvalidOperationException("synthetic private recovery-window failure")
        );
        ReleaseVerificationWindow releaseWindow = workflow.CreateInitialWindow();
        ReleaseVerificationViewModel release = (ReleaseVerificationViewModel)releaseWindow.DataContext!;
        await VerifyAsync(release);
        release.ContinueCommand.Execute(null);
        await WaitUntilAsync(() => discoveryWindow is not null);
        GameDiscoveryViewModel discovery = (GameDiscoveryViewModel)discoveryWindow!.DataContext!;
        await discovery.StartAsync();

        discovery.ManageRecoveriesCommand.Execute(null);
        await WaitUntilAsync(() => discovery.Heading == "The recovery-history screen could not open" && client.DisposeCalls == 1);

        discovery.Message.Should().Contain("No game files were changed").And.NotContain("synthetic");
        discovery.IsContinueVisible.Should().BeFalse();
        discovery.IsRecoveryCleanupVisible.Should().BeFalse();
        discovery.IsExitVisible.Should().BeTrue();
        client.ExecuteCalls.Should().Be(0);
        await discoveryWindow.DisposeAsync();
        await releaseWindow.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [AvaloniaTest]
    public async Task PartialRecoveryWindowActivationClosesVisibleWindowAndTransferredAuthority()
    {
        ProtocolGameCandidate game = new("/games/Stardew Valley", LinuxGameFolderStatus.Valid, "Stardew Valley test installation");
        TrackingClient client = new() { DiscoveredGames = [game] };
        GameDiscoveryWindow? discoveryWindow = null;
        RecoveryPruneWindow? partiallyActivated = null;
        ProductionInstallerWorkflow workflow = CreateWorkflow(
            client,
            next =>
            {
                if (next is GameDiscoveryWindow discovery)
                    discoveryWindow = discovery;
                else
                {
                    partiallyActivated = next.Should().BeOfType<RecoveryPruneWindow>().Subject;
                    partiallyActivated.Show();
                    throw new InvalidOperationException("synthetic private recovery activation failure");
                }
            }
        );
        ReleaseVerificationWindow releaseWindow = workflow.CreateInitialWindow();
        ReleaseVerificationViewModel release = (ReleaseVerificationViewModel)releaseWindow.DataContext!;
        await VerifyAsync(release);
        release.ContinueCommand.Execute(null);
        await WaitUntilAsync(() => discoveryWindow is not null);
        GameDiscoveryViewModel discovery = (GameDiscoveryViewModel)discoveryWindow!.DataContext!;
        await discovery.StartAsync();

        discovery.ManageRecoveriesCommand.Execute(null);
        await WaitUntilAsync(() => discovery.Heading == "The recovery-history screen could not open" && client.DisposeCalls == 1);

        partiallyActivated.Should().NotBeNull();
        partiallyActivated!.IsVisible.Should().BeFalse();
        discovery.Message.Should().NotContain("synthetic").And.Contain("No game files were changed");
        client.ExecuteCalls.Should().Be(0);
        await discoveryWindow.DisposeAsync();
        await releaseWindow.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [AvaloniaTest]
    public async Task PlanWindowConstructionFailureClosesTransferredAuthorityAndShowsSafeError()
    {
        ProtocolGameCandidate game = new("/games/Stardew Valley", LinuxGameFolderStatus.Valid, "Stardew Valley test installation");
        TrackingClient client = new() { DiscoveredGames = [game] };
        GameDiscoveryWindow? discoveryWindow = null;
        ProductionInstallerWorkflow workflow = CreateWorkflow(
            client,
            next => discoveryWindow = next.Should().BeOfType<GameDiscoveryWindow>().Subject,
            planWindowFactory: _ => throw new InvalidOperationException("synthetic private plan-window failure")
        );
        ReleaseVerificationWindow releaseWindow = workflow.CreateInitialWindow();
        ReleaseVerificationViewModel release = (ReleaseVerificationViewModel)releaseWindow.DataContext!;
        await VerifyAsync(release);
        release.ContinueCommand.Execute(null);
        await WaitUntilAsync(() => discoveryWindow is not null);
        GameDiscoveryViewModel discovery = (GameDiscoveryViewModel)discoveryWindow!.DataContext!;
        await discovery.StartAsync();

        discovery.ContinueCommand.Execute(null);
        await WaitUntilAsync(() => discovery.Heading == "The read-only plan screen could not open" && client.DisposeCalls == 1);

        discovery.Message.Should().Contain("No game files were changed").And.NotContain("synthetic");
        discovery.IsContinueVisible.Should().BeFalse();
        discovery.IsExitVisible.Should().BeTrue();
        await discoveryWindow.DisposeAsync();
        await releaseWindow.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [AvaloniaTest]
    public async Task PartialPlanWindowActivationClosesVisibleWindowAndTransferredAuthority()
    {
        ProtocolGameCandidate game = new("/games/Stardew Valley", LinuxGameFolderStatus.Valid, "Stardew Valley test installation");
        TrackingClient client = new() { DiscoveredGames = [game] };
        GameDiscoveryWindow? discoveryWindow = null;
        PlanReviewWindow? partiallyActivated = null;
        ProductionInstallerWorkflow workflow = CreateWorkflow(
            client,
            next =>
            {
                if (next is GameDiscoveryWindow discovery)
                    discoveryWindow = discovery;
                else
                {
                    partiallyActivated = next.Should().BeOfType<PlanReviewWindow>().Subject;
                    partiallyActivated.Show();
                    throw new InvalidOperationException("synthetic private activation failure");
                }
            }
        );
        ReleaseVerificationWindow releaseWindow = workflow.CreateInitialWindow();
        ReleaseVerificationViewModel release = (ReleaseVerificationViewModel)releaseWindow.DataContext!;
        await VerifyAsync(release);
        release.ContinueCommand.Execute(null);
        await WaitUntilAsync(() => discoveryWindow is not null);
        GameDiscoveryViewModel discovery = (GameDiscoveryViewModel)discoveryWindow!.DataContext!;
        await discovery.StartAsync();

        discovery.ContinueCommand.Execute(null);
        await WaitUntilAsync(() => discovery.Heading == "The read-only plan screen could not open" && client.DisposeCalls == 1);

        partiallyActivated.Should().NotBeNull();
        partiallyActivated!.IsVisible.Should().BeFalse();
        discovery.Message.Should().NotContain("synthetic").And.Contain("No game files were changed");
        discovery.IsExitVisible.Should().BeTrue();
        await discoveryWindow.DisposeAsync();
        await releaseWindow.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [AvaloniaTest]
    public async Task ConfirmedPlanOpensReadyExecutionWindowWithoutRunningUntilExplicitRun()
    {
        TrackingClient client = new() { EnablePlan = true };
        WorkflowContext context = await OpenExecutablePlanAsync(client);
        await context.Plan.ConfirmCommand.ExecuteAsync();
        await WaitUntilAsync(() => context.ExecutionWindow is not null);

        client.ConfirmCalls.Should().Be(1);
        client.ExecuteCalls.Should().Be(0, "confirmation and window activation must not execute");
        ExecutionViewModel execution = (ExecutionViewModel)context.ExecutionWindow!.DataContext!;
        execution.IsReady.Should().BeTrue();
        await execution.RunCommand.ExecuteAsync();

        client.ExecuteCalls.Should().Be(1);
        await WaitUntilAsync(() => execution.Heading.Contains("completed", StringComparison.Ordinal));
        execution.Heading.Should().Contain("completed");
        await context.ExecutionWindow.DisposeAsync();
        await context.PlanWindow.DisposeAsync();
        await context.DiscoveryWindow.DisposeAsync();
        await context.ReleaseWindow.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [AvaloniaTest]
    public async Task ExplicitRollbackSelectionReachesReadyWindowWithoutRunningUntilExplicitRun()
    {
        TrackingClient client = new() { EnableRollback = true };
        WorkflowContext context = await OpenPlanReviewAsync(client);

        context.Plan.RecoveryChoices.Should().BeEmpty();
        context.Plan.SelectedRecoveryChoice.Should().BeNull();
        client.RecoveryListCalls.Should().Be(0, "opening the production plan screen must not load recovery history");
        client.RollbackInspectionCalls.Should().Be(0);
        client.ExecuteCalls.Should().Be(0);

        await context.Plan.LoadRecoveriesCommand.ExecuteAsync();
        context.Plan.RecoveryChoices.Should().ContainSingle();
        context.Plan.SelectedRecoveryChoice.Should().BeNull("listing never chooses a rollback target");
        context.Plan.SelectedRecoveryChoice = context.Plan.RecoveryChoices.Single();
        await context.Plan.InspectRollbackCommand.ExecuteAsync();

        context.Plan.Heading.Should().Be("Rollback plan inspected — preview only");
        context.Plan.ConfirmCommand.CanExecute(null).Should().BeTrue();
        client.RecoveryListCalls.Should().Be(1);
        client.RollbackInspectionCalls.Should().Be(1);
        client.ConfirmCalls.Should().Be(0);
        client.ExecuteCalls.Should().Be(0);

        await context.Plan.ConfirmCommand.ExecuteAsync();
        await WaitUntilAsync(() => context.ExecutionWindow is not null);

        ExecutionViewModel execution = (ExecutionViewModel)context.ExecutionWindow!.DataContext!;
        execution.OperationLabel.Should().Be("Rollback");
        execution.Heading.Should().Be("Ready to run rollback");
        execution.Message.Should().Contain("No files have changed").And.Contain("Run operation");
        client.ConfirmCalls.Should().Be(1);
        client.ExecuteCalls.Should().Be(0, "listing, selection, inspection, confirmation, and window activation must not execute rollback");

        await execution.RunCommand.ExecuteAsync();

        client.ExecuteCalls.Should().Be(1);
        await WaitUntilAsync(() => execution.Heading == "Rollback completed");
        await context.ExecutionWindow.DisposeAsync();
        await context.PlanWindow.DisposeAsync();
        await context.DiscoveryWindow.DisposeAsync();
        await context.ReleaseWindow.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [AvaloniaTest]
    public async Task ExecutionConstructionFailureDisposesOwnerAndLeavesSanitizedPlanExit()
    {
        TrackingClient client = new() { EnablePlan = true };
        WorkflowContext context = await OpenExecutablePlanAsync(
            client,
            executionFactory: _ => throw new InvalidOperationException("private execution factory /home/user")
        );
        await context.Plan.ConfirmCommand.ExecuteAsync();
        await WaitUntilAsync(() => context.Plan.Heading == "The final Run screen could not open");

        client.ExecuteCalls.Should().Be(0);
        client.DisposeCalls.Should().Be(1);
        context.Plan.IsExitVisible.Should().BeTrue();
        context.Plan.Message.Should().Contain("No installer operation started").And.NotContain("private").And.NotContain("/home");
        Dispatcher.UIThread.RunJobs();
        context.PlanWindow.FindControl<Button>("ExitButton")!.IsFocused.Should().BeTrue();
        context.PlanWindow.Close();
        await WaitUntilAsync(() => !context.PlanWindow.IsVisible);
        await context.ReleaseWindow.DisposeAsync();
    }

    [AvaloniaTest]
    public async Task PartialExecutionActivationClosesNewWindowWithZeroExecutionAndKeepsPlanExit()
    {
        TrackingClient client = new() { EnablePlan = true };
        ExecutionWindow? partial = null;
        WorkflowContext context = await OpenExecutablePlanAsync(client, execution =>
        {
            partial = execution;
            execution.Show();
            throw new InvalidOperationException("private partial execution activation");
        });
        await context.Plan.ConfirmCommand.ExecuteAsync();
        await WaitUntilAsync(() => context.Plan.Heading == "The final Run screen could not open" && partial is { IsVisible: false });

        client.ExecuteCalls.Should().Be(0);
        client.DisposeCalls.Should().Be(1);
        context.Plan.IsExitVisible.Should().BeTrue();
        context.Plan.Message.Should().NotContain("private");
        context.PlanWindow.Close();
        await WaitUntilAsync(() => !context.PlanWindow.IsVisible);
        await context.ReleaseWindow.DisposeAsync();
    }

    private static async Task<WorkflowContext> OpenExecutablePlanAsync(
        TrackingClient client,
        Action<ExecutionWindow>? activateExecution = null,
        Func<ExecutionViewModel, ExecutionWindow>? executionFactory = null
    )
    {
        WorkflowContext context = await OpenPlanReviewAsync(client, activateExecution, executionFactory);
        context.Plan.SelectedOperation = context.Plan.OperationChoices.Single(choice => choice.Operation == InstallerOperation.Install);
        await context.Plan.InspectCommand.ExecuteAsync();
        return context;
    }

    private static async Task<WorkflowContext> OpenPlanReviewAsync(
        TrackingClient client,
        Action<ExecutionWindow>? activateExecution = null,
        Func<ExecutionViewModel, ExecutionWindow>? executionFactory = null
    )
    {
        client.DiscoveredGames = [new("/games/Stardew Valley", LinuxGameFolderStatus.Valid, "Stardew Valley test installation")];
        WorkflowContext context = new();
        ProductionInstallerWorkflow workflow = CreateWorkflow(
            client,
            next =>
            {
                if (next is GameDiscoveryWindow discovery)
                    context.DiscoveryWindow = discovery;
                else if (next is PlanReviewWindow plan)
                {
                    context.PlanWindow = plan;
                    plan.Show();
                }
                else
                {
                    context.ExecutionWindow = (ExecutionWindow)next;
                    activateExecution?.Invoke(context.ExecutionWindow);
                }
            },
            executionWindowFactory: executionFactory
        );
        context.ReleaseWindow = workflow.CreateInitialWindow();
        ReleaseVerificationViewModel release = (ReleaseVerificationViewModel)context.ReleaseWindow.DataContext!;
        await VerifyAsync(release);
        release.ContinueCommand.Execute(null);
        await WaitUntilAsync(() => context.DiscoveryWindow is not null);
        GameDiscoveryViewModel discovery = (GameDiscoveryViewModel)context.DiscoveryWindow.DataContext!;
        await discovery.StartAsync();
        discovery.ContinueCommand.Execute(null);
        await WaitUntilAsync(() => context.PlanWindow is not null);
        context.Plan = (PlanReviewViewModel)context.PlanWindow.DataContext!;
        return context;
    }

    private sealed class WorkflowContext
    {
        public ReleaseVerificationWindow ReleaseWindow { get; set; } = null!;
        public GameDiscoveryWindow DiscoveryWindow { get; set; } = null!;
        public PlanReviewWindow PlanWindow { get; set; } = null!;
        public PlanReviewViewModel Plan { get; set; } = null!;
        public ExecutionWindow? ExecutionWindow { get; set; }
    }

    private static ProductionInstallerWorkflow CreateWorkflow(
        TrackingClient client,
        Action<Window> activate,
        Func<GameDiscoveryViewModel, GameDiscoveryWindow>? windowFactory = null,
        Func<GameDiscoveryWindow, Task<string?>>? pickFolder = null,
        Func<PlanReviewViewModel, PlanReviewWindow>? planWindowFactory = null,
        Func<ExecutionViewModel, ExecutionWindow>? executionWindowFactory = null,
        Func<RecoveryPruneViewModel, RecoveryPruneWindow>? recoveryPruneWindowFactory = null
    )
    {
        ReviewedReleaseCandidate candidate = ReleaseVerificationViewModelTests.Candidate();
        ReleaseVerificationViewModelTests.FakeReleaseService service = new([candidate])
        {
            CompletePreparation = true
        };
        return ProductionInstallerWorkflow.CreateWithoutDiagnosticsForTesting(
            service,
            () => client,
            activate,
            windowFactory,
            pickFolder,
            planWindowFactory,
            executionWindowFactory,
            recoveryPruneWindowFactory
        );
    }

    private static async Task VerifyAsync(ReleaseVerificationViewModel viewModel)
    {
        await viewModel.StartAsync();
        await viewModel.DownloadAndVerifyCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();
        viewModel.IsVerifiedVisible.Should().BeTrue();
        viewModel.IsContinueVisible.Should().BeTrue();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The expected production workflow state was not reached.");
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class TrackingClient : IInstallerProtocolClient
    {
        private readonly TaskCompletionSource<InstallerProtocolClientException> Fault = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCalls { get; private set; }
        public int ConfirmCalls { get; private set; }
        public int ExecuteCalls { get; private set; }
        public int RecoveryListCalls { get; private set; }
        public int RollbackInspectionCalls { get; private set; }
        public string? ValidatedPath { get; private set; }
        public IReadOnlyList<ProtocolGameCandidate> DiscoveredGames { get; set; } = [];
        public bool ThrowOnDispose { get; init; }
        public bool EnablePlan { get; init; }
        public bool EnableRollback { get; init; }
        private ProtocolReleaseIdentity? OpenedRelease;
        private InstallerRecoveryPoint? CurrentRecoveryPoint;
        public Task<InstallerProtocolClientException> SessionFaulted => this.Fault.Task;

        public void Fail()
        {
            this.Fault.TrySetResult(new InstallerProtocolClientException("synthetic private session fault"));
        }

        public Task<HandshakeEvent> HandshakeAsync(string clientName, string clientVersion, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new HandshakeEvent(
                ProtocolSessionId.Parse("11111111111111111111111111111111"),
                "1",
                [
                    "verified-local-package",
                    "linux-game-discovery",
                    "linux-game-validation",
                    ProcessInstallerProtocolClient.PlanInspectionCapability,
                    ProcessInstallerProtocolClient.CandidateApprovalCapability,
                    ProcessInstallerProtocolClient.ExactCoreProgressCapability,
                    ProcessInstallerProtocolClient.CancellationCapability,
                    ProcessInstallerProtocolClient.InterruptedRecoveryCapability
                ]
            ));
        }

        public Task<InstallerPackageOpenResult> OpenPackageAsync(InstallerPackageOpenInput package, CancellationToken cancellationToken = default)
        {
            InstallerPackageOpenSuccess success = new(new ProtocolReleaseIdentity(
                ForkReleaseIdentity.RepositoryUrl,
                package.ReleaseTag,
                ForkReleaseIdentity.Parse(package.ReleaseTag).EmbeddedVersion,
                Path.GetFileName(package.PackagePath),
                package.ExpectedSourceCommit,
                new string('b', 40),
                new string('c', 64),
                100,
                "workflow",
                "Release",
                "linux-x64"
            ));
            this.OpenedRelease = success.Release;
            return Task.FromResult<InstallerPackageOpenResult>(success);
        }

        public Task<IReadOnlyList<ProtocolGameCandidate>> DiscoverGamesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(this.DiscoveredGames);

        public Task<ProtocolGameCandidate> ValidateGameAsync(string canonicalPath, CancellationToken cancellationToken = default)
        {
            this.ValidatedPath = canonicalPath;
            return Task.FromResult(new ProtocolGameCandidate(
                canonicalPath,
                LinuxGameFolderStatus.Valid,
                "Stardew Valley test installation"
            ));
        }

        public Task<InstallerReadOnlyPlanResult> InspectPlanAsync(
            string canonicalGamePath,
            InstallerOperation operation,
            CancellationToken cancellationToken = default
        )
        {
            if (!this.EnablePlan)
                throw new AssertionException("The release-to-discovery workflow must not inspect a plan.");
            InstallerPlanRelease release = new(this.OpenedRelease!.Tag, this.OpenedRelease.EmbeddedVersion);
            return Task.FromResult<InstallerReadOnlyPlanResult>(new InstallerReadOnlyPlanSuccess(
                operation,
                ObservedInstallState.NotInstalled,
                null,
                release,
                false,
                [],
                ProtocolRecommendedDefault.Cancel,
                true,
                [],
                [],
                [],
                0
            )
            {
                Confirmation = new InstallerPlanConfirmation()
            });
        }

        public Task<InstallerRecoveryCatalogResult> ListRecoveriesAsync(
            string canonicalGamePath,
            CancellationToken cancellationToken = default
        )
        {
            if (!this.EnableRollback)
                throw new AssertionException("This workflow must not list recovery history.");
            canonicalGamePath.Should().Be("/games/Stardew Valley");
            this.RecoveryListCalls++;
            ProtocolReleaseIdentity release = this.OpenedRelease!;
            InstallerRecoveryPoint point = new(
                1,
                true,
                false,
                InstallerOperation.Update,
                new InstallerRecoveryReleaseTarget(release.Tag, release.EmbeddedVersion)
            );
            this.CurrentRecoveryPoint = point;
            return Task.FromResult<InstallerRecoveryCatalogResult>(new InstallerRecoveryCatalogSuccess([point]));
        }

        public Task<InstallerReadOnlyPlanResult> InspectRollbackAsync(
            string canonicalGamePath,
            InstallerRecoveryPoint recoveryPoint,
            CancellationToken cancellationToken = default
        )
        {
            if (!this.EnableRollback)
                throw new AssertionException("This workflow must not inspect rollback.");
            canonicalGamePath.Should().Be("/games/Stardew Valley");
            recoveryPoint.Should().BeSameAs(this.CurrentRecoveryPoint);
            this.RollbackInspectionCalls++;
            ProtocolReleaseIdentity release = this.OpenedRelease!;
            InstallerPlanRelease exactRelease = new(release.Tag, release.EmbeddedVersion);
            return Task.FromResult<InstallerReadOnlyPlanResult>(new InstallerReadOnlyPlanSuccess(
                InstallerOperation.Rollback,
                ObservedInstallState.KnownUnmodified,
                exactRelease,
                exactRelease,
                false,
                [ProtocolPlanRisk.Rollback],
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
            });
        }

        public Task<InstallerConfirmedPlanAuthority> ConfirmPlanAsync(InstallerPlanConfirmation confirmation, CancellationToken cancellationToken = default)
        {
            this.ConfirmCalls++;
            return Task.FromResult(new InstallerConfirmedPlanAuthority());
        }

        public Task<InstallerExecutionOperation> ExecutePlanAsync(InstallerConfirmedPlanAuthority authority, CancellationToken cancellationToken = default)
        {
            this.ExecuteCalls++;
            Channel<InstallerExecutionProgress> progress = Channel.CreateBounded<InstallerExecutionProgress>(1);
            progress.Writer.TryComplete();
            InstallerExecutionResult result = new InstallerExecutionTerminalResult(
                ProtocolExecutionOutcome.Succeeded,
                ProtocolDurableState.Committed,
                null,
                ProtocolRecoveryDisposition.NotRequired,
                ProtocolNextAction.InspectAgain,
                new(0, null, 0, null, null, null),
                InstallerBackendSettlement.ConfirmedClosed
            );
            return Task.FromResult(new InstallerExecutionOperation(progress.Reader, Task.FromResult(result), () => Task.CompletedTask));
        }

        public ValueTask DisposeAsync()
        {
            this.DisposeCalls++;
            if (this.ThrowOnDispose)
                throw new InvalidOperationException("synthetic private cleanup detail");
            return ValueTask.CompletedTask;
        }
    }
}
