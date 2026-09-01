using Avalonia.Automation;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using FluentAssertions;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Gui.Backend;
using StardewModdingAPI.Installer.Gui.Frontend;
using StardewModdingAPI.Installer.Gui.ViewModels;

namespace StardewModdingAPI.Installer.Gui.Tests;

internal sealed partial class PlanReviewPresentationTests
{
    [AvaloniaTest]
    public async Task StartsWithExactlyFiveNoDefaultReadOnlyChoices()
    {
        FakePlanSession session = new();
        await using PlanReviewViewModel viewModel = CreateViewModel(session);

        viewModel.SelectedOperation.Should().BeNull();
        viewModel.OperationChoices.Select(choice => choice.Operation).Should().Equal(
            InstallerOperation.Install,
            InstallerOperation.Update,
            InstallerOperation.Repair,
            InstallerOperation.Uninstall,
            InstallerOperation.Backup
        );
        viewModel.OperationChoices.Should().OnlyHaveUniqueItems(choice => choice.Label);
        viewModel.OperationChoices.Should().NotContain(choice => choice.Operation == InstallerOperation.Rollback);
        viewModel.InspectCommand.CanExecute(null).Should().BeFalse();
        session.InspectedOperations.Should().BeEmpty("choosing an operation requires an explicit user action");
        viewModel.DurableState.Should().Contain("Unchanged").And.Contain("no installer action has run");
    }

    [AvaloniaTest]
    public async Task SelectionDoesNotInspectAndChangingItClearsEveryStalePlanRow()
    {
        FakePlanSession session = new();
        await using PlanReviewViewModel viewModel = CreateViewModel(session);
        viewModel.SelectedOperation = Choice(viewModel, InstallerOperation.Install);

        session.InspectedOperations.Should().BeEmpty();
        viewModel.InspectCommand.CanExecute(null).Should().BeTrue();
        await viewModel.InspectCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();
        viewModel.IsResultVisible.Should().BeTrue();

        viewModel.SelectedOperation = Choice(viewModel, InstallerOperation.Backup);
        Dispatcher.UIThread.RunJobs();

        viewModel.Heading.Should().Contain("Operation changed");
        viewModel.Message.Should().Contain("previous preview was cleared");
        viewModel.IsResultVisible.Should().BeFalse();
        viewModel.OperationRows.Should().BeEmpty();
        viewModel.ConflictRows.Should().BeEmpty();
        viewModel.CandidateRows.Should().BeEmpty();
        viewModel.AdditionalNoticeDetail.Should().Be("No plan has been inspected.");
        session.InspectedOperations.Should().Equal(InstallerOperation.Install);
    }

    [AvaloniaTest]
    public async Task AvailablePlanUsesTypedAggregateCopyWithoutClaimingApprovalOrMutation()
    {
        InstallerReadOnlyPlanSuccess plan = CreatePlan(InstallerOperation.Install) with
        {
            HasBlockingConflicts = true,
            Risks = [ProtocolPlanRisk.ModifiedOrUnknownFileApproval],
            OperationCounts =
            [
                new(PlanOperationKind.Create, 2),
                new(PlanOperationKind.Replace, 1)
            ],
            ConflictCounts = [new(PlanConflictCode.UnknownCollision, 2)],
            CandidateCounts =
            [
                new(
                    FileReplacementCandidateReason.UnknownCollision,
                    FileReplacementCandidateDisposition.Replace,
                    true,
                    1
                )
            ],
            AdditionalNoticeCount = 3
        };
        FakePlanSession session = new() { Inspection = (_, _) => Task.FromResult<InstallerReadOnlyPlanResult>(plan) };
        await using PlanReviewViewModel viewModel = CreateViewModel(session);
        viewModel.SelectedOperation = Choice(viewModel, InstallerOperation.Install);

        await viewModel.InspectCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();

        viewModel.Heading.Should().Contain("preview").And.Contain("blocking conflicts");
        viewModel.Message.Should().Contain("cannot approve or run");
        viewModel.LiveAnnouncement.Should().Contain("preview").And.Contain("cannot approve or run");
        viewModel.OperationSummary.Should().Contain("3 planned file action").And.Contain("None have run");
        viewModel.ConflictSummary.Should().Contain("2 blocking conflict");
        viewModel.CandidateSummary.Should().Contain("not approval");
        viewModel.CandidateRows.Single().Detail.Should().Contain("Provisionally included").And.Contain("not approved by you");
        viewModel.AdditionalNoticeDetail.Should().Contain("3").And.Contain("does not expose their text");
        viewModel.SafetyDetail.Should().Contain("Cancel").And.Contain("no confirmation control");
        viewModel.DurableState.Should().Contain("no installer action has run");
    }

    [AvaloniaTest]
    public async Task ZeroActionConflictFreePlanStatesItsLimitations()
    {
        FakePlanSession session = new();
        await using PlanReviewViewModel viewModel = CreateViewModel(session);
        viewModel.SelectedOperation = Choice(viewModel, InstallerOperation.Install);

        await viewModel.InspectCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();

        viewModel.Heading.Should().Contain("preview only");
        viewModel.Message.Should().Contain("not approval").And.Contain("no action ran");
        viewModel.OperationSummary.Should().StartWith("0 planned file actions were reported");
        viewModel.OperationSummary.Should().NotContain("nothing to do").And.NotContain("safe");
        viewModel.ConflictSummary.Should().Contain("not approval");
        viewModel.RiskSummary.Should().Contain("not a safety guarantee");
    }

    [AvaloniaTest]
    [TestCase(ObservedInstallState.NotInstalled, "No managed fork receipt")]
    [TestCase(ObservedInstallState.KnownUnmodified, "managed files matched")]
    [TestCase(ObservedInstallState.KnownModified, "managed files differed")]
    [TestCase(ObservedInstallState.LegacyOrOfficial, "ownership is not confirmed")]
    [TestCase(ObservedInstallState.Unknown, "could not be classified")]
    public async Task EveryObservedStateHasEvidenceBoundedCopy(ObservedInstallState state, string expected)
    {
        InstallerOperation operation = state is ObservedInstallState.KnownUnmodified or ObservedInstallState.KnownModified
            ? InstallerOperation.Update
            : InstallerOperation.Install;
        InstallerReadOnlyPlanSuccess plan = CreatePlan(operation) with { ObservedState = state };
        FakePlanSession session = new() { Inspection = (_, _) => Task.FromResult<InstallerReadOnlyPlanResult>(plan) };
        await using PlanReviewViewModel viewModel = CreateViewModel(session);
        viewModel.SelectedOperation = Choice(viewModel, operation);

        await viewModel.InspectCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();

        viewModel.ObservedStateDetail.Should().Contain(expected);
        viewModel.ObservedStateDetail.ToLowerInvariant().Should().NotContain("definitely").And.NotContain("safe");
    }

    [AvaloniaTest]
    [TestCase(ProtocolPrePlanErrorCode.RequestCancelled, ProtocolNextAction.RetryRequest, "Try the same read-only inspection again")]
    [TestCase(ProtocolPrePlanErrorCode.InspectionFailed, ProtocolNextAction.InspectAgain, "Try the same read-only inspection again")]
    [TestCase(ProtocolPrePlanErrorCode.PermissionDenied, ProtocolNextAction.ReviewFilesystem, "Do not run the installer as root")]
    public async Task RetryableRejectionsExposeOnlyTruthfulTypedRecovery(
        ProtocolPrePlanErrorCode error,
        ProtocolNextAction action,
        string expected
    )
    {
        FakePlanSession session = new()
        {
            Inspection = (_, _) => Task.FromResult<InstallerReadOnlyPlanResult>(new InstallerReadOnlyPlanRejection(error, action, false))
        };
        await using PlanReviewViewModel viewModel = CreateViewModel(session);
        viewModel.SelectedOperation = Choice(viewModel, InstallerOperation.Install);

        await viewModel.InspectCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();

        viewModel.IsErrorVisible.Should().BeTrue();
        viewModel.IsRetryVisible.Should().BeTrue();
        viewModel.IsExitVisible.Should().BeFalse();
        viewModel.Message.Should().Contain(expected).And.Contain("No installer action ran");
        viewModel.LiveAnnouncement.Should().Contain(viewModel.Message);
    }

    [AvaloniaTest]
    public async Task TerminalRejectionPublishesClosingUntilCleanupActuallySettles()
    {
        TaskCompletionSource releaseCleanup = NewCompletion();
        FakePlanSession session = new()
        {
            Inspection = (_, _) => Task.FromResult<InstallerReadOnlyPlanResult>(new InstallerReadOnlyPlanRejection(
                ProtocolPrePlanErrorCode.UnexpectedFailure,
                ProtocolNextAction.ViewPrivateLog,
                true
            )),
            Disposal = () => releaseCleanup.Task
        };
        await using PlanReviewViewModel viewModel = CreateViewModel(session);
        viewModel.SelectedOperation = Choice(viewModel, InstallerOperation.Install);

        Task inspection = viewModel.InspectCommand.ExecuteAsync();
        await WaitUntilAsync(() => viewModel.Heading.StartsWith("Closing the read-only", StringComparison.Ordinal));

        viewModel.IsBusy.Should().BeTrue();
        viewModel.IsRetryVisible.Should().BeFalse();
        viewModel.IsExitVisible.Should().BeFalse();
        viewModel.LiveAnnouncement.Should().Contain("cleanup").And.Contain("no installer action ran");

        releaseCleanup.TrySetResult();
        await inspection;
        Dispatcher.UIThread.RunJobs();
        viewModel.IsBusy.Should().BeFalse();
        viewModel.IsExitVisible.Should().BeTrue();
        viewModel.Message.Should().Contain("private local log").And.NotContain("/tmp");
    }

    [AvaloniaTest]
    public async Task PresentationAutomationAndLiveTextNeverContainTheVisibleGamePath()
    {
        const string privatePath = "/home/private-user/secret-game";
        FakePlanSession session = new(privatePath);
        await using PlanReviewViewModel viewModel = CreateViewModel(session);
        viewModel.GameDetail.Should().Contain(privatePath, "the escaped path is intentionally visible in non-live text");
        viewModel.GameAccessibleName.Should().Be("Bound game folder").And.NotContain(privatePath);
        viewModel.LiveAnnouncement.Should().NotContain(privatePath);

        viewModel.SelectedOperation = Choice(viewModel, InstallerOperation.Install);
        await viewModel.InspectCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();

        viewModel.LiveAnnouncement.Should().NotContain(privatePath);
        viewModel.Heading.Should().NotContain(privatePath);
        viewModel.Message.Should().NotContain(privatePath);
    }

    [Test]
    public void ViewModelExposesNoApprovalConfirmationOrExecutionCommand()
    {
        string[] commandNames = typeof(PlanReviewViewModel).GetProperties()
            .Where(property => property.Name.EndsWith("Command", StringComparison.Ordinal))
            .Select(property => property.Name)
            .ToArray();

        commandNames.Should().Equal(
            nameof(PlanReviewViewModel.InspectCommand),
            nameof(PlanReviewViewModel.CancelCommand),
            nameof(PlanReviewViewModel.RetryCommand),
            nameof(PlanReviewViewModel.ExitCommand)
        );
        commandNames.Should().NotContain(name =>
            name.Contains("Approve", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Confirm", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Execute", StringComparison.OrdinalIgnoreCase)
        );
    }

    private static PlanReviewViewModel CreateViewModel(FakePlanSession session)
        => new(new PlanReviewController(session));

    private static PlanReviewOperationChoice Choice(PlanReviewViewModel viewModel, InstallerOperation operation)
        => viewModel.OperationChoices.Single(choice => choice.Operation == operation);

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

    private static TaskCompletionSource NewCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The expected plan-review presentation state was not reached.");
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class FakePlanSession : IPlanInspectionSession
    {
        private readonly object Sync = new();
        private readonly List<InstallerOperation> Operations = [];
        private int DisposeCount;

        public ProtocolReleaseIdentity Release { get; } = GameDiscoveryControllerTests.Release();
        public VerifiedGamePresentation Game { get; }
        public TaskCompletionSource<InstallerProtocolClientException> Fault { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<InstallerProtocolClientException> SessionFaulted => this.Fault.Task;
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

        public FakePlanSession(string path = "/games/Stardew Valley")
        {
            this.Game = new(path, "Stardew Valley");
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
