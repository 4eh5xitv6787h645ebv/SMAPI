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
        : this(
            launchMode,
            launchMode == GuiLaunchMode.Demo
                ? (InstallerDiagnosticSession?)null
                : throw new ArgumentException("Production app composition requires private diagnostics.", nameof(launchMode))
        )
    {
    }

    internal App(GuiLaunchMode launchMode, InstallerDiagnosticSession? diagnosticSession)
        : this(launchMode, diagnosticSession, (mode, session, activateNext) => GuiComposition.CreateMainWindow(mode, activateNext, session))
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
        if (launchMode == GuiLaunchMode.Production && diagnosticSession is null)
            throw new ArgumentNullException(nameof(diagnosticSession), "Production app composition requires private diagnostics.");
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
