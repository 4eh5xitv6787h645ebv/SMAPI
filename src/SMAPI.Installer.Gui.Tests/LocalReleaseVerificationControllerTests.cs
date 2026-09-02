using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Gui.Backend;
using StardewModdingAPI.Installer.Gui.Frontend;

namespace StardewModdingAPI.Installer.Gui.Tests;

[NonParallelizable]
internal sealed class LocalReleaseVerificationControllerTests
{
    private const string ReviewedTag = "fork-4eh5xitv6787h645ebv-linux-v4.5.4-alpha.2";

    [Test]
    public async Task LocalHandshakePrecedesPreparationAndOnlyBackendSuccessPublishesIdentity()
    {
        const string selectedDirectory = "/home/alice/Private Mods/secret-local-package";
        List<string> trace = [];
        ConcurrentQueue<string> snapshots = new();
        TaskCompletionSource<InstallerPackageOpenResult> opened = NewCompletion<InstallerPackageOpenResult>();
        FakePreparedPackage package = new(trace);
        FakeLocalReleaseService local = new()
        {
            Prepare = (path, _) =>
            {
                trace.Add("local-prepare");
                path.Should().Be(selectedDirectory);
                return Task.FromResult<IPreparedReleasePackage>(package);
            }
        };
        FakeClient client = new()
        {
            Handshake = (_, _, _) =>
            {
                trace.Add("handshake");
                return Task.FromResult(CreateHandshake());
            },
            Open = (input, _) =>
            {
                trace.Add("open");
                input.Should().BeSameAs(package.Package);
                return opened.Task;
            }
        };
        ReleaseVerificationController controller = new(new FakeReleaseService(), () => client, local);
        controller.Changed += (_, _) => snapshots.Enqueue(controller.Snapshot.ToString());

        Task running = controller.StartLocalAsync(selectedDirectory);
        await AwaitBounded(client.OpenStarted.Task);

        trace.Should().Equal("handshake", "local-prepare", "open");
        controller.Snapshot.State.Should().Be(ReleaseVerificationState.OpeningPackage);
        controller.Snapshot.Source.Should().Be(ReleasePackageSource.LocalFolder);
        controller.Snapshot.SelectedRelease.Should().BeNull();
        controller.Snapshot.VerifiedRelease.Should().BeNull();
        package.DisposeCalls.Should().Be(0);

        ProtocolReleaseIdentity backendIdentity = CreateRelease();
        opened.SetResult(new InstallerPackageOpenSuccess(backendIdentity));
        await AwaitBounded(running);

        trace.Should().Equal("handshake", "local-prepare", "open", "prepared-dispose");
        controller.Snapshot.State.Should().Be(ReleaseVerificationState.Verified);
        controller.Snapshot.VerifiedRelease.Should().BeSameAs(backendIdentity);
        local.Paths.Should().Equal(selectedDirectory);
        snapshots.Should().OnlyContain(value => !value.Contains(selectedDirectory, StringComparison.Ordinal));
        client.DisposeCalls.Should().Be(0);
        await AwaitBounded(controller.DisposeAsync().AsTask());
        client.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task LocalRejectionDisposesPackageAndClientWithoutPublishingIdentity()
    {
        const string selectedDirectory = "/home/alice/private/rejected-package";
        FakePreparedPackage package = new();
        FakeLocalReleaseService local = PreparedLocalService(package);
        FakeClient client = new()
        {
            Open = (_, _) => Task.FromResult<InstallerPackageOpenResult>(new InstallerPackageOpenRejection(
                ProtocolPrePlanErrorCode.PackageRejected,
                ProtocolNextAction.ReopenVerifiedPackage,
                $"unsafe detail from {selectedDirectory}",
                false
            ))
        };
        ReleaseVerificationController controller = new(new FakeReleaseService(), () => client, local);

        await AwaitBounded(controller.StartLocalAsync(selectedDirectory));

        ReleaseVerificationSnapshot snapshot = controller.Snapshot;
        snapshot.State.Should().Be(ReleaseVerificationState.Failed);
        snapshot.Error.Should().Be(ReleaseVerificationError.PackageRejected);
        snapshot.Source.Should().Be(ReleasePackageSource.LocalFolder);
        snapshot.VerifiedRelease.Should().BeNull();
        snapshot.RejectionCode.Should().Be(ProtocolPrePlanErrorCode.PackageRejected);
        snapshot.RejectionNextAction.Should().Be(ProtocolNextAction.ReopenVerifiedPackage);
        snapshot.CanRetry.Should().BeFalse("public-release retry must never reuse a local selection");
        snapshot.CanChooseLocal.Should().BeTrue("a retryable rejection permits a fresh folder selection");
        snapshot.ToString().Should().NotContain(selectedDirectory);
        package.DisposeCalls.Should().Be(1);
        client.DisposeCalls.Should().Be(1);
        await AwaitBounded(controller.DisposeAsync().AsTask());
    }

    [TestCase(ProtocolPrePlanErrorCode.PackageIntegrityRejected, ReleaseVerificationError.PackageIntegrityOrMetadataRejected)]
    [TestCase(ProtocolPrePlanErrorCode.PackageMetadataRejected, ReleaseVerificationError.PackageIntegrityOrMetadataRejected)]
    [TestCase(ProtocolPrePlanErrorCode.PackageArchiveRejected, ReleaseVerificationError.PackageIntegrityOrMetadataRejected)]
    [TestCase(ProtocolPrePlanErrorCode.PackageProvenanceRejected, ReleaseVerificationError.PackageProvenanceOrIdentityRejected)]
    [TestCase(ProtocolPrePlanErrorCode.PackageReleaseIdentityRejected, ReleaseVerificationError.PackageProvenanceOrIdentityRejected)]
    public async Task TypedLocalRejectionRequiresFreshFolderSelectionInsteadOfRetry(
        ProtocolPrePlanErrorCode rejectionCode,
        ReleaseVerificationError expectedError
    )
    {
        const string firstPath = "/home/alice/private/first-package";
        const string secondPath = "/home/alice/private/second-package";
        Queue<FakePreparedPackage> packages = new([new(), new()]);
        FakeLocalReleaseService local = new()
        {
            Prepare = (_, _) => Task.FromResult<IPreparedReleasePackage>(packages.Dequeue())
        };
        Func<FakeClient> rejectingClient = () => new()
        {
            Open = (_, _) => Task.FromResult<InstallerPackageOpenResult>(new InstallerPackageOpenRejection(
                rejectionCode,
                ProtocolNextAction.ReopenVerifiedPackage,
                $"private rejection for {firstPath}?token=SECRET",
                false
            ))
        };
        FakeClient firstClient = rejectingClient();
        FakeClient secondClient = rejectingClient();
        Queue<FakeClient> clients = new([firstClient, secondClient]);
        ReleaseVerificationController controller = new(new FakeReleaseService(), () => clients.Dequeue(), local);

        await AwaitBounded(controller.StartLocalAsync(firstPath));

        ReleaseVerificationSnapshot firstFailure = controller.Snapshot;
        firstFailure.State.Should().Be(ReleaseVerificationState.Failed);
        firstFailure.Error.Should().Be(expectedError);
        firstFailure.RejectionCode.Should().Be(rejectionCode);
        firstFailure.VerifiedRelease.Should().BeNull();
        firstFailure.CanRetry.Should().BeFalse("a local path is never retained as retry authority");
        firstFailure.CanChooseLocal.Should().BeTrue("the user may explicitly select a fresh complete folder");
        firstFailure.ToString().Should().NotContain("alice").And.NotContain("SECRET");
        Action retry = () => _ = controller.RetryAsync();
        retry.Should().Throw<InvalidOperationException>().WithMessage("*selected again*");

        await AwaitBounded(controller.StartLocalAsync(secondPath));

        ReleaseVerificationSnapshot secondFailure = controller.Snapshot;
        secondFailure.State.Should().Be(ReleaseVerificationState.Failed);
        secondFailure.Error.Should().Be(expectedError);
        secondFailure.RejectionCode.Should().Be(rejectionCode);
        secondFailure.AttemptNumber.Should().Be(2);
        secondFailure.VerifiedRelease.Should().BeNull();
        secondFailure.CanRetry.Should().BeFalse();
        secondFailure.CanChooseLocal.Should().BeTrue();
        secondFailure.ToString().Should().NotContain("alice").And.NotContain("SECRET");
        local.Paths.Should().Equal(firstPath, secondPath);
        firstClient.DisposeCalls.Should().Be(1);
        secondClient.DisposeCalls.Should().Be(1);
        await AwaitBounded(controller.DisposeAsync().AsTask());
    }

    [Test]
    public async Task LocalImportFailureProjectsNoRawPathAndReapsBackendClient()
    {
        const string selectedDirectory = "/home/alice/private/import-failure-token";
        int importCleanupCalls = 0;
        FakeLocalReleaseService local = new()
        {
            Prepare = (path, _) =>
            {
                path.Should().Be(selectedDirectory);
                importCleanupCalls++;
                return Task.FromException<IPreparedReleasePackage>(new IOException($"failed to import {path}"));
            }
        };
        FakeClient client = new();
        ReleaseVerificationController controller = new(new FakeReleaseService(), () => client, local);

        await AwaitBounded(controller.StartLocalAsync(selectedDirectory));

        ReleaseVerificationSnapshot snapshot = controller.Snapshot;
        snapshot.State.Should().Be(ReleaseVerificationState.Failed);
        snapshot.Error.Should().Be(ReleaseVerificationError.PreparationFailed);
        snapshot.Source.Should().Be(ReleasePackageSource.LocalFolder);
        snapshot.VerifiedRelease.Should().BeNull();
        snapshot.ToString().Should().NotContain(selectedDirectory).And.NotContain("import-failure-token");
        importCleanupCalls.Should().Be(1);
        client.OpenCalls.Should().Be(0);
        client.DisposeCalls.Should().Be(1);
        await AwaitBounded(controller.DisposeAsync().AsTask());
    }

    [Test]
    public async Task LocalCancellationWaitsForPackageAndClientCleanup()
    {
        FakePreparedPackage package = new();
        FakeLocalReleaseService local = PreparedLocalService(package);
        FakeClient client = new()
        {
            Open = async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new AssertionException("A cancelled package-open request must not complete normally.");
            }
        };
        ReleaseVerificationController controller = new(new FakeReleaseService(), () => client, local);
        Task running = controller.StartLocalAsync("/home/alice/private/cancelled-package");
        await AwaitBounded(client.OpenStarted.Task);

        await AwaitBounded(controller.CancelAsync());
        await AwaitBounded(running);

        controller.Snapshot.State.Should().Be(ReleaseVerificationState.Cancelled);
        controller.Snapshot.Error.Should().Be(ReleaseVerificationError.None);
        controller.Snapshot.VerifiedRelease.Should().BeNull();
        package.DisposeCalls.Should().Be(1);
        client.DisposeCalls.Should().Be(1);
        await AwaitBounded(controller.DisposeAsync().AsTask());
    }

    [Test]
    public async Task SessionFaultDuringLocalImportCancelsAndObservesImportBeforeCleanupCompletes()
    {
        TaskCompletionSource importStarted = NewCompletion();
        int importCleanupCalls = 0;
        FakeLocalReleaseService local = new()
        {
            Prepare = async (_, cancellationToken) =>
            {
                importStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new AssertionException("A faulted backend must cancel local import.");
                }
                finally
                {
                    importCleanupCalls++;
                }
            }
        };
        FakeClient client = new();
        ReleaseVerificationController controller = new(new FakeReleaseService(), () => client, local);
        Task running = controller.StartLocalAsync("/home/alice/private/faulted-package");
        await AwaitBounded(importStarted.Task);

        client.Fault.SetResult(new InstallerProtocolClientException("sanitized session fault"));
        await AwaitBounded(running);

        controller.Snapshot.State.Should().Be(ReleaseVerificationState.Failed);
        controller.Snapshot.Error.Should().Be(ReleaseVerificationError.SessionFaulted);
        controller.Snapshot.VerifiedRelease.Should().BeNull();
        importCleanupCalls.Should().Be(1);
        client.OpenCalls.Should().Be(0);
        client.DisposeCalls.Should().Be(1);
        await AwaitBounded(controller.DisposeAsync().AsTask());
    }

    [Test]
    public async Task LocalAttemptsRequireFreshSelectionWhileReviewedRetryReloadsOnlyPublicCatalog()
    {
        FakeLocalReleaseService local = new()
        {
            Prepare = (_, _) => Task.FromResult<IPreparedReleasePackage>(new FakePreparedPackage())
        };
        Queue<FakeClient> localClients = new([
            RejectingClient(),
            RejectingClient()
        ]);
        ReleaseVerificationController localController = new(
            new FakeReleaseService(),
            () => localClients.Dequeue(),
            local
        );
        await AwaitBounded(localController.StartLocalAsync("/selected/first"));

        Action localRetry = () => _ = localController.RetryAsync();
        localRetry.Should().Throw<InvalidOperationException>().WithMessage("*selected again*");
        local.Paths.Should().Equal("/selected/first");

        await AwaitBounded(localController.StartLocalAsync("/selected/second"));
        local.Paths.Should().Equal("/selected/first", "/selected/second");
        await AwaitBounded(localController.DisposeAsync().AsTask());

        ReviewedReleaseCandidate first = Candidate();
        ReviewedReleaseCandidate refreshed = Candidate();
        FakeReleaseService reviewed = new();
        reviewed.Catalogs.Enqueue([first]);
        reviewed.Catalogs.Enqueue([refreshed]);
        reviewed.Prepare = (_, progress, _) =>
        {
            EmitValidProgress(progress!);
            return Task.FromResult<IPreparedReleasePackage>(new FakePreparedPackage());
        };
        FakeLocalReleaseService forbiddenLocal = new()
        {
            Prepare = (_, _) => throw new AssertionException("Reviewed retry must not call the local importer.")
        };
        Queue<FakeClient> reviewedClients = new([
            RejectingClient(),
            RejectingClient()
        ]);
        ReleaseVerificationController reviewedController = new(
            reviewed,
            () => reviewedClients.Dequeue(),
            forbiddenLocal
        );
        await AwaitBounded(reviewedController.LoadCatalogAsync());
        await AwaitBounded(reviewedController.StartAsync());
        await AwaitBounded(reviewedController.RetryAsync());

        reviewed.LoadCalls.Should().Be(2);
        reviewed.PrepareCalls.Should().Be(2);
        reviewed.Candidates.Should().Equal(first, refreshed);
        forbiddenLocal.Paths.Should().BeEmpty();
        reviewedController.Snapshot.Source.Should().Be(ReleasePackageSource.ReviewedDownload);
        reviewedController.Snapshot.AttemptNumber.Should().Be(2);
        reviewedController.Snapshot.CanRetry.Should().BeTrue();
        await AwaitBounded(reviewedController.DisposeAsync().AsTask());
    }

    [Test]
    public async Task LocalSelectionCancelsAndJoinsCatalogLoadBeforeStartingReplacementAttempt()
    {
        const string selectedDirectory = "/home/alice/private/catalog-replacement";
        List<string> trace = [];
        TaskCompletionSource catalogStarted = NewCompletion();
        FakeReleaseService reviewed = new()
        {
            Load = async cancellationToken =>
            {
                trace.Add("catalog-start");
                catalogStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    trace.Add("catalog-cancelled");
                    return [Candidate()];
                }
                throw new AssertionException("The replaced catalog load must be cancelled.");
            }
        };
        FakeLocalReleaseService local = new()
        {
            Prepare = (_, _) =>
            {
                trace.Add("local-prepare");
                return Task.FromResult<IPreparedReleasePackage>(new FakePreparedPackage());
            }
        };
        FakeClient client = new()
        {
            Handshake = (_, _, _) =>
            {
                trace.Add("handshake");
                return Task.FromResult(CreateHandshake());
            },
            Open = (_, _) =>
            {
                trace.Add("open");
                return Task.FromResult<InstallerPackageOpenResult>(new InstallerPackageOpenSuccess(CreateRelease()));
            }
        };
        ReleaseVerificationController controller = new(reviewed, () => client, local);
        Task catalog = controller.LoadCatalogAsync();
        await AwaitBounded(catalogStarted.Task);

        Task replacement = controller.StartLocalAsync(selectedDirectory);
        controller.Snapshot.State.Should().Be(ReleaseVerificationState.CleaningUp);
        await AwaitBounded(replacement);
        await AwaitBounded(catalog);

        trace.Should().Equal("catalog-start", "catalog-cancelled", "handshake", "local-prepare", "open");
        controller.Snapshot.State.Should().Be(ReleaseVerificationState.Verified);
        controller.Snapshot.Source.Should().Be(ReleasePackageSource.LocalFolder);
        controller.Snapshot.Releases.Should().BeEmpty("the cancelled catalog result must not replace local state");
        controller.Snapshot.SelectedRelease.Should().BeNull();
        controller.Snapshot.ToString().Should().NotContain(selectedDirectory);
        reviewed.LoadCalls.Should().Be(1);
        local.Paths.Should().Equal(selectedDirectory);
        await AwaitBounded(controller.DisposeAsync().AsTask());
    }

    [Test]
    public async Task LocalSelectionCannotReplaceRetryAfterRefreshedCatalogBecomesAttempt()
    {
        ReviewedReleaseCandidate initial = Candidate();
        ReviewedReleaseCandidate refreshed = Candidate();
        FakeReleaseService reviewed = new();
        reviewed.Catalogs.Enqueue([initial]);
        reviewed.Catalogs.Enqueue([refreshed]);
        reviewed.Prepare = (_, progress, _) =>
        {
            EmitValidProgress(progress!);
            return Task.FromResult<IPreparedReleasePackage>(new FakePreparedPackage());
        };
        FakeLocalReleaseService local = new();
        Queue<FakeClient> clients = new([
            RejectingClient(),
            RejectingClient()
        ]);
        ReleaseVerificationController controller = new(reviewed, () => clients.Dequeue(), local);
        await AwaitBounded(controller.LoadCatalogAsync());
        await AwaitBounded(controller.StartAsync());

        Exception? replacementFailure = null;
        controller.Changed += (_, _) =>
        {
            ReleaseVerificationSnapshot snapshot = controller.Snapshot;
            if (
                snapshot.State == ReleaseVerificationState.Handshaking
                && snapshot.Source == ReleasePackageSource.ReviewedDownload
                && snapshot.AttemptNumber == 2
                && replacementFailure is null
            )
            {
                try
                {
                    _ = controller.StartLocalAsync("/selected/racing-local-package");
                }
                catch (Exception ex)
                {
                    replacementFailure = ex;
                }
            }
        };

        await AwaitBounded(controller.RetryAsync());

        replacementFailure.Should().BeOfType<InvalidOperationException>();
        local.Paths.Should().BeEmpty("an active reviewed retry must never be reclassified as replaceable catalog work");
        controller.Snapshot.Source.Should().Be(ReleasePackageSource.ReviewedDownload);
        controller.Snapshot.AttemptNumber.Should().Be(2);
        await AwaitBounded(controller.DisposeAsync().AsTask());
    }

    private static FakeClient RejectingClient()
    {
        return new FakeClient
        {
            Open = (_, _) => Task.FromResult<InstallerPackageOpenResult>(new InstallerPackageOpenRejection(
                ProtocolPrePlanErrorCode.PackageRejected,
                ProtocolNextAction.ReopenVerifiedPackage,
                "safe rejection",
                false
            ))
        };
    }

    private static FakeLocalReleaseService PreparedLocalService(FakePreparedPackage package)
    {
        return new FakeLocalReleaseService
        {
            Prepare = (_, _) => Task.FromResult<IPreparedReleasePackage>(package)
        };
    }

    private static ReviewedReleaseCandidate Candidate()
    {
        ForkReleaseIdentity identity = ForkReleaseIdentity.Parse(ReviewedTag);
        object[] assets = Enum.GetValues<ReviewedReleaseAssetKind>().Select(kind => (object)new
        {
            name = ReviewedGitHubReleaseUris.GetAssetName(identity, kind),
            size = 10,
            state = "uploaded",
            browser_download_url = ReviewedGitHubReleaseUris.GetAssetUri(identity, kind).AbsoluteUri
        }).ToArray();
        byte[] catalog = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new
            {
                tag_name = identity.Tag,
                draft = false,
                prerelease = true,
                assets
            }
        });
        return ReviewedGitHubReleaseCatalog.Parse(catalog).Single();
    }

    private static void EmitValidProgress(IProgress<ReviewedReleasePreparationProgress> progress)
    {
        progress.Report(new ReviewedReleasePreparationProgress(
            ReviewedReleasePreparationStage.ObservingTag,
            null,
            0,
            0,
            0,
            0
        ));
        ReviewedReleaseAssetKind[] kinds = Enum.GetValues<ReviewedReleaseAssetKind>();
        for (int index = 0; index < kinds.Length; index++)
        {
            progress.Report(new ReviewedReleasePreparationProgress(
                ReviewedReleasePreparationStage.Downloading,
                kinds[index],
                index + 1,
                kinds.Length,
                (index + 1) * 10,
                kinds.Length * 10
            ));
        }
        progress.Report(new ReviewedReleasePreparationProgress(
            ReviewedReleasePreparationStage.RefreshingTag,
            null,
            0,
            0,
            0,
            0
        ));
    }

    private static InstallerPackageOpenInput CreatePackage()
    {
        return new(
            ReviewedTag,
            new string('1', 40),
            "/proc/1/fd/9/package.zip",
            "/proc/1/fd/9/SHA256SUMS",
            "/proc/1/fd/9/build-metadata.json",
            "/proc/1/fd/9/install-manifest.json",
            "/proc/1/fd/9/attestation.jsonl",
            "/proc/1/fd/9/attestation.sha256"
        );
    }

    private static HandshakeEvent CreateHandshake()
    {
        return new(
            ProtocolSessionId.Parse("11111111111111111111111111111111"),
            "1",
            [ProcessInstallerProtocolClient.PackageVerificationCapability]
        );
    }

    private static ProtocolReleaseIdentity CreateRelease()
    {
        return new(
            ForkReleaseIdentity.RepositoryUrl,
            ReviewedTag,
            "4.5.4-unofficial.4eh5xitv6787h645ebv.linux.alpha.2",
            "SMAPI-4.5.4-unofficial.4eh5xitv6787h645ebv.linux.alpha.2-linux-x64-installer.zip",
            new string('1', 40),
            new string('2', 40),
            new string('a', 64),
            10,
            "4eh5xitv6787h645ebv/SMAPI/.github/workflows/linux-alpha-release.yml@refs/tags/" + ReviewedTag,
            "Release",
            "linux-x64"
        );
    }

    private static TaskCompletionSource NewCompletion()
    {
        return new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static TaskCompletionSource<T> NewCompletion<T>()
    {
        return new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static async Task AwaitBounded(Task task)
    {
        await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class FakeReleaseService : IReviewedReleaseService
    {
        public Queue<IReadOnlyList<ReviewedReleaseCandidate>> Catalogs { get; } = new();
        public List<ReviewedReleaseCandidate> Candidates { get; } = [];
        public Func<CancellationToken, Task<IReadOnlyList<ReviewedReleaseCandidate>>>? Load { get; init; }
        public Func<ReviewedReleaseCandidate, IProgress<ReviewedReleasePreparationProgress>?, CancellationToken, Task<IPreparedReleasePackage>>? Prepare { get; set; }
        public int LoadCalls { get; private set; }
        public int PrepareCalls { get; private set; }

        public Task<IReadOnlyList<ReviewedReleaseCandidate>> LoadCatalogAsync(
            CancellationToken cancellationToken = default
        )
        {
            this.LoadCalls++;
            if (this.Load is not null)
                return this.Load(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(this.Catalogs.Count > 0
                ? this.Catalogs.Dequeue()
                : (IReadOnlyList<ReviewedReleaseCandidate>)[]);
        }

        public Task<IPreparedReleasePackage> PrepareAsync(
            ReviewedReleaseCandidate candidate,
            IProgress<ReviewedReleasePreparationProgress>? progress = null,
            CancellationToken cancellationToken = default
        )
        {
            this.PrepareCalls++;
            this.Candidates.Add(candidate);
            return this.Prepare?.Invoke(candidate, progress, cancellationToken)
                ?? throw new AssertionException("Unexpected reviewed package preparation.");
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeLocalReleaseService : ILocalReleasePackageService
    {
        public List<string> Paths { get; } = [];
        public Func<string, CancellationToken, Task<IPreparedReleasePackage>> Prepare { get; init; } =
            (_, _) => throw new AssertionException("Unexpected local package preparation.");

        public Task<IPreparedReleasePackage> PrepareAsync(
            string selectedDirectory,
            CancellationToken cancellationToken = default
        )
        {
            this.Paths.Add(selectedDirectory);
            return this.Prepare(selectedDirectory, cancellationToken);
        }
    }

    private sealed class FakePreparedPackage : IPreparedReleasePackage
    {
        private readonly List<string>? Trace;

        public InstallerPackageOpenInput Package { get; } = CreatePackage();
        public int DisposeCalls { get; private set; }

        public FakePreparedPackage(List<string>? trace = null)
        {
            this.Trace = trace;
        }

        public ValueTask DisposeAsync()
        {
            this.DisposeCalls++;
            this.Trace?.Add("prepared-dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeClient : IInstallerProtocolClient
    {
        public TaskCompletionSource<InstallerProtocolClientException> Fault { get; } =
            NewCompletion<InstallerProtocolClientException>();
        public TaskCompletionSource OpenStarted { get; } = NewCompletion();
        public Func<string, string, CancellationToken, Task<HandshakeEvent>> Handshake { get; init; } =
            (_, _, _) => Task.FromResult(CreateHandshake());
        public Func<InstallerPackageOpenInput, CancellationToken, Task<InstallerPackageOpenResult>> Open { get; init; } =
            (_, _) => Task.FromResult<InstallerPackageOpenResult>(new InstallerPackageOpenSuccess(CreateRelease()));
        public int OpenCalls { get; private set; }
        public int DisposeCalls { get; private set; }

        public Task<InstallerProtocolClientException> SessionFaulted => this.Fault.Task;

        public Task<HandshakeEvent> HandshakeAsync(
            string clientName,
            string clientVersion,
            CancellationToken cancellationToken = default
        )
        {
            return this.Handshake(clientName, clientVersion, cancellationToken);
        }

        public Task<InstallerPackageOpenResult> OpenPackageAsync(
            InstallerPackageOpenInput package,
            CancellationToken cancellationToken = default
        )
        {
            this.OpenCalls++;
            Task<InstallerPackageOpenResult> result = this.Open(package, cancellationToken);
            this.OpenStarted.TrySetResult();
            return result;
        }

        public Task<IReadOnlyList<ProtocolGameCandidate>> DiscoverGamesAsync(
            CancellationToken cancellationToken = default
        ) => throw new AssertionException("Release verification must not discover games.");

        public Task<ProtocolGameCandidate> ValidateGameAsync(
            string canonicalPath,
            CancellationToken cancellationToken = default
        ) => throw new AssertionException("Release verification must not validate games.");

        public Task<InstallerReadOnlyPlanResult> InspectPlanAsync(
            string canonicalGamePath,
            InstallerOperation operation,
            CancellationToken cancellationToken = default
        ) => throw new AssertionException("Release verification must not inspect a plan.");

        public ValueTask DisposeAsync()
        {
            this.DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }
}
