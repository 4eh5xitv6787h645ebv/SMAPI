using System;
using System.Globalization;
using System.Linq;
using StardewModdingAPI.Framework.Health;
using StardewModdingAPI.Framework.Performance;

namespace StardewModdingAPI.Framework.Commands;

/// <summary>The built-in <c>performance</c> diagnostic command.</summary>
internal sealed class PerformanceCommand : IInternalCommand
{
    /*********
    ** Fields
    *********/
    /// <summary>Collects mod performance diagnostics.</summary>
    private readonly ModPerformanceManager PerformanceManager;

    /// <summary>Coordinates ownership with the health workflow, if initialized.</summary>
    private readonly ModHealthSessionCoordinator? Coordinator;


    /*********
    ** Accessors
    *********/
    /// <inheritdoc />
    public string Name { get; } = "performance";

    /// <inheritdoc />
    public string Description { get; } =
        """
        Record and report which mod callbacks consume time or emit errors. This includes SMAPI events, content load/edit callbacks, mod console commands, and lifecycle callbacks. Each measured tick distinguishes the base game update (which can include Harmony patches and other unobserved work invoked by the game), observed mod callbacks, and residual time outside those boundaries. Separate SMAPI/other update timing is unavailable until SMAPI has an owned measurement boundary. Garbage collection counts are included as an allocation-pressure signal.

        Usage: performance start [tick-threshold-ms]
        Start a fresh sample. If a nonnegative threshold is provided, log each update tick at or above that duration; use 0 to log every tick.

        Usage: performance ticks <off|tick-threshold-ms>
        Change live tick logging without resetting the sample. Use 0 to log every tick.

        Usage: performance report [limit]
        Show ranked mod, callback, error, and recent slow-tick data. The default limit is 10.

        Usage: performance status
        Show whether sampling and individual tick logging are enabled.

        Usage: performance reset
        Clear recorded performance and error data without changing whether sampling is enabled.

        Usage: performance stop [limit]
        Stop sampling and show the final report.
        """;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="performanceManager">Collects mod performance diagnostics.</param>
    public PerformanceCommand(ModPerformanceManager performanceManager)
    {
        this.PerformanceManager = performanceManager;
    }

    /// <summary>Construct an instance using the shared health/performance coordinator.</summary>
    /// <param name="coordinator">Coordinates the single diagnostic timing capture.</param>
    public PerformanceCommand(ModHealthSessionCoordinator coordinator)
    {
        this.Coordinator = coordinator;
        this.PerformanceManager = null!;
    }

    /// <inheritdoc />
    public void HandleCommand(string[] args, IMonitor monitor)
    {
        using IDisposable? reporterScope = this.Coordinator is not null ? ModHealthReporterLogScope.Enter() : null;
        string action = args.Length > 0 ? args[0].ToLowerInvariant() : "report";
        switch (action)
        {
            case "start":
                this.Start(args, monitor);
                break;

            case "ticks":
                this.ConfigureTicks(args, monitor);
                break;

            case "report":
                if (this.TryGetLimit(args, 1, monitor, out int reportLimit))
                    monitor.Log(ModPerformanceReportFormatter.Format(this.GetSnapshot(), reportLimit), LogLevel.Info);
                break;

            case "status":
                this.LogStatus(monitor);
                break;

            case "reset":
                if (args.Length != 1)
                {
                    monitor.Log("Usage: performance reset", LogLevel.Error);
                    break;
                }

                if (this.Coordinator is null)
                {
                    this.PerformanceManager.Reset();
                    monitor.Log("Cleared the mod performance, warning, and error diagnostics.", LogLevel.Info);
                }
                else
                    this.LogResult(this.Coordinator.ResetPerformance(), monitor);
                break;

            case "stop":
                if (!this.TryGetLimit(args, 1, monitor, out int stopLimit))
                    break;

                if (this.Coordinator is null)
                    this.PerformanceManager.Stop();
                else
                    this.LogResult(this.Coordinator.StopPerformance(), monitor);
                monitor.Log(ModPerformanceReportFormatter.Format(this.GetSnapshot(), stopLimit), LogLevel.Info);
                break;

            default:
                monitor.Log($"Unknown performance action '{args[0]}'. Valid actions: start, ticks, report, status, reset, stop.", LogLevel.Error);
                break;
        }
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Handle the start action.</summary>
    /// <param name="args">The command arguments.</param>
    /// <param name="monitor">Writes messages to the console.</param>
    private void Start(string[] args, IMonitor monitor)
    {
        if (args.Length > 2)
        {
            monitor.Log("Usage: performance start [tick-threshold-ms]", LogLevel.Error);
            return;
        }

        bool logTicks = args.Length == 2;
        double threshold = 0;
        if (logTicks && !PerformanceCommand.TryParseThreshold(args[1], monitor, out threshold))
            return;

        ModHealthCoordinatorResult? result = this.Coordinator?.StartPerformance(logTicks, threshold);
        if (result?.IsError == true)
        {
            this.LogResult(result, monitor);
            return;
        }
        if (this.Coordinator is null)
            this.PerformanceManager.Start(logTicks, threshold);
        monitor.Log(
            result?.Code == ModHealthCoordinatorResultCode.Replaced
                ? "Started a fresh mod performance sample; the previous advanced sample was reset."
                : logTicks
                ? $"Started a fresh mod performance sample. Individual update ticks at or above {threshold.ToString("0.###", CultureInfo.InvariantCulture)}ms will be logged. Use 'performance stop' when the slowdown has occurred."
                : "Started a fresh mod performance sample. Use 'performance report' at any time or 'performance stop' when the slowdown has occurred.",
            LogLevel.Info
        );
    }

    /// <summary>Handle the individual-tick logging action.</summary>
    /// <param name="args">The command arguments.</param>
    /// <param name="monitor">Writes messages to the console.</param>
    private void ConfigureTicks(string[] args, IMonitor monitor)
    {
        if (args.Length != 2)
        {
            monitor.Log("Usage: performance ticks <off|tick-threshold-ms>", LogLevel.Error);
            return;
        }

        if (args[1].Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            this.ConfigureTickLogging(enabled: false, thresholdMilliseconds: 0);
            monitor.Log("Individual performance tick logging is disabled; aggregate sampling is unchanged.", LogLevel.Info);
            return;
        }

        if (!PerformanceCommand.TryParseThreshold(args[1], monitor, out double threshold))
            return;

        this.ConfigureTickLogging(enabled: true, threshold);
        monitor.Log($"Individual update ticks at or above {threshold.ToString("0.###", CultureInfo.InvariantCulture)}ms will be logged while performance sampling is active.", LogLevel.Info);
    }

    /// <summary>Log the current diagnostic state.</summary>
    /// <param name="monitor">Writes messages to the console.</param>
    private void LogStatus(IMonitor monitor)
    {
        ModPerformanceSnapshot snapshot = this.GetSnapshot();
        string tickLogging = snapshot.LogIndividualTicks
            ? $"enabled at {snapshot.TickLogThresholdMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)}ms or slower"
            : "disabled";

        monitor.Log($"Mod performance sampling is {(snapshot.IsTracking ? "active" : "stopped")}; individual tick logging is {tickLogging}; {snapshot.CompletedTickCount:N0} ticks and {snapshot.Handlers.Sum(entry => entry.CallCount):N0} callback calls are recorded.", LogLevel.Info);
    }

    /// <summary>Parse an optional report entry limit.</summary>
    private bool TryGetLimit(string[] args, int index, IMonitor monitor, out int limit)
    {
        limit = 10;
        if (args.Length <= index)
            return true;
        if (args.Length > index + 1 || !int.TryParse(args[index], NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 100)
        {
            monitor.Log($"The report limit must be a whole number from 1 through 100.", LogLevel.Error);
            return false;
        }

        return true;
    }

    /// <summary>Parse a nonnegative finite tick threshold.</summary>
    private static bool TryParseThreshold(string raw, IMonitor monitor, out double threshold)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out threshold) || !double.IsFinite(threshold) || threshold < 0)
        {
            monitor.Log("The tick threshold must be a nonnegative number of milliseconds; use 0 to log every tick.", LogLevel.Error);
            return false;
        }

        return true;
    }

    /// <summary>Get the live or retained performance snapshot.</summary>
    private ModPerformanceSnapshot GetSnapshot()
    {
        return this.Coordinator?.GetPerformanceSnapshot() ?? this.PerformanceManager.GetSnapshot();
    }

    /// <summary>Configure live tick logging through the coordinator when available.</summary>
    private void ConfigureTickLogging(bool enabled, double thresholdMilliseconds)
    {
        if (this.Coordinator is null)
            this.PerformanceManager.ConfigureTickLogging(enabled, thresholdMilliseconds);
        else
            this.Coordinator.ConfigureTickLogging(enabled, thresholdMilliseconds);
    }

    /// <summary>Log a typed coordinator result.</summary>
    private void LogResult(ModHealthCoordinatorResult result, IMonitor monitor)
    {
        monitor.Log(result.Message, result.IsError ? LogLevel.Error : LogLevel.Info);
    }
}
