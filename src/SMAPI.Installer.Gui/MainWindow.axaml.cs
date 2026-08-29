using Avalonia.Controls;
using StardewModdingAPI.Installer.Gui.Frontend;
using StardewModdingAPI.Installer.Gui.ViewModels;

namespace StardewModdingAPI.Installer.Gui;

public sealed partial class MainWindow : Window
{
    public MainWindow()
        : this(new MainWindowViewModel(new DemoInstallerFrontendSession()))
    {
    }

    public MainWindow(MainWindowViewModel viewModel)
    {
        this.InitializeComponent();
        this.DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }
}
