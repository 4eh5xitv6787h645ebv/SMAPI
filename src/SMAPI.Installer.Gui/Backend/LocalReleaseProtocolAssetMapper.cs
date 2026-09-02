using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Protocol.V1;

namespace StardewModdingAPI.Installer.Gui.Backend;

/// <summary>Maps Core's untrusted local snapshot projection to the existing authenticated backend request.</summary>
internal static class LocalReleaseProtocolAssetMapper
{
    public static InstallerPackageOpenInput Map(LocalReleaseProtocolAssetPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new InstallerPackageOpenInput(
            paths.ReleaseTag,
            paths.UntrustedSourceCommitHint,
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
