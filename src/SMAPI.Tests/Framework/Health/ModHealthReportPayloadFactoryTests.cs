using System;
using System.Collections.Immutable;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.Health;

namespace SMAPI.Tests.Framework.Health;

[TestFixture]
internal sealed class ModHealthReportPayloadFactoryTests
{
    [Test]
    public void Create_UsesOneFinalDtoForBothFormats()
    {
        ModHealthReportPayload payload = new ModHealthReportPayloadFactory().Create(ModHealthReportFixtureFactory.CreateCanonical());

        payload.Model.Findings.Should().NotBeEmpty();
        payload.Json.Should().Contain(payload.Model.Header.ReportId);
        payload.Text.Should().Contain(payload.Model.Header.ReportId);
        payload.TextByteCount.Should().Be(System.Text.Encoding.UTF8.GetByteCount(payload.Text));
        payload.JsonByteCount.Should().Be(System.Text.Encoding.UTF8.GetByteCount(payload.Json));
    }

    [Test]
    public void Create_PrunesInDeterministicOrder()
    {
        ModHealthReport report = ModHealthReportFixtureFactory.CreateCanonical();
        report = report with
        {
            Performance = report.Performance with { RecentUpdates = ImmutableArray.Create(report.Performance.WorstUpdates[0]) }
        };

        ModHealthReportPruner pruner = new();
        pruner.TryPrune(report, out ModHealthReport pruned).Should().BeTrue();

        pruned.Header.IsTruncated.Should().BeTrue();
        pruned.Performance.RecentUpdates.Should().BeEmpty();
        pruned.Omissions.Should().Contain(omission => omission.Section == "recentUpdates" && omission.Count == 1);
        pruned.Findings.Should().ContainSingle(finding => finding.RuleId == "capacity-reached");
    }

    [Test]
    public void CreateMinimalFallback_RetainsBoundedProblemFirstInventory()
    {
        ModHealthReport report = ModHealthReportFixtureFactory.CreateCanonical();
        ImmutableArray<ModHealthMod> mods = Enumerable.Range(0, 25)
            .Select(index => report.Mods[0] with { Id = $"Healthy.{index:00}", Name = $"Healthy {index:00}" })
            .Append(report.Mods[0] with { Id = "Problem.Z", Name = "Problem Z", Status = ModHealthModStatus.Failed })
            .Append(report.Mods[0] with
            {
                Id = "Problem.A",
                Name = "Problem A",
                CallbackFailureCount = 1,
                Dependencies = ImmutableArray.Create("Required.Mod"),
                WarningFlags = ImmutableArray.Create("deprecated-code")
            })
            .Reverse()
            .ToImmutableArray();
        report = report with
        {
            Mods = mods,
            ModInventory = report.ModInventory with { TotalDiscovered = mods.Length, Loaded = 25, Failed = 1, Retained = mods.Length }
        };

        ModHealthReport fallback = new ModHealthReportPruner().CreateMinimalFallback(report);

        fallback.Header.IsMinimalFallback.Should().BeTrue();
        fallback.Mods.Should().HaveCount(20);
        fallback.Mods.Take(2).Select(mod => mod.Id).Should().Equal("Problem.A", "Problem.Z");
        fallback.ModInventory.Retained.Should().Be(20);
        fallback.Mods.SelectMany(mod => mod.Dependencies).Should().BeEmpty();
        fallback.Mods.SelectMany(mod => mod.WarningFlags).Should().BeEmpty();
        fallback.Omissions.Should().Contain(omission => omission.Section == "minimalFallbackMods" && omission.Count == mods.Length - 20);
        fallback.Omissions.Should().Contain(omission => omission.Section == "minimalFallbackDependencies" && omission.Count == 1);
        fallback.Omissions.Should().Contain(omission => omission.Section == "minimalFallbackWarningFlags" && omission.Count == 1);
        fallback.Findings.Should().ContainSingle(finding => finding.RuleId == "capacity-reached");
    }

    [Test]
    public void Create_MaximumUnicodeFailureSignaturesPrunesBeforeFormattingAndFallbackFitsBound()
    {
        ModHealthReport canonical = ModHealthReportFixtureFactory.CreateCanonical();
        string identity = string.Concat(Enumerable.Repeat("😀", ModHealthReportLimits.MaxIdentityLength / 2));
        string callbackIdentity = string.Concat(Enumerable.Repeat("😀", ModHealthReportLimits.MaxCallbackNameLength / 2));
        ModHealthCallbackFailure failure = new(
            identity,
            identity,
            ModHealthExecutionPhase.Update,
            ModHealthOperationKind.Event,
            callbackIdentity,
            callbackIdentity,
            identity,
            1,
            1,
            0,
            1
        );
        ImmutableArray<ModHealthCallbackFailure> failures = Enumerable
            .Repeat(failure, ModHealthReportLimits.MaxCallbackFailures)
            .ToImmutableArray();
        ModHealthMod problem = canonical.Mods[0] with
        {
            Id = identity,
            Name = identity,
            CallbackFailureCount = failures.Length,
            SessionErrorCount = failures.Length
        };
        ModHealthReport large = canonical with
        {
            Mods = ImmutableArray.Create(problem),
            ModInventory = canonical.ModInventory with { Retained = 1 },
            CallbackFailureTotals = new ModHealthCallbackFailureTotals(failures.Length, failures.Length),
            CallbackFailures = failures
        };
        ModHealthReportPayloadFactory factory = new();
        factory.Create(canonical); // warm formatter and serializer before the allocation gate

        long firstBefore = GC.GetAllocatedBytesForCurrentThread();
        ModHealthReportPayload first = factory.Create(large);
        long firstAllocated = GC.GetAllocatedBytesForCurrentThread() - firstBefore;
        long secondBefore = GC.GetAllocatedBytesForCurrentThread();
        ModHealthReportPayload second = factory.Create(large);
        long secondAllocated = GC.GetAllocatedBytesForCurrentThread() - secondBefore;

        first.Model.Header.IsMinimalFallback.Should().BeTrue();
        first.Model.CallbackFailures.Should().BeEmpty();
        first.Model.CallbackFailureTotals.Should().Be(large.CallbackFailureTotals);
        first.Model.LogTotals.Should().Be(large.LogTotals);
        first.Model.Mods.Should().ContainSingle(mod =>
            mod.Id == identity
            && mod.CallbackFailureCount == failures.Length
            && mod.SessionErrorCount == failures.Length
        );
        first.Model.Omissions.Should().Contain(omission => omission.Section == "minimalFallbackCallbackFailures" && omission.Count == failures.Length);
        first.TextByteCount.Should().BeLessThanOrEqualTo(ModHealthReportLimits.MaxOutputBytes);
        first.JsonByteCount.Should().BeLessThanOrEqualTo(ModHealthReportLimits.MaxOutputBytes);
        second.Text.Should().Be(first.Text);
        second.Json.Should().Be(first.Json);
        firstAllocated.Should().BeLessThan(16 * 1024 * 1024);
        secondAllocated.Should().BeLessThan(16 * 1024 * 1024);
    }
}
