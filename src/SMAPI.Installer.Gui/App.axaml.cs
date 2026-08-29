using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace StardewModdingAPI.Installer.Gui;

public sealed partial class App : Application
{
    private readonly GuiLaunchMode LaunchMode;
    private readonly Func<GuiLaunchMode, GuiMainWindowComposition> CreateMainWindow;
    private GuiMainWindowComposition? MainWindowComposition;

    public App()
        : this(GuiLaunchMode.Demo)
    {
    }

    internal App(GuiLaunchMode launchMode)
        : this(launchMode, GuiComposition.CreateMainWindow)
    {
    }

    internal App(
        GuiLaunchMode launchMode,
        Func<GuiLaunchMode, GuiMainWindowComposition> createMainWindow
    )
    {
        if (!Enum.IsDefined(launchMode))
            throw new ArgumentOutOfRangeException(nameof(launchMode));
        this.LaunchMode = launchMode;
        this.CreateMainWindow = createMainWindow ?? throw new ArgumentNullException(nameof(createMainWindow));
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (this.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            this.MainWindowComposition = this.CreateMainWindow(this.LaunchMode);
            desktop.MainWindow = this.MainWindowComposition.MainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
