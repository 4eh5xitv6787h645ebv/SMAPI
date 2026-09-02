using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using StardewModdingAPI.Installer.Gui.Diagnostics;

namespace StardewModdingAPI.Installer.Gui;

public sealed partial class App : Application
{
    private readonly GuiLaunchMode LaunchMode;
    private readonly InstallerDiagnosticSession? DiagnosticSession;
    private readonly Func<GuiLaunchMode, InstallerDiagnosticSession?, Action<Window>, Window> CreateMainWindow;

    public App()
        : this(GuiLaunchMode.Demo)
    {
    }

    internal App(GuiLaunchMode launchMode)
        : this(launchMode, (InstallerDiagnosticSession?)null)
    {
    }

    internal App(GuiLaunchMode launchMode, InstallerDiagnosticSession? diagnosticSession)
        : this(launchMode, diagnosticSession, (mode, session, activateNext) => GuiComposition.CreateMainWindow(mode, activateNext, session))
    {
    }

    internal App(
        GuiLaunchMode launchMode,
        Func<GuiLaunchMode, Window> createMainWindow
    )
        : this(launchMode, null, (mode, _, _) => createMainWindow(mode))
    {
    }

    internal App(
        GuiLaunchMode launchMode,
        Func<GuiLaunchMode, Action<Window>, Window> createMainWindow
    )
        : this(launchMode, null, (mode, _, activateNext) => createMainWindow(mode, activateNext))
    {
    }

    internal App(
        GuiLaunchMode launchMode,
        InstallerDiagnosticSession? diagnosticSession,
        Func<GuiLaunchMode, InstallerDiagnosticSession?, Action<Window>, Window> createMainWindow
    )
    {
        if (!Enum.IsDefined(launchMode))
            throw new ArgumentOutOfRangeException(nameof(launchMode));
        if (launchMode == GuiLaunchMode.Demo && diagnosticSession is not null)
            throw new ArgumentException("Demo mode must not receive production diagnostics.", nameof(diagnosticSession));
        this.LaunchMode = launchMode;
        this.DiagnosticSession = diagnosticSession;
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

            desktop.MainWindow = this.CreateMainWindow(this.LaunchMode, this.DiagnosticSession, ActivateNext);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
