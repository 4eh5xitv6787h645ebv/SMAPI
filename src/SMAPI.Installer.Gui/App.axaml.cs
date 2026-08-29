using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace StardewModdingAPI.Installer.Gui;

public sealed partial class App : Application
{
    private readonly GuiLaunchMode LaunchMode;
    private readonly Func<GuiLaunchMode, Window> CreateMainWindow;

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
        Func<GuiLaunchMode, Window> createMainWindow
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
            desktop.MainWindow = this.CreateMainWindow(this.LaunchMode);

        base.OnFrameworkInitializationCompleted();
    }
}
