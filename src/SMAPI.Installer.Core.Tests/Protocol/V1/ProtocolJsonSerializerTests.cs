using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Core.Transactions;

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
        ProtocolGameRootIdentity recoveredRoot = new("/game", 1, 2, 3, 8);

        ProtocolRequest[] requests =
        [
            new HandshakeRequest("gui", "1"), new DiscoverGamesRequest(session), new RecoverInterruptedRequest(session, "/game"),
            new OpenPackageRequest(session, CreateRelease().Tag, CreateRelease().SourceCommit, "/tmp/package.zip", "/tmp/SHA256SUMS", "/tmp/build.json", "/tmp/install.json", "/tmp/bundle.jsonl", "/tmp/bundle.sha256"),
            new ListRecoveriesRequest(session, "/game"), new InspectPlanRequest(session, "/game", InstallerOperation.Repair, package, null),
            new SelectPlanCandidatesRequest(session, plan, digest, [candidate]), new GetPlanPageRequest(session, plan, digest, ProtocolPlanPageKind.Candidates, 0), new ConfirmPlanRequest(session, plan, digest), new ExecutePlanRequest(session, plan, digest), new CancelPlanRequest(session, plan, digest),
            new InspectPruneRequest(session, catalog, 1), new ConfirmPruneRequest(session, prune, pruneDigest), new ExecutePruneRequest(session, prune, pruneDigest), new CancelPruneRequest(session, prune, pruneDigest)
        ];
        ProtocolEvent[] events =
        [
            new HandshakeEvent(session, "server", ["v1"]), new GameDiscoveryEvent(session, [new("/game", LinuxGameFolderStatus.Valid, "Stardew Valley")]),
            new RecoveryProgressEvent(session, 0, TransactionStage.Recovering, 0, null, "Recovering."),
            new RecoveryCompletedEvent(session, ProtocolInterruptedRecoveryOutcome.RecoveryCompleted, new(ProtocolDurableState.RecoveryCompleted, null, ProtocolRecoveryDisposition.Completed, ProtocolNextAction.InspectAgain), new(recoveredRoot, 7, 8, true, [new("11111111111111111111111111111111", 2)]), "Recovered.", null),
            new RecoveryFailureEvent(session, ProtocolInterruptedRecoveryOutcome.PartialFailure, new(ProtocolDurableState.RecoveryRequired, ProtocolTerminalErrorCode.RecoveryFailed, ProtocolRecoveryDisposition.InterruptedRecoveryRequired, ProtocolNextAction.RecoverInterrupted), "Recovery failed.", null, new(new("/game", 1, 2, 3, 7), 7, null, null, [new("11111111111111111111111111111111", 2)])),
            new PackageOpenedEvent(session, package, CreateRelease()), new RecoveryCatalogEvent(session, catalog, GameRoot, HashA, [new(retainedRecovery, "11111111111111111111111111111111", InstallerOperation.Backup, true, true), new(recovery, "22222222222222222222222222222222", InstallerOperation.Update, false, false)]),
            new PlanEvent(session, plan, digest, ExecutionDigest, InstallerOperation.Repair, package, null, GameRoot, CreateRelease(), CreateRelease(), ObservedInstallState.KnownModified, operations, conflicts, candidates, "Repair.", [], true),
            new CommandAcknowledgedEvent(session, ProtocolAcknowledgementKind.PlanConfirmed, plan, null),
            new PlanPageEvent(session, plan, digest, ProtocolPlanPageKind.Candidates, 0, 1, null, [], [], candidates, []),
            new PrunePlanEvent(session, prune, pruneDigest, ExecutionDigest, catalog, GameRoot, HashA, 1, [retainedRecovery], [recovery], cleanup, "Prune.", [], true),
            new ProgressEvent(session, plan, digest, 0, TransactionStage.PreparingRecovery, 0, null, "Hashing."),
            new PruneProgressEvent(session, prune, pruneDigest, 0, TransactionStage.Revalidating, 0, null, "Revalidating."),
            new SuccessEvent(session, plan, digest, InstallerOperation.Repair, ProtocolExecutionOutcome.Succeeded, new(ProtocolDurableState.Committed, null, ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.InspectAgain), new(1, 0, 0, 0, 0, 0), "Done.", "/tmp/smapi.log"),
            new RolledBackFailureEvent(session, plan, digest, ProtocolExecutionOutcome.FailedAndRolledBack, new(ProtocolDurableState.RolledBack, ProtocolTerminalErrorCode.IoFailure, ProtocolRecoveryDisposition.Completed, ProtocolNextAction.InspectAgain), new(1, 1, 0, 0, 0, 0), "Failed.", "Restored.", null),
            new RecoverableInterruptionEvent(session, plan, digest, ProtocolExecutionOutcome.InterruptedRecoveryRequired, new(ProtocolDurableState.RecoveryRequired, ProtocolTerminalErrorCode.RecoveryFailed, ProtocolRecoveryDisposition.InterruptedRecoveryRequired, ProtocolNextAction.RecoverInterrupted), new(1, 0, 0, 0, 0, 0), "Interrupted.", "Journal retained.", null),
            new CancelledEvent(session, plan, digest, ProtocolExecutionOutcome.CancelledBeforeMutation, new(ProtocolDurableState.Unchanged, null, ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.InspectAgain), new(0, 0, 0, 0, 0, 0), "Cancelled.", null),
            new PruneSuccessEvent(session, prune, pruneDigest, ProtocolPruneOutcome.Succeeded, new(ProtocolDurableState.PruneApplied, null, ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.ListRecoveries), new(1, 1, 0, false), "Pruned.", null),
            new PruneFailureEvent(session, prune, pruneDigest, ProtocolPruneOutcome.FailedBeforePublication, new(ProtocolDurableState.Unchanged, ProtocolTerminalErrorCode.IoFailure, ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.ListRecoveries), new(0, 0, 0, false), "Failed.", null),
            new PruneInterruptionEvent(session, prune, pruneDigest, ProtocolPruneOutcome.Interrupted, new(ProtocolDurableState.Unchanged, ProtocolTerminalErrorCode.IoFailure, ProtocolRecoveryDisposition.CleanupPending, ProtocolNextAction.ListRecoveries), new(0, 0, 1, false), "Interrupted.", null),
            new PruneCancelledEvent(session, prune, pruneDigest, ProtocolPruneOutcome.CancelledBeforePublication, new(ProtocolDurableState.Unchanged, null, ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.ListRecoveries), new(0, 0, 0, false), "Cancelled.", null),
            new PrePlanRejectedEvent(session, ProtocolPrePlanErrorCode.PackageRejected, "Invalid.", ProtocolNextAction.ReopenVerifiedPackage, false, null)
        ];

        requests.Cast<ProtocolMessage>().Concat(events).Select(message => message.Kind).Should().BeEquivalentTo(Enum.GetValues<ProtocolMessageKind>());
        foreach (ProtocolRequest request in requests)
        {
            string line = ProtocolJsonSerializer.SerializeLine(request);
            ProtocolJsonSerializer.DeserializeRequestLine(line).Should().BeEquivalentTo(request);
            FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeRequestLine(RemoveCommandId(line, request.CommandId))).Should().Throw<ProtocolException>().WithMessage("*missing the required 'commandId'*");
            ProtocolRequest defaultId = request with { CommandId = default };
            defaultId.Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*command ID*");
        }
        foreach (ProtocolEvent item in events)
        {
            string line = ProtocolJsonSerializer.SerializeLine(item);
            ProtocolJsonSerializer.DeserializeEventLine(line).Should().BeEquivalentTo(item);
            FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeEventLine(RemoveCommandId(line, item.CommandId))).Should().Throw<ProtocolException>().WithMessage("*missing the required 'commandId'*");
            ProtocolEvent defaultId = item with { CommandId = default };
            defaultId.Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*command ID*");
        }
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
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeRequestLine("{\"protocolVersion\":1,\"messageType\":\"handshake.request\"}")).Should().Throw<ProtocolException>().WithMessage("*missing the required 'payload'*");
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeRequestLine(valid + "\n")).Should().Throw<ProtocolException>().WithMessage("*line terminator*");
        ProtocolSessionId session = ProtocolSessionId.CreateRandom(); string inspect = ProtocolJsonSerializer.SerializeLine(new InspectPlanRequest(session, "/game", InstallerOperation.Uninstall, null, null));
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeRequestLine(inspect.Replace("\"operation\":\"uninstall\"", "\"operation\":3", StringComparison.Ordinal))).Should().Throw<ProtocolException>().WithMessage("*canonical camel case*");
    }

    [Test]
    public void Serialize_UsesExactDeterministicWirePropertyOrder()
    {
        HandshakeRequest request = new("gui", "1") { CommandId = ProtocolCommandId.Parse("11111111111111111111111111111111") };
        string line = ProtocolJsonSerializer.SerializeLine(request);
        line.Should().Be("{\"protocolVersion\":1,\"messageType\":\"handshake.request\",\"payload\":{\"commandId\":\"11111111111111111111111111111111\",\"clientName\":\"gui\",\"clientVersion\":\"1\"}}");
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
        ProtocolInterruptedRecoveryAttempt attempt = new(new("/game", 1, 2, 3, 7), 7, null, null, [new("11111111111111111111111111111111", 2)]);
        ProtocolRecoveredTransactionResult[] returnedRecoveries = attempt.RecoveredTransactions; returnedRecoveries[0] = returnedRecoveries[0] with { ChangedPathCount = 1 };
        attempt.RecoveredTransactions.Should().ContainSingle().Which.ChangedPathCount.Should().Be(2);
    }

    [Test]
    public void Deserialize_RejectsUnknownNestedCandidateFieldAndDuplicateSelection()
    {
        PlanEvent plan = CreatePlan();
        string line = ProtocolJsonSerializer.SerializeLine(new PlanPageEvent(plan.SessionId, plan.PlanId, plan.PlanDigest, ProtocolPlanPageKind.Candidates, 0, 1, null, [], [], plan.Candidates, []));
        string unknown = line.Replace("\"evidence\":", "\"unknown\":0,\"evidence\":", StringComparison.Ordinal);
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeEventLine(unknown)).Should().Throw<ProtocolException>().WithMessage("*unknown 'unknown'*");

        ProtocolCandidateId id = plan.Candidates[0].CandidateId;
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new SelectPlanCandidatesRequest(plan.SessionId, plan.PlanId, plan.PlanDigest, [id, id])))
            .Should().Throw<ProtocolException>().WithMessage("*duplicate IDs*");
    }

    [Test]
    public void OpenPackageRequest_RejectsFrontendProvidedGitHubCliAuthority()
    {
        OpenPackageRequest request = new(ProtocolSessionId.CreateRandom(), CreateRelease().Tag, CreateRelease().SourceCommit, "/tmp/package.zip", "/tmp/SHA256SUMS", "/tmp/build.json", "/tmp/install.json", "/tmp/bundle.jsonl", "/tmp/bundle.sha256");
        string line = ProtocolJsonSerializer.SerializeLine(request);
        string injected = line.Replace("\"attestationBundleChecksumPath\":\"/tmp/bundle.sha256\"", "\"attestationBundleChecksumPath\":\"/tmp/bundle.sha256\",\"gitHubCliPath\":\"/tmp/untrusted-gh\"", StringComparison.Ordinal);

        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeRequestLine(injected))
            .Should().Throw<ProtocolException>().WithMessage("*unknown 'gitHubCliPath'*");
    }

    [Test]
    public void OpenPackageRequest_ValidatesExactOptionalProcWorkspaceIdentity()
    {
        OpenPackageRequest request = new(
            ProtocolSessionId.CreateRandom(),
            CreateRelease().Tag,
            CreateRelease().SourceCommit,
            "/proc/123/fd/4/package.zip",
            "/proc/123/fd/4/SHA256SUMS",
            "/proc/123/fd/4/build.json",
            "/proc/123/fd/4/install.json",
            "/proc/123/fd/4/bundle.jsonl",
            "/proc/123/fd/4/bundle.sha256",
            new ProtocolProcWorkspaceIdentity(1, 2, 3, 4, 5)
        );
        string line = ProtocolJsonSerializer.SerializeLine(request);
        ProtocolJsonSerializer.DeserializeRequestLine(line).Should().BeEquivalentTo(request);
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeRequestLine(
            line.Replace("\"inode\":3", "\"inode\":3,\"extra\":4", StringComparison.Ordinal)
        )).Should().Throw<ProtocolException>().WithMessage("*unknown 'extra'*");
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeRequestLine(
            line.Replace(",\"inode\":3", "", StringComparison.Ordinal)
        )).Should().Throw<ProtocolException>().WithMessage("*missing the required 'inode'*");
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeRequestLine(
            line.Replace("\"inode\":3", "\"inode\":0", StringComparison.Ordinal)
        )).Should().Throw<ProtocolException>().WithMessage("*outside its canonical bounds*");
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeRequestLine(
            line.Replace("\"procWorkspaceIdentity\":{", "\"procWorkspaceIdentity\":[]", StringComparison.Ordinal)
        )).Should().Throw<ProtocolException>();
    }

    [Test]
    public void PlanSummaryAndPages_ValidateStructuredDataIndependentlyOfPagedDigestAuthority()
    {
        PlanEvent plan = CreatePlan();
        new PlanEvent(plan.SessionId, plan.PlanId, plan.PlanDigest, plan.ExecutionBindingDigest, plan.Operation, plan.PackageId, plan.RecoveryAuthority, plan.GameRoot, plan.CurrentRelease, plan.TargetRelease, plan.ObservedState, 1, 1, 1, 0, true, plan.Risks, ProtocolRecommendedDefault.Cancel, plan.Summary, true)
            .Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*executability*");
        ProtocolPlanCandidate invalid = plan.Candidates[0] with { ProposedResultSha256 = null };
        new PlanPageEvent(plan.SessionId, plan.PlanId, plan.PlanDigest, ProtocolPlanPageKind.Candidates, 0, 1, null, [], [], [invalid], [])
            .Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*Only removal candidates*");
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
        ProtocolRecoveryAuthority missingRestoreOutcome = authority with { Generation = generation with { RestoreRelease = null, RestoresUninstalledState = false } };
        new PlanEvent(plan.SessionId, plan.PlanId, plan.PlanDigest, plan.ExecutionBindingDigest, plan.Operation, null, missingRestoreOutcome, plan.GameRoot, plan.CurrentRelease, plan.TargetRelease, plan.ObservedState, plan.Operations, plan.Conflicts, plan.Candidates, plan.Summary, plan.Warnings, true)
            .Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*either one exact restore release or an uninstalled result*");

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
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new GameDiscoveryEvent(session, Enumerable.Range(0, ProtocolJsonSerializer.MaxGameCandidates + 1).Select(i => new ProtocolGameCandidate($"/game/{i}", LinuxGameFolderStatus.Valid, "Game")).ToArray())))
            .Should().Throw<ProtocolException>().WithMessage("*too large*");
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new PrePlanRejectedEvent(session, ProtocolPrePlanErrorCode.UnexpectedFailure, new string('x', 4097), ProtocolNextAction.ViewPrivateLog, false, null))).Should().Throw<ProtocolException>();
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new DiscoverGamesRequest(new ProtocolSessionId(new string('0', 32))))).Should().Throw<ProtocolException>().WithMessage("*session ID*");
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new InspectPruneRequest(session, ProtocolRecoveryCatalogId.CreateRandom(), 0))).Should().Throw<ProtocolException>().WithMessage("*between 1 and 64*");
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new InspectPruneRequest(session, ProtocolRecoveryCatalogId.CreateRandom(), 65))).Should().Throw<ProtocolException>().WithMessage("*between 1 and 64*");
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new RecoverInterruptedRequest(session, "relative/game"))).Should().Throw<ProtocolException>().WithMessage("*absolute*");
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new RecoveryProgressEvent(session, 0, TransactionStage.Recovering, 2, 1, "Invalid."))).Should().Throw<ProtocolException>().WithMessage("*inconsistent*");
        ProtocolTerminalState recoveredState = new(ProtocolDurableState.RecoveryCompleted, null, ProtocolRecoveryDisposition.Completed, ProtocolNextAction.InspectAgain);
        ProtocolRecoveredTransactionResult[] tooMany = Enumerable.Range(1, InstallerTransactionExecutor.MaximumTransactionStoreEntries + 1).Select(index => new ProtocolRecoveredTransactionResult(Guid.Parse(index.ToString("x32")).ToString("N"), 0)).ToArray();
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new RecoveryCompletedEvent(session, ProtocolInterruptedRecoveryOutcome.RecoveryCompleted, recoveredState, new(new("/game", 1, 2, 3, 8), 7, 8, true, tooMany), "Recovered.", null))).Should().Throw<ProtocolException>().WithMessage("*too large*");
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new RecoveryCompletedEvent(session, ProtocolInterruptedRecoveryOutcome.RecoveryCompleted, recoveredState, new(new("/game", 1, 2, 3, 8), 7, 8, true, [new("11111111111111111111111111111111", TransactionPlan.MaximumOperationCount + 1)]), "Recovered.", null))).Should().Throw<ProtocolException>().WithMessage("*invalid or duplicate*");
        ProtocolInterruptedRecoveryAttempt inconsistent = new(new("/game", 1, 2, 3, 8), 7, null, null, [new("11111111111111111111111111111111", 2)]);
        ProtocolTerminalState partialState = new(ProtocolDurableState.RecoveryRequired, ProtocolTerminalErrorCode.RecoveryFailed, ProtocolRecoveryDisposition.InterruptedRecoveryRequired, ProtocolNextAction.RecoverInterrupted);
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new RecoveryFailureEvent(session, ProtocolInterruptedRecoveryOutcome.PartialFailure, partialState, "Failed.", null, inconsistent))).Should().Throw<ProtocolException>().WithMessage("*root or generation identity*");
        ProtocolInterruptedRecoveryAttempt duplicate = new(new("/game", 1, 2, 3, 8), 7, 8, true, [new("11111111111111111111111111111111", 0), new("11111111111111111111111111111111", 1)]);
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new RecoveryFailureEvent(session, ProtocolInterruptedRecoveryOutcome.PartialFailure, partialState, "Failed.", null, duplicate))).Should().Throw<ProtocolException>().WithMessage("*invalid or duplicate*");
    }

    [Test]
    public void Deserialize_EnforcesByteDepthAndNestedExactnessLimits()
    {
        string oversized = new string('x', ProtocolJsonSerializer.MaxLineBytes + 1);
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeRequestLine(oversized)).Should().Throw<ProtocolException>().WithMessage("*byte limit*");
        string deep = "{\"protocolVersion\":1,\"messageType\":\"handshake.request\",\"payload\":" + string.Concat(Enumerable.Repeat("{\"x\":", ProtocolJsonSerializer.MaxDepth + 1)) + "0" + new string('}', ProtocolJsonSerializer.MaxDepth + 1) + "}";
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeRequestLine(deep)).Should().Throw<ProtocolException>().WithMessage("*strict version 1 JSON*");

        PlanEvent planSummary = CreatePlan();
        string plan = ProtocolJsonSerializer.SerializeLine(new PlanPageEvent(planSummary.SessionId, planSummary.PlanId, planSummary.PlanDigest, ProtocolPlanPageKind.Candidates, 0, 1, null, [], [], planSummary.Candidates, []));
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeEventLine(plan.Replace("\"evidence\":\"Observed.\"", "\"evidence\":\"Observed.\",\"evidence\":\"Again.\"", StringComparison.Ordinal))).Should().Throw<ProtocolException>().WithMessage("*duplicate 'evidence'*");
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeEventLine(plan.Replace(",\"evidence\":\"Observed.\"", "", StringComparison.Ordinal))).Should().Throw<ProtocolException>().WithMessage("*missing the required 'evidence'*");
        ProtocolSessionId session = ProtocolSessionId.CreateRandom();
        string recovery = ProtocolJsonSerializer.SerializeLine(new RecoveryFailureEvent(session, ProtocolInterruptedRecoveryOutcome.PartialFailure, new(ProtocolDurableState.RecoveryRequired, ProtocolTerminalErrorCode.RecoveryFailed, ProtocolRecoveryDisposition.InterruptedRecoveryRequired, ProtocolNextAction.RecoverInterrupted), "Failed.", null, new(new("/game", 1, 2, 3, 7), 7, null, null, [new("11111111111111111111111111111111", 2)])));
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeEventLine(recovery.Replace("\"changedPathCount\":2", "\"changedPathCount\":2,\"unknown\":true", StringComparison.Ordinal))).Should().Throw<ProtocolException>().WithMessage("*unknown 'unknown'*");
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
    public void TerminalStateRejectsInvalidCartesianCombinationsAndUnknownCountClaims()
    {
        ProtocolSessionId session = ProtocolSessionId.CreateRandom(); ProtocolPlanId plan = ProtocolPlanId.CreateRandom(); ProtocolPlanDigest digest = ProtocolPlanDigest.Parse(HashA);
        SuccessEvent success = new(session, plan, digest, InstallerOperation.Uninstall, ProtocolExecutionOutcome.Succeeded, new(ProtocolDurableState.Committed, null, ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.InspectAgain), new(1, 0, 0, 0, 0, 0), "Done.", null);
        (success with { TerminalState = success.TerminalState with { DurableState = ProtocolDurableState.Unchanged } }).Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*exact typed outcome table*");
        (success with { TerminalState = success.TerminalState with { ErrorCode = ProtocolTerminalErrorCode.IoFailure } }).Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*exact typed outcome table*");

        SuccessEvent cleanup = success with { Outcome = ProtocolExecutionOutcome.SucceededWithCleanupWarning, TerminalState = new(ProtocolDurableState.Committed, null, ProtocolRecoveryDisposition.CleanupPending, ProtocolNextAction.InspectAgain) };
        cleanup.Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*exact typed outcome table*");
        RecoverableInterruptionEvent unknown = new(session, plan, digest, ProtocolExecutionOutcome.UnexpectedCoreFailure, new(ProtocolDurableState.Unknown, ProtocolTerminalErrorCode.UnexpectedCoreFailure, ProtocolRecoveryDisposition.InterruptedRecoveryRequired, ProtocolNextAction.RecoverInterrupted), new(null, null, null, null, null, null), "Stopped.", "Unknown.", null);
        (unknown with { ExecutionSummary = new(0, 0, 0, 0, 0, 0) }).Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*known and unknown*");

        ProtocolPrunePlanId prune = ProtocolPrunePlanId.CreateRandom();
        PruneInterruptionEvent pending = new(session, prune, digest, ProtocolPruneOutcome.Interrupted, new(ProtocolDurableState.Unchanged, ProtocolTerminalErrorCode.IoFailure, ProtocolRecoveryDisposition.CleanupPending, ProtocolNextAction.ListRecoveries), new(0, 0, 0, false), "Stopped.", null);
        pending.Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*exact typed outcome table*");
    }

    [Test]
    public void ExecutionSummariesRejectStatusSpecificCountClaims()
    {
        ProtocolSessionId session = ProtocolSessionId.CreateRandom(); ProtocolPlanId plan = ProtocolPlanId.CreateRandom(); ProtocolPlanDigest digest = ProtocolPlanDigest.Parse(HashA);
        ProtocolTerminalState unchanged = new(ProtocolDurableState.Unchanged, ProtocolTerminalErrorCode.IoFailure, ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.InspectAgain);
        new RolledBackFailureEvent(session, plan, digest, ProtocolExecutionOutcome.FailedBeforeMutation, unchanged, new(1, 0, 0, 0, 0, 0), "Failed.", "No mutation.", null)
            .Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*exact typed outcome counts*");

        ProtocolTerminalState rolledBack = new(ProtocolDurableState.RolledBack, ProtocolTerminalErrorCode.IoFailure, ProtocolRecoveryDisposition.Completed, ProtocolNextAction.InspectAgain);
        new RolledBackFailureEvent(session, plan, digest, ProtocolExecutionOutcome.FailedAndRolledBack, rolledBack, new(1, 1, 1, 0, 0, 0), "Failed.", "Rolled back.", null)
            .Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*exact typed outcome counts*");

        ProtocolTerminalState recovered = new(ProtocolDurableState.RecoveryCompleted, ProtocolTerminalErrorCode.PathChanged, ProtocolRecoveryDisposition.Completed, ProtocolNextAction.InspectAgain);
        new RolledBackFailureEvent(session, plan, digest, ProtocolExecutionOutcome.AutomaticRecoveryCompletedFreshInspectionRequired, recovered, new(0, 0, 0, 0, 0, 0), "Recovered.", "Inspect again.", null)
            .Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*exact typed outcome counts*");

        ProtocolTerminalState committed = new(ProtocolDurableState.Committed, null, ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.InspectAgain);
        new SuccessEvent(session, plan, digest, InstallerOperation.Repair, ProtocolExecutionOutcome.Succeeded, committed, new(TransactionPlan.MaximumOperationCount, 0, TransactionPlan.MaximumOperationCount, 0, 0, 0), "Committed.", null)
            .Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*outside their bounds*");
    }

    [Test]
    public void InterruptedRecoveryAttemptBindsRootGenerationAndOutcomeExactly()
    {
        ProtocolSessionId session = ProtocolSessionId.CreateRandom();
        ProtocolTerminalState complete = new(ProtocolDurableState.RecoveryCompleted, null, ProtocolRecoveryDisposition.Completed, ProtocolNextAction.InspectAgain);
        ProtocolInterruptedRecoveryAttempt rootMismatch = new(new("/game", 1, 2, 3, 7), 7, 8, true, []);
        new RecoveryCompletedEvent(session, ProtocolInterruptedRecoveryOutcome.RecoveryCompleted, complete, rootMismatch, "Recovered.", null)
            .Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*root or generation identity*");

        ProtocolInterruptedRecoveryAttempt regressed = new(new("/game", 1, 2, 3, 6), 7, 6, true, []);
        new RecoveryCompletedEvent(session, ProtocolInterruptedRecoveryOutcome.RecoveryCompleted, complete, regressed, "Recovered.", null)
            .Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*root or generation identity*");

        ProtocolInterruptedRecoveryAttempt unknownSelection = new(new("/game", 1, 2, 3, 8), 7, 8, null, []);
        new RecoveryCompletedEvent(session, ProtocolInterruptedRecoveryOutcome.RecoveryCompleted, complete, unknownSelection, "Recovered.", null)
            .Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*selected root*");

        RecoveryFailureEvent cancelled = new(session, ProtocolInterruptedRecoveryOutcome.CancelledBeforeRecovery, new(ProtocolDurableState.Unchanged, null, ProtocolRecoveryDisposition.InterruptedRecoveryRequired, ProtocolNextAction.RecoverInterrupted), "Cancelled.", null);
        ProtocolJsonSerializer.SerializeLine(cancelled).Should().NotBeEmpty(); cancelled.RequiresRecovery.Should().BeTrue();
        (cancelled with { TerminalState = cancelled.TerminalState with { RecoveryDisposition = ProtocolRecoveryDisposition.NotRequired } })
            .Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*exact typed outcome table*");
    }

    [Test]
    public void TerminalProseIsMutableDisplayTextButTypedAuthorityIsStable()
    {
        ProtocolSessionId session = ProtocolSessionId.CreateRandom(); ProtocolPlanId plan = ProtocolPlanId.CreateRandom(); ProtocolPlanDigest digest = ProtocolPlanDigest.Parse(HashA);
        SuccessEvent original = new(session, plan, digest, InstallerOperation.Uninstall, ProtocolExecutionOutcome.Succeeded, new(ProtocolDurableState.Committed, null, ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.InspectAgain), new(1, 0, 0, 0, 0, 0), "One bounded explanation.", null);
        SuccessEvent reworded = original with { Summary = "Different bounded display prose." };
        ProtocolJsonSerializer.SerializeLine(original).Should().NotBeEmpty(); ProtocolJsonSerializer.SerializeLine(reworded).Should().NotBeEmpty();
        reworded.Outcome.Should().Be(original.Outcome); reworded.TerminalState.Should().Be(original.TerminalState); reworded.ExecutionSummary.Should().Be(original.ExecutionSummary);
    }

    [Test]
    public void TerminalJsonRejectsNestedUnknownFieldsAndNoncanonicalTypedEnums()
    {
        SuccessEvent terminal = new(ProtocolSessionId.CreateRandom(), ProtocolPlanId.CreateRandom(), ProtocolPlanDigest.Parse(HashA), InstallerOperation.Uninstall, ProtocolExecutionOutcome.Succeeded, new(ProtocolDurableState.Committed, null, ProtocolRecoveryDisposition.NotRequired, ProtocolNextAction.InspectAgain), new(1, 0, 0, 0, 0, 0), "Done.", null);
        string line = ProtocolJsonSerializer.SerializeLine(terminal);
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeEventLine(line.Replace("\"durableState\":", "\"unknown\":true,\"durableState\":", StringComparison.Ordinal))).Should().Throw<ProtocolException>().WithMessage("*unknown 'unknown'*");
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeEventLine(line.Replace("\"durableState\":\"committed\"", "\"durableState\":0", StringComparison.Ordinal))).Should().Throw<ProtocolException>().WithMessage("*canonical camel case*");
    }

    [Test]
    public void PlanPages_RejectFieldByFieldStructuredMutations()
    {
        PlanEvent plan = CreatePlan();
        ProtocolPlanOperation badOperation = plan.Operations[0] with { ResultSha256 = null };
        new PlanPageEvent(plan.SessionId, plan.PlanId, plan.PlanDigest, ProtocolPlanPageKind.Operations, 0, 1, null, [badOperation], [], [], [])
            .Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*hashes are inconsistent*");
        new PlanPageEvent(plan.SessionId, plan.PlanId, plan.PlanDigest, ProtocolPlanPageKind.Warnings, 1, 1, null, [], [], [], ["out of bounds"])
            .Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*page bounds*");
    }

    [Test]
    public void UnsafePathsUndefinedEnumsAndNoOpPrunesAreRejected()
    {
        ProtocolSessionId session = ProtocolSessionId.CreateRandom();
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new ListRecoveriesRequest(session, "/game/../other"))).Should().Throw<ProtocolException>().WithMessage("*canonical absolute Linux path*");
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new GameDiscoveryEvent(session, [new("/game", (LinuxGameFolderStatus)999, "Game")]))).Should().Throw<ProtocolException>().WithMessage("*isn't defined*");
        PlanEvent plan = CreatePlan();
        Func<ProtocolPlanCandidate, PlanPageEvent> candidatePage = candidate => new(plan.SessionId, plan.PlanId, plan.PlanDigest, ProtocolPlanPageKind.Candidates, 0, 1, null, [], [], [candidate], []);
        candidatePage(plan.Candidates[0] with { ProposedResultSha256 = null }).Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*Only removal candidates*");
        candidatePage(plan.Candidates[0] with { Reason = FileReplacementCandidateReason.ModifiedReceiptOwned, Disposition = FileReplacementCandidateDisposition.Remove }).Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*Only removal candidates*");
        candidatePage(plan.Candidates[0] with { Reason = (FileReplacementCandidateReason)999 }).Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*isn't defined*");
        ProtocolPlanCandidate impossible = plan.Candidates[0] with { Reason = FileReplacementCandidateReason.OfficialLauncherBackup };
        candidatePage(impossible).Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*core-defined pair*");
        ProtocolPlanCandidate misleadingRetain = plan.Candidates[0] with { Reason = FileReplacementCandidateReason.OfficialLauncherBackup, Disposition = FileReplacementCandidateDisposition.TrustRetained };
        candidatePage(misleadingRetain).Invoking(ProtocolJsonSerializer.SerializeLine).Should().Throw<ProtocolException>().WithMessage("*exact observed digest*");
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

    private static string RemoveCommandId(string line, ProtocolCommandId commandId) =>
        line.Replace($"\"commandId\":\"{commandId.Value}\",", "", StringComparison.Ordinal);
}
