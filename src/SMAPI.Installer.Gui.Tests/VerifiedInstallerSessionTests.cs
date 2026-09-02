using System.Threading.Channels;
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

        InstallerReadOnlyPlanSuccess safe = actual.Should().BeOfType<InstallerReadOnlyPlanSuccess>().Which;
        safe.Should().NotBeSameAs(expected);
        safe.Should().BeEquivalentTo(
            (InstallerReadOnlyPlanSuccess)expected,
            options => options.Excluding(member => member.Name == nameof(InstallerReadOnlyPlanSuccess.Confirmation))
        );
        safe.Confirmation.Should().NotBeNull().And.NotBeSameAs(((InstallerReadOnlyPlanSuccess)expected).Confirmation, "the bound session remints layer-local authority");
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
    public async Task BoundRecoveryCatalogRemintsExactPointsAndUsesOnlyThePrivateBoundPath()
    {
        InstallerRecoveryPoint backendPoint = RecoveryPoint(1, current: true);
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("rollback-remint", LinuxGameFolderStatus.Valid)]),
            RecoveryCatalog = (_, _) => Task.FromResult<InstallerRecoveryCatalogResult>(new InstallerRecoveryCatalogSuccess([backendPoint])),
            RollbackInspection = (_, point, _) =>
            {
                point.Should().BeSameAs(backendPoint);
                return Task.FromResult<InstallerReadOnlyPlanResult>(Plan(InstallerOperation.Rollback));
            }
        };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        await using IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());

        BoundInstallerRecoveryCatalogSuccess catalog = (BoundInstallerRecoveryCatalogSuccess)await bound.ListRecoveriesAsync();
        BoundInstallerRecoveryPoint point = catalog.RecoveryPoints.Should().ContainSingle().Subject;

        client.RecoveryCatalogPaths.Should().Equal("/games/rollback-remint");
        point.Ordinal.Should().Be(1);
        point.IsCurrent.Should().BeTrue();
        point.RestoreTarget.Should().BeOfType<BoundInstallerRecoveryReleaseTarget>();
        typeof(BoundInstallerRecoveryPoint).GetProperties()
            .Should().NotContain(property => property.PropertyType == typeof(InstallerRecoveryPoint));

        BoundInstallerRecoveryPoint reconstructed = new(
            point.Ordinal,
            point.IsCurrent,
            point.IsUserCheckpoint,
            point.OriginOperation,
            point.RestoreTarget
        );
        Func<Task> foreign = () => bound.InspectRollbackAsync(reconstructed);
        await foreign.Should().ThrowAsync<ArgumentException>();
        client.RollbackInspections.Should().BeEmpty();

        InstallerReadOnlyPlanSuccess rollback = (InstallerReadOnlyPlanSuccess)await bound.InspectRollbackAsync(point);
        rollback.Operation.Should().Be(InstallerOperation.Rollback);
        rollback.Confirmation.Should().NotBeNull();
        client.RollbackInspections.Should().ContainSingle().Which.Path.Should().Be("/games/rollback-remint");
    }

    [Test]
    public async Task RecoveryRefreshAndNormalInspectionRevokeEveryOlderWrapperPoint()
    {
        InstallerRecoveryPoint first = RecoveryPoint(1, current: true);
        InstallerRecoveryPoint second = RecoveryPoint(1, current: true);
        Queue<InstallerRecoveryPoint> catalogs = new([first, second]);
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("rollback-refresh", LinuxGameFolderStatus.Valid)]),
            RecoveryCatalog = (_, _) => Task.FromResult<InstallerRecoveryCatalogResult>(new InstallerRecoveryCatalogSuccess([catalogs.Dequeue()])),
            RollbackInspection = (_, _, _) => Task.FromResult<InstallerReadOnlyPlanResult>(Plan(InstallerOperation.Rollback))
        };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        await using IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        BoundInstallerRecoveryPoint old = ((BoundInstallerRecoveryCatalogSuccess)await bound.ListRecoveriesAsync()).RecoveryPoints.Single();
        BoundInstallerRecoveryPoint current = ((BoundInstallerRecoveryCatalogSuccess)await bound.ListRecoveriesAsync()).RecoveryPoints.Single();

        Func<Task> stale = () => bound.InspectRollbackAsync(old);
        await stale.Should().ThrowAsync<ArgumentException>();
        await bound.InspectPlanAsync(InstallerOperation.Backup);
        Func<Task> staleAfterOrdinary = () => bound.InspectRollbackAsync(current);
        await staleAfterOrdinary.Should().ThrowAsync<ArgumentException>();
        Func<Task> relist = () => bound.ListRecoveriesAsync();
        await relist.Should().ThrowAsync<InvalidOperationException>().WithMessage("*before any plan inspection*");
        client.RollbackInspections.Should().BeEmpty();
    }

    [Test]
    public async Task PreCancelledRollbackSelectionPreservesTheExactPointForOneAdmission()
    {
        InstallerRecoveryPoint backendPoint = RecoveryPoint(1, current: true);
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("rollback-cancel", LinuxGameFolderStatus.Valid)]),
            RecoveryCatalog = (_, _) => Task.FromResult<InstallerRecoveryCatalogResult>(new InstallerRecoveryCatalogSuccess([backendPoint])),
            RollbackInspection = (_, _, _) => Task.FromResult<InstallerReadOnlyPlanResult>(Plan(InstallerOperation.Rollback))
        };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        await using IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        BoundInstallerRecoveryPoint point = ((BoundInstallerRecoveryCatalogSuccess)await bound.ListRecoveriesAsync()).RecoveryPoints.Single();
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        Func<Task> first = () => bound.InspectRollbackAsync(point, cancelled.Token);
        await first.Should().ThrowAsync<OperationCanceledException>();
        client.RollbackInspections.Should().BeEmpty();

        _ = await bound.InspectRollbackAsync(point);
        client.RollbackInspections.Should().ContainSingle();
    }

    [Test]
    public async Task NonterminalRollbackRejectionRequiresFreshCatalogAndReselection()
    {
        InstallerRecoveryPoint firstBackendPoint = RecoveryPoint(1, current: true);
        InstallerRecoveryPoint secondBackendPoint = RecoveryPoint(1, current: true);
        Queue<InstallerRecoveryPoint> catalogs = new([firstBackendPoint, secondBackendPoint]);
        int inspections = 0;
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("rollback-retry", LinuxGameFolderStatus.Valid)]),
            RecoveryCatalog = (_, _) => Task.FromResult<InstallerRecoveryCatalogResult>(new InstallerRecoveryCatalogSuccess([catalogs.Dequeue()])),
            RollbackInspection = (_, point, _) => Task.FromResult<InstallerReadOnlyPlanResult>(inspections++ == 0
                ? new InstallerReadOnlyPlanRejection(ProtocolPrePlanErrorCode.InspectionFailed, ProtocolNextAction.InspectAgain, false)
                : Plan(InstallerOperation.Rollback))
        };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        await using IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        BoundInstallerRecoveryPoint stale = ((BoundInstallerRecoveryCatalogSuccess)await bound.ListRecoveriesAsync()).RecoveryPoints.Single();

        InstallerReadOnlyPlanRejection rejection = (InstallerReadOnlyPlanRejection)await bound.InspectRollbackAsync(stale);
        rejection.NextAction.Should().Be(ProtocolNextAction.InspectAgain);
        Func<Task> reuse = () => bound.InspectRollbackAsync(stale);
        await reuse.Should().ThrowAsync<ArgumentException>();

        BoundInstallerRecoveryPoint fresh = ((BoundInstallerRecoveryCatalogSuccess)await bound.ListRecoveriesAsync()).RecoveryPoints.Single();
        InstallerReadOnlyPlanSuccess retry = (InstallerReadOnlyPlanSuccess)await bound.InspectRollbackAsync(fresh);
        retry.Operation.Should().Be(InstallerOperation.Rollback);
        client.RollbackInspections.Select(call => call.Point).Should().Equal(firstBackendPoint, secondBackendPoint);
    }

    [Test]
    public async Task ConcurrentRollbackSelectionHasOneWinnerWithoutDestroyingItsConfirmation()
    {
        InstallerRecoveryPoint backendPoint = RecoveryPoint(1, current: true);
        TaskCompletionSource<InstallerReadOnlyPlanResult> result = NewCompletion<InstallerReadOnlyPlanResult>();
        InstallerPlanConfirmation backendConfirmation = new();
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("rollback-race", LinuxGameFolderStatus.Valid)]),
            RecoveryCatalog = (_, _) => Task.FromResult<InstallerRecoveryCatalogResult>(new InstallerRecoveryCatalogSuccess([backendPoint])),
            RollbackInspection = async (_, _, _) => await result.Task
        };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        await using IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        BoundInstallerRecoveryPoint point = ((BoundInstallerRecoveryCatalogSuccess)await bound.ListRecoveriesAsync()).RecoveryPoints.Single();

        Task<InstallerReadOnlyPlanResult> winner = bound.InspectRollbackAsync(point);
        await WaitUntilAsync(() => client.RollbackInspections.Count == 1);
        Task<InstallerReadOnlyPlanResult> loser = bound.InspectRollbackAsync(point);
        result.SetResult(Plan(InstallerOperation.Rollback) with { Confirmation = backendConfirmation });
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await winner;
        Func<Task> awaitLoser = async () => await loser;
        await awaitLoser.Should().ThrowAsync<ArgumentException>();

        IConfirmedInstallerSession confirmed = await bound.ConfirmPlanAsync(plan.Confirmation!);
        client.ConfirmedPlans.Should().ContainSingle().Which.Should().BeSameAs(backendConfirmation);
        await confirmed.DisposeAsync();
    }

    [Test]
    public async Task QueuedRollbackLoserCannotInvalidateWinnerDuringImmediateConfirmation()
    {
        InstallerRecoveryPoint backendPoint = RecoveryPoint(1, current: true);
        TaskCompletionSource<InstallerReadOnlyPlanResult> inspectionResult = NewCompletion<InstallerReadOnlyPlanResult>();
        TaskCompletionSource<InstallerConfirmedPlanAuthority> confirmationResult = NewCompletion<InstallerConfirmedPlanAuthority>();
        using ManualResetEventSlim winnerEnteredGate = new();
        using ManualResetEventSlim releaseWinner = new();
        using ManualResetEventSlim loserEnteredGate = new();
        using ManualResetEventSlim releaseLoser = new();
        InstallerPlanConfirmation backendConfirmation = new();
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("rollback-confirm-race", LinuxGameFolderStatus.Valid)]),
            RecoveryCatalog = (_, _) => Task.FromResult<InstallerRecoveryCatalogResult>(new InstallerRecoveryCatalogSuccess([backendPoint])),
            RollbackInspection = async (_, _, _) => await inspectionResult.Task,
            Confirmation = (_, _) => confirmationResult.Task
        };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        int gateAdmissions = 0;
        session.BeforeRollbackAdmissionForTesting = () =>
        {
            int admission = Interlocked.Increment(ref gateAdmissions);
            if (admission == 1)
            {
                winnerEnteredGate.Set();
                releaseWinner.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue();
            }
            else if (admission == 2)
            {
                loserEnteredGate.Set();
                releaseLoser.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue();
            }
        };
        await using IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        BoundInstallerRecoveryPoint point = ((BoundInstallerRecoveryCatalogSuccess)await bound.ListRecoveriesAsync()).RecoveryPoints.Single();

        Task<InstallerReadOnlyPlanResult> winner = Task.Run(async () => await bound.InspectRollbackAsync(point));
        winnerEnteredGate.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue();
        Task<InstallerReadOnlyPlanResult> loser = bound.InspectRollbackAsync(point);
        releaseWinner.Set();
        await WaitUntilAsync(() => client.RollbackInspections.Count == 1);
        inspectionResult.SetResult(Plan(InstallerOperation.Rollback) with { Confirmation = backendConfirmation });
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await winner;
        loserEnteredGate.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue();

        Task<IConfirmedInstallerSession> confirming = bound.ConfirmPlanAsync(plan.Confirmation!);
        releaseLoser.Set();
        Func<Task> awaitLoser = async () => await loser;
        await awaitLoser.Should().ThrowAsync<ArgumentException>();
        await client.ConfirmationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        client.DisposeCalls.Should().Be(0);

        confirmationResult.SetResult(new InstallerConfirmedPlanAuthority());
        await using IConfirmedInstallerSession confirmed = await confirming;
        client.ConfirmedPlans.Should().ContainSingle().Which.Should().BeSameAs(backendConfirmation);
        client.DisposeCalls.Should().Be(0);
    }

    [Test]
    public async Task QueuedOrdinaryInspectionCannotInvalidateRollbackWinnerDuringImmediateConfirmation()
    {
        InstallerRecoveryPoint backendPoint = RecoveryPoint(1, current: true);
        TaskCompletionSource<InstallerReadOnlyPlanResult> rollbackResult = NewCompletion<InstallerReadOnlyPlanResult>();
        TaskCompletionSource<InstallerConfirmedPlanAuthority> confirmationResult = NewCompletion<InstallerConfirmedPlanAuthority>();
        using ManualResetEventSlim rollbackEnteredGate = new();
        using ManualResetEventSlim releaseRollback = new();
        using ManualResetEventSlim ordinaryEnteredGate = new();
        using ManualResetEventSlim releaseOrdinary = new();
        InstallerPlanConfirmation backendConfirmation = new();
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("rollback-ordinary-race", LinuxGameFolderStatus.Valid)]),
            RecoveryCatalog = (_, _) => Task.FromResult<InstallerRecoveryCatalogResult>(new InstallerRecoveryCatalogSuccess([backendPoint])),
            RollbackInspection = async (_, _, _) => await rollbackResult.Task,
            Confirmation = (_, _) => confirmationResult.Task
        };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        session.BeforeRollbackAdmissionForTesting = () =>
        {
            rollbackEnteredGate.Set();
            releaseRollback.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue();
        };
        session.BeforePlanAdmissionForTesting = () =>
        {
            ordinaryEnteredGate.Set();
            releaseOrdinary.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue();
        };
        await using IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        BoundInstallerRecoveryPoint point = ((BoundInstallerRecoveryCatalogSuccess)await bound.ListRecoveriesAsync()).RecoveryPoints.Single();

        Task<InstallerReadOnlyPlanResult> rollback = Task.Run(async () => await bound.InspectRollbackAsync(point));
        rollbackEnteredGate.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue();
        Task<InstallerReadOnlyPlanResult> ordinary = bound.InspectPlanAsync(InstallerOperation.Backup);
        releaseRollback.Set();
        await WaitUntilAsync(() => client.RollbackInspections.Count == 1);
        rollbackResult.SetResult(Plan(InstallerOperation.Rollback) with { Confirmation = backendConfirmation });
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await rollback;
        ordinaryEnteredGate.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue();

        Task<IConfirmedInstallerSession> confirming = bound.ConfirmPlanAsync(plan.Confirmation!);
        releaseOrdinary.Set();
        Func<Task> awaitOrdinary = async () => await ordinary;
        await awaitOrdinary.Should().ThrowAsync<InvalidOperationException>();
        await client.ConfirmationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        client.InspectedOperations.Should().BeEmpty();
        client.DisposeCalls.Should().Be(0);

        confirmationResult.SetResult(new InstallerConfirmedPlanAuthority());
        await using IConfirmedInstallerSession confirmed = await confirming;
        client.ConfirmedPlans.Should().ContainSingle().Which.Should().BeSameAs(backendConfirmation);
        client.DisposeCalls.Should().Be(0);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task QueuedRecoveryListOrCandidateApprovalCannotInvalidateRollbackWinnerDuringImmediateConfirmation(bool approval)
    {
        InstallerRecoveryPoint backendPoint = RecoveryPoint(1, current: true);
        TaskCompletionSource<InstallerReadOnlyPlanResult> rollbackResult = NewCompletion<InstallerReadOnlyPlanResult>();
        TaskCompletionSource<InstallerConfirmedPlanAuthority> confirmationResult = NewCompletion<InstallerConfirmedPlanAuthority>();
        using ManualResetEventSlim rollbackEnteredGate = new();
        using ManualResetEventSlim releaseRollback = new();
        using ManualResetEventSlim queuedEnteredGate = new();
        using ManualResetEventSlim releaseQueued = new();
        InstallerPlanConfirmation backendConfirmation = new();
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("rollback-adjacent-race", LinuxGameFolderStatus.Valid)]),
            RecoveryCatalog = (_, _) => Task.FromResult<InstallerRecoveryCatalogResult>(new InstallerRecoveryCatalogSuccess([backendPoint])),
            RollbackInspection = async (_, _, _) => await rollbackResult.Task,
            Confirmation = (_, _) => confirmationResult.Task
        };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        await using IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        BoundInstallerRecoveryPoint point = ((BoundInstallerRecoveryCatalogSuccess)await bound.ListRecoveriesAsync()).RecoveryPoints.Single();
        session.BeforeRollbackAdmissionForTesting = () =>
        {
            rollbackEnteredGate.Set();
            releaseRollback.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue();
        };
        Action queuedHook = () =>
        {
            queuedEnteredGate.Set();
            releaseQueued.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue();
        };
        if (approval)
            session.BeforeCandidateApprovalAdmissionForTesting = queuedHook;
        else
            session.BeforeRecoveryListAdmissionForTesting = queuedHook;

        Task<InstallerReadOnlyPlanResult> rollback = Task.Run(async () => await bound.InspectRollbackAsync(point));
        rollbackEnteredGate.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue();
        Task queued = approval
            ? bound.ApprovePlanCandidatesAsync([CandidateCapability('9', "mods/stale.dll", false)])
            : bound.ListRecoveriesAsync();
        releaseRollback.Set();
        await WaitUntilAsync(() => client.RollbackInspections.Count == 1);
        rollbackResult.SetResult(Plan(InstallerOperation.Rollback) with { Confirmation = backendConfirmation });
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await rollback;
        queuedEnteredGate.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue();

        Task<IConfirmedInstallerSession> confirming = bound.ConfirmPlanAsync(plan.Confirmation!);
        releaseQueued.Set();
        Func<Task> awaitQueued = async () => await queued;
        await awaitQueued.Should().ThrowAsync<ObjectDisposedException>();
        await client.ConfirmationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        client.RecoveryCatalogPaths.Should().ContainSingle("/games/rollback-adjacent-race");
        client.ApprovedCandidates.Should().BeEmpty();
        client.DisposeCalls.Should().Be(0);

        confirmationResult.SetResult(new InstallerConfirmedPlanAuthority());
        await using IConfirmedInstallerSession confirmed = await confirming;
        client.ConfirmedPlans.Should().ContainSingle().Which.Should().BeSameAs(backendConfirmation);
        client.DisposeCalls.Should().Be(0);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ThrowingAdmissionHookReleasesCommandGateBeforeCleanup(bool rollback)
    {
        InstallerRecoveryPoint backendPoint = RecoveryPoint(1, current: true);
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("hook-failure", LinuxGameFolderStatus.Valid)]),
            RecoveryCatalog = (_, _) => Task.FromResult<InstallerRecoveryCatalogResult>(new InstallerRecoveryCatalogSuccess([backendPoint]))
        };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        BoundInstallerRecoveryPoint? point = rollback
            ? ((BoundInstallerRecoveryCatalogSuccess)await bound.ListRecoveriesAsync()).RecoveryPoints.Single()
            : null;
        Action failure = () => throw new InvalidOperationException("synthetic admission-hook failure");
        if (rollback)
            session.BeforeRollbackAdmissionForTesting = failure;
        else
            session.BeforePlanAdmissionForTesting = failure;

        Func<Task> action = () => (rollback
            ? bound.InspectRollbackAsync(point!)
            : bound.InspectPlanAsync(InstallerOperation.Backup)).WaitAsync(TimeSpan.FromSeconds(2));

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("synthetic admission-hook failure");
        client.DisposeCalls.Should().Be(1);
        await bound.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task RollbackRejectsCandidateAuthorityAndCleansTheSessionExactlyOnce()
    {
        InstallerRecoveryPoint backendPoint = RecoveryPoint(1, current: true);
        InstallerReadOnlyPlanCandidate candidate = CandidateCapability('9', "mods/rollback.dll", false);
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("rollback-candidate", LinuxGameFolderStatus.Valid)]),
            RecoveryCatalog = (_, _) => Task.FromResult<InstallerRecoveryCatalogResult>(new InstallerRecoveryCatalogSuccess([backendPoint])),
            RollbackInspection = (_, _, _) => Task.FromResult<InstallerReadOnlyPlanResult>(Plan(InstallerOperation.Rollback) with
            {
                CandidateCounts = [new(FileReplacementCandidateReason.ModifiedReceiptOwned, FileReplacementCandidateDisposition.Replace, false, 1)],
                Candidates = [candidate]
            })
        };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        BoundInstallerRecoveryPoint point = ((BoundInstallerRecoveryCatalogSuccess)await bound.ListRecoveriesAsync()).RecoveryPoints.Single();

        Func<Task> inspect = () => bound.InspectRollbackAsync(point);
        await inspect.Should().ThrowAsync<InstallerProtocolClientException>().WithMessage("*unexpected candidate*");
        client.DisposeCalls.Should().Be(1);
        await bound.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task ListRollbackConfirmExecuteReusesTheExactExistingOwnershipPipeline()
    {
        InstallerRecoveryPoint backendPoint = RecoveryPoint(1, current: true);
        InstallerPlanConfirmation backendConfirmation = new();
        InstallerConfirmedPlanAuthority executionAuthority = new();
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("rollback-pipeline", LinuxGameFolderStatus.Valid)]),
            RecoveryCatalog = (_, _) => Task.FromResult<InstallerRecoveryCatalogResult>(new InstallerRecoveryCatalogSuccess([backendPoint])),
            RollbackInspection = (_, _, _) => Task.FromResult<InstallerReadOnlyPlanResult>(Plan(InstallerOperation.Rollback) with { Confirmation = backendConfirmation }),
            Confirmation = (confirmation, _) =>
            {
                confirmation.Should().BeSameAs(backendConfirmation);
                return Task.FromResult(executionAuthority);
            }
        };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        BoundInstallerRecoveryPoint point = ((BoundInstallerRecoveryCatalogSuccess)await bound.ListRecoveriesAsync()).RecoveryPoints.Single();
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await bound.InspectRollbackAsync(point);

        IConfirmedInstallerSession confirmed = await bound.ConfirmPlanAsync(plan.Confirmation!);
        InstallerExecutionOperation execution = await confirmed.ExecuteAsync();
        _ = await execution.Completion;

        client.RecoveryCatalogPaths.Should().Equal("/games/rollback-pipeline");
        client.RollbackInspections.Should().ContainSingle();
        client.ConfirmedPlans.Should().ContainSingle().Which.Should().BeSameAs(backendConfirmation);
        client.ExecutedAuthorities.Should().ContainSingle().Which.Should().BeSameAs(executionAuthority);
        await confirmed.DisposeAsync();
    }

    [Test]
    public async Task RecoveryPruneRemintsExactPointAndConfirmationWithoutLeakingClientAuthority()
    {
        InstallerRecoveryPoint[] backendPoints = [RecoveryPoint(1, current: true), RecoveryPoint(2, current: false), RecoveryPoint(3, current: false)];
        InstallerRecoveryPruneConfirmation backendConfirmation = new();
        InstallerRecoveryPrunePlanSuccess backendPlan = RecoveryPrunePlan() with { Confirmation = backendConfirmation };
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("prune-remint", LinuxGameFolderStatus.Valid)]),
            RecoveryCatalog = (_, _) => Task.FromResult<InstallerRecoveryCatalogResult>(new InstallerRecoveryCatalogSuccess(backendPoints)),
            PruneInspection = (_, point, _) =>
            {
                point.Should().BeSameAs(backendPoints[1]);
                return Task.FromResult<InstallerRecoveryPrunePlanResult>(backendPlan);
            }
        };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        await using IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        BoundInstallerRecoveryPoint point = ((BoundInstallerRecoveryCatalogSuccess)await bound.ListRecoveriesAsync()).RecoveryPoints[1];
        BoundInstallerRecoveryPoint reconstructed = new(
            point.Ordinal,
            point.IsCurrent,
            point.IsUserCheckpoint,
            point.OriginOperation,
            point.RestoreTarget
        );

        await FluentActions.Awaiting(() => bound.InspectRecoveryPruneAsync(reconstructed)).Should().ThrowAsync<ArgumentException>();
        client.PruneInspections.Should().BeEmpty();

        BoundInstallerRecoveryPrunePlanSuccess plan = (await bound.InspectRecoveryPruneAsync(point))
            .Should().BeOfType<BoundInstallerRecoveryPrunePlanSuccess>().Subject;
        plan.Should().BeEquivalentTo(backendPlan, options => options.Excluding(member => member.Name == nameof(InstallerRecoveryPrunePlanSuccess.Confirmation)));
        plan.Confirmation.Should().NotBeNull().And.NotBeSameAs(backendConfirmation);
        client.PruneInspections.Should().ContainSingle().Which.Should().Be(("/games/prune-remint", backendPoints[1]));

        foreach (Type projection in new[]
        {
            typeof(BoundInstallerRecoveryPrunePlanSuccess),
            typeof(BoundInstallerRecoveryPrunePlanRejection),
            typeof(BoundInstallerRecoveryPruneConfirmation)
        })
        {
            projection.GetProperties().Should().NotContain(property =>
                property.PropertyType == typeof(string)
                || property.Name.Contains("Digest", StringComparison.OrdinalIgnoreCase)
                || property.Name.EndsWith("Id", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase)
                || property.PropertyType == typeof(InstallerRecoveryPruneConfirmation));
        }
    }

    [Test]
    public async Task RecoveryPruneRejectionRequiresFreshCatalogAndExactReselection()
    {
        InstallerRecoveryPoint firstPoint = RecoveryPoint(1, current: true);
        InstallerRecoveryPoint secondPoint = RecoveryPoint(1, current: true);
        Queue<InstallerRecoveryPoint> catalogs = new([firstPoint, secondPoint]);
        int inspections = 0;
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("prune-retry", LinuxGameFolderStatus.Valid)]),
            RecoveryCatalog = (_, _) => Task.FromResult<InstallerRecoveryCatalogResult>(new InstallerRecoveryCatalogSuccess([catalogs.Dequeue()])),
            PruneInspection = (_, _, _) => Task.FromResult<InstallerRecoveryPrunePlanResult>(inspections++ == 0
                ? new InstallerRecoveryPrunePlanRejection(ProtocolPrePlanErrorCode.NothingToPrune, ProtocolNextAction.ListRecoveries, false)
                : RecoveryPrunePlan(retainNewest: 1, retainedCount: 1, removedCount: 0, auxiliaryCleanupPlanned: true))
        };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        await using IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        BoundInstallerRecoveryPoint stale = ((BoundInstallerRecoveryCatalogSuccess)await bound.ListRecoveriesAsync()).RecoveryPoints.Single();

        BoundInstallerRecoveryPrunePlanRejection rejection = (BoundInstallerRecoveryPrunePlanRejection)await bound.InspectRecoveryPruneAsync(stale);
        rejection.NextAction.Should().Be(ProtocolNextAction.ListRecoveries);
        await FluentActions.Awaiting(() => bound.InspectRecoveryPruneAsync(stale)).Should().ThrowAsync<ArgumentException>();

        BoundInstallerRecoveryPoint fresh = ((BoundInstallerRecoveryCatalogSuccess)await bound.ListRecoveriesAsync()).RecoveryPoints.Single();
        (await bound.InspectRecoveryPruneAsync(fresh)).Should().BeOfType<BoundInstallerRecoveryPrunePlanSuccess>();
        client.PruneInspections.Select(call => call.Point).Should().Equal(firstPoint, secondPoint);
    }

    [TestCase(HostilePrunePlan.NegativeCount)]
    [TestCase(HostilePrunePlan.WrongRetentionBoundary)]
    [TestCase(HostilePrunePlan.MissingConfirmation)]
    [TestCase(HostilePrunePlan.WrongRisk)]
    [TestCase(HostilePrunePlan.NoConfirmationRequired)]
    [TestCase(HostilePrunePlan.CleanupSmallerThanRemoved)]
    [TestCase(HostilePrunePlan.WarningOverflow)]
    [TestCase(HostilePrunePlan.WrongRecommendedDefault)]
    [TestCase(HostilePrunePlan.DuplicateRisk)]
    [TestCase(HostilePrunePlan.TrueNoOp)]
    public async Task MalformedRecoveryPruneProjectionFailsClosedAndDisposesOnce(HostilePrunePlan hostile)
    {
        InstallerRecoveryPrunePlanSuccess malformed = hostile switch
        {
            HostilePrunePlan.NegativeCount => RecoveryPrunePlan() with { RemovedCount = -1 },
            HostilePrunePlan.WrongRetentionBoundary => RecoveryPrunePlan(retainNewest: 1, retainedCount: 1, removedCount: 2),
            HostilePrunePlan.MissingConfirmation => RecoveryPrunePlan() with { Confirmation = null },
            HostilePrunePlan.WrongRisk => RecoveryPrunePlan() with { Risks = [ProtocolPlanRisk.Rollback] },
            HostilePrunePlan.NoConfirmationRequired => RecoveryPrunePlan() with { RequiresConfirmation = false },
            HostilePrunePlan.CleanupSmallerThanRemoved => RecoveryPrunePlan() with { CleanupGenerationCount = 0 },
            HostilePrunePlan.WarningOverflow => RecoveryPrunePlan() with { WarningCount = 257 },
            HostilePrunePlan.WrongRecommendedDefault => RecoveryPrunePlan() with { RecommendedDefault = (ProtocolRecommendedDefault)999 },
            HostilePrunePlan.DuplicateRisk => RecoveryPrunePlan() with { Risks = [ProtocolPlanRisk.RecoveryPrune, ProtocolPlanRisk.RecoveryPrune] },
            HostilePrunePlan.TrueNoOp => RecoveryPrunePlan(retainNewest: 3, retainedCount: 3, removedCount: 0),
            _ => throw new ArgumentOutOfRangeException(nameof(hostile), hostile, null)
        };
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("prune-malformed", LinuxGameFolderStatus.Valid)]),
            RecoveryCatalog = (_, _) => Task.FromResult<InstallerRecoveryCatalogResult>(new InstallerRecoveryCatalogSuccess([
                RecoveryPoint(1, current: true), RecoveryPoint(2, current: false), RecoveryPoint(3, current: false)
            ])),
            PruneInspection = (_, _, _) => Task.FromResult<InstallerRecoveryPrunePlanResult>(malformed)
        };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        BoundInstallerRecoveryPoint point = ((BoundInstallerRecoveryCatalogSuccess)await bound.ListRecoveriesAsync()).RecoveryPoints[1];

        await FluentActions.Awaiting(() => bound.InspectRecoveryPruneAsync(point)).Should().ThrowAsync<InstallerProtocolClientException>();
        client.DisposeCalls.Should().Be(1);
        await bound.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task AuxiliaryOnlyRecoveryPruneCanConfirmAndExecuteThroughTheReducedOwner()
    {
        InstallerRecoveryPoint[] backendPoints = [RecoveryPoint(1, current: true), RecoveryPoint(2, current: false), RecoveryPoint(3, current: false)];
        InstallerRecoveryPruneConfirmation backendConfirmation = new();
        InstallerConfirmedRecoveryPruneAuthority backendAuthority = new();
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("prune-auxiliary", LinuxGameFolderStatus.Valid)]),
            RecoveryCatalog = (_, _) => Task.FromResult<InstallerRecoveryCatalogResult>(new InstallerRecoveryCatalogSuccess(backendPoints)),
            PruneInspection = (_, _, _) => Task.FromResult<InstallerRecoveryPrunePlanResult>(
                RecoveryPrunePlan(retainNewest: 3, retainedCount: 3, removedCount: 0, auxiliaryCleanupPlanned: true) with { Confirmation = backendConfirmation }
            ),
            PruneConfirmation = (confirmation, _) =>
            {
                confirmation.Should().BeSameAs(backendConfirmation);
                return Task.FromResult(backendAuthority);
            }
        };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        BoundInstallerRecoveryPoint point = ((BoundInstallerRecoveryCatalogSuccess)await bound.ListRecoveriesAsync()).RecoveryPoints[^1];
        BoundInstallerRecoveryPrunePlanSuccess plan = (BoundInstallerRecoveryPrunePlanSuccess)await bound.InspectRecoveryPruneAsync(point);

        IConfirmedRecoveryPruneSession confirmed = await bound.ConfirmRecoveryPruneAsync(plan.Confirmation!);
        InstallerRecoveryPruneOperation operation = await confirmed.ExecuteAsync();
        (await operation.Completion).Should().BeOfType<InstallerRecoveryPruneTerminalResult>();
        client.ConfirmedPrunes.Should().ContainSingle().Which.Should().BeSameAs(backendConfirmation);
        client.ExecutedPrunes.Should().ContainSingle().Which.Should().BeSameAs(backendAuthority);
        await confirmed.DisposeAsync();
    }

    [Test]
    public async Task ForeignPruneConfirmationFailsLocallyAndPreservesTheExactCurrentCapability()
    {
        InstallerRecoveryPruneConfirmation backendConfirmation = new();
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("prune-confirm", LinuxGameFolderStatus.Valid)]),
            RecoveryCatalog = (_, _) => Task.FromResult<InstallerRecoveryCatalogResult>(new InstallerRecoveryCatalogSuccess([RecoveryPoint(1, current: true)])),
            PruneInspection = (_, _, _) => Task.FromResult<InstallerRecoveryPrunePlanResult>(
                RecoveryPrunePlan(retainNewest: 1, retainedCount: 1, removedCount: 0, auxiliaryCleanupPlanned: true) with { Confirmation = backendConfirmation }
            )
        };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        BoundInstallerRecoveryPoint point = ((BoundInstallerRecoveryCatalogSuccess)await bound.ListRecoveriesAsync()).RecoveryPoints.Single();
        BoundInstallerRecoveryPrunePlanSuccess plan = (BoundInstallerRecoveryPrunePlanSuccess)await bound.InspectRecoveryPruneAsync(point);

        await FluentActions.Awaiting(() => bound.ConfirmRecoveryPruneAsync(new BoundInstallerRecoveryPruneConfirmation())).Should().ThrowAsync<ArgumentException>();
        client.ConfirmedPrunes.Should().BeEmpty();

        await using IConfirmedRecoveryPruneSession confirmed = await bound.ConfirmRecoveryPruneAsync(plan.Confirmation!);
        client.ConfirmedPrunes.Should().ContainSingle().Which.Should().BeSameAs(backendConfirmation);
        await bound.DisposeAsync();
        await session.DisposeAsync();
        client.DisposeCalls.Should().Be(0, "only the confirmed prune owner retains cleanup authority");
    }

    [Test]
    public async Task ConcurrentPruneConfirmationPublishesExactlyOneConfirmedOwner()
    {
        TaskCompletionSource releaseConfirmation = NewCompletion();
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("prune-confirm-race", LinuxGameFolderStatus.Valid)]),
            RecoveryCatalog = (_, _) => Task.FromResult<InstallerRecoveryCatalogResult>(new InstallerRecoveryCatalogSuccess([RecoveryPoint(1, current: true)])),
            PruneInspection = (_, _, _) => Task.FromResult<InstallerRecoveryPrunePlanResult>(
                RecoveryPrunePlan(retainNewest: 1, retainedCount: 1, removedCount: 0, auxiliaryCleanupPlanned: true)
            ),
            PruneConfirmation = async (_, cancellationToken) =>
            {
                await releaseConfirmation.Task.WaitAsync(cancellationToken);
                return new InstallerConfirmedRecoveryPruneAuthority();
            }
        };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        BoundInstallerRecoveryPoint point = ((BoundInstallerRecoveryCatalogSuccess)await bound.ListRecoveriesAsync()).RecoveryPoints.Single();
        BoundInstallerRecoveryPrunePlanSuccess plan = (BoundInstallerRecoveryPrunePlanSuccess)await bound.InspectRecoveryPruneAsync(point);

        Task<IConfirmedRecoveryPruneSession> winner = bound.ConfirmRecoveryPruneAsync(plan.Confirmation!);
        await client.PruneConfirmationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await FluentActions.Awaiting(() => bound.ConfirmRecoveryPruneAsync(plan.Confirmation!)).Should().ThrowAsync<ObjectDisposedException>();
        releaseConfirmation.SetResult();

        await using IConfirmedRecoveryPruneSession confirmed = await winner;
        client.ConfirmedPrunes.Should().ContainSingle();
    }

    [Test]
    public async Task PreCancelledPruneExecutionPreservesOneExactRetryAndExecutionIsOneShot()
    {
        RecordingClient client = PrunePipelineClient("prune-execute-once");
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        BoundInstallerRecoveryPoint point = ((BoundInstallerRecoveryCatalogSuccess)await bound.ListRecoveriesAsync()).RecoveryPoints.Single();
        BoundInstallerRecoveryPrunePlanSuccess plan = (BoundInstallerRecoveryPrunePlanSuccess)await bound.InspectRecoveryPruneAsync(point);
        IConfirmedRecoveryPruneSession confirmed = await bound.ConfirmRecoveryPruneAsync(plan.Confirmation!);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        await FluentActions.Awaiting(() => confirmed.ExecuteAsync(cancelled.Token)).Should().ThrowAsync<OperationCanceledException>();
        client.ExecutedPrunes.Should().BeEmpty();

        InstallerRecoveryPruneOperation operation = await confirmed.ExecuteAsync();
        _ = await operation.Completion;
        await FluentActions.Awaiting(() => confirmed.ExecuteAsync()).Should().ThrowAsync<ObjectDisposedException>();
        client.ExecutedPrunes.Should().ContainSingle();
        await confirmed.DisposeAsync();
    }

    [Test]
    public async Task ConcurrentPruneExecutionTransfersExactlyOneOperationAndCleansUpOnce()
    {
        RecordingClient client = PrunePipelineClient("prune-execute-race");
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        BoundInstallerRecoveryPoint point = ((BoundInstallerRecoveryCatalogSuccess)await bound.ListRecoveriesAsync()).RecoveryPoints.Single();
        BoundInstallerRecoveryPrunePlanSuccess plan = (BoundInstallerRecoveryPrunePlanSuccess)await bound.InspectRecoveryPruneAsync(point);
        IConfirmedRecoveryPruneSession confirmed = await bound.ConfirmRecoveryPruneAsync(plan.Confirmation!);

        async Task<(InstallerRecoveryPruneOperation? Operation, Exception? Error)> AttemptAsync()
        {
            try { return (await confirmed.ExecuteAsync(), null); }
            catch (Exception error) { return (null, error); }
        }

        (InstallerRecoveryPruneOperation? Operation, Exception? Error)[] results = await Task.WhenAll(AttemptAsync(), AttemptAsync());
        results.Should().ContainSingle(result => result.Operation != null && result.Error == null);
        results.Should().ContainSingle(result => result.Operation == null && result.Error is ObjectDisposedException);
        client.ExecutedPrunes.Should().ContainSingle();
        await confirmed.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task PruneStartFailurePublishesStateUnknownAndNeverRestoresExecutionAuthority()
    {
        RecordingClient client = PrunePipelineClient(
            "prune-start-unknown",
            _ => throw new InvalidOperationException("hostile private failure")
        );
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        BoundInstallerRecoveryPoint point = ((BoundInstallerRecoveryCatalogSuccess)await bound.ListRecoveriesAsync()).RecoveryPoints.Single();
        BoundInstallerRecoveryPrunePlanSuccess plan = (BoundInstallerRecoveryPrunePlanSuccess)await bound.InspectRecoveryPruneAsync(point);
        IConfirmedRecoveryPruneSession confirmed = await bound.ConfirmRecoveryPruneAsync(plan.Confirmation!);

        InstallerRecoveryPruneOperation operation = await confirmed.ExecuteAsync();
        (await operation.Completion).Should().BeOfType<InstallerRecoveryPruneStateUnknownResult>();
        await FluentActions.Awaiting(() => confirmed.ExecuteAsync()).Should().ThrowAsync<ObjectDisposedException>();
        client.ExecutedPrunes.Should().ContainSingle();
        await confirmed.DisposeAsync();
    }

    [Test]
    public async Task DisposeDuringPruneExecutionRequestsCancellationAndWaitsForTerminalBeforeCleanup()
    {
        TaskCompletionSource<InstallerRecoveryPruneResult> completion = NewCompletion<InstallerRecoveryPruneResult>();
        TaskCompletionSource cancellationRequested = NewCompletion();
        RecordingClient client = PrunePipelineClient(
            "prune-dispose",
            _ => Task.FromResult(RecoveryPruneOperation(completion, () =>
            {
                cancellationRequested.TrySetResult();
                return Task.CompletedTask;
            }))
        );
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        BoundInstallerRecoveryPoint point = ((BoundInstallerRecoveryCatalogSuccess)await bound.ListRecoveriesAsync()).RecoveryPoints.Single();
        BoundInstallerRecoveryPrunePlanSuccess plan = (BoundInstallerRecoveryPrunePlanSuccess)await bound.InspectRecoveryPruneAsync(point);
        IConfirmedRecoveryPruneSession confirmed = await bound.ConfirmRecoveryPruneAsync(plan.Confirmation!);
        InstallerRecoveryPruneOperation operation = await confirmed.ExecuteAsync();

        Task disposal = confirmed.DisposeAsync().AsTask();
        await cancellationRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        disposal.IsCompleted.Should().BeFalse();
        client.DisposeCalls.Should().Be(0);

        completion.SetResult(new InstallerRecoveryPruneStateUnknownResult());
        await operation.Completion;
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
        client.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task DisposeWhilePruneStartIsPendingSettlesTheLatePublishedOperationBeforeCleanup()
    {
        TaskCompletionSource releaseStart = NewCompletion();
        TaskCompletionSource<InstallerRecoveryPruneResult> completion = NewCompletion<InstallerRecoveryPruneResult>();
        TaskCompletionSource operationCancellation = NewCompletion();
        RecordingClient client = PrunePipelineClient(
            "prune-pending-start-dispose",
            async _ =>
            {
                await releaseStart.Task;
                return RecoveryPruneOperation(completion, () =>
                {
                    operationCancellation.TrySetResult();
                    return Task.CompletedTask;
                });
            }
        );
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        BoundInstallerRecoveryPoint point = ((BoundInstallerRecoveryCatalogSuccess)await bound.ListRecoveriesAsync()).RecoveryPoints.Single();
        BoundInstallerRecoveryPrunePlanSuccess plan = (BoundInstallerRecoveryPrunePlanSuccess)await bound.InspectRecoveryPruneAsync(point);
        IConfirmedRecoveryPruneSession confirmed = await bound.ConfirmRecoveryPruneAsync(plan.Confirmation!);

        Task<InstallerRecoveryPruneOperation> starting = confirmed.ExecuteAsync();
        await WaitUntilAsync(() => client.ExecutedPrunes.Count == 1);
        Task disposal = confirmed.DisposeAsync().AsTask();
        disposal.IsCompleted.Should().BeFalse();
        client.DisposeCalls.Should().Be(0);

        releaseStart.SetResult();
        InstallerRecoveryPruneOperation operation = await starting.WaitAsync(TimeSpan.FromSeconds(2));
        await operationCancellation.Task.WaitAsync(TimeSpan.FromSeconds(2));
        disposal.IsCompleted.Should().BeFalse();
        client.DisposeCalls.Should().Be(0);

        completion.SetResult(new InstallerRecoveryPruneStateUnknownResult());
        _ = await operation.Completion;
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
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
        )
        {
            Confirmation = new InstallerPlanConfirmation()
        };
    }

    [Test]
    public async Task ConfirmationRemintsExactAuthorityAndTransfersExclusiveCleanupOwnership()
    {
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("confirm", LinuxGameFolderStatus.Valid)])
        };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await bound.InspectPlanAsync(InstallerOperation.Backup);

        IConfirmedInstallerSession confirmed = await bound.ConfirmPlanAsync(plan.Confirmation!);

        client.ConfirmedPlans.Should().ContainSingle();
        client.ConfirmedPlans.Single().Should().NotBeSameAs(plan.Confirmation, "the process authority must remain below the bound-session layer");
        confirmed.Release.Should().Be(bound.Release);
        confirmed.Game.Should().BeSameAs(bound.Game);
        await FluentActions.Awaiting(() => bound.InspectPlanAsync(InstallerOperation.Backup)).Should().ThrowAsync<ObjectDisposedException>();
        await FluentActions.Awaiting(() => bound.ConfirmPlanAsync(plan.Confirmation!)).Should().ThrowAsync<ObjectDisposedException>();

        await session.DisposeAsync();
        await bound.DisposeAsync();
        client.DisposeCalls.Should().Be(0, "only the confirmed owner retains cleanup authority");
        await confirmed.DisposeAsync();
        await confirmed.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task ConcurrentConfirmationTransfersExactlyOneOwnerAndCleansUpOnce()
    {
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("concurrent-confirm", LinuxGameFolderStatus.Valid)])
        };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await bound.InspectPlanAsync(InstallerOperation.Backup);
        TaskCompletionSource start = NewCompletion();

        async Task<(IConfirmedInstallerSession? Owner, Exception? Error)> AttemptAsync()
        {
            await start.Task;
            try { return (await bound.ConfirmPlanAsync(plan.Confirmation!), null); }
            catch (Exception error) { return (null, error); }
        }

        Task<(IConfirmedInstallerSession? Owner, Exception? Error)>[] attempts = [AttemptAsync(), AttemptAsync()];
        start.TrySetResult();
        (IConfirmedInstallerSession? Owner, Exception? Error)[] results = await Task.WhenAll(attempts);

        IConfirmedInstallerSession owner = results.Should().ContainSingle(result => result.Owner != null && result.Error == null).Subject.Owner!;
        results.Should().ContainSingle(result => result.Owner == null && result.Error != null && result.Error.GetType() == typeof(ObjectDisposedException));
        client.ConfirmedPlans.Should().ContainSingle();
        await bound.DisposeAsync();
        await session.DisposeAsync();
        client.DisposeCalls.Should().Be(0);
        await owner.DisposeAsync();
        await owner.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task ForeignAndStaleConfirmationReferencesFailBeforeTheClientAndPreserveTheCurrentCapability()
    {
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("stale-confirm", LinuxGameFolderStatus.Valid)])
        };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        InstallerReadOnlyPlanSuccess stale = (InstallerReadOnlyPlanSuccess)await bound.InspectPlanAsync(InstallerOperation.Backup);
        InstallerReadOnlyPlanSuccess current = (InstallerReadOnlyPlanSuccess)await bound.InspectPlanAsync(InstallerOperation.Backup);

        await FluentActions.Awaiting(() => bound.ConfirmPlanAsync(new InstallerPlanConfirmation())).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => bound.ConfirmPlanAsync(stale.Confirmation!)).Should().ThrowAsync<ArgumentException>();
        client.ConfirmedPlans.Should().BeEmpty();

        await using IConfirmedInstallerSession confirmed = await bound.ConfirmPlanAsync(current.Confirmation!);
        client.ConfirmedPlans.Should().ContainSingle();
    }

    [Test]
    public async Task CandidateReissueRevokesThePriorConfirmationAndTheReplacementCanConfirm()
    {
        InstallerReadOnlyPlanCandidate candidate = CandidateCapability('4', "mods/reissue.dll", false);
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("reissue-confirm", LinuxGameFolderStatus.Valid)]),
            Inspection = (_, operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(Plan(operation) with { Candidates = [candidate] }),
            Approval = (_, _) => Task.FromResult<InstallerReadOnlyPlanResult>(Plan(InstallerOperation.Install))
        };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        InstallerReadOnlyPlanSuccess first = (InstallerReadOnlyPlanSuccess)await bound.InspectPlanAsync(InstallerOperation.Install);
        InstallerReadOnlyPlanSuccess replacement = (InstallerReadOnlyPlanSuccess)await bound.ApprovePlanCandidatesAsync([first.Candidates.Single()]);

        await FluentActions.Awaiting(() => bound.ConfirmPlanAsync(first.Confirmation!)).Should().ThrowAsync<ArgumentException>();
        client.ConfirmedPlans.Should().BeEmpty();
        await using IConfirmedInstallerSession confirmed = await bound.ConfirmPlanAsync(replacement.Confirmation!);
        client.ConfirmedPlans.Should().ContainSingle();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task BlockedPlanNeverPublishesConfirmationAuthority(bool maliciousAuthority)
    {
        InstallerReadOnlyPlanSuccess blocked = Plan(InstallerOperation.Backup) with
        {
            HasBlockingConflicts = true,
            Confirmation = maliciousAuthority ? new InstallerPlanConfirmation() : null
        };
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("blocked-confirm", LinuxGameFolderStatus.Valid)]),
            Inspection = (_, _, _) => Task.FromResult<InstallerReadOnlyPlanResult>(blocked)
        };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());

        if (maliciousAuthority)
        {
            await FluentActions.Awaiting(() => bound.InspectPlanAsync(InstallerOperation.Backup)).Should().ThrowAsync<InstallerProtocolClientException>();
            client.DisposeCalls.Should().Be(1);
        }
        else
        {
            InstallerReadOnlyPlanSuccess result = (InstallerReadOnlyPlanSuccess)await bound.InspectPlanAsync(InstallerOperation.Backup);
            result.Confirmation.Should().BeNull();
            await FluentActions.Awaiting(() => bound.ConfirmPlanAsync(new InstallerPlanConfirmation())).Should().ThrowAsync<ArgumentException>();
            client.ConfirmedPlans.Should().BeEmpty();
            await bound.DisposeAsync();
        }
    }

    [Test]
    public async Task ExecutablePlanWithoutConfirmationAuthorityFailsClosedAndDisposesOnce()
    {
        InstallerReadOnlyPlanSuccess malformed = Plan(InstallerOperation.Backup) with { Confirmation = null };
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("missing-confirm", LinuxGameFolderStatus.Valid)]),
            Inspection = (_, _, _) => Task.FromResult<InstallerReadOnlyPlanResult>(malformed)
        };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());

        await FluentActions.Awaiting(() => bound.InspectPlanAsync(InstallerOperation.Backup)).Should().ThrowAsync<InstallerProtocolClientException>();

        client.ConfirmedPlans.Should().BeEmpty();
        client.DisposeCalls.Should().Be(1);
        await session.DisposeAsync();
        await bound.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task ConfirmationCallerCancellationRevokesAuthorityAndDisposesExactlyOnce()
    {
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("cancel-confirm", LinuxGameFolderStatus.Valid)]),
            Confirmation = async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new InstallerConfirmedPlanAuthority();
            }
        };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await bound.InspectPlanAsync(InstallerOperation.Backup);
        using CancellationTokenSource cancellation = new();
        Task<IConfirmedInstallerSession> confirming = bound.ConfirmPlanAsync(plan.Confirmation!, cancellation.Token);
        await client.ConfirmationStarted.Task;

        cancellation.Cancel();

        await FluentActions.Awaiting(() => confirming).Should().ThrowAsync<OperationCanceledException>();
        client.DisposeCalls.Should().Be(1);
        await FluentActions.Awaiting(() => bound.ConfirmPlanAsync(plan.Confirmation!)).Should().ThrowAsync<ObjectDisposedException>();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ConfirmationDisposalOrSessionFaultWinsOverALateAuthority(bool fault)
    {
        TaskCompletionSource<InstallerConfirmedPlanAuthority> completion = NewCompletion<InstallerConfirmedPlanAuthority>();
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("late-confirm", LinuxGameFolderStatus.Valid)]),
            Confirmation = (_, _) => completion.Task
        };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await bound.InspectPlanAsync(InstallerOperation.Backup);
        Task<IConfirmedInstallerSession> confirming = bound.ConfirmPlanAsync(plan.Confirmation!);
        await client.ConfirmationStarted.Task;

        Task? disposal = null;
        if (fault)
            client.Fault.TrySetResult(new InstallerProtocolClientException("generic test fault"));
        else
            disposal = bound.DisposeAsync().AsTask();
        completion.TrySetResult(new InstallerConfirmedPlanAuthority());

        if (fault)
            await FluentActions.Awaiting(() => confirming).Should().ThrowAsync<InstallerProtocolClientException>();
        else
            await FluentActions.Awaiting(() => confirming).Should().ThrowAsync<ObjectDisposedException>();
        if (disposal is not null)
            await disposal;
        client.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task ConfirmationReentrancyObservesRevokedBoundStateWithoutDeadlock()
    {
        IPlanInspectionSession bound = null!;
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("reentrant-confirm", LinuxGameFolderStatus.Valid)]),
            Confirmation = (_, _) =>
            {
                Action reentrant = () => bound.InspectPlanAsync(InstallerOperation.Backup).GetAwaiter().GetResult();
                reentrant.Should().Throw<ObjectDisposedException>();
                return Task.FromResult(new InstallerConfirmedPlanAuthority());
            }
        };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await bound.InspectPlanAsync(InstallerOperation.Backup);

        await using IConfirmedInstallerSession confirmed = await bound.ConfirmPlanAsync(plan.Confirmation!);
        client.ConfirmedPlans.Should().ContainSingle();
    }

    [Test]
    public async Task PreCancelledExecutionPreservesTheExactConfirmedOwnerForOneRetry()
    {
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("execute-precancel", LinuxGameFolderStatus.Valid)])
        };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await bound.InspectPlanAsync(InstallerOperation.Backup);
        IConfirmedInstallerSession confirmed = await bound.ConfirmPlanAsync(plan.Confirmation!);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        await FluentActions.Awaiting(() => confirmed.ExecuteAsync(cancelled.Token)).Should().ThrowAsync<OperationCanceledException>();
        client.ExecutedAuthorities.Should().BeEmpty();

        InstallerExecutionOperation execution = await confirmed.ExecuteAsync();
        client.ExecutedAuthorities.Should().ContainSingle();
        await FluentActions.Awaiting(() => confirmed.ExecuteAsync()).Should().ThrowAsync<ObjectDisposedException>();
        await execution.Completion;
        await confirmed.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task CallerCancellationRemainsLiveAfterExecutionOperationIsReturned()
    {
        TaskCompletionSource<InstallerExecutionResult> terminal = NewCompletion<InstallerExecutionResult>();
        TaskCompletionSource cancellationObserved = NewCompletion();
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("execute-token", LinuxGameFolderStatus.Valid)]),
            Execution = (_, token) =>
            {
                token.Register(() => cancellationObserved.TrySetResult());
                return Task.FromResult(ExecutionOperation(terminal));
            }
        };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await bound.InspectPlanAsync(InstallerOperation.Backup);
        IConfirmedInstallerSession confirmed = await bound.ConfirmPlanAsync(plan.Confirmation!);
        using CancellationTokenSource cancellation = new();

        _ = await confirmed.ExecuteAsync(cancellation.Token);
        cancellation.Cancel();
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        terminal.TrySetResult(new InstallerExecutionStateUnknownResult());
        await confirmed.DisposeAsync();
    }

    [Test]
    public async Task DisposeDuringExecutionRequestsCancellationAndAwaitsTerminalBeforeClientCleanup()
    {
        TaskCompletionSource<InstallerExecutionResult> terminal = NewCompletion<InstallerExecutionResult>();
        int cancellationRequests = 0;
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("execute-dispose", LinuxGameFolderStatus.Valid)]),
            Execution = (_, _) => Task.FromResult(ExecutionOperation(terminal, () =>
            {
                Interlocked.Increment(ref cancellationRequests);
                return Task.CompletedTask;
            }))
        };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await bound.InspectPlanAsync(InstallerOperation.Backup);
        IConfirmedInstallerSession confirmed = await bound.ConfirmPlanAsync(plan.Confirmation!);
        _ = await confirmed.ExecuteAsync();
        await bound.DisposeAsync();
        await session.DisposeAsync();
        client.DisposeCalls.Should().Be(0, "stale pre-handoff owners stay cleanup-inert during execution");

        Task first = confirmed.DisposeAsync().AsTask();
        Task second = confirmed.DisposeAsync().AsTask();
        first.Should().BeSameAs(second);
        await WaitUntilAsync(() => Volatile.Read(ref cancellationRequests) == 1);
        await bound.DisposeAsync();
        client.DisposeCalls.Should().Be(0);
        terminal.TrySetResult(new InstallerExecutionStateUnknownResult());
        await first;

        client.DisposeCalls.Should().Be(1);
        cancellationRequests.Should().Be(1);
        await bound.DisposeAsync();
    }

    [TestCase(HostileStartFailureKind.Unexpected)]
    [TestCase(HostileStartFailureKind.ProtocolClient)]
    [TestCase(HostileStartFailureKind.UnrequestedCancellation)]
    [TestCase(HostileStartFailureKind.ObjectDisposed)]
    public async Task HostileExecutionStartFailureIsSanitizedAndFailsStop(HostileStartFailureKind kind)
    {
        Exception failure = kind switch
        {
            HostileStartFailureKind.Unexpected => new InvalidOperationException("private /home/wife/Mods sentinel"),
            HostileStartFailureKind.ProtocolClient => new InstallerProtocolClientException("private /home/wife/Mods sentinel"),
            HostileStartFailureKind.UnrequestedCancellation => new OperationCanceledException("private /home/wife/Mods sentinel"),
            HostileStartFailureKind.ObjectDisposed => new ObjectDisposedException("private /home/wife/Mods sentinel"),
            _ => throw new AssertionException("Unsupported hostile start failure.")
        };
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("execute-hostile", LinuxGameFolderStatus.Valid)]),
            Execution = (_, _) => throw failure
        };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await bound.InspectPlanAsync(InstallerOperation.Backup);
        IConfirmedInstallerSession confirmed = await bound.ConfirmPlanAsync(plan.Confirmation!);

        InstallerProtocolClientException error = (await FluentActions.Awaiting(() => confirmed.ExecuteAsync())
            .Should().ThrowAsync<InstallerProtocolClientException>()).Which;
        error.Message.Should().NotContain("wife").And.NotContain("/home").And.NotContain("Mods");
        client.DisposeCalls.Should().Be(1);
        await FluentActions.Awaiting(() => confirmed.ExecuteAsync()).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Test]
    public async Task RequestedExecutionStartCancellationPreservesOnlySanitizedCancellationSemantics()
    {
        TaskCompletionSource started = NewCompletion();
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("execute-cancel-sanitize", LinuxGameFolderStatus.Valid)]),
            Execution = async (_, token) =>
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException)
                {
                    throw new OperationCanceledException("private /home/wife/Mods sentinel", token);
                }
                throw new AssertionException("The cancelled execution start unexpectedly continued.");
            }
        };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await bound.InspectPlanAsync(InstallerOperation.Backup);
        IConfirmedInstallerSession confirmed = await bound.ConfirmPlanAsync(plan.Confirmation!);
        using CancellationTokenSource cancellation = new();
        Task<InstallerExecutionOperation> executing = confirmed.ExecuteAsync(cancellation.Token);
        await started.Task;

        cancellation.Cancel();

        OperationCanceledException error = (await FluentActions.Awaiting(() => executing)
            .Should().ThrowAsync<OperationCanceledException>()).Which;
        error.Message.Should().NotContain("wife").And.NotContain("/home").And.NotContain("Mods");
        error.CancellationToken.Should().Be(cancellation.Token);
        client.DisposeCalls.Should().Be(1);
        await FluentActions.Awaiting(() => confirmed.ExecuteAsync()).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Test]
    public async Task DisposalDrivenExecutionStartCancellationUsesASanitizedCancelledFallbackToken()
    {
        TaskCompletionSource started = NewCompletion();
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("execute-dispose-cancel-sanitize", LinuxGameFolderStatus.Valid)]),
            Execution = async (_, token) =>
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException)
                {
                    throw new OperationCanceledException("private /home/wife/Mods fallback sentinel", token);
                }
                throw new AssertionException("The disposal-cancelled execution start unexpectedly continued.");
            }
        };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await bound.InspectPlanAsync(InstallerOperation.Backup);
        IConfirmedInstallerSession confirmed = await bound.ConfirmPlanAsync(plan.Confirmation!);
        Task<InstallerExecutionOperation> executing = confirmed.ExecuteAsync(CancellationToken.None);
        await started.Task;

        Task disposal = confirmed.DisposeAsync().AsTask();

        OperationCanceledException error = (await FluentActions.Awaiting(() => executing)
            .Should().ThrowAsync<OperationCanceledException>()).Which;
        error.Message.Should().NotContain("wife").And.NotContain("/home").And.NotContain("Mods").And.NotContain("fallback sentinel");
        error.CancellationToken.IsCancellationRequested.Should().BeTrue();
        error.CancellationToken.Should().NotBe(CancellationToken.None);
        await disposal;
        client.DisposeCalls.Should().Be(1);
        await confirmed.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
        await bound.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task ConcurrentExecutionAdmissionHasExactlyOneWinnerAndOneClientCall()
    {
        TaskCompletionSource start = NewCompletion();
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("execute-concurrent", LinuxGameFolderStatus.Valid)])
        };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await bound.InspectPlanAsync(InstallerOperation.Backup);
        IConfirmedInstallerSession confirmed = await bound.ConfirmPlanAsync(plan.Confirmation!);

        async Task<(InstallerExecutionOperation? Operation, Exception? Error)> AttemptAsync()
        {
            await start.Task;
            try { return (await confirmed.ExecuteAsync(), null); }
            catch (Exception error) { return (null, error); }
        }

        Task<(InstallerExecutionOperation? Operation, Exception? Error)>[] attempts = [AttemptAsync(), AttemptAsync()];
        start.TrySetResult();
        (InstallerExecutionOperation? Operation, Exception? Error)[] results = await Task.WhenAll(attempts);

        results.Should().ContainSingle(result => result.Operation != null && result.Error == null);
        results.Should().ContainSingle(result => result.Operation == null && result.Error != null && result.Error.GetType() == typeof(ObjectDisposedException));
        client.ExecutedAuthorities.Should().ContainSingle();
        await confirmed.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task DisposeWhileExecutionStartIsPendingCancelsAndSettlesLateOperationBeforeCleanup()
    {
        TaskCompletionSource<InstallerExecutionOperation> start = NewCompletion<InstallerExecutionOperation>();
        TaskCompletionSource<InstallerExecutionResult> terminal = NewCompletion<InstallerExecutionResult>();
        TaskCompletionSource tokenCancelled = NewCompletion();
        int cancellationRequests = 0;
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("execute-start-dispose", LinuxGameFolderStatus.Valid)]),
            Execution = (_, token) =>
            {
                token.Register(() => tokenCancelled.TrySetResult());
                return start.Task;
            }
        };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await bound.InspectPlanAsync(InstallerOperation.Backup);
        IConfirmedInstallerSession confirmed = await bound.ConfirmPlanAsync(plan.Confirmation!);

        Task<InstallerExecutionOperation> starting = confirmed.ExecuteAsync();
        Task disposal = confirmed.DisposeAsync().AsTask();
        await tokenCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        client.DisposeCalls.Should().Be(0);
        start.TrySetResult(ExecutionOperation(terminal, () =>
        {
            Interlocked.Increment(ref cancellationRequests);
            return Task.CompletedTask;
        }));
        _ = await starting;
        await WaitUntilAsync(() => Volatile.Read(ref cancellationRequests) == 1);
        client.DisposeCalls.Should().Be(0);
        terminal.TrySetResult(new InstallerExecutionStateUnknownResult());
        await disposal;

        client.DisposeCalls.Should().Be(1);
        cancellationRequests.Should().Be(1);
    }

    [Test]
    public async Task CompletedExecutionNeverRestoresStaleRootOrBoundCleanupAuthority()
    {
        TaskCompletionSource<InstallerExecutionResult> terminal = NewCompletion<InstallerExecutionResult>();
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("execute-terminal-owner", LinuxGameFolderStatus.Valid)]),
            Execution = (_, _) => Task.FromResult(ExecutionOperation(terminal))
        };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await bound.InspectPlanAsync(InstallerOperation.Backup);
        IConfirmedInstallerSession confirmed = await bound.ConfirmPlanAsync(plan.Confirmation!);
        InstallerExecutionOperation execution = await confirmed.ExecuteAsync();
        terminal.TrySetResult(new InstallerExecutionStateUnknownResult());
        await execution.Completion;
        await Task.Yield();

        await session.DisposeAsync();
        await bound.DisposeAsync();
        client.DisposeCalls.Should().Be(0);
        await confirmed.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
        await bound.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task ExecutionStartFaultAndConfirmedDisposalShareOneCleanup()
    {
        for (int iteration = 0; iteration < 20; iteration++)
        {
            TaskCompletionSource<InstallerExecutionOperation> start = NewCompletion<InstallerExecutionOperation>();
            RecordingClient client = new()
            {
                Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate($"execute-start-fault-{iteration}", LinuxGameFolderStatus.Valid)]),
                Execution = (_, _) => start.Task
            };
            VerifiedInstallerSession session = new(CreateRelease(), client);
            IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
            InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await bound.InspectPlanAsync(InstallerOperation.Backup);
            IConfirmedInstallerSession confirmed = await bound.ConfirmPlanAsync(plan.Confirmation!);
            Task<InstallerExecutionOperation> starting = confirmed.ExecuteAsync();
            Task disposal = confirmed.DisposeAsync().AsTask();
            start.TrySetException(new InvalidOperationException("private start sentinel"));

            await FluentActions.Awaiting(() => starting).Should().ThrowAsync<Exception>();
            await disposal;
            client.DisposeCalls.Should().Be(1);
            await bound.DisposeAsync();
            client.DisposeCalls.Should().Be(1);
        }
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

        await FluentActions.Awaiting(() => bound.InspectPlanAsync(InstallerOperation.Update))
            .Should().ThrowAsync<InstallerProtocolClientException>("a nonterminal rejection must not erase session-lifetime reference tombstones");

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

    [Test]
    public async Task BoundCandidateApprovalRejectsAReferenceResurrectedAfterAnIntermediateGeneration()
    {
        InstallerReadOnlyPlanCandidate first = CandidateCapability('4', "mods/first.dll", false);
        InstallerReadOnlyPlanCandidate second = CandidateCapability('5', "mods/second.dll", false);
        int generation = 0;
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("valid", LinuxGameFolderStatus.Valid)]),
            Inspection = (_, operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(Plan(operation) with { Candidates = [first] }),
            Approval = (_, _) => Task.FromResult<InstallerReadOnlyPlanResult>(Plan(InstallerOperation.Install) with
            {
                Candidates = ++generation == 1 ? [second] : [first]
            })
        };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        InstallerReadOnlyPlanSuccess initial = (InstallerReadOnlyPlanSuccess)await bound.InspectPlanAsync(InstallerOperation.Install);
        InstallerReadOnlyPlanSuccess intermediate = (InstallerReadOnlyPlanSuccess)await bound.ApprovePlanCandidatesAsync([initial.Candidates.Single()]);

        await FluentActions.Awaiting(() => bound.ApprovePlanCandidatesAsync([intermediate.Candidates.Single()]))
            .Should().ThrowAsync<InstallerProtocolClientException>();

        client.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task BoundCandidateLifetimeCapacityIsBoundedAndFailsClosed()
    {
        int generation = 0;
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("capacity", LinuxGameFolderStatus.Valid)]),
            Inspection = (_, operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(Plan(operation) with
            {
                Candidates = Enumerable.Range(0, ProtocolJsonSerializer.MaxPlanCandidates)
                    .Select(index => CandidateCapability((generation * ProtocolJsonSerializer.MaxPlanCandidates + index + 1).ToString("x32"), $"mods/{generation:D2}-{index:D3}.dll", false))
                    .ToArray()
            })
        };
        await using VerifiedInstallerSession session = new(CreateRelease(), client)
        {
            IssuedPlanCandidateCapacityForTesting = ProtocolJsonSerializer.MaxPlanCandidates * 2
        };
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        InstallerCandidateSelection.MaximumIssuedCandidatesPerSession.Should().Be(
            ProtocolJsonSerializer.MaxPlanCandidates * ProtocolJsonSerializer.MaxPlanCandidates
        );
        int acceptedGenerations = session.IssuedPlanCandidateCapacityForTesting / ProtocolJsonSerializer.MaxPlanCandidates;
        for (; generation < acceptedGenerations; generation++)
            (await bound.InspectPlanAsync(InstallerOperation.Install)).Should().BeOfType<InstallerReadOnlyPlanSuccess>();

        await FluentActions.Awaiting(() => bound.InspectPlanAsync(InstallerOperation.Install))
            .Should().ThrowAsync<InstallerProtocolClientException>();

        client.InspectedOperations.Should().HaveCount(acceptedGenerations + 1);
        client.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task BoundApprovalCallerCancellationRejectsLateSuccessAndDisposesOnce()
    {
        (IPlanInspectionSession bound, RecordingClient client, InstallerReadOnlyPlanCandidate candidate, TaskCompletionSource<InstallerReadOnlyPlanResult> completion) =
            await CreateBlockedApprovalSessionAsync("approval-cancel");
        using CancellationTokenSource cancellation = new();
        Task<InstallerReadOnlyPlanResult> approval = bound.ApprovePlanCandidatesAsync([candidate], cancellation.Token);
        await client.ApprovalStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        completion.SetResult(Plan(InstallerOperation.Install) with { Candidates = [CandidateCapability('6', "mods/replacement.dll", false)] });

        await FluentActions.Awaiting(() => approval).Should().ThrowAsync<OperationCanceledException>();
        client.DisposeCalls.Should().Be(1);
        await FluentActions.Awaiting(() => bound.ApprovePlanCandidatesAsync([candidate])).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Test]
    public async Task BoundApprovalSessionFaultTakesPrecedenceOverCallerCancellationAndLateSuccess()
    {
        (IPlanInspectionSession bound, RecordingClient client, InstallerReadOnlyPlanCandidate candidate, TaskCompletionSource<InstallerReadOnlyPlanResult> completion) =
            await CreateBlockedApprovalSessionAsync("approval-fault-cancel");
        using CancellationTokenSource cancellation = new();
        Task<InstallerReadOnlyPlanResult> approval = bound.ApprovePlanCandidatesAsync([candidate], cancellation.Token);
        await client.ApprovalStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        client.Fault.SetResult(new InstallerProtocolClientException("synthetic approval fault"));
        completion.SetResult(Plan(InstallerOperation.Install));

        await FluentActions.Awaiting(() => approval)
            .Should().ThrowAsync<InstallerProtocolClientException>()
            .WithMessage("*faulted before the plan result*");
        client.DisposeCalls.Should().Be(1);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task BoundApprovalDisposalTakesPrecedenceOverLateSuccessOrFault(bool lateFault)
    {
        (IPlanInspectionSession bound, RecordingClient client, InstallerReadOnlyPlanCandidate candidate, TaskCompletionSource<InstallerReadOnlyPlanResult> completion) =
            await CreateBlockedApprovalSessionAsync($"approval-dispose-{lateFault}");
        Task<InstallerReadOnlyPlanResult> approval = bound.ApprovePlanCandidatesAsync([candidate]);
        await client.ApprovalStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task disposal = bound.DisposeAsync().AsTask();
        if (lateFault)
            completion.SetException(new InstallerProtocolClientException("late synthetic failure"));
        else
            completion.SetResult(Plan(InstallerOperation.Install) with { Candidates = [CandidateCapability('6', "mods/replacement.dll", false)] });

        await FluentActions.Awaiting(() => approval).Should().ThrowAsync<ObjectDisposedException>();
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
        client.DisposeCalls.Should().Be(1);
        await FluentActions.Awaiting(() => bound.ApprovePlanCandidatesAsync([candidate])).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Test]
    public async Task QueuedBoundApprovalCancellationTerminatesTheActiveApprovalAndNeverCallsTheClientTwice()
    {
        InstallerReadOnlyPlanCandidate first = CandidateCapability('4', "mods/first.dll", false);
        InstallerReadOnlyPlanCandidate second = CandidateCapability('5', "mods/second.dll", false);
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate("approval-queue", LinuxGameFolderStatus.Valid)]),
            Inspection = (_, operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(Plan(operation) with { Candidates = [first, second] }),
            Approval = async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Plan(InstallerOperation.Install);
            }
        };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        InstallerReadOnlyPlanSuccess plan = (InstallerReadOnlyPlanSuccess)await bound.InspectPlanAsync(InstallerOperation.Install);
        Task<InstallerReadOnlyPlanResult> active = bound.ApprovePlanCandidatesAsync([plan.Candidates[0]]);
        await client.ApprovalStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using CancellationTokenSource queuedCancellation = new();
        Task<InstallerReadOnlyPlanResult> queued = bound.ApprovePlanCandidatesAsync([plan.Candidates[1]], queuedCancellation.Token);
        queuedCancellation.Cancel();

        await FluentActions.Awaiting(() => queued).Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(() => active).Should().ThrowAsync<ObjectDisposedException>();
        client.ApprovedCandidates.Should().ContainSingle();
        client.DisposeCalls.Should().Be(1);
    }

    [TestCase(false, HostileResultCandidates.NegativeCount)]
    [TestCase(false, HostileResultCandidates.OversizedCount)]
    [TestCase(false, HostileResultCandidates.LyingCount)]
    [TestCase(false, HostileResultCandidates.ChangingAfterCount)]
    [TestCase(false, HostileResultCandidates.ThrowingCount)]
    [TestCase(false, HostileResultCandidates.ThrowingIndexer)]
    [TestCase(false, HostileResultCandidates.NullCollection)]
    [TestCase(false, HostileResultCandidates.NullCandidate)]
    [TestCase(true, HostileResultCandidates.NegativeCount)]
    [TestCase(true, HostileResultCandidates.OversizedCount)]
    [TestCase(true, HostileResultCandidates.LyingCount)]
    [TestCase(true, HostileResultCandidates.ChangingAfterCount)]
    [TestCase(true, HostileResultCandidates.ThrowingCount)]
    [TestCase(true, HostileResultCandidates.ThrowingIndexer)]
    [TestCase(true, HostileResultCandidates.NullCollection)]
    [TestCase(true, HostileResultCandidates.NullCandidate)]
    public async Task BoundPlanResultsRejectHostileCandidateCollectionsOutsideLifecycleAuthority(
        bool replacement,
        HostileResultCandidates fault
    )
    {
        const string privateSentinel = "/home/private-user/hostile-candidate-collection";
        InstallerReadOnlyPlanCandidate candidate = CandidateCapability('4', "mods/initial.dll", false);
        IReadOnlyList<InstallerReadOnlyPlanCandidate>? hostile = CreateHostileResultCandidates(fault, candidate, privateSentinel);
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate($"hostile-{replacement}-{fault}", LinuxGameFolderStatus.Valid)]),
            Inspection = (_, operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(Plan(operation) with
            {
                Candidates = replacement ? [candidate] : hostile!
            }),
            Approval = (_, _) => Task.FromResult<InstallerReadOnlyPlanResult>(Plan(InstallerOperation.Install) with { Candidates = hostile! })
        };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        Func<Task> action;
        if (replacement)
        {
            InstallerReadOnlyPlanSuccess initial = (InstallerReadOnlyPlanSuccess)await bound.InspectPlanAsync(InstallerOperation.Install);
            action = () => bound.ApprovePlanCandidatesAsync([initial.Candidates.Single()]);
        }
        else
            action = () => bound.InspectPlanAsync(InstallerOperation.Install);

        Exception exception = (await action.Should().ThrowAsync<InstallerProtocolClientException>()).Which;

        exception.ToString().Should().NotContain(privateSentinel);
        client.DisposeCalls.Should().Be(1);
        await action.Should().ThrowAsync<ObjectDisposedException>();
        client.DisposeCalls.Should().Be(1);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task BoundPlanResultsReturnAStableReadOnlySnapshotWithoutRevisitingTheBackendCollection(bool replacement)
    {
        const string privateSentinel = "/home/private-user/one-shot-candidate-collection";
        InstallerReadOnlyPlanCandidate initial = CandidateCapability('4', "mods/initial.dll", false);
        InstallerReadOnlyPlanCandidate projected = CandidateCapability('5', "mods/projected.dll", false);
        int countCalls = 0;
        int indexCalls = 0;
        HostileCandidateList oneShot = new(
            () => ++countCalls == 1 ? 1 : throw new InvalidOperationException(privateSentinel),
            index => index == 0 && ++indexCalls == 1 ? projected : throw new InvalidOperationException(privateSentinel)
        );
        int approvals = 0;
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate($"stable-result-{replacement}", LinuxGameFolderStatus.Valid)]),
            Inspection = (_, operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(Plan(operation) with
            {
                Candidates = replacement ? [initial] : oneShot
            }),
            Approval = (_, _) => Task.FromResult<InstallerReadOnlyPlanResult>(Plan(InstallerOperation.Install) with
            {
                Candidates = replacement && ++approvals == 1 ? oneShot : []
            })
        };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        await using IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        InstallerReadOnlyPlanSuccess result = (InstallerReadOnlyPlanSuccess)await bound.InspectPlanAsync(InstallerOperation.Install);
        if (replacement)
            result = (InstallerReadOnlyPlanSuccess)await bound.ApprovePlanCandidatesAsync([result.Candidates.Single()]);

        result.Candidates.Should().NotBeSameAs(oneShot);
        ((ICollection<InstallerReadOnlyPlanCandidate>)result.Candidates).IsReadOnly.Should().BeTrue();
        result.Candidates.Should().ContainSingle().Which.Should().BeSameAs(projected);
        result.Candidates.Single().Should().BeSameAs(projected);
        result.Candidates.ToArray().Should().ContainSingle().Which.Should().BeSameAs(projected);
        InstallerReadOnlyPlanSuccess next = (InstallerReadOnlyPlanSuccess)await bound.ApprovePlanCandidatesAsync([result.Candidates.Single()]);

        next.Candidates.Should().BeEmpty();
        countCalls.Should().Be(1);
        indexCalls.Should().Be(1);
        client.ApprovedCandidates.Last().Should().ContainSingle().Which.Should().BeSameAs(projected);
        await bound.DisposeAsync();
        client.DisposeCalls.Should().Be(1);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task BoundPlanResultSnapshotAllowsReentrantDisposalWithoutHoldingTheLifecycleLock(bool replacement)
    {
        InstallerReadOnlyPlanCandidate candidate = CandidateCapability('4', "mods/initial.dll", false);
        IPlanInspectionSession bound = null!;
        Task? disposal = null;
        HostileCandidateList reentrant = new(
            () =>
            {
                disposal = bound.DisposeAsync().AsTask();
                return 0;
            },
            _ => throw new AssertionException("a zero-count result must not be indexed")
        );
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate($"reentrant-dispose-{replacement}", LinuxGameFolderStatus.Valid)]),
            Inspection = (_, operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(Plan(operation) with { Candidates = replacement ? [candidate] : reentrant }),
            Approval = (_, _) => Task.FromResult<InstallerReadOnlyPlanResult>(Plan(InstallerOperation.Install) with { Candidates = reentrant })
        };
        await using VerifiedInstallerSession session = new(CreateRelease(), client);
        bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        Func<Task> action;
        if (replacement)
        {
            InstallerReadOnlyPlanSuccess initial = (InstallerReadOnlyPlanSuccess)await bound.InspectPlanAsync(InstallerOperation.Install);
            action = () => bound.ApprovePlanCandidatesAsync([initial.Candidates.Single()]);
        }
        else
            action = () => bound.InspectPlanAsync(InstallerOperation.Install);

        await action.Should().ThrowAsync<ObjectDisposedException>();
        disposal.Should().NotBeNull();
        await disposal!.WaitAsync(TimeSpan.FromSeconds(2));
        client.DisposeCalls.Should().Be(1);
    }

    private static IReadOnlyList<InstallerReadOnlyPlanCandidate>? CreateHostileResultCandidates(
        HostileResultCandidates fault,
        InstallerReadOnlyPlanCandidate candidate,
        string privateSentinel
    )
    {
        bool countRead = false;
        return fault switch
        {
            HostileResultCandidates.NegativeCount => new HostileCandidateList(() => -1, _ => candidate),
            HostileResultCandidates.OversizedCount => new HostileCandidateList(() => ProtocolJsonSerializer.MaxPlanCandidates + 1, _ => candidate),
            HostileResultCandidates.LyingCount => new HostileCandidateList(() => 2, index => index == 0 ? candidate : throw new InvalidOperationException(privateSentinel)),
            HostileResultCandidates.ChangingAfterCount => new HostileCandidateList(
                () => { countRead = true; return 1; },
                _ => countRead ? throw new InvalidOperationException(privateSentinel) : candidate
            ),
            HostileResultCandidates.ThrowingCount => new HostileCandidateList(() => throw new InvalidOperationException(privateSentinel), _ => candidate),
            HostileResultCandidates.ThrowingIndexer => new HostileCandidateList(() => 1, _ => throw new InvalidOperationException(privateSentinel)),
            HostileResultCandidates.NullCollection => null,
            HostileResultCandidates.NullCandidate => new HostileCandidateList(() => 1, _ => null!),
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
    }

    private static async Task<(
        IPlanInspectionSession Bound,
        RecordingClient Client,
        InstallerReadOnlyPlanCandidate Candidate,
        TaskCompletionSource<InstallerReadOnlyPlanResult> Completion
    )> CreateBlockedApprovalSessionAsync(string suffix)
    {
        InstallerReadOnlyPlanCandidate candidate = CandidateCapability('4', $"mods/{suffix}.dll", false);
        TaskCompletionSource<InstallerReadOnlyPlanResult> completion = NewCompletion<InstallerReadOnlyPlanResult>();
        RecordingClient client = new()
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate(suffix, LinuxGameFolderStatus.Valid)]),
            Inspection = (_, operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(Plan(operation) with { Candidates = [candidate] }),
            Approval = async (_, _) => await completion.Task
        };
        VerifiedInstallerSession session = new(CreateRelease(), client);
        IPlanInspectionSession bound = session.BindToGame((await session.DiscoverGamesAsync()).Single());
        await bound.InspectPlanAsync(InstallerOperation.Install);
        return (bound, client, candidate, completion);
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

    private static InstallerReadOnlyPlanCandidate CandidateCapability(string id, string path, bool selected) => new(new ProtocolPlanCandidate(
        ProtocolCandidateId.Parse(id),
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

    public enum HostileResultCandidates
    {
        NegativeCount,
        OversizedCount,
        LyingCount,
        ChangingAfterCount,
        ThrowingCount,
        ThrowingIndexer,
        NullCollection,
        NullCandidate
    }

    private sealed class HostileCandidateList(
        Func<int> count,
        Func<int, InstallerReadOnlyPlanCandidate> indexer
    ) : IReadOnlyList<InstallerReadOnlyPlanCandidate>
    {
        public int Count => count();
        public InstallerReadOnlyPlanCandidate this[int index] => indexer(index);
        public IEnumerator<InstallerReadOnlyPlanCandidate> GetEnumerator() => throw new AssertionException("backend result enumeration isn't bounded");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => this.GetEnumerator();
    }

    private static ProtocolGameCandidate Candidate(string suffix, LinuxGameFolderStatus status)
        => new($"/games/{suffix}", status, $"Stardew Valley {suffix}");

    private static ProtocolReleaseIdentity CreateRelease() => GameDiscoveryControllerTests.Release();

    private static InstallerRecoveryPoint RecoveryPoint(int ordinal, bool current)
    {
        ProtocolReleaseIdentity release = CreateRelease();
        return new(
            ordinal,
            current,
            false,
            InstallerOperation.Update,
            new InstallerRecoveryReleaseTarget(release.Tag, release.EmbeddedVersion)
        );
    }

    private static InstallerRecoveryPrunePlanSuccess RecoveryPrunePlan(
        int retainNewest = 2,
        int retainedCount = 2,
        int removedCount = 1,
        bool auxiliaryCleanupPlanned = false
    ) => new(
        retainNewest,
        retainedCount,
        removedCount,
        removedCount,
        auxiliaryCleanupPlanned,
        1,
        [ProtocolPlanRisk.RecoveryPrune],
        ProtocolRecommendedDefault.Cancel,
        true
    )
    {
        Confirmation = new InstallerRecoveryPruneConfirmation()
    };

    private static RecordingClient PrunePipelineClient(
        string suffix,
        Func<InstallerConfirmedRecoveryPruneAuthority, Task<InstallerRecoveryPruneOperation>>? execution = null
    )
    {
        InstallerRecoveryPruneConfirmation confirmation = new();
        return new RecordingClient
        {
            Discovery = _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([Candidate(suffix, LinuxGameFolderStatus.Valid)]),
            RecoveryCatalog = (_, _) => Task.FromResult<InstallerRecoveryCatalogResult>(new InstallerRecoveryCatalogSuccess([RecoveryPoint(1, current: true)])),
            PruneInspection = (_, _, _) => Task.FromResult<InstallerRecoveryPrunePlanResult>(
                RecoveryPrunePlan(retainNewest: 1, retainedCount: 1, removedCount: 0, auxiliaryCleanupPlanned: true) with { Confirmation = confirmation }
            ),
            PruneExecution = (authority, _) => execution?.Invoke(authority) ?? Task.FromResult(RecoveryPruneOperation())
        };
    }

    private static TaskCompletionSource NewCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewCompletion<T>()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static InstallerExecutionOperation ExecutionOperation(
        TaskCompletionSource<InstallerExecutionResult>? completion = null,
        Func<Task>? cancellation = null
    )
    {
        Channel<InstallerExecutionProgress> progress = Channel.CreateUnbounded<InstallerExecutionProgress>();
        progress.Writer.TryComplete();
        return new(
            progress.Reader,
            completion?.Task ?? Task.FromResult<InstallerExecutionResult>(new InstallerExecutionStateUnknownResult()),
            cancellation ?? (() => Task.CompletedTask)
        );
    }

    private static InstallerRecoveryPruneOperation RecoveryPruneOperation(
        TaskCompletionSource<InstallerRecoveryPruneResult>? completion = null,
        Func<Task>? cancellation = null
    )
    {
        Channel<InstallerRecoveryPruneProgress> progress = Channel.CreateUnbounded<InstallerRecoveryPruneProgress>();
        progress.Writer.TryComplete();
        return new(
            progress.Reader,
            completion?.Task ?? Task.FromResult<InstallerRecoveryPruneResult>(new InstallerRecoveryPruneTerminalResult(
                ProtocolPruneOutcome.Succeeded,
                ProtocolDurableState.PruneApplied,
                null,
                ProtocolRecoveryDisposition.NotRequired,
                ProtocolNextAction.ListRecoveries,
                new InstallerRecoveryPruneSummary(0, 0, 0, false),
                InstallerBackendSettlement.ConfirmedClosed
            )),
            cancellation ?? (() => Task.CompletedTask)
        );
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The expected execution state was not reached.");
            await Task.Delay(10);
        }
    }

    internal enum HostileStartFailureKind
    {
        Unexpected,
        ProtocolClient,
        UnrequestedCancellation,
        ObjectDisposed
    }

    internal enum HostilePrunePlan
    {
        NegativeCount,
        WrongRetentionBoundary,
        MissingConfirmation,
        WrongRisk,
        NoConfirmationRequired,
        CleanupSmallerThanRemoved,
        WarningOverflow,
        WrongRecommendedDefault,
        DuplicateRisk,
        TrueNoOp
    }

    private sealed class RecordingClient : IInstallerProtocolClient
    {
        public TaskCompletionSource<InstallerProtocolClientException> Fault { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource InspectionStarted { get; } = NewCompletion();
        public TaskCompletionSource ApprovalStarted { get; } = NewCompletion();
        public TaskCompletionSource ConfirmationStarted { get; } = NewCompletion();
        public TaskCompletionSource PruneConfirmationStarted { get; } = NewCompletion();
        public Func<CancellationToken, Task<IReadOnlyList<ProtocolGameCandidate>>> Discovery { get; init; } =
            _ => Task.FromResult<IReadOnlyList<ProtocolGameCandidate>>([]);
        public Func<string, CancellationToken, Task<ProtocolGameCandidate>> Validation { get; init; } =
            (path, _) => Task.FromResult(Candidate(path.GetHashCode(StringComparison.Ordinal).ToString(), LinuxGameFolderStatus.Valid));
        public Func<string, InstallerOperation, CancellationToken, Task<InstallerReadOnlyPlanResult>> Inspection { get; init; } =
            (_, operation, _) => Task.FromResult<InstallerReadOnlyPlanResult>(Plan(operation));
        public Func<string, CancellationToken, Task<InstallerRecoveryCatalogResult>> RecoveryCatalog { get; init; } =
            (_, _) => throw new AssertionException("Recovery history wasn't expected.");
        public Func<string, InstallerRecoveryPoint, CancellationToken, Task<InstallerReadOnlyPlanResult>> RollbackInspection { get; init; } =
            (_, _, _) => throw new AssertionException("Rollback inspection wasn't expected.");
        public Func<string, InstallerRecoveryPoint, CancellationToken, Task<InstallerRecoveryPrunePlanResult>> PruneInspection { get; init; } =
            (_, _, _) => throw new AssertionException("Recovery-prune inspection wasn't expected.");
        public Func<IReadOnlyList<InstallerReadOnlyPlanCandidate>, CancellationToken, Task<InstallerReadOnlyPlanResult>> Approval { get; init; } =
            (_, _) => throw new AssertionException("Candidate approval wasn't expected.");
        public Func<InstallerPlanConfirmation, CancellationToken, Task<InstallerConfirmedPlanAuthority>> Confirmation { get; init; } =
            (_, _) => Task.FromResult(new InstallerConfirmedPlanAuthority());
        public Func<InstallerRecoveryPruneConfirmation, CancellationToken, Task<InstallerConfirmedRecoveryPruneAuthority>> PruneConfirmation { get; init; } =
            (_, _) => Task.FromResult(new InstallerConfirmedRecoveryPruneAuthority());
        public Func<InstallerConfirmedPlanAuthority, CancellationToken, Task<InstallerExecutionOperation>> Execution { get; init; } =
            (_, _) => Task.FromResult(ExecutionOperation());
        public Func<InstallerConfirmedRecoveryPruneAuthority, CancellationToken, Task<InstallerRecoveryPruneOperation>> PruneExecution { get; init; } =
            (_, _) => Task.FromResult(RecoveryPruneOperation());
        public List<string> InspectedPaths { get; } = [];
        public List<InstallerOperation> InspectedOperations { get; } = [];
        public List<string> RecoveryCatalogPaths { get; } = [];
        public List<(string Path, InstallerRecoveryPoint Point)> RollbackInspections { get; } = [];
        public List<(string Path, InstallerRecoveryPoint Point)> PruneInspections { get; } = [];
        public List<IReadOnlyList<InstallerReadOnlyPlanCandidate>> ApprovedCandidates { get; } = [];
        public List<InstallerPlanConfirmation> ConfirmedPlans { get; } = [];
        public List<InstallerRecoveryPruneConfirmation> ConfirmedPrunes { get; } = [];
        public List<InstallerConfirmedPlanAuthority> ExecutedAuthorities { get; } = [];
        public List<InstallerConfirmedRecoveryPruneAuthority> ExecutedPrunes { get; } = [];
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

        public Task<InstallerRecoveryCatalogResult> ListRecoveriesAsync(
            string canonicalGamePath,
            CancellationToken cancellationToken = default
        )
        {
            this.RecoveryCatalogPaths.Add(canonicalGamePath);
            return this.RecoveryCatalog(canonicalGamePath, cancellationToken);
        }

        public Task<InstallerReadOnlyPlanResult> InspectRollbackAsync(
            string canonicalGamePath,
            InstallerRecoveryPoint point,
            CancellationToken cancellationToken = default
        )
        {
            this.RollbackInspections.Add((canonicalGamePath, point));
            return this.RollbackInspection(canonicalGamePath, point, cancellationToken);
        }

        public Task<InstallerRecoveryPrunePlanResult> InspectRecoveryPruneAsync(
            string canonicalGamePath,
            InstallerRecoveryPoint oldestPointToKeep,
            CancellationToken cancellationToken = default
        )
        {
            this.PruneInspections.Add((canonicalGamePath, oldestPointToKeep));
            return this.PruneInspection(canonicalGamePath, oldestPointToKeep, cancellationToken);
        }

        public Task<InstallerReadOnlyPlanResult> ApprovePlanCandidatesAsync(
            IReadOnlyList<InstallerReadOnlyPlanCandidate> candidates,
            CancellationToken cancellationToken = default
        )
        {
            this.ApprovedCandidates.Add(candidates.ToArray());
            this.ApprovalStarted.TrySetResult();
            return this.Approval(candidates, cancellationToken);
        }

        public Task<InstallerConfirmedPlanAuthority> ConfirmPlanAsync(
            InstallerPlanConfirmation confirmation,
            CancellationToken cancellationToken = default
        )
        {
            this.ConfirmedPlans.Add(confirmation);
            this.ConfirmationStarted.TrySetResult();
            return this.Confirmation(confirmation, cancellationToken);
        }

        public Task<InstallerConfirmedRecoveryPruneAuthority> ConfirmRecoveryPruneAsync(
            InstallerRecoveryPruneConfirmation confirmation,
            CancellationToken cancellationToken = default
        )
        {
            this.ConfirmedPrunes.Add(confirmation);
            this.PruneConfirmationStarted.TrySetResult();
            return this.PruneConfirmation(confirmation, cancellationToken);
        }

        public Task<InstallerExecutionOperation> ExecutePlanAsync(
            InstallerConfirmedPlanAuthority authority,
            CancellationToken cancellationToken = default
        )
        {
            this.ExecutedAuthorities.Add(authority);
            return this.Execution(authority, cancellationToken);
        }

        public Task<InstallerRecoveryPruneOperation> ExecuteRecoveryPruneAsync(
            InstallerConfirmedRecoveryPruneAuthority authority,
            CancellationToken cancellationToken = default
        )
        {
            this.ExecutedPrunes.Add(authority);
            return this.PruneExecution(authority, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            this.DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }
}
