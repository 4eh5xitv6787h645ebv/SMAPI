using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using StardewModdingAPI.Framework.Health;
using StardewModdingAPI.Framework.Health.Presentation;
using StardewModdingAPI.Framework.Health.Viewer.Content;
using StardewModdingAPI.Framework.Health.Viewer.Layout;

namespace SMAPI.Tests.Framework.Health.Viewer.Content;

[TestFixture]
internal sealed class ModHealthViewerContentAdapterTests
{
    [Test]
    public void Adapter_ExposesAllEightCanonicalSections()
    {
        ModHealthViewerContentAdapter adapter = CreateAdapter(ModHealthReportFixtureFactory.CreateCanonical());

        Enum.GetValues<ModHealthViewerSection>().Should().Equal(
            ModHealthViewerSection.Overview,
            ModHealthViewerSection.Findings,
            ModHealthViewerSection.Capture,
            ModHealthViewerSection.Attention,
            ModHealthViewerSection.Performance,
            ModHealthViewerSection.Errors,
            ModHealthViewerSection.Inventory,
            ModHealthViewerSection.Context
        );
        foreach (ModHealthViewerSection section in Enum.GetValues<ModHealthViewerSection>())
        {
            int count = adapter.GetRowCount(section);
            adapter.GetPage(section, 0, int.MaxValue).Should().HaveCount(Math.Min(count, ModHealthViewerContentAdapter.MaxPageSize));
        }
    }

    [Test]
    public void Findings_PreserveCanonicalOrderAndEveryCanonicalField()
    {
        ImmutableArray<ModHealthFinding> findings = ImmutableArray.Create(
            new ModHealthFinding("rule-z", ModHealthFindingSeverity.Info, ModHealthFindingConfidence.Limited, null, "First summary", "first evidence", "first action", "first limit"),
            new ModHealthFinding("rule-a", ModHealthFindingSeverity.ActionNeeded, ModHealthFindingConfidence.Factual, "Example.Mod", "Second summary", "second evidence", "second action", "second limit")
        );
        ModHealthReport report = ModHealthReportFixtureFactory.CreateCanonical() with { Findings = findings };

        ImmutableArray<ModHealthViewerDisplayRow> rows = CreateAdapter(report).GetPage(ModHealthViewerSection.Findings, 0, 50);

        rows.Select(row => row.StableId).Should().Equal(
            "finding-1-rule-z-none",
            "finding-2-rule-a-Example.Mod",
            "suggested-action-1",
            "suggested-action-2"
        );
        rows.Take(2).Select(row => row.Title).Should().Equal("First summary", "Second summary");
        rows[0].Detail.Should().Contain("Rule: rule-z").And.Contain("severity: info").And.Contain("confidence: limited").And.Contain("mod: none");
        rows[1].Detail.Should().Contain("Rule: rule-a").And.Contain("severity: action needed").And.Contain("confidence: factual").And.Contain("mod: Example.Mod");
        ImmutableArray<ModHealthViewerDetailRow> firstDetails = CreateAdapter(report).GetDetailPage(ModHealthViewerSection.Findings, 0, 0, 50);
        firstDetails.Should().Contain(row => row.Label == "Evidence" && row.Value == "first evidence")
            .And.Contain(row => row.Label == "Suggested action" && row.Value == "first action")
            .And.Contain(row => row.Label == "Limitation" && row.Value == "first limit");
        rows[1].Severity.Should().Be(ModHealthViewerRowSeverity.Error);
        rows.Skip(2).Select(row => row.Detail).Should().Equal("first action", "second action");
    }

    [Test]
    public void Paging_IsSourceBackedClampedAndLimitedToFiftyMaterializedRows()
    {
        ModHealthReport canonical = ModHealthReportFixtureFactory.CreateCanonical();
        ModHealthReportPresentation presentation = new ModHealthReportPresentationMapper().Map(canonical);
        ImmutableArray<ModHealthMod> mods = Enumerable.Range(0, 120)
            .Select(index => canonical.Mods[0] with { Id = $"Example.Mod.{index:D3}", Name = $"Mod {index:D3}" })
            .ToImmutableArray();
        ModHealthModPresentation template = presentation.Inventory.Mods.GetPage(0, 1)[0];
        int projected = 0;
        ModHealthVirtualRowSource<ModHealthMod, ModHealthModPresentation> source = new(mods, mod =>
        {
            projected++;
            return template with { Id = mod.Id, Name = mod.Name };
        });
        presentation = presentation with { Inventory = presentation.Inventory with { Mods = source } };
        ModHealthViewerContentAdapter adapter = new(presentation);

        adapter.GetRowCount(ModHealthViewerSection.Inventory).Should().Be(121);
        projected.Should().Be(0, "counting rows must not project the full inventory");

        ImmutableArray<ModHealthViewerDisplayRow> rows = adapter.GetPage(ModHealthViewerSection.Inventory, 1, int.MaxValue);

        rows.Should().HaveCount(50);
        projected.Should().Be(50);
        rows[0].StableId.Should().Be("mod-Example.Mod.000");
        rows[^1].StableId.Should().Be("mod-Example.Mod.049");
        adapter.GetPage(ModHealthViewerSection.Inventory, 111, 50).Should().HaveCount(10);
        adapter.GetPage(ModHealthViewerSection.Inventory, -10, -1).Should().BeEmpty();
        adapter.GetPage(ModHealthViewerSection.Inventory, int.MaxValue, 10).Should().BeEmpty();
    }

    [Test]
    public void Performance_DistinguishesMeasuredZeroUnavailableInvalidAndLedgerOnly()
    {
        ModHealthReport canonical = ModHealthReportFixtureFactory.CreateCanonical();
        ModHealthReport measuredZero = canonical with
        {
            Performance = canonical.Performance with { TotalSmapiOtherMilliseconds = 0, SmapiOtherTimingAvailable = true }
        };
        ModHealthReport unavailable = measuredZero with
        {
            Performance = measuredZero.Performance with { SmapiOtherTimingAvailable = false }
        };
        ModHealthReport invalid = measuredZero with
        {
            Capture = measuredZero.Capture with { TimingValid = false }
        };
        ModHealthReport ledgerOnly = measuredZero with
        {
            Capture = measuredZero.Capture with { Mode = ModHealthCaptureMode.LedgerOnly, TimingValid = false }
        };

        ModHealthViewerDisplayRow zeroRow = GetSmapiTimingRow(measuredZero);
        ModHealthViewerDisplayRow unavailableRow = GetSmapiTimingRow(unavailable);
        ModHealthViewerDisplayRow invalidRow = GetSmapiTimingRow(invalid);
        ModHealthViewerDisplayRow ledgerRow = GetSmapiTimingRow(ledgerOnly);

        zeroRow.Title.Should().Be(ModHealthPresentationText.SmapiUpdateDispatchLabel);
        zeroRow.Detail.Should().Be("0 ms (measured)");
        unavailableRow.Detail.Should().Be("Unavailable; the unseparated time is folded into residual.");
        invalidRow.Detail.Should().Be("Invalid timing evidence; timing percentages are hidden.");
        ledgerRow.Detail.Should().Be("Not applicable; this ledger-only report has no timed capture.");
        CreateAdapter(ledgerOnly).GetPage(ModHealthViewerSection.Performance, 6, 1)[0].Detail.Should().Contain("ledger-only report");
        CreateAdapter(ledgerOnly).GetDetailPage(ModHealthViewerSection.Inventory, 1, 0, 50)
            .Should().Contain(row => row.Label == "Instrumented time share" && row.Value == "not applicable; no timed capture");
    }

    [Test]
    public void Overview_UsesRequiredPrivacyWordingAndNeverIntroducesSensitivePayloads()
    {
        ModHealthViewerContentAdapter adapter = CreateAdapter(ModHealthReportFixtureFactory.CreateCanonical());

        ImmutableArray<ModHealthViewerDisplayRow> rows = adapter.GetPage(ModHealthViewerSection.Overview, 0, 50);
        rows.Select(row => row.Detail).Should().Contain(ModHealthPresentationText.InspectBeforeSharingNotice)
            .And.Contain(ModHealthPresentationText.NoUploadNotice)
            .And.Contain(ModHealthPresentationText.NormalLogNotice)
            .And.Contain(ModHealthPresentationText.StandaloneParserNotice);
        string allText = EnumerateAllText(adapter);
        allText.Should().NotContain("/home/").And.NotContain(" at Example.Mod.").And.NotContain("secret raw payload");
    }

    [Test]
    public void InventoryAndContext_PresentFullFieldsCapacitiesOmissionsAndLimitations()
    {
        ModHealthReport canonical = ModHealthReportFixtureFactory.CreateCanonical();
        ModHealthMod mod = canonical.Mods[0] with
        {
            ParentId = "Parent.Mod",
            FailureCategory = "dependency",
            WarningFlags = ImmutableArray.Create("obsolete", "broken-code"),
            Dependencies = ImmutableArray.Create("A.Mod", "B.Mod"),
            UpdateStatus = ModHealthReportUpdateStatus.UpdateAvailable,
            SuggestedUpdateVersion = "2.0.0"
        };
        ModHealthReport report = canonical with
        {
            Mods = ImmutableArray.Create(mod),
            Capacities = ImmutableArray.Create(new ModHealthCapacity("callbacks", 500, true), new ModHealthCapacity("mods", 2000, false)),
            Omissions = ImmutableArray.Create(new ModHealthOmission("callbacks", 14), new ModHealthOmission("mods", 0)),
            Limitations = ImmutableArray.Create("limit one", "limit two")
        };
        ModHealthViewerContentAdapter adapter = CreateAdapter(report);

        ModHealthViewerDisplayRow inventory = adapter.GetPage(ModHealthViewerSection.Inventory, 1, 1)[0];
        inventory.Detail.Length.Should().BeLessThanOrEqualTo(ModHealthViewerContentAdapter.MaxSummaryDetailCharacters);
        ImmutableArray<ModHealthViewerDetailRow> inventoryDetails = adapter.GetDetailPage(ModHealthViewerSection.Inventory, 1, 0, 50);
        inventoryDetails.Should().Contain(row => row.Label == "Mod ID" && row.Value == "Example.Mod")
            .And.Contain(row => row.Label == "Parent ID" && row.Value == "Parent.Mod")
            .And.Contain(row => row.Label == "Failure category" && row.Value == "dependency")
            .And.Contain(row => row.Label == "Warning flag" && row.Value == "obsolete")
            .And.Contain(row => row.Label == "Dependency ID" && row.Value == "B.Mod")
            .And.Contain(row => row.Label == "Suggested update version" && row.Value == "2.0.0")
            .And.Contain(row => row.Label == "Peak messages per second");

        ImmutableArray<ModHealthViewerDisplayRow> context = adapter.GetPage(ModHealthViewerSection.Context, 0, 50);
        context.Should().Contain(row => row.StableId == "capacity-callbacks" && row.Severity == ModHealthViewerRowSeverity.Warning && row.Detail.Contains("Limit: 500; reached: yes"));
        context.Should().Contain(row => row.StableId == "context-omission-callbacks" && row.Severity == ModHealthViewerRowSeverity.Warning && row.Detail.Contains("14 entries"));
        context.Select(row => row.Detail).Should().Contain("limit one").And.Contain("limit two");
    }

    [Test]
    public void Performance_IncludesCallbacksEpisodesWorstRecentErrorsAndFailures()
    {
        ModHealthReport canonical = ModHealthReportFixtureFactory.CreateCanonical();
        ModHealthUpdate update = canonical.Performance.WorstUpdates[0];
        ModHealthCallbackFailure failure = new("Example.Mod", "Example Mod", ModHealthExecutionPhase.Update, ModHealthOperationKind.Event, "Example.Mod.OnUpdate", "InvalidOperationException", null, 2, 1, 100, 200);
        ModHealthReport report = canonical with
        {
            Performance = canonical.Performance with { RecentUpdates = ImmutableArray.Create(update with { UpdateTick = 1300 }) },
            CallbackFailureTotals = new(2, 1),
            CallbackFailures = ImmutableArray.Create(failure)
        };
        ModHealthViewerContentAdapter adapter = CreateAdapter(report);

        ImmutableArray<ModHealthViewerDisplayRow> performance = adapter.GetPage(ModHealthViewerSection.Performance, 0, 50);
        int callbackIndex = performance
            .Select((row, index) => (row, index))
            .Single(pair => pair.row.IconKey == ModHealthViewerRowIconKey.Callback)
            .index;
        callbackIndex.Should().BeGreaterThanOrEqualTo(0);
        adapter.GetDetailPage(ModHealthViewerSection.Performance, callbackIndex, 0, 50)
            .Should().Contain(row => row.Label == "Callback" && row.Value == "Example.Mod.OnUpdate");
        performance.Should().Contain(row => row.IconKey == ModHealthViewerRowIconKey.Episode && row.Detail.Contains("representative update: 1200"));
        performance.Should().Contain(row => row.StableId == "worst-update-1200");
        performance.Should().Contain(row => row.StableId == "recent-update-1300");

        ImmutableArray<ModHealthViewerDisplayRow> errors = adapter.GetPage(ModHealthViewerSection.Errors, 0, 50);
        errors.Should().Contain(row => row.StableId == "callback-failure-totals" && row.Detail.Contains("during capture: 1"));
        errors.Should().Contain(row => row.IconKey == ModHealthViewerRowIconKey.Log && row.Detail.Contains("since ledger start:"));
        adapter.GetDetailPage(ModHealthViewerSection.Errors, 3, 0, 50)
            .Should().Contain(row => row.Label == "Exception type" && row.Value == "InvalidOperationException");
    }

    [Test]
    public void Findings_ExposeAnExplicitEmptyState()
    {
        ModHealthViewerContentAdapter adapter = CreateAdapter(ModHealthReportFixtureFactory.CreateCanonical());

        adapter.GetRowCount(ModHealthViewerSection.Findings).Should().Be(1);
        ModHealthViewerDisplayRow row = adapter.GetPage(ModHealthViewerSection.Findings, 0, 1)[0];
        row.Title.Should().Be("No findings were generated");
        row.Severity.Should().Be(ModHealthViewerRowSeverity.Positive);
    }

    [Test]
    public void Details_PageEveryWarningAndDependencyWithoutExpandingTheSummary()
    {
        ModHealthReport canonical = ModHealthReportFixtureFactory.CreateCanonical();
        ImmutableArray<string> warnings = Enumerable.Range(0, 120).Select(index => $"warning-{index:D3}").ToImmutableArray();
        ImmutableArray<string> dependencies = Enumerable.Range(0, 120).Select(index => $"Dependency.{index:D3}").ToImmutableArray();
        ModHealthReport report = canonical with
        {
            Mods = ImmutableArray.Create(canonical.Mods[0] with
            {
                Name = new string('N', ModHealthReportLimits.MaxIdentityLength),
                WarningFlags = warnings,
                Dependencies = dependencies
            })
        };
        ModHealthViewerContentAdapter adapter = CreateAdapter(report);

        ModHealthViewerDisplayRow summary = adapter.GetPage(ModHealthViewerSection.Inventory, 1, 1)[0];
        summary.Title.Length.Should().BeLessThanOrEqualTo(ModHealthViewerContentAdapter.MaxSummaryTitleCharacters);
        summary.Detail.Length.Should().BeLessThanOrEqualTo(ModHealthViewerContentAdapter.MaxSummaryDetailCharacters);

        int detailCount = adapter.GetDetailRowCount(ModHealthViewerSection.Inventory, 1);
        ImmutableArray<ModHealthViewerDetailRow> details = Enumerable.Range(0, (detailCount + 49) / 50)
            .SelectMany(page => adapter.GetDetailPage(ModHealthViewerSection.Inventory, 1, page * 50, int.MaxValue))
            .ToImmutableArray();
        details.Should().HaveCount(detailCount);
        details.Count(row => row.Label == "Warning flag").Should().Be(120);
        details.Count(row => row.Label == "Dependency ID").Should().Be(120);
        details.Should().Contain(row => row.Value == "warning-119").And.Contain(row => row.Value == "Dependency.119");
        adapter.GetDetailPage(ModHealthViewerSection.Inventory, 1, 0, int.MaxValue).Should().HaveCount(50);
    }

    [Test]
    public void Summary_DoesNotJoinMaximumDependencyPayloadsBeforeClipping()
    {
        ModHealthReport canonical = ModHealthReportFixtureFactory.CreateCanonical();
        ImmutableArray<string> dependencies = Enumerable.Range(0, ModHealthReportLimits.MaxDependenciesPerMod)
            .Select(index => $"{index:D3}-{new string('D', ModHealthReportLimits.MaxIdentityLength - 4)}")
            .ToImmutableArray();
        ModHealthReport report = canonical with
        {
            Mods = ImmutableArray.Create(canonical.Mods[0] with
            {
                Name = new string('N', ModHealthReportLimits.MaxIdentityLength),
                WarningFlags = dependencies,
                Dependencies = dependencies
            })
        };
        ModHealthViewerContentAdapter adapter = CreateAdapter(report);
        adapter.GetPage(ModHealthViewerSection.Inventory, 1, 1);

        long before = GC.GetAllocatedBytesForCurrentThread();
        string detail = "";
        for (int iteration = 0; iteration < 100; iteration++)
        {
            ImmutableArray<ModHealthViewerDisplayRow> page = adapter.GetPage(ModHealthViewerSection.Inventory, 1, 1);
            detail = page[0].Detail;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        detail.Should().Contain("warning flags: 256; dependencies: 256");
        allocated.Should().BeLessThan(1_000_000, "summary projection must not join the 128 KiB of bounded warning/dependency identity text");
    }

    [Test]
    public void Performance_AveragesAreFiniteOrExplicitlyUnavailable()
    {
        ModHealthReport canonical = ModHealthReportFixtureFactory.CreateCanonical();
        ModHealthCallback callback = canonical.Performance.Callbacks[0] with { CallCount = 0, TotalMilliseconds = 12 };
        ModHealthReport report = canonical with
        {
            Performance = canonical.Performance with
            {
                Histogram = canonical.Performance.Histogram with { Count = 0, SumMilliseconds = 12 },
                Callbacks = ImmutableArray.Create(callback)
            }
        };
        ModHealthViewerContentAdapter adapter = CreateAdapter(report);

        adapter.GetPage(ModHealthViewerSection.Performance, 0, 1)[0].Detail.Should().Contain("mean: unavailable");
        int callbackIndex = 7
            + ModHealthPresentationText.TimingCaveats.Length
            + new ModHealthReportPresentationMapper().Map(report).Performance.ObservedMods.Count;
        adapter.GetPage(ModHealthViewerSection.Performance, callbackIndex, 1)[0].Detail.Should().Contain("total/average/maximum: 12 ms / unavailable / 0.08 ms");
        adapter.GetDetailPage(ModHealthViewerSection.Performance, callbackIndex, 0, 50)
            .Should().Contain(row => row.Label == "Average" && row.Value == "unavailable");
    }

    [Test]
    public void PrivacyCanary_EnumeratesEverySummaryAndDetailPageAcrossTheSanitizedBoundary()
    {
        const string prohibited = "/home/private-user-canary/Blossom/private-save-canary";
        string sanitized = ModHealthTextSanitizer.SanitizeIdentity(prohibited);
        ModHealthReport canonical = ModHealthReportFixtureFactory.CreateCanonical();
        ImmutableArray<ModHealthMod> mods = Enumerable.Range(0, 120)
            .Select(index => canonical.Mods[0] with
            {
                Id = $"{sanitized}.{index:D3}",
                Name = $"{sanitized} {index:D3}",
                Dependencies = ImmutableArray.Create(sanitized)
            })
            .ToImmutableArray();
        ModHealthReport report = canonical with
        {
            Mods = mods,
            Environment = canonical.Environment with { SmapiCommit = sanitized, LinuxDistribution = sanitized, Kernel = sanitized },
            Limitations = ImmutableArray.Create(sanitized)
        };
        var prohibitedSources = new { SavePath = prohibited, RawLog = $"raw log {prohibited}", Configuration = $"config {prohibited}" };

        string allText = EnumerateAllText(CreateAdapter(report));

        allText.Should().NotContain(prohibited).And.NotContain("private-user-canary").And.Contain(sanitized);
        GC.KeepAlive(prohibitedSources);
    }

    [Test]
    public void Formatting_IsInvariantAndDeterministic()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            ModHealthViewerContentAdapter adapter = CreateAdapter(ModHealthReportFixtureFactory.CreateCanonical());

            ImmutableArray<ModHealthViewerDisplayRow> first = adapter.GetPage(ModHealthViewerSection.Performance, 0, 50);
            ImmutableArray<ModHealthViewerDisplayRow> second = adapter.GetPage(ModHealthViewerSection.Performance, 0, 50);

            first.Should().Equal(second);
            first[0].Detail.Should().Contain("mean: 16.8 ms").And.Contain("minimum: 15.2 ms").And.NotContain("15,2 ms");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Test]
    public void TranslationBoundary_DelegatesGeneratedContentAndPreservesCanonicalReportText()
    {
        ModHealthFinding finding = new(
            "canonical-rule",
            ModHealthFindingSeverity.ActionNeeded,
            ModHealthFindingConfidence.Factual,
            "Example.Mod",
            "Canonical summary",
            "Canonical evidence",
            "Canonical suggested action",
            "Canonical limitation"
        );
        ModHealthReport report = ModHealthReportFixtureFactory.CreateCanonical() with
        {
            Findings = ImmutableArray.Create(finding)
        };
        List<string> requestedKeys = new();
        string Translate(string key)
        {
            requestedKeys.Add(key);
            return key switch
            {
                "health-view.content.summary.report.title" => "Translated report {0}",
                "health-view.content.summary.finding.detail" => "Translated rule {0}; severity {1}; confidence {2}; mod {3}.",
                "health-view.content.enum.finding-severity.actionneeded" => "translated action needed",
                "health-view.content.enum.confidence.factual" => "translated factual",
                "health-view.content.label.evidence" => "Translated evidence label",
                _ => key
            };
        }
        ModHealthViewerContentAdapter adapter = new(new ModHealthReportPresentationMapper().Map(report), Translate);

        ImmutableArray<ModHealthViewerDisplayRow> overviewRows = adapter.GetPage(ModHealthViewerSection.Overview, 0, 50);
        ModHealthViewerDisplayRow overview = overviewRows[0];
        ModHealthViewerDisplayRow findingRow = adapter.GetPage(ModHealthViewerSection.Findings, 0, 1)[0];
        ImmutableArray<ModHealthViewerDetailRow> details = adapter.GetDetailPage(ModHealthViewerSection.Findings, 0, 0, 50);
        ImmutableArray<ModHealthViewerDisplayRow> timingCaveats = adapter.GetPage(ModHealthViewerSection.Performance, 7, ModHealthPresentationText.TimingCaveats.Length);

        overview.Title.Should().StartWith("Translated report ").And.EndWith(report.Header.ReportId);
        findingRow.Title.Should().Be("Canonical summary", "schema-v1 finding prose is report-owned canonical text");
        findingRow.Detail.Should().Contain("severity translated action needed").And.Contain("confidence translated factual");
        details.Should().Contain(row => row.Label == "Translated evidence label" && row.Value == "Canonical evidence")
            .And.Contain(row => row.Value == "Canonical suggested action")
            .And.Contain(row => row.Value == "Canonical limitation");
        overviewRows.Skip(2).Select(row => row.Detail).Should().Equal(ModHealthPresentationText.PrivacyNotices);
        timingCaveats.Select(row => row.Detail).Should().Equal(ModHealthPresentationText.TimingCaveats);
        requestedKeys.Should().Contain("health-view.content.summary.report.title")
            .And.Contain("health-view.content.summary.finding.detail")
            .And.Contain("health-view.content.enum.finding-severity.actionneeded")
            .And.Contain("health-view.content.label.evidence");
        requestedKeys.Should().OnlyContain(key => key.StartsWith("health-view.content.", StringComparison.Ordinal));
    }

    [Test]
    public void FrozenReport_PreservesFindingSemanticsAcrossViewerTextAndJson()
    {
        ImmutableArray<ModHealthFinding> findings = ImmutableArray.Create(
            new ModHealthFinding("rule-first", ModHealthFindingSeverity.Check, ModHealthFindingConfidence.Possible, "First.Mod", "First summary", "First evidence", "First action", "First limitation"),
            new ModHealthFinding("rule-second", ModHealthFindingSeverity.ActionNeeded, ModHealthFindingConfidence.Factual, null, "Second summary", "Second evidence", "Second action", "Second limitation")
        );
        ModHealthReport report = ModHealthReportFixtureFactory.CreateCanonical() with { Findings = findings };

        string textReport = new ModHealthReportTextFormatter().Format(report);
        JObject jsonReport = JObject.Parse(new ModHealthReportJsonSerializer().Serialize(report));
        ModHealthViewerContentAdapter viewer = CreateAdapter(report);
        ImmutableArray<ModHealthViewerDisplayRow> viewerRows = viewer.GetPage(ModHealthViewerSection.Findings, 0, 50);

        jsonReport.Value<string>("schemaVersion").Should().Be(report.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        jsonReport["header"]!.Value<string>("reportId").Should().Be(report.Header.ReportId);
        textReport.Should().Contain($"Report ID: {report.Header.ReportId}");
        viewer.GetPage(ModHealthViewerSection.Overview, 0, 1)[0].Title.Should().Contain(report.Header.ReportId);
        viewerRows.Take(findings.Length).Select(row => row.Title).Should().Equal(findings.Select(finding => finding.Summary));

        JArray jsonFindings = (JArray)jsonReport["findings"]!;
        jsonFindings.Should().HaveCount(findings.Length);
        for (int index = 0; index < findings.Length; index++)
        {
            ModHealthFinding finding = findings[index];
            JObject jsonFinding = (JObject)jsonFindings[index]!;
            ImmutableArray<ModHealthViewerDetailRow> viewerDetails = viewer.GetDetailPage(ModHealthViewerSection.Findings, index, 0, 50);

            jsonFinding.Value<string>("ruleId").Should().Be(finding.RuleId);
            jsonFinding.Value<string>("summary").Should().Be(finding.Summary);
            jsonFinding.Value<string>("evidence").Should().Be(finding.Evidence);
            jsonFinding.Value<string>("suggestedAction").Should().Be(finding.SuggestedAction);
            jsonFinding.Value<string>("limitation").Should().Be(finding.Limitation);
            textReport.Should().Contain(finding.Summary).And.Contain(finding.Evidence).And.Contain(finding.SuggestedAction).And.Contain(finding.Limitation);
            viewerRows[index].Detail.Should().Contain(finding.RuleId);
            viewerDetails.Should().Contain(row => row.Label == "Evidence" && row.Value == finding.Evidence)
                .And.Contain(row => row.Label == "Suggested action" && row.Value == finding.SuggestedAction)
                .And.Contain(row => row.Label == "Limitation" && row.Value == finding.Limitation);
        }
        textReport.IndexOf(findings[0].Summary, StringComparison.Ordinal).Should().BeLessThan(textReport.IndexOf(findings[1].Summary, StringComparison.Ordinal));
        viewerRows.Skip(findings.Length).Select(row => row.Detail).Should().Equal(findings.Select(finding => finding.SuggestedAction));
    }

    [Test]
    public void TranslationBoundary_UsesNonEnglishValuesAndFallsBackForMissingOrMalformedFormats()
    {
        ModHealthReport report = ModHealthReportFixtureFactory.CreateCanonical();

        ModHealthViewerContentAdapter translated = new(
            new ModHealthReportPresentationMapper().Map(report),
            key => key switch
            {
                "health-view.content.summary.report.title" => "Bericht {0}",
                "health-view.content.summary.report.detail" => "Fehlerhaft {9}",
                _ => key
            }
        );

        ModHealthViewerDisplayRow row = translated.GetPage(ModHealthViewerSection.Overview, 0, 1)[0];
        row.Title.Should().Be($"Bericht {report.Header.ReportId}");
        row.Detail.Should().StartWith($"Schema {report.SchemaVersion};", "a malformed translated format must use the complete built-in fallback");
        translated.GetPage(ModHealthViewerSection.Overview, 1, 1)[0].Title.Should().Be("Privacy summary", "a missing translation key must use the built-in fallback");
    }

    private static ModHealthViewerDisplayRow GetSmapiTimingRow(ModHealthReport report)
    {
        return CreateAdapter(report).GetPage(ModHealthViewerSection.Performance, 3, 1)[0];
    }

    private static ModHealthViewerContentAdapter CreateAdapter(ModHealthReport report)
    {
        return new(new ModHealthReportPresentationMapper().Map(report));
    }

    private static string EnumerateAllText(ModHealthViewerContentAdapter adapter)
    {
        ImmutableArray<string>.Builder text = ImmutableArray.CreateBuilder<string>();
        foreach (ModHealthViewerSection section in Enum.GetValues<ModHealthViewerSection>())
        {
            int rowCount = adapter.GetRowCount(section);
            for (int pageOffset = 0; pageOffset < rowCount; pageOffset += ModHealthViewerContentAdapter.MaxPageSize)
            {
                ImmutableArray<ModHealthViewerDisplayRow> rows = adapter.GetPage(section, pageOffset, int.MaxValue);
                for (int rowOffset = 0; rowOffset < rows.Length; rowOffset++)
                {
                    int rowIndex = pageOffset + rowOffset;
                    text.Add(rows[rowOffset].Title);
                    text.Add(rows[rowOffset].Detail);
                    int detailCount = adapter.GetDetailRowCount(section, rowIndex);
                    for (int detailOffset = 0; detailOffset < detailCount; detailOffset += ModHealthViewerContentAdapter.MaxPageSize)
                    {
                        foreach (ModHealthViewerDetailRow detail in adapter.GetDetailPage(section, rowIndex, detailOffset, int.MaxValue))
                        {
                            text.Add(detail.Label);
                            text.Add(detail.Value);
                        }
                    }
                }
            }
        }
        return string.Join('\n', text);
    }
}
