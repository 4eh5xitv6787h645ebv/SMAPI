using Avalonia;

namespace StardewModdingAPI.Installer.Gui;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!DemoLaunchPolicy.TryValidate(args, out string? error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
    }
}

internal static class DemoLaunchPolicy
{
    public static bool TryValidate(IReadOnlyList<string> args, out string? error)
    {
        bool valid = args.Count == 0 || (args.Count == 1 && args[0] == "--demo");
        error = valid
            ? null
            : "This preview only supports safe demo mode. Launch it with no arguments or with --demo.";
        return valid;
    }
}
