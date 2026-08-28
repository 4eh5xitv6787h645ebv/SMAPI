using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Protocol.V1;

namespace StardewModdingAPI.Installer.Core.Tests.Protocol.V1;

[TestFixture]
internal sealed class ProtocolSessionStateMachineTests
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly ProtocolPlanDigest ExecutionDigest = ProtocolPlanDigest.Parse(HashB);
    private static readonly ProtocolGameRootIdentity Root = new("/game", 1, 2, 3, 4);

    [Test]
    public void Authorities_AreRandomSessionLocalAndRequiredByOperation()
    {
        ProtocolSessionStateMachine machine = Ready();
        machine.RecordDiscovery(new DiscoverGamesRequest(machine.SessionId), [new("/game", ProtocolGameCandidateState.Valid, "Game")]).Candidates.Should().ContainSingle();
        PackageOpenedEvent package = Open(machine);
        RecoveryCatalogEvent catalog = Catalog(machine);
        package.PackageId.Value.Should().HaveLength(32);
        catalog.Generations[0].SelectionId.Value.Should().HaveLength(32);
        catalog.Generations[0].SelectionId.Value.Should().NotBe(catalog.CatalogId.Value);

        InspectPlanRequest unknown = new(machine.SessionId, "/game", InstallerOperation.Install, ProtocolPackageId.CreateRandom(), null);
        FluentActions.Invoking(() => Issue(machine, unknown)).Should().Throw<ProtocolException>().WithMessage("*unknown or stale*");
        InspectPlanRequest wrongRoot = new(machine.SessionId, "/other", InstallerOperation.Rollback, null, catalog.Generations[0].SelectionId);
        FluentActions.Invoking(() => Issue(machine, wrongRoot)).Should().Throw<ProtocolException>().WithMessage("*different game root*");
    }

    [Test]
    public void RelistingRecoveries_InvalidatesOldCatalogAndSelectionIds()
    {
        ProtocolSessionStateMachine machine = Ready(); RecoveryCatalogEvent stale = Catalog(machine); RecoveryCatalogEvent current = Catalog(machine);
        current.CatalogId.Should().NotBe(stale.CatalogId);
        FluentActions.Invoking(() => Issue(machine, new(machine.SessionId, "/game", InstallerOperation.Rollback, null, stale.Generations[0].SelectionId))).Should().Throw<ProtocolException>().WithMessage("*unknown or stale*");
        FluentActions.Invoking(() => machine.IssuePrunePlan(new InspectPruneRequest(machine.SessionId, stale.CatalogId, 0), ExecutionDigest, "Prune.", [])).Should().Throw<ProtocolException>().WithMessage("*unknown or stale*");
    }

    [Test]
    public void CandidateSelection_MintsIdsAndInvalidatesOldPlanBinding()
    {
        ProtocolSessionStateMachine machine = Ready(); PackageOpenedEvent package = Open(machine);
        PlanEvent old = Issue(machine, new(machine.SessionId, "/game", InstallerOperation.Repair, package.PackageId, null), Candidates());
        ProtocolCandidateId selected = old.Candidates[0].CandidateId;
        PlanEvent current = machine.SelectCandidates(new(machine.SessionId, old.PlanId, old.PlanDigest, [selected]), ExecutionDigest, Operations(), [], "Repair selected.", []);
        current.PlanId.Should().NotBe(old.PlanId); current.PlanDigest.Should().NotBe(old.PlanDigest); current.Candidates[0].Selected.Should().BeTrue();
        FluentActions.Invoking(() => machine.ConfirmPlan(new(machine.SessionId, old.PlanId, old.PlanDigest))).Should().Throw<ProtocolException>().WithMessage("*stale*");
        FluentActions.Invoking(() => machine.SelectCandidates(new(machine.SessionId, current.PlanId, current.PlanDigest, [ProtocolCandidateId.CreateRandom()]), ExecutionDigest, Operations(), [], "Bad.", [])).Should().Throw<ProtocolException>().WithMessage("*unknown or stale*");
    }

    [Test]
    public void Plan_HappyPathRequiresExactConfirmationAndHasDetailedTerminal()
    {
        ProtocolSessionStateMachine machine = Ready(); PackageOpenedEvent package = Open(machine); PlanEvent plan = Issue(machine, new(machine.SessionId, "/game", InstallerOperation.Install, package.PackageId, null));
        FluentActions.Invoking(() => machine.BeginExecution(new(machine.SessionId, plan.PlanId, plan.PlanDigest))).Should().Throw<ProtocolException>().WithMessage("*confirmed*");
        machine.ConfirmPlan(new(machine.SessionId, plan.PlanId, plan.PlanDigest)); machine.BeginExecution(new(machine.SessionId, plan.PlanId, plan.PlanDigest));
        machine.RecordProgress(new(machine.SessionId, plan.PlanId, plan.PlanDigest, 0, InstallerProgressStage.BackingUp, 0, null, "Hashing."));
        machine.Complete(new SuccessEvent(machine.SessionId, plan.PlanId, plan.PlanDigest, InstallerOperation.Install, "Installed.", 4, ProtocolRecoveryResult.NotNeeded, "Launch.", "/tmp/smapi.log"));
        machine.State.Should().Be(ProtocolSessionState.Completed); machine.LastProgressSequence.Should().Be(0);
    }

    [Test]
    public void ConflictsCannotConfirmAndStaleDigestCannotCancel()
    {
        ProtocolSessionStateMachine machine = Ready(); PackageOpenedEvent package = Open(machine);
        PlanEvent plan = machine.IssuePlan(new(machine.SessionId, "/game", InstallerOperation.Repair, package.PackageId, null), ExecutionDigest, Root, CreateRelease(), ObservedInstallState.KnownModified, Operations(), [new(PlanConflictCode.ModifiedOwnedFile, "a")], [], "Blocked.", []);
        FluentActions.Invoking(() => machine.ConfirmPlan(new(machine.SessionId, plan.PlanId, plan.PlanDigest))).Should().Throw<ProtocolException>().WithMessage("*unresolved conflicts*");
        FluentActions.Invoking(() => machine.RequestCancellation(new(machine.SessionId, plan.PlanId, ProtocolPlanDigest.Parse(HashA)))).Should().Throw<ProtocolException>().WithMessage("*stale or altered*");
        machine.RequestCancellation(new(machine.SessionId, plan.PlanId, plan.PlanDigest));
        machine.Complete(new CancelledEvent(machine.SessionId, plan.PlanId, plan.PlanDigest, "Cancelled.", "No changes.", 0, ProtocolRecoveryResult.NotNeeded, "Close.", null));
    }

    [Test]
    public void Prune_RequiresExactCatalogPlanConfirmationAndResultCount()
    {
        ProtocolSessionStateMachine machine = Ready(); RecoveryCatalogEvent catalog = Catalog(machine);
        PrunePlanEvent plan = machine.IssuePrunePlan(new(machine.SessionId, catalog.CatalogId, 0), ExecutionDigest, "Prune.", []);
        plan.RemovedSelectionIds.Should().HaveCount(2);
        FluentActions.Invoking(() => machine.BeginPrune(new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest))).Should().Throw<ProtocolException>().WithMessage("*confirmed*");
        machine.ConfirmPrune(new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest)); machine.BeginPrune(new(machine.SessionId, plan.PrunePlanId, plan.PruneDigest));
        FluentActions.Invoking(() => machine.Complete(new PruneSuccessEvent(machine.SessionId, plan.PrunePlanId, plan.PruneDigest, 1, "Wrong.", "Inspect.", null))).Should().Throw<ProtocolException>().WithMessage("*count*");
        machine.Complete(new PruneSuccessEvent(machine.SessionId, plan.PrunePlanId, plan.PruneDigest, 2, "Pruned.", "Close.", null)); machine.State.Should().Be(ProtocolSessionState.Completed);
    }

    [Test]
    public void PrePlanErrors_AreBoundedOrderedAndCanBeTerminal()
    {
        ProtocolSessionStateMachine machine = Ready();
        machine.RecordPrePlanError(new(machine.SessionId, "bad-package", "Invalid package.", "Choose another.", false, null)); machine.State.Should().Be(ProtocolSessionState.Ready);
        machine.RecordPrePlanError(new(machine.SessionId, "no-game", "No game.", "Install the game.", true, "/tmp/smapi.log")); machine.State.Should().Be(ProtocolSessionState.Completed);
        FluentActions.Invoking(() => machine.RecordPrePlanError(new(machine.SessionId, "again", "Again.", "Close.", false, null))).Should().Throw<ProtocolException>().WithMessage("*Completed*");
    }

    [Test]
    public void DuplicateRecoveryAndCandidateSourcesAreRejected()
    {
        ProtocolSessionStateMachine machine = Ready();
        ProtocolRecoveryGenerationSource source = new("11111111111111111111111111111111", InstallerOperation.Backup, true, true);
        FluentActions.Invoking(() => machine.RecordRecoveryCatalog(new(machine.SessionId, "/game"), Root, HashA, [source, source])).Should().Throw<ProtocolException>().WithMessage("*unique*");
        PackageOpenedEvent package = Open(machine); ProtocolPlanCandidateSource candidate = Candidates()[0];
        FluentActions.Invoking(() => Issue(machine, new(machine.SessionId, "/game", InstallerOperation.Repair, package.PackageId, null), [candidate, candidate])).Should().Throw<ProtocolException>().WithMessage("*duplicate path*");
    }

    private static ProtocolSessionStateMachine Ready() { ProtocolSessionStateMachine machine = new(); machine.AcceptHandshake(new("gui", "1"), "server", "v1"); return machine; }
    private static PackageOpenedEvent Open(ProtocolSessionStateMachine machine) { ProtocolReleaseIdentity release = CreateRelease(); return machine.OpenPackage(new(machine.SessionId, release.Tag, release.SourceCommit, "/tmp/package.zip", "/tmp/SHA256SUMS", "/tmp/build.json", "/tmp/install.json"), release); }
    private static RecoveryCatalogEvent Catalog(ProtocolSessionStateMachine machine) => machine.RecordRecoveryCatalog(new(machine.SessionId, "/game"), Root, HashA,
        [new("11111111111111111111111111111111", InstallerOperation.Backup, true, true), new("22222222222222222222222222222222", InstallerOperation.Update, false, false)]);
    private static PlanEvent Issue(ProtocolSessionStateMachine machine, InspectPlanRequest request, ProtocolPlanCandidateSource[]? candidates = null) => machine.IssuePlan(request, ExecutionDigest, Root, request.Operation == InstallerOperation.Install ? null : CreateRelease(), ObservedInstallState.KnownModified, Operations(), [], candidates ?? [], $"{request.Operation}.", []);
    private static ProtocolPlanOperation[] Operations() => [new(PlanOperationKind.Create, "a", null, HashA)];
    private static ProtocolPlanCandidateSource[] Candidates() => [new(ProtocolCandidateKind.LegacyInstallerFile, "legacy", HashA, 1, 420, HashB, false, "Observed legacy file.")];
    private static ProtocolReleaseIdentity CreateRelease() => new("https://github.com/4eh5xitv6787h645ebv/SMAPI", "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2", "4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2", "SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2-linux-x64-installer.zip", "1111111111111111111111111111111111111111", "2222222222222222222222222222222222222222", HashA, 123456, "4eh5xitv6787h645ebv/SMAPI/.github/workflows/linux-alpha-release.yml@refs/tags/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2", "Release", "linux-x64");
}
