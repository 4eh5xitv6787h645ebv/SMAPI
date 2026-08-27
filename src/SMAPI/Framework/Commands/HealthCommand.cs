using System;
using System.Globalization;
using StardewModdingAPI.Framework.Health;
using StardewModdingAPI.Framework.Health.Viewer;
using StardewModdingAPI.Framework.Performance;

namespace StardewModdingAPI.Framework.Commands;

/// <summary>The Linux desktop <c>health</c> diagnostic workflow.</summary>
internal sealed class HealthCommand : IInternalCommand
{
    private readonly ModHealthSessionCoordinator Coordinator;
    private readonly Func<ModHealthViewerActionDisposition>? QueueViewerOpen;

    /// <inheritdoc />
    public string Name { get; } = "health";

    /// <inheritdoc />
    public string Description { get; } =
        """
        Record a private, bounded mod health sample and write text/JSON reports for troubleshooting.

        Usage: health
        Show the current state and next step.

        Usage: health start
        Start a fresh timing sample. Reproduce the problem, then enter `health stop`.

        Usage: health status
        Show separate session-ledger, timed-capture, capacity, and export state.

        Usage: health view
        Open the current session's private Mod Health Report in-game on Linux desktop.
        Use mouse click/wheel; keyboard arrows, Page Up/Down, Home/End, Tab, Enter, I/P, and Escape; or controller D-pad, shoulders, A/B/X/Y (confirm/back/privacy/status).
        The viewer shows only the sanitized in-memory report and stable local relative paths. It never uploads, copies, opens external apps, or changes mods, saves, or settings. Inspect reports before sharing.
        Viewer labels fall back to English when a translation is unavailable; schema-v1 finding text remains canonical English.

        Usage: health mark
        Add a numbered reproduction mark. Free-text marks are deliberately unsupported.

        Usage: health report
        Queue an interim report without stopping, or export the retained/session-only evidence.

        Usage: health stop
        Stop and freeze the timing sample, then queue its final report.

        Usage: health retry
        Retry the exact frozen report retained after a write failure.

        Usage: health reset confirm
        Explicitly discard timed evidence and any failed retry. The session ledger is kept.

        Reports stay in private local storage. After a successful export, SMAPI keeps at most five complete report pairs and removes pairs older than 30 days.
        """;

    /// <summary>Construct an instance.</summary>
    public HealthCommand(ModHealthSessionCoordinator coordinator, Func<ModHealthViewerActionDisposition>? queueViewerOpen = null)
    {
        this.Coordinator = coordinator;
        this.QueueViewerOpen = queueViewerOpen;
    }

    /// <inheritdoc />
    public void HandleCommand(string[] args, IMonitor monitor)
    {
        using IDisposable reporterScope = ModHealthReporterLogScope.Enter();
        string action = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        switch (action)
        {
            case "help":
                if (args.Length != 0)
                    this.LogUsage(monitor, "Usage: health");
                else
                    this.LogOverview(monitor);
                break;

            case "start":
                if (RequireExactArguments(args, 1, monitor, "Usage: health start"))
                    this.LogResult(this.Coordinator.StartHealth(), monitor);
                break;

            case "status":
                if (RequireExactArguments(args, 1, monitor, "Usage: health status"))
                    this.LogStatus(monitor);
                break;

            case "view":
                if (RequireExactArguments(args, 1, monitor, "Usage: health view"))
                    this.OpenViewer(monitor);
                break;

            case "mark":
                if (RequireExactArguments(args, 1, monitor, "Usage: health mark (free text is not accepted)"))
                    this.LogResult(this.Coordinator.Mark(), monitor);
                break;

            case "report":
                if (RequireExactArguments(args, 1, monitor, "Usage: health report"))
                    this.LogResult(this.Coordinator.ReportHealth(), monitor);
                break;

            case "stop":
                if (RequireExactArguments(args, 1, monitor, "Usage: health stop"))
                    this.LogResult(this.Coordinator.StopHealth(), monitor);
                break;

            case "retry":
                if (RequireExactArguments(args, 1, monitor, "Usage: health retry"))
                    this.LogResult(this.Coordinator.RetryHealthExport(), monitor);
                break;

            case "reset":
                if (args.Length == 1)
                    monitor.Log("Reset discards timed evidence and any failed frozen export, but keeps the session ledger. Enter 'health reset confirm' to continue.", LogLevel.Warn);
                else if (args.Length == 2 && args[1].Equals("confirm", StringComparison.OrdinalIgnoreCase))
                    this.LogResult(this.Coordinator.ResetHealth(), monitor);
                else
                    this.LogUsage(monitor, "Usage: health reset confirm");
                break;

            default:
                monitor.Log($"Unknown health action '{args[0]}'. Valid actions: start, status, view, mark, report, stop, retry, reset confirm.", LogLevel.Error);
                break;
        }
    }

    private void OpenViewer(IMonitor monitor)
    {
        if (this.QueueViewerOpen is null)
        {
            monitor.Log("The in-game Mod Health Report viewer is only available in Linux desktop SMAPI.", LogLevel.Error);
            return;
        }

        ModHealthViewerActionDisposition result = this.QueueViewerOpen();
        switch (result)
        {
            case ModHealthViewerActionDisposition.Queued:
                monitor.Log("Opening the Mod Health Report viewer on the next safe game update.", LogLevel.Info);
                break;

            case ModHealthViewerActionDisposition.Coalesced:
                monitor.Log("The Mod Health Report viewer is already open or queued for this screen.", LogLevel.Info);
                break;

            default:
                monitor.Log("The Mod Health Report viewer request queue is full. Try 'health view' again after the next game update.", LogLevel.Error);
                break;
        }
    }

    private void LogOverview(IMonitor monitor)
    {
        ModHealthSessionStatus status = this.Coordinator.GetStatus();
        string next = status.CaptureState switch
        {
            ModHealthCaptureState.Active => "Reproduce the problem, optionally enter 'health mark', then enter 'health stop'.",
            ModHealthCaptureState.StoppedRetained when status.Export.State == ModHealthExportState.Failed => "Enter 'health retry', or 'health reset confirm' to discard the failed report.",
            ModHealthCaptureState.StoppedRetained when status.Export.State is ModHealthExportState.Queued or ModHealthExportState.Writing => "The retained report is queued or being written. Enter 'health status' to check it, or wait for the completed paths.",
            ModHealthCaptureState.StoppedRetained when status.Export.State == ModHealthExportState.Succeeded => HealthCommand.FormatCompletedNextStep(status.Export),
            ModHealthCaptureState.StoppedRetained => "Enter 'health report' to save the retained sample, or 'health reset confirm' to discard it.",
            _ => "Enter 'health start', reproduce the problem, then enter 'health stop'."
        };
        monitor.Log($"Mod health capture is {FormatCaptureState(status)}. {next} Enter 'health view' to open the in-game report viewer.", LogLevel.Info);
    }

    private void LogStatus(IMonitor monitor)
    {
        ModHealthSessionStatus status = this.Coordinator.GetStatus();
        ModPerformanceSnapshot? capture = status.Performance;
        string captureDetails = capture == null
            ? "Timed capture: none."
            : $"Timed capture: {FormatCaptureState(status)}; {capture.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} seconds; {capture.CompletedTickCount.ToString("N0", CultureInfo.InvariantCulture)} completed update ticks; {status.SlowUpdateCount.ToString("N0", CultureInfo.InvariantCulture)} slow updates observed with {status.RetainedSlowMomentCount.ToString("N0", CultureInfo.InvariantCulture)} retained worst moments; {status.CaptureWarningCount.ToString("N0", CultureInfo.InvariantCulture)} warnings/alerts and {status.CaptureErrorCount.ToString("N0", CultureInfo.InvariantCulture)} errors during capture; {status.MarkCount.ToString(CultureInfo.InvariantCulture)} marks.";
        string capacity = status.CapacityReached ? " One or more bounded capacities were reached; omissions will be listed in the report." : " No capacity omissions are currently recorded.";
        string pending = status.HasPendingConfiguration ? " Persistent start/stop settings are pending until the manual sample ends." : "";
        monitor.Log(
            $"Session ledger: {status.SessionWarningCount.ToString("N0", CultureInfo.InvariantCulture)} warnings/alerts and {status.SessionErrorCount.ToString("N0", CultureInfo.InvariantCulture)} errors since managed core initialization. {captureDetails} Export: {status.Export.State.ToString().ToLowerInvariant()}.{capacity}{pending} Enter 'health view' to open the in-game report viewer.",
            LogLevel.Info
        );
    }

    private void LogResult(ModHealthCoordinatorResult result, IMonitor monitor)
    {
        monitor.Log(result.Message, result.IsError ? LogLevel.Error : LogLevel.Info);
    }

    private void LogUsage(IMonitor monitor, string usage)
    {
        monitor.Log(usage, LogLevel.Error);
    }

    private static bool RequireExactArguments(string[] args, int count, IMonitor monitor, string usage)
    {
        if (args.Length == count)
            return true;
        monitor.Log(usage, LogLevel.Error);
        return false;
    }

    private static string FormatCaptureState(ModHealthSessionStatus status)
    {
        string owner = status.Owner switch
        {
            ModHealthCaptureOwner.Health => "health-owned",
            ModHealthCaptureOwner.Performance => "performance-owned",
            _ => "unowned"
        };
        return status.CaptureState switch
        {
            ModHealthCaptureState.Active => $"active ({owner})",
            ModHealthCaptureState.StoppedRetained => $"stopped with a retained {owner} sample",
            _ => "inactive with no retained sample"
        };
    }

    private static string FormatCompletedNextStep(ModHealthExportStatus export)
    {
        string saved = export.TextPath is not null && export.JsonPath is not null
            ? $"The retained report was saved as {export.TextPath} and {export.JsonPath}."
            : "The retained report was saved successfully.";
        return $"{saved} Enter 'health start' for a fresh sample, or 'health reset confirm' to discard the retained timing.";
    }
}
