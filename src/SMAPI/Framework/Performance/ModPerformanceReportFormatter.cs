using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace StardewModdingAPI.Framework.Performance;

/// <summary>Formats mod performance diagnostics for the SMAPI console and log.</summary>
internal static class ModPerformanceReportFormatter
{
    /// <summary>Format a complete ranked performance report.</summary>
    /// <param name="snapshot">The diagnostic snapshot to format.</param>
    /// <param name="limit">The maximum number of entries to show in each ranked section.</param>
    public static string Format(ModPerformanceSnapshot snapshot, int limit = 10)
    {
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit));

        Dictionary<string, MutableModSummary> mods = new(StringComparer.OrdinalIgnoreCase);
        foreach (HandlerPerformanceSnapshot handler in snapshot.Handlers)
        {
            if (!mods.TryGetValue(handler.ModId, out MutableModSummary? mod))
                mods[handler.ModId] = mod = new MutableModSummary(handler.ModId, handler.ModName);

            mod.CallCount += handler.CallCount;
            mod.TotalMilliseconds += handler.TotalMilliseconds;
            mod.MaximumMilliseconds = Math.Max(mod.MaximumMilliseconds, handler.MaximumMilliseconds);
            mod.FailureCount += handler.FailureCount;
        }

        foreach (ModLogSnapshot log in snapshot.ModLogs)
        {
            if (!mods.TryGetValue(log.ModId, out MutableModSummary? mod))
                mods[log.ModId] = mod = new MutableModSummary(log.ModId, log.ModName);

            mod.WarningCount += log.WarningCount;
            mod.ErrorCount += log.ErrorCount;
        }

        StringBuilder report = new();
        report.Append("Mod performance sample: ")
            .Append(snapshot.IsTracking ? "recording" : "stopped")
            .Append("; ")
            .Append(ModPerformanceReportFormatter.FormatDuration(snapshot.Elapsed))
            .Append(" elapsed; ")
            .Append(snapshot.CompletedTickCount.ToString("N0", CultureInfo.InvariantCulture))
            .Append(" update ticks; ")
            .Append(snapshot.Handlers.Sum(entry => entry.CallCount).ToString("N0", CultureInfo.InvariantCulture))
            .AppendLine(" instrumented mod callback calls.");

        if (snapshot.LogIndividualTicks)
        {
            report.Append("Individual tick logging: enabled at ")
                .Append(ModPerformanceReportFormatter.FormatMilliseconds(snapshot.TickLogThresholdMilliseconds))
                .AppendLine("ms or slower (0ms means every tick).");
        }
        else
            report.AppendLine("Individual tick logging: disabled.");

        if (mods.Count == 0)
            report.AppendLine("No mod callback timings, warnings, or errors have been recorded yet.");
        else
        {
            report.AppendLine();
            report.AppendLine($"Top mods (up to {limit}, ranked by instrumented callback time):");
            foreach (MutableModSummary mod in mods.Values
                         .OrderByDescending(entry => entry.TotalMilliseconds)
                         .ThenByDescending(entry => entry.ErrorCount)
                         .ThenBy(entry => entry.ModId, StringComparer.OrdinalIgnoreCase)
                         .Take(limit))
            {
                double average = mod.CallCount > 0 ? mod.TotalMilliseconds / mod.CallCount : 0;
                report.Append("   ")
                    .Append(mod.ModName)
                    .Append(" (")
                    .Append(mod.ModId)
                    .Append("): ")
                    .Append(ModPerformanceReportFormatter.FormatMilliseconds(mod.TotalMilliseconds))
                    .Append("ms total, ")
                    .Append(ModPerformanceReportFormatter.FormatMilliseconds(average))
                    .Append("ms/call, ")
                    .Append(ModPerformanceReportFormatter.FormatMilliseconds(mod.MaximumMilliseconds))
                    .Append("ms peak, ")
                    .Append(mod.CallCount.ToString("N0", CultureInfo.InvariantCulture))
                    .Append(" calls, ")
                    .Append(mod.WarningCount.ToString("N0", CultureInfo.InvariantCulture))
                    .Append(" warnings, ")
                    .Append(mod.ErrorCount.ToString("N0", CultureInfo.InvariantCulture))
                    .Append(" errors, ")
                    .Append(mod.FailureCount.ToString("N0", CultureInfo.InvariantCulture))
                    .AppendLine(" failed callbacks.");
            }
        }

        HandlerPerformanceSnapshot[] slowHandlers = snapshot.Handlers
            .OrderByDescending(entry => entry.TotalMilliseconds)
            .ThenByDescending(entry => entry.MaximumMilliseconds)
            .Take(limit)
            .ToArray();
        if (slowHandlers.Length > 0)
        {
            report.AppendLine();
            report.AppendLine($"Top mod callbacks (up to {limit}):");
            foreach (HandlerPerformanceSnapshot handler in slowHandlers)
            {
                report.Append("   ")
                    .Append(handler.ModName)
                    .Append(" (")
                    .Append(handler.ModId)
                    .Append(") | ")
                    .Append(handler.EventName)
                    .Append(" | ")
                    .Append(handler.HandlerName)
                    .Append(": ")
                    .Append(ModPerformanceReportFormatter.FormatMilliseconds(handler.TotalMilliseconds))
                    .Append("ms total, ")
                    .Append(ModPerformanceReportFormatter.FormatMilliseconds(handler.AverageMilliseconds))
                    .Append("ms average, ")
                    .Append(ModPerformanceReportFormatter.FormatMilliseconds(handler.MaximumMilliseconds))
                    .Append("ms peak, ")
                    .Append(handler.CallCount.ToString("N0", CultureInfo.InvariantCulture))
                    .Append(" calls, ")
                    .Append(handler.FailureCount.ToString("N0", CultureInfo.InvariantCulture))
                    .AppendLine(" failures.");
            }
        }

        TickPerformanceSnapshot[] slowTicks = snapshot.RecentTicks
            .OrderByDescending(entry => entry.TotalMilliseconds)
            .Take(limit)
            .ToArray();
        if (slowTicks.Length > 0)
        {
            report.AppendLine();
            report.AppendLine($"Slowest retained update ticks (up to {limit} of the latest {snapshot.RecentTicks.Count}):");
            foreach (TickPerformanceSnapshot tick in slowTicks)
                report.Append("   ").AppendLine(ModPerformanceReportFormatter.FormatTick(tick));
        }

        if (snapshot.OmittedHandlerInvocations > 0)
        {
            report.AppendLine();
            report.Append("Warning: ")
                .Append(snapshot.OmittedHandlerInvocations.ToString("N0", CultureInfo.InvariantCulture))
                .AppendLine(" callback calls were omitted after the bounded distinct-callback counter capacity was reached.");
        }

        report.AppendLine();
        report.Append("Interpretation: instrumented time covers SMAPI-managed event handlers, content load/edit callbacks, console commands, and lifecycle callbacks. ")
            .Append("Unattributed tick time can include Stardew Valley, SMAPI itself, Harmony patches, direct mod API calls, background work, waiting, or operating-system scheduling. ")
            .Append("A high timing identifies where SMAPI observed time; it does not by itself prove the underlying cause.");

        return report.ToString();
    }

    /// <summary>Format one completed tick for live diagnostic logging.</summary>
    /// <param name="tick">The completed tick.</param>
    public static string FormatTick(TickPerformanceSnapshot tick)
    {
        string slowest = tick.SlowestModId is null
            ? "none"
            : $"{tick.SlowestModName} ({tick.SlowestModId}) {ModPerformanceReportFormatter.FormatMilliseconds(tick.SlowestModMilliseconds)}ms";

        return $"tick {tick.Tick}: {ModPerformanceReportFormatter.FormatMilliseconds(tick.TotalMilliseconds)}ms total; {ModPerformanceReportFormatter.FormatMilliseconds(tick.InstrumentedModMilliseconds)}ms instrumented mod callbacks; {ModPerformanceReportFormatter.FormatMilliseconds(tick.UnattributedMilliseconds)}ms unattributed; slowest mod {slowest}; {tick.ErrorCount} mod errors";
    }

    /// <summary>Format milliseconds with enough precision for handler-level diagnostics.</summary>
    private static string FormatMilliseconds(double milliseconds)
    {
        return milliseconds.ToString(milliseconds >= 100 ? "F1" : "F3", CultureInfo.InvariantCulture);
    }

    /// <summary>Format a sample duration.</summary>
    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalMinutes >= 1
            ? $"{duration.TotalMinutes.ToString("F1", CultureInfo.InvariantCulture)} minutes"
            : $"{duration.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} seconds";
    }

    /// <summary>Mutable aggregate report data for one mod.</summary>
    private sealed class MutableModSummary(string modId, string modName)
    {
        public string ModId { get; } = modId;
        public string ModName { get; } = modName;
        public long CallCount;
        public double TotalMilliseconds;
        public double MaximumMilliseconds;
        public long WarningCount;
        public long ErrorCount;
        public long FailureCount;
    }
}
