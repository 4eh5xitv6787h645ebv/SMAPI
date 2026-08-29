using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Protocol.V1;

namespace StardewModdingAPI.Installer.Gui.Backend;

/// <summary>Maps Core's nonforgeable retained acquisition projection to the existing protocol request shape.</summary>
internal static class ReviewedReleaseProtocolAssetMapper
{
    public static InstallerPackageOpenInput Map(ReviewedReleaseProtocolAssetPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new InstallerPackageOpenInput(
            paths.ReleaseTag,
            paths.SourceCommit,
            paths.InstallerPackagePath,
            paths.ChecksumsPath,
            paths.BuildMetadataPath,
            paths.InstallManifestPath,
            paths.AttestationBundlePath,
            paths.AttestationBundleChecksumPath,
            new ProtocolProcWorkspaceIdentity(
                paths.WorkspaceDeviceMajor,
                paths.WorkspaceDeviceMinor,
                paths.WorkspaceInode,
                paths.WorkspaceChangeSeconds,
                paths.WorkspaceChangeNanoseconds
            )
        );
    }
}
