using Avalonia.Controls;
using Avalonia.Platform.Storage;
using StardewModdingAPI.Installer.Gui.Backend;
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
    private ReleaseVerificationController? ReleaseController;
    private ReleaseVerificationViewModel? ReleaseViewModel;
    private ReleaseVerificationWindow? ReleaseWindow;
    private int TransitionStarted;
    private int PickerActive;

    public ProductionInstallerWorkflow(
        IReviewedReleaseService releaseService,
        Func<IInstallerProtocolClient> clientFactory,
        Action<Window> activateNextWindow,
        Func<GameDiscoveryViewModel, GameDiscoveryWindow>? discoveryWindowFactory = null,
        Func<GameDiscoveryWindow, Task<string?>>? pickFolder = null
    )
    {
        this.ReleaseService = releaseService ?? throw new ArgumentNullException(nameof(releaseService));
        this.ClientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        this.ActivateNextWindow = activateNextWindow ?? throw new ArgumentNullException(nameof(activateNextWindow));
        this.DiscoveryWindowFactory = discoveryWindowFactory ?? (viewModel => new(viewModel));
        this.PickFolder = pickFolder ?? PickFolderAsync;
    }

    /// <summary>Create the initial window exactly once without starting network or backend work.</summary>
    public ReleaseVerificationWindow CreateInitialWindow()
    {
        if (this.ReleaseWindow is not null)
            throw new InvalidOperationException("The production installer workflow already created its initial window.");

        this.ReleaseController = new(this.ReleaseService, this.ClientFactory);
        this.ReleaseViewModel = new(this.ReleaseController);
        this.ReleaseWindow = new(this.ReleaseViewModel);
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
            session = null;
            viewModel = new(controller);
            controller = null;
            window = this.DiscoveryWindowFactory(viewModel)
                ?? throw new InvalidOperationException("The game-discovery window factory returned null.");
            viewModel = null;
            GameDiscoveryWindow transitionedWindow = window;
            transitionedWindow.FolderPickerRequested += (_, _) => this.OnFolderPickerRequested(transitionedWindow);

            this.ActivateNextWindow(transitionedWindow);
            this.ReleaseViewModel!.ContinueRequested -= this.OnContinueRequested;
            this.ReleaseWindow!.Close();
            window = null;
        }
        catch
        {
            try
            {
                if (window is not null)
                    await window.DisposeAsync().ConfigureAwait(true);
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
}
