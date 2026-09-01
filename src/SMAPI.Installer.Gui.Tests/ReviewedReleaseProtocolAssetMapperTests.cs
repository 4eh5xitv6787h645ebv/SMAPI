using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Gui.Backend;

namespace StardewModdingAPI.Installer.Gui.Tests;

internal sealed class ReviewedReleaseProtocolAssetMapperTests
{
    [Test]
    public void Map_PreservesExactProtocolOrderWithoutWideningClientSurface()
    {
        ConstructorInfo constructor = typeof(ReviewedReleaseProtocolAssetPaths)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Should().ContainSingle().Subject;
        ReviewedReleaseProtocolAssetPaths paths = (ReviewedReleaseProtocolAssetPaths)constructor.Invoke(
            ["tag", "commit", "package", "manifest", "checksums", "metadata", "bundle", "bundle-checksum", 1u, 2u, 3ul, 4L, 5u]
        );

        InstallerPackageOpenInput mapped = ReviewedReleaseProtocolAssetMapper.Map(paths);

        mapped.Should().Be(new InstallerPackageOpenInput(
            "tag",
            "commit",
            "package",
            "checksums",
            "metadata",
            "manifest",
            "bundle",
            "bundle-checksum",
            new ProtocolProcWorkspaceIdentity(1, 2, 3, 4, 5)
        ));
        typeof(IInstallerProtocolClient).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => method.Name)
            .Should().Equal(
                "get_SessionFaulted",
                nameof(IInstallerProtocolClient.HandshakeAsync),
                nameof(IInstallerProtocolClient.OpenPackageAsync),
                nameof(IInstallerProtocolClient.DiscoverGamesAsync),
                nameof(IInstallerProtocolClient.ValidateGameAsync),
                nameof(IInstallerProtocolClient.InspectPlanAsync),
                nameof(IInstallerProtocolClient.ApprovePlanCandidatesAsync),
                nameof(IInstallerProtocolClient.ConfirmPlanAsync)
            );
    }
}
