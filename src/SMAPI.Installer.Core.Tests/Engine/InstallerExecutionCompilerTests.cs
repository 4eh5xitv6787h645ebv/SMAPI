using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Ownership.Persistence;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Security;
using StardewModdingAPI.Installer.Core.Tests.Ownership;

namespace StardewModdingAPI.Installer.Core.Tests.Engine;

[TestFixture]
public class InstallerExecutionCompilerTests
{
    private static readonly Guid TransactionId = Guid.Parse("12345678-1234-1234-1234-123456789abc");
    private static readonly GameRootIdentity GameRoot = new("/game", 8, 1, 1234);
    private const ulong OperationGeneration = 7;
    private readonly InstallationPlanner Planner = new();
    private readonly InstallerExecutionCompiler Compiler = new();

    [Test]
    public void FreshInstall_UsesCurrentLauncherOnlyForBackupAndVerifiedPackageForWrites()
    {
        PackageManifest manifest = OwnershipTestData.Manifest(
            otherEntries: [OwnershipTestData.Entry("StardewModdingAPI", '2', OwnedEntryKind.RuntimeFile, mode: 493)]
        );
        CurrentFile vanilla = new(OwnershipTestData.Path("StardewValley"), OwnershipTestData.Digest('f'), 493);
        InstallationPlanningRequest request = new(
            InstallationAction.Install,
            InstallationInventory.Create(manifest, null, [vanilla]),
            LauncherState.Assess(vanilla.Sha256, null, null),
            targetManifest: manifest
        );

        (InstallationPlan plan, InstallationExecutionPreparation preparation) = this.Compile(request);

        AssertLossless(plan, preparation);
        preparation.TransactionDestinations.Should().HaveCount(3);
        FilePreparationInstruction launcherBackup = preparation.Instructions.Single(item => item.Path.Value == "StardewValley-original");
        launcherBackup.PlanKind.Should().Be(PlanOperationKind.Create);
        launcherBackup.Source.Should().BeOfType<CurrentGameLauncherSource>().Which.Role.Should().Be(CurrentGameLauncherRole.CurrentLauncher);
        launcherBackup.Source.As<CurrentGameLauncherSource>().SourcePath.Value.Should().Be("StardewValley");
        launcherBackup.Source.As<CurrentGameLauncherSource>().Sha256.Should().Be(vanilla.Sha256);

        preparation.Instructions.Single(item => item.Path.Value == "StardewValley").Source.Should().BeOfType<VerifiedPackageFileSource>();
        preparation.Instructions.Single(item => item.Path.Value == "StardewModdingAPI").Source.Should().BeOfType<VerifiedPackageFileSource>();
        preparation.Receipt.Kind.Should().Be(ReceiptPreparationKind.WriteAtomically);
        preparation.Receipt.ExpectedExistingReceiptSha256.Should().BeNull();
        GeneratedCanonicalReceiptSource generated = preparation.Receipt.Source.Should().BeOfType<GeneratedCanonicalReceiptSource>().Subject;
        generated.Receipt.TransactionId.Should().Be(TransactionId.ToString("N"));
        generated.Receipt.ManifestSha256.Should().Be(manifest.GetCanonicalDigest());
        generated.Receipt.Launcher.OriginalLauncherSha256.Should().Be(vanilla.Sha256);
        generated.GetCanonicalBytes().Should().Equal(System.Text.Encoding.UTF8.GetBytes(generated.Receipt.ToCanonicalJson()));

        preparation.RecoverySnapshot.Should().NotBeNull();
        RecoverySnapshotPreparation recovery = preparation.RecoverySnapshot!;
        recovery.SnapshotSha256.Should().Be(Sha256Digest.Hash(recovery.GetCanonicalSnapshotBytes()));
        recovery.Snapshot.PreviousReceiptSha256.Should().BeNull();
        recovery.Snapshot.ExpectedCurrentReceiptSha256.Should().Be(generated.Sha256);
        recovery.PathBindings.Select(binding => binding.Path.Value).Should().BeEquivalentTo(
            "StardewValley",
            "StardewValley-original",
            "StardewModdingAPI"
        );
        recovery.PathBindings.Single(binding => binding.Path.Value == "StardewValley-original").OwnedKind
            .Should().Be(OwnedEntryKind.RecoveryLauncherBackup);
        RollbackSnapshotEntry launcherRoundTrip = recovery.Snapshot.Entries.Single(entry => entry.Path.Value == "StardewValley");
        launcherRoundTrip.Kind.Should().Be(RollbackEntryKind.Restore);
        launcherRoundTrip.Backup.Should().Be(Identity('f', mode: 493));
        RollbackSnapshotEntry backupRoundTrip = recovery.Snapshot.Entries.Single(entry => entry.Path.Value == "StardewValley-original");
        backupRoundTrip.Kind.Should().Be(RollbackEntryKind.Remove);
        backupRoundTrip.ExpectedCurrent.Should().Be(Identity('f', mode: 493));
    }

    [Test]
    public void Update_MapsCreatesReplacesRemovesAndRetainsWithoutFrontendSourceChoices()
    {
        PackageManifest installedManifest = OwnershipTestData.Manifest(
            otherEntries:
            [
                OwnershipTestData.Entry("StardewModdingAPI", '2', OwnedEntryKind.RuntimeFile, mode: 493),
                OwnershipTestData.Entry("smapi-internal/old.dll", '3', OwnedEntryKind.InternalFile)
            ]
        );
        InstallationReceipt installedReceipt = OwnershipTestData.Receipt(installedManifest);
        PackageManifest target = OwnershipTestData.Manifest(
            OwnershipTestData.Release(alpha: 2, packageHash: 'e'),
            launcherDigest: '4',
            OwnershipTestData.Entry("StardewModdingAPI", '5', OwnedEntryKind.RuntimeFile, mode: 493),
            OwnershipTestData.Entry("smapi-internal/new.dll", '6', OwnedEntryKind.InternalFile)
        );
        CurrentFile[] current = installedManifest.Entries.Select(entry => OwnershipTestData.Current(entry)).ToArray();
        InstallationPlanningRequest request = new(
            InstallationAction.Update,
            InstallationInventory.Create(target, installedReceipt, current),
            LauncherState.Assess(
                installedReceipt.Launcher.InstalledLauncherSha256,
                installedReceipt.Launcher.OriginalLauncherSha256,
                installedReceipt.Launcher
            ),
            targetManifest: target,
            installedReceipt: installedReceipt
        );

        (InstallationPlan plan, InstallationExecutionPreparation preparation) = this.Compile(request);

        AssertLossless(plan, preparation);
        preparation.Instructions.Where(item => item.PlanKind is PlanOperationKind.Create or PlanOperationKind.Replace)
            .Should().OnlyContain(item => item.Source is VerifiedPackageFileSource);
        preparation.Instructions.Should().ContainSingle(item => item.Path.Value == "smapi-internal/old.dll" && item.Kind == PreparationInstructionKind.RemoveTransactionDestination);
        preparation.Instructions.Should().ContainSingle(item => item.Path.Value == "smapi-internal/new.dll" && item.Kind == PreparationInstructionKind.WriteTransactionDestination);
        preparation.Receipt.Kind.Should().Be(ReceiptPreparationKind.WriteAtomically);
        preparation.Receipt.ExpectedExistingReceiptSha256.Should().Be(installedReceipt.GetCanonicalDigest());
        preparation.Receipt.Source.As<GeneratedCanonicalReceiptSource>().Receipt.Release.Should().Be(target.Release);
        preparation.RecoverySnapshot.Should().NotBeNull();
        RecoverySnapshotPreparation recovery = preparation.RecoverySnapshot!;
        recovery.Snapshot.Entries.Select(entry => entry.Path.Value).Should().BeEquivalentTo(
            "StardewValley",
            "StardewModdingAPI",
            "smapi-internal/old.dll",
            "smapi-internal/new.dll"
        );
        recovery.PathBindings.Should().ContainSingle(binding => binding.Path.Value == "StardewValley-original" && !binding.RequiresContentCapture);
        recovery.GetPreviousReceiptBytes().Should().Equal(System.Text.Encoding.UTF8.GetBytes(installedReceipt.ToCanonicalJson()));
    }

    [Test]
    public void Repair_UsesVerifiedPackageForMissingFilesAndRegeneratesBoundReceipt()
    {
        PackageManifest manifest = OwnershipTestData.Manifest(
            otherEntries: [OwnershipTestData.Entry("StardewModdingAPI", '2', OwnedEntryKind.RuntimeFile, mode: 493)]
        );
        InstallationReceipt receipt = OwnershipTestData.Receipt(manifest);
        PackageManifestEntry launcher = manifest.Entries.Single(entry => entry.Kind == OwnedEntryKind.Launcher);
        InstallationPlanningRequest request = new(
            InstallationAction.Repair,
            InstallationInventory.Create(manifest, receipt, [OwnershipTestData.Current(launcher)]),
            LauncherState.Assess(receipt.Launcher.InstalledLauncherSha256, receipt.Launcher.OriginalLauncherSha256, receipt.Launcher),
            targetManifest: manifest,
            installedReceipt: receipt
        );

        (InstallationPlan plan, InstallationExecutionPreparation preparation) = this.Compile(request);

        AssertLossless(plan, preparation);
        FilePreparationInstruction runtime = preparation.Instructions.Single(item => item.Path.Value == "StardewModdingAPI");
        runtime.PlanKind.Should().Be(PlanOperationKind.Create);
        runtime.Source.Should().BeOfType<VerifiedPackageFileSource>();
        preparation.Instructions.Single(item => item.Path.Value == "StardewValley").Kind.Should().Be(PreparationInstructionKind.VerifyUnchanged);
        preparation.Receipt.Kind.Should().Be(ReceiptPreparationKind.WriteAtomically);
        preparation.Receipt.ExpectedExistingReceiptSha256.Should().Be(receipt.GetCanonicalDigest());
        preparation.Receipt.Source.As<GeneratedCanonicalReceiptSource>().Receipt.ManifestSha256.Should().Be(manifest.GetCanonicalDigest());
        preparation.RecoverySnapshot.Should().NotBeNull();
        preparation.RecoverySnapshot!.Snapshot.Entries.Should().ContainSingle(entry => entry.Path.Value == "StardewModdingAPI");
    }

    [Test]
    public void Uninstall_CoreSelectsOriginalLauncherRestoreAndBackupRemovalThenRemovesReceipt()
    {
        PackageManifest manifest = OwnershipTestData.Manifest(
            otherEntries: [OwnershipTestData.Entry("StardewModdingAPI", '2', OwnedEntryKind.RuntimeFile, mode: 493)]
        );
        InstallationReceipt receipt = OwnershipTestData.Receipt(manifest);
        PackageManifestEntry runtime = manifest.Entries.Single(entry => entry.Kind == OwnedEntryKind.RuntimeFile);
        InstallationPlanningRequest request = new(
            InstallationAction.Uninstall,
            InstallationInventory.Create(null, receipt, [OwnershipTestData.Current(runtime)]),
            LauncherState.Assess(receipt.Launcher.InstalledLauncherSha256, receipt.Launcher.OriginalLauncherSha256, receipt.Launcher),
            installedReceipt: receipt
        );

        (InstallationPlan plan, InstallationExecutionPreparation preparation) = this.Compile(request);

        AssertLossless(plan, preparation);
        FilePreparationInstruction restore = preparation.Instructions.Single(item => item.Path.Value == "StardewValley");
        restore.PlanKind.Should().Be(PlanOperationKind.Restore);
        CurrentGameLauncherSource source = restore.Source.Should().BeOfType<CurrentGameLauncherSource>().Subject;
        source.Role.Should().Be(CurrentGameLauncherRole.OriginalLauncherBackup);
        source.SourcePath.Value.Should().Be("StardewValley-original");
        source.Sha256.Should().Be(receipt.Launcher.OriginalLauncherSha256);

        FilePreparationInstruction removeBackup = preparation.Instructions.Single(item => item.Path.Value == "StardewValley-original");
        removeBackup.Kind.Should().Be(PreparationInstructionKind.RemoveTransactionDestination);
        removeBackup.ExpectedCurrentSha256.Should().Be(receipt.Launcher.OriginalLauncherSha256);
        preparation.Receipt.Kind.Should().Be(ReceiptPreparationKind.RemoveAtomically);
        preparation.Receipt.ExpectedExistingReceiptSha256.Should().Be(receipt.GetCanonicalDigest());
        preparation.Receipt.Source.Should().BeNull();
        preparation.RecoverySnapshot.Should().NotBeNull();
        RecoverySnapshotPreparation recovery = preparation.RecoverySnapshot!;
        recovery.Snapshot.ExpectedCurrentReceiptSha256.Should().BeNull();
        recovery.Snapshot.PreviousReceiptSha256.Should().Be(receipt.GetCanonicalDigest());
        recovery.Snapshot.Entries.Should().ContainSingle(entry =>
            entry.Path.Value == "StardewValley-original"
            && entry.OwnedKind == OwnedEntryKind.RecoveryLauncherBackup
            && entry.Kind == RollbackEntryKind.Restore
            && entry.ExpectedCurrent == null
            && entry.Backup!.UnixMode == 493
        );
        recovery.Snapshot.Entries.Single(entry => entry.Path.Value == "StardewValley").Backup!.Sha256
            .Should().Be(receipt.Launcher.InstalledLauncherSha256);
    }

    [Test]
    public void UserBackup_CapturesOnlyReceiptOwnedFilesAndCreatesNoGameTransactionDestinations()
    {
        NormalizedRelativePath userPath = OwnershipTestData.Path("Mods/UserMod/config.json");
        CurrentFile userFile = new(userPath, OwnershipTestData.Digest('8'), 420);
        Sha256Digest launcher = OwnershipTestData.Digest('1');
        Sha256Digest original = OwnershipTestData.Digest('f');
        PackageManifest manifest = OwnershipTestData.Manifest(
            otherEntries: [OwnershipTestData.Entry("StardewModdingAPI", '8', OwnedEntryKind.RuntimeFile)]
        );
        InstallationReceipt receipt = OwnershipTestData.Receipt(manifest);
        CurrentFile runtime = new(
            OwnershipTestData.Path("StardewModdingAPI"),
            OwnershipTestData.Digest('8'),
            420
        );
        InstallationPlanningRequest request = new(
            InstallationAction.Backup,
            InstallationInventory.Create(manifest, receipt, [runtime, userFile], preservedPaths: [userPath]),
            LauncherState.Assess(launcher, original, receipt.Launcher),
            targetManifest: manifest,
            installedReceipt: receipt
        );

        (InstallationPlan plan, InstallationExecutionPreparation preparation) = this.Compile(request);

        AssertLossless(plan, preparation);
        preparation.TransactionDestinations.Should().BeEmpty();
        preparation.Instructions.Should().OnlyContain(item => item.Kind == PreparationInstructionKind.CaptureRecoveryFile);
        preparation.Instructions.Should().NotContain(item => item.Path.Equals(userPath));
        preparation.Instructions.Single(item => item.Path.Equals(runtime.Path)).Source.Should().BeOfType<CurrentGameFileSource>();
        preparation.Instructions.Single(item => item.Path.Value == "StardewValley").Source.As<CurrentGameLauncherSource>().Role.Should().Be(CurrentGameLauncherRole.CurrentLauncher);
        preparation.Instructions.Single(item => item.Path.Value == "StardewValley-original").Source.As<CurrentGameLauncherSource>().Role.Should().Be(CurrentGameLauncherRole.OriginalLauncherBackup);
        preparation.Receipt.Kind.Should().Be(ReceiptPreparationKind.None);
        preparation.RecoverySnapshot.Should().NotBeNull();
        preparation.RecoverySnapshot!.Snapshot.ExpectedCurrentReceiptSha256.Should().Be(receipt.GetCanonicalDigest());
        preparation.RecoverySnapshot.Snapshot.PreviousReceiptSha256.Should().Be(receipt.GetCanonicalDigest());
        preparation.RecoverySnapshot.Snapshot.Entries.Should().HaveCount(preparation.Instructions.Count);
        preparation.RecoverySnapshot.PathBindings.Should().OnlyContain(binding => binding.RequiresContentCapture);
    }

    [Test]
    public void Rollback_UsesOnlyExactRecoverySnapshotSourcesAndRestoresPriorReceiptAtomically()
    {
        PackageManifest manifest = OwnershipTestData.Manifest(
            otherEntries:
            [
                OwnershipTestData.Entry("StardewModdingAPI", '2', OwnedEntryKind.RuntimeFile),
                OwnershipTestData.Entry("smapi-internal/new.dll", '3', OwnedEntryKind.InternalFile)
            ]
        );
        InstallationReceipt receipt = OwnershipTestData.Receipt(manifest);
        InstallationReceiptEntry runtime = receipt.Entries.Single(entry => entry.Path.Value == "StardewModdingAPI");
        InstallationReceiptEntry created = receipt.Entries.Single(entry => entry.Path.Value == "smapi-internal/new.dll");
        RollbackSnapshot snapshot = new(
            receipt.GetCanonicalDigest(),
            OwnershipTestData.Digest('7'),
            [
                new RollbackSnapshotEntry(runtime.Path, runtime.Kind, RollbackEntryKind.Restore, Identity(runtime.InstalledSha256, mode: runtime.UnixMode), Identity('8', mode: runtime.UnixMode)),
                new RollbackSnapshotEntry(created.Path, created.Kind, RollbackEntryKind.Remove, Identity(created.InstalledSha256, mode: created.UnixMode), null)
            ]
        );
        InstallationPlanningRequest request = new(
            InstallationAction.Rollback,
            InstallationInventory.Create(null, receipt, [
                new CurrentFile(runtime.Path, runtime.InstalledSha256, runtime.UnixMode),
                new CurrentFile(created.Path, created.InstalledSha256, created.UnixMode)
            ]),
            LauncherState.Assess(receipt.Launcher.InstalledLauncherSha256, receipt.Launcher.OriginalLauncherSha256, receipt.Launcher),
            installedReceipt: receipt,
            rollbackSnapshot: snapshot,
            recoveryObservations:
            [
                new RecoveryFileObservation(runtime.Path, Identity(runtime.InstalledSha256, mode: runtime.UnixMode)),
                new RecoveryFileObservation(created.Path, Identity(created.InstalledSha256, mode: created.UnixMode))
            ]
        );

        (InstallationPlan plan, InstallationExecutionPreparation preparation) = this.Compile(request);

        AssertLossless(plan, preparation);
        preparation.Binding.RollbackSnapshotSha256.Should().NotBeNull();
        Sha256Digest snapshotIdentity = preparation.Binding.RollbackSnapshotSha256!;
        RecoverySnapshotSource restore = preparation.Instructions.Single(item => item.PlanKind == PlanOperationKind.Restore)
            .Source.Should().BeOfType<RecoverySnapshotSource>().Subject;
        restore.Content.Should().Be(RecoverySnapshotContent.GameFile);
        restore.SnapshotSha256.Should().Be(snapshotIdentity);
        restore.EntryPath.Should().Be(runtime.Path);
        restore.ExpectedSizeBytes.Should().Be(10);
        restore.ExpectedUnixMode.Should().Be(runtime.UnixMode);
        preparation.Instructions.Single(item => item.PlanKind == PlanOperationKind.Restore).ResultUnixMode.Should().Be(runtime.UnixMode);
        preparation.Instructions.Single(item => item.PlanKind == PlanOperationKind.Remove).Source.Should().BeNull();

        preparation.Receipt.Kind.Should().Be(ReceiptPreparationKind.WriteAtomically);
        preparation.Receipt.ExpectedExistingReceiptSha256.Should().Be(receipt.GetCanonicalDigest());
        RecoverySnapshotSource receiptSource = preparation.Receipt.Source.Should().BeOfType<RecoverySnapshotSource>().Subject;
        receiptSource.Content.Should().Be(RecoverySnapshotContent.InstalledReceipt);
        receiptSource.SnapshotSha256.Should().Be(snapshotIdentity);
        receiptSource.EntryPath.Should().BeNull();
        receiptSource.ExpectedContentSha256.Should().Be(OwnershipTestData.Digest('7'));
    }

    [Test]
    public void Rollback_AfterUninstallRestoresReceiptIntoAnExpectedAbsentSlot()
    {
        Sha256Digest priorReceiptSha256 = OwnershipTestData.Digest('7');
        NormalizedRelativePath runtimePath = OwnershipTestData.Path("StardewModdingAPI");
        RollbackSnapshot snapshot = new(
            expectedCurrentReceiptSha256: null,
            previousReceiptSha256: priorReceiptSha256,
            [
                new RollbackSnapshotEntry(
                    runtimePath,
                    OwnedEntryKind.RuntimeFile,
                    RollbackEntryKind.Restore,
                    expectedCurrent: null,
                    backup: Identity('8')
                )
            ]
        );
        InstallationPlanningRequest request = new(
            InstallationAction.Rollback,
            InstallationInventory.Create(null, null, []),
            LauncherState.Assess(OwnershipTestData.Digest('f'), null, null),
            rollbackSnapshot: snapshot,
            recoveryObservations: [new RecoveryFileObservation(runtimePath, null)]
        );

        (InstallationPlan plan, InstallationExecutionPreparation preparation) = this.Compile(request);

        plan.CanExecute.Should().BeTrue();
        preparation.Receipt.Kind.Should().Be(ReceiptPreparationKind.WriteAtomically);
        preparation.Receipt.ExpectedExistingReceiptSha256.Should().BeNull();
        RecoverySnapshotSource receiptSource = preparation.Receipt.Source.Should().BeOfType<RecoverySnapshotSource>().Subject;
        receiptSource.Content.Should().Be(RecoverySnapshotContent.InstalledReceipt);
        receiptSource.ExpectedContentSha256.Should().Be(priorReceiptSha256);
        preparation.RecoverySnapshot.Should().NotBeNull();
        preparation.RecoverySnapshot!.Snapshot.ExpectedCurrentReceiptSha256.Should().Be(priorReceiptSha256);
        preparation.RecoverySnapshot.Snapshot.PreviousReceiptSha256.Should().BeNull();
        preparation.RecoverySnapshot.Snapshot.Entries.Should().ContainSingle(entry =>
            entry.Path.Equals(runtimePath)
            && entry.Kind == RollbackEntryKind.Remove
            && entry.ExpectedCurrentSha256 == OwnershipTestData.Digest('8')
        );
    }

    [Test]
    public void Repair_WithNoFileChangesProducesReceiptOnlyDurableRecoverySnapshot()
    {
        PackageManifest manifest = OwnershipTestData.Manifest(
            otherEntries: [OwnershipTestData.Entry("StardewModdingAPI", '2', OwnedEntryKind.RuntimeFile)]
        );
        InstallationReceipt receipt = OwnershipTestData.Receipt(manifest);
        InstallationPlanningRequest request = new(
            InstallationAction.Repair,
            InstallationInventory.Create(manifest, receipt, manifest.Entries.Select(entry => OwnershipTestData.Current(entry))),
            LauncherState.Assess(receipt.Launcher.InstalledLauncherSha256, receipt.Launcher.OriginalLauncherSha256, receipt.Launcher),
            targetManifest: manifest,
            installedReceipt: receipt
        );

        (_, InstallationExecutionPreparation preparation) = this.Compile(request);

        preparation.RecoverySnapshot.Should().NotBeNull();
        RecoverySnapshotPreparation recovery = preparation.RecoverySnapshot!;
        recovery.Snapshot.Entries.Should().BeEmpty();
        recovery.Snapshot.PreviousReceiptSha256.Should().Be(receipt.GetCanonicalDigest());
        recovery.Snapshot.ExpectedCurrentReceiptSha256.Should().Be(
            preparation.Receipt.Source.As<GeneratedCanonicalReceiptSource>().Sha256
        );
        recovery.PathBindings.Select(binding => binding.Path.Value).Should().BeEquivalentTo("StardewValley", "StardewValley-original");
        recovery.PathBindings.Should().OnlyContain(binding => !binding.RequiresContentCapture);
    }

    [Test]
    public void BoundPlan_RejectsRecoveryMetadataTamperingEvenWhenPlanHashesAreUnchanged()
    {
        (InstallationPlanningRequest request, InstallationPlan plan) = this.CreateFreshInstallRequest('2', size: 10);
        BoundInstallationPlan binding = this.Compiler.BindPlan(
            plan,
            request,
            GameRoot,
            OperationGeneration,
            Authority(request),
            RecoveryAuthority(request)
        );
        RecoveryFileObservation[] tampered = request.RecoveryObservations
            .Select(observation => observation.Path.Value == "StardewValley"
                ? new RecoveryFileObservation(
                    observation.Path,
                    new RecoveryFileIdentity(
                        observation.Identity!.Sha256,
                        observation.Identity.SizeBytes + 1,
                        observation.Identity.UnixMode,
                        observation.Identity.FileType
                    )
                )
                : observation)
            .ToArray();
        InstallationPlanningRequest changed = new(
            request.Action,
            request.Inventory,
            request.Launcher,
            request.TargetManifest,
            request.InstalledReceipt,
            request.RollbackSnapshot,
            tampered
        );

        Action prepare = () => this.Compiler.Prepare(binding, plan, changed, TransactionId);

        prepare.Should().Throw<ExecutionCompilationException>().Which.Error.Should().Be(ExecutionCompilationError.StalePlan);
    }

    [Test]
    public void BindPlan_RawManifestWithoutLivePackageContentAuthority_Rejects()
    {
        (InstallationPlanningRequest request, InstallationPlan plan) = this.CreateFreshInstallRequest('2', size: 10);

        Action action = () => this.Compiler.BindPlan(plan, request, GameRoot, OperationGeneration, null);

        action.Should().Throw<ExecutionCompilationException>()
            .Which.Error.Should().Be(ExecutionCompilationError.StaleManifest);
    }

    [Test]
    public void BindAndPrepare_RejectNonExecutableStaleAndMismatchedState()
    {
        (InstallationPlanningRequest firstRequest, InstallationPlan firstPlan) = this.CreateFreshInstallRequest('2', size: 10);
        BoundInstallationPlan binding = this.Compiler.BindPlan(firstPlan, firstRequest, GameRoot, OperationGeneration, Authority(firstRequest));
        BoundInstallationPlan changedRootBinding = this.Compiler.BindPlan(
            firstPlan,
            firstRequest,
            new GameRootIdentity(GameRoot.CanonicalPath, GameRoot.DeviceMajor, GameRoot.DeviceMinor, GameRoot.Inode + 1),
            OperationGeneration,
            Authority(firstRequest)
        );
        BoundInstallationPlan changedGenerationBinding = this.Compiler.BindPlan(
            firstPlan,
            firstRequest,
            GameRoot,
            OperationGeneration + 1,
            Authority(firstRequest)
        );

        (InstallationPlanningRequest changedPlanRequest, InstallationPlan changedPlan) = this.CreateFreshInstallRequest('3', size: 10);
        Action stalePlan = () => this.Compiler.Prepare(binding, changedPlan, changedPlanRequest, TransactionId);

        (InstallationPlanningRequest changedManifestRequest, InstallationPlan samePlan) = this.CreateFreshInstallRequest('2', size: 11);
        samePlan.GetCanonicalDigest().Should().Be(firstPlan.GetCanonicalDigest(), "manifest sizes aren't represented as file-operation digests");
        BoundInstallationPlan changedManifestBinding = this.Compiler.BindPlan(
            samePlan,
            changedManifestRequest,
            GameRoot,
            OperationGeneration,
            Authority(changedManifestRequest)
        );
        Action staleManifest = () => this.Compiler.Prepare(binding, firstPlan, changedManifestRequest, TransactionId);

        PackageManifest collisionManifest = OwnershipTestData.Manifest(
            otherEntries: [OwnershipTestData.Entry("StardewModdingAPI", '2', OwnedEntryKind.RuntimeFile)]
        );
        CurrentFile collision = new(OwnershipTestData.Path("StardewModdingAPI"), OwnershipTestData.Digest('9'), 420);
        CurrentFile vanilla = new(OwnershipTestData.Path("StardewValley"), OwnershipTestData.Digest('f'), 493);
        InstallationPlanningRequest collisionRequest = new(
            InstallationAction.Install,
            InstallationInventory.Create(collisionManifest, null, [collision, vanilla]),
            LauncherState.Assess(vanilla.Sha256, null, null),
            targetManifest: collisionManifest
        );
        InstallationPlan conflictPlan = this.Planner.Plan(collisionRequest);
        Action nonExecutable = () => this.Compiler.BindPlan(
            conflictPlan,
            collisionRequest,
            GameRoot,
            OperationGeneration,
            Authority(collisionRequest)
        );

        stalePlan.Should().Throw<ExecutionCompilationException>().Which.Error.Should().Be(ExecutionCompilationError.StalePlan);
        staleManifest.Should().Throw<ExecutionCompilationException>().Which.Error.Should().Be(ExecutionCompilationError.StaleManifest);
        changedManifestBinding.GetCanonicalDigest().Should().NotBe(binding.GetCanonicalDigest(), "confirmation must bind the complete manifest identity");
        changedRootBinding.GetCanonicalDigest().Should().NotBe(binding.GetCanonicalDigest(), "confirmation must bind the anchored root object");
        changedGenerationBinding.GetCanonicalDigest().Should().NotBe(binding.GetCanonicalDigest(), "confirmation must bind the operation generation");
        nonExecutable.Should().Throw<ExecutionCompilationException>().Which.Error.Should().Be(ExecutionCompilationError.NonExecutablePlan);
    }

    [Test]
    public void Prepare_RejectsReceiptSwapEvenWhenUninstallPlanIsIdentical()
    {
        PackageManifest manifest = OwnershipTestData.Manifest(
            otherEntries: [OwnershipTestData.Entry("StardewModdingAPI", '2', OwnedEntryKind.RuntimeFile)]
        );
        InstallationReceipt firstReceipt = OwnershipTestData.Receipt(manifest);
        InstallationReceipt secondReceipt = new(
            firstReceipt.Release,
            firstReceipt.ManifestSha256,
            new string('e', 32),
            firstReceipt.Entries,
            firstReceipt.Launcher
        );
        InstallationPlanningRequest firstRequest = CreateUninstallRequest(firstReceipt);
        InstallationPlanningRequest secondRequest = CreateUninstallRequest(secondReceipt);
        InstallationPlan firstPlan = this.Planner.Plan(firstRequest);
        InstallationPlan secondPlan = this.Planner.Plan(secondRequest);
        secondPlan.GetCanonicalDigest().Should().Be(firstPlan.GetCanonicalDigest());
        BoundInstallationPlan binding = this.Compiler.BindPlan(firstPlan, firstRequest, GameRoot, OperationGeneration, null);

        Action prepare = () => this.Compiler.Prepare(binding, firstPlan, secondRequest, TransactionId);

        prepare.Should().Throw<ExecutionCompilationException>().Which.Error.Should().Be(ExecutionCompilationError.StaleInstalledReceipt);
    }

    [Test]
    public void TransactionDestinations_AreUniqueAllowedAndFrontendCannotConstructRules()
    {
        (InstallationPlanningRequest request, InstallationPlan plan) = this.CreateFreshInstallRequest('2', size: 10);
        InstallationExecutionPreparation preparation = this.Compiler.Prepare(
            this.Compiler.BindPlan(plan, request, GameRoot, OperationGeneration, Authority(request)),
            plan,
            request,
            TransactionId
        );

        string[] destinations = preparation.TransactionDestinations.Select(item => item.Path.Value).ToArray();
        destinations.Distinct(StringComparer.OrdinalIgnoreCase).Should().HaveCount(destinations.Length);
        preparation.TransactionDestinations.Should().OnlyContain(item => OwnedNamespacePolicy.IsAllowedTransactionDestination(item.Path));
        typeof(FilePreparationInstruction).GetConstructors().Should().BeEmpty();
        typeof(CurrentGameLauncherSource).GetConstructors().Should().BeEmpty();
        typeof(RecoverySnapshotSource).GetConstructors().Should().BeEmpty();
        preparation.Instructions.Count.Should().Be(plan.Operations.Count);
    }

    private (InstallationPlan Plan, InstallationExecutionPreparation Preparation) Compile(InstallationPlanningRequest request)
    {
        request = AddRequiredRecoveryObservations(request);
        InstallationPlan plan = this.Planner.Plan(request);
        BoundInstallationPlan binding = this.Compiler.BindPlan(
            plan,
            request,
            GameRoot,
            OperationGeneration,
            Authority(request),
            RecoveryAuthority(request)
        );
        return (plan, this.Compiler.Prepare(binding, plan, request, TransactionId));
    }

    private static void AssertLossless(InstallationPlan plan, InstallationExecutionPreparation preparation)
    {
        preparation.Action.Should().Be(plan.Action);
        preparation.Instructions.Select(item => (item.PlanKind, item.Path.Value, item.ExpectedCurrentSha256, item.ExpectedResultSha256))
            .Should().Equal(plan.Operations.Select(item => (item.Kind, item.Path.Value, item.ExpectedCurrentSha256, item.ResultSha256)));
        preparation.Binding.PlanSha256.Should().Be(plan.GetCanonicalDigest());
    }

    private static IVerifiedPackageContentAuthority? Authority(InstallationPlanningRequest request)
    {
        bool requiresTarget = request.Action is InstallationAction.Install or InstallationAction.Update or InstallationAction.Repair;
        return requiresTarget && request.TargetManifest is not null
            ? new FakePackageContentAuthority(request.TargetManifest)
            : null;
    }

    private static ICommittedRecoveryContentAuthority? RecoveryAuthority(InstallationPlanningRequest request)
    {
        return request.Action == InstallationAction.Rollback && request.RollbackSnapshot is not null
            ? new FakeRecoveryContentAuthority(request.RollbackSnapshot)
            : null;
    }

    private sealed class FakePackageContentAuthority : IVerifiedPackageContentAuthority
    {
        public PackageManifest Manifest { get; }
        public Sha256Digest ManifestSha256 => this.Manifest.GetCanonicalDigest();

        public FakePackageContentAuthority(PackageManifest manifest)
        {
            this.Manifest = manifest;
        }

        public LinuxAnchoredFile OpenFile(PackageManifestEntry expected)
        {
            throw new NotSupportedException("The pure compiler tests never materialize package bytes.");
        }

        public void AssertUsable() { }
    }

    private sealed class FakeRecoveryContentAuthority : ICommittedRecoveryContentAuthority
    {
        public Guid GenerationId { get; } = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        public GameRootIdentity GameRoot => InstallerExecutionCompilerTests.GameRoot;
        public RollbackSnapshot Snapshot { get; }
        public Sha256Digest SnapshotSha256 => Sha256Digest.Hash(CanonicalOwnershipDocuments.SerializeRollbackSnapshot(this.Snapshot));
        public Sha256Digest? PreviousManifestSha256 => this.Snapshot.PreviousReceiptSha256 is null ? null : OwnershipTestData.Digest('a');
        public Sha256Digest? PreviousReceiptSha256 => this.Snapshot.PreviousReceiptSha256;

        public FakeRecoveryContentAuthority(RollbackSnapshot snapshot)
        {
            this.Snapshot = snapshot;
        }

        public LinuxAnchoredFile OpenGameFile(NormalizedRelativePath path, RecoveryFileIdentity expectedIdentity)
            => throw new NotSupportedException("The pure compiler tests never materialize recovery bytes.");

        public LinuxAnchoredFile OpenPreviousReceipt(Sha256Digest expectedSha256)
            => throw new NotSupportedException("The pure compiler tests never materialize recovery bytes.");

        public LinuxAnchoredFile OpenPreviousManifest(Sha256Digest expectedSha256)
            => throw new NotSupportedException("The pure compiler tests never materialize recovery bytes.");

        public void AssertUsable() { }
    }

    private (InstallationPlanningRequest Request, InstallationPlan Plan) CreateFreshInstallRequest(char runtimeDigest, long size)
    {
        PackageManifest manifest = OwnershipTestData.Manifest(
            otherEntries: [OwnershipTestData.Entry("StardewModdingAPI", runtimeDigest, OwnedEntryKind.RuntimeFile, mode: 493, size: size)]
        );
        CurrentFile vanilla = new(OwnershipTestData.Path("StardewValley"), OwnershipTestData.Digest('f'), 493);
        InstallationPlanningRequest request = new(
            InstallationAction.Install,
            InstallationInventory.Create(manifest, null, [vanilla]),
            LauncherState.Assess(vanilla.Sha256, null, null),
            targetManifest: manifest,
            recoveryObservations:
            [
                new RecoveryFileObservation(OwnershipTestData.Path("StardewValley"), Identity('f', mode: 493)),
                new RecoveryFileObservation(OwnershipTestData.Path("StardewValley-original"), null),
                new RecoveryFileObservation(OwnershipTestData.Path("StardewModdingAPI"), null)
            ]
        );
        return (request, this.Planner.Plan(request));
    }

    private static InstallationPlanningRequest CreateUninstallRequest(InstallationReceipt receipt)
    {
        InstallationReceiptEntry runtime = receipt.Entries.Single(entry => entry.Kind == OwnedEntryKind.RuntimeFile);
        InstallationPlanningRequest request = new(
            InstallationAction.Uninstall,
            InstallationInventory.Create(null, receipt, [new CurrentFile(runtime.Path, runtime.InstalledSha256, runtime.UnixMode)]),
            LauncherState.Assess(receipt.Launcher.InstalledLauncherSha256, receipt.Launcher.OriginalLauncherSha256, receipt.Launcher),
            installedReceipt: receipt
        );
        return AddRequiredRecoveryObservations(request);
    }

    private static InstallationPlanningRequest AddRequiredRecoveryObservations(InstallationPlanningRequest request)
    {
        if (request.RecoveryObservations.Count > 0 || request.Action == InstallationAction.Rollback)
            return request;

        InstallationPlan plan = new InstallationPlanner().Plan(request);
        IEnumerable<NormalizedRelativePath> required = request.Action == InstallationAction.Backup
            ? plan.Operations.Select(operation => operation.Path)
            : plan.Operations
                .Where(operation => operation.Kind is PlanOperationKind.Create or PlanOperationKind.Replace or PlanOperationKind.Remove or PlanOperationKind.Restore)
                .Select(operation => operation.Path)
                .Append(OwnershipTestData.Path("StardewValley"))
                .Append(OwnershipTestData.Path("StardewValley-original"));
        Dictionary<string, CurrentFile> current = request.Inventory.Entries
            .Where(entry => entry.Current is not null)
            .ToDictionary(entry => entry.Path.Value, entry => entry.Current!, StringComparer.Ordinal);
        RecoveryFileObservation[] observations = required
            .DistinctBy(path => path.Value)
            .Select(path => new RecoveryFileObservation(path, GetTestIdentity(path, current, request.Launcher)))
            .ToArray();
        return new InstallationPlanningRequest(
            request.Action,
            request.Inventory,
            request.Launcher,
            request.TargetManifest,
            request.InstalledReceipt,
            request.RollbackSnapshot,
            observations
        );
    }

    private static RecoveryFileIdentity? GetTestIdentity(
        NormalizedRelativePath path,
        IReadOnlyDictionary<string, CurrentFile> current,
        LauncherState launcher
    )
    {
        if (current.TryGetValue(path.Value, out CurrentFile? file))
            return Identity(file.Sha256, mode: file.UnixMode);
        if (path.Value == "StardewValley" && launcher.CurrentLauncherSha256 is not null)
            return Identity(launcher.CurrentLauncherSha256, mode: 493);
        if (path.Value == "StardewValley-original" && launcher.BackupLauncherSha256 is not null)
            return Identity(launcher.BackupLauncherSha256, mode: 493);
        return null;
    }

    private static RecoveryFileIdentity Identity(char digest, long size = 10, int mode = 420)
        => Identity(OwnershipTestData.Digest(digest), size, mode);

    private static RecoveryFileIdentity Identity(Sha256Digest digest, long size = 10, int mode = 420)
        => new(digest, size, mode);
}
