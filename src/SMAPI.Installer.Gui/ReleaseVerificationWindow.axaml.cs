using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using StardewModdingAPI.Installer.Gui.Diagnostics;
using StardewModdingAPI.Installer.Gui.ViewModels;

namespace StardewModdingAPI.Installer.Gui;

internal sealed partial class ReleaseVerificationWindow : Window, IAsyncDisposable
{
    private readonly object DisposeLock = new();
    private readonly ReleaseVerificationViewModel ViewModel;
    private Task? DisposeTask;
    private bool CloseApproved;
    private bool CloseStarted;

    internal ReleaseVerificationWindow(ReleaseVerificationViewModel viewModel, InstallerDiagnosticSession? diagnosticSession = null)
    {
        this.InitializeComponent();
        this.ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.DiagnosticsAccess.Attach(diagnosticSession);
        this.DataContext = viewModel;
        this.Opened += this.OnOpened;
        this.Closing += this.OnClosing;
        this.Closed += this.OnClosed;
        this.KeyDown += this.OnKeyDown;
        this.SizeChanged += (_, eventArgs) => this.ApplyResponsiveLayout(eventArgs.NewSize.Width);
        this.ViewModel.FocusRequested += this.OnFocusRequested;
        this.ApplyResponsiveLayout(this.Width);
    }

    internal bool IsNarrowLayout { get; private set; }

    public event EventHandler? LocalPackageFolderRequested
    {
        add => this.ViewModel.LocalPackageFolderRequested += value;
        remove => this.ViewModel.LocalPackageFolderRequested -= value;
    }

    public Task ApplyLocalPackageFolderAsync(string? path)
    {
        return this.ViewModel.ApplyLocalPackageFolderAsync(path);
    }

    internal void ApplyResponsiveLayout(double viewportWidth)
    {
        this.IsNarrowLayout = viewportWidth < 620;
        this.PageGrid.Margin = this.IsNarrowLayout ? new Avalonia.Thickness(14) : new Avalonia.Thickness(28);
    }

    /// <summary>Dispose the production view model, controller, backend session, and release service exactly once.</summary>
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
    }

    private void OnExitClick(object? sender, RoutedEventArgs e)
    {
        this.Close();
        e.Handled = true;
    }

    private void OnFocusRequested(object? sender, ReleaseVerificationFocusTarget target)
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                Control control = target switch
                {
                    ReleaseVerificationFocusTarget.ReleaseSelector => this.ReleaseSelector,
                    ReleaseVerificationFocusTarget.LocalPackage => this.LocalPackageButton,
                    ReleaseVerificationFocusTarget.Retry => this.RetryButton,
                    ReleaseVerificationFocusTarget.Continue => this.ContinueButton,
                    _ when this.ViewModel.IsErrorVisible => this.ErrorRegion,
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
        await this.ViewModel.DisposeAsync();
    }
}
