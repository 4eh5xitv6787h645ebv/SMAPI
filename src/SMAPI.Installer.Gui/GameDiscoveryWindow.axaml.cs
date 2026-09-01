using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using StardewModdingAPI.Installer.Gui.ViewModels;

namespace StardewModdingAPI.Installer.Gui;

/// <summary>A read-only discovery window. The host owns the native folder-picker implementation through an event seam.</summary>
internal sealed partial class GameDiscoveryWindow : Window, IAsyncDisposable
{
    private readonly object DisposeLock = new();
    private readonly GameDiscoveryViewModel ViewModel;
    private Task? DisposeTask;
    private bool CloseApproved;
    private bool CloseStarted;

    public GameDiscoveryWindow(GameDiscoveryViewModel viewModel)
    {
        this.InitializeComponent();
        this.ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.DataContext = viewModel;
        this.Opened += this.OnOpened;
        this.Closing += this.OnClosing;
        this.Closed += this.OnClosed;
        this.KeyDown += this.OnKeyDown;
        this.SizeChanged += (_, eventArgs) => this.ApplyResponsiveLayout(eventArgs.NewSize.Width);
        this.ViewModel.FocusRequested += this.OnFocusRequested;
        this.ViewModel.CloseRequested += this.OnCloseRequested;
        this.ApplyResponsiveLayout(this.Width);
    }

    public event EventHandler? FolderPickerRequested
    {
        add => this.ViewModel.FolderPickerRequested += value;
        remove => this.ViewModel.FolderPickerRequested -= value;
    }

    internal bool IsNarrowLayout { get; private set; }

    public Task ApplyManualFolderAsync(string? path)
    {
        return this.ViewModel.ApplyManualFolderAsync(path);
    }

    internal void ApplyResponsiveLayout(double viewportWidth)
    {
        this.IsNarrowLayout = viewportWidth < 620;
        this.PageGrid.Margin = this.IsNarrowLayout ? new Avalonia.Thickness(14) : new Avalonia.Thickness(28);
    }

    public ValueTask DisposeAsync()
    {
        lock (this.DisposeLock)
        {
            this.DisposeTask ??= this.DisposeCoreAsync();
            return new ValueTask(this.DisposeTask);
        }
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        this.StatusRegion.Focus(NavigationMethod.Tab);
        await this.ViewModel.StartAsync();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (this.CloseApproved)
            return;
        e.Cancel = true;
        if (this.CloseStarted)
            return;
        this.CloseStarted = true;
        await this.DisposeAsync();
        this.CloseApproved = true;
        this.Close();
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        await this.DisposeAsync();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && this.ViewModel.CancelCommand.CanExecute(null))
        {
            this.ViewModel.CancelCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && this.ViewModel.ExitCommand.CanExecute(null))
        {
            this.ViewModel.ExitCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        this.Close();
    }

    private void OnFocusRequested(object? sender, GameDiscoveryFocusTarget target)
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                Control control = target switch
                {
                    GameDiscoveryFocusTarget.CandidateList => this.CandidateList,
                    GameDiscoveryFocusTarget.Browse => this.BrowseButton,
                    GameDiscoveryFocusTarget.Retry => this.RetryButton,
                    GameDiscoveryFocusTarget.Exit => this.ExitButton,
                    _ when this.ViewModel.IsProblemVisible => this.ProblemRegion,
                    _ => this.StatusRegion
                };
                if (control.IsVisible && control.IsEffectivelyEnabled)
                    control.Focus(NavigationMethod.Tab);
            },
            DispatcherPriority.Input
        );
    }

    private async Task DisposeCoreAsync()
    {
        this.ViewModel.FocusRequested -= this.OnFocusRequested;
        this.ViewModel.CloseRequested -= this.OnCloseRequested;
        await this.ViewModel.DisposeAsync();
    }
}
