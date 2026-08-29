using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using StardewModdingAPI.Installer.Gui.Frontend;
using StardewModdingAPI.Installer.Gui.ViewModels;

namespace StardewModdingAPI.Installer.Gui;

public sealed partial class MainWindow : Window
{
    public MainWindow()
        : this(new MainWindowViewModel(new DemoInstallerFrontendSession()))
    {
    }

    internal MainWindow(MainWindowViewModel viewModel)
    {
        this.InitializeComponent();
        this.DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.SizeChanged += (_, eventArgs) => this.ApplyResponsiveLayout(eventArgs.NewSize.Width);
        this.Opened += (_, _) => Dispatcher.UIThread.Post(
            () => this.FolderSelector.Focus(NavigationMethod.Tab),
            DispatcherPriority.Input
        );
        this.ApplyResponsiveLayout(this.Width);
    }

    internal bool IsNarrowLayout { get; private set; }

    internal void ApplyResponsiveLayout(double viewportWidth)
    {
        const double NarrowThreshold = 850;
        this.IsNarrowLayout = viewportWidth < NarrowThreshold;

        if (this.IsNarrowLayout)
        {
            this.HeaderGrid.ColumnDefinitions = new ColumnDefinitions("*");
            this.HeaderGrid.RowDefinitions = new RowDefinitions("Auto,Auto");
            this.HeaderGrid.ColumnSpacing = 0;
            this.HeaderGrid.RowSpacing = 10;
            Grid.SetColumn(this.HeaderBadge, 0);
            Grid.SetRow(this.HeaderBadge, 1);
            this.HeaderBadge.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;

            this.SelectionReviewGrid.ColumnDefinitions = new ColumnDefinitions("*");
            this.SelectionReviewGrid.RowDefinitions = new RowDefinitions("Auto,Auto");
            this.SelectionReviewGrid.ColumnSpacing = 0;
            this.SelectionReviewGrid.RowSpacing = 18;
            Grid.SetColumn(this.ReviewCard, 0);
            Grid.SetRow(this.ReviewCard, 1);
        }
        else
        {
            this.HeaderGrid.ColumnDefinitions = new ColumnDefinitions("*,Auto");
            this.HeaderGrid.RowDefinitions = new RowDefinitions("Auto");
            this.HeaderGrid.ColumnSpacing = 18;
            this.HeaderGrid.RowSpacing = 0;
            Grid.SetColumn(this.HeaderBadge, 1);
            Grid.SetRow(this.HeaderBadge, 0);
            this.HeaderBadge.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;

            this.SelectionReviewGrid.ColumnDefinitions = new ColumnDefinitions("3*,2*");
            this.SelectionReviewGrid.RowDefinitions = new RowDefinitions("Auto");
            this.SelectionReviewGrid.ColumnSpacing = 18;
            this.SelectionReviewGrid.RowSpacing = 0;
            Grid.SetColumn(this.ReviewCard, 1);
            Grid.SetRow(this.ReviewCard, 0);
        }
    }
}
