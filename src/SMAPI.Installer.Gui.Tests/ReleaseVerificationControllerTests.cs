using System.Text.Json;
using FluentAssertions;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Gui.Backend;
using StardewModdingAPI.Installer.Gui.Frontend;

namespace StardewModdingAPI.Installer.Gui.Tests;

[NonParallelizable]
internal sealed class ReleaseVerificationControllerTests
{
    [Test]
    public async Task EmptyCurrentCatalogNeverCreatesDownloadOrBackendAuthority()
    {
        FakeReleaseService service = new([]);
        int clients = 0;
        ReleaseVerificationController controller = new(service, () =>
        {
            clients++;
            throw new AssertionException("An empty compatible catalog must not create a backend client.");
        });

        await AwaitBounded(controller.LoadCatalogAsync());

        controller.Snapshot.State.Should().Be(ReleaseVerificationState.NoCompatibleRelease);
        controller.Snapshot.Releases.Should().BeEmpty();
        controller.Snapshot.SelectedRelease.Should().BeNull();
        controller.Snapshot.CanStart.Should().BeFalse();
        service.PrepareCalls.Should().Be(0);
        clients.Should().Be(0);
        await AwaitBounded(controller.DisposeAsync().AsTask());
    }

    [Test]
    public async Task SelectionRequiresTheExactCurrentCatalogCandidateReference()
    {
        ReviewedReleaseCandidate listed = Candidate();
        ReviewedReleaseCandidate equalButDifferent = Candidate();
        FakeReleaseService service = new([listed]);
        ReleaseVerificationController controller = new(service, () => new FakeClient());
        await AwaitBounded(controller.LoadCatalogAsync());

        Action action = () => controller.SelectRelease(equalButDifferent);

        action.Should().Throw<ArgumentException>().WithMessage("*exact current catalog candidate instance*");
        controller.Snapshot.SelectedRelease.Should().BeSameAs(listed);
        await AwaitBounded(controller.DisposeAsync().AsTask());
    }

    [Test]
    public async Task HandshakePrecedesPreparationAndOnlyOpenedPackagePublishesVerifiedIdentity()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        List<string> trace = [];
        TaskCompletionSource<HandshakeEvent> handshake = NewCompletion<HandshakeEvent>();
        TaskCompletionSource<InstallerPackageOpenResult> opened = NewCompletion<InstallerPackageOpenResult>();
        FakePreparedPackage package = new();
        FakeReleaseService service = new([candidate])
        {
            Prepare = (selected, progress, _) =>
            {
                trace.Add("prepare");
                selected.Should().BeSameAs(candidate);
                EmitValidProgress(progress!);
                return Task.FromResult<IPreparedReleasePackage>(package);
            }
        };
        FakeClient client = new()
        {
            Handshake = (_, _, _) =>
            {
                trace.Add("handshake");
                return handshake.Task;
            },
            Open = (_, _) =>
            {
                trace.Add("open");
                return opened.Task;
            }
        };
        ReleaseVerificationController controller = new(service, () => client);
        await AwaitBounded(controller.LoadCatalogAsync());

        Task running = controller.StartAsync();
        await AwaitBounded(client.HandshakeStarted.Task);
        trace.Should().Equal("handshake");
        service.PrepareCalls.Should().Be(0);
        controller.Snapshot.State.Should().Be(ReleaseVerificationState.Handshaking);

        handshake.SetResult(CreateHandshake());
        await AwaitBounded(client.OpenStarted.Task);
        trace.Should().Equal("handshake", "prepare", "open");
        controller.Snapshot.State.Should().Be(ReleaseVerificationState.OpeningPackage);
        controller.Snapshot.VerifiedRelease.Should().BeNull();
        package.DisposeCalls.Should().Be(0, "the preparation lease must remain live until package-open settles");

        ProtocolReleaseIdentity release = CreateRelease();
        opened.SetResult(new InstallerPackageOpenSuccess(release));
        await AwaitBounded(running);

        controller.Snapshot.State.Should().Be(ReleaseVerificationState.Verified);
        controller.Snapshot.VerifiedRelease.Should().BeSameAs(release);
        package.DisposeCalls.Should().Be(1);
        client.DisposeCalls.Should().Be(0, "the verified backend retains package authority for the next screen");
        await AwaitBounded(controller.DisposeAsync().AsTask());
    }

    [Test]
    public async Task VerifiedSessionTransfersExactlyOnceAndBecomesTheSoleClientOwner()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        ProtocolReleaseIdentity release = CreateRelease();
        ProtocolGameCandidate discovered = new("/games/Stardew Valley", LinuxGameFolderStatus.Valid, "Stardew Valley 1.6.15");
        FakeClient client = new()
        {
            Open = (_, _) => Task.FromResult<InstallerPackageOpenResult>(new InstallerPackageOpenSuccess(release)),
            Discover = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([discovered]),
            Validate = (path, _) => Task.FromResult(discovered with { CanonicalPath = path })
        };
        ReleaseVerificationController controller = new(PreparedService(candidate, new FakePreparedPackage()), () => client);
        await AwaitBounded(controller.LoadCatalogAsync());
        await AwaitBounded(controller.StartAsync());

        IVerifiedInstallerSession session = controller.TakeVerifiedSession();
        Func<IVerifiedInstallerSession> secondTake = controller.TakeVerifiedSession;
        Func<Task> restart = () => controller.LoadCatalogAsync();

        session.Release.Should().BeSameAs(release);
        (await session.DiscoverGamesAsync()).Should().Equal(discovered);
        (await session.ValidateGameAsync("/games/manual")).CanonicalPath.Should().Be("/games/manual");
        secondTake.Should().Throw<InvalidOperationException>();
        await restart.Should().ThrowAsync<InvalidOperationException>();

        await AwaitBounded(controller.DisposeAsync().AsTask());
        client.DisposeCalls.Should().Be(0, "the transferred session, not the controller, owns the backend client");
        await AwaitBounded(session.DisposeAsync().AsTask());
        client.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task FaultAfterVerifiedSessionTransferBelongsOnlyToTheTransferredOwner()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        FakeClient client = new();
        ReleaseVerificationController controller = new(PreparedService(candidate, new FakePreparedPackage()), () => client);
        await AwaitBounded(controller.LoadCatalogAsync());
        await AwaitBounded(controller.StartAsync());
        IVerifiedInstallerSession session = controller.TakeVerifiedSession();

        InstallerProtocolClientException fault = new("late transferred fault");
        client.Fault.SetResult(fault);
        (await session.SessionFaulted).Should().BeSameAs(fault);
        await AwaitBounded(controller.DisposeAsync().AsTask());

        client.DisposeCalls.Should().Be(0, "the stopped controller watcher must not reclaim transferred authority");
        await AwaitBounded(session.DisposeAsync().AsTask());
        client.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task TransferredSessionDisposalCancelsActiveCommandAndDisposesClientOnce()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        TaskCompletionSource discoveryStarted = NewCompletion();
        FakeClient client = new()
        {
            Discover = async cancellationToken =>
            {
                discoveryStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Array.Empty<ProtocolGameCandidate>();
            }
        };
        ReleaseVerificationController controller = new(PreparedService(candidate, new FakePreparedPackage()), () => client);
        await AwaitBounded(controller.LoadCatalogAsync());
        await AwaitBounded(controller.StartAsync());
        IVerifiedInstallerSession session = controller.TakeVerifiedSession();
        Task discovery = session.DiscoverGamesAsync();
        await AwaitBounded(discoveryStarted.Task);

        Task firstDisposal = session.DisposeAsync().AsTask();
        Task secondDisposal = session.DisposeAsync().AsTask();

        await AwaitBounded(firstDisposal);
        await AwaitBounded(secondDisposal);
        await FluentActions.Awaiting(async () => await discovery).Should().ThrowAsync<OperationCanceledException>();
        client.DisposeCalls.Should().Be(1);
        await FluentActions.Awaiting(async () => await session.DiscoverGamesAsync()).Should().ThrowAsync<ObjectDisposedException>();
        await AwaitBounded(controller.DisposeAsync().AsTask());
        client.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task CancelWaitsForOpenLeaseDisposalAndBackendReapBeforeEnablingRetry()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        TaskCompletionSource<InstallerPackageOpenResult> opened = NewCompletion<InstallerPackageOpenResult>();
        TaskCompletionSource packageDispose = NewCompletion();
        TaskCompletionSource clientDispose = NewCompletion();
        FakePreparedPackage package = new() { Disposal = packageDispose.Task };
        FakeReleaseService service = PreparedService(candidate, package);
        FakeClient client = new()
        {
            Open = (_, _) => opened.Task,
            Disposal = clientDispose.Task
        };
        ReleaseVerificationController controller = new(service, () => client);
        await AwaitBounded(controller.LoadCatalogAsync());
        Task running = controller.StartAsync();
        await AwaitBounded(client.OpenStarted.Task);

        Task cancellation = controller.CancelAsync();
        controller.Snapshot.State.Should().Be(ReleaseVerificationState.CleaningUp);
        controller.Snapshot.CanRetry.Should().BeFalse();
        cancellation.IsCompleted.Should().BeFalse();
        opened.SetResult(new InstallerPackageOpenRejection(
            ProtocolPrePlanErrorCode.RequestCancelled,
            ProtocolNextAction.RetryRequest,
            "cancelled",
            false
        ));
        await AwaitBounded(package.DisposeStarted.Task);
        cancellation.IsCompleted.Should().BeFalse();
        packageDispose.SetResult();
        await AwaitBounded(client.DisposeStarted.Task);
        cancellation.IsCompleted.Should().BeFalse();
        controller.Snapshot.CanRetry.Should().BeFalse();

        clientDispose.SetResult();
        await AwaitBounded(cancellation);
        await AwaitBounded(running);

        controller.Snapshot.State.Should().Be(ReleaseVerificationState.Cancelled);
        controller.Snapshot.CanRetry.Should().BeTrue();
        controller.Snapshot.RejectionCode.Should().BeNull("a cancellation-winning late rejection is stale");
        controller.Snapshot.RejectionNextAction.Should().BeNull();
        package.DisposeCalls.Should().Be(1);
        client.DisposeCalls.Should().Be(1);
        await AwaitBounded(controller.DisposeAsync().AsTask());
    }

    [Test]
    public async Task SessionFaultDuringOpenWinsAndSettlesTheOperationBeforeCleanup()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        TaskCompletionSource<InstallerPackageOpenResult> opened = NewCompletion<InstallerPackageOpenResult>();
        FakePreparedPackage package = new();
        FakeReleaseService service = PreparedService(candidate, package);
        FakeClient client = new() { Open = (_, _) => opened.Task };
        ReleaseVerificationController controller = new(service, () => client);
        await AwaitBounded(controller.LoadCatalogAsync());
        Task running = controller.StartAsync();
        await AwaitBounded(client.OpenStarted.Task);

        client.Fault.SetResult(new InstallerProtocolClientException("private /home/alice ?token=SECRET"));
        await Task.Yield();
        running.IsCompleted.Should().BeFalse("the raced package-open task must be observed and settled");
        opened.SetResult(new InstallerPackageOpenSuccess(CreateRelease()));
        await AwaitBounded(running);

        controller.Snapshot.State.Should().Be(ReleaseVerificationState.Failed);
        controller.Snapshot.Error.Should().Be(ReleaseVerificationError.SessionFaulted);
        controller.Snapshot.VerifiedRelease.Should().BeNull();
        controller.Snapshot.ToString().Should().NotContain("alice").And.NotContain("SECRET");
        package.DisposeCalls.Should().Be(1);
        client.DisposeCalls.Should().Be(1);
        await AwaitBounded(controller.DisposeAsync().AsTask());
    }

    [Test]
    public async Task FaultAfterSuccessRevokesIdentityAndConcurrentDisposeAwaitsTheSameReap()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        TaskCompletionSource clientDispose = NewCompletion();
        FakePreparedPackage package = new();
        FakeReleaseService service = PreparedService(candidate, package);
        FakeClient client = new() { Disposal = clientDispose.Task };
        ReleaseVerificationController controller = new(service, () => client);
        await AwaitBounded(controller.LoadCatalogAsync());
        await AwaitBounded(controller.StartAsync());
        controller.Snapshot.State.Should().Be(ReleaseVerificationState.Verified);

        client.Fault.SetResult(new InstallerProtocolClientException("late fault"));
        await AwaitBounded(client.DisposeStarted.Task);
        controller.Snapshot.State.Should().Be(ReleaseVerificationState.CleaningUp);
        controller.Snapshot.VerifiedRelease.Should().BeNull();
        controller.Snapshot.CanRetry.Should().BeFalse();

        Task controllerDisposal = controller.DisposeAsync().AsTask();
        controllerDisposal.IsCompleted.Should().BeFalse("controller disposal must not miss an in-progress verified-session reap");
        clientDispose.SetResult();
        await AwaitBounded(controllerDisposal);

        controller.Snapshot.State.Should().Be(ReleaseVerificationState.Disposed);
        client.DisposeCalls.Should().Be(1);
        service.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task RetryRefreshesCatalogAndAuthoritiesAndStopsAfterThreeExplicitAttempts()
    {
        ReviewedReleaseCandidate first = Candidate();
        ReviewedReleaseCandidate second = Candidate();
        ReviewedReleaseCandidate third = Candidate();
        FakeReleaseService service = new([first]);
        service.Catalogs.Enqueue([second]);
        service.Catalogs.Enqueue([third]);
        service.Prepare = (_, progress, _) =>
        {
            EmitValidProgress(progress!);
            return Task.FromResult<IPreparedReleasePackage>(new FakePreparedPackage());
        };
        List<FakeClient> clients = [];
        Func<IInstallerProtocolClient> factory = () =>
        {
            FakeClient client = new()
            {
                Open = (_, _) => Task.FromResult<InstallerPackageOpenResult>(new InstallerPackageOpenRejection(
                    ProtocolPrePlanErrorCode.PackageRejected,
                    ProtocolNextAction.RetryRequest,
                    "safe rejection",
                    false
                ))
            };
            clients.Add(client);
            return client;
        };
        ReleaseVerificationController controller = new(service, factory);
        await AwaitBounded(controller.LoadCatalogAsync());
        await AwaitBounded(controller.StartAsync());
        await AwaitBounded(controller.RetryAsync());
        await AwaitBounded(controller.RetryAsync());

        service.Candidates.Should().HaveCount(3);
        service.Candidates[0].Should().BeSameAs(first);
        service.Candidates[1].Should().BeSameAs(second);
        service.Candidates[2].Should().BeSameAs(third);
        service.LoadCalls.Should().Be(3);
        clients.Should().HaveCount(3).And.OnlyContain(client => client.DisposeCalls == 1);
        controller.Snapshot.AttemptNumber.Should().Be(3);
        controller.Snapshot.CanRetry.Should().BeFalse();

        Action fourth = () => _ = controller.RetryAsync();
        fourth.Should().Throw<InvalidOperationException>().WithMessage("*retry limit*");
        controller.Snapshot.Error.Should().Be(ReleaseVerificationError.RetryLimitReached);
        clients.Should().HaveCount(3);
        await AwaitBounded(controller.DisposeAsync().AsTask());
    }

    [Test]
    public async Task TerminalPackageRejectionExposesOnlyTypedSafeActionAndCannotRetry()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        FakePreparedPackage package = new();
        FakeReleaseService service = PreparedService(candidate, package);
        FakeClient client = new()
        {
            Open = (_, _) => Task.FromResult<InstallerPackageOpenResult>(new InstallerPackageOpenRejection(
                ProtocolPrePlanErrorCode.PackageRejected,
                ProtocolNextAction.StartNewSession,
                "private /proc/123/fd/9 ?token=SECRET",
                true
            ))
        };
        ReleaseVerificationController controller = new(service, () => client);
        await AwaitBounded(controller.LoadCatalogAsync());
        await AwaitBounded(controller.StartAsync());

        ReleaseVerificationSnapshot snapshot = controller.Snapshot;
        snapshot.Error.Should().Be(ReleaseVerificationError.PackageRejected);
        snapshot.RejectionCode.Should().Be(ProtocolPrePlanErrorCode.PackageRejected);
        snapshot.RejectionNextAction.Should().Be(ProtocolNextAction.StartNewSession);
        snapshot.RejectionIsTerminal.Should().BeTrue();
        snapshot.CanRetry.Should().BeFalse();
        snapshot.ToString().Should().NotContain("/proc/").And.NotContain("SECRET");
        Action retry = () => _ = controller.RetryAsync();
        retry.Should().Throw<InvalidOperationException>().WithMessage("*new installer session*");
        await AwaitBounded(controller.DisposeAsync().AsTask());
    }

    [Test]
    public async Task InvalidOrOutOfOrderProgressFailsClosedBeforePackageOpen()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        FakeReleaseService service = new([candidate])
        {
            Prepare = (_, progress, _) =>
            {
                progress!.Report(NonDownload(ReviewedReleasePreparationStage.ObservingTag));
                progress.Report(new ReviewedReleasePreparationProgress(
                    ReviewedReleasePreparationStage.Downloading,
                    ReviewedReleaseAssetKind.InstallManifest,
                    1,
                    6,
                    10,
                    60
                ));
                return Task.FromResult<IPreparedReleasePackage>(new FakePreparedPackage());
            }
        };
        FakeClient client = new();
        ReleaseVerificationController controller = new(service, () => client);
        await AwaitBounded(controller.LoadCatalogAsync());

        await AwaitBounded(controller.StartAsync());

        controller.Snapshot.State.Should().Be(ReleaseVerificationState.Failed);
        controller.Snapshot.Error.Should().Be(ReleaseVerificationError.PreparationFailed);
        client.OpenCalls.Should().Be(0);
        client.DisposeCalls.Should().Be(1);
        await AwaitBounded(controller.DisposeAsync().AsTask());
    }

    [Test]
    public async Task DuplicateSemanticCatalogIdentityFailsBeforeReadyOrSelection()
    {
        ReviewedReleaseCandidate first = Candidate();
        ReviewedReleaseCandidate separatelyParsed = Candidate();
        FakeReleaseService service = new([first, separatelyParsed]);
        ReleaseVerificationController controller = new(service, () => new FakeClient());

        await AwaitBounded(controller.LoadCatalogAsync());

        controller.Snapshot.State.Should().Be(ReleaseVerificationState.Failed);
        controller.Snapshot.Error.Should().Be(ReleaseVerificationError.CatalogUnavailable);
        controller.Snapshot.Releases.Should().BeEmpty();
        controller.Snapshot.SelectedRelease.Should().BeNull();
        controller.Snapshot.CanStart.Should().BeFalse();
        await AwaitBounded(controller.DisposeAsync().AsTask());
    }

    [Test]
    public async Task RepeatedRefreshingTagProgressFailsClosed()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        FakeReleaseService service = new([candidate])
        {
            Prepare = (_, progress, _) =>
            {
                EmitValidProgress(progress!);
                progress!.Report(NonDownload(ReviewedReleasePreparationStage.RefreshingTag));
                return Task.FromResult<IPreparedReleasePackage>(new FakePreparedPackage());
            }
        };
        FakeClient client = new();
        ReleaseVerificationController controller = new(service, () => client);
        await AwaitBounded(controller.LoadCatalogAsync());

        await AwaitBounded(controller.StartAsync());

        controller.Snapshot.State.Should().Be(ReleaseVerificationState.Failed);
        controller.Snapshot.Error.Should().Be(ReleaseVerificationError.PreparationFailed);
        client.OpenCalls.Should().Be(0);
        client.DisposeCalls.Should().Be(1);
        await AwaitBounded(controller.DisposeAsync().AsTask());
    }

    [Test]
    public async Task DisposalAttemptsServiceCleanupAfterClientCleanupFailureAndSettlesDisposed()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        FakePreparedPackage package = new();
        FakeReleaseService service = PreparedService(candidate, package);
        service.Disposal = Task.FromException(new IOException("private service failure"));
        FakeClient client = new()
        {
            Disposal = Task.FromException(new IOException("private client failure"))
        };
        ReleaseVerificationController controller = new(service, () => client);
        await AwaitBounded(controller.LoadCatalogAsync());
        await AwaitBounded(controller.StartAsync());

        await AwaitBounded(controller.DisposeAsync().AsTask());

        client.DisposeCalls.Should().Be(1);
        service.DisposeCalls.Should().Be(1, "service cleanup remains mandatory after client cleanup fails");
        controller.Snapshot.State.Should().Be(ReleaseVerificationState.Disposed);
        controller.Snapshot.Error.Should().Be(ReleaseVerificationError.CleanupFailed);
        controller.Snapshot.VerifiedRelease.Should().BeNull();
    }

    [Test]
    public async Task PreparedLeaseDisposalFailureIsCalledOnceAndNeverPublishesSuccessOrRetry()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        FakePreparedPackage package = new()
        {
            Disposal = Task.FromException(new IOException("private lease cleanup failure"))
        };
        FakeReleaseService service = PreparedService(candidate, package);
        FakeClient client = new();
        ReleaseVerificationController controller = new(service, () => client);
        await AwaitBounded(controller.LoadCatalogAsync());

        await AwaitBounded(controller.StartAsync());

        controller.Snapshot.State.Should().Be(ReleaseVerificationState.Failed);
        controller.Snapshot.Error.Should().Be(ReleaseVerificationError.CleanupFailed);
        controller.Snapshot.VerifiedRelease.Should().BeNull();
        controller.Snapshot.CanRetry.Should().BeFalse();
        package.DisposeCalls.Should().Be(1, "a failed asynchronous disposal must not be invoked twice");
        client.DisposeCalls.Should().Be(1);
        await AwaitBounded(controller.DisposeAsync().AsTask());
    }

    [Test]
    public async Task RejectionLeaseDisposalFailureSupersedesRetryableResultAndClearsAuthority()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        FakePreparedPackage package = new()
        {
            Disposal = Task.FromException(new IOException("private rejection lease cleanup failure"))
        };
        FakeReleaseService service = PreparedService(candidate, package);
        FakeClient client = new()
        {
            Open = (_, _) => Task.FromResult<InstallerPackageOpenResult>(new InstallerPackageOpenRejection(
                ProtocolPrePlanErrorCode.PackageRejected,
                ProtocolNextAction.RetryRequest,
                "retryable before cleanup",
                false
            ))
        };
        ReleaseVerificationController controller = new(service, () => client);
        await AwaitBounded(controller.LoadCatalogAsync());

        await AwaitBounded(controller.StartAsync());

        ReleaseVerificationSnapshot snapshot = controller.Snapshot;
        snapshot.State.Should().Be(ReleaseVerificationState.Failed);
        snapshot.Error.Should().Be(ReleaseVerificationError.CleanupFailed);
        snapshot.CanRetry.Should().BeFalse();
        snapshot.VerifiedRelease.Should().BeNull();
        snapshot.RejectionCode.Should().BeNull();
        snapshot.RejectionNextAction.Should().BeNull();
        snapshot.RejectionIsTerminal.Should().BeFalse();
        package.DisposeCalls.Should().Be(1);
        client.DisposeCalls.Should().Be(1);
        await AwaitBounded(controller.DisposeAsync().AsTask());
    }

    [Test]
    public async Task CancellationLeaseDisposalFailureSupersedesCancelledAndReapsClientOnce()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        TaskCompletionSource<InstallerPackageOpenResult> opened = NewCompletion<InstallerPackageOpenResult>();
        FakePreparedPackage package = new()
        {
            Disposal = Task.FromException(new IOException("private cancelled lease cleanup failure"))
        };
        FakeReleaseService service = PreparedService(candidate, package);
        FakeClient client = new() { Open = (_, _) => opened.Task };
        ReleaseVerificationController controller = new(service, () => client);
        await AwaitBounded(controller.LoadCatalogAsync());
        Task running = controller.StartAsync();
        await AwaitBounded(client.OpenStarted.Task);

        Task cancellation = controller.CancelAsync();
        opened.SetResult(new InstallerPackageOpenRejection(
            ProtocolPrePlanErrorCode.RequestCancelled,
            ProtocolNextAction.RetryRequest,
            "late cancellation rejection",
            false
        ));
        await AwaitBounded(cancellation);
        await AwaitBounded(running);

        ReleaseVerificationSnapshot snapshot = controller.Snapshot;
        snapshot.State.Should().Be(ReleaseVerificationState.Failed);
        snapshot.Error.Should().Be(ReleaseVerificationError.CleanupFailed);
        snapshot.CanRetry.Should().BeFalse();
        snapshot.VerifiedRelease.Should().BeNull();
        snapshot.RejectionCode.Should().BeNull();
        snapshot.RejectionNextAction.Should().BeNull();
        package.DisposeCalls.Should().Be(1);
        client.DisposeCalls.Should().Be(1);
        await AwaitBounded(controller.DisposeAsync().AsTask());
    }

    [Test]
    public async Task ClientDisposalFailureSupersedesRejectionAndClearsAuthority()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        FakePreparedPackage package = new();
        FakeReleaseService service = PreparedService(candidate, package);
        FakeClient client = new()
        {
            Open = (_, _) => Task.FromResult<InstallerPackageOpenResult>(new InstallerPackageOpenRejection(
                ProtocolPrePlanErrorCode.PackageRejected,
                ProtocolNextAction.RetryRequest,
                "retryable before client cleanup",
                false
            )),
            Disposal = Task.FromException(new IOException("private client cleanup failure"))
        };
        ReleaseVerificationController controller = new(service, () => client);
        await AwaitBounded(controller.LoadCatalogAsync());

        await AwaitBounded(controller.StartAsync());

        ReleaseVerificationSnapshot snapshot = controller.Snapshot;
        snapshot.State.Should().Be(ReleaseVerificationState.Failed);
        snapshot.Error.Should().Be(ReleaseVerificationError.CleanupFailed);
        snapshot.CanRetry.Should().BeFalse();
        snapshot.VerifiedRelease.Should().BeNull();
        snapshot.RejectionCode.Should().BeNull();
        snapshot.RejectionNextAction.Should().BeNull();
        package.DisposeCalls.Should().Be(1);
        client.DisposeCalls.Should().Be(1);
        await AwaitBounded(controller.DisposeAsync().AsTask());
    }

    [Test]
    public async Task ConcurrentCancelAndDisposeJoinThrowingCancellationCallbackAndMandatoryCleanup()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        TaskCompletionSource<InstallerPackageOpenResult> opened = NewCompletion<InstallerPackageOpenResult>();
        TaskCompletionSource callbackEntered = NewCompletion();
        TaskCompletionSource releaseCallback = NewCompletion();
        FakePreparedPackage package = new();
        FakeReleaseService service = PreparedService(candidate, package);
        FakeClient client = new()
        {
            Open = (_, cancellationToken) =>
            {
                cancellationToken.Register(() =>
                {
                    callbackEntered.TrySetResult();
                    if (!releaseCallback.Task.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("The deterministic cancellation-callback gate wasn't released.");
                    throw new InvalidOperationException("private throwing cancellation callback");
                });
                return opened.Task;
            }
        };
        ReleaseVerificationController controller = new(service, () => client);
        await AwaitBounded(controller.LoadCatalogAsync());
        Task running = controller.StartAsync();
        await AwaitBounded(client.OpenStarted.Task);

        Task cancellation = Task.Run(controller.CancelAsync);
        await AwaitBounded(callbackEntered.Task);
        Task disposal = Task.Run(() => controller.DisposeAsync().AsTask());
        cancellation.IsCompleted.Should().BeFalse();
        disposal.IsCompleted.Should().BeFalse();
        client.DisposeCalls.Should().Be(0);
        service.DisposeCalls.Should().Be(0);

        releaseCallback.SetResult();
        opened.SetResult(new InstallerPackageOpenRejection(
            ProtocolPrePlanErrorCode.RequestCancelled,
            ProtocolNextAction.RetryRequest,
            "late after throwing callback",
            false
        ));
        await AwaitBounded(cancellation);
        await AwaitBounded(running);
        await AwaitBounded(disposal);

        ReleaseVerificationSnapshot snapshot = controller.Snapshot;
        snapshot.State.Should().Be(ReleaseVerificationState.Disposed);
        snapshot.Error.Should().Be(ReleaseVerificationError.CleanupFailed);
        snapshot.CanRetry.Should().BeFalse();
        snapshot.VerifiedRelease.Should().BeNull();
        snapshot.RejectionCode.Should().BeNull();
        package.DisposeCalls.Should().Be(1);
        client.DisposeCalls.Should().Be(1);
        service.DisposeCalls.Should().Be(1);
        await AwaitBounded(controller.DisposeAsync().AsTask());
    }

    [Test]
    public async Task CancelAcceptedAfterFinalCtsDisposalCannotPublishVerified()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        TaskCompletionSource finalCtsDisposed = NewCompletion();
        TaskCompletionSource releaseAuthorityHandoff = NewCompletion();
        FakePreparedPackage package = new();
        FakeReleaseService service = PreparedService(candidate, package);
        FakeClient client = new();
        ReleaseVerificationController controller = new(service, () => client)
        {
            AfterAttemptCancellationDisposedForTesting = () =>
            {
                finalCtsDisposed.TrySetResult();
                if (!releaseAuthorityHandoff.Task.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("The deterministic authority-handoff gate wasn't released.");
            }
        };
        List<ReleaseVerificationState> publishedStates = [];
        object publishedSync = new();
        controller.Changed += (_, _) =>
        {
            lock (publishedSync)
                publishedStates.Add(controller.Snapshot.State);
        };
        await AwaitBounded(controller.LoadCatalogAsync());

        Task running = controller.StartAsync();
        await AwaitBounded(finalCtsDisposed.Task);
        controller.Snapshot.State.Should().Be(ReleaseVerificationState.OpeningPackage);
        controller.Snapshot.CanCancel.Should().BeTrue();
        controller.Snapshot.VerifiedRelease.Should().BeNull();

        Task cancellation = controller.CancelAsync();
        controller.Snapshot.State.Should().Be(ReleaseVerificationState.CleaningUp);
        controller.Snapshot.CanCancel.Should().BeFalse();
        controller.Snapshot.VerifiedRelease.Should().BeNull();
        cancellation.IsCompleted.Should().BeFalse();

        releaseAuthorityHandoff.SetResult();
        await AwaitBounded(cancellation);
        await AwaitBounded(running);

        ReleaseVerificationSnapshot snapshot = controller.Snapshot;
        snapshot.State.Should().Be(ReleaseVerificationState.Cancelled);
        snapshot.Error.Should().Be(ReleaseVerificationError.None);
        snapshot.VerifiedRelease.Should().BeNull();
        snapshot.CanRetry.Should().BeTrue();
        package.DisposeCalls.Should().Be(1);
        client.DisposeCalls.Should().Be(1);
        lock (publishedSync)
            publishedStates.Should().NotContain(ReleaseVerificationState.Verified);
        await AwaitBounded(controller.DisposeAsync().AsTask());
    }

    private static FakeReleaseService PreparedService(
        ReviewedReleaseCandidate candidate,
        FakePreparedPackage package
    )
    {
        return new([candidate])
        {
            Prepare = (_, progress, _) =>
            {
                EmitValidProgress(progress!);
                return Task.FromResult<IPreparedReleasePackage>(package);
            }
        };
    }

    private static void EmitValidProgress(IProgress<ReviewedReleasePreparationProgress> progress)
    {
        progress.Report(NonDownload(ReviewedReleasePreparationStage.ObservingTag));
        ReviewedReleaseAssetKind[] kinds = Enum.GetValues<ReviewedReleaseAssetKind>();
        for (int index = 0; index < kinds.Length; index++)
        {
            progress.Report(new(
                ReviewedReleasePreparationStage.Downloading,
                kinds[index],
                index,
                kinds.Length,
                index * 10,
                kinds.Length * 10
            ));
            progress.Report(new(
                ReviewedReleasePreparationStage.Downloading,
                kinds[index],
                index + 1,
                kinds.Length,
                (index + 1) * 10,
                kinds.Length * 10
            ));
        }
        progress.Report(NonDownload(ReviewedReleasePreparationStage.RefreshingTag));
    }

    private static ReviewedReleasePreparationProgress NonDownload(ReviewedReleasePreparationStage stage)
    {
        return new(stage, null, 0, 0, 0, 0);
    }

    private static ReviewedReleaseCandidate Candidate()
    {
        ForkReleaseIdentity identity = ForkReleaseIdentity.Parse(
            "fork-4eh5xitv6787h645ebv-linux-v4.5.4-alpha.2"
        );
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

    private static InstallerPackageOpenInput CreatePackage()
    {
        return new(
            "fork-4eh5xitv6787h645ebv-linux-v4.5.4-alpha.2",
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
            "fork-4eh5xitv6787h645ebv-linux-v4.5.4-alpha.2",
            "4.5.4-unofficial.4eh5xitv6787h645ebv.linux.alpha.2",
            "SMAPI-4.5.4-unofficial.4eh5xitv6787h645ebv.linux.alpha.2-linux-x64-installer.zip",
            new string('1', 40),
            new string('2', 40),
            new string('a', 64),
            10,
            "4eh5xitv6787h645ebv/SMAPI/.github/workflows/linux-alpha-release.yml@refs/tags/fork-4eh5xitv6787h645ebv-linux-v4.5.4-alpha.2",
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
        public Func<ReviewedReleaseCandidate, IProgress<ReviewedReleasePreparationProgress>?, CancellationToken, Task<IPreparedReleasePackage>>? Prepare { get; set; }
        public List<ReviewedReleaseCandidate> Candidates { get; } = [];
        public int LoadCalls { get; private set; }
        public int PrepareCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public Task Disposal { get; set; } = Task.CompletedTask;

        public FakeReleaseService(IReadOnlyList<ReviewedReleaseCandidate> initial)
        {
            this.Catalogs.Enqueue(initial);
        }

        public Task<IReadOnlyList<ReviewedReleaseCandidate>> LoadCatalogAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.LoadCalls++;
            return Task.FromResult(this.Catalogs.Dequeue());
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
                ?? throw new AssertionException("Unexpected package preparation.");
        }

        public ValueTask DisposeAsync()
        {
            this.DisposeCalls++;
            return new(this.Disposal);
        }
    }

    private sealed class FakePreparedPackage : IPreparedReleasePackage
    {
        public InstallerPackageOpenInput Package { get; } = CreatePackage();
        public Task Disposal { get; init; } = Task.CompletedTask;
        public TaskCompletionSource DisposeStarted { get; } = NewCompletion();
        public int DisposeCalls { get; private set; }

        public ValueTask DisposeAsync()
        {
            this.DisposeCalls++;
            this.DisposeStarted.TrySetResult();
            return new(this.Disposal);
        }
    }

    private sealed class FakeClient : IInstallerProtocolClient
    {
        public TaskCompletionSource<InstallerProtocolClientException> Fault { get; } = NewCompletion<InstallerProtocolClientException>();
        public TaskCompletionSource HandshakeStarted { get; } = NewCompletion();
        public TaskCompletionSource OpenStarted { get; } = NewCompletion();
        public TaskCompletionSource DisposeStarted { get; } = NewCompletion();
        public Func<string, string, CancellationToken, Task<HandshakeEvent>> Handshake { get; init; } = (_, _, _) => Task.FromResult(CreateHandshake());
        public Func<InstallerPackageOpenInput, CancellationToken, Task<InstallerPackageOpenResult>> Open { get; init; } = (_, _) => Task.FromResult<InstallerPackageOpenResult>(new InstallerPackageOpenSuccess(CreateRelease()));
        public Func<CancellationToken, Task<IReadOnlyList<ProtocolGameCandidate>>> Discover { get; init; } = _ => throw new NotSupportedException();
        public Func<string, CancellationToken, Task<ProtocolGameCandidate>> Validate { get; init; } = (_, _) => throw new NotSupportedException();
        public Task Disposal { get; init; } = Task.CompletedTask;
        public int OpenCalls { get; private set; }
        public int DisposeCalls { get; private set; }

        public Task<InstallerProtocolClientException> SessionFaulted => this.Fault.Task;

        public Task<HandshakeEvent> HandshakeAsync(
            string clientName,
            string clientVersion,
            CancellationToken cancellationToken = default
        )
        {
            Task<HandshakeEvent> result = this.Handshake(clientName, clientVersion, cancellationToken);
            this.HandshakeStarted.TrySetResult();
            return result;
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

        public Task<IReadOnlyList<ProtocolGameCandidate>> DiscoverGamesAsync(CancellationToken cancellationToken = default)
            => this.Discover(cancellationToken);

        public Task<ProtocolGameCandidate> ValidateGameAsync(string canonicalPath, CancellationToken cancellationToken = default)
            => this.Validate(canonicalPath, cancellationToken);

        public Task<InstallerReadOnlyPlanResult> InspectPlanAsync(
            string canonicalGamePath,
            InstallerOperation operation,
            CancellationToken cancellationToken = default
        ) => throw new AssertionException("Release verification must not inspect a plan.");

        public ValueTask DisposeAsync()
        {
            this.DisposeCalls++;
            this.DisposeStarted.TrySetResult();
            return new(this.Disposal);
        }
    }
}
