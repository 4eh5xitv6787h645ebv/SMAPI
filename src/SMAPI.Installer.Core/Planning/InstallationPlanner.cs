using StardewModdingAPI.Installer.Core.Ownership;

namespace StardewModdingAPI.Installer.Core.Planning;

/// <summary>Creates deterministic, side-effect-free ownership plans for every supported installer action.</summary>
internal sealed class InstallationPlanner
{
    private static readonly NormalizedRelativePath LauncherPath = NormalizedRelativePath.Parse("StardewValley");
    private static readonly NormalizedRelativePath LauncherBackupPath = NormalizedRelativePath.Parse("StardewValley-original");

    /// <summary>Create a complete immutable plan without touching the filesystem.</summary>
    public InstallationPlan Plan(InstallationPlanningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<PlannedOperation> operations = new();
        List<PlanConflict> conflicts = new();

        switch (request.Action)
        {
            case InstallationAction.Install:
                this.PlanInstall(request, operations, conflicts);
                break;
            case InstallationAction.Update:
                this.PlanUpdate(request, operations, conflicts);
                break;
            case InstallationAction.Repair:
                this.PlanRepair(request, operations, conflicts);
                break;
            case InstallationAction.Uninstall:
                this.PlanUninstall(request, operations, conflicts);
                break;
            case InstallationAction.Backup:
                this.PlanBackup(request, operations, conflicts);
                break;
            case InstallationAction.Rollback:
                this.PlanRollback(request, operations, conflicts);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }

        return new InstallationPlan(request.Action, operations, conflicts);
    }

    private void PlanInstall(InstallationPlanningRequest request, List<PlannedOperation> operations, List<PlanConflict> conflicts)
    {
        if (request.TargetManifest == null)
        {
            conflicts.Add(new PlanConflict(PlanConflictCode.TargetManifestRequired));
            return;
        }
        if (request.InstalledReceipt != null)
        {
            conflicts.Add(new PlanConflict(PlanConflictCode.ExistingInstallationRequiresUpdate));
            return;
        }

        this.PlanLauncherInstallOrUpdate(request.TargetManifest, request.Launcher, isFreshInstall: true, operations, conflicts);
        foreach (InventoryEntry entry in request.Inventory.Entries.Where(entry => entry.Path.Value != "StardewValley"))
        {
            if (entry.Target == null)
            {
                this.PreserveCurrent(entry, operations);
                continue;
            }

            switch (entry.Classification)
            {
                case InventoryClassification.Absent:
                    operations.Add(Create(entry));
                    break;
                case InventoryClassification.Preserved:
                    operations.Add(Preserve(entry));
                    conflicts.Add(new PlanConflict(PlanConflictCode.PreservedTargetCollision, entry.Path));
                    break;
                case InventoryClassification.Legacy:
                    conflicts.Add(new PlanConflict(PlanConflictCode.LegacyOwnershipUnconfirmed, entry.Path));
                    break;
                case InventoryClassification.UnknownCollision:
                    conflicts.Add(new PlanConflict(PlanConflictCode.UnknownCollision, entry.Path));
                    break;
                case InventoryClassification.UnchangedOwned:
                case InventoryClassification.ModifiedOwned:
                    conflicts.Add(new PlanConflict(PlanConflictCode.ExistingInstallationRequiresUpdate, entry.Path));
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }

    private void PlanUpdate(InstallationPlanningRequest request, List<PlannedOperation> operations, List<PlanConflict> conflicts)
    {
        if (!this.AssertManifestAndReceiptPresent(request, conflicts))
            return;

        this.PlanLauncherInstallOrUpdate(request.TargetManifest!, request.Launcher, isFreshInstall: false, operations, conflicts);
        foreach (InventoryEntry entry in request.Inventory.Entries.Where(entry => entry.Path.Value != "StardewValley"))
            this.PlanUpdateEntry(entry, operations, conflicts);
    }

    private void PlanRepair(InstallationPlanningRequest request, List<PlannedOperation> operations, List<PlanConflict> conflicts)
    {
        if (!this.AssertManifestAndReceiptPresent(request, conflicts))
            return;
        if (
            !request.TargetManifest!.Release.Equals(request.InstalledReceipt!.Release)
            || request.TargetManifest.GetCanonicalDigest() != request.InstalledReceipt.ManifestSha256
        )
        {
            conflicts.Add(new PlanConflict(PlanConflictCode.ReleaseDoesNotMatchReceipt));
            return;
        }

        this.PlanLauncherInstallOrUpdate(request.TargetManifest, request.Launcher, isFreshInstall: false, operations, conflicts);
        foreach (InventoryEntry entry in request.Inventory.Entries.Where(entry => entry.Path.Value != "StardewValley"))
        {
            if (entry.Target != null && entry.Installed != null)
            {
                switch (entry.Classification)
                {
                    case InventoryClassification.UnchangedOwned:
                        operations.Add(Retain(entry));
                        break;
                    case InventoryClassification.Absent:
                        operations.Add(Create(entry));
                        break;
                    case InventoryClassification.ModifiedOwned:
                        conflicts.Add(new PlanConflict(PlanConflictCode.ModifiedOwnedFile, entry.Path));
                        break;
                    default:
                        this.AddCollisionConflict(entry, conflicts);
                        break;
                }
            }
            else if (entry.Target != null || entry.Installed != null)
                conflicts.Add(new PlanConflict(PlanConflictCode.ReceiptDoesNotMatchManifest, entry.Path));
            else
                this.PreserveCurrent(entry, operations);
        }
    }

    private void PlanUninstall(InstallationPlanningRequest request, List<PlannedOperation> operations, List<PlanConflict> conflicts)
    {
        if (request.InstalledReceipt == null)
        {
            conflicts.Add(new PlanConflict(PlanConflictCode.InstalledReceiptRequired));
            foreach (InventoryEntry entry in request.Inventory.Entries.Where(entry => entry.Classification == InventoryClassification.Legacy))
                conflicts.Add(new PlanConflict(PlanConflictCode.LegacyOwnershipUnconfirmed, entry.Path));
            return;
        }

        this.PlanLauncherUninstall(request.Launcher, operations, conflicts);
        foreach (InventoryEntry entry in request.Inventory.Entries.Where(entry => entry.Path.Value != "StardewValley"))
        {
            if (entry.Installed == null)
            {
                this.PreserveCurrent(entry, operations);
                continue;
            }

            switch (entry.Classification)
            {
                case InventoryClassification.UnchangedOwned:
                    operations.Add(new PlannedOperation(PlanOperationKind.Remove, entry.Path, entry.Current!.Sha256, null));
                    break;
                case InventoryClassification.Absent:
                    break;
                case InventoryClassification.ModifiedOwned:
                    conflicts.Add(new PlanConflict(PlanConflictCode.ModifiedOwnedFile, entry.Path));
                    break;
                default:
                    this.AddCollisionConflict(entry, conflicts);
                    break;
            }
        }
    }

    private void PlanBackup(
        InstallationPlanningRequest request,
        List<PlannedOperation> operations,
        List<PlanConflict> conflicts
    )
    {
        if (request.InstalledReceipt is null)
        {
            conflicts.Add(new PlanConflict(PlanConflictCode.InstalledReceiptRequired));
            return;
        }
        HashSet<string> planned = new(StringComparer.Ordinal);
        foreach (InventoryEntry entry in request.Inventory.Entries.Where(entry => entry.Installed is not null))
        {
            if (entry.Classification != InventoryClassification.UnchangedOwned || entry.Current is null)
            {
                conflicts.Add(new PlanConflict(PlanConflictCode.ModifiedOwnedFile, entry.Path));
                continue;
            }
            if (planned.Add(entry.Path.Value))
                operations.Add(new PlannedOperation(PlanOperationKind.Backup, entry.Path, entry.Current.Sha256, entry.Current.Sha256));
        }

        if (request.Launcher.Classification != LauncherClassification.InstalledUnchanged)
        {
            conflicts.Add(new PlanConflict(PlanConflictCode.ModifiedInstalledLauncher, InstallationPlanner.LauncherPath));
            return;
        }
        if (request.Launcher.CurrentLauncherSha256 is not null && planned.Add(InstallationPlanner.LauncherPath.Value))
        {
            operations.Add(new PlannedOperation(
                PlanOperationKind.Backup,
                InstallationPlanner.LauncherPath,
                request.Launcher.CurrentLauncherSha256,
                request.Launcher.CurrentLauncherSha256
            ));
        }
        if (request.Launcher.BackupLauncherSha256 is not null && planned.Add(InstallationPlanner.LauncherBackupPath.Value))
        {
            operations.Add(new PlannedOperation(
                PlanOperationKind.Backup,
                InstallationPlanner.LauncherBackupPath,
                request.Launcher.BackupLauncherSha256,
                request.Launcher.BackupLauncherSha256
            ));
        }
    }

    private void PlanRollback(InstallationPlanningRequest request, List<PlannedOperation> operations, List<PlanConflict> conflicts)
    {
        if (request.RollbackSnapshot == null)
        {
            conflicts.Add(new PlanConflict(PlanConflictCode.RollbackSnapshotRequired));
            return;
        }
        if (request.RollbackOriginAction == InstallationAction.Backup)
        {
            this.PlanUserBackupRollback(request, operations, conflicts);
            return;
        }
        Sha256Digest? observedReceiptSha256 = request.InstalledReceipt?.GetCanonicalDigest();
        if (observedReceiptSha256 != request.RollbackSnapshot.ExpectedCurrentReceiptSha256)
        {
            conflicts.Add(new PlanConflict(PlanConflictCode.RollbackReceiptMismatch));
            return;
        }

        Dictionary<string, RecoveryFileObservation> current = request.RecoveryObservations
            .ToDictionary(entry => entry.Path.Value, StringComparer.Ordinal);
        foreach (RollbackSnapshotEntry entry in request.RollbackSnapshot.Entries)
        {
            if (!current.TryGetValue(entry.Path.Value, out RecoveryFileObservation? observed) || observed.Identity != entry.ExpectedCurrent)
            {
                conflicts.Add(new PlanConflict(PlanConflictCode.RollbackDrift, entry.Path));
                continue;
            }

            operations.Add(new PlannedOperation(
                entry.Kind == RollbackEntryKind.Restore ? PlanOperationKind.Restore : PlanOperationKind.Remove,
                entry.Path,
                entry.ExpectedCurrentSha256,
                entry.BackupSha256
            ));
        }
    }

    private void PlanUserBackupRollback(
        InstallationPlanningRequest request,
        List<PlannedOperation> operations,
        List<PlanConflict> conflicts
    )
    {
        RollbackSnapshot snapshot = request.RollbackSnapshot!;
        InstallationReceipt? currentReceipt = request.InstalledReceipt;
        if (currentReceipt is null)
        {
            conflicts.Add(new PlanConflict(PlanConflictCode.RollbackReceiptMismatch));
            return;
        }
        Dictionary<string, RecoveryFileObservation> observed = request.RecoveryObservations
            .ToDictionary(item => item.Path.Value, StringComparer.Ordinal);
        HashSet<string> planned = new(StringComparer.Ordinal);
        foreach (RollbackSnapshotEntry target in snapshot.Entries)
        {
            observed.TryGetValue(target.Path.Value, out RecoveryFileObservation? current);
            if (!IsSafeCurrentOwnedState(request, target.Path, current?.Identity))
            {
                conflicts.Add(new PlanConflict(PlanConflictCode.RollbackDrift, target.Path));
                continue;
            }
            operations.Add(new PlannedOperation(
                PlanOperationKind.Restore,
                target.Path,
                current?.Identity?.Sha256,
                target.BackupSha256
            ));
            planned.Add(target.Path.Value);
        }
        foreach (InstallationReceiptEntry installed in currentReceipt.Entries.Where(entry => !planned.Contains(entry.Path.Value)))
        {
            observed.TryGetValue(installed.Path.Value, out RecoveryFileObservation? current);
            if (!IsSafeCurrentOwnedState(request, installed.Path, current?.Identity))
            {
                conflicts.Add(new PlanConflict(PlanConflictCode.RollbackDrift, installed.Path));
                continue;
            }
            if (current?.Identity is not null)
                operations.Add(new PlannedOperation(PlanOperationKind.Remove, installed.Path, current.Identity.Sha256, null));
        }
    }

    private static bool IsSafeCurrentOwnedState(
        InstallationPlanningRequest request,
        NormalizedRelativePath path,
        RecoveryFileIdentity? current
    )
    {
        if (current is null)
            return true;
        if (path.Equals(LauncherBackupPath))
        {
            return request.Launcher.BackupLauncherSha256 == current.Sha256
                && request.Launcher.BackupLauncherUnixMode == current.UnixMode
                && request.InstalledReceipt?.Launcher.OriginalLauncherSha256 == current.Sha256
                && request.InstalledReceipt.Launcher.OriginalLauncherUnixMode == current.UnixMode;
        }
        InstallationReceiptEntry? installed = request.InstalledReceipt?.Entries.SingleOrDefault(entry => entry.Path.Equals(path));
        return installed is not null
            && installed.InstalledSha256 == current.Sha256
            && installed.UnixMode == current.UnixMode;
    }

    private bool AssertManifestAndReceiptPresent(InstallationPlanningRequest request, List<PlanConflict> conflicts)
    {
        if (request.TargetManifest == null)
            conflicts.Add(new PlanConflict(PlanConflictCode.TargetManifestRequired));
        if (request.InstalledReceipt == null)
            conflicts.Add(new PlanConflict(PlanConflictCode.InstalledReceiptRequired));
        return request.TargetManifest != null && request.InstalledReceipt != null;
    }

    private void PlanUpdateEntry(InventoryEntry entry, List<PlannedOperation> operations, List<PlanConflict> conflicts)
    {
        if (entry.Target != null && entry.Installed != null)
        {
            switch (entry.Classification)
            {
                case InventoryClassification.UnchangedOwned:
                    if (
                        entry.Current!.Sha256 == entry.Target.Sha256
                        && entry.Current.UnixMode == entry.Target.UnixMode
                    )
                        operations.Add(Retain(entry));
                    else
                        operations.Add(Replace(entry));
                    return;
                case InventoryClassification.Absent:
                    operations.Add(Create(entry));
                    return;
                case InventoryClassification.ModifiedOwned:
                    conflicts.Add(new PlanConflict(PlanConflictCode.ModifiedOwnedFile, entry.Path));
                    return;
                default:
                    this.AddCollisionConflict(entry, conflicts);
                    return;
            }
        }

        if (entry.Target != null)
        {
            switch (entry.Classification)
            {
                case InventoryClassification.Absent:
                    operations.Add(Create(entry));
                    break;
                default:
                    this.AddCollisionConflict(entry, conflicts);
                    break;
            }
            return;
        }

        if (entry.Installed != null)
        {
            switch (entry.Classification)
            {
                case InventoryClassification.UnchangedOwned:
                    operations.Add(new PlannedOperation(PlanOperationKind.Remove, entry.Path, entry.Current!.Sha256, null));
                    break;
                case InventoryClassification.Absent:
                    break;
                case InventoryClassification.ModifiedOwned:
                    conflicts.Add(new PlanConflict(PlanConflictCode.ModifiedOwnedFile, entry.Path));
                    break;
                default:
                    this.AddCollisionConflict(entry, conflicts);
                    break;
            }
            return;
        }

        this.PreserveCurrent(entry, operations);
    }

    private void PlanLauncherInstallOrUpdate(
        PackageManifest manifest,
        LauncherState launcher,
        bool isFreshInstall,
        List<PlannedOperation> operations,
        List<PlanConflict> conflicts
    )
    {
        PackageManifestEntry target = manifest.Entries.Single(entry => entry.Kind == OwnedEntryKind.Launcher);
        switch (launcher.Classification)
        {
            case LauncherClassification.FreshVanilla when isFreshInstall:
                operations.Add(new PlannedOperation(PlanOperationKind.Create, InstallationPlanner.LauncherBackupPath, null, launcher.CurrentLauncherSha256));
                operations.Add(new PlannedOperation(PlanOperationKind.Replace, InstallationPlanner.LauncherPath, launcher.CurrentLauncherSha256, target.Sha256));
                break;
            case LauncherClassification.InstalledUnchanged when !isFreshInstall:
                operations.Add(launcher.CurrentLauncherSha256 == target.Sha256
                    ? new PlannedOperation(PlanOperationKind.Retain, InstallationPlanner.LauncherPath, launcher.CurrentLauncherSha256, target.Sha256)
                    : new PlannedOperation(PlanOperationKind.Replace, InstallationPlanner.LauncherPath, launcher.CurrentLauncherSha256, target.Sha256));
                break;
            case LauncherClassification.InstalledLauncherMissing when !isFreshInstall:
                operations.Add(new PlannedOperation(PlanOperationKind.Create, InstallationPlanner.LauncherPath, null, target.Sha256));
                break;
            case LauncherClassification.MissingGameLauncher:
                conflicts.Add(new PlanConflict(PlanConflictCode.MissingGameLauncher, InstallationPlanner.LauncherPath));
                break;
            case LauncherClassification.InstalledModified:
                conflicts.Add(new PlanConflict(PlanConflictCode.ModifiedInstalledLauncher, InstallationPlanner.LauncherPath));
                break;
            case LauncherClassification.AmbiguousBackup:
                conflicts.Add(new PlanConflict(PlanConflictCode.AmbiguousLauncherBackup, InstallationPlanner.LauncherBackupPath));
                break;
            case LauncherClassification.MissingOriginalBackup:
                conflicts.Add(new PlanConflict(PlanConflictCode.MissingOriginalLauncherBackup, InstallationPlanner.LauncherBackupPath));
                break;
            default:
                conflicts.Add(new PlanConflict(
                    isFreshInstall ? PlanConflictCode.ExistingInstallationRequiresUpdate : PlanConflictCode.InstalledReceiptRequired,
                    InstallationPlanner.LauncherPath
                ));
                break;
        }
    }

    private void PlanLauncherUninstall(LauncherState launcher, List<PlannedOperation> operations, List<PlanConflict> conflicts)
    {
        switch (launcher.Classification)
        {
            case LauncherClassification.InstalledUnchanged:
            case LauncherClassification.InstalledLauncherMissing:
                operations.Add(new PlannedOperation(
                    PlanOperationKind.Restore,
                    InstallationPlanner.LauncherPath,
                    launcher.CurrentLauncherSha256,
                    launcher.BackupLauncherSha256
                ));
                operations.Add(new PlannedOperation(
                    PlanOperationKind.Remove,
                    InstallationPlanner.LauncherBackupPath,
                    launcher.BackupLauncherSha256,
                    null
                ));
                break;
            case LauncherClassification.InstalledModified:
                conflicts.Add(new PlanConflict(PlanConflictCode.ModifiedInstalledLauncher, InstallationPlanner.LauncherPath));
                break;
            case LauncherClassification.AmbiguousBackup:
                conflicts.Add(new PlanConflict(PlanConflictCode.AmbiguousLauncherBackup, InstallationPlanner.LauncherBackupPath));
                break;
            case LauncherClassification.MissingOriginalBackup:
                conflicts.Add(new PlanConflict(PlanConflictCode.MissingOriginalLauncherBackup, InstallationPlanner.LauncherBackupPath));
                break;
            default:
                conflicts.Add(new PlanConflict(PlanConflictCode.InstalledReceiptRequired, InstallationPlanner.LauncherPath));
                break;
        }
    }

    private void AddCollisionConflict(InventoryEntry entry, List<PlanConflict> conflicts)
    {
        PlanConflictCode code = entry.Classification switch
        {
            InventoryClassification.ModifiedOwned => PlanConflictCode.ModifiedOwnedFile,
            InventoryClassification.Legacy => PlanConflictCode.LegacyOwnershipUnconfirmed,
            InventoryClassification.Preserved => PlanConflictCode.PreservedTargetCollision,
            _ => PlanConflictCode.UnknownCollision
        };
        conflicts.Add(new PlanConflict(code, entry.Path));
    }

    private void PreserveCurrent(InventoryEntry entry, List<PlannedOperation> operations)
    {
        if (entry.Current != null)
            operations.Add(Preserve(entry));
    }

    private static PlannedOperation Create(InventoryEntry entry) => new(PlanOperationKind.Create, entry.Path, null, entry.Target!.Sha256);
    private static PlannedOperation Replace(InventoryEntry entry) => new(PlanOperationKind.Replace, entry.Path, entry.Current!.Sha256, entry.Target!.Sha256);
    private static PlannedOperation Retain(InventoryEntry entry) => new(PlanOperationKind.Retain, entry.Path, entry.Current!.Sha256, entry.Current.Sha256);
    private static PlannedOperation Preserve(InventoryEntry entry) => new(PlanOperationKind.Preserve, entry.Path, entry.Current!.Sha256, entry.Current.Sha256);
}
