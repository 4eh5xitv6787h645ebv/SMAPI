using Avalonia.Automation;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using FluentAssertions;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Gui.Backend;
using StardewModdingAPI.Installer.Gui.Frontend;
using StardewModdingAPI.Installer.Gui.ViewModels;

namespace StardewModdingAPI.Installer.Gui.Tests;

internal sealed partial class GameDiscoveryAccessibilityTests
{
    [Test]
    public void DisplayedCanonicalPathEscapesBidirectionalFormattingAndSurrogates()
    {
        ProtocolGameCandidate candidate = new(
            "/games/normal-\u202Espoof-\U0001F3AE",
            LinuxGameFolderStatus.Valid,
            "Stardew Valley"
        );

        GameCandidateItem item = new(candidate);

        item.CanonicalPath.Should().Be("/games/normal-\\u202Espoof-\\uD83C\\uDFAE");
        item.CanonicalPath.Any(character =>
            char.IsSurrogate(character)
            || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format
        ).Should().BeFalse();
        item.Candidate.CanonicalPath.Should().BeSameAs(candidate.CanonicalPath, "display escaping must not change backend path authority");
    }

    [Test]
    public void LongCommonPrefixPathsKeepDistinctAccessibleTails()
    {
        string commonPath = $"/games/{new string('a', 4000)}/";
        GameCandidateItem first = new(new(commonPath + "folder-one", LinuxGameFolderStatus.Valid, "Stardew Valley"));
        GameCandidateItem second = new(new(commonPath + "folder-two", LinuxGameFolderStatus.Valid, "Stardew Valley"));

        first.AccessibleName.Should().HaveLength(1024).And.EndWith("folder-one");
        second.AccessibleName.Should().HaveLength(1024).And.EndWith("folder-two");
        first.AccessibleName.Should().NotBe(second.AccessibleName);
        first.AccessibleName.Should().Contain("Ready");
        second.AccessibleName.Should().Contain("Ready");
    }

    [AvaloniaTest]
    public async Task OlderPostedSnapshotCannotRestoreSelectionAfterSessionFault()
    {
        ProtocolGameCandidate valid = GameDiscoveryControllerTests.Candidate("ready", LinuxGameFolderStatus.Valid);
        GameDiscoveryControllerTests.FakeVerifiedSession session = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([valid])
        };
        await using GameDiscoveryController controller = new(session);
        await using GameDiscoveryViewModel viewModel = new(controller);
        await controller.DiscoverAsync();
        Dispatcher.UIThread.RunJobs();
        GameDiscoverySnapshot ready = controller.Snapshot;
        ready.CanContinue.Should().BeTrue("the controller retains inert readiness for the future ownership handoff");

        session.Fault.TrySetResult(new InstallerProtocolClientException("late fault"));
        await WaitUntilAsync(() => controller.Snapshot is { State: GameDiscoveryState.SessionFaulted } snapshot
            && snapshot.Revision > ready.Revision);
        GameDiscoverySnapshot faulted = controller.Snapshot;
        faulted.Revision.Should().BeGreaterThan(ready.Revision);
        viewModel.ApplySnapshotForTesting(faulted);

        viewModel.ApplySnapshotForTesting(ready);

        viewModel.Heading.Should().Be("The verified installer session closed");
        viewModel.SelectedCandidate.Should().BeNull();
    }

    [AvaloniaTest]
    public async Task EmptyDiscoveryIsTruthfulAndFolderPickerIsAnExplicitEventSeam()
    {
        GameDiscoveryControllerTests.FakeVerifiedSession session = new();
        await using GameDiscoveryViewModel viewModel = CreateViewModel(session);
        int pickerRequests = 0;
        viewModel.FolderPickerRequested += (_, _) => pickerRequests++;

        await viewModel.StartAsync();
        Dispatcher.UIThread.RunJobs();

        viewModel.Heading.Should().Be("No Stardew Valley folder was found automatically");
        viewModel.Message.Should().Contain("Choose the game folder manually");
        viewModel.IsEmptyVisible.Should().BeTrue();
        viewModel.IsBrowseVisible.Should().BeTrue();
        viewModel.IsRetryVisible.Should().BeTrue();
        viewModel.DurableState.Should().Contain("no game files have been modified");

        viewModel.BrowseCommand.Execute(null);
        pickerRequests.Should().Be(1);
        session.ValidatedPaths.Should().BeEmpty("the picker seam never invents or reads a path");
        await viewModel.ApplyManualFolderAsync(null);
        session.ValidatedPaths.Should().BeEmpty("a cancelled picker performs no validation");
    }

    [AvaloniaTest]
    public async Task OneValidCandidateIsSelectedButInvalidOrManyCandidatesRequireExplicitSelection()
    {
        foreach (ProtocolGameCandidate[] candidates in new[]
        {
            new[] { GameDiscoveryControllerTests.Candidate("one", LinuxGameFolderStatus.Valid) },
            new[] { GameDiscoveryControllerTests.Candidate("invalid", LinuxGameFolderStatus.MissingLauncher) },
            new[]
            {
                GameDiscoveryControllerTests.Candidate("one", LinuxGameFolderStatus.Valid),
                GameDiscoveryControllerTests.Candidate("two", LinuxGameFolderStatus.MissingLauncher)
            }
        })
        {
            GameDiscoveryControllerTests.FakeVerifiedSession session = new()
            {
                Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>(candidates)
            };
            await using GameDiscoveryViewModel viewModel = CreateViewModel(session);
            await viewModel.StartAsync();
            Dispatcher.UIThread.RunJobs();

            bool autoSelected = candidates is [{ State: LinuxGameFolderStatus.Valid }];
            viewModel.Heading.Should().Be(autoSelected
                ? "Game folder selected"
                : candidates.Length == 1
                    ? "One possible game folder was found"
                    : "2 possible game folders were found");
            if (autoSelected)
            {
                viewModel.SelectedCandidate.Should().NotBeNull();
                viewModel.SelectedCandidate!.Candidate.Should().BeSameAs(candidates[0]);
            }
            else
            {
                viewModel.SelectedCandidate.Should().BeNull();
                viewModel.Message.Should().Be("Select one folder to review its validation result. Unsupported folders show a safe next step.");
                viewModel.Message.Should().NotContain("continue");
            }
            viewModel.Candidates.Should().HaveCount(candidates.Length);
        }
    }

    [AvaloniaTest]
    public async Task ManualInvalidReasonIsTypedAndManualValidSelectionRemainsReadOnly()
    {
        ProtocolGameCandidate invalid = GameDiscoveryControllerTests.Candidate("manual", LinuxGameFolderStatus.UnsupportedGameVersion);
        ProtocolGameCandidate valid = GameDiscoveryControllerTests.Candidate("manual", LinuxGameFolderStatus.Valid);
        Queue<ProtocolGameCandidate> results = new([invalid, valid]);
        GameDiscoveryControllerTests.FakeVerifiedSession session = new()
        {
            Validation = (_, _) => Task.FromResult(results.Dequeue())
        };
        await using GameDiscoveryViewModel viewModel = CreateViewModel(session);

        await viewModel.ApplyManualFolderAsync("/games/manual");
        Dispatcher.UIThread.RunJobs();
        viewModel.Heading.Should().Be("Game version is unsupported");
        viewModel.Message.Should().Contain("Update Stardew Valley");
        viewModel.IsProblemVisible.Should().BeTrue();
        viewModel.ProblemLiveSetting.Should().Be(AutomationLiveSetting.Polite);
        viewModel.LiveAnnouncement.Should().Be($"{viewModel.Heading}. {viewModel.Message}");

        await viewModel.ApplyManualFolderAsync("/games/manual");
        Dispatcher.UIThread.RunJobs();
        viewModel.Heading.Should().Be("Selected game folder is valid");
        viewModel.Message.Should().Contain("Nothing has been changed");
        viewModel.SelectedCandidate!.Candidate.Should().BeSameAs(valid);
    }

    [AvaloniaTest]
    public async Task TransferredStateAnnouncesATruthfulReadOnlyTransitionAndHidesEveryDiscoveryAction()
    {
        ProtocolGameCandidate valid = GameDiscoveryControllerTests.Candidate("transfer", LinuxGameFolderStatus.Valid);
        GameDiscoveryControllerTests.FakeVerifiedSession session = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([valid])
        };
        GameDiscoveryController controller = new(session);
        await using GameDiscoveryViewModel viewModel = new(controller);
        viewModel.FolderPickerRequested += (_, _) => { };
        GameDiscoveryFocusTarget? focus = null;
        viewModel.FocusRequested += (_, target) => focus = target;
        await viewModel.StartAsync();
        Dispatcher.UIThread.RunJobs();

        IPlanInspectionSession handoff = controller.TakeSelectedGameSession();
        Dispatcher.UIThread.RunJobs();

        viewModel.Heading.Should().Be("Opening plan review…");
        viewModel.Message.Should().Contain("read-only plan screen").And.Contain("Nothing has been changed");
        viewModel.Message.Should().NotContain("installing").And.NotContain("approved").And.NotContain("executed");
        viewModel.LiveAnnouncement.Should().Be($"{viewModel.Heading}. {viewModel.Message}");
        viewModel.LiveAnnouncement.Should().Contain("read-only").And.Contain("Nothing has been changed");
        focus.Should().Be(GameDiscoveryFocusTarget.Status);
        viewModel.Candidates.Should().BeEmpty();
        viewModel.SelectedCandidate.Should().BeNull();
        viewModel.IsCandidateListVisible.Should().BeFalse();
        viewModel.IsEmptyVisible.Should().BeFalse();
        viewModel.IsProblemVisible.Should().BeFalse();
        viewModel.IsBrowseVisible.Should().BeFalse();
        viewModel.IsRetryVisible.Should().BeFalse();
        viewModel.IsCancelVisible.Should().BeFalse();
        viewModel.IsExitVisible.Should().BeFalse();
        viewModel.BrowseCommand.CanExecute(null).Should().BeFalse();
        viewModel.RetryCommand.CanExecute(null).Should().BeFalse();
        viewModel.CancelCommand.CanExecute(null).Should().BeFalse();
        viewModel.ExitCommand.CanExecute(null).Should().BeFalse();

        await handoff.DisposeAsync();
    }

    [AvaloniaTest]
    public async Task EveryInvalidFolderStatusHasSpecificSafeGuidance()
    {
        foreach (LinuxGameFolderStatus status in Enum.GetValues<LinuxGameFolderStatus>().Where(value => value != LinuxGameFolderStatus.Valid))
        {
            ProtocolGameCandidate candidate = GameDiscoveryControllerTests.Candidate(status.ToString(), status);
            GameCandidateItem item = new(candidate);

            item.IsValid.Should().BeFalse();
            item.StatusLabel.Should().NotBeNullOrWhiteSpace();
            item.StatusDetail.Should().NotBeNullOrWhiteSpace();
            item.StatusDetail.ToLowerInvariant().Should().NotContain("exception");
        }
        await Task.CompletedTask;
    }

    [AvaloniaTest]
    public async Task CancellationAndSessionFaultHaveDistinctSafeRecoveryCopy()
    {
        TaskCompletionSource started = GameDiscoveryControllerTests.NewCompletion();
        GameDiscoveryControllerTests.FakeVerifiedSession cancelling = new()
        {
            Discovery = async cancellationToken =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Array.Empty<ProtocolGameCandidate>();
            }
        };
        await using (GameDiscoveryViewModel viewModel = CreateViewModel(cancelling))
        {
            Task load = viewModel.StartAsync();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await viewModel.CancelCommand.ExecuteAsync();
            await load;
            Dispatcher.UIThread.RunJobs();
            viewModel.Heading.Should().Be("Game-folder check cancelled and session closed");
            viewModel.Message.Should().Contain("Close and reopen");
            viewModel.IsRetryVisible.Should().BeFalse();
            viewModel.IsBrowseVisible.Should().BeFalse();
            viewModel.SelectedCandidate.Should().BeNull();
            viewModel.IsExitVisible.Should().BeTrue();
        }

        GameDiscoveryControllerTests.FakeVerifiedSession faulted = new();
        await using GameDiscoveryViewModel faultViewModel = CreateViewModel(faulted);
        await faultViewModel.StartAsync();
        faulted.Fault.TrySetResult(new("private SECRET /home/name"));
        await WaitForViewModelAsync(() => faultViewModel.Heading == "The verified installer session closed");
        faultViewModel.Message.Should().Contain("Close and reopen");
        faultViewModel.LiveAnnouncement.Should().NotContain("SECRET").And.NotContain("/home/name");
        faultViewModel.ProblemLiveSetting.Should().Be(AutomationLiveSetting.Assertive);
        faultViewModel.IsRetryVisible.Should().BeFalse();
    }

    [AvaloniaTest]
    public async Task BackendFailureTruthfullyRequiresAClosedSessionRestart()
    {
        GameDiscoveryControllerTests.FakeVerifiedSession session = new()
        {
            Discovery = _ => throw new InstallerProtocolClientException("private SECRET /home/name")
        };
        await using GameDiscoveryViewModel viewModel = CreateViewModel(session);

        await viewModel.StartAsync();
        Dispatcher.UIThread.RunJobs();

        viewModel.Heading.Should().Be("The verified installer session stopped safely");
        viewModel.Message.Should().Contain("Close and reopen");
        viewModel.LiveAnnouncement.Should().NotContain("SECRET").And.NotContain("/home/name");
        viewModel.IsRetryVisible.Should().BeFalse();
        viewModel.IsBrowseVisible.Should().BeFalse();
        viewModel.IsExitVisible.Should().BeTrue();
    }

    private static GameDiscoveryViewModel CreateViewModel(GameDiscoveryControllerTests.FakeVerifiedSession session)
    {
        return new(new GameDiscoveryController(session));
    }

    private static async Task WaitForViewModelAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The expected game-discovery view-model state was not reached.");
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }
}
