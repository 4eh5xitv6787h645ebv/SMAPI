using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace StardewModdingAPI.Installer.Gui;

public sealed partial class App : Application
{
    private readonly GuiLaunchMode LaunchMode;

    public App()
        : this(GuiLaunchMode.Demo)
    {
    }

    internal App(GuiLaunchMode launchMode)
    {
        if (!Enum.IsDefined(launchMode))
            throw new ArgumentOutOfRangeException(nameof(launchMode));
        this.LaunchMode = launchMode;
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (this.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = this.LaunchMode switch
            {
                GuiLaunchMode.Production => GuiComposition.CreateReleaseVerificationWindow(),
                GuiLaunchMode.Demo => new MainWindow(),
                _ => throw new InvalidOperationException("The graphical installer launch mode is invalid.")
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
