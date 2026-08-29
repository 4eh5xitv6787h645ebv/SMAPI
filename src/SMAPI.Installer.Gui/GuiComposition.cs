using Avalonia.Controls;
using StardewModdingAPI.Installer.Gui.Backend;
using StardewModdingAPI.Installer.Gui.Frontend;
using StardewModdingAPI.Installer.Gui.ViewModels;

namespace StardewModdingAPI.Installer.Gui;

/// <summary>Production-only composition. The sealed synthetic demo remains a separate explicit launch mode.</summary>
internal static class GuiComposition
{
    /// <summary>Create the selected top-level window.</summary>
    public static Window CreateMainWindow(GuiLaunchMode mode)
    {
        return CreateMainWindow(
            mode,
            CreateReleaseVerificationWindow,
            () => new MainWindow()
        );
    }

    /// <summary>Create the selected top-level window through explicit factories for deterministic composition tests.</summary>
    internal static Window CreateMainWindow(
        GuiLaunchMode mode,
        Func<Window> createProduction,
        Func<Window> createDemo
    )
    {
        ArgumentNullException.ThrowIfNull(createProduction);
        ArgumentNullException.ThrowIfNull(createDemo);

        return mode switch
        {
            GuiLaunchMode.Production => createProduction(),
            GuiLaunchMode.Demo => createDemo(),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    private static ReleaseVerificationWindow CreateReleaseVerificationWindow()
    {
        ReviewedGitHubReleaseService releaseService = new();
        ReleaseVerificationController controller = new(
            releaseService,
            ProcessInstallerProtocolClient.CreateForCurrentProcess
        );
        ReleaseVerificationViewModel viewModel = new(controller);
        return new ReleaseVerificationWindow(viewModel);
    }
}
