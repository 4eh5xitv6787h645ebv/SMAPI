using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;

namespace StardewModdingAPI.Framework.Health;

/// <summary>Generates deterministic, cautious findings from a frozen mod health report.</summary>
internal sealed class ModHealthInsightAnalyzer
{
    /// <summary>Analyze a report and return findings in stable priority order.</summary>
    public ImmutableArray<ModHealthFinding> Analyze(ModHealthReport report)
    {
        List<ModHealthFinding> findings = [];

        foreach (ModHealthMod mod in report.Mods.OrderBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (mod.Status is ModHealthModStatus.Failed or ModHealthModStatus.Invalid or ModHealthModStatus.Skipped)
            {
                findings.Add(new(
                    RuleId: "mod-load-problem",
                    Severity: ModHealthFindingSeverity.ActionNeeded,
                    Confidence: ModHealthFindingConfidence.Factual,
                    ModId: mod.Id,
                    Summary: $"{mod.Name} ({mod.Id}) did not load normally.",
                    Evidence: $"SMAPI recorded the mod status as {ModHealthInsightAnalyzer.GetStatusText(mod.Status)}{(mod.FailureCategory is null ? "." : $" ({mod.FailureCategory}).")}",
                    SuggestedAction: mod.SuggestedUpdateVersion is null
                        ? "Review the normal SMAPI log for the detailed load error and check the mod's requirements."
                        : $"Review the normal SMAPI log and consider updating to {mod.SuggestedUpdateVersion}.",
                    Limitation: "A load status does not show whether removing the mod is safe for this save or its dependencies."
                ));
            }

            if (mod.CallbackFailureCount > 0)
            {
                findings.Add(new(
                    "callback-failure",
                    ModHealthFindingSeverity.ActionNeeded,
                    ModHealthFindingConfidence.Factual,
                    mod.Id,
                    $"SMAPI observed failed callbacks from {mod.Name} ({mod.Id}).",
                    $"{mod.CallbackFailureCount.ToString(CultureInfo.InvariantCulture)} callback invocation(s) failed.",
                    "Review the normal SMAPI log for exception details and check for a compatible update.",
                    "A failed callback may also emit an error log entry; those counts must not be summed as unique incidents."
                ));
            }

            if (mod.CaptureErrorCount >= ModHealthReportLimits.HighCaptureErrorCount || mod.SessionErrorCount >= ModHealthReportLimits.HighSessionErrorCount)
            {
                findings.Add(new(
                    "high-error-volume",
                    ModHealthFindingSeverity.Check,
                    ModHealthFindingConfidence.Factual,
                    mod.Id,
                    $"{mod.Name} ({mod.Id}) logged many errors.",
                    $"SMAPI counted {mod.CaptureErrorCount.ToString(CultureInfo.InvariantCulture)} during capture and {mod.SessionErrorCount.ToString(CultureInfo.InvariantCulture)} since the ledger started.",
                    "Review the normal SMAPI log for the message text and check for a compatible update.",
                    "Repeated log entries may describe the same underlying incident."
                ));
            }

            if (mod.PeakMessagesPerSecond >= ModHealthReportLimits.LogFloodMessagesPerSecond || mod.PeakCharactersPerSecond >= ModHealthReportLimits.LogFloodCharactersPerSecond)
            {
                findings.Add(new(
                    "log-flood",
                    ModHealthFindingSeverity.Check,
                    ModHealthFindingConfidence.Factual,
                    mod.Id,
                    $"SMAPI observed a burst of logging from {mod.Name} ({mod.Id}).",
                    $"The highest one-second bucket contained {mod.PeakMessagesPerSecond.ToString(CultureInfo.InvariantCulture)} messages and approximately {mod.PeakCharactersPerSecond.ToString(CultureInfo.InvariantCulture)} characters.",
                    "Review the normal SMAPI log and the mod's diagnostic settings.",
                    "Logging volume can correlate with extra work, but this report does not measure the cost of every destination or device."
                ));
            }

            bool hasProblem = mod.Status is not ModHealthModStatus.Loaded || mod.CaptureErrorCount > 0 || mod.SessionErrorCount > 0 || mod.CallbackFailureCount > 0;
            if (hasProblem && mod.SuggestedUpdateVersion is not null)
            {
                findings.Add(new(
                    "update-available-for-problem",
                    ModHealthFindingSeverity.Check,
                    ModHealthFindingConfidence.Factual,
                    mod.Id,
                    $"An update is available for {mod.Name} ({mod.Id}), which also has recorded issues.",
                    $"The installed version is {mod.Version}; SMAPI's existing update result suggests {mod.SuggestedUpdateVersion}.",
                    "Check the mod page and its requirements before updating, and back up the save first.",
                    "Update availability is informational and does not show that the update addresses the recorded issue."
                ));
            }
        }

        foreach (ModHealthCallback callback in report.Performance.Callbacks
                     .Where(callback => callback.MaximumMilliseconds >= ModHealthReportLimits.ExtremeCallbackMilliseconds)
                     .OrderByDescending(callback => callback.MaximumMilliseconds)
                     .ThenBy(callback => callback.ModId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(callback => callback.Callback, StringComparer.Ordinal)
                     .Take(ModHealthReportLimits.MaxFindings))
        {
            findings.Add(new(
                "extreme-callback-peak",
                ModHealthFindingSeverity.Performance,
                ModHealthFindingConfidence.Factual,
                callback.ModId,
                $"SMAPI observed a long callback from {callback.ModName} ({callback.ModId}).",
                $"The {ModHealthReportLabels.GetOperation(callback.Operation)} callback peaked at {callback.MaximumMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms.",
                "Reproduce again and compare with the normal SMAPI log before changing the mod setup.",
                "Elapsed callback time is wall-clock correlation at an SMAPI boundary, not proof of root cause."
            ));
        }

        if (!report.Capture.IsShortSample && report.Performance.SlowUpdateCount >= ModHealthReportLimits.RepeatedSlowUpdateCount)
        {
            findings.Add(new(
                "repeated-slow-updates",
                ModHealthFindingSeverity.Performance,
                ModHealthFindingConfidence.Factual,
                null,
                "SMAPI recorded repeated slow update ticks.",
                $"{report.Performance.SlowUpdateCount.ToString(CultureInfo.InvariantCulture)} update ticks met the {report.Capture.SlowUpdateThresholdMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms threshold.",
                "Compare the slow episodes, observed callback contributors, and unattributed time below.",
                "Update ticks are not a complete presentation-rate measurement, and a slow update can include work SMAPI cannot attribute."
            ));
        }

        if (!report.Capture.IsShortSample)
        {
            this.AddDominanceFinding(report, findings);
            this.AddUnattributedFinding(report, findings);
        }

        if (report.Capture.IsShortSample)
        {
            findings.Add(new(
                "short-sample",
                ModHealthFindingSeverity.Info,
                ModHealthFindingConfidence.Factual,
                null,
                "This timing sample is short.",
                $"It contains {report.Capture.CompletedUpdateCount.ToString(CultureInfo.InvariantCulture)} completed update ticks across {report.Capture.DurationMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms.",
                "Record for at least 30 seconds and 600 update ticks when checking a sustained problem.",
                "Direct failures and individual peaks remain factual, but the absence of a finding does not show that no issue exists."
            ));
        }

        if (report.Capacities.Any(capacity => capacity.Reached) || report.Omissions.Any(omission => omission.Count > 0))
        {
            findings.Add(new(
                "capacity-reached",
                ModHealthFindingSeverity.Check,
                ModHealthFindingConfidence.Factual,
                null,
                "Some report detail was omitted by a safety limit.",
                "One or more bounded collections reached capacity or were pruned for output size.",
                "Use the aggregate counts and omissions section when interpreting this report.",
                "Omitted detail can hide individual examples, so conclusions should remain cautious."
            ));
        }

        if (findings.Count == 0)
        {
            findings.Add(report.Capture.Mode == ModHealthCaptureMode.LedgerOnly
                ? new(
                    "ledger-only",
                    ModHealthFindingSeverity.Info,
                    ModHealthFindingConfidence.Limited,
                    null,
                    "No direct mod issue was recorded in the session ledger.",
                    "No deep timing sample was available, and the configured direct-failure, error-volume, and logging thresholds were not reached.",
                    "If the problem continues, enter 'health start', reproduce it, then enter 'health stop'.",
                    "Ledger-only evidence cannot support timing conclusions, and the absence of a finding does not show that no issue exists."
                )
                : new(
                    "no-clear-observed-issue",
                    ModHealthFindingSeverity.Info,
                    ModHealthFindingConfidence.Limited,
                    null,
                    "No clear mod-owned issue was observed during this capture.",
                    "The configured direct-failure, error-volume, logging, and timing thresholds were not reached.",
                    "If the problem continues, reproduce for longer and share the health text report with the normal SMAPI log after inspecting both.",
                    "SMAPI cannot observe Harmony bodies, arbitrary background work, native code, GPU work, I/O waits, or operating-system scheduling."
                ));
        }

        return findings
            .Select(ModHealthInsightAnalyzer.NormalizeFinding)
            .OrderBy(finding => ModHealthInsightAnalyzer.GetSeverityOrder(finding.Severity))
            .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
            .ThenBy(finding => finding.ModId, StringComparer.OrdinalIgnoreCase)
            .Take(ModHealthReportLimits.MaxFindings)
            .ToImmutableArray();
    }

    private static ModHealthFinding NormalizeFinding(ModHealthFinding finding)
    {
        return finding with
        {
            RuleId = ModHealthTextSanitizer.SanitizeIdentity(finding.RuleId),
            ModId = finding.ModId is null ? null : ModHealthTextSanitizer.SanitizeIdentity(finding.ModId),
            Summary = ModHealthTextSanitizer.SanitizeIdentity(finding.Summary),
            Evidence = ModHealthTextSanitizer.SanitizeIdentity(finding.Evidence, ModHealthReportLimits.MaxCallbackNameLength),
            SuggestedAction = ModHealthTextSanitizer.SanitizeIdentity(finding.SuggestedAction, ModHealthReportLimits.MaxCallbackNameLength),
            Limitation = ModHealthTextSanitizer.SanitizeIdentity(finding.Limitation, ModHealthReportLimits.MaxCallbackNameLength)
        };
    }

    private void AddDominanceFinding(ModHealthReport report, List<ModHealthFinding> findings)
    {
        ModHealthUpdate[] validSlowUpdates = report.Performance.WorstUpdates
            .Where(update => update.TimingValid && update.TotalMilliseconds >= report.Capture.SlowUpdateThresholdMilliseconds)
            .ToArray();
        if (validSlowUpdates.Length < ModHealthReportLimits.RepeatedSlowUpdateCount)
            return;

        var leading = validSlowUpdates
            .Where(update => update.Contributors.Length > 0)
            .Select(update => update.Contributors.OrderByDescending(contributor => contributor.Milliseconds).ThenBy(contributor => contributor.ModId, StringComparer.OrdinalIgnoreCase).First())
            .GroupBy(contributor => contributor.ModId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { ModId = group.Key, Count = group.Count() })
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.ModId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (leading == null || leading.Count * 2 < validSlowUpdates.Length)
            return;

        ModHealthUpdate[] ledUpdates = validSlowUpdates
            .Where(update => update.Contributors.Length > 0 && update.Contributors.OrderByDescending(contributor => contributor.Milliseconds).ThenBy(contributor => contributor.ModId, StringComparer.OrdinalIgnoreCase).First().ModId.Equals(leading.ModId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        double modMilliseconds = validSlowUpdates.Sum(update => update.Contributors
            .Where(contributor => contributor.ModId.Equals(leading.ModId, StringComparison.OrdinalIgnoreCase))
            .Sum(contributor => contributor.Milliseconds));
        double instrumentedMilliseconds = validSlowUpdates.Sum(update => update.ObservedModMilliseconds);
        if (instrumentedMilliseconds <= 0 || modMilliseconds / instrumentedMilliseconds < ModHealthReportLimits.DominantInstrumentedShare)
            return;

        double totalMilliseconds = validSlowUpdates.Sum(update => update.TotalMilliseconds);
        ModHealthFindingConfidence confidence = totalMilliseconds > 0 && instrumentedMilliseconds / totalMilliseconds >= ModHealthReportLimits.SufficientInstrumentedShare
            ? ModHealthFindingConfidence.Likely
            : ModHealthFindingConfidence.Possible;
        string modName = report.Mods.FirstOrDefault(mod => mod.Id.Equals(leading.ModId, StringComparison.OrdinalIgnoreCase))?.Name ?? leading.ModId;
        findings.Add(new(
            "observed-mod-dominance",
            ModHealthFindingSeverity.Performance,
            confidence,
            leading.ModId,
            $"{modName} ({leading.ModId}) was often the largest callback contributor SMAPI observed.",
            $"It was largest in {ledUpdates.Length.ToString(CultureInfo.InvariantCulture)} of {validSlowUpdates.Length.ToString(CultureInfo.InvariantCulture)} retained slow update ticks and represented {modMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms of observed callback work there.",
            "Reproduce again and compare with the normal SMAPI log before temporarily testing without the mod; back up first and check dependencies and save implications.",
            "This correlation covers only the callback work SMAPI observed; the remaining time has other or unknown ownership."
        ));
    }

    private void AddUnattributedFinding(ModHealthReport report, List<ModHealthFinding> findings)
    {
        ModHealthUpdate[] slow = report.Performance.WorstUpdates
            .Where(update => update.TimingValid && update.TotalMilliseconds >= report.Capture.SlowUpdateThresholdMilliseconds)
            .ToArray();
        if (slow.Length < ModHealthReportLimits.RepeatedSlowUpdateCount)
            return;

        double total = slow.Sum(update => update.TotalMilliseconds);
        double unattributed = slow.Sum(update => update.ResidualMilliseconds + update.BaseGameExclusiveMilliseconds + update.SmapiOtherMilliseconds);
        if (total <= 0 || unattributed / total < ModHealthReportLimits.MostlyUnattributedShare)
            return;

        findings.Add(new(
            "mostly-unattributed-slow-updates",
            ModHealthFindingSeverity.Performance,
            ModHealthFindingConfidence.Factual,
            null,
            "Most time in the retained slow update ticks was outside observed mod callbacks.",
            $"SMAPI could not attribute {unattributed.ToString("0.###", CultureInfo.InvariantCulture)} of {total.ToString("0.###", CultureInfo.InvariantCulture)} ms to observed mod callback boundaries.",
            "Use the normal SMAPI log and an external process or system profiler if this pattern continues.",
            "Unattributed time can include the game, SMAPI, Harmony patches, background/native work, waits, GC correlation, GPU/driver work, and operating-system scheduling."
        ));
    }

    private static int GetSeverityOrder(ModHealthFindingSeverity severity) => severity switch
    {
        ModHealthFindingSeverity.ActionNeeded => 0,
        ModHealthFindingSeverity.Performance => 1,
        ModHealthFindingSeverity.Check => 2,
        _ => 3
    };

    private static string GetStatusText(ModHealthModStatus status) => status switch
    {
        ModHealthModStatus.Failed => "failed",
        ModHealthModStatus.Invalid => "invalid",
        ModHealthModStatus.Skipped => "skipped",
        ModHealthModStatus.Ignored => "ignored",
        _ => "loaded"
    };
}
