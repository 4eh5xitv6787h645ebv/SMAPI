using System.Collections.Immutable;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.Health;

namespace SMAPI.Tests.Framework.Health;

[TestFixture]
internal sealed class ModHealthCompletionSummaryTests
{
    [Test]
    public void Format_UsesFrozenCaptureCountsFlagsAndAtMostThreeSafeFindings()
    {
        ModHealthReport report = ModHealthReportFixtureFactory.CreateCanonical();
        ImmutableArray<ModHealthFinding> findings = ImmutableArray.Create(
            CreateFinding(ModHealthFindingSeverity.ActionNeeded, "First\nsummary\u001b[31m", "First\taction"),
            CreateFinding(ModHealthFindingSeverity.Performance, new string('x', 300), "Second action"),
            CreateFinding(ModHealthFindingSeverity.Check, "Third summary", "Third action"),
            CreateFinding(ModHealthFindingSeverity.Info, "Fourth summary", "Fourth action")
        );
        report = report with
        {
            Header = report.Header with { IsTruncated = true },
            Capture = report.Capture with { CompletedUpdateCount = 42, IsShortSample = true, TimingValid = false },
            Findings = findings,
            LogTotals = report.LogTotals with
            {
                SinceLedgerStart = report.LogTotals.SinceLedgerStart with { WarningMessages = 91, ErrorMessages = 92 },
                DuringCapture = report.LogTotals.DuringCapture with { WarningMessages = 2, AlertMessages = 6, ErrorMessages = 3 }
            },
            CallbackFailureTotals = report.CallbackFailureTotals with { SinceLedgerStart = 93, DuringCapture = 4 },
            Performance = report.Performance with { SlowUpdateCount = 5 }
        };

        ModHealthCompletionSummary summary = ModHealthCompletionSummary.FromReport(report);
        string text = ModHealthCompletionSummaryFormatter.Format(
            summary,
            "ErrorLogs/HealthReports/report.txt",
            "ErrorLogs/HealthReports/report.json",
            terminalWidth: 60
        );

        text.Should().Contain("Sample: 42 completed update ticks.");
        text.Should().Contain("Frozen counts: 8 warnings/alerts, 3 errors, 4 failed");
        text.Should().Contain("callbacks, 5 slow updates.");
        text.Should().Contain("slow updates.");
        text.Should().Contain("Flags: short sample, truncated, invalid timing.");
        text.Should().Contain("1. Action needed: First summary [31m");
        text.Should().Contain("Next: First action");
        text.Should().Contain("2. Performance:");
        text.Should().Contain("3. Check: Third summary");
        text.Should().Contain("at most five complete").And.Contain("30 days");
        text.Should().NotContain("Fourth summary");
        text.Should().NotContain("\u001b");
        text.Split('\n').Should().OnlyContain(line => line.Length <= 60);
        summary.Findings.Should().HaveCount(3);
        summary.Findings[1].Summary.Should().HaveLength(160);
    }

    [Test]
    public void FromReport_LedgerOnlyUsesFrozenSessionTotalsAndRejectsUnsafePaths()
    {
        ModHealthReport report = ModHealthReportFixtureFactory.CreateCanonical();
        report = report with
        {
            Capture = report.Capture with { Mode = ModHealthCaptureMode.LedgerOnly, CompletedUpdateCount = 123, TimingValid = false },
            LogTotals = report.LogTotals with
            {
                SinceLedgerStart = report.LogTotals.SinceLedgerStart with { WarningMessages = long.MaxValue, AlertMessages = 9, ErrorMessages = 8 },
                DuringCapture = report.LogTotals.DuringCapture with { WarningMessages = 70, ErrorMessages = 80 }
            },
            CallbackFailureTotals = report.CallbackFailureTotals with { SinceLedgerStart = 9, DuringCapture = 90 }
        };

        ModHealthCompletionSummary summary = ModHealthCompletionSummary.FromReport(report);
        string text = ModHealthCompletionSummaryFormatter.Format(summary, "/home/player/report.txt", "C:\\secret\\report.json", 80);

        summary.IsLedgerOnly.Should().BeTrue();
        summary.CompletedUpdateCount.Should().Be(0);
        summary.WarningCount.Should().Be(long.MaxValue);
        summary.ErrorCount.Should().Be(8);
        summary.FailedCallbackCount.Should().Be(9);
        summary.IsTimingInvalid.Should().BeFalse();
        summary.IsShortSample.Should().BeFalse();
        text.Should().Contain("Sample: ledger-only; no deep timing sample was available.");
        text.Should().Contain("relative path unavailable");
        text.Should().NotContain("/home/player").And.NotContain("secret");
    }

    [Test]
    public void FromReport_RemovesBidirectionalFormattingControlsFromFindingText()
    {
        ModHealthReport report = ModHealthReportFixtureFactory.CreateCanonical() with
        {
            Findings = ImmutableArray.Create(CreateFinding(ModHealthFindingSeverity.Check, "safe\u202eevil\u202c", "next\u2066step\u2069"))
        };

        ModHealthCompletionSummary summary = ModHealthCompletionSummary.FromReport(report);

        summary.Findings[0].Summary.Should().Be("safeevil");
        summary.Findings[0].Action.Should().Be("nextstep");
    }

    private static ModHealthFinding CreateFinding(ModHealthFindingSeverity severity, string summary, string action)
    {
        return new(
            RuleId: summary,
            Severity: severity,
            Confidence: ModHealthFindingConfidence.Factual,
            ModId: null,
            Summary: summary,
            Evidence: "evidence",
            SuggestedAction: action,
            Limitation: "limitation"
        );
    }
}
