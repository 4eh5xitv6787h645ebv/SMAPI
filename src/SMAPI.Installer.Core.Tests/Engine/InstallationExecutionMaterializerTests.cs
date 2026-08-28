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
public sealed class InstallationExecutionMaterializerTests
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
    public void Apply_FreshInstall_CommitsFilesOwnershipAndExecutableRecoveryAsOneTuple()
    {
        string game = this.CreateDirectory();
        string packageRoot = this.CreateDirectory();
        Write(game, "StardewValley", "vanilla launcher", 0x1ed);
        Write(packageRoot, "StardewValley", "smapi launcher", 0x1ed);
        Write(packageRoot, "StardewModdingAPI.dll", "runtime", 0x1a4);
        PackageManifest manifest = new(
            OwnershipTestData.Release(),
            new[]
            {
                Entry("StardewValley", "smapi launcher", 0x1ed, OwnedEntryKind.Launcher),
                Entry("StardewModdingAPI.dll", "runtime", 0x1a4, OwnedEntryKind.RuntimeFile)
            }
        );
        using FilePackageAuthority package = new(manifest, packageRoot);
        Sha256Digest vanillaSha = Hash("vanilla launcher");
        LinuxInstallerEngine engine = new();
        InspectedInstallationState inspection;
        using (InstallerOperationLease lease = InstallerOperationLease.Acquire(game))
            inspection = engine.InspectLocked(lease, InstallationAction.Install, package, null);
        using (inspection)
        {
            inspection.Plan.CanExecute.Should().BeTrue();
            engine.ExecuteAsync(inspection, inspection.ConfirmationDigest).GetAwaiter().GetResult().Status
                .Should().Be(TransactionStatus.Committed);
        }

        File.ReadAllText(Path.Combine(game, "StardewValley")).Should().Be("smapi launcher");
        File.GetUnixFileMode(Path.Combine(game, "StardewValley")).Should().Be((UnixFileMode)0x1ed);
        File.ReadAllText(Path.Combine(game, "StardewValley-original")).Should().Be("vanilla launcher");
        File.GetUnixFileMode(Path.Combine(game, "StardewValley-original")).Should().Be((UnixFileMode)0x1ed);
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("runtime");

        using InstallerOperationLease verificationLease = InstallerOperationLease.Acquire(game);
        AnchoredCoreStateAuthority committed = AnchoredCoreStateAuthority.Inspect(verificationLease);
        using CommittedRecoveryHandle recovery = CommittedRecoveryHandle.OpenCurrent(verificationLease, committed);
        committed.ManifestSha256.Should().Be(manifest.GetCanonicalDigest());
        committed.Receipt.Should().NotBeNull();
        committed.Pointer.Should().NotBeNull();
        recovery.Action.Should().Be(InstallationAction.Install);
        recovery.Snapshot.Entries.Should().Contain(entry =>
            entry.Path.Value == "StardewValley"
            && entry.Kind == RollbackEntryKind.Restore
            && entry.BackupSha256 == vanillaSha
        );
    }

    [Test]
    public void Apply_AllSixActions_UpdateRepairBackupUninstallAndRollbackRoundTrip()
    {
        string game = this.CreateDirectory();
        Write(game, "StardewValley", "vanilla launcher", 0x1ed);
        LinuxInstallerEngine engine = new();
        using FilePackageAuthority first = this.CreatePackage("launcher one", "runtime one");
        using FilePackageAuthority second = this.CreatePackage("launcher two", "runtime two");

        Execute(this.Inspect(engine, game, InstallationAction.Install, first), engine);
        Execute(this.Inspect(engine, game, InstallationAction.Update, second), engine);
        File.ReadAllText(Path.Combine(game, "StardewValley")).Should().Be("launcher two");
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("runtime two");

        File.Delete(Path.Combine(game, "StardewModdingAPI.dll"));
        Execute(this.Inspect(engine, game, InstallationAction.Repair, second), engine);
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("runtime two");

        Execute(engine.InspectAsync(game, InstallationAction.Backup).GetAwaiter().GetResult(), engine);
        using (CommittedRecoveryHandle backup = engine.OpenCurrentRecoveryAsync(game).GetAwaiter().GetResult())
            backup.Action.Should().Be(InstallationAction.Backup);

        Execute(engine.InspectAsync(game, InstallationAction.Uninstall).GetAwaiter().GetResult(), engine);
        File.ReadAllText(Path.Combine(game, "StardewValley")).Should().Be("vanilla launcher");
        File.Exists(Path.Combine(game, "StardewValley-original")).Should().BeFalse();
        File.Exists(Path.Combine(game, "StardewModdingAPI.dll")).Should().BeFalse();

        using CommittedRecoveryHandle uninstallRecovery = engine.OpenCurrentRecoveryAsync(game).GetAwaiter().GetResult();
        uninstallRecovery.Action.Should().Be(InstallationAction.Uninstall);
        Execute(
            engine.InspectAsync(game, InstallationAction.Rollback, recovery: uninstallRecovery).GetAwaiter().GetResult(),
            engine
        );

        File.ReadAllText(Path.Combine(game, "StardewValley")).Should().Be("launcher two");
        File.ReadAllText(Path.Combine(game, "StardewValley-original")).Should().Be("vanilla launcher");
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("runtime two");
        using InstallerOperationLease lease = InstallerOperationLease.Acquire(game);
        AnchoredCoreStateAuthority state = AnchoredCoreStateAuthority.Inspect(lease);
        state.Manifest!.Release.Should().Be(second.Manifest.Release);
        state.Receipt.Should().NotBeNull();
        state.Pointer!.Action.Should().Be(InstallationAction.Rollback);
    }

    [Test]
    public void Apply_SelectedBackupAfterUpdate_RestoresCheckpointAndOwnershipTuple()
    {
        string game = this.CreateDirectory();
        Write(game, "StardewValley", "vanilla launcher", 0x1ed);
        LinuxInstallerEngine engine = new();
        using FilePackageAuthority checkpointPackage = this.CreatePackage("launcher one", "runtime one");
        using FilePackageAuthority laterPackage = this.CreatePackage("launcher two", "runtime two");

        Execute(this.Inspect(engine, game, InstallationAction.Install, checkpointPackage), engine);
        Execute(engine.InspectAsync(game, InstallationAction.Backup).GetAwaiter().GetResult(), engine);
        Guid backupGeneration;
        using (CommittedRecoveryHandle backup = engine.OpenCurrentRecoveryAsync(game).GetAwaiter().GetResult())
            backupGeneration = backup.GenerationId;

        Execute(this.Inspect(engine, game, InstallationAction.Update, laterPackage), engine);
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("runtime two");

        using CommittedRecoveryHandle selected = engine.OpenRecoveryAsync(game, backupGeneration).GetAwaiter().GetResult();
        selected.Action.Should().Be(InstallationAction.Backup);
        Execute(engine.InspectAsync(game, InstallationAction.Rollback, recovery: selected).GetAwaiter().GetResult(), engine);

        File.ReadAllText(Path.Combine(game, "StardewValley")).Should().Be("launcher one");
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("runtime one");
        using InstallerOperationLease lease = InstallerOperationLease.Acquire(game);
        AnchoredCoreStateAuthority state = AnchoredCoreStateAuthority.Inspect(lease);
        state.Manifest!.Release.Should().Be(checkpointPackage.Manifest.Release);
        state.Receipt!.ManifestSha256.Should().Be(checkpointPackage.Manifest.GetCanonicalDigest());
        state.Pointer!.Action.Should().Be(InstallationAction.Rollback);
    }

    [Test]
    public void Execute_ModeOnlyDriftAfterInspection_DoesNotCommitFalseReceipt()
    {
        string game = this.CreateDirectory();
        Write(game, "StardewValley", "vanilla launcher", 0x1ed);
        LinuxInstallerEngine engine = new();
        using FilePackageAuthority package = this.CreatePackage("launcher one", "runtime one");
        Execute(this.Inspect(engine, game, InstallationAction.Install, package), engine);
        InspectedInstallationState inspection = this.Inspect(engine, game, InstallationAction.Repair, package);
        File.SetUnixFileMode(Path.Combine(game, "StardewModdingAPI.dll"), (UnixFileMode)0x1ed);

        Action execute = () => engine.ExecuteAsync(inspection, inspection.ConfirmationDigest).GetAwaiter().GetResult();

        using (inspection)
            execute.Should().Throw<Exception>();
        using InstallerOperationLease lease = InstallerOperationLease.Acquire(game);
        AnchoredCoreStateAuthority state = AnchoredCoreStateAuthority.Inspect(lease);
        state.Receipt!.ManifestSha256.Should().Be(package.Manifest.GetCanonicalDigest());
        state.Pointer!.Action.Should().Be(InstallationAction.Install);
        File.GetUnixFileMode(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be((UnixFileMode)0x1ed);
    }

    [Test]
    public void Execute_FullRecoveryStoreRejectsBeforeGameMutation()
    {
        string game = this.CreateDirectory();
        Write(game, "StardewValley", "vanilla launcher", 0x1ed);
        LinuxInstallerEngine engine = new();
        using FilePackageAuthority package = this.CreatePackage("launcher one", "runtime one");
        Execute(this.Inspect(engine, game, InstallationAction.Install, package), engine);
        string generations = Path.Combine(game, ".smapi-installer", "recovery", "generations");
        for (int index = 0; index < 63; index++)
        {
            string generation = Path.Combine(generations, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(generation);
            File.SetUnixFileMode(generation, (UnixFileMode)0x1c0);
        }
        using InspectedInstallationState inspection = engine.InspectAsync(game, InstallationAction.Backup).GetAwaiter().GetResult();

        Action execute = () => engine.ExecuteAsync(inspection, inspection.ConfirmationDigest).GetAwaiter().GetResult();

        execute.Should().Throw<InstallerTransactionException>().Which.Code.Should().Be(TransactionErrorCode.WorkspaceConflict);
        File.ReadAllText(Path.Combine(game, "StardewValley")).Should().Be("launcher one");
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("runtime one");
        using InstallerOperationLease lease = InstallerOperationLease.Acquire(game);
        AnchoredCoreStateAuthority state = AnchoredCoreStateAuthority.Inspect(lease);
        state.Pointer!.Action.Should().Be(InstallationAction.Install);
    }

    [TestCase("StardewValley", InstallationAction.Repair, PlanConflictCode.ModifiedInstalledLauncher)]
    [TestCase("StardewValley-original", InstallationAction.Uninstall, PlanConflictCode.AmbiguousLauncherBackup)]
    public void Inspect_ModeOnlyLauncherCorruption_IsNonExecutable(
        string path,
        InstallationAction action,
        PlanConflictCode expectedConflict
    )
    {
        string game = this.CreateDirectory();
        Write(game, "StardewValley", "vanilla launcher", 0x1ed);
        LinuxInstallerEngine engine = new();
        using FilePackageAuthority package = this.CreatePackage("launcher one", "runtime one");
        Execute(this.Inspect(engine, game, InstallationAction.Install, package), engine);
        File.SetUnixFileMode(Path.Combine(game, path), (UnixFileMode)0x1a4);

        using InspectedInstallationState inspection = action == InstallationAction.Repair
            ? this.Inspect(engine, game, action, package)
            : engine.InspectAsync(game, action).GetAwaiter().GetResult();

        inspection.Plan.CanExecute.Should().BeFalse();
        inspection.Plan.Conflicts.Should().Contain(conflict => conflict.Code == expectedConflict);
    }

    [Test]
    public void Execute_RetainedModeDriftAtMutationBoundary_DoesNotAdvanceReceipt()
    {
        string game = this.CreateDirectory();
        Write(game, "StardewValley", "vanilla launcher", 0x1ed);
        using FilePackageAuthority package = this.CreatePackage("launcher one", "runtime one");
        LinuxInstallerEngine installer = new();
        Execute(this.Inspect(installer, game, InstallationAction.Install, package), installer);
        string runtime = Path.Combine(game, "StardewModdingAPI.dll");
        InstallerTransactionExecutor executor = new(faultInjector: new CallbackFaultInjector(() =>
            File.SetUnixFileMode(runtime, (UnixFileMode)0x1ed)
        ));
        LinuxInstallerEngine repair = new(executor);
        using InspectedInstallationState inspection = this.Inspect(repair, game, InstallationAction.Repair, package);

        Action execute = () => repair.ExecuteAsync(inspection, inspection.ConfirmationDigest).GetAwaiter().GetResult();

        execute.Should().Throw<InstallerTransactionException>().Which.Code.Should().Be(TransactionErrorCode.ExistingFileMismatch);
        using InstallerOperationLease lease = InstallerOperationLease.Acquire(game);
        AnchoredCoreStateAuthority state = AnchoredCoreStateAuthority.Inspect(lease);
        state.Pointer!.Action.Should().Be(InstallationAction.Install);
    }

    [Test]
    public void Execute_RetainedModeDriftAfterMutation_DoesNotCommitReceipt()
    {
        string game = this.CreateDirectory();
        Write(game, "StardewValley", "vanilla launcher", 0x1ed);
        using FilePackageAuthority package = this.CreatePackage("launcher one", "runtime one");
        LinuxInstallerEngine installer = new();
        Execute(this.Inspect(installer, game, InstallationAction.Install, package), installer);
        string runtime = Path.Combine(game, "StardewModdingAPI.dll");
        bool changed = false;
        InstallerTransactionExecutor executor = new(faultInjector: new CallbackFaultInjector(
            after: () =>
            {
                if (changed)
                    return;
                File.SetUnixFileMode(runtime, (UnixFileMode)0x1ed);
                changed = true;
            }
        ));
        LinuxInstallerEngine repair = new(executor);
        using InspectedInstallationState inspection = this.Inspect(repair, game, InstallationAction.Repair, package);

        Action execute = () => repair.ExecuteAsync(inspection, inspection.ConfirmationDigest).GetAwaiter().GetResult();

        execute.Should().Throw<InstallerTransactionException>().Which.Code.Should().Be(TransactionErrorCode.ExistingFileMismatch);
        using InstallerOperationLease lease = InstallerOperationLease.Acquire(game);
        AnchoredCoreStateAuthority state = AnchoredCoreStateAuthority.Inspect(lease);
        state.Pointer!.Action.Should().Be(InstallationAction.Install);
    }

    [Test]
    public void RecoveryHistory_AtCapacity_CanBeListedPrunedAndUsedAgain()
    {
        string game = this.CreateDirectory();
        Write(game, "StardewValley", "vanilla launcher", 0x1ed);
        LinuxInstallerEngine engine = new();
        using FilePackageAuthority package = this.CreatePackage("launcher one", "runtime one");
        Execute(this.Inspect(engine, game, InstallationAction.Install, package), engine);
        for (int index = 0; index < 63; index++)
            Execute(engine.InspectAsync(game, InstallationAction.Backup).GetAwaiter().GetResult(), engine);

        RecoveryHistory full = engine.ListRecoveriesAsync(game).GetAwaiter().GetResult();
        full.Generations.Should().HaveCount(64);
        full.Generations[0].IsCurrent.Should().BeTrue();
        full.Generations.Skip(1).Should().OnlyContain(item => !item.IsCurrent);

        engine.PruneRecoveryHistoryAsync(game, 8, full.HeadConfirmationDigest).GetAwaiter().GetResult()
            .Should().Be(56);
        RecoveryHistory retained = engine.ListRecoveriesAsync(game).GetAwaiter().GetResult();
        retained.Generations.Should().HaveCount(8);
        retained.Generations.Select(item => item.GenerationId).Should().Equal(
            full.Generations.Take(8).Select(item => item.GenerationId)
        );

        Execute(engine.InspectAsync(game, InstallationAction.Backup).GetAwaiter().GetResult(), engine);
        engine.ListRecoveriesAsync(game).GetAwaiter().GetResult().Generations.Should().HaveCount(9);
    }

    [Test]
    public void Repair_ApprovedModifiedOwnedFile_IsCapturedAndRollbackRestoresIt()
    {
        string game = this.CreateDirectory();
        Write(game, "StardewValley", "vanilla launcher", 0x1ed);
        LinuxInstallerEngine engine = new();
        using FilePackageAuthority package = this.CreatePackage("launcher one", "runtime one");
        Execute(this.Inspect(engine, game, InstallationAction.Install, package), engine);
        string runtimePath = Path.Combine(game, "StardewModdingAPI.dll");
        File.WriteAllText(runtimePath, "user modified runtime");
        File.SetUnixFileMode(runtimePath, (UnixFileMode)0x1a4);
        using (InspectedInstallationState blocked = this.Inspect(engine, game, InstallationAction.Repair, package))
            blocked.Plan.Conflicts.Should().Contain(conflict => conflict.Code == PlanConflictCode.ModifiedOwnedFile);
        ModifiedFileReplacementApproval approval = new(
            NormalizedRelativePath.Parse("StardewModdingAPI.dll"),
            Hash("user modified runtime"),
            0x1a4
        );

        Execute(this.Inspect(engine, game, InstallationAction.Repair, package, [approval]), engine);
        File.ReadAllText(runtimePath).Should().Be("runtime one");
        using CommittedRecoveryHandle recovery = engine.OpenCurrentRecoveryAsync(game).GetAwaiter().GetResult();
        recovery.Action.Should().Be(InstallationAction.Repair);
        recovery.Snapshot.Entries.Single(entry => entry.Path.Value == "StardewModdingAPI.dll").Backup!.Sha256
            .Should().Be(Hash("user modified runtime"));

        Execute(engine.InspectAsync(game, InstallationAction.Rollback, recovery: recovery).GetAwaiter().GetResult(), engine);
        File.ReadAllText(runtimePath).Should().Be("user modified runtime");
        File.GetUnixFileMode(runtimePath).Should().Be((UnixFileMode)0x1a4);
    }

    private string CreateDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"smapi-materializer-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        this.TemporaryDirectories.Add(path);
        return path;
    }

    private FilePackageAuthority CreatePackage(string launcher, string runtime)
    {
        string root = this.CreateDirectory();
        Write(root, "StardewValley", launcher, 0x1ed);
        Write(root, "StardewModdingAPI.dll", runtime, 0x1a4);
        int alpha = launcher.EndsWith("two", StringComparison.Ordinal) ? 2 : 1;
        PackageManifest manifest = new(
            OwnershipTestData.Release(alpha),
            new[]
            {
                Entry("StardewValley", launcher, 0x1ed, OwnedEntryKind.Launcher),
                Entry("StardewModdingAPI.dll", runtime, 0x1a4, OwnedEntryKind.RuntimeFile)
            }
        );
        return new FilePackageAuthority(manifest, root);
    }

    private InspectedInstallationState Inspect(
        LinuxInstallerEngine engine,
        string game,
        InstallationAction action,
        IVerifiedPackageContentAuthority? package = null,
        IEnumerable<ModifiedFileReplacementApproval>? modifiedFileReplacementApprovals = null
    )
    {
        using InstallerOperationLease lease = InstallerOperationLease.Acquire(game);
        return engine.InspectLocked(lease, action, package, null, modifiedFileReplacementApprovals);
    }

    private static void Execute(InspectedInstallationState inspection, LinuxInstallerEngine engine)
    {
        using (inspection)
        {
            inspection.Plan.CanExecute.Should().BeTrue(string.Join(", ", inspection.Plan.Conflicts.Select(conflict => conflict.Code)));
            engine.ExecuteAsync(inspection, inspection.ConfirmationDigest).GetAwaiter().GetResult().Status
                .Should().Be(TransactionStatus.Committed);
        }
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

        public FilePackageAuthority(PackageManifest manifest, string payloadRoot)
        {
            this.Manifest = manifest;
            this.Payload = new LinuxAnchoredFileSystem(payloadRoot);
        }

        public LinuxAnchoredFile OpenFile(PackageManifestEntry expected)
        {
            if (!this.Manifest.Entries.Contains(expected))
                throw new InvalidOperationException("The requested entry isn't in this test authority.");
            LinuxAnchoredFile file = this.Payload.OpenRegularFileForRead(expected.Path.Value);
            if (
                file.Identity.Size != expected.SizeBytes
                || file.Identity.UnixMode != expected.UnixMode
                || Sha256Digest.Parse(this.Payload.ComputeSha256(file)) != expected.Sha256
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

    private sealed class CallbackFaultInjector : ITransactionFaultInjector
    {
        private readonly Action? Before;
        private readonly Action? After;

        public CallbackFaultInjector(Action? before = null, Action? after = null)
        {
            this.Before = before;
            this.After = after;
        }

        public void BeforeMutation(Guid transactionId, int operationIndex) => this.Before?.Invoke();
        public void AfterMutation(Guid transactionId, int operationIndex) => this.After?.Invoke();
    }
}
