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
        FakePlanSession session = new();
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
        await controller.DisposeAsync();
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
            CandidateCounts = candidates
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
        projected.Risks.Should().BeAssignableTo<System.Collections.ObjectModel.ReadOnlyCollection<ProtocolPlanRisk>>();
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

        controller.Game.DisplayPath.Length.Should().BeGreaterThan(8192).And.BeLessThanOrEqualTo(4096 * 6);
        controller.Game.DisplayName.Length.Should().Be(4096 * 6);
        controller.Snapshot.State.Should().Be(PlanReviewState.Choosing);
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
        );
    }

    private static InstallerPlanCandidateCount CandidateCount(int count)
        => new(
            FileReplacementCandidateReason.ModifiedReceiptOwned,
            FileReplacementCandidateDisposition.Replace,
            false,
            count
        );

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

        public Func<InstallerOperation, CancellationToken, Task<InstallerReadOnlyPlanResult>> Inspection { get; init; }
            = (operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CreatePlan(operation));
        public Func<Task> Disposal { get; init; } = () => Task.CompletedTask;

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

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref this.DisposeCount);
            await this.Disposal();
        }
    }
}
