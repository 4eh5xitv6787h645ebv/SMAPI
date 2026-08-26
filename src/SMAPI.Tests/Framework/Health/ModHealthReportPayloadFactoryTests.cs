using System.Collections.Immutable;
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
    }
}
