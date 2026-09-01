using FluentAssertions;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Gui.Backend;
using StardewModdingAPI.Installer.Gui.Frontend;

namespace StardewModdingAPI.Installer.Gui.Tests;

internal sealed class PlanReviewControllerTests
{
    [Test]
    public async Task StartsWithoutAnImplicitOperationAndRequiresAnExplicitSelection()
    {
        FakePlanSession session = new();
        await using PlanReviewController controller = new(session);

        PlanReviewSnapshot snapshot = controller.Snapshot;
        snapshot.State.Should().Be(PlanReviewState.Choosing);
        snapshot.SelectedOperation.Should().BeNull();
        snapshot.Result.Should().BeNull();
        snapshot.CanSelect.Should().BeTrue();
        snapshot.CanInspect.Should().BeFalse();
        snapshot.CanCancel.Should().BeFalse();
        snapshot.CanRetry.Should().BeFalse();
        snapshot.CanExit.Should().BeFalse();

        Func<Task> inspect = () => controller.InspectAsync();
        await inspect.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Select one supported operation*");
        session.InspectedOperations.Should().BeEmpty();
    }

    [TestCase(InstallerOperation.Install)]
    [TestCase(InstallerOperation.Update)]
    [TestCase(InstallerOperation.Repair)]
    [TestCase(InstallerOperation.Uninstall)]
    [TestCase(InstallerOperation.Backup)]
    public async Task InspectsEachSupportedReadOnlyOperationExactly(InstallerOperation operation)
    {
        FakePlanSession session = new();
        await using PlanReviewController controller = new(session);

        controller.SelectOperation(operation);
        await controller.InspectAsync();

        PlanReviewSnapshot snapshot = controller.Snapshot;
        snapshot.State.Should().Be(PlanReviewState.Available);
        snapshot.SelectedOperation.Should().Be(operation);
        PlanReviewPlan plan = snapshot.Result.Should().BeOfType<PlanReviewPlan>().Subject;
        plan.Operation.Should().Be(operation);
        plan.AdditionalNoticeCount.Should().Be(0);
        snapshot.CanSelect.Should().BeTrue();
        snapshot.CanInspect.Should().BeTrue("the same operation can be refreshed explicitly");
        snapshot.CanCancel.Should().BeFalse();
        snapshot.CanRetry.Should().BeFalse();
        session.InspectedOperations.Should().Equal(operation);
    }

    [TestCase(InstallerOperation.Rollback)]
    [TestCase((InstallerOperation)999)]
    public async Task RejectsRollbackAndUndefinedOperationsBeforeCallingTheBackend(InstallerOperation operation)
    {
        FakePlanSession session = new();
        await using PlanReviewController controller = new(session);

        Action select = () => controller.SelectOperation(operation);

        select.Should().Throw<ArgumentOutOfRangeException>();
        controller.Snapshot.SelectedOperation.Should().BeNull();
        controller.Snapshot.Result.Should().BeNull();
        session.InspectedOperations.Should().BeEmpty();
    }

    [Test]
    public async Task ChangingSelectionClearsAFormerResultBeforeAnotherInspection()
    {
        FakePlanSession session = new();
        await using PlanReviewController controller = new(session);
        controller.SelectOperation(InstallerOperation.Install);
        await controller.InspectAsync();
        controller.Snapshot.Result.Should().NotBeNull();

        controller.SelectOperation(InstallerOperation.Backup);

        PlanReviewSnapshot snapshot = controller.Snapshot;
        snapshot.State.Should().Be(PlanReviewState.SelectionChanged);
        snapshot.SelectedOperation.Should().Be(InstallerOperation.Backup);
        snapshot.Result.Should().BeNull();
        snapshot.CanInspect.Should().BeTrue();
        session.InspectedOperations.Should().Equal(InstallerOperation.Install);
    }

    [Test]
    public async Task SequentialInspectionRefreshesTheSameSelectionWithoutQueuing()
    {
        int response = 0;
        FakePlanSession session = new()
        {
            Inspection = (operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(
                CreatePlan(operation) with { AdditionalNoticeCount = Interlocked.Increment(ref response) }
            )
        };
        await using PlanReviewController controller = new(session);
        controller.SelectOperation(InstallerOperation.Repair);

        await controller.InspectAsync();
        ((PlanReviewPlan)controller.Snapshot.Result!).AdditionalNoticeCount.Should().Be(1);
        await controller.InspectAsync();

        ((PlanReviewPlan)controller.Snapshot.Result!).AdditionalNoticeCount.Should().Be(2);
        controller.Snapshot.Generation.Should().Be(2);
        session.InspectedOperations.Should().Equal(InstallerOperation.Repair, InstallerOperation.Repair);
    }

    [Test]
    public async Task ExactExecutablePlanCanConfirmWithoutExecutingAndHandsOffExactlyOnce()
    {
        FakePlanSession session = new();
        FakeConfirmedSession confirmed = new(session.Release, session.Game, session.SessionFaulted);
        session.Confirmation = (_, _) => Task.FromResult<IConfirmedInstallerSession>(confirmed);
        PlanReviewController controller = new(session);
        controller.SelectOperation(InstallerOperation.Install);
        await controller.InspectAsync();

        controller.Snapshot.CanConfirm.Should().BeTrue();
        controller.Snapshot.HandoffReady.Should().BeFalse();
        await controller.ConfirmAsync();

        controller.Snapshot.State.Should().Be(PlanReviewState.HandoffReady);
        controller.Snapshot.CanConfirm.Should().BeFalse();
        controller.Snapshot.HandoffReady.Should().BeTrue();
        session.ConfirmedPlans.Should().ContainSingle();
        confirmed.ExecuteCalls.Should().Be(0, "confirmation alone must not mutate files");

        controller.TakeConfirmedSession().Should().BeSameAs(confirmed);
        Action secondTake = () => controller.TakeConfirmedSession();
        secondTake.Should().Throw<ObjectDisposedException>();
        await controller.DisposeAsync();
        confirmed.DisposeCalls.Should().Be(0, "cleanup ownership was transferred to the caller");
        await confirmed.DisposeAsync();
        confirmed.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task UnappliedCandidateSelectionDisablesConfirmationUntilCleared()
    {
        InstallerReadOnlyPlanCandidate candidate = CandidateCapability("mods/confirm.dll", false);
        FakePlanSession session = new()
        {
            Inspection = (operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CandidatePlan(operation, [candidate]))
        };
        await using PlanReviewController controller = new(session);
        controller.SelectOperation(InstallerOperation.Install);
        await controller.InspectAsync();
        PlanReviewCandidate choice = controller.Snapshot.Candidates.Single();

        controller.SetCandidateSelection([choice]);
        controller.Snapshot.CanConfirm.Should().BeFalse();
        Func<Task> confirm = () => controller.ConfirmAsync();
        await confirm.Should().ThrowAsync<InvalidOperationException>();
        session.ConfirmedPlans.Should().BeEmpty();

        controller.ClearCandidateSelection();
        controller.Snapshot.CanConfirm.Should().BeTrue();
    }

    [Test]
    public async Task DisposeAtHandoffReadyDisposesTheConfirmedOwnerInsteadOfTheOldBoundOwner()
    {
        FakePlanSession session = new();
        FakeConfirmedSession confirmed = new(session.Release, session.Game, session.SessionFaulted);
        session.Confirmation = (_, _) => Task.FromResult<IConfirmedInstallerSession>(confirmed);
        PlanReviewController controller = new(session);
        controller.SelectOperation(InstallerOperation.Backup);
        await controller.InspectAsync();
        await controller.ConfirmAsync();

        await controller.DisposeAsync();

        confirmed.DisposeCalls.Should().Be(1);
        session.DisposeCalls.Should().Be(0, "the old bound owner became cleanup-inert after confirmation");
    }

    [Test]
    public async Task ConcurrentConfirmationHasOneWinnerAndOneBackendCall()
    {
        TaskCompletionSource<IConfirmedInstallerSession> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakePlanSession session = new()
        {
            Confirmation = (_, _) => completion.Task
        };
        PlanReviewController controller = new(session);
        controller.SelectOperation(InstallerOperation.Backup);
        await controller.InspectAsync();

        Task winner = controller.ConfirmAsync();
        Func<Task> loser = () => controller.ConfirmAsync();
        await loser.Should().ThrowAsync<InvalidOperationException>();
        FakeConfirmedSession confirmed = new(session.Release, session.Game, session.SessionFaulted);
        completion.TrySetResult(confirmed);
        await winner;

        session.ConfirmedPlans.Should().ContainSingle();
        await controller.DisposeAsync();
        confirmed.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task PreCancelledConfirmationPreservesTheCurrentPlanForRetry()
    {
        FakePlanSession session = new();
        await using PlanReviewController controller = new(session);
        controller.SelectOperation(InstallerOperation.Backup);
        await controller.InspectAsync();
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        Func<Task> rejected = () => controller.ConfirmAsync(cancelled.Token);
        await rejected.Should().ThrowAsync<OperationCanceledException>();
        session.ConfirmedPlans.Should().BeEmpty();
        controller.Snapshot.CanConfirm.Should().BeTrue();

        await controller.ConfirmAsync();
        session.ConfirmedPlans.Should().ContainSingle();
    }

    [Test]
    public async Task CancelPendingConfirmationDisposesALateOwnerAndNeverPublishesHandoff()
    {
        TaskCompletionSource<IConfirmedInstallerSession> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakePlanSession session = new() { Confirmation = (_, _) => completion.Task };
        FakeConfirmedSession confirmed = new(session.Release, session.Game, session.SessionFaulted);
        PlanReviewController controller = new(session);
        controller.SelectOperation(InstallerOperation.Backup);
        await controller.InspectAsync();
        Task confirming = controller.ConfirmAsync();
        controller.Snapshot.CanCancel.Should().BeTrue();
        Task cancelling = controller.CancelAsync();
        completion.TrySetResult(confirmed);

        await cancelling;
        await confirming;

        controller.Snapshot.State.Should().Be(PlanReviewState.Cancelled);
        controller.Snapshot.HandoffReady.Should().BeFalse();
        confirmed.DisposeCalls.Should().Be(1);
        session.DisposeCalls.Should().Be(0);
        await controller.DisposeAsync();
        session.DisposeCalls.Should().Be(0, "late exact confirmation cleanup must remain the cleanup owner");
    }

    [Test]
    public async Task SessionFaultAtHandoffReadyDisposesConfirmedOwner()
    {
        FakePlanSession session = new();
        FakeConfirmedSession confirmed = new(session.Release, session.Game, session.SessionFaulted);
        session.Confirmation = (_, _) => Task.FromResult<IConfirmedInstallerSession>(confirmed);
        PlanReviewController controller = new(session);
        controller.SelectOperation(InstallerOperation.Backup);
        await controller.InspectAsync();
        await controller.ConfirmAsync();

        session.Fail();
        await WaitUntilAsync(() => controller.Snapshot.State == PlanReviewState.SessionFaulted);

        confirmed.DisposeCalls.Should().Be(1);
        session.DisposeCalls.Should().Be(0);
        await controller.DisposeAsync();
    }

    [Test]
    public async Task ForeignConfirmedOwnerFailsClosedAndDisposesBothPotentialOwners()
    {
        FakePlanSession session = new();
        FakeConfirmedSession foreign = new(GameDiscoveryControllerTests.Release(), session.Game, session.SessionFaulted);
        session.Confirmation = (_, _) => Task.FromResult<IConfirmedInstallerSession>(foreign);
        PlanReviewController controller = new(session);
        controller.SelectOperation(InstallerOperation.Backup);
        await controller.InspectAsync();

        await controller.ConfirmAsync();

        controller.Snapshot.State.Should().Be(PlanReviewState.Failed);
        controller.Snapshot.HandoffReady.Should().BeFalse();
        foreign.DisposeCalls.Should().Be(1);
        session.DisposeCalls.Should().Be(1);
        await controller.DisposeAsync();
        foreign.DisposeCalls.Should().Be(1);
        session.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task TakeAndDisposeRaceTransfersOrCleansTheConfirmedOwnerExactlyOnce()
    {
        for (int iteration = 0; iteration < 20; iteration++)
        {
            FakePlanSession session = new();
            FakeConfirmedSession confirmed = new(session.Release, session.Game, session.SessionFaulted);
            session.Confirmation = (_, _) => Task.FromResult<IConfirmedInstallerSession>(confirmed);
            PlanReviewController controller = new(session);
            controller.SelectOperation(InstallerOperation.Backup);
            await controller.InspectAsync();
            await controller.ConfirmAsync();
            TaskCompletionSource start = NewCompletion();
            IConfirmedInstallerSession? taken = null;

            Task take = Task.Run(async () =>
            {
                await start.Task;
                try { taken = controller.TakeConfirmedSession(); }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            });
            Task dispose = Task.Run(async () =>
            {
                await start.Task;
                await controller.DisposeAsync();
            });
            start.TrySetResult();
            await Task.WhenAll(take, dispose);
            if (taken is not null)
                await taken.DisposeAsync();

            confirmed.DisposeCalls.Should().Be(1);
            session.DisposeCalls.Should().Be(0);
        }
    }

    [Test]
    public async Task TakeAndCommittedFaultRaceTransfersOrCleansTheConfirmedOwnerExactlyOnce()
    {
        for (int iteration = 0; iteration < 10; iteration++)
        {
            FakePlanSession session = new();
            FakeConfirmedSession confirmed = new(session.Release, session.Game, session.SessionFaulted);
            session.Confirmation = (_, _) => Task.FromResult<IConfirmedInstallerSession>(confirmed);
            PlanReviewController controller = new(session);
            controller.SelectOperation(InstallerOperation.Backup);
            await controller.InspectAsync();
            await controller.ConfirmAsync();
            TaskCompletionSource atCommit = NewCompletion();
            TaskCompletionSource releaseCommit = NewCompletion();
            controller.BeforeSessionFaultCommitForTesting = () =>
            {
                atCommit.TrySetResult();
                releaseCommit.Task.GetAwaiter().GetResult();
            };
            session.Fail();
            await atCommit.Task.WaitAsync(TimeSpan.FromSeconds(2));
            IConfirmedInstallerSession? taken = null;
            Task take = Task.Run(() =>
            {
                try { taken = controller.TakeConfirmedSession(); }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            });
            releaseCommit.TrySetResult();
            await take;
            if (taken is not null)
                await taken.DisposeAsync();
            else
                await WaitUntilAsync(() => controller.Snapshot.State == PlanReviewState.SessionFaulted);
            await controller.DisposeAsync();

            confirmed.DisposeCalls.Should().Be(1);
            session.DisposeCalls.Should().Be(0);
        }
    }

    [TestCase(ProtocolPrePlanErrorCode.RequestCancelled, ProtocolNextAction.RetryRequest)]
    [TestCase(ProtocolPrePlanErrorCode.InspectionFailed, ProtocolNextAction.InspectAgain)]
    [TestCase(ProtocolPrePlanErrorCode.PermissionDenied, ProtocolNextAction.ReviewFilesystem)]
    public async Task ExactRetryableRejectionsRetainOnlyReadOnlyReinspection(
        ProtocolPrePlanErrorCode errorCode,
        ProtocolNextAction nextAction
    )
    {
        InstallerReadOnlyPlanRejection rejection = new(errorCode, nextAction, false);
        FakePlanSession session = new()
        {
            Inspection = (_, _) => Task.FromResult<InstallerReadOnlyPlanResult>(rejection)
        };
        await using PlanReviewController controller = new(session);
        controller.SelectOperation(InstallerOperation.Update);

        await controller.InspectAsync();

        PlanReviewSnapshot snapshot = controller.Snapshot;
        snapshot.State.Should().Be(PlanReviewState.Rejected);
        snapshot.Result.Should().Be(new PlanReviewRejection(errorCode, nextAction, false));
        snapshot.CanRetry.Should().BeTrue();
        snapshot.CanInspect.Should().BeTrue();
        snapshot.CanSelect.Should().BeTrue();
        snapshot.CanExit.Should().BeFalse();
        session.DisposeCalls.Should().Be(0);
    }

    [TestCase(ProtocolPrePlanErrorCode.InvalidGameFolder, ProtocolNextAction.SelectGameFolder, false)]
    [TestCase(ProtocolPrePlanErrorCode.PackageRejected, ProtocolNextAction.ReopenVerifiedPackage, false)]
    [TestCase(ProtocolPrePlanErrorCode.UnexpectedFailure, ProtocolNextAction.StartNewSession, true)]
    [TestCase(ProtocolPrePlanErrorCode.UnexpectedFailure, ProtocolNextAction.ViewPrivateLog, true)]
    public async Task WorkflowTerminalRejectionsRevokeSelectionAndDisposeExactlyOnce(
        ProtocolPrePlanErrorCode errorCode,
        ProtocolNextAction nextAction,
        bool protocolTerminal
    )
    {
        InstallerReadOnlyPlanRejection rejection = new(errorCode, nextAction, protocolTerminal);
        FakePlanSession session = new()
        {
            Inspection = (_, _) => Task.FromResult<InstallerReadOnlyPlanResult>(rejection)
        };
        PlanReviewController controller = new(session);
        controller.SelectOperation(InstallerOperation.Install);

        await controller.InspectAsync();
        await WaitUntilAsync(() => session.DisposeCalls == 1);

        PlanReviewSnapshot snapshot = controller.Snapshot;
        snapshot.State.Should().Be(PlanReviewState.Rejected);
        snapshot.Result.Should().Be(new PlanReviewRejection(errorCode, nextAction, protocolTerminal));
        snapshot.CanSelect.Should().BeFalse();
        snapshot.CanInspect.Should().BeFalse();
        snapshot.CanRetry.Should().BeFalse();
        snapshot.CanExit.Should().BeTrue();
        Action select = () => controller.SelectOperation(InstallerOperation.Repair);
        select.Should().Throw<InvalidOperationException>();
        await controller.DisposeAsync();
        await controller.DisposeAsync();
        session.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task TerminalInspectionDoesNotSettleUntilSessionCleanupSettles()
    {
        TaskCompletionSource cleanupStarted = NewCompletion();
        TaskCompletionSource releaseCleanup = NewCompletion();
        FakePlanSession session = new()
        {
            Inspection = (_, _) => Task.FromResult<InstallerReadOnlyPlanResult>(new InstallerReadOnlyPlanRejection(
                ProtocolPrePlanErrorCode.PackageRejected,
                ProtocolNextAction.ReopenVerifiedPackage,
                false
            )),
            Disposal = async () =>
            {
                cleanupStarted.TrySetResult();
                await releaseCleanup.Task;
            }
        };
        PlanReviewController controller = new(session);
        controller.SelectOperation(InstallerOperation.Install);

        Task inspection = controller.InspectAsync();
        await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        inspection.IsCompleted.Should().BeFalse("terminal publication must wait for exactly-once session cleanup");
        controller.Snapshot.State.Should().Be(PlanReviewState.Closing);
        controller.Snapshot.Result.Should().BeNull();
        releaseCleanup.TrySetResult();
        await inspection.WaitAsync(TimeSpan.FromSeconds(2));

        controller.Snapshot.State.Should().Be(PlanReviewState.Rejected);
        controller.Snapshot.CanExit.Should().BeTrue();
        session.DisposeCalls.Should().Be(1);
        await controller.DisposeAsync();
        session.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task CallerCancellationClearsResultsTerminatesTheSessionAndDisposesOnce()
    {
        TaskCompletionSource started = NewCompletion();
        FakePlanSession session = new()
        {
            Inspection = async (_, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return CreatePlan(InstallerOperation.Backup);
            }
        };
        PlanReviewController controller = new(session);
        controller.SelectOperation(InstallerOperation.Backup);
        Task inspection = controller.InspectAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await controller.CancelAsync();
        await inspection;
        await WaitUntilAsync(() => session.DisposeCalls == 1);

        controller.Snapshot.State.Should().Be(PlanReviewState.Cancelled);
        controller.Snapshot.Result.Should().BeNull();
        controller.Snapshot.CanExit.Should().BeTrue();
        controller.Snapshot.CanInspect.Should().BeFalse();
        await controller.DisposeAsync();
        session.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task LinkedCallerTokenCancellationIsAlsoTerminal()
    {
        TaskCompletionSource started = NewCompletion();
        FakePlanSession session = new()
        {
            Inspection = async (_, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return CreatePlan(InstallerOperation.Install);
            }
        };
        using CancellationTokenSource cancellation = new();
        await using PlanReviewController controller = new(session);
        controller.SelectOperation(InstallerOperation.Install);
        Task inspection = controller.InspectAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await cancellation.CancelAsync();
        await inspection;
        await WaitUntilAsync(() => session.DisposeCalls == 1);

        controller.Snapshot.State.Should().Be(PlanReviewState.Cancelled);
        controller.Snapshot.Result.Should().BeNull();
    }

    [Test]
    public async Task IdleSessionFaultClearsAuthorityAndDisposesExactlyOnce()
    {
        FakePlanSession session = new();
        PlanReviewController controller = new(session);
        controller.SelectOperation(InstallerOperation.Install);

        session.Fail();
        await WaitUntilAsync(() => controller.Snapshot.State == PlanReviewState.SessionFaulted);
        await WaitUntilAsync(() => session.DisposeCalls == 1);

        controller.Snapshot.Result.Should().BeNull();
        controller.Snapshot.CanExit.Should().BeTrue();
        controller.Snapshot.CanInspect.Should().BeFalse();
        await controller.DisposeAsync();
        session.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task FaultedFaultNotificationTaskIsStillTerminalAndCleanedUp()
    {
        FakePlanSession session = new()
        {
            FaultNotification = Task.FromException<InstallerProtocolClientException>(
                new InvalidOperationException("synthetic broken fault notifier")
            )
        };
        PlanReviewController controller = new(session);

        await WaitUntilAsync(() => controller.Snapshot.State == PlanReviewState.SessionFaulted);

        controller.Snapshot.Result.Should().BeNull();
        controller.Snapshot.CanExit.Should().BeTrue();
        await controller.DisposeAsync();
        session.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task FaultAfterPublishedSuccessClearsTheStalePlan()
    {
        FakePlanSession session = new();
        PlanReviewController controller = new(session);
        controller.SelectOperation(InstallerOperation.Update);
        await controller.InspectAsync();
        controller.Snapshot.Result.Should().NotBeNull();

        session.Fail();
        await WaitUntilAsync(() => controller.Snapshot.State == PlanReviewState.SessionFaulted);

        controller.Snapshot.Result.Should().BeNull();
        controller.Snapshot.CanSelect.Should().BeFalse();
        await controller.DisposeAsync();
        session.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task SessionFaultWinsAResultCommitRaceAndNeverPublishesThePlan()
    {
        TaskCompletionSource atCommit = NewCompletion();
        TaskCompletionSource releaseCommit = NewCompletion();
        InstallerReadOnlyPlanCandidate backend = CandidateCapability("mods/race.dll", provisional: false);
        FakePlanSession session = new()
        {
            Inspection = (operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CandidatePlan(operation, [backend]))
        };
        PlanReviewController controller = new(session)
        {
            BeforeResultCommitForTesting = () =>
            {
                atCommit.TrySetResult();
                releaseCommit.Task.GetAwaiter().GetResult();
            }
        };
        controller.SelectOperation(InstallerOperation.Repair);
        Task inspection = controller.InspectAsync();
        await atCommit.Task.WaitAsync(TimeSpan.FromSeconds(2));

        session.Fail();
        releaseCommit.TrySetResult();
        await inspection.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => controller.Snapshot.State == PlanReviewState.SessionFaulted);

        controller.Snapshot.Result.Should().BeNull();
        controller.Snapshot.Candidates.Should().BeEmpty();
        controller.Snapshot.AppliedCandidateApprovalCount.Should().Be(0);
        await controller.DisposeAsync();
        session.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task CancellationWinsTheExactCandidateResultCommitGap()
    {
        InstallerReadOnlyPlanCandidate backend = CandidateCapability("mods/example.dll", provisional: false);
        TaskCompletionSource atCommit = NewCompletion();
        TaskCompletionSource releaseCommit = NewCompletion();
        FakePlanSession session = new()
        {
            Inspection = (operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CandidatePlan(operation, [backend])),
            Approval = (_, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CandidatePlan(InstallerOperation.Install, [
                CandidateCapability("mods/replacement.dll", provisional: false)
            ]))
        };
        PlanReviewController controller = new(session);
        controller.SelectOperation(InstallerOperation.Install);
        await controller.InspectAsync();
        controller.SetCandidateSelection(controller.Snapshot.Candidates);
        controller.BeforeResultCommitForTesting = () =>
        {
            atCommit.TrySetResult();
            releaseCommit.Task.GetAwaiter().GetResult();
        };

        Task approval = controller.ApplyCandidateSelectionAsync();
        await atCommit.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task cancellation = controller.CancelAsync();
        releaseCommit.TrySetResult();
        await Task.WhenAll(approval, cancellation).WaitAsync(TimeSpan.FromSeconds(2));

        PlanReviewSnapshot snapshot = controller.Snapshot;
        snapshot.State.Should().Be(PlanReviewState.Cancelled);
        snapshot.Result.Should().BeNull();
        snapshot.Candidates.Should().BeEmpty();
        snapshot.AppliedCandidateApprovalCount.Should().Be(0);
        session.DisposeCalls.Should().Be(1);
        await controller.DisposeAsync();
        session.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task DisposalDuringCandidateApprovalRevokesPresentationAndJoinsCleanup()
    {
        InstallerReadOnlyPlanCandidate backend = CandidateCapability("mods/example.dll", provisional: false);
        TaskCompletionSource approvalStarted = NewCompletion();
        FakePlanSession session = new()
        {
            Inspection = (operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CandidatePlan(operation, [backend])),
            Approval = async (_, token) =>
            {
                approvalStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new AssertionException("Disposal should cancel candidate approval.");
            }
        };
        PlanReviewController controller = new(session);
        controller.SelectOperation(InstallerOperation.Install);
        await controller.InspectAsync();
        controller.SetCandidateSelection(controller.Snapshot.Candidates);
        Task approval = controller.ApplyCandidateSelectionAsync();
        await approvalStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await controller.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await approval.WaitAsync(TimeSpan.FromSeconds(2));

        PlanReviewSnapshot snapshot = controller.Snapshot;
        snapshot.State.Should().Be(PlanReviewState.Disposed);
        snapshot.Result.Should().BeNull();
        snapshot.Candidates.Should().BeEmpty();
        snapshot.SelectedCandidates.Should().BeEmpty();
        snapshot.AppliedCandidateApprovalCount.Should().Be(0);
        session.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task ConcurrentDisposalCancelsAndJoinsAnActiveInspection()
    {
        TaskCompletionSource started = NewCompletion();
        FakePlanSession session = new()
        {
            Inspection = async (_, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return CreatePlan(InstallerOperation.Uninstall);
            }
        };
        PlanReviewController controller = new(session);
        controller.SelectOperation(InstallerOperation.Uninstall);
        Task inspection = controller.InspectAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task[] disposals = Enumerable.Range(0, 8)
            .Select(_ => controller.DisposeAsync().AsTask())
            .ToArray();
        await Task.WhenAll(disposals).WaitAsync(TimeSpan.FromSeconds(2));
        await inspection.WaitAsync(TimeSpan.FromSeconds(2));

        controller.Snapshot.State.Should().Be(PlanReviewState.Disposed);
        controller.Snapshot.Result.Should().BeNull();
        session.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task ProjectionCopiesEveryCollectionAndLabelsBackendSelectionAsProvisional()
    {
        List<ProtocolPlanRisk> risks = [ProtocolPlanRisk.ModifiedOrUnknownFileApproval];
        List<InstallerPlanOperationCount> operations = [new(PlanOperationKind.Replace, 1)];
        List<InstallerPlanConflictCount> conflicts = [];
        List<InstallerPlanCandidateCount> candidates =
        [
            new(
                FileReplacementCandidateReason.ModifiedReceiptOwned,
                FileReplacementCandidateDisposition.Replace,
                true,
                1
            )
        ];
        InstallerReadOnlyPlanSuccess source = CreatePlan(InstallerOperation.Repair) with
        {
            Risks = risks,
            OperationCounts = operations,
            ConflictCounts = conflicts,
            CandidateCounts = candidates,
            Candidates = [CandidateCapability("mods/example.dll", provisional: true)]
        };
        FakePlanSession session = new()
        {
            Inspection = (_, _) => Task.FromResult<InstallerReadOnlyPlanResult>(source)
        };
        await using PlanReviewController controller = new(session);
        controller.SelectOperation(InstallerOperation.Repair);
        await controller.InspectAsync();
        PlanReviewPlan projected = controller.Snapshot.Result.Should().BeOfType<PlanReviewPlan>().Subject;

        risks.Clear();
        operations[0] = new(PlanOperationKind.Remove, 999);
        candidates[0] = new(
            FileReplacementCandidateReason.UnknownCollision,
            FileReplacementCandidateDisposition.Replace,
            false,
            999
        );

        projected.Risks.Should().Equal(ProtocolPlanRisk.ModifiedOrUnknownFileApproval);
        projected.OperationCounts.Should().Equal(new PlanReviewOperationCount(PlanOperationKind.Replace, 1));
        projected.CandidateCounts.Should().Equal(new PlanReviewCandidateCount(
            FileReplacementCandidateReason.ModifiedReceiptOwned,
            FileReplacementCandidateDisposition.Replace,
            true,
            1
        ));
        projected.CandidateCounts.Single().ProvisionallyIncluded.Should().BeTrue();
        projected.Candidates.Single().Should().Match<PlanReviewCandidate>(candidate =>
            candidate.DisplayPath == "mods/example.dll"
            && candidate.Reason == FileReplacementCandidateReason.ModifiedReceiptOwned
            && candidate.Disposition == FileReplacementCandidateDisposition.Replace
            && candidate.BackendProvisionallyIncluded
        );
        projected.Risks.Should().BeAssignableTo<System.Collections.ObjectModel.ReadOnlyCollection<ProtocolPlanRisk>>();
    }

    [Test]
    public async Task CandidateSelectionRequiresExactCurrentReferencesAndInvalidInputsPreserveTheBinding()
    {
        InstallerReadOnlyPlanCandidate firstBackend = CandidateCapability("mods/first.dll", provisional: false);
        InstallerReadOnlyPlanCandidate secondBackend = CandidateCapability("mods/second.dll", provisional: true);
        FakePlanSession session = new()
        {
            Inspection = (operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CandidatePlan(operation, [firstBackend, secondBackend]))
        };
        await using PlanReviewController controller = new(session);
        controller.SelectOperation(InstallerOperation.Repair);
        await controller.InspectAsync();
        PlanReviewCandidate[] choices = controller.Snapshot.Candidates.ToArray();

        controller.Snapshot.SelectedCandidates.Should().BeEmpty("backend provisional inclusion is evidence, not a local user selection");
        controller.SetCandidateSelection([choices[0]]);

        PlanReviewCandidate reconstructed = new(
            choices[0].DisplayPath,
            choices[0].Reason,
            choices[0].Disposition,
            choices[0].BackendProvisionallyIncluded
        );
        Action reconstructedSet = () => controller.SetCandidateSelection([reconstructed]);
        reconstructedSet.Should().Throw<ArgumentException>();
        Action mixedSet = () => controller.SetCandidateSelection([choices[0], reconstructed]);
        mixedSet.Should().Throw<ArgumentException>();
        Action duplicateSet = () => controller.SetCandidateSelection([choices[0], choices[0]]);
        duplicateSet.Should().Throw<ArgumentException>();
        Action unreadableSet = () => controller.SetCandidateSelection(new ThrowingCandidateList());
        unreadableSet.Should().Throw<ArgumentException>();
        Func<Task> prematureFreshInspection = () => controller.StartFreshInspectionAsync();
        await prematureFreshInspection.Should().ThrowAsync<InvalidOperationException>();

        controller.Snapshot.SelectedCandidates.Should().ContainSingle().Which.Should().BeSameAs(choices[0]);
        session.ApprovedCandidates.Should().BeEmpty();
        controller.ClearCandidateSelection();
        controller.Snapshot.SelectedCandidates.Should().BeEmpty();
        session.ApprovedCandidates.Should().BeEmpty("clear is presentation-local and cannot invoke the backend");
    }

    [Test]
    public async Task CandidateApprovalIsAdditiveBoundedAndFreshInspectionRevokesOldChoices()
    {
        InstallerReadOnlyPlanCandidate first = CandidateCapability("mods/first.dll", provisional: false);
        InstallerReadOnlyPlanCandidate second = CandidateCapability("mods/second.dll", provisional: true);
        InstallerReadOnlyPlanCandidate subsequent = CandidateCapability("mods/subsequent.dll", provisional: false);
        InstallerReadOnlyPlanCandidate fresh = CandidateCapability("mods/fresh.dll", provisional: false);
        int inspectionCount = 0;
        int approvalCount = 0;
        TaskCompletionSource approvalStarted = NewCompletion();
        TaskCompletionSource releaseApproval = NewCompletion();
        FakePlanSession session = new()
        {
            Inspection = (operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(
                CandidatePlan(operation, Interlocked.Increment(ref inspectionCount) == 1 ? [first, second] : [fresh])
            ),
            Approval = async (_, _) =>
            {
                int count = Interlocked.Increment(ref approvalCount);
                if (count == 1)
                {
                    approvalStarted.TrySetResult();
                    await releaseApproval.Task;
                }
                return CandidatePlan(InstallerOperation.Repair, count == 1 ? [subsequent] : []);
            }
        };
        await using PlanReviewController controller = new(session);
        controller.SelectOperation(InstallerOperation.Repair);
        await controller.InspectAsync();
        PlanReviewCandidate[] oldChoices = controller.Snapshot.Candidates.ToArray();
        controller.SetCandidateSelection(oldChoices);

        Task approval = controller.ApplyCandidateSelectionAsync();
        await approvalStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        controller.Snapshot.State.Should().Be(PlanReviewState.Approving);
        controller.Snapshot.CanCancel.Should().BeTrue();
        controller.Snapshot.Candidates.Should().BeEmpty("candidate authority is revoked before the backend call");
        releaseApproval.TrySetResult();
        await approval.WaitAsync(TimeSpan.FromSeconds(2));

        session.ApprovedCandidates.Should().ContainSingle().Which.Should().Equal(first, second);
        PlanReviewSnapshot applied = controller.Snapshot;
        applied.State.Should().Be(PlanReviewState.Available);
        applied.Candidates.Should().ContainSingle().Which.DisplayPath.Should().Be("mods/subsequent.dll");
        applied.AppliedCandidateApprovalCount.Should().Be(2);
        applied.HasAppliedCandidateApprovals.Should().BeTrue();
        controller.SetCandidateSelection(applied.Candidates);
        await controller.ApplyCandidateSelectionAsync();
        session.ApprovedCandidates.Should().HaveCount(2);
        session.ApprovedCandidates[1].Should().Equal(subsequent);
        controller.Snapshot.Candidates.Should().BeEmpty();
        controller.Snapshot.AppliedCandidateApprovalCount.Should().Be(3);
        controller.Snapshot.CanStartFreshInspection.Should().BeTrue(
            "cumulative applied history stays reachable even when the replacement has no candidates"
        );

        await controller.StartFreshInspectionAsync();
        PlanReviewSnapshot refreshed = controller.Snapshot;
        refreshed.AppliedCandidateApprovalCount.Should().Be(0);
        refreshed.HasAppliedCandidateApprovals.Should().BeFalse();
        refreshed.Candidates.Should().ContainSingle().Which.DisplayPath.Should().Be("mods/fresh.dll");
        Action staleSet = () => controller.SetCandidateSelection([oldChoices[0]]);
        staleSet.Should().Throw<ArgumentException>();
    }

    [Test]
    public async Task CandidateApprovalRejectionRevokesAuthorityAndRequiresAFullInspection()
    {
        InstallerReadOnlyPlanCandidate backend = CandidateCapability("mods/example.dll", provisional: false);
        FakePlanSession session = new()
        {
            Inspection = (operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CandidatePlan(operation, [backend])),
            Approval = (_, _) => Task.FromResult<InstallerReadOnlyPlanResult>(new InstallerReadOnlyPlanRejection(
                ProtocolPrePlanErrorCode.CandidateApprovalFailed,
                ProtocolNextAction.InspectAgain,
                false
            ))
        };
        await using PlanReviewController controller = new(session);
        controller.SelectOperation(InstallerOperation.Install);
        await controller.InspectAsync();
        controller.SetCandidateSelection(controller.Snapshot.Candidates);

        await controller.ApplyCandidateSelectionAsync();

        PlanReviewSnapshot rejected = controller.Snapshot;
        rejected.State.Should().Be(PlanReviewState.Rejected);
        rejected.Result.Should().Be(new PlanReviewRejection(
            ProtocolPrePlanErrorCode.CandidateApprovalFailed,
            ProtocolNextAction.InspectAgain,
            false
        ));
        rejected.Candidates.Should().BeEmpty();
        rejected.AppliedCandidateApprovalCount.Should().Be(0);
        rejected.CanRetry.Should().BeTrue();
        rejected.CanInspect.Should().BeTrue();
        rejected.CanApplyCandidates.Should().BeFalse();
    }

    [Test]
    public async Task BackupNeverExposesCandidateApproval()
    {
        InstallerReadOnlyPlanCandidate backend = CandidateCapability("mods/example.dll", provisional: false);
        FakePlanSession session = new()
        {
            Inspection = (operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CandidatePlan(operation, [backend]))
        };
        await using PlanReviewController controller = new(session);
        controller.SelectOperation(InstallerOperation.Backup);
        await controller.InspectAsync();

        controller.Snapshot.Candidates.Should().ContainSingle();
        controller.Snapshot.CanSelectCandidates.Should().BeFalse();
        Action select = () => controller.SetCandidateSelection(controller.Snapshot.Candidates);
        select.Should().Throw<InvalidOperationException>();
        Func<Task> apply = () => controller.ApplyCandidateSelectionAsync();
        await apply.Should().ThrowAsync<InvalidOperationException>();
        session.ApprovedCandidates.Should().BeEmpty();
    }

    [Test]
    public async Task CandidateApprovalCapacityRejectsBeforeRevokingTheCurrentBindingAndOperationChangeClearsHistory()
    {
        InstallerReadOnlyPlanCandidate[] maximum = Enumerable.Range(0, ProtocolJsonSerializer.MaxPlanCandidates)
            .Select(index => CandidateCapability($"mods/{index:D3}.dll", provisional: false))
            .ToArray();
        InstallerReadOnlyPlanCandidate remaining = CandidateCapability("mods/remaining.dll", provisional: false);
        FakePlanSession session = new()
        {
            Inspection = (operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CandidatePlan(operation, maximum)),
            Approval = (_, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CandidatePlan(InstallerOperation.Update, [remaining]))
        };
        await using PlanReviewController controller = new(session);
        controller.SelectOperation(InstallerOperation.Update);
        await controller.InspectAsync();
        controller.SetCandidateSelection(controller.Snapshot.Candidates);
        await controller.ApplyCandidateSelectionAsync();
        controller.Snapshot.AppliedCandidateApprovalCount.Should().Be(ProtocolJsonSerializer.MaxPlanCandidates);
        PlanReviewCandidate remainingChoice = controller.Snapshot.Candidates.Single();
        controller.Snapshot.CanSelectCandidates.Should().BeTrue();
        controller.SetCandidateSelection([remainingChoice]);
        controller.Snapshot.CanApplyCandidates.Should().BeFalse("no cumulative approval capacity remains");
        controller.Snapshot.CanClearCandidates.Should().BeTrue("capacity must not trap a local selection");

        Func<Task> overCapacity = () => controller.ApplyCandidateSelectionAsync();
        await overCapacity.Should().ThrowAsync<InvalidOperationException>().WithMessage("*remaining*capacity*");
        controller.Snapshot.SelectedCandidates.Should().ContainSingle().Which.Should().BeSameAs(remainingChoice);
        session.ApprovedCandidates.Should().ContainSingle("the invalid request is rejected before a backend call");
        controller.ClearCandidateSelection();
        controller.Snapshot.SelectedCandidates.Should().BeEmpty();
        controller.Snapshot.CanApplyCandidates.Should().BeFalse();

        controller.SelectOperation(InstallerOperation.Backup);
        controller.Snapshot.AppliedCandidateApprovalCount.Should().Be(0);
        controller.Snapshot.Candidates.Should().BeEmpty();
        controller.Snapshot.SelectedCandidates.Should().BeEmpty();
    }

    [Test]
    public async Task PartialCandidateApprovalCapacityAdvertisesOnlySelectionsThatAdmissionCanAccept()
    {
        InstallerReadOnlyPlanCandidate[] initial = Enumerable.Range(0, ProtocolJsonSerializer.MaxPlanCandidates - 1)
            .Select(index => CandidateCapability($"mods/initial-{index:D3}.dll", provisional: false))
            .ToArray();
        InstallerReadOnlyPlanCandidate firstRemaining = CandidateCapability("mods/remaining-first.dll", provisional: false);
        InstallerReadOnlyPlanCandidate secondRemaining = CandidateCapability("mods/remaining-second.dll", provisional: false);
        int approvalCount = 0;
        FakePlanSession session = new()
        {
            Inspection = (operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CandidatePlan(operation, initial)),
            Approval = (_, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CandidatePlan(
                InstallerOperation.Update,
                Interlocked.Increment(ref approvalCount) == 1 ? [firstRemaining, secondRemaining] : []
            ))
        };
        await using PlanReviewController controller = new(session);
        controller.SelectOperation(InstallerOperation.Update);
        await controller.InspectAsync();
        controller.SetCandidateSelection(controller.Snapshot.Candidates);
        controller.Snapshot.CanApplyCandidates.Should().BeTrue();
        await controller.ApplyCandidateSelectionAsync();
        controller.Snapshot.AppliedCandidateApprovalCount.Should().Be(ProtocolJsonSerializer.MaxPlanCandidates - 1);

        PlanReviewCandidate[] remainingChoices = controller.Snapshot.Candidates.ToArray();
        controller.SetCandidateSelection(remainingChoices);
        PlanReviewSnapshot oversized = controller.Snapshot;
        oversized.CanSelectCandidates.Should().BeTrue();
        oversized.CanApplyCandidates.Should().BeFalse("two selected candidates exceed the one remaining approval slot");
        oversized.CanClearCandidates.Should().BeTrue();
        Func<Task> overCapacity = () => controller.ApplyCandidateSelectionAsync();
        await overCapacity.Should().ThrowAsync<InvalidOperationException>().WithMessage("*remaining*capacity*");
        session.ApprovedCandidates.Should().ContainSingle("capability and admission use the same remaining-capacity bound");
        controller.Snapshot.SelectedCandidates.Should().Equal(remainingChoices);

        controller.ClearCandidateSelection();
        controller.SetCandidateSelection([remainingChoices[0]]);
        controller.Snapshot.CanApplyCandidates.Should().BeTrue("one selected candidate exactly fits the remaining approval slot");
        await controller.ApplyCandidateSelectionAsync();
        controller.Snapshot.AppliedCandidateApprovalCount.Should().Be(ProtocolJsonSerializer.MaxPlanCandidates);
        session.ApprovedCandidates.Should().HaveCount(2);
        session.ApprovedCandidates[1].Should().Equal(firstRemaining);
    }

    [Test]
    public async Task MalformedResultsFailClosedClearPresentationAndDisposeExactlyOnce()
    {
        InstallerReadOnlyPlanSuccess valid = CreatePlan(InstallerOperation.Install);
        (string Name, InstallerReadOnlyPlanResult? Result)[] malformed =
        [
            ("null", null),
            ("wrong operation", valid with { Operation = InstallerOperation.Update }),
            ("undefined state", valid with { ObservedState = (ObservedInstallState)999 }),
            ("unsafe default", valid with { RecommendedDefault = (ProtocolRecommendedDefault)999 }),
            ("no confirmation", valid with { SeparateConfirmationRequired = false }),
            ("negative notices", valid with { AdditionalNoticeCount = -1 }),
            ("excessive notices", valid with { AdditionalNoticeCount = 257 }),
            ("null risks", valid with { Risks = null! }),
            ("duplicate risks", valid with { Risks = [ProtocolPlanRisk.ModifiedOrUnknownFileApproval, ProtocolPlanRisk.ModifiedOrUnknownFileApproval] }),
            ("rollback risk", valid with { Risks = [ProtocolPlanRisk.Rollback] }),
            ("zero operation group", valid with { OperationCounts = [new(PlanOperationKind.Create, 0)] }),
            ("duplicate operation group", valid with { OperationCounts = [new(PlanOperationKind.Create, 1), new(PlanOperationKind.Create, 2)] }),
            ("blocking mismatch", valid with { HasBlockingConflicts = true, ConflictCounts = [] }),
            ("duplicate conflict group", valid with { HasBlockingConflicts = true, ConflictCounts = [new(PlanConflictCode.UnknownCollision, 1), new(PlanConflictCode.UnknownCollision, 2)] }),
            ("duplicate candidate group", valid with { CandidateCounts = [CandidateCount(1), CandidateCount(2)] }),
            ("invalid candidate pair", valid with { Risks = [ProtocolPlanRisk.ModifiedOrUnknownFileApproval], CandidateCounts = [new(FileReplacementCandidateReason.OfficialLauncherBackup, FileReplacementCandidateDisposition.Replace, false, 1)] }),
            ("candidate risk missing", valid with { CandidateCounts = [CandidateCount(1)] }),
            ("unexpected candidate risk", valid with { Risks = [ProtocolPlanRisk.ModifiedOrUnknownFileApproval] }),
            ("candidate detail missing", valid with { Risks = [ProtocolPlanRisk.ModifiedOrUnknownFileApproval], CandidateCounts = [CandidateCount(1)] }),
            ("candidate aggregate missing", valid with { Candidates = [CandidateCapability("mods/detail-only.dll", false)] }),
            ("duplicate candidate reference", valid with
            {
                Risks = [ProtocolPlanRisk.ModifiedOrUnknownFileApproval],
                CandidateCounts = [CandidateCount(2)],
                Candidates = DuplicateCandidateReferences()
            }),
            ("duplicate candidate display path", valid with
            {
                Risks = [ProtocolPlanRisk.ModifiedOrUnknownFileApproval],
                CandidateCounts = [CandidateCount(2)],
                Candidates = [CandidateCapability("mods/same.dll", false), CandidateCapability("mods/same.dll", false)]
            }),
            ("excessive conflicts", valid with { HasBlockingConflicts = true, ConflictCounts = [new(PlanConflictCode.UnknownCollision, 257)] }),
            ("count overflow", valid with { OperationCounts = [new(PlanOperationKind.Create, int.MaxValue), new(PlanOperationKind.Replace, 1)] })
        ];

        foreach ((string name, InstallerReadOnlyPlanResult? result) in malformed)
        {
            FakePlanSession session = new()
            {
                Inspection = (_, _) => Task.FromResult(result!)
            };
            PlanReviewController controller = new(session);
            controller.SelectOperation(InstallerOperation.Install);

            await controller.InspectAsync();
            await WaitUntilAsync(() => session.DisposeCalls == 1);

            controller.Snapshot.State.Should().Be(PlanReviewState.Failed, name);
            controller.Snapshot.Result.Should().BeNull(name);
            controller.Snapshot.CanInspect.Should().BeFalse(name);
            controller.Snapshot.CanExit.Should().BeTrue(name);
            await controller.DisposeAsync();
            session.DisposeCalls.Should().Be(1, name);
        }
    }

    [Test]
    public async Task HostileCandidateDetailCollectionsFailClosedWithoutRetainingAuthority()
    {
        (string Name, IReadOnlyList<InstallerReadOnlyPlanCandidate> Candidates)[] hostile =
        [
            ("throwing count", new ThrowingCountBackendCandidateList()),
            ("throwing index", new ThrowingIndexBackendCandidateList()),
            ("oversized count", new OversizedBackendCandidateList())
        ];

        foreach ((string name, IReadOnlyList<InstallerReadOnlyPlanCandidate> candidates) in hostile)
        {
            FakePlanSession session = new()
            {
                Inspection = (operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CreatePlan(operation) with
                {
                    Candidates = candidates
                })
            };
            PlanReviewController controller = new(session);
            controller.SelectOperation(InstallerOperation.Install);

            await controller.InspectAsync();
            await WaitUntilAsync(() => session.DisposeCalls == 1);

            controller.Snapshot.State.Should().Be(PlanReviewState.Failed, name);
            controller.Snapshot.Result.Should().BeNull(name);
            controller.Snapshot.Candidates.Should().BeEmpty(name);
            controller.Snapshot.SelectedCandidates.Should().BeEmpty(name);
            controller.Snapshot.AppliedCandidateApprovalCount.Should().Be(0, name);
            await controller.DisposeAsync();
            session.DisposeCalls.Should().Be(1, name);
        }
    }

    [Test]
    public async Task MalformedRejectionMatrixFailsClosed()
    {
        InstallerReadOnlyPlanRejection[] malformed =
        [
            new((ProtocolPrePlanErrorCode)999, ProtocolNextAction.RetryRequest, false),
            new(ProtocolPrePlanErrorCode.InspectionFailed, ProtocolNextAction.RetryRequest, false),
            new(ProtocolPrePlanErrorCode.InspectionFailed, ProtocolNextAction.InspectAgain, true),
            new(ProtocolPrePlanErrorCode.RecoveryUnavailable, ProtocolNextAction.ListRecoveries, false),
            new(ProtocolPrePlanErrorCode.CandidateApprovalFailed, ProtocolNextAction.InspectAgain, false),
            new(ProtocolPrePlanErrorCode.InputOutputFailure, ProtocolNextAction.RetryRequest, false),
            new(ProtocolPrePlanErrorCode.UnexpectedFailure, ProtocolNextAction.StartNewSession, false)
        ];

        foreach (InstallerReadOnlyPlanRejection rejection in malformed)
        {
            FakePlanSession session = new()
            {
                Inspection = (_, _) => Task.FromResult<InstallerReadOnlyPlanResult>(rejection)
            };
            PlanReviewController controller = new(session);
            controller.SelectOperation(InstallerOperation.Backup);

            await controller.InspectAsync();
            await WaitUntilAsync(() => session.DisposeCalls == 1);

            controller.Snapshot.State.Should().Be(PlanReviewState.Failed);
            controller.Snapshot.Result.Should().BeNull();
            await controller.DisposeAsync();
            session.DisposeCalls.Should().Be(1);
        }
    }

    [Test]
    public async Task ObserverExceptionsCannotBreakStatePublicationOrCleanup()
    {
        FakePlanSession session = new();
        PlanReviewController controller = new(session);
        int healthyObserverCalls = 0;
        controller.Changed += (_, _) => throw new InvalidOperationException("synthetic observer failure");
        controller.Changed += (_, _) => healthyObserverCalls++;

        controller.SelectOperation(InstallerOperation.Install);
        await controller.InspectAsync();
        session.Fail();
        await WaitUntilAsync(() => controller.Snapshot.State == PlanReviewState.SessionFaulted);
        await controller.DisposeAsync();

        healthyObserverCalls.Should().BeGreaterThanOrEqualTo(4);
        controller.Snapshot.State.Should().Be(PlanReviewState.Disposed);
        session.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task MaximumEscapedGamePresentationRemainsUsable()
    {
        string hostilePath = "/" + new string('\u202E', 4095);
        FakePlanSession session = new()
        {
            Game = new VerifiedGamePresentation(hostilePath, new string('\u202E', 4096))
        };

        await using PlanReviewController controller = new(session);

        controller.Snapshot.State.Should().Be(PlanReviewState.Choosing);
        typeof(PlanReviewController).GetProperty("Game").Should().BeNull(
            "the exact game presentation remains private to the bound backend owner"
        );
    }

    [Test]
    public async Task ThrowingCancellationCallbackCannotPreventTerminalCleanup()
    {
        TaskCompletionSource started = NewCompletion();
        FakePlanSession session = new()
        {
            Inspection = async (_, token) =>
            {
                using CancellationTokenRegistration registration = token.Register(
                    () => throw new InvalidOperationException("synthetic hostile cancellation callback")
                );
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new AssertionException("Cancellation should stop the synthetic inspection.");
            }
        };
        PlanReviewController controller = new(session);
        controller.SelectOperation(InstallerOperation.Install);
        Task inspection = controller.InspectAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await controller.CancelAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await inspection.WaitAsync(TimeSpan.FromSeconds(2));

        controller.Snapshot.State.Should().Be(PlanReviewState.Cancelled);
        controller.Snapshot.Result.Should().BeNull();
        session.DisposeCalls.Should().Be(1);
        await controller.DisposeAsync();
        session.DisposeCalls.Should().Be(1);
    }

    private static InstallerReadOnlyPlanSuccess CreatePlan(InstallerOperation operation)
    {
        ProtocolReleaseIdentity release = GameDiscoveryControllerTests.Release();
        InstallerPlanRelease projected = new(release.Tag, release.EmbeddedVersion);
        InstallerPlanRelease? current = operation == InstallerOperation.Install ? null : projected;
        InstallerPlanRelease? target = operation switch
        {
            InstallerOperation.Install or InstallerOperation.Update or InstallerOperation.Repair => projected,
            InstallerOperation.Backup => current,
            _ => null
        };
        return new(
            operation,
            operation == InstallerOperation.Install ? ObservedInstallState.NotInstalled : ObservedInstallState.KnownUnmodified,
            current,
            target,
            false,
            operation == InstallerOperation.Uninstall ? [ProtocolPlanRisk.Uninstall] : [],
            ProtocolRecommendedDefault.Cancel,
            true,
            [],
            [],
            [],
            0
        )
        {
            Confirmation = new InstallerPlanConfirmation()
        };
    }

    private static InstallerPlanCandidateCount CandidateCount(int count)
        => new(
            FileReplacementCandidateReason.ModifiedReceiptOwned,
            FileReplacementCandidateDisposition.Replace,
            false,
            count
        );

    private static InstallerReadOnlyPlanSuccess CandidatePlan(
        InstallerOperation operation,
        IReadOnlyList<InstallerReadOnlyPlanCandidate> candidates
    ) => CreatePlan(operation) with
    {
        Risks = candidates.Count == 0 ? [] : [ProtocolPlanRisk.ModifiedOrUnknownFileApproval],
        CandidateCounts = candidates
            .GroupBy(candidate => new { candidate.Reason, candidate.Disposition, candidate.BackendProvisionallyIncluded })
            .Select(group => new InstallerPlanCandidateCount(
                group.Key.Reason,
                group.Key.Disposition,
                group.Key.BackendProvisionallyIncluded,
                group.Count()
            ))
            .ToArray(),
        Candidates = candidates
    };

    private static InstallerReadOnlyPlanCandidate CandidateCapability(string path, bool provisional)
        => new(new ProtocolPlanCandidate(
            ProtocolCandidateId.Parse(Guid.NewGuid().ToString("N")),
            FileReplacementCandidateReason.ModifiedReceiptOwned,
            FileReplacementCandidateDisposition.Replace,
            path,
            new string('a', 64),
            123,
            420,
            new string('b', 64),
            provisional,
            "private evidence"
        ));

    private static IReadOnlyList<InstallerReadOnlyPlanCandidate> DuplicateCandidateReferences()
    {
        InstallerReadOnlyPlanCandidate candidate = CandidateCapability("mods/duplicate.dll", false);
        return [candidate, candidate];
    }

    private static TaskCompletionSource NewCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The expected plan-review state was not reached.");
            await Task.Delay(10);
        }
    }

    private sealed class FakePlanSession : IPlanInspectionSession
    {
        private readonly object Sync = new();
        private readonly List<InstallerOperation> Operations = [];
        private readonly List<InstallerReadOnlyPlanCandidate[]> Approvals = [];
        private int DisposeCount;

        public ProtocolReleaseIdentity Release { get; } = GameDiscoveryControllerTests.Release();
        public VerifiedGamePresentation Game { get; init; } = new("/games/Stardew Valley", "Stardew Valley");
        public TaskCompletionSource<InstallerProtocolClientException> Fault { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<InstallerProtocolClientException>? FaultNotification { get; init; }
        public Task<InstallerProtocolClientException> SessionFaulted => this.FaultNotification ?? this.Fault.Task;
        public int DisposeCalls => Volatile.Read(ref this.DisposeCount);
        public InstallerOperation[] InspectedOperations
        {
            get
            {
                lock (this.Sync)
                    return this.Operations.ToArray();
            }
        }
        public InstallerReadOnlyPlanCandidate[][] ApprovedCandidates
        {
            get
            {
                lock (this.Sync)
                    return this.Approvals.Select(candidates => candidates.ToArray()).ToArray();
            }
        }

        public Func<InstallerOperation, CancellationToken, Task<InstallerReadOnlyPlanResult>> Inspection { get; init; }
            = (operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CreatePlan(operation));
        public Func<IReadOnlyList<InstallerReadOnlyPlanCandidate>, CancellationToken, Task<InstallerReadOnlyPlanResult>> Approval { get; init; }
            = (_, _) => throw new AssertionException("Candidate approval was not expected.");
        public Func<InstallerPlanConfirmation, CancellationToken, Task<IConfirmedInstallerSession>>? Confirmation { get; set; }
        public Func<Task> Disposal { get; init; } = () => Task.CompletedTask;
        public List<InstallerPlanConfirmation> ConfirmedPlans { get; } = [];

        public void Fail()
        {
            this.Fault.TrySetResult(new InstallerProtocolClientException("synthetic private session fault"));
        }

        public Task<InstallerReadOnlyPlanResult> InspectPlanAsync(
            InstallerOperation operation,
            CancellationToken cancellationToken = default
        )
        {
            lock (this.Sync)
                this.Operations.Add(operation);
            return this.Inspection(operation, cancellationToken);
        }

        public Task<InstallerReadOnlyPlanResult> ApprovePlanCandidatesAsync(
            IReadOnlyList<InstallerReadOnlyPlanCandidate> candidates,
            CancellationToken cancellationToken = default
        )
        {
            InstallerReadOnlyPlanCandidate[] snapshot = candidates.ToArray();
            lock (this.Sync)
                this.Approvals.Add(snapshot);
            return this.Approval(snapshot, cancellationToken);
        }

        public Task<IConfirmedInstallerSession> ConfirmPlanAsync(
            InstallerPlanConfirmation confirmation,
            CancellationToken cancellationToken = default
        )
        {
            lock (this.Sync)
                this.ConfirmedPlans.Add(confirmation);
            return this.Confirmation?.Invoke(confirmation, cancellationToken)
                ?? Task.FromResult<IConfirmedInstallerSession>(new FakeConfirmedSession(this.Release, this.Game, this.SessionFaulted));
        }

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref this.DisposeCount);
            await this.Disposal();
        }
    }

    private sealed class FakeConfirmedSession(
        ProtocolReleaseIdentity release,
        VerifiedGamePresentation game,
        Task<InstallerProtocolClientException> sessionFaulted
    ) : IConfirmedInstallerSession
    {
        private int DisposeCount;
        private int ExecuteCount;
        public ProtocolReleaseIdentity Release { get; } = release;
        public VerifiedGamePresentation Game { get; } = game;
        public Task<InstallerProtocolClientException> SessionFaulted { get; } = sessionFaulted;
        public int DisposeCalls => Volatile.Read(ref this.DisposeCount);
        public int ExecuteCalls => Volatile.Read(ref this.ExecuteCount);
        public Task<InstallerExecutionOperation> ExecuteAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this.ExecuteCount);
            throw new AssertionException("Plan confirmation must not execute the plan.");
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref this.DisposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingCandidateList : IReadOnlyList<PlanReviewCandidate>
    {
        public int Count => 1;
        public PlanReviewCandidate this[int index] => throw new InvalidOperationException("synthetic caller failure");
        public IEnumerator<PlanReviewCandidate> GetEnumerator() => throw new AssertionException("Enumeration must not be used.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => this.GetEnumerator();
    }

    private sealed class ThrowingCountBackendCandidateList : IReadOnlyList<InstallerReadOnlyPlanCandidate>
    {
        public int Count => throw new InvalidOperationException("synthetic hostile count");
        public InstallerReadOnlyPlanCandidate this[int index] => throw new AssertionException("Index must not be read.");
        public IEnumerator<InstallerReadOnlyPlanCandidate> GetEnumerator() => throw new AssertionException("Enumeration must not be used.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => this.GetEnumerator();
    }

    private sealed class ThrowingIndexBackendCandidateList : IReadOnlyList<InstallerReadOnlyPlanCandidate>
    {
        public int Count => 1;
        public InstallerReadOnlyPlanCandidate this[int index] => throw new InvalidOperationException("synthetic hostile index");
        public IEnumerator<InstallerReadOnlyPlanCandidate> GetEnumerator() => throw new AssertionException("Enumeration must not be used.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => this.GetEnumerator();
    }

    private sealed class OversizedBackendCandidateList : IReadOnlyList<InstallerReadOnlyPlanCandidate>
    {
        public int Count => ProtocolJsonSerializer.MaxPlanCandidates + 1;
        public InstallerReadOnlyPlanCandidate this[int index] => throw new AssertionException("Index must not be read.");
        public IEnumerator<InstallerReadOnlyPlanCandidate> GetEnumerator() => throw new AssertionException("Enumeration must not be used.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => this.GetEnumerator();
    }
}
