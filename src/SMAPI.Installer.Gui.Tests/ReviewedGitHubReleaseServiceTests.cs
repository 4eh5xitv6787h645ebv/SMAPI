using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Gui.Backend;
using StardewModdingAPI.Installer.Gui.Frontend;

namespace StardewModdingAPI.Installer.Gui.Tests;

internal sealed class ReviewedGitHubReleaseServiceTests
{
    private const string Tag = "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2";
    private const string TagObject = "1111111111111111111111111111111111111111";
    private const string OtherTagObject = "2222222222222222222222222222222222222222";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    [Test]
    public async Task LoadCatalogUsesExactCredentialFreeBoundedGitHubRequest()
    {
        SequenceHandler handler = new(JsonResponse(CatalogDocument()));
        await using ReviewedGitHubReleaseService service = new(handler);

        IReadOnlyList<ReviewedReleaseCandidate> result = await service.LoadCatalogAsync();

        result.Should().ContainSingle().Which.Identity.Tag.Should().Be(Tag);
        RecordedRequest request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.Uri.Should().Be(ReviewedGitHubReleaseUris.GetCatalogUri());
        request.Accept.Should().Equal(ReviewedGitHubReleaseService.GitHubAccept);
        request.UserAgent.Should().Be(ReviewedGitHubReleaseService.UserAgent);
        request.ApiVersion.Should().Be(ReviewedGitHubReleaseService.GitHubApiVersion);
        request.Authorization.Should().BeNull();
        request.Cookie.Should().BeNull();
    }

    [Test]
    public void ProductionHandlerDisablesRedirectsCookiesCredentialsAndDecompression()
    {
        using SocketsHttpHandler handler = ReviewedGitHubReleaseService.CreateHandler();

        handler.AllowAutoRedirect.Should().BeFalse();
        handler.UseCookies.Should().BeFalse();
        handler.Credentials.Should().BeNull();
        handler.DefaultProxyCredentials.Should().BeNull();
        handler.PreAuthenticate.Should().BeFalse();
        handler.AutomaticDecompression.Should().Be(DecompressionMethods.None);
        handler.UseProxy.Should().BeFalse();
    }

    [Test]
    public async Task PrepareOwnsObserveAcquireRefreshResolveBindAndRetainedLifetimeInExactOrder()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        List<string> order = [];
        SequenceHandler handler = new(
            JsonResponse(ReferenceDocument(TagObject)),
            JsonResponse(AnnotatedTagDocument(TagObject)),
            JsonResponse(ReferenceDocument(TagObject))
        )
        {
            RequestObserved = uri => order.Add(
                uri == ReviewedGitHubReleaseUris.GetTagObjectUri(TagObject)
                    ? "annotated-tag"
                    : order.Count == 0 ? "initial-reference" : "refreshed-reference"
            )
        };
        FakeAcquisition acquisition = new(order);
        ReviewedReleaseAcquisitionFactory factory = (selected, progress, cancellationToken) =>
        {
            selected.Should().BeSameAs(candidate);
            selected.Assets.Select(asset => asset.Kind).Should().Equal(Enum.GetValues<ReviewedReleaseAssetKind>());
            cancellationToken.CanBeCanceled.Should().BeTrue();
            order.Add("acquire-six");
            progress!.Report(new(
                ReviewedReleaseAssetKind.InstallerPackage,
                0,
                6,
                3,
                4,
                0,
                10
            ));
            progress.Report(new(
                ReviewedReleaseAssetKind.InstallerPackage,
                1,
                6,
                4,
                4,
                4,
                10
            ));
            return Task.FromResult<IReviewedReleaseAcquisition>(acquisition);
        };
        List<ReviewedReleasePreparationProgress> progress = [];
        await using ReviewedGitHubReleaseService service = new(handler, acquisitionFactory: factory);

        IPreparedReleasePackage prepared = await service.PrepareAsync(
            candidate,
            new RecordingProgress<ReviewedReleasePreparationProgress>(progress.Add)
        );

        order.Should().Equal(
            "initial-reference",
            "annotated-tag",
            "acquire-six",
            "refreshed-reference",
            "bind"
        );
        handler.Requests.Select(request => request.Uri).Should().Equal(
            ReviewedGitHubReleaseUris.GetTagReferenceUri(candidate.Identity),
            ReviewedGitHubReleaseUris.GetTagObjectUri(TagObject),
            ReviewedGitHubReleaseUris.GetTagReferenceUri(candidate.Identity)
        );
        acquisition.ResolvedTag!.Release.Should().BeSameAs(candidate);
        acquisition.ResolvedTag.SourceCommit.Should().Be(Commit);
        prepared.Package.ReleaseTag.Should().Be(Tag);
        prepared.Package.ExpectedSourceCommit.Should().Be(Commit);
        progress.Select(value => value.Stage).Should().Equal(
            ReviewedReleasePreparationStage.ObservingTag,
            ReviewedReleasePreparationStage.Downloading,
            ReviewedReleasePreparationStage.Downloading,
            ReviewedReleasePreparationStage.RefreshingTag
        );
        progress[1].TransferredBytes.Should().Be(3);
        progress[2].TransferredBytes.Should().Be(4, "asset completion bytes must not be counted twice");

        await prepared.DisposeAsync();
        acquisition.DisposeCount.Should().Be(1);
        Action readDisposed = () => _ = prepared.Package;
        readDisposed.Should().Throw<ObjectDisposedException>();
        await prepared.DisposeAsync();
        acquisition.DisposeCount.Should().Be(1);
    }

    [Test]
    public async Task PrepareRejectsMovedTagAfterAcquisitionAndDisposesRetainedAssetsWithoutBinding()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        List<string> order = [];
        SequenceHandler handler = new(
            JsonResponse(ReferenceDocument(TagObject)),
            JsonResponse(AnnotatedTagDocument(TagObject)),
            JsonResponse(ReferenceDocument(OtherTagObject))
        );
        FakeAcquisition acquisition = new(order);
        await using ReviewedGitHubReleaseService service = new(
            handler,
            acquisitionFactory: (_, _, _) => Task.FromResult<IReviewedReleaseAcquisition>(acquisition)
        );

        Func<Task> action = async () => await service.PrepareAsync(candidate);

        await action.Should().ThrowAsync<PackageSecurityException>().WithMessage("*moved while its assets were acquired*");
        acquisition.ResolvedTag.Should().BeNull();
        acquisition.DisposeCount.Should().Be(1);
    }

    [Test]
    public async Task PrepareMapsAllSixSequentialAssetProgressEventsWithoutDoubleCountingCompletions()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        SequenceHandler handler = new(
            JsonResponse(ReferenceDocument(TagObject)),
            JsonResponse(AnnotatedTagDocument(TagObject)),
            JsonResponse(ReferenceDocument(TagObject))
        );
        FakeAcquisition acquisition = new([]);
        ReviewedReleaseAcquisitionFactory factory = (_, progress, _) =>
        {
            const long assetBytes = 4;
            const long totalBytes = assetBytes * 6;
            foreach (ReviewedReleaseAssetKind kind in Enum.GetValues<ReviewedReleaseAssetKind>())
            {
                int index = (int)kind;
                long completedBefore = index * assetBytes;
                progress!.Report(new(
                    kind,
                    index,
                    6,
                    2,
                    assetBytes,
                    completedBefore,
                    totalBytes
                ));
                progress.Report(new(
                    kind,
                    index + 1,
                    6,
                    assetBytes,
                    assetBytes,
                    completedBefore + assetBytes,
                    totalBytes
                ));
            }
            return Task.FromResult<IReviewedReleaseAcquisition>(acquisition);
        };
        List<ReviewedReleasePreparationProgress> observed = [];
        await using ReviewedGitHubReleaseService service = new(handler, acquisitionFactory: factory);

        await using IPreparedReleasePackage prepared = await service.PrepareAsync(
            candidate,
            new RecordingProgress<ReviewedReleasePreparationProgress>(observed.Add)
        );

        ReviewedReleasePreparationProgress[] downloading = observed
            .Where(value => value.Stage == ReviewedReleasePreparationStage.Downloading)
            .ToArray();
        downloading.Should().HaveCount(12);
        downloading.Select(value => value.AssetKind).Should().Equal(
            Enum.GetValues<ReviewedReleaseAssetKind>().SelectMany(kind => new ReviewedReleaseAssetKind?[] { kind, kind })
        );
        downloading.Select(value => value.TransferredBytes).Should().Equal(
            2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24
        );
        downloading[^1].CompletedAssets.Should().Be(6);
        downloading[^1].TotalAssets.Should().Be(6);
        downloading[^1].TransferredBytes.Should().Be(downloading[^1].TotalBytes);
    }

    [Test]
    [CancelAfter(5000)]
    public async Task ServiceDisposalJoinsIgnoredAcquisitionCancellationAndRetainedCleanupBeforePrepareFails()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        TaskCompletionSource acquisitionStarted = NewSignal();
        TaskCompletionSource releaseAcquisition = NewSignal();
        TaskCompletionSource cleanupStarted = NewSignal();
        TaskCompletionSource releaseCleanup = NewSignal();
        JoinedCleanupAcquisition acquisition = new(cleanupStarted, releaseCleanup);
        SequenceHandler handler = new(
            JsonResponse(ReferenceDocument(TagObject)),
            JsonResponse(AnnotatedTagDocument(TagObject))
        );
        ReviewedReleaseAcquisitionFactory factory = async (_, _, _) =>
        {
            acquisitionStarted.TrySetResult();
            await releaseAcquisition.Task.ConfigureAwait(false); // deliberately ignores cancellation
            return acquisition;
        };
        ReviewedGitHubReleaseService service = new(handler, acquisitionFactory: factory);
        Task<IPreparedReleasePackage> preparing = service.PrepareAsync(candidate);
        await acquisitionStarted.Task;

        Task disposing = service.DisposeAsync().AsTask();
        service.DisposeAsync().AsTask().Should().BeSameAs(disposing);
        disposing.IsCompleted.Should().BeFalse("active preparation authority has not been reaped");
        releaseAcquisition.TrySetResult();
        await cleanupStarted.Task;
        disposing.IsCompleted.Should().BeFalse("retained acquisition cleanup is still incomplete");
        releaseCleanup.TrySetResult();

        Func<Task> prepareAction = async () => await preparing;
        await prepareAction.Should().ThrowAsync<ObjectDisposedException>();
        await disposing;
        acquisition.DisposeCount.Should().Be(1);
        handler.Requests.Should().HaveCount(2, "a disposed service must not issue the refreshed-reference request");
    }

    [Test]
    [CancelAfter(5000)]
    public async Task ServiceDisposalWinsGatedBindAndPreventsFinalPreparedAuthorityPublication()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        TaskCompletionSource bindStarted = NewSignal();
        using ManualResetEventSlim releaseBind = new(false);
        BlockingBindAcquisition acquisition = new(bindStarted, releaseBind);
        SequenceHandler handler = new(
            JsonResponse(ReferenceDocument(TagObject)),
            JsonResponse(AnnotatedTagDocument(TagObject)),
            JsonResponse(ReferenceDocument(TagObject))
        );
        ReviewedGitHubReleaseService service = new(
            handler,
            acquisitionFactory: (_, _, _) => Task.FromResult<IReviewedReleaseAcquisition>(acquisition)
        );
        Task<IPreparedReleasePackage> preparing = Task.Run(() => service.PrepareAsync(candidate));
        await bindStarted.Task;

        Task disposing = service.DisposeAsync().AsTask();
        disposing.IsCompleted.Should().BeFalse("the in-flight bind remains an active service operation");
        releaseBind.Set();

        Func<Task> prepareAction = async () => await preparing;
        await prepareAction.Should().ThrowAsync<ObjectDisposedException>();
        await disposing;
        acquisition.DisposeCount.Should().Be(1);
    }

    [Test]
    [CancelAfter(5000)]
    public async Task PreparedPackageConcurrentDisposalsJoinOneCleanupAndAuthorityAccessFailsOnceCleanupStarts()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        TaskCompletionSource cleanupStarted = NewSignal();
        TaskCompletionSource releaseCleanup = NewSignal();
        JoinedCleanupAcquisition acquisition = new(cleanupStarted, releaseCleanup);
        SequenceHandler handler = new(
            JsonResponse(ReferenceDocument(TagObject)),
            JsonResponse(AnnotatedTagDocument(TagObject)),
            JsonResponse(ReferenceDocument(TagObject))
        );
        await using ReviewedGitHubReleaseService service = new(
            handler,
            acquisitionFactory: (_, _, _) => Task.FromResult<IReviewedReleaseAcquisition>(acquisition)
        );
        IPreparedReleasePackage prepared = await service.PrepareAsync(candidate);

        Task first = prepared.DisposeAsync().AsTask();
        await cleanupStarted.Task;
        Task second = prepared.DisposeAsync().AsTask();
        second.Should().BeSameAs(first);
        first.IsCompleted.Should().BeFalse();
        Action accessWhileDisposing = () => _ = prepared.Package;
        accessWhileDisposing.Should().Throw<ObjectDisposedException>();
        releaseCleanup.TrySetResult();

        await Task.WhenAll(first, second);
        acquisition.DisposeCount.Should().Be(1);
        prepared.DisposeAsync().AsTask().Should().BeSameAs(first);
    }

    [Test]
    [CancelAfter(5000)]
    public async Task CoreAcquisitionSerializesBindAgainstDisposalAndAllDisposalsJoinOneCleanup()
    {
        ReviewedGitHubResolvedTag resolved = Resolve(Candidate());
        TaskCompletionSource bindStarted = NewSignal();
        using ManualResetEventSlim releaseBind = new(false);
        TaskCompletionSource cleanupStarted = NewSignal();
        TaskCompletionSource releaseCleanup = NewSignal();
        int cleanupCount = 0;
        ReviewedGitHubReleaseService.CoreReviewedReleaseAcquisition acquisition = new(
            tag =>
            {
                bindStarted.TrySetResult();
                releaseBind.Wait();
                return Package(tag);
            },
            async () =>
            {
                Interlocked.Increment(ref cleanupCount);
                cleanupStarted.TrySetResult();
                await releaseCleanup.Task.ConfigureAwait(false);
            }
        );
        Task<InstallerPackageOpenInput> binding = Task.Run(() => acquisition.Bind(resolved));
        await bindStarted.Task;

        Task<Task> invokingDispose = Task.Factory.StartNew(
            () => acquisition.DisposeAsync().AsTask(),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default
        );
        invokingDispose.IsCompleted.Should().BeFalse("DisposeAsync is serialized behind the synchronous bind handoff");
        releaseBind.Set();
        (await binding).ExpectedSourceCommit.Should().Be(Commit);
        Task first = await invokingDispose;
        await cleanupStarted.Task;
        Task second = acquisition.DisposeAsync().AsTask();
        second.Should().BeSameAs(first);
        Action bindAfterDisposalStarted = () => acquisition.Bind(resolved);
        bindAfterDisposalStarted.Should().Throw<ObjectDisposedException>();
        releaseCleanup.TrySetResult();

        await Task.WhenAll(first, second);
        cleanupCount.Should().Be(1);
        acquisition.DisposeAsync().AsTask().Should().BeSameAs(first);
    }

    [Test]
    public async Task PrepareDisposesAcquisitionWhenRefreshedReferenceRequestFails()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        FakeAcquisition acquisition = new([]);
        SequenceHandler handler = new(
            JsonResponse(ReferenceDocument(TagObject)),
            JsonResponse(AnnotatedTagDocument(TagObject)),
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        );
        await using ReviewedGitHubReleaseService service = new(
            handler,
            acquisitionFactory: (_, _, _) => Task.FromResult<IReviewedReleaseAcquisition>(acquisition)
        );

        Func<Task> action = async () => await service.PrepareAsync(candidate);

        await action.Should().ThrowAsync<PackageSecurityException>().WithMessage("*unexpected HTTP status*");
        acquisition.DisposeCount.Should().Be(1);
        acquisition.ResolvedTag.Should().BeNull();
    }

    [TestCase(HttpStatusCode.Redirect)]
    [TestCase(HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.TooManyRequests)]
    [TestCase(HttpStatusCode.InternalServerError)]
    public async Task LoadCatalogRejectsEveryNonOkStatusWithoutFollowingIt(HttpStatusCode status)
    {
        HttpResponseMessage response = new(status);
        response.Headers.Location = new Uri("https://example.invalid/forbidden");
        SequenceHandler handler = new(response);
        await using ReviewedGitHubReleaseService service = new(handler);

        Func<Task> action = async () => await service.LoadCatalogAsync();

        await action.Should().ThrowAsync<PackageSecurityException>().WithMessage("*unexpected HTTP status*");
        handler.Requests.Should().HaveCount(1);
    }

    [Test]
    public async Task LoadCatalogRejectsEncodedResponse()
    {
        HttpResponseMessage response = JsonResponse(CatalogDocument());
        response.Content.Headers.ContentEncoding.Add("gzip");
        await using ReviewedGitHubReleaseService service = new(new SequenceHandler(response));

        Func<Task> action = async () => await service.LoadCatalogAsync();

        await action.Should().ThrowAsync<PackageSecurityException>().WithMessage("*unexpected content encoding*");
    }

    [Test]
    public async Task LoadCatalogRejectsOversizedDeclaredLengthBeforeReadingBody()
    {
        ThrowOnReadContent content = new();
        content.Headers.ContentLength = ReviewedGitHubReleaseUris.MaximumCatalogBytes + 1L;
        HttpResponseMessage response = new(HttpStatusCode.OK) { Content = content };
        await using ReviewedGitHubReleaseService service = new(new SequenceHandler(response));

        Func<Task> action = async () => await service.LoadCatalogAsync();

        await action.Should().ThrowAsync<PackageSecurityException>().WithMessage("*size limit*");
        content.ReadAttempted.Should().BeFalse();
    }

    [Test]
    public async Task LoadCatalogRejectsChunkedBodyWhichCrossesBound()
    {
        byte[] oversized = new byte[ReviewedGitHubReleaseUris.MaximumCatalogBytes + 1];
        HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(oversized)
        };
        await using ReviewedGitHubReleaseService service = new(new SequenceHandler(response));

        Func<Task> action = async () => await service.LoadCatalogAsync();

        await action.Should().ThrowAsync<PackageSecurityException>().WithMessage("*size limit*");
    }

    [Test]
    public async Task LoadCatalogRejectsBodyLengthDifferentFromDeclaration()
    {
        HttpResponseMessage response = JsonResponse(CatalogDocument());
        response.Content.Headers.ContentLength = CatalogDocument().Length + 1L;
        await using ReviewedGitHubReleaseService service = new(new SequenceHandler(response));

        Func<Task> action = async () => await service.LoadCatalogAsync();

        await action.Should().ThrowAsync<PackageSecurityException>().WithMessage("*differs from its HTTP declaration*");
    }

    [Test]
    public async Task CallerCancellationIsPreserved()
    {
        WaitingHandler handler = new();
        await using ReviewedGitHubReleaseService service = new(handler, Timeout.InfiniteTimeSpan);
        using CancellationTokenSource cancellation = new();

        Task<IReadOnlyList<ReviewedReleaseCandidate>> loading = service.LoadCatalogAsync(cancellation.Token);
        await handler.Started.Task;
        cancellation.Cancel();

        Func<Task> action = async () => await loading;
        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task RequestTimeoutIsMappedToSanitizedSecurityFailure()
    {
        WaitingHandler handler = new();
        await using ReviewedGitHubReleaseService service = new(handler, TimeSpan.FromMilliseconds(20));

        Func<Task> action = async () => await service.LoadCatalogAsync();

        await action.Should().ThrowAsync<PackageSecurityException>().WithMessage("*bounded time limit*");
    }

    [Test]
    public async Task DisposalCancelsActiveRequestAndBlocksNewRequests()
    {
        WaitingHandler handler = new();
        ReviewedGitHubReleaseService service = new(handler, Timeout.InfiniteTimeSpan);
        Task<IReadOnlyList<ReviewedReleaseCandidate>> loading = service.LoadCatalogAsync();
        await handler.Started.Task;

        await service.DisposeAsync();

        Func<Task> active = async () => await loading;
        await active.Should().ThrowAsync<ObjectDisposedException>();
        Func<Task> later = async () => await service.LoadCatalogAsync();
        await later.Should().ThrowAsync<ObjectDisposedException>();
        await service.DisposeAsync();
    }

    private static ReviewedReleaseCandidate Candidate()
    {
        return ReviewedGitHubReleaseCatalog.Parse(CatalogDocument()).Single();
    }

    private static ReviewedGitHubResolvedTag Resolve(ReviewedReleaseCandidate candidate)
    {
        ReviewedGitHubTagReference reference = ReviewedGitHubTagResolver.ParseReference(
            ReferenceDocument(TagObject),
            candidate
        );
        return ReviewedGitHubTagResolver.ResolveAfterRefresh(
            candidate,
            reference,
            AnnotatedTagDocument(TagObject),
            ReferenceDocument(TagObject)
        );
    }

    private static InstallerPackageOpenInput Package(ReviewedGitHubResolvedTag resolvedTag)
    {
        return new(
            resolvedTag.ReleaseTag,
            resolvedTag.SourceCommit,
            "/tmp/package",
            "/tmp/checksums",
            "/tmp/build",
            "/tmp/manifest",
            "/tmp/bundle",
            "/tmp/bundle-checksum"
        );
    }

    private static TaskCompletionSource NewSignal()
    {
        return new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static byte[] CatalogDocument()
    {
        ForkReleaseIdentity identity = ForkReleaseIdentity.Parse(Tag);
        object[] assets = Enum.GetValues<ReviewedReleaseAssetKind>()
            .Select(kind => (object)new
            {
                name = ReviewedGitHubReleaseUris.GetAssetName(identity, kind),
                size = 4,
                state = "uploaded",
                browser_download_url = ReviewedGitHubReleaseUris.GetAssetUri(identity, kind).AbsoluteUri
            })
            .ToArray();
        return JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new { tag_name = Tag, draft = false, prerelease = true, assets }
        });
    }

    private static byte[] ReferenceDocument(string tagObject)
    {
        ForkReleaseIdentity identity = ForkReleaseIdentity.Parse(Tag);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            @ref = $"refs/tags/{Tag}",
            url = $"https://api.github.com/repos/{ForkReleaseIdentity.Repository}/git/refs/tags/{Tag}",
            @object = new
            {
                type = "tag",
                sha = tagObject,
                url = ReviewedGitHubReleaseUris.GetTagObjectUri(tagObject).AbsoluteUri
            }
        });
    }

    private static byte[] AnnotatedTagDocument(string tagObject)
    {
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            sha = tagObject,
            tag = Tag,
            url = ReviewedGitHubReleaseUris.GetTagObjectUri(tagObject).AbsoluteUri,
            @object = new
            {
                type = "commit",
                sha = Commit,
                url = $"https://api.github.com/repos/{ForkReleaseIdentity.Repository}/git/commits/{Commit}"
            }
        });
    }

    private static HttpResponseMessage JsonResponse(byte[] document)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(document)
        };
    }

    private sealed class FakeAcquisition(List<string> order) : IReviewedReleaseAcquisition
    {
        public ReviewedGitHubResolvedTag? ResolvedTag { get; private set; }
        public int DisposeCount { get; private set; }

        public InstallerPackageOpenInput Bind(ReviewedGitHubResolvedTag resolvedTag)
        {
            order.Add("bind");
            this.ResolvedTag = resolvedTag;
            return new(
                resolvedTag.ReleaseTag,
                resolvedTag.SourceCommit,
                "/tmp/package",
                "/tmp/checksums",
                "/tmp/build",
                "/tmp/manifest",
                "/tmp/bundle",
                "/tmp/bundle-checksum"
            );
        }

        public ValueTask DisposeAsync()
        {
            this.DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class JoinedCleanupAcquisition(
        TaskCompletionSource cleanupStarted,
        TaskCompletionSource releaseCleanup
    ) : IReviewedReleaseAcquisition
    {
        public int DisposeCount { get; private set; }

        public InstallerPackageOpenInput Bind(ReviewedGitHubResolvedTag resolvedTag)
        {
            return Package(resolvedTag);
        }

        public async ValueTask DisposeAsync()
        {
            this.DisposeCount++;
            cleanupStarted.TrySetResult();
            await releaseCleanup.Task.ConfigureAwait(false);
        }
    }

    private sealed class BlockingBindAcquisition(
        TaskCompletionSource bindStarted,
        ManualResetEventSlim releaseBind
    ) : IReviewedReleaseAcquisition
    {
        public int DisposeCount { get; private set; }

        public InstallerPackageOpenInput Bind(ReviewedGitHubResolvedTag resolvedTag)
        {
            bindStarted.TrySetResult();
            releaseBind.Wait();
            return Package(resolvedTag);
        }

        public ValueTask DisposeAsync()
        {
            this.DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        string[] Accept,
        string UserAgent,
        string? ApiVersion,
        string? Authorization,
        string? Cookie
    );

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> Responses = new(responses);
        public List<RecordedRequest> Requests { get; } = [];
        public Action<Uri>? RequestObserved { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Requests.Add(new(
                request.Method,
                request.RequestUri!,
                request.Headers.Accept.Select(value => value.MediaType!).ToArray(),
                request.Headers.UserAgent.ToString(),
                request.Headers.GetValues("X-GitHub-Api-Version").Single(),
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Cookie", out IEnumerable<string>? cookies) ? string.Join(";", cookies) : null
            ));
            this.RequestObserved?.Invoke(request.RequestUri!);
            if (this.Responses.Count == 0)
                throw new InvalidOperationException("No deterministic response remains.");
            return Task.FromResult(this.Responses.Dequeue());
        }
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new AssertionException("The deterministic waiting request unexpectedly completed.");
        }
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return stream.WriteAsync(bytes).AsTask();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class ThrowOnReadContent : HttpContent
    {
        public bool ReadAttempted { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            this.ReadAttempted = true;
            throw new AssertionException("An oversized declared response must be rejected before its body is read.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class RecordingProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
