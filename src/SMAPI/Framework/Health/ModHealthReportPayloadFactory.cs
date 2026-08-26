using System;
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
            ModHealthReportPayload payload = this.Format(candidate);
            if (payload.TextByteCount <= this.MaximumBytes && payload.JsonByteCount <= this.MaximumBytes)
                return payload;

            if (!this.Pruner.TryPrune(candidate, out candidate))
                break;
        }

        candidate = this.Pruner.CreateMinimalFallback(candidate);
        ModHealthReportPayload fallback = this.Format(candidate);
        if (fallback.TextByteCount <= this.MaximumBytes && fallback.JsonByteCount <= this.MaximumBytes)
            return fallback;

        throw new InvalidOperationException("The mandatory minimal mod health report exceeds the configured output limit.");
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
