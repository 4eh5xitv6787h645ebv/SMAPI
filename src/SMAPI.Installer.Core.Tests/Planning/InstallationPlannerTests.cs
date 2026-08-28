using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Tests.Ownership;

namespace StardewModdingAPI.Installer.Core.Tests.Planning;

[TestFixture]
public class InstallationPlannerTests
{
    private readonly InstallationPlanner Planner = new();

    [Test]
    public void Install_BacksUpLauncherCreatesPayloadAndIsDeterministic()
    {
        PackageManifest manifest = OwnershipTestData.Manifest(
            otherEntries: [OwnershipTestData.Entry("StardewModdingAPI", '2', OwnedEntryKind.RuntimeFile, mode: 493)]
        );
        CurrentFile vanilla = new(OwnershipTestData.Path("StardewValley"), OwnershipTestData.Digest('f'), 493);
        InstallationInventory inventory = InstallationInventory.Create(manifest, null, [vanilla]);
        LauncherState launcher = LauncherState.Assess(vanilla.Sha256, null, null);
        InstallationPlanningRequest request = new(InstallationAction.Install, inventory, launcher, targetManifest: manifest);

        InstallationPlan first = this.Planner.Plan(request);
        InstallationPlan second = this.Planner.Plan(request);

        first.CanExecute.Should().BeTrue();
        first.Operations.Select(operation => (operation.Path.Value, operation.Kind)).Should().Equal(
            ("StardewModdingAPI", PlanOperationKind.Create),
            ("StardewValley", PlanOperationKind.Replace),
            ("StardewValley-original", PlanOperationKind.Create)
        );
        first.ToCanonicalJson().Should().Be(second.ToCanonicalJson());
        first.GetCanonicalDigest().Should().Be(second.GetCanonicalDigest());
    }

    [Test]
    public void Install_BlocksUnknownCollisionAndExistingReceipt()
    {
        PackageManifest manifest = OwnershipTestData.Manifest(
            otherEntries: [OwnershipTestData.Entry("StardewModdingAPI", '2', OwnedEntryKind.RuntimeFile)]
        );
        PackageManifestEntry runtime = manifest.Entries.Single(entry => entry.Kind == OwnedEntryKind.RuntimeFile);
        CurrentFile collision = OwnershipTestData.Current(runtime, digest: '9');
        CurrentFile vanilla = new(OwnershipTestData.Path("StardewValley"), OwnershipTestData.Digest('f'), 493);
        InstallationInventory inventory = InstallationInventory.Create(manifest, null, [collision, vanilla]);

        InstallationPlan collisionPlan = this.Planner.Plan(new InstallationPlanningRequest(
            InstallationAction.Install,
            inventory,
            LauncherState.Assess(vanilla.Sha256, null, null),
            targetManifest: manifest
        ));
        InstallationPlan receiptPlan = this.Planner.Plan(new InstallationPlanningRequest(
            InstallationAction.Install,
            inventory,
            LauncherState.Assess(vanilla.Sha256, null, null),
            targetManifest: manifest,
            installedReceipt: OwnershipTestData.Receipt(manifest)
        ));

        collisionPlan.Conflicts.Should().ContainSingle(conflict => conflict.Code == PlanConflictCode.UnknownCollision && conflict.Path!.Equals(runtime.Path));
        receiptPlan.Conflicts.Should().ContainSingle(conflict => conflict.Code == PlanConflictCode.ExistingInstallationRequiresUpdate);
    }

    [Test]
    public void Update_ReplacesCreatesRemovesRetainsAndPreservesByOwnership()
    {
        PackageManifest oldManifest = OwnershipTestData.Manifest(
            launcherDigest: '1',
            otherEntries:
            [
                OwnershipTestData.Entry("StardewModdingAPI", '2', OwnedEntryKind.RuntimeFile, mode: 493),
                OwnershipTestData.Entry("smapi-internal/stable.dll", '3', OwnedEntryKind.InternalFile),
                OwnershipTestData.Entry("smapi-internal/removed.dll", '4', OwnedEntryKind.InternalFile)
            ]
        );
        InstallationReceipt receipt = OwnershipTestData.Receipt(oldManifest);
        PackageManifest target = OwnershipTestData.Manifest(
            OwnershipTestData.Release(alpha: 2, packageHash: 'e'),
            launcherDigest: '5',
            OwnershipTestData.Entry("StardewModdingAPI", '6', OwnedEntryKind.RuntimeFile, mode: 493),
            OwnershipTestData.Entry("smapi-internal/stable.dll", '3', OwnedEntryKind.InternalFile),
            OwnershipTestData.Entry("smapi-internal/new.dll", '7', OwnedEntryKind.InternalFile)
        );
        NormalizedRelativePath privatePath = OwnershipTestData.Path("Mods/PrivateMod/config.json");
        CurrentFile privateFile = new(privatePath, OwnershipTestData.Digest('8'), 420);
        CurrentFile[] current = oldManifest.Entries.Select(entry => OwnershipTestData.Current(entry)).Append(privateFile).Reverse().ToArray();
        InstallationInventory inventory = InstallationInventory.Create(target, receipt, current, preservedPaths: [privatePath]);
        LauncherState launcher = LauncherState.Assess(OwnershipTestData.Digest('1'), OwnershipTestData.Digest('f'), receipt.Launcher);

        InstallationPlan plan = this.Planner.Plan(new InstallationPlanningRequest(
            InstallationAction.Update,
            inventory,
            launcher,
            targetManifest: target,
            installedReceipt: receipt
        ));

        plan.CanExecute.Should().BeTrue();
        plan.Operations.Should().Contain(operation => operation.Path.Value == "StardewModdingAPI" && operation.Kind == PlanOperationKind.Replace);
        plan.Operations.Should().Contain(operation => operation.Path.Value == "StardewValley" && operation.Kind == PlanOperationKind.Replace);
        plan.Operations.Should().Contain(operation => operation.Path.Value == "smapi-internal/new.dll" && operation.Kind == PlanOperationKind.Create);
        plan.Operations.Should().Contain(operation => operation.Path.Value == "smapi-internal/removed.dll" && operation.Kind == PlanOperationKind.Remove);
        plan.Operations.Should().Contain(operation => operation.Path.Value == "smapi-internal/stable.dll" && operation.Kind == PlanOperationKind.Retain);
        plan.Operations.Should().Contain(operation => operation.Path.Equals(privatePath) && operation.Kind == PlanOperationKind.Preserve);
        plan.Operations.Select(operation => operation.Path.Value).Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Test]
    public void Update_BlocksModifiedOwnedFileAndAmbiguousLauncherBackup()
    {
        PackageManifest manifest = OwnershipTestData.Manifest(
            otherEntries: [OwnershipTestData.Entry("StardewModdingAPI", '2', OwnedEntryKind.RuntimeFile)]
        );
        InstallationReceipt receipt = OwnershipTestData.Receipt(manifest);
        PackageManifestEntry runtime = manifest.Entries.Single(entry => entry.Kind == OwnedEntryKind.RuntimeFile);
        InstallationInventory inventory = InstallationInventory.Create(
            manifest,
            receipt,
            [OwnershipTestData.Current(runtime, digest: '9')]
        );
        LauncherState ambiguous = LauncherState.Assess(
            receipt.Launcher.InstalledLauncherSha256,
            OwnershipTestData.Digest('8'),
            receipt.Launcher
        );

        InstallationPlan plan = this.Planner.Plan(new InstallationPlanningRequest(
            InstallationAction.Update,
            inventory,
            ambiguous,
            targetManifest: manifest,
            installedReceipt: receipt
        ));

        plan.CanExecute.Should().BeFalse();
        plan.Conflicts.Should().Contain(conflict => conflict.Code == PlanConflictCode.ModifiedOwnedFile && conflict.Path!.Equals(runtime.Path));
        plan.Conflicts.Should().Contain(conflict => conflict.Code == PlanConflictCode.AmbiguousLauncherBackup);
    }

    [Test]
    public void Repair_RestoresMissingButBlocksModifiedAndReleaseMismatch()
    {
        PackageManifest manifest = OwnershipTestData.Manifest(
            otherEntries:
            [
                OwnershipTestData.Entry("StardewModdingAPI", '2', OwnedEntryKind.RuntimeFile),
                OwnershipTestData.Entry("smapi-internal/modified.dll", '3', OwnedEntryKind.InternalFile)
            ]
        );
        InstallationReceipt receipt = OwnershipTestData.Receipt(manifest);
        PackageManifestEntry modified = manifest.Entries.Single(entry => entry.Path.Value == "smapi-internal/modified.dll");
        InstallationInventory inventory = InstallationInventory.Create(
            manifest,
            receipt,
            [OwnershipTestData.Current(modified, digest: '9')]
        );
        LauncherState launcher = LauncherState.Assess(receipt.Launcher.InstalledLauncherSha256, receipt.Launcher.OriginalLauncherSha256, receipt.Launcher);

        InstallationPlan plan = this.Planner.Plan(new InstallationPlanningRequest(
            InstallationAction.Repair,
            inventory,
            launcher,
            targetManifest: manifest,
            installedReceipt: receipt
        ));
        PackageManifest otherRelease = OwnershipTestData.Manifest(OwnershipTestData.Release(alpha: 2), launcherDigest: '4');
        InstallationPlan mismatch = this.Planner.Plan(new InstallationPlanningRequest(
            InstallationAction.Repair,
            InstallationInventory.Create(otherRelease, receipt, []),
            launcher,
            targetManifest: otherRelease,
            installedReceipt: receipt
        ));

        plan.Operations.Should().Contain(operation => operation.Path.Value == "StardewModdingAPI" && operation.Kind == PlanOperationKind.Create);
        plan.Conflicts.Should().Contain(conflict => conflict.Code == PlanConflictCode.ModifiedOwnedFile && conflict.Path!.Equals(modified.Path));
        mismatch.Conflicts.Should().ContainSingle(conflict => conflict.Code == PlanConflictCode.ReleaseDoesNotMatchReceipt);
    }

    [Test]
    public void Uninstall_RemovesOnlyUnchangedReceiptFilesAndRestoresLauncher()
    {
        PackageManifest manifest = OwnershipTestData.Manifest(
            otherEntries: [OwnershipTestData.Entry("StardewModdingAPI", '2', OwnedEntryKind.RuntimeFile)]
        );
        InstallationReceipt receipt = OwnershipTestData.Receipt(manifest);
        PackageManifestEntry runtime = manifest.Entries.Single(entry => entry.Kind == OwnedEntryKind.RuntimeFile);
        NormalizedRelativePath unrelatedPath = OwnershipTestData.Path("unrelated-user-file.txt");
        CurrentFile unrelated = new(unrelatedPath, OwnershipTestData.Digest('8'), 420);
        InstallationInventory inventory = InstallationInventory.Create(null, receipt, [OwnershipTestData.Current(runtime), unrelated]);
        LauncherState launcher = LauncherState.Assess(receipt.Launcher.InstalledLauncherSha256, receipt.Launcher.OriginalLauncherSha256, receipt.Launcher);

        InstallationPlan plan = this.Planner.Plan(new InstallationPlanningRequest(
            InstallationAction.Uninstall,
            inventory,
            launcher,
            installedReceipt: receipt
        ));

        plan.CanExecute.Should().BeTrue();
        plan.Operations.Should().Contain(operation => operation.Path.Equals(runtime.Path) && operation.Kind == PlanOperationKind.Remove);
        plan.Operations.Should().Contain(operation => operation.Path.Value == "StardewValley" && operation.Kind == PlanOperationKind.Restore && operation.ResultSha256 == receipt.Launcher.OriginalLauncherSha256);
        plan.Operations.Should().Contain(operation => operation.Path.Value == "StardewValley-original" && operation.Kind == PlanOperationKind.Remove && operation.ExpectedCurrentSha256 == receipt.Launcher.OriginalLauncherSha256);
        plan.Operations.Should().Contain(operation => operation.Path.Equals(unrelatedPath) && operation.Kind == PlanOperationKind.Preserve);
    }

    [Test]
    public void Uninstall_WithoutReceiptRefusesLegacyCandidates()
    {
        NormalizedRelativePath legacyPath = OwnershipTestData.Path("StardewModdingAPI");
        InstallationInventory inventory = InstallationInventory.Create(
            null,
            null,
            [new CurrentFile(legacyPath, OwnershipTestData.Digest('2'), 493)],
            legacyPaths: [legacyPath]
        );

        InstallationPlan plan = this.Planner.Plan(new InstallationPlanningRequest(
            InstallationAction.Uninstall,
            inventory,
            LauncherState.Assess(OwnershipTestData.Digest('f'), null, null)
        ));

        plan.Conflicts.Should().Contain(conflict => conflict.Code == PlanConflictCode.InstalledReceiptRequired);
        plan.Conflicts.Should().Contain(conflict => conflict.Code == PlanConflictCode.LegacyOwnershipUnconfirmed && conflict.Path!.Equals(legacyPath));
    }

    [Test]
    public void Backup_CapturesReceiptOwnedFilesButExcludesPreservedPrivateModData()
    {
        PackageManifest manifest = OwnershipTestData.Manifest(
            otherEntries: [OwnershipTestData.Entry("StardewModdingAPI", '2', OwnedEntryKind.RuntimeFile)]
        );
        InstallationReceipt receipt = OwnershipTestData.Receipt(manifest);
        PackageManifestEntry runtime = manifest.Entries.Single(entry => entry.Kind == OwnedEntryKind.RuntimeFile);
        NormalizedRelativePath preservedPath = OwnershipTestData.Path("Mods/PrivateMod/config.json");
        InstallationInventory inventory = InstallationInventory.Create(
            manifest,
            receipt,
            [
                OwnershipTestData.Current(runtime, digest: '9'),
                new CurrentFile(preservedPath, OwnershipTestData.Digest('8'), 420)
            ],
            preservedPaths: [preservedPath]
        );
        LauncherState launcher = LauncherState.Assess(receipt.Launcher.InstalledLauncherSha256, receipt.Launcher.OriginalLauncherSha256, receipt.Launcher);

        InstallationPlan plan = this.Planner.Plan(new InstallationPlanningRequest(
            InstallationAction.Backup,
            inventory,
            launcher,
            targetManifest: manifest,
            installedReceipt: receipt
        ));

        plan.CanExecute.Should().BeTrue();
        plan.Operations.Should().OnlyContain(operation => operation.Kind == PlanOperationKind.Backup);
        plan.Operations.Select(operation => operation.Path.Value).Should().BeEquivalentTo(
            "StardewModdingAPI",
            "StardewValley",
            "StardewValley-original"
        );
        plan.Operations.Should().NotContain(operation => operation.Path.Equals(preservedPath));
    }

    [Test]
    public void Rollback_RequiresExactReceiptAndCurrentHashes()
    {
        PackageManifest manifest = OwnershipTestData.Manifest(
            otherEntries:
            [
                OwnershipTestData.Entry("StardewModdingAPI", '2', OwnedEntryKind.RuntimeFile),
                OwnershipTestData.Entry("smapi-internal/new.dll", '3', OwnedEntryKind.InternalFile)
            ]
        );
        InstallationReceipt receipt = OwnershipTestData.Receipt(manifest);
        PackageManifestEntry runtime = manifest.Entries.Single(entry => entry.Kind == OwnedEntryKind.RuntimeFile);
        PackageManifestEntry created = manifest.Entries.Single(entry => entry.Path.Value == "smapi-internal/new.dll");
        InstallationInventory inventory = InstallationInventory.Create(
            null,
            receipt,
            [OwnershipTestData.Current(runtime), OwnershipTestData.Current(created)]
        );
        RollbackSnapshot snapshot = new(
            receipt.GetCanonicalDigest(),
            OwnershipTestData.Digest('7'),
            [
                new RollbackSnapshotEntry(runtime.Path, runtime.Kind, RollbackEntryKind.Restore, Identity(runtime), Identity('8', mode: runtime.UnixMode)),
                new RollbackSnapshotEntry(created.Path, created.Kind, RollbackEntryKind.Remove, Identity(created), null)
            ]
        );
        LauncherState launcher = LauncherState.Assess(receipt.Launcher.InstalledLauncherSha256, receipt.Launcher.OriginalLauncherSha256, receipt.Launcher);

        InstallationPlan valid = this.Planner.Plan(new InstallationPlanningRequest(
            InstallationAction.Rollback,
            inventory,
            launcher,
            installedReceipt: receipt,
            rollbackSnapshot: snapshot,
            recoveryObservations:
            [
                new RecoveryFileObservation(runtime.Path, Identity(runtime)),
                new RecoveryFileObservation(created.Path, Identity(created))
            ]
        ));
        InstallationInventory driftedInventory = InstallationInventory.Create(
            null,
            receipt,
            [OwnershipTestData.Current(runtime, digest: '9'), OwnershipTestData.Current(created)]
        );
        InstallationPlan drifted = this.Planner.Plan(new InstallationPlanningRequest(
            InstallationAction.Rollback,
            driftedInventory,
            launcher,
            installedReceipt: receipt,
            rollbackSnapshot: snapshot,
            recoveryObservations:
            [
                new RecoveryFileObservation(runtime.Path, Identity('9', mode: runtime.UnixMode)),
                new RecoveryFileObservation(created.Path, Identity(created))
            ]
        ));

        valid.CanExecute.Should().BeTrue();
        valid.Operations.Should().Contain(operation => operation.Path.Equals(runtime.Path) && operation.Kind == PlanOperationKind.Restore && operation.ResultSha256 == OwnershipTestData.Digest('8'));
        valid.Operations.Should().Contain(operation => operation.Path.Equals(created.Path) && operation.Kind == PlanOperationKind.Remove);
        drifted.Conflicts.Should().ContainSingle(conflict => conflict.Code == PlanConflictCode.RollbackDrift && conflict.Path!.Equals(runtime.Path));
    }

    private static RecoveryFileIdentity Identity(PackageManifestEntry entry)
        => new(entry.Sha256, entry.SizeBytes, entry.UnixMode);

    private static RecoveryFileIdentity Identity(char digest, long size = 10, int mode = 420)
        => new(OwnershipTestData.Digest(digest), size, mode);

    [Test]
    public void Plans_SortConflictsAndOperationsIndependentlyOfObservationOrder()
    {
        PackageManifest manifest = OwnershipTestData.Manifest(
            otherEntries:
            [
                OwnershipTestData.Entry("smapi-internal/b.dll", '2', OwnedEntryKind.InternalFile),
                OwnershipTestData.Entry("smapi-internal/a.dll", '3', OwnedEntryKind.InternalFile)
            ]
        );
        CurrentFile vanilla = new(OwnershipTestData.Path("StardewValley"), OwnershipTestData.Digest('f'), 493);
        CurrentFile[] collisions = manifest.Entries
            .Where(entry => entry.Kind != OwnedEntryKind.Launcher)
            .Select(entry => OwnershipTestData.Current(entry, digest: '9'))
            .Append(vanilla)
            .ToArray();

        InstallationPlan first = this.Planner.Plan(new InstallationPlanningRequest(
            InstallationAction.Install,
            InstallationInventory.Create(manifest, null, collisions),
            LauncherState.Assess(vanilla.Sha256, null, null),
            targetManifest: manifest
        ));
        InstallationPlan second = this.Planner.Plan(new InstallationPlanningRequest(
            InstallationAction.Install,
            InstallationInventory.Create(manifest, null, collisions.Reverse()),
            LauncherState.Assess(vanilla.Sha256, null, null),
            targetManifest: manifest
        ));

        first.ToCanonicalJson().Should().Be(second.ToCanonicalJson());
        first.Conflicts.Select(conflict => conflict.Path!.Value).Should().BeInAscendingOrder(StringComparer.Ordinal);
    }
}
