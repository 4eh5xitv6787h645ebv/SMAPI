using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Gui.Frontend;

namespace StardewModdingAPI.Installer.Gui.Backend;

/// <summary>
/// Snapshots one local six-asset folder through Core and retains it only for one existing backend package-open request.
/// </summary>
internal sealed class LocalReleasePackageService : ILocalReleasePackageService
{
    public async Task<IPreparedReleasePackage> PrepareAsync(
        string selectedDirectory,
        CancellationToken cancellationToken = default
    )
    {
        LocalReleaseAssetLease? lease = await LocalReleaseAssetImporter.ImportDirectoryAsync(
            selectedDirectory,
            cancellationToken
        ).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            InstallerPackageOpenInput package = LocalReleaseProtocolAssetMapper.Map(lease.Bind());
            cancellationToken.ThrowIfCancellationRequested();
            RetainedPreparedReleasePackage prepared = new(package, lease);
            lease = null;
            return prepared;
        }
        finally
        {
            if (lease is not null)
                await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

}
