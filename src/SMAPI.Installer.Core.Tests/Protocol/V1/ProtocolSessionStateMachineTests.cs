using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Protocol.V1;

namespace StardewModdingAPI.Installer.Core.Tests.Protocol.V1;

/// <summary>Tests exact ID/digest binding and transition validation for a one-shot backend session.</summary>
[TestFixture]
internal sealed class ProtocolSessionStateMachineTests
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly ProtocolPlanDigest ExecutionBindingDigest = ProtocolPlanDigest.Parse("dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd");
    private static readonly ProtocolGameRootIdentity GameRoot = new("/game", 10, 11, 20, 7);

    [Test]
    public void Session_HappyPathRequiresHandshakePlanConfirmationAndExecution()
    {
        ProtocolSessionStateMachine machine = new();

        HandshakeEvent handshake = machine.AcceptHandshake(new HandshakeRequest("gui", "1.0.0"), "4.5.3-alpha.2", "inspect-plan");
        PlanEvent plan = IssuePlan(machine, InstallerOperation.Install);
        machine.ConfirmPlan(new ConfirmPlanRequest(handshake.SessionId, plan.PlanId, plan.PlanDigest));
        machine.BeginExecution(new ExecutePlanRequest(handshake.SessionId, plan.PlanId, plan.PlanDigest));
        machine.RecordProgress(new ProgressEvent(handshake.SessionId, plan.PlanId, plan.PlanDigest, 0, InstallerProgressStage.BackingUp, 0, 1, "Starting backup."));
        machine.RecordProgress(new ProgressEvent(handshake.SessionId, plan.PlanId, plan.PlanDigest, 1, InstallerProgressStage.Finalizing, 1, 1, "Verifying."));
        machine.Complete(new SuccessEvent(handshake.SessionId, plan.PlanId, plan.PlanDigest, InstallerOperation.Install, "Installed and verified."));

        handshake.SessionId.Should().Be(machine.SessionId);
        machine.CurrentPlanId.Should().Be(plan.PlanId);
        machine.CurrentPlanDigest.Should().Be(plan.PlanDigest);
        machine.LastProgressSequence.Should().Be(1);
        machine.State.Should().Be(ProtocolSessionState.Completed);
    }

    [Test]
    public void BeginExecution_BeforeMatchingConfirmationIsRejected()
    {
        (ProtocolSessionStateMachine machine, PlanEvent plan) = CreatePlannedMachine();

        FluentActions.Invoking(() => machine.BeginExecution(new ExecutePlanRequest(machine.SessionId, plan.PlanId, plan.PlanDigest))).Should()
            .Throw<ProtocolException>().WithMessage("*must be confirmed before execution*");
        machine.State.Should().Be(ProtocolSessionState.PlanIssued);
    }

    [Test]
    public void Replanning_InvalidatesOldPlanIdAndDigestForEveryFollowingRequest()
    {
        (ProtocolSessionStateMachine machine, PlanEvent stalePlan) = CreatePlannedMachine();
        PlanEvent currentPlan = IssuePlan(machine, InstallerOperation.Repair, "smapi-internal/repair.dll", HashB);

        currentPlan.PlanId.Should().NotBe(stalePlan.PlanId);
        currentPlan.PlanDigest.Should().NotBe(stalePlan.PlanDigest);
        FluentActions.Invoking(() => machine.ConfirmPlan(new ConfirmPlanRequest(machine.SessionId, stalePlan.PlanId, stalePlan.PlanDigest))).Should()
            .Throw<ProtocolException>().WithMessage("*plan ID*stale*");
        FluentActions.Invoking(() => machine.RequestCancellation(new CancelPlanRequest(machine.SessionId, stalePlan.PlanId, stalePlan.PlanDigest))).Should()
            .Throw<ProtocolException>().WithMessage("*plan ID*stale*");

        machine.ConfirmPlan(new ConfirmPlanRequest(machine.SessionId, currentPlan.PlanId, currentPlan.PlanDigest));
        FluentActions.Invoking(() => machine.BeginExecution(new ExecutePlanRequest(machine.SessionId, stalePlan.PlanId, stalePlan.PlanDigest))).Should()
            .Throw<ProtocolException>().WithMessage("*plan ID*stale*");
        machine.State.Should().Be(ProtocolSessionState.PlanConfirmed);
    }

    [Test]
    public void MismatchedDigest_IsRejectedAtConfirmationExecutionCancellationProgressAndTerminal()
    {
        ProtocolPlanDigest wrongDigest = ProtocolPlanDigest.Parse("cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc");

        (ProtocolSessionStateMachine confirmMachine, PlanEvent confirmPlan) = CreatePlannedMachine();
        FluentActions.Invoking(() => confirmMachine.ConfirmPlan(new ConfirmPlanRequest(confirmMachine.SessionId, confirmPlan.PlanId, wrongDigest))).Should()
            .Throw<ProtocolException>().WithMessage("*execution-plan digest*stale or altered*");

        (ProtocolSessionStateMachine executeMachine, PlanEvent executePlan) = CreatePlannedMachine();
        executeMachine.ConfirmPlan(new ConfirmPlanRequest(executeMachine.SessionId, executePlan.PlanId, executePlan.PlanDigest));
        FluentActions.Invoking(() => executeMachine.BeginExecution(new ExecutePlanRequest(executeMachine.SessionId, executePlan.PlanId, wrongDigest))).Should()
            .Throw<ProtocolException>().WithMessage("*execution-plan digest*stale or altered*");

        (ProtocolSessionStateMachine cancelMachine, PlanEvent cancelPlan) = CreatePlannedMachine();
        FluentActions.Invoking(() => cancelMachine.RequestCancellation(new CancelPlanRequest(cancelMachine.SessionId, cancelPlan.PlanId, wrongDigest))).Should()
            .Throw<ProtocolException>().WithMessage("*execution-plan digest*stale or altered*");

        (ProtocolSessionStateMachine progressMachine, PlanEvent progressPlan) = CreateExecutingMachine();
        FluentActions.Invoking(() => progressMachine.RecordProgress(new ProgressEvent(progressMachine.SessionId, progressPlan.PlanId, wrongDigest, 0, InstallerProgressStage.BackingUp, 0, null, "Starting."))).Should()
            .Throw<ProtocolException>().WithMessage("*execution-plan digest*stale or altered*");

        (ProtocolSessionStateMachine terminalMachine, PlanEvent terminalPlan) = CreateExecutingMachine();
        FluentActions.Invoking(() => terminalMachine.Complete(new SuccessEvent(terminalMachine.SessionId, terminalPlan.PlanId, wrongDigest, InstallerOperation.Install, "Done."))).Should()
            .Throw<ProtocolException>().WithMessage("*execution-plan digest*stale or altered*");
    }

    [Test]
    public void Requests_WithAnotherSessionAreRejected()
    {
        ProtocolSessionStateMachine machine = new();
        machine.AcceptHandshake(new HandshakeRequest("gui", "1"), "server");
        ProtocolSessionId otherSession = ProtocolSessionId.CreateRandom();

        FluentActions.Invoking(() => machine.IssuePlan(
            new InspectPlanRequest(otherSession, "/game", InstallerOperation.Install, CreateRelease().EmbeddedVersion),
            ExecutionBindingDigest,
            GameRoot,
            null,
            CreateRelease(),
            ObservedInstallState.NotInstalled,
            CreateOperations("smapi-internal/a.dll", HashA),
            [],
            "Install.",
            []
        )).Should().Throw<ProtocolException>().WithMessage("*doesn't match this process session ID*");
    }

    [Test]
    public void IssuingPlan_RejectsTargetSelectionMismatchWithoutChangingState()
    {
        ProtocolSessionStateMachine machine = new();
        machine.AcceptHandshake(new HandshakeRequest("gui", "1"), "server");

        FluentActions.Invoking(() => machine.IssuePlan(
            new InspectPlanRequest(machine.SessionId, "/game", InstallerOperation.Install, "different-release"),
            ExecutionBindingDigest,
            GameRoot,
            null,
            CreateRelease(),
            ObservedInstallState.NotInstalled,
            CreateOperations("smapi-internal/a.dll", HashA),
            [],
            "Install.",
            []
        )).Should().Throw<ProtocolException>().WithMessage("*doesn't match the exact target release identity*");
        machine.State.Should().Be(ProtocolSessionState.Ready);
        machine.CurrentPlanId.Should().BeNull();
        machine.CurrentPlanDigest.Should().BeNull();
    }

    [Test]
    public void ConflictedPlan_CannotBeConfirmedButCanBeCleanlyCancelled()
    {
        ProtocolSessionStateMachine machine = new();
        machine.AcceptHandshake(new HandshakeRequest("gui", "1"), "server");
        ProtocolPlanOperation[] operations = CreateOperations("smapi-internal/a.dll", HashA);
        ProtocolPlanConflict[] conflicts = [new(PlanConflictCode.ModifiedOwnedFile, "smapi-internal/a.dll")];
        PlanEvent plan = machine.IssuePlan(
            new InspectPlanRequest(machine.SessionId, "/game", InstallerOperation.Install, CreateRelease().EmbeddedVersion),
            ExecutionBindingDigest,
            GameRoot,
            null,
            CreateRelease(),
            ObservedInstallState.KnownModified,
            operations,
            conflicts,
            "Blocked.",
            []
        );

        machine.CurrentPlanCanExecute.Should().BeFalse();
        FluentActions.Invoking(() => machine.ConfirmPlan(new ConfirmPlanRequest(machine.SessionId, plan.PlanId, plan.PlanDigest))).Should()
            .Throw<ProtocolException>().WithMessage("*unresolved conflicts*");
        machine.RequestCancellation(new CancelPlanRequest(machine.SessionId, plan.PlanId, plan.PlanDigest));
        machine.Complete(new CancelledEvent(machine.SessionId, plan.PlanId, plan.PlanDigest, "Cancelled.", "No files were changed."));
        machine.State.Should().Be(ProtocolSessionState.Completed);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Cancellation_HasExplicitCleanTerminalPathBeforeOrDuringExecution(bool beginExecution)
    {
        (ProtocolSessionStateMachine machine, PlanEvent plan) = CreatePlannedMachine();
        if (beginExecution)
        {
            machine.ConfirmPlan(new ConfirmPlanRequest(machine.SessionId, plan.PlanId, plan.PlanDigest));
            machine.BeginExecution(new ExecutePlanRequest(machine.SessionId, plan.PlanId, plan.PlanDigest));
        }

        machine.RequestCancellation(new CancelPlanRequest(machine.SessionId, plan.PlanId, plan.PlanDigest));
        machine.State.Should().Be(ProtocolSessionState.CancellationRequested);
        if (!beginExecution)
        {
            FluentActions.Invoking(() => machine.RecordProgress(new ProgressEvent(machine.SessionId, plan.PlanId, plan.PlanDigest, 0, InstallerProgressStage.Finalizing, 0, null, "Stopping."))).Should()
                .Throw<ProtocolException>().WithMessage("*cancelled before execution began*");
        }
        machine.Complete(new CancelledEvent(
            machine.SessionId,
            plan.PlanId,
            plan.PlanDigest,
            "Cancelled at a safe boundary.",
            beginExecution ? "The transaction was rolled back and verified." : "No files were changed."
        ));
        machine.State.Should().Be(ProtocolSessionState.Completed);
    }

    [Test]
    public void CancellationTerminal_RequiresAcceptedCancellationAndExactBinding()
    {
        (ProtocolSessionStateMachine machine, PlanEvent plan) = CreateExecutingMachine();
        CancelledEvent result = new(machine.SessionId, plan.PlanId, plan.PlanDigest, "Cancelled.", "Safe.");

        FluentActions.Invoking(() => machine.Complete(result)).Should()
            .Throw<ProtocolException>().WithMessage("*Expected protocol state 'CancellationRequested'*");

        machine.RequestCancellation(new CancelPlanRequest(machine.SessionId, plan.PlanId, plan.PlanDigest));
        CancelledEvent wrong = result with { PlanDigest = ProtocolPlanDigest.Parse("cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc") };
        FluentActions.Invoking(() => machine.Complete(wrong)).Should()
            .Throw<ProtocolException>().WithMessage("*execution-plan digest*");
        machine.State.Should().Be(ProtocolSessionState.CancellationRequested);

        FluentActions.Invoking(() => machine.Complete(new SuccessEvent(machine.SessionId, plan.PlanId, plan.PlanDigest, InstallerOperation.Install, "Done."))).Should()
            .Throw<ProtocolException>().WithMessage("*terminal event can't be recorded*");
        machine.Complete(result);
        machine.State.Should().Be(ProtocolSessionState.Completed);
    }

    [Test]
    public void Progress_RequiresExecutionMatchingBindingAndIncreasingSequence()
    {
        (ProtocolSessionStateMachine machine, PlanEvent plan) = CreatePlannedMachine();
        ProgressEvent beforeExecution = new(machine.SessionId, plan.PlanId, plan.PlanDigest, 0, InstallerProgressStage.BackingUp, 0, null, "Starting.");
        FluentActions.Invoking(() => machine.RecordProgress(beforeExecution)).Should()
            .Throw<ProtocolException>().WithMessage("*Progress can't be recorded*");

        machine.ConfirmPlan(new ConfirmPlanRequest(machine.SessionId, plan.PlanId, plan.PlanDigest));
        machine.BeginExecution(new ExecutePlanRequest(machine.SessionId, plan.PlanId, plan.PlanDigest));
        machine.RecordProgress(beforeExecution);
        FluentActions.Invoking(() => machine.RecordProgress(beforeExecution)).Should()
            .Throw<ProtocolException>().WithMessage("*increase monotonically*");
    }

    [Test]
    public void TerminalResult_RequiresExecutionMatchingBindingAndOperation()
    {
        (ProtocolSessionStateMachine machine, PlanEvent plan) = CreatePlannedMachine();
        SuccessEvent premature = new(machine.SessionId, plan.PlanId, plan.PlanDigest, InstallerOperation.Install, "Done.");
        FluentActions.Invoking(() => machine.Complete(premature)).Should()
            .Throw<ProtocolException>().WithMessage("*terminal event can't be recorded*");

        machine.ConfirmPlan(new ConfirmPlanRequest(machine.SessionId, plan.PlanId, plan.PlanDigest));
        machine.BeginExecution(new ExecutePlanRequest(machine.SessionId, plan.PlanId, plan.PlanDigest));
        SuccessEvent wrongOperation = new(machine.SessionId, plan.PlanId, plan.PlanDigest, InstallerOperation.Repair, "Done.");
        FluentActions.Invoking(() => machine.Complete(wrongOperation)).Should()
            .Throw<ProtocolException>().WithMessage("*operation doesn't match the current plan*");
        RolledBackFailureEvent wrong = new(machine.SessionId, ProtocolPlanId.CreateRandom(), plan.PlanDigest, "failed", "Failed.", "Rolled back.");
        FluentActions.Invoking(() => machine.Complete(wrong)).Should()
            .Throw<ProtocolException>().WithMessage("*plan ID*stale*");
    }

    [Test]
    public void FailureAndInterruptionTerminals_RejectMismatchedDigest()
    {
        ProtocolPlanDigest wrongDigest = ProtocolPlanDigest.Parse("cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc");
        (ProtocolSessionStateMachine failureMachine, PlanEvent failurePlan) = CreateExecutingMachine();
        FluentActions.Invoking(() => failureMachine.Complete(new RolledBackFailureEvent(
            failureMachine.SessionId,
            failurePlan.PlanId,
            wrongDigest,
            "failed",
            "Failed.",
            "Rolled back."
        ))).Should().Throw<ProtocolException>().WithMessage("*execution-plan digest*");

        (ProtocolSessionStateMachine interruptionMachine, PlanEvent interruptionPlan) = CreateExecutingMachine();
        FluentActions.Invoking(() => interruptionMachine.Complete(new RecoverableInterruptionEvent(
            interruptionMachine.SessionId,
            interruptionPlan.PlanId,
            wrongDigest,
            "interrupted",
            "Interrupted.",
            InstallerRecoveryAction.InspectAgain,
            "Inspect again."
        ))).Should().Throw<ProtocolException>().WithMessage("*execution-plan digest*");
    }

    private static (ProtocolSessionStateMachine Machine, PlanEvent Plan) CreatePlannedMachine()
    {
        ProtocolSessionStateMachine machine = new();
        machine.AcceptHandshake(new HandshakeRequest("gui", "1.0.0"), "4.5.3-alpha.2");
        return (machine, IssuePlan(machine, InstallerOperation.Install));
    }

    private static (ProtocolSessionStateMachine Machine, PlanEvent Plan) CreateExecutingMachine()
    {
        (ProtocolSessionStateMachine machine, PlanEvent plan) = CreatePlannedMachine();
        machine.ConfirmPlan(new ConfirmPlanRequest(machine.SessionId, plan.PlanId, plan.PlanDigest));
        machine.BeginExecution(new ExecutePlanRequest(machine.SessionId, plan.PlanId, plan.PlanDigest));
        return (machine, plan);
    }

    private static PlanEvent IssuePlan(
        ProtocolSessionStateMachine machine,
        InstallerOperation operation,
        string path = "smapi-internal/a.dll",
        string resultHash = HashA
    )
    {
        return machine.IssuePlan(
            new InspectPlanRequest(machine.SessionId, "/game", operation, CreateRelease().EmbeddedVersion),
            ExecutionBindingDigest,
            GameRoot,
            operation == InstallerOperation.Install ? null : CreateRelease(),
            CreateRelease(),
            ObservedInstallState.NotInstalled,
            CreateOperations(path, resultHash),
            [],
            $"{operation} SMAPI.",
            []
        );
    }

    private static ProtocolPlanOperation[] CreateOperations(string path, string resultHash)
    {
        return [new ProtocolPlanOperation(PlanOperationKind.Create, path, null, resultHash)];
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
}
