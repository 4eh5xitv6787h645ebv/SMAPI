using Avalonia;
using System.Runtime.InteropServices;

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
            mode => StartSelectedMode(mode, () => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args), Console.Error),
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

    internal static int StartSelectedMode(GuiLaunchMode mode, Func<int> startDemo, TextWriter diagnostics)
    {
        ArgumentNullException.ThrowIfNull(startDemo);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (mode == GuiLaunchMode.Production)
        {
            // The production composition remains fail-closed until the reviewed release-verification
            // screen owns this bridge. Never present the sealed synthetic demo as production UI.
            diagnostics.WriteLine("The production graphical installer is not enabled in this build. Use exactly --demo to view the safe demo.");
            return 2;
        }

        return startDemo();
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
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
