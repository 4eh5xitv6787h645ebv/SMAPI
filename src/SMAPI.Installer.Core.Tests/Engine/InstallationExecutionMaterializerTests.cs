using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Ownership.Persistence;
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
    public void Inspection_ExposesAuthenticatedCurrentResultStateAndRecoveryCapacity()
    {
        string game = this.CreateDirectory();
        Write(game, "StardewValley", "vanilla launcher", 0x1ed);
        LinuxInstallerEngine engine = new();
        using FilePackageAuthority package = this.CreatePackage("launcher one", "runtime one");

        using (InspectedInstallationState install = this.Inspect(engine, game, InstallationAction.Install, package))
        {
            install.CurrentRelease.Should().BeNull();
            install.ExpectedResultRelease.Should().Be(package.Manifest.Release);
            install.ObservedState.Should().Be(ObservedInstallationState.NotInstalled);
            install.RecoveryCapacity.Should().Be(new RecoveryCapacityState(0, 64));
        }
        Execute(this.Inspect(engine, game, InstallationAction.Install, package), engine);

        using (InspectedInstallationState backup = engine.InspectAsync(game, InstallationAction.Backup).GetAwaiter().GetResult())
        {
            backup.CurrentRelease.Should().Be(package.Manifest.Release);
            backup.ExpectedResultRelease.Should().Be(package.Manifest.Release);
            backup.ObservedState.Should().Be(ObservedInstallationState.KnownUnmodified);
            backup.RecoveryCapacity.Should().Be(new RecoveryCapacityState(1, 64));
        }

        Write(game, "StardewModdingAPI.dll", "locally modified", 0x1a4);
        using InspectedInstallationState repair = this.Inspect(engine, game, InstallationAction.Repair, package);
        repair.ObservedState.Should().Be(ObservedInstallationState.KnownModified);
        repair.CurrentRelease.Should().Be(package.Manifest.Release);
        repair.ExpectedResultRelease.Should().Be(package.Manifest.Release);
    }

    [Test]
    public void RecoveryPresentation_ExposesAuthenticatedRestoreRelease()
    {
        string game = this.CreateDirectory();
        Write(game, "StardewValley", "vanilla launcher", 0x1ed);
        LinuxInstallerEngine engine = new();
        using FilePackageAuthority first = this.CreatePackage("launcher one", "runtime one");
        using FilePackageAuthority second = this.CreatePackage("launcher two", "runtime two");
        Execute(this.Inspect(engine, game, InstallationAction.Install, first), engine);
        Execute(this.Inspect(engine, game, InstallationAction.Update, second), engine);

        using CommittedRecoveryHandle recovery = engine.OpenCurrentRecoveryAsync(game).GetAwaiter().GetResult();
        recovery.RestoreRelease.Should().Be(first.Manifest.Release);
        RecoveryHistory history = engine.ListRecoveriesAsync(game).GetAwaiter().GetResult();
        history.Generations[0].RestoreRelease.Should().Be(first.Manifest.Release);
        history.Generations[1].RestoreRelease.Should().BeNull();

        using InspectedInstallationState rollback = engine.InspectAsync(
            game,
            InstallationAction.Rollback,
            recovery: recovery
        ).GetAwaiter().GetResult();
        rollback.CurrentRelease.Should().Be(second.Manifest.Release);
        rollback.ExpectedResultRelease.Should().Be(first.Manifest.Release);
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
    public void Inspect_FullRecoveryStoreBlocksBeforeConfirmationAndGameMutation()
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
        inspection.Plan.CanExecute.Should().BeFalse();
        inspection.Plan.Conflicts.Should().ContainSingle(conflict => conflict.Code == PlanConflictCode.RecoveryCapacityReached);

        Action execute = () => engine.ExecuteAsync(inspection, inspection.ConfirmationDigest).GetAwaiter().GetResult();

        execute.Should().Throw<ExecutionCompilationException>().Which.Error.Should().Be(ExecutionCompilationError.NonExecutablePlan);
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
        using (InspectedInstallationState blocked = engine.InspectAsync(game, InstallationAction.Backup).GetAwaiter().GetResult())
        {
            blocked.RecoveryCapacity.Should().Be(new RecoveryCapacityState(64, 64));
            blocked.Plan.CanExecute.Should().BeFalse();
            blocked.Plan.Conflicts.Should().ContainSingle(conflict => conflict.Code == PlanConflictCode.RecoveryCapacityReached);
        }

        RecoveryPrunePlan prune = engine.InspectRecoveryPruneAsync(game, 8).GetAwaiter().GetResult();
        prune.OrderedCatalogGenerationIds.Should().Equal(full.Generations.Select(item => item.GenerationId));
        prune.RetainedGenerationIds.Should().Equal(full.Generations.Take(8).Select(item => item.GenerationId));
        prune.RemovedGenerationIds.Should().Equal(full.Generations.Skip(8).Select(item => item.GenerationId));
        engine.ExecuteRecoveryPruneAsync(prune, prune.ConfirmationDigest).GetAwaiter().GetResult().Should().Be(56);
        RecoveryHistory retained = engine.ListRecoveriesAsync(game).GetAwaiter().GetResult();
        retained.Generations.Should().HaveCount(8);
        retained.Generations.Select(item => item.GenerationId).Should().Equal(
            full.Generations.Take(8).Select(item => item.GenerationId)
        );

        using (InspectedInstallationState available = engine.InspectAsync(game, InstallationAction.Backup).GetAwaiter().GetResult())
            available.RecoveryCapacity.Should().Be(new RecoveryCapacityState(8, 64));
        Execute(engine.InspectAsync(game, InstallationAction.Backup).GetAwaiter().GetResult(), engine);
        engine.ListRecoveriesAsync(game).GetAwaiter().GetResult().Generations.Should().HaveCount(9);
    }

    [Test]
    public void EngineProgress_CoversInspectionPreparationAndRecoveryAndIsFaultIsolated()
    {
        string game = this.CreateDirectory();
        Write(game, "StardewValley", "vanilla launcher", 0x1ed);
        RecordingProgress progress = new();
        LinuxInstallerEngine engine = new(progress);
        using FilePackageAuthority package = this.CreatePackage("launcher one", "runtime one");
        Execute(this.Inspect(engine, game, InstallationAction.Install, package), engine);
        Execute(engine.InspectAsync(game, InstallationAction.Backup).GetAwaiter().GetResult(), engine);
        _ = engine.ListRecoveriesAsync(game).GetAwaiter().GetResult();

        progress.Items.Should().Contain(item => item.Stage == TransactionStage.Inspecting);
        progress.Items.Should().Contain(item => item.Stage == TransactionStage.VerifyingRecovery && item.TotalOperations == null);
        progress.Items.Should().Contain(item => item.Stage == TransactionStage.PreparingRecovery && item.TotalOperations == null);
        progress.Items.Should().Contain(item => item.Stage == TransactionStage.PreparingPayload && item.TotalOperations == null);
        progress.Items.Should().OnlyContain(item => item.CompletedOperations >= 0);

        LinuxInstallerEngine throwing = new(new ThrowingProgress());
        using InspectedInstallationState retry = throwing.InspectAsync(game, InstallationAction.Backup).GetAwaiter().GetResult();
        Action execute = () => throwing.ExecuteAsync(retry, retry.ConfirmationDigest).GetAwaiter().GetResult();
        execute.Should().NotThrow();
        Action list = () => throwing.ListRecoveriesAsync(game).GetAwaiter().GetResult();
        list.Should().NotThrow();
    }

    [Test]
    public void RecoveryPrune_StaleReplayedAndMismatchedConfirmationsAreRejected()
    {
        (string game, LinuxInstallerEngine engine, FilePackageAuthority package) = this.CreateRecoveryHistory(4);
        using (package)
        {
            RecoveryPrunePlan stale = engine.InspectRecoveryPruneAsync(game, 2).GetAwaiter().GetResult();
            Action mismatch = () => engine.ExecuteRecoveryPruneAsync(stale, Hash("wrong confirmation")).GetAwaiter().GetResult();
            mismatch.Should().Throw<InstallerTransactionException>().Which.Code.Should().Be(TransactionErrorCode.PathChanged);
            Execute(engine.InspectAsync(game, InstallationAction.Backup).GetAwaiter().GetResult(), engine);

            Action changed = () => engine.ExecuteRecoveryPruneAsync(stale, stale.ConfirmationDigest).GetAwaiter().GetResult();
            changed.Should().Throw<InstallerTransactionException>().Which.Code.Should().Be(TransactionErrorCode.PathChanged);

            RecoveryPrunePlan exact = engine.InspectRecoveryPruneAsync(game, 2).GetAwaiter().GetResult();
            engine.ExecuteRecoveryPruneAsync(exact, exact.ConfirmationDigest).GetAwaiter().GetResult().Should().Be(3);
            Action replay = () => engine.ExecuteRecoveryPruneAsync(exact, exact.ConfirmationDigest).GetAwaiter().GetResult();
            replay.Should().Throw<InstallerTransactionException>().Which.Code.Should().Be(TransactionErrorCode.PathChanged);

            RecoveryPrunePlan noOp = engine.InspectRecoveryPruneAsync(game, 2).GetAwaiter().GetResult();
            noOp.RemovedGenerationIds.Should().BeEmpty();
            noOp.CleanupGenerationIds.Should().BeEmpty();
            Action noOpExecution = () => engine.ExecuteRecoveryPruneAsync(noOp, noOp.ConfirmationDigest).GetAwaiter().GetResult();
            noOpExecution.Should().Throw<InstallerTransactionException>().Which.Code.Should().Be(TransactionErrorCode.InvalidPlan);
            engine.InspectRecoveryPruneAsync(game, 2).GetAwaiter().GetResult().ConfirmationDigest.Should().Be(noOp.ConfirmationDigest);
        }
    }

    [Test]
    public void RecoveryHistory_ExternalTailDeletionWithoutCutoffIsDetected()
    {
        (string game, LinuxInstallerEngine engine, FilePackageAuthority package) = this.CreateRecoveryHistory(4);
        using (package)
        {
            RecoveryHistory history = engine.ListRecoveriesAsync(game).GetAwaiter().GetResult();
            Guid oldest = history.Generations[^1].GenerationId;
            Directory.Delete(Path.Combine(game, ".smapi-installer", "recovery", "generations", oldest.ToString("N")), recursive: true);

            Action list = () => engine.ListRecoveriesAsync(game).GetAwaiter().GetResult();

            list.Should().Throw<OwnershipDocumentException>().WithMessage("*retained*missing*");
        }
    }

    [Test]
    public void RecoveryHistory_TamperedRetentionBoundaryIsRejected()
    {
        (string game, LinuxInstallerEngine engine, FilePackageAuthority package) = this.CreateRecoveryHistory(4);
        using (package)
        {
            RecoveryPrunePlan plan = engine.InspectRecoveryPruneAsync(game, 2).GetAwaiter().GetResult();
            engine.ExecuteRecoveryPruneAsync(plan, plan.ConfirmationDigest).GetAwaiter().GetResult();
            CommittedRecoveryPointer pointer = CanonicalRecoveryPointerDocument.Parse(File.ReadAllBytes(
                Path.Combine(game, ".smapi-installer", "recovery", "current.json")
            ));
            string retention = Path.Combine(
                game,
                ".smapi-installer",
                "recovery",
                "retention",
                $"{pointer.RetentionSha256!.Value}.json"
            );
            File.WriteAllText(retention, "{}");
            File.SetUnixFileMode(retention, (UnixFileMode)0x180);

            Action list = () => engine.ListRecoveriesAsync(game).GetAwaiter().GetResult();

            list.Should().Throw<OwnershipDocumentException>();
        }
    }

    [TestCase(0, 4)]
    [TestCase(1, 4)]
    [TestCase(2, 4)]
    [TestCase(3, 2)]
    [TestCase(4, 2)]
    [TestCase(5, 2)]
    public void RecoveryPrune_InterruptionHasOneAuthenticatedVisibilityBoundaryAndResumableCleanup(
        int boundaryValue,
        int visibleAfterFault
    )
    {
        RecoveryPruneBoundary boundary = (RecoveryPruneBoundary)boundaryValue;
        (string game, LinuxInstallerEngine normal, FilePackageAuthority package) = this.CreateRecoveryHistory(4);
        using (package)
        {
            RecoveryPrunePlan plan = normal.InspectRecoveryPruneAsync(game, 2).GetAwaiter().GetResult();
            LinuxInstallerEngine faulting = new(
                new InstallerTransactionExecutor(),
                new OneShotRecoveryPruneFaultInjector(boundary)
            );

            Action execute = () => faulting.ExecuteRecoveryPruneAsync(plan, plan.ConfirmationDigest).GetAwaiter().GetResult();

            execute.Should().Throw<SimulatedProcessTerminationException>();
            normal.ListRecoveriesAsync(game).GetAwaiter().GetResult().Generations.Should().HaveCount(visibleAfterFault);
            RecoveryPrunePlan resume = normal.InspectRecoveryPruneAsync(game, 2).GetAwaiter().GetResult();
            normal.ExecuteRecoveryPruneAsync(resume, resume.ConfirmationDigest).GetAwaiter().GetResult();
            RecoveryHistory retained = normal.ListRecoveriesAsync(game).GetAwaiter().GetResult();
            retained.Generations.Should().HaveCount(2);
            Directory.EnumerateDirectories(Path.Combine(game, ".smapi-installer", "recovery", "generations"))
                .Should().HaveCount(2);
        }
    }

    [Test]
    public void RecoveryPrune_CancelledBeforeExecutionLeavesExactStateUnchanged()
    {
        (string game, LinuxInstallerEngine engine, FilePackageAuthority package) = this.CreateRecoveryHistory(4);
        using (package)
        {
            RecoveryPrunePlan before = engine.InspectRecoveryPruneAsync(game, 2).GetAwaiter().GetResult();
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();

            Action execute = () => engine.ExecuteRecoveryPruneAsync(before, before.ConfirmationDigest, cancellation.Token).GetAwaiter().GetResult();

            execute.Should().Throw<OperationCanceledException>();
            RecoveryPrunePlan after = engine.InspectRecoveryPruneAsync(game, 2).GetAwaiter().GetResult();
            after.ConfirmationDigest.Should().Be(before.ConfirmationDigest);
            engine.ListRecoveriesAsync(game).GetAwaiter().GetResult().Generations.Should().HaveCount(4);
        }
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
        using InspectedInstallationState blocked = this.Inspect(engine, game, InstallationAction.Repair, package);
        blocked.Plan.Conflicts.Should().Contain(conflict => conflict.Code == PlanConflictCode.ModifiedOwnedFile);
        ModifiedFileReplacementCandidate candidate = blocked.ModifiedFileReplacementCandidates.Should().ContainSingle().Subject;
        candidate.Path.Value.Should().Be("StardewModdingAPI.dll");
        candidate.ObservedSha256.Should().Be(Hash("user modified runtime"));
        candidate.ObservedSizeBytes.Should().Be(21);
        candidate.ObservedUnixMode.Should().Be(0x1a4);
        candidate.ObservedFileType.Should().Be(RecoveryFileType.RegularFile);
        candidate.Reason.Should().Be(FileReplacementCandidateReason.ModifiedReceiptOwned);
        candidate.Disposition.Should().Be(FileReplacementCandidateDisposition.Replace);
        candidate.ProposedResultSha256.Should().Be(Hash("runtime one"));

        Execute(engine.ApproveRepairAsync(blocked, [candidate]).GetAwaiter().GetResult(), engine);
        File.ReadAllText(runtimePath).Should().Be("runtime one");
        using CommittedRecoveryHandle recovery = engine.OpenCurrentRecoveryAsync(game).GetAwaiter().GetResult();
        recovery.Action.Should().Be(InstallationAction.Repair);
        recovery.Snapshot.Entries.Single(entry => entry.Path.Value == "StardewModdingAPI.dll").Backup!.Sha256
            .Should().Be(Hash("user modified runtime"));

        Execute(engine.InspectAsync(game, InstallationAction.Rollback, recovery: recovery).GetAwaiter().GetResult(), engine);
        File.ReadAllText(runtimePath).Should().Be("user modified runtime");
        File.GetUnixFileMode(runtimePath).Should().Be((UnixFileMode)0x1a4);
    }

    [Test]
    public void RepairCandidates_AreInspectionBoundDeterministicAndSupportPartialSelection()
    {
        string game = this.CreateDirectory();
        Write(game, "StardewValley", "vanilla launcher", 0x1ed);
        LinuxInstallerEngine engine = new();
        using FilePackageAuthority package = this.CreatePackage("launcher one", "runtime one");
        Execute(this.Inspect(engine, game, InstallationAction.Install, package), engine);
        Write(game, "StardewModdingAPI.dll", "modified runtime", 0x180);
        Write(game, "StardewValley", "modified launcher", 0x1c0);
        Write(game, "unrelated-user-file.txt", "preserve me", 0x180);
        using InspectedInstallationState blocked = this.Inspect(engine, game, InstallationAction.Repair, package);

        blocked.ModifiedFileReplacementCandidates.Select(candidate => candidate.Path.Value).Should().Equal(
            "StardewModdingAPI.dll",
            "StardewValley"
        );
        ModifiedFileReplacementCandidate runtime = blocked.ModifiedFileReplacementCandidates[0];
        ModifiedFileReplacementCandidate launcher = blocked.ModifiedFileReplacementCandidates[1];
        Action duplicate = () => engine.ApproveRepairAsync(blocked, [runtime, runtime]).GetAwaiter().GetResult();
        duplicate.Should().Throw<ArgumentException>();

        using InspectedInstallationState partial = engine.ApproveRepairAsync(blocked, [runtime]).GetAwaiter().GetResult();
        partial.Plan.CanExecute.Should().BeFalse();
        partial.ModifiedFileReplacementCandidates.Should().ContainSingle()
            .Which.Path.Should().Be(launcher.Path);
        partial.Plan.Conflicts.Should().ContainSingle(conflict => conflict.Code == PlanConflictCode.ModifiedInstalledLauncher);
        Action foreign = () => engine.ApproveRepairAsync(partial, [launcher]).GetAwaiter().GetResult();
        foreign.Should().Throw<ExecutionCompilationException>().Which.Error.Should().Be(ExecutionCompilationError.StalePlan);

        using InspectedInstallationState approved = engine.ApproveRepairAsync(
            partial,
            [partial.ModifiedFileReplacementCandidates.Single()]
        ).GetAwaiter().GetResult();
        approved.Plan.CanExecute.Should().BeTrue();
        Execute(approved, engine);
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("runtime one");
        File.ReadAllText(Path.Combine(game, "StardewValley")).Should().Be("launcher one");
        File.ReadAllText(Path.Combine(game, "unrelated-user-file.txt")).Should().Be("preserve me");
    }

    [Test]
    public void RepairCandidates_RejectDisposedSourceAndFullIdentityDrift()
    {
        string game = this.CreateDirectory();
        Write(game, "StardewValley", "vanilla launcher", 0x1ed);
        LinuxInstallerEngine engine = new();
        using FilePackageAuthority package = this.CreatePackage("launcher one", "runtime one");
        Execute(this.Inspect(engine, game, InstallationAction.Install, package), engine);
        string runtimePath = Path.Combine(game, "StardewModdingAPI.dll");
        Write(game, "StardewModdingAPI.dll", "modified runtime", 0x180);
        InspectedInstallationState disposed = this.Inspect(engine, game, InstallationAction.Repair, package);
        ModifiedFileReplacementCandidate disposedCandidate = disposed.ModifiedFileReplacementCandidates.Single();
        disposed.Dispose();

        Action useDisposed = () => engine.ApproveRepairAsync(disposed, [disposedCandidate]).GetAwaiter().GetResult();
        useDisposed.Should().Throw<ObjectDisposedException>();

        using InspectedInstallationState drifted = this.Inspect(engine, game, InstallationAction.Repair, package);
        ModifiedFileReplacementCandidate driftedCandidate = drifted.ModifiedFileReplacementCandidates.Single();
        File.SetUnixFileMode(runtimePath, (UnixFileMode)0x1a4);
        Action approveDrifted = () => engine.ApproveRepairAsync(drifted, [driftedCandidate]).GetAwaiter().GetResult();
        approveDrifted.Should().Throw<ExecutionCompilationException>().Which.Error.Should().Be(ExecutionCompilationError.StalePlan);
        File.ReadAllText(runtimePath).Should().Be("modified runtime");
        using InstallerOperationLease lease = InstallerOperationLease.Acquire(game);
        AnchoredCoreStateAuthority state = AnchoredCoreStateAuthority.Inspect(lease);
        state.Pointer!.Action.Should().Be(InstallationAction.Install);
    }

    [Test]
    public void RepairCandidates_RejectForeignRootPackageAndOperationGeneration()
    {
        string firstGame = this.CreateDirectory();
        string secondGame = this.CreateDirectory();
        Write(firstGame, "StardewValley", "first vanilla", 0x1ed);
        Write(secondGame, "StardewValley", "second vanilla", 0x1ed);
        LinuxInstallerEngine engine = new();
        using FilePackageAuthority firstPackage = this.CreatePackage("launcher one", "runtime one");
        using FilePackageAuthority secondPackage = this.CreatePackage("launcher two", "runtime two");
        Execute(this.Inspect(engine, firstGame, InstallationAction.Install, firstPackage), engine);
        Execute(this.Inspect(engine, secondGame, InstallationAction.Install, secondPackage), engine);
        Write(firstGame, "StardewModdingAPI.dll", "first modified", 0x180);
        Write(secondGame, "StardewModdingAPI.dll", "second modified", 0x180);
        using InspectedInstallationState first = this.Inspect(engine, firstGame, InstallationAction.Repair, firstPackage);
        using InspectedInstallationState second = this.Inspect(engine, secondGame, InstallationAction.Repair, secondPackage);
        ModifiedFileReplacementCandidate firstCandidate = first.ModifiedFileReplacementCandidates.Single();

        Action foreign = () => engine.ApproveRepairAsync(second, [firstCandidate]).GetAwaiter().GetResult();
        foreign.Should().Throw<ExecutionCompilationException>().Which.Error.Should().Be(ExecutionCompilationError.StalePlan);

        using (InstallerOperationLease lease = InstallerOperationLease.Acquire(firstGame))
            lease.ReserveNextGeneration(lease.Generation);
        Action staleGeneration = () => engine.ApproveRepairAsync(first, [firstCandidate]).GetAwaiter().GetResult();
        staleGeneration.Should().Throw<ExecutionCompilationException>().Which.Error.Should().Be(ExecutionCompilationError.StalePlan);
        File.ReadAllText(Path.Combine(firstGame, "StardewModdingAPI.dll")).Should().Be("first modified");
        File.ReadAllText(Path.Combine(secondGame, "StardewModdingAPI.dll")).Should().Be("second modified");
    }

    [Test]
    public void RepairCandidates_AmbiguousLauncherBackupIsNeverApprovable()
    {
        string game = this.CreateDirectory();
        Write(game, "StardewValley", "vanilla launcher", 0x1ed);
        LinuxInstallerEngine engine = new();
        using FilePackageAuthority package = this.CreatePackage("launcher one", "runtime one");
        Execute(this.Inspect(engine, game, InstallationAction.Install, package), engine);
        Write(game, "StardewValley-original", "unknown backup", 0x1ed);

        using InspectedInstallationState blocked = this.Inspect(engine, game, InstallationAction.Repair, package);

        blocked.Plan.Conflicts.Should().Contain(conflict => conflict.Code == PlanConflictCode.AmbiguousLauncherBackup);
        blocked.ModifiedFileReplacementCandidates.Should().BeEmpty();
    }

    [Test]
    public void RecoveryHandle_IsBorrowedAcrossInspectionAndExecution()
    {
        string game = this.CreateDirectory();
        Write(game, "StardewValley", "vanilla launcher", 0x1ed);
        LinuxInstallerEngine engine = new();
        using FilePackageAuthority package = this.CreatePackage("launcher one", "runtime one");
        Execute(this.Inspect(engine, game, InstallationAction.Install, package), engine);
        CommittedRecoveryHandle recovery = engine.OpenCurrentRecoveryAsync(game).GetAwaiter().GetResult();
        InspectedInstallationState first = engine.InspectAsync(game, InstallationAction.Rollback, recovery: recovery).GetAwaiter().GetResult();
        first.Dispose();
        using InspectedInstallationState retry = engine.InspectAsync(game, InstallationAction.Rollback, recovery: recovery).GetAwaiter().GetResult();
        retry.Plan.CanExecute.Should().BeTrue();

        engine.ExecuteAsync(retry, retry.ConfirmationDigest).GetAwaiter().GetResult().Status.Should().Be(TransactionStatus.Committed);
        Action retained = () => ((ICommittedRecoveryContentAuthority)recovery).AssertUsable();
        retained.Should().NotThrow();
        recovery.Dispose();
    }

    [Test]
    public void RecoveryHandle_DisposedBeforeExecutionRejectsWithoutMutation()
    {
        string game = this.CreateDirectory();
        Write(game, "StardewValley", "vanilla launcher", 0x1ed);
        LinuxInstallerEngine engine = new();
        using FilePackageAuthority package = this.CreatePackage("launcher one", "runtime one");
        Execute(this.Inspect(engine, game, InstallationAction.Install, package), engine);
        CommittedRecoveryHandle recovery = engine.OpenCurrentRecoveryAsync(game).GetAwaiter().GetResult();
        using InspectedInstallationState rollback = engine.InspectAsync(game, InstallationAction.Rollback, recovery: recovery).GetAwaiter().GetResult();
        recovery.Dispose();

        Action execute = () => engine.ExecuteAsync(rollback, rollback.ConfirmationDigest).GetAwaiter().GetResult();

        execute.Should().Throw<ObjectDisposedException>();
        File.ReadAllText(Path.Combine(game, "StardewValley")).Should().Be("launcher one");
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("runtime one");
    }

    private string CreateDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"smapi-materializer-tests-{Guid.NewGuid():N}");
        LinuxGameTestFolder.MakeValid(path);
        this.TemporaryDirectories.Add(path);
        return path;
    }

    private (string Game, LinuxInstallerEngine Engine, FilePackageAuthority Package) CreateRecoveryHistory(int generationCount)
    {
        string game = this.CreateDirectory();
        Write(game, "StardewValley", "vanilla launcher", 0x1ed);
        LinuxInstallerEngine engine = new();
        FilePackageAuthority package = this.CreatePackage("launcher one", "runtime one");
        Execute(this.Inspect(engine, game, InstallationAction.Install, package), engine);
        for (int index = 1; index < generationCount; index++)
            Execute(engine.InspectAsync(game, InstallationAction.Backup).GetAwaiter().GetResult(), engine);
        return (game, engine, package);
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
        IVerifiedPackageContentAuthority? package = null
    )
    {
        using InstallerOperationLease lease = InstallerOperationLease.Acquire(game);
        return engine.InspectLocked(lease, action, package, null);
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

    private sealed class RecordingProgress : ITransactionProgressSink
    {
        public List<TransactionProgress> Items { get; } = new();
        public void Report(TransactionProgress progress) => this.Items.Add(progress);
    }

    private sealed class ThrowingProgress : ITransactionProgressSink
    {
        public void Report(TransactionProgress progress) => throw new InvalidOperationException("observer failure");
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

    private sealed class OneShotRecoveryPruneFaultInjector : IRecoveryPruneFaultInjector
    {
        private readonly RecoveryPruneBoundary Boundary;
        private bool Triggered;

        public OneShotRecoveryPruneFaultInjector(RecoveryPruneBoundary boundary)
        {
            this.Boundary = boundary;
        }

        public void AtBoundary(RecoveryPruneBoundary boundary, Guid? generationId = null)
        {
            if (!this.Triggered && boundary == this.Boundary)
            {
                this.Triggered = true;
                throw new SimulatedProcessTerminationException($"Simulated recovery-prune interruption at {boundary}.");
            }
        }
    }
}
