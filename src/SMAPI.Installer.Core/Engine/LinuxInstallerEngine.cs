using System.Collections.ObjectModel;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Recovery;
using StardewModdingAPI.Installer.Core.Security;
using StardewModdingAPI.Installer.Core.Transactions;

namespace StardewModdingAPI.Installer.Core.Engine;

/// <summary>An opaque, user-reviewable installer plan bound to one exact root generation and live content authorities.</summary>
public sealed class InspectedInstallationState : IDisposable
{
    private bool Disposed;

    public InstallationAction Action => this.Plan.Action;
    public GameRootIdentity GameRoot => this.Binding.GameRoot;
    public ulong OperationGeneration => this.Binding.OperationGeneration;
    public InstallationPlan Plan { get; }
    public Sha256Digest ConfirmationDigest { get; }
    internal BoundInstallationPlan Binding { get; }
    internal IVerifiedPackageContentAuthority? TargetPackageContent { get; }
    internal ICommittedRecoveryContentAuthority? RollbackContent { get; }

    internal InspectedInstallationState(
        InstallationPlan plan,
        BoundInstallationPlan binding,
        IVerifiedPackageContentAuthority? targetPackageContent,
        ICommittedRecoveryContentAuthority? rollbackContent
    )
    {
        this.Plan = plan;
        this.Binding = binding;
        this.ConfirmationDigest = binding.GetCanonicalDigest();
        this.TargetPackageContent = targetPackageContent;
        this.RollbackContent = rollbackContent;
    }

    internal void AssertUsable()
    {
        if (this.Disposed)
            throw new ObjectDisposedException(nameof(InspectedInstallationState));
        this.TargetPackageContent?.AssertUsable();
        this.RollbackContent?.AssertUsable();
    }

    public void Dispose() => this.Disposed = true;
}

/// <summary>The single public Linux installer inspection and execution authority.</summary>
public sealed class LinuxInstallerEngine
{
    private readonly InstallerExecutionCompiler Compiler = new();
    private readonly InstallerTransactionExecutor Executor;
    private readonly InstallationExecutionMaterializer Materializer;

    public LinuxInstallerEngine()
    {
        this.Executor = new InstallerTransactionExecutor();
        this.Materializer = new InstallationExecutionMaterializer(this.Executor);
    }

    internal LinuxInstallerEngine(InstallerTransactionExecutor executor)
    {
        this.Executor = executor ?? throw new ArgumentNullException(nameof(executor));
        this.Materializer = new InstallationExecutionMaterializer(this.Executor);
    }

    /// <summary>Inspect and plan one action without changing game or ownership files.</summary>
    public Task<InspectedInstallationState> InspectAsync(
        string gameRoot,
        InstallationAction action,
        VerifiedPackageContent? targetPackage = null,
        CommittedRecoveryHandle? recovery = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        IVerifiedPackageContentAuthority? packageAuthority = targetPackage;
        ICommittedRecoveryContentAuthority? recoveryAuthority = recovery;
        using InstallerOperationLease lease = InstallerOperationLease.Acquire(gameRoot);
        this.Executor.RecoverLocked(lease);
        return Task.FromResult(this.InspectLocked(lease, action, packageAuthority, recoveryAuthority));
    }

    /// <summary>Execute only the exact opaque inspection and digest the user reviewed.</summary>
    public Task<TransactionResult> ExecuteAsync(
        InspectedInstallationState inspection,
        Sha256Digest confirmedDigest,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(confirmedDigest);
        inspection.AssertUsable();
        cancellationToken.ThrowIfCancellationRequested();
        if (!inspection.Plan.CanExecute)
            throw new ExecutionCompilationException(ExecutionCompilationError.NonExecutablePlan, "A plan with unresolved conflicts can't execute.");
        if (inspection.ConfirmationDigest != confirmedDigest)
            throw new ExecutionCompilationException(ExecutionCompilationError.StalePlan, "The supplied confirmation digest doesn't match this inspected plan.");

        using InstallerOperationLease lease = InstallerOperationLease.Acquire(inspection.GameRoot.CanonicalPath);
        lease.AssertRootAndGeneration(inspection.GameRoot, inspection.OperationGeneration);
        if (this.Executor.RecoverLocked(lease).Count > 0)
            throw new InstallerTransactionException(TransactionErrorCode.PathChanged, "Crash recovery invalidated the inspected plan. Inspect and confirm again.");
        lease.AssertRootAndGeneration(inspection.GameRoot, inspection.OperationGeneration);
        InspectedInstallationState current = this.InspectLocked(
            lease,
            inspection.Action,
            inspection.TargetPackageContent,
            inspection.RollbackContent
        );
        using (current)
        {
            if (current.ConfirmationDigest != inspection.ConfirmationDigest)
                throw new ExecutionCompilationException(ExecutionCompilationError.StalePlan, "The installation state changed after confirmation.");
            AnchoredCoreStateAuthority coreState = AnchoredCoreStateAuthority.Inspect(lease);
            InstallationPlanningRequest request = InstallationStateInspector.CreateRequest(
                lease,
                inspection.Action,
                inspection.TargetPackageContent,
                inspection.RollbackContent,
                coreState
            );
            InstallationExecutionPreparation preparation = this.Compiler.Prepare(
                current.Binding,
                current.Plan,
                request,
                Guid.NewGuid()
            );
            return Task.FromResult(this.Materializer.Apply(lease, preparation, coreState, cancellationToken));
        }
    }

    /// <summary>Open the current committed recovery generation through an opaque anchored handle.</summary>
    public Task<CommittedRecoveryHandle> OpenCurrentRecoveryAsync(
        string gameRoot,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        using InstallerOperationLease lease = InstallerOperationLease.Acquire(gameRoot);
        this.Executor.RecoverLocked(lease);
        AnchoredCoreStateAuthority state = AnchoredCoreStateAuthority.Inspect(lease);
        return Task.FromResult(CommittedRecoveryHandle.OpenCurrent(lease, state));
    }

    /// <summary>Open one selected generation from the bounded authenticated recovery chain.</summary>
    public Task<CommittedRecoveryHandle> OpenRecoveryAsync(
        string gameRoot,
        Guid generationId,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        using InstallerOperationLease lease = InstallerOperationLease.Acquire(gameRoot);
        this.Executor.RecoverLocked(lease);
        AnchoredCoreStateAuthority state = AnchoredCoreStateAuthority.Inspect(lease);
        return Task.FromResult(CommittedRecoveryHandle.OpenSelected(lease, state, generationId));
    }

    internal InspectedInstallationState InspectLocked(
        InstallerOperationLease lease,
        InstallationAction action,
        IVerifiedPackageContentAuthority? targetPackage,
        ICommittedRecoveryContentAuthority? recovery
    )
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (action is InstallationAction.Install or InstallationAction.Update or InstallationAction.Repair)
        {
            if (targetPackage is null)
                throw new ArgumentException("This action requires a verified target package.", nameof(targetPackage));
        }
        else if (targetPackage is not null)
            throw new ArgumentException("This action doesn't accept a target package.", nameof(targetPackage));
        if ((action == InstallationAction.Rollback) != (recovery is not null))
            throw new ArgumentException("Only rollback accepts and requires a committed recovery handle.", nameof(recovery));

        AnchoredCoreStateAuthority state = AnchoredCoreStateAuthority.Inspect(lease);
        InstallationPlanningRequest request = InstallationStateInspector.CreateRequest(
            lease,
            action,
            targetPackage,
            recovery,
            state
        );
        InstallationPlan plan = new InstallationPlanner().Plan(request);
        if (!plan.CanExecute)
            return new InspectedInstallationState(
                plan,
                CreateNonExecutableBinding(plan, lease, state),
                targetPackage,
                recovery
            );
        BoundInstallationPlan binding = this.Compiler.BindPlan(
            plan,
            request,
            lease.RootIdentity,
            lease.Generation,
            targetPackage,
            recovery,
            state.PointerSha256
        );
        return new InspectedInstallationState(plan, binding, targetPackage, recovery);
    }

    private static BoundInstallationPlan CreateNonExecutableBinding(
        InstallationPlan plan,
        InstallerOperationLease lease,
        AnchoredCoreStateAuthority state
    )
    {
        return new BoundInstallationPlan(
            plan.Action,
            lease.RootIdentity,
            lease.Generation,
            plan.GetCanonicalDigest(),
            null,
            state.ReceiptSha256,
            state.ManifestSha256,
            null,
            null,
            null,
            state.PointerSha256,
            null,
            null
        );
    }
}

internal static class InstallationStateInspector
{
    private static readonly NormalizedRelativePath LauncherPath = NormalizedRelativePath.Parse("StardewValley");
    private static readonly NormalizedRelativePath LauncherBackupPath = NormalizedRelativePath.Parse("StardewValley-original");

    public static InstallationPlanningRequest CreateRequest(
        InstallerOperationLease lease,
        InstallationAction action,
        IVerifiedPackageContentAuthority? targetPackage,
        ICommittedRecoveryContentAuthority? recovery,
        AnchoredCoreStateAuthority state
    )
    {
        PackageManifest? targetManifest = targetPackage?.Manifest;
        InstallationReceipt? receipt = state.Receipt;
        HashSet<NormalizedRelativePath> inventoryPaths = new();
        if (targetManifest is not null)
            inventoryPaths.UnionWith(targetManifest.Entries.Select(entry => entry.Path));
        if (receipt is not null)
            inventoryPaths.UnionWith(receipt.Entries.Select(entry => entry.Path));
        inventoryPaths.Add(LauncherPath);
        CurrentFile[] currentFiles = inventoryPaths
            .Select(path => ReadCurrentFile(lease.Game, path))
            .Where(file => file is not null)
            .Cast<CurrentFile>()
            .ToArray();
        Sha256Digest? launcherSha = currentFiles.SingleOrDefault(file => file.Path.Equals(LauncherPath))?.Sha256;
        CurrentFile? launcherBackup = ReadCurrentFile(lease.Game, LauncherBackupPath);
        LauncherState launcher = LauncherState.Assess(launcherSha, launcherBackup?.Sha256, receipt?.Launcher);
        InstallationInventory inventory = InstallationInventory.Create(targetManifest, receipt, currentFiles);
        RollbackSnapshot? rollbackSnapshot = recovery?.Snapshot;

        InstallationPlanningRequest initial = new(
            action,
            inventory,
            launcher,
            targetManifest,
            receipt,
            rollbackSnapshot,
            action == InstallationAction.Rollback && rollbackSnapshot is not null
                ? ReadObservations(
                    lease.Game,
                    rollbackSnapshot.Entries.Select(entry => entry.Path)
                        .Concat(
                            rollbackSnapshot.IsUserBackup
                                ? receipt?.Entries.Select(entry => entry.Path) ?? Array.Empty<NormalizedRelativePath>()
                                : Array.Empty<NormalizedRelativePath>()
                        )
                )
                : Array.Empty<RecoveryFileObservation>()
        );
        InstallationPlan plan = new InstallationPlanner().Plan(initial);
        IEnumerable<NormalizedRelativePath> required = action switch
        {
            InstallationAction.Install or InstallationAction.Update or InstallationAction.Repair or InstallationAction.Uninstall => plan.Operations
                .Select(operation => operation.Path)
                .Append(LauncherPath)
                .Append(LauncherBackupPath),
            InstallationAction.Backup => plan.Operations.Select(operation => operation.Path),
            InstallationAction.Rollback when rollbackSnapshot?.IsUserBackup == true => rollbackSnapshot.Entries
                .Select(entry => entry.Path)
                .Concat(receipt?.Entries.Select(entry => entry.Path) ?? Array.Empty<NormalizedRelativePath>()),
            InstallationAction.Rollback => rollbackSnapshot?.Entries.Select(entry => entry.Path) ?? Array.Empty<NormalizedRelativePath>(),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        RecoveryFileObservation[] observations = ReadObservations(lease.Game, required.Distinct());
        return new InstallationPlanningRequest(
            action,
            inventory,
            launcher,
            targetManifest,
            receipt,
            rollbackSnapshot,
            observations
        );
    }

    private static CurrentFile? ReadCurrentFile(LinuxAnchoredFileSystem game, NormalizedRelativePath path)
    {
        LinuxFileIdentity? identity = game.Stat(path.Value);
        if (identity is null)
            return null;
        using LinuxAnchoredFile file = game.OpenRegularFileForRead(path.Value);
        if (file.Identity != identity)
            throw new InstallerTransactionException(TransactionErrorCode.PathChanged, $"'{path}' changed during inspection.");
        return new CurrentFile(path, Sha256Digest.Parse(game.ComputeSha256(file)), identity.UnixMode);
    }

    private static RecoveryFileObservation[] ReadObservations(
        LinuxAnchoredFileSystem game,
        IEnumerable<NormalizedRelativePath> paths
    )
    {
        return paths.Distinct().OrderBy(path => path.Value, StringComparer.Ordinal).Select(path =>
        {
            LinuxFileIdentity? identity = game.Stat(path.Value);
            if (identity is null)
                return new RecoveryFileObservation(path, null);
            using LinuxAnchoredFile file = game.OpenRegularFileForRead(path.Value);
            if (file.Identity != identity)
                throw new InstallerTransactionException(TransactionErrorCode.PathChanged, $"'{path}' changed during recovery inspection.");
            return new RecoveryFileObservation(
                path,
                new RecoveryFileIdentity(
                    Sha256Digest.Parse(game.ComputeSha256(file)),
                    identity.Size,
                    identity.UnixMode,
                    RecoveryFileType.RegularFile
                )
            );
        }).ToArray();
    }
}
