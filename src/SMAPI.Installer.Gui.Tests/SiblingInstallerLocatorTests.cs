using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;
using StardewModdingAPI.Installer.Core.Security;
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
    public void RetainsOnlyExactExecutableSiblingWithPathMetacharacters()
    {
        string gui = Path.Combine(this.Root, "SMAPI.Installer.Gui");
        string backend = Path.Combine(this.Root, SiblingInstallerLocator.InstallerFileName);
        File.WriteAllText(gui, "gui");
        File.WriteAllText(backend, "backend");
        File.SetUnixFileMode(backend, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        using LinuxExternalExecutableLease lease = SiblingInstallerLocator.OpenSibling(gui);
        File.ReadAllText(lease.ProcPath).Should().Be("backend");
    }

    [Test]
    public void RejectsRelativeMissingNonExecutableAndSymlinkCandidates()
    {
        string gui = Path.Combine(this.Root, "SMAPI.Installer.Gui");
        File.WriteAllText(gui, "gui");

        FluentActions.Invoking(() => SiblingInstallerLocator.OpenSibling("SMAPI.Installer.Gui")).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => SiblingInstallerLocator.OpenSibling(gui)).Should().Throw<InvalidOperationException>();

        string backend = Path.Combine(this.Root, SiblingInstallerLocator.InstallerFileName);
        File.WriteAllText(backend, "backend");
        File.SetUnixFileMode(backend, UnixFileMode.UserRead);
        FluentActions.Invoking(() => SiblingInstallerLocator.OpenSibling(gui)).Should().Throw<InvalidOperationException>();

        File.Delete(backend);
        string target = Path.Combine(this.Root, "target");
        File.WriteAllText(target, "backend");
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        File.CreateSymbolicLink(backend, target);
        FluentActions.Invoking(() => SiblingInstallerLocator.OpenSibling(gui)).Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void RetainedSiblingLeaseRejectsHardlinksAndFifosWithoutBlocking()
    {
        string gui = Path.Combine(this.Root, "SMAPI.Installer.Gui");
        string backend = Path.Combine(this.Root, SiblingInstallerLocator.InstallerFileName);
        string hardlinkSource = Path.Combine(this.Root, "hardlink-source");
        File.WriteAllText(gui, "gui");
        File.WriteAllText(hardlinkSource, "backend");
        File.SetUnixFileMode(hardlinkSource, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        link(hardlinkSource, backend).Should().Be(0);
        FluentActions.Invoking(() => SiblingInstallerLocator.OpenSibling(gui)).Should().Throw<InvalidOperationException>();

        File.Delete(backend);
        mkfifo(backend, 0x1c0).Should().Be(0);
        FluentActions.Invoking(() => SiblingInstallerLocator.OpenSibling(gui)).Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void RetainedSiblingProcPathExistsOnlyForLeaseLifetime()
    {
        string gui = Path.Combine(this.Root, "SMAPI.Installer.Gui");
        string backend = Path.Combine(this.Root, SiblingInstallerLocator.InstallerFileName);
        File.WriteAllText(gui, "gui");
        File.WriteAllText(backend, "backend");
        File.SetUnixFileMode(backend, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        LinuxExternalExecutableLease lease = SiblingInstallerLocator.OpenSibling(gui);
        string procPath = lease.ProcPath;
        File.Exists(procPath).Should().BeTrue();

        lease.Dispose();
        File.Exists(procPath).Should().BeFalse();
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string path, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string oldPath, string newPath);
}
