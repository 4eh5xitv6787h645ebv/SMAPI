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
        ProtocolPlanCandidate[] candidates = [new(candidate, FileReplacementCandidateReason.LegacyInstaller, FileReplacementCandidateDisposition.Replace, "legacy", HashA, 10, 420, HashB, true, "Observed legacy file.")];
        ProtocolPlanOperation[] operations = [new(PlanOperationKind.Create, "a", null, HashA)];
        ProtocolPlanConflict[] conflicts = [];
        ProtocolPlanId plan = ProtocolPlanId.CreateRandom();
        ProtocolPlanDigest digest = ProtocolPlanDigest.Compute(ExecutionDigest, InstallerOperation.Repair, package, null, GameRoot, CreateRelease(), CreateRelease(), ObservedInstallState.KnownModified, operations, conflicts, candidates, "Repair.", [], true);
        ProtocolPrunePlanId prune = ProtocolPrunePlanId.CreateRandom();
        string[] cleanup = ["22222222222222222222222222222222"];
        ProtocolPlanDigest pruneDigest = ProtocolPlanDigest.ComputePrune(ExecutionDigest, catalog, GameRoot, HashA, 1, [retainedRecovery], [recovery], cleanup, "Prune.", [], true);

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
            new PrunePlanEvent(session, prune, pruneDigest, ExecutionDigest, catalog, GameRoot, HashA, 1, [retainedRecovery], [recovery], cleanup, "Prune.", [], true),
            new ProgressEvent(session, plan, digest, 0, InstallerProgressStage.BackingUp, 0, null, "Hashing."),
            new PruneProgressEvent(session, prune, pruneDigest, 0, ProtocolPruneProgressStage.Revalidating, 0, null, "Revalidating."),
            new SuccessEvent(session, plan, digest, InstallerOperation.Repair, "Done.", 1, ProtocolRecoveryResult.NotNeeded, "Launch.", "/tmp/smapi.log"),
            new RolledBackFailureEvent(session, plan, digest, "copy-failed", "Failed.", "Restored.", 1, ProtocolRecoveryResult.Succeeded, "Retry.", null),
            new RecoverableInterruptionEvent(session, plan, digest, "power-loss", "Interrupted.", InstallerRecoveryAction.InspectAgain, "Journal retained.", 1, ProtocolRecoveryResult.Pending, "Inspect.", null),
            new CancelledEvent(session, plan, digest, "Cancelled.", "Safe.", 0, ProtocolRecoveryResult.NotNeeded, "Close.", null),
            new PruneSuccessEvent(session, prune, pruneDigest, 1, 1, "Pruned.", "Close.", null),
            new PruneFailureEvent(session, prune, pruneDigest, "failed", "Failed.", 0, 0, ProtocolRecoveryResult.Succeeded, "Retry.", null),
            new PruneInterruptionEvent(session, prune, pruneDigest, "interrupted", "Interrupted.", InstallerRecoveryAction.InspectAgain, 0, 0, ProtocolRecoveryResult.Pending, "Inspect.", null),
            new PruneCancelledEvent(session, prune, pruneDigest, "Cancelled.", "Safe.", 0, 0, ProtocolRecoveryResult.NotNeeded, "Close.", null),
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
    public void Deserialize_RejectsVersionTypeMissingNewlineAndIntegerEnumMatrix()
    {
        string valid = ProtocolJsonSerializer.SerializeLine(new HandshakeRequest("gui", "1"));
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeRequestLine(valid.Replace("\"protocolVersion\":1", "\"protocolVersion\":2", StringComparison.Ordinal))).Should().Throw<ProtocolException>().WithMessage("*Unsupported protocol version*");
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeRequestLine(valid.Replace("handshake.request", "unknown.request", StringComparison.Ordinal))).Should().Throw<ProtocolException>().WithMessage("*Unknown version 1 protocol message type*");
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeRequestLine(valid.Replace(",\"payload\":{\"clientName\":\"gui\",\"clientVersion\":\"1\"}", "", StringComparison.Ordinal))).Should().Throw<ProtocolException>().WithMessage("*missing the required 'payload'*");
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeRequestLine(valid + "\n")).Should().Throw<ProtocolException>().WithMessage("*line terminator*");
        ProtocolSessionId session = ProtocolSessionId.CreateRandom(); string inspect = ProtocolJsonSerializer.SerializeLine(new InspectPlanRequest(session, "/game", InstallerOperation.Uninstall, null, null));
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeRequestLine(inspect.Replace("\"operation\":\"uninstall\"", "\"operation\":3", StringComparison.Ordinal))).Should().Throw<ProtocolException>().WithMessage("*canonical camel case*");
    }

    [Test]
    public void Serialize_UsesExactDeterministicWirePropertyOrder()
    {
        string line = ProtocolJsonSerializer.SerializeLine(new HandshakeRequest("gui", "1"));
        line.Should().Be("{\"protocolVersion\":1,\"messageType\":\"handshake.request\",\"payload\":{\"clientName\":\"gui\",\"clientVersion\":\"1\"}}");
    }

    [Test]
    public void OutputCollectionGetters_ReturnDefensiveCopies()
    {
        PlanEvent plan = CreatePlan(); ProtocolPlanOperation originalOperation = plan.Operations[0]; ProtocolPlanOperation[] operations = plan.Operations; operations[0] = operations[0] with { Path = "mutated" }; plan.Operations[0].Should().Be(originalOperation);
        string[] warnings = plan.Warnings; warnings.Should().BeEmpty(); Array.Resize(ref warnings, 1); warnings[0] = "mutated"; plan.Warnings.Should().BeEmpty();
        ProtocolRecoverySelectionId keep = ProtocolRecoverySelectionId.CreateRandom(); ProtocolRecoverySelectionId remove = ProtocolRecoverySelectionId.CreateRandom(); string[] cleanup = ["11111111111111111111111111111111"]; ProtocolRecoveryCatalogId catalog = ProtocolRecoveryCatalogId.CreateRandom();
        ProtocolPlanDigest digest = ProtocolPlanDigest.ComputePrune(ExecutionDigest, catalog, GameRoot, HashA, 1, [keep], [remove], cleanup, "Prune.", [], true);
        PrunePlanEvent prune = new(plan.SessionId, ProtocolPrunePlanId.CreateRandom(), digest, ExecutionDigest, catalog, GameRoot, HashA, 1, [keep], [remove], cleanup, "Prune.", [], true);
        string[] returnedCleanup = prune.CleanupGenerationIds; returnedCleanup[0] = "22222222222222222222222222222222"; prune.CleanupGenerationIds.Should().Equal(cleanup);
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
        AlterPlan(plan, candidates: [plan.Candidates[0] with { Reason = FileReplacementCandidateReason.UnknownCollision }]).Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*display data*");
        AlterPlan(plan, candidates: [plan.Candidates[0] with { Disposition = FileReplacementCandidateDisposition.Restore }]).Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*core-defined pair*");
        AlterPlan(plan, candidates: [plan.Candidates[0] with { ProposedResultSha256 = HashA }]).Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*display data*");
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

        ProtocolRecoveryAuthority wrongRoot = authority with { GameRoot = GameRoot with { Inode = 99 } };
        ProtocolPlanDigest wrongRootDigest = ProtocolPlanDigest.Compute(ExecutionDigest, InstallerOperation.Rollback, null, wrongRoot, GameRoot, CreateRelease(), CreateRelease(), ObservedInstallState.KnownModified, operations, [], [], "Rollback.", [], true);
        new PlanEvent(session, ProtocolPlanId.CreateRandom(), wrongRootDigest, ExecutionDigest, InstallerOperation.Rollback, null, wrongRoot, GameRoot, CreateRelease(), CreateRelease(), ObservedInstallState.KnownModified, operations, [], [], "Rollback.", [], true)
            .Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*outer plan game root*");
        ProtocolRecoveryGeneration badGeneration = generation with { OriginOperation = InstallerOperation.Update, IsUserCheckpoint = true };
        ProtocolRecoveryAuthority badCheckpoint = authority with { Generation = badGeneration };
        ProtocolPlanDigest badCheckpointDigest = ProtocolPlanDigest.Compute(ExecutionDigest, InstallerOperation.Rollback, null, badCheckpoint, GameRoot, CreateRelease(), CreateRelease(), ObservedInstallState.KnownModified, operations, [], [], "Rollback.", [], true);
        new PlanEvent(session, ProtocolPlanId.CreateRandom(), badCheckpointDigest, ExecutionDigest, InstallerOperation.Rollback, null, badCheckpoint, GameRoot, CreateRelease(), CreateRelease(), ObservedInstallState.KnownModified, operations, [], [], "Rollback.", [], true)
            .Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*checkpoint flag*");
    }

    [Test]
    public void PruneDigest_BindsExactCatalogMembershipAndDisplayData()
    {
        ProtocolSessionId session = ProtocolSessionId.CreateRandom(); ProtocolRecoveryCatalogId catalog = ProtocolRecoveryCatalogId.CreateRandom(); ProtocolRecoverySelectionId retained = ProtocolRecoverySelectionId.CreateRandom(); ProtocolRecoverySelectionId removed = ProtocolRecoverySelectionId.CreateRandom(); ProtocolPrunePlanId id = ProtocolPrunePlanId.CreateRandom();
        string[] cleanup = ["22222222222222222222222222222222"];
        ProtocolPlanDigest digest = ProtocolPlanDigest.ComputePrune(ExecutionDigest, catalog, GameRoot, HashA, 1, [retained], [removed], cleanup, "Prune.", [], true);
        PrunePlanEvent plan = new(session, id, digest, ExecutionDigest, catalog, GameRoot, HashA, 1, [retained], [removed], cleanup, "Prune.", [], true);
        ProtocolJsonSerializer.SerializeLine(plan).Should().NotBeEmpty();
        new PrunePlanEvent(plan.SessionId, plan.PrunePlanId, plan.PruneDigest, plan.ExecutionBindingDigest, plan.CatalogId, plan.GameRoot, plan.HeadSha256, plan.RetainNewest, plan.RetainedSelectionIds, [ProtocolRecoverySelectionId.CreateRandom()], plan.CleanupGenerationIds, plan.Summary, plan.Warnings, true)
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
    public void PlanDigest_RejectsFieldByFieldStructuredMutations()
    {
        PlanEvent plan = CreatePlan();
        PlanEvent[] altered =
        [
            CopyPlan(plan, gameRoot: plan.GameRoot with { Inode = plan.GameRoot.Inode + 1 }),
            CopyPlan(plan, currentRelease: plan.CurrentRelease! with { PackageSha256 = HashB }),
            CopyPlan(plan, targetRelease: plan.TargetRelease! with { PackageSha256 = HashB }),
            CopyPlan(plan, observedState: ObservedInstallState.Unknown),
            CopyPlan(plan, operations: [plan.Operations[0] with { ResultSha256 = HashB }]),
            CopyPlan(plan, conflicts: [new(PlanConflictCode.ModifiedOwnedFile, "a")]),
            CopyPlan(plan, warnings: ["Changed warning."])
        ];
        foreach (PlanEvent value in altered)
            value.Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*digest*");
    }

    [Test]
    public void UnsafePathsUndefinedEnumsAndNoOpPrunesAreRejected()
    {
        ProtocolSessionId session = ProtocolSessionId.CreateRandom();
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new ListRecoveriesRequest(session, "/game/../other"))).Should().Throw<ProtocolException>().WithMessage("*canonical absolute Linux path*");
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new GameDiscoveryEvent(session, [new("/game", (ProtocolGameCandidateState)999, "Game")]))).Should().Throw<ProtocolException>().WithMessage("*isn't defined*");
        PlanEvent plan = CreatePlan();
        AlterPlan(plan, candidates: [plan.Candidates[0] with { ProposedResultSha256 = null }]).Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*Only removal candidates*");
        AlterPlan(plan, candidates: [plan.Candidates[0] with { Reason = FileReplacementCandidateReason.ModifiedReceiptOwned, Disposition = FileReplacementCandidateDisposition.Remove }]).Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*Only removal candidates*");
        AlterPlan(plan, candidates: [plan.Candidates[0] with { Reason = (FileReplacementCandidateReason)999 }]).Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*isn't defined*");
        ProtocolPlanCandidate impossible = plan.Candidates[0] with { Reason = FileReplacementCandidateReason.OfficialLauncherBackup };
        RecomputePlanWithCandidates(plan, [impossible]).Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*core-defined pair*");
        ProtocolPlanCandidate misleadingRetain = plan.Candidates[0] with { Reason = FileReplacementCandidateReason.OfficialLauncherBackup, Disposition = FileReplacementCandidateDisposition.TrustRetained };
        RecomputePlanWithCandidates(plan, [misleadingRetain]).Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*exact observed digest*");
        ProtocolRecoveryCatalogId catalog = ProtocolRecoveryCatalogId.CreateRandom(); ProtocolRecoverySelectionId only = ProtocolRecoverySelectionId.CreateRandom(); ProtocolPrunePlanId id = ProtocolPrunePlanId.CreateRandom();
        ProtocolPlanDigest digest = ProtocolPlanDigest.ComputePrune(ExecutionDigest, catalog, GameRoot, HashA, 1, [only], [], [], "No-op.", [], true);
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new PrunePlanEvent(session, id, digest, ExecutionDigest, catalog, GameRoot, HashA, 1, [only], [], [], "No-op.", [], true))).Should().Throw<ProtocolException>().WithMessage("*no-op*");
    }

    [Test]
    public void RecoveryCatalog_RejectsInvalidCurrentAndCheckpointSemantics()
    {
        ProtocolSessionId session = ProtocolSessionId.CreateRandom(); ProtocolRecoveryCatalogId catalog = ProtocolRecoveryCatalogId.CreateRandom(); ProtocolRecoverySelectionId selection = ProtocolRecoverySelectionId.CreateRandom();
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new RecoveryCatalogEvent(session, catalog, GameRoot, HashA, [new(selection, "11111111111111111111111111111111", InstallerOperation.Update, false, false)]))).Should().Throw<ProtocolException>().WithMessage("*first generation as current*");
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new RecoveryCatalogEvent(session, catalog, GameRoot, HashA, [new(selection, "11111111111111111111111111111111", InstallerOperation.Update, true, true)]))).Should().Throw<ProtocolException>().WithMessage("*checkpoint flag*");
    }

    [Test]
    public void IdentifierParsers_RejectUppercaseZeroAndMalformedValues()
    {
        string uppercase = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        FluentActions.Invoking(() => ProtocolSessionId.Parse(uppercase)).Should().Throw<ProtocolException>();
        FluentActions.Invoking(() => ProtocolPlanId.Parse(new string('0', 32))).Should().Throw<ProtocolException>();
        FluentActions.Invoking(() => ProtocolPackageId.Parse("not-an-id")).Should().Throw<ProtocolException>();
        FluentActions.Invoking(() => ProtocolRecoveryCatalogId.Parse(uppercase)).Should().Throw<ProtocolException>();
        FluentActions.Invoking(() => ProtocolRecoverySelectionId.Parse(new string('0', 32))).Should().Throw<ProtocolException>();
        FluentActions.Invoking(() => ProtocolCandidateId.Parse("bad")).Should().Throw<ProtocolException>();
        FluentActions.Invoking(() => ProtocolPrunePlanId.Parse(uppercase)).Should().Throw<ProtocolException>();
    }

    [Test]
    public void PrunePlan_RejectsCombinedCatalogOver64AndBindsCleanupSet()
    {
        ProtocolSessionId session = ProtocolSessionId.CreateRandom(); ProtocolRecoveryCatalogId catalog = ProtocolRecoveryCatalogId.CreateRandom(); ProtocolPrunePlanId id = ProtocolPrunePlanId.CreateRandom();
        ProtocolRecoverySelectionId[] retained = Enumerable.Range(1, 40).Select(_ => ProtocolRecoverySelectionId.CreateRandom()).ToArray();
        ProtocolRecoverySelectionId[] removed = Enumerable.Range(1, 25).Select(_ => ProtocolRecoverySelectionId.CreateRandom()).ToArray();
        string[] cleanup = Enumerable.Range(1, 25).Select(index => Guid.Parse(index.ToString("x32")).ToString("N")).ToArray();
        ProtocolPlanDigest digest = ProtocolPlanDigest.ComputePrune(ExecutionDigest, catalog, GameRoot, HashA, 40, retained, removed, cleanup, "Prune.", [], true);
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new PrunePlanEvent(session, id, digest, ExecutionDigest, catalog, GameRoot, HashA, 40, retained, removed, cleanup, "Prune.", [], true))).Should().Throw<ProtocolException>().WithMessage("*bounded exact catalog partition*");

        ProtocolRecoverySelectionId keep = ProtocolRecoverySelectionId.CreateRandom(); string pending = "11111111111111111111111111111111";
        ProtocolPlanDigest cleanupDigest = ProtocolPlanDigest.ComputePrune(ExecutionDigest, catalog, GameRoot, HashA, 1, [keep], [], [pending], "Cleanup.", [], true);
        PrunePlanEvent cleanupPlan = new(session, id, cleanupDigest, ExecutionDigest, catalog, GameRoot, HashA, 1, [keep], [], [pending], "Cleanup.", [], true);
        ProtocolJsonSerializer.SerializeLine(cleanupPlan).Should().NotBeEmpty();
        new PrunePlanEvent(session, id, cleanupDigest, ExecutionDigest, catalog, GameRoot, HashA, 1, [keep], [], ["22222222222222222222222222222222"], "Cleanup.", [], true)
            .Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*digest*");
    }

    private static PlanEvent CreatePlan()
    {
        ProtocolSessionId session = ProtocolSessionId.CreateRandom(); ProtocolPackageId package = ProtocolPackageId.CreateRandom(); ProtocolCandidateId candidateId = ProtocolCandidateId.CreateRandom();
        ProtocolPlanOperation[] operations = [new(PlanOperationKind.Create, "a", null, HashA)]; ProtocolPlanConflict[] conflicts = [];
        ProtocolPlanCandidate[] candidates = [new(candidateId, FileReplacementCandidateReason.LegacyInstaller, FileReplacementCandidateDisposition.Replace, "legacy", HashA, 1, 420, HashB, false, "Observed.")];
        ProtocolPlanDigest digest = ProtocolPlanDigest.Compute(ExecutionDigest, InstallerOperation.Repair, package, null, GameRoot, CreateRelease(), CreateRelease(), ObservedInstallState.KnownModified, operations, conflicts, candidates, "Repair.", [], true);
        return new(session, ProtocolPlanId.CreateRandom(), digest, ExecutionDigest, InstallerOperation.Repair, package, null, GameRoot, CreateRelease(), CreateRelease(), ObservedInstallState.KnownModified, operations, conflicts, candidates, "Repair.", [], true);
    }

    private static PlanEvent AlterPlan(PlanEvent plan, string? summary = null, ProtocolPlanCandidate[]? candidates = null, ProtocolPackageId? packageId = null) =>
        new(plan.SessionId, plan.PlanId, plan.PlanDigest, plan.ExecutionBindingDigest, plan.Operation, packageId ?? plan.PackageId, plan.RecoveryAuthority, plan.GameRoot, plan.CurrentRelease, plan.TargetRelease, plan.ObservedState, plan.Operations, plan.Conflicts, candidates ?? plan.Candidates, summary ?? plan.Summary, plan.Warnings, plan.RequiresConfirmation);

    private static PlanEvent RecomputePlanWithCandidates(PlanEvent plan, ProtocolPlanCandidate[] candidates)
    {
        ProtocolPlanDigest digest = ProtocolPlanDigest.Compute(plan.ExecutionBindingDigest, plan.Operation, plan.PackageId, plan.RecoveryAuthority, plan.GameRoot, plan.CurrentRelease, plan.TargetRelease, plan.ObservedState, plan.Operations, plan.Conflicts, candidates, plan.Summary, plan.Warnings, plan.RequiresConfirmation);
        return new(plan.SessionId, plan.PlanId, digest, plan.ExecutionBindingDigest, plan.Operation, plan.PackageId, plan.RecoveryAuthority, plan.GameRoot, plan.CurrentRelease, plan.TargetRelease, plan.ObservedState, plan.Operations, plan.Conflicts, candidates, plan.Summary, plan.Warnings, plan.RequiresConfirmation);
    }

    private static PlanEvent CopyPlan(PlanEvent plan, ProtocolGameRootIdentity? gameRoot = null, ProtocolReleaseIdentity? currentRelease = null, ProtocolReleaseIdentity? targetRelease = null, ObservedInstallState? observedState = null, ProtocolPlanOperation[]? operations = null, ProtocolPlanConflict[]? conflicts = null, string[]? warnings = null) =>
        new(plan.SessionId, plan.PlanId, plan.PlanDigest, plan.ExecutionBindingDigest, plan.Operation, plan.PackageId, plan.RecoveryAuthority, gameRoot ?? plan.GameRoot, currentRelease ?? plan.CurrentRelease, targetRelease ?? plan.TargetRelease, observedState ?? plan.ObservedState, operations ?? plan.Operations, conflicts ?? plan.Conflicts, plan.Candidates, plan.Summary, warnings ?? plan.Warnings, true);

    private static ProtocolReleaseIdentity CreateRelease() => new(
        "https://github.com/4eh5xitv6787h645ebv/SMAPI", "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2", "4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2",
        "SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2-linux-x64-installer.zip", "1111111111111111111111111111111111111111", "2222222222222222222222222222222222222222", HashA, 123456,
        "4eh5xitv6787h645ebv/SMAPI/.github/workflows/linux-alpha-release.yml@refs/tags/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2", "Release", "linux-x64"
    );
}
