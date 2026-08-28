using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Protocol.V1;

namespace StardewModdingAPI.Installer.Core.Tests.Protocol.V1;

[TestFixture]
internal sealed class ProtocolJsonSerializerTests
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly ProtocolGameRootIdentity GameRoot = new("/game", 1, 2, 3, 4);
    private static readonly ProtocolPlanDigest ExecutionDigest = ProtocolPlanDigest.Parse(HashB);

    [Test]
    public void AllMessageKinds_RoundTripWithStrictContracts()
    {
        ProtocolSessionId session = ProtocolSessionId.CreateRandom();
        ProtocolPackageId package = ProtocolPackageId.CreateRandom();
        ProtocolRecoveryCatalogId catalog = ProtocolRecoveryCatalogId.CreateRandom();
        ProtocolRecoverySelectionId recovery = ProtocolRecoverySelectionId.CreateRandom();
        ProtocolRecoverySelectionId retainedRecovery = ProtocolRecoverySelectionId.CreateRandom();
        ProtocolCandidateId candidate = ProtocolCandidateId.CreateRandom();
        ProtocolPlanCandidate[] candidates = [new(candidate, ProtocolCandidateKind.LegacyInstallerFile, "legacy", HashA, 10, 420, HashB, true, "Observed legacy file.")];
        ProtocolPlanOperation[] operations = [new(PlanOperationKind.Create, "a", null, HashA)];
        ProtocolPlanConflict[] conflicts = [];
        ProtocolPlanId plan = ProtocolPlanId.CreateRandom();
        ProtocolPlanDigest digest = ProtocolPlanDigest.Compute(ExecutionDigest, InstallerOperation.Repair, package, null, GameRoot, CreateRelease(), CreateRelease(), ObservedInstallState.KnownModified, operations, conflicts, candidates, "Repair.", [], true);
        ProtocolPrunePlanId prune = ProtocolPrunePlanId.CreateRandom();
        ProtocolPlanDigest pruneDigest = ProtocolPlanDigest.ComputePrune(ExecutionDigest, catalog, GameRoot, HashA, 1, [retainedRecovery], [recovery], "Prune.", [], true);

        ProtocolRequest[] requests =
        [
            new HandshakeRequest("gui", "1"), new DiscoverGamesRequest(session),
            new OpenPackageRequest(session, CreateRelease().Tag, CreateRelease().SourceCommit, "/tmp/package.zip", "/tmp/SHA256SUMS", "/tmp/build.json", "/tmp/install.json"),
            new ListRecoveriesRequest(session, "/game"), new InspectPlanRequest(session, "/game", InstallerOperation.Repair, package, null),
            new SelectPlanCandidatesRequest(session, plan, digest, [candidate]), new ConfirmPlanRequest(session, plan, digest), new ExecutePlanRequest(session, plan, digest), new CancelPlanRequest(session, plan, digest),
            new InspectPruneRequest(session, catalog, 1), new ConfirmPruneRequest(session, prune, pruneDigest), new ExecutePruneRequest(session, prune, pruneDigest), new CancelPruneRequest(session, prune, pruneDigest)
        ];
        ProtocolEvent[] events =
        [
            new HandshakeEvent(session, "server", ["v1"]), new GameDiscoveryEvent(session, [new("/game", ProtocolGameCandidateState.Valid, "Stardew Valley")]),
            new PackageOpenedEvent(session, package, CreateRelease()), new RecoveryCatalogEvent(session, catalog, GameRoot, HashA, [new(retainedRecovery, "11111111111111111111111111111111", InstallerOperation.Backup, true, true), new(recovery, "22222222222222222222222222222222", InstallerOperation.Update, false, false)]),
            new PlanEvent(session, plan, digest, ExecutionDigest, InstallerOperation.Repair, package, null, GameRoot, CreateRelease(), CreateRelease(), ObservedInstallState.KnownModified, operations, conflicts, candidates, "Repair.", [], true),
            new PrunePlanEvent(session, prune, pruneDigest, ExecutionDigest, catalog, GameRoot, HashA, 1, [retainedRecovery], [recovery], "Prune.", [], true),
            new ProgressEvent(session, plan, digest, 0, InstallerProgressStage.BackingUp, 0, null, "Hashing."),
            new PruneProgressEvent(session, prune, pruneDigest, 0, ProtocolPruneProgressStage.Revalidating, 0, null, "Revalidating."),
            new SuccessEvent(session, plan, digest, InstallerOperation.Repair, "Done.", 1, ProtocolRecoveryResult.NotNeeded, "Launch.", "/tmp/smapi.log"),
            new RolledBackFailureEvent(session, plan, digest, "copy-failed", "Failed.", "Restored.", 1, ProtocolRecoveryResult.Succeeded, "Retry.", null),
            new RecoverableInterruptionEvent(session, plan, digest, "power-loss", "Interrupted.", InstallerRecoveryAction.InspectAgain, "Journal retained.", 1, ProtocolRecoveryResult.Pending, "Inspect.", null),
            new CancelledEvent(session, plan, digest, "Cancelled.", "Safe.", 0, ProtocolRecoveryResult.NotNeeded, "Close.", null),
            new PruneSuccessEvent(session, prune, pruneDigest, 1, "Pruned.", "Close.", null),
            new PruneFailureEvent(session, prune, pruneDigest, "failed", "Failed.", 0, ProtocolRecoveryResult.Succeeded, "Retry.", null),
            new PruneInterruptionEvent(session, prune, pruneDigest, "interrupted", "Interrupted.", InstallerRecoveryAction.InspectAgain, 0, ProtocolRecoveryResult.Pending, "Inspect.", null),
            new PruneCancelledEvent(session, prune, pruneDigest, "Cancelled.", "Safe.", 0, ProtocolRecoveryResult.NotNeeded, "Close.", null),
            new PrePlanErrorEvent(session, "invalid-package", "Invalid.", "Choose another.", false, null)
        ];

        foreach (ProtocolRequest request in requests)
            ProtocolJsonSerializer.DeserializeRequestLine(ProtocolJsonSerializer.SerializeLine(request)).Should().BeEquivalentTo(request);
        foreach (ProtocolEvent item in events)
            ProtocolJsonSerializer.DeserializeEventLine(ProtocolJsonSerializer.SerializeLine(item)).Should().BeEquivalentTo(item);
    }

    [Test]
    public void Deserialize_RejectsUnknownDuplicateAndWrongDirection()
    {
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeRequestLine("{\"protocolVersion\":1,\"messageType\":\"handshake.request\",\"payload\":{\"clientName\":\"a\",\"clientVersion\":\"1\",\"extra\":true}}"))
            .Should().Throw<ProtocolException>().WithMessage("*unknown 'extra'*");
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeRequestLine("{\"protocolVersion\":1,\"protocolVersion\":1,\"messageType\":\"handshake.request\",\"payload\":{\"clientName\":\"a\",\"clientVersion\":\"1\"}}"))
            .Should().Throw<ProtocolException>().WithMessage("*duplicate 'protocolVersion'*");
        string eventLine = ProtocolJsonSerializer.SerializeLine(new HandshakeEvent(ProtocolSessionId.CreateRandom(), "server", []));
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeRequestLine(eventLine)).Should().Throw<ProtocolException>().WithMessage("*event can't be accepted as a request*");
    }

    [Test]
    public void Deserialize_RejectsUnknownNestedCandidateFieldAndDuplicateSelection()
    {
        string line = ProtocolJsonSerializer.SerializeLine(CreatePlan());
        string unknown = line.Replace("\"evidence\":", "\"unknown\":0,\"evidence\":", StringComparison.Ordinal);
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeEventLine(unknown)).Should().Throw<ProtocolException>().WithMessage("*unknown 'unknown'*");

        PlanEvent plan = CreatePlan(); ProtocolCandidateId id = plan.Candidates[0].CandidateId;
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new SelectPlanCandidatesRequest(plan.SessionId, plan.PlanId, plan.PlanDigest, [id, id])))
            .Should().Throw<ProtocolException>().WithMessage("*duplicate IDs*");
    }

    [Test]
    public void PlanDigest_BindsCandidatesRecoveryAndDisplayData()
    {
        PlanEvent plan = CreatePlan();
        AlterPlan(plan, summary: "Altered.").Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*display data*");
        ProtocolPlanCandidate changed = plan.Candidates[0] with { Selected = !plan.Candidates[0].Selected };
        AlterPlan(plan, candidates: [changed]).Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*display data*");
        AlterPlan(plan, packageId: ProtocolPackageId.CreateRandom()).Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*display data*");
    }

    [Test]
    public void RollbackDigest_BindsFullCatalogRootHeadAndGenerationAuthority()
    {
        ProtocolSessionId session = ProtocolSessionId.CreateRandom(); ProtocolRecoveryCatalogId catalog = ProtocolRecoveryCatalogId.CreateRandom(); ProtocolRecoverySelectionId selection = ProtocolRecoverySelectionId.CreateRandom();
        ProtocolRecoveryGeneration generation = new(selection, "11111111111111111111111111111111", InstallerOperation.Backup, true, true);
        ProtocolRecoveryAuthority authority = new(catalog, selection, GameRoot, HashA, generation);
        ProtocolPlanOperation[] operations = [new(PlanOperationKind.Restore, "a", null, HashA)];
        ProtocolPlanDigest digest = ProtocolPlanDigest.Compute(ExecutionDigest, InstallerOperation.Rollback, null, authority, GameRoot, CreateRelease(), CreateRelease(), ObservedInstallState.KnownModified, operations, [], [], "Rollback.", [], true);
        PlanEvent plan = new(session, ProtocolPlanId.CreateRandom(), digest, ExecutionDigest, InstallerOperation.Rollback, null, authority, GameRoot, CreateRelease(), CreateRelease(), ObservedInstallState.KnownModified, operations, [], [], "Rollback.", [], true);
        ProtocolJsonSerializer.SerializeLine(plan).Should().NotBeEmpty();
        ProtocolRecoveryAuthority changed = authority with { HeadSha256 = HashB };
        new PlanEvent(plan.SessionId, plan.PlanId, plan.PlanDigest, plan.ExecutionBindingDigest, plan.Operation, null, changed, plan.GameRoot, plan.CurrentRelease, plan.TargetRelease, plan.ObservedState, plan.Operations, plan.Conflicts, plan.Candidates, plan.Summary, plan.Warnings, true)
            .Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*display data*");
    }

    [Test]
    public void PruneDigest_BindsExactCatalogMembershipAndDisplayData()
    {
        ProtocolSessionId session = ProtocolSessionId.CreateRandom(); ProtocolRecoveryCatalogId catalog = ProtocolRecoveryCatalogId.CreateRandom(); ProtocolRecoverySelectionId retained = ProtocolRecoverySelectionId.CreateRandom(); ProtocolRecoverySelectionId removed = ProtocolRecoverySelectionId.CreateRandom(); ProtocolPrunePlanId id = ProtocolPrunePlanId.CreateRandom();
        ProtocolPlanDigest digest = ProtocolPlanDigest.ComputePrune(ExecutionDigest, catalog, GameRoot, HashA, 1, [retained], [removed], "Prune.", [], true);
        PrunePlanEvent plan = new(session, id, digest, ExecutionDigest, catalog, GameRoot, HashA, 1, [retained], [removed], "Prune.", [], true);
        ProtocolJsonSerializer.SerializeLine(plan).Should().NotBeEmpty();
        new PrunePlanEvent(plan.SessionId, plan.PrunePlanId, plan.PruneDigest, plan.ExecutionBindingDigest, plan.CatalogId, plan.GameRoot, plan.HeadSha256, plan.RetainNewest, plan.RetainedSelectionIds, [ProtocolRecoverySelectionId.CreateRandom()], plan.Summary, plan.Warnings, true)
            .Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*exact catalog selection*");
    }

    [Test]
    public void BoundedAndCanonicalValues_AreRejected()
    {
        ProtocolSessionId session = ProtocolSessionId.CreateRandom();
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new GameDiscoveryEvent(session, Enumerable.Range(0, ProtocolJsonSerializer.MaxGameCandidates + 1).Select(i => new ProtocolGameCandidate($"/game/{i}", ProtocolGameCandidateState.Valid, "Game")).ToArray())))
            .Should().Throw<ProtocolException>().WithMessage("*too large*");
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new PrePlanErrorEvent(session, "bad", new string('x', 4097), "Retry.", false, null))).Should().Throw<ProtocolException>();
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new DiscoverGamesRequest(new ProtocolSessionId(new string('0', 32))))).Should().Throw<ProtocolException>().WithMessage("*session ID*");
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new InspectPruneRequest(session, ProtocolRecoveryCatalogId.CreateRandom(), 0))).Should().Throw<ProtocolException>().WithMessage("*between 1 and 64*");
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new InspectPruneRequest(session, ProtocolRecoveryCatalogId.CreateRandom(), 65))).Should().Throw<ProtocolException>().WithMessage("*between 1 and 64*");
    }

    [Test]
    public void Deserialize_EnforcesByteDepthAndNestedExactnessLimits()
    {
        string oversized = new string('x', ProtocolJsonSerializer.MaxLineBytes + 1);
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeRequestLine(oversized)).Should().Throw<ProtocolException>().WithMessage("*byte limit*");
        string deep = "{\"protocolVersion\":1,\"messageType\":\"handshake.request\",\"payload\":" + string.Concat(Enumerable.Repeat("{\"x\":", ProtocolJsonSerializer.MaxDepth + 1)) + "0" + new string('}', ProtocolJsonSerializer.MaxDepth + 1) + "}";
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeRequestLine(deep)).Should().Throw<ProtocolException>().WithMessage("*strict version 1 JSON*");

        string plan = ProtocolJsonSerializer.SerializeLine(CreatePlan());
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeEventLine(plan.Replace("\"evidence\":\"Observed.\"", "\"evidence\":\"Observed.\",\"evidence\":\"Again.\"", StringComparison.Ordinal))).Should().Throw<ProtocolException>().WithMessage("*duplicate 'evidence'*");
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeEventLine(plan.Replace(",\"evidence\":\"Observed.\"", "", StringComparison.Ordinal))).Should().Throw<ProtocolException>().WithMessage("*missing the required 'evidence'*");
    }

    [Test]
    public void WireAndDigests_AreDeterministicAndEnumTokensAreCanonical()
    {
        PlanEvent plan = CreatePlan();
        ProtocolJsonSerializer.SerializeLine(plan).Should().Be(ProtocolJsonSerializer.SerializeLine(plan));
        ProtocolPlanDigest.Compute(plan.ExecutionBindingDigest, plan.Operation, plan.PackageId, plan.RecoveryAuthority, plan.GameRoot, plan.CurrentRelease, plan.TargetRelease, plan.ObservedState, plan.Operations, plan.Conflicts, plan.Candidates, plan.Summary, plan.Warnings, true).Should().Be(plan.PlanDigest);
        string line = ProtocolJsonSerializer.SerializeLine(new InspectPlanRequest(plan.SessionId, "/game", InstallerOperation.Repair, plan.PackageId, null));
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeRequestLine(line.Replace("\"repair\"", "\"Repair\"", StringComparison.Ordinal))).Should().Throw<ProtocolException>().WithMessage("*canonical camel case*");
    }

    [Test]
    public void UnsafePathsUndefinedEnumsAndNoOpPrunesAreRejected()
    {
        ProtocolSessionId session = ProtocolSessionId.CreateRandom();
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new ListRecoveriesRequest(session, "/game/../other"))).Should().Throw<ProtocolException>().WithMessage("*canonical absolute Linux path*");
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new GameDiscoveryEvent(session, [new("/game", (ProtocolGameCandidateState)999, "Game")]))).Should().Throw<ProtocolException>().WithMessage("*isn't defined*");
        ProtocolRecoveryCatalogId catalog = ProtocolRecoveryCatalogId.CreateRandom(); ProtocolRecoverySelectionId only = ProtocolRecoverySelectionId.CreateRandom(); ProtocolPrunePlanId id = ProtocolPrunePlanId.CreateRandom();
        ProtocolPlanDigest digest = ProtocolPlanDigest.ComputePrune(ExecutionDigest, catalog, GameRoot, HashA, 1, [only], [], "No-op.", [], true);
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new PrunePlanEvent(session, id, digest, ExecutionDigest, catalog, GameRoot, HashA, 1, [only], [], "No-op.", [], true))).Should().Throw<ProtocolException>().WithMessage("*no-op*");
    }

    [Test]
    public void RecoveryCatalog_RejectsInvalidCurrentAndCheckpointSemantics()
    {
        ProtocolSessionId session = ProtocolSessionId.CreateRandom(); ProtocolRecoveryCatalogId catalog = ProtocolRecoveryCatalogId.CreateRandom(); ProtocolRecoverySelectionId selection = ProtocolRecoverySelectionId.CreateRandom();
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new RecoveryCatalogEvent(session, catalog, GameRoot, HashA, [new(selection, "11111111111111111111111111111111", InstallerOperation.Update, false, false)]))).Should().Throw<ProtocolException>().WithMessage("*first generation as current*");
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new RecoveryCatalogEvent(session, catalog, GameRoot, HashA, [new(selection, "11111111111111111111111111111111", InstallerOperation.Update, true, true)]))).Should().Throw<ProtocolException>().WithMessage("*checkpoint flag*");
    }

    private static PlanEvent CreatePlan()
    {
        ProtocolSessionId session = ProtocolSessionId.CreateRandom(); ProtocolPackageId package = ProtocolPackageId.CreateRandom(); ProtocolCandidateId candidateId = ProtocolCandidateId.CreateRandom();
        ProtocolPlanOperation[] operations = [new(PlanOperationKind.Create, "a", null, HashA)]; ProtocolPlanConflict[] conflicts = [];
        ProtocolPlanCandidate[] candidates = [new(candidateId, ProtocolCandidateKind.LegacyInstallerFile, "legacy", HashA, 1, 420, HashB, false, "Observed.")];
        ProtocolPlanDigest digest = ProtocolPlanDigest.Compute(ExecutionDigest, InstallerOperation.Repair, package, null, GameRoot, CreateRelease(), CreateRelease(), ObservedInstallState.KnownModified, operations, conflicts, candidates, "Repair.", [], true);
        return new(session, ProtocolPlanId.CreateRandom(), digest, ExecutionDigest, InstallerOperation.Repair, package, null, GameRoot, CreateRelease(), CreateRelease(), ObservedInstallState.KnownModified, operations, conflicts, candidates, "Repair.", [], true);
    }

    private static PlanEvent AlterPlan(PlanEvent plan, string? summary = null, ProtocolPlanCandidate[]? candidates = null, ProtocolPackageId? packageId = null) =>
        new(plan.SessionId, plan.PlanId, plan.PlanDigest, plan.ExecutionBindingDigest, plan.Operation, packageId ?? plan.PackageId, plan.RecoveryAuthority, plan.GameRoot, plan.CurrentRelease, plan.TargetRelease, plan.ObservedState, plan.Operations, plan.Conflicts, candidates ?? plan.Candidates, summary ?? plan.Summary, plan.Warnings, plan.RequiresConfirmation);

    private static ProtocolReleaseIdentity CreateRelease() => new(
        "https://github.com/4eh5xitv6787h645ebv/SMAPI", "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2", "4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2",
        "SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2-linux-x64-installer.zip", "1111111111111111111111111111111111111111", "2222222222222222222222222222222222222222", HashA, 123456,
        "4eh5xitv6787h645ebv/SMAPI/.github/workflows/linux-alpha-release.yml@refs/tags/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2", "Release", "linux-x64"
    );
}
