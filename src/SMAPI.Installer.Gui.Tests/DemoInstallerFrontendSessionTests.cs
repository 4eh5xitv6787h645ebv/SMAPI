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

        session.IsDemoMode.Should().BeTrue();
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
}
