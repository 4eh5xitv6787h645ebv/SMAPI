using Avalonia.Controls;
using StardewModdingAPI.Installer.Gui.Backend;
using StardewModdingAPI.Installer.Gui.Diagnostics;
using StardewModdingAPI.Installer.Gui.Frontend;
using StardewModdingAPI.Installer.Gui.ViewModels;

namespace StardewModdingAPI.Installer.Gui;

/// <summary>Production-only composition. The sealed synthetic demo remains a separate explicit launch mode.</summary>
internal static class GuiComposition
{
    /// <summary>Create the selected top-level window.</summary>
    public static Window CreateMainWindow(GuiLaunchMode mode)
    {
        return mode switch
        {
            GuiLaunchMode.Demo => new MainWindow(),
            GuiLaunchMode.Production => throw new InvalidOperationException("Production composition requires an initialized private diagnostic session."),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    /// <summary>Create the selected top-level window with the production-only diagnostic owner.</summary>
    internal static Window CreateMainWindow(
        GuiLaunchMode mode,
        Action<Window> activateNextWindow,
        InstallerDiagnosticSession? diagnosticSession
    )
    {
        ArgumentNullException.ThrowIfNull(activateNextWindow);
        return mode switch
        {
            GuiLaunchMode.Production => CreateReleaseVerificationWindow(
                activateNextWindow,
                diagnosticSession ?? throw new ArgumentNullException(nameof(diagnosticSession), "Production composition requires private diagnostics.")
            ),
            GuiLaunchMode.Demo when diagnosticSession is null => new MainWindow(),
            GuiLaunchMode.Demo => throw new ArgumentException("Demo mode must not receive production diagnostics.", nameof(diagnosticSession)),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    /// <summary>Create the selected top-level window through explicit factories for deterministic composition tests.</summary>
    internal static Window CreateMainWindowFromFactoriesForTesting(
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

    private static ReleaseVerificationWindow CreateReleaseVerificationWindow(
        Action<Window> activateNextWindow,
        InstallerDiagnosticSession diagnosticSession
    )
    {
        ProductionInstallerWorkflow workflow = new(
            new ReviewedGitHubReleaseService(),
            ProcessInstallerProtocolClient.CreateForCurrentProcess,
            activateNextWindow,
            diagnosticSession
        );
        return workflow.CreateInitialWindow();
    }
}
