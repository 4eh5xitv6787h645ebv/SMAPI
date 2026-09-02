using System.Runtime.InteropServices;
using Avalonia;
using StardewModdingAPI.Installer.Gui.Diagnostics;

namespace StardewModdingAPI.Installer.Gui;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        return Run(
            args,
            OperatingSystem.IsLinux(),
            OperatingSystem.IsLinux() ? GetEffectiveUserId() : null,
            mode => StartSelectedMode(
                mode,
                selectedMode => StartSelectedModeWithDiagnostics(
                    selectedMode,
                    () => InstallerDiagnosticSession.CreateProduction(),
                    (desktopMode, diagnosticSession) => BuildAvaloniaApp(desktopMode, diagnosticSession).StartWithClassicDesktopLifetime(args),
                    Console.Error
                ),
                Console.Error
            ),
            Console.Error
        );
    }

    internal static int Run(IReadOnlyList<string> args, bool isLinux, uint? effectiveUserId, Func<GuiLaunchMode, int> startDesktop, TextWriter diagnostics)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(startDesktop);
        ArgumentNullException.ThrowIfNull(diagnostics);

        // Normal desktop installation must never need elevated privileges. This gate precedes
        // launch parsing and every Avalonia, process, network, staging, or logging side effect.
        if (isLinux && effectiveUserId == 0)
        {
            diagnostics.WriteLine("The SMAPI graphical installer must not be run as root or with sudo. Run it as your normal desktop user instead.");
            return 2;
        }

        if (!GuiLaunchPolicy.TryParse(args, out GuiLaunchMode mode, out string? error))
        {
            diagnostics.WriteLine(error);
            return 2;
        }

        return startDesktop(mode);
    }

    internal static int StartSelectedMode(GuiLaunchMode mode, Func<GuiLaunchMode, int> startDesktop, TextWriter diagnostics)
    {
        ArgumentNullException.ThrowIfNull(startDesktop);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (!Enum.IsDefined(mode))
        {
            diagnostics.WriteLine("The graphical installer launch mode is invalid.");
            return 2;
        }

        return startDesktop(mode);
    }

    /// <summary>Create production diagnostics after launch gates and before the desktop can start any work.</summary>
    internal static int StartSelectedModeWithDiagnostics(
        GuiLaunchMode mode,
        Func<InstallerDiagnosticSession> createDiagnostics,
        Func<GuiLaunchMode, InstallerDiagnosticSession?, int> startDesktop,
        TextWriter diagnostics
    )
    {
        ArgumentNullException.ThrowIfNull(createDiagnostics);
        ArgumentNullException.ThrowIfNull(startDesktop);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (!Enum.IsDefined(mode))
        {
            diagnostics.WriteLine("The graphical installer launch mode is invalid.");
            return 2;
        }
        if (mode == GuiLaunchMode.Demo)
            return startDesktop(mode, null);

        InstallerDiagnosticSession session;
        try
        {
            session = createDiagnostics()
                ?? throw new InstallerDiagnosticsUnavailableException();
        }
        catch
        {
            diagnostics.WriteLine("The graphical installer couldn't create its private local diagnostic log safely. No network request or game access was started.");
            return 1;
        }

        try
        {
            int exitCode = startDesktop(mode, session);
            session.MarkCompleted();
            return exitCode;
        }
        finally
        {
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public static AppBuilder BuildAvaloniaApp(GuiLaunchMode mode, InstallerDiagnosticSession? diagnosticSession = null)
    {
        return AppBuilder.Configure(() => new App(mode, diagnosticSession))
            .UsePlatformDetect()
            .LogToTrace();
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
}

internal enum GuiLaunchMode
{
    Production,
    Demo
}

internal static class GuiLaunchPolicy
{
    public static bool TryParse(IReadOnlyList<string> args, out GuiLaunchMode mode, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0)
        {
            mode = GuiLaunchMode.Production;
            error = null;
            return true;
        }
        if (args.Count == 1 && args[0] == "--demo")
        {
            mode = GuiLaunchMode.Demo;
            error = null;
            return true;
        }

        mode = default;
        error = "The graphical installer accepts either no arguments or exactly --demo.";
        return false;
    }
}
