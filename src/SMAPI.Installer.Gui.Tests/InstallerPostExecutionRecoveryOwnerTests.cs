using System.Reflection;
using System.Threading.Channels;
using FluentAssertions;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Gui.Backend;

namespace StardewModdingAPI.Installer.Gui.Tests;

[NonParallelizable]
internal sealed class InstallerPostExecutionRecoveryOwnerTests
{
    private const string ExactPath = "/games/private-selected";

    [Test]
    public async Task IntegratedTakeRetainsExactPathAndCreatesNoFreshClientUntilExplicitRecovery()
    {
        RecoveryClient recovery = new();
        int factories = 0;
        (IConfirmedInstallerSession confirmed, InstallerExecutionOperation execution, SourceClient source) = await CreateConfirmedExecutionAsync(
            InterruptedExecution(),
            () => { factories++; return recovery; }
        );
        _ = await execution.Completion;

        InstallerPostExecutionRecoveryOwner owner = await confirmed.TakePostExecutionRecoveryOwnerAsync();
        factories.Should().Be(0, "taking ownership must not automatically start recovery");

        InstallerRecoveryResult result = await (await owner.RecoverInterruptedAsync()).Completion;

        factories.Should().Be(1);
        source.DisposeCalls.Should().Be(1, "the old execution backend must settle before the fresh process is created");
        recovery.Commands.Should().Equal("handshake", "validate", "recover", "dispose");
        recovery.ValidatedPaths.Should().Equal(ExactPath);
        recovery.RecoveryCandidates.Should().ContainSingle().Which.Should().BeSameAs(recovery.IssuedCandidate);
        recovery.IrrelevantCalls.Should().Be(0);
        result.Should().BeOfType<InstallerRecoveryTerminalResult>();
        await owner.DisposeAsync();
        await confirmed.DisposeAsync();
    }

    [TestCase(LinuxGameFolderStatus.MissingDirectory, "/games/private-selected")]
    [TestCase(LinuxGameFolderStatus.Valid, "/games/changed")]
    public async Task InvalidOrChangedExactPathFailsBeforeAdmissionAndCanRetryFresh(
        LinuxGameFolderStatus state,
        string returnedPath
    )
    {
        RecoveryClient bad = new() { ValidationResult = new(returnedPath, state, "hostile\nname") };
        RecoveryClient good = new();
        Queue<IInstallerProtocolClient> attempts = new([bad, good]);
        InstallerPostExecutionRecoveryOwner owner = Owner(() => attempts.Dequeue());

        Func<Task> first = () => owner.RecoverInterruptedAsync();
        await first.Should().ThrowAsync<InstallerProtocolClientException>().WithMessage("*could not be prepared safely*");
        bad.RecoverCalls.Should().Be(0);
        bad.DisposeCalls.Should().Be(1);

        InstallerRecoveryResult result = await (await owner.RecoverInterruptedAsync()).Completion;
        result.Should().BeOfType<InstallerRecoveryTerminalResult>();
        good.RecoverCalls.Should().Be(1);
        await owner.DisposeAsync();
    }

    [Test]
    public async Task HandshakeOrCapabilityFailureIsSanitizedDisposedAndFreshRetryable()
    {
        const string privateFailure = "/private/backend/log\nsecret";
        RecoveryClient failed = new()
        {
            Handshake = (_, _, _) => throw new InvalidOperationException(privateFailure)
        };
        RecoveryClient good = new();
        Queue<IInstallerProtocolClient> attempts = new([failed, good]);
        InstallerPostExecutionRecoveryOwner owner = Owner(() => attempts.Dequeue());

        Func<Task> first = () => owner.RecoverInterruptedAsync();
        Exception error = (await first.Should().ThrowAsync<InstallerProtocolClientException>()).Which;
        error.Message.Should().NotContain(privateFailure).And.NotContain("private");
        failed.DisposeCalls.Should().Be(1);

        _ = await (await owner.RecoverInterruptedAsync()).Completion;
        good.RecoverCalls.Should().Be(1);
        await owner.DisposeAsync();
    }

    [Test]
    public async Task CapabilityOmissionAndFactoryFailureAreSanitizedAndDoNotConsumeRetryAuthority()
    {
        RecoveryClient omitted = new()
        {
            Handshake = (_, _, _) => Task.FromResult(new HandshakeEvent(
                ProtocolSessionId.Parse(new string('2', 32)),
                "1",
                [ProcessInstallerProtocolClient.GameValidationCapability]
            ))
        };
        RecoveryClient good = new();
        int attempt = 0;
        InstallerPostExecutionRecoveryOwner owner = Owner(() => ++attempt switch
        {
            1 => throw new InvalidOperationException("/private/factory secret"),
            2 => omitted,
            _ => good
        });

        Func<Task> factoryFailure = () => owner.RecoverInterruptedAsync();
        (await factoryFailure.Should().ThrowAsync<InstallerProtocolClientException>()).Which.Message.Should().NotContain("factory");
        Func<Task> capabilityFailure = () => owner.RecoverInterruptedAsync();
        await capabilityFailure.Should().ThrowAsync<InstallerProtocolClientException>();
        omitted.DisposeCalls.Should().Be(1);

        _ = await (await owner.RecoverInterruptedAsync()).Completion;
        good.RecoverCalls.Should().Be(1);
        await owner.DisposeAsync();
    }

    [Test]
    public async Task PriorBackendMustSettleBeforeFactoryAndCleanupFailureNeverStrandsRecoveryAuthority()
    {
        TaskCompletionSource oldCleanup = NewCompletion();
        RecoveryClient recovery = new();
        int factories = 0;
        InstallerPostExecutionRecoveryOwner owner = new(() => { factories++; return recovery; }, ExactPath, Release());
        owner.AttachPriorBackendSettlement(oldCleanup.Task);

        Task<InstallerRecoveryOperation> starting = owner.RecoverInterruptedAsync();
        await Task.Yield();
        factories.Should().Be(0);
        oldCleanup.SetResult();
        _ = await (await starting).Completion;
        factories.Should().Be(1);
        await owner.DisposeAsync();

        RecoveryClient afterFault = new();
        InstallerPostExecutionRecoveryOwner faulted = new(() => afterFault, ExactPath, Release());
        faulted.AttachPriorBackendSettlement(Task.FromException(new IOException("/private/old cleanup")));
        _ = await (await faulted.RecoverInterruptedAsync()).Completion;
        afterFault.RecoverCalls.Should().Be(1);
        await faulted.DisposeAsync();
    }

    [Test]
    public async Task PreAdmissionCancellationDisposesClientAndPreservesFreshRetry()
    {
        TaskCompletionSource validationStarted = NewCompletion();
        RecoveryClient cancelled = new()
        {
            Validation = async (_, token) =>
            {
                validationStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new AssertionException("unreachable");
            }
        };
        RecoveryClient good = new();
        Queue<IInstallerProtocolClient> clients = new([cancelled, good]);
        InstallerPostExecutionRecoveryOwner owner = Owner(() => clients.Dequeue());
        using CancellationTokenSource cancellation = new();
        Task<InstallerRecoveryOperation> first = owner.RecoverInterruptedAsync(cancellation.Token);
        await validationStarted.Task;

        await cancellation.CancelAsync();
        Func<Task> awaitFirst = async () => await first;
        await awaitFirst.Should().ThrowAsync<OperationCanceledException>();
        cancelled.DisposeCalls.Should().Be(1);
        cancelled.RecoverCalls.Should().Be(0);

        _ = await (await owner.RecoverInterruptedAsync()).Completion;
        good.RecoverCalls.Should().Be(1);
        await owner.DisposeAsync();
    }

    [Test]
    public async Task DisposeAtClientAdmissionCancelsOnlyBeforeAdmissionAndNeverDisposesAnAdmittedClientEarly()
    {
        TaskCompletionSource recoverEntered = NewCompletion();
        TaskCompletionSource allowAdmission = NewCompletion();
        TaskCompletionSource<InstallerRecoveryResult> terminal = NewCompletion<InstallerRecoveryResult>();
        RecoveryClient client = new()
        {
            Recovery = async (_, token) =>
            {
                recoverEntered.TrySetResult();
                await allowAdmission.Task.WaitAsync(token);
                return RecoveryOperation(terminal.Task);
            }
        };
        InstallerPostExecutionRecoveryOwner owner = Owner(() => client);
        Task<InstallerRecoveryOperation> starting = owner.RecoverInterruptedAsync();
        await recoverEntered.Task;

        ValueTask disposing = owner.DisposeAsync();
        Func<Task> awaitStarting = async () => await starting;
        await awaitStarting.Should().ThrowAsync<OperationCanceledException>();
        await disposing;
        client.DisposeCalls.Should().Be(1);
        client.RecoverCalls.Should().Be(1);

        // The complementary boundary: if admission wins, disposal waits for the exact terminal.
        terminal = NewCompletion<InstallerRecoveryResult>();
        RecoveryClient admitted = new() { Recovery = (_, _) => Task.FromResult(RecoveryOperation(terminal.Task)) };
        InstallerPostExecutionRecoveryOwner second = Owner(() => admitted);
        InstallerRecoveryOperation operation = await second.RecoverInterruptedAsync();
        ValueTask admittedDisposal = second.DisposeAsync();
        admittedDisposal.IsCompleted.Should().BeFalse();
        admitted.DisposeCalls.Should().Be(0);
        terminal.SetResult(CompletedRecovery());
        _ = await operation.Completion;
        await admittedDisposal;
        admitted.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task NonCancellationFailureAtRecoveryBoundaryReturnsUnknownAndAllowsFreshRetry()
    {
        const string privateFailure = "/private/mutated/backend-state";
        RecoveryClient uncertain = new() { Recovery = (_, _) => throw new IOException(privateFailure) };
        RecoveryClient retry = new();
        Queue<IInstallerProtocolClient> clients = new([uncertain, retry]);
        InstallerPostExecutionRecoveryOwner owner = Owner(() => clients.Dequeue());

        InstallerRecoveryResult first = await (await owner.RecoverInterruptedAsync()).Completion;
        first.Should().BeOfType<InstallerRecoveryStateUnknownResult>();
        uncertain.DisposeCalls.Should().Be(1);

        _ = await (await owner.RecoverInterruptedAsync()).Completion;
        retry.RecoverCalls.Should().Be(1);
        await owner.DisposeAsync();
    }

    [Test]
    public async Task ActiveAttemptIsExclusiveAndEveryRetryUsesExactlyOneFreshClient()
    {
        TaskCompletionSource<InstallerRecoveryResult> firstTerminal = NewCompletion<InstallerRecoveryResult>();
        RecoveryClient first = new() { Recovery = (_, _) => Task.FromResult(RecoveryOperation(firstTerminal.Task)) };
        RecoveryClient second = new();
        int factories = 0;
        Queue<IInstallerProtocolClient> clients = new([first, second]);
        InstallerPostExecutionRecoveryOwner owner = Owner(() => { factories++; return clients.Dequeue(); });
        InstallerRecoveryOperation active = await owner.RecoverInterruptedAsync();

        Func<Task> overlap = () => owner.RecoverInterruptedAsync();
        await overlap.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already active*");
        factories.Should().Be(1);

        firstTerminal.SetResult(CancelledRecovery());
        _ = await active.Completion;
        _ = await (await owner.RecoverInterruptedAsync()).Completion;
        factories.Should().Be(2);
        await owner.DisposeAsync();
    }

    [Test]
    public async Task CompletionOutcomesPermitOnlyRecoveryRequiredOrUnknownRetries()
    {
        InstallerRecoveryResult[] retryable =
        [
            CancelledRecovery(),
            PartialRecovery(),
            UnexpectedRecovery(),
            new InstallerRecoveryStateUnknownResult()
        ];
        foreach (InstallerRecoveryResult expected in retryable)
        {
            RecoveryClient first = new() { Result = expected };
            RecoveryClient second = new();
            Queue<IInstallerProtocolClient> clients = new([first, second]);
            InstallerPostExecutionRecoveryOwner owner = Owner(() => clients.Dequeue());
            (await (await owner.RecoverInterruptedAsync()).Completion).Should().BeSameAs(expected);
            _ = await (await owner.RecoverInterruptedAsync()).Completion;
            second.RecoverCalls.Should().Be(1);
            await owner.DisposeAsync();
        }

        foreach (bool stillSelected in new[] { true, false })
        {
            RecoveryClient completed = new() { Result = CompletedRecovery(stillSelected) };
            InstallerPostExecutionRecoveryOwner owner = Owner(() => completed);
            _ = await (await owner.RecoverInterruptedAsync()).Completion;
            Func<Task> retry = () => owner.RecoverInterruptedAsync();
            await retry.Should().ThrowAsync<InvalidOperationException>().WithMessage("*completed*");
            await owner.DisposeAsync();
        }
    }

    [Test]
    public async Task HostileRecoveryTerminalBoundsAndSettlementBecomeConservativeUnknown()
    {
        InstallerRecoveryTerminalResult[] hostile =
        [
            PartialRecovery() with { Attempt = new(false, true, -1, 0) },
            PartialRecovery() with { Attempt = new(false, true, ProcessInstallerProtocolClient.MaximumRecoveryTransactions + 1, 0) },
            PartialRecovery() with { Attempt = new(false, true, 1, ProcessInstallerProtocolClient.MaximumExecutionProgressUnits + 1) },
            PartialRecovery() with { BackendSettlement = (InstallerBackendSettlement)999 },
            PartialRecovery() with { ErrorCode = (ProtocolTerminalErrorCode)999 }
        ];
        foreach (InstallerRecoveryTerminalResult terminal in hostile)
        {
            RecoveryClient first = new() { Result = terminal };
            RecoveryClient second = new();
            Queue<IInstallerProtocolClient> clients = new([first, second]);
            InstallerPostExecutionRecoveryOwner owner = Owner(() => clients.Dequeue());
            (await (await owner.RecoverInterruptedAsync()).Completion).Should().BeOfType<InstallerRecoveryStateUnknownResult>();
            _ = await (await owner.RecoverInterruptedAsync()).Completion;
            second.RecoverCalls.Should().Be(1);
            await owner.DisposeAsync();
        }
    }

    [Test]
    public async Task TakeRejectsOnlyAValidatedIneligibleExecutionTerminalWithoutCreatingAClient()
    {
        int factories = 0;
        (IConfirmedInstallerSession confirmed, InstallerExecutionOperation operation, _) = await CreateConfirmedExecutionAsync(
            SuccessfulExecution(),
            () => { factories++; return new RecoveryClient(); }
        );
        _ = await operation.Completion;
        Func<Task> take = () => confirmed.TakePostExecutionRecoveryOwnerAsync();
        await take.Should().ThrowAsync<InvalidOperationException>().WithMessage("*doesn't require*");
        factories.Should().Be(0);
        await confirmed.DisposeAsync();
    }

    [Test]
    public async Task InconsistentPostAdmissionExecutionTruthConservativelyPreservesRecoveryOwnership()
    {
        InstallerExecutionResult?[] results =
        [
            null,
            InterruptedExecution() with { DurableState = ProtocolDurableState.Unchanged },
            InterruptedExecution() with { ErrorCode = null },
            InterruptedExecution() with { NextAction = ProtocolNextAction.InspectAgain },
            InterruptedExecution() with { ErrorCode = (ProtocolTerminalErrorCode)999 },
            InterruptedExecution() with { ErrorCode = ProtocolTerminalErrorCode.UnexpectedCoreFailure },
            SuccessfulExecution() with { BackendSettlement = (InstallerBackendSettlement)999 },
            SuccessfulExecution() with { Summary = new(-1, 0, 0, 0, 0, 0) }
        ];
        foreach (InstallerExecutionResult? result in results)
        {
            (IConfirmedInstallerSession confirmed, InstallerExecutionOperation operation, _) = await CreateConfirmedExecutionAsync(
                Task.FromResult(result!),
                () => new RecoveryClient()
            );
            _ = await operation.Completion;
            InstallerPostExecutionRecoveryOwner owner = await confirmed.TakePostExecutionRecoveryOwnerAsync();
            await owner.DisposeAsync();
        }
    }

    [Test]
    public async Task TakeIsOneShotAndWaitCancellationDoesNotConsumeAuthority()
    {
        TaskCompletionSource<InstallerExecutionResult> terminal = NewCompletion<InstallerExecutionResult>();
        RecoveryClient recovery = new();
        (IConfirmedInstallerSession confirmed, _, _) = await CreateConfirmedExecutionAsync(terminal.Task, () => recovery);
        using CancellationTokenSource cancellation = new();
        Task<InstallerPostExecutionRecoveryOwner> waiting = confirmed.TakePostExecutionRecoveryOwnerAsync(cancellation.Token);
        await cancellation.CancelAsync();
        Func<Task> awaitWaiting = async () => await waiting;
        await awaitWaiting.Should().ThrowAsync<OperationCanceledException>();

        terminal.SetResult(new InstallerExecutionStateUnknownResult());
        InstallerPostExecutionRecoveryOwner owner = await confirmed.TakePostExecutionRecoveryOwnerAsync();
        Func<Task> second = () => confirmed.TakePostExecutionRecoveryOwnerAsync();
        await second.Should().ThrowAsync<ObjectDisposedException>();
        await owner.DisposeAsync();
    }

    [Test]
    public void PublicRecoveryOwnerSurfaceContainsNoPathTransportOrAuthorityData()
    {
        Type type = typeof(InstallerPostExecutionRecoveryOwner);
        type.IsSealed.Should().BeTrue();
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => property.Name)
            .Should().Equal(nameof(InstallerPostExecutionRecoveryOwner.Release));
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .Should().BeEquivalentTo([
                nameof(InstallerPostExecutionRecoveryOwner.RecoverInterruptedAsync),
                nameof(InstallerPostExecutionRecoveryOwner.DisposeAsync)
            ]);
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Should().NotContain(property => property.PropertyType == typeof(string) || property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase));
        type.GetFields(BindingFlags.Public | BindingFlags.Instance).Should().BeEmpty();
    }

    [Test]
    public async Task PriorBackendSettlementCanBeAttachedOnlyOnce()
    {
        InstallerPostExecutionRecoveryOwner owner = new(() => new RecoveryClient(), ExactPath, Release());
        owner.AttachPriorBackendSettlement(Task.CompletedTask);
        Action duplicate = () => owner.AttachPriorBackendSettlement(Task.CompletedTask);
        duplicate.Should().Throw<InvalidOperationException>().WithMessage("*already attached*");
        await owner.DisposeAsync();
    }

    [Test]
    public async Task RecoveryCannotStartBeforePriorBackendSettlementIsAttached()
    {
        int factories = 0;
        InstallerPostExecutionRecoveryOwner owner = new(() =>
        {
            factories++;
            return new RecoveryClient();
        }, ExactPath, Release());
        Func<Task> start = () => owner.RecoverInterruptedAsync();

        await start.Should().ThrowAsync<InvalidOperationException>().WithMessage("*must be settled*");
        factories.Should().Be(0);
        await owner.DisposeAsync();
    }

    private static InstallerPostExecutionRecoveryOwner Owner(Func<IInstallerProtocolClient> factory)
    {
        InstallerPostExecutionRecoveryOwner owner = new(factory, ExactPath, Release());
        owner.AttachPriorBackendSettlement(Task.CompletedTask);
        return owner;
    }

    private static async Task<(IConfirmedInstallerSession Confirmed, InstallerExecutionOperation Execution, SourceClient Source)> CreateConfirmedExecutionAsync(
        InstallerExecutionResult result,
        Func<IInstallerProtocolClient> freshFactory
    ) => await CreateConfirmedExecutionAsync(Task.FromResult(result), freshFactory);

    private static async Task<(IConfirmedInstallerSession Confirmed, InstallerExecutionOperation Execution, SourceClient Source)> CreateConfirmedExecutionAsync(
        Task<InstallerExecutionResult> result,
        Func<IInstallerProtocolClient> freshFactory
    )
    {
        SourceClient source = new(result);
        VerifiedInstallerSession session = new(Release(), source, freshFactory);
        ProtocolGameCandidate candidate = (await session.DiscoverGamesAsync()).Single();
        IPlanInspectionSession bound = session.BindToGame(candidate);
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await bound.InspectPlanAsync(InstallerOperation.Install);
        IConfirmedInstallerSession confirmed = await bound.ConfirmPlanAsync(plan.Confirmation!);
        InstallerExecutionOperation execution = await confirmed.ExecuteAsync();
        return (confirmed, execution, source);
    }

    private static InstallerRecoveryOperation RecoveryOperation(Task<InstallerRecoveryResult> completion)
    {
        Channel<InstallerRecoveryProgress> progress = Channel.CreateUnbounded<InstallerRecoveryProgress>();
        progress.Writer.TryComplete();
        return new(progress.Reader, completion);
    }

    private static InstallerExecutionTerminalResult InterruptedExecution() => new(
        ProtocolExecutionOutcome.InterruptedRecoveryRequired,
        ProtocolDurableState.RecoveryRequired,
        ProtocolTerminalErrorCode.RecoveryFailed,
        ProtocolRecoveryDisposition.InterruptedRecoveryRequired,
        ProtocolNextAction.RecoverInterrupted,
        new(1, 0, 1, 0, 0, 0),
        InstallerBackendSettlement.ConfirmedClosed
    );

    private static InstallerExecutionTerminalResult SuccessfulExecution() => new(
        ProtocolExecutionOutcome.Succeeded,
        ProtocolDurableState.Committed,
        null,
        ProtocolRecoveryDisposition.NotRequired,
        ProtocolNextAction.InspectAgain,
        new(1, 0, 1, 0, 0, 0),
        InstallerBackendSettlement.ConfirmedClosed
    );

    private static InstallerRecoveryTerminalResult CompletedRecovery(bool stillSelected = true) => new(
        ProtocolInterruptedRecoveryOutcome.RecoveryCompleted,
        ProtocolDurableState.RecoveryCompleted,
        null,
        ProtocolRecoveryDisposition.Completed,
        stillSelected ? ProtocolNextAction.InspectAgain : ProtocolNextAction.SelectGameFolder,
        new(true, stillSelected, 1, 2),
        InstallerBackendSettlement.ConfirmedClosed
    );

    private static InstallerRecoveryTerminalResult CancelledRecovery() => new(
        ProtocolInterruptedRecoveryOutcome.CancelledBeforeRecovery,
        ProtocolDurableState.Unchanged,
        null,
        ProtocolRecoveryDisposition.InterruptedRecoveryRequired,
        ProtocolNextAction.RecoverInterrupted,
        null,
        InstallerBackendSettlement.ConfirmedClosed
    );

    private static InstallerRecoveryTerminalResult PartialRecovery() => new(
        ProtocolInterruptedRecoveryOutcome.PartialFailure,
        ProtocolDurableState.RecoveryRequired,
        ProtocolTerminalErrorCode.RecoveryFailed,
        ProtocolRecoveryDisposition.InterruptedRecoveryRequired,
        ProtocolNextAction.RecoverInterrupted,
        new(false, true, 1, 2),
        InstallerBackendSettlement.ConfirmedClosed
    );

    private static InstallerRecoveryTerminalResult UnexpectedRecovery() => new(
        ProtocolInterruptedRecoveryOutcome.UnexpectedFailure,
        ProtocolDurableState.Unknown,
        ProtocolTerminalErrorCode.UnexpectedCoreFailure,
        ProtocolRecoveryDisposition.InterruptedRecoveryRequired,
        ProtocolNextAction.RecoverInterrupted,
        null,
        InstallerBackendSettlement.Unconfirmed
    );

    private static ProtocolReleaseIdentity Release() => GameDiscoveryControllerTests.Release();
    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<T> NewCompletion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class SourceClient(Task<InstallerExecutionResult> executionResult) : IInstallerProtocolClient
    {
        private readonly ProtocolGameCandidate Candidate = new(ExactPath, LinuxGameFolderStatus.Valid, "private selected");
        public int DisposeCalls { get; private set; }
        public Task<InstallerProtocolClientException> SessionFaulted { get; } = new TaskCompletionSource<InstallerProtocolClientException>().Task;
        public Task<HandshakeEvent> HandshakeAsync(string clientName, string clientVersion, CancellationToken cancellationToken = default) => throw new AssertionException("no second handshake");
        public Task<InstallerPackageOpenResult> OpenPackageAsync(InstallerPackageOpenInput package, CancellationToken cancellationToken = default) => throw new AssertionException("no package open");
        public Task<IReadOnlyList<ProtocolGameCandidate>> DiscoverGamesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([this.Candidate]);
        public Task<ProtocolGameCandidate> ValidateGameAsync(string canonicalPath, CancellationToken cancellationToken = default) => throw new AssertionException("discovery candidate used");
        public Task<InstallerReadOnlyPlanResult> InspectPlanAsync(string canonicalGamePath, InstallerOperation operation, CancellationToken cancellationToken = default)
            => Task.FromResult<InstallerReadOnlyPlanResult>(new InstallerReadOnlyPlanSuccess(
                operation,
                ObservedInstallState.NotInstalled,
                null,
                null,
                false,
                [],
                ProtocolRecommendedDefault.Cancel,
                true,
                [],
                [],
                [],
                0
            )
            { Confirmation = new InstallerPlanConfirmation() });
        public Task<InstallerConfirmedPlanAuthority> ConfirmPlanAsync(InstallerPlanConfirmation confirmation, CancellationToken cancellationToken = default)
            => Task.FromResult(new InstallerConfirmedPlanAuthority());
        public Task<InstallerExecutionOperation> ExecutePlanAsync(InstallerConfirmedPlanAuthority authority, CancellationToken cancellationToken = default)
        {
            Channel<InstallerExecutionProgress> progress = Channel.CreateUnbounded<InstallerExecutionProgress>();
            progress.Writer.TryComplete();
            return Task.FromResult(new InstallerExecutionOperation(progress.Reader, executionResult, () => Task.CompletedTask));
        }
        public ValueTask DisposeAsync() { this.DisposeCalls++; return ValueTask.CompletedTask; }
    }

    private sealed class RecoveryClient : IInstallerProtocolClient
    {
        public Func<string, string, CancellationToken, Task<HandshakeEvent>> Handshake { get; init; } = (_, _, _) => Task.FromResult(HandshakeResult());
        public Func<string, CancellationToken, Task<ProtocolGameCandidate>>? Validation { get; init; }
        public Func<ProtocolGameCandidate, CancellationToken, Task<InstallerRecoveryOperation>>? Recovery { get; init; }
        public ProtocolGameCandidate ValidationResult { get; init; } = new(ExactPath, LinuxGameFolderStatus.Valid, "private selected");
        public InstallerRecoveryResult Result { get; init; } = CompletedRecovery();
        public ProtocolGameCandidate? IssuedCandidate { get; private set; }
        public List<string> Commands { get; } = [];
        public List<string> ValidatedPaths { get; } = [];
        public List<ProtocolGameCandidate> RecoveryCandidates { get; } = [];
        public int RecoverCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public int IrrelevantCalls { get; private set; }
        public Task<InstallerProtocolClientException> SessionFaulted { get; } = new TaskCompletionSource<InstallerProtocolClientException>().Task;
        public async Task<HandshakeEvent> HandshakeAsync(string clientName, string clientVersion, CancellationToken cancellationToken = default)
        {
            this.Commands.Add("handshake");
            clientName.Should().Be(InstallerProtocolClientIdentity.Name);
            clientVersion.Should().Be(InstallerProtocolClientIdentity.Version);
            return await this.Handshake(clientName, clientVersion, cancellationToken);
        }
        public Task<InstallerPackageOpenResult> OpenPackageAsync(InstallerPackageOpenInput package, CancellationToken cancellationToken = default) { this.IrrelevantCalls++; throw new AssertionException("package history is forbidden"); }
        public Task<IReadOnlyList<ProtocolGameCandidate>> DiscoverGamesAsync(CancellationToken cancellationToken = default) { this.IrrelevantCalls++; throw new AssertionException("recovery uses exact validation"); }
        public async Task<ProtocolGameCandidate> ValidateGameAsync(string canonicalPath, CancellationToken cancellationToken = default)
        {
            this.Commands.Add("validate");
            this.ValidatedPaths.Add(canonicalPath);
            ProtocolGameCandidate candidate = this.Validation is null
                ? this.ValidationResult
                : await this.Validation(canonicalPath, cancellationToken);
            this.IssuedCandidate = candidate;
            return candidate;
        }
        public Task<InstallerReadOnlyPlanResult> InspectPlanAsync(string canonicalGamePath, InstallerOperation operation, CancellationToken cancellationToken = default) { this.IrrelevantCalls++; throw new AssertionException("plan history is forbidden"); }
        public async Task<InstallerRecoveryOperation> RecoverInterruptedAsync(ProtocolGameCandidate candidate, CancellationToken cancellationToken = default)
        {
            this.Commands.Add("recover");
            this.RecoverCalls++;
            this.RecoveryCandidates.Add(candidate);
            return this.Recovery is null ? RecoveryOperation(Task.FromResult(this.Result)) : await this.Recovery(candidate, cancellationToken);
        }
        public ValueTask DisposeAsync() { this.Commands.Add("dispose"); this.DisposeCalls++; return ValueTask.CompletedTask; }
    }

    private static HandshakeEvent HandshakeResult() => new(
        ProtocolSessionId.Parse(new string('1', 32)),
        "1",
        [
            ProcessInstallerProtocolClient.GameValidationCapability,
            ProcessInstallerProtocolClient.InterruptedRecoveryCapability
        ]
    );
}
