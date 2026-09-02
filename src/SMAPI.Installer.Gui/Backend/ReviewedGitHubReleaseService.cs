using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Gui.Frontend;

namespace StardewModdingAPI.Installer.Gui.Backend;

internal delegate Task<IReviewedReleaseAcquisition> ReviewedReleaseAcquisitionFactory(
    ReviewedReleaseCandidate candidate,
    IProgress<ReviewedReleaseAcquisitionProgress>? progress,
    CancellationToken cancellationToken
);

/// <summary>An injectable retained-acquisition seam; production wraps Core's nonforgeable lease.</summary>
internal interface IReviewedReleaseAcquisition : IAsyncDisposable
{
    InstallerPackageOpenInput Bind(ReviewedGitHubResolvedTag resolvedTag);
}

/// <summary>
/// Loads only the reviewed fork's bounded public GitHub documents and prepares one exact six-asset package set.
/// </summary>
internal sealed class ReviewedGitHubReleaseService : IReviewedReleaseService
{
    internal const string UserAgent = "SMAPI-Linux-Installer";
    internal const string GitHubAccept = "application/vnd.github+json";
    internal const string GitHubApiVersion = "2022-11-28";
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient Client;
    private readonly ReviewedGitHubReleaseApiPolicy ApiPolicy = new();
    private readonly ReviewedReleaseAcquisitionFactory AcquisitionFactory;
    private readonly TimeSpan RequestTimeout;
    private readonly CancellationTokenSource Lifetime = new();
    private readonly CancellationToken LifetimeToken;
    private readonly object LifecycleSync = new();
    private TaskCompletionSource? ActiveOperationsCompleted;
    private Task? DisposalTask;
    private int ActiveOperations;
    private int Disposed;

    public ReviewedGitHubReleaseService()
        : this(CreateHandler(), DefaultRequestTimeout, AcquireWithCoreAsync)
    {
    }

    internal ReviewedGitHubReleaseService(
        HttpMessageHandler handler,
        TimeSpan? requestTimeout = null,
        ReviewedReleaseAcquisitionFactory? acquisitionFactory = null
    )
    {
        this.Client = new HttpClient(handler ?? throw new ArgumentNullException(nameof(handler)), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        this.RequestTimeout = requestTimeout ?? DefaultRequestTimeout;
        if (this.RequestTimeout <= TimeSpan.Zero && this.RequestTimeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        this.AcquisitionFactory = acquisitionFactory ?? AcquireWithCoreAsync;
        this.LifetimeToken = this.Lifetime.Token;
    }

    public async Task<IReadOnlyList<ReviewedReleaseCandidate>> LoadCatalogAsync(
        CancellationToken cancellationToken = default
    )
    {
        this.BeginOperation();
        try
        {
            byte[] document = await this.GetDocumentAsync(
                ReviewedGitHubReleaseUris.GetCatalogUri(),
                ReviewedGitHubReleaseUris.MaximumCatalogBytes,
                cancellationToken
            ).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            this.ThrowIfLifetimeEnded();
            return ReviewedGitHubReleaseCatalog.Parse(document);
        }
        finally
        {
            this.EndOperation();
        }
    }

    public async Task<IPreparedReleasePackage> PrepareAsync(
        ReviewedReleaseCandidate candidate,
        IProgress<ReviewedReleasePreparationProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(candidate);
        this.BeginOperation();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(Stage(ReviewedReleasePreparationStage.ObservingTag));
            byte[] initialReferenceDocument = await this.GetDocumentAsync(
                ReviewedGitHubReleaseUris.GetTagReferenceUri(candidate.Identity),
                ReviewedGitHubReleaseUris.MaximumTagDocumentBytes,
                cancellationToken
            ).ConfigureAwait(false);
            ReviewedGitHubTagReference initialReference = ReviewedGitHubTagResolver.ParseReference(
                initialReferenceDocument,
                candidate
            );
            byte[] annotatedTagDocument = await this.GetDocumentAsync(
                initialReference.AnnotatedTagDocumentUri,
                ReviewedGitHubReleaseUris.MaximumTagDocumentBytes,
                cancellationToken
            ).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            this.ThrowIfLifetimeEnded();

            IProgress<ReviewedReleaseAcquisitionProgress>? acquisitionProgress = progress is null
                ? null
                : new SynchronousProgress<ReviewedReleaseAcquisitionProgress>(value => progress.Report(ToPreparationProgress(value)));
            IReviewedReleaseAcquisition? acquisition = await this.AcquireWithLifetimeAsync(
                candidate,
                acquisitionProgress,
                cancellationToken
            ).ConfigureAwait(false);
            try
            {
                progress?.Report(Stage(ReviewedReleasePreparationStage.RefreshingTag));
                byte[] refreshedReferenceDocument = await this.GetDocumentAsync(
                    ReviewedGitHubReleaseUris.GetTagReferenceUri(candidate.Identity),
                    ReviewedGitHubReleaseUris.MaximumTagDocumentBytes,
                    cancellationToken
                ).ConfigureAwait(false);
                ReviewedGitHubResolvedTag resolved = ReviewedGitHubTagResolver.ResolveAfterRefresh(
                    candidate,
                    initialReference,
                    annotatedTagDocument,
                    refreshedReferenceDocument
                );
                cancellationToken.ThrowIfCancellationRequested();
                this.ThrowIfLifetimeEnded();
                InstallerPackageOpenInput package = acquisition.Bind(resolved);
                RetainedPreparedReleasePackage prepared = this.PublishPrepared(package, acquisition);
                acquisition = null;
                return prepared;
            }
            finally
            {
                if (acquisition is not null)
                    await acquisition.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            this.EndOperation();
        }
    }

    public ValueTask DisposeAsync()
    {
        Task disposal;
        Task activeOperations;
        TaskCompletionSource? owner = null;
        lock (this.LifecycleSync)
        {
            if (this.DisposalTask is not null)
                return new ValueTask(this.DisposalTask);

            Volatile.Write(ref this.Disposed, 1);
            activeOperations = this.ActiveOperations == 0
                ? Task.CompletedTask
                : (this.ActiveOperationsCompleted ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            owner = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = this.DisposalTask = owner.Task;
        }

        _ = this.DisposeCoreAsync(activeOperations, owner);
        return new ValueTask(disposal);
    }

    private async Task DisposeCoreAsync(Task activeOperations, TaskCompletionSource completion)
    {
        List<Exception> failures = [];
        try
        {
            this.Lifetime.Cancel();
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
        try
        {
            this.Client.Dispose();
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
        try
        {
            await activeOperations.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
        try
        {
            this.Lifetime.Dispose();
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        if (failures.Count == 0)
            completion.TrySetResult();
        else
            completion.TrySetException(failures);
    }

    private async Task<IReviewedReleaseAcquisition> AcquireWithLifetimeAsync(
        ReviewedReleaseCandidate candidate,
        IProgress<ReviewedReleaseAcquisitionProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            this.LifetimeToken
        );
        try
        {
            return await this.AcquisitionFactory(candidate, progress, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (this.LifetimeToken.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(ReviewedGitHubReleaseService));
        }
    }

    private async Task<byte[]> GetDocumentAsync(Uri uri, int maximumBytes, CancellationToken cancellationToken)
    {
        this.AssertNotDisposed();
        ArgumentNullException.ThrowIfNull(uri);
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        this.ApiPolicy.AssertAllowed(uri, isInitial: true);
        cancellationToken.ThrowIfCancellationRequested();

        using CancellationTokenSource timeout = this.RequestTimeout == Timeout.InfiniteTimeSpan
            ? new CancellationTokenSource()
            : new CancellationTokenSource(this.RequestTimeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token,
            this.LifetimeToken
        );
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(GitHubAccept));
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Add("X-GitHub-Api-Version", GitHubApiVersion);

            using HttpResponseMessage response = await this.Client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                linked.Token
            ).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new PackageSecurityException("The reviewed GitHub release API returned an unexpected HTTP status.");
            if (response.Content.Headers.ContentEncoding.Count != 0)
                throw new PackageSecurityException("The reviewed GitHub release API returned an unexpected content encoding.");

            long? declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength is < 0 || declaredLength > maximumBytes)
                throw new PackageSecurityException("The reviewed GitHub release document exceeds its configured size limit.");

            await using Stream input = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            using MemoryStream output = declaredLength is > 0
                ? new MemoryStream((int)declaredLength.Value)
                : new MemoryStream();
            byte[] buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
            try
            {
                while (true)
                {
                    int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), linked.Token).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    if (output.Length > maximumBytes - read)
                        throw new PackageSecurityException("The reviewed GitHub release document exceeds its configured size limit.");
                    output.Write(buffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (declaredLength.HasValue && output.Length != declaredLength.Value)
                throw new PackageSecurityException("The reviewed GitHub release document length differs from its HTTP declaration.");
            return output.ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (this.LifetimeToken.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(ReviewedGitHubReleaseService));
        }
        catch (OperationCanceledException)
        {
            throw new PackageSecurityException("The reviewed GitHub release API request exceeded its bounded time limit.");
        }
        catch (PackageSecurityException)
        {
            throw;
        }
        catch (Exception) when (this.LifetimeToken.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(ReviewedGitHubReleaseService));
        }
        catch (Exception ex)
        {
            throw new PackageSecurityException($"The reviewed GitHub release API request failed safely ({ex.GetType().Name}).");
        }
    }

    internal static SocketsHttpHandler CreateHandler()
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            Credentials = null,
            DefaultProxyCredentials = null,
            MaxResponseHeadersLength = 64,
            PreAuthenticate = false,
            UseCookies = false,
            UseProxy = false
        };
    }

    private static async Task<IReviewedReleaseAcquisition> AcquireWithCoreAsync(
        ReviewedReleaseCandidate candidate,
        IProgress<ReviewedReleaseAcquisitionProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        ReviewedReleaseAssetLease lease = await ReviewedReleaseAssetAcquirer.AcquireAsync(
            candidate,
            progress,
            cancellationToken
        ).ConfigureAwait(false);
        return new CoreReviewedReleaseAcquisition(lease);
    }

    private static ReviewedReleasePreparationProgress Stage(ReviewedReleasePreparationStage stage)
    {
        return new(stage, null, 0, 0, 0, 0);
    }

    private static ReviewedReleasePreparationProgress ToPreparationProgress(
        ReviewedReleaseAcquisitionProgress progress
    )
    {
        long transferred = progress.CompletedAssets > (int)progress.AssetKind
            ? progress.CompletedBytes
            : checked(progress.CompletedBytes + progress.CurrentAssetBytes);
        return new(
            ReviewedReleasePreparationStage.Downloading,
            progress.AssetKind,
            progress.CompletedAssets,
            progress.TotalAssets,
            transferred,
            progress.TotalBytes
        );
    }

    private void AssertNotDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this.Disposed) != 0, this);
    }

    private void BeginOperation()
    {
        lock (this.LifecycleSync)
        {
            if (Volatile.Read(ref this.Disposed) != 0)
                throw new ObjectDisposedException(nameof(ReviewedGitHubReleaseService));
            checked
            {
                this.ActiveOperations++;
            }
        }
    }

    private void EndOperation()
    {
        TaskCompletionSource? completed = null;
        lock (this.LifecycleSync)
        {
            if (this.ActiveOperations <= 0)
                throw new InvalidOperationException("The reviewed release service operation lifetime is unbalanced.");
            this.ActiveOperations--;
            if (this.ActiveOperations == 0 && this.ActiveOperationsCompleted is not null)
                completed = this.ActiveOperationsCompleted;
        }
        completed?.TrySetResult();
    }

    private RetainedPreparedReleasePackage PublishPrepared(
        InstallerPackageOpenInput package,
        IReviewedReleaseAcquisition acquisition
    )
    {
        lock (this.LifecycleSync)
        {
            if (Volatile.Read(ref this.Disposed) != 0)
                throw new ObjectDisposedException(nameof(ReviewedGitHubReleaseService));
            return new RetainedPreparedReleasePackage(package, acquisition);
        }
    }

    private void ThrowIfLifetimeEnded()
    {
        if (this.LifetimeToken.IsCancellationRequested)
            throw new ObjectDisposedException(nameof(ReviewedGitHubReleaseService));
    }

    internal sealed class CoreReviewedReleaseAcquisition : IReviewedReleaseAcquisition
    {
        private readonly object Sync = new();
        private Func<ReviewedGitHubResolvedTag, InstallerPackageOpenInput>? BindAction;
        private Func<ValueTask>? DisposeAction;
        private Task? DisposalTask;

        public CoreReviewedReleaseAcquisition(ReviewedReleaseAssetLease lease)
        {
            ArgumentNullException.ThrowIfNull(lease);
            this.BindAction = resolvedTag => ReviewedReleaseProtocolAssetMapper.Map(lease.Bind(resolvedTag));
            this.DisposeAction = lease.DisposeAsync;
        }

        internal CoreReviewedReleaseAcquisition(
            Func<ReviewedGitHubResolvedTag, InstallerPackageOpenInput> bind,
            Func<ValueTask> dispose
        )
        {
            this.BindAction = bind ?? throw new ArgumentNullException(nameof(bind));
            this.DisposeAction = dispose ?? throw new ArgumentNullException(nameof(dispose));
        }

        public InstallerPackageOpenInput Bind(ReviewedGitHubResolvedTag resolvedTag)
        {
            ArgumentNullException.ThrowIfNull(resolvedTag);
            lock (this.Sync)
            {
                Func<ReviewedGitHubResolvedTag, InstallerPackageOpenInput> current = this.BindAction
                    ?? throw new ObjectDisposedException(nameof(CoreReviewedReleaseAcquisition));
                return current(resolvedTag);
            }
        }

        public ValueTask DisposeAsync()
        {
            Task disposal;
            TaskCompletionSource? owner = null;
            Func<ValueTask>? dispose = null;
            lock (this.Sync)
            {
                if (this.DisposalTask is not null)
                    return new ValueTask(this.DisposalTask);

                this.BindAction = null;
                dispose = this.DisposeAction;
                this.DisposeAction = null;
                owner = new(TaskCreationOptions.RunContinuationsAsynchronously);
                disposal = this.DisposalTask = owner.Task;
            }

            _ = DisposeJoinedAsync(dispose!, owner!);
            return new ValueTask(disposal);
        }
    }

    private static async Task DisposeJoinedAsync(Func<ValueTask> dispose, TaskCompletionSource completion)
    {
        try
        {
            await dispose().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        private readonly Action<T> ReportAction = report ?? throw new ArgumentNullException(nameof(report));

        public void Report(T value)
        {
            this.ReportAction(value);
        }
    }
}
