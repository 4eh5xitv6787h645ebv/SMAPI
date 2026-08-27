using System;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Framework.Health.Viewer;
using StardewModdingAPI.Framework.Health.Viewer.Content;
using StardewModdingAPI.Framework.Health.Viewer.Game;

namespace SMAPI.Tests.Framework.Health.Viewer.Game;

[TestFixture]
internal sealed class ModHealthViewerTextTests
{
    [Test]
    public void ClipToWidth_UsesMeasuredWidthAndNeverCrossesBound()
    {
        static float Measure(string value) => value.Sum(character => character == 'W' ? 12 : 3);

        string clipped = ModHealthViewerText.ClipToWidth("WWWW narrow text", 35, Measure);

        clipped.Should().EndWith("…");
        Measure(clipped).Should().BeLessThanOrEqualTo(35);
        clipped.Should().NotBe(ModHealthViewerText.ClipToWidth("iiii narrow text", 35, Measure));
    }

    [Test]
    public void ClipToWidth_ReturnsEmptyWhenEvenEllipsisCannotFit()
    {
        ModHealthViewerText.ClipToWidth("long", 1, value => value.Length * 2).Should().BeEmpty();
    }

    [Test]
    public void Wrap_PreservesEveryCharacterInLongUnbrokenArtifactPath()
    {
        string path = $"HealthReports/{new string('a', 4000)}.json";

        string[] lines = ModHealthViewerText.Wrap(path, 37, value => value.Length).ToArray();

        lines.Should().OnlyContain(line => line.Length <= 37);
        string.Concat(lines).Should().Be(path);
    }

    [Test]
    public void Wrap_PreservesWhitespaceAndUnicodeScalarBoundariesInCanonicalValues()
    {
        string value = "mod name  with spaces 🌻 and\ttabs";

        string[] lines = ModHealthViewerText.Wrap(value, 11, text => text.Length).ToArray();

        string.Concat(lines).Should().Be(value);
        lines.Should().OnlyContain(line => !line.Any(character => char.IsSurrogate(character)) || char.IsSurrogatePair(line, line.IndexOf(line.First(char.IsSurrogate))));
    }

    [Test]
    public void ExpandedText_PagesWithKeyboardAndControllerNavigationCommands()
    {
        ModHealthViewerExpandedText text = new("label", string.Join('\n', Enumerable.Range(0, 11).Select(index => $"line-{index}")));
        text.Reflow(100, 3, value => value.Length);

        text.PageCount.Should().Be(4);
        text.Apply(ModHealthViewerNavigationCommand.PageDown);
        text.PageIndex.Should().Be(1);
        text.Apply(ModHealthViewerNavigationCommand.LastRow);
        text.PageIndex.Should().Be(3);
        text.Apply(ModHealthViewerNavigationCommand.NextSection);
        text.PageIndex.Should().Be(3);
        text.Apply(ModHealthViewerNavigationCommand.FirstRow);
        text.PageIndex.Should().Be(0);
    }

    [Test]
    public void RowCue_ContainsNonColorSeverityAndUniqueIconCues()
    {
        Enum.GetValues<ModHealthViewerRowSeverity>()
            .Select(severity => ModHealthViewerText.GetRowCue(severity, ModHealthViewerRowIconKey.Report))
            .Should().OnlyHaveUniqueItems();
        Enum.GetValues<ModHealthViewerRowIconKey>()
            .Select(icon => ModHealthViewerText.GetRowCue(ModHealthViewerRowSeverity.Neutral, icon))
            .Should().OnlyHaveUniqueItems();
    }

    [Test]
    public void ExpandedText_RejectsInputBeyondBoundInsteadOfGrowingWithoutLimit()
    {
        string value = string.Join('\n', Enumerable.Repeat("x", ModHealthViewerText.MaximumExpandedLines + 1));
        Action action = () => ModHealthViewerText.Wrap(value, 20, text => text.Length);

        action.Should().Throw<InvalidOperationException>();
    }
}
