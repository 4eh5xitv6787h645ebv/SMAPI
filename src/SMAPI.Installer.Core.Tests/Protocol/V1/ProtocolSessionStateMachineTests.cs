using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Core.Recovery;
using StardewModdingAPI.Installer.Core.Security;
using StardewModdingAPI.Installer.Core.Transactions;

namespace StardewModdingAPI.Installer.Core.Tests.Protocol.V1;

[TestFixture]
internal sealed class ProtocolSessionStateMachineTests
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly GameRootIdentity Root = new("/game", 1, 2, 3);

    [Test]
    public void HandshakeAndDiscovery_DefensivelySnapshotCallerArrays()
    {
        ProtocolSessionStateMachine machine = new(); string[] capabilities = ["v1"];
        HandshakeEvent handshake = machine.AcceptHandshake(new("gui", "1"), "server", capabilities); capabilities[0] = "changed";
        handshake.Capabilities.Should().Equal("v1");
        string[] returnedCapabilities = handshake.Capabilities; returnedCapabilities[0] = "mutated"; handshake.Capabilities.Should().Equal("v1");
        ProtocolGameCandidate[] candidates = [new("/game", LinuxGameFolderStatus.Valid, "Game")];
        GameDiscoveryEvent discovery = machine.RecordDiscovery(new(machine.SessionId), candidates); candidates[0] = new("/other", LinuxGameFolderStatus.InvalidGameAssembly, "Other");
        discovery.Candidates.Should().ContainSingle().Which.CanonicalPath.Should().Be("/game");
        ProtocolGameCandidate[] returnedCandidates = discovery.Candidates; returnedCandidates[0] = new("/mutated", LinuxGameFolderStatus.InvalidGameAssembly, "Mutated"); discovery.Candidates.Single().CanonicalPath.Should().Be("/game");
    }

    [Test]
    public void Requests_RejectCrossSessionAndInvalidTransitions()
    {
        ProtocolSessionStateMachine machine = new();
        FluentActions.Invoking(() => machine.RecordDiscovery(new(ProtocolSessionId.CreateRandom()), [])).Should().Throw<ProtocolException>().WithMessage("*Expected protocol state 'Ready'*");
        machine.AcceptHandshake(new("gui", "1"), "server");
        FluentActions.Invoking(() => machine.RecordDiscovery(new(ProtocolSessionId.CreateRandom()), [])).Should().Throw<ProtocolException>().WithMessage("*session ID*");
        FluentActions.Invoking(() => machine.AcceptHandshake(new("again", "1"), "server")).Should().Throw<ProtocolException>().WithMessage("*Ready*");
    }

    [Test]
    public void IssuePlan_DerivesExactPresentationFromCoreInspection()
    {
        ProtocolSessionStateMachine machine = Ready(); InspectedInstallationState inspection = Inspection(InstallationAction.Uninstall, conflicts: [new(PlanConflictCode.ModifiedOwnedFile, NormalizedRelativePath.Parse("StardewModdingAPI.dll"))]);
        PlanEvent plan = machine.IssuePlan(new(machine.SessionId, "/game", InstallerOperation.Uninstall, null, null), inspection);
        plan.ExecutionBindingDigest.Value.Should().Be(inspection.ConfirmationDigest.Value);
        plan.Operations.Should().ContainSingle(); plan.Operations[0].Path.Should().Be(inspection.Plan.Operations[0].Path.Value); plan.Operations[0].Kind.Should().Be(inspection.Plan.Operations[0].Kind);
        plan.Conflicts.Should().ContainSingle().Which.Code.Should().Be(PlanConflictCode.ModifiedOwnedFile);
        plan.Summary.Should().Contain("blocked by 1"); plan.Warnings.Should().ContainSingle().Which.Should().Contain("ModifiedOwnedFile");
        machine.CurrentPlanCanExecute.Should().BeFalse();
        FluentActions.Invoking(() => machine.ConfirmPlan(new(machine.SessionId, plan.PlanId, plan.PlanDigest))).Should().Throw<ProtocolException>().WithMessage("*unresolved conflicts*");
    }

    [Test]
    public void IssuePlan_RejectsCallerActionPathAndAuthorityMismatch()
    {
        ProtocolSessionStateMachine machine = Ready(); InspectedInstallationState inspection = Inspection(InstallationAction.Uninstall);
        FluentActions.Invoking(() => machine.IssuePlan(new(machine.SessionId, "/other", InstallerOperation.Uninstall, null, null), inspection)).Should().Throw<ProtocolException>().WithMessage("*doesn't match*");
        FluentActions.Invoking(() => machine.IssuePlan(new(machine.SessionId, "/game", InstallerOperation.Backup, null, null), Inspection(InstallationAction.Uninstall))).Should().Throw<ProtocolException>().WithMessage("*doesn't match*");
        FluentActions.Invoking(() => machine.IssuePlan(new(machine.SessionId, "/game", InstallerOperation.Install, ProtocolPackageId.CreateRandom(), null), Inspection(InstallationAction.Install, package: new FakePackageAuthority(CreateRelease())))).Should().Throw<ProtocolException>().WithMessage("*unknown, stale*");
    }

    [Test]
    public void PackageRegistry_ResolvesExactAuthorityIdentityAndRejectsAnotherAuthority()
    {
        ProtocolSessionStateMachine machine = Ready(); FakePackageAuthority registered = new(CreateRelease()); PackageOpenedEvent package = Register(machine, registered);
        PlanEvent plan = machine.IssuePlan(new(machine.SessionId, "/game", InstallerOperation.Install, package.PackageId, null), Inspection(InstallationAction.Install, package: registered));
        plan.PackageId.Should().Be(package.PackageId);
        FluentActions.Invoking(() => machine.IssuePlan(new(machine.SessionId, "/game", InstallerOperation.Install, package.PackageId, null), Inspection(InstallationAction.Install, package: new FakePackageAuthority(CreateRelease())))).Should().Throw<ProtocolException>().WithMessage("*exact verified content authority*");
    }

    [Test]
    public void CandidateSelection_IsAdditiveAcrossReplansAndRejectsDroppedOrAlteredPriorApprovals()
    {
        ProtocolSessionStateMachine machine = Ready(); FakePackageAuthority packageAuthority = new(CreateRelease()); PackageOpenedEvent package = Register(machine, packageAuthority);
        object candidateAuthority = new();
        ModifiedFileReplacementCandidate first = new(candidateAuthority, NormalizedRelativePath.Parse("StardewModdingAPI.dll"), new RecoveryFileIdentity(Sha256Digest.Parse(HashA), 10, 420, RecoveryFileType.RegularFile), FileReplacementCandidateReason.ModifiedInstalledLauncher, FileReplacementCandidateDisposition.Restore, Sha256Digest.Parse(HashA));
        ModifiedFileReplacementCandidate second = new(candidateAuthority, NormalizedRelativePath.Parse("smapi-internal/a.dll"), new RecoveryFileIdentity(Sha256Digest.Parse(HashB), 20, 420, RecoveryFileType.RegularFile), FileReplacementCandidateReason.UnknownCollision, FileReplacementCandidateDisposition.Replace, Sha256Digest.Parse(HashB));
        InspectedInstallationState blocked = Inspection(InstallationAction.Repair, packageAuthority, candidates: [first, second], conflicts: [new(PlanConflictCode.ModifiedOwnedFile, first.Path), new(PlanConflictCode.ModifiedOwnedFile, second.Path)], repairAuthority: candidateAuthority);
        PlanEvent old = machine.IssuePlan(new(machine.SessionId, "/game", InstallerOperation.Repair, package.PackageId, null), blocked);
        old.Candidates.Single(candidate => candidate.Path == first.Path.Value).Should().Match<ProtocolPlanCandidate>(candidate => candidate.Reason == FileReplacementCandidateReason.ModifiedInstalledLauncher && candidate.Disposition == FileReplacementCandidateDisposition.Restore && candidate.ProposedResultSha256 == HashA);
        old.Candidates.Single(candidate => candidate.Path == second.Path.Value).Should().Match<ProtocolPlanCandidate>(candidate => candidate.Reason == FileReplacementCandidateReason.UnknownCollision && candidate.Disposition == FileReplacementCandidateDisposition.Replace && candidate.ProposedResultSha256 == HashB);
        ProtocolCandidateId firstId = old.Candidates.Single(candidate => candidate.Path == first.Path.Value).CandidateId;
        SelectPlanCandidatesRequest firstSelection = new(machine.SessionId, old.PlanId, old.PlanDigest, [firstId]);
        ModifiedFileReplacementApproval firstApproval = new(first.Path, first.ObservedIdentity);
        object secondAuthority = new();
        ModifiedFileReplacementCandidate remainingSecond = new(secondAuthority, second.Path, second.ObservedIdentity, second.Reason, second.Disposition, second.ProposedResultSha256);
        InspectedInstallationState firstReplacement = Inspection(InstallationAction.Repair, packageAuthority, candidates: [remainingSecond], approvals: [firstApproval], conflicts: [new(PlanConflictCode.ModifiedOwnedFile, second.Path)], repairAuthority: secondAuthority);
        PlanEvent middle = machine.IssueCandidatePlan(firstSelection, firstReplacement);
        SelectPlanCandidatesRequest secondSelection = new(machine.SessionId, middle.PlanId, middle.PlanDigest, [middle.Candidates.Single().CandidateId]);
        ModifiedFileReplacementApproval secondApproval = new(second.Path, second.ObservedIdentity);

        InspectedInstallationState dropped = Inspection(InstallationAction.Repair, packageAuthority, approvals: [secondApproval], repairAuthority: new object());
        FluentActions.Invoking(() => machine.IssueCandidatePlan(secondSelection, dropped)).Should().Throw<ProtocolException>().WithMessage("*exact selected candidate authorities*");
        ModifiedFileReplacementApproval alteredFirst = new(first.Path, new RecoveryFileIdentity(Sha256Digest.Parse(HashB), 10, 420, RecoveryFileType.RegularFile));
        InspectedInstallationState altered = Inspection(InstallationAction.Repair, packageAuthority, approvals: [alteredFirst, secondApproval], repairAuthority: new object());
        FluentActions.Invoking(() => machine.IssueCandidatePlan(secondSelection, altered)).Should().Throw<ProtocolException>().WithMessage("*exact selected candidate authorities*");

        InspectedInstallationState complete = Inspection(InstallationAction.Repair, packageAuthority, approvals: [firstApproval, secondApproval], repairAuthority: new object());
        PlanEvent current = machine.IssueCandidatePlan(secondSelection, complete);
        current.PlanId.Should().NotBe(middle.PlanId); current.Candidates.Should().BeEmpty();
        FluentActions.Invoking(() => machine.ResolveCandidateSelection(firstSelection)).Should().Throw<ProtocolException>().WithMessage("*stale*");
    }

    [Test]
    public void RecoverySelection_BindsExactCatalogRootHeadAndGeneration()
    {
        ProtocolSessionStateMachine machine = Ready(); FakeRecoveryAuthority current = Recovery(Guid.ParseExact("11111111111111111111111111111111", "N"), HashA, Root);
        RecoveryCatalogEvent catalog = Catalog(machine, [current]);
        ProtocolRecoverySelectionId selection = catalog.Generations.Single().SelectionId;
        PlanEvent plan = machine.IssuePlan(new(machine.SessionId, "/game", InstallerOperation.Rollback, null, selection), Inspection(InstallationAction.Rollback, recovery: current));
        plan.RecoveryAuthority.Should().NotBeNull(); plan.RecoveryAuthority!.CatalogId.Should().Be(catalog.CatalogId); plan.RecoveryAuthority.HeadSha256.Should().Be(HashA); plan.RecoveryAuthority.Generation.GenerationId.Should().Be(current.GenerationId.ToString("N"));

        FakeRecoveryAuthority differentHead = Recovery(current.GenerationId, HashB, Root);
        FluentActions.Invoking(() => machine.IssuePlan(new(machine.SessionId, "/game", InstallerOperation.Rollback, null, selection), Inspection(InstallationAction.Rollback, recovery: differentHead))).Should().Throw<ProtocolException>().WithMessage("*exact committed handle*");
    }

    [Test]
    public void GeneralizedCandidatePlan_PreservesUninstallAndAllowsRemovalResult()
    {
        ProtocolSessionStateMachine machine = Ready(); object authority = new();
        ModifiedFileReplacementCandidate candidate = new(authority, NormalizedRelativePath.Parse("StardewModdingAPI.dll"), new RecoveryFileIdentity(Sha256Digest.Parse(HashA), 10, 420, RecoveryFileType.RegularFile), FileReplacementCandidateReason.ModifiedReceiptOwned, FileReplacementCandidateDisposition.Remove, proposedResultSha256: null);
        InspectedInstallationState blocked = Inspection(InstallationAction.Uninstall, candidates: [candidate], conflicts: [new(PlanConflictCode.ModifiedOwnedFile, candidate.Path)], repairAuthority: authority);
        PlanEvent plan = machine.IssuePlan(new(machine.SessionId, "/game", InstallerOperation.Uninstall, null, null), blocked);
        plan.Candidates.Single().Reason.Should().Be(FileReplacementCandidateReason.ModifiedReceiptOwned); plan.Candidates.Single().Disposition.Should().Be(FileReplacementCandidateDisposition.Remove); plan.Candidates.Single().ProposedResultSha256.Should().BeNull();
        SelectPlanCandidatesRequest selection = new(machine.SessionId, plan.PlanId, plan.PlanDigest, [plan.Candidates.Single().CandidateId]);
        ModifiedFileReplacementApproval approval = new(candidate.Path, candidate.ObservedIdentity);
        PlanEvent replacement = machine.IssueCandidatePlan(selection, Inspection(InstallationAction.Uninstall, approvals: [approval], repairAuthority: new object()));
        replacement.Operation.Should().Be(InstallerOperation.Uninstall);
    }

    [Test]
    public void RelistingCatalog_InvalidatesOldCatalogAndSelectionIds()
    {
        ProtocolSessionStateMachine machine = Ready(); RecoveryCatalogEvent stale = Catalog(machine, [Recovery(Guid.NewGuid(), HashA, Root)]); RecoveryCatalogEvent current = Catalog(machine, [Recovery(Guid.NewGuid(), HashA, Root)]);
        current.CatalogId.Should().NotBe(stale.CatalogId);
        FluentActions.Invoking(() => machine.IssuePlan(new(machine.SessionId, "/game", InstallerOperation.Rollback, null, stale.Generations[0].SelectionId), Inspection(InstallationAction.Rollback, recovery: Recovery(Guid.NewGuid(), HashA, Root)))).Should().Throw<ProtocolException>().WithMessage("*unknown, stale*");
    }

    [Test]
    public void InterruptedRecoveryInvalidatesPlansCatalogsAndStrictlyOrdersProgressAndTerminal()
    {
        ProtocolSessionStateMachine machine = Ready(); FakeRecoveryAuthority handle = Recovery(Guid.NewGuid(), HashA, Root);
        RecoveryCatalogEvent catalog = Catalog(machine, [handle]);
        PlanEvent stale = machine.IssuePlan(new(machine.SessionId, "/game", InstallerOperation.Uninstall, null, null), Inspection(InstallationAction.Uninstall));
        RecoverInterruptedRequest request = new(machine.SessionId, "/game");

        machine.BeginInterruptedRecovery(request);
        machine.State.Should().Be(ProtocolSessionState.Recovering); handle.DisposeCount.Should().Be(1);
        FluentActions.Invoking(() => machine.RecordRecoveryProgress(new(machine.SessionId, 0, TransactionStage.Recovering, 2, 1, "Invalid."))).Should().Throw<ProtocolException>().WithMessage("*inconsistent*");
        machine.LastProgressSequence.Should().Be(-1);
        machine.RecordRecoveryProgress(new(machine.SessionId, 0, TransactionStage.Recovering, 0, null, "Recovering."));
        FluentActions.Invoking(() => machine.RecordRecoveryProgress(new(machine.SessionId, 0, TransactionStage.Completed, 1, 1, "Duplicate."))).Should().Throw<ProtocolException>().WithMessage("*increase monotonically*");
        RecoveryCompletedEvent completed = new(machine.SessionId, new("/game", 1, 2, 3, 8), true, 7, 8, 1, 2, "Recovered.", "Inspect again.", null);
        machine.CompleteInterruptedRecovery(request, completed);

        machine.State.Should().Be(ProtocolSessionState.Ready); machine.LastProgressSequence.Should().Be(-1);
        FluentActions.Invoking(() => machine.ResolveRecoveryCatalog(machine.SessionId, catalog.CatalogId)).Should().Throw<ProtocolException>().WithMessage("*unknown or stale*");
        FluentActions.Invoking(() => machine.ConfirmPlan(new(machine.SessionId, stale.PlanId, stale.PlanDigest))).Should().Throw<ProtocolException>();
    }

    [Test]
    public void PlanProgressCancellationAndTerminals_RequireExactOrderingAndBinding()
    {
        ProtocolSessionStateMachine machine = Ready(); PlanEvent plan = machine.IssuePlan(new(machine.SessionId, "/game", InstallerOperation.Uninstall, null, null), Inspection(InstallationAction.Uninstall));
        FluentActions.Invoking(() => machine.RecordProgress(new(machine.SessionId, plan.PlanId, plan.PlanDigest, 0, TransactionStage.PreparingRecovery, 0, null, "Wait."))).Should().Throw<ProtocolException>().WithMessage("*Progress can't be recorded*");
        FluentActions.Invoking(() => machine.BeginExecution(new(machine.SessionId, plan.PlanId, plan.PlanDigest))).Should().Throw<ProtocolException>().WithMessage("*confirmed*");
        machine.ConfirmPlan(new(machine.SessionId, plan.PlanId, plan.PlanDigest)); machine.BeginExecution(new(machine.SessionId, plan.PlanId, plan.PlanDigest));
        FluentActions.Invoking(() => machine.RecordProgress(new(machine.SessionId, plan.PlanId, plan.PlanDigest, 0, TransactionStage.PreparingRecovery, 2, 1, "Invalid."))).Should().Throw<ProtocolException>().WithMessage("*inconsistent*");
        machine.LastProgressSequence.Should().Be(-1);
        machine.RecordProgress(new(machine.SessionId, plan.PlanId, plan.PlanDigest, 0, TransactionStage.PreparingRecovery, 0, null, "Wait."));
        FluentActions.Invoking(() => machine.RecordProgress(new(machine.SessionId, plan.PlanId, plan.PlanDigest, 0, TransactionStage.Completed, 1, 1, "Again."))).Should().Throw<ProtocolException>().WithMessage("*increase monotonically*");
        machine.RequestCancellation(new(machine.SessionId, plan.PlanId, plan.PlanDigest));
        machine.Complete(new SuccessEvent(machine.SessionId, plan.PlanId, plan.PlanDigest, InstallerOperation.Uninstall, "Done.", 1, ProtocolRecoveryResult.NotNeeded, "Close.", null));
        machine.State.Should().Be(ProtocolSessionState.Completed, "a late cancellation request can't erase a truthful durable commit");
    }

    [Test]
    public void WrongDigest_IsRejectedAcrossRequestsProgressAndEveryPlanTerminal()
    {
        ProtocolSessionStateMachine machine = Ready(); PlanEvent plan = machine.IssuePlan(new(machine.SessionId, "/game", InstallerOperation.Uninstall, null, null), Inspection(InstallationAction.Uninstall)); ProtocolPlanDigest wrong = ProtocolPlanDigest.Parse(HashA);
        FluentActions.Invoking(() => machine.ConfirmPlan(new(machine.SessionId, plan.PlanId, wrong))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
        FluentActions.Invoking(() => machine.RequestCancellation(new(machine.SessionId, plan.PlanId, wrong))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
        machine.ConfirmPlan(new(machine.SessionId, plan.PlanId, plan.PlanDigest));
        FluentActions.Invoking(() => machine.BeginExecution(new(machine.SessionId, plan.PlanId, wrong))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
        machine.BeginExecution(new(machine.SessionId, plan.PlanId, plan.PlanDigest));
        FluentActions.Invoking(() => machine.RecordProgress(new(machine.SessionId, plan.PlanId, wrong, 0, TransactionStage.PreparingRecovery, 0, null, "Wrong."))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
        FluentActions.Invoking(() => machine.Complete(new SuccessEvent(machine.SessionId, plan.PlanId, wrong, InstallerOperation.Uninstall, "Done.", 1, ProtocolRecoveryResult.NotNeeded, "Close.", null))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
        FluentActions.Invoking(() => machine.Complete(new RolledBackFailureEvent(machine.SessionId, plan.PlanId, wrong, "failed", "Failed.", "Restored.", 1, ProtocolRecoveryResult.Succeeded, "Retry.", null))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
        FluentActions.Invoking(() => machine.Complete(new RecoverableInterruptionEvent(machine.SessionId, plan.PlanId, wrong, "interrupted", "Interrupted.", InstallerRecoveryAction.InspectAgain, "Pending.", 1, ProtocolRecoveryResult.Pending, "Inspect.", null))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
        machine.RequestCancellation(new(machine.SessionId, plan.PlanId, plan.PlanDigest));
        FluentActions.Invoking(() => machine.Complete(new CancelledEvent(machine.SessionId, plan.PlanId, wrong, "Cancelled.", "Safe.", 0, ProtocolRecoveryResult.Succeeded, "Close.", null))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
    }

    [Test]
    public void PreExecutionCancellation_CannotMasqueradeAsFailureOrMutation()
    {
        ProtocolSessionStateMachine machine = Ready(); PlanEvent plan = machine.IssuePlan(new(machine.SessionId, "/game", InstallerOperation.Uninstall, null, null), Inspection(InstallationAction.Uninstall));
        machine.RequestCancellation(new(machine.SessionId, plan.PlanId, plan.PlanDigest));
        FluentActions.Invoking(() => machine.Complete(new RolledBackFailureEvent(machine.SessionId, plan.PlanId, plan.PlanDigest, "failed", "Failed.", "Restored.", 0, ProtocolRecoveryResult.Succeeded, "Retry.", null))).Should().Throw<ProtocolException>().WithMessage("*terminal event can't be recorded*");
        FluentActions.Invoking(() => machine.Complete(new CancelledEvent(machine.SessionId, plan.PlanId, plan.PlanDigest, "Cancelled.", "Safe.", 1, ProtocolRecoveryResult.Succeeded, "Close.", null))).Should().Throw<ProtocolException>().WithMessage("*pre-execution cancellation*");
        machine.Complete(new CancelledEvent(machine.SessionId, plan.PlanId, plan.PlanDigest, "Cancelled.", "Safe.", 0, ProtocolRecoveryResult.NotNeeded, "Close.", null));
    }

    [Test]
    public void PrunePlan_RequiresExactStoredCatalogPartitionAndRejectsNoOp()
    {
        ProtocolSessionStateMachine machine = Ready(); FakeRecoveryAuthority first = Recovery(Guid.ParseExact("11111111111111111111111111111111", "N"), HashA, Root); FakeRecoveryAuthority second = Recovery(Guid.ParseExact("22222222222222222222222222222222", "N"), HashA, Root); RecoveryCatalogEvent catalog = Catalog(machine, [first, second]);
        RecoveryPrunePlan core = Prune([first.GenerationId, second.GenerationId], 1, [first.GenerationId], [second.GenerationId]);
        PrunePlanEvent plan = machine.IssuePrunePlan(new(machine.SessionId, catalog.CatalogId, 1), core); plan.RemovedSelectionIds.Should().ContainSingle();
        ProtocolSessionStateMachine reorderedMachine = Ready(); RecoveryCatalogEvent reorderedCatalog = Catalog(reorderedMachine, [first, second]); RecoveryPrunePlan reordered = Prune([second.GenerationId, first.GenerationId], 1, [second.GenerationId], [first.GenerationId]);
        FluentActions.Invoking(() => reorderedMachine.IssuePrunePlan(new(reorderedMachine.SessionId, reorderedCatalog.CatalogId, 1), reordered)).Should().Throw<ProtocolException>().WithMessage("*exact stored catalog order*");
        ProtocolSessionStateMachine noOpMachine = Ready(); RecoveryCatalogEvent noOpCatalog = Catalog(noOpMachine, [first, second]); RecoveryPrunePlan noOp = Prune([first.GenerationId, second.GenerationId], 2, [first.GenerationId, second.GenerationId], []);
        FluentActions.Invoking(() => noOpMachine.IssuePrunePlan(new(noOpMachine.SessionId, noOpCatalog.CatalogId, 2), noOp)).Should().Throw<ProtocolException>().WithMessage("*no-op*");
    }

    [Test]
    public void PrunePlan_CleanupOnlyIsExecutableAndReportsPhysicalWorkHonestly()
    {
        ProtocolSessionStateMachine machine = Ready(); FakeRecoveryAuthority first = Recovery(Guid.ParseExact("11111111111111111111111111111111", "N"), HashA, Root); FakeRecoveryAuthority second = Recovery(Guid.ParseExact("22222222222222222222222222222222", "N"), HashA, Root); RecoveryCatalogEvent catalog = Catalog(machine, [first, second]);
        Guid pending = Guid.ParseExact("33333333333333333333333333333333", "N");
        RecoveryPrunePlan core = Prune([first.GenerationId, second.GenerationId], 2, [first.GenerationId, second.GenerationId], [], [pending]);
        PrunePlanEvent plan = machine.IssuePrunePlan(new(machine.SessionId, catalog.CatalogId, 2), core);
        plan.RemovedSelectionIds.Should().BeEmpty(); plan.CleanupGenerationIds.Should().Equal(pending.ToString("N")); plan.Summary.Should().Contain("Logically remove 0").And.Contain("clean up 1");
        machine.ConfirmPrune(new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest)); machine.BeginPrune(new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest));
        FluentActions.Invoking(() => machine.Complete(new PruneSuccessEvent(machine.SessionId, plan.PrunePlanId, plan.PruneDigest, 0, 0, "Wrong.", "Inspect.", null))).Should().Throw<ProtocolException>().WithMessage("*physical-cleanup count*");
        machine.Complete(new PruneSuccessEvent(machine.SessionId, plan.PrunePlanId, plan.PruneDigest, 0, 1, "Cleaned.", "Close.", null));
    }

    [Test]
    public void PruneProgressCancellationAndTerminalStates_AreStrict()
    {
        ProtocolSessionStateMachine machine = Ready(); FakeRecoveryAuthority first = Recovery(Guid.ParseExact("11111111111111111111111111111111", "N"), HashA, Root); FakeRecoveryAuthority second = Recovery(Guid.ParseExact("22222222222222222222222222222222", "N"), HashA, Root); RecoveryCatalogEvent catalog = Catalog(machine, [first, second]);
        PrunePlanEvent plan = machine.IssuePrunePlan(new(machine.SessionId, catalog.CatalogId, 1), Prune([first.GenerationId, second.GenerationId], 1, [first.GenerationId], [second.GenerationId]));
        FluentActions.Invoking(() => machine.RecordPruneProgress(new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest, 0, TransactionStage.Revalidating, 0, null, "Wait."))).Should().Throw<ProtocolException>().WithMessage("*Prune progress can't be recorded*");
        machine.ConfirmPrune(new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest)); machine.BeginPrune(new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest));
        FluentActions.Invoking(() => machine.RecordPruneProgress(new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest, 0, TransactionStage.Revalidating, 2, 1, "Invalid."))).Should().Throw<ProtocolException>().WithMessage("*inconsistent*");
        machine.LastProgressSequence.Should().Be(-1);
        machine.RecordPruneProgress(new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest, 0, TransactionStage.Revalidating, 0, null, "Wait."));
        machine.RequestPruneCancellation(new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest));
        machine.RecordPruneProgress(new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest, 1, TransactionStage.Completed, 1, 1, "Stopping."));
        machine.Complete(new PruneCancelledEvent(machine.SessionId, plan.PrunePlanId, plan.PruneDigest, "Cancelled.", "Safe.", 0, 0, ProtocolRecoveryResult.Succeeded, "Inspect.", null)); machine.State.Should().Be(ProtocolSessionState.Completed);
    }

    [Test]
    public void WrongPruneDigest_IsRejectedAcrossRequestsProgressAndEveryTerminal()
    {
        ProtocolSessionStateMachine machine = Ready(); FakeRecoveryAuthority first = Recovery(Guid.ParseExact("11111111111111111111111111111111", "N"), HashA, Root); FakeRecoveryAuthority second = Recovery(Guid.ParseExact("22222222222222222222222222222222", "N"), HashA, Root); RecoveryCatalogEvent catalog = Catalog(machine, [first, second]);
        PrunePlanEvent plan = machine.IssuePrunePlan(new(machine.SessionId, catalog.CatalogId, 1), Prune([first.GenerationId, second.GenerationId], 1, [first.GenerationId], [second.GenerationId])); ProtocolPlanDigest wrong = ProtocolPlanDigest.Parse(HashA);
        FluentActions.Invoking(() => machine.ConfirmPrune(new(machine.SessionId, plan.PrunePlanId, wrong))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
        FluentActions.Invoking(() => machine.RequestPruneCancellation(new(machine.SessionId, plan.PrunePlanId, wrong))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
        machine.ConfirmPrune(new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest));
        FluentActions.Invoking(() => machine.BeginPrune(new(machine.SessionId, plan.PrunePlanId, wrong))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
        machine.BeginPrune(new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest));
        FluentActions.Invoking(() => machine.RecordPruneProgress(new(machine.SessionId, plan.PrunePlanId, wrong, 0, TransactionStage.Revalidating, 0, null, "Wrong."))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
        FluentActions.Invoking(() => machine.Complete(new PruneSuccessEvent(machine.SessionId, plan.PrunePlanId, wrong, 1, 1, "Done.", "Close.", null))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
        FluentActions.Invoking(() => machine.Complete(new PruneFailureEvent(machine.SessionId, plan.PrunePlanId, wrong, "failed", "Failed.", 0, 0, ProtocolRecoveryResult.Succeeded, "Retry.", null))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
        FluentActions.Invoking(() => machine.Complete(new PruneInterruptionEvent(machine.SessionId, plan.PrunePlanId, wrong, "interrupted", "Interrupted.", InstallerRecoveryAction.InspectAgain, 0, 0, ProtocolRecoveryResult.Pending, "Inspect.", null))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
        machine.RequestPruneCancellation(new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest));
        FluentActions.Invoking(() => machine.Complete(new PruneCancelledEvent(machine.SessionId, plan.PrunePlanId, wrong, "Cancelled.", "Safe.", 0, 0, ProtocolRecoveryResult.Succeeded, "Inspect.", null))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
    }

    [Test]
    public void PrePlanErrorAndDisposal_EnforceTerminalLifecycle()
    {
        ProtocolSessionStateMachine machine = Ready(); machine.RecordPrePlanError(new(machine.SessionId, "bad", "Bad package.", "Retry.", false, null)); machine.State.Should().Be(ProtocolSessionState.Ready);
        machine.RecordPrePlanError(new(machine.SessionId, "fatal", "No game.", "Close.", true, null)); machine.State.Should().Be(ProtocolSessionState.Completed);
        machine.Dispose(); FluentActions.Invoking(() => machine.RecordPrePlanError(new(machine.SessionId, "again", "Again.", "Close.", false, null))).Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public void SessionAndCatalogReplacement_DisposeOwnedAuthoritiesExactlyOnce()
    {
        ProtocolSessionStateMachine machine = Ready(); FakePackageAuthority package = new(CreateRelease()); Register(machine, package);
        FakeRecoveryAuthority stale = Recovery(Guid.NewGuid(), HashA, Root); Catalog(machine, [stale]);
        FakeRecoveryAuthority current = Recovery(Guid.NewGuid(), HashA, Root); Catalog(machine, [current]);
        stale.DisposeCount.Should().Be(1); current.DisposeCount.Should().Be(0); package.DisposeCount.Should().Be(0);
        machine.Dispose(); machine.Dispose();
        stale.DisposeCount.Should().Be(1); current.DisposeCount.Should().Be(1); package.DisposeCount.Should().Be(1);
    }

    private static ProtocolSessionStateMachine Ready() { ProtocolSessionStateMachine machine = new(); machine.AcceptHandshake(new("gui", "1"), "server"); return machine; }

    private static PackageOpenedEvent Register(ProtocolSessionStateMachine machine, FakePackageAuthority authority)
    {
        InstallationReleaseIdentity release = authority.Release;
        return machine.RegisterPackageAuthority(new(machine.SessionId, release.Tag, release.SourceCommit, "/tmp/package.zip", "/tmp/SHA256SUMS", "/tmp/build.json", "/tmp/install.json"), release, authority, authority);
    }

    private static RecoveryCatalogEvent Catalog(ProtocolSessionStateMachine machine, FakeRecoveryAuthority[] recoveries)
    {
        RecoveryHistory history = new(Sha256Digest.Parse(HashA), recoveries.Select((recovery, index) => new RecoveryGenerationInfo(recovery.GenerationId, recovery.OriginAction, index == 0, recovery.OriginAction == InstallationAction.Backup, recovery.RestoreRelease)));
        return machine.RecordRecoveryCatalogAuthorities(new(machine.SessionId, "/game"), history, recoveries);
    }

    private static InspectedInstallationState Inspection(
        InstallationAction action,
        FakePackageAuthority? package = null,
        FakeRecoveryAuthority? recovery = null,
        ModifiedFileReplacementCandidate[]? candidates = null,
        ModifiedFileReplacementApproval[]? approvals = null,
        PlanConflict[]? conflicts = null,
        object? repairAuthority = null
    )
    {
        PlannedOperation operation = action switch
        {
            InstallationAction.Uninstall => new(PlanOperationKind.Remove, NormalizedRelativePath.Parse("StardewModdingAPI.dll"), Sha256Digest.Parse(HashA), null),
            InstallationAction.Backup => new(PlanOperationKind.Retain, NormalizedRelativePath.Parse("StardewModdingAPI.dll"), Sha256Digest.Parse(HashA), Sha256Digest.Parse(HashA)),
            _ => new(PlanOperationKind.Create, NormalizedRelativePath.Parse("StardewModdingAPI.dll"), null, Sha256Digest.Parse(HashB))
        };
        InstallationPlan plan = new(action, [operation], conflicts ?? [], ObservedInstallationState.KnownModified, new RecoveryCapacityState(0, 64));
        Sha256Digest planSha = Sha256Digest.Hash(System.Text.Encoding.UTF8.GetBytes(plan.ToCanonicalJson()));
        BoundInstallationPlan binding = new(action, Root, 7, planSha, package?.ManifestSha256, null, null, recovery?.SnapshotSha256, null, recovery?.GenerationId, recovery?.AuthorizedHeadPointerSha256, package, recovery);
        return new(plan, binding, package, recovery, repairAuthority ?? new object(), action == InstallationAction.Install ? null : CreateRelease(), action is InstallationAction.Uninstall or InstallationAction.Backup ? null : CreateRelease(), ObservedInstallationState.KnownModified, new RecoveryCapacityState(0, 64), candidates, approvals);
    }

    private static RecoveryPrunePlan Prune(Guid[] catalog, int retain, Guid[] retained, Guid[] removed, Guid[]? cleanup = null) => new(Root, 7, Sha256Digest.Parse(HashA), retain, catalog, retained, removed, cleanup ?? removed, [], null);
    private static FakeRecoveryAuthority Recovery(Guid id, string head, GameRootIdentity root) => new(id, InstallationAction.Backup, root, Sha256Digest.Parse(head), CreateRelease());
    private static InstallationReleaseIdentity CreateRelease() => new("https://github.com/4eh5xitv6787h645ebv/SMAPI", "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2", "4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2", "SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2-linux-x64-installer.zip", "1111111111111111111111111111111111111111", "2222222222222222222222222222222222222222", Sha256Digest.Parse(HashA), 123, "4eh5xitv6787h645ebv/SMAPI/.github/workflows/linux-alpha-release.yml@refs/tags/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2", "Release", "linux-x64");

    private sealed class FakePackageAuthority : IVerifiedPackageContentAuthority, IDisposable
    {
        public InstallationReleaseIdentity Release { get; }
        public PackageManifest Manifest { get; }
        public Sha256Digest ManifestSha256 { get; }
        public object AuthorityIdentity => this;
        public int DisposeCount { get; private set; }
        public FakePackageAuthority(InstallationReleaseIdentity release) { this.Release = release; this.Manifest = new(release, [new PackageManifestEntry(NormalizedRelativePath.Parse("StardewValley"), Sha256Digest.Parse(HashA), 10, 493, OwnedEntryKind.Launcher), new PackageManifestEntry(NormalizedRelativePath.Parse("StardewModdingAPI.dll"), Sha256Digest.Parse(HashB), 10, 420, OwnedEntryKind.RuntimeFile), new PackageManifestEntry(NormalizedRelativePath.Parse("smapi-internal/a.dll"), Sha256Digest.Parse(HashA), 20, 420, OwnedEntryKind.InternalFile)]); this.ManifestSha256 = this.Manifest.GetCanonicalDigest(); }
        public LinuxAnchoredFile OpenFile(PackageManifestEntry expected, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void AssertUsable() { }
        public void Dispose() => this.DisposeCount++;
    }

    private sealed class FakeRecoveryAuthority : ICommittedRecoveryContentAuthority, IDisposable
    {
        public Guid GenerationId { get; }
        public InstallationAction OriginAction { get; }
        public GameRootIdentity GameRoot { get; }
        public RollbackSnapshot Snapshot { get; } = new(null, Sha256Digest.Parse(HashA), []);
        public Sha256Digest SnapshotSha256 { get; } = Sha256Digest.Parse(HashB);
        public Sha256Digest? PreviousManifestSha256 => null;
        public Sha256Digest? PreviousReceiptSha256 => Sha256Digest.Parse(HashA);
        public Sha256Digest AuthorizedHeadPointerSha256 { get; }
        public InstallationReleaseIdentity? RestoreRelease { get; }
        public int DisposeCount { get; private set; }
        public FakeRecoveryAuthority(Guid id, InstallationAction action, GameRootIdentity root, Sha256Digest head, InstallationReleaseIdentity? release) { this.GenerationId = id; this.OriginAction = action; this.GameRoot = root; this.AuthorizedHeadPointerSha256 = head; this.RestoreRelease = release; }
        public LinuxAnchoredFile OpenGameFile(NormalizedRelativePath path, RecoveryFileIdentity expectedIdentity) => throw new NotSupportedException();
        public LinuxAnchoredFile OpenPreviousReceipt(Sha256Digest expectedSha256) => throw new NotSupportedException();
        public LinuxAnchoredFile OpenPreviousManifest(Sha256Digest expectedSha256) => throw new NotSupportedException();
        public void AssertUsable() { }
        public void Dispose() => this.DisposeCount++;
    }
}
