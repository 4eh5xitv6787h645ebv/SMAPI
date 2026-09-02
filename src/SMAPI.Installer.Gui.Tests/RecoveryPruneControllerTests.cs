using System.Threading.Channels;
using FluentAssertions;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Core.Transactions;
using StardewModdingAPI.Installer.Gui.Backend;
using StardewModdingAPI.Installer.Gui.Frontend;

namespace StardewModdingAPI.Installer.Gui.Tests;

[NonParallelizable]
internal sealed class RecoveryPruneControllerTests
{
    [Test]
    public async Task HappyPathRequiresEveryExplicitGateExactAuthorityAndDestructiveConsent()
    {
        BoundInstallerRecoveryPoint[] backendPoints =
        [
            Point(1, current: true),
            Point(2, current: false),
            Point(3, current: false)
        ];
        BoundInstallerRecoveryPruneConfirmation backendConfirmation = new();
        FakeConfirmedPruneSession confirmed = new();
        FakePlanSession session = new()
        {
            RecoveryCatalog = _ => Task.FromResult<BoundInstallerRecoveryCatalogResult>(new BoundInstallerRecoveryCatalogSuccess(backendPoints)),
            PruneInspection = (point, _) =>
            {
                point.Should().BeSameAs(backendPoints[1]);
                return Task.FromResult<BoundInstallerRecoveryPrunePlanResult>(Plan(2, 2, 1) with { Confirmation = backendConfirmation });
            },
            PruneConfirmation = (confirmation, _) =>
            {
                confirmation.Should().BeSameAs(backendConfirmation);
                return Task.FromResult<IConfirmedRecoveryPruneSession>(confirmed);
            }
        };
        confirmed.Release = session.Release;
        confirmed.Game = session.Game;
        InstallerRecoveryPruneTerminalResult terminal = ExactTerminal();
        confirmed.Execute = _ => Task.FromResult(Operation(Task.FromResult<InstallerRecoveryPruneResult>(terminal)));
        await using RecoveryPruneController controller = new(session);

        controller.Snapshot.State.Should().Be(RecoveryPruneControllerState.NotLoaded);
        session.RecoveryListCalls.Should().Be(0);
        session.InspectedPrunes.Should().BeEmpty();
        session.ConfirmedPrunes.Should().BeEmpty();
        confirmed.ExecuteCalls.Should().Be(0);

        await controller.ListRecoveriesAsync().WaitAsync(TimeSpan.FromSeconds(2));
        RecoveryPruneSnapshot listed = controller.Snapshot;
        listed.State.Should().Be(RecoveryPruneControllerState.CatalogReady);
        listed.Choices.Should().HaveCount(3);
        listed.CanSelect.Should().BeTrue();
        session.InspectedPrunes.Should().BeEmpty();

        RecoveryPruneChoice selected = listed.Choices[1];
        controller.SelectRecoveryPoint(selected);
        controller.Snapshot.Selected.Should().BeSameAs(selected);
        controller.Snapshot.CanInspect.Should().BeTrue();
        await controller.InspectAsync().WaitAsync(TimeSpan.FromSeconds(2));

        RecoveryPruneSnapshot reviewed = controller.Snapshot;
        reviewed.State.Should().Be(RecoveryPruneControllerState.ReviewReady);
        reviewed.Selected.Should().BeSameAs(selected);
        reviewed.Plan.Should().BeEquivalentTo(new RecoveryPrunePlanPresentation(
            2,
            2,
            1,
            1,
            false,
            1,
            [ProtocolPlanRisk.RecoveryPrune],
            ProtocolRecommendedDefault.Cancel,
            true
        ));
        reviewed.CanSelect.Should().BeFalse("the exact backend catalog was consumed by inspection");
        reviewed.CanConfirm.Should().BeTrue();
        session.ConfirmedPrunes.Should().BeEmpty();

        await controller.ConfirmAsync(RecoveryPruneConsent.Cancel);
        controller.Snapshot.State.Should().Be(RecoveryPruneControllerState.ReviewReady);
        session.ConfirmedPrunes.Should().BeEmpty();
        confirmed.ExecuteCalls.Should().Be(0);

        await controller.ConfirmAsync(RecoveryPruneConsent.ConfirmDestructiveCleanup).WaitAsync(TimeSpan.FromSeconds(2));
        controller.Snapshot.State.Should().Be(RecoveryPruneControllerState.ReadyToRun);
        controller.Snapshot.CanRun.Should().BeTrue();
        session.ConfirmedPrunes.Should().ContainSingle().Which.Should().BeSameAs(backendConfirmation);
        confirmed.ExecuteCalls.Should().Be(0, "confirmation alone must never mutate recovery history");

        await controller.RunAsync().WaitAsync(TimeSpan.FromSeconds(2));

        RecoveryPruneSnapshot completed = controller.Snapshot;
        completed.State.Should().Be(RecoveryPruneControllerState.Terminal);
        completed.Result.Should().Be(new RecoveryPruneTerminalPresentation(
            terminal.Outcome,
            terminal.DurableState,
            terminal.ErrorCode,
            terminal.RecoveryDisposition,
            terminal.NextAction,
            terminal.Summary.LogicallyRemovedGenerationCount,
            terminal.Summary.PhysicallyCleanedGenerationCount,
            terminal.Summary.PendingCleanupGenerationCount,
            terminal.Summary.AuxiliaryCleanupPending,
            terminal.BackendSettlement
        ));
        confirmed.ExecuteCalls.Should().Be(1);
        confirmed.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task RejectionRequiresFreshRelistAndEveryOldControllerChoiceBecomesStale()
    {
        BoundInstallerRecoveryPoint firstBackend = Point(1, current: true);
        BoundInstallerRecoveryPoint secondBackend = Point(1, current: true);
        Queue<BoundInstallerRecoveryPoint> catalogs = new([firstBackend, secondBackend]);
        int inspections = 0;
        FakePlanSession session = new()
        {
            RecoveryCatalog = _ => Task.FromResult<BoundInstallerRecoveryCatalogResult>(
                new BoundInstallerRecoveryCatalogSuccess([catalogs.Dequeue()])
            ),
            PruneInspection = (_, _) => Task.FromResult<BoundInstallerRecoveryPrunePlanResult>(inspections++ == 0
                ? new BoundInstallerRecoveryPrunePlanRejection(
                    ProtocolPrePlanErrorCode.NothingToPrune,
                    ProtocolNextAction.ListRecoveries,
                    false
                )
                : Plan(1, 1, 0, auxiliaryCleanup: true))
        };
        await using RecoveryPruneController controller = new(session);
        await controller.ListRecoveriesAsync();
        RecoveryPruneChoice stale = controller.Snapshot.Choices.Single();
        controller.SelectRecoveryPoint(stale);

        await controller.InspectAsync();

        controller.Snapshot.State.Should().Be(RecoveryPruneControllerState.RelistRequired);
        controller.Snapshot.Rejection.Should().Be(new RecoveryPruneRejection(
            ProtocolPrePlanErrorCode.NothingToPrune,
            ProtocolNextAction.ListRecoveries,
            false
        ));
        controller.Snapshot.Choices.Should().BeEmpty();
        controller.Snapshot.CanList.Should().BeTrue();
        await controller.ListRecoveriesAsync();

        RecoveryPruneChoice fresh = controller.Snapshot.Choices.Single();
        FluentActions.Invoking(() => controller.SelectRecoveryPoint(stale)).Should().Throw<ArgumentException>();
        controller.SelectRecoveryPoint(fresh);
        await controller.InspectAsync();

        controller.Snapshot.State.Should().Be(RecoveryPruneControllerState.ReviewReady);
        session.InspectedPrunes.Should().Equal(firstBackend, secondBackend);
    }

    [Test]
    public async Task RepeatedCancellationBeforeLateRunPublicationCancelsTheExactOperationOnceAndTerminalWins()
    {
        TaskCompletionSource<InstallerRecoveryPruneOperation> publication = NewSource<InstallerRecoveryPruneOperation>();
        TaskCompletionSource<InstallerRecoveryPruneResult> completion = NewSource<InstallerRecoveryPruneResult>();
        int cancellationCalls = 0;
        (RecoveryPruneController Controller, FakePlanSession Session, FakeConfirmedPruneSession Confirmed) prepared =
            await CreateReadyToRunAsync(_ => publication.Task);
        await using RecoveryPruneController controller = prepared.Controller;

        Task run = controller.RunAsync();
        await WaitUntilAsync(() => prepared.Confirmed.ExecuteCalls == 1);
        Task first = controller.RequestCancellationAsync();
        Task second = controller.RequestCancellationAsync();
        await Task.WhenAll(first, second);

        publication.SetResult(Operation(completion.Task, () =>
        {
            Interlocked.Increment(ref cancellationCalls);
            return Task.CompletedTask;
        }));
        await WaitUntilAsync(() => cancellationCalls == 1);
        InstallerRecoveryPruneTerminalResult terminal = ExactTerminal(0);
        completion.SetResult(terminal);
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        cancellationCalls.Should().Be(1);
        controller.Snapshot.State.Should().Be(RecoveryPruneControllerState.Terminal);
        controller.Snapshot.Result.Should().BeOfType<RecoveryPruneTerminalPresentation>();
        prepared.Confirmed.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task DisposeWhileRunStartIsPendingSettlesLateOperationBeforeConfirmedOwnerCleanup()
    {
        TaskCompletionSource<InstallerRecoveryPruneOperation> publication = NewSource<InstallerRecoveryPruneOperation>();
        TaskCompletionSource<InstallerRecoveryPruneResult> completion = NewSource<InstallerRecoveryPruneResult>();
        TaskCompletionSource cancellationRequested = NewSource();
        (RecoveryPruneController Controller, FakePlanSession Session, FakeConfirmedPruneSession Confirmed) prepared =
            await CreateReadyToRunAsync(_ => publication.Task);
        RecoveryPruneController controller = prepared.Controller;
        Task run = controller.RunAsync();
        await WaitUntilAsync(() => prepared.Confirmed.ExecuteCalls == 1);

        Task disposal = controller.DisposeAsync().AsTask();
        disposal.IsCompleted.Should().BeFalse();
        prepared.Confirmed.DisposeCalls.Should().Be(0);
        publication.SetResult(Operation(completion.Task, () =>
        {
            cancellationRequested.TrySetResult();
            return Task.CompletedTask;
        }));
        await cancellationRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        disposal.IsCompleted.Should().BeFalse();
        prepared.Confirmed.DisposeCalls.Should().Be(0);

        completion.SetResult(new InstallerRecoveryPruneStateUnknownResult());
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));

        controller.Snapshot.State.Should().Be(RecoveryPruneControllerState.Disposed);
        prepared.Confirmed.DisposeCalls.Should().Be(1);
        await controller.DisposeAsync();
        prepared.Confirmed.DisposeCalls.Should().Be(1);
    }

    [TestCase(ProtocolPruneOutcome.Succeeded)]
    [TestCase(ProtocolPruneOutcome.FailedBeforePublication)]
    [TestCase(ProtocolPruneOutcome.CancelledBeforePublication)]
    [TestCase(ProtocolPruneOutcome.Interrupted)]
    [TestCase(ProtocolPruneOutcome.CancelledWithCleanupPending)]
    [TestCase(ProtocolPruneOutcome.FailedWithCleanupPending)]
    [TestCase(ProtocolPruneOutcome.UnexpectedCoreFailure)]
    [TestCase(ProtocolPruneOutcome.CancelledAfterApply)]
    [TestCase(ProtocolPruneOutcome.FailedAfterApply)]
    public async Task EveryValidTypedTerminalFamilyPublishesItsExactOutcome(ProtocolPruneOutcome outcome)
    {
        TaskCompletionSource<InstallerRecoveryPruneResult> completion = NewSource<InstallerRecoveryPruneResult>();
        var prepared = await CreateReadyToRunAsync(
            _ => Task.FromResult(Operation(completion.Task)),
            removesGeneration: true
        );
        await using RecoveryPruneController controller = prepared.Controller;
        Task run = controller.RunAsync();
        await WaitUntilAsync(() => controller.Snapshot.State == RecoveryPruneControllerState.Running);
        if (outcome is ProtocolPruneOutcome.CancelledBeforePublication
            or ProtocolPruneOutcome.CancelledWithCleanupPending
            or ProtocolPruneOutcome.CancelledAfterApply)
        {
            await controller.RequestCancellationAsync();
        }

        completion.SetResult(TerminalForOutcome(outcome));
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        controller.Snapshot.State.Should().Be(RecoveryPruneControllerState.Terminal);
        controller.Snapshot.Result.Should().BeOfType<RecoveryPruneTerminalPresentation>()
            .Which.Outcome.Should().Be(outcome);
        prepared.Confirmed.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task BoundedProgressRetainsTheLatestValueWithoutChangingTheExactTerminal()
    {
        Channel<InstallerRecoveryPruneProgress> progress = Channel.CreateUnbounded<InstallerRecoveryPruneProgress>();
        TaskCompletionSource<InstallerRecoveryPruneResult> completion = NewSource<InstallerRecoveryPruneResult>();
        var prepared = await CreateReadyToRunAsync(
            _ => Task.FromResult(Operation(progress.Reader, completion.Task)),
            removesGeneration: true
        );
        await using RecoveryPruneController controller = prepared.Controller;
        Task run = controller.RunAsync();
        await WaitUntilAsync(() => controller.Snapshot.State == RecoveryPruneControllerState.Running);

        progress.Writer.TryWrite(new InstallerRecoveryPruneProgress(TransactionStage.CleaningRecovery, 0, 3));
        progress.Writer.TryWrite(new InstallerRecoveryPruneProgress(TransactionStage.CleaningRecovery, 1, 3));
        progress.Writer.TryWrite(new InstallerRecoveryPruneProgress(TransactionStage.CleaningRecovery, 3, 3));
        progress.Writer.TryComplete();
        await WaitUntilAsync(() => controller.Snapshot.CompletedUnits == 3);
        completion.SetResult(TerminalForOutcome(ProtocolPruneOutcome.Succeeded));
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        RecoveryPruneSnapshot snapshot = controller.Snapshot;
        snapshot.ProgressStage.Should().Be(TransactionStage.CleaningRecovery);
        snapshot.CompletedUnits.Should().Be(3);
        snapshot.TotalUnits.Should().Be(3);
        snapshot.State.Should().Be(RecoveryPruneControllerState.Terminal);
        snapshot.Result.Should().BeOfType<RecoveryPruneTerminalPresentation>();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MalformedOrOverflowingProgressCannotOverrideExactTerminalOrHang(bool overflow)
    {
        Channel<InstallerRecoveryPruneProgress> inner = Channel.CreateUnbounded<InstallerRecoveryPruneProgress>();
        TaskCompletionSource overflowObserved = NewSource();
        ChannelReader<InstallerRecoveryPruneProgress> reader = overflow
            ? new CountingProgressReader(inner.Reader, 257, overflowObserved)
            : inner.Reader;
        TaskCompletionSource<InstallerRecoveryPruneResult> completion = NewSource<InstallerRecoveryPruneResult>();
        var prepared = await CreateReadyToRunAsync(
            _ => Task.FromResult(Operation(reader, completion.Task)),
            removesGeneration: true
        );
        await using RecoveryPruneController controller = prepared.Controller;
        Task run = controller.RunAsync();
        await WaitUntilAsync(() => controller.Snapshot.State == RecoveryPruneControllerState.Running);
        if (overflow)
        {
            for (int index = 0; index < 257; index++)
                inner.Writer.TryWrite(new InstallerRecoveryPruneProgress(TransactionStage.CleaningRecovery, 1, 1));
            inner.Writer.TryComplete();
            await overflowObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        else
        {
            inner.Writer.TryWrite(new InstallerRecoveryPruneProgress(TransactionStage.CleaningRecovery, -1, 1));
            inner.Writer.TryComplete();
        }

        completion.SetResult(TerminalForOutcome(ProtocolPruneOutcome.Succeeded));
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        controller.Snapshot.State.Should().Be(RecoveryPruneControllerState.Terminal);
        controller.Snapshot.Result.Should().BeOfType<RecoveryPruneTerminalPresentation>();
    }

    [Test]
    public async Task ConcurrentConfirmationAdmitsExactlyOneBackendCallAndOneOwner()
    {
        TaskCompletionSource<IConfirmedRecoveryPruneSession> confirmation = NewSource<IConfirmedRecoveryPruneSession>();
        BoundInstallerRecoveryPoint point = Point(1, true);
        FakePlanSession session = new()
        {
            RecoveryCatalog = _ => Task.FromResult<BoundInstallerRecoveryCatalogResult>(new BoundInstallerRecoveryCatalogSuccess([point])),
            PruneInspection = (_, _) => Task.FromResult<BoundInstallerRecoveryPrunePlanResult>(Plan(1, 1, 0, auxiliaryCleanup: true)),
            PruneConfirmation = (_, _) => confirmation.Task
        };
        FakeConfirmedPruneSession confirmed = new() { Release = session.Release, Game = session.Game };
        await using RecoveryPruneController controller = new(session);
        await controller.ListRecoveriesAsync();
        controller.SelectRecoveryPoint(controller.Snapshot.Choices[0]);
        await controller.InspectAsync();

        Task winner = controller.ConfirmAsync(RecoveryPruneConsent.ConfirmDestructiveCleanup);
        await WaitUntilAsync(() => session.ConfirmedPrunes.Length == 1);
        Action loser = () => _ = controller.ConfirmAsync(RecoveryPruneConsent.ConfirmDestructiveCleanup);
        loser.Should().Throw<InvalidOperationException>();
        session.ConfirmedPrunes.Should().ContainSingle();

        confirmation.SetResult(confirmed);
        await winner.WaitAsync(TimeSpan.FromSeconds(2));
        controller.Snapshot.State.Should().Be(RecoveryPruneControllerState.ReadyToRun);
        session.ConfirmedPrunes.Should().ContainSingle();
        await controller.DisposeAsync();
        confirmed.DisposeCalls.Should().Be(1);
    }

    [TestCase(MalformedCatalog.DuplicateReference)]
    [TestCase(MalformedCatalog.WrongFirstOrdinal)]
    [TestCase(MalformedCatalog.WrongCurrentMarker)]
    [TestCase(MalformedCatalog.ThrowingCount)]
    [TestCase(MalformedCatalog.ThrowingIndexer)]
    public async Task MalformedCatalogFailsClosedAndDisposesTheBoundSession(MalformedCatalog malformed)
    {
        BoundInstallerRecoveryPoint exact = Point(1, current: true);
        IReadOnlyList<BoundInstallerRecoveryPoint> points = malformed switch
        {
            MalformedCatalog.DuplicateReference => [exact, exact],
            MalformedCatalog.WrongFirstOrdinal => [Point(2, current: true)],
            MalformedCatalog.WrongCurrentMarker => [Point(1, current: false)],
            MalformedCatalog.ThrowingCount => new HostileRecoveryPointList(throwCount: true),
            MalformedCatalog.ThrowingIndexer => new HostileRecoveryPointList(throwCount: false),
            _ => throw new ArgumentOutOfRangeException(nameof(malformed), malformed, null)
        };
        FakePlanSession session = new()
        {
            RecoveryCatalog = _ => Task.FromResult<BoundInstallerRecoveryCatalogResult>(new BoundInstallerRecoveryCatalogSuccess(points))
        };
        await using RecoveryPruneController controller = new(session);

        await controller.ListRecoveriesAsync().WaitAsync(TimeSpan.FromSeconds(2));

        controller.Snapshot.State.Should().Be(RecoveryPruneControllerState.Failed);
        controller.Snapshot.Choices.Should().BeEmpty();
        controller.Snapshot.CanList.Should().BeFalse();
        session.DisposeCalls.Should().Be(1);
    }

    [TestCase(MalformedPlan.WrongRetentionBoundary)]
    [TestCase(MalformedPlan.CleanupSmallerThanRemoved)]
    [TestCase(MalformedPlan.MissingConfirmation)]
    [TestCase(MalformedPlan.WrongRisk)]
    [TestCase(MalformedPlan.TrueNoOp)]
    public async Task MalformedPlanFailsClosedWithoutPublishingConfirmation(MalformedPlan malformed)
    {
        BoundInstallerRecoveryPrunePlanSuccess hostile = malformed switch
        {
            MalformedPlan.WrongRetentionBoundary => Plan(1, 1, 2),
            MalformedPlan.CleanupSmallerThanRemoved => Plan(2, 2, 1) with { CleanupGenerationCount = 0 },
            MalformedPlan.MissingConfirmation => Plan(2, 2, 1) with { Confirmation = null },
            MalformedPlan.WrongRisk => Plan(2, 2, 1) with { Risks = [ProtocolPlanRisk.Rollback] },
            MalformedPlan.TrueNoOp => Plan(3, 3, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(malformed), malformed, null)
        };
        FakePlanSession session = new()
        {
            RecoveryCatalog = _ => Task.FromResult<BoundInstallerRecoveryCatalogResult>(new BoundInstallerRecoveryCatalogSuccess([
                Point(1, current: true), Point(2, current: false), Point(3, current: false)
            ])),
            PruneInspection = (_, _) => Task.FromResult<BoundInstallerRecoveryPrunePlanResult>(hostile)
        };
        await using RecoveryPruneController controller = new(session);
        await controller.ListRecoveriesAsync();
        controller.SelectRecoveryPoint(controller.Snapshot.Choices[malformed == MalformedPlan.TrueNoOp ? 2 : 1]);

        await controller.InspectAsync().WaitAsync(TimeSpan.FromSeconds(2));

        controller.Snapshot.State.Should().Be(RecoveryPruneControllerState.Failed);
        controller.Snapshot.Plan.Should().BeNull();
        session.ConfirmedPrunes.Should().BeEmpty();
        session.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task AuxiliaryOnlyPlanIsReviewableAndUnknownOrCancelConsentNeverCallsTheBackend()
    {
        BoundInstallerRecoveryPoint[] points = [Point(1, true), Point(2, false), Point(3, false)];
        FakePlanSession session = new()
        {
            RecoveryCatalog = _ => Task.FromResult<BoundInstallerRecoveryCatalogResult>(new BoundInstallerRecoveryCatalogSuccess(points)),
            PruneInspection = (_, _) => Task.FromResult<BoundInstallerRecoveryPrunePlanResult>(Plan(3, 3, 0, auxiliaryCleanup: true))
        };
        await using RecoveryPruneController controller = new(session);
        await controller.ListRecoveriesAsync();
        controller.SelectRecoveryPoint(controller.Snapshot.Choices[^1]);
        await controller.InspectAsync();

        controller.Snapshot.State.Should().Be(RecoveryPruneControllerState.ReviewReady);
        controller.Snapshot.Plan!.AuxiliaryCleanupPlanned.Should().BeTrue();
        await controller.ConfirmAsync(RecoveryPruneConsent.Cancel);
        session.ConfirmedPrunes.Should().BeEmpty();

        Action unknown = () => _ = controller.ConfirmAsync((RecoveryPruneConsent)999);
        unknown.Should().Throw<ArgumentOutOfRangeException>();
        controller.Snapshot.State.Should().Be(RecoveryPruneControllerState.ReviewReady);
        session.ConfirmedPrunes.Should().BeEmpty();
    }

    [TestCase(HostileCompletion.FaultedCompletion)]
    [TestCase(HostileCompletion.UnrequestedCancellationTerminal)]
    [TestCase(HostileCompletion.InvalidTerminalAccounting)]
    public async Task UntrustedOrUnconfirmedCompletionPublishesOnlyStateUnknown(HostileCompletion hostile)
    {
        Task<InstallerRecoveryPruneResult> completion = hostile switch
        {
            HostileCompletion.FaultedCompletion => Task.FromException<InstallerRecoveryPruneResult>(
                new InvalidOperationException("private completion failure /home/wife/Mods")
            ),
            HostileCompletion.UnrequestedCancellationTerminal => Task.FromResult<InstallerRecoveryPruneResult>(new InstallerRecoveryPruneTerminalResult(
                ProtocolPruneOutcome.CancelledBeforePublication,
                ProtocolDurableState.Unchanged,
                null,
                ProtocolRecoveryDisposition.NotRequired,
                ProtocolNextAction.ListRecoveries,
                new InstallerRecoveryPruneSummary(0, 0, 0, false),
                InstallerBackendSettlement.ConfirmedClosed
            )),
            HostileCompletion.InvalidTerminalAccounting => Task.FromResult<InstallerRecoveryPruneResult>(ExactTerminal()),
            _ => throw new ArgumentOutOfRangeException(nameof(hostile), hostile, null)
        };
        var prepared = await CreateReadyToRunAsync(_ => Task.FromResult(Operation(completion)));
        await using RecoveryPruneController controller = prepared.Controller;

        await controller.RunAsync().WaitAsync(TimeSpan.FromSeconds(2));

        controller.Snapshot.State.Should().Be(RecoveryPruneControllerState.StateUnknown);
        controller.Snapshot.Result.Should().BeOfType<RecoveryPruneStateUnknownPresentation>();
        prepared.Confirmed.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task SessionFaultBeforeAnyActionPublishesFaultAndDisposesWithoutBackendCalls()
    {
        FakePlanSession session = new();
        await using RecoveryPruneController controller = new(session);

        session.Fail();
        await WaitUntilAsync(() => controller.Snapshot.State == RecoveryPruneControllerState.SessionFaulted);

        session.RecoveryListCalls.Should().Be(0);
        session.InspectedPrunes.Should().BeEmpty();
        session.ConfirmedPrunes.Should().BeEmpty();
        session.DisposeCalls.Should().Be(1);
    }

    [TestCase(PendingStage.List)]
    [TestCase(PendingStage.Inspect)]
    [TestCase(PendingStage.Confirm)]
    public async Task DisposeWhilePreExecutionCommandIsPendingRejectsLatePublicationAndCleansEveryOwner(PendingStage stage)
    {
        TaskCompletionSource<BoundInstallerRecoveryCatalogResult> list = NewSource<BoundInstallerRecoveryCatalogResult>();
        TaskCompletionSource<BoundInstallerRecoveryPrunePlanResult> inspect = NewSource<BoundInstallerRecoveryPrunePlanResult>();
        TaskCompletionSource<IConfirmedRecoveryPruneSession> confirm = NewSource<IConfirmedRecoveryPruneSession>();
        BoundInstallerRecoveryPoint point = Point(1, true);
        FakeConfirmedPruneSession lateConfirmed = new();
        FakePlanSession session = new()
        {
            RecoveryCatalog = _ => stage == PendingStage.List
                ? list.Task
                : Task.FromResult<BoundInstallerRecoveryCatalogResult>(new BoundInstallerRecoveryCatalogSuccess([point])),
            PruneInspection = (_, _) => stage == PendingStage.Inspect
                ? inspect.Task
                : Task.FromResult<BoundInstallerRecoveryPrunePlanResult>(Plan(1, 1, 0, auxiliaryCleanup: true)),
            PruneConfirmation = (_, _) => confirm.Task
        };
        lateConfirmed.Release = session.Release;
        lateConfirmed.Game = session.Game;
        RecoveryPruneController controller = new(session);
        Task active;
        switch (stage)
        {
            case PendingStage.List:
                active = controller.ListRecoveriesAsync();
                await WaitUntilAsync(() => session.RecoveryListCalls == 1);
                break;
            case PendingStage.Inspect:
                await controller.ListRecoveriesAsync();
                controller.SelectRecoveryPoint(controller.Snapshot.Choices.Single());
                active = controller.InspectAsync();
                await WaitUntilAsync(() => session.InspectedPrunes.Length == 1);
                break;
            case PendingStage.Confirm:
                await controller.ListRecoveriesAsync();
                controller.SelectRecoveryPoint(controller.Snapshot.Choices.Single());
                await controller.InspectAsync();
                active = controller.ConfirmAsync(RecoveryPruneConsent.ConfirmDestructiveCleanup);
                await WaitUntilAsync(() => session.ConfirmedPrunes.Length == 1);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(stage), stage, null);
        }

        Task disposal = controller.DisposeAsync().AsTask();
        disposal.IsCompleted.Should().BeFalse();
        if (stage == PendingStage.List)
            list.SetResult(new BoundInstallerRecoveryCatalogSuccess([point]));
        else if (stage == PendingStage.Inspect)
            inspect.SetResult(Plan(1, 1, 0, auxiliaryCleanup: true));
        else
            confirm.SetResult(lateConfirmed);

        await active.WaitAsync(TimeSpan.FromSeconds(2));
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));

        controller.Snapshot.State.Should().Be(RecoveryPruneControllerState.Disposed);
        session.DisposeCalls.Should().Be(1);
        lateConfirmed.DisposeCalls.Should().Be(stage == PendingStage.Confirm ? 1 : 0);
    }

    [Test]
    public async Task ProjectionsExposeNoBackendAuthorityPathIdentifierDigestOrRawText()
    {
        Type[] presentations =
        [
            typeof(RecoveryPruneChoice),
            typeof(RecoveryPrunePlanPresentation),
            typeof(RecoveryPruneRejection),
            typeof(RecoveryPruneResultPresentation),
            typeof(RecoveryPruneTerminalPresentation),
            typeof(RecoveryPruneStateUnknownPresentation)
        ];

        foreach (Type presentation in presentations)
        {
            presentation.GetProperties().Should().NotContain(property =>
                property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Digest", StringComparison.OrdinalIgnoreCase)
                || property.Name.EndsWith("Id", StringComparison.OrdinalIgnoreCase)
                || property.PropertyType == typeof(BoundInstallerRecoveryPoint)
                || property.PropertyType == typeof(BoundInstallerRecoveryPruneConfirmation)
                || property.PropertyType == typeof(IConfirmedRecoveryPruneSession)
                || property.PropertyType == typeof(InstallerRecoveryPruneConfirmation)
                || property.PropertyType == typeof(InstallerConfirmedRecoveryPruneAuthority));
        }

        FakePlanSession session = new()
        {
            RecoveryCatalog = _ => Task.FromResult<BoundInstallerRecoveryCatalogResult>(
                new BoundInstallerRecoveryCatalogSuccess([Point(1, current: true)])
            )
        };
        RecoveryPruneController controller = new(session);
        int healthyCalls = 0;
        controller.Changed += (_, _) => throw new InvalidOperationException("private observer /home/wife/Mods");
        controller.Changed += (_, _) =>
        {
            _ = controller.Snapshot;
            Interlocked.Increment(ref healthyCalls);
        };

        await controller.ListRecoveriesAsync();
        healthyCalls.Should().BeGreaterThan(0, "one observer cannot suppress a later observer or state publication");
        controller.Snapshot.State.Should().Be(RecoveryPruneControllerState.CatalogReady);
        await controller.DisposeAsync();
        int afterDispose = healthyCalls;
        session.Fail();
        await Task.Delay(50);
        healthyCalls.Should().Be(afterDispose, "late fault callbacks cannot publish after disposal");
    }

    private static async Task<(RecoveryPruneController Controller, FakePlanSession Session, FakeConfirmedPruneSession Confirmed)> CreateReadyToRunAsync(
        Func<CancellationToken, Task<InstallerRecoveryPruneOperation>> execute,
        bool removesGeneration = false
    )
    {
        BoundInstallerRecoveryPoint[] points = removesGeneration
            ? [Point(1, current: true), Point(2, current: false)]
            : [Point(1, current: true)];
        FakeConfirmedPruneSession confirmed = new() { Execute = execute };
        FakePlanSession session = new()
        {
            RecoveryCatalog = _ => Task.FromResult<BoundInstallerRecoveryCatalogResult>(new BoundInstallerRecoveryCatalogSuccess(points)),
            PruneInspection = (_, _) => Task.FromResult<BoundInstallerRecoveryPrunePlanResult>(removesGeneration
                ? Plan(1, 1, 1)
                : Plan(1, 1, 0, auxiliaryCleanup: true)),
            PruneConfirmation = (_, _) => Task.FromResult<IConfirmedRecoveryPruneSession>(confirmed)
        };
        confirmed.Release = session.Release;
        confirmed.Game = session.Game;
        RecoveryPruneController controller = new(session);
        await controller.ListRecoveriesAsync();
        controller.SelectRecoveryPoint(controller.Snapshot.Choices[0]);
        await controller.InspectAsync();
        await controller.ConfirmAsync(RecoveryPruneConsent.ConfirmDestructiveCleanup);
        controller.Snapshot.State.Should().Be(RecoveryPruneControllerState.ReadyToRun);
        return (controller, session, confirmed);
    }

    private static BoundInstallerRecoveryPoint Point(int ordinal, bool current) => new(
        ordinal,
        current,
        false,
        InstallerOperation.Update,
        new BoundInstallerRecoveryReleaseTarget(GameDiscoveryControllerTests.Release().Tag, GameDiscoveryControllerTests.Release().EmbeddedVersion)
    );

    private static BoundInstallerRecoveryPrunePlanSuccess Plan(
        int retainNewest,
        int retainedCount,
        int removedCount,
        bool auxiliaryCleanup = false
    ) => new(
        retainNewest,
        retainedCount,
        removedCount,
        removedCount,
        auxiliaryCleanup,
        1,
        [ProtocolPlanRisk.RecoveryPrune],
        ProtocolRecommendedDefault.Cancel,
        true
    )
    {
        Confirmation = new BoundInstallerRecoveryPruneConfirmation()
    };

    private static InstallerRecoveryPruneTerminalResult ExactTerminal(int removedCount = 1) => new(
        ProtocolPruneOutcome.Succeeded,
        ProtocolDurableState.PruneApplied,
        null,
        ProtocolRecoveryDisposition.NotRequired,
        ProtocolNextAction.ListRecoveries,
        new InstallerRecoveryPruneSummary(removedCount, removedCount, 0, false),
        InstallerBackendSettlement.ConfirmedClosed
    );

    private static InstallerRecoveryPruneTerminalResult TerminalForOutcome(ProtocolPruneOutcome outcome)
    {
        (ProtocolDurableState Durable, ProtocolTerminalErrorCode? Error, ProtocolRecoveryDisposition Recovery, InstallerRecoveryPruneSummary Summary) values = outcome switch
        {
            ProtocolPruneOutcome.Succeeded => (
                ProtocolDurableState.PruneApplied,
                null,
                ProtocolRecoveryDisposition.NotRequired,
                new(1, 1, 0, false)
            ),
            ProtocolPruneOutcome.FailedBeforePublication => (
                ProtocolDurableState.Unchanged,
                ProtocolTerminalErrorCode.IoFailure,
                ProtocolRecoveryDisposition.NotRequired,
                new(0, 0, 0, false)
            ),
            ProtocolPruneOutcome.CancelledBeforePublication => (
                ProtocolDurableState.Unchanged,
                null,
                ProtocolRecoveryDisposition.NotRequired,
                new(0, 0, 0, false)
            ),
            ProtocolPruneOutcome.Interrupted => (
                ProtocolDurableState.Unchanged,
                ProtocolTerminalErrorCode.IoFailure,
                ProtocolRecoveryDisposition.StateRefreshRequired,
                new(0, 0, 0, false)
            ),
            ProtocolPruneOutcome.CancelledWithCleanupPending => (
                ProtocolDurableState.PruneApplied,
                null,
                ProtocolRecoveryDisposition.CleanupPending,
                new(1, 0, 1, false)
            ),
            ProtocolPruneOutcome.FailedWithCleanupPending => (
                ProtocolDurableState.PruneApplied,
                ProtocolTerminalErrorCode.IoFailure,
                ProtocolRecoveryDisposition.CleanupPending,
                new(1, 0, 1, false)
            ),
            ProtocolPruneOutcome.UnexpectedCoreFailure => (
                ProtocolDurableState.Unknown,
                ProtocolTerminalErrorCode.UnexpectedCoreFailure,
                ProtocolRecoveryDisposition.StateRefreshRequired,
                new(null, null, null, null)
            ),
            ProtocolPruneOutcome.CancelledAfterApply => (
                ProtocolDurableState.PruneApplied,
                null,
                ProtocolRecoveryDisposition.StateRefreshRequired,
                new(1, 1, 0, false)
            ),
            ProtocolPruneOutcome.FailedAfterApply => (
                ProtocolDurableState.PruneApplied,
                ProtocolTerminalErrorCode.IoFailure,
                ProtocolRecoveryDisposition.StateRefreshRequired,
                new(1, 1, 0, false)
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
        };
        return new(
            outcome,
            values.Durable,
            values.Error,
            values.Recovery,
            ProtocolNextAction.ListRecoveries,
            values.Summary,
            outcome == ProtocolPruneOutcome.UnexpectedCoreFailure
                ? InstallerBackendSettlement.Unconfirmed
                : InstallerBackendSettlement.ConfirmedClosed
        );
    }

    private static InstallerRecoveryPruneOperation Operation(
        Task<InstallerRecoveryPruneResult> completion,
        Func<Task>? cancellation = null
    )
    {
        Channel<InstallerRecoveryPruneProgress> progress = Channel.CreateBounded<InstallerRecoveryPruneProgress>(1);
        progress.Writer.TryComplete();
        return new(progress.Reader, completion, cancellation ?? (() => Task.CompletedTask));
    }

    private static InstallerRecoveryPruneOperation Operation(
        ChannelReader<InstallerRecoveryPruneProgress> progress,
        Task<InstallerRecoveryPruneResult> completion,
        Func<Task>? cancellation = null
    ) => new(progress, completion, cancellation ?? (() => Task.CompletedTask));

    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<T> NewSource<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The expected recovery-cleanup state was not reached.");
            await Task.Delay(10);
        }
    }

    internal enum MalformedCatalog
    {
        DuplicateReference,
        WrongFirstOrdinal,
        WrongCurrentMarker,
        ThrowingCount,
        ThrowingIndexer
    }

    internal enum MalformedPlan
    {
        WrongRetentionBoundary,
        CleanupSmallerThanRemoved,
        MissingConfirmation,
        WrongRisk,
        TrueNoOp
    }

    internal enum HostileCompletion
    {
        FaultedCompletion,
        UnrequestedCancellationTerminal,
        InvalidTerminalAccounting
    }

    internal enum PendingStage
    {
        List,
        Inspect,
        Confirm
    }

    private sealed class HostileRecoveryPointList(bool throwCount) : IReadOnlyList<BoundInstallerRecoveryPoint>
    {
        public int Count => throwCount ? throw new InvalidOperationException("private hostile count") : 1;
        public BoundInstallerRecoveryPoint this[int index] => throw new InvalidOperationException("private hostile index");
        public IEnumerator<BoundInstallerRecoveryPoint> GetEnumerator() => throw new AssertionException("Enumeration wasn't expected.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => this.GetEnumerator();
    }

    private sealed class CountingProgressReader(
        ChannelReader<InstallerRecoveryPruneProgress> inner,
        int signalAt,
        TaskCompletionSource observed
    ) : ChannelReader<InstallerRecoveryPruneProgress>
    {
        private int ReadCount;

        public override Task Completion => inner.Completion;

        public override bool TryRead(out InstallerRecoveryPruneProgress item)
        {
            bool read = inner.TryRead(out item!);
            if (read && Interlocked.Increment(ref this.ReadCount) == signalAt)
                observed.TrySetResult();
            return read;
        }

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
            => inner.WaitToReadAsync(cancellationToken);
    }

    private sealed class FakePlanSession : IPlanInspectionSession
    {
        private readonly object Sync = new();
        private readonly List<BoundInstallerRecoveryPoint> Prunes = [];
        private readonly List<BoundInstallerRecoveryPruneConfirmation> Confirmations = [];
        private int ListCount;
        private int DisposeCount;

        public ProtocolReleaseIdentity Release { get; set; } = GameDiscoveryControllerTests.Release();
        public VerifiedGamePresentation Game { get; set; } = new("/games/Stardew Valley", "Stardew Valley");
        public TaskCompletionSource<InstallerProtocolClientException> Fault { get; } = NewSource<InstallerProtocolClientException>();
        public Task<InstallerProtocolClientException> SessionFaulted => this.Fault.Task;
        public int RecoveryListCalls => Volatile.Read(ref this.ListCount);
        public int DisposeCalls => Volatile.Read(ref this.DisposeCount);
        public BoundInstallerRecoveryPoint[] InspectedPrunes
        {
            get { lock (this.Sync) return this.Prunes.ToArray(); }
        }
        public BoundInstallerRecoveryPruneConfirmation[] ConfirmedPrunes
        {
            get { lock (this.Sync) return this.Confirmations.ToArray(); }
        }

        public Func<CancellationToken, Task<BoundInstallerRecoveryCatalogResult>> RecoveryCatalog { get; init; }
            = _ => throw new AssertionException("Recovery listing wasn't expected.");
        public Func<BoundInstallerRecoveryPoint, CancellationToken, Task<BoundInstallerRecoveryPrunePlanResult>> PruneInspection { get; init; }
            = (_, _) => throw new AssertionException("Recovery-prune inspection wasn't expected.");
        public Func<BoundInstallerRecoveryPruneConfirmation, CancellationToken, Task<IConfirmedRecoveryPruneSession>> PruneConfirmation { get; init; }
            = (_, _) => throw new AssertionException("Recovery-prune confirmation wasn't expected.");
        public Func<Task> Disposal { get; init; } = () => Task.CompletedTask;

        public void Fail() => this.Fault.TrySetResult(new InstallerProtocolClientException("synthetic private session fault"));

        public Task<InstallerReadOnlyPlanResult> InspectPlanAsync(InstallerOperation operation, CancellationToken cancellationToken = default)
            => throw new AssertionException("Ordinary plan inspection wasn't expected.");

        public Task<BoundInstallerRecoveryCatalogResult> ListRecoveriesAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this.ListCount);
            return this.RecoveryCatalog(cancellationToken);
        }

        public Task<InstallerReadOnlyPlanResult> InspectRollbackAsync(BoundInstallerRecoveryPoint point, CancellationToken cancellationToken = default)
            => throw new AssertionException("Rollback inspection wasn't expected.");

        public Task<BoundInstallerRecoveryPrunePlanResult> InspectRecoveryPruneAsync(
            BoundInstallerRecoveryPoint oldestPointToKeep,
            CancellationToken cancellationToken = default
        )
        {
            lock (this.Sync)
                this.Prunes.Add(oldestPointToKeep);
            return this.PruneInspection(oldestPointToKeep, cancellationToken);
        }

        public Task<InstallerReadOnlyPlanResult> ApprovePlanCandidatesAsync(
            IReadOnlyList<InstallerReadOnlyPlanCandidate> candidates,
            CancellationToken cancellationToken = default
        ) => throw new AssertionException("Candidate approval wasn't expected.");

        public Task<IConfirmedInstallerSession> ConfirmPlanAsync(
            InstallerPlanConfirmation confirmation,
            CancellationToken cancellationToken = default
        ) => throw new AssertionException("Ordinary confirmation wasn't expected.");

        public Task<IConfirmedRecoveryPruneSession> ConfirmRecoveryPruneAsync(
            BoundInstallerRecoveryPruneConfirmation confirmation,
            CancellationToken cancellationToken = default
        )
        {
            lock (this.Sync)
                this.Confirmations.Add(confirmation);
            return this.PruneConfirmation(confirmation, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref this.DisposeCount);
            await this.Disposal();
        }
    }

    private sealed class FakeConfirmedPruneSession : IConfirmedRecoveryPruneSession
    {
        private int ExecuteCount;
        private int DisposeCount;

        public ProtocolReleaseIdentity Release { get; set; } = GameDiscoveryControllerTests.Release();
        public VerifiedGamePresentation Game { get; set; } = new("/games/Stardew Valley", "Stardew Valley");
        public TaskCompletionSource<InstallerProtocolClientException> Fault { get; } = NewSource<InstallerProtocolClientException>();
        public Task<InstallerProtocolClientException> SessionFaulted => this.Fault.Task;
        public int ExecuteCalls => Volatile.Read(ref this.ExecuteCount);
        public int DisposeCalls => Volatile.Read(ref this.DisposeCount);
        public Func<CancellationToken, Task<InstallerRecoveryPruneOperation>> Execute { get; set; }
            = _ => throw new AssertionException("Recovery cleanup execution wasn't expected.");
        public Func<Task> Disposal { get; init; } = () => Task.CompletedTask;

        public Task<InstallerRecoveryPruneOperation> ExecuteAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this.ExecuteCount);
            return this.Execute(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref this.DisposeCount);
            await this.Disposal();
        }
    }
}
