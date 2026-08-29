using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace StardewModdingAPI.Installer.Gui;

public sealed partial class App : Application
{
    private readonly GuiLaunchMode LaunchMode;
    private readonly Func<GuiLaunchMode, Action<Window>, Window> CreateMainWindow;

    public App()
        : this(GuiLaunchMode.Demo)
    {
    }

    internal App(GuiLaunchMode launchMode)
        : this(launchMode, (mode, activateNext) => GuiComposition.CreateMainWindow(mode, activateNext))
    {
    }

    internal App(
        GuiLaunchMode launchMode,
        Func<GuiLaunchMode, Window> createMainWindow
    )
        : this(launchMode, (mode, _) => createMainWindow(mode))
    {
    }

    internal App(
        GuiLaunchMode launchMode,
        Func<GuiLaunchMode, Action<Window>, Window> createMainWindow
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
            void ActivateNext(Window next)
            {
                next.Show();
                desktop.MainWindow = next;
            }

            desktop.MainWindow = this.CreateMainWindow(this.LaunchMode, ActivateNext);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
