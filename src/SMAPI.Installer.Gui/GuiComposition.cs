using StardewModdingAPI.Installer.Gui.Backend;
using StardewModdingAPI.Installer.Gui.Frontend;
using StardewModdingAPI.Installer.Gui.ViewModels;

namespace StardewModdingAPI.Installer.Gui;

/// <summary>Production-only composition. The sealed synthetic demo remains a separate explicit launch mode.</summary>
internal static class GuiComposition
{
    public static ReleaseVerificationWindow CreateReleaseVerificationWindow()
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
