using FluentAssertions;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Gui.Backend;

namespace StardewModdingAPI.Installer.Gui.Tests;

[NonParallelizable]
internal sealed class VerifiedInstallerSessionTests
{
    [Test]
    public async Task BindAcceptsOnlyTheExactValidReferenceIssuedByTheBackend()
    {
        ProtocolGameCandidate valid = Candidate("exact", LinuxGameFolderStatus.Valid);
        RecordingClient client = new() { Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([valid]) };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        ProtocolGameCandidate issued = (await session.DiscoverGamesAsync()).Single();

        Action forged = () => session.BindToGame(issued with { });
        forged.Should().Throw<ArgumentException>().WithMessage("*exact valid result*");

        await using IPlanInspectionSession bound = session.BindToGame(issued);
        bound.Game.DisplayPath.Should().Be(valid.CanonicalPath);
        client.DisposeCalls.Should().Be(0);
    }

    [Test]
    public async Task BindRejectsAnExactIssuedInvalidCandidate()
    {
        ProtocolGameCandidate invalid = Candidate("invalid", LinuxGameFolderStatus.UnsafeLauncher);
        RecordingClient client = new() { Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([invalid]) };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        ProtocolGameCandidate issued = (await session.DiscoverGamesAsync()).Single();

        Action bind = () => session.BindToGame(issued);

        bind.Should().Throw<ArgumentException>().WithMessage("*exact valid result*");
    }

    [Test]
    public async Task BindRejectsWhileADiscoveryCommandIsActive()
    {
        ProtocolGameCandidate valid = Candidate("existing", LinuxGameFolderStatus.Valid);
        TaskCompletionSource discoveryStarted = NewCompletion();
        TaskCompletionSource<IReadOnlyList<ProtocolGameCandidate>> releaseDiscovery = NewCompletion<IReadOnlyList<ProtocolGameCandidate>>();
        int calls = 0;
        RecordingClient client = new()
        {
            Discovery = _ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    return Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([valid]);
                discoveryStarted.TrySetResult();
                return releaseDiscovery.Task;
            }
        };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        ProtocolGameCandidate issued = (await session.DiscoverGamesAsync()).Single();
        Task active = session.DiscoverGamesAsync();
        await discoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Action bind = () => session.BindToGame(issued);
        bind.Should().Throw<InvalidOperationException>().WithMessage("*still active*");

        releaseDiscovery.SetResult([]);
        await active.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task BindRejectsACompletedSessionFault()
    {
        ProtocolGameCandidate valid = Candidate("faulted", LinuxGameFolderStatus.Valid);
        RecordingClient client = new() { Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([valid]) };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        ProtocolGameCandidate issued = (await session.DiscoverGamesAsync()).Single();
        client.Fault.SetResult(new InstallerProtocolClientException("synthetic fault"));

        Action bind = () => session.BindToGame(issued);

        bind.Should().Throw<InvalidOperationException>().WithMessage("*already faulted*");
    }

    [Test]
    public async Task BindIsOneTimeAndRevokesDiscoveryAndValidation()
    {
        ProtocolGameCandidate valid = Candidate("bound", LinuxGameFolderStatus.Valid);
        RecordingClient client = new() { Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([valid]) };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        ProtocolGameCandidate issued = (await session.DiscoverGamesAsync()).Single();
        await using IPlanInspectionSession bound = session.BindToGame(issued);

        Action secondBind = () => session.BindToGame(issued);
        secondBind.Should().Throw<InvalidOperationException>().WithMessage("*already bound*");
        Func<Task> discover = () => session.DiscoverGamesAsync();
        await discover.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already bound*");
        Func<Task> validate = () => session.ValidateGameAsync(valid.CanonicalPath);
        await validate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already bound*");
        client.DiscoverCalls.Should().Be(1);
        client.ValidateCalls.Should().Be(0);
    }

    [Test]
    public async Task NewManualValidationRevokesOnlyThePreviousManualCandidateAndRetainsDiscoveryCandidates()
    {
        ProtocolGameCandidate discovered = Candidate("discovered-retained", LinuxGameFolderStatus.Valid);
        ProtocolGameCandidate firstManual = Candidate("manual-stale", LinuxGameFolderStatus.Valid);
        ProtocolGameCandidate currentManual = Candidate("manual-current", LinuxGameFolderStatus.Valid);
        Queue<ProtocolGameCandidate> validations = new([firstManual, currentManual]);
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([discovered]),
            Validation = (_, _) => Task.FromResult(validations.Dequeue())
        };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        ProtocolGameCandidate issuedDiscovery = (await session.DiscoverGamesAsync()).Single();
        ProtocolGameCandidate stale = await session.ValidateGameAsync("/games/manual-stale");
        ProtocolGameCandidate current = await session.ValidateGameAsync("/games/manual-current");

        Action bindStale = () => session.BindToGame(stale);
        bindStale.Should().Throw<ArgumentException>().WithMessage("*exact valid result*");

        await using IPlanInspectionSession bound = session.BindToGame(issuedDiscovery);
        bound.Game.DisplayPath.Should().Be(discovered.CanonicalPath);
        current.Should().BeSameAs(currentManual);
    }

    [Test]
    public async Task LatestManualCandidateRemainsCurrentAcrossANewerDiscoverySnapshot()
    {
        ProtocolGameCandidate manual = Candidate("manual-retained", LinuxGameFolderStatus.Valid);
        ProtocolGameCandidate firstDiscovery = Candidate("discovery-old", LinuxGameFolderStatus.Valid);
        ProtocolGameCandidate latestDiscovery = Candidate("discovery-current", LinuxGameFolderStatus.Valid);
        Queue<IReadOnlyList<ProtocolGameCandidate>> discoveries = new([[firstDiscovery], [latestDiscovery]]);
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult(discoveries.Dequeue()),
            Validation = (_, _) => Task.FromResult(manual)
        };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        ProtocolGameCandidate old = (await session.DiscoverGamesAsync()).Single();
        ProtocolGameCandidate issuedManual = await session.ValidateGameAsync(manual.CanonicalPath);
        ProtocolGameCandidate current = (await session.DiscoverGamesAsync()).Single();

        Action bindOld = () => session.BindToGame(old);
        bindOld.Should().Throw<ArgumentException>().WithMessage("*exact valid result*");
        await using IPlanInspectionSession bound = session.BindToGame(issuedManual);
        bound.Game.DisplayPath.Should().Be(manual.CanonicalPath);
        current.Should().BeSameAs(latestDiscovery);
    }

    [Test]
    public async Task ManualCandidateAuthorityRemainsBoundedToOnlyTheLatestValidation()
    {
        ProtocolGameCandidate[] validations = Enumerable.Range(0, ProtocolJsonSerializer.MaxGameCandidates + 10)
            .Select(index => Candidate($"bounded-manual-{index:D3}", LinuxGameFolderStatus.Valid))
            .ToArray();
        int next = 0;
        RecordingClient client = new() { Validation = (_, _) => Task.FromResult(validations[next++]) };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        foreach (ProtocolGameCandidate candidate in validations)
            (await session.ValidateGameAsync(candidate.CanonicalPath)).Should().BeSameAs(candidate);

        Action bindOldest = () => session.BindToGame(validations[0]);
        bindOldest.Should().Throw<ArgumentException>().WithMessage("*exact valid result*");
        await using IPlanInspectionSession bound = session.BindToGame(validations[^1]);
        bound.Game.DisplayPath.Should().Be(validations[^1].CanonicalPath);
    }

    [Test]
    public async Task OversizedDiscoverySnapshotIsRejectedBeforeItCanGrantCandidateAuthority()
    {
        ProtocolGameCandidate[] oversized = Enumerable.Range(0, ProtocolJsonSerializer.MaxGameCandidates + 1)
            .Select(index => Candidate($"oversized-{index:D3}", LinuxGameFolderStatus.Valid))
            .ToArray();
        ProtocolGameCandidate bounded = Candidate("bounded-discovery", LinuxGameFolderStatus.Valid);
        int calls = 0;
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>(
                Interlocked.Increment(ref calls) == 1 ? oversized : [bounded]
            )
        };
        VerifiedInstallerSession session = new(CreateRelease(), client);

        Func<Task> discoverOversized = () => session.DiscoverGamesAsync();
        await discoverOversized.Should().ThrowAsync<InstallerProtocolClientException>();
        Action bindRejected = () => session.BindToGame(oversized[0]);
        bindRejected.Should().Throw<ArgumentException>().WithMessage("*exact valid result*");

        ProtocolGameCandidate issued = (await session.DiscoverGamesAsync()).Single();
        await using IPlanInspectionSession bound = session.BindToGame(issued);
        bound.Game.DisplayPath.Should().Be(bounded.CanonicalPath);
    }

    [Test]
    public async Task BoundOwnerPreservesReleaseAndGamePresentationAndFixesTheInspectPath()
    {
        ProtocolReleaseIdentity release = CreateRelease();
        ProtocolGameCandidate valid = Candidate("selected", LinuxGameFolderStatus.Valid);
        InstallerReadOnlyPlanResult expected = Plan(InstallerOperation.Backup);
        RecordingClient client = new()
        {
            Validation = (_, _) => Task.FromResult(valid),
            Inspection = (_, _, _) => Task.FromResult(expected)
        };
        VerifiedInstallerSession session = new(release, client);
        ProtocolGameCandidate issued = await session.ValidateGameAsync(valid.CanonicalPath);
        await using IPlanInspectionSession bound = session.BindToGame(issued);

        InstallerReadOnlyPlanResult actual = await bound.InspectPlanAsync(InstallerOperation.Backup);

        actual.Should().BeSameAs(expected);
        bound.Release.Should().BeSameAs(release);
        bound.Game.DisplayPath.Should().Be(valid.CanonicalPath);
        bound.Game.DisplayName.Should().Be(valid.DisplayName);
        client.InspectedPaths.Should().Equal(valid.CanonicalPath);
        client.InspectedOperations.Should().Equal(InstallerOperation.Backup);
    }

    [TestCase(InstallerOperation.Install)]
    [TestCase(InstallerOperation.Update)]
    [TestCase(InstallerOperation.Repair)]
    [TestCase(InstallerOperation.Uninstall)]
    [TestCase(InstallerOperation.Backup)]
    public async Task BoundOwnerAdmitsExactlyTheFiveReadOnlyOperations(InstallerOperation operation)
    {
        ProtocolGameCandidate valid = Candidate($"supported-{operation}", LinuxGameFolderStatus.Valid);
        RecordingClient client = new() { Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([valid]) };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        await using IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());

        InstallerReadOnlyPlanSuccess result = (await bound.InspectPlanAsync(operation))
            .Should().BeOfType<InstallerReadOnlyPlanSuccess>().Subject;

        result.Operation.Should().Be(operation);
        client.InspectedOperations.Should().Equal(operation);
    }

    [TestCase(InstallerOperation.Rollback)]
    [TestCase((InstallerOperation)999)]
    public async Task UnsupportedPlanOperationFailsLocallyWithoutCorruptingTheBoundSession(InstallerOperation unsupported)
    {
        ProtocolGameCandidate valid = Candidate("unsupported-operation", LinuxGameFolderStatus.Valid);
        RecordingClient client = new() { Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([valid]) };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        await using IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());

        Func<Task> reject = () => bound.InspectPlanAsync(unsupported);
        await reject.Should().ThrowAsync<ArgumentOutOfRangeException>();
        client.InspectedOperations.Should().BeEmpty();

        (await bound.InspectPlanAsync(InstallerOperation.Backup)).Should().BeOfType<InstallerReadOnlyPlanSuccess>();
        client.InspectedOperations.Should().Equal(InstallerOperation.Backup);
    }

    [Test]
    public async Task ParentDisposalIsInertAfterTransferAndChildCleanupIsIdempotent()
    {
        ProtocolGameCandidate valid = Candidate("ownership", LinuxGameFolderStatus.Valid);
        RecordingClient client = new() { Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([valid]) };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        ProtocolGameCandidate issued = (await session.DiscoverGamesAsync()).Single();
        IPlanInspectionSession bound = session.BindToGame(issued);

        await session.DisposeAsync();
        await session.DisposeAsync();
        client.DisposeCalls.Should().Be(0, "only the transferred child owns backend cleanup");

        await bound.DisposeAsync();
        await bound.DisposeAsync();
        await session.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task TerminalPlanRejectionCleansUpBeforePublishingAndRevokesTheChild()
    {
        ProtocolGameCandidate valid = Candidate("terminal", LinuxGameFolderStatus.Valid);
        InstallerReadOnlyPlanRejection terminal = new(
            ProtocolPrePlanErrorCode.InspectionFailed,
            ProtocolNextAction.StartNewSession,
            true
        );
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([valid]),
            Inspection = (_, _, _) => Task.FromResult<InstallerReadOnlyPlanResult>(terminal)
        };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());

        InstallerReadOnlyPlanResult result = await bound.InspectPlanAsync(InstallerOperation.Backup);

        result.Should().BeSameAs(terminal);
        client.DisposeCalls.Should().Be(1, "terminal cleanup must complete before the result is returned");
        Func<Task> inspectAgain = () => bound.InspectPlanAsync(InstallerOperation.Backup);
        await inspectAgain.Should().ThrowAsync<ObjectDisposedException>();
        await bound.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task CallerCancellationPreventsACompletedPlanResultFromPublishing()
    {
        (VerifiedInstallerSession session, IPlanInspectionSession bound, RecordingClient client, TaskCompletionSource resultReady) =
            await CreateBlockedPlanSessionAsync("cancelled");
        using CancellationTokenSource cancellation = new();
        Task<InstallerReadOnlyPlanResult> inspection = bound.InspectPlanAsync(InstallerOperation.Backup, cancellation.Token);
        await client.InspectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        resultReady.SetResult();

        await FluentActions.Awaiting(() => inspection).Should().ThrowAsync<OperationCanceledException>();
        client.DisposeCalls.Should().Be(1, "an admitted cancellation is terminal and cleanup completes before it is rethrown");
        Func<Task> retry = () => bound.InspectPlanAsync(InstallerOperation.Backup);
        await retry.Should().ThrowAsync<ObjectDisposedException>();
        client.InspectedOperations.Should().ContainSingle();
        await session.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
        await bound.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task QueuedCallerCancellationTerminatesTheActiveCommandAndDisposesOnceBeforeBothSettle()
    {
        ProtocolGameCandidate valid = Candidate("queued-cancel", LinuxGameFolderStatus.Valid);
        TaskCompletionSource firstStarted = NewCompletion();
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([valid]),
            Inspection = async (_, operation, cancellationToken) =>
            {
                firstStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Plan(operation);
            }
        };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        Task<InstallerReadOnlyPlanResult> active = bound.InspectPlanAsync(InstallerOperation.Backup);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using CancellationTokenSource queuedCancellation = new();
        Task<InstallerReadOnlyPlanResult> queued = bound.InspectPlanAsync(InstallerOperation.Backup, queuedCancellation.Token);
        queuedCancellation.Cancel();

        await FluentActions.Awaiting(() => queued).Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(() => active).Should().ThrowAsync<ObjectDisposedException>();
        client.InspectedOperations.Should().ContainSingle();
        client.DisposeCalls.Should().Be(1);
        await bound.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task ClientFailureTerminatesAndCleansUpBeforeRethrowAndRetryFailsLocally()
    {
        ProtocolGameCandidate valid = Candidate("client-failure", LinuxGameFolderStatus.Valid);
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([valid]),
            Inspection = (_, _, _) => throw new InstallerProtocolClientException("synthetic bounded timeout")
        };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());

        Func<Task> inspect = () => bound.InspectPlanAsync(InstallerOperation.Backup);
        await inspect.Should().ThrowAsync<InstallerProtocolClientException>().WithMessage("synthetic bounded timeout");
        client.DisposeCalls.Should().Be(1);
        await inspect.Should().ThrowAsync<ObjectDisposedException>();
        client.InspectedOperations.Should().ContainSingle();
        await bound.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task UnexpectedPlanClientExceptionAlsoTerminatesAndCleansUpBeforeRethrow()
    {
        ProtocolGameCandidate valid = Candidate("unexpected-client-failure", LinuxGameFolderStatus.Valid);
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([valid]),
            Inspection = (_, _, _) => throw new InvalidOperationException("synthetic unexpected client failure")
        };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());

        Func<Task> inspect = () => bound.InspectPlanAsync(InstallerOperation.Backup);
        await inspect.Should().ThrowAsync<InvalidOperationException>().WithMessage("synthetic unexpected client failure");
        client.DisposeCalls.Should().Be(1);
        await inspect.Should().ThrowAsync<ObjectDisposedException>();
        client.InspectedOperations.Should().ContainSingle();
        await bound.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task SessionFaultTakesPrecedenceOverCallerCancellationBeforeResultPublication()
    {
        (_, IPlanInspectionSession bound, RecordingClient client, TaskCompletionSource resultReady) =
            await CreateBlockedPlanSessionAsync("fault-over-cancel");
        using CancellationTokenSource cancellation = new();
        Task<InstallerReadOnlyPlanResult> inspection = bound.InspectPlanAsync(InstallerOperation.Backup, cancellation.Token);
        await client.InspectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        client.Fault.SetResult(new InstallerProtocolClientException("synthetic fault"));
        resultReady.SetResult();

        await FluentActions.Awaiting(() => inspection)
            .Should().ThrowAsync<InstallerProtocolClientException>()
            .WithMessage("*faulted before the plan result*");
        client.DisposeCalls.Should().Be(1);
        Func<Task> retry = () => bound.InspectPlanAsync(InstallerOperation.Backup);
        await retry.Should().ThrowAsync<ObjectDisposedException>();
        client.InspectedOperations.Should().ContainSingle();
        await bound.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task ChildDisposalTakesPrecedenceOverFaultAndCancellationBeforeResultPublication()
    {
        (_, IPlanInspectionSession bound, RecordingClient client, TaskCompletionSource resultReady) =
            await CreateBlockedPlanSessionAsync("dispose-over-terminal-signals");
        using CancellationTokenSource cancellation = new();
        Task<InstallerReadOnlyPlanResult> inspection = bound.InspectPlanAsync(InstallerOperation.Backup, cancellation.Token);
        await client.InspectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task disposal = bound.DisposeAsync().AsTask();
        cancellation.Cancel();
        client.Fault.SetResult(new InstallerProtocolClientException("synthetic fault"));
        resultReady.SetResult();

        await FluentActions.Awaiting(() => inspection).Should().ThrowAsync<ObjectDisposedException>();
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
        client.DisposeCalls.Should().Be(1);
    }

    private static async Task<(
        VerifiedInstallerSession Session,
        IPlanInspectionSession Bound,
        RecordingClient Client,
        TaskCompletionSource ResultReady
    )> CreateBlockedPlanSessionAsync(string suffix)
    {
        ProtocolGameCandidate valid = Candidate(suffix, LinuxGameFolderStatus.Valid);
        TaskCompletionSource resultReady = NewCompletion();
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([valid]),
            Inspection = async (_, _, _) =>
            {
                await resultReady.Task;
                return Plan(InstallerOperation.Backup);
            }
        };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        return (session, bound, client, resultReady);
    }

    private static InstallerReadOnlyPlanSuccess Plan(InstallerOperation operation)
    {
        InstallerPlanRelease release = new("test-tag", "test-version");
        return new(
            operation,
            ObservedInstallState.KnownUnmodified,
            release,
            release,
            false,
            [],
            ProtocolRecommendedDefault.Cancel,
            true,
            [],
            [],
            [],
            0
        );
    }

    [Test]
    public async Task BoundCandidateApprovalContainsExactReferencesReplacesTheIssuedSetAndClearsItOnRejection()
    {
        InstallerReadOnlyPlanCandidate first = CandidateCapability('4', "mods/first.dll", false);
        InstallerReadOnlyPlanCandidate second = CandidateCapability('5', "mods/second.dll", true);
        InstallerReadOnlyPlanCandidate replacement = CandidateCapability('6', "mods/second.dll", true);
        int approval = 0;
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("valid", LinuxGameFolderStatus.Valid)]),
            Inspection = (_, operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(Plan(operation) with { Candidates = [first, second] }),
            Approval = (_, _) => Task.FromResult<InstallerReadOnlyPlanResult>(++approval == 1
                ? Plan(InstallerOperation.Update) with { Candidates = [replacement] }
                : new InstallerReadOnlyPlanRejection(ProtocolPrePlanErrorCode.CandidateApprovalFailed, ProtocolNextAction.InspectAgain, false))
        };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await bound.InspectPlanAsync(InstallerOperation.Update);

        InstallerReadOnlyPlanCandidate foreign = CandidateCapability('9', "mods/first.dll", false);
        await FluentActions.Awaiting(() => bound.ApprovePlanCandidatesAsync([])).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => bound.ApprovePlanCandidatesAsync([plan.Candidates[0], plan.Candidates[0]])).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => bound.ApprovePlanCandidatesAsync([foreign])).Should().ThrowAsync<ArgumentException>();
        client.ApprovedCandidates.Should().BeEmpty("invalid exact-reference sets must stay above the client boundary");

        InstallerReadOnlyPlanSuccess revised = (InstallerReadOnlyPlanSuccess)await bound.ApprovePlanCandidatesAsync([plan.Candidates[1]]);
        revised.Candidates.Should().Equal(replacement);
        await FluentActions.Awaiting(() => bound.ApprovePlanCandidatesAsync([plan.Candidates[0]])).Should().ThrowAsync<ArgumentException>();
        client.ApprovedCandidates.Should().ContainSingle();

        (await bound.ApprovePlanCandidatesAsync([replacement])).Should().BeOfType<InstallerReadOnlyPlanRejection>();
        await FluentActions.Awaiting(() => bound.ApprovePlanCandidatesAsync([replacement])).Should().ThrowAsync<ArgumentException>();
        client.ApprovedCandidates.Should().HaveCount(2);

        await bound.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task BoundCandidateApprovalFailureTerminatesAndDisposesExactlyOnce()
    {
        InstallerReadOnlyPlanCandidate candidate = CandidateCapability('4', "mods/first.dll", false);
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("valid", LinuxGameFolderStatus.Valid)]),
            Inspection = (_, operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(Plan(operation) with { Candidates = [candidate] }),
            Approval = (_, _) => throw new InstallerProtocolClientException("sanitized failure")
        };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await bound.InspectPlanAsync(InstallerOperation.Install);

        await FluentActions.Awaiting(() => bound.ApprovePlanCandidatesAsync([plan.Candidates.Single()]))
            .Should().ThrowAsync<InstallerProtocolClientException>();
        await bound.DisposeAsync();
        await session.DisposeAsync();

        client.DisposeCalls.Should().Be(1);
        await FluentActions.Awaiting(() => bound.InspectPlanAsync(InstallerOperation.Install)).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Test]
    public async Task BoundCandidateApprovalRejectsAClientWhichReusesAnOldCapabilityReference()
    {
        InstallerReadOnlyPlanCandidate candidate = CandidateCapability('4', "mods/first.dll", false);
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("valid", LinuxGameFolderStatus.Valid)]),
            Inspection = (_, operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(Plan(operation) with { Candidates = [candidate] }),
            Approval = (_, _) => Task.FromResult<InstallerReadOnlyPlanResult>(Plan(InstallerOperation.Install) with { Candidates = [candidate] })
        };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await bound.InspectPlanAsync(InstallerOperation.Install);

        await FluentActions.Awaiting(() => bound.ApprovePlanCandidatesAsync([plan.Candidates.Single()]))
            .Should().ThrowAsync<InstallerProtocolClientException>();

        client.DisposeCalls.Should().Be(1);
    }

    private static InstallerReadOnlyPlanCandidate CandidateCapability(char id, string path, bool selected) => new(new ProtocolPlanCandidate(
        ProtocolCandidateId.Parse(new string(id, 32)),
        FileReplacementCandidateReason.ModifiedReceiptOwned,
        FileReplacementCandidateDisposition.Replace,
        path,
        new string('a', 64),
        123,
        420,
        new string('b', 64),
        selected,
        "private evidence"
    ));

    private static ProtocolGameCandidate Candidate(string suffix, LinuxGameFolderStatus status)
        => new($"/games/{suffix}", status, $"Stardew Valley {suffix}");

    private static ProtocolReleaseIdentity CreateRelease() => GameDiscoveryControllerTests.Release();

    private static TaskCompletionSource NewCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewCompletion<T>()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class RecordingClient : IInstallerProtocolClient
    {
        public TaskCompletionSource<InstallerProtocolClientException> Fault { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource InspectionStarted { get; } = NewCompletion();
        public Func<CancellationToken, Task<IReadOnlyList<ProtocolGameCandidate>>> Discovery { get; init; } =
            _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([]);
        public Func<string, CancellationToken, Task<ProtocolGameCandidate>> Validation { get; init; } =
            (path, _) => Task.FromResult(Candidate(path.GetHashCode(StringComparison.Ordinal).ToString(), LinuxGameFolderStatus.Valid));
        public Func<string, InstallerOperation, CancellationToken, Task<InstallerReadOnlyPlanResult>> Inspection { get; init; } =
            (_, operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(Plan(operation));
        public Func<IReadOnlyList<InstallerReadOnlyPlanCandidate>, CancellationToken, Task<InstallerReadOnlyPlanResult>> Approval { get; init; } =
            (_, _) => throw new AssertionException("Candidate approval wasn't expected.");
        public List<string> InspectedPaths { get; } = [];
        public List<InstallerOperation> InspectedOperations { get; } = [];
        public List<IReadOnlyList<InstallerReadOnlyPlanCandidate>> ApprovedCandidates { get; } = [];
        public int DiscoverCalls { get; private set; }
        public int ValidateCalls { get; private set; }
        public int DisposeCalls { get; private set; }

        public Task<InstallerProtocolClientException> SessionFaulted => this.Fault.Task;

        public Task<HandshakeEvent> HandshakeAsync(
            string clientName,
            string clientVersion,
            CancellationToken cancellationToken = default
        ) => throw new AssertionException("A verified-session test must not handshake again.");

        public Task<InstallerPackageOpenResult> OpenPackageAsync(
            InstallerPackageOpenInput package,
            CancellationToken cancellationToken = default
        ) => throw new AssertionException("A verified-session test must not open another package.");

        public Task<IReadOnlyList<ProtocolGameCandidate>> DiscoverGamesAsync(CancellationToken cancellationToken = default)
        {
            this.DiscoverCalls++;
            return this.Discovery(cancellationToken);
        }

        public Task<ProtocolGameCandidate> ValidateGameAsync(
            string canonicalPath,
            CancellationToken cancellationToken = default
        )
        {
            this.ValidateCalls++;
            return this.Validation(canonicalPath, cancellationToken);
        }

        public Task<InstallerReadOnlyPlanResult> InspectPlanAsync(
            string canonicalGamePath,
            InstallerOperation operation,
            CancellationToken cancellationToken = default
        )
        {
            this.InspectedPaths.Add(canonicalGamePath);
            this.InspectedOperations.Add(operation);
            this.InspectionStarted.TrySetResult();
            return this.Inspection(canonicalGamePath, operation, cancellationToken);
        }

        public Task<InstallerReadOnlyPlanResult> ApprovePlanCandidatesAsync(
            IReadOnlyList<InstallerReadOnlyPlanCandidate> candidates,
            CancellationToken cancellationToken = default
        )
        {
            this.ApprovedCandidates.Add(candidates.ToArray());
            return this.Approval(candidates, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            this.DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }
}
