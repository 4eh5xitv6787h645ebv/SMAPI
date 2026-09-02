using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using FluentAssertions;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Gui.Backend;
using StardewModdingAPI.Installer.Gui.Frontend;
using StardewModdingAPI.Installer.Gui.ViewModels;

namespace StardewModdingAPI.Installer.Gui.Tests;

internal sealed class ReleaseVerificationViewModelTests
{
    [AvaloniaTest]
    public async Task EmptyCatalogStaysTruthfulAndOffersCatalogRetry()
    {
        FakeReleaseService service = new([]);
        await using ReleaseVerificationViewModel viewModel = CreateViewModel(service);

        await viewModel.StartAsync();
        Dispatcher.UIThread.RunJobs();

        viewModel.Heading.Should().Be("No compatible graphical-installer release is available");
        viewModel.Message.Should().Contain("six-file package");
        viewModel.Message.Should().Contain("Nothing was downloaded");
        viewModel.IsEmptyVisible.Should().BeTrue();
        viewModel.IsVerifiedVisible.Should().BeFalse();
        viewModel.Releases.Should().BeEmpty();
        viewModel.RetryCommand.CanExecute(null).Should().BeTrue();
        viewModel.DurableState.Should().Contain("nothing has been installed");
    }

    [AvaloniaTest]
    public async Task BackendSuccessShowsVerifiedReadinessButContinueNeedsARealHandler()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        FakeReleaseService service = new([candidate]) { CompletePreparation = true };
        FakeProtocolClient client = new(success: true, candidate);
        await using ReleaseVerificationViewModel viewModel = CreateViewModel(service, client);
        await viewModel.StartAsync();
        Dispatcher.UIThread.RunJobs();

        viewModel.Heading.Should().Be("Choose an experimental Linux release");
        viewModel.ReleaseDetail.Should().Contain(candidate.Identity.Tag);
        viewModel.ReleaseDetail.Should().Contain("Six advertised files");

        await viewModel.DownloadAndVerifyCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();

        viewModel.Heading.Should().Be("Release verified — ready to review");
        viewModel.IsVerifiedVisible.Should().BeTrue();
        viewModel.Message.Should().Contain("Nothing has been installed yet");
        viewModel.VerifiedIdentityDetail.Should().Contain(candidate.Identity.Tag);
        viewModel.VerifiedIdentityDetail.Should().Contain(new string('a', 40));
        viewModel.ContinueCommand.CanExecute(null).Should().BeFalse("an unwired next screen must not expose a dead enabled action");
        viewModel.IsContinueVisible.Should().BeFalse("an unwired next screen must not expose a dead visible action");

        bool continued = false;
        EventHandler handler = (_, _) => continued = true;
        viewModel.ContinueRequested += handler;
        viewModel.ContinueCommand.CanExecute(null).Should().BeTrue();
        viewModel.IsContinueVisible.Should().BeTrue();
        viewModel.ContinueCommand.Execute(null);
        continued.Should().BeTrue();
        viewModel.ContinueRequested -= handler;
        viewModel.ContinueCommand.CanExecute(null).Should().BeFalse();
        viewModel.IsContinueVisible.Should().BeFalse();
        client.OpenCount.Should().Be(1);
    }

    [AvaloniaTest]
    public async Task TerminalBackendRejectionBlocksRetryAndShowsSafeRestartStep()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        FakeReleaseService service = new([candidate]) { CompletePreparation = true };
        await using ReleaseVerificationViewModel viewModel = CreateViewModel(
            service,
            new FakeProtocolClient(success: false, candidate, terminalRejection: true)
        );
        await viewModel.StartAsync();
        Dispatcher.UIThread.RunJobs();

        await viewModel.DownloadAndVerifyCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();

        viewModel.Heading.Should().Be("The release could not be verified");
        viewModel.Message.Should().Contain("Close and reopen");
        viewModel.IsVerifiedVisible.Should().BeFalse();
        viewModel.IsRetryVisible.Should().BeFalse();
        viewModel.RetryCommand.CanExecute(null).Should().BeFalse();
        viewModel.StatusLiveSetting.Should().Be(AutomationLiveSetting.Off, "the assertive error region is the sole error announcement");
        viewModel.LiveAnnouncement.Should().Be($"{viewModel.Heading}. {viewModel.Message}");
    }

    [AvaloniaTest]
    public async Task ThirdRejectedAttemptShowsRetryLimitWithoutOfferingAnImpossibleAction()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        FakeReleaseService service = new([candidate]) { CompletePreparation = true };
        FakeProtocolClient client = new(success: false, candidate);
        await using ReleaseVerificationViewModel viewModel = CreateViewModel(service, client);
        await viewModel.StartAsync();
        Dispatcher.UIThread.RunJobs();

        await viewModel.DownloadAndVerifyCommand.ExecuteAsync();
        viewModel.RetryCommand.CanExecute(null).Should().BeTrue();
        await viewModel.RetryCommand.ExecuteAsync();
        viewModel.RetryCommand.CanExecute(null).Should().BeTrue();
        await viewModel.RetryCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();

        client.OpenCount.Should().Be(3);
        viewModel.Message.Should().Contain("retry limit has been reached");
        viewModel.Message.Should().Contain("start a new verification session");
        viewModel.Message.Should().NotContain("Try the download");
        viewModel.Message.Should().NotContain("choose another");
        viewModel.IsReleaseSelectorEnabled.Should().BeFalse();
        viewModel.IsRetryVisible.Should().BeFalse();
        viewModel.RetryCommand.CanExecute(null).Should().BeFalse();
    }

    [AvaloniaTest]
    [TestCase(PackageSecurityFailureKind.NetworkUnavailable, ReviewedReleasePreparationStage.ObservingTag, "Couldn’t finish release preparation", "A required release network request became unavailable before preparation finished", "A required network request became unavailable before release preparation finished.")]
    [TestCase(PackageSecurityFailureKind.NetworkUnavailable, ReviewedReleasePreparationStage.RefreshingTag, "Couldn’t finish release preparation", "A required release network request became unavailable before preparation finished", "A required network request became unavailable before release preparation finished.")]
    [TestCase(PackageSecurityFailureKind.NetworkTimeout, ReviewedReleasePreparationStage.ObservingTag, "Release preparation timed out", "A required release network request timed out before preparation finished", "A required network request timed out before release preparation finished.")]
    [TestCase(PackageSecurityFailureKind.NetworkTimeout, ReviewedReleasePreparationStage.RefreshingTag, "Release preparation timed out", "A required release network request timed out before preparation finished", "A required network request timed out before release preparation finished.")]
    [TestCase(PackageSecurityFailureKind.IncompleteDownload, ReviewedReleasePreparationStage.Downloading, "Release file transfer was incomplete", "A release file transfer did not produce one complete expected file", "A release file transfer did not produce one complete expected file.")]
    public async Task PublicTransferFailureShowsExactRetryableEvidenceWithoutPrivateDetails(
        PackageSecurityFailureKind failureKind,
        ReviewedReleasePreparationStage failureStage,
        string expectedHeading,
        string expectedObservedFailure,
        string expectedMessagePrefix
    )
    {
        ReviewedReleaseCandidate candidate = Candidate();
        FakeReleaseService service = new([candidate])
        {
            PreparationFailureKind = failureKind,
            PreparationFailureStage = failureStage
        };
        await using ReleaseVerificationViewModel viewModel = CreateViewModel(service);
        List<ReleaseVerificationFocusTarget> focus = [];
        viewModel.FocusRequested += (_, target) => focus.Add(target);
        await viewModel.StartAsync();
        Dispatcher.UIThread.RunJobs();

        await viewModel.DownloadAndVerifyCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();

        viewModel.Heading.Should().Be(expectedHeading);
        viewModel.Message.Should().Be($"{expectedMessagePrefix} No package from this attempt was published or retained for use, and no game files changed. Check your connection, then choose Try again.");
        viewModel.FailureEvidence.Select(row => row.AccessibleName).Should().Equal(
            $"Observed failure: {expectedObservedFailure}",
            "Installer package availability: No package from this attempt was published or retained for use",
            "Cleanup boundary: The failed attempt settled before this result was shown",
            "Game files: Unchanged",
            "Safe next step: Check your connection, then retry the download"
        );
        viewModel.IsFailureEvidenceVisible.Should().BeTrue();
        viewModel.IsVerifiedVisible.Should().BeFalse();
        viewModel.VerifiedIdentityDetail.Should().BeEmpty();
        viewModel.IsRetryVisible.Should().BeTrue();
        viewModel.RetryCommand.CanExecute(null).Should().BeTrue();
        viewModel.RetryAutomationName.Should().Be("Retry the release download and verification");
        viewModel.IsExitVisible.Should().BeFalse();
        viewModel.StatusLiveSetting.Should().Be(AutomationLiveSetting.Off);
        focus.Should().EndWith(ReleaseVerificationFocusTarget.Status);
        string projection = string.Join('\n', new[]
        {
            viewModel.Heading,
            viewModel.Message,
            viewModel.LiveAnnouncement,
            viewModel.ReleaseDetail,
            viewModel.VerifiedIdentityDetail,
            string.Join('\n', viewModel.FailureEvidence.Select(row => row.AccessibleName))
        });
        projection.Should().NotContain("alice").And.NotContain("SECRET").And.NotContain("download.partial");
    }

    [AvaloniaTest]
    [TestCase(ProtocolPrePlanErrorCode.PackageIntegrityRejected, "Release checksum or package integrity did not match", "Package or checksum integrity did not agree")]
    [TestCase(ProtocolPrePlanErrorCode.PackageMetadataRejected, "Release metadata did not match", "Release or install metadata did not satisfy strict verification")]
    [TestCase(ProtocolPrePlanErrorCode.PackageArchiveRejected, "Release package archive was rejected", "Package archive or verified payload did not satisfy strict verification")]
    public async Task PublicIntegrityFailureShowsExactEvidenceAndFreshDownloadAction(
        ProtocolPrePlanErrorCode rejectionCode,
        string expectedHeading,
        string expectedObservedCheck
    )
    {
        ReviewedReleaseCandidate candidate = Candidate();
        FakeReleaseService service = new([candidate]) { CompletePreparation = true };
        FakeProtocolClient client = new(
            success: false,
            candidate,
            rejectionCode: rejectionCode,
            nextAction: ProtocolNextAction.ReopenVerifiedPackage
        );
        await using ReleaseVerificationViewModel viewModel = CreateViewModel(service, client);
        EventHandler localHandler = (_, _) => { };
        viewModel.LocalPackageFolderRequested += localHandler;
        await viewModel.StartAsync();
        Dispatcher.UIThread.RunJobs();

        await viewModel.DownloadAndVerifyCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();

        viewModel.Heading.Should().Be(expectedHeading);
        viewModel.Message.Should().Be("The selected release did not satisfy strict package verification. It was blocked before installation, the rejected package was not retained for use, and no game files changed. Download and verify the selected release again.");
        viewModel.FailureEvidence.Select(row => row.AccessibleName).Should().Equal(
            $"Observed check: {expectedObservedCheck}",
            "Installation: Not started",
            "Installer package availability: Rejected package was not retained for use",
            "Game files: Unchanged",
            "Safe next step: Download and verify the selected release again"
        );
        viewModel.IsRetryVisible.Should().BeTrue();
        viewModel.RetryCommand.CanExecute(null).Should().BeTrue();
        viewModel.RetryAutomationName.Should().Be("Download and verify the selected release again");
        viewModel.IsLocalPackageActionVisible.Should().BeTrue();
        viewModel.IsExitVisible.Should().BeFalse();
        viewModel.IsVerifiedVisible.Should().BeFalse();
        viewModel.VerifiedIdentityDetail.Should().BeEmpty();
        viewModel.LiveAnnouncement.Should().Be($"{viewModel.Heading}. {viewModel.Message}");
        viewModel.LiveAnnouncement.Should().NotContain("alice").And.NotContain("SECRET");
        viewModel.LocalPackageFolderRequested -= localHandler;
    }

    [AvaloniaTest]
    [TestCase(ProtocolPrePlanErrorCode.PackageProvenanceRejected, "GitHub provenance was not accepted")]
    [TestCase(ProtocolPrePlanErrorCode.PackageReleaseIdentityRejected, "Release identity did not match")]
    public async Task PublicProvenanceOrIdentityFailureRequiresExitAndOffersNoRetryOrLocalBypass(
        ProtocolPrePlanErrorCode rejectionCode,
        string expectedHeading
    )
    {
        ReviewedReleaseCandidate candidate = Candidate();
        FakeReleaseService service = new([candidate]) { CompletePreparation = true };
        FakeProtocolClient client = new(
            success: false,
            candidate,
            rejectionCode: rejectionCode,
            nextAction: ProtocolNextAction.ReopenVerifiedPackage
        );
        await using ReleaseVerificationViewModel viewModel = CreateViewModel(service, client);
        EventHandler localHandler = (_, _) => { };
        viewModel.LocalPackageFolderRequested += localHandler;
        List<ReleaseVerificationFocusTarget> focus = [];
        viewModel.FocusRequested += (_, target) => focus.Add(target);
        await viewModel.StartAsync();
        Dispatcher.UIThread.RunJobs();

        await viewModel.DownloadAndVerifyCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();

        viewModel.Heading.Should().Be(expectedHeading);
        viewModel.Message.Should().Be("The selected release did not satisfy strict release-identity or GitHub provenance verification. It was blocked before installation, and no game files changed. Close and reopen the installer to start a fresh verification session.");
        IReadOnlyList<string> evidence = viewModel.FailureEvidence.Select(row => row.AccessibleName).ToArray();
        if (rejectionCode == ProtocolPrePlanErrorCode.PackageProvenanceRejected)
        {
            evidence.Should().Equal(
                "Package integrity: Passed before GitHub provenance verification",
                "GitHub provenance: Evidence did not satisfy strict verification",
                "Package extraction: Not started",
                "Game files: Unchanged",
                "Safe next step: Close and reopen the installer to start a fresh verification session"
            );
        }
        else
        {
            evidence.Should().Equal(
                "Observed check: Selected release identity did not satisfy strict verification",
                "Package extraction: Not started",
                "Installation: Blocked",
                "Game files: Unchanged",
                "Safe next step: Close and reopen the installer to start a fresh verification session"
            );
        }
        viewModel.IsExitVisible.Should().BeTrue();
        viewModel.IsRetryVisible.Should().BeFalse();
        viewModel.RetryCommand.CanExecute(null).Should().BeFalse();
        viewModel.IsLocalPackageActionVisible.Should().BeFalse();
        viewModel.IsReleaseSelectorEnabled.Should().BeFalse();
        viewModel.IsVerifiedVisible.Should().BeFalse();
        viewModel.VerifiedIdentityDetail.Should().BeEmpty();
        viewModel.StatusLiveSetting.Should().Be(AutomationLiveSetting.Off);
        focus.Should().EndWith(ReleaseVerificationFocusTarget.Status);
        viewModel.LiveAnnouncement.Should().Be($"{viewModel.Heading}. {viewModel.Message}");
        viewModel.LiveAnnouncement.Should().NotContain("alice").And.NotContain("SECRET");
        viewModel.LocalPackageFolderRequested -= localHandler;
    }

    [AvaloniaTest]
    public async Task PublicTagIdentityChangeBeforePackageOpenStillRequiresExitWithoutInventedEvidence()
    {
        ReviewedReleaseCandidate candidate = Candidate();
        FakeReleaseService service = new([candidate])
        {
            PreparationFailureKind = PackageSecurityFailureKind.ReleaseIdentityRejected
        };
        await using ReleaseVerificationViewModel viewModel = CreateViewModel(service);
        EventHandler localHandler = (_, _) => { };
        viewModel.LocalPackageFolderRequested += localHandler;
        await viewModel.StartAsync();
        Dispatcher.UIThread.RunJobs();

        await viewModel.DownloadAndVerifyCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();

        viewModel.Heading.Should().Be("Release identity changed or did not match");
        viewModel.FailureEvidence.Select(row => row.AccessibleName).Should().Equal(
            "Observed check: Selected release identity did not satisfy strict verification",
            "Package extraction: Not started",
            "Installation: Blocked",
            "Game files: Unchanged",
            "Safe next step: Close and reopen the installer to start a fresh verification session"
        );
        viewModel.IsExitVisible.Should().BeTrue();
        viewModel.IsRetryVisible.Should().BeFalse();
        viewModel.RetryCommand.CanExecute(null).Should().BeFalse();
        viewModel.IsLocalPackageActionVisible.Should().BeFalse();
        viewModel.IsReleaseSelectorEnabled.Should().BeFalse();
        viewModel.IsVerifiedVisible.Should().BeFalse();
        viewModel.VerifiedIdentityDetail.Should().BeEmpty();
        string projection = $"{viewModel.Heading}\n{viewModel.Message}\n{viewModel.LiveAnnouncement}\n{string.Join('\n', viewModel.FailureEvidence.Select(row => row.AccessibleName))}";
        projection.Should().NotContain("alice").And.NotContain("SECRET").And.NotContain("tag-observation");
        viewModel.LocalPackageFolderRequested -= localHandler;
    }

    [AvaloniaTest]
    [TestCase(ProtocolPrePlanErrorCode.PackageIntegrityRejected, "Observed check: Package or checksum integrity did not agree")]
    [TestCase(ProtocolPrePlanErrorCode.PackageReleaseIdentityRejected, "Observed check: Selected release identity did not satisfy strict verification")]
    public async Task LocalTypedFailureRequiresFolderReselectionAndNeverExposesThePath(
        ProtocolPrePlanErrorCode rejectionCode,
        string expectedFirstEvidence
    )
    {
        const string privatePath = "/home/alice/private/local-release?token=SECRET";
        ReviewedReleaseCandidate candidate = Candidate();
        FakeReleaseService service = new([candidate]);
        FakeProtocolClient client = new(
            success: false,
            candidate,
            rejectionCode: rejectionCode,
            nextAction: ProtocolNextAction.ReopenVerifiedPackage
        );
        FakeLocalReleaseService local = new(candidate);
        await using ReleaseVerificationViewModel viewModel = new(new ReleaseVerificationController(
            service,
            () => client,
            local
        ));
        EventHandler localHandler = (_, _) => { };
        viewModel.LocalPackageFolderRequested += localHandler;
        await viewModel.StartAsync();
        Dispatcher.UIThread.RunJobs();

        await viewModel.ApplyLocalPackageFolderAsync(privatePath);
        Dispatcher.UIThread.RunJobs();

        viewModel.FailureEvidence.Select(row => row.AccessibleName).Should().StartWith(expectedFirstEvidence);
        viewModel.FailureEvidence.Select(row => row.AccessibleName).Should().Contain(
            "Safe next step: Replace the six files with a fresh complete copy, then choose the folder again"
        );
        viewModel.Message.Should().Contain("Replace the six files with a fresh complete copy, then choose the folder again.");
        viewModel.IsRetryVisible.Should().BeFalse();
        viewModel.RetryCommand.CanExecute(null).Should().BeFalse();
        viewModel.IsLocalPackageActionVisible.Should().BeTrue();
        viewModel.IsReleaseSelectorEnabled.Should().BeTrue();
        viewModel.IsExitVisible.Should().BeFalse();
        viewModel.IsVerifiedVisible.Should().BeFalse();
        viewModel.VerifiedIdentityDetail.Should().BeEmpty();
        local.Paths.Should().Equal(privatePath);
        string projection = string.Join('\n', new[]
        {
            viewModel.Heading,
            viewModel.Message,
            viewModel.LiveAnnouncement,
            viewModel.ReleaseDetail,
            string.Join('\n', viewModel.FailureEvidence.Select(row => row.AccessibleName))
        });
        projection.Should().NotContain("alice").And.NotContain("SECRET").And.NotContain(privatePath);
        viewModel.LocalPackageFolderRequested -= localHandler;
    }

    [AvaloniaTest]
    public async Task DownloadAnnouncementsUseAssetAndTenPercentMilestonesInsteadOfEveryByte()
    {
        ReviewedReleaseCandidate candidate = Candidate(size: 100);
        FakeReleaseService service = new([candidate]);
        await using ReleaseVerificationViewModel viewModel = CreateViewModel(service, new FakeProtocolClient(true, candidate));
        await viewModel.StartAsync();
        Dispatcher.UIThread.RunJobs();
        List<string> announcements = [];
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(viewModel.LiveAnnouncement))
                announcements.Add(viewModel.LiveAnnouncement);
        };

        Task attempt = viewModel.DownloadAndVerifyCommand.ExecuteAsync();
        await service.PreparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        service.Report(new(ReviewedReleasePreparationStage.ObservingTag, null, 0, 0, 0, 0));
        service.Report(new(ReviewedReleasePreparationStage.Downloading, ReviewedReleaseAssetKind.InstallerPackage, 0, 6, 1, 600));
        service.Report(new(ReviewedReleasePreparationStage.Downloading, ReviewedReleaseAssetKind.InstallerPackage, 0, 6, 5, 600));
        service.Report(new(ReviewedReleasePreparationStage.Downloading, ReviewedReleaseAssetKind.InstallerPackage, 0, 6, 59, 600));
        service.Report(new(ReviewedReleasePreparationStage.Downloading, ReviewedReleaseAssetKind.InstallerPackage, 0, 6, 60, 600));
        service.Report(new(ReviewedReleasePreparationStage.Downloading, ReviewedReleaseAssetKind.InstallerPackage, 1, 6, 100, 600));
        service.Report(new(ReviewedReleasePreparationStage.Downloading, ReviewedReleaseAssetKind.InstallManifest, 1, 6, 101, 600));

        announcements.Count(value => value.Contains("file 1", StringComparison.OrdinalIgnoreCase)).Should().Be(2);
        announcements.Should().Contain(value => value == "Downloading file 1 of 6, overall 0 percent.");
        announcements.Should().Contain(value => value == "Downloading file 1 of 6, overall 10 percent.");
        announcements.Should().Contain(value => value == "Downloading file 2 of 6, overall 10 percent.");
        viewModel.ProgressValue.Should().BeApproximately(16.83, 0.02);
        viewModel.ProgressText.Should().Be("File 2 of 6: installation manifest — Overall: 101 B of 600 B (16%)");

        List<string> headings = [];
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(viewModel.Heading))
                headings.Add(viewModel.Heading);
        };
        await viewModel.CancelCommand.ExecuteAsync();
        await attempt;
        Dispatcher.UIThread.RunJobs();
        headings.Should().Contain("Cleaning up safely…");
        headings.Should().NotContain("Cancelling safely…");
        viewModel.Heading.Should().Be("Download cancelled");
        viewModel.RetryCommand.CanExecute(null).Should().BeTrue();
    }

    private static ReleaseVerificationViewModel CreateViewModel(
        FakeReleaseService service,
        FakeProtocolClient? client = null
    )
    {
        ReviewedReleaseCandidate fallback = service.Catalog.FirstOrDefault() ?? Candidate();
        return new(new ReleaseVerificationController(service, () => client ?? new FakeProtocolClient(false, fallback)));
    }

    internal static ReviewedReleaseCandidate Candidate(long size = 100)
    {
        ForkReleaseIdentity identity = ForkReleaseIdentity.Parse(
            "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2"
        );
        object[] assets = Enum.GetValues<ReviewedReleaseAssetKind>()
            .Select(kind => new
            {
                name = ReviewedGitHubReleaseUris.GetAssetName(identity, kind),
                size,
                state = "uploaded",
                browser_download_url = ReviewedGitHubReleaseUris.GetAssetUri(identity, kind).AbsoluteUri
            })
            .Cast<object>()
            .ToArray();
        byte[] document = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new
            {
                tag_name = identity.Tag,
                draft = false,
                prerelease = true,
                assets
            }
        });
        return ReviewedGitHubReleaseCatalog.Parse(document).Single();
    }

    internal sealed class FakeReleaseService(IReadOnlyList<ReviewedReleaseCandidate> catalog) : IReviewedReleaseService
    {
        private IProgress<ReviewedReleasePreparationProgress>? Progress;

        public IReadOnlyList<ReviewedReleaseCandidate> Catalog { get; } = catalog;

        public bool CompletePreparation { get; init; }

        public PackageSecurityFailureKind? PreparationFailureKind { get; init; }

        public ReviewedReleasePreparationStage PreparationFailureStage { get; init; } = ReviewedReleasePreparationStage.Downloading;

        public TaskCompletionSource PreparationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<ReviewedReleaseCandidate>> LoadCatalogAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(this.Catalog);
        }

        public async Task<IPreparedReleasePackage> PrepareAsync(
            ReviewedReleaseCandidate candidate,
            IProgress<ReviewedReleasePreparationProgress>? progress = null,
            CancellationToken cancellationToken = default
        )
        {
            this.Progress = progress;
            this.PreparationStarted.TrySetResult();
            if (this.PreparationFailureKind is { } failureKind)
            {
                progress?.Report(new(ReviewedReleasePreparationStage.ObservingTag, null, 0, 0, 0, 0));
                if (this.PreparationFailureStage != ReviewedReleasePreparationStage.ObservingTag)
                {
                    long total = candidate.Assets.Sum(asset => asset.SizeBytes);
                    if (this.PreparationFailureStage == ReviewedReleasePreparationStage.RefreshingTag)
                    {
                        long transferred = 0;
                        for (int index = 0; index < candidate.Assets.Length; index++)
                        {
                            transferred += candidate.Assets[index].SizeBytes;
                            progress?.Report(new(
                                ReviewedReleasePreparationStage.Downloading,
                                candidate.Assets[index].Kind,
                                index + 1,
                                candidate.Assets.Length,
                                transferred,
                                total
                            ));
                        }
                    }
                    else
                    {
                        progress?.Report(new(
                            ReviewedReleasePreparationStage.Downloading,
                            ReviewedReleaseAssetKind.InstallerPackage,
                            0,
                            candidate.Assets.Length,
                            7,
                            total
                        ));
                    }
                }
                if (this.PreparationFailureStage == ReviewedReleasePreparationStage.RefreshingTag)
                    progress?.Report(new(ReviewedReleasePreparationStage.RefreshingTag, null, 0, 0, 0, 0));
                throw new PackageSecurityException(
                    failureKind,
                    "private /home/alice/download.partial?token=SECRET"
                );
            }
            if (this.CompletePreparation)
            {
                progress?.Report(new(ReviewedReleasePreparationStage.ObservingTag, null, 0, 0, 0, 0));
                long total = candidate.Assets.Sum(asset => asset.SizeBytes);
                long transferred = 0;
                for (int index = 0; index < candidate.Assets.Length; index++)
                {
                    transferred += candidate.Assets[index].SizeBytes;
                    progress?.Report(new(
                        ReviewedReleasePreparationStage.Downloading,
                        candidate.Assets[index].Kind,
                        index + 1,
                        candidate.Assets.Length,
                        transferred,
                        total
                    ));
                }
                progress?.Report(new(ReviewedReleasePreparationStage.RefreshingTag, null, 0, 0, 0, 0));
                return new FakePreparedPackage(candidate);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new AssertionException("The blocked fake preparation should end through cancellation.");
        }

        public void Report(ReviewedReleasePreparationProgress value)
        {
            this.Progress.Should().NotBeNull();
            this.Progress!.Report(value);
            Dispatcher.UIThread.RunJobs();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakePreparedPackage : IPreparedReleasePackage
    {
        public InstallerPackageOpenInput Package { get; }

        public FakePreparedPackage(ReviewedReleaseCandidate candidate)
        {
            this.Package = new(
                candidate.Identity.Tag,
                new string('a', 40),
                "/proc/1/fd/1/package",
                "/proc/1/fd/1/checksums",
                "/proc/1/fd/1/metadata",
                "/proc/1/fd/1/manifest",
                "/proc/1/fd/1/bundle",
                "/proc/1/fd/1/bundle-checksum"
            );
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeLocalReleaseService(ReviewedReleaseCandidate candidate) : ILocalReleasePackageService
    {
        public List<string> Paths { get; } = [];

        public Task<IPreparedReleasePackage> PrepareAsync(
            string selectedDirectory,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.Paths.Add(selectedDirectory);
            return Task.FromResult<IPreparedReleasePackage>(new FakePreparedPackage(candidate));
        }
    }

    internal sealed class FakeProtocolClient(
        bool success,
        ReviewedReleaseCandidate candidate,
        bool terminalRejection = false,
        ProtocolPrePlanErrorCode rejectionCode = ProtocolPrePlanErrorCode.PackageRejected,
        ProtocolNextAction? nextAction = null
    ) : IInstallerProtocolClient
    {
        private readonly TaskCompletionSource<InstallerProtocolClientException> Fault = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int OpenCount { get; private set; }

        public Task<InstallerProtocolClientException> SessionFaulted => this.Fault.Task;

        public Task<HandshakeEvent> HandshakeAsync(string clientName, string clientVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HandshakeEvent(
                ProtocolSessionId.Parse("11111111111111111111111111111111"),
                "1",
                ["verified-local-package"]
            ));
        }

        public Task<InstallerPackageOpenResult> OpenPackageAsync(
            InstallerPackageOpenInput package,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.OpenCount++;
            InstallerPackageOpenResult result = success
                ? new InstallerPackageOpenSuccess(new ProtocolReleaseIdentity(
                    ForkReleaseIdentity.Repository,
                    candidate.Identity.Tag,
                    candidate.Identity.EmbeddedVersion,
                    candidate.Identity.PackageAssetName,
                    new string('a', 40),
                    new string('b', 40),
                    new string('c', 64),
                    100,
                    "workflow",
                    "Release",
                    "linux-x64"
                ))
                : new InstallerPackageOpenRejection(
                    terminalRejection ? ProtocolPrePlanErrorCode.UnexpectedFailure : rejectionCode,
                    terminalRejection
                        ? ProtocolNextAction.StartNewSession
                        : nextAction ?? ProtocolNextAction.ReopenVerifiedPackage,
                    "Private /home/alice/package?token=SECRET",
                    terminalRejection
                );
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<ProtocolGameCandidate>> DiscoverGamesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProtocolGameCandidate> ValidateGameAsync(string canonicalPath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<InstallerReadOnlyPlanResult> InspectPlanAsync(
            string canonicalGamePath,
            InstallerOperation operation,
            CancellationToken cancellationToken = default
        ) => throw new AssertionException("Release verification must not inspect a plan.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
