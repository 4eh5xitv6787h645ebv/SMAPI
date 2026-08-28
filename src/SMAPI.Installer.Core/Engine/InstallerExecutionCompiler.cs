using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Ownership.Persistence;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Planning;

namespace StardewModdingAPI.Installer.Core.Engine;

/// <summary>
/// Binds pure planner output to the state which produced it, then compiles every planner operation into a closed
/// source and preparation instruction without touching a path.
/// </summary>
internal sealed class InstallerExecutionCompiler
{
    private static readonly NormalizedRelativePath LauncherPath = NormalizedRelativePath.Parse("StardewValley");
    private static readonly NormalizedRelativePath LauncherBackupPath = NormalizedRelativePath.Parse("StardewValley-original");
    private readonly InstallationPlanner Planner = new();

    /// <summary>Capture the exact canonical identities which must still agree when execution is prepared.</summary>
    internal BoundInstallationPlan BindPlan(
        InstallationPlan plan,
        InstallationPlanningRequest request,
        GameRootIdentity gameRoot,
        ulong operationGeneration,
        IVerifiedPackageContentAuthority? targetPackageContent,
        ICommittedRecoveryContentAuthority? rollbackContent = null,
        Sha256Digest? currentRecoveryPointerSha256 = null
    )
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(gameRoot);
        if (!plan.CanExecute)
            throw Error(ExecutionCompilationError.NonExecutablePlan, "A plan with conflicts can't be bound for execution.");
        if (plan.Action != request.Action)
            throw Error(ExecutionCompilationError.PlanDoesNotMatchRequest, "The plan action doesn't match its planning request.");
        bool requiresTarget = request.Action is InstallationAction.Install or InstallationAction.Update or InstallationAction.Repair;
        if (requiresTarget)
        {
            if (
                targetPackageContent is null
                || request.TargetManifest is null
                || !ReferenceEquals(targetPackageContent.Manifest, request.TargetManifest)
                || targetPackageContent.ManifestSha256 != request.TargetManifest.GetCanonicalDigest()
            )
            {
                throw Error(ExecutionCompilationError.StaleManifest, "The target manifest isn't backed by its live verified package-content authority.");
            }
            targetPackageContent.AssertUsable();
        }
        else if (targetPackageContent is not null)
            throw Error(ExecutionCompilationError.InvalidOperationMapping, "This action must not invent a target package-content authority.");
        if (request.Action == InstallationAction.Rollback)
        {
            if (
                rollbackContent is null
                || request.RollbackSnapshot is null
                || !ReferenceEquals(rollbackContent.Snapshot, request.RollbackSnapshot)
                || rollbackContent.SnapshotSha256 != GetRollbackSnapshotDigest(request.RollbackSnapshot)
                || rollbackContent.GameRoot != gameRoot
                || rollbackContent.OriginAction != request.RollbackOriginAction
                || rollbackContent.AuthorizedHeadPointerSha256 != currentRecoveryPointerSha256
            )
            {
                throw Error(ExecutionCompilationError.StaleRollbackSnapshot, "The rollback snapshot isn't backed by a live committed recovery authority for this game root.");
            }
            rollbackContent.AssertUsable();
        }
        else if (rollbackContent is not null)
            throw Error(ExecutionCompilationError.InvalidOperationMapping, "This action must not invent a committed recovery authority.");

        InstallationPlan expected = this.Planner.Plan(request);
        if (!expected.CanExecute || expected.GetCanonicalDigest() != plan.GetCanonicalDigest())
            throw Error(ExecutionCompilationError.PlanDoesNotMatchRequest, "The plan isn't the canonical plan for the supplied request.");

        ValidateRecoveryObservations(plan, request);

        return new BoundInstallationPlan(
            plan.Action,
            gameRoot,
            operationGeneration,
            plan.GetCanonicalDigest(),
            request.TargetManifest?.GetCanonicalDigest(),
            request.InstalledReceipt?.GetCanonicalDigest(),
            request.InstalledReceipt?.ManifestSha256,
            GetRollbackSnapshotDigest(request.RollbackSnapshot),
            GetRecoveryObservationsDigest(request.RecoveryObservations),
            rollbackContent?.GenerationId,
            currentRecoveryPointerSha256,
            targetPackageContent,
            rollbackContent
        );
    }

    /// <summary>Compile a previously bound plan after proving that the complete planning state is still exact.</summary>
    internal InstallationExecutionPreparation Prepare(
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
        binding.TargetPackageContent?.AssertUsable();
        binding.RollbackContent?.AssertUsable();

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
        AssertSameDigest(
            binding.RecoveryObservationsSha256,
            GetRecoveryObservationsDigest(currentRequest.RecoveryObservations),
            ExecutionCompilationError.StalePlan,
            "The recovery observations changed after planning."
        );

        InstallationPlan expected = this.Planner.Plan(currentRequest);
        if (!expected.CanExecute || expected.GetCanonicalDigest() != binding.PlanSha256)
            throw Error(ExecutionCompilationError.StalePlan, "The current state no longer produces the bound executable plan.");

        ValidateRecoveryObservations(plan, currentRequest);
        Dictionary<string, RecoveryFileObservation> observations = currentRequest.RecoveryObservations
            .ToDictionary(observation => observation.Path.Value, StringComparer.Ordinal);
        FilePreparationInstruction[] instructions = plan.Operations
            .Select(operation => this.CompileOperation(operation, currentRequest, binding, observations))
            .ToArray();
        AssertUniqueSafeDestinations(instructions);
        ReceiptPreparationInstruction receipt = CreateReceiptInstruction(binding, currentRequest, plan, transactionId);
        ManifestPreparationInstruction manifest = CreateManifestInstruction(binding, currentRequest);
        RecoverySnapshotPreparation? recovery = RequiresNewRecoverySnapshot(plan.Action)
            ? CreateRecoverySnapshotPreparation(currentRequest, instructions, receipt, observations)
            : null;
        return new InstallationExecutionPreparation(transactionId, binding, instructions, manifest, receipt, recovery);
    }

    private FilePreparationInstruction CompileOperation(
        PlannedOperation operation,
        InstallationPlanningRequest request,
        BoundInstallationPlan binding,
        IReadOnlyDictionary<string, RecoveryFileObservation> observations
    )
    {
        switch (operation.Kind)
        {
            case PlanOperationKind.Create:
            case PlanOperationKind.Replace:
                return this.CompileWrite(operation, request, binding, observations);

            case PlanOperationKind.Restore:
                return this.CompileRestore(operation, request, binding, observations);

            case PlanOperationKind.Remove:
                ValidateRemove(operation, request);
                return new FilePreparationInstruction(
                    operation,
                    PreparationInstructionKind.RemoveTransactionDestination,
                    null,
                    null,
                    expectedCurrentIdentity: GetObservedIdentity(observations, operation.Path)
                );

            case PlanOperationKind.Backup:
                return CompileBackup(operation, observations);

            case PlanOperationKind.Retain:
            case PlanOperationKind.Preserve:
                if (operation.ExpectedCurrentSha256 is null || operation.ExpectedCurrentSha256 != operation.ResultSha256)
                    throw Error(ExecutionCompilationError.InvalidOperationMapping, "A no-change operation doesn't preserve its exact observed digest.");
                return new FilePreparationInstruction(
                    operation,
                    PreparationInstructionKind.VerifyUnchanged,
                    null,
                    null,
                    expectedCurrentIdentity: GetObservedIdentity(observations, operation.Path)
                );

            default:
                throw Error(ExecutionCompilationError.InvalidOperationMapping, "The plan contains an unknown operation kind.");
        }
    }

    private FilePreparationInstruction CompileWrite(
        PlannedOperation operation,
        InstallationPlanningRequest request,
        BoundInstallationPlan binding,
        IReadOnlyDictionary<string, RecoveryFileObservation> observations
    )
    {
        if (operation.ResultSha256 is null)
            throw Error(ExecutionCompilationError.InvalidOperationMapping, "A write operation has no result digest.");

        if (
            request.Action == InstallationAction.Install
            && operation.Kind == PlanOperationKind.Create
            && operation.Path.Equals(LauncherBackupPath)
        )
        {
            RecoveryFileIdentity launcher = GetRequiredPresentObservation(observations, LauncherPath);
            if (operation.ExpectedCurrentSha256 is not null || operation.ResultSha256 != launcher.Sha256)
                throw Error(ExecutionCompilationError.InvalidOperationMapping, "The fresh-install launcher backup doesn't exactly copy the observed launcher.");
            return new FilePreparationInstruction(
                operation,
                PreparationInstructionKind.WriteTransactionDestination,
                new CurrentGameLauncherSource(CurrentGameLauncherRole.CurrentLauncher, LauncherPath, launcher),
                launcher.UnixMode,
                launcher.SizeBytes,
                launcher.FileType,
                GetObservedIdentity(observations, operation.Path)
            );
        }

        PackageManifestEntry target = request.TargetManifest?.Entries.SingleOrDefault(entry => entry.Path.Equals(operation.Path))
            ?? throw Error(ExecutionCompilationError.InvalidOperationMapping, $"Write destination '{operation.Path}' isn't supplied by the verified manifest.");
        if (target.Sha256 != operation.ResultSha256)
            throw Error(ExecutionCompilationError.InvalidOperationMapping, $"Write destination '{operation.Path}' doesn't match its verified package digest.");
        return new FilePreparationInstruction(
            operation,
            PreparationInstructionKind.WriteTransactionDestination,
            new VerifiedPackageFileSource(
                target,
                binding.TargetPackageContent
                    ?? throw Error(ExecutionCompilationError.StaleManifest, "A package write has no live verified content authority.")
            ),
            target.UnixMode,
            target.SizeBytes,
            RecoveryFileType.RegularFile,
            GetObservedIdentity(observations, operation.Path)
        );
    }

    private FilePreparationInstruction CompileRestore(
        PlannedOperation operation,
        InstallationPlanningRequest request,
        BoundInstallationPlan binding,
        IReadOnlyDictionary<string, RecoveryFileObservation> observations
    )
    {
        if (operation.ResultSha256 is null)
            throw Error(ExecutionCompilationError.InvalidOperationMapping, "A restore operation has no result digest.");

        if (request.Action == InstallationAction.Uninstall && operation.Path.Equals(LauncherPath))
        {
            RecoveryFileIdentity original = GetRequiredPresentObservation(observations, LauncherBackupPath);
            if (operation.ResultSha256 != original.Sha256 || request.Launcher.BackupLauncherSha256 != original.Sha256)
                throw Error(ExecutionCompilationError.InvalidOperationMapping, "The uninstall restore doesn't match the verified original-launcher backup.");
            return new FilePreparationInstruction(
                operation,
                PreparationInstructionKind.WriteTransactionDestination,
                new CurrentGameLauncherSource(CurrentGameLauncherRole.OriginalLauncherBackup, LauncherBackupPath, original),
                original.UnixMode,
                original.SizeBytes,
                original.FileType,
                GetObservedIdentity(observations, operation.Path)
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
                    binding.RollbackContent
                        ?? throw Error(ExecutionCompilationError.StaleRollbackSnapshot, "A restore has no live committed recovery authority."),
                    RecoverySnapshotContent.GameFile,
                    operation.Path,
                    snapshotEntry.Backup
                ),
                snapshotEntry.Backup!.UnixMode,
                snapshotEntry.Backup.SizeBytes,
                snapshotEntry.Backup.FileType,
                GetObservedIdentity(observations, operation.Path)
            );
        }

        throw Error(ExecutionCompilationError.InvalidOperationMapping, $"Restore destination '{operation.Path}' has no core-owned restore rule.");
    }

    private static FilePreparationInstruction CompileBackup(
        PlannedOperation operation,
        IReadOnlyDictionary<string, RecoveryFileObservation> observations
    )
    {
        if (operation.ExpectedCurrentSha256 is null || operation.ExpectedCurrentSha256 != operation.ResultSha256)
            throw Error(ExecutionCompilationError.InvalidOperationMapping, "A backup operation doesn't copy its exact observed digest.");

        RecoveryFileIdentity identity = GetRequiredPresentObservation(observations, operation.Path);
        if (identity.Sha256 != operation.ExpectedCurrentSha256)
            throw Error(ExecutionCompilationError.InvalidOperationMapping, "A backup observation doesn't match its planned digest.");
        PreparationSource source = operation.Path.Value switch
        {
            "StardewValley" => new CurrentGameLauncherSource(
                CurrentGameLauncherRole.CurrentLauncher,
                LauncherPath,
                identity
            ),
            "StardewValley-original" => new CurrentGameLauncherSource(
                CurrentGameLauncherRole.OriginalLauncherBackup,
                LauncherBackupPath,
                identity
            ),
            _ => new CurrentGameFileSource(operation.Path, identity)
        };
        return new FilePreparationInstruction(
            operation,
            PreparationInstructionKind.CaptureRecoveryFile,
            source,
            null,
            expectedCurrentIdentity: identity
        );
    }

    private static RecoveryFileIdentity? GetObservedIdentity(
        IReadOnlyDictionary<string, RecoveryFileObservation> observations,
        NormalizedRelativePath path
    )
    {
        if (!observations.TryGetValue(path.Value, out RecoveryFileObservation? observation))
            throw Error(ExecutionCompilationError.InvalidOperationMapping, $"'{path}' has no complete recovery observation.");
        return observation.Identity;
    }

    private static void ValidateRemove(PlannedOperation operation, InstallationPlanningRequest request)
    {
        if (operation.ExpectedCurrentSha256 is null || operation.ResultSha256 is not null)
            throw Error(ExecutionCompilationError.InvalidOperationMapping, "A remove operation must bind only its exact current digest.");

        if (request.Action == InstallationAction.Rollback)
        {
            RollbackSnapshot snapshot = request.RollbackSnapshot
                ?? throw Error(ExecutionCompilationError.InvalidOperationMapping, $"Remove destination '{operation.Path}' has no recovery snapshot.");
            if (request.RollbackOriginAction == InstallationAction.Backup)
            {
                InstallationReceiptEntry backupInstalled = request.InstalledReceipt?.Entries.SingleOrDefault(entry => entry.Path.Equals(operation.Path))
                    ?? throw Error(ExecutionCompilationError.InvalidOperationMapping, $"Backup restore removal '{operation.Path}' isn't in the current receipt.");
                if (backupInstalled.InstalledSha256 != operation.ExpectedCurrentSha256)
                    throw Error(ExecutionCompilationError.InvalidOperationMapping, $"Backup restore removal '{operation.Path}' doesn't match the current receipt.");
                return;
            }
            RollbackSnapshotEntry snapshotEntry = snapshot.Entries.SingleOrDefault(entry => entry.Path.Equals(operation.Path))
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
                    int originalLauncherMode = request.Action == InstallationAction.Install
                        ? request.RecoveryObservations.Single(observation => observation.Path.Equals(LauncherPath)).Identity?.UnixMode
                            ?? throw Error(ExecutionCompilationError.InvalidOperationMapping, "A receipt install has no original-launcher mode identity.")
                        : request.InstalledReceipt?.Launcher.OriginalLauncherUnixMode
                            ?? throw Error(ExecutionCompilationError.InvalidOperationMapping, "A receipt update has no original-launcher mode identity.");
                    PackageManifestEntry launcher = manifest.Entries.Single(entry => entry.Kind == OwnedEntryKind.Launcher);
                    InstallationReceipt generated = new(
                        manifest.Release,
                        manifest.GetCanonicalDigest(),
                        transactionId.ToString("N"),
                        manifest.Entries.Select(entry => new InstallationReceiptEntry(entry.Path, entry.Sha256, entry.UnixMode, entry.Kind)),
                        new LauncherReceipt(launcher.Sha256, originalLauncher, launcher.UnixMode, originalLauncherMode)
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
                            binding.RollbackContent
                                ?? throw Error(ExecutionCompilationError.StaleRollbackSnapshot, "A receipt restore has no live committed recovery authority."),
                            RecoverySnapshotContent.InstalledReceipt,
                            null,
                            null,
                            snapshot.PreviousReceiptSha256
                        )
                    );
                }

            default:
                throw Error(ExecutionCompilationError.InvalidOperationMapping, "The action has no receipt-state rule.");
        }
    }

    private static ManifestPreparationInstruction CreateManifestInstruction(
        BoundInstallationPlan binding,
        InstallationPlanningRequest request
    )
    {
        switch (request.Action)
        {
            case InstallationAction.Install:
            case InstallationAction.Update:
            case InstallationAction.Repair:
                {
                    IVerifiedPackageContentAuthority authority = binding.TargetPackageContent
                        ?? throw Error(ExecutionCompilationError.StaleManifest, "A manifest write has no live verified package authority.");
                    return new ManifestPreparationInstruction(
                        ReceiptPreparationKind.WriteAtomically,
                        binding.InstalledManifestSha256,
                        new VerifiedCanonicalManifestSource(
                            authority,
                            CanonicalOwnershipDocuments.SerializeManifest(authority.Manifest)
                        )
                    );
                }

            case InstallationAction.Uninstall:
                if (binding.InstalledManifestSha256 is null)
                    throw Error(ExecutionCompilationError.InvalidOperationMapping, "An uninstall has no installed manifest to remove.");
                return new ManifestPreparationInstruction(
                    ReceiptPreparationKind.RemoveAtomically,
                    binding.InstalledManifestSha256,
                    null
                );

            case InstallationAction.Backup:
                return new ManifestPreparationInstruction(ReceiptPreparationKind.None, null, null);

            case InstallationAction.Rollback:
                {
                    ICommittedRecoveryContentAuthority authority = binding.RollbackContent
                        ?? throw Error(ExecutionCompilationError.StaleRollbackSnapshot, "A rollback has no live committed recovery authority.");
                    if (authority.PreviousManifestSha256 is null)
                    {
                        if (binding.InstalledManifestSha256 is null)
                            throw Error(ExecutionCompilationError.InvalidOperationMapping, "A manifest-removing rollback has no current manifest identity.");
                        return new ManifestPreparationInstruction(
                            ReceiptPreparationKind.RemoveAtomically,
                            binding.InstalledManifestSha256,
                            null
                        );
                    }
                    return new ManifestPreparationInstruction(
                        ReceiptPreparationKind.WriteAtomically,
                        binding.InstalledManifestSha256,
                        new RecoverySnapshotSource(
                            authority,
                            RecoverySnapshotContent.InstalledManifest,
                            null,
                            null,
                            authority.PreviousManifestSha256
                        )
                    );
                }

            default:
                throw Error(ExecutionCompilationError.InvalidOperationMapping, "The action has no manifest-state rule.");
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

    private static RecoverySnapshotPreparation CreateRecoverySnapshotPreparation(
        InstallationPlanningRequest request,
        IReadOnlyList<FilePreparationInstruction> instructions,
        ReceiptPreparationInstruction receipt,
        IReadOnlyDictionary<string, RecoveryFileObservation> observations
    )
    {
        Sha256Digest? expectedReceipt = receipt.Kind switch
        {
            ReceiptPreparationKind.WriteAtomically => receipt.Source switch
            {
                GeneratedCanonicalReceiptSource generated => generated.Sha256,
                RecoverySnapshotSource recoveryReceipt when recoveryReceipt.Content == RecoverySnapshotContent.InstalledReceipt
                    => recoveryReceipt.ExpectedContentSha256,
                _ => throw Error(ExecutionCompilationError.InvalidOperationMapping, "A new recovery snapshot requires a generated receipt source.")
            },
            ReceiptPreparationKind.RemoveAtomically => null,
            ReceiptPreparationKind.None when request.Action == InstallationAction.Backup
                => request.InstalledReceipt?.GetCanonicalDigest(),
            _ => throw Error(ExecutionCompilationError.InvalidOperationMapping, "A mutating action must have an exact receipt transition.")
        };
        Sha256Digest? previousReceipt = request.InstalledReceipt?.GetCanonicalDigest();

        List<RollbackSnapshotEntry> entries = new();
        FilePreparationInstruction[] recoveryInstructions = request.Action == InstallationAction.Backup
            ? instructions.Where(item => item.Kind == PreparationInstructionKind.CaptureRecoveryFile).ToArray()
            : instructions.Where(item => item.IsTransactionDestination).ToArray();
        foreach (FilePreparationInstruction instruction in recoveryInstructions)
        {
            RecoveryFileIdentity? prior = observations[instruction.Path.Value].Identity;
            RecoveryFileIdentity? result = instruction.Kind switch
            {
                PreparationInstructionKind.WriteTransactionDestination => GetInstructionResultIdentity(instruction),
                PreparationInstructionKind.RemoveTransactionDestination => null,
                PreparationInstructionKind.CaptureRecoveryFile when request.Action == InstallationAction.Backup => prior,
                _ => throw Error(ExecutionCompilationError.InvalidOperationMapping, "A transaction destination has no recovery result rule.")
            };
            if (
                request.Action != InstallationAction.Backup
                && !(request.Action == InstallationAction.Rollback && request.RollbackOriginAction == InstallationAction.Backup)
                && prior == result
            )
                throw Error(ExecutionCompilationError.InvalidOperationMapping, $"Changed path '{instruction.Path}' doesn't describe a state transition.");

            OwnedEntryKind ownedKind = GetRecoveryOwnedKind(instruction.Path, request);
            if (request.Action == InstallationAction.Backup)
            {
                if (prior is null)
                    throw Error(ExecutionCompilationError.InvalidOperationMapping, "A backup capture source is unexpectedly absent.");
                entries.Add(new RollbackSnapshotEntry(instruction.Path, ownedKind, RollbackEntryKind.Restore, prior, prior));
            }
            else
            {
                entries.Add(prior is null
                    ? new RollbackSnapshotEntry(instruction.Path, ownedKind, RollbackEntryKind.Remove, result, null)
                    : new RollbackSnapshotEntry(instruction.Path, ownedKind, RollbackEntryKind.Restore, result, prior));
            }
        }

        RollbackSnapshot snapshot = new(expectedReceipt, previousReceipt, entries);
        byte[] snapshotBytes = CanonicalOwnershipDocuments.SerializeRollbackSnapshot(snapshot);
        HashSet<string> changed = recoveryInstructions
            .Select(item => item.Path.Value)
            .ToHashSet(StringComparer.Ordinal);
        RecoveryPathBinding[] bindings = observations.Values
            .Select(observation => new RecoveryPathBinding(
                observation.Path,
                GetRecoveryOwnedKind(observation.Path, request),
                observation.Identity,
                changed.Contains(observation.Path.Value) && observation.Identity is not null
            ))
            .ToArray();
        byte[]? previousReceiptBytes = request.InstalledReceipt is null
            ? null
            : CanonicalOwnershipDocuments.SerializeReceipt(request.InstalledReceipt);
        return new RecoverySnapshotPreparation(snapshot, snapshotBytes, bindings, previousReceiptBytes);
    }

    private static RecoveryFileIdentity GetInstructionResultIdentity(FilePreparationInstruction instruction)
    {
        if (
            instruction.ExpectedResultSha256 is null
            || instruction.ResultSizeBytes is null
            || instruction.ResultUnixMode is null
            || instruction.ResultFileType is null
        )
        {
            throw Error(ExecutionCompilationError.InvalidOperationMapping, $"Write destination '{instruction.Path}' lacks complete result metadata.");
        }
        return new RecoveryFileIdentity(
            instruction.ExpectedResultSha256,
            instruction.ResultSizeBytes.Value,
            instruction.ResultUnixMode.Value,
            instruction.ResultFileType.Value
        );
    }

    private static OwnedEntryKind GetRecoveryOwnedKind(NormalizedRelativePath path, InstallationPlanningRequest request)
    {
        if (path.Equals(LauncherBackupPath))
            return OwnedEntryKind.RecoveryLauncherBackup;
        if (path.Equals(LauncherPath))
            return OwnedEntryKind.Launcher;
        return request.TargetManifest?.Entries.SingleOrDefault(entry => entry.Path.Equals(path))?.Kind
            ?? request.InstalledReceipt?.Entries.SingleOrDefault(entry => entry.Path.Equals(path))?.Kind
            ?? request.RollbackSnapshot?.Entries.SingleOrDefault(entry => entry.Path.Equals(path))?.OwnedKind
            ?? throw Error(ExecutionCompilationError.UnsafeDestination, $"Recovery path '{path}' has no exact core ownership rule.");
    }

    private static void ValidateRecoveryObservations(InstallationPlan plan, InstallationPlanningRequest request)
    {
        HashSet<string> required = plan.Action switch
        {
            InstallationAction.Install or InstallationAction.Update or InstallationAction.Repair or InstallationAction.Uninstall => plan.Operations
                .Select(operation => operation.Path.Value)
                .Append(LauncherPath.Value)
                .Append(LauncherBackupPath.Value)
                .ToHashSet(StringComparer.Ordinal),
            InstallationAction.Backup => plan.Operations.Select(operation => operation.Path.Value).ToHashSet(StringComparer.Ordinal),
            InstallationAction.Rollback when request.RollbackOriginAction == InstallationAction.Backup => request.RollbackSnapshot!.Entries
                .Select(entry => entry.Path.Value)
                .Concat(request.InstalledReceipt?.Entries.Select(entry => entry.Path.Value) ?? Array.Empty<string>())
                .ToHashSet(StringComparer.Ordinal),
            InstallationAction.Rollback => request.RollbackSnapshot?.Entries.Select(entry => entry.Path.Value).ToHashSet(StringComparer.Ordinal)
                ?? new HashSet<string>(StringComparer.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(plan))
        };
        HashSet<string> actual = request.RecoveryObservations.Select(observation => observation.Path.Value).ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(required))
        {
            throw Error(
                ExecutionCompilationError.InvalidOperationMapping,
                "Recovery observations must contain exactly both launcher paths and every changed file (or the exact backup/rollback inputs for those actions)."
            );
        }

        foreach (RecoveryFileObservation observation in request.RecoveryObservations)
            GetRecoveryOwnedKind(observation.Path, request);
    }

    private static RecoveryFileIdentity GetRequiredPresentObservation(
        IReadOnlyDictionary<string, RecoveryFileObservation> observations,
        NormalizedRelativePath path
    )
    {
        if (!observations.TryGetValue(path.Value, out RecoveryFileObservation? observation) || observation.Identity is null)
            throw Error(ExecutionCompilationError.InvalidOperationMapping, $"Required recovery source '{path}' isn't a present regular file.");
        return observation.Identity;
    }

    private static bool RequiresNewRecoverySnapshot(InstallationAction action)
    {
        return Enum.IsDefined(typeof(InstallationAction), action);
    }

    private static Sha256Digest? GetRecoveryObservationsDigest(IReadOnlyList<RecoveryFileObservation> observations)
    {
        if (observations.Count == 0)
            return null;

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(
            stream,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.Default, Indented = false, SkipValidation = false }
        ))
        {
            writer.WriteStartArray();
            foreach (RecoveryFileObservation observation in observations)
            {
                writer.WriteStartObject();
                writer.WriteString("path", observation.Path.Value);
                if (observation.Identity is null)
                    writer.WriteNull("identity");
                else
                {
                    writer.WriteStartObject("identity");
                    writer.WriteString("sha256", observation.Identity.Sha256.Value);
                    writer.WriteNumber("size_bytes", observation.Identity.SizeBytes);
                    writer.WriteNumber("unix_mode", observation.Identity.UnixMode);
                    writer.WriteString("file_type", "regular_file");
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Sha256Digest.Hash(stream.ToArray());
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
