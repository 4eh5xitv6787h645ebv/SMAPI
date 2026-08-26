using System.Collections.Immutable;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.Health;

namespace SMAPI.Tests.Framework.Health;

[TestFixture]
internal sealed class ModHealthInsightAnalyzerTests
{
    [Test]
    public void Analyze_PrioritizesUnattributedEvidenceAndAvoidsUnsupportedClaims()
    {
        ModHealthReport baseReport = ModHealthReportFixtureFactory.CreateCanonical();
        ImmutableArray<ModHealthUpdate> updates = ImmutableArray.Create(
            CreateSlowUpdate(1), CreateSlowUpdate(2), CreateSlowUpdate(3)
        );
        ModHealthReport report = baseReport with
        {
            Performance = baseReport.Performance with { SlowUpdateCount = 3, WorstUpdates = updates }
        };

        ImmutableArray<ModHealthFinding> findings = new ModHealthInsightAnalyzer().Analyze(report);

        findings.Should().Contain(finding => finding.RuleId == "mostly-unattributed-slow-updates");
        string wording = string.Join("\n", findings.SelectMany(finding => new[] { finding.Summary, finding.Evidence, finding.SuggestedAction, finding.Limitation }));
        wording.Should().NotContain("caused your lag").And.NotContain("health score");
    }

    [Test]
    public void Analyze_InvalidTimingSuppressesPercentageFindings()
    {
        ModHealthReport baseReport = ModHealthReportFixtureFactory.CreateCanonical();
        ModHealthReport report = baseReport with
        {
            Capture = baseReport.Capture with { TimingValid = false },
            Performance = baseReport.Performance with
            {
                SlowUpdateCount = 3,
                WorstUpdates = ImmutableArray.Create(CreateSlowUpdate(1) with { TimingValid = false }, CreateSlowUpdate(2) with { TimingValid = false }, CreateSlowUpdate(3) with { TimingValid = false })
            }
        };

        ImmutableArray<ModHealthFinding> findings = new ModHealthInsightAnalyzer().Analyze(report);

        findings.Should().NotContain(finding => finding.RuleId == "mostly-unattributed-slow-updates" || finding.RuleId == "observed-mod-dominance");
    }

    [Test]
    public void Analyze_ReportsDirectFailuresInShortSample()
    {
        ModHealthReport baseReport = ModHealthReportFixtureFactory.CreateCanonical();
        ModHealthMod failed = baseReport.Mods[0] with { Status = ModHealthModStatus.Failed, CallbackFailureCount = 1 };
        ModHealthReport report = baseReport with
        {
            Capture = baseReport.Capture with { DurationMilliseconds = 1000, CompletedUpdateCount = 60, IsShortSample = true },
            Mods = ImmutableArray.Create(failed)
        };

        ImmutableArray<ModHealthFinding> findings = new ModHealthInsightAnalyzer().Analyze(report);

        findings.Select(finding => finding.RuleId).Should().Contain(new[] { "mod-load-problem", "callback-failure", "short-sample" });
    }

    [Test]
    public void Analyze_LedgerOnlyExplainsThatTimingIsUnavailableWithoutCallingItShort()
    {
        ModHealthReport baseReport = ModHealthReportFixtureFactory.CreateCanonical();
        ModHealthReport report = baseReport with
        {
            Capture = baseReport.Capture with
            {
                Mode = ModHealthCaptureMode.LedgerOnly,
                DurationMilliseconds = 0,
                CompletedUpdateCount = 0,
                IsShortSample = false,
                TimingValid = false
            },
            Mods = ImmutableArray<ModHealthMod>.Empty,
            Performance = baseReport.Performance with
            {
                SlowUpdateCount = 0,
                Callbacks = ImmutableArray<ModHealthCallback>.Empty,
                WorstUpdates = ImmutableArray<ModHealthUpdate>.Empty
            }
        };

        ImmutableArray<ModHealthFinding> findings = new ModHealthInsightAnalyzer().Analyze(report);

        findings.Should().ContainSingle(finding => finding.RuleId == "ledger-only");
        findings.Should().NotContain(finding => finding.RuleId == "short-sample" || finding.RuleId == "no-clear-observed-issue");
    }

    [Test]
    public void Analyze_ShortSampleSuppressesSustainedConclusionsButKeepsIndividualPeak()
    {
        ModHealthReport baseReport = ModHealthReportFixtureFactory.CreateCanonical();
        ImmutableArray<ModHealthUpdate> updates = ImmutableArray.Create(CreateSlowUpdate(1), CreateSlowUpdate(2), CreateSlowUpdate(3));
        ModHealthCallback peak = baseReport.Performance.Callbacks[0] with { MaximumMilliseconds = ModHealthReportLimits.ExtremeCallbackMilliseconds };
        ModHealthReport report = baseReport with
        {
            Capture = baseReport.Capture with { DurationMilliseconds = 1000, CompletedUpdateCount = 60, IsShortSample = true },
            Performance = baseReport.Performance with { SlowUpdateCount = 3, WorstUpdates = updates, Callbacks = ImmutableArray.Create(peak) }
        };

        ImmutableArray<ModHealthFinding> findings = new ModHealthInsightAnalyzer().Analyze(report);

        findings.Should().Contain(finding => finding.RuleId == "extreme-callback-peak");
        findings.Should().Contain(finding => finding.RuleId == "short-sample");
        findings.Should().NotContain(finding => finding.RuleId == "repeated-slow-updates");
        findings.Should().NotContain(finding => finding.RuleId == "observed-mod-dominance");
        findings.Should().NotContain(finding => finding.RuleId == "mostly-unattributed-slow-updates");
    }

    [Test]
    public void Analyze_DominanceUsesAllValidQualifyingSlowUpdateTime()
    {
        ModHealthReport baseReport = ModHealthReportFixtureFactory.CreateCanonical();
        ModHealthUpdate first = CreateSlowUpdate(1) with
        {
            ObservedModMilliseconds = 100,
            Contributors = ImmutableArray.Create(new ModHealthContributor("Example.Mod", 51))
        };
        ModHealthUpdate second = first with { UpdateTick = 2 };
        ModHealthUpdate third = first with
        {
            UpdateTick = 3,
            Contributors = ImmutableArray.Create(new ModHealthContributor("Other.Mod", 100))
        };
        ModHealthReport report = baseReport with
        {
            Performance = baseReport.Performance with { SlowUpdateCount = 3, WorstUpdates = ImmutableArray.Create(first, second, third) }
        };

        ImmutableArray<ModHealthFinding> findings = new ModHealthInsightAnalyzer().Analyze(report);

        findings.Should().NotContain(finding => finding.RuleId == "observed-mod-dominance");
    }

    private static ModHealthUpdate CreateSlowUpdate(uint tick)
    {
        return new(tick, tick * 20, 100, 80, 10, 5, true, 5, true, "gameplay", true, 0, 0, 0, 0, 0, 0, 0, true, ImmutableArray.Create(new ModHealthContributor("Example.Mod", 10)), null);
    }
}
