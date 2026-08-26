using System;
using System.Linq;
using System.Text;

namespace StardewModdingAPI.Framework.Health;

/// <summary>Creates matching bounded text and JSON payloads from one immutable report DTO.</summary>
internal sealed class ModHealthReportPayloadFactory
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly ModHealthInsightAnalyzer Analyzer;
    private readonly ModHealthReportTextFormatter TextFormatter;
    private readonly ModHealthReportJsonSerializer JsonSerializer;
    private readonly ModHealthReportPruner Pruner;
    private readonly int MaximumBytes;

    public ModHealthReportPayloadFactory(ModHealthInsightAnalyzer? analyzer = null, ModHealthReportTextFormatter? textFormatter = null, ModHealthReportJsonSerializer? jsonSerializer = null, ModHealthReportPruner? pruner = null, int maximumBytes = ModHealthReportLimits.MaxOutputBytes)
    {
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        this.Analyzer = analyzer ?? new ModHealthInsightAnalyzer();
        this.TextFormatter = textFormatter ?? new ModHealthReportTextFormatter();
        this.JsonSerializer = jsonSerializer ?? new ModHealthReportJsonSerializer();
        this.Pruner = pruner ?? new ModHealthReportPruner();
        this.MaximumBytes = maximumBytes;
    }

    /// <summary>Analyze and format one frozen report, pruning deterministic optional detail if needed.</summary>
    public ModHealthReportPayload Create(ModHealthReport frozenReport)
    {
        ModHealthReport candidate = frozenReport with { Findings = this.Analyzer.Analyze(frozenReport) };
        while (true)
        {
            while (ModHealthReportPayloadFactory.EstimateMaximumFormattedBytes(candidate, this.MaximumBytes) > this.MaximumBytes)
            {
                if (!this.Pruner.TryPrune(candidate, out candidate))
                    goto MinimalFallback;
            }

            ModHealthReportPayload payload = this.Format(candidate);
            if (payload.TextByteCount <= this.MaximumBytes && payload.JsonByteCount <= this.MaximumBytes)
                return payload;

            if (!this.Pruner.TryPrune(candidate, out candidate))
                break;
        }

MinimalFallback:
        candidate = this.Pruner.CreateMinimalFallback(candidate);
        ModHealthReportPayload fallback = this.Format(candidate);
        if (fallback.TextByteCount <= this.MaximumBytes && fallback.JsonByteCount <= this.MaximumBytes)
            return fallback;

        throw new InvalidOperationException("The mandatory minimal mod health report exceeds the configured output limit.");
    }

    /// <summary>Conservatively estimate a shared upper bound for either formatted representation without constructing either string.</summary>
    /// <remarks>
    /// The estimate counts JSON string escaping without allocating and allows for repeated player-facing identity text,
    /// while the per-row allowance covers property names, punctuation, numbers, and fixed prose. It stops once the output
    /// limit is exceeded, so even a DTO with the maximum legal number of nested values doesn't need a full pre-scan.
    /// </remarks>
    private static long EstimateMaximumFormattedBytes(ModHealthReport report, long stopAfterBytes)
    {
        long estimate = 256 * 1024;

        void Add(long value)
        {
            if (value <= 0 || estimate == long.MaxValue)
                return;
            estimate = value > long.MaxValue - estimate ? long.MaxValue : estimate + value;
        }

        // One KiB covers the fixed JSON property names, punctuation, indentation, numeric values, and text labels per row.
        void AddRows(long count) => Add(count > long.MaxValue / 1024 ? long.MaxValue : count * 1024);
        void AddString(string? value, int maximumTextOccurrences = 1)
        {
            if (value is null)
                return;

            long utf8Bytes = 0;
            long jsonBytes = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char character = value[i];
                if (character is '"' or '\\')
                {
                    utf8Bytes += 1;
                    jsonBytes += 2;
                }
                else if (character < ' ')
                {
                    utf8Bytes += 1;
                    jsonBytes += 6;
                }
                else if (char.IsHighSurrogate(character) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    utf8Bytes += 4;
                    jsonBytes += 4;
                    i++;
                }
                else if (char.IsSurrogate(character))
                {
                    // Invalid standalone surrogates aren't expected from the report builder, but six bytes is a safe bound.
                    utf8Bytes += 3;
                    jsonBytes += 6;
                }
                else if (character <= 0x7f)
                {
                    utf8Bytes += 1;
                    jsonBytes += 1;
                }
                else if (character <= 0x7ff)
                {
                    utf8Bytes += 2;
                    jsonBytes += 2;
                }
                else
                {
                    utf8Bytes += 3;
                    jsonBytes += 3;
                }
            }

            long textBytes = utf8Bytes > long.MaxValue / maximumTextOccurrences
                ? long.MaxValue
                : utf8Bytes * maximumTextOccurrences;
            Add(Math.Max(jsonBytes, textBytes));
        }

        bool LimitReached() => estimate > stopAfterBytes;

        AddRows(32);
        AddString(report.Header.ReportId);
        AddString(report.Completeness.Boundary);
        AddString(report.Environment.SmapiVersion);
        AddString(report.Environment.SmapiCommit);
        AddString(report.Environment.GameVersion);
        AddString(report.Environment.RuntimeVersion);
        AddString(report.Environment.ProcessArchitecture);
        AddString(report.Environment.LinuxDistribution);
        AddString(report.Environment.Kernel);
        AddString(report.Environment.SessionType);
        AddString(report.Environment.Locale);
        AddString(report.Environment.MultiplayerRole);
        if (LimitReached())
            return estimate;

        AddRows(report.Capture.Marks.Length);
        AddRows(report.Findings.Length);
        foreach (ModHealthFinding finding in report.Findings)
        {
            AddString(finding.RuleId);
            AddString(finding.ModId);
            AddString(finding.Summary);
            AddString(finding.Evidence);
            AddString(finding.SuggestedAction);
            AddString(finding.Limitation);
            if (LimitReached())
                return estimate;
        }

        AddRows(report.Performance.Histogram.Thresholds.Length);
        AddRows(report.Performance.Callbacks.Length);
        foreach (ModHealthCallback callback in report.Performance.Callbacks)
        {
            AddString(callback.ModId);
            AddString(callback.ModName);
            AddString(callback.Event);
            AddString(callback.Callback);
            AddString(callback.OnBehalfOfModId);
            if (LimitReached())
                return estimate;
        }

        AddRows(report.Performance.WorstUpdates.Length + report.Performance.RecentUpdates.Length);
        foreach (ModHealthUpdate update in report.Performance.WorstUpdates.Concat(report.Performance.RecentUpdates))
        {
            AddString(update.Phase);
            AddRows(update.Contributors.Length);
            foreach (ModHealthContributor contributor in update.Contributors)
            {
                AddString(contributor.ModId);
                if (LimitReached())
                    return estimate;
            }
        }
        AddRows(report.Performance.Episodes.Length);
        if (LimitReached())
            return estimate;

        AddRows(report.Mods.Length);
        foreach (ModHealthMod mod in report.Mods)
        {
            AddString(mod.Id, maximumTextOccurrences: 3);
            AddString(mod.Name, maximumTextOccurrences: 3);
            AddString(mod.Version);
            AddString(mod.ParentId);
            AddString(mod.FailureCategory);
            AddString(mod.SuggestedUpdateVersion);
            AddRows(mod.WarningFlags.Length + mod.Dependencies.Length);
            foreach (string warning in mod.WarningFlags)
                AddString(warning);
            foreach (string dependency in mod.Dependencies)
                AddString(dependency);
            if (LimitReached())
                return estimate;
        }

        AddRows(report.Logs.Length);
        foreach (ModHealthLogSummary log in report.Logs)
        {
            AddString(log.Source);
            if (LimitReached())
                return estimate;
        }

        AddRows(report.CallbackFailures.Length);
        foreach (ModHealthCallbackFailure failure in report.CallbackFailures)
        {
            AddString(failure.ModId);
            AddString(failure.ModName);
            AddString(failure.Callback);
            AddString(failure.ExceptionType);
            AddString(failure.OnBehalfOfModId);
            if (LimitReached())
                return estimate;
        }

        AddRows(report.Capacities.Length + report.Omissions.Length);
        foreach (ModHealthCapacity capacity in report.Capacities)
            AddString(capacity.Name);
        foreach (ModHealthOmission omission in report.Omissions)
            AddString(omission.Section);
        if (LimitReached())
            return estimate;

        AddRows(report.Privacy.IncludedIdentityFields.Length + report.Privacy.ExcludedSources.Length + report.Limitations.Length);
        foreach (string value in report.Privacy.IncludedIdentityFields)
            AddString(value);
        foreach (string value in report.Privacy.ExcludedSources)
            AddString(value);
        foreach (string limitation in report.Limitations)
            AddString(limitation);

        return estimate;
    }

    private ModHealthReportPayload Format(ModHealthReport report)
    {
        string text = this.TextFormatter.Format(report);
        string json = this.JsonSerializer.Serialize(report);
        return new(report, text, json, ModHealthReportPayloadFactory.Utf8.GetByteCount(text), ModHealthReportPayloadFactory.Utf8.GetByteCount(json));
    }
}

/// <summary>Matching text and JSON generated from one final report model.</summary>
internal sealed record ModHealthReportPayload(ModHealthReport Model, string Text, string Json, int TextByteCount, int JsonByteCount);
