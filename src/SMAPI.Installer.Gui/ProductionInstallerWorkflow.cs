using Avalonia.Controls;
using Avalonia.Platform.Storage;
using StardewModdingAPI.Installer.Gui.Backend;
using StardewModdingAPI.Installer.Gui.Diagnostics;
using StardewModdingAPI.Installer.Gui.Frontend;
using StardewModdingAPI.Installer.Gui.ViewModels;

namespace StardewModdingAPI.Installer.Gui;

/// <summary>Moves the single reviewed backend authority through production installer windows.</summary>
internal sealed class ProductionInstallerWorkflow
{
    private readonly IReviewedReleaseService ReleaseService;
    private readonly Func<IInstallerProtocolClient> ClientFactory;
    private readonly Action<Window> ActivateNextWindow;
    private readonly Func<GameDiscoveryViewModel, GameDiscoveryWindow> DiscoveryWindowFactory;
    private readonly Func<GameDiscoveryWindow, Task<string?>> PickFolder;
    private readonly Func<PlanReviewViewModel, PlanReviewWindow> PlanWindowFactory;
    private readonly Func<ExecutionViewModel, ExecutionWindow> ExecutionWindowFactory;
    private readonly Func<RecoveryPruneViewModel, RecoveryPruneWindow> RecoveryPruneWindowFactory;
    private readonly InstallerDiagnosticSession? DiagnosticSession;
    private readonly ProductionInstallerDiagnosticObserver? DiagnosticObserver;
    private ReleaseVerificationController? ReleaseController;
    private ReleaseVerificationViewModel? ReleaseViewModel;
    private ReleaseVerificationWindow? ReleaseWindow;
    private GameDiscoveryController? DiscoveryController;
    private GameDiscoveryViewModel? DiscoveryViewModel;
    private GameDiscoveryWindow? DiscoveryWindow;
    private PlanReviewController? PlanController;
    private PlanReviewViewModel? PlanViewModel;
    private PlanReviewWindow? PlanWindow;
    private ExecutionController? ExecutionController;
    private RecoveryPruneController? RecoveryPruneController;
    private int TransitionStarted;
    private int SelectedGameTransitionStarted;
    private int PickerActive;
    private int ExecutionTransitionStarted;

    public ProductionInstallerWorkflow(
        IReviewedReleaseService releaseService,
        Func<IInstallerProtocolClient> clientFactory,
        Action<Window> activateNextWindow,
        InstallerDiagnosticSession diagnosticSession,
        Func<GameDiscoveryViewModel, GameDiscoveryWindow>? discoveryWindowFactory = null,
        Func<GameDiscoveryWindow, Task<string?>>? pickFolder = null,
        Func<PlanReviewViewModel, PlanReviewWindow>? planWindowFactory = null,
        Func<ExecutionViewModel, ExecutionWindow>? executionWindowFactory = null,
        Func<RecoveryPruneViewModel, RecoveryPruneWindow>? recoveryPruneWindowFactory = null
    )
        : this(
            releaseService,
            clientFactory,
            activateNextWindow,
            discoveryWindowFactory,
            pickFolder,
            planWindowFactory,
            executionWindowFactory,
            recoveryPruneWindowFactory,
            diagnosticSession ?? throw new ArgumentNullException(nameof(diagnosticSession)),
            allowMissingDiagnostics: false
        )
    {
    }

    /// <summary>Create the diagnostics-free workflow seam used only by deterministic controller/window tests.</summary>
    internal static ProductionInstallerWorkflow CreateWithoutDiagnosticsForTesting(
        IReviewedReleaseService releaseService,
        Func<IInstallerProtocolClient> clientFactory,
        Action<Window> activateNextWindow,
        Func<GameDiscoveryViewModel, GameDiscoveryWindow>? discoveryWindowFactory = null,
        Func<GameDiscoveryWindow, Task<string?>>? pickFolder = null,
        Func<PlanReviewViewModel, PlanReviewWindow>? planWindowFactory = null,
        Func<ExecutionViewModel, ExecutionWindow>? executionWindowFactory = null,
        Func<RecoveryPruneViewModel, RecoveryPruneWindow>? recoveryPruneWindowFactory = null
    ) => new(
        releaseService,
        clientFactory,
        activateNextWindow,
        discoveryWindowFactory,
        pickFolder,
        planWindowFactory,
        executionWindowFactory,
        recoveryPruneWindowFactory,
        diagnosticSession: null,
        allowMissingDiagnostics: true
    );

    private ProductionInstallerWorkflow(
        IReviewedReleaseService releaseService,
        Func<IInstallerProtocolClient> clientFactory,
        Action<Window> activateNextWindow,
        Func<GameDiscoveryViewModel, GameDiscoveryWindow>? discoveryWindowFactory,
        Func<GameDiscoveryWindow, Task<string?>>? pickFolder,
        Func<PlanReviewViewModel, PlanReviewWindow>? planWindowFactory,
        Func<ExecutionViewModel, ExecutionWindow>? executionWindowFactory,
        Func<RecoveryPruneViewModel, RecoveryPruneWindow>? recoveryPruneWindowFactory,
        InstallerDiagnosticSession? diagnosticSession,
        bool allowMissingDiagnostics
    )
    {
        if (!allowMissingDiagnostics && diagnosticSession is null)
            throw new ArgumentNullException(nameof(diagnosticSession));
        this.ReleaseService = releaseService ?? throw new ArgumentNullException(nameof(releaseService));
        this.ClientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        this.ActivateNextWindow = activateNextWindow ?? throw new ArgumentNullException(nameof(activateNextWindow));
        this.DiscoveryWindowFactory = discoveryWindowFactory ?? (viewModel => new(viewModel, diagnosticSession));
        this.PickFolder = pickFolder ?? PickFolderAsync;
        this.PlanWindowFactory = planWindowFactory ?? (viewModel => new(viewModel, diagnosticSession));
        this.ExecutionWindowFactory = executionWindowFactory ?? (viewModel => new(viewModel, diagnosticSession));
        this.RecoveryPruneWindowFactory = recoveryPruneWindowFactory ?? (viewModel => new(viewModel, diagnosticSession));
        this.DiagnosticSession = diagnosticSession;
        this.DiagnosticObserver = diagnosticSession is null ? null : new(diagnosticSession);
    }

    /// <summary>Create the initial window exactly once without starting network or backend work.</summary>
    public ReleaseVerificationWindow CreateInitialWindow()
    {
        if (this.ReleaseWindow is not null)
            throw new InvalidOperationException("The production installer workflow already created its initial window.");

        this.ReleaseController = new(this.ReleaseService, this.ClientFactory);
        this.ObserveReleaseController(this.ReleaseController);
        this.ReleaseController.Changed += this.OnReleaseDiagnosticChanged;
        this.ReleaseViewModel = new(this.ReleaseController);
        this.ReleaseWindow = new(this.ReleaseViewModel, this.DiagnosticSession);
        this.ReleaseViewModel.ContinueRequested += this.OnContinueRequested;
        return this.ReleaseWindow;
    }

    private async void OnContinueRequested(object? sender, EventArgs eventArgs)
    {
        if (Interlocked.Exchange(ref this.TransitionStarted, 1) != 0)
            return;

        IVerifiedInstallerSession? session = null;
        GameDiscoveryController? controller = null;
        GameDiscoveryViewModel? viewModel = null;
        GameDiscoveryWindow? window = null;
        try
        {
            session = this.ReleaseController!.TakeVerifiedSession();
            controller = new(session);
            this.ObserveGameController(controller);
            controller.Changed += this.OnGameDiagnosticChanged;
            session = null;
            viewModel = new(controller);
            window = this.DiscoveryWindowFactory(viewModel)
                ?? throw new InvalidOperationException("The game-discovery window factory returned null.");
            GameDiscoveryWindow transitionedWindow = window;
            transitionedWindow.FolderPickerRequested += (_, _) => this.OnFolderPickerRequested(transitionedWindow);
            viewModel.ContinueRequested += this.OnPlanContinueRequested;
            viewModel.RecoveryCleanupRequested += this.OnRecoveryCleanupRequested;

            this.DiscoveryController = controller;
            this.DiscoveryViewModel = viewModel;
            this.DiscoveryWindow = window;

            this.ActivateNextWindow(transitionedWindow);
            this.ReleaseViewModel!.ContinueRequested -= this.OnContinueRequested;
            this.ReleaseController!.Changed -= this.OnReleaseDiagnosticChanged;
            this.ReleaseWindow!.Close();
            controller = null;
            viewModel = null;
            window = null;
        }
        catch
        {
            if (controller is not null)
                controller.Changed -= this.OnGameDiagnosticChanged;
            this.DiscoveryController = null;
            this.DiscoveryViewModel = null;
            this.DiscoveryWindow = null;
            try
            {
                if (window is not null)
                    await window.CloseAfterFailedActivationAsync().ConfigureAwait(true);
                else if (viewModel is not null)
                    await viewModel.DisposeAsync().ConfigureAwait(true);
                else if (controller is not null)
                    await controller.DisposeAsync().ConfigureAwait(true);
                else if (session is not null)
                    await session.DisposeAsync().ConfigureAwait(true);
            }
            catch
            {
                // The transferred authority remains unusable; only sanitized failure state reaches the UI.
            }
            this.ReleaseViewModel?.ReportTransitionFailure();
        }
    }

    private async void OnPlanContinueRequested(object? sender, EventArgs eventArgs)
    {
        if (Interlocked.Exchange(ref this.SelectedGameTransitionStarted, 1) != 0)
            return;

        IPlanInspectionSession? session = null;
        PlanReviewController? controller = null;
        PlanReviewViewModel? viewModel = null;
        PlanReviewWindow? window = null;
        try
        {
            session = this.DiscoveryController!.TakeSelectedGameSession();
            controller = new(session);
            this.ObservePlanController(controller);
            controller.Changed += this.OnPlanDiagnosticChanged;
            session = null;
            viewModel = new(controller);
            viewModel.ConfirmationReady += this.OnConfirmationReady;
            window = this.PlanWindowFactory(viewModel)
                ?? throw new InvalidOperationException("The plan-review window factory returned null.");

            this.PlanController = controller;
            this.PlanViewModel = viewModel;
            this.PlanWindow = window;

            this.ActivateNextWindow(window);
            this.DiscoveryViewModel!.ContinueRequested -= this.OnPlanContinueRequested;
            this.DiscoveryViewModel.RecoveryCleanupRequested -= this.OnRecoveryCleanupRequested;
            this.DiscoveryController!.Changed -= this.OnGameDiagnosticChanged;
            this.DiscoveryWindow!.Close();
            this.DiscoveryController = null;
            this.DiscoveryViewModel = null;
            this.DiscoveryWindow = null;
            controller = null;
            viewModel = null;
            window = null;
        }
        catch
        {
            if (controller is not null)
                controller.Changed -= this.OnPlanDiagnosticChanged;
            if (viewModel is not null)
                viewModel.ConfirmationReady -= this.OnConfirmationReady;
            this.PlanController = null;
            this.PlanViewModel = null;
            this.PlanWindow = null;
            try
            {
                if (window is not null)
                    await window.CloseAfterFailedActivationAsync().ConfigureAwait(true);
                else if (viewModel is not null)
                    await viewModel.DisposeAsync().ConfigureAwait(true);
                else if (controller is not null)
                    await controller.DisposeAsync().ConfigureAwait(true);
                else if (session is not null)
                    await session.DisposeAsync().ConfigureAwait(true);
            }
            catch
            {
                // The transferred authority remains unusable; only sanitized failure state reaches the UI.
            }
            this.DiscoveryViewModel?.ReportTransitionFailure(GameDiscoveryTransitionDestination.PlanReview);
        }
    }

    private async void OnRecoveryCleanupRequested(object? sender, EventArgs eventArgs)
    {
        if (Interlocked.Exchange(ref this.SelectedGameTransitionStarted, 1) != 0)
            return;

        IPlanInspectionSession? session = null;
        RecoveryPruneController? controller = null;
        RecoveryPruneViewModel? viewModel = null;
        RecoveryPruneWindow? window = null;
        try
        {
            session = this.DiscoveryController!.TakeSelectedGameSession();
            controller = new(session);
            this.ObserveRecoveryPruneController(controller);
            controller.Changed += this.OnRecoveryPruneDiagnosticChanged;
            this.RecoveryPruneController = controller;
            session = null;
            viewModel = new(
                controller,
                ensureDiagnosticLoggingReady: this.DiagnosticSession is null ? null : this.DiagnosticSession.EnsureReadyForMutation
            );
            controller = null;
            window = this.RecoveryPruneWindowFactory(viewModel)
                ?? throw new InvalidOperationException("The recovery-history window factory returned null.");
            viewModel = null;

            this.ActivateNextWindow(window);
            this.DiscoveryViewModel!.ContinueRequested -= this.OnPlanContinueRequested;
            this.DiscoveryViewModel.RecoveryCleanupRequested -= this.OnRecoveryCleanupRequested;
            this.DiscoveryController!.Changed -= this.OnGameDiagnosticChanged;
            this.DiscoveryWindow!.Close();
            this.DiscoveryController = null;
            this.DiscoveryViewModel = null;
            this.DiscoveryWindow = null;
            window = null;
        }
        catch
        {
            if (this.RecoveryPruneController is { } failedController)
            {
                failedController.Changed -= this.OnRecoveryPruneDiagnosticChanged;
                this.RecoveryPruneController = null;
            }
            try
            {
                if (window is not null)
                    await window.CloseAfterFailedActivationAsync().ConfigureAwait(true);
                else if (viewModel is not null)
                    await viewModel.DisposeAsync().ConfigureAwait(true);
                else if (controller is not null)
                    await controller.DisposeAsync().ConfigureAwait(true);
                else if (session is not null)
                    await session.DisposeAsync().ConfigureAwait(true);
            }
            catch
            {
                // The transferred authority remains unusable; only sanitized failure state reaches the UI.
            }
            this.DiscoveryViewModel?.ReportTransitionFailure(GameDiscoveryTransitionDestination.RecoveryCleanup);
        }
    }

    private async void OnConfirmationReady(object? sender, EventArgs eventArgs)
    {
        if (Interlocked.Exchange(ref this.ExecutionTransitionStarted, 1) != 0)
            return;

        ConfirmedPlanHandoff? handoff = null;
        ExecutionController? controller = null;
        ExecutionViewModel? viewModel = null;
        ExecutionWindow? window = null;
        try
        {
            handoff = this.PlanController!.TakeConfirmedHandoff();
            controller = new(handoff.Session, handoff.Presentation);
            this.ObserveExecutionController(controller);
            controller.Changed += this.OnExecutionDiagnosticChanged;
            this.ExecutionController = controller;
            handoff = null;
            viewModel = new(
                controller,
                ensureDiagnosticLoggingReady: this.DiagnosticSession is null ? null : this.DiagnosticSession.EnsureReadyForMutation
            );
            controller = null;
            window = this.ExecutionWindowFactory(viewModel)
                ?? throw new InvalidOperationException("The execution window factory returned null.");
            viewModel = null;

            this.ActivateNextWindow(window);
            this.PlanViewModel!.ConfirmationReady -= this.OnConfirmationReady;
            this.PlanController!.Changed -= this.OnPlanDiagnosticChanged;
            this.PlanWindow!.Close();
            this.PlanController = null;
            this.PlanViewModel = null;
            this.PlanWindow = null;
            window = null;
        }
        catch
        {
            if (this.ExecutionController is { } failedController)
            {
                failedController.Changed -= this.OnExecutionDiagnosticChanged;
                this.ExecutionController = null;
            }
            try
            {
                if (window is not null)
                    await window.CloseAfterFailedActivationAsync().ConfigureAwait(true);
                else if (viewModel is not null)
                    await viewModel.DisposeAsync().ConfigureAwait(true);
                else if (controller is not null)
                    await controller.DisposeAsync().ConfigureAwait(true);
                else if (handoff is not null)
                    await handoff.Session.DisposeAsync().ConfigureAwait(true);
            }
            catch
            {
                // The one-shot confirmed authority remains unusable and no backend detail reaches presentation.
            }
            this.PlanViewModel?.ReportExecutionTransitionFailure();
        }
    }

    private async void OnFolderPickerRequested(GameDiscoveryWindow window)
    {
        if (Interlocked.Exchange(ref this.PickerActive, 1) != 0)
            return;
        try
        {
            string? selected;
            try
            {
                selected = await this.PickFolder(window).ConfigureAwait(true);
            }
            catch
            {
                if (window.DataContext is GameDiscoveryViewModel pickerViewModel)
                    pickerViewModel.ReportFolderPickerFailure();
                return;
            }

            try
            {
                await window.ApplyManualFolderAsync(selected).ConfigureAwait(true);
            }
            catch
            {
                if (window.DataContext is GameDiscoveryViewModel validationViewModel)
                    validationViewModel.ReportFolderValidationFailure();
            }
        }
        finally
        {
            Volatile.Write(ref this.PickerActive, 0);
        }
    }

    private static async Task<string?> PickFolderAsync(GameDiscoveryWindow owner)
    {
        IReadOnlyList<IStorageFolder> selected = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Choose the Stardew Valley game folder"
        });
        if (selected.Count == 0)
            return null;
        if (selected.Count != 1)
            throw new InvalidOperationException("The desktop folder picker returned an unexpected selection count.");
        return selected[0].TryGetLocalPath()
            ?? throw new InvalidOperationException("The desktop folder picker did not return a local folder.");
    }

    private void OnReleaseDiagnosticChanged(object? sender, EventArgs eventArgs)
    {
        if (this.ReleaseController is { } controller)
            this.ObserveReleaseController(controller);
    }

    private void OnGameDiagnosticChanged(object? sender, EventArgs eventArgs)
    {
        if (this.DiscoveryController is { } controller)
            this.ObserveGameController(controller);
    }

    private void OnPlanDiagnosticChanged(object? sender, EventArgs eventArgs)
    {
        if (this.PlanController is { } controller)
            this.ObservePlanController(controller);
    }

    private void OnExecutionDiagnosticChanged(object? sender, EventArgs eventArgs)
    {
        if (this.ExecutionController is { } controller)
            this.ObserveExecutionController(controller);
    }

    private void OnRecoveryPruneDiagnosticChanged(object? sender, EventArgs eventArgs)
    {
        if (this.RecoveryPruneController is { } controller)
            this.ObserveRecoveryPruneController(controller);
    }

    private void ObserveReleaseController(ReleaseVerificationController controller)
        => this.DiagnosticObserver?.Observe(controller.Snapshot);

    private void ObserveGameController(GameDiscoveryController controller)
        => this.DiagnosticObserver?.Observe(controller.Snapshot);

    private void ObservePlanController(PlanReviewController controller)
        => this.DiagnosticObserver?.Observe(controller.Snapshot);

    private void ObserveExecutionController(ExecutionController controller)
        => this.DiagnosticObserver?.Observe(controller.Snapshot);

    private void ObserveRecoveryPruneController(RecoveryPruneController controller)
        => this.DiagnosticObserver?.Observe(controller.Snapshot);
}
