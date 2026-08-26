using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace StardewModdingAPI.Framework.Health;

/// <summary>Formats a human-readable report from the stable schema-v1 DTO.</summary>
internal sealed class ModHealthReportTextFormatter
{
    /// <summary>Format a report using invariant values and LF newlines.</summary>
    public string Format(ModHealthReport report)
    {
        StringBuilder text = new();
        text.Append("SMAPI Mod Health Report\n")
            .Append("Report ID: ").Append(report.Header.ReportId).Append('\n')
            .Append("Generated UTC: ").Append(report.Header.GeneratedUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)).Append("\n\n")
            .Append("Privacy notice\n")
            .Append("This report contains installed mod names, IDs, versions, and statuses. Inspect it before sharing. It was not uploaded automatically.\n")
            .Append("The normal SMAPI log is still needed for detailed exceptions. The standalone health report is not currently parsed by smapi.io/log.\n\n")
            .Append("What SMAPI observed\n");

        foreach (ModHealthFinding finding in report.Findings)
        {
            text.Append('[').Append(ModHealthReportTextFormatter.GetSeverityLabel(finding.Severity)).Append("] ").Append(finding.Summary).Append('\n')
                .Append("  Evidence: ").Append(finding.Evidence).Append('\n')
                .Append("  Confidence: ").Append(finding.Confidence.ToString().ToLowerInvariant()).Append('\n')
                .Append("  Limitation: ").Append(finding.Limitation).Append('\n');
        }
        if (report.Findings.Length == 0)
            text.Append("[INFO] No findings were generated.\n");

        text.Append("\nSuggested next steps\n");
        foreach (string action in report.Findings.Select(finding => finding.SuggestedAction).Distinct(StringComparer.Ordinal))
            text.Append("- ").Append(action).Append('\n');
        text.Append("- After inspecting both for private information, share this health .txt report and the normal SMAPI log. Provide the health .json only when requested.\n");

        text.Append("\nCapture quality and scope\n")
            .Append("Mode: ").Append(ModHealthReportTextFormatter.GetCaptureMode(report.Capture.Mode)).Append('\n')
            .Append("Completion: ").Append(ModHealthReportTextFormatter.GetCompletionReason(report.Capture.CompletionReason)).Append('\n')
            .Append("Duration: ").Append(ModHealthReportTextFormatter.FormatMilliseconds(report.Capture.DurationMilliseconds)).Append(" ms\n")
            .Append("Completed update ticks: ").Append(report.Capture.CompletedUpdateCount.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append("Timing data: ").Append(report.Capture.Mode == ModHealthCaptureMode.LedgerOnly
                ? "unavailable; this report contains session-ledger evidence only"
                : report.Capture.TimingValid
                    ? "valid"
                    : "invalid; percentage conclusions were suppressed").Append('\n')
            .Append("Ledger boundary: ").Append(report.Completeness.Boundary).Append('\n');
        if (report.Header.IsTruncated)
            text.Append("Report detail was truncated; see omissions below.\n");

        text.Append("\nSlow update overview\n")
            .Append("Definitions: a callback is mod-owned code observed at a named SMAPI boundary; unattributed time is work outside those measured boundaries and is not assigned to a cause.\n")
            .Append("Slow update threshold: ").Append(ModHealthReportTextFormatter.FormatMilliseconds(report.Capture.SlowUpdateThresholdMilliseconds)).Append(" ms\n")
            .Append("Slow update ticks: ").Append(report.Performance.SlowUpdateCount.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append("Observed mod callbacks: ").Append(ModHealthReportTextFormatter.FormatMilliseconds(report.Performance.TotalObservedModMilliseconds)).Append(" ms\n")
            .Append("Base-game update (exclusive): ").Append(ModHealthReportTextFormatter.FormatMilliseconds(report.Performance.TotalBaseGameExclusiveMilliseconds)).Append(" ms\n")
            .Append("Separately measured SMAPI/other update work: ").Append(ModHealthReportTextFormatter.FormatMilliseconds(report.Performance.TotalSmapiOtherMilliseconds)).Append(" ms\n")
            .Append("Remaining unattributed time: ").Append(ModHealthReportTextFormatter.FormatMilliseconds(report.Performance.TotalResidualMilliseconds)).Append(" ms\n");
        ModHealthHistogram histogram = report.Performance.Histogram;
        if (histogram.Count > 0)
        {
            text.Append("Mean update duration: ").Append(ModHealthReportTextFormatter.FormatMilliseconds(histogram.SumMilliseconds / histogram.Count)).Append(" ms\n")
                .Append("Update distribution: p50 ").Append(ModHealthReportTextFormatter.FormatNullableMilliseconds(histogram.P50Milliseconds))
                .Append(" ms, p95 ").Append(ModHealthReportTextFormatter.FormatNullableMilliseconds(histogram.P95Milliseconds))
                .Append(" ms, p99 ").Append(ModHealthReportTextFormatter.FormatNullableMilliseconds(histogram.P99Milliseconds))
                .Append(" ms (approximate bucket upper bounds)\n");
        }
        if (report.Performance.GcCollectionDataValid)
            text.Append("Process-wide GC collections during capture: ").Append(report.Performance.Gen0Collections.ToString(CultureInfo.InvariantCulture)).Append(" gen0, ").Append(report.Performance.Gen1Collections.ToString(CultureInfo.InvariantCulture)).Append(" gen1, ").Append(report.Performance.Gen2Collections.ToString(CultureInfo.InvariantCulture)).Append(" gen2 (correlation only, not mod attribution).\n");
        else
            text.Append("Process-wide GC collections during capture: unavailable; zero values below are placeholders, not observed counts.\n");

        text.Append("\nMods needing attention\n");
        ModHealthMod[] problemMods = report.Mods.Where(ModHealthReportTextFormatter.IsProblemMod).ToArray();
        if (problemMods.Length == 0)
            text.Append("None recorded.\n");
        else
        {
            foreach (ModHealthMod mod in problemMods)
                text.Append("- ").Append(mod.Name).Append(" (").Append(mod.Id).Append(") — ").Append(mod.Status.ToString().ToLowerInvariant()).Append(", ").Append(mod.SessionErrorCount.ToString(CultureInfo.InvariantCulture)).Append(" errors, ").Append(mod.CallbackFailureCount.ToString(CultureInfo.InvariantCulture)).Append(" failed callbacks\n");
        }

        text.Append("\nTop observed mods and callbacks\n");
        foreach (ModHealthMod mod in report.Mods
                     .Where(mod => mod.ObservedCallbackCount > 0)
                     .OrderByDescending(mod => mod.ObservedCallbackMilliseconds)
                     .ThenByDescending(mod => mod.ObservedCallbackPeakMilliseconds)
                     .ThenBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase)
                     .Take(20))
        {
            text.Append("- Mod total: ").Append(mod.Name).Append(" (").Append(mod.Id).Append("): ").Append(ModHealthReportTextFormatter.FormatMilliseconds(mod.ObservedCallbackMilliseconds)).Append(" ms exclusive observed time, ").Append(ModHealthReportTextFormatter.FormatMilliseconds(mod.ObservedCallbackPeakMilliseconds)).Append(" ms peak, ").Append(mod.ObservedCallbackCount.ToString(CultureInfo.InvariantCulture)).Append(" calls, ").Append(mod.SlowUpdateParticipationCount.ToString(CultureInfo.InvariantCulture)).Append(" retained slow updates\n");
        }
        if (report.Performance.Callbacks.Length == 0)
            text.Append("No callback timing detail was retained.\n");
        foreach (ModHealthCallback callback in report.Performance.Callbacks.Take(20))
        {
            double averageMilliseconds = callback.CallCount > 0 ? callback.TotalMilliseconds / callback.CallCount : 0;
            text.Append("- ").Append(callback.ModName).Append(" (").Append(callback.ModId).Append(") | ").Append(callback.Phase.ToString().ToLowerInvariant()).Append('/').Append(ModHealthReportLabels.GetOperation(callback.Operation)).Append(" | ").Append(callback.Callback)
                .Append(" [").Append(callback.Event).Append(']')
                .Append(": ").Append(ModHealthReportTextFormatter.FormatMilliseconds(callback.TotalMilliseconds)).Append(" ms total, ").Append(ModHealthReportTextFormatter.FormatMilliseconds(callback.MaximumMilliseconds)).Append(" ms peak, ").Append(ModHealthReportTextFormatter.FormatMilliseconds(averageMilliseconds)).Append(" ms average, ").Append(callback.CallCount.ToString(CultureInfo.InvariantCulture)).Append(" calls\n");
        }

        text.Append("\nSlow episodes and worst updates\n");
        if (report.Performance.Episodes.Length == 0 && report.Performance.WorstUpdates.Length == 0)
            text.Append("No slow episode or worst-update detail was retained.\n");
        foreach (ModHealthEpisode episode in report.Performance.Episodes.Take(10))
            text.Append("- Episode ticks ").Append(episode.FirstUpdateTick.ToString(CultureInfo.InvariantCulture)).Append('-').Append(episode.LastUpdateTick.ToString(CultureInfo.InvariantCulture)).Append(": ").Append(ModHealthReportTextFormatter.FormatMilliseconds(episode.MaximumMilliseconds)).Append(" ms maximum\n");
        foreach (ModHealthUpdate update in report.Performance.WorstUpdates.Take(10))
        {
            text.Append("- Update tick ").Append(update.UpdateTick.ToString(CultureInfo.InvariantCulture)).Append(": ").Append(ModHealthReportTextFormatter.FormatMilliseconds(update.TotalMilliseconds)).Append(" ms total; ").Append(ModHealthReportTextFormatter.FormatMilliseconds(update.ObservedModMilliseconds)).Append(" ms observed mod callbacks; ").Append(ModHealthReportTextFormatter.FormatMilliseconds(update.BaseGameExclusiveMilliseconds)).Append(" ms base-game exclusive; ").Append(ModHealthReportTextFormatter.FormatMilliseconds(update.SmapiOtherMilliseconds)).Append(" ms SMAPI/other; ").Append(ModHealthReportTextFormatter.FormatMilliseconds(update.ResidualMilliseconds)).Append(" ms remaining unattributed; ");
            if (update.GcCollectionDataValid)
                text.Append("GC ").Append(update.Gen0Collections.ToString(CultureInfo.InvariantCulture)).Append('/').Append(update.Gen1Collections.ToString(CultureInfo.InvariantCulture)).Append('/').Append(update.Gen2Collections.ToString(CultureInfo.InvariantCulture)).Append(" process-wide");
            else
                text.Append("process-wide GC evidence unavailable");
            text.Append('\n');
        }

        text.Append("\nErrors, failures, and logging volume\n")
            .Append("A failed callback may also emit an error; do not sum those columns as unique incidents.\n")
            .Append("Session totals: ").Append(report.LogTotals.SinceLedgerStart.WarningMessages.ToString(CultureInfo.InvariantCulture)).Append(" warnings, ").Append(report.LogTotals.SinceLedgerStart.AlertMessages.ToString(CultureInfo.InvariantCulture)).Append(" alerts, ").Append(report.LogTotals.SinceLedgerStart.ErrorMessages.ToString(CultureInfo.InvariantCulture)).Append(" errors, ").Append(report.CallbackFailureTotals.SinceLedgerStart.ToString(CultureInfo.InvariantCulture)).Append(" failed callbacks.\n")
            .Append("Capture totals: ").Append(report.LogTotals.DuringCapture.WarningMessages.ToString(CultureInfo.InvariantCulture)).Append(" warnings, ").Append(report.LogTotals.DuringCapture.AlertMessages.ToString(CultureInfo.InvariantCulture)).Append(" alerts, ").Append(report.LogTotals.DuringCapture.ErrorMessages.ToString(CultureInfo.InvariantCulture)).Append(" errors, ").Append(report.CallbackFailureTotals.DuringCapture.ToString(CultureInfo.InvariantCulture)).Append(" failed callbacks.\n");
        foreach (ModHealthLogSummary log in report.Logs)
            text.Append("- ").Append(log.Source).Append(" [").Append(log.SourceCategory.ToString().ToLowerInvariant()).Append("]: ").Append(log.SinceLedgerStart.WarningMessages.ToString(CultureInfo.InvariantCulture)).Append(" warnings, ").Append(log.SinceLedgerStart.ErrorMessages.ToString(CultureInfo.InvariantCulture)).Append(" errors, approximately ").Append(log.SinceLedgerStart.TotalCharacters.ToString(CultureInfo.InvariantCulture)).Append(" characters since ledger start; ").Append(log.DuringCapture.ErrorMessages.ToString(CultureInfo.InvariantCulture)).Append(" errors during capture\n");
        foreach (ModHealthCallbackFailure failure in report.CallbackFailures)
            text.Append("- Failed callback: ").Append(failure.ModName).Append(" (").Append(failure.ModId).Append(") | ").Append(ModHealthReportLabels.GetOperation(failure.Operation)).Append(" | ").Append(failure.Callback).Append(" | ").Append(failure.ExceptionType).Append(": ").Append(failure.SessionCount.ToString(CultureInfo.InvariantCulture)).Append(" since ledger start, ").Append(failure.CaptureCount.ToString(CultureInfo.InvariantCulture)).Append(" during capture\n");

        text.Append("\nInstalled mod and content-pack inventory\n")
            .Append("Discovered: ").Append(report.ModInventory.TotalDiscovered.ToString(CultureInfo.InvariantCulture)).Append(" total; ").Append(report.ModInventory.Loaded.ToString(CultureInfo.InvariantCulture)).Append(" loaded, ").Append(report.ModInventory.Skipped.ToString(CultureInfo.InvariantCulture)).Append(" skipped, ").Append(report.ModInventory.Ignored.ToString(CultureInfo.InvariantCulture)).Append(" ignored, ").Append(report.ModInventory.Invalid.ToString(CultureInfo.InvariantCulture)).Append(" invalid, ").Append(report.ModInventory.Failed.ToString(CultureInfo.InvariantCulture)).Append(" failed, ").Append(report.ModInventory.Discovered.ToString(CultureInfo.InvariantCulture)).Append(" unresolved.\n");
        foreach (ModHealthMod mod in report.Mods)
            text.Append("- ").Append(mod.Name).Append(" (").Append(mod.Id).Append(") ").Append(mod.Version).Append(" — ").Append(ModHealthReportTextFormatter.GetModKind(mod.Kind)).Append(", ").Append(mod.Status.ToString().ToLowerInvariant()).Append(", update ").Append(ModHealthReportTextFormatter.GetUpdateStatus(mod.UpdateStatus)).Append('\n');

        text.Append("\nEnvironment\n")
            .Append("SMAPI: ").Append(report.Environment.SmapiVersion).Append('\n')
            .Append("Game: ").Append(report.Environment.GameVersion).Append('\n')
            .Append("Runtime: ").Append(report.Environment.RuntimeVersion).Append(" (").Append(report.Environment.ProcessArchitecture).Append(", ").Append(report.Environment.ProcessBitness.ToString(CultureInfo.InvariantCulture)).Append("-bit)\n")
            .Append("Linux/session: ").Append(report.Environment.LinuxDistribution ?? "unknown").Append(" / ").Append(report.Environment.SessionType).Append('\n')
            .Append("Locale: ").Append(report.Environment.Locale).Append('\n')
            .Append("Multiplayer/split screen: ").Append(report.Environment.MultiplayerRole).Append(" / ").Append(report.Environment.SplitScreenCount.ToString(CultureInfo.InvariantCulture)).Append('\n');

        text.Append("\nOmissions and capacities\n");
        foreach (ModHealthOmission omission in report.Omissions.Where(omission => omission.Count > 0))
            text.Append("- ").Append(omission.Section).Append(": ").Append(omission.Count.ToString(CultureInfo.InvariantCulture)).Append(" omitted\n");
        foreach (ModHealthCapacity capacity in report.Capacities.Where(capacity => capacity.Reached))
            text.Append("- ").Append(capacity.Name).Append(": limit ").Append(capacity.Limit.ToString(CultureInfo.InvariantCulture)).Append(" reached\n");

        text.Append("\nAttribution and privacy limitations\n");
        foreach (string limitation in report.Limitations)
            text.Append("- ").Append(limitation).Append('\n');
        text.Append("- Allowed mod identity text is length-capped and structurally sanitized, but arbitrary author-chosen text cannot be semantically guaranteed private.\n")
            .Append("- No report was transmitted automatically.\n");

        return text.ToString();
    }

    private static bool IsProblemMod(ModHealthMod mod)
    {
        return mod.Status is not ModHealthModStatus.Loaded || mod.SessionErrorCount > 0 || mod.CallbackFailureCount > 0;
    }

    private static string FormatMilliseconds(double value) => value.ToString(value >= 100 ? "0.0" : "0.###", CultureInfo.InvariantCulture);
    private static string FormatNullableMilliseconds(double? value) => value.HasValue ? ModHealthReportTextFormatter.FormatMilliseconds(value.Value) : "unavailable";

    private static string GetSeverityLabel(ModHealthFindingSeverity severity) => severity switch
    {
        ModHealthFindingSeverity.ActionNeeded => "ACTION NEEDED",
        ModHealthFindingSeverity.Performance => "PERFORMANCE",
        ModHealthFindingSeverity.Check => "CHECK",
        _ => "INFO"
    };

    private static string GetCaptureMode(ModHealthCaptureMode mode) => mode switch
    {
        ModHealthCaptureMode.LedgerOnly => "ledger-only",
        ModHealthCaptureMode.Performance => "performance",
        _ => "health"
    };

    private static string GetCompletionReason(ModHealthCompletionReason reason) => reason switch
    {
        ModHealthCompletionReason.NotStopped => "not-stopped",
        ModHealthCompletionReason.UserStop => "user-stop",
        ModHealthCompletionReason.PerformanceStop => "performance-stop",
        ModHealthCompletionReason.NormalShutdown => "normal-shutdown",
        _ => "interim-report"
    };

    private static string GetModKind(ModHealthModKind kind) => kind switch
    {
        ModHealthModKind.CodeMod => "code-mod",
        ModHealthModKind.ContentPack => "content-pack",
        _ => "invalid"
    };

    private static string GetUpdateStatus(ModHealthReportUpdateStatus status) => status switch
    {
        ModHealthReportUpdateStatus.UpToDate => "up-to-date",
        ModHealthReportUpdateStatus.UpdateAvailable => "update-available",
        _ => status.ToString().ToLowerInvariant()
    };
}
