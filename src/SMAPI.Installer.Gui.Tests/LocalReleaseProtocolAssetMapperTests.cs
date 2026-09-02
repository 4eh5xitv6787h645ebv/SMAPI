using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Gui.Backend;

namespace StardewModdingAPI.Installer.Gui.Tests;

internal sealed class LocalReleaseProtocolAssetMapperTests
{
    [Test]
    public void Map_PreservesExactProtocolOrderAndMarksTheCommitOnlyAsExistingRequestInput()
    {
        ConstructorInfo constructor = typeof(LocalReleaseProtocolAssetPaths)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Should().ContainSingle().Subject;
        LocalReleaseProtocolAssetPaths paths = (LocalReleaseProtocolAssetPaths)constructor.Invoke(
            ["tag", "untrusted-commit-hint", "package", "manifest", "checksums", "metadata", "bundle", "bundle-checksum", 11u, 22u, 33ul, 44L, 55u]
        );

        InstallerPackageOpenInput mapped = LocalReleaseProtocolAssetMapper.Map(paths);

        mapped.Should().Be(new InstallerPackageOpenInput(
            "tag",
            "untrusted-commit-hint",
            "package",
            "checksums",
            "metadata",
            "manifest",
            "bundle",
            "bundle-checksum",
            new ProtocolProcWorkspaceIdentity(11, 22, 33, 44, 55)
        ));
    }

    [Test]
    public void Map_NullProjection_IsRejected()
    {
        FluentActions.Invoking(() => LocalReleaseProtocolAssetMapper.Map(null!))
            .Should().Throw<ArgumentNullException>();
    }
}
