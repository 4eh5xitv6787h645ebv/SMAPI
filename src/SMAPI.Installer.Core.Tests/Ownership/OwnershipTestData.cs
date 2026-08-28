using StardewModdingAPI.Installer.Core.Ownership;

namespace StardewModdingAPI.Installer.Core.Tests.Ownership;

internal static class OwnershipTestData
{
    public static NormalizedRelativePath Path(string value) => NormalizedRelativePath.Parse(value);

    public static Sha256Digest Digest(char value) => Sha256Digest.Parse(new string(value, 64));

    public static InstallationReleaseIdentity Release(int alpha = 1, char packageHash = 'a')
    {
        string version = $"4.5.{alpha + 2}";
        return new InstallationReleaseIdentity(
            InstallationReleaseIdentity.ReviewedRepository,
            $"fork-4eh5xitv6787h645ebv-linux-v{version}-alpha.{alpha}",
            $"{version}-unofficial.4eh5xitv6787h645ebv.linux.alpha.{alpha}",
            $"SMAPI-{version}-unofficial.4eh5xitv6787h645ebv.linux.alpha.{alpha}-linux-x64-installer.zip",
            new string('b', 40),
            new string('c', 40),
            Digest(packageHash)
        );
    }

    public static PackageManifestEntry Entry(
        string path,
        char digest,
        OwnedEntryKind kind,
        int mode = 420,
        long size = 10
    ) => new(Path(path), Digest(digest), size, mode, kind);

    public static PackageManifest Manifest(
        InstallationReleaseIdentity? release = null,
        char launcherDigest = '1',
        params PackageManifestEntry[] otherEntries
    )
    {
        return new PackageManifest(
            release ?? Release(),
            otherEntries.Append(Entry("StardewValley", launcherDigest, OwnedEntryKind.Launcher, mode: 493))
        );
    }

    public static InstallationReceipt Receipt(PackageManifest manifest, char originalLauncherDigest = 'f')
    {
        PackageManifestEntry launcher = manifest.Entries.Single(entry => entry.Kind == OwnedEntryKind.Launcher);
        return new InstallationReceipt(
            manifest.Release,
            manifest.GetCanonicalDigest(),
            new string('d', 32),
            manifest.Entries.Select(entry => new InstallationReceiptEntry(entry.Path, entry.Sha256, entry.UnixMode, entry.Kind)),
            new LauncherReceipt(launcher.Sha256, Digest(originalLauncherDigest))
        );
    }

    public static CurrentFile Current(PackageManifestEntry entry, char? digest = null, int? mode = null)
    {
        return new CurrentFile(entry.Path, digest.HasValue ? Digest(digest.Value) : entry.Sha256, mode ?? entry.UnixMode);
    }
}
