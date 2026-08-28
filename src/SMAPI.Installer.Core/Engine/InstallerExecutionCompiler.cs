using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Ownership.Persistence;
using StardewModdingAPI.Installer.Core.Planning;

namespace StardewModdingAPI.Installer.Core.Engine;

/// <summary>
/// Binds pure planner output to the state which produced it, then compiles every planner operation into a closed
/// source and preparation instruction without touching a path.
/// </summary>
public sealed class InstallerExecutionCompiler
{
    private static readonly NormalizedRelativePath LauncherPath = NormalizedRelativePath.Parse("StardewValley");
    private static readonly NormalizedRelativePath LauncherBackupPath = NormalizedRelativePath.Parse("StardewValley-original");
    private readonly InstallationPlanner Planner = new();

    /// <summary>Capture the exact canonical identities which must still agree when execution is prepared.</summary>
    public BoundInstallationPlan BindPlan(InstallationPlan plan, InstallationPlanningRequest request)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(request);
        if (!plan.CanExecute)
            throw Error(ExecutionCompilationError.NonExecutablePlan, "A plan with conflicts can't be bound for execution.");
        if (plan.Action != request.Action)
            throw Error(ExecutionCompilationError.PlanDoesNotMatchRequest, "The plan action doesn't match its planning request.");

        InstallationPlan expected = this.Planner.Plan(request);
        if (!expected.CanExecute || expected.GetCanonicalDigest() != plan.GetCanonicalDigest())
            throw Error(ExecutionCompilationError.PlanDoesNotMatchRequest, "The plan isn't the canonical plan for the supplied request.");

        return new BoundInstallationPlan(
            plan.Action,
            plan.GetCanonicalDigest(),
            request.TargetManifest?.GetCanonicalDigest(),
            request.InstalledReceipt?.GetCanonicalDigest(),
            GetRollbackSnapshotDigest(request.RollbackSnapshot)
        );
    }

    /// <summary>Compile a previously bound plan after proving that the complete planning state is still exact.</summary>
    public InstallationExecutionPreparation Prepare(
        BoundInstallationPlan binding,
        InstallationPlan plan,
        InstallationPlanningRequest currentRequest,
        Guid transactionId
    )
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(currentRequest);
        if (transactionId == Guid.Empty)
            throw new ArgumentException("A non-empty transaction ID is required.", nameof(transactionId));
        if (!plan.CanExecute)
            throw Error(ExecutionCompilationError.NonExecutablePlan, "A plan with conflicts can't be prepared for execution.");
        if (binding.Action != plan.Action || binding.Action != currentRequest.Action)
            throw Error(ExecutionCompilationError.StalePlan, "The bound action no longer matches the plan and request.");
        if (binding.PlanSha256 != plan.GetCanonicalDigest())
            throw Error(ExecutionCompilationError.StalePlan, "The canonical plan changed after it was bound.");

        AssertSameDigest(
            binding.ManifestSha256,
            currentRequest.TargetManifest?.GetCanonicalDigest(),
            ExecutionCompilationError.StaleManifest,
            "The target manifest changed after planning."
        );
        AssertSameDigest(
            binding.InstalledReceiptSha256,
            currentRequest.InstalledReceipt?.GetCanonicalDigest(),
            ExecutionCompilationError.StaleInstalledReceipt,
            "The installed receipt changed after planning."
        );
        AssertSameDigest(
            binding.RollbackSnapshotSha256,
            GetRollbackSnapshotDigest(currentRequest.RollbackSnapshot),
            ExecutionCompilationError.StaleRollbackSnapshot,
            "The rollback snapshot changed after planning."
        );

        InstallationPlan expected = this.Planner.Plan(currentRequest);
        if (!expected.CanExecute || expected.GetCanonicalDigest() != binding.PlanSha256)
            throw Error(ExecutionCompilationError.StalePlan, "The current state no longer produces the bound executable plan.");

        FilePreparationInstruction[] instructions = plan.Operations
            .Select(operation => this.CompileOperation(operation, currentRequest, binding))
            .ToArray();
        AssertUniqueSafeDestinations(instructions);
        ReceiptPreparationInstruction receipt = CreateReceiptInstruction(binding, currentRequest, plan, transactionId);
        return new InstallationExecutionPreparation(transactionId, binding, instructions, receipt);
    }

    private FilePreparationInstruction CompileOperation(
        PlannedOperation operation,
        InstallationPlanningRequest request,
        BoundInstallationPlan binding
    )
    {
        switch (operation.Kind)
        {
            case PlanOperationKind.Create:
            case PlanOperationKind.Replace:
                return this.CompileWrite(operation, request);

            case PlanOperationKind.Restore:
                return this.CompileRestore(operation, request, binding);

            case PlanOperationKind.Remove:
                ValidateRemove(operation, request);
                return new FilePreparationInstruction(operation, PreparationInstructionKind.RemoveTransactionDestination, null, null);

            case PlanOperationKind.Backup:
                return CompileBackup(operation);

            case PlanOperationKind.Retain:
            case PlanOperationKind.Preserve:
                if (operation.ExpectedCurrentSha256 is null || operation.ExpectedCurrentSha256 != operation.ResultSha256)
                    throw Error(ExecutionCompilationError.InvalidOperationMapping, "A no-change operation doesn't preserve its exact observed digest.");
                return new FilePreparationInstruction(operation, PreparationInstructionKind.VerifyUnchanged, null, null);

            default:
                throw Error(ExecutionCompilationError.InvalidOperationMapping, "The plan contains an unknown operation kind.");
        }
    }

    private FilePreparationInstruction CompileWrite(PlannedOperation operation, InstallationPlanningRequest request)
    {
        if (operation.ResultSha256 is null)
            throw Error(ExecutionCompilationError.InvalidOperationMapping, "A write operation has no result digest.");

        if (
            request.Action == InstallationAction.Install
            && operation.Kind == PlanOperationKind.Create
            && operation.Path.Equals(LauncherBackupPath)
        )
        {
            Sha256Digest launcher = request.Launcher.CurrentLauncherSha256
                ?? throw Error(ExecutionCompilationError.InvalidOperationMapping, "The fresh-install launcher backup has no current launcher source.");
            if (operation.ExpectedCurrentSha256 is not null || operation.ResultSha256 != launcher)
                throw Error(ExecutionCompilationError.InvalidOperationMapping, "The fresh-install launcher backup doesn't exactly copy the observed launcher.");
            return new FilePreparationInstruction(
                operation,
                PreparationInstructionKind.WriteTransactionDestination,
                new CurrentGameLauncherSource(CurrentGameLauncherRole.CurrentLauncher, LauncherPath, launcher),
                null
            );
        }

        PackageManifestEntry target = request.TargetManifest?.Entries.SingleOrDefault(entry => entry.Path.Equals(operation.Path))
            ?? throw Error(ExecutionCompilationError.InvalidOperationMapping, $"Write destination '{operation.Path}' isn't supplied by the verified manifest.");
        if (target.Sha256 != operation.ResultSha256)
            throw Error(ExecutionCompilationError.InvalidOperationMapping, $"Write destination '{operation.Path}' doesn't match its verified package digest.");
        return new FilePreparationInstruction(
            operation,
            PreparationInstructionKind.WriteTransactionDestination,
            new VerifiedPackageFileSource(target),
            target.UnixMode
        );
    }

    private FilePreparationInstruction CompileRestore(
        PlannedOperation operation,
        InstallationPlanningRequest request,
        BoundInstallationPlan binding
    )
    {
        if (operation.ResultSha256 is null)
            throw Error(ExecutionCompilationError.InvalidOperationMapping, "A restore operation has no result digest.");

        if (request.Action == InstallationAction.Uninstall && operation.Path.Equals(LauncherPath))
        {
            Sha256Digest original = request.Launcher.BackupLauncherSha256
                ?? throw Error(ExecutionCompilationError.InvalidOperationMapping, "The uninstall restore has no verified original-launcher backup.");
            if (operation.ResultSha256 != original)
                throw Error(ExecutionCompilationError.InvalidOperationMapping, "The uninstall restore doesn't match the verified original-launcher backup.");
            return new FilePreparationInstruction(
                operation,
                PreparationInstructionKind.WriteTransactionDestination,
                new CurrentGameLauncherSource(CurrentGameLauncherRole.OriginalLauncherBackup, LauncherBackupPath, original),
                null
            );
        }

        if (request.Action == InstallationAction.Rollback && request.RollbackSnapshot is not null && binding.RollbackSnapshotSha256 is not null)
        {
            RollbackSnapshotEntry snapshotEntry = request.RollbackSnapshot.Entries.SingleOrDefault(entry => entry.Path.Equals(operation.Path))
                ?? throw Error(ExecutionCompilationError.InvalidOperationMapping, $"Restore destination '{operation.Path}' isn't in the recovery snapshot.");
            if (snapshotEntry.Kind != RollbackEntryKind.Restore || snapshotEntry.BackupSha256 != operation.ResultSha256)
                throw Error(ExecutionCompilationError.InvalidOperationMapping, $"Restore destination '{operation.Path}' doesn't match its recovery snapshot entry.");
            return new FilePreparationInstruction(
                operation,
                PreparationInstructionKind.WriteTransactionDestination,
                new RecoverySnapshotSource(
                    binding.RollbackSnapshotSha256,
                    RecoverySnapshotContent.GameFile,
                    operation.Path,
                    operation.ResultSha256
                ),
                null
            );
        }

        throw Error(ExecutionCompilationError.InvalidOperationMapping, $"Restore destination '{operation.Path}' has no core-owned restore rule.");
    }

    private static FilePreparationInstruction CompileBackup(PlannedOperation operation)
    {
        if (operation.ExpectedCurrentSha256 is null || operation.ExpectedCurrentSha256 != operation.ResultSha256)
            throw Error(ExecutionCompilationError.InvalidOperationMapping, "A backup operation doesn't copy its exact observed digest.");

        PreparationSource source = operation.Path.Value switch
        {
            "StardewValley" => new CurrentGameLauncherSource(
                CurrentGameLauncherRole.CurrentLauncher,
                LauncherPath,
                operation.ExpectedCurrentSha256
            ),
            "StardewValley-original" => new CurrentGameLauncherSource(
                CurrentGameLauncherRole.OriginalLauncherBackup,
                LauncherBackupPath,
                operation.ExpectedCurrentSha256
            ),
            _ => new CurrentGameFileSource(operation.Path, operation.ExpectedCurrentSha256)
        };
        return new FilePreparationInstruction(operation, PreparationInstructionKind.CaptureRecoveryFile, source, null);
    }

    private static void ValidateRemove(PlannedOperation operation, InstallationPlanningRequest request)
    {
        if (operation.ExpectedCurrentSha256 is null || operation.ResultSha256 is not null)
            throw Error(ExecutionCompilationError.InvalidOperationMapping, "A remove operation must bind only its exact current digest.");

        if (request.Action == InstallationAction.Rollback)
        {
            RollbackSnapshotEntry snapshotEntry = request.RollbackSnapshot?.Entries.SingleOrDefault(entry => entry.Path.Equals(operation.Path))
                ?? throw Error(ExecutionCompilationError.InvalidOperationMapping, $"Remove destination '{operation.Path}' isn't in the recovery snapshot.");
            if (snapshotEntry.Kind != RollbackEntryKind.Remove || snapshotEntry.ExpectedCurrentSha256 != operation.ExpectedCurrentSha256)
                throw Error(ExecutionCompilationError.InvalidOperationMapping, $"Remove destination '{operation.Path}' doesn't match its recovery snapshot entry.");
            return;
        }

        if (request.Action == InstallationAction.Uninstall && operation.Path.Equals(LauncherBackupPath))
        {
            if (request.Launcher.BackupLauncherSha256 != operation.ExpectedCurrentSha256)
                throw Error(ExecutionCompilationError.InvalidOperationMapping, "The launcher-backup removal doesn't match the verified launcher state.");
            return;
        }

        InstallationReceiptEntry installed = request.InstalledReceipt?.Entries.SingleOrDefault(entry => entry.Path.Equals(operation.Path))
            ?? throw Error(ExecutionCompilationError.InvalidOperationMapping, $"Remove destination '{operation.Path}' isn't in the installed receipt.");
        if (installed.InstalledSha256 != operation.ExpectedCurrentSha256)
            throw Error(ExecutionCompilationError.InvalidOperationMapping, $"Remove destination '{operation.Path}' doesn't match the installed receipt.");
    }

    private static ReceiptPreparationInstruction CreateReceiptInstruction(
        BoundInstallationPlan binding,
        InstallationPlanningRequest request,
        InstallationPlan plan,
        Guid transactionId
    )
    {
        switch (request.Action)
        {
            case InstallationAction.Install:
            case InstallationAction.Update:
            case InstallationAction.Repair:
            {
                PackageManifest manifest = request.TargetManifest
                    ?? throw Error(ExecutionCompilationError.InvalidOperationMapping, "A receipt-writing action has no target manifest.");
                Sha256Digest originalLauncher = request.Action == InstallationAction.Install
                    ? plan.Operations.Single(operation => operation.Kind == PlanOperationKind.Create && operation.Path.Equals(LauncherBackupPath)).ResultSha256!
                    : request.InstalledReceipt?.Launcher.OriginalLauncherSha256
                        ?? throw Error(ExecutionCompilationError.InvalidOperationMapping, "A receipt update has no original-launcher identity.");
                PackageManifestEntry launcher = manifest.Entries.Single(entry => entry.Kind == OwnedEntryKind.Launcher);
                InstallationReceipt generated = new(
                    manifest.Release,
                    manifest.GetCanonicalDigest(),
                    transactionId.ToString("N"),
                    manifest.Entries.Select(entry => new InstallationReceiptEntry(entry.Path, entry.Sha256, entry.UnixMode, entry.Kind)),
                    new LauncherReceipt(launcher.Sha256, originalLauncher)
                );
                GeneratedCanonicalReceiptSource source = new(
                    generated,
                    CanonicalOwnershipDocuments.SerializeReceipt(generated)
                );
                return new ReceiptPreparationInstruction(
                    ReceiptPreparationKind.WriteAtomically,
                    binding.InstalledReceiptSha256,
                    source
                );
            }

            case InstallationAction.Uninstall:
                if (binding.InstalledReceiptSha256 is null)
                    throw Error(ExecutionCompilationError.InvalidOperationMapping, "An uninstall has no installed receipt to remove.");
                return new ReceiptPreparationInstruction(
                    ReceiptPreparationKind.RemoveAtomically,
                    binding.InstalledReceiptSha256,
                    null
                );

            case InstallationAction.Backup:
                return new ReceiptPreparationInstruction(ReceiptPreparationKind.None, null, null);

            case InstallationAction.Rollback:
            {
                RollbackSnapshot snapshot = request.RollbackSnapshot
                    ?? throw Error(ExecutionCompilationError.InvalidOperationMapping, "A rollback has no recovery snapshot.");
                Sha256Digest snapshotSha256 = binding.RollbackSnapshotSha256
                    ?? throw Error(ExecutionCompilationError.InvalidOperationMapping, "A rollback has no exact recovery-snapshot identity.");
                if (snapshot.PreviousReceiptSha256 is null)
                {
                    if (binding.InstalledReceiptSha256 is null)
                        throw Error(ExecutionCompilationError.InvalidOperationMapping, "A receipt-removing rollback has no current receipt identity.");
                    return new ReceiptPreparationInstruction(
                        ReceiptPreparationKind.RemoveAtomically,
                        binding.InstalledReceiptSha256,
                        null
                    );
                }

                return new ReceiptPreparationInstruction(
                    ReceiptPreparationKind.WriteAtomically,
                    binding.InstalledReceiptSha256,
                    new RecoverySnapshotSource(
                        snapshotSha256,
                        RecoverySnapshotContent.InstalledReceipt,
                        null,
                        snapshot.PreviousReceiptSha256
                    )
                );
            }

            default:
                throw Error(ExecutionCompilationError.InvalidOperationMapping, "The action has no receipt-state rule.");
        }
    }

    private static void AssertUniqueSafeDestinations(IEnumerable<FilePreparationInstruction> instructions)
    {
        HashSet<string> exact = new(StringComparer.Ordinal);
        HashSet<string> insensitive = new(StringComparer.OrdinalIgnoreCase);
        foreach (FilePreparationInstruction instruction in instructions.Where(instruction => instruction.IsTransactionDestination))
        {
            if (!OwnedNamespacePolicy.IsAllowedTransactionDestination(instruction.Path))
                throw Error(ExecutionCompilationError.UnsafeDestination, $"Transaction destination '{instruction.Path}' isn't allowed.");
            if (!exact.Add(instruction.Path.Value) || !insensitive.Add(instruction.Path.Value))
                throw Error(ExecutionCompilationError.DuplicateDestination, $"Transaction destination '{instruction.Path}' isn't unique.");
        }
    }

    private static Sha256Digest? GetRollbackSnapshotDigest(RollbackSnapshot? snapshot)
    {
        return snapshot is null
            ? null
            : Sha256Digest.Hash(CanonicalOwnershipDocuments.SerializeRollbackSnapshot(snapshot));
    }

    private static void AssertSameDigest(
        Sha256Digest? expected,
        Sha256Digest? actual,
        ExecutionCompilationError error,
        string message
    )
    {
        if (expected != actual)
            throw Error(error, message);
    }

    private static ExecutionCompilationException Error(ExecutionCompilationError error, string message)
    {
        return new ExecutionCompilationException(error, message);
    }
}
