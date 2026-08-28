using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Protocol.V1;

namespace StardewModdingAPI.Installer.Core.Tests.Protocol.V1;

/// <summary>Contract tests for the strict deterministic version 1 JSONL serializer.</summary>
[TestFixture]
internal sealed class ProtocolJsonSerializerTests
{
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
        ProtocolRequest[] requests =
        [
            new HandshakeRequest("smapi-gui", "1.0.0"),
            new InspectPlanRequest(sessionId, "/game", InstallerOperation.Install, "4.5.3-alpha.2"),
            new ConfirmPlanRequest(sessionId, planId),
            new ExecutePlanRequest(sessionId, planId),
            new CancelPlanRequest(sessionId, planId)
        ];
        ProtocolEvent[] events =
        [
            new HandshakeEvent(sessionId, "4.5.3-alpha.2", ["inspect-plan", "transaction-v1"]),
            new PlanEvent(sessionId, planId, InstallerOperation.Update, "/game", ObservedInstallState.KnownModified, "Update two files.", ["One managed file was modified."], true),
            new ProgressEvent(sessionId, planId, 0, InstallerProgressStage.BackingUp, 1, 2, "Backing up managed files."),
            new SuccessEvent(sessionId, planId, InstallerOperation.Update, "Update verified."),
            new RolledBackFailureEvent(sessionId, planId, "copy-failed", "Copy failed.", "Original files restored."),
            new RecoverableInterruptionEvent(sessionId, planId, "power-loss", "Execution was interrupted.", InstallerRecoveryAction.InspectAgain, "The durable journal can be inspected.")
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
    public void SerializeLine_EnforcesSizeAndSemanticBounds()
    {
        HandshakeRequest oversized = new(new string('x', ProtocolJsonSerializer.MaxLineBytes), "1");
        ProgressEvent inconsistent = new(
            ProtocolSessionId.CreateRandom(),
            ProtocolPlanId.CreateRandom(),
            0,
            InstallerProgressStage.BackingUp,
            2,
            1,
            "invalid"
        );

        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(oversized)).Should()
            .Throw<ProtocolException>();
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(inconsistent)).Should()
            .Throw<ProtocolException>().WithMessage("*progress counters are inconsistent*");

        PlanEvent unconfirmed = new(
            inconsistent.SessionId,
            inconsistent.PlanId,
            InstallerOperation.Install,
            "/game",
            ObservedInstallState.NotInstalled,
            "Install.",
            [],
            RequiresConfirmation: false
        );
        FluentActions.Invoking(() => ProtocolJsonSerializer.SerializeLine(unconfirmed)).Should()
            .Throw<ProtocolException>().WithMessage("*must require explicit confirmation*");
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
}
