using System.Collections.ObjectModel;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Recovery;
using StardewModdingAPI.Installer.Core.Security;
using StardewModdingAPI.Installer.Core.Transactions;

namespace StardewModdingAPI.Installer.Core.Engine;

/// <summary>
/// An opaque, user-reviewable installer plan bound to one exact root generation and borrowed live content authorities.
/// Disposing this inspection invalidates its plan and repair candidates, but never disposes a package or recovery handle supplied by the caller.
/// </summary>
public sealed class InspectedInstallationState : IDisposable
{
    private bool Disposed;

    public InstallationAction Action => this.Plan.Action;
    public GameRootIdentity GameRoot => this.Binding.GameRoot;
    public ulong OperationGeneration => this.Binding.OperationGeneration;
    public InstallationPlan Plan { get; }
    public Sha256Digest ConfirmationDigest { get; }
    /// <summary>The authenticated currently installed release, or <see langword="null"/> when no receipt is installed.</summary>
    public InstallationReleaseIdentity? CurrentRelease { get; }
    /// <summary>The authenticated release expected after this action, or <see langword="null"/> for an uninstalled result.</summary>
    public InstallationReleaseIdentity? ExpectedResultRelease { get; }
    /// <summary>The bounded installed-state classification derived by the core during this inspection.</summary>
    public ObservedInstallationState ObservedState { get; }
    /// <summary>The exact recovery-generation capacity observed while this plan was created.</summary>
    public RecoveryCapacityState RecoveryCapacity { get; }
    /// <summary>Get the deterministic exact modified-file candidates which this blocked repair inspection can authorize.</summary>
    public IReadOnlyList<ModifiedFileReplacementCandidate> ModifiedFileReplacementCandidates { get; }
    internal BoundInstallationPlan Binding { get; }
    internal IVerifiedPackageContentAuthority? TargetPackageContent { get; }
    internal ICommittedRecoveryContentAuthority? RollbackContent { get; }
    internal IReadOnlyList<ModifiedFileReplacementApproval> ModifiedFileReplacementApprovals { get; }
    internal object RepairCandidateAuthority { get; }

    internal InspectedInstallationState(
        InstallationPlan plan,
        BoundInstallationPlan binding,
        IVerifiedPackageContentAuthority? targetPackageContent,
        ICommittedRecoveryContentAuthority? rollbackContent,
        object repairCandidateAuthority,
        InstallationReleaseIdentity? currentRelease,
        InstallationReleaseIdentity? expectedResultRelease,
        ObservedInstallationState observedState,
        RecoveryCapacityState recoveryCapacity,
        IEnumerable<ModifiedFileReplacementCandidate>? modifiedFileReplacementCandidates = null,
        IEnumerable<ModifiedFileReplacementApproval>? modifiedFileReplacementApprovals = null
    )
    {
        ArgumentNullException.ThrowIfNull(repairCandidateAuthority);
        this.Plan = plan;
        this.Binding = binding;
        this.ConfirmationDigest = binding.GetCanonicalDigest();
        this.TargetPackageContent = targetPackageContent;
        this.RollbackContent = rollbackContent;
        this.RepairCandidateAuthority = repairCandidateAuthority;
        this.CurrentRelease = currentRelease;
        this.ExpectedResultRelease = expectedResultRelease;
        this.ObservedState = observedState;
        this.RecoveryCapacity = recoveryCapacity;
        this.ModifiedFileReplacementCandidates = new ReadOnlyCollection<ModifiedFileReplacementCandidate>(
            (modifiedFileReplacementCandidates ?? Array.Empty<ModifiedFileReplacementCandidate>()).ToArray()
        );
        this.ModifiedFileReplacementApprovals = (modifiedFileReplacementApprovals ?? Array.Empty<ModifiedFileReplacementApproval>()).ToArray();
    }

    internal void AssertUsable()
    {
        if (this.Disposed)
            throw new ObjectDisposedException(nameof(InspectedInstallationState));
        this.TargetPackageContent?.AssertUsable();
        this.RollbackContent?.AssertUsable();
    }

    /// <summary>
    /// Invalidate this inspection and its repair candidates. Borrowed package and recovery handles remain caller-owned and usable.
    /// </summary>
    public void Dispose() => this.Disposed = true;
}

/// <summary>The single public Linux installer inspection and execution authority.</summary>
public sealed class LinuxInstallerEngine
{
    private readonly InstallerExecutionCompiler Compiler = new();
    private readonly InstallerTransactionExecutor Executor;
    private readonly InstallationExecutionMaterializer Materializer;
    private readonly IRecoveryPruneFaultInjector RecoveryPruneFaultInjector;
    private readonly ITransactionProgressSink Progress;

    public LinuxInstallerEngine(ITransactionProgressSink? progress = null)
    {
        this.Progress = new NonThrowingTransactionProgressSink(progress);
        this.Executor = new InstallerTransactionExecutor(this.Progress);
        this.Materializer = new InstallationExecutionMaterializer(this.Executor, this.Progress);
        this.RecoveryPruneFaultInjector = NullRecoveryPruneFaultInjector.Instance;
    }

    internal LinuxInstallerEngine(
        InstallerTransactionExecutor executor,
        IRecoveryPruneFaultInjector? recoveryPruneFaultInjector = null,
        ITransactionProgressSink? progress = null
    )
    {
        this.Executor = executor ?? throw new ArgumentNullException(nameof(executor));
        this.Progress = new NonThrowingTransactionProgressSink(progress);
        this.Materializer = new InstallationExecutionMaterializer(this.Executor, this.Progress);
        this.RecoveryPruneFaultInjector = recoveryPruneFaultInjector ?? NullRecoveryPruneFaultInjector.Instance;
    }

    /// <summary>Inspect and plan one action without changing game or ownership files.</summary>
    /// <remarks>
    /// <paramref name="targetPackage"/> and <paramref name="recovery"/> are borrowed. The caller must keep them alive through execution
    /// and remains responsible for disposing them after the inspection is disposed or execution completes.
    /// </remarks>
    public Task<InspectedInstallationState> InspectAsync(
        string gameRoot,
        InstallationAction action,
        VerifiedPackageContent? targetPackage = null,
        CommittedRecoveryHandle? recovery = null,
        CancellationToken cancellationToken = default
    )
        => Task.Run(
            () => this.Inspect(gameRoot, action, targetPackage, recovery, null, cancellationToken),
            cancellationToken
        );

    /// <summary>
    /// Select exact core-minted modified-file candidates from a blocked repair inspection, revalidate them through the anchored game root,
    /// and return a replacement inspection. Selecting only some candidates leaves the remaining conflicts blocked.
    /// </summary>
    /// <remarks>
    /// The source inspection and its borrowed package must remain usable until this method completes. This method does not dispose either.
    /// </remarks>
    public Task<InspectedInstallationState> ApproveRepairAsync(
        InspectedInstallationState sourceInspection,
        IEnumerable<ModifiedFileReplacementCandidate> selectedCandidates,
        CancellationToken cancellationToken = default
    )
        => Task.Run(
            () => this.ApproveRepair(sourceInspection, selectedCandidates, cancellationToken),
            cancellationToken
        );

    private InspectedInstallationState Inspect(
        string gameRoot,
        InstallationAction action,
        IVerifiedPackageContentAuthority? targetPackage,
        ICommittedRecoveryContentAuthority? recovery,
        IEnumerable<ModifiedFileReplacementApproval>? modifiedFileReplacementApprovals,
        CancellationToken cancellationToken
    )
    {
        this.Progress.Report(new(TransactionStage.Inspecting, 0, null));
        cancellationToken.ThrowIfCancellationRequested();
        IVerifiedPackageContentAuthority? packageAuthority = targetPackage;
        ICommittedRecoveryContentAuthority? recoveryAuthority = recovery;
        using InstallerInspectionLease inspection = InstallerInspectionLease.Open(gameRoot);
        AnchoredCoreStateAuthority state = AnchoredCoreStateAuthority.Inspect(inspection.Game, inspection.RootIdentity);
        InspectedInstallationState result = this.InspectCore(
            inspection.Game,
            inspection.RootIdentity,
            inspection.Generation,
            action,
            packageAuthority,
            recoveryAuthority,
            modifiedFileReplacementApprovals,
            state,
            cancellationToken
        );
        try
        {
            state.AssertUsable(inspection.Game, inspection.RootIdentity);
            inspection.AssertStable();
            this.Progress.Report(new(TransactionStage.Inspecting, 1, 1));
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private InspectedInstallationState ApproveRepair(
        InspectedInstallationState sourceInspection,
        IEnumerable<ModifiedFileReplacementCandidate> selectedCandidates,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(sourceInspection);
        ArgumentNullException.ThrowIfNull(selectedCandidates);
        this.Progress.Report(new(TransactionStage.Inspecting, 0, null));
        sourceInspection.AssertUsable();
        cancellationToken.ThrowIfCancellationRequested();
        if (sourceInspection.Action != InstallationAction.Repair || sourceInspection.Plan.CanExecute)
        {
            throw new ExecutionCompilationException(
                ExecutionCompilationError.NonExecutablePlan,
                "Repair candidates can only be selected from a blocked repair inspection."
            );
        }
        IVerifiedPackageContentAuthority targetPackage = sourceInspection.TargetPackageContent
            ?? throw new ExecutionCompilationException(ExecutionCompilationError.StaleManifest, "The repair inspection has no live package authority.");

        List<ModifiedFileReplacementCandidate> selected = new();
        HashSet<ModifiedFileReplacementCandidate> unique = new(ReferenceEqualityComparer.Instance);
        foreach (ModifiedFileReplacementCandidate? candidate in selectedCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (selected.Count >= sourceInspection.ModifiedFileReplacementCandidates.Count)
                throw new ArgumentException("The repair-candidate selection exceeds the bounded issued set.", nameof(selectedCandidates));
            if (candidate is null)
                throw new ArgumentException("A selected repair candidate can't be null.", nameof(selectedCandidates));
            if (!unique.Add(candidate))
                throw new ArgumentException("A repair candidate can't be selected more than once.", nameof(selectedCandidates));
            if (
                !ReferenceEquals(candidate.SourceAuthority, sourceInspection.RepairCandidateAuthority)
                || !sourceInspection.ModifiedFileReplacementCandidates.Any(issued => ReferenceEquals(issued, candidate))
            )
            {
                throw new ExecutionCompilationException(
                    ExecutionCompilationError.StalePlan,
                    "A selected repair candidate wasn't issued by this exact inspection."
                );
            }
            selected.Add(candidate);
        }
        sourceInspection.AssertUsable();

        using InstallerInspectionLease inspection = InstallerInspectionLease.Open(sourceInspection.GameRoot.CanonicalPath);
        if (inspection.RootIdentity != sourceInspection.GameRoot || inspection.Generation != sourceInspection.OperationGeneration)
        {
            throw new ExecutionCompilationException(
                ExecutionCompilationError.StalePlan,
                "The game root or installer generation changed after the repair candidates were inspected."
            );
        }
        foreach (ModifiedFileReplacementCandidate candidate in selected)
        {
            RecoveryFileObservation observed = InstallationStateInspector.ReadObservation(
                inspection.Game,
                candidate.Path,
                cancellationToken
            );
            if (observed.Identity != candidate.ObservedIdentity)
            {
                throw new ExecutionCompilationException(
                    ExecutionCompilationError.StalePlan,
                    $"Repair candidate '{candidate.Path}' changed after inspection."
                );
            }
        }

        ModifiedFileReplacementApproval[] approvals = sourceInspection.ModifiedFileReplacementApprovals
            .Concat(selected.Select(candidate => new ModifiedFileReplacementApproval(candidate.Path, candidate.ObservedIdentity)))
            .OrderBy(approval => approval.Path.Value, StringComparer.Ordinal)
            .ToArray();
        AnchoredCoreStateAuthority state = AnchoredCoreStateAuthority.Inspect(inspection.Game, inspection.RootIdentity);
        InspectedInstallationState result = this.InspectCore(
            inspection.Game,
            inspection.RootIdentity,
            inspection.Generation,
            InstallationAction.Repair,
            targetPackage,
            null,
            approvals,
            state,
            cancellationToken
        );
        try
        {
            state.AssertUsable(inspection.Game, inspection.RootIdentity);
            inspection.AssertStable();
            sourceInspection.AssertUsable();
            this.Progress.Report(new(TransactionStage.Inspecting, 1, 1));
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>Execute only the exact opaque inspection and digest the user reviewed.</summary>
    /// <remarks>
    /// Package and recovery authorities referenced by the inspection are borrowed, must remain alive until this task completes, and are never
    /// disposed by successful, failed, or cancelled execution.
    /// </remarks>
    public Task<TransactionResult> ExecuteAsync(
        InspectedInstallationState inspection,
        Sha256Digest confirmedDigest,
        CancellationToken cancellationToken = default
    )
        => Task.Run(() => this.Execute(inspection, confirmedDigest, cancellationToken), cancellationToken);

    private TransactionResult Execute(
        InspectedInstallationState inspection,
        Sha256Digest confirmedDigest,
        CancellationToken cancellationToken
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
            inspection.RollbackContent,
            inspection.ModifiedFileReplacementApprovals,
            cancellationToken
        );
        using (current)
        {
            if (current.ConfirmationDigest != inspection.ConfirmationDigest)
                throw new ExecutionCompilationException(ExecutionCompilationError.StalePlan, "The installation state changed after confirmation.");
            AnchoredCoreStateAuthority coreState = AnchoredCoreStateAuthority.Inspect(lease);
            InstallationPlanningRequest request = InstallationStateInspector.CreateRequest(
                lease.Game,
                inspection.Action,
                inspection.TargetPackageContent,
                inspection.RollbackContent,
                coreState,
                inspection.ModifiedFileReplacementApprovals,
                cancellationToken
            );
            InstallationExecutionPreparation preparation = this.Compiler.Prepare(
                current.Binding,
                current.Plan,
                request,
                Guid.NewGuid()
            );
            return this.Materializer.Apply(lease, preparation, coreState, cancellationToken);
        }
    }

    /// <summary>Open the current committed recovery generation through an opaque anchored handle owned by the caller.</summary>
    public Task<CommittedRecoveryHandle> OpenCurrentRecoveryAsync(
        string gameRoot,
        CancellationToken cancellationToken = default
    )
        => Task.Run(() => this.OpenCurrentRecovery(gameRoot, cancellationToken), cancellationToken);

    private CommittedRecoveryHandle OpenCurrentRecovery(
        string gameRoot,
        CancellationToken cancellationToken
    )
    {
        this.Progress.Report(new(TransactionStage.VerifyingRecovery, 0, null));
        cancellationToken.ThrowIfCancellationRequested();
        using InstallerInspectionLease inspection = InstallerInspectionLease.Open(gameRoot);
        AnchoredCoreStateAuthority state = AnchoredCoreStateAuthority.Inspect(inspection.Game, inspection.RootIdentity);
        CommittedRecoveryHandle result = CommittedRecoveryHandle.OpenCurrent(
            inspection.Game,
            inspection.CanonicalGameRoot,
            inspection.RootIdentity,
            state,
            cancellationToken,
            this.Progress
        );
        try
        {
            inspection.AssertStable();
            this.Progress.Report(new(TransactionStage.VerifyingRecovery, 1, 1));
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>Open one selected generation from the bounded authenticated recovery chain through a handle owned by the caller.</summary>
    public Task<CommittedRecoveryHandle> OpenRecoveryAsync(
        string gameRoot,
        Guid generationId,
        CancellationToken cancellationToken = default
    )
        => Task.Run(() => this.OpenRecovery(gameRoot, generationId, cancellationToken), cancellationToken);

    private CommittedRecoveryHandle OpenRecovery(
        string gameRoot,
        Guid generationId,
        CancellationToken cancellationToken
    )
    {
        this.Progress.Report(new(TransactionStage.VerifyingRecovery, 0, null));
        cancellationToken.ThrowIfCancellationRequested();
        using InstallerInspectionLease inspection = InstallerInspectionLease.Open(gameRoot);
        AnchoredCoreStateAuthority state = AnchoredCoreStateAuthority.Inspect(inspection.Game, inspection.RootIdentity);
        CommittedRecoveryHandle result = CommittedRecoveryHandle.OpenSelected(
            inspection.Game,
            inspection.CanonicalGameRoot,
            inspection.RootIdentity,
            state,
            generationId,
            cancellationToken,
            this.Progress
        );
        try
        {
            inspection.AssertStable();
            this.Progress.Report(new(TransactionStage.VerifyingRecovery, 1, 1));
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>List the bounded authenticated recovery history without changing the game directory.</summary>
    public Task<RecoveryHistory> ListRecoveriesAsync(
        string gameRoot,
        CancellationToken cancellationToken = default
    )
        => Task.Run(() => this.ListRecoveries(gameRoot, cancellationToken), cancellationToken);

    private RecoveryHistory ListRecoveries(string gameRoot, CancellationToken cancellationToken)
    {
        this.Progress.Report(new(TransactionStage.VerifyingRecovery, 0, null));
        cancellationToken.ThrowIfCancellationRequested();
        using InstallerInspectionLease inspection = InstallerInspectionLease.Open(gameRoot);
        AnchoredCoreStateAuthority state = AnchoredCoreStateAuthority.Inspect(inspection.Game, inspection.RootIdentity);
        RecoveryHistory result = CommittedRecoveryHandle.ListHistory(
            inspection.Game,
            inspection.CanonicalGameRoot,
            inspection.RootIdentity,
            state,
            cancellationToken,
            this.Progress
        );
        inspection.AssertStable();
        this.Progress.Report(new(TransactionStage.VerifyingRecovery, 1, 1));
        return result;
    }

    /// <summary>Inspect an exact bounded recovery-pruning decision without changing installer state.</summary>
    public Task<RecoveryPrunePlan> InspectRecoveryPruneAsync(
        string gameRoot,
        int retainNewest,
        CancellationToken cancellationToken = default
    )
        => Task.Run(
            () => this.InspectRecoveryPrune(gameRoot, retainNewest, cancellationToken),
            cancellationToken
        );

    private RecoveryPrunePlan InspectRecoveryPrune(
        string gameRoot,
        int retainNewest,
        CancellationToken cancellationToken
    )
    {
        this.Progress.Report(new(TransactionStage.VerifyingRecovery, 0, null));
        cancellationToken.ThrowIfCancellationRequested();
        using InstallerInspectionLease inspection = InstallerInspectionLease.Open(gameRoot);
        AnchoredCoreStateAuthority state = AnchoredCoreStateAuthority.Inspect(inspection.Game, inspection.RootIdentity);
        RecoveryPrunePlan result = CommittedRecoveryHandle.CreatePrunePlan(
            inspection.Game,
            inspection.RootIdentity,
            inspection.Generation,
            state,
            retainNewest,
            cancellationToken,
            this.Progress
        );
        state.AssertUsable(inspection.Game, inspection.RootIdentity);
        inspection.AssertStable();
        this.Progress.Report(new(TransactionStage.VerifyingRecovery, 1, 1));
        return result;
    }

    /// <summary>Execute only the exact opaque recovery-pruning decision and digest the user reviewed.</summary>
    public Task<int> ExecuteRecoveryPruneAsync(
        RecoveryPrunePlan plan,
        Sha256Digest confirmedDigest,
        CancellationToken cancellationToken = default
    )
        => Task.Run(() => this.ExecuteRecoveryPrune(plan, confirmedDigest, cancellationToken), cancellationToken);

    private int ExecuteRecoveryPrune(
        RecoveryPrunePlan plan,
        Sha256Digest confirmedDigest,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(confirmedDigest);
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.ConfirmationDigest != confirmedDigest)
            throw new InstallerTransactionException(TransactionErrorCode.PathChanged, "The supplied recovery-prune confirmation digest doesn't match the inspected plan.");
        using InstallerOperationLease lease = InstallerOperationLease.Acquire(plan.GameRoot.CanonicalPath);
        lease.AssertRootAndGeneration(plan.GameRoot, plan.OperationGeneration);
        if (this.Executor.RecoverLocked(lease).Count > 0)
            throw new InstallerTransactionException(TransactionErrorCode.PathChanged, "Crash recovery invalidated the inspected recovery-prune plan.");
        lease.AssertRootAndGeneration(plan.GameRoot, plan.OperationGeneration);
        AnchoredCoreStateAuthority state = AnchoredCoreStateAuthority.Inspect(lease);
        cancellationToken.ThrowIfCancellationRequested();
        return CommittedRecoveryHandle.ExecutePrunePlan(
            lease,
            state,
            plan,
            this.RecoveryPruneFaultInjector,
            cancellationToken,
            this.Progress
        );
    }

    internal InspectedInstallationState InspectLocked(
        InstallerOperationLease lease,
        InstallationAction action,
        IVerifiedPackageContentAuthority? targetPackage,
        ICommittedRecoveryContentAuthority? recovery,
        IEnumerable<ModifiedFileReplacementApproval>? modifiedFileReplacementApprovals = null,
        CancellationToken cancellationToken = default
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
        return this.InspectCore(
            lease.Game,
            lease.RootIdentity,
            lease.Generation,
            action,
            targetPackage,
            recovery,
            modifiedFileReplacementApprovals,
            state,
            cancellationToken
        );
    }

    private InspectedInstallationState InspectCore(
        LinuxAnchoredFileSystem game,
        GameRootIdentity gameRoot,
        ulong operationGeneration,
        InstallationAction action,
        IVerifiedPackageContentAuthority? targetPackage,
        ICommittedRecoveryContentAuthority? recovery,
        IEnumerable<ModifiedFileReplacementApproval>? modifiedFileReplacementApprovals,
        AnchoredCoreStateAuthority state,
        CancellationToken cancellationToken
    )
    {
        if (action is InstallationAction.Install or InstallationAction.Update or InstallationAction.Repair)
        {
            if (targetPackage is null)
                throw new ArgumentException("This action requires a verified target package.", nameof(targetPackage));
        }
        else if (targetPackage is not null)
            throw new ArgumentException("This action doesn't accept a target package.", nameof(targetPackage));
        if ((action == InstallationAction.Rollback) != (recovery is not null))
            throw new ArgumentException("Only rollback accepts and requires a committed recovery handle.", nameof(recovery));

        cancellationToken.ThrowIfCancellationRequested();
        ModifiedFileReplacementApproval[] approvals = (modifiedFileReplacementApprovals ?? Array.Empty<ModifiedFileReplacementApproval>())
            .ToArray();
        foreach (ModifiedFileReplacementApproval approval in approvals)
        {
            RecoveryFileObservation observed = InstallationStateInspector.ReadObservation(game, approval.Path, cancellationToken);
            if (observed.Identity != approval.ObservedIdentity)
            {
                throw new ExecutionCompilationException(
                    ExecutionCompilationError.StalePlan,
                    $"Approved repair file '{approval.Path}' changed after approval."
                );
            }
        }

        InstallationPlanningRequest request = InstallationStateInspector.CreateRequest(
            game,
            action,
            targetPackage,
            recovery,
            state,
            approvals,
            cancellationToken
        );
        InstallationPlan plan = new InstallationPlanner().Plan(request);
        object repairCandidateAuthority = new();
        ModifiedFileReplacementCandidate[] repairCandidates = InstallationStateInspector.CreateRepairCandidates(
            game,
            request,
            plan,
            repairCandidateAuthority,
            cancellationToken
        );
        if (!plan.CanExecute)
            return new InspectedInstallationState(
                plan,
                CreateNonExecutableBinding(plan, gameRoot, operationGeneration, state),
                targetPackage,
                recovery,
                repairCandidateAuthority,
                request.InstalledReceipt?.Release,
                GetExpectedResultRelease(request, recovery),
                request.ObservedState,
                request.RecoveryCapacity,
                repairCandidates,
                approvals
            );
        BoundInstallationPlan binding = this.Compiler.BindPlan(
            plan,
            request,
            gameRoot,
            operationGeneration,
            targetPackage,
            recovery,
            state.PointerSha256
        );
        return new InspectedInstallationState(
            plan,
            binding,
            targetPackage,
            recovery,
            repairCandidateAuthority,
            request.InstalledReceipt?.Release,
            GetExpectedResultRelease(request, recovery),
            request.ObservedState,
            request.RecoveryCapacity,
            repairCandidates,
            approvals
        );
    }

    private static BoundInstallationPlan CreateNonExecutableBinding(
        InstallationPlan plan,
        GameRootIdentity gameRoot,
        ulong operationGeneration,
        AnchoredCoreStateAuthority state
    )
    {
        return new BoundInstallationPlan(
            plan.Action,
            gameRoot,
            operationGeneration,
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

    private static InstallationReleaseIdentity? GetExpectedResultRelease(
        InstallationPlanningRequest request,
        ICommittedRecoveryContentAuthority? recovery
    )
    {
        return request.Action switch
        {
            InstallationAction.Install or InstallationAction.Update or InstallationAction.Repair => request.TargetManifest?.Release,
            InstallationAction.Backup => request.InstalledReceipt?.Release,
            InstallationAction.Uninstall => null,
            InstallationAction.Rollback => recovery?.RestoreRelease,
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };
    }
}

internal static class InstallationStateInspector
{
    private static readonly NormalizedRelativePath LauncherPath = NormalizedRelativePath.Parse("StardewValley");
    private static readonly NormalizedRelativePath LauncherBackupPath = NormalizedRelativePath.Parse("StardewValley-original");

    public static InstallationPlanningRequest CreateRequest(
        LinuxAnchoredFileSystem game,
        InstallationAction action,
        IVerifiedPackageContentAuthority? targetPackage,
        ICommittedRecoveryContentAuthority? recovery,
        AnchoredCoreStateAuthority state,
        IEnumerable<ModifiedFileReplacementApproval>? modifiedFileReplacementApprovals = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        PackageManifest? targetManifest = targetPackage?.Manifest;
        InstallationReceipt? receipt = state.Receipt;
        HashSet<NormalizedRelativePath> inventoryPaths = new();
        if (targetManifest is not null)
            inventoryPaths.UnionWith(targetManifest.Entries.Select(entry => entry.Path));
        if (receipt is not null)
            inventoryPaths.UnionWith(receipt.Entries.Select(entry => entry.Path));
        inventoryPaths.Add(LauncherPath);
        List<CurrentFile> currentFiles = new(inventoryPaths.Count);
        foreach (NormalizedRelativePath path in inventoryPaths.OrderBy(path => path.Value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CurrentFile? current = ReadCurrentFile(game, path, cancellationToken);
            if (current is not null)
                currentFiles.Add(current);
        }
        CurrentFile? currentLauncher = currentFiles.SingleOrDefault(file => file.Path.Equals(LauncherPath));
        CurrentFile? launcherBackup = ReadCurrentFile(game, LauncherBackupPath, cancellationToken);
        LauncherState launcher = LauncherState.Assess(
            currentLauncher?.Sha256,
            launcherBackup?.Sha256,
            receipt?.Launcher,
            currentLauncher?.UnixMode,
            launcherBackup?.UnixMode
        );
        InstallationInventory inventory = InstallationInventory.Create(targetManifest, receipt, currentFiles);
        RollbackSnapshot? rollbackSnapshot = recovery?.Snapshot;
        RecoveryCapacityState recoveryCapacity = CommittedRecoveryHandle.InspectCapacity(game, cancellationToken);
        ObservedInstallationState observedState = GetObservedState(receipt, inventory, launcher);

        InstallationPlanningRequest initial = new(
            action,
            inventory,
            launcher,
            targetManifest,
            receipt,
            rollbackSnapshot,
            action == InstallationAction.Rollback && rollbackSnapshot is not null
                ? ReadObservations(
                    game,
                    rollbackSnapshot.Entries.Select(entry => entry.Path)
                        .Concat(
                            recovery?.OriginAction == InstallationAction.Backup
                                ? receipt?.Entries.Select(entry => entry.Path) ?? Array.Empty<NormalizedRelativePath>()
                                : Array.Empty<NormalizedRelativePath>()
                        ),
                    cancellationToken
                )
                : Array.Empty<RecoveryFileObservation>(),
            recovery?.OriginAction,
            modifiedFileReplacementApprovals,
            recoveryCapacity,
            observedState
        );
        InstallationPlan plan = new InstallationPlanner().Plan(initial);
        IEnumerable<NormalizedRelativePath> required = action switch
        {
            InstallationAction.Install or InstallationAction.Update or InstallationAction.Repair or InstallationAction.Uninstall => plan.Operations
                .Select(operation => operation.Path)
                .Append(LauncherPath)
                .Append(LauncherBackupPath),
            InstallationAction.Backup => plan.Operations.Select(operation => operation.Path),
            InstallationAction.Rollback when recovery?.OriginAction == InstallationAction.Backup => rollbackSnapshot!.Entries
                .Select(entry => entry.Path)
                .Concat(receipt?.Entries.Select(entry => entry.Path) ?? Array.Empty<NormalizedRelativePath>()),
            InstallationAction.Rollback => rollbackSnapshot?.Entries.Select(entry => entry.Path) ?? Array.Empty<NormalizedRelativePath>(),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        RecoveryFileObservation[] observations = ReadObservations(game, required.Distinct(), cancellationToken);
        return new InstallationPlanningRequest(
            action,
            inventory,
            launcher,
            targetManifest,
            receipt,
            rollbackSnapshot,
            observations,
            recovery?.OriginAction,
            modifiedFileReplacementApprovals,
            recoveryCapacity,
            observedState
        );
    }

    private static ObservedInstallationState GetObservedState(
        InstallationReceipt? receipt,
        InstallationInventory inventory,
        LauncherState launcher
    )
    {
        if (receipt is null)
        {
            bool hasCollision = inventory.Entries.Any(entry =>
                !entry.Path.Equals(LauncherPath)
                && entry.Current is not null
                && entry.Classification is InventoryClassification.Legacy or InventoryClassification.UnknownCollision
            );
            return launcher.Classification == LauncherClassification.FreshVanilla && !hasCollision
                ? ObservedInstallationState.NotInstalled
                : ObservedInstallationState.Unknown;
        }

        bool entriesUnmodified = inventory.Entries
            .Where(entry => entry.Installed is not null)
            .All(entry => entry.Classification == InventoryClassification.UnchangedOwned);
        return launcher.Classification == LauncherClassification.InstalledUnchanged && entriesUnmodified
            ? ObservedInstallationState.KnownUnmodified
            : ObservedInstallationState.KnownModified;
    }

    internal static ModifiedFileReplacementCandidate[] CreateRepairCandidates(
        LinuxAnchoredFileSystem game,
        InstallationPlanningRequest request,
        InstallationPlan plan,
        object sourceAuthority,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(sourceAuthority);
        if (request.Action != InstallationAction.Repair || plan.CanExecute)
            return Array.Empty<ModifiedFileReplacementCandidate>();

        NormalizedRelativePath[] paths = plan.Conflicts
            .Where(conflict => conflict.Path is not null && conflict.Code is PlanConflictCode.ModifiedOwnedFile or PlanConflictCode.ModifiedInstalledLauncher)
            .Select(conflict => conflict.Path!)
            .Distinct()
            .OrderBy(path => path.Value, StringComparer.Ordinal)
            .Where(path => IsExactRepairCandidate(request, path))
            .ToArray();
        List<ModifiedFileReplacementCandidate> result = new(paths.Length);
        foreach (NormalizedRelativePath path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecoveryFileObservation observation = ReadObservation(game, path, cancellationToken);
            RecoveryFileIdentity identity = observation.Identity
                ?? throw new InstallerTransactionException(TransactionErrorCode.PathChanged, $"Repair candidate '{path}' disappeared during inspection.");
            CurrentFile expected = request.Inventory.Entries.Single(entry => entry.Path.Equals(path)).Current
                ?? throw new InstallerTransactionException(TransactionErrorCode.PathChanged, $"Repair candidate '{path}' has no current inventory observation.");
            if (identity.Sha256 != expected.Sha256 || identity.UnixMode != expected.UnixMode)
                throw new InstallerTransactionException(TransactionErrorCode.PathChanged, $"Repair candidate '{path}' changed during inspection.");
            result.Add(new ModifiedFileReplacementCandidate(sourceAuthority, path, identity));
        }
        return result.ToArray();
    }

    internal static RecoveryFileObservation ReadObservation(
        LinuxAnchoredFileSystem game,
        NormalizedRelativePath path,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        LinuxFileIdentity? identity = game.Stat(path.Value);
        if (identity is null)
            return new RecoveryFileObservation(path, null);
        using LinuxAnchoredFile file = game.OpenRegularFileForRead(path.Value);
        if (file.Identity != identity)
            throw new InstallerTransactionException(TransactionErrorCode.PathChanged, $"'{path}' changed during recovery inspection.");
        return new RecoveryFileObservation(
            path,
            new RecoveryFileIdentity(
                Sha256Digest.Parse(game.ComputeSha256(file, cancellationToken)),
                identity.Size,
                identity.UnixMode,
                RecoveryFileType.RegularFile
            )
        );
    }

    private static bool IsExactRepairCandidate(InstallationPlanningRequest request, NormalizedRelativePath path)
    {
        if (path.Equals(LauncherPath))
        {
            return request.Launcher.Classification == LauncherClassification.InstalledModified
                && request.Launcher.CurrentLauncherSha256 is not null
                && request.Launcher.CurrentLauncherUnixMode is not null
                && request.InstalledReceipt is not null
                && request.TargetManifest is not null;
        }
        InventoryEntry? entry = request.Inventory.Entries.SingleOrDefault(candidate => candidate.Path.Equals(path));
        return entry is
        {
            Classification: InventoryClassification.ModifiedOwned,
            Current: not null,
            Installed: not null,
            Target: not null
        };
    }

    private static CurrentFile? ReadCurrentFile(
        LinuxAnchoredFileSystem game,
        NormalizedRelativePath path,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        LinuxFileIdentity? identity = game.Stat(path.Value);
        if (identity is null)
            return null;
        using LinuxAnchoredFile file = game.OpenRegularFileForRead(path.Value);
        if (file.Identity != identity)
            throw new InstallerTransactionException(TransactionErrorCode.PathChanged, $"'{path}' changed during inspection.");
        return new CurrentFile(path, Sha256Digest.Parse(game.ComputeSha256(file, cancellationToken)), identity.UnixMode);
    }

    private static RecoveryFileObservation[] ReadObservations(
        LinuxAnchoredFileSystem game,
        IEnumerable<NormalizedRelativePath> paths,
        CancellationToken cancellationToken
    )
    {
        List<RecoveryFileObservation> result = new();
        foreach (NormalizedRelativePath path in paths.Distinct().OrderBy(path => path.Value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(ReadObservation(game, path, cancellationToken));
        }
        return result.ToArray();
    }
}
