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
        ValidateGameRequest validationRequest = new(machine.SessionId, "/selected/game");
        GameValidationEvent validation = machine.RecordGameValidation(validationRequest, new("/canonical/game", LinuxGameFolderStatus.Valid, "Game"));
        validation.CommandId.Should().Be(validationRequest.CommandId);
        validation.Candidate.CanonicalPath.Should().Be("/canonical/game");
    }

    [Test]
    public void Requests_RejectCrossSessionAndInvalidTransitions()
    {
        ProtocolSessionStateMachine machine = new();
        FluentActions.Invoking(() => machine.RecordDiscovery(new(ProtocolSessionId.CreateRandom()), [])).Should().Throw<ProtocolException>().WithMessage("*Expected protocol state 'Ready'*");
        FluentActions.Invoking(() => machine.RecordGameValidation(new(machine.SessionId, "/game"), new("/game", LinuxGameFolderStatus.Valid, "Game"))).Should().Throw<ProtocolException>().WithMessage("*Expected protocol state 'Ready'*");
        machine.AcceptHandshake(new("gui", "1"), "server");
        FluentActions.Invoking(() => machine.RecordDiscovery(new(ProtocolSessionId.CreateRandom()), [])).Should().Throw<ProtocolException>().WithMessage("*session ID*");
        FluentActions.Invoking(() => machine.RecordGameValidation(new(ProtocolSessionId.CreateRandom(), "/game"), new("/game", LinuxGameFolderStatus.Valid, "Game"))).Should().Throw<ProtocolException>().WithMessage("*session ID*");
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
    public void IssuePlan_BindsExactRecoveryCapacityAndRejectsDuplicatedModelDrift()
    {
        RecoveryCapacityState oneSlotLeft = new(63, ProtocolJsonSerializer.MaxRecoveryGenerations);
        ProtocolSessionStateMachine availableMachine = Ready();
        PlanEvent available = availableMachine.IssuePlan(
            new(availableMachine.SessionId, "/game", InstallerOperation.Uninstall, null, null),
            Inspection(InstallationAction.Uninstall, planRecoveryCapacity: oneSlotLeft, inspectionRecoveryCapacity: oneSlotLeft)
        );
        available.RecoveryUsedGenerationCount.Should().Be(63);
        available.RecoveryMaximumGenerationCount.Should().Be(ProtocolJsonSerializer.MaxRecoveryGenerations);
        available.RecoveryRemainingGenerationCount.Should().Be(1);
        available.CanCreateRecoveryGeneration.Should().BeTrue();

        RecoveryCapacityState full = new(64, ProtocolJsonSerializer.MaxRecoveryGenerations);
        ProtocolSessionStateMachine fullMachine = Ready();
        PlanEvent blocked = fullMachine.IssuePlan(
            new(fullMachine.SessionId, "/game", InstallerOperation.Uninstall, null, null),
            Inspection(
                InstallationAction.Uninstall,
                conflicts: [new(PlanConflictCode.RecoveryCapacityReached)],
                planRecoveryCapacity: full,
                inspectionRecoveryCapacity: full
            )
        );
        blocked.RecoveryUsedGenerationCount.Should().Be(64);
        blocked.CanCreateRecoveryGeneration.Should().BeFalse();

        ProtocolSessionStateMachine mismatchMachine = Ready();
        FluentActions.Invoking(() => mismatchMachine.IssuePlan(
            new(mismatchMachine.SessionId, "/game", InstallerOperation.Uninstall, null, null),
            Inspection(InstallationAction.Uninstall, planRecoveryCapacity: oneSlotLeft, inspectionRecoveryCapacity: new(62, 64))
        )).Should().Throw<ProtocolException>().WithMessage("*doesn't match the exact plan*");

        ProtocolSessionStateMachine missingConflictMachine = Ready();
        FluentActions.Invoking(() => missingConflictMachine.IssuePlan(
            new(missingConflictMachine.SessionId, "/game", InstallerOperation.Uninstall, null, null),
            Inspection(InstallationAction.Uninstall, planRecoveryCapacity: full, inspectionRecoveryCapacity: full)
        )).Should().Throw<ProtocolException>().WithMessage("*conflict doesn't match*");

        ProtocolSessionStateMachine forgedConflictMachine = Ready();
        FluentActions.Invoking(() => forgedConflictMachine.IssuePlan(
            new(forgedConflictMachine.SessionId, "/game", InstallerOperation.Uninstall, null, null),
            Inspection(
                InstallationAction.Uninstall,
                conflicts: [new(PlanConflictCode.RecoveryCapacityReached)],
                planRecoveryCapacity: oneSlotLeft,
                inspectionRecoveryCapacity: oneSlotLeft
            )
        )).Should().Throw<ProtocolException>().WithMessage("*conflict doesn't match*");
    }

    [Test]
    public void PlanPages_PullEveryMaximumBoundOperationUnderTheWireLimitAndRejectInvalidOffsetsOrStates()
    {
        ProtocolSessionStateMachine machine = Ready();
        PlannedOperation[] operations = Enumerable.Range(0, TransactionPlan.MaximumOperationCount)
            .Select(index => new PlannedOperation(PlanOperationKind.Create, NormalizedRelativePath.Parse($"smapi-internal/generated/{index:D5}.dll"), null, Sha256Digest.Parse(HashA)))
            .ToArray();
        PlanEvent plan = machine.IssuePlan(new(machine.SessionId, "/game", InstallerOperation.Uninstall, null, null), Inspection(InstallationAction.Uninstall, operations: operations));

        plan.OperationCount.Should().Be(TransactionPlan.MaximumOperationCount);
        System.Text.Encoding.UTF8.GetByteCount(ProtocolJsonSerializer.SerializeLine(plan)).Should().BeLessThan(ProtocolJsonSerializer.MaxLineBytes);
        List<ProtocolPlanOperation> pulled = [];
        int offset = 0;
        while (offset < plan.OperationCount)
        {
            GetPlanPageRequest request = new(machine.SessionId, plan.PlanId, plan.PlanDigest, ProtocolPlanPageKind.Operations, offset);
            PlanPageEvent page = machine.GetPlanPage(request);
            page.CommandId.Should().Be(request.CommandId);
            page.Offset.Should().Be(offset);
            System.Text.Encoding.UTF8.GetByteCount(ProtocolJsonSerializer.SerializeLine(page)).Should().BeLessThan(ProtocolJsonSerializer.MaxLineBytes);
            pulled.AddRange(page.Operations);
            offset = page.NextOffset ?? plan.OperationCount;
        }
        pulled.Should().HaveCount(TransactionPlan.MaximumOperationCount);
        pulled.Select(item => item.Path).Should().Equal(operations.Select(item => item.Path.Value));

        FluentActions.Invoking(() => machine.GetPlanPage(new(machine.SessionId, plan.PlanId, plan.PlanDigest, ProtocolPlanPageKind.Operations, -1))).Should().Throw<ProtocolException>();
        FluentActions.Invoking(() => machine.GetPlanPage(new(machine.SessionId, plan.PlanId, plan.PlanDigest, ProtocolPlanPageKind.Operations, plan.OperationCount))).Should().Throw<ProtocolException>().WithMessage("*offset*");
        machine.RequestCancellation(new(machine.SessionId, plan.PlanId, plan.PlanDigest));
        FluentActions.Invoking(() => machine.GetPlanPage(new(machine.SessionId, plan.PlanId, plan.PlanDigest, ProtocolPlanPageKind.Operations, 0))).Should().Throw<ProtocolException>().WithMessage("*Completed*");
    }

    [Test]
    public void PlanPages_RejectOneOversizedItemBeforeItCanBecomeAnOversizedLine()
    {
        ProtocolSessionId session = ProtocolSessionId.CreateRandom(); ProtocolPlanId plan = ProtocolPlanId.CreateRandom();
        PlanPageEvent oversized = new(session, plan, ProtocolPlanDigest.Parse(HashA), ProtocolPlanPageKind.Warnings, 0, 1, null, [], [], [], [new string('x', ProtocolJsonSerializer.MaxLineBytes)]);
        oversized.Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*too long*");
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
    public void PlanRisks_AcceptCanonicalVersionComponentsBeyondInt32()
    {
        InstallationReleaseIdentity current = CreateRelease("2147483648.0.0", 9);
        InstallationReleaseIdentity target = CreateRelease("9999999999999999999999999999999999999999.0.0", 3);
        ProtocolSessionStateMachine machine = Ready();
        FakePackageAuthority packageAuthority = new(target);
        PackageOpenedEvent package = Register(machine, packageAuthority);

        PlanEvent plan = machine.IssuePlan(
            new(machine.SessionId, "/game", InstallerOperation.Update, package.PackageId, null),
            Inspection(
                InstallationAction.Update,
                packageAuthority,
                currentRelease: current,
                expectedResultRelease: target
            )
        );

        plan.CurrentRelease!.Tag.Should().Be(current.Tag);
        plan.TargetRelease!.Tag.Should().Be(target.Tag);
        plan.Risks.Should().BeEmpty("the unbounded canonical target version is newer");
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
    public void RecoveryCatalogAcceptsDistinctValueEqualRestoreReleaseInstances()
    {
        ProtocolSessionStateMachine machine = Ready(); FakeRecoveryAuthority recovery = Recovery(Guid.NewGuid(), HashA, Root);
        InstallationReleaseIdentity reopenedRelease = CreateRelease();
        reopenedRelease.Should().Be(recovery.RestoreRelease).And.NotBeSameAs(recovery.RestoreRelease);
        RecoveryHistory history = new(Sha256Digest.Parse(HashA), [new(recovery.GenerationId, recovery.OriginAction, true, true, reopenedRelease)]);

        RecoveryCatalogEvent catalog = machine.RecordRecoveryCatalogAuthorities(new(machine.SessionId, "/game"), history, [recovery]);

        ProtocolReleaseIdentity? projected = catalog.Generations.Should().ContainSingle().Which.RestoreRelease;
        projected.Should().NotBeNull();
        projected!.Tag.Should().Be(reopenedRelease.Tag);
        projected.PackageSha256.Should().Be(reopenedRelease.PackageSha256.Value);
    }

    [Test]
    public void NoRecoveryHistory_IsCorrelatedReadyAndRevokesEarlierAuthoritiesWithoutMintingACatalog()
    {
        ProtocolSessionStateMachine machine = Ready();
        FakeRecoveryAuthority recovery = Recovery(Guid.ParseExact("11111111111111111111111111111111", "N"), HashA, Root);
        RecoveryCatalogEvent staleCatalog = Catalog(machine, [recovery]);
        ProtocolRecoverySelectionId staleSelection = staleCatalog.Generations.Single().SelectionId;
        ListRecoveriesRequest request = new(machine.SessionId, "/game");

        NoRecoveryHistoryEvent result = machine.RecordNoRecoveryHistory(request, Root);

        result.SessionId.Should().Be(machine.SessionId);
        result.CommandId.Should().Be(request.CommandId);
        machine.State.Should().Be(ProtocolSessionState.Ready);
        machine.CurrentPlanId.Should().BeNull();
        machine.CurrentPrunePlanId.Should().BeNull();
        recovery.DisposeCount.Should().Be(1);
        FluentActions.Invoking(() => machine.ResolveInspectionAuthorities(new(machine.SessionId, "/game", InstallerOperation.Rollback, null, staleSelection)))
            .Should().Throw<ProtocolException>().WithMessage("*unknown, stale, or missing*");

        ValidateGameRequest followUp = new(machine.SessionId, "/game");
        machine.RecordGameValidation(followUp, new("/game", LinuxGameFolderStatus.Valid, "Game")).CommandId.Should().Be(followUp.CommandId);
    }

    [Test]
    public void NoRecoveryHistory_RejectsWrongSessionWithoutRevokingLiveAuthorities()
    {
        ProtocolSessionStateMachine machine = Ready();
        FakeRecoveryAuthority recovery = Recovery(Guid.ParseExact("11111111111111111111111111111111", "N"), HashA, Root);
        RecoveryCatalogEvent catalog = Catalog(machine, [recovery]);

        FluentActions.Invoking(() => machine.RecordNoRecoveryHistory(new(ProtocolSessionId.CreateRandom(), "/game"), Root))
            .Should().Throw<ProtocolException>().WithMessage("*session ID*");

        recovery.DisposeCount.Should().Be(0);
        machine.ResolveInspectionAuthorities(new(machine.SessionId, "/game", InstallerOperation.Rollback, null, catalog.Generations.Single().SelectionId)).Recovery.Should().BeSameAs(recovery);
    }

    [Test]
    public void NoRecoveryHistory_RevokesAllCatalogsForTheObservedCanonicalPathOnly()
    {
        ProtocolSessionStateMachine machine = Ready();
        FakeRecoveryAuthority sameRoot = Recovery(Guid.ParseExact("11111111111111111111111111111111", "N"), HashA, Root);
        GameRootIdentity replacedRoot = new("/game", 40, 50, 60);
        FakeRecoveryAuthority replacedSamePath = Recovery(Guid.ParseExact("22222222222222222222222222222222", "N"), HashA, replacedRoot);
        GameRootIdentity otherRoot = new("/other", 4, 5, 6);
        FakeRecoveryAuthority otherPath = Recovery(Guid.ParseExact("33333333333333333333333333333333", "N"), HashA, otherRoot);
        _ = Catalog(machine, [sameRoot]);
        _ = Catalog(machine, [replacedSamePath]);
        RecoveryCatalogEvent otherCatalog = Catalog(machine, [otherPath], "/other");

        machine.RecordNoRecoveryHistory(new(machine.SessionId, "/game"), Root);

        sameRoot.DisposeCount.Should().Be(1);
        replacedSamePath.DisposeCount.Should().Be(1);
        otherPath.DisposeCount.Should().Be(0);
        machine.ResolveInspectionAuthorities(new(machine.SessionId, "/other", InstallerOperation.Rollback, null, otherCatalog.Generations.Single().SelectionId)).Recovery.Should().BeSameAs(otherPath);
    }

    [Test]
    public void NoRecoveryHistory_RejectsAliasPathWithoutRevokingTheAnchoredRootsLiveCatalog()
    {
        ProtocolSessionStateMachine machine = Ready();
        FakeRecoveryAuthority recovery = Recovery(Guid.ParseExact("11111111111111111111111111111111", "N"), HashA, Root);
        RecoveryCatalogEvent catalog = Catalog(machine, [recovery]);

        FluentActions.Invoking(() => machine.RecordNoRecoveryHistory(new(machine.SessionId, "/alias"), Root))
            .Should().Throw<ProtocolException>().WithMessage("*doesn't match the requested path*");

        recovery.DisposeCount.Should().Be(0);
        machine.State.Should().Be(ProtocolSessionState.Ready);
        machine.ResolveInspectionAuthorities(new(machine.SessionId, "/game", InstallerOperation.Rollback, null, catalog.Generations.Single().SelectionId)).Recovery.Should().BeSameAs(recovery);
    }

    [Test]
    public void RecoveryCatalogStillRejectsAValueDifferentRestoreRelease()
    {
        ProtocolSessionStateMachine machine = Ready(); FakeRecoveryAuthority recovery = Recovery(Guid.NewGuid(), HashA, Root);
        InstallationReleaseIdentity differentRelease = CreateReleaseWithPackage(Sha256Digest.Parse(HashB), 124);
        differentRelease.Should().NotBe(recovery.RestoreRelease);
        RecoveryHistory history = new(Sha256Digest.Parse(HashA), [new(recovery.GenerationId, recovery.OriginAction, true, true, differentRelease)]);

        FluentActions.Invoking(() => machine.RecordRecoveryCatalogAuthorities(new(machine.SessionId, "/game"), history, [recovery]))
            .Should().Throw<ProtocolException>().WithMessage("*generation, or release*");
    }

    [Test]
    public void RecoveryCatalogStillRejectsNullAgainstANonNullRestoreRelease()
    {
        ProtocolSessionStateMachine machine = Ready(); FakeRecoveryAuthority recovery = Recovery(Guid.NewGuid(), HashA, Root);
        RecoveryHistory history = new(Sha256Digest.Parse(HashA), [new(recovery.GenerationId, recovery.OriginAction, true, true, null)]);

        FluentActions.Invoking(() => machine.RecordRecoveryCatalogAuthorities(new(machine.SessionId, "/game"), history, [recovery]))
            .Should().Throw<ProtocolException>().WithMessage("*generation, or release*");
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
        FluentActions.Invoking(() => machine.RecordRecoveryProgress(new RecoveryProgressEvent(machine.SessionId, 0, TransactionStage.Recovering, 2, 1, "Invalid.") { CommandId = request.CommandId })).Should().Throw<ProtocolException>().WithMessage("*inconsistent*");
        machine.LastProgressSequence.Should().Be(-1);
        machine.RecordRecoveryProgress(new RecoveryProgressEvent(machine.SessionId, 0, TransactionStage.Recovering, 0, null, "Recovering.") { CommandId = request.CommandId });
        FluentActions.Invoking(() => machine.RecordRecoveryProgress(new RecoveryProgressEvent(machine.SessionId, 0, TransactionStage.Completed, 1, 1, "Duplicate.") { CommandId = request.CommandId })).Should().Throw<ProtocolException>().WithMessage("*increase monotonically*");
        RecoveryCompletedEvent completed = new(machine.SessionId, ProtocolInterruptedRecoveryOutcome.RecoveryCompleted, new(ProtocolDurableState.RecoveryCompleted, null, ProtocolRecoveryDisposition.Completed, ProtocolNextAction.InspectAgain), new(new("/game", 1, 2, 3, 8), 7, 8, true, [new("11111111111111111111111111111111", 2)]), "Recovered.", null) { CommandId = request.CommandId };
        machine.CompleteInterruptedRecovery(request, completed);

        machine.State.Should().Be(ProtocolSessionState.Ready); machine.LastProgressSequence.Should().Be(-1);
        FluentActions.Invoking(() => machine.ResolveRecoveryCatalog(machine.SessionId, catalog.CatalogId)).Should().Throw<ProtocolException>().WithMessage("*unknown or stale*");
        FluentActions.Invoking(() => machine.ConfirmPlan(new(machine.SessionId, stale.PlanId, stale.PlanDigest))).Should().Throw<ProtocolException>();
    }

    [Test]
    public void PendingRecoveryFailureAllowsOnlyExactRecoveryRetryUntilSafeCompletionOrPreStartCancellation()
    {
        ProtocolSessionStateMachine machine = Ready(); RecoverInterruptedRequest request = new(machine.SessionId, "/game");
        machine.BeginInterruptedRecovery(request);
        machine.FailInterruptedRecovery(new RecoveryFailureEvent(machine.SessionId, ProtocolInterruptedRecoveryOutcome.UnexpectedFailure, new(ProtocolDurableState.Unknown, ProtocolTerminalErrorCode.UnexpectedCoreFailure, ProtocolRecoveryDisposition.InterruptedRecoveryRequired, ProtocolNextAction.RecoverInterrupted), "Recovery failed.", null) { CommandId = request.CommandId });
        machine.State.Should().Be(ProtocolSessionState.RecoveryRequired);
        FluentActions.Invoking(() => machine.ValidateReadyRequest(machine.SessionId)).Should().Throw<ProtocolException>();
        FluentActions.Invoking(() => machine.RecordPrePlanRejection(new(machine.SessionId, ProtocolPrePlanErrorCode.PackageRejected, "Unsafe.", ProtocolNextAction.ReopenVerifiedPackage, false, null))).Should().Throw<ProtocolException>();

        machine.BeginInterruptedRecovery(request);
        RecoveryFailureEvent cancelled = new(machine.SessionId, ProtocolInterruptedRecoveryOutcome.CancelledBeforeRecovery, new(ProtocolDurableState.Unchanged, null, ProtocolRecoveryDisposition.InterruptedRecoveryRequired, ProtocolNextAction.RecoverInterrupted), "Cancelled before recovery.", null) { CommandId = request.CommandId };
        cancelled.RequiresRecovery.Should().BeTrue();
        machine.FailInterruptedRecovery(cancelled);
        machine.State.Should().Be(ProtocolSessionState.RecoveryRequired);
    }

    [Test]
    public void PlanProgressCancellationAndTerminals_RequireExactOrderingAndBinding()
    {
        ProtocolSessionStateMachine machine = Ready(); PlanEvent plan = machine.IssuePlan(new(machine.SessionId, "/game", InstallerOperation.Uninstall, null, null), Inspection(InstallationAction.Uninstall));
        FluentActions.Invoking(() => machine.RecordProgress(new(machine.SessionId, plan.PlanId, plan.PlanDigest, 0, TransactionStage.PreparingRecovery, 0, null, "Wait."))).Should().Throw<ProtocolException>().WithMessage("*Progress can't be recorded*");
        FluentActions.Invoking(() => machine.BeginExecution(new(machine.SessionId, plan.PlanId, plan.PlanDigest))).Should().Throw<ProtocolException>().WithMessage("*confirmed*");
        machine.ConfirmPlan(new(machine.SessionId, plan.PlanId, plan.PlanDigest)); ExecutePlanRequest execute = new(machine.SessionId, plan.PlanId, plan.PlanDigest); InspectedInstallationState retained = machine.BeginExecution(execute);
        retained.ConfirmationDigest.Value.Should().Be(plan.ExecutionBindingDigest.Value);
        FluentActions.Invoking(() => machine.RecordProgress(new ProgressEvent(machine.SessionId, plan.PlanId, plan.PlanDigest, 0, TransactionStage.PreparingRecovery, 2, 1, "Invalid.") { CommandId = execute.CommandId })).Should().Throw<ProtocolException>().WithMessage("*inconsistent*");
        machine.LastProgressSequence.Should().Be(-1);
        machine.RecordProgress(new ProgressEvent(machine.SessionId, plan.PlanId, plan.PlanDigest, 0, TransactionStage.PreparingRecovery, 0, null, "Wait.") { CommandId = execute.CommandId });
        FluentActions.Invoking(() => machine.RecordProgress(new ProgressEvent(machine.SessionId, plan.PlanId, plan.PlanDigest, 0, TransactionStage.Completed, 1, 1, "Again.") { CommandId = execute.CommandId })).Should().Throw<ProtocolException>().WithMessage("*increase monotonically*");
        machine.RequestCancellation(new(machine.SessionId, plan.PlanId, plan.PlanDigest));
        machine.Complete(Success(machine, plan.PlanId, plan.PlanDigest, execute.CommandId));
        machine.State.Should().Be(ProtocolSessionState.Completed, "a late cancellation request can't erase a truthful durable commit");
    }

    [Test]
    public void ExecutionTerminalCannotClaimMoreManagedChangesThanTheConfirmedPlan()
    {
        ProtocolSessionStateMachine machine = Ready(); PlanEvent plan = machine.IssuePlan(new(machine.SessionId, "/game", InstallerOperation.Uninstall, null, null), Inspection(InstallationAction.Uninstall));
        machine.ConfirmPlan(new(machine.SessionId, plan.PlanId, plan.PlanDigest)); ExecutePlanRequest execute = new(machine.SessionId, plan.PlanId, plan.PlanDigest); machine.BeginExecution(execute);
        SuccessEvent overreported = new(machine.SessionId, plan.PlanId, plan.PlanDigest, InstallerOperation.Uninstall, ProtocolExecutionOutcome.Succeeded, new(ProtocolDurableState.Committed, null, ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.InspectAgain), new(plan.OperationCount + 1, 0, 0, 0, 0, 0), "Done.", null) { CommandId = execute.CommandId };
        FluentActions.Invoking(() => machine.Complete(overreported)).Should().Throw<ProtocolException>().WithMessage("*exceeds the confirmed plan operation count*");
    }

    [Test]
    public void CancellationTerminalsCannotInventWorkWhenExecutionStartAuthorityIsAbsent()
    {
        ProtocolSessionStateMachine machine = Ready(); PlanEvent plan = machine.IssuePlan(new(machine.SessionId, "/game", InstallerOperation.Uninstall, null, null), Inspection(InstallationAction.Uninstall));
        machine.ConfirmPlan(new(machine.SessionId, plan.PlanId, plan.PlanDigest)); ExecutePlanRequest execute = new(machine.SessionId, plan.PlanId, plan.PlanDigest); machine.BeginExecution(execute); machine.RequestCancellation(new(machine.SessionId, plan.PlanId, plan.PlanDigest));
        typeof(ProtocolSessionStateMachine).GetField("ExecutionStartedForCurrentPlan", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(machine, false);
        CancelledEvent forged = new(machine.SessionId, plan.PlanId, plan.PlanDigest, ProtocolExecutionOutcome.CancelledAndRolledBack, new(ProtocolDurableState.RolledBack, null, ProtocolRecoveryDisposition.Completed, ProtocolNextAction.InspectAgain), new(1, 1, 0, 0, 0, 0), "Rolled back.", null) { CommandId = execute.CommandId };
        FluentActions.Invoking(() => machine.Complete(forged)).Should().Throw<ProtocolException>().WithMessage("*pre-execution cancellation*");

        ProtocolSessionStateMachine pruneMachine = Ready(); FakeRecoveryAuthority first = Recovery(Guid.ParseExact("11111111111111111111111111111111", "N"), HashA, Root); FakeRecoveryAuthority second = Recovery(Guid.ParseExact("22222222222222222222222222222222", "N"), HashA, Root); RecoveryCatalogEvent catalog = Catalog(pruneMachine, [first, second]);
        PrunePlanEvent prune = pruneMachine.IssuePrunePlan(new(pruneMachine.SessionId, catalog.CatalogId, 1), Prune([first.GenerationId, second.GenerationId], 1, [first.GenerationId], [second.GenerationId]));
        pruneMachine.ConfirmPrune(new(pruneMachine.SessionId, prune.PrunePlanId, prune.PruneDigest)); ExecutePruneRequest pruneExecute = new(pruneMachine.SessionId, prune.PrunePlanId, prune.PruneDigest); pruneMachine.BeginPrune(pruneExecute); pruneMachine.RequestPruneCancellation(new(pruneMachine.SessionId, prune.PrunePlanId, prune.PruneDigest));
        typeof(ProtocolSessionStateMachine).GetField("PruneStarted", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(pruneMachine, false);
        PruneCancelledEvent forgedPrune = new(pruneMachine.SessionId, prune.PrunePlanId, prune.PruneDigest, ProtocolPruneOutcome.CancelledWithCleanupPending, new(ProtocolDurableState.Unchanged, null, ProtocolRecoveryDisposition.CleanupPending, ProtocolNextAction.ListRecoveries), new(0, 0, 1, false), "Cleanup pending.", null) { CommandId = pruneExecute.CommandId };
        FluentActions.Invoking(() => pruneMachine.Complete(forgedPrune)).Should().Throw<ProtocolException>().WithMessage("*pre-execution prune cancellation*");
    }

    [Test]
    public void InnerExecutionBindingDigest_IsRejectedAcrossRequestsProgressAndEveryPlanTerminal()
    {
        ProtocolSessionStateMachine machine = Ready(); PlanEvent plan = machine.IssuePlan(new(machine.SessionId, "/game", InstallerOperation.Uninstall, null, null), Inspection(InstallationAction.Uninstall)); ProtocolPlanDigest wrong = plan.ExecutionBindingDigest;
        wrong.Should().NotBe(plan.PlanDigest);
        FluentActions.Invoking(() => machine.ConfirmPlan(new(machine.SessionId, plan.PlanId, wrong))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
        FluentActions.Invoking(() => machine.RequestCancellation(new(machine.SessionId, plan.PlanId, wrong))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
        machine.ConfirmPlan(new(machine.SessionId, plan.PlanId, plan.PlanDigest));
        FluentActions.Invoking(() => machine.BeginExecution(new(machine.SessionId, plan.PlanId, wrong))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
        machine.BeginExecution(new(machine.SessionId, plan.PlanId, plan.PlanDigest));
        FluentActions.Invoking(() => machine.RecordProgress(new(machine.SessionId, plan.PlanId, wrong, 0, TransactionStage.PreparingRecovery, 0, null, "Wrong."))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
        FluentActions.Invoking(() => machine.Complete(Success(machine, plan.PlanId, wrong, ProtocolCommandId.CreateRandom()))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
        FluentActions.Invoking(() => machine.Complete(RolledBack(machine, plan.PlanId, wrong))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
        FluentActions.Invoking(() => machine.Complete(Interrupted(machine, plan.PlanId, wrong))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
        machine.RequestCancellation(new(machine.SessionId, plan.PlanId, plan.PlanDigest));
        FluentActions.Invoking(() => machine.Complete(Cancelled(machine, plan.PlanId, wrong))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
    }

    [Test]
    public void PreExecutionCancellation_CannotMasqueradeAsFailureOrMutation()
    {
        ProtocolSessionStateMachine machine = Ready(); PlanEvent plan = machine.IssuePlan(new(machine.SessionId, "/game", InstallerOperation.Uninstall, null, null), Inspection(InstallationAction.Uninstall));
        CommandAcknowledgedEvent acknowledgement = machine.RequestCancellation(new(machine.SessionId, plan.PlanId, plan.PlanDigest));
        acknowledgement.Acknowledgement.Should().Be(ProtocolAcknowledgementKind.PlanCancelledBeforeExecution);
        machine.State.Should().Be(ProtocolSessionState.Completed);
        FluentActions.Invoking(() => machine.Complete(RolledBack(machine, plan.PlanId, plan.PlanDigest))).Should().Throw<ProtocolException>().WithMessage("*terminal event can't be recorded*");
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
    public void NoPruneWork_RequiresExactCatalogBindingAndReturnsToReusableReadyState()
    {
        static (ProtocolSessionStateMachine Machine, RecoveryCatalogEvent Catalog, FakeRecoveryAuthority First, FakeRecoveryAuthority Second) Context()
        {
            ProtocolSessionStateMachine machine = Ready();
            FakeRecoveryAuthority first = Recovery(Guid.ParseExact("11111111111111111111111111111111", "N"), HashA, Root);
            FakeRecoveryAuthority second = Recovery(Guid.ParseExact("22222222222222222222222222222222", "N"), HashA, Root);
            return (machine, Catalog(machine, [first, second]), first, second);
        }

        (ProtocolSessionStateMachine machine, RecoveryCatalogEvent catalog, FakeRecoveryAuthority first, FakeRecoveryAuthority second) = Context();
        PlanEvent prior = machine.IssuePlan(new(machine.SessionId, "/game", InstallerOperation.Uninstall, null, null), Inspection(InstallationAction.Uninstall));
        machine.ConfirmPlan(new(machine.SessionId, prior.PlanId, prior.PlanDigest));
        InspectPruneRequest request = new(machine.SessionId, catalog.CatalogId, 2);
        RecoveryPrunePlan exact = Prune([first.GenerationId, second.GenerationId], 2, [first.GenerationId, second.GenerationId], []);
        PrePlanRejectedEvent noWork = machine.RecordNoPruneWork(request, exact);
        noWork.ErrorCode.Should().Be(ProtocolPrePlanErrorCode.NothingToPrune);
        noWork.CommandId.Should().Be(request.CommandId);
        machine.State.Should().Be(ProtocolSessionState.Ready);
        Catalog(machine, [Recovery(first.GenerationId, HashA, Root), Recovery(second.GenerationId, HashA, Root)]).Should().NotBeNull();
        FluentActions.Invoking(() => machine.ConfirmPlan(new(machine.SessionId, prior.PlanId, prior.PlanDigest))).Should().Throw<ProtocolException>();

        (ProtocolSessionStateMachine wrongHeadMachine, RecoveryCatalogEvent wrongHeadCatalog, FakeRecoveryAuthority wrongHeadFirst, FakeRecoveryAuthority wrongHeadSecond) = Context();
        RecoveryPrunePlan wrongHead = new(Root, 7, Sha256Digest.Parse(HashB), 2, [wrongHeadFirst.GenerationId, wrongHeadSecond.GenerationId], [wrongHeadFirst.GenerationId, wrongHeadSecond.GenerationId], [], [], [], null);
        FluentActions.Invoking(() => wrongHeadMachine.RecordNoPruneWork(new(wrongHeadMachine.SessionId, wrongHeadCatalog.CatalogId, 2), wrongHead)).Should().Throw<ProtocolException>().WithMessage("*root, head, and retention*");

        (ProtocolSessionStateMachine wrongRootMachine, RecoveryCatalogEvent wrongRootCatalog, FakeRecoveryAuthority wrongRootFirst, FakeRecoveryAuthority wrongRootSecond) = Context();
        RecoveryPrunePlan wrongRoot = new(new("/other", 4, 5, 6), 7, Sha256Digest.Parse(HashA), 2, [wrongRootFirst.GenerationId, wrongRootSecond.GenerationId], [wrongRootFirst.GenerationId, wrongRootSecond.GenerationId], [], [], [], null);
        FluentActions.Invoking(() => wrongRootMachine.RecordNoPruneWork(new(wrongRootMachine.SessionId, wrongRootCatalog.CatalogId, 2), wrongRoot)).Should().Throw<ProtocolException>().WithMessage("*root, head, and retention*");

        (ProtocolSessionStateMachine wrongRetainMachine, RecoveryCatalogEvent wrongRetainCatalog, FakeRecoveryAuthority wrongRetainFirst, FakeRecoveryAuthority wrongRetainSecond) = Context();
        RecoveryPrunePlan wrongRetain = Prune([wrongRetainFirst.GenerationId, wrongRetainSecond.GenerationId], 2, [wrongRetainFirst.GenerationId, wrongRetainSecond.GenerationId], []);
        FluentActions.Invoking(() => wrongRetainMachine.RecordNoPruneWork(new(wrongRetainMachine.SessionId, wrongRetainCatalog.CatalogId, 1), wrongRetain)).Should().Throw<ProtocolException>().WithMessage("*retention request*");

        (ProtocolSessionStateMachine wrongOrderMachine, RecoveryCatalogEvent wrongOrderCatalog, FakeRecoveryAuthority wrongOrderFirst, FakeRecoveryAuthority wrongOrderSecond) = Context();
        RecoveryPrunePlan wrongOrder = Prune([wrongOrderSecond.GenerationId, wrongOrderFirst.GenerationId], 2, [wrongOrderSecond.GenerationId, wrongOrderFirst.GenerationId], []);
        FluentActions.Invoking(() => wrongOrderMachine.RecordNoPruneWork(new(wrongOrderMachine.SessionId, wrongOrderCatalog.CatalogId, 2), wrongOrder)).Should().Throw<ProtocolException>().WithMessage("*exact stored catalog order*");
    }

    [TestCase("plan-issued")]
    [TestCase("plan-confirmed")]
    [TestCase("prune-issued")]
    [TestCase("prune-confirmed")]
    public void NoPruneWork_ReplacesEveryReviewablePlanStateAndRevokesPriorAuthority(string priorState)
    {
        ProtocolSessionStateMachine machine = Ready();
        FakeRecoveryAuthority first = Recovery(Guid.ParseExact("11111111111111111111111111111111", "N"), HashA, Root);
        FakeRecoveryAuthority second = Recovery(Guid.ParseExact("22222222222222222222222222222222", "N"), HashA, Root);
        RecoveryCatalogEvent catalog = Catalog(machine, [first, second]);
        PlanEvent? ordinary = null;
        PrunePlanEvent? prune = null;
        if (priorState.StartsWith("plan", StringComparison.Ordinal))
        {
            ordinary = machine.IssuePlan(new(machine.SessionId, "/game", InstallerOperation.Uninstall, null, null), Inspection(InstallationAction.Uninstall));
            if (priorState == "plan-confirmed")
                machine.ConfirmPlan(new(machine.SessionId, ordinary.PlanId, ordinary.PlanDigest));
        }
        else
        {
            prune = machine.IssuePrunePlan(new(machine.SessionId, catalog.CatalogId, 1), Prune([first.GenerationId, second.GenerationId], 1, [first.GenerationId], [second.GenerationId]));
            if (priorState == "prune-confirmed")
                machine.ConfirmPrune(new(machine.SessionId, prune.PrunePlanId, prune.PruneDigest));
        }

        RecoveryPrunePlan noWork = Prune([first.GenerationId, second.GenerationId], 2, [first.GenerationId, second.GenerationId], []);
        machine.RecordNoPruneWork(new(machine.SessionId, catalog.CatalogId, 2), noWork);

        machine.State.Should().Be(ProtocolSessionState.Ready);
        Catalog(machine, [Recovery(first.GenerationId, HashA, Root), Recovery(second.GenerationId, HashA, Root)]).Should().NotBeNull();
        if (ordinary is not null)
            FluentActions.Invoking(() => machine.ConfirmPlan(new(machine.SessionId, ordinary.PlanId, ordinary.PlanDigest))).Should().Throw<ProtocolException>();
        if (prune is not null)
            FluentActions.Invoking(() => machine.ConfirmPrune(new(machine.SessionId, prune.PrunePlanId, prune.PruneDigest))).Should().Throw<ProtocolException>();
    }

    [Test]
    public void PrunePlan_CleanupOnlyIsExecutableAndReportsPhysicalWorkHonestly()
    {
        ProtocolSessionStateMachine machine = Ready(); FakeRecoveryAuthority first = Recovery(Guid.ParseExact("11111111111111111111111111111111", "N"), HashA, Root); FakeRecoveryAuthority second = Recovery(Guid.ParseExact("22222222222222222222222222222222", "N"), HashA, Root); RecoveryCatalogEvent catalog = Catalog(machine, [first, second]);
        Guid pending = Guid.ParseExact("33333333333333333333333333333333", "N");
        RecoveryPrunePlan core = Prune([first.GenerationId, second.GenerationId], 2, [first.GenerationId, second.GenerationId], [], [pending]);
        PrunePlanEvent plan = machine.IssuePrunePlan(new(machine.SessionId, catalog.CatalogId, 2), core);
        plan.RemovedSelectionIds.Should().BeEmpty(); plan.CleanupGenerationIds.Should().Equal(pending.ToString("N")); plan.Summary.Should().Contain("Logically remove 0").And.Contain("clean up 1");
        machine.ConfirmPrune(new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest)); ExecutePruneRequest execute = new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest); RecoveryPrunePlan retained = machine.BeginPrune(execute);
        retained.ConfirmationDigest.Value.Should().Be(plan.ExecutionBindingDigest.Value);
        FluentActions.Invoking(() => machine.Complete(PruneSuccess(machine, plan.PrunePlanId, plan.PruneDigest, execute.CommandId, 0, 0))).Should().Throw<ProtocolException>().WithMessage("*physical-cleanup count*");
        machine.Complete(PruneSuccess(machine, plan.PrunePlanId, plan.PruneDigest, execute.CommandId, 0, 1));
    }

    [Test]
    public void AfterApplyPruneTerminalsRequireEveryConfirmedLogicalAndPhysicalCount()
    {
        static (ProtocolSessionStateMachine Machine, PrunePlanEvent Plan, ExecutePruneRequest Execute) Started()
        {
            ProtocolSessionStateMachine machine = Ready();
            FakeRecoveryAuthority first = Recovery(Guid.ParseExact("11111111111111111111111111111111", "N"), HashA, Root);
            FakeRecoveryAuthority second = Recovery(Guid.ParseExact("22222222222222222222222222222222", "N"), HashA, Root);
            RecoveryCatalogEvent catalog = Catalog(machine, [first, second]);
            PrunePlanEvent plan = machine.IssuePrunePlan(new(machine.SessionId, catalog.CatalogId, 1), Prune([first.GenerationId, second.GenerationId], 1, [first.GenerationId], [second.GenerationId]));
            machine.ConfirmPrune(new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest));
            ExecutePruneRequest execute = new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest);
            machine.BeginPrune(execute);
            return (machine, plan, execute);
        }

        (ProtocolSessionStateMachine failedMachine, PrunePlanEvent failedPlan, ExecutePruneRequest failedExecute) = Started();
        ProtocolTerminalState failedState = new(ProtocolDurableState.PruneApplied, ProtocolTerminalErrorCode.IoFailure, ProtocolRecoveryDisposition.StateRefreshRequired, ProtocolNextAction.ListRecoveries);
        PruneFailureEvent MissingFailureCount(int logical, int physical) => new(failedMachine.SessionId, failedPlan.PrunePlanId, failedPlan.PruneDigest, ProtocolPruneOutcome.FailedAfterApply, failedState, new(logical, physical, 0, false), "Failed.", null) { CommandId = failedExecute.CommandId };
        FluentActions.Invoking(() => failedMachine.Complete(MissingFailureCount(0, 1))).Should().Throw<ProtocolException>().WithMessage("*exactly match the confirmed plan*");
        FluentActions.Invoking(() => failedMachine.Complete(MissingFailureCount(1, 0))).Should().Throw<ProtocolException>().WithMessage("*exactly match the confirmed plan*");
        failedMachine.Complete(MissingFailureCount(1, 1));

        (ProtocolSessionStateMachine cancelledMachine, PrunePlanEvent cancelledPlan, ExecutePruneRequest cancelledExecute) = Started();
        cancelledMachine.RequestPruneCancellation(new(cancelledMachine.SessionId, cancelledPlan.PrunePlanId, cancelledPlan.PruneDigest));
        ProtocolTerminalState cancelledState = new(ProtocolDurableState.PruneApplied, null, ProtocolRecoveryDisposition.StateRefreshRequired, ProtocolNextAction.ListRecoveries);
        PruneCancelledEvent MissingCancellationCount(int logical, int physical) => new(cancelledMachine.SessionId, cancelledPlan.PrunePlanId, cancelledPlan.PruneDigest, ProtocolPruneOutcome.CancelledAfterApply, cancelledState, new(logical, physical, 0, false), "Cancelled.", null) { CommandId = cancelledExecute.CommandId };
        FluentActions.Invoking(() => cancelledMachine.Complete(MissingCancellationCount(0, 1))).Should().Throw<ProtocolException>().WithMessage("*exactly match the confirmed plan*");
        FluentActions.Invoking(() => cancelledMachine.Complete(MissingCancellationCount(1, 0))).Should().Throw<ProtocolException>().WithMessage("*exactly match the confirmed plan*");
        cancelledMachine.Complete(MissingCancellationCount(1, 1));
    }

    [Test]
    public void PrunePlan_AuxiliaryOnlyIsExplicitlyDigestBoundAndExecutable()
    {
        ProtocolSessionStateMachine machine = Ready();
        FakeRecoveryAuthority first = Recovery(Guid.ParseExact("11111111111111111111111111111111", "N"), HashA, Root);
        FakeRecoveryAuthority second = Recovery(Guid.ParseExact("22222222222222222222222222222222", "N"), HashA, Root);
        RecoveryCatalogEvent catalog = Catalog(machine, [first, second]);
        RecoveryPrunePlan core = Prune([first.GenerationId, second.GenerationId], 2, [first.GenerationId, second.GenerationId], [], [], hasAuxiliaryCleanup: true);

        PrunePlanEvent plan = machine.IssuePrunePlan(new(machine.SessionId, catalog.CatalogId, 2), core);

        plan.RemovedSelectionIds.Should().BeEmpty();
        plan.CleanupGenerationIds.Should().BeEmpty();
        plan.AuxiliaryCleanupPlanned.Should().BeTrue();
        machine.ConfirmPrune(new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest));
        ExecutePruneRequest execute = new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest);
        machine.BeginPrune(execute).Should().BeSameAs(core);
        machine.Complete(PruneSuccess(machine, plan.PrunePlanId, plan.PruneDigest, execute.CommandId, 0, 0));
        machine.State.Should().Be(ProtocolSessionState.Completed);
    }

    [Test]
    public void PruneProgressCancellationAndTerminalStates_AreStrict()
    {
        ProtocolSessionStateMachine machine = Ready(); FakeRecoveryAuthority first = Recovery(Guid.ParseExact("11111111111111111111111111111111", "N"), HashA, Root); FakeRecoveryAuthority second = Recovery(Guid.ParseExact("22222222222222222222222222222222", "N"), HashA, Root); RecoveryCatalogEvent catalog = Catalog(machine, [first, second]);
        PrunePlanEvent plan = machine.IssuePrunePlan(new(machine.SessionId, catalog.CatalogId, 1), Prune([first.GenerationId, second.GenerationId], 1, [first.GenerationId], [second.GenerationId]));
        FluentActions.Invoking(() => machine.RecordPruneProgress(new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest, 0, TransactionStage.Revalidating, 0, null, "Wait."))).Should().Throw<ProtocolException>().WithMessage("*Prune progress can't be recorded*");
        machine.ConfirmPrune(new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest)); ExecutePruneRequest execute = new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest); machine.BeginPrune(execute);
        FluentActions.Invoking(() => machine.RecordPruneProgress(new PruneProgressEvent(machine.SessionId, plan.PrunePlanId, plan.PruneDigest, 0, TransactionStage.Revalidating, 2, 1, "Invalid.") { CommandId = execute.CommandId })).Should().Throw<ProtocolException>().WithMessage("*inconsistent*");
        machine.LastProgressSequence.Should().Be(-1);
        machine.RecordPruneProgress(new PruneProgressEvent(machine.SessionId, plan.PrunePlanId, plan.PruneDigest, 0, TransactionStage.Revalidating, 0, null, "Wait.") { CommandId = execute.CommandId });
        machine.RequestPruneCancellation(new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest));
        machine.RecordPruneProgress(new PruneProgressEvent(machine.SessionId, plan.PrunePlanId, plan.PruneDigest, 1, TransactionStage.Completed, 1, 1, "Stopping.") { CommandId = execute.CommandId });
        machine.Complete(PruneCancelled(machine, plan.PrunePlanId, plan.PruneDigest, execute.CommandId)); machine.State.Should().Be(ProtocolSessionState.Completed);
    }

    [Test]
    public void InnerPruneExecutionBindingDigest_IsRejectedAcrossRequestsProgressAndEveryTerminal()
    {
        ProtocolSessionStateMachine machine = Ready(); FakeRecoveryAuthority first = Recovery(Guid.ParseExact("11111111111111111111111111111111", "N"), HashA, Root); FakeRecoveryAuthority second = Recovery(Guid.ParseExact("22222222222222222222222222222222", "N"), HashA, Root); RecoveryCatalogEvent catalog = Catalog(machine, [first, second]);
        PrunePlanEvent plan = machine.IssuePrunePlan(new(machine.SessionId, catalog.CatalogId, 1), Prune([first.GenerationId, second.GenerationId], 1, [first.GenerationId], [second.GenerationId])); ProtocolPlanDigest wrong = plan.ExecutionBindingDigest;
        wrong.Should().NotBe(plan.PruneDigest);
        FluentActions.Invoking(() => machine.ConfirmPrune(new(machine.SessionId, plan.PrunePlanId, wrong))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
        FluentActions.Invoking(() => machine.RequestPruneCancellation(new(machine.SessionId, plan.PrunePlanId, wrong))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
        machine.ConfirmPrune(new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest));
        FluentActions.Invoking(() => machine.BeginPrune(new(machine.SessionId, plan.PrunePlanId, wrong))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
        machine.BeginPrune(new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest));
        FluentActions.Invoking(() => machine.RecordPruneProgress(new(machine.SessionId, plan.PrunePlanId, wrong, 0, TransactionStage.Revalidating, 0, null, "Wrong."))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
        FluentActions.Invoking(() => machine.Complete(PruneSuccess(machine, plan.PrunePlanId, wrong, ProtocolCommandId.CreateRandom(), 1, 1))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
        FluentActions.Invoking(() => machine.Complete(PruneFailure(machine, plan.PrunePlanId, wrong))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
        FluentActions.Invoking(() => machine.Complete(PruneInterrupted(machine, plan.PrunePlanId, wrong))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
        machine.RequestPruneCancellation(new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest));
        FluentActions.Invoking(() => machine.Complete(PruneCancelled(machine, plan.PrunePlanId, wrong, ProtocolCommandId.CreateRandom()))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
    }

    [Test]
    public void PrePlanErrorAndDisposal_EnforceTerminalLifecycle()
    {
        ProtocolSessionStateMachine machine = Ready(); machine.RecordPrePlanRejection(new(machine.SessionId, ProtocolPrePlanErrorCode.PackageRejected, "Bad package.", ProtocolNextAction.ReopenVerifiedPackage, false, null)); machine.State.Should().Be(ProtocolSessionState.Ready);
        machine.RecordPrePlanRejection(new(machine.SessionId, ProtocolPrePlanErrorCode.UnexpectedFailure, "No game.", ProtocolNextAction.StartNewSession, true, null)); machine.State.Should().Be(ProtocolSessionState.Completed);
        machine.Dispose(); FluentActions.Invoking(() => machine.RecordPrePlanRejection(new(machine.SessionId, ProtocolPrePlanErrorCode.UnexpectedFailure, "Again.", ProtocolNextAction.StartNewSession, true, null))).Should().Throw<ObjectDisposedException>();
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

    private static SuccessEvent Success(ProtocolSessionStateMachine machine, ProtocolPlanId plan, ProtocolPlanDigest digest, ProtocolCommandId command) =>
        new(machine.SessionId, plan, digest, InstallerOperation.Uninstall, ProtocolExecutionOutcome.Succeeded, new(ProtocolDurableState.Committed, null, ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.InspectAgain), new(1, 0, 0, 0, 0, 0), "Done.", null) { CommandId = command };
    private static RolledBackFailureEvent RolledBack(ProtocolSessionStateMachine machine, ProtocolPlanId plan, ProtocolPlanDigest digest) =>
        new(machine.SessionId, plan, digest, ProtocolExecutionOutcome.FailedAndRolledBack, new(ProtocolDurableState.RolledBack, ProtocolTerminalErrorCode.IoFailure, ProtocolRecoveryDisposition.Completed, ProtocolNextAction.InspectAgain), new(1, 1, 0, 0, 0, 0), "Failed.", "Restored.", null);
    private static RecoverableInterruptionEvent Interrupted(ProtocolSessionStateMachine machine, ProtocolPlanId plan, ProtocolPlanDigest digest) =>
        new(machine.SessionId, plan, digest, ProtocolExecutionOutcome.InterruptedRecoveryRequired, new(ProtocolDurableState.RecoveryRequired, ProtocolTerminalErrorCode.RecoveryFailed, ProtocolRecoveryDisposition.InterruptedRecoveryRequired, ProtocolNextAction.RecoverInterrupted), new(1, 0, 0, 0, 0, 0), "Interrupted.", "Recovery required.", null);
    private static CancelledEvent Cancelled(ProtocolSessionStateMachine machine, ProtocolPlanId plan, ProtocolPlanDigest digest) =>
        new(machine.SessionId, plan, digest, ProtocolExecutionOutcome.CancelledBeforeMutation, new(ProtocolDurableState.Unchanged, null, ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.InspectAgain), new(0, 0, 0, 0, 0, 0), "Cancelled.", null);
    private static PruneSuccessEvent PruneSuccess(ProtocolSessionStateMachine machine, ProtocolPrunePlanId plan, ProtocolPlanDigest digest, ProtocolCommandId command, int logical, int physical) =>
        new(machine.SessionId, plan, digest, ProtocolPruneOutcome.Succeeded, new(ProtocolDurableState.PruneApplied, null, ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.ListRecoveries), new(logical, physical, 0, false), "Done.", null) { CommandId = command };
    private static PruneFailureEvent PruneFailure(ProtocolSessionStateMachine machine, ProtocolPrunePlanId plan, ProtocolPlanDigest digest) =>
        new(machine.SessionId, plan, digest, ProtocolPruneOutcome.FailedBeforePublication, new(ProtocolDurableState.Unchanged, ProtocolTerminalErrorCode.IoFailure, ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.ListRecoveries), new(0, 0, 0, false), "Failed.", null);
    private static PruneInterruptionEvent PruneInterrupted(ProtocolSessionStateMachine machine, ProtocolPrunePlanId plan, ProtocolPlanDigest digest) =>
        new(machine.SessionId, plan, digest, ProtocolPruneOutcome.Interrupted, new(ProtocolDurableState.Unchanged, ProtocolTerminalErrorCode.IoFailure, ProtocolRecoveryDisposition.CleanupPending, ProtocolNextAction.ListRecoveries), new(0, 0, 1, false), "Interrupted.", null);
    private static PruneCancelledEvent PruneCancelled(ProtocolSessionStateMachine machine, ProtocolPrunePlanId plan, ProtocolPlanDigest digest, ProtocolCommandId? command = null) =>
        new(machine.SessionId, plan, digest, ProtocolPruneOutcome.CancelledBeforePublication, new(ProtocolDurableState.Unchanged, null, ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.ListRecoveries), new(0, 0, 0, false), "Cancelled.", null) { CommandId = command ?? ProtocolCommandId.CreateRandom() };

    private static ProtocolSessionStateMachine Ready() { ProtocolSessionStateMachine machine = new(); machine.AcceptHandshake(new("gui", "1"), "server"); return machine; }

    private static PackageOpenedEvent Register(ProtocolSessionStateMachine machine, FakePackageAuthority authority)
    {
        InstallationReleaseIdentity release = authority.Release;
        return machine.RegisterPackageAuthority(new(machine.SessionId, release.Tag, release.SourceCommit, "/tmp/package.zip", "/tmp/SHA256SUMS", "/tmp/build.json", "/tmp/install.json", "/tmp/bundle.jsonl", "/tmp/bundle.sha256"), release, authority, authority);
    }

    private static RecoveryCatalogEvent Catalog(ProtocolSessionStateMachine machine, FakeRecoveryAuthority[] recoveries, string gamePath = "/game")
    {
        RecoveryHistory history = new(Sha256Digest.Parse(HashA), recoveries.Select((recovery, index) => new RecoveryGenerationInfo(recovery.GenerationId, recovery.OriginAction, index == 0, recovery.OriginAction == InstallationAction.Backup, recovery.RestoreRelease)));
        return machine.RecordRecoveryCatalogAuthorities(new(machine.SessionId, gamePath), history, recoveries);
    }

    private static InspectedInstallationState Inspection(
        InstallationAction action,
        FakePackageAuthority? package = null,
        FakeRecoveryAuthority? recovery = null,
        ModifiedFileReplacementCandidate[]? candidates = null,
        ModifiedFileReplacementApproval[]? approvals = null,
        PlanConflict[]? conflicts = null,
        object? repairAuthority = null,
        PlannedOperation[]? operations = null,
        InstallationReleaseIdentity? currentRelease = null,
        InstallationReleaseIdentity? expectedResultRelease = null,
        RecoveryCapacityState? planRecoveryCapacity = null,
        RecoveryCapacityState? inspectionRecoveryCapacity = null
    )
    {
        PlannedOperation operation = action switch
        {
            InstallationAction.Uninstall => new(PlanOperationKind.Remove, NormalizedRelativePath.Parse("StardewModdingAPI.dll"), Sha256Digest.Parse(HashA), null),
            InstallationAction.Backup => new(PlanOperationKind.Retain, NormalizedRelativePath.Parse("StardewModdingAPI.dll"), Sha256Digest.Parse(HashA), Sha256Digest.Parse(HashA)),
            _ => new(PlanOperationKind.Create, NormalizedRelativePath.Parse("StardewModdingAPI.dll"), null, Sha256Digest.Parse(HashB))
        };
        RecoveryCapacityState planCapacity = planRecoveryCapacity ?? new RecoveryCapacityState(0, 64);
        InstallationPlan plan = new(action, operations ?? [operation], conflicts ?? [], ObservedInstallationState.KnownModified, planCapacity);
        Sha256Digest planSha = Sha256Digest.Hash(System.Text.Encoding.UTF8.GetBytes(plan.ToCanonicalJson()));
        BoundInstallationPlan binding = new(action, Root, 7, planSha, package?.ManifestSha256, null, null, recovery?.SnapshotSha256, null, recovery?.GenerationId, recovery?.AuthorizedHeadPointerSha256, package, recovery);
        return new(
            plan,
            binding,
            package,
            recovery,
            repairAuthority ?? new object(),
            currentRelease ?? (action == InstallationAction.Install ? null : CreateRelease()),
            expectedResultRelease ?? (action is InstallationAction.Uninstall or InstallationAction.Backup ? null : CreateRelease()),
            ObservedInstallationState.KnownModified,
            inspectionRecoveryCapacity ?? planCapacity,
            candidates,
            approvals
        );
    }

    private static RecoveryPrunePlan Prune(Guid[] catalog, int retain, Guid[] retained, Guid[] removed, Guid[]? cleanup = null, bool hasAuxiliaryCleanup = false) => new(Root, 7, Sha256Digest.Parse(HashA), retain, catalog, retained, removed, cleanup ?? removed, [], null, hasAuxiliaryCleanup);
    private static FakeRecoveryAuthority Recovery(Guid id, string head, GameRootIdentity root) => new(id, InstallationAction.Backup, root, Sha256Digest.Parse(head), CreateRelease());
    private static InstallationReleaseIdentity CreateRelease() => CreateReleaseWithPackage(Sha256Digest.Parse(HashA), 123);
    private static InstallationReleaseIdentity CreateRelease(string version, int alpha)
    {
        string tag = $"fork-4eh5xitv6787h645ebv-linux-v{version}-alpha.{alpha}";
        string embeddedVersion = $"{version}-unofficial.4eh5xitv6787h645ebv.linux.alpha.{alpha}";
        return new(
            "https://github.com/4eh5xitv6787h645ebv/SMAPI",
            tag,
            embeddedVersion,
            $"SMAPI-{embeddedVersion}-linux-x64-installer.zip",
            "1111111111111111111111111111111111111111",
            "2222222222222222222222222222222222222222",
            Sha256Digest.Parse(HashA),
            123,
            $"4eh5xitv6787h645ebv/SMAPI/.github/workflows/linux-alpha-release.yml@refs/tags/{tag}",
            "Release",
            "linux-x64"
        );
    }
    private static InstallationReleaseIdentity CreateReleaseWithPackage(Sha256Digest packageSha256, long packageSize) => new("https://github.com/4eh5xitv6787h645ebv/SMAPI", "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2", "4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2", "SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2-linux-x64-installer.zip", "1111111111111111111111111111111111111111", "2222222222222222222222222222222222222222", packageSha256, packageSize, "4eh5xitv6787h645ebv/SMAPI/.github/workflows/linux-alpha-release.yml@refs/tags/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2", "Release", "linux-x64");

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
