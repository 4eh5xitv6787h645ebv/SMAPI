using System.Threading.Channels;
using Avalonia.Automation;
using FluentAssertions;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Core.Transactions;
using StardewModdingAPI.Installer.Gui.Backend;
using StardewModdingAPI.Installer.Gui.Frontend;
using StardewModdingAPI.Installer.Gui.ViewModels;

namespace StardewModdingAPI.Installer.Gui.Tests;

[NonParallelizable]
internal sealed class RecoveryPruneViewModelTests
{
    [Test]
    public async Task ExplicitGatesKeepConsentUncheckedAndConfirmationSeparateFromRun()
    {
        BoundInstallerRecoveryPoint[] points = [Point(1, current: true), Point(2, current: false)];
        BoundInstallerRecoveryPruneConfirmation exactConfirmation = new();
        TaskCompletionSource<InstallerRecoveryPruneResult> completion = NewSource<InstallerRecoveryPruneResult>();
        FakeConfirmedPruneSession confirmed = new()
        {
            Execute = _ => Task.FromResult(Operation(completion.Task))
        };
        FakePlanSession session = new()
        {
            RecoveryCatalog = _ => Task.FromResult<BoundInstallerRecoveryCatalogResult>(new BoundInstallerRecoveryCatalogSuccess(points)),
            PruneInspection = (point, _) =>
            {
                point.Should().BeSameAs(points[0]);
                return Task.FromResult<BoundInstallerRecoveryPrunePlanResult>(Plan(1, 1, 1) with { Confirmation = exactConfirmation });
            },
            PruneConfirmation = (confirmation, _) =>
            {
                confirmation.Should().BeSameAs(exactConfirmation);
                return Task.FromResult<IConfirmedRecoveryPruneSession>(confirmed);
            }
        };
        Bind(confirmed, session);
        await using RecoveryPruneViewModel viewModel = CreateViewModel(session);
        List<RecoveryPruneFocusTarget> focus = [];
        viewModel.FocusRequested += (_, target) => focus.Add(target);
        int closeRequests = 0;
        viewModel.CloseRequested += (_, _) => closeRequests++;

        viewModel.Choices.Should().BeEmpty();
        viewModel.SelectedChoice.Should().BeNull();
        viewModel.ListCommand.CanExecute(null).Should().BeTrue();
        viewModel.InspectCommand.CanExecute(null).Should().BeFalse();
        session.ListCalls.Should().Be(0, "construction must not list recovery history");

        await viewModel.ListCommand.ExecuteAsync();

        viewModel.Choices.Should().HaveCount(2);
        viewModel.SelectedChoice.Should().BeNull("listing never chooses a destructive retention boundary");
        viewModel.Choices[0].AccessibleName.Should().Contain("Keep this point and every newer point");
        session.InspectedPoints.Should().BeEmpty();
        viewModel.SelectedChoice = viewModel.Choices[0];
        viewModel.InspectCommand.CanExecute(null).Should().BeTrue();
        focus.Should().NotContain(RecoveryPruneFocusTarget.Inspect, "arrow-key list selection must not steal focus from recovery-point browsing");

        await viewModel.InspectCommand.ExecuteAsync();

        viewModel.IsPlanVisible.Should().BeTrue();
        viewModel.PlanRows.Should().HaveCount(9);
        viewModel.PlanRows.Should().Contain(row => row.Label == "Older points to remove" && row.Value == "1");
        viewModel.PlanRows.Should().Contain(row => row.Label == "Recovery generations to clean" && row.Value == "1");
        viewModel.IsDestructiveConsentChecked.Should().BeFalse();
        viewModel.ConfirmCommand.CanExecute(null).Should().BeFalse();
        confirmed.ExecuteCalls.Should().Be(0);
        viewModel.IsDestructiveConsentChecked = true;
        viewModel.ConfirmCommand.CanExecute(null).Should().BeTrue();
        focus.Should().EndWith(RecoveryPruneFocusTarget.Confirm);

        await viewModel.ConfirmCommand.ExecuteAsync();

        viewModel.IsDestructiveConsentChecked.Should().BeFalse("confirmation invalidates the local consent generation");
        viewModel.IsRunVisible.Should().BeTrue();
        viewModel.RunCommand.CanExecute(null).Should().BeTrue();
        focus.Should().EndWith(RecoveryPruneFocusTarget.Cancel, "Cancel remains the safe recommended default");
        confirmed.ExecuteCalls.Should().Be(0, "confirmation must never run cleanup");
        await viewModel.CancelCommand.ExecuteAsync();
        closeRequests.Should().Be(1);
        confirmed.ExecuteCalls.Should().Be(0);

        Task run = viewModel.RunCommand.ExecuteAsync();
        await WaitUntilAsync(() => confirmed.ExecuteCalls == 1);
        completion.SetResult(ExactSuccess());
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        confirmed.ExecuteCalls.Should().Be(1);
        viewModel.IsResultVisible.Should().BeTrue();
        viewModel.ResultRows.Should().HaveCount(10);
        viewModel.Heading.Should().Be("Recovery cleanup completed");
    }

    [Test]
    public async Task RelistRemintsWrappersClearsSelectionAndRejectsEveryStaleWrapper()
    {
        Queue<BoundInstallerRecoveryPoint> catalogs = new([Point(1, true), Point(1, true)]);
        FakePlanSession session = new()
        {
            RecoveryCatalog = _ => Task.FromResult<BoundInstallerRecoveryCatalogResult>(
                new BoundInstallerRecoveryCatalogSuccess([catalogs.Dequeue()])
            )
        };
        await using RecoveryPruneViewModel viewModel = CreateViewModel(session);
        await viewModel.ListCommand.ExecuteAsync();
        RecoveryPruneChoiceItem stale = viewModel.Choices.Single();
        viewModel.SelectedChoice = stale;
        viewModel.ListCommand.CanExecute(null).Should().BeTrue();

        await viewModel.ListCommand.ExecuteAsync();

        session.ListCalls.Should().Be(2);
        viewModel.Heading.Should().Be("Choose the oldest recovery point to keep");
        viewModel.SelectedChoice.Should().BeNull();
        viewModel.Choices.Should().ContainSingle().Which.Should().NotBeSameAs(stale);
        Action selectStale = () => viewModel.SelectedChoice = stale;
        selectStale.Should().Throw<ArgumentException>();
        session.InspectedPoints.Should().BeEmpty();
    }

    [Test]
    public async Task AuxiliaryOnlyPlanHasBoundedTruthfulRowsAndNoImpliedSelectionOrRun()
    {
        FakePlanSession session = new()
        {
            RecoveryCatalog = _ => Task.FromResult<BoundInstallerRecoveryCatalogResult>(
                new BoundInstallerRecoveryCatalogSuccess([Point(1, current: true)])
            ),
            PruneInspection = (_, _) => Task.FromResult<BoundInstallerRecoveryPrunePlanResult>(Plan(1, 1, 0, auxiliaryCleanup: true))
        };
        await using RecoveryPruneViewModel viewModel = CreateViewModel(session);
        await viewModel.ListCommand.ExecuteAsync();
        viewModel.SelectedChoice = viewModel.Choices.Single();

        await viewModel.InspectCommand.ExecuteAsync();

        viewModel.PlanRows.Should().HaveCount(9).And.OnlyContain(row => row.AccessibleName.Length <= 256);
        viewModel.PlanRows.Should().Contain(row => row.Label == "Older points to remove" && row.Value == "0");
        viewModel.PlanRows.Should().Contain(row => row.Label == "Auxiliary cleanup" && row.Value == "Planned");
        viewModel.CleanupScopeWarning.Should().Be("No recovery points will be removed; authenticated auxiliary recovery metadata will be cleaned permanently. It cannot be restored unless you have a separate external backup.");
        viewModel.IsDestructiveConsentChecked.Should().BeFalse();
        viewModel.RunCommand.CanExecute(null).Should().BeFalse();
        session.ConfirmedPoints.Should().BeEmpty();
    }

    [Test]
    public void BackgroundSnapshotsCoalesceToNewestRevisionAndInvalidateConsentMonotonically()
    {
        List<Action> posted = [];
        FakePlanSession session = new();
        RecoveryPruneController controller = new(session);
        RecoveryPruneViewModel viewModel = new(controller, () => false, posted.Add);
        RecoveryPruneSnapshot initial = controller.Snapshot;
        RecoveryPrunePlanPresentation plan = PlanPresentation();
        RecoveryPruneSnapshot review = Snapshot(initial, 1, 1, RecoveryPruneControllerState.ReviewReady, plan: plan, canConfirm: true);
        viewModel.ApplySnapshotForTesting(review);
        viewModel.IsDestructiveConsentChecked = true;

        viewModel.QueueSnapshotForTesting(Snapshot(initial, 2, 2, RecoveryPruneControllerState.Confirming, plan: plan));
        viewModel.QueueSnapshotForTesting(Snapshot(initial, 4, 4, RecoveryPruneControllerState.ReadyToRun, plan: plan, canRun: true));
        viewModel.QueueSnapshotForTesting(Snapshot(initial, 3, 3, RecoveryPruneControllerState.ReviewReady, plan: plan, canConfirm: true));

        posted.Should().ContainSingle("one UI drain should own all pending snapshots");
        posted.Single()();
        viewModel.Heading.Should().Be("Recovery cleanup confirmed — not started");
        viewModel.IsDestructiveConsentChecked.Should().BeFalse();
        viewModel.RunCommand.CanExecute(null).Should().BeTrue();
        viewModel.QueueSnapshotForTesting(Snapshot(initial, 2, 2, RecoveryPruneControllerState.ReviewReady, plan: plan, canConfirm: true));
        posted.Should().ContainSingle("an older revision cannot schedule or restore stale consent");

        viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
        session.DisposeCalls.Should().Be(1);
    }

    [Test]
    public void EveryControllerStateHasFixedCopyFocusCommandsAndExactlyOneAnnouncementChannel()
    {
        FakePlanSession session = new();
        RecoveryPruneController controller = new(session);
        RecoveryPruneViewModel viewModel = new(controller, () => true, _ => throw new AssertionException("No post expected."));
        RecoveryPruneSnapshot initial = controller.Snapshot;
        long revision = 0;

        foreach (RecoveryPruneControllerState state in Enum.GetValues<RecoveryPruneControllerState>())
        {
            RecoveryPruneSnapshot snapshot = Snapshot(
                initial,
                ++revision,
                revision,
                state,
                plan: state is RecoveryPruneControllerState.ReviewReady or RecoveryPruneControllerState.Confirming
                    or RecoveryPruneControllerState.ReadyToRun or RecoveryPruneControllerState.Starting
                    or RecoveryPruneControllerState.Running or RecoveryPruneControllerState.CancellationRequested
                    or RecoveryPruneControllerState.Terminal ? PlanPresentation() : null,
                rejection: state == RecoveryPruneControllerState.RelistRequired
                    ? new(ProtocolPrePlanErrorCode.NothingToPrune, ProtocolNextAction.ListRecoveries, false)
                    : null,
                result: state switch
                {
                    RecoveryPruneControllerState.Terminal => ExactTerminalPresentation(),
                    RecoveryPruneControllerState.StateUnknown => new RecoveryPruneStateUnknownPresentation(),
                    _ => null
                },
                stage: state == RecoveryPruneControllerState.Running ? TransactionStage.CleaningRecovery : null,
                canList: state is RecoveryPruneControllerState.NotLoaded or RecoveryPruneControllerState.NoHistory or RecoveryPruneControllerState.RelistRequired,
                canConfirm: state == RecoveryPruneControllerState.ReviewReady,
                canRun: state == RecoveryPruneControllerState.ReadyToRun,
                canCancel: state is RecoveryPruneControllerState.Listing or RecoveryPruneControllerState.Inspecting
                    or RecoveryPruneControllerState.Confirming or RecoveryPruneControllerState.Starting or RecoveryPruneControllerState.Running,
                canExit: state is RecoveryPruneControllerState.NotLoaded or RecoveryPruneControllerState.NoHistory
                    or RecoveryPruneControllerState.RelistRequired or RecoveryPruneControllerState.ReviewReady
                    or RecoveryPruneControllerState.ReadyToRun or RecoveryPruneControllerState.Cancelled
                    or RecoveryPruneControllerState.CancelledBeforeStart or RecoveryPruneControllerState.Terminal
                    or RecoveryPruneControllerState.StateUnknown or RecoveryPruneControllerState.Failed
                    or RecoveryPruneControllerState.SessionFaulted
            );

            Action apply = () => viewModel.ApplySnapshotForTesting(snapshot);
            apply.Should().NotThrow(state.ToString());
            viewModel.Heading.Should().NotBeNullOrWhiteSpace();
            viewModel.Message.Should().NotBeNullOrWhiteSpace();
            viewModel.LiveAnnouncement.Should().Be($"{viewModel.Heading}. {viewModel.Message}");
            new[]
            {
                viewModel.StatusLiveSetting,
                viewModel.ReviewLiveSetting,
                viewModel.StageLiveSetting,
                viewModel.ResultLiveSetting,
                viewModel.ErrorLiveSetting
            }.Count(setting => setting != AutomationLiveSetting.Off).Should().Be(1, state.ToString());
        }

        viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [TestCase(ProtocolPruneOutcome.Succeeded, false)]
    [TestCase(ProtocolPruneOutcome.FailedBeforePublication, true)]
    [TestCase(ProtocolPruneOutcome.CancelledBeforePublication, false)]
    [TestCase(ProtocolPruneOutcome.Interrupted, true)]
    [TestCase(ProtocolPruneOutcome.CancelledWithCleanupPending, true)]
    [TestCase(ProtocolPruneOutcome.FailedWithCleanupPending, true)]
    [TestCase(ProtocolPruneOutcome.UnexpectedCoreFailure, true)]
    [TestCase(ProtocolPruneOutcome.CancelledAfterApply, false)]
    [TestCase(ProtocolPruneOutcome.FailedAfterApply, true)]
    public void EveryTerminalFamilyHasBoundedExactRowsAndIntentionalLiveSeverity(ProtocolPruneOutcome outcome, bool assertive)
    {
        FakePlanSession session = new();
        RecoveryPruneController controller = new(session);
        RecoveryPruneViewModel viewModel = new(controller, () => true, _ => { });
        RecoveryPruneTerminalPresentation terminal = TerminalPresentation(outcome);
        viewModel.ApplySnapshotForTesting(Snapshot(
            controller.Snapshot,
            1,
            1,
            RecoveryPruneControllerState.Terminal,
            plan: PlanPresentation(),
            result: terminal,
            canExit: true
        ));

        viewModel.ResultRows.Should().HaveCount(10).And.OnlyContain(row => row.AccessibleName.Length <= 256);
        viewModel.ResultRows.Should().Contain(row => row.Label == "Outcome");
        viewModel.ResultLiveSetting.Should().Be(assertive ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        viewModel.StatusLiveSetting.Should().Be(AutomationLiveSetting.Off);
        viewModel.LiveAnnouncement.Should().NotContain("/home").And.NotContain("digest");

        viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Test]
    public void UnconfirmedSettlementGetsProminentFixedSafeWarningWithoutBackendDetail()
    {
        FakePlanSession session = new();
        RecoveryPruneController controller = new(session);
        RecoveryPruneViewModel viewModel = new(controller, () => true, _ => { });
        RecoveryPruneTerminalPresentation terminal = TerminalPresentation(ProtocolPruneOutcome.UnexpectedCoreFailure) with
        {
            BackendSettlement = InstallerBackendSettlement.Unconfirmed
        };
        viewModel.ApplySnapshotForTesting(Snapshot(
            controller.Snapshot,
            1,
            1,
            RecoveryPruneControllerState.Terminal,
            plan: PlanPresentation(),
            result: terminal,
            canExit: true
        ));

        viewModel.IsSettlementWarningVisible.Should().BeTrue();
        viewModel.SettlementWarning.Should().Contain("did not confirm a clean close")
            .And.Contain("fresh verified installer session")
            .And.NotContain("/home")
            .And.NotContain("log");

        viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Test]
    public async Task PrepareToCloseCancelsActiveRequestOnceAndKeepsWindowUntilSettlement()
    {
        TaskCompletionSource<BoundInstallerRecoveryCatalogResult> catalog = NewSource<BoundInstallerRecoveryCatalogResult>();
        FakePlanSession session = new()
        {
            RecoveryCatalog = _ => catalog.Task
        };
        await using RecoveryPruneViewModel viewModel = CreateViewModel(session);
        (await viewModel.PrepareToCloseAsync()).Should().BeTrue("idle not-loaded state is safe to close");
        Task loading = viewModel.ListCommand.ExecuteAsync();
        await WaitUntilAsync(() => session.ListCalls == 1);

        (await viewModel.PrepareToCloseAsync()).Should().BeFalse();
        (await viewModel.PrepareToCloseAsync()).Should().BeFalse("a repeated close cannot issue a second authority-consuming action");
        catalog.SetResult(new BoundInstallerRecoveryCatalogSuccess([Point(1, current: true)]));
        await loading.WaitAsync(TimeSpan.FromSeconds(2));

        viewModel.Heading.Should().Be("Recovery cleanup request cancelled");
        (await viewModel.PrepareToCloseAsync()).Should().BeTrue();
        session.DisposeCalls.Should().Be(1);
    }

    [Test]
    public void PresentationSurfaceShowsExactEscapedTargetOnlyInBoundContextWithoutBackendAuthority()
    {
        const string FirstPath = "/games/first-\u202E-install";
        const string SecondPath = "/games/second-install";
        FakePlanSession firstSession = new() { Game = new(FirstPath, "Stardew Valley") };
        FakePlanSession secondSession = new() { Game = new(SecondPath, "Stardew Valley") };
        RecoveryPruneViewModel first = new(new RecoveryPruneController(firstSession), () => true, _ => { });
        RecoveryPruneViewModel second = new(new RecoveryPruneController(secondSession), () => true, _ => { });

        typeof(RecoveryPruneChoiceItem).GetProperties().Should().NotContain(property =>
            property.PropertyType == typeof(RecoveryPruneChoice)
            || property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Digest", StringComparison.OrdinalIgnoreCase)
            || property.Name.EndsWith("Id", StringComparison.OrdinalIgnoreCase));
        typeof(RecoveryPruneViewModel).GetProperties().Should().NotContain(property =>
            property.PropertyType == typeof(RecoveryPruneController)
            || property.PropertyType == typeof(RecoveryPruneChoice)
            || property.Name.Contains("Digest", StringComparison.OrdinalIgnoreCase));
        first.GameDetail.Should().Contain("Stardew Valley").And.Contain("/games/first-\\u202E-install").And.NotContain("\u202E");
        first.GameAccessibleName.Should().Contain("/games/first-\\u202E-install").And.NotContain("\u202E");
        second.GameDetail.Should().Contain(SecondPath).And.NotBe(first.GameDetail, "same-name installations must remain distinguishable before destructive cleanup");
        string.Join('|', first.Heading, first.Message, first.LiveAnnouncement).Should().NotContain("/games/");

        first.DisposeAsync().AsTask().GetAwaiter().GetResult();
        second.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static RecoveryPruneViewModel CreateViewModel(FakePlanSession session)
    {
        // Serialize controller callbacks like a UI dispatcher while keeping headless tests synchronous.
        object dispatcherSync = new();
        return new(
            new RecoveryPruneController(session),
            () => false,
            action =>
            {
                lock (dispatcherSync)
                    action();
            }
        );
    }

    private static RecoveryPruneSnapshot Snapshot(
        RecoveryPruneSnapshot basis,
        long generation,
        long revision,
        RecoveryPruneControllerState state,
        RecoveryPrunePlanPresentation? plan = null,
        RecoveryPruneRejection? rejection = null,
        RecoveryPruneResultPresentation? result = null,
        TransactionStage? stage = null,
        bool canList = false,
        bool canConfirm = false,
        bool canRun = false,
        bool canCancel = false,
        bool canExit = false
    ) => basis with
    {
        Generation = generation,
        Revision = revision,
        State = state,
        Choices = [],
        Selected = null,
        Plan = plan,
        Rejection = rejection,
        ProgressStage = stage,
        CompletedUnits = stage is null ? 0 : 1,
        TotalUnits = stage is null ? null : 2,
        Result = result,
        CanList = canList,
        CanSelect = false,
        CanInspect = false,
        CanConfirm = canConfirm,
        CanRun = canRun,
        CanCancel = canCancel,
        CanExit = canExit
    };

    private static RecoveryPrunePlanPresentation PlanPresentation() => new(
        1,
        1,
        1,
        1,
        false,
        1,
        [ProtocolPlanRisk.RecoveryPrune],
        ProtocolRecommendedDefault.Cancel,
        true
    );

    private static RecoveryPruneTerminalPresentation ExactTerminalPresentation() => new(
        ProtocolPruneOutcome.Succeeded,
        ProtocolDurableState.PruneApplied,
        null,
        ProtocolRecoveryDisposition.NotRequired,
        ProtocolNextAction.ListRecoveries,
        1,
        1,
        0,
        false,
        InstallerBackendSettlement.ConfirmedClosed
    );

    private static RecoveryPruneTerminalPresentation TerminalPresentation(ProtocolPruneOutcome outcome)
    {
        bool applied = outcome is ProtocolPruneOutcome.Succeeded
            or ProtocolPruneOutcome.CancelledWithCleanupPending
            or ProtocolPruneOutcome.FailedWithCleanupPending
            or ProtocolPruneOutcome.CancelledAfterApply
            or ProtocolPruneOutcome.FailedAfterApply;
        bool error = outcome is ProtocolPruneOutcome.FailedBeforePublication
            or ProtocolPruneOutcome.Interrupted
            or ProtocolPruneOutcome.FailedWithCleanupPending
            or ProtocolPruneOutcome.FailedAfterApply;
        bool pending = outcome is ProtocolPruneOutcome.CancelledWithCleanupPending or ProtocolPruneOutcome.FailedWithCleanupPending;
        bool unknown = outcome == ProtocolPruneOutcome.UnexpectedCoreFailure;
        return new(
            outcome,
            unknown ? ProtocolDurableState.Unknown : applied ? ProtocolDurableState.PruneApplied : ProtocolDurableState.Unchanged,
            unknown ? ProtocolTerminalErrorCode.UnexpectedCoreFailure : error ? ProtocolTerminalErrorCode.IoFailure : null,
            pending ? ProtocolRecoveryDisposition.CleanupPending : unknown || outcome is ProtocolPruneOutcome.Interrupted
                or ProtocolPruneOutcome.CancelledAfterApply or ProtocolPruneOutcome.FailedAfterApply
                ? ProtocolRecoveryDisposition.StateRefreshRequired
                : ProtocolRecoveryDisposition.NotRequired,
            ProtocolNextAction.ListRecoveries,
            unknown ? null : applied ? 1 : 0,
            unknown ? null : outcome is ProtocolPruneOutcome.Succeeded or ProtocolPruneOutcome.CancelledAfterApply or ProtocolPruneOutcome.FailedAfterApply ? 1 : 0,
            unknown ? null : pending ? 1 : 0,
            unknown ? null : false,
            unknown ? InstallerBackendSettlement.Unconfirmed : InstallerBackendSettlement.ConfirmedClosed
        );
    }

    private static BoundInstallerRecoveryPoint Point(int ordinal, bool current) => new(
        ordinal,
        current,
        false,
        InstallerOperation.Update,
        new BoundInstallerRecoveryReleaseTarget(
            GameDiscoveryControllerTests.Release().Tag,
            GameDiscoveryControllerTests.Release().EmbeddedVersion
        )
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

    private static InstallerRecoveryPruneTerminalResult ExactSuccess() => new(
        ProtocolPruneOutcome.Succeeded,
        ProtocolDurableState.PruneApplied,
        null,
        ProtocolRecoveryDisposition.NotRequired,
        ProtocolNextAction.ListRecoveries,
        new InstallerRecoveryPruneSummary(1, 1, 0, false),
        InstallerBackendSettlement.ConfirmedClosed
    );

    private static InstallerRecoveryPruneOperation Operation(Task<InstallerRecoveryPruneResult> completion)
    {
        Channel<InstallerRecoveryPruneProgress> progress = Channel.CreateBounded<InstallerRecoveryPruneProgress>(1);
        progress.Writer.TryComplete();
        return new(progress.Reader, completion, () => Task.CompletedTask);
    }

    private static void Bind(FakeConfirmedPruneSession confirmed, FakePlanSession source)
    {
        confirmed.Release = source.Release;
        confirmed.Game = source.Game;
        confirmed.SessionFaulted = source.SessionFaulted;
    }

    private static TaskCompletionSource<T> NewSource<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The expected recovery-cleanup presentation state was not reached.");
            await Task.Delay(10);
        }
    }

    private sealed class FakePlanSession : IPlanInspectionSession
    {
        private readonly List<BoundInstallerRecoveryPoint> Inspected = [];
        private readonly List<BoundInstallerRecoveryPruneConfirmation> Confirmed = [];
        private int ListCount;
        private int DisposeCount;

        public ProtocolReleaseIdentity Release { get; set; } = GameDiscoveryControllerTests.Release();
        public VerifiedGamePresentation Game { get; set; } = new("/games/Stardew Valley", "Stardew Valley");
        public TaskCompletionSource<InstallerProtocolClientException> Fault { get; } = NewSource<InstallerProtocolClientException>();
        public Task<InstallerProtocolClientException> SessionFaulted => this.Fault.Task;
        public int ListCalls => Volatile.Read(ref this.ListCount);
        public int DisposeCalls => Volatile.Read(ref this.DisposeCount);
        public BoundInstallerRecoveryPoint[] InspectedPoints { get { lock (this.Inspected) return this.Inspected.ToArray(); } }
        public BoundInstallerRecoveryPruneConfirmation[] ConfirmedPoints { get { lock (this.Confirmed) return this.Confirmed.ToArray(); } }
        public Func<CancellationToken, Task<BoundInstallerRecoveryCatalogResult>> RecoveryCatalog { get; init; }
            = _ => throw new AssertionException("Recovery listing wasn't expected.");
        public Func<BoundInstallerRecoveryPoint, CancellationToken, Task<BoundInstallerRecoveryPrunePlanResult>> PruneInspection { get; init; }
            = (_, _) => throw new AssertionException("Recovery inspection wasn't expected.");
        public Func<BoundInstallerRecoveryPruneConfirmation, CancellationToken, Task<IConfirmedRecoveryPruneSession>> PruneConfirmation { get; init; }
            = (_, _) => throw new AssertionException("Recovery confirmation wasn't expected.");

        public Task<InstallerReadOnlyPlanResult> InspectPlanAsync(InstallerOperation operation, CancellationToken cancellationToken = default)
            => throw new AssertionException("Ordinary inspection wasn't expected.");

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
            lock (this.Inspected)
                this.Inspected.Add(oldestPointToKeep);
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
            lock (this.Confirmed)
                this.Confirmed.Add(confirmation);
            return this.PruneConfirmation(confirmation, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref this.DisposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeConfirmedPruneSession : IConfirmedRecoveryPruneSession
    {
        private int ExecuteCount;
        private int DisposeCount;

        public ProtocolReleaseIdentity Release { get; set; } = GameDiscoveryControllerTests.Release();
        public VerifiedGamePresentation Game { get; set; } = new("/games/Stardew Valley", "Stardew Valley");
        public Task<InstallerProtocolClientException> SessionFaulted { get; set; } = NewSource<InstallerProtocolClientException>().Task;
        public int ExecuteCalls => Volatile.Read(ref this.ExecuteCount);
        public int DisposeCalls => Volatile.Read(ref this.DisposeCount);
        public Func<CancellationToken, Task<InstallerRecoveryPruneOperation>> Execute { get; init; }
            = _ => throw new AssertionException("Recovery execution wasn't expected.");

        public Task<InstallerRecoveryPruneOperation> ExecuteAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this.ExecuteCount);
            return this.Execute(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref this.DisposeCount);
            return ValueTask.CompletedTask;
        }
    }
}
