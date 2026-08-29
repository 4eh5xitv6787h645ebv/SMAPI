using FluentAssertions;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Gui.Frontend;

namespace StardewModdingAPI.Installer.Gui.Tests;

public sealed class DemoInstallerFrontendSessionTests
{
    [Test]
    public void ExposesEveryCoreOperationExactlyOnce()
    {
        DemoInstallerFrontendSession session = new();

        session.Operations.Select(p => p.Operation)
            .Should().BeEquivalentTo(Enum.GetValues<InstallerOperation>());
        session.Operations.Select(p => p.Operation)
            .Should().OnlyHaveUniqueItems();
    }

    [Test]
    public void UsesOnlyClearlySyntheticFolderAndReleaseData()
    {
        DemoInstallerFrontendSession session = new();

        session.Folders.Should().OnlyContain(p => p.Path.StartsWith("/home/demo/", StringComparison.Ordinal));
        session.Releases.Should().OnlyContain(p => p.Label.Contains("synthetic", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void PreviewCannotClaimMutationOrCompletion()
    {
        DemoInstallerFrontendSession session = new();

        FrontendPreview preview = session.CreatePreview(session.Folders[0], session.Releases[0], session.Operations[0]);

        preview.DurableState.Should().Be(ProtocolDurableState.Unchanged);
        preview.StateLabel.Should().Contain("Unchanged");
        preview.LogEntries.Should().Contain(p => p.Contains("No backend action ran", StringComparison.Ordinal));
        preview.LogEntries.Should().Contain(p => p.Contains("no files", StringComparison.Ordinal));
    }

    [Test]
    public void EveryFixedDisplayValueIsBoundedAndFreeOfControlOrBidiFormatting()
    {
        DemoInstallerFrontendSession session = new();
        List<string> values =
        [
            .. session.Folders.SelectMany(p => new[] { p.Label, p.Path, p.Detail }),
            .. session.Releases.SelectMany(p => new[] { p.Label, p.VersionLabel, p.Detail }),
            .. session.Operations.SelectMany(p => new[] { p.Label, p.Summary })
        ];

        foreach (OperationChoice operation in session.Operations)
        {
            FrontendPreview preview = session.CreatePreview(session.Folders[0], session.Releases[0], operation);
            values.AddRange([preview.Heading, preview.Summary, preview.StateLabel, .. preview.LogEntries]);
        }

        values.Should().OnlyContain(value => value.Length > 0 && value.Length <= DemoText.MaxDisplayLength);
        foreach (string value in values)
            ((Action)(() => DemoText.Validate(value))).Should().NotThrow();
    }

    [TestCase("unsafe\u202Etext")]
    [TestCase("unsafe\ntext")]
    public void DisplayGuardRejectsBidiAndControlData(string value)
    {
        ((Action)(() => DemoText.Validate(value))).Should().Throw<ArgumentException>();
    }
}
