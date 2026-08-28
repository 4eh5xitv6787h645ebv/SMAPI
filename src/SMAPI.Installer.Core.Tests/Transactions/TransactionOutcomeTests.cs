using System.Runtime.Versioning;
using System.Security.Cryptography;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Security;
using StardewModdingAPI.Installer.Core.Transactions;

namespace StardewModdingAPI.Installer.Core.Tests.Transactions;

[TestFixture]
[SupportedOSPlatform("linux")]
public sealed class TransactionOutcomeTests
{
    private readonly List<string> TemporaryDirectories = new();

    [TearDown]
    public void TearDown()
    {
        foreach (string path in this.TemporaryDirectories)
        {
            if (System.IO.Directory.Exists(path))
                System.IO.Directory.Delete(path, recursive: true);
        }
        this.TemporaryDirectories.Clear();
    }

    [Test]
    public void DetailedOutcome_ReportsExactCommittedPathsAndSemanticStages()
    {
        string game = this.Directory();
        string payload = this.Directory();
        Write(game, "steam_appid.txt", "old");
        Write(payload, "dll", "dll");
        Write(payload, "launcher", "launcher");
        RecordingProgress progress = new();
        TransactionPlan plan = new(Guid.NewGuid(), new[]
        {
            Write("StardewModdingAPI.dll", null, "dll", "dll"),
            Write("StardewValley", null, "launcher", "launcher"),
            new TransactionFileOperation(TransactionOperationKind.RemoveFile, "steam_appid.txt", Hash("old"))
        });

        TransactionExecutionOutcome outcome = ApplyDetailed(game, payload, plan, new InstallerTransactionExecutor(progress));

        outcome.Status.Should().Be(TransactionOutcomeStatus.Committed);
        outcome.ChangedPaths.Should().Equal(
            new TransactionPathChange("StardewModdingAPI.dll", TransactionOperationKind.WriteFile),
            new TransactionPathChange("StardewValley", TransactionOperationKind.WriteFile),
            new TransactionPathChange("steam_appid.txt", TransactionOperationKind.RemoveFile)
        );
        progress.Items.Should().Contain(item => item.Stage == TransactionStage.WritingFiles);
        progress.Items.Should().Contain(item => item.Stage == TransactionStage.UpdatingLauncher);
        progress.Items.Should().Contain(item => item.Stage == TransactionStage.RemovingFiles);
    }

    [Test]
    public void DetailedOutcome_DistinguishesRollbackInterruptionAndCancellationAfterMutation()
    {
        foreach ((Exception failure, TransactionOutcomeStatus expected, TransactionCancellationDisposition cancellation) in new[]
        {
            ((Exception)new InvalidOperationException("fault"), TransactionOutcomeStatus.FailedAndRolledBack, TransactionCancellationDisposition.None),
            (new SimulatedProcessTerminationException("fault"), TransactionOutcomeStatus.InterruptedRecoveryRequired, TransactionCancellationDisposition.None),
            (new OperationCanceledException("fault"), TransactionOutcomeStatus.CancelledAndRolledBack, TransactionCancellationDisposition.ObservedAfterMutationAndRolledBack)
        })
        {
            string game = this.Directory();
            string payload = this.Directory();
            Write(game, "StardewModdingAPI.dll", "old");
            Write(payload, "new", "new");
            TransactionPlan plan = new(Guid.NewGuid(), new[] { Write("StardewModdingAPI.dll", Hash("old"), "new", "new") });
            InstallerTransactionExecutor executor = new(faultInjector: new AfterMutationFailure(failure));

            TransactionExecutionOutcome outcome = ApplyDetailed(game, payload, plan, executor);

            outcome.Status.Should().Be(expected);
            outcome.Cancellation.Should().Be(cancellation);
            outcome.ChangedPaths.Should().ContainSingle().Which.RelativePath.Should().Be("StardewModdingAPI.dll");
            if (expected == TransactionOutcomeStatus.InterruptedRecoveryRequired)
                File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("new");
            else
            {
                outcome.RolledBackPaths.Should().Equal(outcome.ChangedPaths);
                File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("old");
            }
        }
    }

    [Test]
    public void DetailedAndLegacyOutcomes_TreatDurableCommitAsSuccessWhenCleanupWarns()
    {
        foreach (bool detailed in new[] { true, false })
        {
            string game = this.Directory();
            string payload = this.Directory();
            Write(payload, "new", "new");
            TransactionPlan plan = new(Guid.NewGuid(), new[] { Write("StardewModdingAPI.dll", null, "new", "new") });
            InstallerTransactionExecutor executor = new(faultInjector: new PostCommitFailure());
            if (detailed)
            {
                TransactionExecutionOutcome outcome = ApplyDetailed(game, payload, plan, executor);
                outcome.Status.Should().Be(TransactionOutcomeStatus.CommittedWithCleanupWarning);
                outcome.Result.Should().NotBeNull();
            }
            else
                executor.Apply(game, payload, plan).Status.Should().Be(TransactionStatus.Committed);
            File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("new");
        }
    }

    [Test]
    public void DetailedOutcome_RollbackFailureReportsRecoveryRequiredAndPreservesUnexpectedFile()
    {
        string game = this.Directory();
        string payload = this.Directory();
        Write(game, "StardewModdingAPI.dll", "old");
        Write(payload, "new", "new");
        TransactionPlan plan = new(Guid.NewGuid(), new[] { Write("StardewModdingAPI.dll", Hash("old"), "new", "new") });
        InstallerTransactionExecutor executor = new(faultInjector: new TamperingFailure(game));

        TransactionExecutionOutcome outcome = ApplyDetailed(game, payload, plan, executor);

        outcome.Status.Should().Be(TransactionOutcomeStatus.RollbackFailedRecoveryRequired);
        outcome.RequiresRecovery.Should().BeTrue();
        outcome.ChangedPaths.Should().ContainSingle();
        outcome.RolledBackPaths.Should().BeEmpty();
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("tampered");
    }

    [Test]
    public void DetailedOutcome_PreCancelledHasNoMutationOrTransactionStatus()
    {
        string game = this.Directory();
        string payload = this.Directory();
        Write(payload, "new", "new");
        TransactionPlan plan = new(Guid.NewGuid(), new[] { Write("StardewModdingAPI.dll", null, "new", "new") });
        using CancellationTokenSource source = new();
        source.Cancel();
        using InstallerOperationLease lease = InstallerOperationLease.Acquire(game);
        using LinuxAnchoredFileSystem payloadRoot = new(payload);

        TransactionExecutionOutcome outcome = new InstallerTransactionExecutor().ApplyLockedWithOutcome(
            lease, payloadRoot, plan, lease.RootIdentity, lease.Generation, source.Token
        );

        outcome.Status.Should().Be(TransactionOutcomeStatus.CancelledBeforeMutation);
        outcome.DurableStatus.Should().Be(TransactionStatus.RolledBack);
        outcome.ChangedPaths.Should().BeEmpty();
        File.Exists(Path.Combine(game, "StardewModdingAPI.dll")).Should().BeFalse();
    }

    [Test]
    public void DetailedOutcome_CancellationRequestedAfterCommitDoesNotMisreportCommittedMutation()
    {
        string game = this.Directory();
        string payload = this.Directory();
        Write(payload, "new", "new");
        TransactionPlan plan = new(Guid.NewGuid(), new[] { Write("StardewModdingAPI.dll", null, "new", "new") });
        using CancellationTokenSource source = new();
        InstallerTransactionExecutor executor = new(faultInjector: new PostCommitCancellation(source));
        using InstallerOperationLease lease = InstallerOperationLease.Acquire(game);
        using LinuxAnchoredFileSystem payloadRoot = new(payload);

        TransactionExecutionOutcome outcome = executor.ApplyLockedWithOutcome(
            lease, payloadRoot, plan, lease.RootIdentity, lease.Generation, source.Token
        );

        outcome.Status.Should().Be(TransactionOutcomeStatus.Committed);
        outcome.Cancellation.Should().Be(TransactionCancellationDisposition.RequestedAfterMutationStartedAndCommitted);
        File.ReadAllText(Path.Combine(game, "StardewModdingAPI.dll")).Should().Be("new");
    }

    private static TransactionExecutionOutcome ApplyDetailed(string game, string payload, TransactionPlan plan, InstallerTransactionExecutor executor)
    {
        using InstallerOperationLease lease = InstallerOperationLease.Acquire(game);
        using LinuxAnchoredFileSystem payloadRoot = new(payload);
        return executor.ApplyLockedWithOutcome(lease, payloadRoot, plan, lease.RootIdentity, lease.Generation);
    }

    private string Directory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"smapi-outcome-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(path);
        this.TemporaryDirectories.Add(path);
        return path;
    }

    private static TransactionFileOperation Write(string destination, string? existing, string source, string result)
        => new(TransactionOperationKind.WriteFile, destination, existing, source, Hash(result), 0x1a4);

    private static void Write(string root, string path, string content)
    {
        string full = Path.Combine(root, path);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class RecordingProgress : ITransactionProgressSink
    {
        public List<TransactionProgress> Items { get; } = new();
        public void Report(TransactionProgress progress) => this.Items.Add(progress);
    }

    private sealed class AfterMutationFailure : ITransactionFaultInjector
    {
        private readonly Exception Failure;
        public AfterMutationFailure(Exception failure) => this.Failure = failure;
        public void BeforeMutation(Guid transactionId, int operationIndex) { }
        public void AfterMutation(Guid transactionId, int operationIndex) => throw this.Failure;
    }

    private sealed class PostCommitFailure : ITransactionFaultInjector
    {
        public void BeforeMutation(Guid transactionId, int operationIndex) { }
        public void AfterMutation(Guid transactionId, int operationIndex) { }
        public void AfterDurableCommit(Guid transactionId) => throw new IOException("cleanup fault");
    }

    private sealed class PostCommitCancellation : ITransactionFaultInjector
    {
        private readonly CancellationTokenSource Source;
        public PostCommitCancellation(CancellationTokenSource source) => this.Source = source;
        public void BeforeMutation(Guid transactionId, int operationIndex) { }
        public void AfterMutation(Guid transactionId, int operationIndex) { }
        public void AfterDurableCommit(Guid transactionId) => this.Source.Cancel();
    }

    private sealed class TamperingFailure : ITransactionFaultInjector
    {
        private readonly string GameRoot;
        public TamperingFailure(string gameRoot) => this.GameRoot = gameRoot;
        public void BeforeMutation(Guid transactionId, int operationIndex) { }
        public void AfterMutation(Guid transactionId, int operationIndex)
        {
            File.WriteAllText(Path.Combine(this.GameRoot, "StardewModdingAPI.dll"), "tampered");
            throw new IOException("rollback fault");
        }
    }
}
