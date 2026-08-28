using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Tests.Ownership;

namespace StardewModdingAPI.Installer.Core.Tests.Engine;

[TestFixture]
public class InstallerExecutionCompilerTests
{
    private static readonly Guid TransactionId = Guid.Parse("12345678-1234-1234-1234-123456789abc");
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
                new RollbackSnapshotEntry(runtime.Path, runtime.Kind, RollbackEntryKind.Restore, runtime.InstalledSha256, OwnershipTestData.Digest('8')),
                new RollbackSnapshotEntry(created.Path, created.Kind, RollbackEntryKind.Remove, created.InstalledSha256, null)
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
            rollbackSnapshot: snapshot
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
                    expectedCurrentSha256: null,
                    backupSha256: OwnershipTestData.Digest('8')
                )
            ]
        );
        InstallationPlanningRequest request = new(
            InstallationAction.Rollback,
            InstallationInventory.Create(null, null, []),
            LauncherState.Assess(OwnershipTestData.Digest('f'), null, null),
            rollbackSnapshot: snapshot
        );

        (InstallationPlan plan, InstallationExecutionPreparation preparation) = this.Compile(request);

        plan.CanExecute.Should().BeTrue();
        preparation.Receipt.Kind.Should().Be(ReceiptPreparationKind.WriteAtomically);
        preparation.Receipt.ExpectedExistingReceiptSha256.Should().BeNull();
        RecoverySnapshotSource receiptSource = preparation.Receipt.Source.Should().BeOfType<RecoverySnapshotSource>().Subject;
        receiptSource.Content.Should().Be(RecoverySnapshotContent.InstalledReceipt);
        receiptSource.ExpectedContentSha256.Should().Be(priorReceiptSha256);
    }

    [Test]
    public void BindAndPrepare_RejectNonExecutableStaleAndMismatchedState()
    {
        (InstallationPlanningRequest firstRequest, InstallationPlan firstPlan) = CreateFreshInstallRequest('2', size: 10);
        BoundInstallationPlan binding = this.Compiler.BindPlan(firstPlan, firstRequest);

        (InstallationPlanningRequest changedPlanRequest, InstallationPlan changedPlan) = CreateFreshInstallRequest('3', size: 10);
        Action stalePlan = () => this.Compiler.Prepare(binding, changedPlan, changedPlanRequest, TransactionId);

        (InstallationPlanningRequest changedManifestRequest, InstallationPlan samePlan) = CreateFreshInstallRequest('2', size: 11);
        samePlan.GetCanonicalDigest().Should().Be(firstPlan.GetCanonicalDigest(), "manifest sizes aren't represented as file-operation digests");
        BoundInstallationPlan changedManifestBinding = this.Compiler.BindPlan(samePlan, changedManifestRequest);
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
        Action nonExecutable = () => this.Compiler.BindPlan(conflictPlan, collisionRequest);

        stalePlan.Should().Throw<ExecutionCompilationException>().Which.Error.Should().Be(ExecutionCompilationError.StalePlan);
        staleManifest.Should().Throw<ExecutionCompilationException>().Which.Error.Should().Be(ExecutionCompilationError.StaleManifest);
        changedManifestBinding.GetCanonicalDigest().Should().NotBe(binding.GetCanonicalDigest(), "confirmation must bind the complete manifest identity");
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
        BoundInstallationPlan binding = this.Compiler.BindPlan(firstPlan, firstRequest);

        Action prepare = () => this.Compiler.Prepare(binding, firstPlan, secondRequest, TransactionId);

        prepare.Should().Throw<ExecutionCompilationException>().Which.Error.Should().Be(ExecutionCompilationError.StaleInstalledReceipt);
    }

    [Test]
    public void TransactionDestinations_AreUniqueAllowedAndFrontendCannotConstructRules()
    {
        (InstallationPlanningRequest request, InstallationPlan plan) = CreateFreshInstallRequest('2', size: 10);
        InstallationExecutionPreparation preparation = this.Compiler.Prepare(
            this.Compiler.BindPlan(plan, request),
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
        InstallationPlan plan = this.Planner.Plan(request);
        BoundInstallationPlan binding = this.Compiler.BindPlan(plan, request);
        return (plan, this.Compiler.Prepare(binding, plan, request, TransactionId));
    }

    private static void AssertLossless(InstallationPlan plan, InstallationExecutionPreparation preparation)
    {
        preparation.Action.Should().Be(plan.Action);
        preparation.Instructions.Select(item => (item.PlanKind, item.Path.Value, item.ExpectedCurrentSha256, item.ExpectedResultSha256))
            .Should().Equal(plan.Operations.Select(item => (item.Kind, item.Path.Value, item.ExpectedCurrentSha256, item.ResultSha256)));
        preparation.Binding.PlanSha256.Should().Be(plan.GetCanonicalDigest());
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
            targetManifest: manifest
        );
        return (request, this.Planner.Plan(request));
    }

    private static InstallationPlanningRequest CreateUninstallRequest(InstallationReceipt receipt)
    {
        InstallationReceiptEntry runtime = receipt.Entries.Single(entry => entry.Kind == OwnedEntryKind.RuntimeFile);
        return new InstallationPlanningRequest(
            InstallationAction.Uninstall,
            InstallationInventory.Create(null, receipt, [new CurrentFile(runtime.Path, runtime.InstalledSha256, runtime.UnixMode)]),
            LauncherState.Assess(receipt.Launcher.InstalledLauncherSha256, receipt.Launcher.OriginalLauncherSha256, receipt.Launcher),
            installedReceipt: receipt
        );
    }
}
