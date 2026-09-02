using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.Globalization;
using StardewModdingAPI.Installer.Gui.Diagnostics;

namespace StardewModdingAPI.Installer.Gui;

/// <summary>A stable, bounded viewer for the GUI-owned sanitized diagnostic projection.</summary>
internal sealed partial class InstallerDiagnosticsWindow : Window
{
    private static readonly TimeSpan ClipboardDeadline = TimeSpan.FromSeconds(3);
    private readonly string SnapshotText;
    private readonly InstallerDiagnosticSession Session;
    private readonly Func<string, Task> SetClipboardText;
    private readonly TimeSpan CopyDeadline;
    private int CopyActive;

    internal InstallerDiagnosticsWindow(
        InstallerDiagnosticSession session,
        Func<string, Task>? setClipboardText = null,
        TimeSpan? copyDeadline = null
    )
    {
        ArgumentNullException.ThrowIfNull(session);
        if (copyDeadline is { } deadline && (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromSeconds(10)))
            throw new ArgumentOutOfRangeException(nameof(copyDeadline));
        this.InitializeComponent();
        this.Session = session;
        InstallerDiagnosticCapture capture = session.CreateSanitizedCapture();
        this.SnapshotText = capture.Text;
        this.SetClipboardText = setClipboardText ?? this.SetClipboardTextAsync;
        this.CopyDeadline = copyDeadline ?? ClipboardDeadline;
        this.DiagnosticText.Text = this.SnapshotText;
        this.SnapshotHealthText.Text = capture.HealthLabel;
        this.SnapshotCountText.Text = string.Format(
            CultureInfo.InvariantCulture,
            "{0} displayed entries · {1} omitted from the display window · {2} omitted from the private raw log · {3} intermediate events coalesced",
            capture.DisplayedEntryCount,
            capture.DisplayOmittedEntryCount,
            capture.RawLogOmittedEntryCount,
            capture.CoalescedEventCount
        );
        this.Opened += this.OnOpened;
        this.KeyDown += this.OnKeyDown;
        this.SizeChanged += (_, eventArgs) => this.ApplyResponsiveLayout(eventArgs.NewSize.Width);
        this.ApplyResponsiveLayout(this.Width);
    }

    internal bool IsNarrowLayout { get; private set; }

    internal void ApplyResponsiveLayout(double viewportWidth)
    {
        this.IsNarrowLayout = viewportWidth < 620;
        this.PageGrid.Margin = this.IsNarrowLayout ? new Avalonia.Thickness(14) : new Avalonia.Thickness(28);
    }

    internal Task CopyForTestingAsync() => this.CopyOnceAsync();

    private void OnOpened(object? sender, EventArgs eventArgs)
        => Dispatcher.UIThread.Post(() => this.PrivacyRegion.Focus(NavigationMethod.Tab), DispatcherPriority.Input);

    private async void OnCopyClicked(object? sender, RoutedEventArgs eventArgs)
        => await this.CopyOnceAsync();

    private void OnCloseClicked(object? sender, RoutedEventArgs eventArgs) => this.Close();

    private void OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Escape)
            return;
        eventArgs.Handled = true;
        this.Close();
    }

    private async Task CopyOnceAsync()
    {
        if (Interlocked.CompareExchange(ref this.CopyActive, 1, 0) != 0)
            return;
        if (!this.Session.TryAcquireClipboardWriteAuthority())
        {
            Volatile.Write(ref this.CopyActive, 0);
            this.CopyStatusText.Text = "Another clipboard write from this installer session is still pending. No second write was started.";
            return;
        }
        this.CopyButton.IsEnabled = false;
        this.CopyStatusText.Text = "Copying the bounded sanitized snapshot…";
        bool allowAnotherAttempt = false;
        try
        {
            Task pendingWrite = this.SetClipboardText(this.SnapshotText);
            try
            {
                await pendingWrite.WaitAsync(this.CopyDeadline).ConfigureAwait(true);
            }
            catch (TimeoutException)
            {
                _ = this.ObserveLateClipboardSettlementAsync(pendingWrite);
                this.CopyStatusText.Text = "The desktop clipboard has not confirmed completion. Copy remains disabled in this viewer because the original write may still finish.";
                return;
            }
            this.CopyStatusText.Text = "Sanitized diagnostics copied once. Review them before sharing.";
            allowAnotherAttempt = true;
        }
        catch
        {
            this.CopyStatusText.Text = "The sanitized diagnostics could not be copied. Nothing was read from the clipboard.";
            allowAnotherAttempt = true;
        }
        finally
        {
            if (allowAnotherAttempt)
            {
                this.Session.ReleaseClipboardWriteAuthority();
                Volatile.Write(ref this.CopyActive, 0);
                this.CopyButton.IsEnabled = true;
            }
        }
    }

    private async Task ObserveLateClipboardSettlementAsync(Task pendingWrite)
    {
        try
        {
            await pendingWrite.ConfigureAwait(false);
        }
        catch
        {
            // The uncertain one-shot authority stays consumed; late provider details are never displayed or logged.
        }
        finally
        {
            this.Session.ReleaseClipboardWriteAuthority();
        }
    }

    private Task SetClipboardTextAsync(string value)
    {
        IClipboard clipboard = this.Clipboard
            ?? throw new InvalidOperationException("The desktop clipboard is unavailable.");
        return clipboard.SetTextAsync(value);
    }
}
