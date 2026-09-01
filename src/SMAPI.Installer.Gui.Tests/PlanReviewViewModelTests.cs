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
        InstallerReadOnlyPlanSuccess plan = CandidatePlan(
            InstallerOperation.Install,
            [CandidateCapability("mods/private.dll", true)]
        ) with
        {
            HasBlockingConflicts = true,
            Confirmation = null,
            Risks = [ProtocolPlanRisk.ModifiedOrUnknownFileApproval],
            OperationCounts =
            [
                new(PlanOperationKind.Create, 2),
                new(PlanOperationKind.Replace, 1)
            ],
            ConflictCounts = [new(PlanConflictCode.UnknownCollision, 2)],
            AdditionalNoticeCount = 3
        };
        FakePlanSession session = new() { Inspection = (_, _) => Task.FromResult<InstallerReadOnlyPlanResult>(plan) };
        await using PlanReviewViewModel viewModel = CreateViewModel(session);
        viewModel.SelectedOperation = Choice(viewModel, InstallerOperation.Install);

        await viewModel.InspectCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();

        viewModel.Heading.Should().Contain("preview").And.Contain("blocking conflicts");
        viewModel.Message.Should().Contain("cannot confirm or execute");
        viewModel.LiveAnnouncement.Should().Contain("preview").And.Contain("cannot confirm or execute");
        viewModel.OperationSummary.Should().Contain("3 planned file action").And.Contain("None have run");
        viewModel.ConflictSummary.Should().Contain("2 blocking conflict");
        viewModel.CandidateSummary.Should().Contain("not approval");
        viewModel.CandidateRows.Single().Detail.Should().Contain("Provisionally included").And.Contain("not approved by you");
        viewModel.AdditionalNoticeDetail.Should().Contain("3").And.Contain("does not expose their text");
        viewModel.SafetyDetail.Should().Contain("Cancel").And.Contain("Confirm plan").And.Contain("does not change files").And.Contain("explicit Run");
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
        viewModel.Message.Should().Contain("not confirmation").And.Contain("no file action ran");
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
        viewModel.GameDetail.Should().Be("Validated Stardew Valley game folder").And.NotContain(privatePath);
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
    public void ViewModelExposesBoundedConfirmationButNoExecutionCommandOrAuthority()
    {
        string[] commandNames = typeof(PlanReviewViewModel).GetProperties()
            .Where(property => property.Name.EndsWith("Command", StringComparison.Ordinal))
            .Select(property => property.Name)
            .ToArray();

        commandNames.Should().Equal(
            nameof(PlanReviewViewModel.InspectCommand),
            nameof(PlanReviewViewModel.CancelCommand),
            nameof(PlanReviewViewModel.RetryCommand),
            nameof(PlanReviewViewModel.ApplyCandidatesCommand),
            nameof(PlanReviewViewModel.ClearCandidatesCommand),
            nameof(PlanReviewViewModel.StartFreshInspectionCommand),
            nameof(PlanReviewViewModel.ConfirmCommand),
            nameof(PlanReviewViewModel.ExitCommand)
        );
        commandNames.Should().NotContain(name =>
            name.Contains("Execute", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Run", StringComparison.OrdinalIgnoreCase)
        );
    }

    [AvaloniaTest]
    public async Task CandidateChoicesStartUncheckedAndExposeOnlyFixedEvidenceBoundedCopy()
    {
        InstallerReadOnlyPlanCandidate candidate = CandidateCapability("mods/bi\u202Edi.dll", true);
        FakePlanSession session = new()
        {
            Inspection = (operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CandidatePlan(operation, [candidate]))
        };
        await using PlanReviewViewModel viewModel = CreateViewModel(session);
        viewModel.SelectedOperation = Choice(viewModel, InstallerOperation.Install);

        await viewModel.InspectCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();

        PlanReviewCandidateChoice row = viewModel.CandidateChoices.Should().ContainSingle().Which;
        row.IsSelected.Should().BeFalse("backend-provisional inclusion is never a user default");
        row.BackendProvisionallyIncluded.Should().BeTrue();
        row.DisplayPath.Should().Be("mods/bi\\u202Edi.dll").And.NotContain("\u202E");
        row.ReasonDetail.Should().Contain("differs from its recorded identity").And.Contain("cause was not observed");
        row.DispositionDetail.Should().Contain("later confirmed plan may replace");
        row.ProvisionalDetail.Should().Contain("not your approval");
        row.AccessibleName.Should().Contain(row.DisplayPath).And.NotContain(new string('a', 64)).And.NotContain("private evidence");
        viewModel.ApplyCandidatesCommand.CanExecute(null).Should().BeFalse();
        viewModel.ClearCandidatesCommand.CanExecute(null).Should().BeFalse();
        viewModel.CandidateSelectionAnnouncement.Should().Be("0 of 1 files selected.").And.NotContain(row.DisplayPath);
        session.ApprovedCandidates.Should().BeEmpty();
    }

    [AvaloniaTest]
    public async Task CandidateSelectionIsExplicitClearIsLocalAndLiveCopyIsCountOnly()
    {
        InstallerReadOnlyPlanCandidate first = CandidateCapability("mods/first.dll", false);
        InstallerReadOnlyPlanCandidate second = CandidateCapability("mods/second.dll", false);
        FakePlanSession session = new()
        {
            Inspection = (operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CandidatePlan(operation, [first, second]))
        };
        await using PlanReviewViewModel viewModel = CreateViewModel(session);
        viewModel.SelectedOperation = Choice(viewModel, InstallerOperation.Update);
        await viewModel.InspectCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();

        viewModel.CandidateChoices[1].IsSelected = true;
        Dispatcher.UIThread.RunJobs();

        viewModel.CandidateChoices.Select(choice => choice.IsSelected).Should().Equal(false, true);
        viewModel.CandidateSelectionAnnouncement.Should().Be("1 of 2 files selected.");
        viewModel.CandidateSelectionAnnouncement.Should().NotContain("first.dll").And.NotContain("second.dll");
        viewModel.ApplyCandidatesCommand.CanExecute(null).Should().BeTrue();
        viewModel.ClearCandidatesCommand.CanExecute(null).Should().BeTrue();
        session.ApprovedCandidates.Should().BeEmpty("local selection and clearing must not contact the backend");

        viewModel.ClearCandidatesCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        viewModel.CandidateChoices.Should().OnlyContain(choice => !choice.IsSelected);
        viewModel.CandidateSelectionAnnouncement.Should().Be("0 of 2 files selected.");
        viewModel.ApplyCandidatesCommand.CanExecute(null).Should().BeFalse();
        session.ApprovedCandidates.Should().BeEmpty();
    }

    [AvaloniaTest]
    public async Task ApplyIsAdditiveBusyCopyIsNonMutatingAndFreshInspectionRemainsReachableAtZeroCandidates()
    {
        InstallerReadOnlyPlanCandidate candidate = CandidateCapability("mods/only.dll", false);
        TaskCompletionSource releaseApproval = NewCompletion();
        int inspections = 0;
        FakePlanSession session = new()
        {
            Inspection = (operation, _) =>
            {
                inspections++;
                return Task.FromResult<InstallerReadOnlyPlanResult>(CandidatePlan(operation, [candidate]));
            },
            Approval = async (_, _) =>
            {
                await releaseApproval.Task;
                return CandidatePlan(InstallerOperation.Install, []);
            }
        };
        await using PlanReviewViewModel viewModel = CreateViewModel(session);
        viewModel.SelectedOperation = Choice(viewModel, InstallerOperation.Install);
        await viewModel.InspectCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();
        viewModel.CandidateChoices.Single().IsSelected = true;

        Task apply = viewModel.ApplyCandidatesCommand.ExecuteAsync();
        await WaitUntilAsync(() => viewModel.IsBusy);

        viewModel.Heading.Should().Contain("additive candidate approvals");
        viewModel.Message.Should().Contain("No files are being changed").And.Contain("confirmed").And.Contain("executed");
        viewModel.IsCandidateReviewVisible.Should().BeFalse("candidate authority is revoked while approval is active");
        releaseApproval.TrySetResult();
        await apply;
        Dispatcher.UIThread.RunJobs();

        session.ApprovedCandidates.Should().ContainSingle().Which.Should().ContainSingle().Which.Should().BeSameAs(candidate);
        viewModel.CandidateChoices.Should().BeEmpty("accepted candidates disappear from the refreshed preview");
        viewModel.IsCandidateReviewVisible.Should().BeTrue("fresh inspection must remain reachable after the final candidate disappears");
        viewModel.CandidateSelectionAnnouncement.Should().Be("1 approval already applied and fixed in this preview; 0 of 0 remaining files selected.");
        viewModel.CandidateReviewDetail.Should().Contain("1 additive file approval is already applied")
            .And.Contain("cannot be removed individually")
            .And.Contain("0 candidates remain")
            .And.Contain("cannot confirm or execute");
        viewModel.IsCandidateApprovalCapacityFull.Should().BeFalse();
        viewModel.IsCandidateSelectionOverRemainingCapacity.Should().BeFalse();
        viewModel.IsCandidateCapacityDetailVisible.Should().BeFalse();
        viewModel.CandidateCapacityDetail.Should().BeEmpty();
        viewModel.StartFreshInspectionCommand.CanExecute(null).Should().BeTrue();

        await viewModel.StartFreshInspectionCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();

        inspections.Should().Be(2);
        viewModel.CandidateChoices.Should().ContainSingle().Which.IsSelected.Should().BeFalse();
        viewModel.StartFreshInspectionCommand.CanExecute(null).Should().BeFalse();
    }

    [AvaloniaTest]
    public async Task MultipleApprovalRoundsReportImmutableCumulativeAndRemainingCountsWithoutPaths()
    {
        InstallerReadOnlyPlanCandidate first = CandidateCapability("mods/first-private.dll", false);
        InstallerReadOnlyPlanCandidate second = CandidateCapability("mods/second-private.dll", false);
        InstallerReadOnlyPlanCandidate third = CandidateCapability("mods/third-private.dll", false);
        Queue<InstallerReadOnlyPlanResult> approvalResults = new(
        [
            CandidatePlan(InstallerOperation.Install, [second, third]),
            CandidatePlan(InstallerOperation.Install, [third])
        ]);
        FakePlanSession session = new()
        {
            Inspection = (operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CandidatePlan(operation, [first, second, third])),
            Approval = (_, _) => Task.FromResult(approvalResults.Dequeue())
        };
        await using PlanReviewViewModel viewModel = CreateViewModel(session);
        viewModel.SelectedOperation = Choice(viewModel, InstallerOperation.Install);
        await viewModel.InspectCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();

        viewModel.CandidateChoices[0].IsSelected = true;
        await viewModel.ApplyCandidatesCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();

        viewModel.CandidateReviewDetail.Should().Contain("1 additive file approval is already applied")
            .And.Contain("cannot be removed individually")
            .And.Contain("2 candidates remain");
        viewModel.CandidateSelectionAnnouncement.Should().Be("1 approval already applied and fixed in this preview; 0 of 2 remaining files selected.");
        viewModel.CandidateSelectionAnnouncement.Should().NotContain("private.dll");
        viewModel.ApplyCandidatesCommand.CanExecute(null).Should().BeFalse();
        viewModel.ClearCandidatesCommand.CanExecute(null).Should().BeFalse();
        viewModel.StartFreshInspectionCommand.CanExecute(null).Should().BeTrue();

        viewModel.CandidateChoices.Single(choice => choice.DisplayPath.Contains("second", StringComparison.Ordinal)).IsSelected = true;
        viewModel.CandidateSelectionAnnouncement.Should().Be("1 approval already applied and fixed in this preview; 1 of 2 remaining files selected.");
        viewModel.ApplyCandidatesCommand.CanExecute(null).Should().BeTrue();
        await viewModel.ApplyCandidatesCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();

        viewModel.CandidateReviewDetail.Should().Contain("2 additive file approvals are already applied")
            .And.Contain("1 candidate remains");
        viewModel.CandidateSelectionAnnouncement.Should().Be("2 approvals already applied and fixed in this preview; 0 of 1 remaining files selected.");
        viewModel.CandidateSelectionAnnouncement.Should().NotContain("third-private.dll");
        viewModel.IsCandidateApprovalCapacityFull.Should().BeFalse();
        viewModel.IsCandidateSelectionOverRemainingCapacity.Should().BeFalse();
        viewModel.IsCandidateCapacityDetailVisible.Should().BeFalse();
        viewModel.CandidateCapacityDetail.Should().BeEmpty();
        session.ApprovedCandidates.Should().HaveCount(2);
        session.ApprovedCandidates.SelectMany(candidates => candidates).Should().Equal(first, second);
    }

    [AvaloniaTest]
    public async Task FullApprovalHistoryLeavesRemainingChoiceSelectableButDisablesApply()
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
        await using PlanReviewViewModel viewModel = CreateViewModel(session);
        viewModel.SelectedOperation = Choice(viewModel, InstallerOperation.Install);
        await viewModel.InspectCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();
        foreach (PlanReviewCandidateChoice choice in viewModel.CandidateChoices)
            choice.IsSelected = true;

        await viewModel.ApplyCandidatesCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();
        viewModel.CandidateChoices.Single().IsSelected = true;

        viewModel.CandidateSelectionAnnouncement.Should().Be($"{ProtocolJsonSerializer.MaxPlanCandidates} approvals already applied and fixed in this preview; 1 of 1 remaining files selected.");
        viewModel.CandidateSelectionAnnouncement.Should().NotContain("remaining-private.dll");
        viewModel.CandidateReviewDetail.Should().Contain($"{ProtocolJsonSerializer.MaxPlanCandidates} additive file approvals are already applied")
            .And.Contain("1 candidate remains")
            .And.Contain("cannot be removed individually");
        viewModel.IsCandidateApprovalCapacityFull.Should().BeTrue();
        viewModel.IsCandidateSelectionOverRemainingCapacity.Should().BeTrue();
        viewModel.IsCandidateCapacityDetailVisible.Should().BeTrue();
        viewModel.CandidateCapacityDetail.Should().Contain("bounded approval history is full")
            .And.Contain("no more candidate approvals fit")
            .And.Contain("Clear local choices only unchecks this screen")
            .And.Contain("does not free approval capacity")
            .And.Contain("Start a fresh inspection")
            .And.Contain("No files change")
            .And.Contain("cannot confirm or execute");
        viewModel.CandidateCapacityDetail.Should().NotContain("remaining-private.dll");
        viewModel.IsCandidateSelectionEnabled.Should().BeTrue("the user may still inspect and clear a local choice");
        viewModel.ClearCandidatesCommand.CanExecute(null).Should().BeTrue();
        viewModel.ApplyCandidatesCommand.CanExecute(null).Should().BeFalse("the bounded additive approval history is full");
        session.ApprovedCandidates.Should().ContainSingle().Which.Should().HaveCount(ProtocolJsonSerializer.MaxPlanCandidates);
    }

    [AvaloniaTest]
    public async Task SelectionBeyondRemainingApprovalCapacityExplainsHowToRestoreApply()
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
        await using PlanReviewViewModel viewModel = CreateViewModel(session);
        viewModel.SelectedOperation = Choice(viewModel, InstallerOperation.Install);
        await viewModel.InspectCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();
        foreach (PlanReviewCandidateChoice choice in viewModel.CandidateChoices)
            choice.IsSelected = true;
        await viewModel.ApplyCandidatesCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();

        foreach (PlanReviewCandidateChoice choice in viewModel.CandidateChoices)
            choice.IsSelected = true;

        viewModel.IsCandidateApprovalCapacityFull.Should().BeFalse();
        viewModel.IsCandidateSelectionOverRemainingCapacity.Should().BeTrue();
        viewModel.IsCandidateCapacityDetailVisible.Should().BeTrue();
        viewModel.CandidateCapacityDetail.Should().Contain("Only 1 more approval fits")
            .And.Contain("2 files are selected")
            .And.Contain("Apply is unavailable")
            .And.Contain("Uncheck files or Clear local choices")
            .And.Contain("start a fresh inspection")
            .And.Contain("No files change")
            .And.Contain("cannot confirm or execute");
        viewModel.CandidateCapacityDetail.Should().NotContain("remaining-private.dll");
        viewModel.CandidateSelectionAnnouncement.Should().Be($"{ProtocolJsonSerializer.MaxPlanCandidates - 1} approvals already applied and fixed in this preview; 2 of 2 remaining files selected.");
        viewModel.ApplyCandidatesCommand.CanExecute(null).Should().BeFalse();
        viewModel.ClearCandidatesCommand.CanExecute(null).Should().BeTrue();

        viewModel.CandidateChoices[1].IsSelected = false;

        viewModel.IsCandidateSelectionOverRemainingCapacity.Should().BeFalse();
        viewModel.IsCandidateCapacityDetailVisible.Should().BeFalse();
        viewModel.CandidateCapacityDetail.Should().BeEmpty();
        viewModel.CandidateSelectionAnnouncement.Should().Be($"{ProtocolJsonSerializer.MaxPlanCandidates - 1} approvals already applied and fixed in this preview; 1 of 2 remaining files selected.");
        viewModel.ApplyCandidatesCommand.CanExecute(null).Should().BeTrue();
        viewModel.ClearCandidatesCommand.CanExecute(null).Should().BeTrue();
        session.ApprovedCandidates.Should().ContainSingle().Which.Should().HaveCount(ProtocolJsonSerializer.MaxPlanCandidates - 1);
    }

    [AvaloniaTest]
    public async Task StaleCandidateRowResynchronizesWithoutReplacingTheValidCurrentPreview()
    {
        InstallerReadOnlyPlanCandidate oldCandidate = CandidateCapability("mods/old.dll", false);
        InstallerReadOnlyPlanCandidate currentCandidate = CandidateCapability("mods/current.dll", false);
        FakePlanSession session = new()
        {
            Inspection = (operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CandidatePlan(operation, [oldCandidate])),
            Approval = (_, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CandidatePlan(InstallerOperation.Install, [currentCandidate]))
        };
        await using PlanReviewViewModel viewModel = CreateViewModel(session);
        viewModel.SelectedOperation = Choice(viewModel, InstallerOperation.Install);
        await viewModel.InspectCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();
        PlanReviewCandidateChoice stale = viewModel.CandidateChoices.Single();
        stale.IsSelected = true;
        await viewModel.ApplyCandidatesCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();
        string heading = viewModel.Heading;

        stale.IsSelected = true;
        Dispatcher.UIThread.RunJobs();

        stale.IsSelected.Should().BeFalse();
        viewModel.CandidateChoices.Should().ContainSingle().Which.DisplayPath.Should().Be("mods/current.dll");
        viewModel.CandidateChoices.Single().IsSelected.Should().BeFalse();
        viewModel.Heading.Should().Be(heading);
        viewModel.Message.Should().NotContain("close and reopen");
        session.ApprovedCandidates.Should().ContainSingle();
    }

    [AvaloniaTest]
    public async Task StaleQueuedClearResynchronizesAfterControllerRevokesCandidateAuthorityWithoutBackendCall()
    {
        InstallerReadOnlyPlanCandidate candidate = CandidateCapability("mods/stale-clear.dll", false);
        FakePlanSession session = new()
        {
            Inspection = (operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CandidatePlan(operation, [candidate]))
        };
        PlanReviewController controller = new(session);
        await using PlanReviewViewModel viewModel = new(controller);
        viewModel.SelectedOperation = Choice(viewModel, InstallerOperation.Install);
        await viewModel.InspectCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();
        viewModel.CandidateChoices.Single().IsSelected = true;
        viewModel.ClearCandidatesCommand.CanExecute(null).Should().BeTrue();

        await Task.Run(() => controller.SelectOperation(InstallerOperation.Update));
        Action staleClear = () => viewModel.ClearCandidatesCommand.Execute(null);

        staleClear.Should().NotThrow();
        viewModel.SelectedOperation!.Operation.Should().Be(InstallerOperation.Update);
        viewModel.CandidateChoices.Should().BeEmpty();
        viewModel.Heading.Should().Contain("Operation changed");
        session.ApprovedCandidates.Should().BeEmpty("stale local clearing must never cross the backend boundary");
        session.InspectedOperations.Should().Equal(InstallerOperation.Install);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaTest]
    [TestCase(FileReplacementCandidateReason.ModifiedReceiptOwned, FileReplacementCandidateDisposition.Replace, "cause was not observed", "may replace")]
    [TestCase(FileReplacementCandidateReason.ModifiedReceiptOwned, FileReplacementCandidateDisposition.Remove, "cause was not observed", "may remove")]
    [TestCase(FileReplacementCandidateReason.ModifiedInstalledLauncher, FileReplacementCandidateDisposition.Restore, "cause was not observed", "may restore")]
    [TestCase(FileReplacementCandidateReason.LegacyInstaller, FileReplacementCandidateDisposition.Replace, "exact file was classified", "may replace")]
    [TestCase(FileReplacementCandidateReason.UnknownCollision, FileReplacementCandidateDisposition.Replace, "owner and creator are unknown", "may replace")]
    [TestCase(FileReplacementCandidateReason.OfficialOrLegacyLauncher, FileReplacementCandidateDisposition.Replace, "ownership is unconfirmed", "may replace")]
    [TestCase(FileReplacementCandidateReason.OfficialLauncherBackup, FileReplacementCandidateDisposition.TrustRetained, "exact backup meets", "may retain")]
    public async Task CandidateReasonAndDispositionCopyIsTypedAndDoesNotAttributeUnobservedCause(
        FileReplacementCandidateReason reason,
        FileReplacementCandidateDisposition disposition,
        string expectedReason,
        string expectedDisposition
    )
    {
        InstallerReadOnlyPlanCandidate candidate = CandidateCapability("mods/typed.dll", false, reason, disposition);
        FakePlanSession session = new()
        {
            Inspection = (operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(CandidatePlan(operation, [candidate]))
        };
        await using PlanReviewViewModel viewModel = CreateViewModel(session);
        viewModel.SelectedOperation = Choice(viewModel, InstallerOperation.Install);

        await viewModel.InspectCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();

        PlanReviewCandidateChoice row = viewModel.CandidateChoices.Single();
        row.ReasonDetail.Should().Contain(expectedReason);
        row.DispositionDetail.Should().Contain(expectedDisposition).And.Contain("later confirmed plan");
        row.AccessibleName.Should().Contain(row.DisplayPath).And.Contain(row.ReasonDetail).And.Contain(row.DispositionDetail);
    }

    [AvaloniaTest]
    public async Task BackupNeverExposesCandidateApprovalControls()
    {
        FakePlanSession session = new();
        await using PlanReviewViewModel viewModel = CreateViewModel(session);
        viewModel.SelectedOperation = Choice(viewModel, InstallerOperation.Backup);

        await viewModel.InspectCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();

        viewModel.IsCandidateReviewVisible.Should().BeFalse();
        viewModel.CandidateChoices.Should().BeEmpty();
        viewModel.ApplyCandidatesCommand.CanExecute(null).Should().BeFalse();
        viewModel.ClearCandidatesCommand.CanExecute(null).Should().BeFalse();
        viewModel.StartFreshInspectionCommand.CanExecute(null).Should().BeFalse();
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
        )
        {
            Confirmation = new InstallerPlanConfirmation()
        };
    }

    private static InstallerReadOnlyPlanSuccess CandidatePlan(
        InstallerOperation operation,
        IReadOnlyList<InstallerReadOnlyPlanCandidate> candidates
    )
    {
        return CreatePlan(operation) with
        {
            Risks = candidates.Count == 0 ? [] : [ProtocolPlanRisk.ModifiedOrUnknownFileApproval],
            CandidateCounts = candidates
                .GroupBy(candidate => new { candidate.Reason, candidate.Disposition, candidate.BackendProvisionallyIncluded })
                .Select(group => new InstallerPlanCandidateCount(group.Key.Reason, group.Key.Disposition, group.Key.BackendProvisionallyIncluded, group.Count()))
                .ToArray(),
            Candidates = candidates
        };
    }

    private static InstallerReadOnlyPlanCandidate CandidateCapability(
        string path,
        bool provisional,
        FileReplacementCandidateReason reason = FileReplacementCandidateReason.ModifiedReceiptOwned,
        FileReplacementCandidateDisposition disposition = FileReplacementCandidateDisposition.Replace
    )
    {
        return new(new ProtocolPlanCandidate(
            ProtocolCandidateId.Parse(Guid.NewGuid().ToString("N")),
            reason,
            disposition,
            path,
            new string('a', 64),
            123,
            420,
            new string('b', 64),
            provisional,
            "private evidence"
        ));
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
        public Func<IReadOnlyList<InstallerReadOnlyPlanCandidate>, CancellationToken, Task<InstallerReadOnlyPlanResult>> Approval { get; init; }
            = (_, _) => throw new AssertionException("Candidate approval wasn't expected.");
        public Func<Task> Disposal { get; init; } = () => Task.CompletedTask;
        public List<IReadOnlyList<InstallerReadOnlyPlanCandidate>> ApprovedCandidates { get; } = [];

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

        public Task<InstallerReadOnlyPlanResult> ApprovePlanCandidatesAsync(
            IReadOnlyList<InstallerReadOnlyPlanCandidate> candidates,
            CancellationToken cancellationToken = default
        )
        {
            InstallerReadOnlyPlanCandidate[] snapshot = candidates.ToArray();
            this.ApprovedCandidates.Add(snapshot);
            return this.Approval(snapshot, cancellationToken);
        }
    }
}
