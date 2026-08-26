using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace StardewModdingAPI.Framework.Health;

/// <summary>A small immutable completion summary derived from the exact finalized report payload.</summary>
internal sealed record ModHealthCompletionSummary(
    bool IsLedgerOnly,
    long CompletedUpdateCount,
    long WarningCount,
    long ErrorCount,
    long FailedCallbackCount,
    long SlowUpdateCount,
    bool IsShortSample,
    bool IsTruncated,
    bool IsTimingInvalid,
    ImmutableArray<ModHealthCompletionFinding> Findings
)
{
    private const int MaxFindingTextLength = 160;

    /// <summary>Create a bounded summary from the exact report model used for the published payload.</summary>
    public static ModHealthCompletionSummary FromReport(ModHealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        bool isLedgerOnly = report.Capture.Mode == ModHealthCaptureMode.LedgerOnly;
        ModHealthLogSeveritySummary logs = isLedgerOnly
            ? report.LogTotals.SinceLedgerStart
            : report.LogTotals.DuringCapture;
        long failedCallbacks = isLedgerOnly
            ? report.CallbackFailureTotals.SinceLedgerStart
            : report.CallbackFailureTotals.DuringCapture;
        ImmutableArray<ModHealthCompletionFinding> findings = report.Findings
            .Take(3)
            .Select(finding => new ModHealthCompletionFinding(
                ModHealthCompletionSummary.GetSeverityLabel(finding.Severity),
                ModHealthCompletionSummary.NormalizeText(finding.Summary),
                ModHealthCompletionSummary.NormalizeText(finding.SuggestedAction)
            ))
            .ToImmutableArray();

        return new(
            IsLedgerOnly: isLedgerOnly,
            CompletedUpdateCount: isLedgerOnly ? 0 : Math.Max(0, report.Capture.CompletedUpdateCount),
            WarningCount: ModHealthCompletionSummary.SaturatingNonnegativeSum(logs.WarningMessages, logs.AlertMessages),
            ErrorCount: Math.Max(0, logs.ErrorMessages),
            FailedCallbackCount: Math.Max(0, failedCallbacks),
            SlowUpdateCount: Math.Max(0, report.Performance.SlowUpdateCount),
            IsShortSample: report.Capture.IsShortSample,
            IsTruncated: report.Header.IsTruncated,
            IsTimingInvalid: !isLedgerOnly && !report.Capture.TimingValid,
            Findings: findings
        );
    }

    private static string GetSeverityLabel(ModHealthFindingSeverity severity) => severity switch
    {
        ModHealthFindingSeverity.ActionNeeded => "Action needed",
        ModHealthFindingSeverity.Performance => "Performance",
        ModHealthFindingSeverity.Check => "Check",
        _ => "Info"
    };

    private static long SaturatingNonnegativeSum(long first, long second)
    {
        first = Math.Max(0, first);
        second = Math.Max(0, second);
        return first > long.MaxValue - second ? long.MaxValue : first + second;
    }

    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Details are available in the report.";

        StringBuilder normalized = new(Math.Min(value.Length, ModHealthCompletionSummary.MaxFindingTextLength));
        bool pendingSpace = false;
        foreach (char character in value)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                pendingSpace = normalized.Length > 0;
                continue;
            }

            if (pendingSpace && normalized.Length < ModHealthCompletionSummary.MaxFindingTextLength)
                normalized.Append(' ');
            pendingSpace = false;
            if (normalized.Length >= ModHealthCompletionSummary.MaxFindingTextLength)
                break;
            normalized.Append(character);
        }

        if (normalized.Length == 0)
            return "Details are available in the report.";
        if (normalized.Length == ModHealthCompletionSummary.MaxFindingTextLength && value.Length > normalized.Length)
        {
            int retainedLength = ModHealthCompletionSummary.MaxFindingTextLength - 3;
            if (retainedLength > 0 && char.IsHighSurrogate(normalized[retainedLength - 1]))
                retainedLength--;
            normalized.Length = retainedLength;
            normalized.Append("...");
        }
        return normalized.ToString();
    }
}

/// <summary>One bounded finding shown after an export completes.</summary>
internal readonly record struct ModHealthCompletionFinding(string Label, string Summary, string Action);

/// <summary>Formats a compact terminal-width completion message.</summary>
internal static class ModHealthCompletionSummaryFormatter
{
    private const int DefaultWidth = 100;
    private const int MinimumWidth = 48;
    private const int MaximumWidth = 120;

    /// <summary>Format a completion summary followed by safe relative report paths.</summary>
    public static string Format(ModHealthCompletionSummary summary, string textPath, string jsonPath, int? terminalWidth = null)
    {
        ArgumentNullException.ThrowIfNull(summary);
        int width = Math.Clamp(terminalWidth ?? ModHealthCompletionSummaryFormatter.DefaultWidth, ModHealthCompletionSummaryFormatter.MinimumWidth, ModHealthCompletionSummaryFormatter.MaximumWidth);
        List<string> lines = ["Mod health report complete."];

        string sample = summary.IsLedgerOnly
            ? "Sample: ledger-only; no deep timing sample was available."
            : $"Sample: {summary.CompletedUpdateCount.ToString(CultureInfo.InvariantCulture)} completed update ticks.";
        ModHealthCompletionSummaryFormatter.AppendWrapped(lines, sample, width);
        ModHealthCompletionSummaryFormatter.AppendWrapped(
            lines,
            $"Frozen counts: {summary.WarningCount.ToString(CultureInfo.InvariantCulture)} warnings, {summary.ErrorCount.ToString(CultureInfo.InvariantCulture)} errors, {summary.FailedCallbackCount.ToString(CultureInfo.InvariantCulture)} failed callbacks, {summary.SlowUpdateCount.ToString(CultureInfo.InvariantCulture)} slow updates.",
            width
        );

        List<string> flags = [];
        if (summary.IsShortSample)
            flags.Add("short sample");
        if (summary.IsTruncated)
            flags.Add("truncated");
        if (summary.IsTimingInvalid)
            flags.Add("invalid timing");
        ModHealthCompletionSummaryFormatter.AppendWrapped(lines, $"Flags: {(flags.Count > 0 ? string.Join(", ", flags) : "none")}.", width);

        if (summary.Findings.Length > 0)
        {
            lines.Add("Top findings:");
            for (int i = 0; i < Math.Min(3, summary.Findings.Length); i++)
            {
                ModHealthCompletionFinding finding = summary.Findings[i];
                ModHealthCompletionSummaryFormatter.AppendWrapped(lines, $"{i + 1}. {finding.Label}: {finding.Summary}", width, continuationIndent: 3);
                ModHealthCompletionSummaryFormatter.AppendWrapped(lines, $"   Next: {finding.Action}", width, continuationIndent: 9);
            }
        }

        lines.Add("Reports:");
        ModHealthCompletionSummaryFormatter.AppendWrapped(lines, $"Text: {ModHealthCompletionSummaryFormatter.GetSafeRelativePath(textPath)}", width, continuationIndent: 6);
        ModHealthCompletionSummaryFormatter.AppendWrapped(lines, $"JSON: {ModHealthCompletionSummaryFormatter.GetSafeRelativePath(jsonPath)}", width, continuationIndent: 6);
        return string.Join('\n', lines);
    }

    /// <summary>Get the current terminal content width, with deterministic bounds and a safe redirected-output fallback.</summary>
    public static int GetTerminalWidth()
    {
        try
        {
            if (!Console.IsOutputRedirected && Console.WindowWidth > 0)
                return Math.Clamp(Console.WindowWidth - 8, ModHealthCompletionSummaryFormatter.MinimumWidth, ModHealthCompletionSummaryFormatter.MaximumWidth);
        }
        catch (IOException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }

        return ModHealthCompletionSummaryFormatter.DefaultWidth;
    }

    private static string GetSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "relative path unavailable";

        string normalized = path.Replace('\\', '/');
        bool rooted = normalized.StartsWith("/", StringComparison.Ordinal)
            || (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':');
        bool unsafeSegment = normalized.Split('/').Any(segment => segment is "" or "." or "..");
        if (rooted || unsafeSegment || normalized.Any(char.IsControl))
            return "relative path unavailable";
        return normalized;
    }

    private static void AppendWrapped(List<string> lines, string value, int width, int continuationIndent = 0)
    {
        string remaining = value.Trim();
        string continuation = new(' ', continuationIndent);
        bool first = true;
        while (remaining.Length > 0)
        {
            string prefix = first ? "" : continuation;
            int available = Math.Max(1, width - prefix.Length);
            if (remaining.Length <= available)
            {
                lines.Add(prefix + remaining);
                return;
            }

            int split = remaining.LastIndexOf(' ', available - 1, available);
            if (split <= 0)
                split = available;
            lines.Add(prefix + remaining[..split].TrimEnd());
            remaining = remaining[split..].TrimStart();
            first = false;
        }
    }
}
