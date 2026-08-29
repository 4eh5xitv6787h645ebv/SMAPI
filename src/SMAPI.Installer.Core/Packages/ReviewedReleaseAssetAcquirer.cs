using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Packages;

/// <summary>Bounded aggregate progress for the fixed reviewed-release asset set.</summary>
public sealed record ReviewedReleaseAcquisitionProgress(
    ReviewedReleaseAssetKind AssetKind,
    int CompletedAssets,
    int TotalAssets,
    long CurrentAssetBytes,
    long CurrentAssetSizeBytes,
    long CompletedBytes,
    long TotalBytes
);

/// <summary>
/// The exact six retained process-descriptor paths bound to one freshly resolved reviewed tag. This remains internal
/// until the dependent protocol opener can validate cross-process retained-directory descriptor paths explicitly.
/// </summary>
internal sealed class ReviewedReleaseProtocolAssetPaths
{
    public string ReleaseTag { get; }
    public string SourceCommit { get; }
    public string InstallerPackagePath { get; }
    public string InstallManifestPath { get; }
    public string ChecksumsPath { get; }
    public string BuildMetadataPath { get; }
    public string AttestationBundlePath { get; }
    public string AttestationBundleChecksumPath { get; }

    internal ReviewedReleaseProtocolAssetPaths(
        string releaseTag,
        string sourceCommit,
        string installerPackagePath,
        string installManifestPath,
        string checksumsPath,
        string buildMetadataPath,
        string attestationBundlePath,
        string attestationBundleChecksumPath
    )
    {
        this.ReleaseTag = releaseTag;
        this.SourceCommit = sourceCommit;
        this.InstallerPackagePath = installerPackagePath;
        this.InstallManifestPath = installManifestPath;
        this.ChecksumsPath = checksumsPath;
        this.BuildMetadataPath = buildMetadataPath;
        this.AttestationBundlePath = attestationBundlePath;
        this.AttestationBundleChecksumPath = attestationBundleChecksumPath;
    }
}

/// <summary>A retained private acquisition whose descriptor paths stay valid only while this lease is held.</summary>
/// <remarks>
/// This slice owns acquisition authority only. Its internal process-descriptor projection isn't accepted by the
/// current production protocol opener; that requires the immediately dependent strict cross-process proc-directory
/// anchoring change. A process crash can leave one bounded random private workspace; normal disposal never performs
/// recursive or pathname-fallback deletion.
/// </remarks>
public sealed class ReviewedReleaseAssetLease : IAsyncDisposable
{
    private readonly ReviewedReleaseCandidate Candidate;
    private readonly ReviewedReleaseAssetWorkspace Workspace;

    internal ReviewedReleaseAssetLease(ReviewedReleaseCandidate candidate, ReviewedReleaseAssetWorkspace workspace)
    {
        this.Candidate = candidate;
        this.Workspace = workspace;
    }

    /// <summary>The exact release tag selected from the reviewed catalog.</summary>
    public string ReleaseTag => this.Candidate.Identity.Tag;

    /// <summary>
    /// Bind the retained assets to a freshly resolved tag authority for this exact candidate. The projection remains
    /// internal until a dependent backend change can consume proc-directory capabilities without a named-path fallback.
    /// </summary>
    internal ReviewedReleaseProtocolAssetPaths Bind(ReviewedGitHubResolvedTag resolvedTag)
    {
        ArgumentNullException.ThrowIfNull(resolvedTag);
        if (!ReferenceEquals(resolvedTag.Release, this.Candidate))
            throw new PackageSecurityException("The resolved tag belongs to a different reviewed release selection.");

        return new ReviewedReleaseProtocolAssetPaths(
            resolvedTag.ReleaseTag,
            resolvedTag.SourceCommit,
            this.GetPath(ReviewedReleaseAssetKind.InstallerPackage),
            this.GetPath(ReviewedReleaseAssetKind.InstallManifest),
            this.GetPath(ReviewedReleaseAssetKind.Checksums),
            this.GetPath(ReviewedReleaseAssetKind.BuildMetadata),
            this.GetPath(ReviewedReleaseAssetKind.AttestationBundle),
            this.GetPath(ReviewedReleaseAssetKind.AttestationBundleChecksum)
        );
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return this.Workspace.DisposeAsync();
    }

    private string GetPath(ReviewedReleaseAssetKind kind)
    {
        return this.Workspace.GetProcPath(this.Candidate.GetAsset(kind).Name);
    }
}

/// <summary>
/// Acquires exactly the six public assets of one Core-reviewed GitHub release into private retained storage. This is
/// acquisition authority only and isn't wired to the current production protocol opener yet.
/// </summary>
public static class ReviewedReleaseAssetAcquirer
{
    private const int MaximumRedirects = 5;
    private static readonly TimeSpan PerAssetTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AggregateTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Acquire the exact six reviewed assets sequentially through fixed credential-free GitHub policy.</summary>
    public static Task<ReviewedReleaseAssetLease> AcquireAsync(
        ReviewedReleaseCandidate candidate,
        IProgress<ReviewedReleaseAcquisitionProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        return AcquireAsync(candidate, new ReviewedReleaseAssetHttpTransport(), progress, cancellationToken);
    }

    internal static async Task<ReviewedReleaseAssetLease> AcquireAsync(
        ReviewedReleaseCandidate candidate,
        IReviewedReleaseAssetTransport transport,
        IProgress<ReviewedReleaseAcquisitionProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<ReviewedReleaseAssetWorkspace>? workspaceFactory = null
    )
    {
        ArgumentNullException.ThrowIfNull(transport);
        try
        {
            LinuxPrivilegeGuard.AssertNotRoot();
            ArgumentNullException.ThrowIfNull(candidate);
            ValidateCandidate(candidate);
            cancellationToken.ThrowIfCancellationRequested();

            ReviewedReleaseAssetWorkspace? workspace = null;
            using CancellationTokenSource aggregateTimeout = new(AggregateTimeout);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                aggregateTimeout.Token
            );
            try
            {
                workspace = (workspaceFactory ?? (() => ReviewedReleaseAssetWorkspace.Create()))();
                ReviewedReleaseAsset[] assets = Enum.GetValues<ReviewedReleaseAssetKind>()
                    .Select(candidate.GetAsset)
                    .ToArray();
                long totalBytes = checked(assets.Sum(asset => asset.SizeBytes));
                long completedBytes = 0;
                for (int index = 0; index < assets.Length; index++)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    ReviewedReleaseAsset asset = assets[index];
                    AnchoredDownloadTarget target = workspace.CreateTarget(asset);
                    int completedAssetsBefore = index;
                    long completedBytesBefore = completedBytes;
                    InlineProgress<DownloadProgress>? currentProgress = progress is null
                        ? null
                        : new(download => progress.Report(new ReviewedReleaseAcquisitionProgress(
                            asset.Kind,
                            completedAssetsBefore,
                            assets.Length,
                            download.BytesReceived,
                            asset.SizeBytes,
                            completedBytesBefore,
                            totalBytes
                        )));
                    DownloadResult result = await transport.DownloadAsync(
                            asset,
                            target,
                            new DownloadLimits(asset.SizeBytes, PerAssetTimeout, MaximumRedirects),
                            currentProgress,
                        linked.Token
                    ).ConfigureAwait(false);
                    linked.Token.ThrowIfCancellationRequested();
                    if (result.BytesReceived != asset.SizeBytes)
                        throw new PackageSecurityException("A reviewed release asset length differs from its catalog advertisement.");
                    workspace.RetainPublished(target, asset);
                    completedBytes = checked(completedBytes + result.BytesReceived);
                    progress?.Report(new ReviewedReleaseAcquisitionProgress(
                            asset.Kind,
                            index + 1,
                            assets.Length,
                            result.BytesReceived,
                            asset.SizeBytes,
                            completedBytes,
                        totalBytes
                    ));
                    linked.Token.ThrowIfCancellationRequested();
                }

                linked.Token.ThrowIfCancellationRequested();
                ReviewedReleaseAssetLease lease = new(candidate, workspace);
                workspace = null;
                return lease;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw new PackageSecurityException("Reviewed release acquisition exceeded its fixed aggregate time limit.");
            }
            catch (PackageSecurityException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PackageSecurityException($"Reviewed release acquisition failed safely ({ex.GetType().Name}).");
            }
            finally
            {
                if (workspace is not null)
                    await workspace.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            if (transport is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else if (transport is IDisposable disposable)
                disposable.Dispose();
        }
    }

    private static void ValidateCandidate(ReviewedReleaseCandidate candidate)
    {
        ReviewedReleaseAsset[] assets = candidate.Assets;
        ReviewedReleaseAssetKind[] kinds = Enum.GetValues<ReviewedReleaseAssetKind>();
        if (assets.Length != kinds.Length || assets.Select(asset => asset.Kind).SequenceEqual(kinds) is false)
            throw new PackageSecurityException("The reviewed release candidate doesn't contain the exact six-asset sequence.");
        long aggregate = 0;
        foreach (ReviewedReleaseAsset asset in assets)
        {
            string expectedName = ReviewedGitHubReleaseUris.GetAssetName(candidate.Identity, asset.Kind);
            Uri expectedUri = ReviewedGitHubReleaseUris.GetAssetUri(candidate.Identity, asset.Kind);
            long expectedMaximum = ReviewedGitHubReleaseUris.GetMaximumAssetBytes(asset.Kind);
            if (
                !string.Equals(asset.Name, expectedName, StringComparison.Ordinal)
                || !string.Equals(asset.DownloadUri.OriginalString, expectedUri.AbsoluteUri, StringComparison.Ordinal)
                || asset.MaximumBytes != expectedMaximum
                || asset.SizeBytes <= 0
                || asset.SizeBytes > asset.MaximumBytes
            )
            {
                throw new PackageSecurityException("The reviewed release candidate asset contract changed before acquisition.");
            }
            new ReviewedGitHubReleaseAssetPolicy().AssertAllowed(asset.DownloadUri, isInitial: true);
            aggregate = checked(aggregate + asset.SizeBytes);
        }
        if (aggregate > ReviewedGitHubReleaseUris.GetMaximumAssetSetBytes())
            throw new PackageSecurityException("The reviewed release asset set exceeds its fixed aggregate size bound.");
    }
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    private readonly Action<T> ReportAction = report ?? throw new ArgumentNullException(nameof(report));

    public void Report(T value)
    {
        this.ReportAction(value);
    }
}

internal interface IReviewedReleaseAssetTransport
{
    Task<DownloadResult> DownloadAsync(
        ReviewedReleaseAsset asset,
        AnchoredDownloadTarget destination,
        DownloadLimits limits,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken
    );
}

internal sealed class ReviewedReleaseAssetHttpTransport : IReviewedReleaseAssetTransport, IDisposable
{
    private readonly BoundedHttpDownloader Downloader = new(new ReviewedGitHubReleaseAssetPolicy());

    public Task<DownloadResult> DownloadAsync(
        ReviewedReleaseAsset asset,
        AnchoredDownloadTarget destination,
        DownloadLimits limits,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        return this.Downloader.DownloadAsync(asset.DownloadUri, destination, limits, progress, cancellationToken);
    }

    public void Dispose()
    {
        this.Downloader.Dispose();
    }
}
