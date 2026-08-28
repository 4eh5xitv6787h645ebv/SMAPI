using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Engine;

/// <summary>The exact canonical Linux game-directory object observed by the installer.</summary>
public sealed record GameRootIdentity(
    string CanonicalPath,
    uint DeviceMajor,
    uint DeviceMinor,
    ulong Inode
)
{
    internal static GameRootIdentity From(string canonicalPath, LinuxFileIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(canonicalPath);
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Kind != LinuxAnchoredEntryKind.Directory)
            throw new ArgumentException("The game-root identity must describe a directory.", nameof(identity));
        return new GameRootIdentity(canonicalPath, identity.DeviceMajor, identity.DeviceMinor, identity.Inode);
    }

    internal bool Matches(LinuxFileIdentity identity)
    {
        return identity.Kind == LinuxAnchoredEntryKind.Directory
            && identity.DeviceMajor == this.DeviceMajor
            && identity.DeviceMinor == this.DeviceMinor
            && identity.Inode == this.Inode;
    }
}
