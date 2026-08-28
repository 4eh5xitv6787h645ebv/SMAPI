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
        ProtocolCandidateId candidate = ProtocolCandidateId.CreateRandom();
        ProtocolPlanCandidate[] candidates = [new(candidate, ProtocolCandidateKind.LegacyInstallerFile, "legacy", HashA, 10, 420, HashB, true, "Observed legacy file.")];
        ProtocolPlanOperation[] operations = [new(PlanOperationKind.Create, "a", null, HashA)];
        ProtocolPlanConflict[] conflicts = [];
        ProtocolPlanId plan = ProtocolPlanId.CreateRandom();
        ProtocolPlanDigest digest = ProtocolPlanDigest.Compute(ExecutionDigest, InstallerOperation.Repair, package, null, GameRoot, CreateRelease(), CreateRelease(), ObservedInstallState.KnownModified, operations, conflicts, candidates, "Repair.", [], true);
        ProtocolPrunePlanId prune = ProtocolPrunePlanId.CreateRandom();
        ProtocolPlanDigest pruneDigest = ProtocolPlanDigest.ComputePrune(ExecutionDigest, catalog, GameRoot, HashA, 0, [], [recovery], "Prune.", [], true);

        ProtocolRequest[] requests =
        [
            new HandshakeRequest("gui", "1"), new DiscoverGamesRequest(session),
            new OpenPackageRequest(session, CreateRelease().Tag, CreateRelease().SourceCommit, "/tmp/package.zip", "/tmp/SHA256SUMS", "/tmp/build.json", "/tmp/install.json"),
            new ListRecoveriesRequest(session, "/game"), new InspectPlanRequest(session, "/game", InstallerOperation.Repair, package, null),
            new SelectPlanCandidatesRequest(session, plan, digest, [candidate]), new ConfirmPlanRequest(session, plan, digest), new ExecutePlanRequest(session, plan, digest), new CancelPlanRequest(session, plan, digest),
            new InspectPruneRequest(session, catalog, 0), new ConfirmPruneRequest(session, prune, pruneDigest), new ExecutePruneRequest(session, prune, pruneDigest)
        ];
        ProtocolEvent[] events =
        [
            new HandshakeEvent(session, "server", ["v1"]), new GameDiscoveryEvent(session, [new("/game", ProtocolGameCandidateState.Valid, "Stardew Valley")]),
            new PackageOpenedEvent(session, package, CreateRelease()), new RecoveryCatalogEvent(session, catalog, GameRoot, HashA, [new(recovery, "11111111111111111111111111111111", InstallerOperation.Backup, true, true)]),
            new PlanEvent(session, plan, digest, ExecutionDigest, InstallerOperation.Repair, package, null, GameRoot, CreateRelease(), CreateRelease(), ObservedInstallState.KnownModified, operations, conflicts, candidates, "Repair.", [], true),
            new PrunePlanEvent(session, prune, pruneDigest, ExecutionDigest, catalog, GameRoot, HashA, 0, [], [recovery], "Prune.", [], true),
            new ProgressEvent(session, plan, digest, 0, InstallerProgressStage.BackingUp, 0, null, "Hashing."),
            new SuccessEvent(session, plan, digest, InstallerOperation.Repair, "Done.", 1, ProtocolRecoveryResult.NotNeeded, "Launch.", "/tmp/smapi.log"),
            new RolledBackFailureEvent(session, plan, digest, "copy-failed", "Failed.", "Restored.", 1, ProtocolRecoveryResult.Succeeded, "Retry.", null),
            new RecoverableInterruptionEvent(session, plan, digest, "power-loss", "Interrupted.", InstallerRecoveryAction.InspectAgain, "Journal retained.", 1, ProtocolRecoveryResult.Pending, "Inspect.", null),
            new CancelledEvent(session, plan, digest, "Cancelled.", "Safe.", 0, ProtocolRecoveryResult.NotNeeded, "Close.", null),
            new PruneSuccessEvent(session, prune, pruneDigest, 1, "Pruned.", "Close.", null), new PrePlanErrorEvent(session, "invalid-package", "Invalid.", "Choose another.", false, null)
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
    public void PruneDigest_BindsExactCatalogMembershipAndDisplayData()
    {
        ProtocolSessionId session = ProtocolSessionId.CreateRandom(); ProtocolRecoveryCatalogId catalog = ProtocolRecoveryCatalogId.CreateRandom(); ProtocolRecoverySelectionId removed = ProtocolRecoverySelectionId.CreateRandom(); ProtocolPrunePlanId id = ProtocolPrunePlanId.CreateRandom();
        ProtocolPlanDigest digest = ProtocolPlanDigest.ComputePrune(ExecutionDigest, catalog, GameRoot, HashA, 0, [], [removed], "Prune.", [], true);
        PrunePlanEvent plan = new(session, id, digest, ExecutionDigest, catalog, GameRoot, HashA, 0, [], [removed], "Prune.", [], true);
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
        new(plan.SessionId, plan.PlanId, plan.PlanDigest, plan.ExecutionBindingDigest, plan.Operation, packageId ?? plan.PackageId, plan.RecoverySelectionId, plan.GameRoot, plan.CurrentRelease, plan.TargetRelease, plan.ObservedState, plan.Operations, plan.Conflicts, candidates ?? plan.Candidates, summary ?? plan.Summary, plan.Warnings, plan.RequiresConfirmation);

    private static ProtocolReleaseIdentity CreateRelease() => new(
        "https://github.com/4eh5xitv6787h645ebv/SMAPI", "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2", "4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2",
        "SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2-linux-x64-installer.zip", "1111111111111111111111111111111111111111", "2222222222222222222222222222222222222222", HashA, 123456,
        "4eh5xitv6787h645ebv/SMAPI/.github/workflows/linux-alpha-release.yml@refs/tags/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2", "Release", "linux-x64"
    );
}
