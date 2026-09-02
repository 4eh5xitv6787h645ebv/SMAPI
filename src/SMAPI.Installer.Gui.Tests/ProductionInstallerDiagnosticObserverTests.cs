using FluentAssertions;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Protocol.V1;
using StardewModdingAPI.Installer.Core.Transactions;
using StardewModdingAPI.Installer.Gui.Backend;
using StardewModdingAPI.Installer.Gui.Diagnostics;
using StardewModdingAPI.Installer.Gui.Frontend;

namespace StardewModdingAPI.Installer.Gui.Tests;

[TestFixture]
internal sealed class ProductionInstallerDiagnosticObserverTests
{
    [Test]
    public void FixedMappings_AreExhaustiveAndRejectUndefinedValues()
    {
        foreach (ReleaseVerificationState value in Enum.GetValues<ReleaseVerificationState>())
            _ = ProductionInstallerDiagnosticObserver.MapReleaseState(value);
        foreach (ReleaseVerificationError value in Enum.GetValues<ReleaseVerificationError>())
            ProductionInstallerDiagnosticObserver.ValidateReleaseError(value);
        foreach (ReviewedReleasePreparationStage value in Enum.GetValues<ReviewedReleasePreparationStage>())
            _ = ProductionInstallerDiagnosticObserver.MapReleaseProgressStage(value);
        foreach (GameDiscoveryState value in Enum.GetValues<GameDiscoveryState>())
            _ = ProductionInstallerDiagnosticObserver.MapGameState(value);
        foreach (PlanReviewState value in Enum.GetValues<PlanReviewState>())
            _ = ProductionInstallerDiagnosticObserver.MapPlanState(value);
        foreach (ExecutionState value in Enum.GetValues<ExecutionState>())
            _ = ProductionInstallerDiagnosticObserver.MapExecutionState(value);
        foreach (RecoveryPruneControllerState value in Enum.GetValues<RecoveryPruneControllerState>())
            _ = ProductionInstallerDiagnosticObserver.MapPruneState(value);
        foreach (TransactionStage value in Enum.GetValues<TransactionStage>())
        {
            _ = ProductionInstallerDiagnosticObserver.MapExecutionProgressStage(value, recovery: false);
            _ = ProductionInstallerDiagnosticObserver.MapExecutionProgressStage(value, recovery: true);
            _ = ProductionInstallerDiagnosticObserver.MapPruneProgressStage(value);
        }

        Action[] invalidMappings =
        [
            () => ProductionInstallerDiagnosticObserver.MapReleaseState((ReleaseVerificationState)999),
            () => ProductionInstallerDiagnosticObserver.ValidateReleaseError((ReleaseVerificationError)999),
            () => ProductionInstallerDiagnosticObserver.MapReleaseProgressStage((ReviewedReleasePreparationStage)999),
            () => ProductionInstallerDiagnosticObserver.MapGameState((GameDiscoveryState)999),
            () => ProductionInstallerDiagnosticObserver.MapPlanState((PlanReviewState)999),
            () => ProductionInstallerDiagnosticObserver.MapExecutionState((ExecutionState)999),
            () => ProductionInstallerDiagnosticObserver.MapPruneState((RecoveryPruneControllerState)999),
            () => ProductionInstallerDiagnosticObserver.MapExecutionProgressStage((TransactionStage)999, recovery: false),
            () => ProductionInstallerDiagnosticObserver.MapPruneProgressStage((TransactionStage)999)
        ];
        foreach (Action mapping in invalidMappings)
            mapping.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void Observe_DeduplicatesStateAndProgressAcrossRevisionsAndRecordsExactTerminalsOnce()
    {
        RecordingSink sink = new();
        ProductionInstallerDiagnosticObserver observer = new(sink);
        ExecutionPlanPresentation plan = CreateExecutionPlan();

        observer.Observe(CreateExecution(1, ExecutionState.Running, plan, TransactionStage.Staging));
        observer.Observe(CreateExecution(1, ExecutionState.Running, plan, TransactionStage.Staging));
        observer.Observe(CreateExecution(2, ExecutionState.Running, plan, TransactionStage.Staging));
        observer.Observe(CreateExecution(3, ExecutionState.Running, plan, TransactionStage.Applying));
        InstallerExecutionTerminalResult terminal = new(
            ProtocolExecutionOutcome.FailedBeforeMutation,
            ProtocolDurableState.Unchanged,
            ProtocolTerminalErrorCode.PermissionDenied,
            ProtocolRecoveryDisposition.NotRequired,
            ProtocolNextAction.ViewPrivateLog,
            new(null, null, null, null, null, null),
            InstallerBackendSettlement.ConfirmedClosed
        );
        observer.Observe(CreateExecution(4, ExecutionState.Terminal, plan, result: terminal));
        observer.Observe(CreateExecution(5, ExecutionState.Terminal, plan, result: terminal));

        sink.Calls.Should().Equal(
            new DiagnosticCall(InstallerDiagnosticCode.ExecutionProgress, null, DiagnosticErrorKind.None, "Staging"),
            new DiagnosticCall(InstallerDiagnosticCode.ExecutionProgress, null, DiagnosticErrorKind.None, "Applying"),
            new DiagnosticCall(
                InstallerDiagnosticCode.ExecutionTerminal,
                "PermissionDenied",
                DiagnosticErrorKind.Terminal,
                null,
                "Install|FailedBeforeMutation|Unchanged|ViewPrivateLog"
            )
        );
    }

    [Test]
    public void Observe_ProjectsSuccessfulExecutionRecoveryAndPruneTerminalFacts()
    {
        RecordingSink sink = new();
        ProductionInstallerDiagnosticObserver observer = new(sink);
        ExecutionPlanPresentation plan = CreateExecutionPlan();
        InstallerExecutionTerminalResult execution = new(
            ProtocolExecutionOutcome.Succeeded,
            ProtocolDurableState.Committed,
            null,
            ProtocolRecoveryDisposition.NotRequired,
            ProtocolNextAction.InspectAgain,
            new(1, 0, 1, 0, null, null),
            InstallerBackendSettlement.ConfirmedClosed
        );
        InstallerRecoveryTerminalResult recovery = new(
            ProtocolInterruptedRecoveryOutcome.RecoveryCompleted,
            ProtocolDurableState.RecoveryCompleted,
            null,
            ProtocolRecoveryDisposition.StateRefreshRequired,
            ProtocolNextAction.InspectAgain,
            new(true, true, 1, 1),
            InstallerBackendSettlement.ConfirmedClosed
        );

        observer.Observe(CreateExecution(1, ExecutionState.Terminal, plan, result: execution));
        observer.Observe(CreateExecution(2, ExecutionState.RecoveryCompleted, plan, result: execution, recoveryResult: recovery));

        RecoveryPruneTerminalPresentation prune = new(
            ProtocolPruneOutcome.Succeeded,
            ProtocolDurableState.PruneApplied,
            null,
            ProtocolRecoveryDisposition.NotRequired,
            ProtocolNextAction.ListRecoveries,
            1,
            1,
            0,
            false,
            InstallerBackendSettlement.ConfirmedClosed
        );
        observer.Observe(CreatePrune(1, 1, RecoveryPruneControllerState.Terminal, "PRIVATE", result: prune));

        sink.Calls.Where(call => call.TerminalFacts is not null).Select(call => call.TerminalFacts).Should().Equal(
            "Install|Succeeded|Committed|InspectAgain",
            "Install|RecoveryCompleted|RecoveryCompleted|InspectAgain",
            "Succeeded|PruneApplied|ListRecoveries"
        );
    }

    [Test]
    public void Observe_RejectsStaleGenerationsAndRevisions()
    {
        RecordingSink sink = new();
        ProductionInstallerDiagnosticObserver observer = new(sink);

        observer.Observe(CreateGame(2, 2, GameDiscoveryState.Discovering));
        observer.Observe(CreateGame(1, 99, GameDiscoveryState.Failed));
        observer.Observe(CreateGame(2, 1, GameDiscoveryState.Failed));
        observer.Observe(CreateGame(2, 3, GameDiscoveryState.Ready));

        sink.Calls.Select(call => call.Code).Should().Equal(
            InstallerDiagnosticCode.GameDiscoveryStarted,
            InstallerDiagnosticCode.GameDiscoveryReady
        );
    }

    [Test]
    public void Observe_RecordsOnlyStableTypedReleaseFailures()
    {
        (ReleaseVerificationError Error, ProtocolPrePlanErrorCode? ProtocolCode, InstallerDiagnosticCode DiagnosticCode)[] cases =
        [
            (ReleaseVerificationError.TransferUnavailable, null, InstallerDiagnosticCode.ReleaseNetworkUnavailable),
            (ReleaseVerificationError.TransferTimedOut, null, InstallerDiagnosticCode.ReleaseNetworkTimedOut),
            (ReleaseVerificationError.TransferInterrupted, null, InstallerDiagnosticCode.ReleaseDownloadInterrupted),
            (ReleaseVerificationError.PackageIntegrityOrMetadataRejected, ProtocolPrePlanErrorCode.PackageIntegrityRejected, InstallerDiagnosticCode.ReleaseFailed),
            (ReleaseVerificationError.PackageIntegrityOrMetadataRejected, ProtocolPrePlanErrorCode.PackageMetadataRejected, InstallerDiagnosticCode.ReleaseFailed),
            (ReleaseVerificationError.PackageIntegrityOrMetadataRejected, ProtocolPrePlanErrorCode.PackageArchiveRejected, InstallerDiagnosticCode.ReleaseFailed),
            (ReleaseVerificationError.PackageProvenanceOrIdentityRejected, ProtocolPrePlanErrorCode.PackageProvenanceRejected, InstallerDiagnosticCode.ReleaseFailed),
            (ReleaseVerificationError.PackageProvenanceOrIdentityRejected, ProtocolPrePlanErrorCode.PackageReleaseIdentityRejected, InstallerDiagnosticCode.ReleaseFailed)
        ];

        foreach ((ReleaseVerificationError error, ProtocolPrePlanErrorCode? protocolCode, InstallerDiagnosticCode diagnosticCode) in cases)
        {
            RecordingSink sink = new();
            ProductionInstallerDiagnosticObserver observer = new(sink);
            observer.Observe(CreateReleaseFailure(error, protocolCode));

            sink.Calls.Should().Equal(new DiagnosticCall(
                diagnosticCode,
                protocolCode?.ToString(),
                protocolCode is null ? DiagnosticErrorKind.None : DiagnosticErrorKind.PrePlan,
                null
            ));
            string projection = string.Join('|', sink.Calls);
            projection.Should().NotContain("package.zip").And.NotContain("/home/").And.NotContain("https://");
        }
    }

    [Test]
    public void Observe_ProjectsOnlyFixedTypedFactsAndNeverPrivateSnapshotStrings()
    {
        const string hostile = "PRIVATE-/home/alex/Saves/Blossom-https://token.example-deadbeef";
        RecordingSink sink = new();
        ProductionInstallerDiagnosticObserver observer = new(sink);

        observer.Observe(new ReleaseVerificationSnapshot(
            1,
            ReleaseVerificationState.Verified,
            null,
            [],
            null,
            null,
            1,
            3,
            false,
            false,
            false,
            false,
            ReleasePackageSource.LocalFolder,
            new(hostile, hostile, hostile, hostile, hostile, hostile, hostile, 1, hostile, hostile, hostile)
        ));
        ProtocolGameCandidate game = new(hostile, LinuxGameFolderStatus.Valid, hostile);
        observer.Observe(new GameDiscoverySnapshot(1, 1, GameDiscoveryState.Ready, [game], game, false, false, false, true));
        observer.Observe(new PlanReviewSnapshot(
            1,
            1,
            PlanReviewState.Rejected,
            InstallerOperation.Install,
            new PlanReviewRejection(ProtocolPrePlanErrorCode.PermissionDenied, ProtocolNextAction.ViewPrivateLog, true),
            false,
            false,
            false,
            false,
            true
        )
        {
            Candidates = [new PlanReviewCandidate(hostile, FileReplacementCandidateReason.ModifiedReceiptOwned, FileReplacementCandidateDisposition.Replace, false)]
        });
        observer.Observe(CreatePrune(
            1,
            1,
            RecoveryPruneControllerState.Failed,
            hostile,
            new RecoveryPruneRejection(ProtocolPrePlanErrorCode.InputOutputFailure, ProtocolNextAction.ViewPrivateLog, true)
        ));

        string projection = string.Join('|', sink.Calls);
        projection.Should().NotContain(hostile);
        sink.Calls.Select(call => call.Code).Should().Equal(
            InstallerDiagnosticCode.ReleaseVerified,
            InstallerDiagnosticCode.GameDiscoveryReady,
            InstallerDiagnosticCode.PlanRejected,
            InstallerDiagnosticCode.RecoveryPruneFailed
        );
    }

    [Test]
    public void Observe_NeverPropagatesMalformedSnapshotOrSinkFailuresToControllerPublisher()
    {
        ProductionInstallerDiagnosticObserver throwing = new(new ThrowingSink());

        Action sinkFailure = () => throwing.Observe(CreateGame(1, 1, GameDiscoveryState.Discovering));
        Action malformedEnum = () => throwing.Observe(CreateGame(1, 2, (GameDiscoveryState)999));

        sinkFailure.Should().NotThrow();
        malformedEnum.Should().NotThrow();
    }

    private static GameDiscoverySnapshot CreateGame(long generation, long revision, GameDiscoveryState state)
        => new(generation, revision, state, [], null, false, false, false, false);

    private static ReleaseVerificationSnapshot CreateReleaseFailure(
        ReleaseVerificationError error,
        ProtocolPrePlanErrorCode? protocolCode
    ) => new(
        1,
        ReleaseVerificationState.Failed,
        new(
            error,
            protocolCode,
            protocolCode is null ? null : ProtocolNextAction.ReopenVerifiedPackage,
            false
        ),
        [],
        null,
        null,
        1,
        3,
        false,
        false,
        false,
        false,
        ReleasePackageSource.ReviewedDownload,
        null
    );

    private static ExecutionPlanPresentation CreateExecutionPlan()
    {
        ProtocolReleaseIdentity release = GameDiscoveryControllerTests.Release();
        return new(
            InstallerOperation.Install,
            "Stardew Valley",
            null,
            new ExecutionReleaseTarget(new(release.Tag, release.EmbeddedVersion)),
            null,
            new(0, ProtocolJsonSerializer.MaxRecoveryGenerations),
            [],
            [],
            0,
            [],
            0
        );
    }

    private static ExecutionSnapshot CreateExecution(
        long revision,
        ExecutionState state,
        ExecutionPlanPresentation plan,
        TransactionStage? stage = null,
        InstallerExecutionResult? result = null,
        InstallerRecoveryResult? recoveryResult = null
    ) => new(revision, state, plan, stage, 0, null, result, recoveryResult, false, false, false, false);

    private static RecoveryPruneSnapshot CreatePrune(
        long generation,
        long revision,
        RecoveryPruneControllerState state,
        string hostile,
        RecoveryPruneRejection? rejection = null,
        RecoveryPruneResultPresentation? result = null
    ) => new(
        generation,
        revision,
        state,
        new RecoveryPruneRelease(hostile, hostile),
        new RecoveryPruneGame(hostile, hostile),
        [],
        null,
        null,
        rejection,
        null,
        0,
        null,
        result,
        false,
        false,
        false,
        false,
        false,
        false,
        true
    );

    private enum DiagnosticErrorKind { None, PrePlan, Terminal }

    private sealed record DiagnosticCall(
        InstallerDiagnosticCode Code,
        string? Error,
        DiagnosticErrorKind ErrorKind,
        string? ProgressStage,
        string? TerminalFacts = null
    );

    private sealed class RecordingSink : IProductionInstallerDiagnosticSink
    {
        public List<DiagnosticCall> Calls { get; } = [];

        public void Record(InstallerDiagnosticCode code)
            => this.Calls.Add(new(code, null, DiagnosticErrorKind.None, null));

        public void Record(InstallerDiagnosticCode code, ProtocolPrePlanErrorCode? error)
            => this.Calls.Add(new(code, error?.ToString(), DiagnosticErrorKind.PrePlan, null));

        public void Record(InstallerDiagnosticCode code, ProtocolTerminalErrorCode? error)
            => this.Calls.Add(new(code, error?.ToString(), DiagnosticErrorKind.Terminal, null));

        public void RecordExecutionTerminal(
            InstallerOperation operation,
            ProtocolExecutionOutcome outcome,
            ProtocolDurableState durableState,
            ProtocolTerminalErrorCode? error,
            ProtocolNextAction nextAction
        ) => this.Calls.Add(new(
            InstallerDiagnosticCode.ExecutionTerminal,
            error?.ToString(),
            DiagnosticErrorKind.Terminal,
            null,
            $"{operation}|{outcome}|{durableState}|{nextAction}"
        ));

        public void RecordRecoveryTerminal(
            InstallerOperation operation,
            ProtocolInterruptedRecoveryOutcome outcome,
            ProtocolDurableState durableState,
            ProtocolTerminalErrorCode? error,
            ProtocolNextAction nextAction
        ) => this.Calls.Add(new(
            InstallerDiagnosticCode.ExecutionRecoveryTerminal,
            error?.ToString(),
            DiagnosticErrorKind.Terminal,
            null,
            $"{operation}|{outcome}|{durableState}|{nextAction}"
        ));

        public void RecordPruneTerminal(
            ProtocolPruneOutcome outcome,
            ProtocolDurableState durableState,
            ProtocolTerminalErrorCode? error,
            ProtocolNextAction nextAction
        ) => this.Calls.Add(new(
            InstallerDiagnosticCode.RecoveryPruneTerminal,
            error?.ToString(),
            DiagnosticErrorKind.Terminal,
            null,
            $"{outcome}|{durableState}|{nextAction}"
        ));

        public void RecordProgress(InstallerDiagnosticCode code, ReviewedReleasePreparationStage stage)
            => this.Calls.Add(new(code, null, DiagnosticErrorKind.None, stage.ToString()));

        public void RecordProgress(InstallerDiagnosticCode code, TransactionStage stage)
            => this.Calls.Add(new(code, null, DiagnosticErrorKind.None, stage.ToString()));
    }

    private sealed class ThrowingSink : IProductionInstallerDiagnosticSink
    {
        public void Record(InstallerDiagnosticCode code) => throw new InvalidOperationException();
        public void Record(InstallerDiagnosticCode code, ProtocolPrePlanErrorCode? error) => throw new InvalidOperationException();
        public void Record(InstallerDiagnosticCode code, ProtocolTerminalErrorCode? error) => throw new InvalidOperationException();
        public void RecordExecutionTerminal(InstallerOperation operation, ProtocolExecutionOutcome outcome, ProtocolDurableState durableState, ProtocolTerminalErrorCode? error, ProtocolNextAction nextAction) => throw new InvalidOperationException();
        public void RecordRecoveryTerminal(InstallerOperation operation, ProtocolInterruptedRecoveryOutcome outcome, ProtocolDurableState durableState, ProtocolTerminalErrorCode? error, ProtocolNextAction nextAction) => throw new InvalidOperationException();
        public void RecordPruneTerminal(ProtocolPruneOutcome outcome, ProtocolDurableState durableState, ProtocolTerminalErrorCode? error, ProtocolNextAction nextAction) => throw new InvalidOperationException();
        public void RecordProgress(InstallerDiagnosticCode code, ReviewedReleasePreparationStage stage) => throw new InvalidOperationException();
        public void RecordProgress(InstallerDiagnosticCode code, TransactionStage stage) => throw new InvalidOperationException();
    }
}
