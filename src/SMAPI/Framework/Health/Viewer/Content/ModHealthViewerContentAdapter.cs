using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using StardewModdingAPI.Framework.Health.Presentation;
using StardewModdingAPI.Framework.Health.Viewer.Layout;

namespace StardewModdingAPI.Framework.Health.Viewer.Content;

/// <summary>Formats a finalized report presentation into eight bounded, renderer-independent sections.</summary>
internal sealed class ModHealthViewerContentAdapter
{
    public const int MaxPageSize = ModHealthVirtualRowSource<ModHealthMod, ModHealthModPresentation>.MaxPageSize;
    public const int MaxSummaryTitleCharacters = 160;
    public const int MaxSummaryDetailCharacters = 360;

    private readonly ModHealthReportPresentation Report;
    private readonly Func<string, string> Translate;

    /// <summary>Create the adapter, optionally resolving adapter-owned visible text from stable translation keys.</summary>
    /// <param name="report">The exact presentation model to render.</param>
    /// <param name="translate">Resolve a translation key, or <see langword="null"/> to use the built-in English fallbacks.</param>
    public ModHealthViewerContentAdapter(ModHealthReportPresentation report, Func<string, string>? translate = null)
    {
        this.Report = report ?? throw new ArgumentNullException(nameof(report));
        this.Translate = translate ?? (static key => key);
    }

    /// <summary>Get the total display row count for a section without projecting its virtual rows.</summary>
    public int GetRowCount(ModHealthViewerSection section)
    {
        return section switch
        {
            ModHealthViewerSection.Overview => Add(2, this.Report.Overview.PrivacyNotices.Length),
            ModHealthViewerSection.Findings => Add(Math.Max(1, this.Report.Findings.Rows.Length), this.Report.Findings.SuggestedActions.Length),
            ModHealthViewerSection.Capture => Add(2, this.Report.Capture.Details.Marks.Length, this.Report.Capture.PositiveOmissions.Length),
            ModHealthViewerSection.Attention => this.Report.Attention.Mods.Count,
            ModHealthViewerSection.Performance => Add(
                7,
                this.Report.Performance.AttributionCaveats.Length,
                this.Report.Performance.ObservedMods.Count,
                this.Report.Performance.Callbacks.Count,
                this.Report.Performance.Episodes.Count,
                this.Report.Performance.WorstUpdates.Count,
                this.Report.Performance.RecentUpdates.Count
            ),
            ModHealthViewerSection.Errors => Add(2, this.Report.Errors.Logs.Count, this.Report.Errors.CallbackFailures.Count),
            ModHealthViewerSection.Inventory => Add(1, this.Report.Inventory.Mods.Count),
            ModHealthViewerSection.Context => Add(
                2,
                this.Report.Context.Capacities.Length,
                this.Report.Context.Omissions.Length,
                this.Report.Context.Limitations.Length
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(section), section, "Unknown health-report section.")
        };
    }

    /// <summary>Materialize a clamped page of at most <see cref="MaxPageSize"/> display rows.</summary>
    public ImmutableArray<ModHealthViewerDisplayRow> GetPage(ModHealthViewerSection section, int offset, int count)
    {
        int rowCount = this.GetRowCount(section);
        int start = Math.Clamp(offset, 0, rowCount);
        int take = Math.Min(Math.Clamp(count, 0, MaxPageSize), rowCount - start);
        if (take == 0)
            return ImmutableArray<ModHealthViewerDisplayRow>.Empty;

        ImmutableArray<ModHealthViewerDisplayRow>.Builder rows = ImmutableArray.CreateBuilder<ModHealthViewerDisplayRow>(take);
        this.AppendSummaryRows(rows, section, start, take);
        return rows.MoveToImmutable();
    }

    /// <summary>Get the number of bounded label/value details for one selected summary row.</summary>
    public int GetDetailRowCount(ModHealthViewerSection section, int rowIndex)
    {
        this.ValidateRowIndex(section, rowIndex);
        return this.CreateDetailSource(section, rowIndex).Count;
    }

    /// <summary>Materialize a clamped page of details for one selected summary row.</summary>
    public ImmutableArray<ModHealthViewerDetailRow> GetDetailPage(ModHealthViewerSection section, int rowIndex, int offset, int count)
    {
        this.ValidateRowIndex(section, rowIndex);
        DetailSource source = this.CreateDetailSource(section, rowIndex);
        int start = Math.Clamp(offset, 0, source.Count);
        int take = Math.Min(Math.Clamp(count, 0, MaxPageSize), source.Count - start);
        if (take == 0)
            return ImmutableArray<ModHealthViewerDetailRow>.Empty;

        ImmutableArray<ModHealthViewerDetailRow>.Builder rows = ImmutableArray.CreateBuilder<ModHealthViewerDetailRow>(take);
        for (int index = start; index < start + take; index++)
            rows.Add(source.GetRow(index));
        return rows.MoveToImmutable();
    }

    private void ValidateRowIndex(ModHealthViewerSection section, int rowIndex)
    {
        int count = this.GetRowCount(section);
        if ((uint)rowIndex >= (uint)count)
            throw new ArgumentOutOfRangeException(nameof(rowIndex), rowIndex, "The selected health-report row doesn't exist.");
    }

    private void AppendSummaryRows(ImmutableArray<ModHealthViewerDisplayRow>.Builder rows, ModHealthViewerSection section, int start, int count)
    {
        int end = start + count;
        int cursor = start;
        while (cursor < end)
        {
            if (section == ModHealthViewerSection.Attention)
            {
                this.AppendModPage(rows, this.Report.Attention.Mods, cursor, end - cursor, attention: true, "mod");
                return;
            }
            if (section == ModHealthViewerSection.Inventory && cursor > 0)
            {
                this.AppendModPage(rows, this.Report.Inventory.Mods, cursor - 1, end - cursor, attention: false, "mod");
                return;
            }
            if (section == ModHealthViewerSection.Errors && cursor >= 2)
            {
                int relative = cursor - 2;
                if (relative < this.Report.Errors.Logs.Count)
                {
                    int take = Math.Min(end - cursor, this.Report.Errors.Logs.Count - relative);
                    ImmutableArray<ModHealthLogSummary> page = this.Report.Errors.Logs.GetPage(relative, take);
                    for (int index = 0; index < page.Length; index++)
                        rows.Add(ClipSummary(this.FormatLog(page[index], relative + index)));
                    cursor += take;
                    continue;
                }

                relative -= this.Report.Errors.Logs.Count;
                int failureTake = Math.Min(end - cursor, this.Report.Errors.CallbackFailures.Count - relative);
                ImmutableArray<ModHealthCallbackFailure> failures = this.Report.Errors.CallbackFailures.GetPage(relative, failureTake);
                for (int index = 0; index < failures.Length; index++)
                    rows.Add(ClipSummary(this.FormatFailure(failures[index], relative + index)));
                cursor += failureTake;
                continue;
            }
            if (section == ModHealthViewerSection.Performance && cursor >= 7 + this.Report.Performance.AttributionCaveats.Length)
            {
                int relative = cursor - 7 - this.Report.Performance.AttributionCaveats.Length;
                int consumed = this.AppendPerformanceVirtualRows(rows, relative, end - cursor);
                cursor += consumed;
                continue;
            }

            rows.Add(ClipSummary(this.GetRow(section, cursor)));
            cursor++;
        }
    }

    private int AppendPerformanceVirtualRows(ImmutableArray<ModHealthViewerDisplayRow>.Builder rows, int relative, int requested)
    {
        ModHealthPerformancePresentation performance = this.Report.Performance;
        if (relative < performance.ObservedMods.Count)
            return this.AppendModPage(rows, performance.ObservedMods, relative, requested, attention: false, "observed-mod");
        relative -= performance.ObservedMods.Count;

        if (relative < performance.Callbacks.Count)
        {
            int take = Math.Min(requested, performance.Callbacks.Count - relative);
            ImmutableArray<ModHealthCallback> page = performance.Callbacks.GetPage(relative, take);
            for (int index = 0; index < page.Length; index++)
                rows.Add(ClipSummary(this.FormatCallback(page[index], relative + index)));
            return take;
        }
        relative -= performance.Callbacks.Count;

        if (relative < performance.Episodes.Count)
        {
            int take = Math.Min(requested, performance.Episodes.Count - relative);
            ImmutableArray<ModHealthEpisode> page = performance.Episodes.GetPage(relative, take);
            for (int index = 0; index < page.Length; index++)
                rows.Add(ClipSummary(this.FormatEpisode(page[index], relative + index)));
            return take;
        }
        relative -= performance.Episodes.Count;

        if (relative < performance.WorstUpdates.Count)
        {
            int take = Math.Min(requested, performance.WorstUpdates.Count - relative);
            ImmutableArray<ModHealthUpdatePresentation> page = performance.WorstUpdates.GetPage(relative, take);
            foreach (ModHealthUpdatePresentation update in page)
                rows.Add(ClipSummary(this.FormatUpdate(update, "health-view.content.summary.worst-update.title", "Worst update {0}", "worst-update")));
            return take;
        }
        relative -= performance.WorstUpdates.Count;

        int recentTake = Math.Min(requested, performance.RecentUpdates.Count - relative);
        ImmutableArray<ModHealthUpdatePresentation> recent = performance.RecentUpdates.GetPage(relative, recentTake);
        foreach (ModHealthUpdatePresentation update in recent)
            rows.Add(ClipSummary(this.FormatUpdate(update, "health-view.content.summary.recent-update.title", "Recent update {0}", "recent-update")));
        return recentTake;
    }

    private int AppendModPage(ImmutableArray<ModHealthViewerDisplayRow>.Builder rows, ModHealthVirtualRowSource<ModHealthMod, ModHealthModPresentation> source, int offset, int requested, bool attention, string stablePrefix)
    {
        int take = Math.Min(requested, source.Count - offset);
        ImmutableArray<ModHealthModPresentation> page = source.GetPage(offset, take);
        foreach (ModHealthModPresentation mod in page)
            rows.Add(ClipSummary(this.FormatMod(mod, attention, stablePrefix)));
        return take;
    }

    private ModHealthViewerDisplayRow GetRow(ModHealthViewerSection section, int index)
    {
        return section switch
        {
            ModHealthViewerSection.Overview => this.GetOverviewRow(index),
            ModHealthViewerSection.Findings => this.GetFindingRow(index),
            ModHealthViewerSection.Capture => this.GetCaptureRow(index),
            ModHealthViewerSection.Attention => this.FormatMod(GetOne(this.Report.Attention.Mods, index), attention: true),
            ModHealthViewerSection.Performance => this.GetPerformanceRow(index),
            ModHealthViewerSection.Errors => this.GetErrorsRow(index),
            ModHealthViewerSection.Inventory => this.GetInventoryRow(index),
            ModHealthViewerSection.Context => this.GetContextRow(index),
            _ => throw new ArgumentOutOfRangeException(nameof(section), section, "Unknown health-report section.")
        };
    }

    private ModHealthViewerDisplayRow GetOverviewRow(int index)
    {
        ModHealthOverviewPresentation overview = this.Report.Overview;
        if (index == 0)
        {
            return new(
                this.F("health-view.content.summary.report.title", "Mod Health Report {0}", overview.Header.ReportId),
                this.F("health-view.content.summary.report.detail", "Schema {0}; generated {1}; truncated: {2}; minimal fallback: {3}; write retry: {4}.", N(this.Report.SchemaVersion), Utc(overview.Header.GeneratedUtc), this.YesNo(overview.Header.IsTruncated), this.YesNo(overview.Header.IsMinimalFallback), this.YesNo(overview.Header.WriteRetry)),
                overview.Header.IsTruncated || overview.Header.IsMinimalFallback ? ModHealthViewerRowSeverity.Warning : ModHealthViewerRowSeverity.Neutral,
                ModHealthViewerRowIconKey.Report,
                overview.Header.ReportId
            );
        }
        if (index == 1)
        {
            return new(
                this.T("health-view.content.summary.privacy.title", "Privacy summary"),
                this.F("health-view.content.summary.privacy.detail", "Inspect before sharing: {0}; automatic upload: {1}; included identity field groups: {2}; excluded source groups: {3}.", this.YesNo(overview.Privacy.InspectBeforeSharing), this.YesNo(overview.Privacy.AutomaticUpload), N(overview.Privacy.IncludedIdentityFields.Length), N(overview.Privacy.ExcludedSources.Length)),
                ModHealthViewerRowSeverity.Warning,
                ModHealthViewerRowIconKey.Privacy,
                "privacy-summary"
            );
        }

        int notice = index - 2;
        return new(
            this.T("health-view.content.summary.privacy-notice.title", "Privacy and sharing"),
            overview.PrivacyNotices[notice],
            ModHealthViewerRowSeverity.Info,
            ModHealthViewerRowIconKey.Privacy,
            $"privacy-notice-{N(notice + 1)}"
        );
    }

    private ModHealthViewerDisplayRow GetFindingRow(int index)
    {
        int findingRows = Math.Max(1, this.Report.Findings.Rows.Length);
        if (index < findingRows)
        {
            if (this.Report.Findings.Rows.Length == 0)
            {
                return new(
                    this.T("health-view.content.summary.no-findings.title", "No findings were generated"),
                    this.T("health-view.content.summary.no-findings.detail", "SMAPI did not generate a finding from the evidence retained in this report."),
                    ModHealthViewerRowSeverity.Positive,
                    ModHealthViewerRowIconKey.Finding,
                    "finding-none"
                );
            }
            return this.FormatFinding(this.Report.Findings.Rows[index], index);
        }

        int actionIndex = index - findingRows;
        return new(
            this.F("health-view.content.summary.suggested-action.title", "Suggested next step {0}", N(actionIndex + 1)),
            this.Report.Findings.SuggestedActions[actionIndex],
            ModHealthViewerRowSeverity.Info,
            ModHealthViewerRowIconKey.Finding,
            $"suggested-action-{N(actionIndex + 1)}"
        );
    }

    private ModHealthViewerDisplayRow GetCaptureRow(int index)
    {
        ModHealthCapturePresentation capture = this.Report.Capture;
        ModHealthCapture details = capture.Details;
        if (index == 0)
        {
            return new(
                this.T("health-view.content.summary.capture.title", "Capture"),
                this.F("health-view.content.summary.capture.detail", "Mode: {0}; completion: {1}; started: {2}; ended: {3}; duration: {4}; completed updates: {5}; slow-update threshold: {6}.", this.CaptureMode(details.Mode), this.Completion(details.CompletionReason), this.OptionalUtc(details.StartedUtc), this.OptionalUtc(details.EndedUtc), Ms(details.DurationMilliseconds), N(details.CompletedUpdateCount), Ms(details.SlowUpdateThresholdMilliseconds)),
                details.Mode == ModHealthCaptureMode.LedgerOnly ? ModHealthViewerRowSeverity.Info : ModHealthViewerRowSeverity.Neutral,
                ModHealthViewerRowIconKey.Capture,
                "capture-summary"
            );
        }
        if (index == 1)
        {
            bool warning = details.IsShortSample || !details.TimingValid || capture.IsTruncated || capture.IsMinimalFallback || capture.WriteRetry || capture.PositiveOmissions.Length > 0;
            return new(
                this.T("health-view.content.summary.capture-quality.title", "Capture quality"),
                this.F("health-view.content.summary.capture-quality.detail", "Short sample: {0}; timing valid: {1}; truncated: {2}; minimal fallback: {3}; write retry: {4}; marks: {5}; positive omissions: {6}.", this.YesNo(details.IsShortSample), this.YesNo(details.TimingValid), this.YesNo(capture.IsTruncated), this.YesNo(capture.IsMinimalFallback), this.YesNo(capture.WriteRetry), N(details.Marks.Length), N(capture.PositiveOmissions.Length)),
                warning ? ModHealthViewerRowSeverity.Warning : ModHealthViewerRowSeverity.Positive,
                ModHealthViewerRowIconKey.Capture,
                "capture-quality"
            );
        }

        int relative = index - 2;
        if (relative < details.Marks.Length)
        {
            ModHealthMark mark = details.Marks[relative];
            return new(
                this.F("health-view.content.summary.mark.title", "Mark {0}", N(mark.Number)),
                this.F("health-view.content.summary.mark.detail", "Update tick {0} at {1} after capture start.", N(mark.UpdateTick), Ms(mark.OffsetMilliseconds)),
                ModHealthViewerRowSeverity.Info,
                ModHealthViewerRowIconKey.Mark,
                $"mark-{N(mark.Number)}"
            );
        }

        ModHealthOmission omission = capture.PositiveOmissions[relative - details.Marks.Length];
        return this.FormatOmission(omission, "capture-omission");
    }

    private ModHealthViewerDisplayRow GetPerformanceRow(int index)
    {
        ModHealthPerformancePresentation performance = this.Report.Performance;
        if (index == 0)
        {
            ModHealthHistogram histogram = performance.Histogram;
            return new(
                this.T("health-view.content.summary.timing.title", "Completed update timing"),
                this.F("health-view.content.summary.timing.detail", "Count: {0}; mean: {1}; minimum: {2}; maximum: {3}; p50/p95/p99: {4} / {5} / {6}.", N(histogram.Count), this.SafeAverage(histogram.SumMilliseconds, histogram.Count), this.OptionalMs(histogram.MinimumMilliseconds), this.OptionalMs(histogram.MaximumMilliseconds), this.OptionalMs(histogram.P50Milliseconds), this.OptionalMs(histogram.P95Milliseconds), this.OptionalMs(histogram.P99Milliseconds)),
                ModHealthViewerRowSeverity.Neutral,
                ModHealthViewerRowIconKey.Timing,
                "performance-histogram"
            );
        }
        if (index == 1)
            return this.FormatEvidence("health-view.content.summary.observed-callbacks.title", "Observed mod callbacks", performance.ObservedCallbacks, "performance-observed-callbacks");
        if (index == 2)
            return this.FormatEvidence("health-view.content.summary.base-game-exclusive.title", "Base-game-exclusive update time", performance.BaseGameExclusive, "performance-base-game-exclusive");
        if (index == 3)
            return this.FormatEvidence("health-view.content.summary.smapi-dispatch.title", ModHealthPresentationText.SmapiUpdateDispatchLabel, performance.SmapiUpdateDispatch, "performance-smapi-update-dispatch");
        if (index == 4)
            return this.FormatEvidence("health-view.content.summary.residual.title", "Residual update time", performance.Residual, "performance-residual");
        if (index == 5)
        {
            return new(
                this.T("health-view.content.summary.slow-updates.title", "Slow updates"),
                this.F("health-view.content.summary.slow-updates.detail", "{0} completed updates met the report's slow-update threshold.", N(performance.SlowUpdateCount)),
                performance.SlowUpdateCount > 0 ? ModHealthViewerRowSeverity.Warning : ModHealthViewerRowSeverity.Positive,
                ModHealthViewerRowIconKey.Timing,
                "performance-slow-updates"
            );
        }
        if (index == 6)
            return this.FormatGc(performance.Gc, "performance-gc");

        int relative = index - 7;
        if (relative < performance.AttributionCaveats.Length)
        {
            return new(
                this.T("health-view.content.summary.timing-limitation.title", "Timing limitation"),
                performance.AttributionCaveats[relative],
                ModHealthViewerRowSeverity.Info,
                ModHealthViewerRowIconKey.Limitation,
                $"timing-limitation-{N(relative + 1)}"
            );
        }
        relative -= performance.AttributionCaveats.Length;

        if (relative < performance.ObservedMods.Count)
            return this.FormatMod(GetOne(performance.ObservedMods, relative), attention: false, "observed-mod");
        relative -= performance.ObservedMods.Count;

        if (relative < performance.Callbacks.Count)
            return this.FormatCallback(GetOne(performance.Callbacks, relative), relative);
        relative -= performance.Callbacks.Count;

        if (relative < performance.Episodes.Count)
            return this.FormatEpisode(GetOne(performance.Episodes, relative), relative);
        relative -= performance.Episodes.Count;

        if (relative < performance.WorstUpdates.Count)
            return this.FormatUpdate(GetOne(performance.WorstUpdates, relative), "health-view.content.summary.worst-update.title", "Worst update {0}", "worst-update");
        relative -= performance.WorstUpdates.Count;

        return this.FormatUpdate(GetOne(performance.RecentUpdates, relative), "health-view.content.summary.recent-update.title", "Recent update {0}", "recent-update");
    }

    private ModHealthViewerDisplayRow GetErrorsRow(int index)
    {
        ModHealthErrorsPresentation errors = this.Report.Errors;
        if (index == 0)
        {
            bool hasErrors = HasErrors(errors.LogTotals.SinceLedgerStart) || HasErrors(errors.LogTotals.DuringCapture);
            return new(
                this.T("health-view.content.summary.log-totals.title", "Log totals"),
                this.F("health-view.content.summary.log-totals.detail", "Since ledger start: {0}; during capture: {1}.", this.FormatSeverity(errors.LogTotals.SinceLedgerStart), this.FormatSeverity(errors.LogTotals.DuringCapture)),
                hasErrors ? ModHealthViewerRowSeverity.Error : ModHealthViewerRowSeverity.Neutral,
                ModHealthViewerRowIconKey.Log,
                "log-totals"
            );
        }
        if (index == 1)
        {
            ModHealthCallbackFailureTotals totals = errors.CallbackFailureTotals;
            return new(
                this.T("health-view.content.summary.callback-failures.title", "Callback failure totals"),
                this.F("health-view.content.summary.callback-failures.detail", "Since ledger start: {0}; during capture: {1}.", N(totals.SinceLedgerStart), N(totals.DuringCapture)),
                totals.SinceLedgerStart > 0 || totals.DuringCapture > 0 ? ModHealthViewerRowSeverity.Error : ModHealthViewerRowSeverity.Positive,
                ModHealthViewerRowIconKey.Failure,
                "callback-failure-totals"
            );
        }

        int relative = index - 2;
        if (relative < errors.Logs.Count)
            return this.FormatLog(GetOne(errors.Logs, relative), relative);
        relative -= errors.Logs.Count;
        return this.FormatFailure(GetOne(errors.CallbackFailures, relative), relative);
    }

    private ModHealthViewerDisplayRow GetInventoryRow(int index)
    {
        if (index == 0)
        {
            ModHealthModInventorySummary summary = this.Report.Inventory.Summary;
            bool warning = summary.Skipped > 0 || summary.Ignored > 0 || summary.Invalid > 0 || summary.Failed > 0;
            return new(
                this.T("health-view.content.summary.inventory.title", "Mod inventory"),
                this.F("health-view.content.summary.inventory.detail", "Total discovered: {0}; discovered: {1}; loaded: {2}; skipped: {3}; ignored: {4}; invalid: {5}; failed: {6}; retained records: {7}.", N(summary.TotalDiscovered), N(summary.Discovered), N(summary.Loaded), N(summary.Skipped), N(summary.Ignored), N(summary.Invalid), N(summary.Failed), N(summary.Retained)),
                warning ? ModHealthViewerRowSeverity.Warning : ModHealthViewerRowSeverity.Neutral,
                ModHealthViewerRowIconKey.Inventory,
                "inventory-summary"
            );
        }
        return this.FormatMod(GetOne(this.Report.Inventory.Mods, index - 1), attention: false);
    }

    private ModHealthViewerDisplayRow GetContextRow(int index)
    {
        ModHealthContextPresentation context = this.Report.Context;
        if (index == 0)
        {
            ModHealthEnvironment environment = context.Environment;
            return new(
                this.T("health-view.content.summary.environment.title", "Environment"),
                this.F("health-view.content.summary.environment.detail", "SMAPI {0}; game {1}; runtime {2}; {3}-bit {4}; session {5}.", Clip(environment.SmapiVersion, 64), Clip(environment.GameVersion, 64), Clip(environment.RuntimeVersion, 64), N(environment.ProcessBitness), Clip(environment.ProcessArchitecture, 32), Clip(environment.SessionType, 32)),
                ModHealthViewerRowSeverity.Neutral,
                ModHealthViewerRowIconKey.Environment,
                "environment"
            );
        }
        if (index == 1)
        {
            ModHealthCompleteness completeness = context.Completeness;
            return new(
                this.T("health-view.content.summary.evidence-boundary.title", "Evidence boundary"),
                this.F("health-view.content.summary.evidence-boundary.detail", "Ledger started {0}; startup observed: {1}; lifecycle timing observed: {2}; boundary: {3}", Utc(completeness.LedgerStartedUtc), this.YesNo(completeness.StartupObserved), this.YesNo(completeness.LifecycleTimingObserved), completeness.Boundary),
                completeness.StartupObserved && completeness.LifecycleTimingObserved ? ModHealthViewerRowSeverity.Info : ModHealthViewerRowSeverity.Warning,
                ModHealthViewerRowIconKey.Environment,
                "completeness"
            );
        }

        int relative = index - 2;
        if (relative < context.Capacities.Length)
        {
            ModHealthCapacity capacity = context.Capacities[relative];
            return new(
                this.F("health-view.content.summary.capacity.title", "Capacity: {0}", capacity.Name),
                this.F("health-view.content.summary.capacity.detail", "Limit: {0}; reached: {1}.", N(capacity.Limit), this.YesNo(capacity.Reached)),
                capacity.Reached ? ModHealthViewerRowSeverity.Warning : ModHealthViewerRowSeverity.Neutral,
                ModHealthViewerRowIconKey.Capacity,
                $"capacity-{capacity.Name}"
            );
        }
        relative -= context.Capacities.Length;

        if (relative < context.Omissions.Length)
            return this.FormatOmission(context.Omissions[relative], "context-omission");
        relative -= context.Omissions.Length;

        return new(
            this.T("health-view.content.summary.report-limitation.title", "Report limitation"),
            context.Limitations[relative],
            ModHealthViewerRowSeverity.Info,
            ModHealthViewerRowIconKey.Limitation,
            $"report-limitation-{N(relative + 1)}"
        );
    }

    private ModHealthViewerDisplayRow FormatFinding(ModHealthFinding finding, int index)
    {
        ModHealthViewerRowSeverity severity = finding.Severity switch
        {
            ModHealthFindingSeverity.ActionNeeded => ModHealthViewerRowSeverity.Error,
            ModHealthFindingSeverity.Performance => ModHealthViewerRowSeverity.Warning,
            ModHealthFindingSeverity.Check => ModHealthViewerRowSeverity.Warning,
            _ => ModHealthViewerRowSeverity.Info
        };
        return new(
            finding.Summary,
            this.F("health-view.content.summary.finding.detail", "Rule: {0}; severity: {1}; confidence: {2}; mod: {3}. Select for evidence and next steps.", Clip(finding.RuleId, 64), this.FindingSeverity(finding.Severity), this.Confidence(finding.Confidence), Clip(this.Optional(finding.ModId), 96)),
            severity,
            ModHealthViewerRowIconKey.Finding,
            $"finding-{N(index + 1)}-{finding.RuleId}-{this.Optional(finding.ModId)}"
        );
    }

    private ModHealthViewerDisplayRow FormatMod(ModHealthModPresentation mod, bool attention, string stablePrefix = "mod")
    {
        bool error = mod.Status is ModHealthModStatus.Invalid or ModHealthModStatus.Failed || mod.SessionErrorCount > 0 || mod.CaptureErrorCount > 0 || mod.CallbackFailureCount > 0;
        bool warning = error || mod.Status != ModHealthModStatus.Loaded || mod.SessionWarningCount > 0 || mod.WarningFlags.Length > 0 || mod.UpdateStatus == ModHealthReportUpdateStatus.UpdateAvailable;
        string detail = this.F("health-view.content.summary.mod.detail", "ID: {0}; {1} {2} v{3}; warnings/errors: {4}/{5}; capture errors: {6}; callback failures: {7}; warning flags: {8}; dependencies: {9}; update: {10}.", Clip(mod.Id, 96), this.ModStatus(mod.Status), this.ModKind(mod.Kind), Clip(mod.Version, 48), N(mod.SessionWarningCount), N(mod.SessionErrorCount), N(mod.CaptureErrorCount), N(mod.CallbackFailureCount), N(mod.WarningFlags.Length), N(mod.Dependencies.Length), this.UpdateStatus(mod.UpdateStatus));
        return new(
            attention ? this.F("health-view.content.summary.mod.attention-title", "Needs attention: {0}", mod.Name) : mod.Name,
            detail,
            error ? ModHealthViewerRowSeverity.Error : warning ? ModHealthViewerRowSeverity.Warning : ModHealthViewerRowSeverity.Neutral,
            ModHealthViewerRowIconKey.Mod,
            $"{stablePrefix}-{mod.Id}"
        );
    }

    private ModHealthViewerDisplayRow FormatCallback(ModHealthCallback callback, int index)
    {
        return new(
            this.F("health-view.content.summary.callback.title", "Callback: {0}", callback.ModName),
            this.F("health-view.content.summary.callback.detail", "Mod ID: {0}; {1}/{2}; calls: {3}; total/average/maximum: {4} / {5} / {6}; failures: {7}. Select for callback identity.", Clip(callback.ModId, 96), this.ExecutionPhase(callback.Phase), this.Operation(callback.Operation), N(callback.CallCount), Ms(callback.TotalMilliseconds), this.SafeAverage(callback.TotalMilliseconds, callback.CallCount), Ms(callback.MaximumMilliseconds), N(callback.FailureCount)),
            callback.FailureCount > 0 ? ModHealthViewerRowSeverity.Error : ModHealthViewerRowSeverity.Neutral,
            ModHealthViewerRowIconKey.Callback,
            $"callback-{N(index + 1)}"
        );
    }

    private ModHealthViewerDisplayRow FormatEpisode(ModHealthEpisode episode, int index)
    {
        return new(
            this.F("health-view.content.summary.episode.title", "Slow episode {0}", N(index + 1)),
            this.F("health-view.content.summary.episode.detail", "Updates {0}–{1}; qualifying updates: {2}; maximum: {3}; summed qualifying time: {4}; representative update: {5}; nearby mark: {6}.", N(episode.FirstUpdateTick), N(episode.LastUpdateTick), N(episode.QualifyingUpdateCount), Ms(episode.MaximumMilliseconds), Ms(episode.SummedQualifyingMilliseconds), N(episode.RepresentativeUpdateTick), this.OptionalNumber(episode.NearbyMark)),
            ModHealthViewerRowSeverity.Warning,
            ModHealthViewerRowIconKey.Episode,
            $"episode-{N(index + 1)}-{N(episode.RepresentativeUpdateTick)}"
        );
    }

    private ModHealthViewerDisplayRow FormatUpdate(ModHealthUpdatePresentation update, string titleKey, string titleFormat, string stablePrefix)
    {
        string detail = this.F("health-view.content.summary.update.detail", "Tick {0}; total: {1}; observed callbacks: {2}; residual: {3}; timing valid: {4}; phase: {5}; warnings/errors/failures: {6}/{7}/{8}; contributors: {9}.", N(update.UpdateTick), Ms(update.TotalMilliseconds), this.Evidence(update.ObservedCallbacks), this.Evidence(update.Residual), this.YesNo(update.TimingValid), Clip(update.Phase, 48), N(update.WarningCount), N(update.ErrorCount), N(update.CallbackFailureCount), N(update.Contributors.Length));
        return new(
            this.F(titleKey, titleFormat, N(update.UpdateTick)),
            detail,
            update.ErrorCount > 0 || update.CallbackFailureCount > 0 ? ModHealthViewerRowSeverity.Error : ModHealthViewerRowSeverity.Warning,
            ModHealthViewerRowIconKey.Update,
            $"{stablePrefix}-{N(update.UpdateTick)}"
        );
    }

    private ModHealthViewerDisplayRow FormatLog(ModHealthLogSummary log, int index)
    {
        ModHealthViewerRowSeverity severity = HasErrors(log.SinceLedgerStart) || HasErrors(log.DuringCapture)
            ? ModHealthViewerRowSeverity.Error
            : log.SinceLedgerStart.WarningMessages > 0 || log.DuringCapture.WarningMessages > 0
                ? ModHealthViewerRowSeverity.Warning
                : ModHealthViewerRowSeverity.Neutral;
        return new(
            this.F("health-view.content.summary.log.title", "Log source: {0}", log.Source),
            this.F("health-view.content.summary.log.detail", "Category: {0}; since ledger start: {1}; during capture: {2}; peak: {3} messages/s, {4} characters/s; first offset: {5}; last offset: {6}.", this.LogCategory(log.SourceCategory), this.FormatSeverity(log.SinceLedgerStart), this.FormatSeverity(log.DuringCapture), N(log.PeakMessagesPerSecond), N(log.PeakCharactersPerSecond), this.OptionalMs(log.FirstOffsetMilliseconds), this.OptionalMs(log.LastOffsetMilliseconds)),
            severity,
            ModHealthViewerRowIconKey.Log,
            $"log-{N(index + 1)}-{log.Source}"
        );
    }

    private ModHealthViewerDisplayRow FormatFailure(ModHealthCallbackFailure failure, int index)
    {
        return new(
            this.F("health-view.content.summary.failure.title", "Callback failure: {0}", failure.ModName),
            this.F("health-view.content.summary.failure.detail", "Mod ID: {0}; {1}/{2}; session/capture count: {3}/{4}; first/last offset: {5} / {6}. Select for callback and exception identities.", Clip(failure.ModId, 96), this.ExecutionPhase(failure.Phase), this.Operation(failure.Operation), N(failure.SessionCount), N(failure.CaptureCount), Ms(failure.FirstOffsetMilliseconds), Ms(failure.LastOffsetMilliseconds)),
            ModHealthViewerRowSeverity.Error,
            ModHealthViewerRowIconKey.Failure,
            $"failure-{N(index + 1)}-{failure.ModId}"
        );
    }

    private ModHealthViewerDisplayRow FormatEvidence(string titleKey, string title, ModHealthMeasuredMilliseconds value, string stableId)
    {
        return new(
            this.T(titleKey, title),
            this.Evidence(value),
            value.State == ModHealthEvidenceState.Invalid ? ModHealthViewerRowSeverity.Warning : ModHealthViewerRowSeverity.Info,
            ModHealthViewerRowIconKey.Timing,
            stableId
        );
    }

    private ModHealthViewerDisplayRow FormatGc(ModHealthGcPresentation gc, string stableId)
    {
        return new(
            this.T("health-view.content.summary.gc.title", "GC collection correlation"),
            this.Gc(gc),
            gc.State == ModHealthEvidenceState.Measured ? ModHealthViewerRowSeverity.Info : ModHealthViewerRowSeverity.Warning,
            ModHealthViewerRowIconKey.Timing,
            stableId
        );
    }

    private ModHealthViewerDisplayRow FormatOmission(ModHealthOmission omission, string stablePrefix)
    {
        return new(
            this.F("health-view.content.summary.omission.title", "Omitted: {0}", omission.Section),
            this.F("health-view.content.summary.omission.detail", "{0} entries were omitted from this bounded report section.", N(omission.Count)),
            omission.Count > 0 ? ModHealthViewerRowSeverity.Warning : ModHealthViewerRowSeverity.Neutral,
            ModHealthViewerRowIconKey.Omission,
            $"{stablePrefix}-{omission.Section}"
        );
    }

    private string Evidence(ModHealthMeasuredMilliseconds value)
    {
        return value.State switch
        {
            ModHealthEvidenceState.Measured => this.F("health-view.content.value.evidence.measured", "{0} (measured)", Ms(value.Value)),
            ModHealthEvidenceState.Unavailable => this.T("health-view.content.value.evidence.unavailable", "Unavailable; the unseparated time is folded into residual."),
            ModHealthEvidenceState.Invalid => this.T("health-view.content.value.evidence.invalid", "Invalid timing evidence; timing percentages are hidden."),
            ModHealthEvidenceState.NotApplicable => this.T("health-view.content.value.evidence.not-applicable", "Not applicable; this ledger-only report has no timed capture."),
            _ => this.T("health-view.content.value.evidence.unknown", "Unknown evidence state.")
        };
    }

    private string Ratio(ModHealthMeasuredRatio ratio)
    {
        return ratio.State switch
        {
            ModHealthEvidenceState.Measured => this.F("health-view.content.value.ratio.measured", "{0} (measured)", Percent(ratio.Value)),
            ModHealthEvidenceState.Invalid => this.T("health-view.content.value.ratio.invalid", "invalid; percentage hidden"),
            ModHealthEvidenceState.NotApplicable => this.T("health-view.content.value.ratio.not-applicable", "not applicable; no timed capture"),
            _ => this.T("health-view.content.value.unavailable", "unavailable")
        };
    }

    private string Gc(ModHealthGcPresentation gc)
    {
        return gc.State switch
        {
            ModHealthEvidenceState.Measured => this.F("health-view.content.value.gc.measured", "Process-wide collections (measured correlation): gen0 {0}, gen1 {1}, gen2 {2}.", N(gc.Gen0Collections), N(gc.Gen1Collections), N(gc.Gen2Collections)),
            ModHealthEvidenceState.NotApplicable => this.T("health-view.content.value.gc.not-applicable", "Not applicable; this ledger-only report has no timed capture."),
            _ => this.T("health-view.content.value.gc.unavailable", "Unavailable; collection counts are not presented as measured zeros.")
        };
    }

    private string FormatSeverity(ModHealthLogSeveritySummary summary)
    {
        return this.F("health-view.content.value.log-severity", "trace {0} messages/{1} chars, debug {2}/{3}, info {4}/{5}, warning {6}/{7}, error {8}/{9}, alert {10}/{11}", N(summary.TraceMessages), N(summary.TraceCharacters), N(summary.DebugMessages), N(summary.DebugCharacters), N(summary.InfoMessages), N(summary.InfoCharacters), N(summary.WarningMessages), N(summary.WarningCharacters), N(summary.ErrorMessages), N(summary.ErrorCharacters), N(summary.AlertMessages), N(summary.AlertCharacters));
    }

    private static bool HasErrors(ModHealthLogSeveritySummary summary)
    {
        return summary.ErrorMessages > 0 || summary.AlertMessages > 0;
    }

    private DetailSource CreateDetailSource(ModHealthViewerSection section, int rowIndex)
    {
        return section switch
        {
            ModHealthViewerSection.Overview => this.CreateOverviewDetails(rowIndex),
            ModHealthViewerSection.Findings => this.CreateFindingDetails(rowIndex),
            ModHealthViewerSection.Capture => this.CreateCaptureDetails(rowIndex),
            ModHealthViewerSection.Attention => CreateModDetails(GetOne(this.Report.Attention.Mods, rowIndex)),
            ModHealthViewerSection.Performance => this.CreatePerformanceDetails(rowIndex),
            ModHealthViewerSection.Errors => this.CreateErrorDetails(rowIndex),
            ModHealthViewerSection.Inventory => rowIndex == 0
                ? CreateInventorySummaryDetails(this.Report.Inventory.Summary)
                : CreateModDetails(GetOne(this.Report.Inventory.Mods, rowIndex - 1)),
            ModHealthViewerSection.Context => this.CreateContextDetails(rowIndex),
            _ => throw new ArgumentOutOfRangeException(nameof(section), section, "Unknown health-report section.")
        };
    }

    private DetailSource CreateOverviewDetails(int rowIndex)
    {
        ModHealthOverviewPresentation overview = this.Report.Overview;
        if (rowIndex == 0)
        {
            return FixedDetails(
                D("Schema version", N(this.Report.SchemaVersion)),
                D("Report ID", overview.Header.ReportId),
                D("Generated UTC", Utc(overview.Header.GeneratedUtc)),
                D("Truncated", YesNo(overview.Header.IsTruncated)),
                D("Minimal fallback", YesNo(overview.Header.IsMinimalFallback)),
                D("Write retry", YesNo(overview.Header.WriteRetry))
            );
        }
        if (rowIndex == 1)
        {
            ImmutableArray<ModHealthViewerDetailRow> fixedRows = ImmutableArray.Create(
                D("Inspect before sharing", YesNo(overview.Privacy.InspectBeforeSharing)),
                D("Automatic upload", YesNo(overview.Privacy.AutomaticUpload))
            );
            return AppendedDetails(
                fixedRows,
                overview.Privacy.IncludedIdentityFields,
                "Included identity field",
                overview.Privacy.ExcludedSources,
                "Excluded source"
            );
        }
        return FixedDetails(D("Privacy notice", overview.PrivacyNotices[rowIndex - 2]));
    }

    private DetailSource CreateFindingDetails(int rowIndex)
    {
        int findingRows = Math.Max(1, this.Report.Findings.Rows.Length);
        if (rowIndex < findingRows)
        {
            if (this.Report.Findings.Rows.Length == 0)
                return FixedDetails(D("Finding", this.T("health-view.content.value.no-findings", "No findings were generated from the retained evidence.")));

            ModHealthFinding finding = this.Report.Findings.Rows[rowIndex];
            return FixedDetails(
                D("Rule ID", finding.RuleId),
                D("Severity", FindingSeverity(finding.Severity)),
                D("Confidence", Confidence(finding.Confidence)),
                D("Affected mod ID", Optional(finding.ModId)),
                D("Summary", finding.Summary),
                D("Evidence", finding.Evidence),
                D("Suggested action", finding.SuggestedAction),
                D("Limitation", finding.Limitation)
            );
        }
        return FixedDetails(D("Suggested next step", this.Report.Findings.SuggestedActions[rowIndex - findingRows]));
    }

    private DetailSource CreateCaptureDetails(int rowIndex)
    {
        ModHealthCapturePresentation capture = this.Report.Capture;
        ModHealthCapture details = capture.Details;
        if (rowIndex == 0)
        {
            return FixedDetails(
                D("Mode", CaptureMode(details.Mode)),
                D("Completion reason", Completion(details.CompletionReason)),
                D("Started UTC", OptionalUtc(details.StartedUtc)),
                D("Ended UTC", OptionalUtc(details.EndedUtc)),
                D("Duration", Ms(details.DurationMilliseconds)),
                D("Completed update ticks", N(details.CompletedUpdateCount)),
                D("Slow-update threshold", Ms(details.SlowUpdateThresholdMilliseconds)),
                D("Short sample", YesNo(details.IsShortSample)),
                D("Timing valid", YesNo(details.TimingValid))
            );
        }
        if (rowIndex == 1)
        {
            return FixedDetails(
                D("Short sample", YesNo(details.IsShortSample)),
                D("Timing valid", YesNo(details.TimingValid)),
                D("Truncated", YesNo(capture.IsTruncated)),
                D("Minimal fallback", YesNo(capture.IsMinimalFallback)),
                D("Write retry", YesNo(capture.WriteRetry)),
                D("Mark count", N(details.Marks.Length)),
                D("Positive omission count", N(capture.PositiveOmissions.Length))
            );
        }

        int relative = rowIndex - 2;
        if (relative < details.Marks.Length)
        {
            ModHealthMark mark = details.Marks[relative];
            return FixedDetails(
                D("Mark number", N(mark.Number)),
                D("Update tick", N(mark.UpdateTick)),
                D("Offset after capture start", Ms(mark.OffsetMilliseconds))
            );
        }
        return CreateOmissionDetails(capture.PositiveOmissions[relative - details.Marks.Length]);
    }

    private DetailSource CreatePerformanceDetails(int rowIndex)
    {
        ModHealthPerformancePresentation performance = this.Report.Performance;
        if (rowIndex == 0)
            return CreateHistogramDetails(performance.Histogram, performance.CanShowTimingPercentages);
        if (rowIndex == 1)
            return CreateEvidenceDetails(performance.ObservedCallbacks);
        if (rowIndex == 2)
            return CreateEvidenceDetails(performance.BaseGameExclusive);
        if (rowIndex == 3)
            return CreateEvidenceDetails(performance.SmapiUpdateDispatch);
        if (rowIndex == 4)
            return CreateEvidenceDetails(performance.Residual);
        if (rowIndex == 5)
            return FixedDetails(D("Slow update count", N(performance.SlowUpdateCount)));
        if (rowIndex == 6)
            return CreateGcDetails(performance.Gc);

        int relative = rowIndex - 7;
        if (relative < performance.AttributionCaveats.Length)
            return FixedDetails(D("Timing limitation", performance.AttributionCaveats[relative]));
        relative -= performance.AttributionCaveats.Length;

        if (relative < performance.ObservedMods.Count)
            return CreateModDetails(GetOne(performance.ObservedMods, relative));
        relative -= performance.ObservedMods.Count;

        if (relative < performance.Callbacks.Count)
            return CreateCallbackDetails(GetOne(performance.Callbacks, relative));
        relative -= performance.Callbacks.Count;

        if (relative < performance.Episodes.Count)
            return CreateEpisodeDetails(GetOne(performance.Episodes, relative));
        relative -= performance.Episodes.Count;

        if (relative < performance.WorstUpdates.Count)
            return CreateUpdateDetails(GetOne(performance.WorstUpdates, relative));
        relative -= performance.WorstUpdates.Count;

        return CreateUpdateDetails(GetOne(performance.RecentUpdates, relative));
    }

    private DetailSource CreateErrorDetails(int rowIndex)
    {
        ModHealthErrorsPresentation errors = this.Report.Errors;
        if (rowIndex == 0)
        {
            return AppendedDetails(
                ImmutableArray<ModHealthViewerDetailRow>.Empty,
                CreateSeverityDetails(errors.LogTotals.SinceLedgerStart, "Since ledger start"),
                CreateSeverityDetails(errors.LogTotals.DuringCapture, "During capture")
            );
        }
        if (rowIndex == 1)
        {
            return FixedDetails(
                D("Since ledger start", N(errors.CallbackFailureTotals.SinceLedgerStart)),
                D("During capture", N(errors.CallbackFailureTotals.DuringCapture)),
                D("Counting limitation", this.T("health-view.content.value.counting-limitation", "A failed callback may also emit an error; do not sum those columns as unique incidents."))
            );
        }

        int relative = rowIndex - 2;
        if (relative < errors.Logs.Count)
            return CreateLogDetails(GetOne(errors.Logs, relative));
        relative -= errors.Logs.Count;
        return CreateFailureDetails(GetOne(errors.CallbackFailures, relative));
    }

    private DetailSource CreateContextDetails(int rowIndex)
    {
        ModHealthContextPresentation context = this.Report.Context;
        if (rowIndex == 0)
        {
            ModHealthEnvironment environment = context.Environment;
            return FixedDetails(
                D("SMAPI version", environment.SmapiVersion),
                D("SMAPI commit", Optional(environment.SmapiCommit)),
                D("Game version", environment.GameVersion),
                D("Runtime version", environment.RuntimeVersion),
                D("Process architecture", environment.ProcessArchitecture),
                D("Process bitness", N(environment.ProcessBitness)),
                D("Linux distribution", Optional(environment.LinuxDistribution)),
                D("Kernel", Optional(environment.Kernel)),
                D("Session type", environment.SessionType),
                D("Locale", environment.Locale),
                D("Logical processor count", N(environment.LogicalProcessorCount)),
                D("Multiplayer role", environment.MultiplayerRole),
                D("Split-screen count", N(environment.SplitScreenCount))
            );
        }
        if (rowIndex == 1)
        {
            ModHealthCompleteness completeness = context.Completeness;
            return FixedDetails(
                D("Ledger started UTC", Utc(completeness.LedgerStartedUtc)),
                D("Startup observed", YesNo(completeness.StartupObserved)),
                D("Lifecycle timing observed", YesNo(completeness.LifecycleTimingObserved)),
                D("Evidence boundary", completeness.Boundary)
            );
        }

        int relative = rowIndex - 2;
        if (relative < context.Capacities.Length)
        {
            ModHealthCapacity capacity = context.Capacities[relative];
            return FixedDetails(D("Capacity name", capacity.Name), D("Limit", N(capacity.Limit)), D("Reached", YesNo(capacity.Reached)));
        }
        relative -= context.Capacities.Length;
        if (relative < context.Omissions.Length)
            return CreateOmissionDetails(context.Omissions[relative]);
        relative -= context.Omissions.Length;
        return FixedDetails(D("Report limitation", context.Limitations[relative]));
    }

    private DetailSource CreateHistogramDetails(ModHealthHistogram histogram, bool canShowTimingPercentages)
    {
        ImmutableArray<ModHealthViewerDetailRow> fixedRows = ImmutableArray.Create(
            D("Count", N(histogram.Count)),
            D("Sum", Ms(histogram.SumMilliseconds)),
            D("Mean", SafeAverage(histogram.SumMilliseconds, histogram.Count)),
            D("Minimum", OptionalMs(histogram.MinimumMilliseconds)),
            D("Maximum", OptionalMs(histogram.MaximumMilliseconds)),
            D("P50", OptionalMs(histogram.P50Milliseconds)),
            D("P95", OptionalMs(histogram.P95Milliseconds)),
            D("P99", OptionalMs(histogram.P99Milliseconds)),
            D("Percentiles approximate", YesNo(histogram.PercentilesApproximate)),
            D("Maximum relative bucket error", Percent(histogram.MaximumRelativeBucketError)),
            D("Underflow count", N(histogram.UnderflowCount)),
            D("Overflow count", N(histogram.OverflowCount)),
            D("Timing percentages available", YesNo(canShowTimingPercentages))
        );
        return new DetailSource(Add(fixedRows.Length, histogram.Thresholds.Length), index =>
        {
            if (index < fixedRows.Length)
                return fixedRows[index];
            ModHealthThresholdCount threshold = histogram.Thresholds[index - fixedRows.Length];
            return D("Threshold", this.F("health-view.content.value.threshold", "{0}: {1} update ticks", Ms(threshold.Milliseconds), N(threshold.Count)), $"threshold-{N(index - fixedRows.Length + 1)}");
        });
    }

    private DetailSource CreateEvidenceDetails(ModHealthMeasuredMilliseconds evidence)
    {
        return evidence.State == ModHealthEvidenceState.Measured
            ? FixedDetails(D("Evidence state", this.T("health-view.content.value.measured", "measured")), D("Measured value", Ms(evidence.Value)))
            : FixedDetails(D("Evidence state", Evidence(evidence)));
    }

    private DetailSource CreateGcDetails(ModHealthGcPresentation gc)
    {
        return gc.State == ModHealthEvidenceState.Measured
            ? FixedDetails(
                D("Evidence state", this.T("health-view.content.value.measured-process-wide", "measured process-wide correlation")),
                D("Generation 0 collections", N(gc.Gen0Collections)),
                D("Generation 1 collections", N(gc.Gen1Collections)),
                D("Generation 2 collections", N(gc.Gen2Collections))
            )
            : FixedDetails(D("Evidence state", Gc(gc)));
    }

    private DetailSource CreateModDetails(ModHealthModPresentation mod)
    {
        ImmutableArray<ModHealthViewerDetailRow> fixedRows = ImmutableArray.Create(
            D("Mod ID", mod.Id),
            D("Display name", mod.Name),
            D("Version", mod.Version),
            D("Kind", ModKind(mod.Kind)),
            D("Parent ID", Optional(mod.ParentId)),
            D("Status", ModStatus(mod.Status)),
            D("Failure category", Optional(mod.FailureCategory)),
            D("Update status", UpdateStatus(mod.UpdateStatus)),
            D("Suggested update version", Optional(mod.SuggestedUpdateVersion)),
            D("Session warning count", N(mod.SessionWarningCount)),
            D("Session error count", N(mod.SessionErrorCount)),
            D("Capture error count", N(mod.CaptureErrorCount)),
            D("Callback failure count", N(mod.CallbackFailureCount)),
            D("Observed callback time", Ms(mod.ObservedCallbackMilliseconds)),
            D("Observed callback peak", Ms(mod.ObservedCallbackPeakMilliseconds)),
            D("Observed callback count", N(mod.ObservedCallbackCount)),
            D("Observed callback failure count", N(mod.ObservedCallbackFailureCount)),
            D("Slow-update participation count", N(mod.SlowUpdateParticipationCount)),
            D("Instrumented time share", Ratio(mod.InstrumentedTimeShare)),
            D("Peak messages per second", N(mod.PeakMessagesPerSecond)),
            D("Peak characters per second", N(mod.PeakCharactersPerSecond))
        );
        return AppendedDetails(fixedRows, mod.WarningFlags, "Warning flag", mod.Dependencies, "Dependency ID");
    }

    private DetailSource CreateCallbackDetails(ModHealthCallback callback)
    {
        return FixedDetails(
            D("Mod ID", callback.ModId),
            D("Mod name", callback.ModName),
            D("Execution phase", ExecutionPhase(callback.Phase)),
            D("Operation", Operation(callback.Operation)),
            D("Event", callback.Event),
            D("Callback", callback.Callback),
            D("On behalf of mod ID", Optional(callback.OnBehalfOfModId)),
            D("Call count", N(callback.CallCount)),
            D("Total", Ms(callback.TotalMilliseconds)),
            D("Average", SafeAverage(callback.TotalMilliseconds, callback.CallCount)),
            D("Maximum", Ms(callback.MaximumMilliseconds)),
            D("Failure count", N(callback.FailureCount))
        );
    }

    private DetailSource CreateEpisodeDetails(ModHealthEpisode episode)
    {
        return FixedDetails(
            D("First update tick", N(episode.FirstUpdateTick)),
            D("Last update tick", N(episode.LastUpdateTick)),
            D("Qualifying update count", N(episode.QualifyingUpdateCount)),
            D("Maximum", Ms(episode.MaximumMilliseconds)),
            D("Summed qualifying time", Ms(episode.SummedQualifyingMilliseconds)),
            D("Representative update tick", N(episode.RepresentativeUpdateTick)),
            D("Nearby mark", OptionalNumber(episode.NearbyMark))
        );
    }

    private DetailSource CreateUpdateDetails(ModHealthUpdatePresentation update)
    {
        ImmutableArray<ModHealthViewerDetailRow> fixedRows = ImmutableArray.Create(
            D("Update tick", N(update.UpdateTick)),
            D("Offset after capture start", Ms(update.OffsetMilliseconds)),
            D("Total", Ms(update.TotalMilliseconds)),
            D("Base-game exclusive", Evidence(update.BaseGameExclusive)),
            D("Observed callbacks", Evidence(update.ObservedCallbacks)),
            D(ModHealthPresentationText.SmapiUpdateDispatchLabel, Evidence(update.SmapiUpdateDispatch)),
            D("Residual", Evidence(update.Residual)),
            D("Timing valid", YesNo(update.TimingValid)),
            D("Phase", update.Phase),
            D("Focused", YesNo(update.Focused)),
            D("Screen", N(update.Screen)),
            D("Warning count", N(update.WarningCount)),
            D("Error count", N(update.ErrorCount)),
            D("Callback failure count", N(update.CallbackFailureCount)),
            D("GC collection correlation", Gc(update.Gc)),
            D("Nearby mark", OptionalNumber(update.NearbyMark))
        );
        return new DetailSource(Add(fixedRows.Length, update.Contributors.Length), index =>
        {
            if (index < fixedRows.Length)
                return fixedRows[index];
            ModHealthContributor contributor = update.Contributors[index - fixedRows.Length];
            return D("Observed contributor", this.F("health-view.content.value.contributor", "{0}: {1}", contributor.ModId, Ms(contributor.Milliseconds)), $"contributor-{N(index - fixedRows.Length + 1)}");
        });
    }

    private DetailSource CreateLogDetails(ModHealthLogSummary log)
    {
        ImmutableArray<ModHealthViewerDetailRow> fixedRows = ImmutableArray.Create(
            D("Source", log.Source),
            D("Source category", LogCategory(log.SourceCategory)),
            D("Peak messages per second", N(log.PeakMessagesPerSecond)),
            D("Peak characters per second", N(log.PeakCharactersPerSecond)),
            D("First offset", OptionalMs(log.FirstOffsetMilliseconds)),
            D("Last offset", OptionalMs(log.LastOffsetMilliseconds))
        );
        return AppendedDetails(
            fixedRows,
            CreateSeverityDetails(log.SinceLedgerStart, "Since ledger start"),
            CreateSeverityDetails(log.DuringCapture, "During capture")
        );
    }

    private DetailSource CreateFailureDetails(ModHealthCallbackFailure failure)
    {
        return FixedDetails(
            D("Mod ID", failure.ModId),
            D("Mod name", failure.ModName),
            D("Execution phase", ExecutionPhase(failure.Phase)),
            D("Operation", Operation(failure.Operation)),
            D("Callback", failure.Callback),
            D("Exception type", failure.ExceptionType),
            D("On behalf of mod ID", Optional(failure.OnBehalfOfModId)),
            D("Session count", N(failure.SessionCount)),
            D("Capture count", N(failure.CaptureCount)),
            D("First offset", Ms(failure.FirstOffsetMilliseconds)),
            D("Last offset", Ms(failure.LastOffsetMilliseconds)),
            D("Counting limitation", this.T("health-view.content.value.counting-limitation", "A failed callback may also emit an error; do not sum those columns as unique incidents."))
        );
    }

    private DetailSource CreateInventorySummaryDetails(ModHealthModInventorySummary summary)
    {
        return FixedDetails(
            D("Total discovered", N(summary.TotalDiscovered)),
            D("Discovered", N(summary.Discovered)),
            D("Loaded", N(summary.Loaded)),
            D("Skipped", N(summary.Skipped)),
            D("Ignored", N(summary.Ignored)),
            D("Invalid", N(summary.Invalid)),
            D("Failed", N(summary.Failed)),
            D("Retained records", N(summary.Retained))
        );
    }

    private DetailSource CreateOmissionDetails(ModHealthOmission omission)
    {
        return FixedDetails(D("Section", omission.Section), D("Omitted entry count", N(omission.Count)));
    }

    private ImmutableArray<ModHealthViewerDetailRow> CreateSeverityDetails(ModHealthLogSeveritySummary summary, string prefix)
    {
        return ImmutableArray.Create(
            D($"{prefix} trace messages", N(summary.TraceMessages)),
            D($"{prefix} trace characters", N(summary.TraceCharacters)),
            D($"{prefix} debug messages", N(summary.DebugMessages)),
            D($"{prefix} debug characters", N(summary.DebugCharacters)),
            D($"{prefix} info messages", N(summary.InfoMessages)),
            D($"{prefix} info characters", N(summary.InfoCharacters)),
            D($"{prefix} warning messages", N(summary.WarningMessages)),
            D($"{prefix} warning characters", N(summary.WarningCharacters)),
            D($"{prefix} error messages", N(summary.ErrorMessages)),
            D($"{prefix} error characters", N(summary.ErrorCharacters)),
            D($"{prefix} alert messages", N(summary.AlertMessages)),
            D($"{prefix} alert characters", N(summary.AlertCharacters))
        );
    }

    private DetailSource FixedDetails(params ModHealthViewerDetailRow[] rows)
    {
        return new(rows.Length, index => rows[index]);
    }

    private DetailSource AppendedDetails(ImmutableArray<ModHealthViewerDetailRow> fixedRows, ImmutableArray<ModHealthViewerDetailRow> first, ImmutableArray<ModHealthViewerDetailRow> second)
    {
        return new DetailSource(Add(fixedRows.Length, first.Length, second.Length), index =>
        {
            if (index < fixedRows.Length)
                return fixedRows[index];
            index -= fixedRows.Length;
            if (index < first.Length)
                return first[index];
            return second[index - first.Length];
        });
    }

    private DetailSource AppendedDetails(ImmutableArray<ModHealthViewerDetailRow> fixedRows, ImmutableArray<string> first, string firstLabel, ImmutableArray<string> second, string secondLabel)
    {
        return new DetailSource(Add(fixedRows.Length, first.Length, second.Length), index =>
        {
            if (index < fixedRows.Length)
                return fixedRows[index];
            index -= fixedRows.Length;
            if (index < first.Length)
                return D(firstLabel, first[index], $"{firstLabel}-{N(index + 1)}");
            int secondIndex = index - first.Length;
            return D(secondLabel, second[secondIndex], $"{secondLabel}-{N(secondIndex + 1)}");
        });
    }

    private ModHealthViewerDetailRow D(string label, string value, string? stableId = null)
    {
        return new(this.T(GetLabelKey(label), label), value, stableId);
    }

    private sealed class DetailSource
    {
        private readonly Func<int, ModHealthViewerDetailRow> Get;

        public int Count { get; }

        public DetailSource(int count, Func<int, ModHealthViewerDetailRow> get)
        {
            this.Count = count;
            this.Get = get;
        }

        public ModHealthViewerDetailRow GetRow(int index) => this.Get(index);
    }

    private static TRow GetOne<TSource, TRow>(ModHealthVirtualRowSource<TSource, TRow> source, int index)
    {
        return source.GetPage(index, 1)[0];
    }

    private static ModHealthViewerDisplayRow ClipSummary(ModHealthViewerDisplayRow row)
    {
        return row with
        {
            Title = Clip(row.Title, MaxSummaryTitleCharacters),
            Detail = Clip(row.Detail, MaxSummaryDetailCharacters)
        };
    }

    private static string Clip(string value, int maximum)
    {
        if (value.Length <= maximum)
            return value;
        return string.Concat(value.AsSpan(0, maximum - 3), "...");
    }

    private string SafeAverage(double total, long count)
    {
        if (count <= 0 || !double.IsFinite(total))
            return this.T("health-view.content.value.unavailable", "unavailable");
        double average = total / count;
        return double.IsFinite(average) && average >= 0 ? Ms(average) : this.T("health-view.content.value.unavailable", "unavailable");
    }

    private static int Add(params int[] values)
    {
        long result = 0;
        foreach (int value in values)
            result += value;
        return result >= int.MaxValue ? int.MaxValue : (int)result;
    }

    private string Optional(string? value) => string.IsNullOrWhiteSpace(value) ? this.T("health-view.content.value.none", "none") : value;
    private string OptionalNumber(int? value) => value is null ? this.T("health-view.content.value.none", "none") : N(value.Value);
    private string OptionalMs(double? value) => value is null ? this.T("health-view.content.value.unavailable", "unavailable") : Ms(value.Value);
    private string OptionalUtc(DateTimeOffset? value) => value is null ? this.T("health-view.content.value.not-available", "not available") : Utc(value.Value);
    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
    private static string N(long value) => value.ToString("0", CultureInfo.InvariantCulture);
    private static string N(uint value) => value.ToString("0", CultureInfo.InvariantCulture);
    private static string N(int value) => value.ToString("0", CultureInfo.InvariantCulture);
    private static string Ms(double value) => $"{value.ToString("0.###", CultureInfo.InvariantCulture)} ms";
    private static string Percent(double ratio) => $"{(ratio * 100).ToString("0.###", CultureInfo.InvariantCulture)}%";
    private string YesNo(bool value) => value ? this.T("health-view.content.value.yes", "yes") : this.T("health-view.content.value.no", "no");

    private string CaptureMode(ModHealthCaptureMode value) => this.EnumValue("capture-mode", value.ToString(), value switch { ModHealthCaptureMode.LedgerOnly => "ledger only", ModHealthCaptureMode.Health => "health", _ => "performance" });
    private string Completion(ModHealthCompletionReason value) => this.EnumValue("completion", value.ToString(), value switch { ModHealthCompletionReason.NotStopped => "not stopped", ModHealthCompletionReason.UserStop => "user stop", ModHealthCompletionReason.PerformanceStop => "performance stop", ModHealthCompletionReason.NormalShutdown => "normal shutdown", _ => "interim report" });
    private string FindingSeverity(ModHealthFindingSeverity value) => this.EnumValue("finding-severity", value.ToString(), value switch { ModHealthFindingSeverity.ActionNeeded => "action needed", ModHealthFindingSeverity.Performance => "performance", ModHealthFindingSeverity.Check => "check", _ => "info" });
    private string Confidence(ModHealthFindingConfidence value) => this.EnumValue("confidence", value.ToString(), value switch { ModHealthFindingConfidence.Factual => "factual", ModHealthFindingConfidence.Likely => "likely", ModHealthFindingConfidence.Possible => "possible", _ => "limited" });
    private string ModKind(ModHealthModKind value) => this.EnumValue("mod-kind", value.ToString(), value switch { ModHealthModKind.CodeMod => "code mod", ModHealthModKind.ContentPack => "content pack", _ => "invalid" });
    private string ModStatus(ModHealthModStatus value) => this.EnumValue("mod-status", value.ToString(), value.ToString().ToLowerInvariant());
    private string UpdateStatus(ModHealthReportUpdateStatus value) => this.EnumValue("update-status", value.ToString(), value switch { ModHealthReportUpdateStatus.UpToDate => "up to date", ModHealthReportUpdateStatus.UpdateAvailable => "update available", _ => value.ToString().ToLowerInvariant() });
    private string ExecutionPhase(ModHealthExecutionPhase value) => this.EnumValue("execution-phase", value.ToString(), value.ToString().ToLowerInvariant());
    private string Operation(ModHealthOperationKind value) => this.EnumValue("operation", value.ToString(), value switch { ModHealthOperationKind.ContentLoad => "content load", ModHealthOperationKind.ContentEdit => "content edit", ModHealthOperationKind.GetApi => "get API", _ => value.ToString().ToLowerInvariant() });
    private string LogCategory(ModHealthReportLogSourceCategory value) => this.EnumValue("log-category", value.ToString(), value.ToString().ToLowerInvariant());

    private string EnumValue(string group, string name, string fallback)
    {
        return this.T($"health-view.content.enum.{group}.{name.ToLowerInvariant()}", fallback);
    }

    private string T(string key, string fallback)
    {
        string translated = this.Translate(key);
        return string.IsNullOrWhiteSpace(translated) || translated.Equals(key, StringComparison.Ordinal)
            ? fallback
            : translated;
    }

    private string F(string key, string fallback, params object[] arguments)
    {
        string format = this.T(key, fallback);
        try
        {
            return string.Format(CultureInfo.InvariantCulture, format, arguments);
        }
        catch (FormatException)
        {
            return string.Format(CultureInfo.InvariantCulture, fallback, arguments);
        }
    }

    private static string GetLabelKey(string label)
    {
        StringBuilder key = new("health-view.content.label.");
        bool separator = false;
        foreach (char character in label)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (separator && key[^1] != '.')
                    key.Append('-');
                key.Append(char.ToLowerInvariant(character));
                separator = false;
            }
            else
                separator = true;
        }
        return key.ToString();
    }
}
