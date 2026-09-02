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
        return CreateMainWindow(mode, window => window.Show());
    }

    /// <summary>Create the selected top-level window and provide the production next-window activation boundary.</summary>
    internal static Window CreateMainWindow(GuiLaunchMode mode, Action<Window> activateNextWindow)
        => CreateMainWindow(mode, activateNextWindow, null);

    /// <summary>Create the selected top-level window with the production-only diagnostic owner.</summary>
    internal static Window CreateMainWindow(
        GuiLaunchMode mode,
        Action<Window> activateNextWindow,
        InstallerDiagnosticSession? diagnosticSession
    )
    {
        ArgumentNullException.ThrowIfNull(activateNextWindow);
        if (mode == GuiLaunchMode.Demo && diagnosticSession is not null)
            throw new ArgumentException("Demo mode must not receive production diagnostics.", nameof(diagnosticSession));
        return CreateMainWindow(
            mode,
            () => CreateReleaseVerificationWindow(activateNextWindow, diagnosticSession),
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

    private static ReleaseVerificationWindow CreateReleaseVerificationWindow(
        Action<Window> activateNextWindow,
        InstallerDiagnosticSession? diagnosticSession
    )
    {
        ProductionInstallerWorkflow workflow = new(
            new ReviewedGitHubReleaseService(),
            ProcessInstallerProtocolClient.CreateForCurrentProcess,
            activateNextWindow,
            diagnosticSession: diagnosticSession
        );
        return workflow.CreateInitialWindow();
    }
}
