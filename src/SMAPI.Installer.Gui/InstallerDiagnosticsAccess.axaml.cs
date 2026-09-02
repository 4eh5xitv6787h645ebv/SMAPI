using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using StardewModdingAPI.Installer.Gui.Diagnostics;

namespace StardewModdingAPI.Installer.Gui;

/// <summary>A reusable production-only entry point into the GUI-owned diagnostic snapshot.</summary>
internal sealed partial class InstallerDiagnosticsAccess : UserControl
{
    private InstallerDiagnosticSession? Session;
    private InstallerDiagnosticsWindow? ActiveWindow;
    private bool Attached;

    public InstallerDiagnosticsAccess()
    {
        this.InitializeComponent();
    }

    internal void Attach(InstallerDiagnosticSession? session)
    {
        if (this.Attached)
            throw new InvalidOperationException("The diagnostic access control is already attached.");
        this.Attached = true;
        this.Session = session;
        this.IsVisible = session is not null;
    }

    private void OnOpenClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (this.Session is null)
            return;
        if (this.ActiveWindow is { } active)
        {
            active.Activate();
            return;
        }
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;

        InstallerDiagnosticsWindow window = new(this.Session);
        this.ActiveWindow = window;
        window.Closed += (_, _) =>
        {
            this.ActiveWindow = null;
            Dispatcher.UIThread.Post(() => this.OpenButton.Focus(NavigationMethod.Tab), DispatcherPriority.Input);
        };
        _ = window.ShowDialog(owner);
    }
}
