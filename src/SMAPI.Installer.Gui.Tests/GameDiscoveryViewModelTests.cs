using Avalonia.Automation;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using FluentAssertions;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Gui.Frontend;
using StardewModdingAPI.Installer.Gui.ViewModels;

namespace StardewModdingAPI.Installer.Gui.Tests;

internal sealed partial class GameDiscoveryAccessibilityTests
{
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
                viewModel.SelectedCandidate.Should().BeNull();
            viewModel.IsContinueVisible.Should().BeFalse();
            viewModel.Candidates.Should().HaveCount(candidates.Length);
        }
    }

    [AvaloniaTest]
    public async Task ManualInvalidReasonIsTypedAndManualValidContinueNeedsARealHandler()
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
        viewModel.IsContinueVisible.Should().BeFalse();

        await viewModel.ApplyManualFolderAsync("/games/manual");
        Dispatcher.UIThread.RunJobs();
        viewModel.Heading.Should().Be("Selected game folder is valid");
        viewModel.Message.Should().Contain("Nothing has been changed");
        viewModel.ContinueCommand.CanExecute(null).Should().BeFalse();
        viewModel.IsContinueVisible.Should().BeFalse();

        ProtocolGameCandidate? continued = null;
        EventHandler<GameCandidateSelectedEventArgs> handler = (_, args) => continued = args.Candidate;
        viewModel.ContinueRequested += handler;
        viewModel.ContinueCommand.CanExecute(null).Should().BeTrue();
        viewModel.IsContinueVisible.Should().BeTrue();
        viewModel.ContinueCommand.Execute(null);
        continued.Should().BeSameAs(valid);
        viewModel.ContinueRequested -= handler;
        viewModel.IsContinueVisible.Should().BeFalse();
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
            viewModel.Heading.Should().Be("Game-folder check cancelled");
            viewModel.IsRetryVisible.Should().BeTrue();
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
