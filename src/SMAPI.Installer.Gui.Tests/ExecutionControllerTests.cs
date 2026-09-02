using System.Threading.Channels;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using FluentAssertions;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Gui.Backend;
using StardewModdingAPI.Installer.Gui.Diagnostics;
using StardewModdingAPI.Installer.Gui.Frontend;
using StardewModdingAPI.Installer.Gui.ViewModels;

namespace StardewModdingAPI.Installer.Gui.Tests;

internal sealed class ExecutionControllerTests
{
    [Test]
    public async Task ExactConfirmedPlanNeverExecutesBeforeExplicitRunAndSynchronousCompletionSettles()
    {
        int cancellationCalls = 0;
        FakeConfirmedSession session = new()
        {
            Execute = _ => Task.FromResult(Operation(Task.FromResult<InstallerExecutionResult>(ExactSuccess()), () =>
            {
                Interlocked.Increment(ref cancellationCalls);
                return Task.CompletedTask;
            }))
        };
        ExecutionController controller = new(session, Plan());

        controller.Snapshot.State.Should().Be(ExecutionState.Ready);
        session.ExecuteCalls.Should().Be(0);

        await controller.RunAsync().WaitAsync(TimeSpan.FromSeconds(2));

        session.ExecuteCalls.Should().Be(1);
        controller.Snapshot.State.Should().Be(ExecutionState.Terminal);
        controller.Snapshot.ExecutionResult.Should().BeEquivalentTo(ExactSuccess());
        session.DisposeCalls.Should().Be(1);
        await controller.DisposeAsync();
        session.DisposeCalls.Should().Be(1);
        cancellationCalls.Should().Be(0, "closing an exact terminal must not request late cancellation");
    }

    [Test]
    public async Task RepeatedCancellationBeforeLateAdmissionRequestsProtocolCancellationExactlyOnceAndExactTerminalWins()
    {
        TaskCompletionSource<InstallerExecutionOperation> publication = NewSource<InstallerExecutionOperation>();
        TaskCompletionSource<InstallerExecutionResult> completion = NewSource<InstallerExecutionResult>();
        int cancellationCalls = 0;
        FakeConfirmedSession session = new() { Execute = _ => publication.Task };
        await using ExecutionController controller = new(session, Plan());
        Task run = controller.RunAsync();
        await WaitUntilAsync(() => session.ExecuteCalls == 1);

        Task first = controller.RequestCancellationAsync();
        Task second = controller.RequestCancellationAsync();
        await Task.WhenAll(first, second);
        InstallerExecutionTerminalResult exact = ExactSuccess();
        publication.SetResult(Operation(completion.Task, () =>
        {
            Interlocked.Increment(ref cancellationCalls);
            throw new InvalidOperationException("private late cancellation transport");
        }));
        await WaitUntilAsync(() => cancellationCalls == 1);
        completion.SetResult(exact);
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        cancellationCalls.Should().Be(1);
        controller.Snapshot.State.Should().Be(ExecutionState.Terminal);
        controller.Snapshot.ExecutionResult.Should().BeSameAs(exact);
    }

    [Test]
    public async Task FaultBeforeAdmissionDisablesRunAndClosesWithoutExecution()
    {
        FakeConfirmedSession session = new();
        await using ExecutionController controller = new(session, Plan());

        session.Fail();
        await WaitUntilAsync(() => controller.Snapshot.State == ExecutionState.PrestartFault);

        controller.Snapshot.CanRun.Should().BeFalse();
        session.ExecuteCalls.Should().Be(0);
        session.DisposeCalls.Should().Be(1);
        await controller.DisposeAsync();
        session.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task ExactTerminalSurvivesFaultedProgressAndThrowingCancellationRequest()
    {
        TaskCompletionSource<InstallerExecutionResult> completion = NewSource<InstallerExecutionResult>();
        Channel<InstallerExecutionProgress> progress = Channel.CreateBounded<InstallerExecutionProgress>(1);
        progress.Writer.TryComplete(new InvalidOperationException("private progress failure /home/user"));
        int cancellationCalls = 0;
        FakeConfirmedSession session = new()
        {
            Execute = _ => Task.FromResult(new InstallerExecutionOperation(
                progress.Reader,
                completion.Task,
                () =>
                {
                    Interlocked.Increment(ref cancellationCalls);
                    throw new InvalidOperationException("private cancellation transport");
                }
            ))
        };
        await using ExecutionController controller = new(session, Plan());
        Task run = controller.RunAsync();
        await WaitUntilAsync(() => controller.Snapshot.State == ExecutionState.Running);
        Func<Task> cancel = controller.RequestCancellationAsync;
        await cancel.Should().ThrowAsync<InvalidOperationException>();
        await cancel.Should().ThrowAsync<InvalidOperationException>();
        cancellationCalls.Should().Be(1);
        controller.Snapshot.State.Should().Be(ExecutionState.CancellationRequested);
        InstallerExecutionTerminalResult exact = ExactSuccess();
        completion.SetResult(exact);

        await run.WaitAsync(TimeSpan.FromSeconds(2));
        controller.Snapshot.ExecutionResult.Should().BeSameAs(exact);
        controller.Snapshot.State.Should().Be(ExecutionState.Terminal);
    }

    [Test]
    public async Task ReentrantCancellationObservesReservedOneShotTaskWithoutDeadlockOrDuplicate()
    {
        TaskCompletionSource<InstallerExecutionResult> completion = NewSource<InstallerExecutionResult>();
        ExecutionController? controller = null;
        Task? reentrant = null;
        int calls = 0;
        FakeConfirmedSession session = new()
        {
            Execute = _ => Task.FromResult(Operation(completion.Task, () =>
            {
                Interlocked.Increment(ref calls);
                ExecutionSnapshot observed = controller!.Snapshot;
                observed.State.Should().Be(ExecutionState.CancellationRequested);
                reentrant = controller.RequestCancellationAsync();
                return Task.CompletedTask;
            }))
        };
        controller = new(session, Plan());
        await using (controller)
        {
            Task run = controller.RunAsync();
            await WaitUntilAsync(() => controller.Snapshot.State == ExecutionState.Running);
            Task outer = controller.RequestCancellationAsync();
            await outer.WaitAsync(TimeSpan.FromSeconds(2));

            reentrant.Should().BeSameAs(outer);
            calls.Should().Be(1);
            completion.SetResult(ExactSuccess());
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Test]
    public async Task ExplicitRecoveryUsesFreshValidatedClientAndCompletesWithExactResultDespiteProgressFault()
    {
        InstallerRecoveryTerminalResult exact = RecoveryForCopy(ProtocolInterruptedRecoveryOutcome.RecoveryCompleted, ProtocolNextAction.InspectAgain);
        RecoveryClient client = new() { Result = exact, FaultProgress = true };
        InstallerPostExecutionRecoveryOwner owner = RecoveryOwner(client);
        FakeConfirmedSession session = new()
        {
            Execute = _ => Task.FromResult(Operation(InterruptedExecution())),
            TakeRecovery = _ => Task.FromResult(owner)
        };
        await using ExecutionController controller = new(session, Plan());
        await controller.RunAsync();

        controller.Snapshot.State.Should().Be(ExecutionState.RecoveryRequired);
        client.RecoveryCalls.Should().Be(0, "recovery is never automatic");
        await controller.RecoverAsync().WaitAsync(TimeSpan.FromSeconds(2));

        client.HandshakeCalls.Should().Be(1);
        client.ValidatedPath.Should().Be("/games/private-target");
        client.RecoveryCalls.Should().Be(1);
        controller.Snapshot.State.Should().Be(ExecutionState.RecoveryCompleted);
        controller.Snapshot.RecoveryResult.Should().BeSameAs(exact);
    }

    [Test]
    public async Task RecoveryPreparationCanBeCancelledAndReturnsToNormalRetryState()
    {
        RecoveryClient client = new() { WaitDuringHandshake = true };
        InstallerPostExecutionRecoveryOwner owner = RecoveryOwner(client);
        FakeConfirmedSession session = new()
        {
            Execute = _ => Task.FromResult(Operation(InterruptedExecution())),
            TakeRecovery = _ => Task.FromResult(owner)
        };
        await using ExecutionController controller = new(session, Plan());
        await controller.RunAsync();
        Task recovery = controller.RecoverAsync();
        await WaitUntilAsync(() => client.HandshakeCalls == 1);

        await controller.RequestCancellationAsync();
        await recovery.WaitAsync(TimeSpan.FromSeconds(2));

        controller.Snapshot.State.Should().Be(ExecutionState.RecoveryRequired);
        controller.Snapshot.CanRecover.Should().BeTrue();
        client.RecoveryCalls.Should().Be(0);

        Task secondRecovery = controller.RecoverAsync();
        await WaitUntilAsync(() => client.HandshakeCalls == 2);
        await controller.RequestCancellationAsync();
        await secondRecovery.WaitAsync(TimeSpan.FromSeconds(2));
        controller.Snapshot.State.Should().Be(ExecutionState.RecoveryRequired);
        client.HandshakeCalls.Should().Be(2, "each retry prepares a fresh authenticated attempt");
    }

    [Test]
    public async Task AdmittedRecoveryCannotBeCancelledAndExactRetryResultWins()
    {
        TaskCompletionSource<InstallerRecoveryResult> completion = NewSource<InstallerRecoveryResult>();
        RecoveryClient client = new() { Completion = completion };
        InstallerPostExecutionRecoveryOwner owner = RecoveryOwner(client);
        FakeConfirmedSession session = new()
        {
            Execute = _ => Task.FromResult(Operation(InterruptedExecution())),
            TakeRecovery = _ => Task.FromResult(owner)
        };
        await using ExecutionController controller = new(session, Plan());
        await controller.RunAsync();
        Task recovery = controller.RecoverAsync();
        await WaitUntilAsync(() => controller.Snapshot.State == ExecutionState.RecoveryRunning);

        await controller.RequestCancellationAsync();
        recovery.IsCompleted.Should().BeFalse();
        InstallerRecoveryTerminalResult partial = RecoveryForCopy(ProtocolInterruptedRecoveryOutcome.PartialFailure, ProtocolNextAction.RecoverInterrupted);
        completion.SetResult(partial);
        await recovery.WaitAsync(TimeSpan.FromSeconds(2));

        controller.Snapshot.State.Should().Be(ExecutionState.RecoveryRequired);
        controller.Snapshot.RecoveryResult.Should().BeSameAs(partial);
        controller.Snapshot.CanRecover.Should().BeTrue("retry gets another fresh authenticated client attempt");
    }

    [Test]
    public async Task DisposeDuringExecutionSettlesRecoveryOwnerMintedAfterCancellationRace()
    {
        TaskCompletionSource<InstallerExecutionResult> completion = NewSource<InstallerExecutionResult>();
        int cancels = 0;
        RecoveryClient client = new();
        InstallerPostExecutionRecoveryOwner owner = RecoveryOwner(client);
        FakeConfirmedSession session = new()
        {
            Execute = _ => Task.FromResult(Operation(completion.Task, () =>
            {
                Interlocked.Increment(ref cancels);
                return Task.CompletedTask;
            })),
            TakeRecovery = _ => Task.FromResult(owner)
        };
        ExecutionController controller = new(session, Plan());
        Task run = controller.RunAsync();
        await WaitUntilAsync(() => controller.Snapshot.State == ExecutionState.Running);

        Task dispose = controller.DisposeAsync().AsTask();
        await WaitUntilAsync(() => cancels == 1);
        completion.SetResult(InterruptedExecution());
        await Task.WhenAll(run, dispose).WaitAsync(TimeSpan.FromSeconds(2));

        Func<Task> recoverDisposed = async () => await owner.RecoverInterruptedAsync();
        await recoverDisposed.Should().ThrowAsync<ObjectDisposedException>();
        session.DisposeCalls.Should().Be(0, "the recovery-owner transfer settles the old confirmed session");
    }

    [Test]
    public async Task ExactTerminalWinsOverConcurrentSessionFaultAfterAdmission()
    {
        TaskCompletionSource<InstallerExecutionResult> completion = NewSource<InstallerExecutionResult>();
        FakeConfirmedSession session = new() { Execute = _ => Task.FromResult(Operation(completion.Task)) };
        await using ExecutionController controller = new(session, Plan());
        Task run = controller.RunAsync();
        await WaitUntilAsync(() => controller.Snapshot.State == ExecutionState.Running);

        session.Fail();
        InstallerExecutionTerminalResult exact = ExactSuccess() with { BackendSettlement = InstallerBackendSettlement.Unconfirmed };
        completion.SetResult(exact);
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        controller.Snapshot.State.Should().Be(ExecutionState.Terminal);
        controller.Snapshot.ExecutionResult.Should().BeSameAs(exact);
    }

    [Test]
    public async Task AlternatingProgressFloodSchedulesOneUiDrainAndNewestTerminalWins()
    {
        List<Action> posted = [];
        FakeConfirmedSession session = new();
        await using ExecutionController controller = new(session, Plan());
        await using ExecutionViewModel viewModel = new(controller, () => false, posted.Add);
        ExecutionSnapshot applying = Snapshot(10, ExecutionState.Running, stage: Core.Transactions.TransactionStage.Applying, completed: 1);
        ExecutionSnapshot rollingBack = Snapshot(10, ExecutionState.Running, stage: Core.Transactions.TransactionStage.RollingBack, completed: 2);

        for (int index = 0; index < 1_000_000; index++)
            viewModel.QueueSnapshotForTesting(index % 2 == 0 ? applying : rollingBack);
        viewModel.QueueSnapshotForTesting(Snapshot(11, ExecutionState.Terminal, execution: ExactSuccess()));

        posted.Should().ContainSingle("a progress flood must retain at most one pending UI callback");
        posted[0]();
        viewModel.IsResultVisible.Should().BeTrue();
        viewModel.Heading.Should().Be("Install completed");

        posted[0]();
        viewModel.QueueSnapshotForTesting(rollingBack);
        posted.Should().ContainSingle("a completed or stale post must be a no-op");
        viewModel.Heading.Should().Be("Install completed");
    }

    [Test]
    public void RejectsMalformedPlanPresentationBeforeItCanReachBindings()
    {
        FakeConfirmedSession session = new();
        ExecutionPlanPresentation invalid = Plan() with
        {
            OperationCounts =
            [
                new(PlanOperationKind.Create, 20_000),
                new(PlanOperationKind.Replace, 1)
            ]
        };

        Action construct = () => _ = new ExecutionController(session, invalid);

        construct.Should().Throw<ArgumentException>();
        session.ExecuteCalls.Should().Be(0);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RollbackPresentationIsImmutableAndNeverExecutesBeforeExplicitRun(bool downgrade)
    {
        List<PlanReviewOperationCount> operationCounts = [new(PlanOperationKind.Restore, 2)];
        List<ProtocolPlanRisk> risks = downgrade
            ? [ProtocolPlanRisk.Rollback, ProtocolPlanRisk.Downgrade]
            : [ProtocolPlanRisk.Rollback];
        ExecutionPlanPresentation source = new(
            InstallerOperation.Rollback,
            operationCounts,
            risks,
            1
        );
        FakeConfirmedSession session = new()
        {
            Execute = _ => Task.FromResult(Operation(ExactSuccess()))
        };
        ExecutionController controller = new(session, source);

        ExecutionSnapshot initial = controller.Snapshot;
        initial.State.Should().Be(ExecutionState.Ready);
        initial.CanRun.Should().BeTrue();
        initial.Plan.Operation.Should().Be(InstallerOperation.Rollback);
        initial.Plan.OperationCounts.Should().Equal(new PlanReviewOperationCount(PlanOperationKind.Restore, 2));
        initial.Plan.Risks.Should().Equal(risks);
        session.ExecuteCalls.Should().Be(0);

        operationCounts[0] = new(PlanOperationKind.Remove, 9);
        risks.Clear();
        initial.Plan.OperationCounts.Should().Equal(new PlanReviewOperationCount(PlanOperationKind.Restore, 2));
        initial.Plan.Risks.Should().Equal(downgrade
            ? [ProtocolPlanRisk.Rollback, ProtocolPlanRisk.Downgrade]
            : [ProtocolPlanRisk.Rollback]);
        Action mutateCounts = () => ((IList<PlanReviewOperationCount>)initial.Plan.OperationCounts)[0] = new(PlanOperationKind.Remove, 1);
        Action mutateRisks = () => ((IList<ProtocolPlanRisk>)initial.Plan.Risks)[0] = ProtocolPlanRisk.Downgrade;
        mutateCounts.Should().Throw<NotSupportedException>();
        mutateRisks.Should().Throw<NotSupportedException>();
        session.ExecuteCalls.Should().Be(0);

        await controller.RunAsync().WaitAsync(TimeSpan.FromSeconds(2));

        session.ExecuteCalls.Should().Be(1);
        controller.Snapshot.State.Should().Be(ExecutionState.Terminal);
        await controller.DisposeAsync();
    }

    [AvaloniaTest]
    public async Task RollbackViewModelKeepsExplicitRunBoundaryAndUsesExactActionCopyThroughoutExecution()
    {
        TaskCompletionSource<InstallerExecutionResult> completion = NewSource<InstallerExecutionResult>();
        Channel<InstallerExecutionProgress> progress = Channel.CreateBounded<InstallerExecutionProgress>(1);
        FakeConfirmedSession session = new()
        {
            Execute = _ => Task.FromResult(new InstallerExecutionOperation(
                progress.Reader,
                completion.Task,
                () => Task.CompletedTask
            ))
        };
        ExecutionController controller = new(session, RollbackPlan());
        await using ExecutionViewModel viewModel = new(controller, () => true, action => action());
        await using ExecutionWindow window = new(viewModel);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Button runButton = window.FindControl<Button>("RunButton")!;

        viewModel.OperationLabel.Should().Be("Rollback");
        viewModel.Heading.Should().Be("Ready to run rollback");
        viewModel.Message.Should().Contain("No files have changed");
        viewModel.Message.Should().Contain("Choose Run rollback");
        viewModel.Message.Should().Contain("restore the selected previous managed state");
        viewModel.PlanDetail.Should().Contain("Confirmed operation: Rollback");
        viewModel.BoundaryDetail.Should().Contain("Nothing runs until you choose Run rollback");
        runButton.Content.Should().Be("_Run rollback");
        AutomationProperties.GetName(runButton).Should().Be("Run the exact confirmed rollback");
        viewModel.RunCommand.CanExecute(null).Should().BeTrue();
        session.ExecuteCalls.Should().Be(0);

        Task run = viewModel.RunCommand.ExecuteAsync();
        await WaitUntilAsync(() => controller.Snapshot.State == ExecutionState.Running);

        session.ExecuteCalls.Should().Be(1);
        viewModel.Heading.Should().Be("Rollback is running");
        viewModel.Message.Should().Contain("Restoring the selected previous managed state");

        progress.Writer.TryWrite(new InstallerExecutionProgress(Core.Transactions.TransactionStage.Applying, 1, 2)).Should().BeTrue();
        await WaitUntilAsync(() => viewModel.ProgressDetail.Contains("Applying planned changes", StringComparison.Ordinal));
        viewModel.ProgressDetail.Should().Contain("1 of 2 units reported");

        progress.Writer.TryComplete().Should().BeTrue();
        completion.SetResult(ExactSuccess());
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        viewModel.Heading.Should().Be("Rollback completed");
        viewModel.Message.Should().Contain("selected previous managed state was restored and committed");
        controller.Snapshot.State.Should().Be(ExecutionState.Terminal);
        session.ExecuteCalls.Should().Be(1);
    }

    [Test]
    public async Task DiagnosticAdmissionFailure_PreventsExecutionBeforeBackendMutation()
    {
        FakeConfirmedSession session = new()
        {
            Execute = _ => throw new AssertionException("Execution must not be called when diagnostic admission fails.")
        };
        await using ExecutionController controller = new(session, Plan());
        int admissionChecks = 0;
        await using ExecutionViewModel viewModel = new(
            controller,
            ensureDiagnosticLoggingReady: () =>
            {
                admissionChecks++;
                throw new InstallerDiagnosticsUnavailableException();
            }
        );

        await viewModel.RunCommand.ExecuteAsync();

        admissionChecks.Should().Be(1);
        session.ExecuteCalls.Should().Be(0);
        controller.Snapshot.State.Should().Be(ExecutionState.Ready);
        viewModel.Heading.Should().Be("Private diagnostic logging is unavailable");
        viewModel.Message.Should().Contain("No operation was started");
    }

    private static IEnumerable<TestCaseData> InvalidRollbackRiskCases()
    {
        yield return new TestCaseData(Array.Empty<ProtocolPlanRisk>()).SetName("RollbackRisksRejectMissingRollback");
        yield return new TestCaseData(new[] { ProtocolPlanRisk.Downgrade }).SetName("RollbackRisksRejectDowngradeAlone");
        yield return new TestCaseData(new[] { ProtocolPlanRisk.Rollback, ProtocolPlanRisk.Rollback }).SetName("RollbackRisksRejectDuplicateRollback");
        yield return new TestCaseData(new[] { ProtocolPlanRisk.Downgrade, ProtocolPlanRisk.Rollback }).SetName("RollbackRisksRejectWrongOrder");
        yield return new TestCaseData(new[] { ProtocolPlanRisk.Rollback, ProtocolPlanRisk.Downgrade, ProtocolPlanRisk.Downgrade }).SetName("RollbackRisksRejectDuplicateDowngrade");
        yield return new TestCaseData(new[] { ProtocolPlanRisk.Rollback, ProtocolPlanRisk.ModifiedOrUnknownFileApproval }).SetName("RollbackRisksRejectCandidateApproval");
        yield return new TestCaseData(new[] { ProtocolPlanRisk.Rollback, ProtocolPlanRisk.Uninstall }).SetName("RollbackRisksRejectUninstall");
        yield return new TestCaseData(new[] { ProtocolPlanRisk.Rollback, ProtocolPlanRisk.Downgrade, ProtocolPlanRisk.ModifiedOrUnknownFileApproval }).SetName("RollbackRisksRejectExtraRisk");
        yield return new TestCaseData(new[] { ProtocolPlanRisk.Rollback, ProtocolPlanRisk.RecoveryPrune }).SetName("RollbackRisksRejectRecoveryPrune");
        yield return new TestCaseData(new[] { ProtocolPlanRisk.Rollback, (ProtocolPlanRisk)int.MaxValue }).SetName("RollbackRisksRejectUnknownRisk");
    }

    [TestCaseSource(nameof(InvalidRollbackRiskCases))]
    public void RejectsEveryMalformedRollbackRiskSequenceBeforeItCanExecute(ProtocolPlanRisk[] risks)
    {
        FakeConfirmedSession session = new();
        ExecutionPlanPresentation invalid = new(
            InstallerOperation.Rollback,
            [new(PlanOperationKind.Restore, 1)],
            risks,
            0
        );

        Action construct = () => _ = new ExecutionController(session, invalid);

        construct.Should().Throw<ArgumentException>();
        session.ExecuteCalls.Should().Be(0);
        session.DisposeCalls.Should().Be(0, "rejected presentation never transfers controller ownership");
    }

    [TestCase(InstallerOperation.Install)]
    [TestCase(InstallerOperation.Update)]
    [TestCase(InstallerOperation.Repair)]
    [TestCase(InstallerOperation.Uninstall)]
    [TestCase(InstallerOperation.Backup)]
    public void RejectsRollbackRiskForEveryNonRollbackOperation(InstallerOperation operation)
    {
        FakeConfirmedSession session = new();
        ExecutionPlanPresentation invalid = new(
            operation,
            [new(PlanOperationKind.Restore, 1)],
            [ProtocolPlanRisk.Rollback],
            0
        );

        Action construct = () => _ = new ExecutionController(session, invalid);

        construct.Should().Throw<ArgumentException>();
        session.ExecuteCalls.Should().Be(0);
    }

    [TestCase(ProtocolExecutionOutcome.Succeeded, "Install completed")]
    [TestCase(ProtocolExecutionOutcome.SucceededWithCleanupWarning, "cleanup is pending")]
    [TestCase(ProtocolExecutionOutcome.FailedBeforeMutation, "failed before changing files")]
    [TestCase(ProtocolExecutionOutcome.CancelledBeforeMutation, "cancelled before changing files")]
    [TestCase(ProtocolExecutionOutcome.CancelledAndRolledBack, "changes were rolled back")]
    [TestCase(ProtocolExecutionOutcome.FailedAndRolledBack, "failed and changes were rolled back")]
    [TestCase(ProtocolExecutionOutcome.AutomaticRecoveryCompletedFreshInspectionRequired, "inspect again")]
    public async Task ViewModelUsesExactTypedCopyForEveryNonRecoveryTerminal(ProtocolExecutionOutcome outcome, string heading)
    {
        FakeConfirmedSession session = new();
        await using ExecutionController controller = new(session, Plan());
        await using ExecutionViewModel viewModel = new(controller);
        InstallerExecutionTerminalResult result = TerminalForCopy(outcome);

        viewModel.ApplySnapshotForTesting(Snapshot(10, ExecutionState.Terminal, execution: result));

        viewModel.Heading.Should().ContainEquivalentOf(heading);
        viewModel.Heading.Should().NotContain("/home").And.NotContain("digest").And.NotContain("private");
        viewModel.ResultRows.Should().OnlyContain(row => !row.AccessibleName.Contains("/home", StringComparison.Ordinal));
    }

    [TestCase(ProtocolInterruptedRecoveryOutcome.RecoveryCompleted, ProtocolNextAction.InspectAgain, "inspect again")]
    [TestCase(ProtocolInterruptedRecoveryOutcome.RecoveryCompleted, ProtocolNextAction.SelectGameFolder, "select a game folder")]
    [TestCase(ProtocolInterruptedRecoveryOutcome.CancelledBeforeRecovery, ProtocolNextAction.RecoverInterrupted, "did not begin")]
    [TestCase(ProtocolInterruptedRecoveryOutcome.PartialFailure, ProtocolNextAction.RecoverInterrupted, "incomplete")]
    [TestCase(ProtocolInterruptedRecoveryOutcome.UnexpectedFailure, ProtocolNextAction.RecoverInterrupted, "could not be confirmed")]
    public async Task ViewModelUsesExactTypedRecoveryOutcomeAndNextAction(
        ProtocolInterruptedRecoveryOutcome outcome,
        ProtocolNextAction nextAction,
        string heading
    )
    {
        FakeConfirmedSession session = new();
        await using ExecutionController controller = new(session, Plan());
        await using ExecutionViewModel viewModel = new(controller);
        InstallerRecoveryTerminalResult result = RecoveryForCopy(outcome, nextAction);
        ExecutionState state = outcome == ProtocolInterruptedRecoveryOutcome.RecoveryCompleted
            ? ExecutionState.RecoveryCompleted
            : ExecutionState.RecoveryRequired;

        viewModel.ApplySnapshotForTesting(Snapshot(10, state, recovery: result, canRecover: state == ExecutionState.RecoveryRequired));

        viewModel.Heading.Should().ContainEquivalentOf(heading);
        viewModel.Message.Should().NotContain("private").And.NotContain("/home");
    }

    [Test]
    public async Task UnknownProgressIsIndeterminateAndKnownProgressIsClampedWithoutAnnouncingCounts()
    {
        FakeConfirmedSession session = new();
        await using ExecutionController controller = new(session, Plan());
        await using ExecutionViewModel viewModel = new(controller);

        viewModel.ApplySnapshotForTesting(Snapshot(10, ExecutionState.Running, stage: Core.Transactions.TransactionStage.Staging, completed: 5));
        string firstAnnouncement = viewModel.StageAnnouncement;
        viewModel.IsProgressIndeterminate.Should().BeTrue();
        viewModel.ApplySnapshotForTesting(Snapshot(11, ExecutionState.Running, stage: Core.Transactions.TransactionStage.Staging, completed: 11, total: 10));

        viewModel.ProgressValue.Should().Be(10);
        viewModel.StageAnnouncement.Should().Be(firstAnnouncement, "count-only updates aren't live-announced");
        viewModel.ProgressDetail.Should().Contain("10 of 10");
    }

    [AvaloniaTest]
    public async Task WindowStartsOnCancelHasUniqueKeysAndResponsiveMarginsWithoutAutoRun()
    {
        FakeConfirmedSession session = new();
        await using ExecutionController controller = new(session, Plan());
        await using ExecutionViewModel viewModel = new(controller);
        await using ExecutionWindow window = new(viewModel);

        window.Show();
        Dispatcher.UIThread.RunJobs();
        Button cancel = window.FindControl<Button>("CancelButton")!;
        Button run = window.FindControl<Button>("RunButton")!;
        Button recover = window.FindControl<Button>("RecoverButton")!;

        cancel.IsFocused.Should().BeTrue();
        cancel.TabIndex.Should().BeLessThan(run.TabIndex);
        AutomationProperties.GetAccessKey(cancel).Should().Be("Alt+C");
        AutomationProperties.GetAccessKey(run).Should().Be("Alt+R");
        AutomationProperties.GetAccessKey(recover).Should().Be("Alt+V");
        session.ExecuteCalls.Should().Be(0);
        window.MinWidth.Should().Be(420);
        window.ApplyResponsiveLayout(420);
        window.IsNarrowLayout.Should().BeTrue();
        window.ApplyResponsiveLayout(620);
        window.IsNarrowLayout.Should().BeFalse();
        window.ApplyResponsiveLayout(980);
        window.IsNarrowLayout.Should().BeFalse();
        window.Close();
    }

    [AvaloniaTest]
    [TestCase(1.00)]
    [TestCase(1.25)]
    [TestCase(1.50)]
    [TestCase(2.00)]
    public async Task MaximalTerminalRendersAcrossDesktopScaleWithoutHorizontalOverflow(double scale)
    {
        const double PhysicalViewportWidth = 840;
        const double PhysicalViewportHeight = 1400;
        InstallerExecutionTerminalResult maximal = ExactSuccess() with
        {
            Summary = new(12, 11, 10, 9, 8, 7),
            BackendSettlement = InstallerBackendSettlement.Unconfirmed
        };
        FakeConfirmedSession session = new();
        await using ExecutionController controller = new(session, Plan());
        await using ExecutionViewModel viewModel = new(controller);
        viewModel.ApplySnapshotForTesting(Snapshot(10, ExecutionState.Terminal, execution: maximal));
        await using ExecutionWindow window = new(viewModel)
        {
            Width = PhysicalViewportWidth / scale,
            Height = PhysicalViewportHeight / scale
        };
        window.ApplyResponsiveLayout(window.Width);

        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        ScrollViewer scroll = window.FindControl<ScrollViewer>("PageScrollViewer")!;
        WrapPanel actions = window.FindControl<WrapPanel>("ActionPanel")!;
        Button exit = window.FindControl<Button>("ExitButton")!;
        TextBlock warning = window.FindControl<TextBlock>("SettlementWarningText")!;
        scroll.HorizontalScrollBarVisibility.Should().Be(ScrollBarVisibility.Disabled);
        scroll.Extent.Width.Should().BeLessThanOrEqualTo(scroll.Viewport.Width + 1);
        viewModel.ResultRows.Should().HaveCount(9);
        viewModel.IsSettlementWarningVisible.Should().BeTrue();
        exit.IsVisible.Should().BeTrue();
        exit.Bounds.Width.Should().BeGreaterThan(0);
        exit.Bounds.Right.Should().BeLessThanOrEqualTo(actions.Bounds.Width + 1);
        warning.Bounds.Width.Should().BeLessThanOrEqualTo(scroll.Viewport.Width + 1);
        warning.Bounds.Height.Should().BeGreaterThan(24, "the safety warning should wrap instead of expanding the page horizontally");
        window.CaptureRenderedFrame().Should().NotBeNull();

        window.Close();
        await WaitUntilUiAsync(() => !window.IsVisible);
    }

    [AvaloniaTest]
    public async Task PhysicalRunAndEscapeCloseContractNeverAutoRunsAndCancelsExactlyOnce()
    {
        TaskCompletionSource<InstallerExecutionResult> completion = NewSource<InstallerExecutionResult>();
        int cancels = 0;
        FakeConfirmedSession session = new()
        {
            Execute = _ => Task.FromResult(Operation(completion.Task, () =>
            {
                Interlocked.Increment(ref cancels);
                return Task.CompletedTask;
            }))
        };
        ExecutionController controller = new(session, Plan());
        ExecutionViewModel viewModel = new(controller);
        ExecutionWindow window = new(viewModel);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        session.ExecuteCalls.Should().Be(0);

        PressAccessKey(window, PhysicalKey.R);
        await WaitUntilAsync(() => controller.Snapshot.State == ExecutionState.Running);
        window.IsVisible.Should().BeTrue();
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await WaitUntilAsync(() => controller.Snapshot.State == ExecutionState.CancellationRequested);
        cancels.Should().Be(1);
        window.IsVisible.Should().BeTrue("an admitted operation must remain visible until exact completion");

        completion.SetResult(ExactSuccess());
        await WaitUntilAsync(() => controller.Snapshot.State == ExecutionState.Terminal);
        Dispatcher.UIThread.RunJobs();
        window.Close();
        await WaitUntilUiAsync(() => !window.IsVisible);
        session.ExecuteCalls.Should().Be(1);
        session.DisposeCalls.Should().Be(1);
    }

    [AvaloniaTest]
    public async Task WindowManagerCloseDuringRunningRequestsOneCancelAndWaitsForTerminal()
    {
        TaskCompletionSource<InstallerExecutionResult> completion = NewSource<InstallerExecutionResult>();
        int cancels = 0;
        FakeConfirmedSession session = new()
        {
            Execute = _ => Task.FromResult(Operation(completion.Task, () =>
            {
                Interlocked.Increment(ref cancels);
                return Task.CompletedTask;
            }))
        };
        ExecutionController controller = new(session, Plan());
        ExecutionViewModel viewModel = new(controller);
        ExecutionWindow window = new(viewModel);
        window.Show();
        PressAccessKey(window, PhysicalKey.R);
        await WaitUntilAsync(() => controller.Snapshot.State == ExecutionState.Running);

        window.Close();
        window.Close();
        await WaitUntilAsync(() => cancels == 1);
        window.IsVisible.Should().BeTrue();
        completion.SetResult(ExactSuccess());
        await WaitUntilAsync(() => controller.Snapshot.State == ExecutionState.Terminal);
        Dispatcher.UIThread.RunJobs();
        window.Close();
        await WaitUntilUiAsync(() => !window.IsVisible);
    }

    [AvaloniaTest]
    public async Task PrecompletedSessionFaultFocusesVisibleExitInsteadOfHiddenCancel()
    {
        FakeConfirmedSession session = new();
        session.Fail();
        ExecutionController controller = new(session, Plan());
        await WaitUntilAsync(() => controller.Snapshot.State == ExecutionState.PrestartFault);
        ExecutionViewModel viewModel = new(controller);
        ExecutionWindow window = new(viewModel);

        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.FindControl<Button>("ExitButton")!.IsFocused.Should().BeTrue();
        window.FindControl<Button>("CancelButton")!.IsVisible.Should().BeFalse();
        session.ExecuteCalls.Should().Be(0);
        PressClosingAccessKey(window, PhysicalKey.X);
        await WaitUntilUiAsync(() => !window.IsVisible);
    }

    [AvaloniaTest]
    public async Task PhysicalRecoveryMnemonicStartsFreshRecoveryAndAdmittedEscapeIsBlocked()
    {
        TaskCompletionSource<InstallerRecoveryResult> completion = NewSource<InstallerRecoveryResult>();
        RecoveryClient client = new() { Completion = completion };
        InstallerPostExecutionRecoveryOwner owner = RecoveryOwner(client);
        FakeConfirmedSession session = new()
        {
            Execute = _ => Task.FromResult(Operation(InterruptedExecution())),
            TakeRecovery = _ => Task.FromResult(owner)
        };
        ExecutionController controller = new(session, Plan());
        await controller.RunAsync();
        ExecutionViewModel viewModel = new(controller);
        ExecutionWindow window = new(viewModel);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.FindControl<Button>("CancelButton")!.IsFocused.Should().BeTrue("recovery is never the default action");

        PressAccessKey(window, PhysicalKey.V);
        await WaitUntilAsync(() => controller.Snapshot.State == ExecutionState.RecoveryRunning);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.IsVisible.Should().BeTrue();
        controller.Snapshot.State.Should().Be(ExecutionState.RecoveryRunning);

        completion.SetResult(RecoveryForCopy(ProtocolInterruptedRecoveryOutcome.RecoveryCompleted, ProtocolNextAction.InspectAgain));
        await WaitUntilAsync(() => controller.Snapshot.State == ExecutionState.RecoveryCompleted);
        await WaitUntilUiAsync(() => viewModel.IsResultVisible);
        window.Close();
        await WaitUntilUiAsync(() => !window.IsVisible);
        client.RecoveryCalls.Should().Be(1);
    }

    [Test]
    public async Task LiveRegionsAreMutuallyExclusiveAndFailuresAreAssertive()
    {
        FakeConfirmedSession session = new();
        await using ExecutionController controller = new(session, Plan());
        await using ExecutionViewModel viewModel = new(controller);
        InstallerExecutionTerminalResult failure = TerminalForCopy(ProtocolExecutionOutcome.FailedBeforeMutation);

        viewModel.ApplySnapshotForTesting(Snapshot(10, ExecutionState.Terminal, execution: failure));
        viewModel.StatusLiveSetting.Should().Be(AutomationLiveSetting.Off);
        viewModel.ResultLiveSetting.Should().Be(AutomationLiveSetting.Assertive);
        viewModel.IsProblemVisible.Should().BeFalse();

        viewModel.ApplySnapshotForTesting(Snapshot(11, ExecutionState.RecoveryRequired, execution: new InstallerExecutionStateUnknownResult(), canRecover: true));
        viewModel.StatusLiveSetting.Should().Be(AutomationLiveSetting.Off);
        viewModel.IsProblemVisible.Should().BeTrue();
        viewModel.IsResultVisible.Should().BeFalse();
    }

    [AvaloniaTest]
    public async Task AutomationHasOneVisibleLiveRegionPerStateAndNoPrivateAuthorityOrPath()
    {
        FakeConfirmedSession session = new();
        await using ExecutionController controller = new(session, Plan());
        await using ExecutionViewModel viewModel = new(controller);
        await using ExecutionWindow window = new(viewModel);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Border status = window.FindControl<Border>("StatusRegion")!;
        Border problem = window.FindControl<Border>("ProblemRegion")!;
        Border result = window.FindControl<Border>("ResultRegion")!;
        Border stage = window.FindControl<Border>("StageLiveRegion")!;

        CountVisibleLive(status, problem, result, stage).Should().Be(1);
        viewModel.ApplySnapshotForTesting(Snapshot(10, ExecutionState.Terminal, execution: TerminalForCopy(ProtocolExecutionOutcome.FailedBeforeMutation)));
        Dispatcher.UIThread.RunJobs();
        CountVisibleLive(status, problem, result, stage).Should().Be(1);
        ControlAutomationPeer.CreatePeerForElement(result).GetLiveSetting().Should().Be(AutomationLiveSetting.Assertive);
        viewModel.ApplySnapshotForTesting(Snapshot(11, ExecutionState.RecoveryRequired, execution: new InstallerExecutionStateUnknownResult(), canRecover: true));
        Dispatcher.UIThread.RunJobs();
        CountVisibleLive(status, problem, result, stage).Should().Be(1);
        ControlAutomationPeer.CreatePeerForElement(problem).GetLiveSetting().Should().Be(AutomationLiveSetting.Assertive);

        typeof(ExecutionViewModel).GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Should().NotContain(property =>
                property.Name.Contains("Session", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Owner", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Authority", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase)
                || typeof(IConfirmedInstallerSession).IsAssignableFrom(property.PropertyType)
                || typeof(InstallerPostExecutionRecoveryOwner).IsAssignableFrom(property.PropertyType)
            );
        foreach (Control control in new Control[] { status, problem, result, stage, window.FindControl<Button>("RecoverButton")! })
        {
            string name = ControlAutomationPeer.CreatePeerForElement(control).GetName();
            name.Should().NotContain("/games/private-target").And.NotContain("/home").And.NotContain("digest").And.NotContain("private backend");
        }
    }

    [AvaloniaTest]
    public async Task UnconfirmedSettlementIsAnnouncedByTheFocusedTerminalResultRegion()
    {
        InstallerExecutionTerminalResult terminal = ExactSuccess() with { BackendSettlement = InstallerBackendSettlement.Unconfirmed };
        FakeConfirmedSession session = new() { Execute = _ => Task.FromResult(Operation(terminal)) };
        ExecutionController controller = new(session, Plan());
        ExecutionViewModel viewModel = new(controller);
        ExecutionWindow window = new(viewModel);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        PressAccessKey(window, PhysicalKey.R);
        await WaitUntilUiAsync(() => viewModel.IsResultVisible);

        Border status = window.FindControl<Border>("StatusRegion")!;
        Border problem = window.FindControl<Border>("ProblemRegion")!;
        Border result = window.FindControl<Border>("ResultRegion")!;
        Border stage = window.FindControl<Border>("StageLiveRegion")!;
        AutomationPeer resultPeer = ControlAutomationPeer.CreatePeerForElement(result);
        result.IsFocused.Should().BeTrue();
        resultPeer.GetLiveSetting().Should().Be(AutomationLiveSetting.Polite);
        resultPeer.GetName().Should().Contain(viewModel.SettlementWarning);
        CountVisibleLive(status, problem, result, stage).Should().Be(1);

        window.Close();
        await WaitUntilUiAsync(() => !window.IsVisible);
    }

    private static ExecutionPlanPresentation Plan() => new(
        InstallerOperation.Install,
        [new(PlanOperationKind.Create, 2)],
        [],
        0
    );

    private static ExecutionPlanPresentation RollbackPlan() => new(
        InstallerOperation.Rollback,
        [new(PlanOperationKind.Restore, 2)],
        [ProtocolPlanRisk.Rollback],
        0
    );

    private static int CountVisibleLive(params Control[] controls)
        => controls.Count(control => control.IsVisible && ControlAutomationPeer.CreatePeerForElement(control).GetLiveSetting() != AutomationLiveSetting.Off);

    private static InstallerExecutionTerminalResult ExactSuccess() => new(
        ProtocolExecutionOutcome.Succeeded,
        ProtocolDurableState.Committed,
        null,
        ProtocolRecoveryDisposition.NotRequired,
        ProtocolNextAction.InspectAgain,
        new(2, null, 1, null, null, null),
        InstallerBackendSettlement.ConfirmedClosed
    );

    private static InstallerExecutionTerminalResult InterruptedExecution() => new(
        ProtocolExecutionOutcome.InterruptedRecoveryRequired,
        ProtocolDurableState.RecoveryRequired,
        ProtocolTerminalErrorCode.IoFailure,
        ProtocolRecoveryDisposition.InterruptedRecoveryRequired,
        ProtocolNextAction.RecoverInterrupted,
        new(null, null, null, null, null, null),
        InstallerBackendSettlement.ConfirmedClosed
    );

    private static InstallerPostExecutionRecoveryOwner RecoveryOwner(RecoveryClient client)
    {
        InstallerPostExecutionRecoveryOwner owner = new(() => client, "/games/private-target", GameDiscoveryControllerTests.Release());
        owner.AttachPriorBackendSettlement(Task.CompletedTask);
        return owner;
    }

    private static InstallerExecutionTerminalResult TerminalForCopy(ProtocolExecutionOutcome outcome)
    {
        (ProtocolDurableState durable, ProtocolTerminalErrorCode? error, ProtocolRecoveryDisposition recovery, ProtocolNextAction next) = outcome switch
        {
            ProtocolExecutionOutcome.Succeeded => (ProtocolDurableState.Committed, (ProtocolTerminalErrorCode?)null, ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.InspectAgain),
            ProtocolExecutionOutcome.SucceededWithCleanupWarning => (ProtocolDurableState.Committed, (ProtocolTerminalErrorCode?)null, ProtocolRecoveryDisposition.CleanupPending, ProtocolNextAction.InspectAgain),
            ProtocolExecutionOutcome.FailedBeforeMutation => (ProtocolDurableState.Unchanged, ProtocolTerminalErrorCode.PermissionDenied, ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.ReviewFilesystem),
            ProtocolExecutionOutcome.CancelledBeforeMutation => (ProtocolDurableState.Unchanged, (ProtocolTerminalErrorCode?)null, ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.InspectAgain),
            ProtocolExecutionOutcome.CancelledAndRolledBack => (ProtocolDurableState.RolledBack, (ProtocolTerminalErrorCode?)null, ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.InspectAgain),
            ProtocolExecutionOutcome.FailedAndRolledBack => (ProtocolDurableState.RolledBack, ProtocolTerminalErrorCode.IoFailure, ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.InspectAgain),
            ProtocolExecutionOutcome.AutomaticRecoveryCompletedFreshInspectionRequired => (ProtocolDurableState.RecoveryCompleted, (ProtocolTerminalErrorCode?)null, ProtocolRecoveryDisposition.Completed, ProtocolNextAction.InspectAgain),
            _ => (ProtocolDurableState.Unknown, ProtocolTerminalErrorCode.UnexpectedCoreFailure, ProtocolRecoveryDisposition.InterruptedRecoveryRequired, ProtocolNextAction.RecoverInterrupted)
        };
        return new(outcome, durable, error, recovery, next, new(null, null, null, null, null, null), InstallerBackendSettlement.ConfirmedClosed);
    }

    private static InstallerRecoveryTerminalResult RecoveryForCopy(ProtocolInterruptedRecoveryOutcome outcome, ProtocolNextAction next)
    {
        return outcome switch
        {
            ProtocolInterruptedRecoveryOutcome.RecoveryCompleted => new(outcome, ProtocolDurableState.RecoveryCompleted, null, ProtocolRecoveryDisposition.Completed, next, new(true, next == ProtocolNextAction.InspectAgain, 1, 2), InstallerBackendSettlement.ConfirmedClosed),
            ProtocolInterruptedRecoveryOutcome.CancelledBeforeRecovery => new(outcome, ProtocolDurableState.Unchanged, null, ProtocolRecoveryDisposition.InterruptedRecoveryRequired, next, null, InstallerBackendSettlement.ConfirmedClosed),
            ProtocolInterruptedRecoveryOutcome.PartialFailure => new(outcome, ProtocolDurableState.RecoveryRequired, ProtocolTerminalErrorCode.RecoveryFailed, ProtocolRecoveryDisposition.InterruptedRecoveryRequired, next, new(false, true, 1, 2), InstallerBackendSettlement.ConfirmedClosed),
            _ => new(outcome, ProtocolDurableState.Unknown, ProtocolTerminalErrorCode.UnexpectedCoreFailure, ProtocolRecoveryDisposition.InterruptedRecoveryRequired, next, null, InstallerBackendSettlement.Unconfirmed)
        };
    }

    private static ExecutionSnapshot Snapshot(
        long revision,
        ExecutionState state,
        InstallerExecutionResult? execution = null,
        InstallerRecoveryResult? recovery = null,
        Core.Transactions.TransactionStage? stage = null,
        int completed = 0,
        int? total = null,
        bool canRecover = false
    ) => new(revision, state, Plan(), stage, completed, total, execution, recovery, state == ExecutionState.Ready, state is ExecutionState.Starting or ExecutionState.Running, canRecover, state is ExecutionState.Terminal or ExecutionState.RecoveryRequired or ExecutionState.RecoveryCompleted or ExecutionState.PrestartFault or ExecutionState.CancelledBeforeStart);

    private static InstallerExecutionOperation Operation(InstallerExecutionResult result)
        => Operation(Task.FromResult(result));

    private static InstallerExecutionOperation Operation(Task<InstallerExecutionResult> completion, Func<Task>? cancel = null)
    {
        Channel<InstallerExecutionProgress> progress = Channel.CreateBounded<InstallerExecutionProgress>(1);
        progress.Writer.TryComplete();
        return new(progress.Reader, completion, cancel ?? (() => Task.CompletedTask));
    }

    private static TaskCompletionSource<T> NewSource<T>()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The expected execution state was not reached.");
            await Task.Delay(10);
        }
    }

    private static void PressAccessKey(ExecutionWindow window, PhysicalKey key)
    {
        window.KeyPressQwerty(PhysicalKey.AltLeft, RawInputModifiers.None);
        window.KeyPressQwerty(key, RawInputModifiers.Alt);
        window.KeyReleaseQwerty(key, RawInputModifiers.Alt);
        window.KeyReleaseQwerty(PhysicalKey.AltLeft, RawInputModifiers.None);
    }

    private static void PressClosingAccessKey(ExecutionWindow window, PhysicalKey key)
    {
        window.KeyPressQwerty(PhysicalKey.AltLeft, RawInputModifiers.None);
        window.KeyPressQwerty(key, RawInputModifiers.Alt);
    }

    private static async Task WaitUntilUiAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The expected execution-window state was not reached.");
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class FakeConfirmedSession : IConfirmedInstallerSession
    {
        private readonly TaskCompletionSource<InstallerProtocolClientException> Fault = NewSource<InstallerProtocolClientException>();
        private int executeCalls;
        private int disposeCalls;

        public ProtocolReleaseIdentity Release { get; } = GameDiscoveryControllerTests.Release();
        public VerifiedGamePresentation Game { get; } = new("/games/private-target", "Stardew Valley");
        public Task<InstallerProtocolClientException> SessionFaulted => this.Fault.Task;
        public Func<CancellationToken, Task<InstallerExecutionOperation>> Execute { get; init; } = _ => throw new AssertionException("Execution wasn't expected.");
        public Func<CancellationToken, Task<InstallerPostExecutionRecoveryOwner>> TakeRecovery { get; init; } = _ => throw new AssertionException("Recovery ownership wasn't expected.");
        public int ExecuteCalls => Volatile.Read(ref this.executeCalls);
        public int DisposeCalls => Volatile.Read(ref this.disposeCalls);

        public void Fail() => this.Fault.TrySetResult(new InstallerProtocolClientException("private backend fault /home/user/log"));

        public Task<InstallerExecutionOperation> ExecuteAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this.executeCalls);
            return this.Execute(cancellationToken);
        }

        public Task<InstallerPostExecutionRecoveryOwner> TakePostExecutionRecoveryOwnerAsync(CancellationToken cancellationToken = default)
            => this.TakeRecovery(cancellationToken);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref this.disposeCalls);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecoveryClient : IInstallerProtocolClient
    {
        private readonly TaskCompletionSource<InstallerProtocolClientException> Fault = NewSource<InstallerProtocolClientException>();
        public Task<InstallerProtocolClientException> SessionFaulted => this.Fault.Task;
        public InstallerRecoveryResult Result { get; init; } = RecoveryForCopy(ProtocolInterruptedRecoveryOutcome.RecoveryCompleted, ProtocolNextAction.InspectAgain);
        public TaskCompletionSource<InstallerRecoveryResult>? Completion { get; init; }
        public bool FaultProgress { get; init; }
        public bool WaitDuringHandshake { get; init; }
        public int HandshakeCalls { get; private set; }
        public int RecoveryCalls { get; private set; }
        public string? ValidatedPath { get; private set; }

        public async Task<HandshakeEvent> HandshakeAsync(string clientName, string clientVersion, CancellationToken cancellationToken = default)
        {
            this.HandshakeCalls++;
            if (this.WaitDuringHandshake)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new(
                ProtocolSessionId.Parse("11111111111111111111111111111111"),
                "1",
                [ProcessInstallerProtocolClient.GameValidationCapability, ProcessInstallerProtocolClient.InterruptedRecoveryCapability]
            );
        }

        public Task<ProtocolGameCandidate> ValidateGameAsync(string canonicalPath, CancellationToken cancellationToken = default)
        {
            this.ValidatedPath = canonicalPath;
            return Task.FromResult(new ProtocolGameCandidate(canonicalPath, LinuxGameFolderStatus.Valid, "private backend name"));
        }

        public Task<InstallerRecoveryOperation> RecoverInterruptedAsync(ProtocolGameCandidate candidate, CancellationToken cancellationToken = default)
        {
            this.RecoveryCalls++;
            Channel<InstallerRecoveryProgress> progress = Channel.CreateBounded<InstallerRecoveryProgress>(1);
            progress.Writer.TryComplete(this.FaultProgress ? new InvalidOperationException("private progress fault") : null);
            return Task.FromResult(new InstallerRecoveryOperation(
                progress.Reader,
                this.Completion?.Task ?? Task.FromResult(this.Result)
            ));
        }

        public Task<InstallerPackageOpenResult> OpenPackageAsync(InstallerPackageOpenInput package, CancellationToken cancellationToken = default)
            => throw new AssertionException("Package opening isn't part of fresh recovery.");
        public Task<IReadOnlyList<ProtocolGameCandidate>> DiscoverGamesAsync(CancellationToken cancellationToken = default)
            => throw new AssertionException("Discovery isn't part of exact recovery.");
        public Task<InstallerReadOnlyPlanResult> InspectPlanAsync(string canonicalGamePath, InstallerOperation operation, CancellationToken cancellationToken = default)
            => throw new AssertionException("Plan inspection isn't part of recovery.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
