using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Recovery;
using StardewModdingAPI.Installer.Core.Security;
using StardewModdingAPI.Installer.Core.Tests.Ownership;
using StardewModdingAPI.Installer.Core.Transactions;

namespace StardewModdingAPI.Installer.Core.Tests.Engine;

[TestFixture]
[SupportedOSPlatform("linux")]
public sealed class InstallerAdoptionTests
{
    private readonly List<string> TemporaryDirectories = new();

    [TearDown]
    public void TearDown()
    {
        foreach (string path in this.TemporaryDirectories)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch
            {
                // Best-effort private test cleanup.
            }
        }
    }

    [Test]
    public async Task PublicInspection_RejectsArbitraryFolderAndAcceptsValidManualPath()
    {
        string arbitrary = this.CreateDirectory(validGame: false);
        Write(arbitrary, "StardewValley", "#!/bin/sh\n", 0x1ed);
        LinuxInstallerEngine engine = new();

        Func<Task> reject = () => engine.InspectAsync(arbitrary, InstallationAction.Backup);

        (await reject.Should().ThrowAsync<LinuxGameFolderException>()).Which.Status
            .Should().Be(LinuxGameFolderStatus.MissingGameAssembly);

        string valid = this.CreateDirectory();
        using InspectedInstallationState inspection = await engine.InspectAsync(valid, InstallationAction.Backup);
        inspection.Plan.CanExecute.Should().BeFalse();
        inspection.ObservedState.Should().Be(ObservedInstallationState.NotInstalled);
    }

    [Test]
    public async Task Install_AdoptsRecognizedOfficialLayoutPreservesBackupAndRollsBack()
    {
        string game = this.CreateDirectory();
        Write(game, "StardewValley", "legacy smapi launcher", 0x1ed);
        Write(game, "StardewValley-original", "official launcher", 0x1c0);
        Write(game, "StardewModdingAPI.dll", "legacy runtime", 0x1a4);
        Write(game, "unrelated-user-file.txt", "preserve me", 0x180);
        LinuxInstallerEngine engine = new();
        using FilePackageAuthority package = this.CreatePackage(1, "new launcher", "new runtime");
        using InspectedInstallationState blocked = Inspect(engine, game, InstallationAction.Install, package);

        blocked.ObservedState.Should().Be(ObservedInstallationState.LegacyOrOfficial);
        blocked.Plan.CanExecute.Should().BeFalse();
        blocked.ModifiedFileReplacementCandidates.Select(candidate => candidate.Path.Value).Should().Equal(
            "StardewModdingAPI.dll",
            "StardewValley",
            "StardewValley-original"
        );
        blocked.ModifiedFileReplacementCandidates[0].Reason.Should().Be(FileReplacementCandidateReason.LegacyInstaller);
        blocked.ModifiedFileReplacementCandidates[0].Disposition.Should().Be(FileReplacementCandidateDisposition.Replace);
        blocked.ModifiedFileReplacementCandidates[0].ProposedResultSha256.Should().Be(Hash("new runtime"));
        blocked.ModifiedFileReplacementCandidates[1].Reason.Should().Be(FileReplacementCandidateReason.OfficialOrLegacyLauncher);
        blocked.ModifiedFileReplacementCandidates[1].Disposition.Should().Be(FileReplacementCandidateDisposition.Replace);
        blocked.ModifiedFileReplacementCandidates[1].ProposedResultSha256.Should().Be(Hash("new launcher"));
        blocked.ModifiedFileReplacementCandidates[2].Reason.Should().Be(FileReplacementCandidateReason.OfficialLauncherBackup);
        blocked.ModifiedFileReplacementCandidates[2].Disposition.Should().Be(FileReplacementCandidateDisposition.TrustRetained);
        blocked.ModifiedFileReplacementCandidates[2].ProposedResultSha256.Should().Be(Hash("official launcher"));

        using InspectedInstallationState partial = await engine.ApproveFileReplacementsAsync(
            blocked,
            [blocked.ModifiedFileReplacementCandidates[0]]
        );
        partial.Plan.CanExecute.Should().BeFalse();
        partial.ModifiedFileReplacementCandidates.Select(candidate => candidate.Path.Value).Should().Equal(
            "StardewValley",
            "StardewValley-original"
        );
        Func<Task> foreign = () => engine.ApproveFileReplacementsAsync(
            partial,
            [blocked.ModifiedFileReplacementCandidates[1]]
        );
        await foreign.Should().ThrowAsync<ExecutionCompilationException>();

        using InspectedInstallationState launcherApproved = await engine.ApproveFileReplacementsAsync(
            partial,
            [partial.ModifiedFileReplacementCandidates[0]]
        );
        launcherApproved.Plan.CanExecute.Should().BeFalse();
        launcherApproved.ModifiedFileReplacementCandidates.Should().ContainSingle()
            .Which.Path.Value.Should().Be("StardewValley-original");
        using InspectedInstallationState sequentiallyApproved = await engine.ApproveFileReplacementsAsync(
            launcherApproved,
            launcherApproved.ModifiedFileReplacementCandidates
        );
        sequentiallyApproved.Plan.CanExecute.Should().BeTrue();

        using InspectedInstallationState approved = await engine.ApproveFileReplacementsAsync(
            blocked,
            blocked.ModifiedFileReplacementCandidates
        );
        approved.Plan.CanExecute.Should().BeTrue();
        await Execute(approved, engine);

        File.ReadAllText(Path.Combine(game, "StardewValley")).Should().Be("new launcher");
        File.ReadAllText(Path.Combine(game, "StardewValley-original")).Should().Be("official launcher");
        File.GetUnixFileMode(Path.Combine(game, "StardewValley-original")).Should().Be((UnixFileMode)0x1c0);
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("new runtime");
        File.ReadAllText(Path.Combine(game, "unrelated-user-file.txt")).Should().Be("preserve me");
        using (InstallerOperationLease lease = InstallerOperationLease.Acquire(game))
        {
            InstallationReceipt receipt = AnchoredCoreStateAuthority.Inspect(lease).Receipt!;
            receipt.Launcher.OriginalLauncherSha256.Should().Be(Hash("official launcher"));
            receipt.Launcher.OriginalLauncherUnixMode.Should().Be(0x1c0);
        }

        using CommittedRecoveryHandle recovery = await engine.OpenCurrentRecoveryAsync(game);
        using InspectedInstallationState rollback = await engine.InspectAsync(
            game,
            InstallationAction.Rollback,
            recovery: recovery
        );
        await Execute(rollback, engine);

        File.ReadAllText(Path.Combine(game, "StardewValley")).Should().Be("legacy smapi launcher");
        File.ReadAllText(Path.Combine(game, "StardewValley-original")).Should().Be("official launcher");
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("legacy runtime");
        File.ReadAllText(Path.Combine(game, "unrelated-user-file.txt")).Should().Be("preserve me");
        using InstallerOperationLease verification = InstallerOperationLease.Acquire(game);
        AnchoredCoreStateAuthority.Inspect(verification).Receipt.Should().BeNull();
    }

    [Test]
    public async Task Install_UnknownTargetCollisionRequiresExactApprovalAndLeavesUnrelatedPathsUntouched()
    {
        string game = this.CreateDirectory();
        Write(game, "StardewValley", "official launcher", 0x1ed);
        Write(game, "steam_appid.txt", "user value", 0x180);
        Write(game, "unrelated-user-file.txt", "preserve me", 0x180);
        LinuxInstallerEngine engine = new();
        using FilePackageAuthority package = this.CreatePackage(
            1,
            "new launcher",
            "new runtime",
            ("steam_appid.txt", "413150", 0x1a4, OwnedEntryKind.RuntimeFile)
        );
        using InspectedInstallationState blocked = Inspect(engine, game, InstallationAction.Install, package);

        ModifiedFileReplacementCandidate collision = blocked.ModifiedFileReplacementCandidates.Should().ContainSingle().Subject;
        collision.Path.Value.Should().Be("steam_appid.txt");
        collision.Reason.Should().Be(FileReplacementCandidateReason.UnknownCollision);
        collision.Disposition.Should().Be(FileReplacementCandidateDisposition.Replace);
        collision.ProposedResultSha256.Should().Be(Hash("413150"));
        using InspectedInstallationState approved = await engine.ApproveFileReplacementsAsync(blocked, [collision]);
        await Execute(approved, engine);

        File.ReadAllText(Path.Combine(game, "steam_appid.txt")).Should().Be("413150");
        File.ReadAllText(Path.Combine(game, "unrelated-user-file.txt")).Should().Be("preserve me");
    }

    [Test]
    public void Install_AmbiguousLauncherBackupWithoutRecognizedLegacyEvidenceIsNotApprovable()
    {
        string game = this.CreateDirectory();
        Write(game, "StardewValley", "unknown launcher", 0x1ed);
        Write(game, "StardewValley-original", "unknown backup", 0x1ed);
        LinuxInstallerEngine engine = new();
        using FilePackageAuthority package = this.CreatePackage(1, "new launcher", "new runtime");

        using InspectedInstallationState blocked = Inspect(engine, game, InstallationAction.Install, package);

        blocked.ObservedState.Should().Be(ObservedInstallationState.Unknown);
        blocked.Plan.Conflicts.Should().Contain(conflict => conflict.Code == PlanConflictCode.AmbiguousLauncherBackup);
        blocked.ModifiedFileReplacementCandidates.Should().BeEmpty();
    }

    [Test]
    public async Task UpdateAndUninstall_ExactModifiedApprovalsAreRecoverable()
    {
        string game = this.CreateDirectory();
        Write(game, "StardewValley", "official launcher", 0x1ed);
        Write(game, "unrelated-user-file.txt", "preserve me", 0x180);
        LinuxInstallerEngine engine = new();
        using FilePackageAuthority first = this.CreatePackage(1, "launcher one", "runtime one");
        using FilePackageAuthority second = this.CreatePackage(2, "launcher two", "runtime two");
        await Execute(Inspect(engine, game, InstallationAction.Install, first), engine);

        Write(game, "StardewValley", "modified launcher", 0x1c0);
        Write(game, "StardewModdingAPI.dll", "modified runtime", 0x180);
        using (InspectedInstallationState update = Inspect(engine, game, InstallationAction.Update, second))
        {
            update.ModifiedFileReplacementCandidates.Select(candidate => candidate.Path.Value).Should().Equal(
                "StardewModdingAPI.dll",
                "StardewValley"
            );
            foreach (ModifiedFileReplacementCandidate candidate in update.ModifiedFileReplacementCandidates)
            {
                candidate.Reason.Should().BeOneOf(
                    FileReplacementCandidateReason.ModifiedReceiptOwned,
                    FileReplacementCandidateReason.ModifiedInstalledLauncher
                );
                candidate.Disposition.Should().Be(FileReplacementCandidateDisposition.Replace);
                candidate.ProposedResultSha256.Should().NotBeNull();
            }
            using InspectedInstallationState approved = await engine.ApproveFileReplacementsAsync(
                update,
                update.ModifiedFileReplacementCandidates
            );
            await Execute(approved, engine);
        }
        File.ReadAllText(Path.Combine(game, "StardewValley")).Should().Be("launcher two");
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("runtime two");

        Write(game, "StardewValley", "second modified launcher", 0x1c0);
        Write(game, "StardewModdingAPI.dll", "second modified runtime", 0x180);
        using InspectedInstallationState uninstall = await engine.InspectAsync(game, InstallationAction.Uninstall);
        uninstall.ModifiedFileReplacementCandidates.Should().ContainSingle(candidate =>
            candidate.Path.Value == "StardewModdingAPI.dll"
            && candidate.Reason == FileReplacementCandidateReason.ModifiedReceiptOwned
            && candidate.Disposition == FileReplacementCandidateDisposition.Remove
            && candidate.ProposedResultSha256 == null
        );
        uninstall.ModifiedFileReplacementCandidates.Should().ContainSingle(candidate =>
            candidate.Path.Value == "StardewValley"
            && candidate.Reason == FileReplacementCandidateReason.ModifiedInstalledLauncher
            && candidate.Disposition == FileReplacementCandidateDisposition.Restore
            && candidate.ProposedResultSha256 == Hash("official launcher")
        );
        using InspectedInstallationState approvedUninstall = await engine.ApproveFileReplacementsAsync(
            uninstall,
            uninstall.ModifiedFileReplacementCandidates
        );
        await Execute(approvedUninstall, engine);

        File.ReadAllText(Path.Combine(game, "StardewValley")).Should().Be("official launcher");
        File.Exists(Path.Combine(game, "StardewValley-original")).Should().BeFalse();
        File.Exists(Path.Combine(game, "StardewModdingAPI.dll")).Should().BeFalse();
        File.ReadAllText(Path.Combine(game, "unrelated-user-file.txt")).Should().Be("preserve me");

        using CommittedRecoveryHandle recovery = await engine.OpenCurrentRecoveryAsync(game);
        using InspectedInstallationState rollback = await engine.InspectAsync(
            game,
            InstallationAction.Rollback,
            recovery: recovery
        );
        await Execute(rollback, engine);
        File.ReadAllText(Path.Combine(game, "StardewValley")).Should().Be("second modified launcher");
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("second modified runtime");
    }

    [Test]
    public async Task ReplacementCandidates_RejectDriftGenerationAndMarkerChangesBeforeMutation()
    {
        string game = this.CreateDirectory();
        Write(game, "StardewValley", "official launcher", 0x1ed);
        Write(game, "StardewModdingAPI.dll", "legacy runtime", 0x1a4);
        LinuxInstallerEngine engine = new();
        using FilePackageAuthority package = this.CreatePackage(1, "new launcher", "new runtime");
        using InspectedInstallationState drifted = Inspect(engine, game, InstallationAction.Install, package);
        ModifiedFileReplacementCandidate candidate = drifted.ModifiedFileReplacementCandidates.Single();
        Write(game, "StardewModdingAPI.dll", "changed runtime", 0x1a4);

        Func<Task> rejectDrift = () => engine.ApproveFileReplacementsAsync(drifted, [candidate]);
        await rejectDrift.Should().ThrowAsync<ExecutionCompilationException>();

        Write(game, "StardewModdingAPI.dll", "legacy runtime", 0x1a4);
        using InspectedInstallationState staleGeneration = Inspect(engine, game, InstallationAction.Install, package);
        using (InstallerOperationLease lease = InstallerOperationLease.Acquire(game))
            lease.ReserveNextGeneration(lease.Generation);
        Func<Task> rejectGeneration = () => engine.ApproveFileReplacementsAsync(
            staleGeneration,
            staleGeneration.ModifiedFileReplacementCandidates
        );
        await rejectGeneration.Should().ThrowAsync<ExecutionCompilationException>();

        using InspectedInstallationState markerDrift = Inspect(engine, game, InstallationAction.Install, package);
        File.Delete(Path.Combine(game, "Stardew Valley.deps.json"));
        Func<Task> rejectMarker = () => engine.ApproveFileReplacementsAsync(
            markerDrift,
            markerDrift.ModifiedFileReplacementCandidates
        );
        (await rejectMarker.Should().ThrowAsync<LinuxGameFolderException>()).Which.Status
            .Should().Be(LinuxGameFolderStatus.MissingGameDependencies);
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("legacy runtime");
    }

    [Test]
    public async Task Execute_RevalidatesGameMarkersAfterConfirmation()
    {
        string game = this.CreateDirectory();
        Write(game, "StardewValley", "official launcher", 0x1ed);
        LinuxInstallerEngine engine = new();
        using FilePackageAuthority package = this.CreatePackage(1, "new launcher", "new runtime");
        using InspectedInstallationState inspection = Inspect(engine, game, InstallationAction.Install, package);
        File.Delete(Path.Combine(game, "Stardew Valley.dll"));

        Func<Task> execute = () => engine.ExecuteAsync(inspection, inspection.ConfirmationDigest);

        (await execute.Should().ThrowAsync<LinuxGameFolderException>()).Which.Status
            .Should().Be(LinuxGameFolderStatus.MissingGameAssembly);
        File.ReadAllText(Path.Combine(game, "StardewValley")).Should().Be("official launcher");
        File.Exists(Path.Combine(game, "StardewModdingAPI.dll")).Should().BeFalse();
    }

    private string CreateDirectory(bool validGame = true)
    {
        string path = Path.Combine(Path.GetTempPath(), $"smapi-adoption-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        if (validGame)
            LinuxGameTestFolder.MakeValid(path);
        this.TemporaryDirectories.Add(path);
        return path;
    }

    private FilePackageAuthority CreatePackage(
        int alpha,
        string launcher,
        string runtime,
        params (string Path, string Contents, int Mode, OwnedEntryKind Kind)[] extraEntries
    )
    {
        string root = this.CreateDirectory(validGame: false);
        Write(root, "StardewValley", launcher, 0x1ed);
        Write(root, "StardewModdingAPI.dll", runtime, 0x1a4);
        foreach ((string path, string contents, int mode, OwnedEntryKind _) in extraEntries)
            Write(root, path, contents, mode);
        PackageManifest manifest = new(
            OwnershipTestData.Release(alpha),
            new[]
            {
                Entry("StardewValley", launcher, 0x1ed, OwnedEntryKind.Launcher),
                Entry("StardewModdingAPI.dll", runtime, 0x1a4, OwnedEntryKind.RuntimeFile)
            }.Concat(extraEntries.Select(entry => Entry(entry.Path, entry.Contents, entry.Mode, entry.Kind)))
        );
        return new FilePackageAuthority(manifest, root);
    }

    private static InspectedInstallationState Inspect(
        LinuxInstallerEngine engine,
        string game,
        InstallationAction action,
        IVerifiedPackageContentAuthority? package = null
    )
    {
        using InstallerOperationLease lease = InstallerOperationLease.Acquire(game);
        return engine.InspectLocked(lease, action, package, null);
    }

    private static async Task Execute(InspectedInstallationState inspection, LinuxInstallerEngine engine)
    {
        inspection.Plan.CanExecute.Should().BeTrue(string.Join(", ", inspection.Plan.Conflicts.Select(conflict => conflict.Code)));
        (await engine.ExecuteAsync(inspection, inspection.ConfirmationDigest)).Status.Should().Be(TransactionStatus.Committed);
    }

    private static PackageManifestEntry Entry(string path, string contents, int mode, OwnedEntryKind kind)
        => new(NormalizedRelativePath.Parse(path), Hash(contents), Encoding.UTF8.GetByteCount(contents), mode, kind);

    private static Sha256Digest Hash(string contents) => Sha256Digest.Hash(Encoding.UTF8.GetBytes(contents));

    private static void Write(string root, string relativePath, string contents, int mode)
    {
        string path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        File.SetUnixFileMode(path, (UnixFileMode)mode);
    }

    private sealed class FilePackageAuthority : IVerifiedPackageContentAuthority, IDisposable
    {
        private readonly LinuxAnchoredFileSystem Payload;
        public PackageManifest Manifest { get; }
        public Sha256Digest ManifestSha256 => this.Manifest.GetCanonicalDigest();

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

        public void AssertUsable() => this.Payload.GetCurrentRootIdentity().Should().Be(this.Payload.Identity);
        public void Dispose() => this.Payload.Dispose();
    }
}
