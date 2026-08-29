using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Security;

namespace StardewModdingAPI.Installer.Core.Tests.Packages;

[Platform("Linux")]
[SupportedOSPlatform("linux")]
internal sealed class ReviewedReleaseAssetAcquirerTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    [Test]
    public void PublicApi_IsHighLevelAndNonForgeable()
    {
        MethodInfo[] methods = typeof(ReviewedReleaseAssetAcquirer).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);

        methods.Should().ContainSingle().Which.Name.Should().Be(nameof(ReviewedReleaseAssetAcquirer.AcquireAsync));
        typeof(ReviewedReleaseAssetLease).GetConstructors(BindingFlags.Public | BindingFlags.Instance).Should().BeEmpty();
        typeof(ReviewedReleaseAssetLease).IsSealed.Should().BeTrue();
        typeof(ReviewedReleaseAssetLease).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Should().Equal(nameof(ReviewedReleaseAssetLease.ReleaseTag));
        typeof(ReviewedReleaseAssetLease).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Should().OnlyContain(property => property.SetMethod == null);
        methods.Single().GetParameters().Select(parameter => parameter.ParameterType).Should().Equal(
            typeof(ReviewedReleaseCandidate),
            typeof(IProgress<ReviewedReleaseAcquisitionProgress>),
            typeof(CancellationToken)
        );
        typeof(IReviewedReleaseAssetTransport).IsPublic.Should().BeFalse();
        typeof(AnchoredDownloadTarget).IsPublic.Should().BeFalse();
        typeof(ReviewedReleaseProtocolAssetPaths).IsPublic.Should().BeTrue();
        typeof(ReviewedReleaseProtocolAssetPaths).GetConstructors(BindingFlags.Public | BindingFlags.Instance).Should().BeEmpty();
        typeof(ReviewedReleaseProtocolAssetPaths).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => (property.Name, property.PropertyType, property.SetMethod))
            .Should().Equal(
                (nameof(ReviewedReleaseProtocolAssetPaths.ReleaseTag), typeof(string), null),
                (nameof(ReviewedReleaseProtocolAssetPaths.SourceCommit), typeof(string), null),
                (nameof(ReviewedReleaseProtocolAssetPaths.InstallerPackagePath), typeof(string), null),
                (nameof(ReviewedReleaseProtocolAssetPaths.InstallManifestPath), typeof(string), null),
                (nameof(ReviewedReleaseProtocolAssetPaths.ChecksumsPath), typeof(string), null),
                (nameof(ReviewedReleaseProtocolAssetPaths.BuildMetadataPath), typeof(string), null),
                (nameof(ReviewedReleaseProtocolAssetPaths.AttestationBundlePath), typeof(string), null),
                (nameof(ReviewedReleaseProtocolAssetPaths.AttestationBundleChecksumPath), typeof(string), null),
                (nameof(ReviewedReleaseProtocolAssetPaths.WorkspaceDeviceMajor), typeof(uint), null),
                (nameof(ReviewedReleaseProtocolAssetPaths.WorkspaceDeviceMinor), typeof(uint), null),
                (nameof(ReviewedReleaseProtocolAssetPaths.WorkspaceInode), typeof(ulong), null),
                (nameof(ReviewedReleaseProtocolAssetPaths.WorkspaceChangeSeconds), typeof(long), null),
                (nameof(ReviewedReleaseProtocolAssetPaths.WorkspaceChangeNanoseconds), typeof(uint), null)
            );
        typeof(ReviewedReleaseAssetLease).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .Should().Equal(nameof(ReviewedReleaseAssetLease.Bind), nameof(ReviewedReleaseAssetLease.DisposeAsync));
    }

    [Test]
    public async Task AcquireAsync_DownloadsExactSixSequentiallyWithFixedBoundsAndSynchronousProgress()
    {
        ReviewedReleaseCandidate candidate = Candidate(8);
        RecordingTransport transport = new();
        List<ReviewedReleaseAcquisitionProgress> progress = [];

        await using ReviewedReleaseAssetLease lease = await ReviewedReleaseAssetAcquirer.AcquireAsync(
            candidate,
            transport,
            new InlineProgress<ReviewedReleaseAcquisitionProgress>(progress.Add)
        );

        transport.Kinds.Should().Equal(Enum.GetValues<ReviewedReleaseAssetKind>());
        transport.MaximumBytes.Should().Equal(candidate.Assets.Select(asset => asset.SizeBytes));
        transport.ConcurrentMaximum.Should().Be(1);
        transport.Disposed.Should().BeTrue();
        progress.Should().HaveCount(12);
        for (int index = 0; index < 6; index++)
        {
            progress[index * 2].Should().Be(new ReviewedReleaseAcquisitionProgress(
                (ReviewedReleaseAssetKind)index,
                index,
                6,
                8,
                8,
                index * 8,
                48
            ));
            progress[(index * 2) + 1].Should().Be(new ReviewedReleaseAcquisitionProgress(
                (ReviewedReleaseAssetKind)index,
                index + 1,
                6,
                8,
                8,
                (index + 1) * 8,
                48
            ));
        }
        progress.Should().OnlyContain(value => value.TotalAssets == 6 && value.TotalBytes == 48);
    }

    [Test]
    public async Task AcquireAsync_LeaseBindsOnlyExactCandidateAndRetainsSixPrivateProcFiles()
    {
        ReviewedReleaseCandidate candidate = Candidate(7);
        ReviewedReleaseCandidate other = Candidate(9, alpha: 3);
        ReviewedReleaseCandidate equalButSeparate = Candidate(7);
        ReviewedReleaseAssetLease lease = await ReviewedReleaseAssetAcquirer.AcquireAsync(candidate, new RecordingTransport());
        ReviewedGitHubResolvedTag resolved = new(candidate, Commit);

        ReviewedReleaseProtocolAssetPaths paths = lease.Bind(resolved);
        string[] values = AssetPaths(paths);

        values.Should().HaveCount(6).And.OnlyContain(path => path.StartsWith($"/proc/{Environment.ProcessId}/fd/", StringComparison.Ordinal));
        paths.ReleaseTag.Should().Be(candidate.Identity.Tag);
        paths.SourceCommit.Should().Be(Commit);
        values.Select(Path.GetFileName).Should().Equal(candidate.Assets.Select(asset => asset.Name));
        string namedDirectory = ResolveProcTarget(Path.GetDirectoryName(values[0])!);
        using LinuxAnchoredFileSystem retainedFiles = new(namedDirectory);
        LinuxFileIdentity retainedWorkspaceIdentity = retainedFiles.GetCurrentRootIdentity();
        paths.WorkspaceDeviceMajor.Should().Be(retainedWorkspaceIdentity.DeviceMajor);
        paths.WorkspaceDeviceMinor.Should().Be(retainedWorkspaceIdentity.DeviceMinor);
        paths.WorkspaceInode.Should().Be(retainedWorkspaceIdentity.Inode);
        paths.WorkspaceChangeSeconds.Should().Be(retainedWorkspaceIdentity.ChangeSeconds);
        paths.WorkspaceChangeNanoseconds.Should().Be(retainedWorkspaceIdentity.ChangeNanoseconds);
        foreach (string path in values)
        {
            File.ReadAllBytes(path).Should().HaveCount(7);
            File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            retainedFiles.Stat(Path.GetFileName(path))!.LinkCount.Should().Be(1);
        }
        FluentActions.Invoking(() => lease.Bind(new ReviewedGitHubResolvedTag(other, Commit)))
            .Should().Throw<PackageSecurityException>();
        FluentActions.Invoking(() => lease.Bind(new ReviewedGitHubResolvedTag(equalButSeparate, Commit)))
            .Should().Throw<PackageSecurityException>();

        ValueTask first = lease.DisposeAsync();
        ValueTask second = lease.DisposeAsync();
        await Task.WhenAll(first.AsTask(), second.AsTask());
        values.Should().OnlyContain(path => !File.Exists(path));
        Directory.Exists(namedDirectory).Should().BeFalse();
    }

    [Test]
    public async Task AcquireAsync_ResultLengthMismatchFailsStopsSequenceAndCleansPublishedFile()
    {
        ReviewedReleaseCandidate candidate = Candidate(11);
        RecordingTransport transport = new() { ResultLengthDelta = -1 };
        string? namedWorkspace = null;

        Func<Task> action = async () => await ReviewedReleaseAssetAcquirer.AcquireAsync(
            candidate,
            transport,
            workspaceFactory: () =>
            {
                ReviewedReleaseAssetWorkspace workspace = ReviewedReleaseAssetWorkspace.Create();
                namedWorkspace = ResolveProcTarget(workspace.ProcPath);
                return workspace;
            }
        );

        await action.Should().ThrowAsync<PackageSecurityException>().WithMessage("*length differs*");
        transport.Kinds.Should().ContainSingle().Which.Should().Be(ReviewedReleaseAssetKind.InstallerPackage);
        Directory.Exists(namedWorkspace).Should().BeFalse();
        transport.Disposed.Should().BeTrue();
    }

    [Test]
    public async Task AnchoredDownloader_ShortCatalogBodyIsRejectedBeforePublicationAndLeavesNoTemporary()
    {
        ReviewedReleaseAsset asset = Candidate(12).GetAsset(ReviewedReleaseAssetKind.InstallerPackage);
        ReviewedReleaseAssetWorkspace workspace = ReviewedReleaseAssetWorkspace.Create();
        string namedWorkspace = ResolveProcTarget(workspace.ProcPath);
        AnchoredDownloadTarget target = workspace.CreateTarget(asset);
        using BoundedHttpDownloader downloader = new(
            new ReviewedGitHubReleaseAssetPolicy(),
            new FixedResponseHandler(new ByteArrayContent(new byte[11]))
        );

        Func<Task> action = () => downloader.DownloadAsync(
            asset.DownloadUri,
            target,
            new DownloadLimits(asset.SizeBytes, TimeSpan.FromSeconds(5), 0)
        );

        await action.Should().ThrowAsync<PackageSecurityException>().WithMessage("*catalog advertisement*");
        Directory.EnumerateFileSystemEntries(workspace.ProcPath).Should().BeEmpty();
        await workspace.DisposeAsync();
        Directory.Exists(namedWorkspace).Should().BeFalse();
    }

    [Test]
    public async Task AnchoredDownloader_UnknownLengthShortBodyIsRejectedBeforePublication()
    {
        ReviewedReleaseAsset asset = Candidate(12).GetAsset(ReviewedReleaseAssetKind.InstallerPackage);
        ReviewedReleaseAssetWorkspace workspace = ReviewedReleaseAssetWorkspace.Create();
        string namedWorkspace = ResolveProcTarget(workspace.ProcPath);
        AnchoredDownloadTarget target = workspace.CreateTarget(asset);
        using BoundedHttpDownloader downloader = new(
            new ReviewedGitHubReleaseAssetPolicy(),
            new FixedResponseHandler(new UnknownLengthContent(new byte[11]))
        );

        Func<Task> action = () => downloader.DownloadAsync(
            asset.DownloadUri,
            target,
            new DownloadLimits(asset.SizeBytes, TimeSpan.FromSeconds(5), 0)
        );

        await action.Should().ThrowAsync<PackageSecurityException>().WithMessage("*catalog advertisement*");
        Directory.EnumerateFileSystemEntries(workspace.ProcPath).Should().BeEmpty();
        await workspace.DisposeAsync();
        Directory.Exists(namedWorkspace).Should().BeFalse();
    }

    [Test]
    public async Task AnchoredDownloader_MidBodyCancellationRemovesTemporaryAndNeverPublishes()
    {
        ReviewedReleaseAsset asset = Candidate(12).GetAsset(ReviewedReleaseAssetKind.InstallerPackage);
        ReviewedReleaseAssetWorkspace workspace = ReviewedReleaseAssetWorkspace.Create();
        string namedWorkspace = ResolveProcTarget(workspace.ProcPath);
        AnchoredDownloadTarget target = workspace.CreateTarget(asset);
        using CancellationTokenSource cancellation = new();
        using BoundedHttpDownloader downloader = new(
            new ReviewedGitHubReleaseAssetPolicy(),
            new FixedResponseHandler(new StreamContent(new CancelAfterFirstReadStream(new byte[12], cancellation)))
        );

        Func<Task> action = () => downloader.DownloadAsync(
            asset.DownloadUri,
            target,
            new DownloadLimits(asset.SizeBytes, TimeSpan.FromSeconds(5), 0),
            cancellationToken: cancellation.Token
        );

        await action.Should().ThrowAsync<OperationCanceledException>();
        Directory.EnumerateFileSystemEntries(workspace.ProcPath).Should().BeEmpty();
        await workspace.DisposeAsync();
        Directory.Exists(namedWorkspace).Should().BeFalse();
    }

    [Test]
    public async Task AcquireAsync_CancellationStopsBeforeNextAssetAndCleansWorkspace()
    {
        ReviewedReleaseCandidate candidate = Candidate(5);
        using CancellationTokenSource cancellation = new();
        RecordingTransport transport = new() { CancelAfterPublication = cancellation };
        string? namedWorkspace = null;

        Func<Task> action = async () => await ReviewedReleaseAssetAcquirer.AcquireAsync(
            candidate,
            transport,
            cancellationToken: cancellation.Token,
            workspaceFactory: () =>
            {
                ReviewedReleaseAssetWorkspace workspace = ReviewedReleaseAssetWorkspace.Create();
                namedWorkspace = ResolveProcTarget(workspace.ProcPath);
                return workspace;
            }
        );

        await action.Should().ThrowAsync<OperationCanceledException>();
        transport.Kinds.Should().ContainSingle();
        Directory.Exists(namedWorkspace).Should().BeFalse();
    }

    [Test]
    public async Task AcquireAsync_CancellationOnFinalPublicationCleansAllSixInsteadOfReturningLease()
    {
        ReviewedReleaseCandidate candidate = Candidate(5);
        using CancellationTokenSource cancellation = new();
        RecordingTransport transport = new() { CancelAfterPublication = cancellation, CancelOnCall = 6, IgnoreCancellationAfterPublication = true };
        string? namedWorkspace = null;

        Func<Task> action = async () => await ReviewedReleaseAssetAcquirer.AcquireAsync(
            candidate,
            transport,
            cancellationToken: cancellation.Token,
            workspaceFactory: () =>
            {
                ReviewedReleaseAssetWorkspace workspace = ReviewedReleaseAssetWorkspace.Create();
                namedWorkspace = ResolveProcTarget(workspace.ProcPath);
                return workspace;
            }
        );

        await action.Should().ThrowAsync<OperationCanceledException>();
        transport.Kinds.Should().HaveCount(6);
        Directory.Exists(namedWorkspace).Should().BeFalse();
    }

    [Test]
    public async Task AcquireAsync_PreCanceledDisposesTransportBeforeWorkspaceOrDownload()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        RecordingTransport transport = new();
        bool workspaceCalled = false;

        Func<Task> action = async () => await ReviewedReleaseAssetAcquirer.AcquireAsync(
            Candidate(4),
            transport,
            cancellationToken: cancellation.Token,
            workspaceFactory: () =>
            {
                workspaceCalled = true;
                return ReviewedReleaseAssetWorkspace.Create();
            }
        );

        await action.Should().ThrowAsync<OperationCanceledException>();
        workspaceCalled.Should().BeFalse();
        transport.Kinds.Should().BeEmpty();
        transport.Disposed.Should().BeTrue();
    }

    [Test]
    public async Task LeaseDisposal_LeavesReplacedLeafButRemovesOtherExactOwnedFiles()
    {
        ReviewedReleaseCandidate candidate = Candidate(6);
        ReviewedReleaseAssetLease lease = await ReviewedReleaseAssetAcquirer.AcquireAsync(candidate, new RecordingTransport());
        ReviewedReleaseProtocolAssetPaths paths = lease.Bind(new ReviewedGitHubResolvedTag(candidate, Commit));
        string[] procPaths = AssetPaths(paths);
        string namedDirectory = ResolveProcTarget(Path.GetDirectoryName(procPaths[0])!);
        string replaced = Path.Combine(namedDirectory, Path.GetFileName(procPaths[0]));
        File.Delete(replaced);
        File.WriteAllText(replaced, "replacement");
        File.SetUnixFileMode(replaced, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        await lease.DisposeAsync();

        File.ReadAllText(replaced).Should().Be("replacement");
        procPaths.Skip(1).Should().OnlyContain(path => !File.Exists(path));
        Directory.EnumerateFileSystemEntries(namedDirectory).Should().Equal(replaced);
        Directory.Delete(namedDirectory, true);
    }

    [Test]
    public async Task Bind_RejectsExtraWorkspaceEntryAndCleanupLeavesItUntouched()
    {
        ReviewedReleaseCandidate candidate = Candidate(6);
        ReviewedReleaseAssetLease lease = await ReviewedReleaseAssetAcquirer.AcquireAsync(candidate, new RecordingTransport());
        ReviewedGitHubResolvedTag resolved = new(candidate, Commit);
        ReviewedReleaseProtocolAssetPaths initial = lease.Bind(resolved);
        string[] procPaths = AssetPaths(initial);
        string namedDirectory = ResolveProcTarget(Path.GetDirectoryName(procPaths[0])!);
        string extra = Path.Combine(namedDirectory, "unrelated-extra");
        File.WriteAllText(extra, "keep");

        FluentActions.Invoking(() => lease.Bind(resolved))
            .Should().Throw<PackageSecurityException>().WithMessage("*exactly six assets*");
        await lease.DisposeAsync();

        File.ReadAllText(extra).Should().Be("keep");
        procPaths.Should().OnlyContain(path => !File.Exists(path));
        Directory.EnumerateFileSystemEntries(namedDirectory).Should().Equal(extra);
        Directory.Delete(namedDirectory, true);
    }

    [Test]
    public async Task Target_RejectsSymlinkHardlinkFifoDirectoryAndWrongOwnerWithoutTouchingSentinel()
    {
        string outsideRoot = Path.Combine(Path.GetTempPath(), $"smapi-target-sentinel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideRoot);
        string sentinel = Path.Combine(outsideRoot, "sentinel");
        File.WriteAllText(sentinel, "keep");

        foreach (string kind in new[] { "symlink", "hardlink", "fifo", "directory" })
        {
            ReviewedReleaseAsset asset = Candidate(3).GetAsset(ReviewedReleaseAssetKind.Checksums);
            ReviewedReleaseAssetWorkspace workspace = ReviewedReleaseAssetWorkspace.Create();
            string named = ResolveProcTarget(workspace.ProcPath);
            string leaf = Path.Combine(named, asset.Name);
            switch (kind)
            {
                case "symlink": File.CreateSymbolicLink(leaf, sentinel); break;
                case "hardlink": link(sentinel, leaf).Should().Be(0); break;
                case "fifo": mkfifo(leaf, 0x180).Should().Be(0); break;
                case "directory": Directory.CreateDirectory(leaf); break;
            }
            AnchoredDownloadTarget target = workspace.CreateTarget(asset);
            Action assertReady = target.AssertReady;
            if (kind == "directory")
                assertReady.Should().Throw<PackageSecurityException>().WithMessage("*workspace changed*");
            else
                assertReady.Should().Throw<IOException>();
            File.ReadAllText(sentinel).Should().Be("keep");
            await workspace.DisposeAsync();
            File.ReadAllText(sentinel).Should().Be("keep");
            Directory.Delete(named, true);
        }

        ReviewedReleaseAsset ownerAsset = Candidate(3).GetAsset(ReviewedReleaseAssetKind.Checksums);
        ReviewedReleaseAssetWorkspace ownerWorkspace = ReviewedReleaseAssetWorkspace.Create();
        AnchoredDownloadTarget wrongOwner = new(
            ownerWorkspace.FileSystem,
            ownerAsset.Name,
            ownerWorkspace.ProcPath,
            geteuid() + 1,
            ownerAsset.SizeBytes,
            _ => { }
        );
        FluentActions.Invoking(wrongOwner.AssertReady).Should().Throw<PackageSecurityException>();
        await ownerWorkspace.DisposeAsync();
        Directory.Delete(outsideRoot, true);
    }

    [Test]
    public async Task Target_RejectsWorkspaceSpecialModeBits()
    {
        ReviewedReleaseAsset asset = Candidate(3).GetAsset(ReviewedReleaseAssetKind.Checksums);
        ReviewedReleaseAssetWorkspace workspace = ReviewedReleaseAssetWorkspace.Create();
        string named = ResolveProcTarget(workspace.ProcPath);
        File.SetUnixFileMode(named, (UnixFileMode)0x3c0);

        FluentActions.Invoking(() => workspace.CreateTarget(asset)).Should().Throw<PackageSecurityException>();

        File.SetUnixFileMode(named, (UnixFileMode)0x1c0);
        await workspace.DisposeAsync();
    }

    [Test]
    public async Task Workspace_IsCurrentUserPrivateEmptyAndCleanupDoesNotChaseRenamedReplacement()
    {
        ReviewedReleaseAssetWorkspace workspace = ReviewedReleaseAssetWorkspace.Create();
        string named = ResolveProcTarget(workspace.ProcPath);
        File.GetUnixFileMode(named).Should().Be(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        );
        using (LinuxAnchoredFileSystem namedAuthority = new(named))
            namedAuthority.Identity.OwnerUserId.Should().Be(geteuid());
        Directory.EnumerateFileSystemEntries(named).Should().BeEmpty();

        string moved = named + "-moved";
        Directory.Move(named, moved);
        Directory.CreateDirectory(named);
        string unrelated = Path.Combine(named, "unrelated");
        File.WriteAllText(unrelated, "keep");

        await workspace.DisposeAsync();

        File.ReadAllText(unrelated).Should().Be("keep");
        Directory.Exists(moved).Should().BeTrue();
        Directory.Delete(named, true);
        Directory.Delete(moved, true);
    }

    [Test]
    public void CreateNewSubdirectory_ReplacementDuringOpenIsLeftUntouched()
    {
        string root = Path.Combine(Path.GetTempPath(), $"smapi-subdirectory-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using LinuxAnchoredFileSystem parent = new(root);
        string replacementMarker = Path.Combine(root, "child", "replacement");

        Action action = () => parent.CreateNewSubdirectory("child", 0x1c0, out _, () =>
        {
            Directory.Move(Path.Combine(root, "child"), Path.Combine(root, "original"));
            Directory.CreateDirectory(Path.Combine(root, "child"));
            File.WriteAllText(replacementMarker, "keep");
        }).Dispose();

        action.Should().Throw<IOException>();
        File.ReadAllText(replacementMarker).Should().Be("keep");
        Directory.Delete(root, true);
    }

    private static ReviewedReleaseCandidate Candidate(long size, int alpha = 2)
    {
        ForkReleaseIdentity identity = ForkReleaseIdentity.Parse($"fork-4eh5xitv6787h645ebv-linux-v4.5.4-alpha.{alpha}");
        byte[] catalog = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new
            {
                tag_name = identity.Tag,
                draft = false,
                prerelease = true,
                assets = Enum.GetValues<ReviewedReleaseAssetKind>().Select(kind => new
                {
                    name = ReviewedGitHubReleaseUris.GetAssetName(identity, kind),
                    size,
                    state = "uploaded",
                    browser_download_url = ReviewedGitHubReleaseUris.GetAssetUri(identity, kind).AbsoluteUri
                })
            }
        });
        return ReviewedGitHubReleaseCatalog.Parse(catalog).Single();
    }

    private static string[] AssetPaths(ReviewedReleaseProtocolAssetPaths paths)
    {
        return
        [
            paths.InstallerPackagePath,
            paths.InstallManifestPath,
            paths.ChecksumsPath,
            paths.BuildMetadataPath,
            paths.AttestationBundlePath,
            paths.AttestationBundleChecksumPath
        ];
    }

    private static string ResolveProcTarget(string procPath)
    {
        return Directory.ResolveLinkTarget(procPath, returnFinalTarget: false)?.FullName
            ?? throw new IOException("The retained workspace descriptor link couldn't be resolved for testing.");
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint geteuid();

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string existingPath, string newPath);

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string path, int mode);

    private sealed class RecordingTransport : IReviewedReleaseAssetTransport, IDisposable
    {
        private int Active;
        public List<ReviewedReleaseAssetKind> Kinds { get; } = [];
        public List<long> MaximumBytes { get; } = [];
        public int ConcurrentMaximum { get; private set; }
        public int ResultLengthDelta { get; init; }
        public CancellationTokenSource? CancelAfterPublication { get; init; }
        public int CancelOnCall { get; init; } = 1;
        public bool IgnoreCancellationAfterPublication { get; init; }
        public bool Disposed { get; private set; }

        public async Task<DownloadResult> DownloadAsync(
            ReviewedReleaseAsset asset,
            AnchoredDownloadTarget destination,
            DownloadLimits limits,
            IProgress<DownloadProgress>? progress,
            CancellationToken cancellationToken
        )
        {
            int active = Interlocked.Increment(ref this.Active);
            this.ConcurrentMaximum = Math.Max(this.ConcurrentMaximum, active);
            try
            {
                this.Kinds.Add(asset.Kind);
                this.MaximumBytes.Add(limits.MaxBytes);
                cancellationToken.ThrowIfCancellationRequested();
                using LinuxAnchoredFile file = destination.FileSystem.CreateNewFile(destination.LeafName, 0x180);
                byte[] bytes = Enumerable.Repeat((byte)((int)asset.Kind + 1), checked((int)asset.SizeBytes)).ToArray();
                await RandomAccess.WriteAsync(file.Handle, bytes, 0, cancellationToken);
                LinuxFileIdentity identity = destination.FileSystem.Stat(destination.LeafName)!;
                destination.SetPublished(identity);
                progress?.Report(new DownloadProgress(asset.SizeBytes, asset.SizeBytes));
                if (this.CancelAfterPublication is not null && this.Kinds.Count == this.CancelOnCall)
                    this.CancelAfterPublication.Cancel();
                if (!this.IgnoreCancellationAfterPublication)
                    cancellationToken.ThrowIfCancellationRequested();
                return new DownloadResult(destination.ProcPath, asset.SizeBytes + this.ResultLengthDelta, asset.DownloadUri);
            }
            finally
            {
                Interlocked.Decrement(ref this.Active);
            }
        }

        public void Dispose()
        {
            this.Disposed = true;
        }
    }

    private sealed class FixedResponseHandler(HttpContent content) : HttpMessageHandler
    {
        private readonly HttpContent Content = content;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = this.Content,
                RequestMessage = request
            });
        }
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        private readonly byte[] Bytes = bytes;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return stream.WriteAsync(this.Bytes).AsTask();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class CancelAfterFirstReadStream(byte[] bytes, CancellationTokenSource cancellation) : Stream
    {
        private readonly byte[] Bytes = bytes;
        private readonly CancellationTokenSource Cancellation = cancellation;
        private bool HasRead;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (this.HasRead)
                return ValueTask.FromResult(0);
            this.HasRead = true;
            int count = Math.Min(4, buffer.Length);
            this.Bytes.AsMemory(0, count).CopyTo(buffer);
            this.Cancellation.Cancel();
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
