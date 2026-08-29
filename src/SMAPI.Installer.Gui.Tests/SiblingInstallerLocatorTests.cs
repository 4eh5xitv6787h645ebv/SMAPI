using System.Runtime.Versioning;
using FluentAssertions;
using StardewModdingAPI.Installer.Gui.Backend;

namespace StardewModdingAPI.Installer.Gui.Tests;

[SupportedOSPlatform("linux")]
public sealed class SiblingInstallerLocatorTests
{
    private string Root = null!;

    [SetUp]
    public void SetUp()
    {
        this.Root = Path.Combine(Path.GetTempPath(), $"smapi-gui-locator-{Guid.NewGuid():N} ;$[]");
        Directory.CreateDirectory(this.Root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(this.Root))
            Directory.Delete(this.Root, true);
    }

    [Test]
    public void ResolvesOnlyExactExecutableSiblingWithPathMetacharacters()
    {
        string gui = Path.Combine(this.Root, "SMAPI.Installer.Gui");
        string backend = Path.Combine(this.Root, SiblingInstallerLocator.InstallerFileName);
        File.WriteAllText(gui, "gui");
        File.WriteAllText(backend, "backend");
        File.SetUnixFileMode(backend, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        SiblingInstallerLocator.Locate(gui).Should().Be(backend);
    }

    [Test]
    public void RejectsRelativeMissingNonExecutableAndSymlinkCandidates()
    {
        string gui = Path.Combine(this.Root, "SMAPI.Installer.Gui");
        File.WriteAllText(gui, "gui");

        FluentActions.Invoking(() => SiblingInstallerLocator.Locate("SMAPI.Installer.Gui")).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => SiblingInstallerLocator.Locate(gui)).Should().Throw<InvalidOperationException>();

        string backend = Path.Combine(this.Root, SiblingInstallerLocator.InstallerFileName);
        File.WriteAllText(backend, "backend");
        File.SetUnixFileMode(backend, UnixFileMode.UserRead);
        FluentActions.Invoking(() => SiblingInstallerLocator.Locate(gui)).Should().Throw<InvalidOperationException>();

        File.Delete(backend);
        string target = Path.Combine(this.Root, "target");
        File.WriteAllText(target, "backend");
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        File.CreateSymbolicLink(backend, target);
        FluentActions.Invoking(() => SiblingInstallerLocator.Locate(gui)).Should().Throw<InvalidOperationException>();
    }
}
