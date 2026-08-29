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

    private static ProductionInstallerWorkflow CreateWorkflow(
        TrackingClient client,
        Action<Window> activate,
        Func<GameDiscoveryViewModel, GameDiscoveryWindow>? windowFactory = null,
        Func<GameDiscoveryWindow, Task<string?>>? pickFolder = null
    )
    {
        ReviewedReleaseCandidate candidate = ReleaseVerificationViewModelTests.Candidate();
        ReleaseVerificationViewModelTests.FakeReleaseService service = new([candidate])
        {
            CompletePreparation = true
        };
        return new(service, () => client, activate, windowFactory, pickFolder);
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
        public bool ThrowOnDispose { get; init; }
        public Task<InstallerProtocolClientException> SessionFaulted => this.Fault.Task;

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
                ForkReleaseIdentity.Repository,
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
            => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([]);

        public Task<ProtocolGameCandidate> ValidateGameAsync(string canonicalPath, CancellationToken cancellationToken = default)
        {
            this.ValidatedPath = canonicalPath;
            return Task.FromResult(new ProtocolGameCandidate(
                canonicalPath,
                LinuxGameFolderStatus.Valid,
                "Stardew Valley test installation"
            ));
        }

        public ValueTask DisposeAsync()
        {
            this.DisposeCalls++;
            if (this.ThrowOnDispose)
                throw new InvalidOperationException("synthetic private cleanup detail");
            return ValueTask.CompletedTask;
        }
    }
}
