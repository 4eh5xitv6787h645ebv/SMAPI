using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Core.Recovery;
using StardewModdingAPI.Installer.Core.Security;

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
        ProtocolGameCandidate[] candidates = [new("/game", ProtocolGameCandidateState.Valid, "Game")];
        GameDiscoveryEvent discovery = machine.RecordDiscovery(new(machine.SessionId), candidates); candidates[0] = new("/other", ProtocolGameCandidateState.Invalid, "Other");
        discovery.Candidates.Should().ContainSingle().Which.CanonicalPath.Should().Be("/game");
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
    public void CandidateSelection_ResolvesCoreObjectsAndMakesOldIdsStale()
    {
        ProtocolSessionStateMachine machine = Ready(); FakePackageAuthority packageAuthority = new(CreateRelease()); PackageOpenedEvent package = Register(machine, packageAuthority);
        object candidateAuthority = new(); ModifiedFileReplacementCandidate candidate = new(candidateAuthority, NormalizedRelativePath.Parse("StardewModdingAPI.dll"), new RecoveryFileIdentity(Sha256Digest.Parse(HashA), 10, 420, RecoveryFileType.RegularFile));
        InspectedInstallationState blocked = Inspection(InstallationAction.Repair, packageAuthority, candidates: [candidate], conflicts: [new(PlanConflictCode.ModifiedOwnedFile, candidate.Path)], repairAuthority: candidateAuthority);
        PlanEvent old = machine.IssuePlan(new(machine.SessionId, "/game", InstallerOperation.Repair, package.PackageId, null), blocked);
        SelectPlanCandidatesRequest selection = new(machine.SessionId, old.PlanId, old.PlanDigest, [old.Candidates.Single().CandidateId]);
        machine.ResolveCandidateSelection(selection).Should().ContainSingle().Which.Should().BeSameAs(candidate);
        ModifiedFileReplacementApproval approval = new(candidate.Path, candidate.ObservedIdentity);
        InspectedInstallationState replacement = Inspection(InstallationAction.Repair, packageAuthority, approvals: [approval], repairAuthority: candidateAuthority);
        PlanEvent current = machine.IssueCandidatePlan(selection, replacement);
        current.PlanId.Should().NotBe(old.PlanId); current.PlanDigest.Should().NotBe(old.PlanDigest);
        FluentActions.Invoking(() => machine.ResolveCandidateSelection(selection)).Should().Throw<ProtocolException>().WithMessage("*stale*");
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
    public void RelistingCatalog_InvalidatesOldCatalogAndSelectionIds()
    {
        ProtocolSessionStateMachine machine = Ready(); RecoveryCatalogEvent stale = Catalog(machine, [Recovery(Guid.NewGuid(), HashA, Root)]); RecoveryCatalogEvent current = Catalog(machine, [Recovery(Guid.NewGuid(), HashA, Root)]);
        current.CatalogId.Should().NotBe(stale.CatalogId);
        FluentActions.Invoking(() => machine.IssuePlan(new(machine.SessionId, "/game", InstallerOperation.Rollback, null, stale.Generations[0].SelectionId), Inspection(InstallationAction.Rollback, recovery: Recovery(Guid.NewGuid(), HashA, Root)))).Should().Throw<ProtocolException>().WithMessage("*unknown, stale*");
    }

    [Test]
    public void PlanProgressCancellationAndTerminals_RequireExactOrderingAndBinding()
    {
        ProtocolSessionStateMachine machine = Ready(); PlanEvent plan = machine.IssuePlan(new(machine.SessionId, "/game", InstallerOperation.Uninstall, null, null), Inspection(InstallationAction.Uninstall));
        FluentActions.Invoking(() => machine.RecordProgress(new(machine.SessionId, plan.PlanId, plan.PlanDigest, 0, InstallerProgressStage.BackingUp, 0, null, "Wait."))).Should().Throw<ProtocolException>().WithMessage("*Progress can't be recorded*");
        FluentActions.Invoking(() => machine.BeginExecution(new(machine.SessionId, plan.PlanId, plan.PlanDigest))).Should().Throw<ProtocolException>().WithMessage("*confirmed*");
        machine.ConfirmPlan(new(machine.SessionId, plan.PlanId, plan.PlanDigest)); machine.BeginExecution(new(machine.SessionId, plan.PlanId, plan.PlanDigest));
        machine.RecordProgress(new(machine.SessionId, plan.PlanId, plan.PlanDigest, 0, InstallerProgressStage.BackingUp, 0, null, "Wait."));
        FluentActions.Invoking(() => machine.RecordProgress(new(machine.SessionId, plan.PlanId, plan.PlanDigest, 0, InstallerProgressStage.Finalizing, 1, 1, "Again."))).Should().Throw<ProtocolException>().WithMessage("*increase monotonically*");
        machine.RequestCancellation(new(machine.SessionId, plan.PlanId, plan.PlanDigest));
        FluentActions.Invoking(() => machine.Complete(new SuccessEvent(machine.SessionId, plan.PlanId, plan.PlanDigest, InstallerOperation.Uninstall, "Done.", 1, ProtocolRecoveryResult.NotNeeded, "Close.", null))).Should().Throw<ProtocolException>().WithMessage("*terminal event can't be recorded*");
        machine.Complete(new CancelledEvent(machine.SessionId, plan.PlanId, plan.PlanDigest, "Cancelled.", "Safe.", 0, ProtocolRecoveryResult.Succeeded, "Close.", null)); machine.State.Should().Be(ProtocolSessionState.Completed);
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
    public void PruneProgressCancellationAndTerminalStates_AreStrict()
    {
        ProtocolSessionStateMachine machine = Ready(); FakeRecoveryAuthority first = Recovery(Guid.ParseExact("11111111111111111111111111111111", "N"), HashA, Root); FakeRecoveryAuthority second = Recovery(Guid.ParseExact("22222222222222222222222222222222", "N"), HashA, Root); RecoveryCatalogEvent catalog = Catalog(machine, [first, second]);
        PrunePlanEvent plan = machine.IssuePrunePlan(new(machine.SessionId, catalog.CatalogId, 1), Prune([first.GenerationId, second.GenerationId], 1, [first.GenerationId], [second.GenerationId]));
        FluentActions.Invoking(() => machine.RecordPruneProgress(new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest, 0, ProtocolPruneProgressStage.Revalidating, 0, null, "Wait."))).Should().Throw<ProtocolException>().WithMessage("*Prune progress can't be recorded*");
        machine.ConfirmPrune(new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest)); machine.BeginPrune(new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest));
        machine.RecordPruneProgress(new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest, 0, ProtocolPruneProgressStage.Revalidating, 0, null, "Wait."));
        machine.RequestPruneCancellation(new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest));
        machine.RecordPruneProgress(new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest, 1, ProtocolPruneProgressStage.Finalizing, 1, 1, "Stopping."));
        machine.Complete(new PruneCancelledEvent(machine.SessionId, plan.PrunePlanId, plan.PruneDigest, "Cancelled.", "Safe.", 0, ProtocolRecoveryResult.Succeeded, "Inspect.", null)); machine.State.Should().Be(ProtocolSessionState.Completed);
    }

    [Test]
    public void PrePlanErrorAndDisposal_EnforceTerminalLifecycle()
    {
        ProtocolSessionStateMachine machine = Ready(); machine.RecordPrePlanError(new(machine.SessionId, "bad", "Bad package.", "Retry.", false, null)); machine.State.Should().Be(ProtocolSessionState.Ready);
        machine.RecordPrePlanError(new(machine.SessionId, "fatal", "No game.", "Close.", true, null)); machine.State.Should().Be(ProtocolSessionState.Completed);
        machine.Dispose(); FluentActions.Invoking(() => machine.RecordPrePlanError(new(machine.SessionId, "again", "Again.", "Close.", false, null))).Should().Throw<ObjectDisposedException>();
    }

    private static ProtocolSessionStateMachine Ready() { ProtocolSessionStateMachine machine = new(); machine.AcceptHandshake(new("gui", "1"), "server"); return machine; }

    private static PackageOpenedEvent Register(ProtocolSessionStateMachine machine, FakePackageAuthority authority)
    {
        InstallationReleaseIdentity release = authority.Release;
        return machine.RegisterPackageAuthority(new(machine.SessionId, release.Tag, release.SourceCommit, "/tmp/package.zip", "/tmp/SHA256SUMS", "/tmp/build.json", "/tmp/install.json"), release, authority);
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

    private static RecoveryPrunePlan Prune(Guid[] catalog, int retain, Guid[] retained, Guid[] removed) => new(Root, 7, Sha256Digest.Parse(HashA), retain, catalog, retained, removed, removed, [], null);
    private static FakeRecoveryAuthority Recovery(Guid id, string head, GameRootIdentity root) => new(id, InstallationAction.Backup, root, Sha256Digest.Parse(head), CreateRelease());
    private static InstallationReleaseIdentity CreateRelease() => new("https://github.com/4eh5xitv6787h645ebv/SMAPI", "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2", "4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2", "SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2-linux-x64-installer.zip", "1111111111111111111111111111111111111111", "2222222222222222222222222222222222222222", Sha256Digest.Parse(HashA), 123, "4eh5xitv6787h645ebv/SMAPI/.github/workflows/linux-alpha-release.yml@refs/tags/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2", "Release", "linux-x64");

    private sealed class FakePackageAuthority : IVerifiedPackageContentAuthority
    {
        public InstallationReleaseIdentity Release { get; }
        public PackageManifest Manifest { get; }
        public Sha256Digest ManifestSha256 { get; }
        public object AuthorityIdentity => this;
        public FakePackageAuthority(InstallationReleaseIdentity release) { this.Release = release; this.Manifest = new(release, [new PackageManifestEntry(NormalizedRelativePath.Parse("StardewValley"), Sha256Digest.Parse(HashA), 10, 493, OwnedEntryKind.Launcher), new PackageManifestEntry(NormalizedRelativePath.Parse("StardewModdingAPI.dll"), Sha256Digest.Parse(HashB), 10, 420, OwnedEntryKind.RuntimeFile)]); this.ManifestSha256 = this.Manifest.GetCanonicalDigest(); }
        public LinuxAnchoredFile OpenFile(PackageManifestEntry expected, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void AssertUsable() { }
    }

    private sealed class FakeRecoveryAuthority : ICommittedRecoveryContentAuthority
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
        public FakeRecoveryAuthority(Guid id, InstallationAction action, GameRootIdentity root, Sha256Digest head, InstallationReleaseIdentity? release) { this.GenerationId = id; this.OriginAction = action; this.GameRoot = root; this.AuthorizedHeadPointerSha256 = head; this.RestoreRelease = release; }
        public LinuxAnchoredFile OpenGameFile(NormalizedRelativePath path, RecoveryFileIdentity expectedIdentity) => throw new NotSupportedException();
        public LinuxAnchoredFile OpenPreviousReceipt(Sha256Digest expectedSha256) => throw new NotSupportedException();
        public LinuxAnchoredFile OpenPreviousManifest(Sha256Digest expectedSha256) => throw new NotSupportedException();
        public void AssertUsable() { }
    }
}
