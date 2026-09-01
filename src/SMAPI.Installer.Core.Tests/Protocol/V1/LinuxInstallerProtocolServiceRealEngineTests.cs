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
    public async Task BackupExecutesDurablyThroughTheRealProtocolEngine()
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
            await backupService.HandleAsync(new ConfirmPlanRequest(backupService.SessionId, backup.PlanId, backup.PlanDigest));
            SuccessEvent result = (SuccessEvent)await backupService.HandleAsync(new ExecutePlanRequest(backupService.SessionId, backup.PlanId, backup.PlanDigest));
            result.Outcome.Should().Be(ProtocolExecutionOutcome.Succeeded);
            result.TerminalState.DurableState.Should().Be(ProtocolDurableState.Committed);
        }

        RecoveryHistory afterBackup = await new LinuxInstallerEngine().ListRecoveriesAsync(game);
        afterBackup.Generations.Should().HaveCount(2);
        afterBackup.Generations[0].Action.Should().Be(InstallationAction.Backup);
    }

    private static LinuxInstallerProtocolService CreateRealService()
        => new(
            "test",
            progress => new LinuxInstallerProtocolEngine(new LinuxInstallerEngine(progress)),
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

    private static void Write(string root, string relativePath, string contents, int mode)
    {
        string path = Path.Combine(root, relativePath);
        File.WriteAllText(path, contents);
        File.SetUnixFileMode(path, (UnixFileMode)mode);
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
