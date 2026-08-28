using System.Security.Cryptography;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Engine;
using StardewModdingAPI.Installer.Core.Planning;
using StardewModdingAPI.Installer.Core.Transactions;

namespace StardewModdingAPI.Installer.Core.Tests.Engine;

[TestFixture]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class LinuxInstallerEngineRecoveryTests
{
    private readonly List<string> TemporaryDirectories = new();

    [TearDown]
    public void TearDown()
    {
        foreach (string path in this.TemporaryDirectories)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch
            {
                // Best-effort test cleanup only.
            }
        }
    }

    [Test]
    public async Task RecoverInterruptedOperation_PartialInstallWithNonExecutableInspection_RestoresAndRequiresFreshInspection()
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        Write(game, "StardewValley", "vanilla launcher");
        Write(payload, "vanilla", "vanilla launcher");
        Write(payload, "smapi", "SMAPI launcher");
        TransactionPlan plan = new(Guid.NewGuid(), new[]
        {
            WriteOperation("StardewValley-original", null, "vanilla", Hash("vanilla launcher")),
            WriteOperation("StardewValley", Hash("vanilla launcher"), "smapi", Hash("SMAPI launcher"))
        });
        Action interrupt = () => new InstallerTransactionExecutor(
            faultInjector: new MutationTerminationFaultInjector(afterOperation: 1)
        ).Apply(game, payload, plan);
        interrupt.Should().Throw<SimulatedProcessTerminationException>();
        File.SetUnixFileMode(Path.Combine(game, "StardewValley"), (UnixFileMode)0x1ed);
        File.ReadAllText(Path.Combine(game, "StardewValley")).Should().Be("SMAPI launcher");
        File.ReadAllText(Path.Combine(game, "StardewValley-original")).Should().Be("vanilla launcher");

        RecordingProgress progress = new();
        LinuxInstallerEngine engine = new(progress);
        using InspectedInstallationState blocked = await engine.InspectAsync(game, InstallationAction.Uninstall);
        blocked.Plan.CanExecute.Should().BeFalse();
        blocked.Plan.Conflicts.Select(conflict => conflict.Code).Should().Contain(PlanConflictCode.InstalledReceiptRequired);

        InterruptedOperationRecoveryResult result = await engine.RecoverInterruptedOperationAsync(game);

        result.GameRoot.Should().Be(blocked.GameRoot);
        result.PreviousOperationGeneration.Should().Be(blocked.OperationGeneration);
        result.CurrentOperationGeneration.Should().BeGreaterThan(result.PreviousOperationGeneration);
        result.RequiresFreshInspection.Should().BeTrue();
        result.RecoveredAny.Should().BeTrue();
        result.RecoveredTransactions.Should().ContainSingle().Which.Should().Be(
            new TransactionResult(plan.TransactionId, TransactionStatus.Recovered, 2)
        );
        File.ReadAllText(Path.Combine(game, "StardewValley")).Should().Be("vanilla launcher");
        File.Exists(Path.Combine(game, "StardewValley-original")).Should().BeFalse();
        progress.Items.Select(item => item.Stage).Should().ContainInOrder(
            TransactionStage.AcquiringLock,
            TransactionStage.Recovering,
            TransactionStage.Completed
        );

        using InspectedInstallationState fresh = await engine.InspectAsync(game, InstallationAction.Uninstall);
        fresh.OperationGeneration.Should().Be(result.CurrentOperationGeneration);
        fresh.OperationGeneration.Should().NotBe(blocked.OperationGeneration);
    }

    [TestCase(TransactionSetupBoundary.PreparationDirectoryCreated, 0)]
    [TestCase(TransactionSetupBoundary.PayloadDirectoriesCreated, 0)]
    [TestCase(TransactionSetupBoundary.ImmutablePlanCreated, 0)]
    [TestCase(TransactionSetupBoundary.CreationEventCreated, 0)]
    [TestCase(TransactionSetupBoundary.TransactionPublished, 1)]
    public async Task RecoverInterruptedOperation_EveryDurableSetupBoundary_IsRestartRecoverable(
        TransactionSetupBoundary boundary,
        int expectedRecoveredTransactions
    )
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        Write(game, "StardewModdingAPI.dll", "original");
        Write(payload, "replacement", "replacement");
        TransactionPlan plan = new(Guid.NewGuid(), new[]
        {
            WriteOperation("StardewModdingAPI.dll", Hash("original"), "replacement", Hash("replacement"))
        });
        Action interrupt = () => new InstallerTransactionExecutor(
            faultInjector: new SetupTerminationFaultInjector(boundary)
        ).Apply(game, payload, plan);
        interrupt.Should().Throw<SimulatedProcessTerminationException>();

        InterruptedOperationRecoveryResult result = await new LinuxInstallerEngine().RecoverInterruptedOperationAsync(game);

        result.RecoveredTransactions.Should().HaveCount(expectedRecoveredTransactions);
        if (expectedRecoveredTransactions == 1)
            result.RecoveredTransactions[0].Should().Be(new TransactionResult(plan.TransactionId, TransactionStatus.Recovered, 0));
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("original");
        Directory.EnumerateDirectories(Path.Combine(game, ".smapi-installer", "transactions"))
            .Select(Path.GetFileName)
            .Should().OnlyContain(name => !name!.StartsWith("preparing-", StringComparison.Ordinal));
    }

    [Test]
    public async Task RecoverInterruptedOperation_WithNoPublishedInterruption_StillInvalidatesPriorInspections()
    {
        string game = this.CreateDirectory();
        Write(game, "StardewValley", "vanilla");
        LinuxInstallerEngine engine = new();
        using InspectedInstallationState prior = await engine.InspectAsync(game, InstallationAction.Uninstall);

        InterruptedOperationRecoveryResult result = await engine.RecoverInterruptedOperationAsync(game);

        result.RecoveredAny.Should().BeFalse();
        result.RecoveredTransactions.Should().BeEmpty();
        result.PreviousOperationGeneration.Should().Be(prior.OperationGeneration);
        result.CurrentOperationGeneration.Should().Be(prior.OperationGeneration + 1);
        using InspectedInstallationState fresh = await engine.InspectAsync(game, InstallationAction.Uninstall);
        fresh.OperationGeneration.Should().Be(result.CurrentOperationGeneration);
    }

    [Test]
    public async Task RecoverInterruptedOperation_PreCancelled_DoesNotRecoverOrInvalidateGeneration()
    {
        var (game, _, plan) = this.CreateInterruptedReplacement();
        LinuxInstallerEngine engine = new();
        using InspectedInstallationState before = await engine.InspectAsync(game, InstallationAction.Uninstall);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Func<Task> recover = () => engine.RecoverInterruptedOperationAsync(game, cancellation.Token);

        await recover.Should().ThrowAsync<OperationCanceledException>();
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("replacement");
        using InspectedInstallationState after = await engine.InspectAsync(game, InstallationAction.Uninstall);
        after.OperationGeneration.Should().Be(before.OperationGeneration);
        new InstallerTransactionExecutor().RecoverIncompleteTransactions(game)
            .Should().ContainSingle().Which.TransactionId.Should().Be(plan.TransactionId);
    }

    [Test]
    public async Task RecoverInterruptedOperation_WhenNamedRootIsReplaced_ReportsRecoveredAnchoredRootAndDoesNotTouchReplacement()
    {
        string parent = this.CreateDirectory();
        string game = Path.Combine(parent, "game");
        string movedGame = Path.Combine(parent, "moved-game");
        Directory.CreateDirectory(game);
        string payload = this.CreateDirectory();
        Write(game, "StardewModdingAPI.dll", "original");
        Write(payload, "replacement", "replacement");
        TransactionPlan plan = new(Guid.NewGuid(), new[]
        {
            WriteOperation("StardewModdingAPI.dll", Hash("original"), "replacement", Hash("replacement"))
        });
        Action interrupt = () => new InstallerTransactionExecutor(
            faultInjector: new MutationTerminationFaultInjector(afterOperation: 0)
        ).Apply(game, payload, plan);
        interrupt.Should().Throw<SimulatedProcessTerminationException>();

        BlockingRecoveryProgress progress = new();
        Task<InterruptedOperationRecoveryResult> recovery = new LinuxInstallerEngine(progress)
            .RecoverInterruptedOperationAsync(game);
        progress.WaitUntilEntered();
        try
        {
            Directory.Move(game, movedGame);
            Directory.CreateDirectory(game);
            Write(game, "unrelated.txt", "replacement root sentinel");
        }
        finally
        {
            progress.Release();
        }

        InterruptedOperationRecoveryResult result = await recovery;
        result.RecoveredTransactions.Should().ContainSingle().Which.Should().Be(
            new TransactionResult(plan.TransactionId, TransactionStatus.Recovered, 1)
        );
        result.NamedRootStillSelected.Should().BeFalse();
        result.NamedRootSelectionChanged.Should().BeTrue();
        result.RequiresFreshInspection.Should().BeTrue();
        File.ReadAllText(Path.Combine(movedGame, "StardewModdingAPI.dll")).Should().Be("original");
        File.ReadAllText(Path.Combine(game, "unrelated.txt")).Should().Be("replacement root sentinel");
        File.Exists(Path.Combine(game, "StardewModdingAPI.dll")).Should().BeFalse();
    }

    [Test]
    public async Task RecoverInterruptedOperation_ThrowingProgressObserver_CannotChangeRecovery()
    {
        var (game, _, plan) = this.CreateInterruptedReplacement();

        InterruptedOperationRecoveryResult result = await new LinuxInstallerEngine(new ThrowingProgress())
            .RecoverInterruptedOperationAsync(game);

        result.RecoveredTransactions.Should().ContainSingle().Which.TransactionId.Should().Be(plan.TransactionId);
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("original");
    }

    [Test]
    public async Task RecoverInterruptedOperation_WhenLaterTransactionChangesAfterFullPreflight_PreservesEarlierExactRecovery()
    {
        (string game, TransactionPlan first, TransactionPlan second) = this.CreateTwoInterruptedReplacements();
        string laterBackup = Path.Combine(
            game,
            ".smapi-installer",
            "transactions",
            second.TransactionId.ToString("N"),
            "backups",
            "00000000"
        );
        InstallerTransactionExecutor executor = new(
            faultInjector: new MutateLaterTransactionAfterPreflight(first.TransactionId, laterBackup)
        );
        LinuxInstallerEngine engine = new(executor);

        Func<Task> recover = () => engine.RecoverInterruptedOperationAsync(game);

        InterruptedOperationRecoveryException failure = (await recover.Should()
            .ThrowAsync<InterruptedOperationRecoveryException>()).Which;
        failure.RecoveredTransactions.Should().ContainSingle().Which.Should().Be(
            new TransactionResult(first.TransactionId, TransactionStatus.Recovered, 1)
        );
        failure.RecoveredTransactions.Should().NotContain(item => item.TransactionId == second.TransactionId);
        failure.RecoveredAny.Should().BeTrue();
        failure.RequiresRecovery.Should().BeTrue();
        failure.RequiresFreshInspection.Should().BeTrue();
        failure.OperationGenerationAdvanced.Should().BeTrue();
        failure.NamedRootStillSelected.Should().BeTrue();
        failure.ErrorCode.Should().Be(TransactionErrorCode.RecoveryFailed);
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("first old");
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.xml")).Should().Be("second new");
    }

    [Test]
    public async Task RecoverInterruptedOperation_WhenCleanupFailsAfterDurableRollback_PreservesExactRecoveryResult()
    {
        (string game, _, TransactionPlan plan) = this.CreateInterruptedReplacement();
        InstallerTransactionExecutor executor = new(
            faultInjector: new RecoveryCleanupFailure(game, plan.TransactionId)
        );
        LinuxInstallerEngine engine = new(executor);

        Func<Task> recover = () => engine.RecoverInterruptedOperationAsync(game);

        InterruptedOperationRecoveryException failure = (await recover.Should()
            .ThrowAsync<InterruptedOperationRecoveryException>()).Which;
        failure.RecoveredTransactions.Should().ContainSingle().Which.Should().Be(
            new TransactionResult(plan.TransactionId, TransactionStatus.Recovered, 1)
        );
        failure.OperationGenerationAdvanced.Should().BeTrue();
        failure.ErrorCode.Should().Be(TransactionErrorCode.IoFailure);
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("original");
    }

    private (string Game, string Payload, TransactionPlan Plan) CreateInterruptedReplacement()
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        Write(game, "StardewModdingAPI.dll", "original");
        Write(payload, "replacement", "replacement");
        TransactionPlan plan = new(Guid.NewGuid(), new[]
        {
            WriteOperation("StardewModdingAPI.dll", Hash("original"), "replacement", Hash("replacement"))
        });
        Action interrupt = () => new InstallerTransactionExecutor(
            faultInjector: new MutationTerminationFaultInjector(afterOperation: 0)
        ).Apply(game, payload, plan);
        interrupt.Should().Throw<SimulatedProcessTerminationException>();
        return (game, payload, plan);
    }

    private (string Game, TransactionPlan First, TransactionPlan Second) CreateTwoInterruptedReplacements()
    {
        string game = this.CreateDirectory();
        string payload = this.CreateDirectory();
        Write(game, "StardewModdingAPI.dll", "first old");
        Write(game, "StardewModdingAPI.xml", "second old");
        Write(payload, "first", "first new");
        Write(payload, "second", "second new");
        TransactionPlan first = new(Guid.ParseExact("11111111111111111111111111111111", "N"), new[]
        {
            WriteOperation("StardewModdingAPI.dll", Hash("first old"), "first", Hash("first new"))
        });
        TransactionPlan second = new(Guid.ParseExact("ffffffffffffffffffffffffffffffff", "N"), new[]
        {
            WriteOperation("StardewModdingAPI.xml", Hash("second old"), "second", Hash("second new"))
        });
        InstallerTransactionExecutor crashing = new(
            faultInjector: new MutationTerminationFaultInjector(afterOperation: 0)
        );
        Action firstCrash = () => crashing.Apply(game, payload, first);
        firstCrash.Should().Throw<SimulatedProcessTerminationException>();
        string transactions = Path.Combine(game, ".smapi-installer", "transactions");
        string held = Path.Combine(game, "held-first-transaction");
        Directory.Move(Path.Combine(transactions, first.TransactionId.ToString("N")), held);
        Action secondCrash = () => crashing.Apply(game, payload, second);
        secondCrash.Should().Throw<SimulatedProcessTerminationException>();
        Directory.Move(held, Path.Combine(transactions, first.TransactionId.ToString("N")));
        return (game, first, second);
    }

    private string CreateDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"smapi-engine-recovery-{Guid.NewGuid():N}");
        LinuxGameTestFolder.MakeValid(path);
        this.TemporaryDirectories.Add(path);
        return path;
    }

    private static void Write(string root, string relativePath, string contents)
    {
        string path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static TransactionFileOperation WriteOperation(
        string path,
        string? expectedCurrent,
        string payloadPath,
        string expectedResult
    )
        => new(TransactionOperationKind.WriteFile, path, expectedCurrent, payloadPath, expectedResult);

    private sealed class MutationTerminationFaultInjector : ITransactionFaultInjector
    {
        private readonly int AfterOperation;

        public MutationTerminationFaultInjector(int afterOperation)
        {
            this.AfterOperation = afterOperation;
        }

        public void BeforeMutation(Guid transactionId, int operationIndex) { }

        public void AfterMutation(Guid transactionId, int operationIndex)
        {
            if (operationIndex == this.AfterOperation)
                throw new SimulatedProcessTerminationException("Injected process termination after mutation.");
        }
    }

    private sealed class SetupTerminationFaultInjector : ITransactionFaultInjector
    {
        private readonly TransactionSetupBoundary Boundary;

        public SetupTerminationFaultInjector(TransactionSetupBoundary boundary)
        {
            this.Boundary = boundary;
        }

        public void AtSetupBoundary(Guid transactionId, TransactionSetupBoundary boundary)
        {
            if (boundary == this.Boundary)
                throw new SimulatedProcessTerminationException($"Injected process termination at {boundary}.");
        }

        public void BeforeMutation(Guid transactionId, int operationIndex) { }
        public void AfterMutation(Guid transactionId, int operationIndex) { }
    }

    private sealed class MutateLaterTransactionAfterPreflight : ITransactionFaultInjector
    {
        private readonly Guid FirstTransactionId;
        private readonly string LaterBackupPath;
        private bool Mutated;

        public MutateLaterTransactionAfterPreflight(Guid firstTransactionId, string laterBackupPath)
        {
            this.FirstTransactionId = firstTransactionId;
            this.LaterBackupPath = laterBackupPath;
        }

        public void BeforeMutation(Guid transactionId, int operationIndex) { }
        public void AfterMutation(Guid transactionId, int operationIndex) { }
        public void BeforeRecoveringTransaction(Guid transactionId)
        {
            if (transactionId == this.FirstTransactionId && !this.Mutated)
            {
                this.Mutated = true;
                File.WriteAllText(this.LaterBackupPath, "changed after full-store preflight");
            }
        }
    }

    private sealed class RecoveryCleanupFailure : ITransactionFaultInjector
    {
        private readonly string GameRoot;
        private readonly Guid ExpectedTransactionId;

        public RecoveryCleanupFailure(string gameRoot, Guid expectedTransactionId)
        {
            this.GameRoot = gameRoot;
            this.ExpectedTransactionId = expectedTransactionId;
        }

        public void BeforeMutation(Guid transactionId, int operationIndex) { }
        public void AfterMutation(Guid transactionId, int operationIndex) { }
        public void AfterRecoveryRollbackBeforeCleanup(Guid transactionId)
        {
            transactionId.Should().Be(this.ExpectedTransactionId);
            File.WriteAllText(
                Path.Combine(this.GameRoot, ".smapi-installer", "transactions", transactionId.ToString("N"), "unknown-state"),
                "block cleanup"
            );
        }
    }

    private sealed class RecordingProgress : ITransactionProgressSink
    {
        public List<TransactionProgress> Items { get; } = new();
        public void Report(TransactionProgress progress) => this.Items.Add(progress);
    }

    private sealed class ThrowingProgress : ITransactionProgressSink
    {
        public void Report(TransactionProgress progress) => throw new InvalidOperationException("Observer failure.");
    }

    private sealed class BlockingRecoveryProgress : ITransactionProgressSink
    {
        private readonly ManualResetEventSlim Entered = new(false);
        private readonly ManualResetEventSlim Continue = new(false);
        private int Blocked;

        public void Report(TransactionProgress progress)
        {
            if (progress.Stage != TransactionStage.Recovering || Interlocked.Exchange(ref this.Blocked, 1) != 0)
                return;
            this.Entered.Set();
            this.Continue.Wait(TimeSpan.FromSeconds(15));
        }

        public void WaitUntilEntered()
        {
            if (!this.Entered.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("Recovery didn't reach its progress boundary.");
        }

        public void Release() => this.Continue.Set();
    }
}
