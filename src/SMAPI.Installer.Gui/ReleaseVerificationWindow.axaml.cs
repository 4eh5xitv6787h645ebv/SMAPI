using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using StardewModdingAPI.Installer.Gui.ViewModels;

namespace StardewModdingAPI.Installer.Gui;

internal sealed partial class ReleaseVerificationWindow : Window
{
    private readonly ReleaseVerificationViewModel ViewModel;
    private bool CloseApproved;
    private bool CloseStarted;

    internal ReleaseVerificationWindow(ReleaseVerificationViewModel viewModel)
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
        this.ApplyResponsiveLayout(this.Width);
    }

    internal bool IsNarrowLayout { get; private set; }

    internal void ApplyResponsiveLayout(double viewportWidth)
    {
        this.IsNarrowLayout = viewportWidth < 620;
        this.PageGrid.Margin = this.IsNarrowLayout ? new Avalonia.Thickness(14) : new Avalonia.Thickness(28);
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
        await this.ViewModel.DisposeAsync();
        this.CloseApproved = true;
        this.Close();
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        this.ViewModel.FocusRequested -= this.OnFocusRequested;
        await this.ViewModel.DisposeAsync();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && this.ViewModel.CancelCommand.CanExecute(null))
        {
            this.ViewModel.CancelCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnFocusRequested(object? sender, ReleaseVerificationFocusTarget target)
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                Control control = target switch
                {
                    ReleaseVerificationFocusTarget.ReleaseSelector => this.ReleaseSelector,
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
}
