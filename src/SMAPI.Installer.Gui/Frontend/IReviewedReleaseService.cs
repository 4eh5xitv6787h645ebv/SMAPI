using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Gui.Backend;

namespace StardewModdingAPI.Installer.Gui.Frontend;

/// <summary>The bounded stages exposed while one reviewed public release is prepared.</summary>
internal enum ReviewedReleasePreparationStage
{
    ObservingTag,
    Downloading,
    RefreshingTag
}

/// <summary>Sanitized aggregate progress which never exposes a download URI or private workspace path.</summary>
internal sealed record ReviewedReleasePreparationProgress(
    ReviewedReleasePreparationStage Stage,
    ReviewedReleaseAssetKind? AssetKind,
    int CompletedAssets,
    int TotalAssets,
    long TransferredBytes,
    long TotalBytes
);

/// <summary>A live retained package set prepared for one backend package-open request.</summary>
internal interface IPreparedReleasePackage : IAsyncDisposable
{
    /// <summary>The package-open input. This remains valid only while this owner is live.</summary>
    InstallerPackageOpenInput Package { get; }
}

/// <summary>The production release-catalog and package-preparation boundary used by the GUI controller.</summary>
internal interface IReviewedReleaseService : IAsyncDisposable
{
    /// <summary>Load the current bounded catalog of complete, compatible fork releases.</summary>
    Task<IReadOnlyList<ReviewedReleaseCandidate>> LoadCatalogAsync(
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Observe the selected annotated tag, acquire its exact six assets, refresh that tag, resolve its commit, and
    /// bind the retained asset set for the direct-child backend.
    /// </summary>
    Task<IPreparedReleasePackage> PrepareAsync(
        ReviewedReleaseCandidate candidate,
        IProgress<ReviewedReleasePreparationProgress>? progress = null,
        CancellationToken cancellationToken = default
    );
}
