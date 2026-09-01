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
    /// <summary>Get the deterministic exact file candidates which this blocked inspection can authorize.</summary>
    public IReadOnlyList<ModifiedFileReplacementCandidate> ModifiedFileReplacementCandidates { get; }
    internal BoundInstallationPlan Binding { get; }
    internal IVerifiedPackageContentAuthority? TargetPackageContent { get; }
    internal object? TargetPackageAuthorityIdentity => this.TargetPackageContent?.AuthorityIdentity;
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
            () => this.ApproveFileReplacements(sourceInspection, selectedCandidates, requireRepair: true, cancellationToken),
            cancellationToken
        );

    /// <summary>
    /// Select exact core-minted replacement or removal candidates from a blocked install, update, repair, or uninstall
    /// inspection, revalidate them through the anchored game root, and return a replacement inspection.
    /// </summary>
    /// <remarks>
    /// Partial selection leaves all unselected conflicts blocked. The source inspection and every borrowed content
    /// authority must remain usable until this method completes; this method does not dispose them.
    /// </remarks>
    public Task<InspectedInstallationState> ApproveFileReplacementsAsync(
        InspectedInstallationState sourceInspection,
        IEnumerable<ModifiedFileReplacementCandidate> selectedCandidates,
        CancellationToken cancellationToken = default
    )
        => Task.Run(
            () => this.ApproveFileReplacements(sourceInspection, selectedCandidates, requireRepair: false, cancellationToken),
            cancellationToken
        );

    /// <summary>
    /// Explicitly recover any interrupted installer transaction under one anchored exclusive lease, then invalidate
    /// every prior inspection by publishing a newer operation generation.
    /// </summary>
    /// <remarks>
    /// Cancellation is honored before recovery begins. Once recovery starts it runs to a safe durable conclusion.
    /// The caller must discard every prior inspection after this method returns or throws
    /// <see cref="InterruptedOperationRecoveryException"/>. That typed exception preserves any exact recoveries which
    /// completed before a later recovery or cleanup boundary failed.
    /// </remarks>
    public Task<InterruptedOperationRecoveryResult> RecoverInterruptedOperationAsync(
        string gameRoot,
        CancellationToken cancellationToken = default
    )
        => Task.Run(() => this.RecoverInterruptedOperation(gameRoot, cancellationToken), cancellationToken);

    private InterruptedOperationRecoveryResult RecoverInterruptedOperation(
        string gameRoot,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.Progress.Report(new(TransactionStage.AcquiringLock, 0, null));
        using InstallerOperationLease lease = InstallerOperationLease.Acquire(gameRoot);
        cancellationToken.ThrowIfCancellationRequested();

        GameRootIdentity gameRootIdentity = lease.RootIdentity;
        ulong previousGeneration = lease.Generation;
        this.Progress.Report(new(TransactionStage.Recovering, 0, null));
        IReadOnlyList<TransactionResult> recovered;
        try
        {
            recovered = this.Executor.RecoverLocked(lease);
        }
        catch (TransactionRecoveryAttemptException exception)
        {
            ulong? failureCurrentGeneration;
            try
            {
                (_, failureCurrentGeneration) = InstallerOperationLease.ReadGeneration(lease.Workspace);
            }
            catch (Exception generationObservationFailure) when (
                generationObservationFailure is IOException or UnauthorizedAccessException or InstallerTransactionException
            )
            {
                failureCurrentGeneration = null;
            }
            bool? failureNamedRootStillSelected;
            try
            {
                using LinuxAnchoredFileSystem currentlyNamedRoot = new(gameRootIdentity.CanonicalPath);
                failureNamedRootStillSelected = gameRootIdentity.Matches(currentlyNamedRoot.Identity);
            }
            catch (Exception namedRootObservationFailure) when (
                namedRootObservationFailure is IOException or UnauthorizedAccessException or InstallerTransactionException
            )
            {
                failureNamedRootStillSelected = null;
            }
            TransactionErrorCode code = InstallerTransactionExecutor.GetErrorCode(exception);
            throw new InterruptedOperationRecoveryException(
                gameRootIdentity,
                previousGeneration,
                failureCurrentGeneration,
                failureNamedRootStillSelected,
                exception.RecoveredTransactions,
                code,
                InstallerTransactionExecutor.SafeMessage(code) ?? "Interrupted-operation recovery did not reach a safe completed result.",
                exception
            );
        }
        if (lease.Generation == previousGeneration)
            lease.ReserveNextGeneration(previousGeneration);
        else if (recovered.Count == 0 || lease.Generation <= previousGeneration)
            throw new InstallerTransactionException(TransactionErrorCode.RecoveryFailed, "Interrupted-operation recovery published an inconsistent operation generation.");

        bool namedRootStillSelected = lease.AssertAnchoredRootAndGenerationAndCheckNamedSelection(
            gameRootIdentity,
            lease.Generation
        );
        InterruptedOperationRecoveryResult result = new(
            gameRootIdentity,
            previousGeneration,
            lease.Generation,
            recovered,
            namedRootStillSelected
        );
        this.Progress.Report(new(TransactionStage.Completed, recovered.Count, recovered.Count));
        return result;
    }

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
        LinuxGameDiscovery.AssertValid(inspection, cancellationToken);
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

    private InspectedInstallationState ApproveFileReplacements(
        InspectedInstallationState sourceInspection,
        IEnumerable<ModifiedFileReplacementCandidate> selectedCandidates,
        bool requireRepair,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(sourceInspection);
        ArgumentNullException.ThrowIfNull(selectedCandidates);
        this.Progress.Report(new(TransactionStage.Inspecting, 0, null));
        sourceInspection.AssertUsable();
        cancellationToken.ThrowIfCancellationRequested();
        bool supportedAction = sourceInspection.Action is
            InstallationAction.Install or InstallationAction.Update or InstallationAction.Repair or InstallationAction.Uninstall;
        if ((requireRepair && sourceInspection.Action != InstallationAction.Repair) || !supportedAction || sourceInspection.Plan.CanExecute)
        {
            throw new ExecutionCompilationException(
                ExecutionCompilationError.NonExecutablePlan,
                requireRepair
                    ? "Repair candidates can only be selected from a blocked repair inspection."
                    : "File candidates can only be selected from a supported blocked inspection."
            );
        }
        IVerifiedPackageContentAuthority? targetPackage = sourceInspection.TargetPackageContent;
        if (sourceInspection.Action is InstallationAction.Install or InstallationAction.Update or InstallationAction.Repair && targetPackage is null)
            throw new ExecutionCompilationException(ExecutionCompilationError.StaleManifest, "The inspection has no live package authority.");

        List<ModifiedFileReplacementCandidate> selected = new();
        HashSet<ModifiedFileReplacementCandidate> unique = new(ReferenceEqualityComparer.Instance);
        foreach (ModifiedFileReplacementCandidate? candidate in selectedCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (selected.Count >= sourceInspection.ModifiedFileReplacementCandidates.Count)
                throw new ArgumentException("The file-candidate selection exceeds the bounded issued set.", nameof(selectedCandidates));
            if (candidate is null)
                throw new ArgumentException("A selected file candidate can't be null.", nameof(selectedCandidates));
            if (!unique.Add(candidate))
                throw new ArgumentException("A file candidate can't be selected more than once.", nameof(selectedCandidates));
            if (
                !ReferenceEquals(candidate.SourceAuthority, sourceInspection.RepairCandidateAuthority)
                || !sourceInspection.ModifiedFileReplacementCandidates.Any(issued => ReferenceEquals(issued, candidate))
            )
            {
                throw new ExecutionCompilationException(
                    ExecutionCompilationError.StalePlan,
                    "A selected file candidate wasn't issued by this exact inspection."
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
                "The game root or installer generation changed after the file candidates were inspected."
            );
        }
        LinuxGameDiscovery.AssertValid(inspection, cancellationToken);
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
                    $"File candidate '{candidate.Path}' changed after inspection."
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
            sourceInspection.Action,
            targetPackage,
            sourceInspection.RollbackContent,
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

    /// <summary>Execute one exact inspection and return a bounded truthful result for success, cancellation, rollback, or interruption.</summary>
    public Task<InstallationExecutionOutcome> ExecuteWithOutcomeAsync(
        InspectedInstallationState inspection,
        Sha256Digest confirmedDigest,
        string? sanitizedLogPath = null,
        CancellationToken cancellationToken = default
    )
        => Task.Run(() => this.ExecuteWithOutcome(inspection, confirmedDigest, sanitizedLogPath, cancellationToken));

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
        LinuxGameDiscovery.AssertValid(lease, cancellationToken);
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

    private InstallationExecutionOutcome ExecuteWithOutcome(
        InspectedInstallationState inspection,
        Sha256Digest confirmedDigest,
        string? sanitizedLogPath,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(confirmedDigest);
        ValidateSanitizedLogPath(sanitizedLogPath);
        InstallationAction action = inspection.Action;
        try
        {
            inspection.AssertUsable();
            cancellationToken.ThrowIfCancellationRequested();
            if (!inspection.Plan.CanExecute)
                throw new ExecutionCompilationException(ExecutionCompilationError.NonExecutablePlan, "A plan with unresolved conflicts can't execute.");
            if (inspection.ConfirmationDigest != confirmedDigest)
                throw new ExecutionCompilationException(ExecutionCompilationError.StalePlan, "The supplied confirmation digest doesn't match this inspected plan.");

            using InstallerOperationLease lease = InstallerOperationLease.Acquire(inspection.GameRoot.CanonicalPath);
            lease.AssertRootAndGeneration(inspection.GameRoot, inspection.OperationGeneration);
            IReadOnlyList<TransactionResult> recovered = this.Executor.RecoverLocked(lease);
            if (recovered.Count > 0)
            {
                return new(
                    action,
                    InstallationExecutionStatus.AutomaticRecoveryCompletedFreshInspectionRequired,
                    null,
                    recovered,
                    TransactionErrorCode.PathChanged,
                    "An interrupted operation was recovered. Inspect and confirm the installation again.",
                    sanitizedLogPath
                );
            }
            lease.AssertRootAndGeneration(inspection.GameRoot, inspection.OperationGeneration);
            LinuxGameDiscovery.AssertValid(lease, cancellationToken);
            using InspectedInstallationState current = this.InspectLocked(
                lease,
                action,
                inspection.TargetPackageContent,
                inspection.RollbackContent,
                inspection.ModifiedFileReplacementApprovals,
                cancellationToken
            );
            if (current.ConfirmationDigest != inspection.ConfirmationDigest)
                throw new ExecutionCompilationException(ExecutionCompilationError.StalePlan, "The installation state changed after confirmation.");
            AnchoredCoreStateAuthority coreState = AnchoredCoreStateAuthority.Inspect(lease);
            InstallationPlanningRequest request = InstallationStateInspector.CreateRequest(
                lease.Game,
                action,
                inspection.TargetPackageContent,
                inspection.RollbackContent,
                coreState,
                inspection.ModifiedFileReplacementApprovals,
                cancellationToken
            );
            InstallationExecutionPreparation preparation = this.Compiler.Prepare(current.Binding, current.Plan, request, Guid.NewGuid());
            TransactionExecutionOutcome transaction = this.Materializer.ApplyWithOutcome(lease, preparation, coreState, cancellationToken);
            return new(
                action,
                MapStatus(transaction.Status),
                transaction,
                Array.Empty<TransactionResult>(),
                transaction.ErrorCode,
                transaction.SafeMessage,
                sanitizedLogPath
            );
        }
        catch (OperationCanceledException)
        {
            return new(action, InstallationExecutionStatus.CancelledBeforeMutation, null, Array.Empty<TransactionResult>(), null, "The operation was cancelled before mutation.", sanitizedLogPath);
        }
        catch (TransactionRecoveryAttemptException exception)
        {
            TransactionErrorCode code = InstallerTransactionExecutor.GetErrorCode(exception);
            return new(
                action,
                InstallationExecutionStatus.InterruptedRecoveryRequired,
                null,
                exception.RecoveredTransactions,
                code,
                InstallerTransactionExecutor.SafeMessage(code),
                sanitizedLogPath
            );
        }
        catch (Exception exception)
        {
            TransactionErrorCode code = InstallerTransactionExecutor.GetErrorCode(exception);
            return new(action, InstallationExecutionStatus.FailedBeforeMutation, null, Array.Empty<TransactionResult>(), code, InstallerTransactionExecutor.SafeMessage(code), sanitizedLogPath);
        }
    }

    private static InstallationExecutionStatus MapStatus(TransactionOutcomeStatus status)
        => status switch
        {
            TransactionOutcomeStatus.Committed => InstallationExecutionStatus.Succeeded,
            TransactionOutcomeStatus.CommittedWithCleanupWarning => InstallationExecutionStatus.SucceededWithCleanupWarning,
            TransactionOutcomeStatus.CancelledBeforeMutation => InstallationExecutionStatus.CancelledBeforeMutation,
            TransactionOutcomeStatus.CancelledAndRolledBack => InstallationExecutionStatus.CancelledAndRolledBack,
            TransactionOutcomeStatus.FailedAndRolledBack => InstallationExecutionStatus.FailedAndRolledBack,
            TransactionOutcomeStatus.InterruptedRecoveryRequired or TransactionOutcomeStatus.RollbackFailedRecoveryRequired
                => InstallationExecutionStatus.InterruptedRecoveryRequired,
            _ => InstallationExecutionStatus.FailedBeforeMutation
        };

    private static void ValidateSanitizedLogPath(string? path)
    {
        if (path is null)
            return;
        if (path.Length is 0 or > 1024 || path.Any(char.IsControl))
            throw new ArgumentException("A sanitized log path must be nonempty, bounded, and contain no control characters.", nameof(path));
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
        RecoveryHistory result;
        try
        {
            result = CommittedRecoveryHandle.ListHistory(
                inspection.Game,
                inspection.CanonicalGameRoot,
                inspection.RootIdentity,
                state,
                cancellationToken,
                this.Progress
            );
        }
        catch (NoCommittedRecoveryHistoryException)
        {
            state.AssertUsable(inspection.Game, inspection.RootIdentity);
            inspection.AssertStable();
            this.Progress.Report(new(TransactionStage.VerifyingRecovery, 1, 1));
            throw;
        }
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

    /// <summary>Execute a recovery-pruning decision and return its exact logical-publication and physical-cleanup result.</summary>
    public Task<RecoveryPruneOutcome> ExecuteRecoveryPruneWithOutcomeAsync(
        RecoveryPrunePlan plan,
        Sha256Digest confirmedDigest,
        CancellationToken cancellationToken = default
    )
        => Task.Run(() => this.ExecuteRecoveryPruneWithOutcome(plan, confirmedDigest, cancellationToken));

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

    private RecoveryPruneOutcome ExecuteRecoveryPruneWithOutcome(
        RecoveryPrunePlan plan,
        Sha256Digest confirmedDigest,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(confirmedDigest);
        if (plan.ConfirmationDigest != confirmedDigest)
            return new(RecoveryPruneOutcomeStatus.FailedBeforePublication, Array.Empty<Guid>(), Array.Empty<Guid>(), Array.Empty<Guid>(), false, TransactionErrorCode.PathChanged, "The recovery history changed and must be inspected again.");
        try
        {
            using InstallerOperationLease lease = InstallerOperationLease.Acquire(plan.GameRoot.CanonicalPath);
            lease.AssertRootAndGeneration(plan.GameRoot, plan.OperationGeneration);
            if (this.Executor.RecoverLocked(lease).Count > 0)
                return new(RecoveryPruneOutcomeStatus.FailedBeforePublication, Array.Empty<Guid>(), Array.Empty<Guid>(), Array.Empty<Guid>(), false, TransactionErrorCode.PathChanged, "An interrupted operation was recovered. Inspect recovery retention again.");
            lease.AssertRootAndGeneration(plan.GameRoot, plan.OperationGeneration);
            AnchoredCoreStateAuthority state = AnchoredCoreStateAuthority.Inspect(lease);
            cancellationToken.ThrowIfCancellationRequested();
            return CommittedRecoveryHandle.ExecutePrunePlanWithOutcome(
                lease,
                state,
                plan,
                this.RecoveryPruneFaultInjector,
                cancellationToken,
                this.Progress
            );
        }
        catch (OperationCanceledException)
        {
            return new(RecoveryPruneOutcomeStatus.CancelledBeforePublication, Array.Empty<Guid>(), Array.Empty<Guid>(), Array.Empty<Guid>(), false, null, "Recovery pruning was cancelled before exact history revalidation. List recoveries and inspect pruning again.");
        }
        catch (Exception exception)
        {
            TransactionErrorCode code = InstallerTransactionExecutor.GetErrorCode(exception);
            return new(RecoveryPruneOutcomeStatus.FailedBeforePublication, Array.Empty<Guid>(), Array.Empty<Guid>(), Array.Empty<Guid>(), false, code, "The recovery history couldn't be revalidated. List recoveries and inspect pruning again.");
        }
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
        if (targetPackage is not null)
            targetPackage = GeneratedPackageContentAuthority.Resolve(game, targetPackage, cancellationToken);
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
        ModifiedFileReplacementCandidate[] repairCandidates = InstallationStateInspector.CreateReplacementCandidates(
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
        PackageManifest? installedManifest = state.Manifest;
        if (receipt is not null && installedManifest is not null && installedManifest.GeneratedFiles.Count > 0)
        {
            PackageManifest evolved = GeneratedPackageContentAuthority.ResolveInstalledManifestEvolution(
                game,
                installedManifest,
                cancellationToken
            );
            bool exactGeneratedResults = evolved.Entries
                .Where(entry => entry.Kind == OwnedEntryKind.GeneratedFile)
                .All(entry => currentFiles.SingleOrDefault(file => file.Path.Equals(entry.Path)) is CurrentFile current
                    && current.Sha256 == entry.Sha256
                    && current.UnixMode == entry.UnixMode
                );
            if (exactGeneratedResults && evolved.GetCanonicalDigest() != installedManifest.GetCanonicalDigest())
            {
                installedManifest = evolved;
                receipt = new InstallationReceipt(
                    evolved.Release,
                    evolved.GetCanonicalDigest(),
                    receipt.TransactionId,
                    evolved.Entries.Select(entry => new InstallationReceiptEntry(
                        entry.Path,
                        entry.Sha256,
                        entry.UnixMode,
                        entry.Kind
                    )),
                    receipt.Launcher,
                    receipt.ReleaseTrust,
                    receipt.SchemaVersion
                );
            }
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
        NormalizedRelativePath[] legacyPaths = receipt is null && targetManifest is not null
            ? targetManifest.Entries
                .Where(OwnedNamespacePolicy.IsRecognizedLegacyCandidate)
                .Select(entry => entry.Path)
                .ToArray()
            : Array.Empty<NormalizedRelativePath>();
        InstallationInventory inventory = InstallationInventory.Create(
            targetManifest,
            receipt,
            currentFiles,
            legacyPaths: legacyPaths
        );
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
            observedState,
            installedManifest,
            state.ManifestSha256,
            state.ReceiptSha256,
            state.Manifest,
            state.Receipt
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
            observedState,
            installedManifest,
            state.ManifestSha256,
            state.ReceiptSha256,
            state.Manifest,
            state.Receipt
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
            bool hasLegacyEvidence = inventory.Entries.Any(entry =>
                !entry.Path.Equals(LauncherPath)
                && entry.Current is not null
                && entry.Classification == InventoryClassification.Legacy
            );
            if (hasLegacyEvidence)
                return ObservedInstallationState.LegacyOrOfficial;
            bool hasUnknownCollision = inventory.Entries.Any(entry =>
                !entry.Path.Equals(LauncherPath)
                && entry.Current is not null
                && entry.Classification == InventoryClassification.UnknownCollision
            );
            return launcher.Classification == LauncherClassification.FreshVanilla && !hasUnknownCollision
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

    internal static ModifiedFileReplacementCandidate[] CreateReplacementCandidates(
        LinuxAnchoredFileSystem game,
        InstallationPlanningRequest request,
        InstallationPlan plan,
        object sourceAuthority,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(sourceAuthority);
        if (
            request.Action is not (InstallationAction.Install or InstallationAction.Update or InstallationAction.Repair or InstallationAction.Uninstall)
            || plan.CanExecute
        )
            return Array.Empty<ModifiedFileReplacementCandidate>();

        List<NormalizedRelativePath> candidatePaths = plan.Conflicts
            .Where(conflict => conflict.Path is not null && IsApprovableConflict(request.Action, conflict.Code))
            .Select(conflict => conflict.Path!)
            .ToList();
        if (IsOfficialLauncherAdoptionCandidate(request, plan))
        {
            candidatePaths.Add(LauncherPath);
            candidatePaths.Add(LauncherBackupPath);
        }
        NormalizedRelativePath[] paths = candidatePaths
            .Distinct()
            .OrderBy(path => path.Value, StringComparer.Ordinal)
            .Where(path => IsExactReplacementCandidate(request, path))
            .ToArray();
        List<ModifiedFileReplacementCandidate> result = new(paths.Length);
        foreach (NormalizedRelativePath path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecoveryFileObservation observation = ReadObservation(game, path, cancellationToken);
            RecoveryFileIdentity identity = observation.Identity
                ?? throw new InstallerTransactionException(TransactionErrorCode.PathChanged, $"File candidate '{path}' disappeared during inspection.");
            (Sha256Digest expectedSha256, int expectedUnixMode) = GetExpectedCandidateIdentity(request, path);
            if (identity.Sha256 != expectedSha256 || identity.UnixMode != expectedUnixMode)
                throw new InstallerTransactionException(TransactionErrorCode.PathChanged, $"File candidate '{path}' changed during inspection.");
            if (request.ModifiedFileReplacementApprovals.Any(approval =>
                approval.Path.Equals(path) && approval.ObservedIdentity == identity
            ))
                continue;
            (FileReplacementCandidateReason reason, FileReplacementCandidateDisposition disposition, Sha256Digest? proposedResult) =
                GetCandidatePresentation(request, path, identity.Sha256);
            result.Add(new ModifiedFileReplacementCandidate(
                sourceAuthority,
                path,
                identity,
                reason,
                disposition,
                proposedResult
            ));
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

    private static bool IsApprovableConflict(InstallationAction action, PlanConflictCode code)
    {
        return action switch
        {
            InstallationAction.Install => code is PlanConflictCode.LegacyOwnershipUnconfirmed or PlanConflictCode.UnknownCollision,
            InstallationAction.Update => code is PlanConflictCode.ModifiedOwnedFile or PlanConflictCode.ModifiedInstalledLauncher
                or PlanConflictCode.LegacyOwnershipUnconfirmed or PlanConflictCode.UnknownCollision,
            InstallationAction.Repair => code is PlanConflictCode.ModifiedOwnedFile or PlanConflictCode.ModifiedInstalledLauncher,
            InstallationAction.Uninstall => code is PlanConflictCode.ModifiedOwnedFile or PlanConflictCode.ModifiedInstalledLauncher,
            _ => false
        };
    }

    private static bool IsOfficialLauncherAdoptionCandidate(InstallationPlanningRequest request, InstallationPlan plan)
    {
        return request.Action == InstallationAction.Install
            && request.InstalledReceipt is null
            && request.TargetManifest is not null
            && request.Launcher.Classification == LauncherClassification.AmbiguousBackup
            && request.Launcher.CurrentLauncherSha256 is not null
            && request.Launcher.CurrentLauncherUnixMode is not null
            && request.Launcher.BackupLauncherSha256 is not null
            && request.Launcher.BackupLauncherUnixMode is not null
            && request.Inventory.Entries.Any(entry => entry.Current is not null && entry.Classification == InventoryClassification.Legacy)
            && plan.Conflicts.Any(conflict => conflict.Code == PlanConflictCode.AmbiguousLauncherBackup);
    }

    private static bool IsExactReplacementCandidate(InstallationPlanningRequest request, NormalizedRelativePath path)
    {
        if (path.Equals(LauncherPath))
        {
            bool modifiedOwnedLauncher = request.Launcher.Classification == LauncherClassification.InstalledModified
                && request.Launcher.CurrentLauncherSha256 is not null
                && request.Launcher.CurrentLauncherUnixMode is not null
                && request.InstalledReceipt is not null
                && (request.Action == InstallationAction.Uninstall || request.TargetManifest is not null);
            bool officialLauncher = request.Action == InstallationAction.Install
                && request.InstalledReceipt is null
                && request.TargetManifest is not null
                && request.Launcher.Classification == LauncherClassification.AmbiguousBackup
                && request.Launcher.CurrentLauncherSha256 is not null
                && request.Launcher.CurrentLauncherUnixMode is not null;
            return modifiedOwnedLauncher || officialLauncher;
        }
        if (path.Equals(LauncherBackupPath))
        {
            return request.Action == InstallationAction.Install
                && request.InstalledReceipt is null
                && request.TargetManifest is not null
                && request.Launcher.Classification == LauncherClassification.AmbiguousBackup
                && request.Launcher.BackupLauncherSha256 is not null
                && request.Launcher.BackupLauncherUnixMode is not null;
        }
        InventoryEntry? entry = request.Inventory.Entries.SingleOrDefault(candidate => candidate.Path.Equals(path));
        if (entry?.Current is null)
            return false;
        return request.Action switch
        {
            InstallationAction.Install => entry.Target is not null
                && entry.Installed is null
                && entry.Classification is InventoryClassification.Legacy or InventoryClassification.UnknownCollision,
            InstallationAction.Update => (
                    entry.Classification == InventoryClassification.ModifiedOwned
                    && entry.Installed is not null
                ) || (
                    entry.Target is not null
                    && entry.Installed is null
                    && entry.Classification is InventoryClassification.Legacy or InventoryClassification.UnknownCollision
                ),
            InstallationAction.Repair => entry.Classification == InventoryClassification.ModifiedOwned
                && entry.Installed is not null
                && entry.Target is not null,
            InstallationAction.Uninstall => entry.Classification == InventoryClassification.ModifiedOwned
                && entry.Installed is not null,
            _ => false
        };
    }

    private static (Sha256Digest Sha256, int UnixMode) GetExpectedCandidateIdentity(
        InstallationPlanningRequest request,
        NormalizedRelativePath path
    )
    {
        if (path.Equals(LauncherPath))
        {
            return (
                request.Launcher.CurrentLauncherSha256
                    ?? throw new InstallerTransactionException(TransactionErrorCode.PathChanged, "The launcher candidate has no content identity."),
                request.Launcher.CurrentLauncherUnixMode
                    ?? throw new InstallerTransactionException(TransactionErrorCode.PathChanged, "The launcher candidate has no mode identity.")
            );
        }
        if (path.Equals(LauncherBackupPath))
        {
            return (
                request.Launcher.BackupLauncherSha256
                    ?? throw new InstallerTransactionException(TransactionErrorCode.PathChanged, "The launcher-backup candidate has no content identity."),
                request.Launcher.BackupLauncherUnixMode
                    ?? throw new InstallerTransactionException(TransactionErrorCode.PathChanged, "The launcher-backup candidate has no mode identity.")
            );
        }
        CurrentFile current = request.Inventory.Entries.Single(entry => entry.Path.Equals(path)).Current
            ?? throw new InstallerTransactionException(TransactionErrorCode.PathChanged, $"File candidate '{path}' has no current inventory observation.");
        return (current.Sha256, current.UnixMode);
    }

    private static (
        FileReplacementCandidateReason Reason,
        FileReplacementCandidateDisposition Disposition,
        Sha256Digest? ProposedResult
    ) GetCandidatePresentation(
        InstallationPlanningRequest request,
        NormalizedRelativePath path,
        Sha256Digest observedSha256
    )
    {
        if (path.Equals(LauncherBackupPath))
        {
            return (
                FileReplacementCandidateReason.OfficialLauncherBackup,
                FileReplacementCandidateDisposition.TrustRetained,
                observedSha256
            );
        }
        if (path.Equals(LauncherPath))
        {
            if (request.Action == InstallationAction.Install && request.InstalledReceipt is null)
            {
                return (
                    FileReplacementCandidateReason.OfficialOrLegacyLauncher,
                    FileReplacementCandidateDisposition.Replace,
                    request.TargetManifest!.Entries.Single(entry => entry.Kind == OwnedEntryKind.Launcher).Sha256
                );
            }
            if (request.Action == InstallationAction.Uninstall)
            {
                return (
                    FileReplacementCandidateReason.ModifiedInstalledLauncher,
                    FileReplacementCandidateDisposition.Restore,
                    request.Launcher.BackupLauncherSha256
                );
            }
            return (
                FileReplacementCandidateReason.ModifiedInstalledLauncher,
                FileReplacementCandidateDisposition.Replace,
                request.TargetManifest!.Entries.Single(entry => entry.Kind == OwnedEntryKind.Launcher).Sha256
            );
        }

        InventoryEntry entry = request.Inventory.Entries.Single(candidate => candidate.Path.Equals(path));
        FileReplacementCandidateReason reason = entry.Classification switch
        {
            InventoryClassification.ModifiedOwned => FileReplacementCandidateReason.ModifiedReceiptOwned,
            InventoryClassification.Legacy => FileReplacementCandidateReason.LegacyInstaller,
            InventoryClassification.UnknownCollision => FileReplacementCandidateReason.UnknownCollision,
            _ => throw new InvalidOperationException($"Candidate '{path}' has no presentable conflict classification.")
        };
        if (request.Action == InstallationAction.Uninstall || entry.Target is null)
            return (reason, FileReplacementCandidateDisposition.Remove, null);
        return (reason, FileReplacementCandidateDisposition.Replace, entry.Target.Sha256);
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
