using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace StardewModdingAPI.Framework.Health.Presentation;

/// <summary>Projects one final, sanitized and pruned report payload into a bounded immutable UI model.</summary>
internal sealed class ModHealthReportPresentationMapper
{
    /// <summary>Map a completed report payload.</summary>
    public ModHealthReportPresentation Map(ModHealthReportPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return this.Map(payload.Model);
    }

    /// <summary>Map the exact immutable report model published by the prepared-report store.</summary>
    public ModHealthReportPresentation Map(ModHealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        bool hasTimingSample = report.Capture.Mode != ModHealthCaptureMode.LedgerOnly;
        bool timingValid = hasTimingSample && report.Capture.TimingValid;

        ImmutableArray<string>.Builder suggestedActions = ImmutableArray.CreateBuilder<string>();
        HashSet<string> seenActions = new(StringComparer.Ordinal);
        foreach (ModHealthFinding finding in report.Findings)
        {
            if (seenActions.Add(finding.SuggestedAction))
                suggestedActions.Add(finding.SuggestedAction);
        }

        ImmutableArray<ModHealthOmission> positiveOmissions = report.Omissions
            .Where(omission => omission.Count > 0)
            .ToImmutableArray();
        ImmutableArray<ModHealthMod> observedMods = report.Mods
            .Where(mod => mod.ObservedCallbackCount > 0)
            .OrderByDescending(mod => mod.ObservedCallbackMilliseconds)
            .ThenByDescending(mod => mod.ObservedCallbackPeakMilliseconds)
            .ThenBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(mod => mod.Id, StringComparer.Ordinal)
            .ToImmutableArray();

        Func<ModHealthMod, ModHealthModPresentation> mapMod = mod => MapMod(mod, hasTimingSample, timingValid);
        Func<ModHealthUpdate, ModHealthUpdatePresentation> mapUpdate = update => MapUpdate(update, report.Capture.Mode, report.Capture.TimingValid);

        return new(
            report.SchemaVersion,
            new(report.Header, report.Privacy, ModHealthPresentationText.PrivacyNotices),
            new(report.Findings, suggestedActions.ToImmutable()),
            new(report.Capture, report.Header.IsTruncated, report.Header.IsMinimalFallback, report.Header.WriteRetry, positiveOmissions),
            new(ModHealthVirtualRowSource<ModHealthMod, ModHealthModPresentation>.Where(report.Mods, IsProblemMod, mapMod)),
            new(
                report.Performance.Histogram,
                MapAggregateValue(report.Performance.TotalObservedModMilliseconds, report.Capture.Mode, report.Capture.TimingValid),
                MapAggregateValue(report.Performance.TotalBaseGameExclusiveMilliseconds, report.Capture.Mode, report.Capture.TimingValid),
                MapSmapiValue(report.Performance.TotalSmapiOtherMilliseconds, report.Capture.Mode, report.Capture.TimingValid, report.Performance.SmapiOtherTimingAvailable),
                MapAggregateValue(report.Performance.TotalResidualMilliseconds, report.Capture.Mode, report.Capture.TimingValid),
                timingValid,
                report.Performance.SlowUpdateCount,
                MapGc(report.Performance.GcCollectionDataValid, report.Performance.Gen0Collections, report.Performance.Gen1Collections, report.Performance.Gen2Collections, hasTimingSample),
                ModHealthPresentationText.TimingCaveats,
                new(observedMods, mapMod),
                new(report.Performance.Callbacks, static callback => callback),
                new(report.Performance.Episodes, static episode => episode),
                new(report.Performance.WorstUpdates, mapUpdate),
                new(report.Performance.RecentUpdates, mapUpdate)
            ),
            new(
                report.LogTotals,
                report.CallbackFailureTotals,
                new(report.Logs, static log => log),
                new(report.CallbackFailures, static failure => failure)
            ),
            new(report.ModInventory, new(report.Mods, mapMod)),
            new(report.Environment, report.Completeness, report.Capacities, report.Omissions, report.Limitations)
        );
    }

    private static bool IsProblemMod(ModHealthMod mod)
    {
        // Include the analyzer/pruner problem conditions plus explicit warning/update states
        // which the presentation labels as needing user attention.
        return mod.Status != ModHealthModStatus.Loaded
            || mod.SessionWarningCount > 0
            || mod.SessionErrorCount > 0
            || mod.CaptureErrorCount > 0
            || mod.CallbackFailureCount > 0
            || !mod.WarningFlags.IsEmpty
            || mod.UpdateStatus == ModHealthReportUpdateStatus.UpdateAvailable;
    }

    private static ModHealthModPresentation MapMod(ModHealthMod mod, bool hasTimingSample, bool timingValid)
    {
        ModHealthMeasuredRatio share = !hasTimingSample
            ? ModHealthMeasuredRatio.NotApplicable()
            : timingValid
                ? ModHealthMeasuredRatio.Measured(mod.InstrumentedTimeShare)
                : ModHealthMeasuredRatio.Invalid();
        return new(
            mod.Id,
            mod.Name,
            mod.Version,
            mod.Kind,
            mod.ParentId,
            mod.Status,
            mod.FailureCategory,
            mod.WarningFlags,
            mod.Dependencies,
            mod.UpdateStatus,
            mod.SuggestedUpdateVersion,
            mod.SessionWarningCount,
            mod.SessionErrorCount,
            mod.CaptureErrorCount,
            mod.CallbackFailureCount,
            mod.ObservedCallbackMilliseconds,
            mod.ObservedCallbackPeakMilliseconds,
            mod.ObservedCallbackCount,
            mod.ObservedCallbackFailureCount,
            mod.SlowUpdateParticipationCount,
            share,
            mod.PeakMessagesPerSecond,
            mod.PeakCharactersPerSecond
        );
    }

    private static ModHealthUpdatePresentation MapUpdate(ModHealthUpdate update, ModHealthCaptureMode mode, bool aggregateTimingValid)
    {
        bool hasTimingSample = mode != ModHealthCaptureMode.LedgerOnly;
        bool timingValid = hasTimingSample && aggregateTimingValid && update.TimingValid;
        return new(
            update.UpdateTick,
            update.OffsetMilliseconds,
            update.TotalMilliseconds,
            MapUpdateValue(update.BaseGameExclusiveMilliseconds, hasTimingSample, timingValid),
            MapUpdateValue(update.ObservedModMilliseconds, hasTimingSample, timingValid),
            !hasTimingSample
                ? ModHealthMeasuredMilliseconds.NotApplicable()
                : !timingValid
                    ? ModHealthMeasuredMilliseconds.Invalid()
                    : update.SmapiOtherTimingAvailable
                        ? ModHealthMeasuredMilliseconds.Measured(update.SmapiOtherMilliseconds)
                        : ModHealthMeasuredMilliseconds.Unavailable(),
            MapUpdateValue(update.ResidualMilliseconds, hasTimingSample, timingValid),
            timingValid,
            update.Phase,
            update.Focused,
            update.Screen,
            update.WarningCount,
            update.ErrorCount,
            update.CallbackFailureCount,
            MapGc(update.GcCollectionDataValid, update.Gen0Collections, update.Gen1Collections, update.Gen2Collections, hasTimingSample),
            update.Contributors,
            update.NearbyMark
        );
    }

    private static ModHealthMeasuredMilliseconds MapAggregateValue(double value, ModHealthCaptureMode mode, bool timingValid)
    {
        if (mode == ModHealthCaptureMode.LedgerOnly)
            return ModHealthMeasuredMilliseconds.NotApplicable();
        return timingValid ? ModHealthMeasuredMilliseconds.Measured(value) : ModHealthMeasuredMilliseconds.Invalid();
    }

    private static ModHealthMeasuredMilliseconds MapSmapiValue(double value, ModHealthCaptureMode mode, bool timingValid, bool available)
    {
        ModHealthMeasuredMilliseconds aggregate = MapAggregateValue(value, mode, timingValid);
        return aggregate.State == ModHealthEvidenceState.Measured && !available
            ? ModHealthMeasuredMilliseconds.Unavailable()
            : aggregate;
    }

    private static ModHealthMeasuredMilliseconds MapUpdateValue(double value, bool hasTimingSample, bool timingValid)
    {
        if (!hasTimingSample)
            return ModHealthMeasuredMilliseconds.NotApplicable();
        return timingValid ? ModHealthMeasuredMilliseconds.Measured(value) : ModHealthMeasuredMilliseconds.Invalid();
    }

    private static ModHealthGcPresentation MapGc(bool valid, long gen0, long gen1, long gen2, bool hasTimingSample)
    {
        if (!hasTimingSample)
            return new(ModHealthEvidenceState.NotApplicable, 0, 0, 0);
        return valid
            ? new(ModHealthEvidenceState.Measured, gen0, gen1, gen2)
            : new(ModHealthEvidenceState.Unavailable, 0, 0, 0);
    }
}
