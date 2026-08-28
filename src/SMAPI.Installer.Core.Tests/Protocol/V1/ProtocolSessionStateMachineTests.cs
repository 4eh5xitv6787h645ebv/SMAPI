using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Protocol.V1;

namespace StardewModdingAPI.Installer.Core.Tests.Protocol.V1;

/// <summary>Tests exact ID binding and transition validation for a one-shot backend session.</summary>
[TestFixture]
internal sealed class ProtocolSessionStateMachineTests
{
    [Test]
    public void Session_HappyPathRequiresHandshakePlanConfirmationAndExecution()
    {
        ProtocolSessionStateMachine machine = new();

        HandshakeEvent handshake = machine.AcceptHandshake(new HandshakeRequest("gui", "1.0.0"), "4.5.3-alpha.2", "inspect-plan");
        PlanEvent plan = machine.IssuePlan(
            new InspectPlanRequest(handshake.SessionId, "/game", InstallerOperation.Install, "4.5.3-alpha.2"),
            ObservedInstallState.NotInstalled,
            "Install SMAPI.",
            []
        );
        machine.ConfirmPlan(new ConfirmPlanRequest(handshake.SessionId, plan.PlanId));
        machine.BeginExecution(new ExecutePlanRequest(handshake.SessionId, plan.PlanId));
        machine.RecordProgress(new ProgressEvent(handshake.SessionId, plan.PlanId, 0, InstallerProgressStage.BackingUp, 0, 1, "Starting backup."));
        machine.RecordProgress(new ProgressEvent(handshake.SessionId, plan.PlanId, 1, InstallerProgressStage.Finalizing, 1, 1, "Verifying."));
        machine.Complete(new SuccessEvent(handshake.SessionId, plan.PlanId, InstallerOperation.Install, "Installed and verified."));

        handshake.SessionId.Should().Be(machine.SessionId);
        machine.CurrentPlanId.Should().Be(plan.PlanId);
        machine.LastProgressSequence.Should().Be(1);
        machine.State.Should().Be(ProtocolSessionState.Completed);
    }

    [Test]
    public void BeginExecution_BeforeMatchingConfirmationIsRejected()
    {
        (ProtocolSessionStateMachine machine, PlanEvent plan) = CreatePlannedMachine();

        FluentActions.Invoking(() => machine.BeginExecution(new ExecutePlanRequest(machine.SessionId, plan.PlanId))).Should()
            .Throw<ProtocolException>().WithMessage("*must be confirmed before execution*");
        machine.State.Should().Be(ProtocolSessionState.PlanIssued);
    }

    [Test]
    public void Replanning_InvalidatesOldPlanForConfirmationExecutionAndCancellation()
    {
        (ProtocolSessionStateMachine machine, PlanEvent stalePlan) = CreatePlannedMachine();
        PlanEvent currentPlan = machine.IssuePlan(
            new InspectPlanRequest(machine.SessionId, "/game", InstallerOperation.Repair, "4.5.3-alpha.2"),
            ObservedInstallState.KnownModified,
            "Repair SMAPI.",
            ["One managed file changed."]
        );

        currentPlan.PlanId.Should().NotBe(stalePlan.PlanId);
        FluentActions.Invoking(() => machine.ConfirmPlan(new ConfirmPlanRequest(machine.SessionId, stalePlan.PlanId))).Should()
            .Throw<ProtocolException>().WithMessage("*may be stale*");
        FluentActions.Invoking(() => machine.RequestCancellation(new CancelPlanRequest(machine.SessionId, stalePlan.PlanId))).Should()
            .Throw<ProtocolException>().WithMessage("*may be stale*");

        machine.ConfirmPlan(new ConfirmPlanRequest(machine.SessionId, currentPlan.PlanId));
        FluentActions.Invoking(() => machine.BeginExecution(new ExecutePlanRequest(machine.SessionId, stalePlan.PlanId))).Should()
            .Throw<ProtocolException>().WithMessage("*may be stale*");
        machine.State.Should().Be(ProtocolSessionState.PlanConfirmed);
    }

    [Test]
    public void Requests_WithAnotherSessionAreRejected()
    {
        ProtocolSessionStateMachine machine = new();
        machine.AcceptHandshake(new HandshakeRequest("gui", "1"), "server");
        ProtocolSessionId otherSession = ProtocolSessionId.CreateRandom();

        FluentActions.Invoking(() => machine.IssuePlan(
            new InspectPlanRequest(otherSession, "/game", InstallerOperation.Install, "1"),
            ObservedInstallState.NotInstalled,
            "Install.",
            []
        )).Should().Throw<ProtocolException>().WithMessage("*doesn't match this process session ID*");
    }

    [Test]
    public void Cancellation_RequiresCurrentIdsAndAllowsTerminalRecoveryEvent()
    {
        (ProtocolSessionStateMachine machine, PlanEvent plan) = CreatePlannedMachine();
        machine.ConfirmPlan(new ConfirmPlanRequest(machine.SessionId, plan.PlanId));
        machine.BeginExecution(new ExecutePlanRequest(machine.SessionId, plan.PlanId));

        ProtocolPlanId wrongPlan = ProtocolPlanId.CreateRandom();
        FluentActions.Invoking(() => machine.RequestCancellation(new CancelPlanRequest(machine.SessionId, wrongPlan))).Should()
            .Throw<ProtocolException>().WithMessage("*may be stale*");

        machine.RequestCancellation(new CancelPlanRequest(machine.SessionId, plan.PlanId));
        machine.State.Should().Be(ProtocolSessionState.CancellationRequested);
        machine.Complete(new RecoverableInterruptionEvent(
            machine.SessionId,
            plan.PlanId,
            "cancelled",
            "Cancelled at a safe boundary.",
            InstallerRecoveryAction.InspectAgain,
            "Reinspect the durable state before continuing."
        ));
        machine.State.Should().Be(ProtocolSessionState.Completed);
    }

    [Test]
    public void Progress_RequiresExecutionMatchingIdsAndIncreasingSequence()
    {
        (ProtocolSessionStateMachine machine, PlanEvent plan) = CreatePlannedMachine();
        ProgressEvent beforeExecution = new(machine.SessionId, plan.PlanId, 0, InstallerProgressStage.BackingUp, 0, null, "Starting.");
        FluentActions.Invoking(() => machine.RecordProgress(beforeExecution)).Should()
            .Throw<ProtocolException>().WithMessage("*Progress can't be recorded*");

        machine.ConfirmPlan(new ConfirmPlanRequest(machine.SessionId, plan.PlanId));
        machine.BeginExecution(new ExecutePlanRequest(machine.SessionId, plan.PlanId));
        machine.RecordProgress(beforeExecution);
        FluentActions.Invoking(() => machine.RecordProgress(beforeExecution)).Should()
            .Throw<ProtocolException>().WithMessage("*increase monotonically*");
    }

    [Test]
    public void TerminalResult_RequiresExecutionAndMatchingPlan()
    {
        (ProtocolSessionStateMachine machine, PlanEvent plan) = CreatePlannedMachine();
        SuccessEvent premature = new(machine.SessionId, plan.PlanId, InstallerOperation.Install, "Done.");
        FluentActions.Invoking(() => machine.Complete(premature)).Should()
            .Throw<ProtocolException>().WithMessage("*terminal event can't be recorded*");

        machine.ConfirmPlan(new ConfirmPlanRequest(machine.SessionId, plan.PlanId));
        machine.BeginExecution(new ExecutePlanRequest(machine.SessionId, plan.PlanId));
        SuccessEvent wrongOperation = new(machine.SessionId, plan.PlanId, InstallerOperation.Repair, "Done.");
        FluentActions.Invoking(() => machine.Complete(wrongOperation)).Should()
            .Throw<ProtocolException>().WithMessage("*operation doesn't match the current plan*");
        RolledBackFailureEvent wrong = new(machine.SessionId, ProtocolPlanId.CreateRandom(), "failed", "Failed.", "Rolled back.");
        FluentActions.Invoking(() => machine.Complete(wrong)).Should()
            .Throw<ProtocolException>().WithMessage("*may be stale*");
    }

    private static (ProtocolSessionStateMachine Machine, PlanEvent Plan) CreatePlannedMachine()
    {
        ProtocolSessionStateMachine machine = new();
        machine.AcceptHandshake(new HandshakeRequest("gui", "1.0.0"), "4.5.3-alpha.2");
        PlanEvent plan = machine.IssuePlan(
            new InspectPlanRequest(machine.SessionId, "/game", InstallerOperation.Install, "4.5.3-alpha.2"),
            ObservedInstallState.NotInstalled,
            "Install SMAPI.",
            []
        );
        return (machine, plan);
    }
}
