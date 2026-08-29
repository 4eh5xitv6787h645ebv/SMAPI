using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Ownership;
using StardewModdingAPI.Installer.Core.Packages;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Core.Recovery;
using StardewModdingAPI.Installer.Core.Security;
using StardewModdingAPI.Installer.Core.Transactions;

namespace StardewModdingAPI.Installer.Core.Tests.Protocol.V1;

[TestFixture]
internal sealed class LinuxInstallerProtocolServiceTests
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly GameRootIdentity Root = new("/game", 1, 2, 3);

    [Test]
    public async Task EveryAcceptedCommandIsUniqueAndEveryResponseProgressAndTerminalIsExactlyCorrelated()
    {
        List<ProtocolEvent> emitted = [];
        FakeEngine engine = new() { BlockExecutionUntilCancellation = true };
        using LinuxInstallerProtocolService service = Create(engine, new FakePackageOpener(), emitted.Add);

        HandshakeRequest handshakeRequest = new("gui", "1");
        HandshakeEvent handshake = (HandshakeEvent)await service.HandleAsync(handshakeRequest);
        handshake.CommandId.Should().Be(handshakeRequest.CommandId);
        Func<Task> reusedHandshake = async () => await service.HandleAsync(new DiscoverGamesRequest(handshake.SessionId) { CommandId = handshakeRequest.CommandId });
        await reusedHandshake.Should().ThrowAsync<ProtocolException>().WithMessage("*can't be reused*");

        DiscoverGamesRequest discoverRequest = new(handshake.SessionId);
        GameDiscoveryEvent discovery = (GameDiscoveryEvent)await service.HandleAsync(discoverRequest);
        discovery.CommandId.Should().Be(discoverRequest.CommandId);
        Func<Task> duplicateShort = async () => await service.HandleAsync(discoverRequest);
        await duplicateShort.Should().ThrowAsync<ProtocolException>().WithMessage("*can't be reused*");

        InspectPlanRequest inspectRequest = new(service.SessionId, "/game", InstallerOperation.Uninstall, null, null);
        PlanEvent plan = (PlanEvent)await service.HandleAsync(inspectRequest);
        plan.CommandId.Should().Be(inspectRequest.CommandId);
        ConfirmPlanRequest confirmRequest = new(service.SessionId, plan.PlanId, plan.PlanDigest);
        CommandAcknowledgedEvent confirmed = (CommandAcknowledgedEvent)await service.HandleAsync(confirmRequest);
        confirmed.CommandId.Should().Be(confirmRequest.CommandId);
        confirmed.Acknowledgement.Should().Be(ProtocolAcknowledgementKind.PlanConfirmed);

        ExecutePlanRequest executeRequest = new(service.SessionId, plan.PlanId, plan.PlanDigest);
        Task<ProtocolEvent> execution = service.HandleAsync(executeRequest);
        await engine.ExecutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        CancelPlanRequest cancelRequest = new(service.SessionId, plan.PlanId, plan.PlanDigest);
        CommandAcknowledgedEvent cancellation = (CommandAcknowledgedEvent)await service.HandleAsync(cancelRequest);
        cancellation.CommandId.Should().Be(cancelRequest.CommandId);
        cancellation.Acknowledgement.Should().Be(ProtocolAcknowledgementKind.PlanCancellationRequested);
        Func<Task> duplicateCancel = async () => await service.HandleAsync(cancelRequest);
        await duplicateCancel.Should().ThrowAsync<ProtocolException>().WithMessage("*can't be reused*");

        ProtocolEvent terminal = await execution;
        terminal.CommandId.Should().Be(executeRequest.CommandId);
        emitted.OfType<ProgressEvent>().Should().NotBeEmpty().And.OnlyContain(item => item.CommandId == executeRequest.CommandId);
        Func<Task> duplicateLong = async () => await service.HandleAsync(executeRequest);
        await duplicateLong.Should().ThrowAsync<ProtocolException>().WithMessage("*can't be reused*");
    }

    [TestCase("checksums", "checksums.txt")]
    [TestCase("metadata", "metadata.json")]
    public async Task ActualPackageOpenerRejectsNonExactReleaseMetadataFilenames(string selectedAsset, string wrongFilename)
    {
        string root = CreateTemporaryDirectory();
        try
        {
            OpenPackageRequest request = CreateActualPackageRequest(root);
            string wrongPath = Path.Combine(root, wrongFilename);
            File.WriteAllText(wrongPath, "untrusted");
            request = selectedAsset == "checksums" ? request with { ChecksumsPath = wrongPath } : request with { BuildMetadataPath = wrongPath };

            Func<Task> open = async () => await new LinuxInstallerProtocolPackageOpener().OpenAsync(request, CancellationToken.None);

            await open.Should().ThrowAsync<PackageSecurityException>().WithMessage("*filename*");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestCase("checksums")]
    [TestCase("metadata")]
    [Platform("Linux")]
    public async Task ActualPackageOpenerRejectsReleaseMetadataPathSubstitution(string selectedAsset)
    {
        string root = CreateTemporaryDirectory();
        try
        {
            OpenPackageRequest request = CreateActualPackageRequest(root);
            File.WriteAllText(request.ChecksumsPath, "untrusted");
            File.WriteAllText(request.BuildMetadataPath, "untrusted");
            string selectedPath = selectedAsset == "checksums" ? request.ChecksumsPath : request.BuildMetadataPath;
            string movedPath = selectedPath + ".substituted-target";
            File.Move(selectedPath, movedPath);
            File.CreateSymbolicLink(selectedPath, movedPath);

            Func<Task> open = async () => await new LinuxInstallerProtocolPackageOpener().OpenAsync(request, CancellationToken.None);

            await open.Should().ThrowAsync<PackageSecurityException>().WithMessage("*safe accessible single-link regular file*");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestCase(null, ProtocolNextAction.StartNewSession)]
    [TestCase("/tmp/sanitized-installer.log", ProtocolNextAction.ViewPrivateLog)]
    public async Task UnexpectedPrePlanFailureOffersOnlyAnActuallyAvailableRecoveryAction(string? sanitizedLogPath, ProtocolNextAction expectedAction)
    {
        FakePackageOpener opener = new() { ThrowUnexpected = true };
        using LinuxInstallerProtocolService service = Create(new FakeEngine(), opener, sanitizedLogPath: sanitizedLogPath);
        await Handshake(service);
        OpenPackageRequest request = new(service.SessionId, CreateRelease().Tag, CreateRelease().SourceCommit, "/tmp/package", "/tmp/checksums", "/tmp/build", "/tmp/manifest");

        PrePlanRejectedEvent rejected = (PrePlanRejectedEvent)await service.HandleAsync(request);

        rejected.CommandId.Should().Be(request.CommandId);
        rejected.ErrorCode.Should().Be(ProtocolPrePlanErrorCode.UnexpectedFailure);
        rejected.NextAction.Should().Be(expectedAction);
        rejected.SanitizedLogPath.Should().Be(sanitizedLogPath);
        rejected.IsTerminal.Should().BeTrue();
    }

    [Test]
    public async Task DiscoveryUsesExactCoreStatesAndEveryEventIsSerializable()
    {
        List<ProtocolEvent> emitted = [];
        FakeEngine engine = new();
        using LinuxInstallerProtocolService service = Create(engine, new FakePackageOpener(), emitted.Add);
        HandshakeEvent handshake = (HandshakeEvent)(await service.HandleAsync(new HandshakeRequest("gui", "1")))!;
        GameDiscoveryEvent discovery = (GameDiscoveryEvent)(await service.HandleAsync(new DiscoverGamesRequest(handshake.SessionId)))!;

        discovery.Candidates.Should().ContainSingle().Which.State.Should().Be(LinuxGameFolderStatus.UnsafeLauncher);
        emitted.Should().BeEmpty("command responses are returned exactly once and only unsolicited progress uses the sink");
        new ProtocolEvent[] { handshake, discovery }.Should().OnlyContain(value => ProtocolJsonSerializer.DeserializeEventLine(ProtocolJsonSerializer.SerializeLine(value)).Kind == value.Kind);
    }

    [Test]
    public async Task InterruptedRecoveryUsesCoreAuthorityInvalidatesPriorStateAndReturnsBoundedExactResult()
    {
        List<ProtocolEvent> emitted = []; FakeEngine engine = new();
        using LinuxInstallerProtocolService service = Create(engine, new FakePackageOpener(), emitted.Add);
        await Handshake(service);
        RecoveryCatalogEvent catalog = (RecoveryCatalogEvent)(await service.HandleAsync(new ListRecoveriesRequest(service.SessionId, "/game")))!;
        PlanEvent stalePlan = (PlanEvent)(await service.HandleAsync(new InspectPlanRequest(service.SessionId, "/game", InstallerOperation.Uninstall, null, null)))!;

        RecoveryCompletedEvent recovered = (RecoveryCompletedEvent)(await service.HandleAsync(new RecoverInterruptedRequest(service.SessionId, "/game")))!;

        recovered.Attempt.GameRoot.Should().Be(new ProtocolGameRootIdentity("/game", 1, 2, 3, 8));
        recovered.Attempt.PreviousOperationGeneration.Should().Be(7); recovered.Attempt.CurrentOperationGeneration.Should().Be(8);
        recovered.Attempt.RecoveredTransactionCount.Should().Be(1); recovered.Attempt.RecoveredPathCount.Should().Be(2);
        emitted.OfType<RecoveryProgressEvent>().Select(value => value.Stage).Should().Equal(TransactionStage.AcquiringLock, TransactionStage.Recovering, TransactionStage.Completed);
        service.State.Should().Be(ProtocolSessionState.Ready);
        Func<Task> staleCatalog = async () => await service.HandleAsync(new InspectPruneRequest(service.SessionId, catalog.CatalogId, 1));
        Func<Task> staleConfirmation = async () => await service.HandleAsync(new ConfirmPlanRequest(service.SessionId, stalePlan.PlanId, stalePlan.PlanDigest));
        await staleCatalog.Should().ThrowAsync<ProtocolException>().WithMessage("*unknown or stale*");
        await staleConfirmation.Should().ThrowAsync<ProtocolException>();
    }

    [Test]
    public async Task InterruptedRecoveryFailureIsSanitizedSerializableAndLeavesSessionRetryable()
    {
        FakeEngine engine = new() { ThrowRecovery = true };
        using LinuxInstallerProtocolService service = Create(engine, new FakePackageOpener());
        await Handshake(service);

        RecoveryFailureEvent failure = (RecoveryFailureEvent)(await service.HandleAsync(new RecoverInterruptedRequest(service.SessionId, "/game")))!;

        failure.Outcome.Should().Be(ProtocolInterruptedRecoveryOutcome.UnexpectedFailure); failure.TerminalState.ErrorCode.Should().Be(ProtocolTerminalErrorCode.UnexpectedCoreFailure); failure.Message.Should().NotContain("private-secret");
        ProtocolJsonSerializer.DeserializeEventLine(ProtocolJsonSerializer.SerializeLine(failure)).Should().BeEquivalentTo(failure);
        service.State.Should().Be(ProtocolSessionState.RecoveryRequired);
        Func<Task> inspect = async () => await service.HandleAsync(new InspectPlanRequest(service.SessionId, "/game", InstallerOperation.Uninstall, null, null));
        Func<Task> open = async () => await Open(service);
        await inspect.Should().ThrowAsync<ProtocolException>(); await open.Should().ThrowAsync<ProtocolException>();
        engine.ThrowRecovery = false;
        (await service.HandleAsync(new RecoverInterruptedRequest(service.SessionId, "/game"))).Should().BeOfType<RecoveryCompletedEvent>();
        service.State.Should().Be(ProtocolSessionState.Ready);
    }

    [Test]
    public async Task UnrequestedCoreCancellationIsRecoveryPendingAndBlocksUnsafeCommands()
    {
        FakeEngine engine = new() { ThrowUnrequestedRecoveryCancellation = true };
        using LinuxInstallerProtocolService service = Create(engine, new FakePackageOpener());
        await Handshake(service);

        RecoveryFailureEvent failure = (RecoveryFailureEvent)(await service.HandleAsync(new RecoverInterruptedRequest(service.SessionId, "/game")))!;

        failure.Outcome.Should().Be(ProtocolInterruptedRecoveryOutcome.UnexpectedFailure); failure.TerminalState.Should().Be(new ProtocolTerminalState(ProtocolDurableState.Unknown, ProtocolTerminalErrorCode.UnexpectedCoreFailure, ProtocolRecoveryDisposition.InterruptedRecoveryRequired, ProtocolNextAction.RecoverInterrupted));
        service.State.Should().Be(ProtocolSessionState.RecoveryRequired);
    }

    [Test]
    public async Task ConcurrentOuterCancellationCannotReclassifyAnUnrelatedCoreCancellationAsSafe()
    {
        FakeEngine engine = new() { ThrowUnrequestedRecoveryCancellation = true, BlockUnrequestedRecoveryCancellation = true };
        using LinuxInstallerProtocolService service = Create(engine, new FakePackageOpener());
        await Handshake(service);
        using CancellationTokenSource outer = new();
        Task<ProtocolEvent> recovery = service.HandleAsync(new RecoverInterruptedRequest(service.SessionId, "/game"), outer.Token);
        await engine.UnrequestedRecoveryCancellationReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

        outer.Cancel(); engine.ReleaseUnrequestedRecoveryCancellation.TrySetResult();
        RecoveryFailureEvent failure = (RecoveryFailureEvent)(await recovery)!;

        failure.Outcome.Should().Be(ProtocolInterruptedRecoveryOutcome.UnexpectedFailure); failure.TerminalState.DurableState.Should().Be(ProtocolDurableState.Unknown);
        service.State.Should().Be(ProtocolSessionState.RecoveryRequired);
    }

    [Test]
    public async Task TypedPartialRecoveryFailurePreservesExactKnownAndUnknownProgressWithoutPaths()
    {
        FakeEngine engine = new() { ThrowPartialRecovery = true };
        using LinuxInstallerProtocolService service = Create(engine, new FakePackageOpener());
        await Handshake(service);

        RecoveryFailureEvent failure = (RecoveryFailureEvent)(await service.HandleAsync(new RecoverInterruptedRequest(service.SessionId, "/game")))!;

        failure.Outcome.Should().Be(ProtocolInterruptedRecoveryOutcome.PartialFailure); failure.TerminalState.ErrorCode.Should().Be(ProtocolTerminalErrorCode.RecoveryFailed);
        failure.Attempt.Should().NotBeNull(); failure.Attempt!.CurrentOperationGeneration.Should().BeNull(); failure.Attempt.OperationGenerationAdvanced.Should().BeNull(); failure.Attempt.NamedRootStillSelected.Should().BeNull(); failure.Attempt.NamedRootSelectionChanged.Should().BeNull();
        failure.RequiresRecovery.Should().BeTrue(); failure.RequiresFreshInspection.Should().BeTrue();
        failure.Attempt.RecoveredTransactionCount.Should().Be(1); failure.Attempt.RecoveredPathCount.Should().Be(2);
        string line = ProtocolJsonSerializer.SerializeLine(failure); line.Should().NotContain("private-partial-failure");
        ProtocolJsonSerializer.DeserializeEventLine(line).Should().BeEquivalentTo(failure);
        service.State.Should().Be(ProtocolSessionState.RecoveryRequired);
    }

    [Test]
    public async Task OuterCancellationAndAsyncDisposalWaitForInterruptedRecoveryTerminal()
    {
        FakeEngine engine = new() { BlockRecoveryUntilCancellation = true };
        LinuxInstallerProtocolService service = Create(engine, new FakePackageOpener());
        await Handshake(service);
        Task<ProtocolEvent> recovery = service.HandleAsync(new RecoverInterruptedRequest(service.SessionId, "/game"));
        await engine.RecoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task first = service.DisposeAsync().AsTask(); Task second = service.DisposeAsync().AsTask();
        RecoveryFailureEvent terminal = (RecoveryFailureEvent)(await recovery)!;
        terminal.Outcome.Should().Be(ProtocolInterruptedRecoveryOutcome.CancelledBeforeRecovery); terminal.TerminalState.Should().Be(new ProtocolTerminalState(ProtocolDurableState.Unchanged, null, ProtocolRecoveryDisposition.InterruptedRecoveryRequired, ProtocolNextAction.RecoverInterrupted)); terminal.RequiresRecovery.Should().BeTrue();
        await Task.WhenAll(first, second);
        Func<Task> useAfterDispose = async () => await service.HandleAsync(new DiscoverGamesRequest(service.SessionId));
        await useAfterDispose.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Test]
    public async Task ForeignAndStalePackageIdsNeverReachEngineAndAuthoritiesDisposeOnce()
    {
        FakePackageOpener openerA = new(); FakeEngine engineA = new();
        FakePackageOpener openerB = new(); FakeEngine engineB = new();
        using LinuxInstallerProtocolService first = Create(engineA, openerA);
        using LinuxInstallerProtocolService second = Create(engineB, openerB);
        await Handshake(first); await Handshake(second);
        PackageOpenedEvent package = await Open(first);

        Func<Task> foreign = async () => await second.HandleAsync(new InspectPlanRequest(second.SessionId, "/game", InstallerOperation.Install, package.PackageId, null));
        await foreign.Should().ThrowAsync<ProtocolException>().WithMessage("*unknown, stale*");
        engineB.InspectCalls.Should().Be(0);
        first.Dispose(); openerA.Owners.Single().DisposeCount.Should().Be(1);
        first.Dispose(); openerA.Owners.Single().DisposeCount.Should().Be(1);
    }

    [Test]
    public async Task ForeignRecoverySelectionNeverReachesEngine()
    {
        FakeEngine engineA = new(); FakeEngine engineB = new();
        using LinuxInstallerProtocolService first = Create(engineA, new FakePackageOpener());
        using LinuxInstallerProtocolService second = Create(engineB, new FakePackageOpener());
        await Handshake(first); await Handshake(second);
        RecoveryCatalogEvent catalog = (RecoveryCatalogEvent)(await first.HandleAsync(new ListRecoveriesRequest(first.SessionId, "/game")))!;

        Func<Task> foreign = async () => await second.HandleAsync(new InspectPlanRequest(second.SessionId, "/game", InstallerOperation.Rollback, null, catalog.Generations[0].SelectionId));
        await foreign.Should().ThrowAsync<ProtocolException>().WithMessage("*unknown, stale*");
        engineB.InspectCalls.Should().Be(0);
    }

    [Test]
    public async Task EveryActionResolvesOnlyTheSessionOwnedPackageOrRecoveryAuthority()
    {
        FakeEngine engine = new(); FakePackageOpener opener = new();
        using LinuxInstallerProtocolService service = Create(engine, opener);
        await Handshake(service); PackageOpenedEvent package = await Open(service);
        RecoveryCatalogEvent catalog = (RecoveryCatalogEvent)(await service.HandleAsync(new ListRecoveriesRequest(service.SessionId, "/game")))!;
        ProtocolRecoverySelectionId recovery = catalog.Generations[0].SelectionId;

        foreach (InstallerOperation action in Enum.GetValues<InstallerOperation>())
        {
            ProtocolPackageId? packageId = action is InstallerOperation.Install or InstallerOperation.Update or InstallerOperation.Repair ? package.PackageId : null;
            ProtocolRecoverySelectionId? recoveryId = action == InstallerOperation.Rollback ? recovery : null;
            PlanEvent plan = (PlanEvent)(await service.HandleAsync(new InspectPlanRequest(service.SessionId, "/game", action, packageId, recoveryId)))!;
            plan.Operation.Should().Be(action);
        }

        engine.Observed.Select(value => value.Action).Should().Equal(Enum.GetValues<InstallationAction>());
        engine.Observed.Where(value => value.Action is InstallationAction.Install or InstallationAction.Update or InstallationAction.Repair).Should().OnlyContain(value => ReferenceEquals(value.Package, opener.Authorities.Single()));
        ICommittedRecoveryContentAuthority? selectedRecovery = engine.Observed.Single(value => value.Action == InstallationAction.Rollback).Recovery;
        engine.OpenedRecoveries.Should().Contain(handle => ReferenceEquals(handle, selectedRecovery));
    }

    [Test]
    public async Task ConfirmedExecutionEmitsExactCoreProgressAndTerminalData()
    {
        List<ProtocolEvent> emitted = []; FakeEngine engine = new() { ExecuteChangedCount = 1 };
        using LinuxInstallerProtocolService service = Create(engine, new FakePackageOpener(), emitted.Add);
        await Handshake(service);
        PlanEvent plan = (PlanEvent)(await service.HandleAsync(new InspectPlanRequest(service.SessionId, "/game", InstallerOperation.Uninstall, null, null)))!;
        await service.HandleAsync(new ConfirmPlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest));
        SuccessEvent success = (SuccessEvent)(await service.HandleAsync(new ExecutePlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest)))!;

        emitted.OfType<ProgressEvent>().Select(value => value.Stage).Should().Equal(TransactionStage.Revalidating, TransactionStage.Applying, TransactionStage.Completed);
        emitted.OfType<ProgressEvent>().Select(value => value.Sequence).Should().Equal(0, 1, 2);
        success.ExecutionSummary.ManagedFileChangeCount.Should().Be(1); success.Operation.Should().Be(InstallerOperation.Uninstall);
        service.State.Should().Be(ProtocolSessionState.Completed);
    }

    [Test]
    public async Task MidExecutionCancellationAndAsyncDisposalWaitForCoreTerminalBeforeAuthorityDisposal()
    {
        FakeEngine engine = new() { BlockExecutionUntilCancellation = true }; FakePackageOpener opener = new();
        LinuxInstallerProtocolService service = Create(engine, opener);
        await Handshake(service); PackageOpenedEvent package = await Open(service);
        PlanEvent plan = (PlanEvent)(await service.HandleAsync(new InspectPlanRequest(service.SessionId, "/game", InstallerOperation.Repair, package.PackageId, null)))!;
        await service.HandleAsync(new ConfirmPlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest));
        Task<ProtocolEvent> execution = service.HandleAsync(new ExecutePlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest));
        await engine.ExecutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task disposal = Task.Run(service.Dispose);
        await disposal;
        (await execution).Should().BeOfType<CancelledEvent>();
        opener.Owners.Single().DisposeCount.Should().Be(1);
    }

    [Test]
    public async Task PruneUsesStoredCatalogIdentityReportsExactStageAndDisposesRelistedHandles()
    {
        List<ProtocolEvent> emitted = []; FakeEngine engine = new();
        using LinuxInstallerProtocolService service = Create(engine, new FakePackageOpener(), emitted.Add);
        await Handshake(service);
        RecoveryCatalogEvent old = (RecoveryCatalogEvent)(await service.HandleAsync(new ListRecoveriesRequest(service.SessionId, "/game")))!;
        FakeRecoveryAuthority[] oldHandles = engine.OpenedRecoveries.ToArray();
        RecoveryCatalogEvent current = (RecoveryCatalogEvent)(await service.HandleAsync(new ListRecoveriesRequest(service.SessionId, "/game")))!;
        oldHandles.Should().OnlyContain(handle => handle.DisposeCount == 1);
        Func<Task> stale = async () => await service.HandleAsync(new InspectPruneRequest(service.SessionId, old.CatalogId, 1));
        await stale.Should().ThrowAsync<ProtocolException>().WithMessage("*unknown or stale*");

        PrunePlanEvent plan = (PrunePlanEvent)(await service.HandleAsync(new InspectPruneRequest(service.SessionId, current.CatalogId, 1)))!;
        await service.HandleAsync(new ConfirmPruneRequest(service.SessionId, plan.PrunePlanId, plan.PruneDigest));
        PruneSuccessEvent success = (PruneSuccessEvent)(await service.HandleAsync(new ExecutePruneRequest(service.SessionId, plan.PrunePlanId, plan.PruneDigest)))!;
        success.PruneSummary.LogicallyRemovedGenerationCount.Should().Be(1); success.PruneSummary.PhysicallyCleanedGenerationCount.Should().Be(1);
        emitted.OfType<PruneProgressEvent>().Should().ContainSingle().Which.Stage.Should().Be(TransactionStage.VerifyingRecovery);
    }

    [TestCase(InstallationExecutionStatus.Succeeded, typeof(SuccessEvent), ProtocolExecutionOutcome.Succeeded, ProtocolDurableState.Committed, ProtocolRecoveryDisposition.NotRequired)]
    [TestCase(InstallationExecutionStatus.SucceededWithCleanupWarning, typeof(SuccessEvent), ProtocolExecutionOutcome.SucceededWithCleanupWarning, ProtocolDurableState.Committed, ProtocolRecoveryDisposition.CleanupPending)]
    [TestCase(InstallationExecutionStatus.FailedBeforeMutation, typeof(RolledBackFailureEvent), ProtocolExecutionOutcome.FailedBeforeMutation, ProtocolDurableState.Unchanged, ProtocolRecoveryDisposition.NotRequired)]
    [TestCase(InstallationExecutionStatus.FailedAndRolledBack, typeof(RolledBackFailureEvent), ProtocolExecutionOutcome.FailedAndRolledBack, ProtocolDurableState.RolledBack, ProtocolRecoveryDisposition.Completed)]
    [TestCase(InstallationExecutionStatus.InterruptedRecoveryRequired, typeof(RecoverableInterruptionEvent), ProtocolExecutionOutcome.InterruptedRecoveryRequired, ProtocolDurableState.RecoveryRequired, ProtocolRecoveryDisposition.InterruptedRecoveryRequired)]
    [TestCase(InstallationExecutionStatus.AutomaticRecoveryCompletedFreshInspectionRequired, typeof(RolledBackFailureEvent), ProtocolExecutionOutcome.AutomaticRecoveryCompletedFreshInspectionRequired, ProtocolDurableState.RecoveryCompleted, ProtocolRecoveryDisposition.Completed)]
    public async Task EveryNonCancellationExecutionOutcomeMapsToOneExactTerminal(InstallationExecutionStatus status, Type expectedType, ProtocolExecutionOutcome expectedOutcome, ProtocolDurableState durable, ProtocolRecoveryDisposition recovery)
    {
        List<ProtocolEvent> emitted = []; FakeEngine engine = new() { NextExecutionOutcome = CreateOutcome(status) };
        using LinuxInstallerProtocolService service = Create(engine, new FakePackageOpener(), emitted.Add);
        await Handshake(service);
        PlanEvent plan = (PlanEvent)(await service.HandleAsync(new InspectPlanRequest(service.SessionId, "/game", InstallerOperation.Uninstall, null, null)))!;
        await service.HandleAsync(new ConfirmPlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest));
        ProtocolEvent terminal = (await service.HandleAsync(new ExecutePlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest)))!;

        terminal.Should().BeOfType(expectedType); GetExecutionOutcome(terminal).Should().Be(expectedOutcome); GetTerminalState(terminal).Should().Match<ProtocolTerminalState>(state => state.DurableState == durable && state.RecoveryDisposition == recovery);
        emitted.Should().OnlyContain(value => value is ProgressEvent, "terminal command responses are returned and never duplicated through the progress sink");
        service.State.Should().Be(ProtocolSessionState.Completed);
    }

    [TestCaseSource(nameof(TransactionErrors))]
    public async Task EveryCoreTransactionErrorMapsToTheExactClosedProtocolError(TransactionErrorCode error)
    {
        FakeEngine engine = new() { NextExecutionOutcome = CreateOutcome(InstallationExecutionStatus.FailedBeforeMutation, error) };
        using LinuxInstallerProtocolService service = Create(engine, new FakePackageOpener()); await Handshake(service);
        PlanEvent plan = (PlanEvent)await service.HandleAsync(new InspectPlanRequest(service.SessionId, "/game", InstallerOperation.Uninstall, null, null));
        await service.HandleAsync(new ConfirmPlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest));
        RolledBackFailureEvent terminal = (RolledBackFailureEvent)await service.HandleAsync(new ExecutePlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest));
        terminal.TerminalState.ErrorCode.Should().NotBeNull();
        terminal.TerminalState.ErrorCode!.Value.ToString().Should().Be(error.ToString());
    }

    [Test]
    public async Task UnknownCoreStatusEscapesAsProtocolFailureInsteadOfInventingATerminal()
    {
        FakeEngine engine = new() { NextExecutionOutcome = CreateOutcome((InstallationExecutionStatus)999, TransactionErrorCode.IoFailure) };
        using LinuxInstallerProtocolService service = Create(engine, new FakePackageOpener()); await Handshake(service);
        PlanEvent plan = (PlanEvent)await service.HandleAsync(new InspectPlanRequest(service.SessionId, "/game", InstallerOperation.Uninstall, null, null));
        await service.HandleAsync(new ConfirmPlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest));
        Func<Task> execute = async () => await service.HandleAsync(new ExecutePlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest));
        await execute.Should().ThrowAsync<ProtocolException>().WithMessage("*unknown installation execution status*");
    }

    [TestCase(true, ProtocolNextAction.InspectAgain)]
    [TestCase(false, ProtocolNextAction.SelectGameFolder)]
    public async Task RecoveryCompletionActionTracksWhetherTheNamedRootStillSelectsTheRecoveredAnchor(bool namedRootStillSelected, ProtocolNextAction action)
    {
        FakeEngine engine = new() { RecoveryNamedRootStillSelected = namedRootStillSelected };
        using LinuxInstallerProtocolService service = Create(engine, new FakePackageOpener()); await Handshake(service);
        RecoveryCompletedEvent terminal = (RecoveryCompletedEvent)await service.HandleAsync(new RecoverInterruptedRequest(service.SessionId, "/game"));
        terminal.Attempt.NamedRootStillSelected.Should().Be(namedRootStillSelected);
        terminal.TerminalState.Should().Be(new ProtocolTerminalState(ProtocolDurableState.RecoveryCompleted, null, ProtocolRecoveryDisposition.Completed, action));
    }

    [Test]
    public async Task InterruptedTerminalReportsExactManagedChangesAndPartialRollbackWithoutPathLeakage()
    {
        TransactionPathChange[] changed = [new("managed-a", TransactionOperationKind.WriteFile), new(".smapi-installer/private", TransactionOperationKind.WriteFile)];
        TransactionPathChange[] restored = [changed[1]];
        TransactionExecutionOutcome transaction = new(Guid.NewGuid(), TransactionOutcomeStatus.RollbackFailedRecoveryRequired, null, changed, restored, TransactionCancellationDisposition.None, TransactionErrorCode.RecoveryFailed, "Recovery required.");
        FakeEngine engine = new() { NextExecutionOutcome = new(InstallationAction.Uninstall, InstallationExecutionStatus.InterruptedRecoveryRequired, transaction, [], TransactionErrorCode.RecoveryFailed, "Recovery required.") };
        using LinuxInstallerProtocolService service = Create(engine, new FakePackageOpener());
        await Handshake(service);
        PlanEvent plan = (PlanEvent)(await service.HandleAsync(new InspectPlanRequest(service.SessionId, "/game", InstallerOperation.Uninstall, null, null)))!;
        await service.HandleAsync(new ConfirmPlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest));

        RecoverableInterruptionEvent terminal = (RecoverableInterruptionEvent)(await service.HandleAsync(new ExecutePlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest)))!;
        terminal.ExecutionSummary.ManagedFileChangeCount.Should().Be(1); terminal.ExecutionSummary.RolledBackManagedFileCount.Should().Be(0); terminal.ExecutionSummary.InternalStateChangeCount.Should().Be(1); terminal.ExecutionSummary.RolledBackInternalStateCount.Should().Be(1); terminal.Summary.Should().Contain("restored 0 of 1");
        ProtocolJsonSerializer.SerializeLine(terminal).Should().NotContain("private");
    }

    [TestCase(InstallationExecutionStatus.CancelledBeforeMutation, 0, ProtocolDurableState.Unchanged, ProtocolRecoveryDisposition.NotRequired)]
    [TestCase(InstallationExecutionStatus.CancelledAndRolledBack, 1, ProtocolDurableState.RolledBack, ProtocolRecoveryDisposition.Completed)]
    public async Task BothCancellationBoundariesMapTruthfully(InstallationExecutionStatus status, int expectedChanged, ProtocolDurableState durable, ProtocolRecoveryDisposition recovery)
    {
        FakeEngine engine = new() { BlockExecutionUntilCancellation = true, CancellationOutcomeStatus = status };
        using LinuxInstallerProtocolService service = Create(engine, new FakePackageOpener());
        await Handshake(service);
        PlanEvent plan = (PlanEvent)(await service.HandleAsync(new InspectPlanRequest(service.SessionId, "/game", InstallerOperation.Uninstall, null, null)))!;
        await service.HandleAsync(new ConfirmPlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest));
        Task<ProtocolEvent> execution = service.HandleAsync(new ExecutePlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest));
        await engine.ExecutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        (await service.HandleAsync(new CancelPlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest))).Should().BeOfType<CommandAcknowledgedEvent>();
        CancelledEvent terminal = (CancelledEvent)(await execution)!;
        terminal.ExecutionSummary.ManagedFileChangeCount.Should().Be(expectedChanged); terminal.TerminalState.Should().Be(new ProtocolTerminalState(durable, null, recovery, ProtocolNextAction.InspectAgain));
    }

    [Test]
    public async Task OuterCancellationTransitionsStateAndPostTerminalProgressIsIgnored()
    {
        List<ProtocolEvent> emitted = []; FakeEngine engine = new() { BlockExecutionUntilCancellation = true };
        using LinuxInstallerProtocolService service = Create(engine, new FakePackageOpener(), emitted.Add);
        await Handshake(service);
        PlanEvent plan = (PlanEvent)(await service.HandleAsync(new InspectPlanRequest(service.SessionId, "/game", InstallerOperation.Uninstall, null, null)))!;
        await service.HandleAsync(new ConfirmPlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest));
        using CancellationTokenSource cancellation = new();
        Task<ProtocolEvent> execution = service.HandleAsync(new ExecutePlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest), cancellation.Token);
        await engine.ExecutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        (await execution).Should().BeOfType<CancelledEvent>();
        int before = emitted.Count; engine.Progress.Report(new(TransactionStage.Completed, 1, 1)); emitted.Should().HaveCount(before);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task LateExplicitOrOuterCancellationPreservesTruthfulCommittedSuccess(bool outer)
    {
        FakeEngine engine = new() { BlockExecutionUntilCancellation = true, CommitExecutionAfterCancellation = true };
        using LinuxInstallerProtocolService service = Create(engine, new FakePackageOpener());
        await Handshake(service);
        PlanEvent plan = (PlanEvent)(await service.HandleAsync(new InspectPlanRequest(service.SessionId, "/game", InstallerOperation.Uninstall, null, null)))!;
        await service.HandleAsync(new ConfirmPlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest));
        using CancellationTokenSource cancellation = new();
        Task<ProtocolEvent> execution = service.HandleAsync(new ExecutePlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest), outer ? cancellation.Token : default);
        await engine.ExecutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (outer) cancellation.Cancel();
        else (await service.HandleAsync(new CancelPlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest))).Should().BeOfType<CommandAcknowledgedEvent>();

        (await execution).Should().BeOfType<SuccessEvent>();
        service.State.Should().Be(ProtocolSessionState.Completed);
    }

    [Test]
    public async Task ExplicitCancellationWinningAtTerminalBoundaryCannotEraseCommittedSuccess()
    {
        TaskCompletionSource terminalStarting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseTerminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeEngine engine = new();
        using LinuxInstallerProtocolService service = Create(engine, new FakePackageOpener(), terminalCompletionStarting: () =>
        {
            terminalStarting.TrySetResult();
            releaseTerminal.Task.GetAwaiter().GetResult();
        });
        await Handshake(service);
        PlanEvent plan = (PlanEvent)(await service.HandleAsync(new InspectPlanRequest(service.SessionId, "/game", InstallerOperation.Uninstall, null, null)))!;
        await service.HandleAsync(new ConfirmPlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest));
        Task<ProtocolEvent> execution = Task.Run(async () => await service.HandleAsync(new ExecutePlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest)));
        await terminalStarting.Task.WaitAsync(TimeSpan.FromSeconds(5));

        (await service.HandleAsync(new CancelPlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest))).Should().BeOfType<CommandAcknowledgedEvent>();
        releaseTerminal.TrySetResult();
        (await execution).Should().BeOfType<SuccessEvent>();
        service.State.Should().Be(ProtocolSessionState.Completed);
    }

    [TestCase("before", 0)]
    [TestCase("after-progress", 1)]
    [TestCase("during-cancel", 1)]
    public async Task UnexpectedExecutionFaultAlwaysReturnsOneSanitizedConservativeTerminal(string boundary, int expectedProgress)
    {
        List<ProtocolEvent> emitted = [];
        FakeEngine engine = new()
        {
            ThrowExecutionBeforeProgress = boundary == "before",
            ThrowExecutionAfterProgress = boundary == "after-progress",
            BlockExecutionUntilCancellation = boundary == "during-cancel",
            ThrowExecutionAfterCancellation = boundary == "during-cancel"
        };
        using LinuxInstallerProtocolService service = Create(engine, new FakePackageOpener(), emitted.Add);
        await Handshake(service);
        PlanEvent plan = (PlanEvent)(await service.HandleAsync(new InspectPlanRequest(service.SessionId, "/game", InstallerOperation.Uninstall, null, null)))!;
        await service.HandleAsync(new ConfirmPlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest));
        Task<ProtocolEvent> execution = service.HandleAsync(new ExecutePlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest));
        if (boundary == "during-cancel")
        {
            await engine.ExecutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await service.HandleAsync(new CancelPlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest));
        }

        RecoverableInterruptionEvent terminal = (RecoverableInterruptionEvent)(await execution)!;
        terminal.Outcome.Should().Be(ProtocolExecutionOutcome.UnexpectedCoreFailure); terminal.TerminalState.Should().Be(new ProtocolTerminalState(ProtocolDurableState.Unknown, ProtocolTerminalErrorCode.UnexpectedCoreFailure, ProtocolRecoveryDisposition.InterruptedRecoveryRequired, ProtocolNextAction.RecoverInterrupted)); terminal.Message.Should().NotContain("private-secret");
        terminal.ExecutionSummary.Should().Be(new ProtocolExecutionSummary(null, null, null, null, null, null));
        emitted.OfType<ProgressEvent>().Should().HaveCount(expectedProgress);
        service.State.Should().Be(ProtocolSessionState.Completed);
    }

    [Test]
    public async Task ConcurrentAndDelayedProgressIsOrderedAndNeverEscapesAfterTerminal()
    {
        List<ProtocolEvent> emitted = []; FakeEngine engine = new() { ReportConcurrentProgress = true, ReportDelayedProgress = true };
        using LinuxInstallerProtocolService service = Create(engine, new FakePackageOpener(), emitted.Add);
        await Handshake(service);
        PlanEvent plan = (PlanEvent)(await service.HandleAsync(new InspectPlanRequest(service.SessionId, "/game", InstallerOperation.Uninstall, null, null)))!;
        await service.HandleAsync(new ConfirmPlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest));
        (await service.HandleAsync(new ExecutePlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest))).Should().BeOfType<SuccessEvent>();
        long[] sequences = emitted.OfType<ProgressEvent>().Select(value => value.Sequence).ToArray();
        sequences.Should().Equal(Enumerable.Range(0, sequences.Length).Select(value => (long)value));
        int terminalCount = emitted.Count;
        engine.ReleaseDelayedProgress.TrySetResult(); await engine.DelayedProgressReported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        emitted.Should().HaveCount(terminalCount);
    }

    [Test]
    public async Task TerminalWaitsForAlreadyAcceptedConcurrentProgressDispatch()
    {
        TaskCompletionSource progressEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseProgress = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource terminalStarting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<ProtocolEvent> delivered = [];
        FakeEngine engine = new() { ReportUnawaitedProgress = true };
        using LinuxInstallerProtocolService service = Create(engine, new FakePackageOpener(), value =>
        {
            if (value is ProgressEvent { Stage: TransactionStage.Applying })
            {
                progressEntered.TrySetResult();
                releaseProgress.Task.GetAwaiter().GetResult();
            }
            delivered.Add(value);
        }, terminalCompletionStarting: terminalStarting.SetResult);
        await Handshake(service);
        PlanEvent plan = (PlanEvent)(await service.HandleAsync(new InspectPlanRequest(service.SessionId, "/game", InstallerOperation.Uninstall, null, null)))!;
        await service.HandleAsync(new ConfirmPlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest));
        Task<ProtocolEvent> execution = service.HandleAsync(new ExecutePlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest));
        await progressEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        engine.ReturnWhileProgressIsBlocked.TrySetResult();
        await terminalStarting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        execution.IsCompleted.Should().BeFalse("terminal completion is serialized behind accepted progress dispatch");
        releaseProgress.TrySetResult();

        (await execution).Should().BeOfType<SuccessEvent>();
        delivered.OfType<ProgressEvent>().Should().Contain(value => value.Stage == TransactionStage.Applying);
    }

    [TestCase(RecoveryPruneOutcomeStatus.Succeeded, typeof(PruneSuccessEvent), ProtocolPruneOutcome.Succeeded, ProtocolRecoveryDisposition.NotRequired)]
    [TestCase(RecoveryPruneOutcomeStatus.FailedBeforePublication, typeof(PruneFailureEvent), ProtocolPruneOutcome.FailedBeforePublication, ProtocolRecoveryDisposition.NotRequired)]
    [TestCase(RecoveryPruneOutcomeStatus.Interrupted, typeof(PruneInterruptionEvent), ProtocolPruneOutcome.Interrupted, ProtocolRecoveryDisposition.CleanupPending)]
    [TestCase(RecoveryPruneOutcomeStatus.FailedWithCleanupPending, typeof(PruneFailureEvent), ProtocolPruneOutcome.FailedWithCleanupPending, ProtocolRecoveryDisposition.CleanupPending)]
    public async Task PruneTerminalMappingsPreserveLogicalPhysicalAndPendingTruth(RecoveryPruneOutcomeStatus status, Type expectedType, ProtocolPruneOutcome expectedOutcome, ProtocolRecoveryDisposition recovery)
    {
        FakeEngine engine = new() { NextPruneStatus = status };
        using LinuxInstallerProtocolService service = Create(engine, new FakePackageOpener());
        await Handshake(service); RecoveryCatalogEvent catalog = (RecoveryCatalogEvent)(await service.HandleAsync(new ListRecoveriesRequest(service.SessionId, "/game")))!;
        PrunePlanEvent plan = (PrunePlanEvent)(await service.HandleAsync(new InspectPruneRequest(service.SessionId, catalog.CatalogId, 1)))!;
        await service.HandleAsync(new ConfirmPruneRequest(service.SessionId, plan.PrunePlanId, plan.PruneDigest));
        ProtocolEvent terminal = (await service.HandleAsync(new ExecutePruneRequest(service.SessionId, plan.PrunePlanId, plan.PruneDigest)))!;
        terminal.Should().BeOfType(expectedType); GetPruneOutcome(terminal).Should().Be(expectedOutcome); GetTerminalState(terminal).RecoveryDisposition.Should().Be(recovery);
    }

    [TestCase(false, false, ProtocolDurableState.PruneApplied, ProtocolRecoveryDisposition.CleanupPending)]
    [TestCase(true, false, ProtocolDurableState.PruneApplied, ProtocolRecoveryDisposition.StateRefreshRequired)]
    [TestCase(false, true, ProtocolDurableState.Unchanged, ProtocolRecoveryDisposition.CleanupPending)]
    public async Task InterruptedPruneDerivesDurableStateAndRecoveryOnlyFromExactObservedWork(bool omitPending, bool suppressWork, ProtocolDurableState durable, ProtocolRecoveryDisposition recovery)
    {
        FakeEngine engine = new() { NextPruneStatus = RecoveryPruneOutcomeStatus.Interrupted, OmitPendingPruneCleanup = omitPending, SuppressPruneWork = suppressWork };
        using LinuxInstallerProtocolService service = Create(engine, new FakePackageOpener()); await Handshake(service);
        RecoveryCatalogEvent catalog = (RecoveryCatalogEvent)await service.HandleAsync(new ListRecoveriesRequest(service.SessionId, "/game"));
        PrunePlanEvent plan = (PrunePlanEvent)await service.HandleAsync(new InspectPruneRequest(service.SessionId, catalog.CatalogId, 1));
        await service.HandleAsync(new ConfirmPruneRequest(service.SessionId, plan.PrunePlanId, plan.PruneDigest));
        PruneInterruptionEvent terminal = (PruneInterruptionEvent)await service.HandleAsync(new ExecutePruneRequest(service.SessionId, plan.PrunePlanId, plan.PruneDigest));
        terminal.TerminalState.DurableState.Should().Be(durable); terminal.TerminalState.RecoveryDisposition.Should().Be(recovery);
        if (recovery == ProtocolRecoveryDisposition.StateRefreshRequired) terminal.Message.Should().NotContain("remain", "no cleanup is known to be pending");
    }

    [TestCase(RecoveryPruneOutcomeStatus.CancelledBeforePublication)]
    [TestCase(RecoveryPruneOutcomeStatus.CancelledWithCleanupPending)]
    public async Task CleanupOnlyPruneCancellationReportsNoLogicalRemovalAndPendingCleanup(RecoveryPruneOutcomeStatus status)
    {
        FakeEngine engine = new() { CleanupOnlyPrune = true, BlockPruneUntilCancellation = true, NextPruneStatus = status };
        using LinuxInstallerProtocolService service = Create(engine, new FakePackageOpener());
        await Handshake(service); RecoveryCatalogEvent catalog = (RecoveryCatalogEvent)(await service.HandleAsync(new ListRecoveriesRequest(service.SessionId, "/game")))!;
        PrunePlanEvent plan = (PrunePlanEvent)(await service.HandleAsync(new InspectPruneRequest(service.SessionId, catalog.CatalogId, 2)))!;
        await service.HandleAsync(new ConfirmPruneRequest(service.SessionId, plan.PrunePlanId, plan.PruneDigest));
        Task<ProtocolEvent> execution = service.HandleAsync(new ExecutePruneRequest(service.SessionId, plan.PrunePlanId, plan.PruneDigest));
        await engine.PruneStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); await service.HandleAsync(new CancelPruneRequest(service.SessionId, plan.PrunePlanId, plan.PruneDigest));
        PruneCancelledEvent terminal = (PruneCancelledEvent)(await execution)!;
        terminal.PruneSummary.LogicallyRemovedGenerationCount.Should().Be(0); terminal.PruneSummary.PhysicallyCleanedGenerationCount.Should().Be(0);
        terminal.TerminalState.RecoveryDisposition.Should().Be(ProtocolRecoveryDisposition.CleanupPending, "cleanup-only plans retain exact pre-existing physical cleanup work even when cancelled before publication");
        terminal.Summary.Should().Contain("physical generation cleanup");
        if (status == RecoveryPruneOutcomeStatus.CancelledWithCleanupPending)
            terminal.Summary.Should().Contain("No logical generations were removed");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task LateExplicitOrOuterPruneCancellationPreservesTruthfulSuccess(bool outer)
    {
        FakeEngine engine = new() { BlockPruneUntilCancellation = true, NextPruneStatus = RecoveryPruneOutcomeStatus.Succeeded };
        using LinuxInstallerProtocolService service = Create(engine, new FakePackageOpener());
        await Handshake(service); RecoveryCatalogEvent catalog = (RecoveryCatalogEvent)(await service.HandleAsync(new ListRecoveriesRequest(service.SessionId, "/game")))!;
        PrunePlanEvent plan = (PrunePlanEvent)(await service.HandleAsync(new InspectPruneRequest(service.SessionId, catalog.CatalogId, 1)))!;
        await service.HandleAsync(new ConfirmPruneRequest(service.SessionId, plan.PrunePlanId, plan.PruneDigest));
        using CancellationTokenSource cancellation = new();
        Task<ProtocolEvent> pruning = service.HandleAsync(new ExecutePruneRequest(service.SessionId, plan.PrunePlanId, plan.PruneDigest), outer ? cancellation.Token : default);
        await engine.PruneStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (outer) cancellation.Cancel();
        else (await service.HandleAsync(new CancelPruneRequest(service.SessionId, plan.PrunePlanId, plan.PruneDigest))).Should().BeOfType<CommandAcknowledgedEvent>();

        (await pruning).Should().BeOfType<PruneSuccessEvent>();
        service.State.Should().Be(ProtocolSessionState.Completed);
    }

    [TestCase("before", 0)]
    [TestCase("after-progress", 1)]
    [TestCase("during-cancel", 1)]
    public async Task UnexpectedPruneFaultAlwaysReturnsOneSanitizedConservativeTerminal(string boundary, int expectedProgress)
    {
        List<ProtocolEvent> emitted = [];
        FakeEngine engine = new()
        {
            ThrowPruneBeforeProgress = boundary == "before",
            ThrowPruneAfterProgress = boundary == "after-progress",
            BlockPruneUntilCancellation = boundary == "during-cancel",
            ThrowPruneAfterCancellation = boundary == "during-cancel"
        };
        using LinuxInstallerProtocolService service = Create(engine, new FakePackageOpener(), emitted.Add);
        await Handshake(service); RecoveryCatalogEvent catalog = (RecoveryCatalogEvent)(await service.HandleAsync(new ListRecoveriesRequest(service.SessionId, "/game")))!;
        PrunePlanEvent plan = (PrunePlanEvent)(await service.HandleAsync(new InspectPruneRequest(service.SessionId, catalog.CatalogId, 1)))!;
        await service.HandleAsync(new ConfirmPruneRequest(service.SessionId, plan.PrunePlanId, plan.PruneDigest));
        Task<ProtocolEvent> pruning = service.HandleAsync(new ExecutePruneRequest(service.SessionId, plan.PrunePlanId, plan.PruneDigest));
        if (boundary == "during-cancel")
        {
            await engine.PruneStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await service.HandleAsync(new CancelPruneRequest(service.SessionId, plan.PrunePlanId, plan.PruneDigest));
        }

        PruneInterruptionEvent terminal = (PruneInterruptionEvent)(await pruning)!;
        terminal.Outcome.Should().Be(ProtocolPruneOutcome.UnexpectedCoreFailure); terminal.TerminalState.Should().Be(new ProtocolTerminalState(ProtocolDurableState.Unknown, ProtocolTerminalErrorCode.UnexpectedCoreFailure, ProtocolRecoveryDisposition.StateRefreshRequired, ProtocolNextAction.ListRecoveries)); terminal.Message.Should().NotContain("private-prune-secret");
        emitted.OfType<PruneProgressEvent>().Should().HaveCount(expectedProgress);
        service.State.Should().Be(ProtocolSessionState.Completed);
    }

    [Test]
    public async Task AuxiliaryPruneCleanupPendingIsPresentedWithoutInventingGenerationWork()
    {
        FakeEngine engine = new() { NextPruneStatus = RecoveryPruneOutcomeStatus.FailedWithCleanupPending, NextPruneAuxiliaryCleanupPending = true, OmitPendingPruneCleanup = true };
        using LinuxInstallerProtocolService service = Create(engine, new FakePackageOpener());
        await Handshake(service); RecoveryCatalogEvent catalog = (RecoveryCatalogEvent)(await service.HandleAsync(new ListRecoveriesRequest(service.SessionId, "/game")))!;
        PrunePlanEvent plan = (PrunePlanEvent)(await service.HandleAsync(new InspectPruneRequest(service.SessionId, catalog.CatalogId, 1)))!;
        await service.HandleAsync(new ConfirmPruneRequest(service.SessionId, plan.PrunePlanId, plan.PruneDigest));
        PruneFailureEvent terminal = (PruneFailureEvent)(await service.HandleAsync(new ExecutePruneRequest(service.SessionId, plan.PrunePlanId, plan.PruneDigest)))!;
        terminal.Message.Should().Contain("auxiliary recovery metadata cleanup"); terminal.PruneSummary.PhysicallyCleanedGenerationCount.Should().Be(0); terminal.TerminalState.RecoveryDisposition.Should().Be(ProtocolRecoveryDisposition.CleanupPending);
    }

    [Test]
    public async Task RegistrationFailureDisposesUnacceptedOwnerExactlyOnce()
    {
        FakePackageOpener opener = new() { ReturnMismatchedRelease = true };
        using LinuxInstallerProtocolService service = Create(new FakeEngine(), opener);
        await Handshake(service);
        Func<Task> open = async () => await Open(service);
        await open.Should().ThrowAsync<ProtocolException>().WithMessage("*doesn't match*");
        opener.Owners.Single().DisposeCount.Should().Be(1);
        service.Dispose(); opener.Owners.Single().DisposeCount.Should().Be(1);
    }

    [Test]
    public async Task AsyncDisposalWaitsForInFlightPackageRegistrationThenDisposesItsOwner()
    {
        FakePackageOpener opener = new() { BlockOpen = true };
        LinuxInstallerProtocolService service = Create(new FakeEngine(), opener);
        await Handshake(service);
        Task<PackageOpenedEvent> opening = Open(service);
        await opener.OpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task disposal = service.DisposeAsync().AsTask(); disposal.IsCompleted.Should().BeFalse(); opener.Owners.Single().DisposeCount.Should().Be(0);
        opener.ContinueOpen.TrySetResult(); await opening; await disposal;
        opener.Owners.Single().DisposeCount.Should().Be(1); service.Dispose(); opener.Owners.Single().DisposeCount.Should().Be(1);
    }

    [Test]
    public async Task SyncFirstMixedAndTwoSyncDisposersShareOnePublishedCompletion()
    {
        TaskCompletionSource disposalPublished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakePackageOpener opener = new() { BlockOpen = true };
        LinuxInstallerProtocolService service = Create(new FakeEngine(), opener, disposalPublished: disposalPublished.SetResult);
        await Handshake(service);
        Task<PackageOpenedEvent> opening = Open(service);
        await opener.OpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task firstSync = Task.Run(service.Dispose);
        await disposalPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task asyncFollower = service.DisposeAsync().AsTask(); Task secondSync = Task.Run(service.Dispose);
        firstSync.IsCompleted.Should().BeFalse(); asyncFollower.IsCompleted.Should().BeFalse(); secondSync.IsCompleted.Should().BeFalse();
        opener.Owners.Single().DisposeCount.Should().Be(0);
        opener.ContinueOpen.TrySetResult(); await opening;
        await Task.WhenAll(firstSync, asyncFollower, secondSync);
        opener.Owners.Single().DisposeCount.Should().Be(1);
    }

    [Test]
    public async Task ConcurrentAsyncAndMixedDisposalSharesOneSafeCompletionAndDisposesOnce()
    {
        FakeEngine engine = new() { BlockExecutionUntilCancellation = true }; FakePackageOpener opener = new();
        LinuxInstallerProtocolService service = Create(engine, opener);
        await Handshake(service); PackageOpenedEvent package = await Open(service);
        PlanEvent plan = (PlanEvent)(await service.HandleAsync(new InspectPlanRequest(service.SessionId, "/game", InstallerOperation.Repair, package.PackageId, null)))!;
        await service.HandleAsync(new ConfirmPlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest));
        Task<ProtocolEvent> execution = service.HandleAsync(new ExecutePlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest));
        await engine.ExecutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task first = service.DisposeAsync().AsTask(); Task second = service.DisposeAsync().AsTask(); service.Dispose();
        await Task.WhenAll(first, second); (await execution).Should().BeOfType<CancelledEvent>();
        opener.Owners.Single().DisposeCount.Should().Be(1);
        service.Dispose(); await service.DisposeAsync(); opener.Owners.Single().DisposeCount.Should().Be(1);
    }

    [Test]
    public async Task DisposalDuringExecutionInitiationWaitsForPublishedOperationBeforeDisposingAuthorities()
    {
        FakeEngine engine = new() { BlockExecutionInitiation = true, BlockExecutionUntilCancellation = true }; FakePackageOpener opener = new();
        LinuxInstallerProtocolService service = Create(engine, opener);
        await Handshake(service); PackageOpenedEvent package = await Open(service);
        PlanEvent plan = (PlanEvent)(await service.HandleAsync(new InspectPlanRequest(service.SessionId, "/game", InstallerOperation.Repair, package.PackageId, null)))!;
        await service.HandleAsync(new ConfirmPlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest));
        Task<ProtocolEvent> execution = Task.Run(async () => await service.HandleAsync(new ExecutePlanRequest(service.SessionId, plan.PlanId, plan.PlanDigest)));
        await engine.ExecutionInitiationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task disposal = service.DisposeAsync().AsTask(); disposal.IsCompleted.Should().BeFalse(); opener.Owners.Single().DisposeCount.Should().Be(0);
        engine.ReleaseExecutionInitiation.TrySetResult();
        (await execution).Should().BeOfType<CancelledEvent>(); await disposal;
        opener.Owners.Single().DisposeCount.Should().Be(1);
    }

    [Test]
    public async Task DisposalDuringPruneInitiationWaitsForPublishedOperationBeforeDisposingRecoveries()
    {
        FakeEngine engine = new() { BlockPruneInitiation = true, BlockPruneUntilCancellation = true, NextPruneStatus = RecoveryPruneOutcomeStatus.CancelledBeforePublication };
        LinuxInstallerProtocolService service = Create(engine, new FakePackageOpener());
        await Handshake(service); RecoveryCatalogEvent catalog = (RecoveryCatalogEvent)(await service.HandleAsync(new ListRecoveriesRequest(service.SessionId, "/game")))!;
        PrunePlanEvent plan = (PrunePlanEvent)(await service.HandleAsync(new InspectPruneRequest(service.SessionId, catalog.CatalogId, 1)))!;
        await service.HandleAsync(new ConfirmPruneRequest(service.SessionId, plan.PrunePlanId, plan.PruneDigest));
        Task<ProtocolEvent> pruning = Task.Run(async () => await service.HandleAsync(new ExecutePruneRequest(service.SessionId, plan.PrunePlanId, plan.PruneDigest)));
        await engine.PruneInitiationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task disposal = service.DisposeAsync().AsTask(); disposal.IsCompleted.Should().BeFalse(); engine.OpenedRecoveries.Should().OnlyContain(handle => handle.DisposeCount == 0);
        engine.ReleasePruneInitiation.TrySetResult();
        (await pruning).Should().BeOfType<PruneCancelledEvent>(); await disposal;
        engine.OpenedRecoveries.Should().OnlyContain(handle => handle.DisposeCount == 1);
    }

    [Test]
    public async Task DisposalDuringRecoveryInitiationWaitsForPublishedOperationTerminal()
    {
        FakeEngine engine = new() { BlockRecoveryInitiation = true, BlockRecoveryUntilCancellation = true };
        LinuxInstallerProtocolService service = Create(engine, new FakePackageOpener());
        await Handshake(service);
        Task<ProtocolEvent> recovery = Task.Run(async () => await service.HandleAsync(new RecoverInterruptedRequest(service.SessionId, "/game")));
        await engine.RecoveryInitiationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task disposal = service.DisposeAsync().AsTask(); disposal.IsCompleted.Should().BeFalse();
        engine.ReleaseRecoveryInitiation.TrySetResult();
        (await recovery).Should().BeOfType<RecoveryFailureEvent>(); await disposal;
    }

    [Test]
    public async Task CandidateApprovalUsesCurrentOpaqueIdsAndReturnsPartialReplacementPlan()
    {
        FakeEngine engine = new() { CandidateApprovalMode = true }; FakePackageOpener opener = new();
        using LinuxInstallerProtocolService service = Create(engine, opener);
        await Handshake(service); PackageOpenedEvent package = await Open(service);
        PlanEvent first = (PlanEvent)(await service.HandleAsync(new InspectPlanRequest(service.SessionId, "/game", InstallerOperation.Repair, package.PackageId, null)))!;
        first.Candidates.Should().HaveCount(2);
        PlanEvent partial = (PlanEvent)(await service.HandleAsync(new SelectPlanCandidatesRequest(service.SessionId, first.PlanId, first.PlanDigest, [first.Candidates[0].CandidateId])))!;
        partial.Candidates.Should().ContainSingle(); engine.ApprovalCalls.Should().Be(1);
        Func<Task> stale = async () => await service.HandleAsync(new SelectPlanCandidatesRequest(service.SessionId, first.PlanId, first.PlanDigest, [first.Candidates[1].CandidateId]));
        await stale.Should().ThrowAsync<ProtocolException>().WithMessage("*stale*");
    }

    private static LinuxInstallerProtocolService Create(FakeEngine engine, FakePackageOpener opener, Action<ProtocolEvent>? sink = null, Action? terminalCompletionStarting = null, Action? disposalPublished = null, string? sanitizedLogPath = null) =>
        new("test", progress => { engine.Progress = progress; return engine; }, new FakeDiscovery(), opener, sink, sanitizedLogPath, terminalCompletionStarting, disposalPublished);
    private static Task<ProtocolEvent> Handshake(LinuxInstallerProtocolService service) => service.HandleAsync(new HandshakeRequest("gui", "1"));
    private static async Task<PackageOpenedEvent> Open(LinuxInstallerProtocolService service) => (PackageOpenedEvent)(await service.HandleAsync(new OpenPackageRequest(service.SessionId, CreateRelease().Tag, CreateRelease().SourceCommit, "/tmp/package", "/tmp/checksums", "/tmp/build", "/tmp/manifest")))!;
    private static string CreateTemporaryDirectory() { string path = Path.Combine(Path.GetTempPath(), $"smapi-protocol-opener-{Guid.NewGuid():N}"); Directory.CreateDirectory(path); return path; }
    private static OpenPackageRequest CreateActualPackageRequest(string root)
    {
        InstallationReleaseIdentity release = CreateRelease(); ForkReleaseIdentity identity = ForkReleaseIdentity.Parse(release.Tag);
        return new(ProtocolSessionId.CreateRandom(), release.Tag, release.SourceCommit, Path.Combine(root, identity.PackageAssetName), Path.Combine(root, ReleasePackageVerifier.ChecksumAssetName), Path.Combine(root, ReleasePackageVerifier.BuildMetadataAssetName), Path.Combine(root, VerifiedInstallerPackageFactory.GetManifestAssetName(identity)));
    }

    private static InstallationExecutionOutcome CreateOutcome(InstallationExecutionStatus status, TransactionErrorCode? errorOverride = null)
    {
        bool committed = status is InstallationExecutionStatus.Succeeded or InstallationExecutionStatus.SucceededWithCleanupWarning;
        bool rolledBack = status is InstallationExecutionStatus.FailedAndRolledBack or InstallationExecutionStatus.CancelledAndRolledBack;
        TransactionPathChange[] changed = committed || rolledBack ? [new("managed-a", TransactionOperationKind.WriteFile), new(".smapi-installer/internal", TransactionOperationKind.WriteFile)] : [];
        TransactionOutcomeStatus transactionStatus = status switch
        {
            InstallationExecutionStatus.Succeeded => TransactionOutcomeStatus.Committed,
            InstallationExecutionStatus.SucceededWithCleanupWarning => TransactionOutcomeStatus.CommittedWithCleanupWarning,
            InstallationExecutionStatus.FailedAndRolledBack => TransactionOutcomeStatus.FailedAndRolledBack,
            InstallationExecutionStatus.InterruptedRecoveryRequired => TransactionOutcomeStatus.InterruptedRecoveryRequired,
            _ => TransactionOutcomeStatus.FailedBeforeMutation
        };
        TransactionErrorCode? error = errorOverride ?? (status switch
        {
            InstallationExecutionStatus.Succeeded or InstallationExecutionStatus.CancelledBeforeMutation or InstallationExecutionStatus.CancelledAndRolledBack => null,
            InstallationExecutionStatus.AutomaticRecoveryCompletedFreshInspectionRequired => TransactionErrorCode.PathChanged,
            _ => TransactionErrorCode.IoFailure
        });
        TransactionExecutionOutcome? transaction = status == InstallationExecutionStatus.AutomaticRecoveryCompletedFreshInspectionRequired ? null : new(Guid.NewGuid(), transactionStatus, committed ? TransactionStatus.Committed : rolledBack ? TransactionStatus.RolledBack : null, changed, rolledBack ? changed : [], TransactionCancellationDisposition.None, error, status.ToString());
        return new(InstallationAction.Uninstall, status, transaction, status == InstallationExecutionStatus.AutomaticRecoveryCompletedFreshInspectionRequired ? [new(Guid.NewGuid(), TransactionStatus.Recovered, 1)] : [], error, status.ToString());
    }

    private static ProtocolTerminalState GetTerminalState(ProtocolEvent terminal) => terminal switch
    {
        SuccessEvent value => value.TerminalState,
        RolledBackFailureEvent value => value.TerminalState,
        RecoverableInterruptionEvent value => value.TerminalState,
        CancelledEvent value => value.TerminalState,
        PruneFailureEvent value => value.TerminalState,
        PruneInterruptionEvent value => value.TerminalState,
        PruneCancelledEvent value => value.TerminalState,
        PruneSuccessEvent value => value.TerminalState,
        _ => throw new AssertionException("Unexpected terminal event type.")
    };
    private static ProtocolExecutionOutcome GetExecutionOutcome(ProtocolEvent terminal) => terminal switch { SuccessEvent value => value.Outcome, RolledBackFailureEvent value => value.Outcome, RecoverableInterruptionEvent value => value.Outcome, CancelledEvent value => value.Outcome, _ => throw new AssertionException("Unexpected execution terminal.") };
    private static ProtocolPruneOutcome GetPruneOutcome(ProtocolEvent terminal) => terminal switch { PruneSuccessEvent value => value.Outcome, PruneFailureEvent value => value.Outcome, PruneInterruptionEvent value => value.Outcome, PruneCancelledEvent value => value.Outcome, _ => throw new AssertionException("Unexpected prune terminal.") };
    private static IEnumerable<TransactionErrorCode> TransactionErrors => Enum.GetValues<TransactionErrorCode>();

    private static InspectedInstallationState Inspection(
        InstallationAction action,
        IVerifiedPackageContentAuthority? package,
        ICommittedRecoveryContentAuthority? recovery,
        IEnumerable<ModifiedFileReplacementCandidate>? candidates = null,
        IEnumerable<ModifiedFileReplacementApproval>? approvals = null,
        IEnumerable<PlanConflict>? conflicts = null,
        object? candidateAuthority = null
    )
    {
        PlannedOperation operation = action switch
        {
            InstallationAction.Uninstall => new(PlanOperationKind.Remove, NormalizedRelativePath.Parse("StardewModdingAPI.dll"), Sha256Digest.Parse(HashA), null),
            InstallationAction.Backup => new(PlanOperationKind.Backup, NormalizedRelativePath.Parse("StardewModdingAPI.dll"), Sha256Digest.Parse(HashA), Sha256Digest.Parse(HashA)),
            InstallationAction.Rollback => new(PlanOperationKind.Restore, NormalizedRelativePath.Parse("StardewModdingAPI.dll"), Sha256Digest.Parse(HashA), Sha256Digest.Parse(HashB)),
            _ => new(PlanOperationKind.Create, NormalizedRelativePath.Parse("StardewModdingAPI.dll"), null, Sha256Digest.Parse(HashB))
        };
        InstallationPlan plan = new(action, [operation], conflicts ?? [], ObservedInstallationState.KnownUnmodified, new RecoveryCapacityState(2, 64));
        BoundInstallationPlan binding = new(action, Root, 7, plan.GetCanonicalDigest(), package?.ManifestSha256, null, null, recovery?.SnapshotSha256, null, recovery?.GenerationId, recovery?.AuthorizedHeadPointerSha256, package, recovery);
        InstallationReleaseIdentity? current = action == InstallationAction.Install ? null : CreateRelease();
        InstallationReleaseIdentity? target = action switch { InstallationAction.Install or InstallationAction.Update or InstallationAction.Repair => CreateRelease(), InstallationAction.Rollback => recovery?.RestoreRelease, _ => null };
        return new(plan, binding, package, recovery, candidateAuthority ?? new object(), current, target, ObservedInstallationState.KnownUnmodified, new RecoveryCapacityState(2, 64), candidates, approvals);
    }

    private static InstallationReleaseIdentity CreateRelease() => new("https://github.com/4eh5xitv6787h645ebv/SMAPI", "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2", "4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2", "SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2-linux-x64-installer.zip", "1111111111111111111111111111111111111111", "2222222222222222222222222222222222222222", Sha256Digest.Parse(HashA), 123, "4eh5xitv6787h645ebv/SMAPI/.github/workflows/linux-alpha-release.yml@refs/tags/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2", "Release", "linux-x64");

    private sealed class FakeDiscovery : ILinuxInstallerProtocolDiscovery
    {
        public Task<IReadOnlyList<LinuxGameFolderCandidate>> DiscoverAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<LinuxGameFolderCandidate>>([new("/game", LinuxGameFolderStatus.UnsafeLauncher)]);
    }

    private sealed class FakePackageOpener : ILinuxInstallerProtocolPackageOpener
    {
        public List<FakePackageAuthority> Authorities { get; } = [];
        public List<FakeOwner> Owners { get; } = [];
        public bool ReturnMismatchedRelease { get; set; }
        public bool ThrowUnexpected { get; set; }
        public bool BlockOpen { get; set; }
        public TaskCompletionSource OpenStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueOpen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ProtocolPackageRegistration> OpenAsync(OpenPackageRequest request, CancellationToken cancellationToken)
        {
            if (this.ThrowUnexpected) throw new InvalidOperationException("private-package-opener-detail");
            FakePackageAuthority authority = new(); FakeOwner owner = new(); this.Authorities.Add(authority); this.Owners.Add(owner);
            this.OpenStarted.TrySetResult(); if (this.BlockOpen) await this.ContinueOpen.Task.WaitAsync(cancellationToken);
            InstallationReleaseIdentity release = this.ReturnMismatchedRelease
                ? new("https://github.com/4eh5xitv6787h645ebv/SMAPI", "fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.3", "4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.3", "SMAPI-4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.3-linux-x64-installer.zip", "3333333333333333333333333333333333333333", "4444444444444444444444444444444444444444", Sha256Digest.Parse(HashB), 124, "4eh5xitv6787h645ebv/SMAPI/.github/workflows/linux-alpha-release.yml@refs/tags/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.3", "Release", "linux-x64")
                : CreateRelease();
            return new ProtocolPackageRegistration(release, authority, owner);
        }
    }

    private sealed class FakeEngine : ILinuxInstallerProtocolEngine
    {
        public ITransactionProgressSink Progress { get; set; } = null!;
        public int InspectCalls { get; private set; }
        public List<(InstallationAction Action, IVerifiedPackageContentAuthority? Package, ICommittedRecoveryContentAuthority? Recovery)> Observed { get; } = [];
        public List<FakeRecoveryAuthority> OpenedRecoveries { get; } = [];
        public int ExecuteChangedCount { get; set; } = 1;
        public InstallationExecutionOutcome? NextExecutionOutcome { get; set; }
        public bool BlockExecutionUntilCancellation { get; set; }
        public bool CommitExecutionAfterCancellation { get; set; }
        public bool ThrowExecutionBeforeProgress { get; set; }
        public bool ThrowExecutionAfterProgress { get; set; }
        public bool ThrowExecutionAfterCancellation { get; set; }
        public bool ReportConcurrentProgress { get; set; }
        public bool ReportDelayedProgress { get; set; }
        public bool ReportUnawaitedProgress { get; set; }
        public InstallationExecutionStatus CancellationOutcomeStatus { get; set; } = InstallationExecutionStatus.CancelledBeforeMutation;
        public bool CandidateApprovalMode { get; set; }
        public int ApprovalCalls { get; private set; }
        public RecoveryPruneOutcomeStatus NextPruneStatus { get; set; } = RecoveryPruneOutcomeStatus.Succeeded;
        public bool NextPruneAuxiliaryCleanupPending { get; set; }
        public bool CleanupOnlyPrune { get; set; }
        public bool BlockPruneUntilCancellation { get; set; }
        public bool ThrowPruneBeforeProgress { get; set; }
        public bool ThrowPruneAfterProgress { get; set; }
        public bool ThrowPruneAfterCancellation { get; set; }
        public bool BlockPruneInitiation { get; set; }
        public TaskCompletionSource ExecutionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PruneStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDelayedProgress { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DelayedProgressReported { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReturnWhileProgressIsBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ThrowRecovery { get; set; }
        public bool ThrowUnrequestedRecoveryCancellation { get; set; }
        public bool BlockUnrequestedRecoveryCancellation { get; set; }
        public bool ThrowPartialRecovery { get; set; }
        public bool RecoveryNamedRootStillSelected { get; set; } = true;
        public bool BlockRecoveryUntilCancellation { get; set; }
        public bool BlockRecoveryInitiation { get; set; }
        public bool BlockExecutionInitiation { get; set; }
        public bool OmitPendingPruneCleanup { get; set; }
        public bool SuppressPruneWork { get; set; }
        public TaskCompletionSource RecoveryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ExecutionInitiationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PruneInitiationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RecoveryInitiationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource UnrequestedRecoveryCancellationReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseExecutionInitiation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleasePruneInitiation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRecoveryInitiation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseUnrequestedRecoveryCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Guid First = Guid.ParseExact("11111111111111111111111111111111", "N");
        private readonly Guid Second = Guid.ParseExact("22222222222222222222222222222222", "N");
        private readonly Guid Pending = Guid.ParseExact("33333333333333333333333333333333", "N");
        private readonly InstallationReleaseIdentity RecoveryRelease = CreateRelease();
        private readonly object CandidateAuthority = new();

        public Task<InspectedInstallationState> InspectAsync(string gameRoot, InstallationAction action, IVerifiedPackageContentAuthority? package, ICommittedRecoveryContentAuthority? recovery, CancellationToken cancellationToken)
        {
            this.InspectCalls++; this.Observed.Add((action, package, recovery));
            if (!this.CandidateApprovalMode) return Task.FromResult(Inspection(action, package, recovery));
            ModifiedFileReplacementCandidate[] candidates = CreateCandidates(this.CandidateAuthority);
            return Task.FromResult(Inspection(action, package, recovery, candidates, conflicts: candidates.Select(candidate => new PlanConflict(PlanConflictCode.ModifiedOwnedFile, candidate.Path)), candidateAuthority: this.CandidateAuthority));
        }
        public async Task<InterruptedOperationRecoveryResult> RecoverInterruptedOperationAsync(string gameRoot, CancellationToken cancellationToken)
        {
            if (this.BlockRecoveryInitiation) { this.RecoveryInitiationStarted.TrySetResult(); this.ReleaseRecoveryInitiation.Task.GetAwaiter().GetResult(); }
            if (this.ThrowUnrequestedRecoveryCancellation)
            {
                this.UnrequestedRecoveryCancellationReady.TrySetResult();
                if (this.BlockUnrequestedRecoveryCancellation) await this.ReleaseUnrequestedRecoveryCancellation.Task;
                throw new OperationCanceledException("core-origin cancellation", null, CancellationToken.None);
            }
            if (this.ThrowPartialRecovery) throw new InterruptedOperationRecoveryException(Root, 7, null, null, [new(Guid.NewGuid(), TransactionStatus.Recovered, 2)], TransactionErrorCode.RecoveryFailed, "Partial recovery failed safely.", new IOException("private-partial-failure"));
            if (this.ThrowRecovery) throw new InvalidOperationException("private-secret");
            this.Progress.Report(new(TransactionStage.AcquiringLock, 0, null)); this.RecoveryStarted.TrySetResult();
            if (this.BlockRecoveryUntilCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            this.Progress.Report(new(TransactionStage.Recovering, 0, null));
            InterruptedOperationRecoveryResult result = new(Root, 7, 8, [new(Guid.NewGuid(), TransactionStatus.Recovered, 2)], this.RecoveryNamedRootStillSelected);
            this.Progress.Report(new(TransactionStage.Completed, 1, 1));
            return result;
        }
        public Task<InspectedInstallationState> ApproveFileReplacementsAsync(InspectedInstallationState source, IEnumerable<ModifiedFileReplacementCandidate> selected, CancellationToken cancellationToken)
        {
            this.ApprovalCalls++; ModifiedFileReplacementCandidate[] chosen = selected.ToArray();
            ModifiedFileReplacementApproval[] approvals = source.ModifiedFileReplacementApprovals.Concat(chosen.Select(candidate => new ModifiedFileReplacementApproval(candidate.Path, candidate.ObservedIdentity))).ToArray();
            object nextAuthority = new(); ModifiedFileReplacementCandidate[] remaining = source.ModifiedFileReplacementCandidates.Where(candidate => !chosen.Contains(candidate)).Select(candidate => new ModifiedFileReplacementCandidate(nextAuthority, candidate.Path, candidate.ObservedIdentity, candidate.Reason, candidate.Disposition, candidate.ProposedResultSha256)).ToArray();
            return Task.FromResult(Inspection(source.Action, source.TargetPackageContent, source.RollbackContent, remaining, approvals, remaining.Select(candidate => new PlanConflict(PlanConflictCode.ModifiedOwnedFile, candidate.Path)), nextAuthority));
        }
        public async Task<InstallationExecutionOutcome> ExecuteAsync(InspectedInstallationState inspection, Sha256Digest confirmedDigest, string? sanitizedLogPath, CancellationToken cancellationToken)
        {
            if (this.BlockExecutionInitiation) { this.ExecutionInitiationStarted.TrySetResult(); this.ReleaseExecutionInitiation.Task.GetAwaiter().GetResult(); }
            if (this.ThrowExecutionBeforeProgress) throw new InvalidOperationException("private-secret-before");
            this.Progress.Report(new(TransactionStage.Revalidating, 0, null)); this.ExecutionStarted.TrySetResult();
            if (this.ThrowExecutionAfterProgress) throw new InvalidOperationException("private-secret-after-progress");
            if (this.BlockExecutionUntilCancellation)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    if (this.ThrowExecutionAfterCancellation) throw new InvalidOperationException("private-secret-after-cancel");
                    if (this.CommitExecutionAfterCancellation) return CreateOutcome(InstallationExecutionStatus.Succeeded);
                    if (this.CancellationOutcomeStatus == InstallationExecutionStatus.CancelledAndRolledBack)
                    {
                        TransactionPathChange[] changed = [new("managed-a", TransactionOperationKind.WriteFile)];
                        return new(inspection.Action, this.CancellationOutcomeStatus, new(Guid.NewGuid(), TransactionOutcomeStatus.CancelledAndRolledBack, TransactionStatus.RolledBack, changed, changed, TransactionCancellationDisposition.ObservedAfterMutationAndRolledBack, null, "Cancelled."), [], null, "Cancelled.");
                    }
                    return new(inspection.Action, this.CancellationOutcomeStatus, new(Guid.NewGuid(), TransactionOutcomeStatus.CancelledBeforeMutation, null, [], [], TransactionCancellationDisposition.ObservedBeforeMutation, null, "Cancelled."), [], null, "Cancelled.");
                }
            }
            if (this.NextExecutionOutcome is not null) return this.NextExecutionOutcome;
            if (this.ReportUnawaitedProgress)
            {
                _ = Task.Run(() => this.Progress.Report(new(TransactionStage.Applying, 1, 1)));
                await this.ReturnWhileProgressIsBlocked.Task;
                return CreateOutcome(InstallationExecutionStatus.Succeeded);
            }
            if (this.ReportConcurrentProgress)
                await Task.WhenAll(Task.Run(() => this.Progress.Report(new(TransactionStage.Applying, 1, 2))), Task.Run(() => this.Progress.Report(new(TransactionStage.UpdatingInstallerState, 2, 2))));
            this.Progress.Report(new(TransactionStage.Applying, 1, this.ExecuteChangedCount)); this.Progress.Report(new(TransactionStage.Completed, this.ExecuteChangedCount, this.ExecuteChangedCount));
            if (this.ReportDelayedProgress)
                _ = Task.Run(async () => { await this.ReleaseDelayedProgress.Task; this.Progress.Report(new(TransactionStage.Completed, 1, 1)); this.DelayedProgressReported.TrySetResult(); });
            TransactionPathChange[] changes = Enumerable.Range(0, this.ExecuteChangedCount).Select(index => new TransactionPathChange($"managed-{index}", TransactionOperationKind.WriteFile)).ToArray();
            return new(inspection.Action, InstallationExecutionStatus.Succeeded, new(Guid.NewGuid(), TransactionOutcomeStatus.Committed, TransactionStatus.Committed, changes, [], TransactionCancellationDisposition.None, null, "Committed."), [], null, "Committed.");
        }
        public Task<RecoveryHistory> ListRecoveriesAsync(string gameRoot, CancellationToken cancellationToken) => Task.FromResult(new RecoveryHistory(Sha256Digest.Parse(HashA), [new(this.First, InstallationAction.Backup, true, true, this.RecoveryRelease), new(this.Second, InstallationAction.Update, false, false, this.RecoveryRelease)]));
        public Task<ICommittedRecoveryContentAuthority> OpenRecoveryAsync(string gameRoot, Guid generationId, CancellationToken cancellationToken) { FakeRecoveryAuthority result = new(generationId, generationId == this.First ? InstallationAction.Backup : InstallationAction.Update, this.RecoveryRelease); this.OpenedRecoveries.Add(result); return Task.FromResult<ICommittedRecoveryContentAuthority>(result); }
        public Task<RecoveryPrunePlan> InspectRecoveryPruneAsync(string gameRoot, int retainNewest, CancellationToken cancellationToken) => Task.FromResult(this.CleanupOnlyPrune
            ? new RecoveryPrunePlan(Root, 7, Sha256Digest.Parse(HashA), retainNewest, [this.First, this.Second], [this.First, this.Second], [], [this.Pending], [], null)
            : new RecoveryPrunePlan(Root, 7, Sha256Digest.Parse(HashA), retainNewest, [this.First, this.Second], [this.First], [this.Second], [this.Second], [], null));
        public async Task<RecoveryPruneOutcome> ExecuteRecoveryPruneAsync(RecoveryPrunePlan plan, Sha256Digest confirmedDigest, CancellationToken cancellationToken)
        {
            if (this.BlockPruneInitiation) { this.PruneInitiationStarted.TrySetResult(); this.ReleasePruneInitiation.Task.GetAwaiter().GetResult(); }
            if (this.ThrowPruneBeforeProgress) throw new InvalidOperationException("private-prune-secret-before");
            this.Progress.Report(new(TransactionStage.VerifyingRecovery, 1, 1)); this.PruneStarted.TrySetResult();
            if (this.ThrowPruneAfterProgress) throw new InvalidOperationException("private-prune-secret-after-progress");
            if (this.BlockPruneUntilCancellation)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    if (this.ThrowPruneAfterCancellation) throw new InvalidOperationException("private-prune-secret-after-cancel");
                }
            }
            bool pending = this.NextPruneStatus is RecoveryPruneOutcomeStatus.Interrupted or RecoveryPruneOutcomeStatus.CancelledWithCleanupPending or RecoveryPruneOutcomeStatus.FailedWithCleanupPending
                || (this.CleanupOnlyPrune && (this.NextPruneStatus == RecoveryPruneOutcomeStatus.FailedBeforePublication || this.NextPruneStatus == RecoveryPruneOutcomeStatus.CancelledBeforePublication));
            IReadOnlyList<Guid> logical = this.SuppressPruneWork || this.NextPruneStatus is RecoveryPruneOutcomeStatus.FailedBeforePublication or RecoveryPruneOutcomeStatus.CancelledBeforePublication ? [] : plan.RemovedGenerationIds;
            IReadOnlyList<Guid> physical = this.NextPruneStatus == RecoveryPruneOutcomeStatus.Succeeded ? plan.CleanupGenerationIds : [];
            IReadOnlyList<Guid> pendingIds = pending && !this.OmitPendingPruneCleanup ? plan.CleanupGenerationIds : [];
            TransactionErrorCode? error = this.NextPruneStatus is RecoveryPruneOutcomeStatus.Succeeded or RecoveryPruneOutcomeStatus.CancelledBeforePublication or RecoveryPruneOutcomeStatus.CancelledWithCleanupPending ? null : TransactionErrorCode.IoFailure;
            return new(this.NextPruneStatus, logical, physical, pendingIds, this.NextPruneAuxiliaryCleanupPending, error, this.NextPruneStatus.ToString());
        }

        private static ModifiedFileReplacementCandidate[] CreateCandidates(object authority) =>
        [
            new(authority, NormalizedRelativePath.Parse("StardewModdingAPI.dll"), new RecoveryFileIdentity(Sha256Digest.Parse(HashA), 10, 420, RecoveryFileType.RegularFile), FileReplacementCandidateReason.ModifiedReceiptOwned, FileReplacementCandidateDisposition.Replace, Sha256Digest.Parse(HashB)),
            new(authority, NormalizedRelativePath.Parse("smapi-internal/a.dll"), new RecoveryFileIdentity(Sha256Digest.Parse(HashB), 20, 420, RecoveryFileType.RegularFile), FileReplacementCandidateReason.ModifiedReceiptOwned, FileReplacementCandidateDisposition.Replace, Sha256Digest.Parse(HashA))
        ];
    }

    private sealed class FakePackageAuthority : IVerifiedPackageContentAuthority
    {
        public PackageManifest Manifest { get; } = new(CreateRelease(), [new(NormalizedRelativePath.Parse("StardewValley"), Sha256Digest.Parse(HashA), 10, 493, OwnedEntryKind.Launcher), new(NormalizedRelativePath.Parse("StardewModdingAPI.dll"), Sha256Digest.Parse(HashB), 10, 420, OwnedEntryKind.RuntimeFile)]);
        public Sha256Digest ManifestSha256 => this.Manifest.GetCanonicalDigest();
        public LinuxAnchoredFile OpenFile(PackageManifestEntry expected, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void AssertUsable() { }
    }

    private sealed class FakeRecoveryAuthority(Guid generationId, InstallationAction action, InstallationReleaseIdentity restoreRelease) : ICommittedRecoveryContentAuthority, IDisposable
    {
        public Guid GenerationId { get; } = generationId;
        public InstallationAction OriginAction { get; } = action;
        public GameRootIdentity GameRoot => Root;
        public RollbackSnapshot Snapshot { get; } = new(null, Sha256Digest.Parse(HashA), []);
        public Sha256Digest SnapshotSha256 => Sha256Digest.Parse(HashB);
        public Sha256Digest? PreviousManifestSha256 => null;
        public Sha256Digest? PreviousReceiptSha256 => Sha256Digest.Parse(HashA);
        public Sha256Digest AuthorizedHeadPointerSha256 => Sha256Digest.Parse(HashA);
        public InstallationReleaseIdentity? RestoreRelease { get; } = restoreRelease;
        public int DisposeCount { get; private set; }
        public LinuxAnchoredFile OpenGameFile(NormalizedRelativePath path, RecoveryFileIdentity expectedIdentity) => throw new NotSupportedException();
        public LinuxAnchoredFile OpenPreviousReceipt(Sha256Digest expectedSha256) => throw new NotSupportedException();
        public LinuxAnchoredFile OpenPreviousManifest(Sha256Digest expectedSha256) => throw new NotSupportedException();
        public void AssertUsable() { if (this.DisposeCount > 0) throw new ObjectDisposedException(nameof(FakeRecoveryAuthority)); }
        public void Dispose() => this.DisposeCount++;
    }

    private sealed class FakeOwner : IDisposable { public int DisposeCount { get; private set; } public void Dispose() => this.DisposeCount++; }
}
