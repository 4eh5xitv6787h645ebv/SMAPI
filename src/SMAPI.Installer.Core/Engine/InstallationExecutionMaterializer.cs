using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Recovery;
using StardewModdingAPI.Installer.Core.Security;
using StardewModdingAPI.Installer.Core.Transactions;

namespace StardewModdingAPI.Installer.Core.Engine;

/// <summary>Materializes one closed preparation into the sole core-authorized transaction under its original root lease.</summary>
internal sealed class InstallationExecutionMaterializer
{
    private const int PrivateFileMode = 0x180;
    private const long MaximumStagedBytes = 8L * 1024 * 1024 * 1024;
    private readonly InstallerTransactionExecutor Executor;
    private readonly ITransactionProgressSink Progress;

    public InstallationExecutionMaterializer(
        InstallerTransactionExecutor? executor = null,
        ITransactionProgressSink? progress = null
    )
    {
        this.Executor = executor ?? new InstallerTransactionExecutor();
        this.Progress = new NonThrowingTransactionProgressSink(progress);
    }

    public TransactionResult Apply(
        InstallerOperationLease lease,
        InstallationExecutionPreparation preparation,
        AnchoredCoreStateAuthority currentState,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(currentState);
        cancellationToken.ThrowIfCancellationRequested();
        BoundInstallationPlan binding = preparation.Binding;
        lease.AssertRootAndGeneration(binding.GameRoot, binding.OperationGeneration);
        currentState.AssertUsable(lease);
        this.Progress.Report(new(TransactionStage.VerifyingRecovery, 0, 1));
        CommittedRecoveryHandle.AssertAuthenticatedHistory(lease, currentState, cancellationToken);
        this.Progress.Report(new(TransactionStage.VerifyingRecovery, 1, 1));
        AssertCurrentState(binding, currentState);
        if (!CommittedRecoveryHandle.InspectCapacity(lease.Game, cancellationToken).CanCreateGeneration)
            throw new InstallerTransactionException(TransactionErrorCode.WorkspaceConflict, "The bounded recovery-generation store is full; remove an older checkpoint before continuing.");
        RecoverySnapshotPreparation recovery = preparation.RecoverySnapshot
            ?? throw new ExecutionCompilationException(ExecutionCompilationError.InvalidOperationMapping, "Every executable action must produce a recovery generation.");
        this.Progress.Report(new(TransactionStage.PreparingPayload, 0, null));
        AssertAllInstructionPreconditions(lease.Game, preparation.Instructions, cancellationToken);
        this.Progress.Report(new(TransactionStage.PreparingRecovery, 0, null));
        AssertRecoveryBindings(lease.Game, recovery.PathBindings, cancellationToken);

        string stagingRoot = PrivatePackageStaging.CreateDirectory();
        LinuxAnchoredFileSystem? payload = null;
        try
        {
            payload = new LinuxAnchoredFileSystem(stagingRoot);
            StagingContext staging = new(payload, cancellationToken);
            this.Progress.Report(new(TransactionStage.PreparingRecovery, 0, null));
            List<TransactionFileOperation> recoveryOperations = this.StageRecoveryGeneration(
                lease,
                preparation,
                currentState,
                recovery,
                staging,
                cancellationToken
            );
            this.Progress.Report(new(TransactionStage.PreparingRecovery, 1, 1));
            this.Progress.Report(new(TransactionStage.PreparingPayload, 0, null));
            List<TransactionFileOperation> ordinaryOperations = this.StageOrdinaryOperations(
                lease,
                preparation.Instructions,
                staging,
                cancellationToken
            );
            this.Progress.Report(new(TransactionStage.PreparingPayload, 1, 1));
            TransactionFileOperation? manifestOperation = this.StageManifestOperation(preparation.Manifest, staging);
            TransactionFileOperation? receiptOperation = this.StageReceiptOperation(preparation.Receipt, staging);

            Sha256Digest? resultManifest = GetResultDigest(
                preparation.Manifest.Kind,
                currentState.ManifestSha256,
                preparation.Manifest.Source
            );
            Sha256Digest? resultReceipt = GetResultDigest(
                preparation.Receipt.Kind,
                currentState.ReceiptSha256,
                preparation.Receipt.Source
            );
            if (
                recovery.Snapshot.ExpectedCurrentReceiptSha256 != resultReceipt
                || recovery.Snapshot.PreviousReceiptSha256 != currentState.ReceiptSha256
            )
            {
                throw new ExecutionCompilationException(ExecutionCompilationError.InvalidOperationMapping, "The recovery snapshot doesn't describe the materialized receipt transition.");
            }
            CommittedRecoveryPointer pointer = new(
                preparation.TransactionId,
                preparation.Action,
                recovery.SnapshotSha256,
                resultManifest,
                resultReceipt,
                currentState.ManifestSha256,
                currentState.ReceiptSha256,
                currentState.Pointer?.GenerationId,
                currentState.PointerSha256,
                currentState.Pointer?.RetentionSha256,
                currentState.Pointer?.SchemaVersion ?? CommittedRecoveryPointer.LegacySchemaVersion
            );
            byte[] pointerBytes = CanonicalRecoveryPointerDocument.Serialize(pointer);
            string pointerSource = staging.StageBytes(pointerBytes);
            TransactionFileOperation pointerOperation = WriteOperation(
                TransactionPlan.CoreRecoveryPointerRelativePath,
                currentState.PointerSha256,
                pointerSource,
                Sha256Digest.Hash(pointerBytes),
                PrivateFileMode,
                currentState.PointerSha256 is null ? null : PrivateFileMode
            );

            TransactionPlan transaction = TransactionPlan.CreateWithCoreState(
                preparation.TransactionId,
                recoveryOperations,
                ordinaryOperations,
                manifestOperation,
                receiptOperation,
                pointerOperation,
                preparation.Instructions
                    .Where(instruction => instruction.Kind == PreparationInstructionKind.VerifyUnchanged)
                    .Select(instruction => new TransactionFilePrecondition(
                        instruction.Path.Value,
                        instruction.ExpectedCurrentSha256?.Value
                            ?? throw new ExecutionCompilationException(ExecutionCompilationError.InvalidOperationMapping, "A retained path has no digest precondition."),
                        instruction.ExpectedCurrentIdentity?.UnixMode
                            ?? throw new ExecutionCompilationException(ExecutionCompilationError.InvalidOperationMapping, "A retained path has no mode precondition.")
                    ))
            );
            AssertAllInstructionPreconditions(lease.Game, preparation.Instructions, cancellationToken);
            AssertRecoveryBindings(lease.Game, recovery.PathBindings, cancellationToken);
            currentState.AssertUsable(lease);
            lease.AssertRootAndGeneration(binding.GameRoot, binding.OperationGeneration);
            return this.Executor.ApplyLocked(
                lease,
                payload,
                transaction,
                binding.GameRoot,
                binding.OperationGeneration,
                cancellationToken
            );
        }
        finally
        {
            payload?.Dispose();
            PrivatePackageStaging.TryDeleteDirectory(stagingRoot);
        }
    }

    private List<TransactionFileOperation> StageRecoveryGeneration(
        InstallerOperationLease lease,
        InstallationExecutionPreparation preparation,
        AnchoredCoreStateAuthority currentState,
        RecoverySnapshotPreparation recovery,
        StagingContext staging,
        CancellationToken cancellationToken
    )
    {
        string prefix = $".smapi-installer/recovery/generations/{preparation.TransactionId:N}";
        List<TransactionFileOperation> operations = new();
        byte[] snapshotBytes = recovery.GetCanonicalSnapshotBytes();
        operations.Add(WriteOperation(
            $"{prefix}/snapshot.json",
            null,
            staging.StageBytes(snapshotBytes),
            recovery.SnapshotSha256,
            PrivateFileMode
        ));

        if (currentState.ReceiptBytes is not null)
        {
            if (recovery.PreviousReceiptSha256 != currentState.ReceiptSha256 || currentState.ManifestBytes is null)
                throw new ExecutionCompilationException(ExecutionCompilationError.StaleInstalledReceipt, "The previous ownership tuple doesn't match recovery preparation.");
            operations.Add(WriteOperation(
                $"{prefix}/previous-receipt.json",
                null,
                staging.StageBytes(currentState.ReceiptBytes),
                currentState.ReceiptSha256!,
                PrivateFileMode
            ));
            operations.Add(WriteOperation(
                $"{prefix}/previous-manifest.json",
                null,
                staging.StageBytes(currentState.ManifestBytes),
                currentState.ManifestSha256!,
                PrivateFileMode
            ));
        }
        if (currentState.PointerBytes is not null)
        {
            operations.Add(WriteOperation(
                $"{prefix}/previous-pointer.json",
                null,
                staging.StageBytes(currentState.PointerBytes),
                currentState.PointerSha256!,
                PrivateFileMode
            ));
        }

        int contentIndex = 0;
        RecoveryPathBinding[] captures = recovery.PathBindings.Where(binding => binding.RequiresContentCapture).ToArray();
        foreach (RecoveryPathBinding pathBinding in captures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.Progress.Report(new(TransactionStage.PreparingRecovery, contentIndex, null));
            RecoveryFileIdentity identity = pathBinding.PriorIdentity
                ?? throw new ExecutionCompilationException(ExecutionCompilationError.InvalidOperationMapping, "A recovery capture path has no prior identity.");
            using LinuxAnchoredFile source = lease.Game.OpenRegularFileForRead(pathBinding.Path.Value);
            AssertSource(lease.Game, source, identity, enforceSourceMode: true, cancellationToken: cancellationToken);
            string staged = staging.StageFile(source, identity.Sha256, identity.SizeBytes);
            operations.Add(WriteOperation(
                $"{prefix}/files/{contentIndex:D8}",
                null,
                staged,
                identity.Sha256,
                PrivateFileMode
            ));
            contentIndex++;
            this.Progress.Report(new(TransactionStage.PreparingRecovery, contentIndex, captures.Length));
        }
        return operations;
    }

    private List<TransactionFileOperation> StageOrdinaryOperations(
        InstallerOperationLease lease,
        IReadOnlyList<FilePreparationInstruction> instructions,
        StagingContext staging,
        CancellationToken cancellationToken
    )
    {
        List<TransactionFileOperation> operations = new();
        FilePreparationInstruction[] destinations = instructions.Where(instruction => instruction.IsTransactionDestination).ToArray();
        for (int index = 0; index < destinations.Length; index++)
        {
            FilePreparationInstruction instruction = destinations[index];
            cancellationToken.ThrowIfCancellationRequested();
            this.Progress.Report(new(TransactionStage.PreparingPayload, index, null));
            if (instruction.Kind == PreparationInstructionKind.RemoveTransactionDestination)
            {
                operations.Add(new TransactionFileOperation(
                    TransactionOperationKind.RemoveFile,
                    instruction.Path.Value,
                    instruction.ExpectedCurrentSha256?.Value,
                    ExpectedExistingUnixMode: instruction.ExpectedCurrentIdentity?.UnixMode
                ));
                this.Progress.Report(new(TransactionStage.PreparingPayload, index + 1, destinations.Length));
                continue;
            }
            if (
                instruction.Source is null
                || instruction.ExpectedResultSha256 is null
                || instruction.ResultSizeBytes is null
                || instruction.ResultUnixMode is null
                || instruction.ResultFileType != RecoveryFileType.RegularFile
            )
            {
                throw new ExecutionCompilationException(ExecutionCompilationError.InvalidOperationMapping, $"Write '{instruction.Path}' isn't completely materializable.");
            }
            string staged = this.StageSource(
                lease,
                instruction.Source,
                instruction.ExpectedResultSha256,
                instruction.ResultSizeBytes.Value,
                staging,
                cancellationToken
            );
            operations.Add(WriteOperation(
                instruction.Path.Value,
                instruction.ExpectedCurrentSha256,
                staged,
                instruction.ExpectedResultSha256,
                instruction.ResultUnixMode.Value,
                instruction.ExpectedCurrentIdentity?.UnixMode
            ));
            this.Progress.Report(new(TransactionStage.PreparingPayload, index + 1, destinations.Length));
        }
        return operations;
    }

    private TransactionFileOperation? StageManifestOperation(
        ManifestPreparationInstruction instruction,
        StagingContext staging
    )
    {
        return instruction.Kind switch
        {
            ReceiptPreparationKind.None => null,
            ReceiptPreparationKind.RemoveAtomically => new TransactionFileOperation(
                TransactionOperationKind.RemoveFile,
                TransactionPlan.CoreManifestRelativePath,
                instruction.ExpectedExistingManifestSha256?.Value,
                ExpectedExistingUnixMode: instruction.ExpectedExistingManifestSha256 is null ? null : PrivateFileMode
            ),
            ReceiptPreparationKind.WriteAtomically => WriteOperation(
                TransactionPlan.CoreManifestRelativePath,
                instruction.ExpectedExistingManifestSha256,
                this.StageDocumentSource(instruction.Source, RecoverySnapshotContent.InstalledManifest, staging),
                GetSourceDigest(instruction.Source),
                PrivateFileMode,
                instruction.ExpectedExistingManifestSha256 is null ? null : PrivateFileMode
            ),
            _ => throw new ExecutionCompilationException(ExecutionCompilationError.InvalidOperationMapping, "The manifest transition kind isn't supported.")
        };
    }

    private TransactionFileOperation? StageReceiptOperation(
        ReceiptPreparationInstruction instruction,
        StagingContext staging
    )
    {
        return instruction.Kind switch
        {
            ReceiptPreparationKind.None => null,
            ReceiptPreparationKind.RemoveAtomically => new TransactionFileOperation(
                TransactionOperationKind.RemoveFile,
                TransactionPlan.CoreReceiptRelativePath,
                instruction.ExpectedExistingReceiptSha256?.Value,
                ExpectedExistingUnixMode: instruction.ExpectedExistingReceiptSha256 is null ? null : PrivateFileMode
            ),
            ReceiptPreparationKind.WriteAtomically => WriteOperation(
                TransactionPlan.CoreReceiptRelativePath,
                instruction.ExpectedExistingReceiptSha256,
                this.StageDocumentSource(instruction.Source, RecoverySnapshotContent.InstalledReceipt, staging),
                GetSourceDigest(instruction.Source),
                PrivateFileMode,
                instruction.ExpectedExistingReceiptSha256 is null ? null : PrivateFileMode
            ),
            _ => throw new ExecutionCompilationException(ExecutionCompilationError.InvalidOperationMapping, "The receipt transition kind isn't supported.")
        };
    }

    private string StageSource(
        InstallerOperationLease lease,
        PreparationSource source,
        Sha256Digest expectedSha256,
        long expectedSize,
        StagingContext staging,
        CancellationToken cancellationToken
    )
    {
        LinuxAnchoredFile opened;
        LinuxAnchoredFileSystem? sourceRoot = null;
        bool enforceMode = false;
        RecoveryFileIdentity? expectedIdentity = null;
        switch (source)
        {
            case VerifiedPackageFileSource package:
                opened = package.Authority.OpenFile(
                    package.Authority.Manifest.Entries.Single(entry => entry.Path.Equals(package.PackagePath)),
                    cancellationToken
                );
                break;
            case CurrentGameLauncherSource currentLauncher:
                opened = lease.Game.OpenRegularFileForRead(currentLauncher.SourcePath.Value);
                sourceRoot = lease.Game;
                enforceMode = true;
                expectedIdentity = new RecoveryFileIdentity(currentLauncher.Sha256, currentLauncher.SizeBytes, currentLauncher.UnixMode, currentLauncher.FileType);
                break;
            case CurrentGameFileSource currentFile:
                opened = lease.Game.OpenRegularFileForRead(currentFile.SourcePath.Value);
                sourceRoot = lease.Game;
                enforceMode = true;
                expectedIdentity = new RecoveryFileIdentity(currentFile.Sha256, currentFile.SizeBytes, currentFile.UnixMode, currentFile.FileType);
                break;
            case GeneratedGameFileSource generated:
                opened = lease.Game.OpenRegularFileForRead(generated.SourcePath.Value);
                sourceRoot = lease.Game;
                enforceMode = true;
                expectedIdentity = new RecoveryFileIdentity(generated.Sha256, generated.SizeBytes, generated.UnixMode, generated.FileType);
                break;
            case RecoverySnapshotSource recovery when recovery.Content == RecoverySnapshotContent.GameFile:
                RecoveryFileIdentity identity = new(
                    recovery.ExpectedContentSha256!,
                    recovery.ExpectedSizeBytes!.Value,
                    recovery.ExpectedUnixMode!.Value,
                    recovery.ExpectedFileType!.Value
                );
                opened = recovery.Authority.OpenGameFile(recovery.EntryPath!, identity);
                expectedIdentity = identity;
                break;
            default:
                throw new ExecutionCompilationException(ExecutionCompilationError.InvalidOperationMapping, "A game-file write has an unsupported source authority.");
        }
        using (opened)
        {
            if (sourceRoot is not null && expectedIdentity is not null)
                AssertSource(sourceRoot, opened, expectedIdentity, enforceMode, cancellationToken);
            return staging.StageFile(opened, expectedSha256, expectedSize);
        }
    }

    private string StageDocumentSource(
        PreparationSource? source,
        RecoverySnapshotContent expectedRecoveryContent,
        StagingContext staging
    )
    {
        switch (source)
        {
            case VerifiedCanonicalManifestSource manifest:
                manifest.Authority.AssertUsable();
                return staging.StageBytes(manifest.GetCanonicalBytes());
            case GeneratedCanonicalReceiptSource receipt:
                return staging.StageBytes(receipt.GetCanonicalBytes());
            case RecoverySnapshotSource recovery when recovery.Content == expectedRecoveryContent:
                using (LinuxAnchoredFile file = expectedRecoveryContent == RecoverySnapshotContent.InstalledReceipt
                    ? recovery.Authority.OpenPreviousReceipt(recovery.ExpectedContentSha256!)
                    : recovery.Authority.OpenPreviousManifest(recovery.ExpectedContentSha256!))
                {
                    return staging.StageFile(file, recovery.ExpectedContentSha256!, file.Identity.Size);
                }
            default:
                throw new ExecutionCompilationException(ExecutionCompilationError.InvalidOperationMapping, "A core document write has an unsupported source authority.");
        }
    }

    private static void AssertCurrentState(BoundInstallationPlan binding, AnchoredCoreStateAuthority state)
    {
        if (
            binding.GameRoot != state.GameRoot
            || binding.InstalledManifestSha256 != state.ManifestSha256
            || binding.InstalledReceiptSha256 != state.ReceiptSha256
            || binding.CurrentRecoveryPointerSha256 != state.PointerSha256
        )
        {
            throw new ExecutionCompilationException(ExecutionCompilationError.StaleInstalledReceipt, "The anchored installed core-state tuple changed after planning.");
        }
    }

    private static void AssertAllInstructionPreconditions(
        LinuxAnchoredFileSystem game,
        IReadOnlyList<FilePreparationInstruction> instructions,
        CancellationToken cancellationToken
    )
    {
        foreach (FilePreparationInstruction instruction in instructions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LinuxFileIdentity? current = game.Stat(instruction.Path.Value);
            if (instruction.ExpectedCurrentSha256 is null)
            {
                if (instruction.IsTransactionDestination && current is not null)
                    throw new InstallerTransactionException(TransactionErrorCode.ExistingFileMismatch, $"'{instruction.Path}' appeared after planning.");
                continue;
            }
            if (current is null || current.Kind != LinuxAnchoredEntryKind.RegularFile || current.LinkCount != 1)
                throw new InstallerTransactionException(TransactionErrorCode.ExistingFileMismatch, $"'{instruction.Path}' changed type or disappeared after planning.");
            using LinuxAnchoredFile file = game.OpenRegularFileForRead(instruction.Path.Value);
            if (
                file.Identity != current
                || Sha256Digest.Parse(game.ComputeSha256(file, cancellationToken)) != instruction.ExpectedCurrentSha256
            )
                throw new InstallerTransactionException(TransactionErrorCode.ExistingFileMismatch, $"'{instruction.Path}' changed after planning.");
        }
    }

    private static void AssertRecoveryBindings(
        LinuxAnchoredFileSystem game,
        IReadOnlyList<RecoveryPathBinding> bindings,
        CancellationToken cancellationToken
    )
    {
        foreach (RecoveryPathBinding binding in bindings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LinuxFileIdentity? current = game.Stat(binding.Path.Value);
            if (binding.PriorIdentity is null)
            {
                if (current is not null)
                    throw new InstallerTransactionException(TransactionErrorCode.PathChanged, $"Recovery path '{binding.Path}' appeared after inspection.");
                continue;
            }
            if (current is null)
                throw new InstallerTransactionException(TransactionErrorCode.PathChanged, $"Recovery path '{binding.Path}' disappeared after inspection.");
            using LinuxAnchoredFile file = game.OpenRegularFileForRead(binding.Path.Value);
            AssertSource(game, file, binding.PriorIdentity, enforceSourceMode: true, cancellationToken: cancellationToken);
        }
    }

    private static void AssertSource(
        LinuxAnchoredFileSystem sourceRoot,
        LinuxAnchoredFile source,
        RecoveryFileIdentity expected,
        bool enforceSourceMode,
        CancellationToken cancellationToken
    )
    {
        if (
            source.Identity.Kind != LinuxAnchoredEntryKind.RegularFile
            || source.Identity.LinkCount != 1
            || source.Identity.Size != expected.SizeBytes
            || (enforceSourceMode && source.Identity.UnixMode != expected.UnixMode)
            || Sha256Digest.Parse(sourceRoot.ComputeSha256(source, cancellationToken)) != expected.Sha256
        )
        {
            throw new InstallerTransactionException(TransactionErrorCode.PathChanged, "A materialization source changed after planning.");
        }
    }

    private static Sha256Digest? GetResultDigest(
        ReceiptPreparationKind kind,
        Sha256Digest? current,
        PreparationSource? source
    )
    {
        return kind switch
        {
            ReceiptPreparationKind.None => current,
            ReceiptPreparationKind.RemoveAtomically => null,
            ReceiptPreparationKind.WriteAtomically => GetSourceDigest(source),
            _ => throw new ExecutionCompilationException(ExecutionCompilationError.InvalidOperationMapping, "A core-state result kind isn't supported.")
        };
    }

    private static Sha256Digest GetSourceDigest(PreparationSource? source)
    {
        return source switch
        {
            VerifiedCanonicalManifestSource manifest => manifest.Sha256,
            GeneratedCanonicalReceiptSource receipt => receipt.Sha256,
            RecoverySnapshotSource recovery when recovery.ExpectedContentSha256 is not null => recovery.ExpectedContentSha256,
            _ => throw new ExecutionCompilationException(ExecutionCompilationError.InvalidOperationMapping, "A core document source has no exact digest.")
        };
    }

    private static TransactionFileOperation WriteOperation(
        string destination,
        Sha256Digest? expectedExisting,
        string payloadPath,
        Sha256Digest result,
        int unixMode,
        int? expectedExistingUnixMode = null
    )
    {
        return new TransactionFileOperation(
            TransactionOperationKind.WriteFile,
            destination,
            expectedExisting?.Value,
            payloadPath,
            result.Value,
            unixMode,
            expectedExistingUnixMode
        );
    }

    private sealed class StagingContext
    {
        private readonly LinuxAnchoredFileSystem Payload;
        private readonly CancellationToken CancellationToken;
        private int NextIndex;
        private long StagedBytes;

        public StagingContext(LinuxAnchoredFileSystem payload, CancellationToken cancellationToken)
        {
            this.Payload = payload;
            this.CancellationToken = cancellationToken;
        }

        public string StageBytes(byte[] bytes)
        {
            ArgumentNullException.ThrowIfNull(bytes);
            this.CancellationToken.ThrowIfCancellationRequested();
            this.AddBytes(bytes.LongLength);
            string name = this.GetNextName();
            using LinuxAnchoredFile file = this.Payload.CreateNewFile(name, PrivateFileMode);
            this.Payload.AppendAndFsync(file, name, bytes, 0, bytes.LongLength);
            return name;
        }

        public string StageFile(LinuxAnchoredFile source, Sha256Digest expectedSha256, long expectedSize)
        {
            if (expectedSize < 0 || source.Identity.Size != expectedSize)
                throw new InstallerTransactionException(TransactionErrorCode.PayloadMismatch, "A materialization source has an unexpected size.");
            this.AddBytes(expectedSize);
            string name = this.GetNextName();
            LinuxFileIdentity copied = this.Payload.CopyFile(source, name, PrivateFileMode, this.CancellationToken);
            using LinuxAnchoredFile verified = this.Payload.OpenRegularFileForRead(name);
            if (
                verified.Identity != copied
                || verified.Identity.Size != expectedSize
                || Sha256Digest.Parse(this.Payload.ComputeSha256(verified, this.CancellationToken)) != expectedSha256
            )
            {
                throw new InstallerTransactionException(TransactionErrorCode.PayloadMismatch, "A staged materialization source failed exact verification.");
            }
            return name;
        }

        private string GetNextName()
        {
            if (this.NextIndex >= TransactionPlan.MaximumOperationCount)
                throw new InstallerTransactionException(TransactionErrorCode.InvalidPlan, "The materialized payload exceeds its file-count limit.");
            return $"source-{this.NextIndex++:D8}";
        }

        private void AddBytes(long count)
        {
            this.StagedBytes = checked(this.StagedBytes + count);
            if (this.StagedBytes > MaximumStagedBytes)
                throw new InstallerTransactionException(TransactionErrorCode.InvalidPlan, "The materialized payload exceeds its aggregate byte limit.");
        }
    }
}
