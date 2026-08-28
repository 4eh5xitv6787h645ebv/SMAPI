using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Protocol.V1;

namespace StardewModdingAPI.Installer.Core.Tests.Protocol.V1;

/// <summary>Contract tests for the strict deterministic version 1 JSONL serializer.</summary>
[TestFixture]
internal sealed class ProtocolJsonSerializerTests
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly ProtocolPlanDigest ExecutionBindingDigest = ProtocolPlanDigest.Parse("dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd");
    private static readonly ProtocolGameRootIdentity GameRoot = new("/game", 10, 20);

    [Test]
    public void SerializeLine_UsesDeterministicEnvelopeAndPropertyOrder()
    {
        ProtocolSessionId sessionId = ProtocolSessionId.Parse("0123456789abcdef0123456789abcdef");
        InspectPlanRequest request = new(sessionId, "/games/Stardew Valley", InstallerOperation.Repair, "4.5.3-alpha.2");

        string first = ProtocolJsonSerializer.SerializeLine(request);
        string second = ProtocolJsonSerializer.SerializeLine(request);

        first.Should().Be(second).And.Be(
            "{\"protocolVersion\":1,\"messageType\":\"inspect-plan.request\",\"payload\":{"
            + "\"sessionId\":\"0123456789abcdef0123456789abcdef\","
            + "\"gamePath\":\"/games/Stardew Valley\","
            + "\"operation\":\"repair\","
            + "\"targetPackageVersion\":\"4.5.3-alpha.2\"}}"
        );
        first.Should().NotContain("\n").And.NotContain("\r");
    }

    [Test]
    public void RoundTrip_AcceptsEveryRequestAndEventKind()
    {
        ProtocolSessionId sessionId = ProtocolSessionId.Parse("0123456789abcdef0123456789abcdef");
        ProtocolPlanId planId = ProtocolPlanId.Parse("fedcba9876543210fedcba9876543210");
        ProtocolPlanOperation[] operations = CreateOperations();
        ProtocolPlanConflict[] conflicts = [];
        ProtocolPlanDigest digest = ComputeDigest(InstallerOperation.Update, ObservedInstallState.KnownModified, operations, conflicts, CreateRelease(), CreateRelease());
        ProtocolRequest[] requests =
        [
            new HandshakeRequest("smapi-gui", "1.0.0"),
            new InspectPlanRequest(sessionId, "/game", InstallerOperation.Install, "4.5.3-alpha.2"),
            new ConfirmPlanRequest(sessionId, planId, digest),
            new ExecutePlanRequest(sessionId, planId, digest),
            new CancelPlanRequest(sessionId, planId, digest)
        ];
        ProtocolEvent[] events =
        [
            new HandshakeEvent(sessionId, "4.5.3-alpha.2", ["inspect-plan", "transaction-v1"]),
            new PlanEvent(sessionId, planId, digest, ExecutionBindingDigest, InstallerOperation.Update, GameRoot, CreateRelease(), CreateRelease(), ObservedInstallState.KnownModified, operations, conflicts, "Update two files.", ["One managed file was modified."], true),
            new ProgressEvent(sessionId, planId, digest, 0, InstallerProgressStage.BackingUp, 1, 2, "Backing up managed files."),
            new SuccessEvent(sessionId, planId, digest, InstallerOperation.Update, "Update verified."),
            new RolledBackFailureEvent(sessionId, planId, digest, "copy-failed", "Copy failed.", "Original files restored."),
            new RecoverableInterruptionEvent(sessionId, planId, digest, "power-loss", "Execution was interrupted.", InstallerRecoveryAction.InspectAgain, "The durable journal can be inspected."),
            new CancelledEvent(sessionId, planId, digest, "Cancelled.", "No incomplete transaction remains.")
        ];

        foreach (ProtocolRequest expected in requests)
        {
            ProtocolRequest actual = ProtocolJsonSerializer.DeserializeRequestLine(ProtocolJsonSerializer.SerializeLine(expected));
            actual.GetType().Should().Be(expected.GetType());
            ProtocolJsonSerializer.SerializeLine(actual).Should().Be(ProtocolJsonSerializer.SerializeLine(expected));
            actual.Kind.Should().Be(expected.Kind);
        }
        foreach (ProtocolEvent expected in events)
        {
            ProtocolEvent actual = ProtocolJsonSerializer.DeserializeEventLine(ProtocolJsonSerializer.SerializeLine(expected));
            actual.GetType().Should().Be(expected.GetType());
            ProtocolJsonSerializer.SerializeLine(actual).Should().Be(ProtocolJsonSerializer.SerializeLine(expected));
            actual.Kind.Should().Be(expected.Kind);
        }
    }

    [Test]
    public void SerializePlan_WritesStructuredIdentityOperationsAndConflictsInStableOrder()
    {
        ProtocolSessionId sessionId = ProtocolSessionId.Parse("0123456789abcdef0123456789abcdef");
        ProtocolPlanId planId = ProtocolPlanId.Parse("fedcba9876543210fedcba9876543210");
        ProtocolPlanOperation[] operations = CreateOperations();
        ProtocolPlanConflict[] conflicts = [];
        ProtocolPlanDigest digest = ComputeDigest(InstallerOperation.Update, ObservedInstallState.KnownModified, operations, conflicts, CreateRelease(), CreateRelease());
        PlanEvent plan = new(sessionId, planId, digest, ExecutionBindingDigest, InstallerOperation.Update, GameRoot, CreateRelease(), CreateRelease(), ObservedInstallState.KnownModified, operations, conflicts, "Update.", [], true);

        string json = ProtocolJsonSerializer.SerializeLine(plan);

        json.Should().Contain(
            $"\"planDigest\":\"{digest.Value}\",\"executionBindingDigest\":\"{ExecutionBindingDigest.Value}\",\"operation\":\"update\","
            + "\"gameRoot\":{\"canonicalPath\":\"/game\",\"deviceId\":10,\"inode\":20},\"currentRelease\":{\"repository\":\"https://github.com/4eh5xitv6787h645ebv/SMAPI\","
        );
        json.Should().Contain(
            $"\"operations\":[{{\"kind\":\"create\",\"path\":\"smapi-internal/a.dll\",\"expectedCurrentSha256\":null,\"resultSha256\":\"{HashA}\"}},"
            + $"{{\"kind\":\"replace\",\"path\":\"smapi-internal/b.dll\",\"expectedCurrentSha256\":\"{HashA}\",\"resultSha256\":\"{HashB}\"}}],\"conflicts\":[]"
        );
    }

    [Test]
    public void PlanDigest_BindsExecutionStateGameRootReleasesObservedStateAndDetails()
    {
        ProtocolPlanOperation[] operations = CreateOperations();
        ProtocolPlanConflict[] conflicts = [];
        ProtocolReleaseIdentity current = CreateRelease() with { SourceCommit = "3333333333333333333333333333333333333333" };
        ProtocolReleaseIdentity target = CreateRelease();
        ProtocolPlanDigest digest = ComputeDigest(InstallerOperation.Update, ObservedInstallState.KnownModified, operations, conflicts, current, target);
        ProtocolSessionId sessionId = ProtocolSessionId.CreateRandom();
        ProtocolPlanId planId = ProtocolPlanId.CreateRandom();

        AssertDigestMismatch(ProtocolPlanDigest.Parse(HashB), GameRoot, current, target, ObservedInstallState.KnownModified, operations);
        AssertDigestMismatch(ExecutionBindingDigest, GameRoot with { Inode = 21 }, current, target, ObservedInstallState.KnownModified, operations);
        AssertDigestMismatch(ExecutionBindingDigest, GameRoot, current with { SourceTree = "4444444444444444444444444444444444444444" }, target, ObservedInstallState.KnownModified, operations);
        AssertDigestMismatch(ExecutionBindingDigest, GameRoot, current, target with { PackageSha256 = HashB }, ObservedInstallState.KnownModified, operations);
        AssertDigestMismatch(ExecutionBindingDigest, GameRoot, current, target, ObservedInstallState.KnownUnmodified, operations);
        AssertDigestMismatch(ExecutionBindingDigest, GameRoot, current, target, ObservedInstallState.KnownModified, [operations[0], operations[1] with { ResultSha256 = HashA }]);

        void AssertDigestMismatch(
            ProtocolPlanDigest executionBinding,
            ProtocolGameRootIdentity gameRoot,
            ProtocolReleaseIdentity? currentRelease,
            ProtocolReleaseIdentity? targetRelease,
            ObservedInstallState state,
            ProtocolPlanOperation[] displayedOperations
        )
        {
            PlanEvent altered = new(
                sessionId,
                planId,
                digest,
                executionBinding,
                InstallerOperation.Update,
                gameRoot,
                currentRelease,
                targetRelease,
                state,
                displayedOperations,
                conflicts,
                "Update.",
                [],
                true
            );
            FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(altered)).Should()
                .Throw<ProtocolException>().WithMessage("*digest doesn't match*");
        }
    }

    [Test]
    public void TargetRelease_IsNullableOnlyForOperationsWhichDoNotRequireOne()
    {
        ProtocolSessionId sessionId = ProtocolSessionId.CreateRandom();
        ProtocolPlanId planId = ProtocolPlanId.CreateRandom();
        ProtocolPlanOperation[] operations = [new(PlanOperationKind.Create, "backup/file", null, HashA)];
        ProtocolPlanDigest backupDigest = ComputeDigest(InstallerOperation.Backup, ObservedInstallState.KnownUnmodified, operations, [], CreateRelease(), null);
        PlanEvent backup = new(sessionId, planId, backupDigest, ExecutionBindingDigest, InstallerOperation.Backup, GameRoot, CreateRelease(), null, ObservedInstallState.KnownUnmodified, operations, [], "Backup.", [], true);

        ProtocolJsonSerializer.SerializeLine(new InspectPlanRequest(sessionId, "/game", InstallerOperation.Backup, null)).Should().Contain("\"targetPackageVersion\":null");
        ProtocolJsonSerializer.SerializeLine(backup).Should().Contain("\"targetRelease\":null");
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(new InspectPlanRequest(sessionId, "/game", InstallerOperation.Backup, "invented"))).Should()
            .Throw<ProtocolException>().WithMessage("*must not invent a target package version*");

        ProtocolPlanDigest invalidInstallDigest = ComputeDigest(InstallerOperation.Install, ObservedInstallState.NotInstalled, operations, [], null, null);
        PlanEvent invalidInstall = new(sessionId, planId, invalidInstallDigest, ExecutionBindingDigest, InstallerOperation.Install, GameRoot, null, null, ObservedInstallState.NotInstalled, operations, [], "Install.", [], true);
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(invalidInstall)).Should()
            .Throw<ProtocolException>().WithMessage("*requires an exact target release identity*");
    }

    [Test]
    public void PlanEvent_DefensivelySnapshotsStructuredCollections()
    {
        ProtocolPlanOperation[] operations = CreateOperations();
        ProtocolPlanConflict[] conflicts = [];
        string[] warnings = ["Initial warning."];
        ProtocolPlanDigest digest = ComputeDigest(InstallerOperation.Update, ObservedInstallState.KnownModified, operations, conflicts, CreateRelease(), CreateRelease());
        PlanEvent plan = new(ProtocolSessionId.CreateRandom(), ProtocolPlanId.CreateRandom(), digest, ExecutionBindingDigest, InstallerOperation.Update, GameRoot, CreateRelease(), CreateRelease(), ObservedInstallState.KnownModified, operations, conflicts, "Update.", warnings, true);

        operations[0] = operations[0] with { Path = "tampered" };
        warnings[0] = "Tampered.";
        ProtocolPlanOperation[] returned = plan.Operations;
        returned[0] = returned[0] with { Path = "also-tampered" };

        plan.Operations[0].Path.Should().Be("smapi-internal/a.dll");
        plan.Warnings.Should().Equal("Initial warning.");
        ProtocolJsonSerializer.SerializeLine(plan).Should().Contain("smapi-internal/a.dll");
    }

    [TestCase("{\"protocolVersion\":2,\"messageType\":\"handshake.request\",\"payload\":{\"clientName\":\"gui\",\"clientVersion\":\"1\"}}", "Unsupported protocol version")]
    [TestCase("{\"protocolVersion\":1,\"messageType\":\"future.request\",\"payload\":{}}", "Unknown version 1 protocol message type")]
    [TestCase("{\"protocolVersion\":1,\"messageType\":\"handshake.request\",\"payload\":{\"clientName\":\"gui\",\"clientVersion\":\"1\",\"extra\":true}}", "unknown 'extra' property")]
    [TestCase("{\"protocolVersion\":1,\"messageType\":\"handshake.request\",\"payload\":{\"clientName\":\"gui\"}}", "missing the required 'clientVersion' property")]
    [TestCase("{\"protocolVersion\":1,\"protocolVersion\":1,\"messageType\":\"handshake.request\",\"payload\":{\"clientName\":\"gui\",\"clientVersion\":\"1\"}}", "duplicate 'protocolVersion' property")]
    [TestCase("{\"protocolVersion\":1,\"messageType\":\"inspect-plan.request\",\"payload\":{\"sessionId\":\"0123456789abcdef0123456789abcdef\",\"gamePath\":\"/game\",\"operation\":0,\"targetPackageVersion\":\"1\"}}", "isn't valid strict version 1 JSON")]
    public void DeserializeRequestLine_RejectsNonContractInput(string line, string expectedMessage)
    {
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeRequestLine(line)).Should()
            .Throw<ProtocolException>()
            .WithMessage($"*{expectedMessage}*");
    }

    [Test]
    public void DeserializePlan_RejectsUnknownDuplicateAndMissingNestedProperties()
    {
        string valid = SerializePlanWithConflict();
        string duplicateRelease = valid.Replace(
            "\"repository\":\"https://github.com/4eh5xitv6787h645ebv/SMAPI\",",
            "\"repository\":\"https://github.com/4eh5xitv6787h645ebv/SMAPI\",\"repository\":\"https://example.invalid/repo\",",
            StringComparison.Ordinal
        );
        string unknownOperation = valid.Replace("\"kind\":\"replace\",", "\"kind\":\"replace\",\"extra\":true,", StringComparison.Ordinal);
        string missingConflictPath = valid.Replace(",\"path\":\"smapi-internal/b.dll\"}],\"summary\"", "}],\"summary\"", StringComparison.Ordinal);

        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeEventLine(duplicateRelease)).Should()
            .Throw<ProtocolException>().WithMessage("*duplicate 'repository' property*");
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeEventLine(unknownOperation)).Should()
            .Throw<ProtocolException>().WithMessage("*unknown 'extra' property*");
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeEventLine(missingConflictPath)).Should()
            .Throw<ProtocolException>().WithMessage("*missing the required 'path' property*");
    }

    [Test]
    public void DeserializeLine_RejectsWrongDirection()
    {
        ProtocolSessionId sessionId = ProtocolSessionId.CreateRandom();
        string eventLine = ProtocolJsonSerializer.SerializeLine(new HandshakeEvent(sessionId, "1.0.0", []));
        string requestLine = ProtocolJsonSerializer.SerializeLine(new HandshakeRequest("gui", "1.0.0"));

        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeRequestLine(eventLine)).Should()
            .Throw<ProtocolException>().WithMessage("*event can't be accepted as a request*");
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeEventLine(requestLine)).Should()
            .Throw<ProtocolException>().WithMessage("*request can't be accepted as an event*");
    }

    [Test]
    public void DeserializeLine_EnforcesLineAndDepthBounds()
    {
        string oversized = new('x', ProtocolJsonSerializer.MaxLineBytes + 1);
        string nested = "{\"protocolVersion\":1,\"messageType\":\"handshake.request\",\"payload\":" + new string('[', ProtocolJsonSerializer.MaxDepth + 1) + "null" + new string(']', ProtocolJsonSerializer.MaxDepth + 1) + "}";

        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeRequestLine(oversized)).Should()
            .Throw<ProtocolException>().WithMessage("*exceeds*");
        FluentActions.Invoking(() => ProtocolJsonSerializer.DeserializeRequestLine(nested)).Should()
            .Throw<ProtocolException>().WithMessage("*isn't valid strict version 1 JSON*");
    }

    [Test]
    public void SerializeLine_EnforcesSizeDigestAndSemanticBounds()
    {
        HandshakeRequest oversized = new(new string('x', ProtocolJsonSerializer.MaxLineBytes), "1");
        ProtocolPlanDigest digest = ProtocolPlanDigest.Parse(HashA);
        ProgressEvent inconsistent = new(
            ProtocolSessionId.CreateRandom(),
            ProtocolPlanId.CreateRandom(),
            digest,
            0,
            InstallerProgressStage.BackingUp,
            2,
            1,
            "invalid"
        );

        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(oversized)).Should().Throw<ProtocolException>();
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(inconsistent)).Should()
            .Throw<ProtocolException>().WithMessage("*progress counters are inconsistent*");

        ProtocolPlanOperation[] operations = CreateOperations();
        PlanEvent digestMismatch = new(
            inconsistent.SessionId,
            inconsistent.PlanId,
            digest,
            ExecutionBindingDigest,
            InstallerOperation.Install,
            GameRoot,
            null,
            CreateRelease(),
            ObservedInstallState.NotInstalled,
            operations,
            [],
            "Install.",
            [],
            requiresConfirmation: true
        );
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(digestMismatch)).Should()
            .Throw<ProtocolException>().WithMessage("*digest doesn't match*");
    }

    [Test]
    public void SerializePlan_RejectsUnsafeOrNoncanonicalStructuredValues()
    {
        ProtocolSessionId sessionId = ProtocolSessionId.CreateRandom();
        ProtocolPlanId planId = ProtocolPlanId.CreateRandom();
        ProtocolReleaseIdentity release = CreateRelease();

        AssertInvalidPlan([new(PlanOperationKind.Create, "../escape", null, HashA)], [], release, "*canonical relative path*");
        AssertInvalidPlan([new(PlanOperationKind.Create, "b", null, HashA), new(PlanOperationKind.Create, "a", null, HashB)], [], release, "*canonical order*");
        AssertInvalidPlan([new(PlanOperationKind.Create, "a", HashA, HashB)], [], release, "*hashes are inconsistent*");
        AssertInvalidPlan([new(PlanOperationKind.Create, "a", null, HashA)], [new(PlanConflictCode.ModifiedOwnedFile, "b"), new(PlanConflictCode.UnknownCollision, "a")], release, "*canonical order*");
        AssertInvalidPlan(
            [new(PlanOperationKind.Create, "a", null, HashA)],
            [],
            release with { SourceCommit = "A" + release.SourceCommit[1..] },
            "*isn't canonical lowercase hexadecimal*"
        );

        void AssertInvalidPlan(
            ProtocolPlanOperation[] operations,
            ProtocolPlanConflict[] conflicts,
            ProtocolReleaseIdentity identity,
            string expected
        )
        {
            ProtocolPlanDigest candidate = ComputeDigest(InstallerOperation.Install, ObservedInstallState.NotInstalled, operations, conflicts, null, identity);
            PlanEvent plan = new(sessionId, planId, candidate, ExecutionBindingDigest, InstallerOperation.Install, GameRoot, null, identity, ObservedInstallState.NotInstalled, operations, conflicts, "Install.", [], true);
            FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(plan)).Should().Throw<ProtocolException>().WithMessage(expected);
        }
    }

    [Test]
    public void PlanDigest_IsCanonicalStableAndRejectsNoncanonicalValues()
    {
        ProtocolPlanOperation[] operations = CreateOperations();
        ProtocolPlanDigest first = ComputeDigest(InstallerOperation.Update, ObservedInstallState.KnownModified, operations, [], CreateRelease(), CreateRelease());
        ProtocolPlanDigest second = ComputeDigest(InstallerOperation.Update, ObservedInstallState.KnownModified, operations, [], CreateRelease(), CreateRelease());

        first.Should().Be(second);
        first.Value.Should().MatchRegex("^[0-9a-f]{64}$");
        FluentActions.Invoking(() => ProtocolPlanDigest.Parse(first.Value.ToUpperInvariant())).Should().Throw<ProtocolException>();
        FluentActions.Invoking(() => ProtocolPlanDigest.Parse(first.Value[..63])).Should().Throw<ProtocolException>();
    }

    [Test]
    public void Identifiers_AreRandomCanonicalAndRejectInvalidValues()
    {
        ProtocolSessionId firstSession = ProtocolSessionId.CreateRandom();
        ProtocolSessionId secondSession = ProtocolSessionId.CreateRandom();
        ProtocolPlanId firstPlan = ProtocolPlanId.CreateRandom();
        ProtocolPlanId secondPlan = ProtocolPlanId.CreateRandom();

        firstSession.Value.Should().MatchRegex("^[0-9a-f]{32}$").And.NotBe(secondSession.Value);
        firstPlan.Value.Should().MatchRegex("^[0-9a-f]{32}$").And.NotBe(secondPlan.Value);
        FluentActions.Invoking(() => ProtocolSessionId.Parse("00000000000000000000000000000000")).Should().Throw<ProtocolException>();
        FluentActions.Invoking(() => ProtocolPlanId.Parse("0123456789ABCDEF0123456789ABCDEF")).Should().Throw<ProtocolException>();
    }

    private static string SerializePlanWithConflict()
    {
        ProtocolPlanOperation[] operations = [new(PlanOperationKind.Replace, "smapi-internal/a.dll", HashA, HashB)];
        ProtocolPlanConflict[] conflicts = [new(PlanConflictCode.ModifiedOwnedFile, "smapi-internal/b.dll")];
        ProtocolPlanDigest digest = ComputeDigest(InstallerOperation.Update, ObservedInstallState.KnownModified, operations, conflicts, CreateRelease(), CreateRelease());
        return ProtocolJsonSerializer.SerializeLine(new PlanEvent(
            ProtocolSessionId.Parse("0123456789abcdef0123456789abcdef"),
            ProtocolPlanId.Parse("fedcba9876543210fedcba9876543210"),
            digest,
            ExecutionBindingDigest,
            InstallerOperation.Update,
            GameRoot,
            CreateRelease(),
            CreateRelease(),
            ObservedInstallState.KnownModified,
            operations,
            conflicts,
            "Blocked update.",
            [],
            true
        ));
    }

    private static ProtocolPlanOperation[] CreateOperations()
    {
        return
        [
            new ProtocolPlanOperation(PlanOperationKind.Create, "smapi-internal/a.dll", null, HashA),
            new ProtocolPlanOperation(PlanOperationKind.Replace, "smapi-internal/b.dll", HashA, HashB)
        ];
    }

    private static ProtocolReleaseIdentity CreateRelease()
    {
        return new ProtocolReleaseIdentity(
            "https://github.com/4eh5xitv6787h645ebv/SMAPI",
            "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2",
            "4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2",
            "SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2-linux-x64-installer.zip",
            "1111111111111111111111111111111111111111",
            "2222222222222222222222222222222222222222",
            HashA,
            123456,
            "4eh5xitv6787h645ebv/SMAPI/.github/workflows/linux-alpha-release.yml@refs/tags/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2",
            "Release",
            "linux-x64"
        );
    }

    private static ProtocolPlanDigest ComputeDigest(
        InstallerOperation operation,
        ObservedInstallState observedState,
        ProtocolPlanOperation[] operations,
        ProtocolPlanConflict[] conflicts,
        ProtocolReleaseIdentity? currentRelease,
        ProtocolReleaseIdentity? targetRelease
    )
    {
        return ProtocolPlanDigest.Compute(
            ExecutionBindingDigest,
            operation,
            GameRoot,
            currentRelease,
            targetRelease,
            observedState,
            operations,
            conflicts
        );
    }
}
