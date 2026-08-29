using Avalonia.Controls;
using StardewModdingAPI.Installer.Gui.Backend;
using StardewModdingAPI.Installer.Gui.Frontend;
using StardewModdingAPI.Installer.Gui.ViewModels;

namespace StardewModdingAPI.Installer.Gui;

/// <summary>Production-only composition. The sealed synthetic demo remains a separate explicit launch mode.</summary>
internal static class GuiComposition
{
    /// <summary>Create the selected top-level window and retain any resources it owns.</summary>
    public static GuiMainWindowComposition CreateMainWindow(GuiLaunchMode mode)
    {
        return CreateMainWindow(
            mode,
            CreateReleaseVerificationWindow,
            () => new GuiMainWindowComposition(new MainWindow())
        );
    }

    /// <summary>Create the selected top-level window through explicit factories for deterministic composition tests.</summary>
    internal static GuiMainWindowComposition CreateMainWindow(
        GuiLaunchMode mode,
        Func<GuiMainWindowComposition> createProduction,
        Func<GuiMainWindowComposition> createDemo
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

    private static GuiMainWindowComposition CreateReleaseVerificationWindow()
    {
        ReviewedGitHubReleaseService releaseService = new();
        ReleaseVerificationController controller = new(
            releaseService,
            ProcessInstallerProtocolClient.CreateForCurrentProcess
        );
        ReleaseVerificationViewModel viewModel = new(controller);
        return new GuiMainWindowComposition(new ReleaseVerificationWindow(viewModel), viewModel);
    }
}

/// <summary>A composed top-level window and the resources whose lifetime it owns.</summary>
internal sealed class GuiMainWindowComposition : IAsyncDisposable
{
    private IAsyncDisposable? Owner;

    public GuiMainWindowComposition(Window mainWindow, IAsyncDisposable? owner = null)
    {
        this.MainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
        this.Owner = owner;
    }

    public Window MainWindow { get; }

    public async ValueTask DisposeAsync()
    {
        IAsyncDisposable? owner = Interlocked.Exchange(ref this.Owner, null);
        if (owner is not null)
            await owner.DisposeAsync().ConfigureAwait(false);
    }
}
