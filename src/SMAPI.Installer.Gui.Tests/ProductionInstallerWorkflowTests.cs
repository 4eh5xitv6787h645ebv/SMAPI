using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using FluentAssertions;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Packages;
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

    private static ProductionInstallerWorkflow CreateWorkflow(
        TrackingClient client,
        Action<Window> activate,
        Func<GameDiscoveryViewModel, GameDiscoveryWindow>? windowFactory = null,
        Func<GameDiscoveryWindow, Task<string?>>? pickFolder = null,
        Func<PlanReviewViewModel, PlanReviewWindow>? planWindowFactory = null
    )
    {
        ReviewedReleaseCandidate candidate = ReleaseVerificationViewModelTests.Candidate();
        ReleaseVerificationViewModelTests.FakeReleaseService service = new([candidate])
        {
            CompletePreparation = true
        };
        return new(service, () => client, activate, windowFactory, pickFolder, planWindowFactory);
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
        public string? ValidatedPath { get; private set; }
        public IReadOnlyList<ProtocolGameCandidate> DiscoveredGames { get; init; } = [];
        public bool ThrowOnDispose { get; init; }
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
                ["verified-local-package", "linux-game-discovery", "linux-game-validation"]
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
        ) => throw new AssertionException("The release-to-discovery workflow must not inspect a plan.");

        public ValueTask DisposeAsync()
        {
            this.DisposeCalls++;
            if (this.ThrowOnDispose)
                throw new InvalidOperationException("synthetic private cleanup detail");
            return ValueTask.CompletedTask;
        }
    }
}
