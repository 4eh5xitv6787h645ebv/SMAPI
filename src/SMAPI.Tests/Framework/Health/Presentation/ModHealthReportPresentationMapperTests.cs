using System.Collections.Immutable;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.Health;
using StardewModdingAPI.Framework.Health.Presentation;

namespace SMAPI.Tests.Framework.Health.Presentation;

[TestFixture]
internal sealed class ModHealthReportPresentationMapperTests
{
    private readonly ModHealthReportPresentationMapper Mapper = new();

    [Test]
    public void Map_PreparedModelMatchesPayloadProjection()
    {
        ModHealthReport report = ModHealthReportFixtureFactory.CreateCanonical();

        ModHealthReportPresentation fromModel = this.Mapper.Map(report);
        ModHealthReportPresentation fromPayload = this.Mapper.Map(CreatePayload(report));

        fromModel.Should().BeEquivalentTo(fromPayload);
    }

    [Test]
    public void Map_CanonicalPayloadProjectsAllEightSectionsFromFinalModel()
    {
        ModHealthReportPayload payload = new ModHealthReportPayloadFactory().Create(ModHealthReportFixtureFactory.CreateCanonical());

        ModHealthReportPresentation result = this.Mapper.Map(payload);

        result.SchemaVersion.Should().Be(payload.Model.SchemaVersion);
        result.Overview.Header.Should().BeSameAs(payload.Model.Header);
        result.Overview.Privacy.Should().BeSameAs(payload.Model.Privacy);
        result.Overview.PrivacyNotices.Should().Equal(ModHealthPresentationText.PrivacyNotices);
        result.Findings.Rows.Should().Equal(payload.Model.Findings);
        result.Capture.Details.Should().BeSameAs(payload.Model.Capture);
        result.Performance.Histogram.Should().BeSameAs(payload.Model.Performance.Histogram);
        result.Errors.LogTotals.Should().BeSameAs(payload.Model.LogTotals);
        result.Inventory.Summary.Should().BeSameAs(payload.Model.ModInventory);
        result.Context.Environment.Should().BeSameAs(payload.Model.Environment);
        result.Context.Completeness.Should().BeSameAs(payload.Model.Completeness);

        result.Performance.Callbacks.GetPage(0, 50).Should().Equal(payload.Model.Performance.Callbacks);
        result.Performance.Episodes.GetPage(0, 50).Should().Equal(payload.Model.Performance.Episodes);
        result.Errors.Logs.GetPage(0, 50).Should().Equal(payload.Model.Logs);
        result.Errors.CallbackFailures.GetPage(0, 50).Should().Equal(payload.Model.CallbackFailures);
        result.Inventory.Mods.GetPage(0, 50).Should().ContainSingle().Which.Id.Should().Be("Example.Mod");
    }

    [Test]
    public void Map_PreservesFindingOrderAndExactFieldsWithoutAnalyzingAgain()
    {
        ImmutableArray<ModHealthFinding> findings = ImmutableArray.Create(
            new ModHealthFinding("second", ModHealthFindingSeverity.Info, ModHealthFindingConfidence.Limited, null, "summary 2", "evidence 2", "same action", "limit 2"),
            new ModHealthFinding("first", ModHealthFindingSeverity.ActionNeeded, ModHealthFindingConfidence.Factual, "Example.Mod", "summary 1", "evidence 1", "same action", "limit 1"),
            new ModHealthFinding("third", ModHealthFindingSeverity.Check, ModHealthFindingConfidence.Possible, null, "summary 3", "evidence 3", "different action", "limit 3")
        );
        ModHealthReport report = ModHealthReportFixtureFactory.CreateCanonical() with { Findings = findings };

        ModHealthReportPresentation result = this.Mapper.Map(CreatePayload(report));

        result.Findings.Rows.Should().Equal(findings);
        result.Findings.SuggestedActions.Should().Equal("same action", "different action");
    }

    [Test]
    public void Map_MeasuredZeroIsDistinctFromUnavailableAndInvalid()
    {
        ModHealthReport canonical = ModHealthReportFixtureFactory.CreateCanonical();
        ModHealthUpdate canonicalUpdate = canonical.Performance.WorstUpdates[0];
        ModHealthReport measuredZero = canonical with
        {
            Performance = canonical.Performance with
            {
                TotalSmapiOtherMilliseconds = 0,
                SmapiOtherTimingAvailable = true,
                WorstUpdates = ImmutableArray.Create(canonicalUpdate with { SmapiOtherMilliseconds = 0, SmapiOtherTimingAvailable = true })
            }
        };
        ModHealthReport unavailable = measuredZero with
        {
            Performance = measuredZero.Performance with
            {
                SmapiOtherTimingAvailable = false,
                WorstUpdates = ImmutableArray.Create(measuredZero.Performance.WorstUpdates[0] with { SmapiOtherTimingAvailable = false })
            }
        };
        ModHealthReport invalid = measuredZero with { Capture = measuredZero.Capture with { TimingValid = false } };

        ModHealthReportPresentation measuredResult = this.Mapper.Map(CreatePayload(measuredZero));
        ModHealthReportPresentation unavailableResult = this.Mapper.Map(CreatePayload(unavailable));
        ModHealthReportPresentation invalidResult = this.Mapper.Map(CreatePayload(invalid));

        measuredResult.Performance.SmapiUpdateDispatch.Should().Be(new ModHealthMeasuredMilliseconds(ModHealthEvidenceState.Measured, 0));
        measuredResult.Performance.WorstUpdates.GetPage(0, 1)[0].SmapiUpdateDispatch.Should().Be(new ModHealthMeasuredMilliseconds(ModHealthEvidenceState.Measured, 0));
        unavailableResult.Performance.SmapiUpdateDispatch.State.Should().Be(ModHealthEvidenceState.Unavailable);
        unavailableResult.Performance.WorstUpdates.GetPage(0, 1)[0].SmapiUpdateDispatch.State.Should().Be(ModHealthEvidenceState.Unavailable);
        unavailableResult.Performance.Residual.State.Should().Be(ModHealthEvidenceState.Measured, "residual contains the folded unavailable category");
        invalidResult.Performance.SmapiUpdateDispatch.State.Should().Be(ModHealthEvidenceState.Invalid);
        invalidResult.Performance.BaseGameExclusive.State.Should().Be(ModHealthEvidenceState.Invalid);
        invalidResult.Performance.CanShowTimingPercentages.Should().BeFalse();
        invalidResult.Inventory.Mods.GetPage(0, 1)[0].InstrumentedTimeShare.State.Should().Be(ModHealthEvidenceState.Invalid);
    }

    [Test]
    public void Map_LedgerOnlyUsesNotApplicableInsteadOfTimingOrGcZeros()
    {
        ModHealthReport canonical = ModHealthReportFixtureFactory.CreateCanonical();
        ModHealthReport ledgerOnly = canonical with
        {
            Capture = canonical.Capture with { Mode = ModHealthCaptureMode.LedgerOnly, TimingValid = false, IsShortSample = false }
        };

        ModHealthReportPresentation result = this.Mapper.Map(CreatePayload(ledgerOnly));

        result.Performance.ObservedCallbacks.State.Should().Be(ModHealthEvidenceState.NotApplicable);
        result.Performance.SmapiUpdateDispatch.State.Should().Be(ModHealthEvidenceState.NotApplicable);
        result.Performance.Gc.State.Should().Be(ModHealthEvidenceState.NotApplicable);
        result.Inventory.Mods.GetPage(0, 1)[0].InstrumentedTimeShare.State.Should().Be(ModHealthEvidenceState.NotApplicable);
    }

    [Test]
    public void Map_GcUnavailableDoesNotExposePlaceholderCountsAsMeasured()
    {
        ModHealthReport canonical = ModHealthReportFixtureFactory.CreateCanonical();
        ModHealthUpdate update = canonical.Performance.WorstUpdates[0] with
        {
            GcCollectionDataValid = false,
            Gen0Collections = 4,
            Gen1Collections = 3,
            Gen2Collections = 2
        };
        ModHealthReport report = canonical with
        {
            Performance = canonical.Performance with
            {
                GcCollectionDataValid = false,
                Gen0Collections = 9,
                Gen1Collections = 8,
                Gen2Collections = 7,
                WorstUpdates = ImmutableArray.Create(update)
            }
        };

        ModHealthReportPresentation result = this.Mapper.Map(CreatePayload(report));

        result.Performance.Gc.Should().Be(new ModHealthGcPresentation(ModHealthEvidenceState.Unavailable, 0, 0, 0));
        result.Performance.WorstUpdates.GetPage(0, 1)[0].Gc.Should().Be(new ModHealthGcPresentation(ModHealthEvidenceState.Unavailable, 0, 0, 0));
    }

    [Test]
    public void Map_ProblemModsUsesCanonicalPredicateAndOriginalOrder()
    {
        ModHealthReport canonical = ModHealthReportFixtureFactory.CreateCanonical();
        ImmutableArray<ModHealthMod> mods = ImmutableArray.Create(
            canonical.Mods[0] with { Id = "loaded.clean", Name = "Clean" },
            canonical.Mods[0] with { Id = "loaded.warning", Name = "Warning", SessionWarningCount = 1 },
            canonical.Mods[0] with { Id = "loaded.error", Name = "Error", SessionErrorCount = 1 },
            canonical.Mods[0] with { Id = "loaded.capture-error", Name = "Capture error", CaptureErrorCount = 1 },
            canonical.Mods[0] with { Id = "loaded.failure", Name = "Failure", CallbackFailureCount = 1 },
            canonical.Mods[0] with { Id = "loaded.warning-flag", Name = "Warning flag", WarningFlags = ImmutableArray.Create("obsolete") },
            canonical.Mods[0] with { Id = "loaded.update", Name = "Update", UpdateStatus = ModHealthReportUpdateStatus.UpdateAvailable },
            canonical.Mods[0] with { Id = "skipped", Name = "Skipped", Status = ModHealthModStatus.Skipped }
        );
        ModHealthReport report = canonical with { Mods = mods };

        ModHealthReportPresentation result = this.Mapper.Map(CreatePayload(report));

        result.Attention.Mods.Count.Should().Be(7);
        result.Attention.Mods.GetPage(0, 50).Select(mod => mod.Id).Should().Equal(
            "loaded.warning",
            "loaded.error",
            "loaded.capture-error",
            "loaded.failure",
            "loaded.warning-flag",
            "loaded.update",
            "skipped"
        );
        result.Inventory.Mods.Count.Should().Be(8);
    }

    [Test]
    public void Map_TruncationAndCoreContextArraysRemainExplicit()
    {
        ModHealthReport canonical = ModHealthReportFixtureFactory.CreateCanonical();
        ModHealthReport report = canonical with
        {
            Header = canonical.Header with { IsTruncated = true, IsMinimalFallback = true, WriteRetry = true },
            Omissions = ImmutableArray.Create(new ModHealthOmission("none", 0), new ModHealthOmission("callbacks", 12)),
            Capacities = ImmutableArray.Create(new ModHealthCapacity("callbacks", 500, true)),
            Limitations = ImmutableArray.Create("one", "two")
        };

        ModHealthReportPresentation result = this.Mapper.Map(CreatePayload(report));

        result.Capture.Should().Match<ModHealthCapturePresentation>(capture => capture.IsTruncated && capture.IsMinimalFallback && capture.WriteRetry);
        result.Capture.PositiveOmissions.Should().Equal(new ModHealthOmission("callbacks", 12));
        result.Context.Omissions.Should().Equal(report.Omissions);
        result.Context.Capacities.Should().Equal(report.Capacities);
        result.Context.Limitations.Should().Equal("one", "two");
    }

    [Test]
    public void Map_LargeListsRemainSourceBackedAndPageBounded()
    {
        ModHealthReport canonical = ModHealthReportFixtureFactory.CreateCanonical();
        ImmutableArray<ModHealthMod> mods = Enumerable.Range(0, 120)
            .Select(index => canonical.Mods[0] with { Id = $"Example.Mod.{index:D3}" })
            .ToImmutableArray();
        ImmutableArray<ModHealthCallback> callbacks = Enumerable.Range(0, 120)
            .Select(index => canonical.Performance.Callbacks[0] with { Callback = $"Example.Callback.{index:D3}" })
            .ToImmutableArray();
        ModHealthReport report = canonical with
        {
            Mods = mods,
            Performance = canonical.Performance with { Callbacks = callbacks },
            ModInventory = canonical.ModInventory with { TotalDiscovered = mods.Length, Loaded = mods.Length, Retained = mods.Length }
        };

        ModHealthReportPresentation result = this.Mapper.Map(CreatePayload(report));

        result.Inventory.Mods.Count.Should().Be(120);
        result.Performance.Callbacks.Count.Should().Be(120);
        result.Inventory.Mods.GetPage(0, int.MaxValue).Should().HaveCount(50);
        result.Performance.Callbacks.GetPage(50, int.MaxValue).Should().HaveCount(50);
        result.Performance.Callbacks.GetPage(100, int.MaxValue).Should().HaveCount(20);
    }

    [Test]
    public void Wording_UsesRequiredPrivacyAndAttributionLimits()
    {
        ModHealthPresentationText.SmapiUpdateDispatchLabel.Should().Be("SMAPI update dispatch observed outside the base-game update");
        ModHealthPresentationText.TimingAttributionCaveat.Should().Contain("elapsed wall-clock").And.Contain("not total SMAPI CPU").And.Contain("proof of cause");
        ModHealthPresentationText.BaseGameCaveat.Should().Contain("Harmony").And.Contain("direct mod API work");
        ModHealthPresentationText.UnavailableSmapiTimingCaveat.Should().Contain("folded into residual");
        ModHealthPresentationText.GcCaveat.Should().Contain("process-wide correlation");
        ModHealthPresentationText.UpdateTickCaveat.Should().Contain("not a complete FPS");
        ModHealthPresentationText.DrawCaveat.Should().Contain("does not provide a complete draw");
        ModHealthPresentationText.NoUploadNotice.Should().Contain("No upload occurred");
        ModHealthPresentationText.PrivacyNotices.Should().Contain(ModHealthPresentationText.NoUploadNotice)
            .And.Contain(ModHealthPresentationText.NormalLogNotice)
            .And.Contain(ModHealthPresentationText.StandaloneParserNotice);
    }

    private static ModHealthReportPayload CreatePayload(ModHealthReport report)
    {
        return new(report, string.Empty, string.Empty, 0, 0);
    }
}
