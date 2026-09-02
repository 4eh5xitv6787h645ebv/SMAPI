using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using FluentAssertions;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Gui.Backend;
using StardewModdingAPI.Installer.Gui.Frontend;
using StardewModdingAPI.Installer.Gui.ViewModels;

namespace StardewModdingAPI.Installer.Gui.Tests;

[NonParallelizable]
internal sealed class LocalPackagePickerUxTests
{
    private const string SelectedPath = "/home/private-person/Downloads/private-release-assets";

    [AvaloniaTest]
    public async Task EmptyCatalogOffersNamedAltLActionAndNeutralCancelRestoresFocus()
    {
        WorkflowHarness harness = this.CreateWorkflow(
            pickLocalReleaseFolder: _ => Task.FromResult<string?>(null)
        );
        ReleaseVerificationWindow window = harness.Workflow.CreateInitialWindow();
        try
        {
            window.Show();
            ReleaseVerificationViewModel viewModel = (ReleaseVerificationViewModel)window.DataContext!;
            await WaitUntilAsync(() => viewModel.IsLocalPackageActionVisible && viewModel.IsEmptyVisible);
            Button local = window.FindControl<Button>("LocalPackageButton")!;
            Control[] namedActions =
            [
                window.FindControl<ComboBox>("ReleaseSelector")!,
                window.FindControl<Button>("DownloadButton")!,
                local,
                window.FindControl<Button>("RetryButton")!,
                window.FindControl<Button>("ContinueButton")!,
                window.FindControl<Button>("CancelButton")!,
                window.FindControl<InstallerDiagnosticsAccess>("DiagnosticsAccess")!.FindControl<Button>("OpenButton")!
            ];

            local.IsVisible.Should().BeTrue();
            local.IsEffectivelyEnabled.Should().BeTrue();
            AutomationProperties.GetName(local).Should().Be("Use local release package folder");
            AutomationProperties.GetHelpText(local).Should().Contain("exactly the six release files");
            AutomationProperties.GetAccessKey(local).Should().Be("Alt+L");
            namedActions.Select(AutomationProperties.GetAccessKey)
                .Should().NotContainNulls().And.OnlyHaveUniqueItems();
            local.IsFocused.Should().BeTrue("the useful local action is the empty-state focus target");

            PressAccessKey(window, PhysicalKey.L);
            await WaitUntilAsync(() => harness.PickerCalls == 1 && viewModel.UseLocalPackageCommand.CanExecute(null));

            harness.LocalService.Calls.Should().Be(0, "picker cancellation must not read a release folder");
            harness.ClientFactoryCalls.Should().Be(0, "picker cancellation must not create backend authority");
            viewModel.Heading.Should().Be("No compatible graphical-installer release is available");
            viewModel.IsErrorVisible.Should().BeFalse();
            local.IsFocused.Should().BeTrue();
        }
        finally
        {
            window.Close();
            await WaitUntilAsync(() => !window.IsVisible);
        }
    }

    [AvaloniaTest]
    public async Task PickerFailureIsSanitizedAndReturnsFocusWithoutReadingFiles()
    {
        WorkflowHarness harness = this.CreateWorkflow(
            pickLocalReleaseFolder: _ => throw new InvalidOperationException($"portal leaked {SelectedPath} token=SECRET")
        );
        ReleaseVerificationWindow window = harness.Workflow.CreateInitialWindow();
        try
        {
            window.Show();
            ReleaseVerificationViewModel viewModel = (ReleaseVerificationViewModel)window.DataContext!;
            await WaitUntilAsync(() => viewModel.IsLocalPackageActionVisible);

            PressAccessKey(window, PhysicalKey.L);
            await WaitUntilAsync(() => viewModel.Heading == "The desktop folder picker could not open");

            viewModel.IsErrorVisible.Should().BeTrue();
            viewModel.Message.Should().Contain("No release files were read");
            viewModel.LiveAnnouncement.Should().NotContain(SelectedPath).And.NotContain("SECRET").And.NotContain("portal leaked");
            harness.LocalService.Calls.Should().Be(0);
            harness.ClientFactoryCalls.Should().Be(0);
            window.FindControl<Button>("LocalPackageButton")!.IsFocused.Should().BeTrue();
        }
        finally
        {
            window.Close();
            await WaitUntilAsync(() => !window.IsVisible);
        }
    }

    [AvaloniaTest]
    public async Task WorkflowStagesSelectedPathPrivatelyAndPublishesOnlyBackendVerifiedIdentity()
    {
        TaskCompletionSource<IPreparedReleasePackage> prepared = NewCompletion<IPreparedReleasePackage>();
        TrackingPreparedPackage retained = new(CreateProcPackage());
        WorkflowHarness harness = this.CreateWorkflow(
            pickLocalReleaseFolder: _ => Task.FromResult<string?>(SelectedPath),
            prepare: (_, _) => prepared.Task
        );
        ReleaseVerificationWindow window = harness.Workflow.CreateInitialWindow();
        try
        {
            window.Show();
            ReleaseVerificationViewModel viewModel = (ReleaseVerificationViewModel)window.DataContext!;
            await WaitUntilAsync(() => viewModel.IsLocalPackageActionVisible);

            PressAccessKey(window, PhysicalKey.L);
            await harness.LocalService.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => viewModel.Heading == "Checking the selected local release files…");

            harness.LocalService.Paths.Should().Equal(SelectedPath);
            viewModel.ReleaseDetail.Should().Contain("not verified yet").And.NotContain(SelectedPath);
            viewModel.Message.Should().Contain("not trusted until backend verification succeeds").And.NotContain(SelectedPath);
            harness.Client.OpenedPackage.Should().BeNull();

            prepared.SetResult(retained);
            await WaitUntilAsync(() => viewModel.IsVerifiedVisible);

            retained.DisposeCalls.Should().Be(1, "the private file projection is released before success is published");
            harness.Client.OpenedPackage.Should().Be(CreateProcPackage());
            harness.Client.OpenedPackage!.ToString().Should().NotContain(SelectedPath);
            viewModel.ReleaseDetail.Should().Contain("Local package").And.NotContain(SelectedPath);
            viewModel.VerifiedIdentityDetail.Should().Contain("Verified package source: Local folder");
            viewModel.VerifiedIdentityDetail.Should().Contain("Verified tag:").And.NotContain(SelectedPath);
            viewModel.IsLocalPackageActionVisible.Should().BeFalse();
        }
        finally
        {
            window.Close();
            await WaitUntilAsync(() => !window.IsVisible);
        }
    }

    [AvaloniaTest]
    public async Task RepeatedLocalActionWhilePickerIsOpenStartsOnlyOnePicker()
    {
        TaskCompletionSource<string?> picker = NewCompletion<string?>();
        WorkflowHarness harness = this.CreateWorkflow(pickLocalReleaseFolder: _ => picker.Task);
        ReleaseVerificationWindow window = harness.Workflow.CreateInitialWindow();
        try
        {
            window.Show();
            ReleaseVerificationViewModel viewModel = (ReleaseVerificationViewModel)window.DataContext!;
            await WaitUntilAsync(() => viewModel.IsLocalPackageActionVisible);

            viewModel.UseLocalPackageCommand.Execute(null);
            viewModel.UseLocalPackageCommand.Execute(null);
            await WaitUntilAsync(() => harness.PickerCalls == 1);

            viewModel.UseLocalPackageCommand.CanExecute(null).Should().BeFalse();
            harness.PickerCalls.Should().Be(1);
            harness.LocalService.Calls.Should().Be(0);

            picker.SetResult(null);
            await WaitUntilAsync(() => viewModel.UseLocalPackageCommand.CanExecute(null));
            window.FindControl<Button>("LocalPackageButton")!.IsFocused.Should().BeTrue();
        }
        finally
        {
            window.Close();
            await WaitUntilAsync(() => !window.IsVisible);
        }
    }

    [AvaloniaTest]
    public async Task PickerResultAfterReleaseWindowDisposalCannotStartImportOrBackend()
    {
        TaskCompletionSource<string?> picker = NewCompletion<string?>();
        WorkflowHarness harness = this.CreateWorkflow(pickLocalReleaseFolder: _ => picker.Task);
        ReleaseVerificationWindow window = harness.Workflow.CreateInitialWindow();
        window.Show();
        ReleaseVerificationViewModel viewModel = (ReleaseVerificationViewModel)window.DataContext!;
        await WaitUntilAsync(() => viewModel.IsLocalPackageActionVisible);

        viewModel.UseLocalPackageCommand.Execute(null);
        await WaitUntilAsync(() => harness.PickerCalls == 1);
        await window.DisposeAsync();
        picker.SetResult(SelectedPath);
        await DrainUiAsync();

        harness.LocalService.Calls.Should().Be(0);
        harness.ClientFactoryCalls.Should().Be(0);
        viewModel.IsVerifiedVisible.Should().BeFalse();
        viewModel.LiveAnnouncement.Should().NotContain(SelectedPath);

        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
    }

    private WorkflowHarness CreateWorkflow(
        Func<ReleaseVerificationWindow, Task<string?>> pickLocalReleaseFolder,
        Func<string, CancellationToken, Task<IPreparedReleasePackage>>? prepare = null
    )
    {
        RecordingLocalService local = new(prepare);
        RecordingClient client = new(ReleaseVerificationViewModelTests.Candidate());
        int clientFactoryCalls = 0;
        int pickerCalls = 0;
        ProductionInstallerWorkflow workflow = ProductionInstallerWorkflow.CreateWithoutDiagnosticsForTesting(
            new CatalogService([]),
            () =>
            {
                clientFactoryCalls++;
                return client;
            },
            _ => throw new AssertionException("Local package selection must not activate the game window before Continue."),
            localReleaseService: local,
            pickLocalReleaseFolder: owner =>
            {
                pickerCalls++;
                return pickLocalReleaseFolder(owner);
            }
        );
        return new WorkflowHarness(workflow, local, client, () => pickerCalls, () => clientFactoryCalls);
    }

    private static InstallerPackageOpenInput CreateProcPackage()
    {
        return new(
            "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2",
            new string('a', 40),
            "/proc/123/fd/17/package.zip",
            "/proc/123/fd/17/SHA256SUMS",
            "/proc/123/fd/17/build-metadata.json",
            "/proc/123/fd/17/install-manifest.json",
            "/proc/123/fd/17/attestation.jsonl",
            "/proc/123/fd/17/attestation.sha256",
            new ProtocolProcWorkspaceIdentity(1, 2, 3, 4, 5)
        );
    }

    private static void PressAccessKey(ReleaseVerificationWindow window, PhysicalKey key)
    {
        window.KeyPressQwerty(PhysicalKey.AltLeft, RawInputModifiers.None);
        window.KeyPressQwerty(key, RawInputModifiers.Alt);
        window.KeyReleaseQwerty(key, RawInputModifiers.Alt);
        window.KeyReleaseQwerty(PhysicalKey.AltLeft, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The expected local-package GUI state was not reached.");
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task DrainUiAsync()
    {
        for (int index = 0; index < 4; index++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static TaskCompletionSource<T> NewCompletion<T>()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class WorkflowHarness(
        ProductionInstallerWorkflow workflow,
        RecordingLocalService localService,
        RecordingClient client,
        Func<int> pickerCalls,
        Func<int> clientFactoryCalls
    )
    {
        public ProductionInstallerWorkflow Workflow { get; } = workflow;
        public RecordingLocalService LocalService { get; } = localService;
        public RecordingClient Client { get; } = client;
        public int PickerCalls => pickerCalls();
        public int ClientFactoryCalls => clientFactoryCalls();
    }

    private sealed class CatalogService(IReadOnlyList<ReviewedReleaseCandidate> releases) : IReviewedReleaseService
    {
        public Task<IReadOnlyList<ReviewedReleaseCandidate>> LoadCatalogAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(releases);
        }

        public Task<IPreparedReleasePackage> PrepareAsync(
            ReviewedReleaseCandidate candidate,
            IProgress<ReviewedReleasePreparationProgress>? progress = null,
            CancellationToken cancellationToken = default
        ) => throw new AssertionException("The local-package picker must not prepare a reviewed public download.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingLocalService(
        Func<string, CancellationToken, Task<IPreparedReleasePackage>>? prepare = null
    ) : ILocalReleasePackageService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Paths { get; } = [];
        public int Calls => this.Paths.Count;

        public Task<IPreparedReleasePackage> PrepareAsync(
            string selectedDirectory,
            CancellationToken cancellationToken = default
        )
        {
            this.Paths.Add(selectedDirectory);
            this.Started.TrySetResult();
            return prepare?.Invoke(selectedDirectory, cancellationToken)
                ?? Task.FromResult<IPreparedReleasePackage>(new TrackingPreparedPackage(CreateProcPackage()));
        }
    }

    private sealed class TrackingPreparedPackage(InstallerPackageOpenInput package) : IPreparedReleasePackage
    {
        public InstallerPackageOpenInput Package { get; } = package;
        public int DisposeCalls { get; private set; }

        public ValueTask DisposeAsync()
        {
            this.DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingClient(ReviewedReleaseCandidate candidate) : IInstallerProtocolClient
    {
        private readonly TaskCompletionSource<InstallerProtocolClientException> Fault = NewCompletion<InstallerProtocolClientException>();
        public InstallerPackageOpenInput? OpenedPackage { get; private set; }
        public int DisposeCalls { get; private set; }
        public Task<InstallerProtocolClientException> SessionFaulted => this.Fault.Task;

        public Task<HandshakeEvent> HandshakeAsync(
            string clientName,
            string clientVersion,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HandshakeEvent(
                ProtocolSessionId.Parse("11111111111111111111111111111111"),
                "1",
                ["verified-local-package"]
            ));
        }

        public Task<InstallerPackageOpenResult> OpenPackageAsync(
            InstallerPackageOpenInput package,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.OpenedPackage = package;
            return Task.FromResult<InstallerPackageOpenResult>(new InstallerPackageOpenSuccess(new ProtocolReleaseIdentity(
                ForkReleaseIdentity.Repository,
                candidate.Identity.Tag,
                candidate.Identity.EmbeddedVersion,
                candidate.Identity.PackageAssetName,
                new string('a', 40),
                new string('b', 40),
                new string('c', 64),
                100,
                "workflow",
                "Release",
                "linux-x64"
            )));
        }

        public Task<IReadOnlyList<ProtocolGameCandidate>> DiscoverGamesAsync(CancellationToken cancellationToken = default)
            => throw new AssertionException("Local package verification must not discover games before Continue.");

        public Task<ProtocolGameCandidate> ValidateGameAsync(
            string canonicalPath,
            CancellationToken cancellationToken = default
        ) => throw new AssertionException("Local package verification must not validate games before Continue.");

        public Task<InstallerReadOnlyPlanResult> InspectPlanAsync(
            string canonicalGamePath,
            InstallerOperation operation,
            CancellationToken cancellationToken = default
        ) => throw new AssertionException("Local package verification must not inspect a plan.");

        public ValueTask DisposeAsync()
        {
            this.DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }
}
