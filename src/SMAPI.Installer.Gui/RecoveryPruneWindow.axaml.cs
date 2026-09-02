using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using StardewModdingAPI.Installer.Gui.ViewModels;

namespace StardewModdingAPI.Installer.Gui;

/// <summary>The explicit review, confirmation, and execution surface for bounded recovery-history cleanup.</summary>
internal sealed partial class RecoveryPruneWindow : Window, IAsyncDisposable
{
    private readonly object DisposeLock = new();
    private readonly RecoveryPruneViewModel ViewModel;
    private Task? DisposeTask;
    private bool CloseApproved;
    private bool CloseCheckActive;

    public RecoveryPruneWindow(RecoveryPruneViewModel viewModel)
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
            return new ValueTask(this.DisposeTask ??= this.DisposeCoreAsync());
    }

    internal async Task CloseAfterFailedActivationAsync()
    {
        await this.DisposeAsync();
        if (!this.IsVisible)
            return;
        this.CloseApproved = true;
        this.Close();
    }

    private void OnOpened(object? sender, EventArgs e)
        => this.OnFocusRequested(this, this.ViewModel.InitialFocusTarget);

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (this.CloseApproved)
            return;
        e.Cancel = true;
        if (this.CloseCheckActive)
            return;
        this.CloseCheckActive = true;
        try
        {
            if (!await this.ViewModel.PrepareToCloseAsync())
                return;
            await this.DisposeAsync();
            this.CloseApproved = true;
            this.Close();
        }
        finally
        {
            this.CloseCheckActive = false;
        }
    }

    private async void OnClosed(object? sender, EventArgs e)
        => await this.DisposeAsync();

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        e.Handled = true;
        if (await this.ViewModel.PrepareToCloseAsync())
            this.Close();
    }

    private void OnCloseRequested(object? sender, EventArgs e) => this.Close();

    private void OnFocusRequested(object? sender, RecoveryPruneFocusTarget target)
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                Control control = target switch
                {
                    RecoveryPruneFocusTarget.Load => this.LoadButton,
                    RecoveryPruneFocusTarget.HistoryList => this.HistoryList,
                    RecoveryPruneFocusTarget.Inspect => this.InspectButton,
                    RecoveryPruneFocusTarget.Review => this.ReviewRegion,
                    RecoveryPruneFocusTarget.Consent => this.ConsentCheckBox,
                    RecoveryPruneFocusTarget.Cancel => this.CancelButton,
                    RecoveryPruneFocusTarget.Confirm => this.ConfirmButton,
                    RecoveryPruneFocusTarget.Run => this.RunButton,
                    RecoveryPruneFocusTarget.Result => this.ResultRegion,
                    RecoveryPruneFocusTarget.Error => this.ErrorRegion,
                    RecoveryPruneFocusTarget.Exit => this.ExitButton,
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
