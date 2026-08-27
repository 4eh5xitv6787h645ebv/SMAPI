using System.Collections.Immutable;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.Health.Presentation;

namespace SMAPI.Tests.Framework.Health.Presentation;

[TestFixture]
internal sealed class ModHealthVirtualRowSourceTests
{
    [Test]
    public void GetPage_DefersProjectionAndCapsEachPage()
    {
        int projectionCount = 0;
        ModHealthVirtualRowSource<int, string> rows = new(
            Enumerable.Range(0, 120).ToImmutableArray(),
            value =>
            {
                projectionCount++;
                return value.ToString();
            }
        );

        rows.Count.Should().Be(120);
        projectionCount.Should().Be(0);

        ImmutableArray<string> page = rows.GetPage(10, 500);

        page.Should().HaveCount(ModHealthVirtualRowSource<int, string>.MaxPageSize);
        page[0].Should().Be("10");
        page[^1].Should().Be("59");
        projectionCount.Should().Be(ModHealthVirtualRowSource<int, string>.MaxPageSize);
    }

    [Test]
    public void GetPage_ClampsOffsetsAndDoesNotProjectOutsideRequestedRows()
    {
        int projectionCount = 0;
        ModHealthVirtualRowSource<int, int> rows = new(
            ImmutableArray.Create(2, 4, 6),
            value =>
            {
                projectionCount++;
                return value;
            }
        );

        rows.GetPage(-10, 2).Should().Equal(2, 4);
        rows.GetPage(99, 2).Should().BeEmpty();
        rows.GetPage(0, -1).Should().BeEmpty();
        projectionCount.Should().Be(2);
    }

    [Test]
    public void Where_StoresOnlyIndexesAndProjectsMatchingRowsOnDemand()
    {
        int projectionCount = 0;
        ModHealthVirtualRowSource<int, int> rows = ModHealthVirtualRowSource<int, int>.Where(
            Enumerable.Range(0, 20).ToImmutableArray(),
            value => value % 3 == 0,
            value =>
            {
                projectionCount++;
                return value * 10;
            }
        );

        rows.Count.Should().Be(7);
        projectionCount.Should().Be(0);
        rows.GetPage(1, 3).Should().Equal(30, 60, 90);
        projectionCount.Should().Be(3);
    }
}
