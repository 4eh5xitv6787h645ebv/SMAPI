namespace StardewModdingAPI.Installer.Gui.Frontend;

/// <summary>The production boundary which prepares one user-selected local release folder for backend verification.</summary>
internal interface ILocalReleasePackageService
{
    /// <summary>
    /// Snapshot the selected folder into private retained storage and return an unauthenticated package-open input.
    /// Trust is established only if the direct-child backend accepts that input.
    /// </summary>
    Task<IPreparedReleasePackage> PrepareAsync(
        string selectedDirectory,
        CancellationToken cancellationToken = default
    );
}
