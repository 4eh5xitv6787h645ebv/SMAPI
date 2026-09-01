using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Core.Recovery;
using StardewModdingAPI.Installer.Core.Security;
using StardewModdingAPI.Installer.Core.Tests.Ownership;
using StardewModdingAPI.Installer.Core.Transactions;

namespace StardewModdingAPI.Installer.Core.Tests.Protocol.V1;

[TestFixture]
[Platform("Linux")]
[SupportedOSPlatform("linux")]
internal sealed class LinuxInstallerProtocolServiceRealEngineTests
{
    private readonly List<string> TemporaryDirectories = [];

    [TearDown]
    public void TearDown()
    {
        foreach (string path in this.TemporaryDirectories)
        {
            try { Directory.Delete(path, recursive: true); }
            catch { }
        }
    }

    [Test]
    public async Task AbsentPointerIsNonterminalButMalformedPointerStillFailsClosed()
    {
        string game = this.CreateDirectory();
        RecordingProgress progress = new();
        using LinuxInstallerProtocolService service = CreateRealService(progress);
        await Handshake(service);
        ListRecoveriesRequest firstRequest = new(service.SessionId, game);

        NoRecoveryHistoryEvent first = (NoRecoveryHistoryEvent)await service.HandleAsync(firstRequest);
        NoRecoveryHistoryEvent retry = (NoRecoveryHistoryEvent)await service.HandleAsync(new ListRecoveriesRequest(service.SessionId, game));

        first.CommandId.Should().Be(firstRequest.CommandId);
        retry.CommandId.Should().NotBe(first.CommandId);
        service.State.Should().Be(ProtocolSessionState.Ready);
        progress.Values.Should().ContainInOrder(
            new TransactionProgress(TransactionStage.VerifyingRecovery, 0, null),
            new TransactionProgress(TransactionStage.VerifyingRecovery, 1, 1),
            new TransactionProgress(TransactionStage.VerifyingRecovery, 0, null),
            new TransactionProgress(TransactionStage.VerifyingRecovery, 1, 1)
        );

        string installerState = Path.Combine(game, ".smapi-installer");
        string recoveryState = Path.Combine(installerState, "recovery");
        Directory.CreateDirectory(recoveryState);
        File.SetUnixFileMode(installerState, (UnixFileMode)0x1c0);
        File.SetUnixFileMode(recoveryState, (UnixFileMode)0x1c0);
        string marker = Path.Combine(installerState, InstallerTransactionExecutor.WorkspaceMarkerName);
        File.WriteAllText(marker, InstallerTransactionExecutor.WorkspaceMarkerContents);
        File.SetUnixFileMode(marker, (UnixFileMode)0x180);
        string pointer = Path.Combine(recoveryState, "current.json");
        File.WriteAllText(pointer, "{\"private-corrupt-pointer\":true}");
        File.SetUnixFileMode(pointer, (UnixFileMode)0x180);

        ProtocolEvent corrupt = await service.HandleAsync(new ListRecoveriesRequest(service.SessionId, game));

        corrupt.Should().BeOfType<PrePlanRejectedEvent>();
        PrePlanRejectedEvent rejected = (PrePlanRejectedEvent)corrupt;
        rejected.ErrorCode.Should().Be(ProtocolPrePlanErrorCode.UnexpectedFailure);
        rejected.Message.Should().NotContain("private-corrupt-pointer");
        rejected.IsTerminal.Should().BeTrue();
        service.State.Should().Be(ProtocolSessionState.Completed);
    }

    [Test]
    public async Task AbsentPointerThroughRootSymlinkIsRejectedUntilTheCanonicalPathIsRequested()
    {
        string game = this.CreateDirectory();
        string alias = Path.Combine(Path.GetTempPath(), $"smapi-real-protocol-alias-{Guid.NewGuid():N}");
        Directory.CreateSymbolicLink(alias, game);
        try
        {
            using LinuxInstallerProtocolService service = CreateRealService();
            await Handshake(service);

            Func<Task> aliased = async () => await service.HandleAsync(new ListRecoveriesRequest(service.SessionId, alias));

            await aliased.Should().ThrowAsync<ProtocolException>().WithMessage("*doesn't match the requested path*");
            service.State.Should().Be(ProtocolSessionState.Ready);
            (await service.HandleAsync(new ListRecoveriesRequest(service.SessionId, game))).Should().BeOfType<NoRecoveryHistoryEvent>();
            service.State.Should().Be(ProtocolSessionState.Ready);
        }
        finally
        {
            Directory.Delete(alias);
        }
    }

    [Test]
    public async Task BackupCatalogAndPruneExecuteDurablyThroughTheRealProtocolEngine()
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        Write(game, "StardewValley", "vanilla launcher", 0x1ed);
        Write(payload, "StardewValley", "smapi launcher", 0x1ed);
        Write(payload, "StardewModdingAPI.dll", "runtime", 0x1a4);
        PackageManifest manifest = new(
            OwnershipTestData.Release(),
            [
                Entry("StardewValley", "smapi launcher", 0x1ed, OwnedEntryKind.Launcher),
                Entry("StardewModdingAPI.dll", "runtime", 0x1a4, OwnedEntryKind.RuntimeFile)
            ]
        );
        using FilePackageAuthority package = new(manifest, payload);
        LinuxInstallerEngine seedingEngine = new();
        InspectedInstallationState install;
        using (InstallerOperationLease lease = InstallerOperationLease.Acquire(game))
            install = seedingEngine.InspectLocked(lease, InstallationAction.Install, package, null);
        using (install)
        {
            install.Plan.CanExecute.Should().BeTrue();
            (await seedingEngine.ExecuteAsync(install, install.ConfirmationDigest)).Status.Should().Be(TransactionStatus.Committed);
        }

        using (LinuxInstallerProtocolService backupService = CreateRealService())
        {
            await Handshake(backupService);
            PlanEvent backup = (PlanEvent)await backupService.HandleAsync(new InspectPlanRequest(backupService.SessionId, game, InstallerOperation.Backup, null, null));
            backup.CanExecute.Should().BeTrue();
            backup.PlanDigest.Should().NotBe(backup.ExecutionBindingDigest);
            string[] beforeConfirmation = SnapshotTree(game);
            CommandAcknowledgedEvent confirmed = (CommandAcknowledgedEvent)await backupService.HandleAsync(new ConfirmPlanRequest(backupService.SessionId, backup.PlanId, backup.PlanDigest));
            confirmed.Acknowledgement.Should().Be(ProtocolAcknowledgementKind.PlanConfirmed);
            SnapshotTree(game).Should().Equal(beforeConfirmation, "confirmation transfers protocol authority but performs no filesystem mutation");
            SuccessEvent result = (SuccessEvent)await backupService.HandleAsync(new ExecutePlanRequest(backupService.SessionId, backup.PlanId, backup.PlanDigest));
            result.Outcome.Should().Be(ProtocolExecutionOutcome.Succeeded);
            result.TerminalState.DurableState.Should().Be(ProtocolDurableState.Committed);
        }

        RecoveryHistory afterBackup = await new LinuxInstallerEngine().ListRecoveriesAsync(game);
        afterBackup.Generations.Should().HaveCount(2);
        afterBackup.Generations[0].Action.Should().Be(InstallationAction.Backup);

        using (CommittedRecoveryHandle reopened = await new LinuxInstallerEngine().OpenRecoveryAsync(game, afterBackup.Generations[0].GenerationId))
        {
            reopened.RestoreRelease.Should().Be(afterBackup.Generations[0].RestoreRelease);
            reopened.RestoreRelease.Should().NotBeSameAs(afterBackup.Generations[0].RestoreRelease);
        }

        using (LinuxInstallerProtocolService pruneService = CreateRealService())
        {
            await Handshake(pruneService);
            RecoveryCatalogEvent catalog = (RecoveryCatalogEvent)await pruneService.HandleAsync(new ListRecoveriesRequest(pruneService.SessionId, game));
            catalog.Generations.Should().HaveCount(2);
            PrunePlanEvent prune = (PrunePlanEvent)await pruneService.HandleAsync(new InspectPruneRequest(pruneService.SessionId, catalog.CatalogId, 1));
            await pruneService.HandleAsync(new ConfirmPruneRequest(pruneService.SessionId, prune.PrunePlanId, prune.PruneDigest));
            PruneSuccessEvent result = (PruneSuccessEvent)await pruneService.HandleAsync(new ExecutePruneRequest(pruneService.SessionId, prune.PrunePlanId, prune.PruneDigest));
            result.Outcome.Should().Be(ProtocolPruneOutcome.Succeeded);
            result.TerminalState.DurableState.Should().Be(ProtocolDurableState.PruneApplied);
            result.PruneSummary.LogicallyRemovedGenerationCount.Should().Be(1);
            result.PruneSummary.PhysicallyCleanedGenerationCount.Should().Be(1);
        }

        (await new LinuxInstallerEngine().ListRecoveriesAsync(game)).Generations.Should().ContainSingle();
    }

    private static LinuxInstallerProtocolService CreateRealService(ITransactionProgressSink? observedProgress = null)
        => new(
            "test",
            progress => new LinuxInstallerProtocolEngine(new LinuxInstallerEngine(new CompositeProgress(progress, observedProgress))),
            new UnusedDiscovery(),
            new UnusedPackageOpener()
        );

    private static Task<ProtocolEvent> Handshake(LinuxInstallerProtocolService service)
        => service.HandleAsync(new HandshakeRequest("gui", "1"));

    private string CreateDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"smapi-real-protocol-{Guid.NewGuid():N}");
        LinuxGameTestFolder.MakeValid(path);
        this.TemporaryDirectories.Add(path);
        return path;
    }

    private static PackageManifestEntry Entry(string path, string contents, int mode, OwnedEntryKind kind)
        => new(NormalizedRelativePath.Parse(path), Hash(contents), Encoding.UTF8.GetByteCount(contents), mode, kind);

    private static Sha256Digest Hash(string contents) => Sha256Digest.Hash(Encoding.UTF8.GetBytes(contents));

    private static string[] SnapshotTree(string root)
    {
        List<string> result = [];
        AddDirectory(root, ".");
        return result.ToArray();

        void AddDirectory(string path, string relativePath)
        {
            DirectoryInfo directory = new(path);
            result.Add($"directory\0{relativePath}\0{(int)File.GetUnixFileMode(path)}\0{directory.LastWriteTimeUtc.Ticks}");
            foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos().OrderBy(entry => entry.Name, StringComparer.Ordinal))
            {
                string relative = Path.GetRelativePath(root, entry.FullName);
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    result.Add($"link\0{relative}\0{entry.LinkTarget}");
                else if (entry is DirectoryInfo)
                    AddDirectory(entry.FullName, relative);
                else
                {
                    FileInfo file = (FileInfo)entry;
                    result.Add($"file\0{relative}\0{(int)File.GetUnixFileMode(entry.FullName)}\0{file.Length}\0{file.LastWriteTimeUtc.Ticks}\0{Sha256Digest.Hash(File.ReadAllBytes(entry.FullName)).Value}");
                }
            }
        }
    }

    private static void Write(string root, string relativePath, string contents, int mode)
    {
        string path = Path.Combine(root, relativePath);
        File.WriteAllText(path, contents);
        File.SetUnixFileMode(path, (UnixFileMode)mode);
    }

    private sealed class CompositeProgress(ITransactionProgressSink protocol, ITransactionProgressSink? observed) : ITransactionProgressSink
    {
        public void Report(TransactionProgress progress)
        {
            protocol.Report(progress);
            observed?.Report(progress);
        }
    }

    private sealed class RecordingProgress : ITransactionProgressSink
    {
        public List<TransactionProgress> Values { get; } = [];
        public void Report(TransactionProgress progress) => this.Values.Add(progress);
    }

    private sealed class FilePackageAuthority : IVerifiedPackageContentAuthority, IDisposable
    {
        private readonly LinuxAnchoredFileSystem Payload;
        public PackageManifest Manifest { get; }
        public Sha256Digest ManifestSha256 => this.Manifest.GetCanonicalDigest();
        public VerifiedTaggedPackageTrust? ReleaseTrust => null;

        public FilePackageAuthority(PackageManifest manifest, string payloadRoot)
        {
            this.Manifest = manifest;
            this.Payload = new LinuxAnchoredFileSystem(payloadRoot);
        }

        public LinuxAnchoredFile OpenFile(PackageManifestEntry expected, CancellationToken cancellationToken = default)
        {
            if (!this.Manifest.Entries.Contains(expected))
                throw new InvalidOperationException("The requested entry isn't in this test authority.");
            LinuxAnchoredFile file = this.Payload.OpenRegularFileForRead(expected.Path.Value);
            if (
                file.Identity.Size != expected.SizeBytes
                || file.Identity.UnixMode != expected.UnixMode
                || Sha256Digest.Parse(this.Payload.ComputeSha256(file, cancellationToken)) != expected.Sha256
            )
            {
                file.Dispose();
                throw new InvalidOperationException("The test package entry changed.");
            }
            return file;
        }

        public void AssertUsable()
        {
            if (this.Payload.GetCurrentRootIdentity() != this.Payload.Identity)
                throw new InvalidOperationException("The test package root changed.");
        }

        public void Dispose() => this.Payload.Dispose();
    }

    private sealed class UnusedDiscovery : ILinuxInstallerProtocolDiscovery
    {
        public Task<IReadOnlyList<LinuxGameFolderCandidate>> DiscoverAsync(CancellationToken cancellationToken) => throw new AssertionException("Discovery should not be used.");
        public Task<LinuxGameFolderCandidate> ValidateAsync(string gameRoot, CancellationToken cancellationToken) => throw new AssertionException("Discovery should not be used.");
    }

    private sealed class UnusedPackageOpener : ILinuxInstallerProtocolPackageOpener
    {
        public Task<ProtocolPackageRegistration> OpenAsync(OpenPackageRequest request, CancellationToken cancellationToken) => throw new AssertionException("Package opening should not be used.");
        public void Dispose() { }
    }
}
