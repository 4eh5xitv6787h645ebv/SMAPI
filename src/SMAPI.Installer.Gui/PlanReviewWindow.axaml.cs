using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using StardewModdingAPI.Installer.Gui.ViewModels;

namespace StardewModdingAPI.Installer.Gui;

/// <summary>A read-only plan-review window with no approval, confirmation, or execution controls.</summary>
internal sealed partial class PlanReviewWindow : Window, IAsyncDisposable
{
    private readonly object DisposeLock = new();
    private readonly PlanReviewViewModel ViewModel;
    private Task? DisposeTask;
    private bool CloseApproved;
    private bool CloseStarted;

    public PlanReviewWindow(PlanReviewViewModel viewModel)
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

    internal bool IsNarrowLayout { get; private set; }

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

    /// <summary>Close a window which may have become visible before activation reported failure.</summary>
    internal async Task CloseAfterFailedActivationAsync()
    {
        await this.DisposeAsync();
        if (!this.IsVisible)
            return;
        this.CloseApproved = true;
        this.Close();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(
            () => this.OperationList.Focus(NavigationMethod.Tab),
            DispatcherPriority.Input
        );
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

    private void OnFocusRequested(object? sender, PlanReviewFocusTarget target)
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                Control control = target switch
                {
                    PlanReviewFocusTarget.OperationList => this.OperationList,
                    PlanReviewFocusTarget.Result => this.ResultSummaryRegion,
                    PlanReviewFocusTarget.Error => this.ErrorRegion,
                    PlanReviewFocusTarget.Retry => this.RetryButton,
                    PlanReviewFocusTarget.Exit => this.ExitButton,
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
